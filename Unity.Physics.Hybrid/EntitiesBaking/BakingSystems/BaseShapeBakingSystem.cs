#if UNITY_EDITOR
#if UNITY_ANDROID && !UNITY_64
#define UNITY_ANDROID_ARM7V
#endif

using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Hash128 = Unity.Entities.Hash128;
using UnityMesh = UnityEngine.Mesh;

namespace Unity.Physics.Authoring
{
    [RequireMatchingQueriesForUpdate]
    [UpdateAfter(typeof(BeginColliderBakingSystem))]
    [UpdateBefore(typeof(BuildCompoundCollidersBakingSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial class BaseShapeBakingSystem : SystemBase
    {
        BeginColliderBakingSystem m_BeginColliderBakingSystem;
        List<UnityEngine.Mesh> meshArray;

        static readonly ProfilerMarker BuffersAcquire = new ProfilerMarker("Buffers Acquire");
        static readonly ProfilerMarker MeshCreate = new ProfilerMarker("Mesh Create");
        static readonly ProfilerMarker ConvexCreate = new ProfilerMarker("Convex Create");

        internal struct ColliderBlobBakingData
        {
            public Entities.Hash128 Hash;
            public BlobAssetReference<Collider> ColliderBlobAsset;
        }

        internal BlobAssetComputationContext<int, Collider> BlobComputationContext =>
            m_BeginColliderBakingSystem.BlobComputationContext;

        EntityQuery m_ShapeQuery;
        EntityQuery m_MeshBlobQuery;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_ShapeQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PhysicsColliderAuthoringData>() },
                Options = EntityQueryOptions.IncludePrefab
            });
            m_BeginColliderBakingSystem = World.GetOrCreateSystemManaged<BeginColliderBakingSystem>();
            meshArray = new List<UnityMesh>();

            m_MeshBlobQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<PhysicsColliderAuthoringData>(), ComponentType.ReadOnly<PhysicsMeshAuthoringData>() },
                Options = EntityQueryOptions.IncludePrefab
            });
        }

        protected override void OnUpdate()
        {
            int shapeCount = m_ShapeQuery.CalculateEntityCount();
            if (shapeCount == 0)
                return;

            // 1) Obtain all meshes in colliders
            Profiler.BeginSample("Get Meshes");

            // Collect meshes doing only one call to the engine
            // It would be better to use a native container, but AcquireReadOnlyMeshData doesn't accept it
            meshArray.Clear();
            int meshCount = 0;
            Entities.ForEach((ref PhysicsMeshAuthoringData meshBakingData) =>
            {
                meshArray.Add(meshBakingData.Mesh.Value);
                meshBakingData.MeshArrayIndex = meshCount;
                ++meshCount;
            }).WithEntityQueryOptions(EntityQueryOptions.IncludePrefab).WithName("ExtractMeshes").WithoutBurst().Run();
            var meshDataArray = UnityEditor.MeshUtility.AcquireReadOnlyMeshData(meshArray);
            Profiler.EndSample();

            // 2) Calculate the hashes for all the colliders
            // -----------------------------------------
            Profiler.BeginSample("Generate Hashes for Inputs");

            // Convex Hull and Meshes
            JobHandle meshHashesJobHandle = Entities
#if UNITY_ANDROID_ARM7V
                .WithoutBurst()
#endif
                    .ForEach((ref PhysicsColliderAuthoringData colliderData, ref PhysicsColliderBakedData generatedData, in PhysicsMeshAuthoringData meshData) =>
            {
                var hash = CalculateMeshHashes(ref colliderData.ShapeComputationalData, meshData, meshDataArray);
                generatedData.Hash = hash;

                // Setting the hash internally inside the ShapeComputationalData
                var shapeData = colliderData.ShapeComputationalData;
                var instance = shapeData.Instance;
                instance.Hash = hash;
                shapeData.Instance = instance;
                colliderData.ShapeComputationalData = shapeData;
            }).WithEntityQueryOptions(EntityQueryOptions.IncludePrefab)
                    .WithName("HashMeshes")
                    .WithReadOnly(meshDataArray)
                    .ScheduleParallel(default);

            // Basic Colliders
            var basicHashesJobHandle =
                Entities
                    .WithNone<PhysicsMeshAuthoringData>()
#if UNITY_ANDROID_ARM7V
                    .WithoutBurst()
#endif
                    .ForEach((ref PhysicsColliderAuthoringData colliderData, ref PhysicsColliderBakedData generatedData) =>
                    {
                        // Calculating the hash
                        var hash = CalculatePhysicsShapeHash(ref colliderData.ShapeComputationalData);
                        generatedData.Hash = hash;

                        // Setting the hash internally inside the ShapeComputationalData
                        var shapeData = colliderData.ShapeComputationalData;
                        var instance = shapeData.Instance;
                        instance.Hash = hash;
                        shapeData.Instance = instance;
                        colliderData.ShapeComputationalData = shapeData;
                    }).WithEntityQueryOptions(EntityQueryOptions.IncludePrefab)
                    .WithName("HashBasics")
                    .ScheduleParallel(meshHashesJobHandle);
            Profiler.EndSample();

            var hashesJobHandle = JobHandle.CombineDependencies(meshHashesJobHandle, basicHashesJobHandle);

            // 3) Deduplicate blobs to calculate, so we do it calculate each only once
            // -----------------------------------------
            Profiler.BeginSample("Determine New Colliders to Create");

            var localBlobComputationContext = BlobComputationContext;

            NativeArray<int> count = new NativeArray<int>(1, Allocator.TempJob);
            NativeArray<ColliderBlobBakingData> generatedDataArray = new NativeArray<ColliderBlobBakingData>(shapeCount, Allocator.TempJob);
            var deduplicateJobHandle = Entities.ForEach((ref PhysicsColliderAuthoringData colliderData) =>
            {
                // Associate the blob hash to the GO Instance ID
                var hash = colliderData.ShapeComputationalData.Instance.Hash;
                var convertedAuthoringInstanceID = colliderData.ShapeComputationalData.Instance.ConvertedAuthoringInstanceID;
                localBlobComputationContext.AssociateBlobAssetWithUnityObject(hash, convertedAuthoringInstanceID);

                if (localBlobComputationContext.NeedToComputeBlobAsset(hash))
                {
                    localBlobComputationContext.AddBlobAssetToCompute(hash, 0);

                    colliderData.BlobIndex = count[0];
                    colliderData.RecalculateBlob = true;
                    ++count[0];
                }
                else
                {
                    colliderData.RecalculateBlob = false;
                }
            }).WithEntityQueryOptions(EntityQueryOptions.IncludePrefab).WithName("Deduplicate").Schedule(hashesJobHandle);
            Profiler.EndSample();

            // 4) Calculate blobs
            // -----------------------------------------
            Profiler.BeginSample("Create New Colliders");

            Profiler.BeginSample("Hull and Meshes");

            var colliderDataArray = m_MeshBlobQuery.ToComponentDataListAsync<PhysicsColliderAuthoringData>(Allocator.TempJob, deduplicateJobHandle, out JobHandle copyColliderData);
            var meshBakingDataArray = m_MeshBlobQuery.ToComponentDataListAsync<PhysicsMeshAuthoringData>(Allocator.TempJob, deduplicateJobHandle, out JobHandle copyMeshData);
            var job = new MeshBlobsJob
            {
                ColliderDataArray = colliderDataArray.AsDeferredJobArray(),
                MeshBakingDataArray = meshBakingDataArray.AsDeferredJobArray(),
                meshDataArray = meshDataArray,
                generatedDataArray = generatedDataArray,

                BuffersAcquire = BuffersAcquire,
                MeshCreate =  MeshCreate,
                ConvexCreate = ConvexCreate
            };
            var meshBlobsJobHandle = job.Schedule(meshDataArray.Length, 1, JobHandle.CombineDependencies(copyColliderData, copyMeshData));

            Profiler.EndSample();

            Profiler.BeginSample("Basic Colliders");
            // Basic Colliders
            var basicBlobsJobHandle =
                Entities
                    .WithNone<PhysicsMeshAuthoringData>()
                    .ForEach((ref PhysicsColliderAuthoringData colliderData) =>
                    {
                        // Calculate the blob assets if needed
                        if (colliderData.RecalculateBlob)
                        {
                            var shapeData = colliderData.ShapeComputationalData;
                            generatedDataArray[colliderData.BlobIndex] = new ColliderBlobBakingData()
                            {
                                Hash = colliderData.ShapeComputationalData.Instance.Hash,
                                ColliderBlobAsset = CalculateBasicBlobAsset(ref shapeData)
                            };
                        }
                    }).WithEntityQueryOptions(EntityQueryOptions.IncludePrefab)
                    .WithName("BasicBlobs")
                    .WithNativeDisableParallelForRestriction(generatedDataArray)
                    .ScheduleParallel(meshBlobsJobHandle);
            Profiler.EndSample();

            Profiler.EndSample();

            var blobJobHandle = JobHandle.CombineDependencies(meshBlobsJobHandle, basicBlobsJobHandle);
            blobJobHandle.Complete();

            // 5) Update BlobComputationContext with the new blobs
            Profiler.BeginSample("Store Blobs in Context");

            for (int blobIndex = 0; blobIndex < count[0]; ++blobIndex)
            {
                var entry = generatedDataArray[blobIndex];
                localBlobComputationContext.AddComputedBlobAsset(entry.Hash, entry.ColliderBlobAsset);
            }
            Profiler.EndSample();

            generatedDataArray.Dispose();
            meshDataArray.Dispose();
            count.Dispose();
            colliderDataArray.Dispose();
            meshBakingDataArray.Dispose();
        }

        static Hash128 CalculateMeshHashes(ref ShapeComputationDataBaking res, PhysicsMeshAuthoringData physicsMeshData, UnityMesh.MeshDataArray meshDataArray)
        {
            // Access the mesh vertices
            AppendMeshPropertiesToNativeBuffers(meshDataArray[physicsMeshData.MeshArrayIndex], !physicsMeshData.Convex, out var pointCloud, out var triangles);

            // Hash Calculation
            return HashableShapeInputs.GetHash128(
                res.ForceUniqueIdentifier,
                res.ConvexHullProperties.GenerationParameters,
                res.Material,
                res.CollisionFilter,
                physicsMeshData.BakeFromShape,
                physicsMeshData.ChildToShape,
                physicsMeshData.MeshID,
                physicsMeshData.MeshBounds,
                pointCloud,
                triangles
            );
        }
    }

    [BurstCompile(CompileSynchronously = true)]
    struct MeshBlobsJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<PhysicsColliderAuthoringData> ColliderDataArray;
        [ReadOnly] public NativeArray<PhysicsMeshAuthoringData> MeshBakingDataArray;
        [ReadOnly] public UnityMesh.MeshDataArray meshDataArray;

        [NativeDisableParallelForRestriction]
        public NativeArray<BaseShapeBakingSystem.ColliderBlobBakingData> generatedDataArray;

        public ProfilerMarker BuffersAcquire;
        public ProfilerMarker MeshCreate;
        public ProfilerMarker ConvexCreate;

        public void Execute(int index)
        {
            var colliderData = ColliderDataArray[index];
            var meshBakingData = MeshBakingDataArray[index];

            if (colliderData.RecalculateBlob)
            {
                BuffersAcquire.Begin();
                BaseShapeBakingSystem.AppendMeshPropertiesToNativeBuffers(meshDataArray[meshBakingData.MeshArrayIndex], !meshBakingData.Convex, out var pointCloud, out var triangles);
                var compoundMatrix = math.mul(meshBakingData.BakeFromShape, meshBakingData.ChildToShape);
                for (int i = 0; i < pointCloud.Length; ++i)
                    pointCloud[i] = math.mul(compoundMatrix, new float4(pointCloud[i], 1f)).xyz;
                BuffersAcquire.End();

                if (meshBakingData.Convex)
                {
                    ConvexCreate.Begin();
                    // Create the blob for Convex meshArray
                    var colliderBlobAsset = ConvexCollider.Create(pointCloud,
                        colliderData.ShapeComputationalData.ConvexHullProperties.GenerationParameters,
                        colliderData.ShapeComputationalData.ConvexHullProperties.Filter,
                        colliderData.ShapeComputationalData.ConvexHullProperties.Material);
                    generatedDataArray[colliderData.BlobIndex] = new BaseShapeBakingSystem.ColliderBlobBakingData()
                    {
                        Hash = colliderData.ShapeComputationalData.Instance.Hash,
                        ColliderBlobAsset = colliderBlobAsset
                    };
                    ConvexCreate.End();
                }
                else if (pointCloud.Length != 0 && triangles.Length != 0)
                {
                    MeshCreate.Begin();
                    // Create the blob for mesh colliders
                    var colliderBlobAsset = MeshCollider.Create(pointCloud, triangles, colliderData.ShapeComputationalData.MeshProperties.Filter, colliderData.ShapeComputationalData.MeshProperties.Material);
                    generatedDataArray[colliderData.BlobIndex] = new BaseShapeBakingSystem.ColliderBlobBakingData()
                    {
                        Hash = colliderData.ShapeComputationalData.Instance.Hash,
                        ColliderBlobAsset = colliderBlobAsset
                    };
                    MeshCreate.End();
                }

                pointCloud.Dispose();
                if (triangles.IsCreated)
                    triangles.Dispose();
            }
        }
    }
}
#endif
