using System;
using System.Collections.Generic;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Unity.Physics.GraphicsIntegration
{
    /// <summary>
    /// A system that can smooth out the motion of rigid bodies if the fixed physics tick rate is slower than the variable graphics framerate.
    /// Each affected body's <see cref="Unity.Transforms.LocalToWorld"/> matrix is adjusted before rendering, but its underlying
    /// <see cref="Unity.Transforms.LocalTransform"/> component is left alone.
    /// </summary>
    [UpdateInGroup(typeof(TransformSystemGroup))]
    [UpdateBefore(typeof(LocalToWorldSystem))]
    [BurstCompile]
    public partial struct SmoothRigidBodiesGraphicalMotion : ISystem
    {
        /// <summary>
        /// An entity query matching dynamic rigid bodies whose motion should be smoothed.
        /// </summary>
        public EntityQuery SmoothedDynamicBodiesQuery { get; private set; }

        private Entity m_MostRecentTimeEntity;
        ComponentTypeHandle<LocalTransform> m_ComponentTypeHandle;
        ComponentTypeHandle<PostTransformMatrix> m_PostTransformMatrixType;
        ComponentTypeHandle<PhysicsMass> m_PhysicsMassType;
        ComponentTypeHandle<PhysicsGraphicalInterpolationBuffer> m_InterpolationBufferType;
        ComponentTypeHandle<PhysicsGraphicalSmoothing> m_PhysicsGraphicalSmoothingType;
        ComponentTypeHandle<LocalToWorld> m_LocalToWorldType;

        //Declaring big capacity since buffers with RigidBodySmoothingWorldIndex and MostRecentFixedTime will be stored together in a singleton Entity
        //and that Entity will get a whole Chunk allocated anyway. This capacity is just a limit for keeping the buffer inside the Chunk,
        //reducing it does not affect memory consumption
        [InternalBufferCapacity(256)]
        struct RigidBodySmoothingWorldIndex : IBufferElementData,
                                              IEquatable<RigidBodySmoothingWorldIndex>, IComparable<RigidBodySmoothingWorldIndex>
        {
            public int Value;
            public RigidBodySmoothingWorldIndex(PhysicsWorldIndex index)
            {
                Value = (int)index.Value;
            }

            public bool Equals(RigidBodySmoothingWorldIndex other)
            {
                return other.Value == Value;
            }

            public int CompareTo(RigidBodySmoothingWorldIndex other)
            {
                return Value.CompareTo(other.Value);
            }
        }

        /// <summary>
        /// Registers the physics world for smooth rigid body motion described by physicsWorldIndex.
        /// </summary>
        /// <param name="state"><see cref="SystemState"/> reference from an <see cref="ISystem"/></param>
        /// <param name="mostRecentTimeEntity">Entity for looking up <see cref="MostRecentFixedTime"/> and <see cref="RigidBodySmoothingWorldIndex"/> buffers.</param>
        /// <param name="physicsWorldIndex">    Zero-based index of the physics world. </param>
        public static void RegisterPhysicsWorldForSmoothRigidBodyMotion(ref SystemState state,
            Entity mostRecentTimeEntity, PhysicsWorldIndex physicsWorldIndex)
        {
            var mostRecentFixedTimes = state.EntityManager.GetBuffer<MostRecentFixedTime>(mostRecentTimeEntity);
            var worldIndexToUpdate = state.EntityManager.GetBuffer<RigidBodySmoothingWorldIndex>(mostRecentTimeEntity);
            var rbSmoothIndex = new RigidBodySmoothingWorldIndex(physicsWorldIndex);
            if (mostRecentFixedTimes.Length <= rbSmoothIndex.Value)
                mostRecentFixedTimes.ResizeUninitialized(rbSmoothIndex.Value + 1);
            if (worldIndexToUpdate.AsNativeArray().IndexOf(rbSmoothIndex) == -1)
            {
                worldIndexToUpdate.Add(rbSmoothIndex);
                worldIndexToUpdate.AsNativeArray().Sort();
            }
        }

        /// <summary>
        /// Unregisters the physics world for smooth rigid body motion described by physicsWorldIndex.
        /// </summary>
        ///
        /// <param name="state"><see cref="SystemState"/> reference from an <see cref="ISystem"/></param>
        /// <param name="mostRecentTimeEntity">Entity for looking up <see cref="MostRecentFixedTime"/> and <see cref="RigidBodySmoothingWorldIndex"/> buffers.</param>
        /// <param name="physicsWorldIndex">    Zero-based index of the physics world. </param>
        public static void UnregisterPhysicsWorldForSmoothRigidBodyMotion(ref SystemState state,
            Entity mostRecentTimeEntity, PhysicsWorldIndex physicsWorldIndex)
        {
            var worldIndexToUpdate = state.EntityManager.GetBuffer<RigidBodySmoothingWorldIndex>(mostRecentTimeEntity);
            for (int i = 0; i < worldIndexToUpdate.Length; ++i)
            {
                //Don't use swap back to keep sorting
                if (worldIndexToUpdate[i].Value == physicsWorldIndex.Value)
                {
                    worldIndexToUpdate.RemoveAt(i);
                    break;
                }
            }
        }

        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            SmoothedDynamicBodiesQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<LocalTransform, LocalToWorld, PhysicsWorldIndex, PhysicsGraphicalSmoothing>()
                .WithOptions(EntityQueryOptions.FilterWriteGroup)
                .Build(ref state);
            // Store a buffer of MostRecentFixedTime element, one for each physics world.
            m_MostRecentTimeEntity = state.EntityManager.CreateEntity();
            state.EntityManager.AddComponent<MostRecentFixedTime>(m_MostRecentTimeEntity);
            state.EntityManager.AddComponent<RigidBodySmoothingWorldIndex>(m_MostRecentTimeEntity);
            state.EntityManager.SetName(m_MostRecentTimeEntity, "MostRecentFixedTime");
            state.RequireForUpdate(SmoothedDynamicBodiesQuery);
            state.RequireForUpdate<MostRecentFixedTime>();

            m_ComponentTypeHandle = state.GetComponentTypeHandle<LocalTransform>(true);
            m_PostTransformMatrixType = state.GetComponentTypeHandle<PostTransformMatrix>(true);
            m_PhysicsMassType = state.GetComponentTypeHandle<PhysicsMass>(true);
            m_InterpolationBufferType = state.GetComponentTypeHandle<PhysicsGraphicalInterpolationBuffer>(true);
            m_PhysicsGraphicalSmoothingType = state.GetComponentTypeHandle<PhysicsGraphicalSmoothing>();
            m_LocalToWorldType = state.GetComponentTypeHandle<LocalToWorld>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var mostRecentTimes = SystemAPI.GetBuffer<MostRecentFixedTime>(m_MostRecentTimeEntity);
            var mostRecentTimeToUpdate = SystemAPI.GetBuffer<RigidBodySmoothingWorldIndex>(m_MostRecentTimeEntity);
            foreach (var worldIndex in mostRecentTimeToUpdate)
            {
                var timeAhead = (float)(SystemAPI.Time.ElapsedTime - mostRecentTimes[worldIndex.Value].ElapsedTime);
                var timeStep = (float)mostRecentTimes[worldIndex.Value].DeltaTime;
                if (timeAhead < 0f || timeStep == 0f)
                    continue;

                var normalizedTimeAhead = math.clamp(timeAhead / timeStep, 0f, 1f);

                SmoothedDynamicBodiesQuery.SetSharedComponentFilter(new PhysicsWorldIndex((uint)worldIndex.Value));
                m_ComponentTypeHandle.Update(ref state);
                m_PostTransformMatrixType.Update(ref state);
                m_PhysicsMassType.Update(ref state);
                m_InterpolationBufferType.Update(ref state);
                m_PhysicsGraphicalSmoothingType.Update(ref state);
                m_LocalToWorldType.Update(ref state);
                state.Dependency = new SmoothMotionJob
                {
                    LocalTransformType = m_ComponentTypeHandle,
                    PostTransformMatrixType = m_PostTransformMatrixType,
                    PhysicsMassType = m_PhysicsMassType,
                    InterpolationBufferType = m_InterpolationBufferType,
                    PhysicsGraphicalSmoothingType = m_PhysicsGraphicalSmoothingType,
                    LocalToWorldType = m_LocalToWorldType,
                    TimeAhead = timeAhead,
                    NormalizedTimeAhead = normalizedTimeAhead
                }.ScheduleParallel(SmoothedDynamicBodiesQuery, state.Dependency);
            }
        }

        [BurstCompile]
        struct SmoothMotionJob : IJobChunk
        {

            [ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransformType;
            [ReadOnly] public ComponentTypeHandle<PostTransformMatrix> PostTransformMatrixType;
            [ReadOnly] public ComponentTypeHandle<PhysicsMass> PhysicsMassType;
            [ReadOnly] public ComponentTypeHandle<PhysicsGraphicalInterpolationBuffer> InterpolationBufferType;
            public ComponentTypeHandle<PhysicsGraphicalSmoothing> PhysicsGraphicalSmoothingType;
            public ComponentTypeHandle<LocalToWorld> LocalToWorldType;
            public float TimeAhead;
            public float NormalizedTimeAhead;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                Assert.IsFalse(useEnabledMask);

                NativeArray<LocalTransform> localTransforms = chunk.GetNativeArray(ref LocalTransformType);
                NativeArray<PostTransformMatrix> postTransformMatrices = chunk.GetNativeArray(ref PostTransformMatrixType);
                NativeArray<PhysicsMass> physicsMasses = chunk.GetNativeArray(ref PhysicsMassType);
                NativeArray<PhysicsGraphicalSmoothing> physicsGraphicalSmoothings = chunk.GetNativeArray(ref PhysicsGraphicalSmoothingType);
                NativeArray<PhysicsGraphicalInterpolationBuffer> interpolationBuffers = chunk.GetNativeArray(ref InterpolationBufferType);
                NativeArray<LocalToWorld> localToWorlds = chunk.GetNativeArray(ref LocalToWorldType);


                // GameObjects with non-identity scale have their scale baked into their collision shape and mass, so
                // the entity's transform scale (if any) should not be applied again here. Entities that did not go
                // through baking should apply their uniform scale value to the rigid body.
                // Baking also adds a PostTransformMatrix component to apply the GameObject's authored scale in the
                // rendering code, so we test for that component to determine whether the entity's current scale
                // should be applied or ignored.
                // TODO(DOTS-7098): More robust check here?
                var hasPostTransformMatrix = postTransformMatrices.IsCreated;
                var hasLocalTransform = localTransforms.IsCreated;

                var hasPhysicsMass = physicsMasses.IsCreated;
                var hasInterpolationBuffer = interpolationBuffers.IsCreated;

                var defaultPhysicsMass = PhysicsMass.CreateKinematic(MassProperties.UnitSphere);
                for (int i = 0, count = chunk.Count; i < count; ++i)
                {
                    var physicsMass = hasPhysicsMass ? physicsMasses[i] : defaultPhysicsMass;
                    var smoothing = physicsGraphicalSmoothings[i];
                    var currentVelocity = smoothing.CurrentVelocity;


                    var currentTransform = hasLocalTransform ? new RigidTransform(localTransforms[i].Rotation, localTransforms[i].Position) : RigidTransform.identity;

                    RigidTransform smoothedTransform;

                    // apply no smoothing (i.e., teleported bodies)
                    if (smoothing.ApplySmoothing == 0 || TimeAhead == 0)
                    {
                        if (hasInterpolationBuffer && smoothing.ApplySmoothing != 0)
                            // When using interpolation the smoothed transform is one physics tick behind, if physics updated this frame we need to apply the state from the history buffer in order to stay one frame behind
                            smoothedTransform = interpolationBuffers[i].PreviousTransform;
                        else
                            smoothedTransform = currentTransform;
                    }
                    else
                    {
                        if (hasInterpolationBuffer)
                        {
                            smoothedTransform = GraphicalSmoothingUtility.Interpolate(
                                interpolationBuffers[i].PreviousTransform, currentTransform, NormalizedTimeAhead);
                        }
                        else
                        {
                            smoothedTransform = GraphicalSmoothingUtility.Extrapolate(
                                currentTransform, currentVelocity, physicsMass, TimeAhead);
                        }
                    }

                    localToWorlds[i] = GraphicalSmoothingUtility.BuildLocalToWorld(

                        i, smoothedTransform,
                        hasLocalTransform ? localTransforms[i].Scale : 1.0f,
                        hasPostTransformMatrix, postTransformMatrices
                    );

                    // reset smoothing to apply again next frame (i.e., finish teleportation)
                    smoothing.ApplySmoothing = 1;
                    physicsGraphicalSmoothings[i] = smoothing;
                }
            }
        }
    }
}
