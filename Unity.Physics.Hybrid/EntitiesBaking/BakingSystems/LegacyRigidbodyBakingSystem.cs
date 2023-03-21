#if LEGACY_PHYSICS
using System.Collections.Generic;
using Unity.Entities;
using Unity.Physics.GraphicsIntegration;
using UnityEngine;
using LegacyRigidBody = UnityEngine.Rigidbody;
using LegacyCollider = UnityEngine.Collider;
using LegacyBox = UnityEngine.BoxCollider;
using LegacyCapsule = UnityEngine.CapsuleCollider;
using LegacyMesh = UnityEngine.MeshCollider;
using LegacySphere = UnityEngine.SphereCollider;

namespace Unity.Physics.Authoring
{
    [TemporaryBakingType]
    public struct LegacyRigidBodyBakingData : IComponentData
    {
        public bool isKinematic;
        public float mass;
    }

    class LegacyRigidBodyBaker : BasePhysicsBaker<LegacyRigidBody>
    {
        public static List<UnityEngine.Collider> colliderComponents = new List<UnityEngine.Collider>();
        public static List<PhysicsShapeAuthoring> physicsShapeComponents = new List<PhysicsShapeAuthoring>();

        public override void Bake(LegacyRigidBody authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new LegacyRigidBodyBakingData
            {
                isKinematic = authoring.isKinematic,
                mass = authoring.mass
            });

            // PhysicsBodyAuthoring has priority, ignore the LegacyRigidBody
            if (GetComponent<PhysicsBodyAuthoring>() != null)
                return;

            AddSharedComponent(entity, new PhysicsWorldIndex());

            var bodyTransform = GetComponent<Transform>();

            var motionType = authoring.isKinematic ? BodyMotionType.Kinematic : BodyMotionType.Dynamic;
            var hasInterpolation = authoring.interpolation != RigidbodyInterpolation.None;
            PostProcessTransform(bodyTransform, motionType);

            // Check that there is at least one collider in the hierarchy to add these three
            GetComponentsInChildren(colliderComponents);
            GetComponentsInChildren(physicsShapeComponents);
            if (colliderComponents.Count > 0 || physicsShapeComponents.Count > 0)
            {
                AddComponent(entity, new PhysicsCompoundData()
                {
                    AssociateBlobToBody = false,
                    ConvertedBodyInstanceID = authoring.GetInstanceID(),
                    Hash = default,
                });
                AddComponent<PhysicsRootBaked>(entity);
                AddComponent<PhysicsCollider>(entity);
                AddBuffer<PhysicsColliderKeyEntityPair>(entity);
            }

            // Ignore the rest if the object is static
            if (IsStatic())
                return;

            if (hasInterpolation)
            {
                AddComponent(entity, new PhysicsGraphicalSmoothing());

                if (authoring.interpolation == RigidbodyInterpolation.Interpolate)
                {
                    AddComponent(entity, new PhysicsGraphicalInterpolationBuffer
                    {
                        PreviousTransform = Math.DecomposeRigidBodyTransform(bodyTransform.localToWorldMatrix)
                    });
                }
            }

            // We add the component here with default values, so it can be reverted when the baker rebakes
            // The values will be rewritten in the system if needed
            var massProperties = MassProperties.UnitSphere;
            AddComponent(entity, !authoring.isKinematic ?
                PhysicsMass.CreateDynamic(massProperties, authoring.mass) :
                PhysicsMass.CreateKinematic(massProperties));

            AddComponent(entity, new PhysicsVelocity());

            if (!authoring.isKinematic)
            {
                AddComponent(entity, new PhysicsDamping
                {
                    Linear = authoring.drag,
                    Angular = authoring.angularDrag
                });
                if (!authoring.useGravity)
                    AddComponent(entity, new PhysicsGravityFactor { Value = 0f });
            }
            else
                AddComponent(entity, new PhysicsGravityFactor { Value = 0 });
        }
    }

    [UpdateAfter(typeof(PhysicsBodyBakingSystem))]
    [WorldSystemFilter(WorldSystemFilterFlags.BakingSystem)]
    public partial class LegacyRigidbodyBakingSystem : SystemBase
    {
        static SimulationMode k_InvalidSimulationMode = (SimulationMode) ~0;
        SimulationMode m_SavedSmulationMode = k_InvalidSimulationMode;
        bool m_ProcessSimulationModeChange;

        protected override void OnCreate()
        {
            m_ProcessSimulationModeChange = Application.isPlaying;

            RequireForUpdate<LegacyRigidBodyBakingData>();
        }

        protected override void OnDestroy()
        {
            // Unless no legacy physics step data is available, restore previously stored legacy physics simulation mode
            // when leaving play-mode.
            if (m_SavedSmulationMode != k_InvalidSimulationMode)
            {
                UnityEngine.Physics.simulationMode = m_SavedSmulationMode;
                m_SavedSmulationMode = k_InvalidSimulationMode;
            }
        }

        protected override void OnUpdate()
        {
            if (m_ProcessSimulationModeChange)
            {
                // When entering playmode, cache and override legacy physics simulation mode, disabling legacy physics and this way
                // preventing it from running while playing with open sub-scenes. Otherwise, the legacy physics simulation
                // will overwrite the last known edit mode state of game objects in the sub-scene with its simulation results.
                m_SavedSmulationMode = UnityEngine.Physics.simulationMode;
                UnityEngine.Physics.simulationMode = SimulationMode.Script;

                m_ProcessSimulationModeChange = false;
            }

            // Fill in the MassProperties based on the potential calculated value by BuildCompoundColliderBakingSystem
            foreach (var(physicsMass, bodyData, collider) in
                     SystemAPI.Query<RefRW<PhysicsMass>, RefRO<LegacyRigidBodyBakingData>, RefRO<PhysicsCollider>>().WithOptions(EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities))
            {
                // Build mass component
                var massProperties = collider.ValueRO.MassProperties;

                // n.b. no way to know if CoM was manually adjusted, so all legacy Rigidbody objects use auto CoM
                physicsMass.ValueRW = !bodyData.ValueRO.isKinematic ?
                    PhysicsMass.CreateDynamic(massProperties, bodyData.ValueRO.mass) :
                    PhysicsMass.CreateKinematic(massProperties);
            }
        }
    }
}
#endif
