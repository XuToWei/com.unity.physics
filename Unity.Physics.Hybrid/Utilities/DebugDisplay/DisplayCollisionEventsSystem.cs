#if !HAVOK_PHYSICS_EXISTS

using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Physics.Systems;

namespace Unity.Physics.Authoring
{
    // A system which draws any collision events produced by the physics step system
    [RequireMatchingQueriesForUpdate]
    [UpdateInGroup(typeof(AfterPhysicsSystemGroup))]
    [BurstCompile]
    internal partial struct DisplayCollisionEventsSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
#if UNITY_EDITOR
            if (!SystemAPI.TryGetSingleton(out PhysicsDebugDisplayData debugDisplay) || debugDisplay.DrawCollisionEvents == 0)
                return;

            state.Dependency = new DisplayCollisionEventsJob
            {
                World = SystemAPI.GetSingleton<PhysicsWorldSingleton>().PhysicsWorld
            }.Schedule(SystemAPI.GetSingleton<SimulationSingleton>(), state.Dependency);
#endif
        }

        // Job which iterates over collision events and writes display info to a PhysicsDebugDisplaySystem.
        [BurstCompile]
        private struct DisplayCollisionEventsJob : ICollisionEventsJob
        {
            [ReadOnly] public PhysicsWorld World;

            public unsafe void Execute(CollisionEvent collisionEvent)
            {
                CollisionEvent.Details details = collisionEvent.CalculateDetails(ref World);

                //Color code the impulse depending on the collision feature
                //vertex - blue
                //edge - cyan
                //face - magenta
                Unity.DebugDisplay.ColorIndex color;
                switch (details.EstimatedContactPointPositions.Length)
                {
                    case 1:
                        color = Unity.DebugDisplay.ColorIndex.Blue;
                        break;
                    case 2:
                        color = Unity.DebugDisplay.ColorIndex.Cyan;
                        break;
                    default:
                        color = Unity.DebugDisplay.ColorIndex.Magenta;
                        break;
                }

                var averageContactPosition = details.AverageContactPointPosition;
                PhysicsDebugDisplaySystem.Point(averageContactPosition, 0.01f, color);
                PhysicsDebugDisplaySystem.Arrow(averageContactPosition, collisionEvent.Normal * details.EstimatedImpulse, color);
            }
        }
    }
}

#endif
