using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using static Unity.Physics.PhysicsStep;

namespace Unity.Physics.Authoring
{
    [AddComponentMenu("Entities/Physics/Physics Step")]
    [DisallowMultipleComponent]
    [HelpURL(HelpURLs.PhysicsStepAuthoring)]
    public sealed class PhysicsStepAuthoring : MonoBehaviour
    {
        PhysicsStepAuthoring() {}

        public SimulationType SimulationType
        {
            get => m_SimulationType;
            set => m_SimulationType = value;
        }
        [SerializeField]
        [Tooltip("Specifies the type of the physics simulation to be executed.")]
        SimulationType m_SimulationType = Default.SimulationType;

        public float3 Gravity
        {
            get => m_Gravity;
            set => m_Gravity = value;
        }
        [SerializeField]
        float3 m_Gravity = Default.Gravity;

        public int SolverIterationCount
        {
            get => m_SolverIterationCount;
            set => m_SolverIterationCount = value;
        }
        [SerializeField]
        [Tooltip("Specifies the number of solver iterations the physics engine will perform. Higher values mean more stability, but also worse performance.")]
        int m_SolverIterationCount = Default.SolverIterationCount;

        public bool EnableSolverStabilizationHeuristic
        {
            get => m_EnableSolverStabilizationHeuristic;
            set => m_EnableSolverStabilizationHeuristic = value;
        }
        [SerializeField]
        bool m_EnableSolverStabilizationHeuristic = Default.SolverStabilizationHeuristicSettings.EnableSolverStabilization;

        public bool MultiThreaded
        {
            get => m_MultiThreaded;
            set => m_MultiThreaded = value;
        }
        [SerializeField]
        [Tooltip("True will go wide with the number of threads and jobs. " +
            "False will result in a simulation with very small number of single threaded jobs.")]
        bool m_MultiThreaded = Default.MultiThreaded > 0 ? true : false;

        public bool SynchronizeCollisionWorld
        {
            get => m_SynchronizeCollisionWorld;
            set => m_SynchronizeCollisionWorld = value;
        }
        [SerializeField]
        [Tooltip("Specifies whether to update the collision world after the step for more precise queries.")]
        bool m_SynchronizeCollisionWorld = Default.SynchronizeCollisionWorld > 0 ? true : false;

        internal PhysicsStep AsComponent => new PhysicsStep
        {
            SimulationType = SimulationType,
            Gravity = Gravity,
            SolverIterationCount = SolverIterationCount,
            SolverStabilizationHeuristicSettings = EnableSolverStabilizationHeuristic ?
                new Solver.StabilizationHeuristicSettings
            {
                EnableSolverStabilization = true,
                EnableFrictionVelocities = Default.SolverStabilizationHeuristicSettings.EnableFrictionVelocities,
                VelocityClippingFactor = Default.SolverStabilizationHeuristicSettings.VelocityClippingFactor,
                InertiaScalingFactor = Default.SolverStabilizationHeuristicSettings.InertiaScalingFactor
            } :
            Solver.StabilizationHeuristicSettings.Default,
            MultiThreaded = (byte)(MultiThreaded ? 1 : 0),
            SynchronizeCollisionWorld = (byte)(SynchronizeCollisionWorld ? 1 : 0)
        };

        void OnValidate()
        {
            SolverIterationCount = math.max(1, SolverIterationCount);
        }
    }

    internal class PhysicsStepBaker : Baker<PhysicsStepAuthoring>
    {
        public override void Bake(PhysicsStepAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, authoring.AsComponent);
        }
    }
}
