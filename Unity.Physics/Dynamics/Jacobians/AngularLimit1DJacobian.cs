using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using static Unity.Physics.Math;

namespace Unity.Physics
{
    // Solve data for a constraint that limits one degree of angular freedom
    [NoAlias]
    struct AngularLimit1DJacobian
    {
        // Limited axis in motion A space
        // TODO could calculate this from AxisIndex and MotionAFromJoint
        public float3 AxisInMotionA;

        // Index of the limited axis
        public int AxisIndex;

        // Relative angle limits
        public float MinAngle;
        public float MaxAngle;

        // The max impulse that can be applied on this joint
        public float3 MaxImpulse;

        // Accumulated impulse applied over all steps
        public float3 AccumulatedImpulse;

        // The joint entity between the body pair
        Entity JointEntity;

        // When true, will send impulse events when max impulse is exceeded
        public bool EnableImpulseEvents;

        // Relative orientation of the motions before solving
        public quaternion MotionBFromA;

        // Rotation to joint space from motion space
        public quaternion MotionAFromJoint;
        public quaternion MotionBFromJoint;

        // Error before solving
        public float InitialError;

        // Fraction of the position error to correct per step
        public float Tau;

        // Fraction of the velocity error to correct per step
        public float Damping;

        // Build the Jacobian
        public void Build(
            Entity jointEntity, MTransform aFromConstraint, MTransform bFromConstraint,
            MotionVelocity velocityA, MotionVelocity velocityB,
            MotionData motionA, MotionData motionB,
            Constraint constraint, float tau, float damping)
        {
            // Copy the constraint into the jacobian
            AxisIndex = constraint.ConstrainedAxis1D;
            AxisInMotionA = aFromConstraint.Rotation[AxisIndex];
            MinAngle = constraint.Min;
            MaxAngle = constraint.Max;
            MaxImpulse = constraint.MaxImpulse;
            EnableImpulseEvents = constraint.EnableImpulseEvents;
            JointEntity = jointEntity;
            AccumulatedImpulse = float3.zero;
            Tau = tau;
            Damping = damping;
            MotionBFromA = math.mul(math.inverse(motionB.WorldFromMotion.rot), motionA.WorldFromMotion.rot);
            MotionAFromJoint = new quaternion(aFromConstraint.Rotation);
            MotionBFromJoint = new quaternion(bFromConstraint.Rotation);

            // Calculate the current error
            InitialError = CalculateError(MotionBFromA);
        }

        // Solve the Jacobian
        public void Solve(ref JacobianHeader jacHeader, ref MotionVelocity velocityA, ref MotionVelocity velocityB, Solver.StepInput stepInput, ref NativeStream.Writer impulseEventsWriter)
        {
            // Predict the relative orientation at the end of the step
            quaternion futureMotionBFromA = JacobianUtilities.IntegrateOrientationBFromA(MotionBFromA, velocityA.AngularVelocity, velocityB.AngularVelocity, stepInput.Timestep);

            // Calculate the effective mass
            float3 axisInMotionB = math.mul(futureMotionBFromA, -AxisInMotionA);
            float effectiveMass;
            {
                float invEffectiveMass = math.csum(AxisInMotionA * AxisInMotionA * velocityA.InverseInertia +
                    axisInMotionB * axisInMotionB * velocityB.InverseInertia);
                effectiveMass = math.select(1.0f / invEffectiveMass, 0.0f, invEffectiveMass == 0.0f);
            }

            // Calculate the error, adjust by tau and damping, and apply an impulse to correct it
            float futureError = CalculateError(futureMotionBFromA);
            float solveError = JacobianUtilities.CalculateCorrection(futureError, InitialError, Tau, Damping);
            float impulse = math.mul(effectiveMass, -solveError) * stepInput.InvTimestep;
            velocityA.ApplyAngularImpulse(impulse * AxisInMotionA);
            velocityB.ApplyAngularImpulse(impulse * axisInMotionB);

            AccumulatedImpulse[AxisIndex] += impulse;

            // if impulse exceeds max impulse, write back data
            if (EnableImpulseEvents && stepInput.IsLastIteration && math.any(math.abs(AccumulatedImpulse) > MaxImpulse))
            {
                impulseEventsWriter.Write(new ImpulseEventData
                {
                    Type = ConstraintType.Angular,
                    Impulse = AccumulatedImpulse,
                    JointEntity = JointEntity,
                    BodyIndices = jacHeader.BodyPair
                });
            }
        }

        // Helper function
        private float CalculateError(quaternion motionBFromA)
        {
            // Calculate the relative body rotation
            quaternion jointBFromA = math.mul(math.mul(math.inverse(MotionBFromJoint), motionBFromA), MotionAFromJoint);

            // Find the twist angle of the rotation.
            //
            // There is no one correct solution for the twist angle. Suppose the joint models a pair of bodies connected by
            // three gimbals, one of which is limited by this jacobian. There are multiple configurations of the gimbals that
            // give the bodies the same relative orientation, so it is impossible to determine the configuration from the
            // bodies' orientations alone, nor therefore the orientation of the limited gimbal.
            //
            // This code instead makes a reasonable guess, the twist angle of the swing-twist decomposition of the bodies'
            // relative orientation. It always works when the limited axis itself is unable to rotate freely, as in a limited
            // hinge. It works fairly well when the limited axis can only rotate a small amount, preferably less than 90
            // degrees. It works poorly at higher angles, especially near 180 degrees where it is not continuous. For systems
            // that require that kind of flexibility, the gimbals should be modeled as separate bodies.
            float angle = CalculateTwistAngle(jointBFromA, AxisIndex);

            // Angle is in [-2pi, 2pi].
            // For comparison against the limits, find k so that angle + 2k * pi is as close to [min, max] as possible.
            float centerAngle = (MinAngle + MaxAngle) / 2.0f;
            bool above = angle > (centerAngle + (float)math.PI);
            bool below = angle < (centerAngle - (float)math.PI);
            angle = math.select(angle, angle - 2.0f * (float)math.PI, above);
            angle = math.select(angle, angle + 2.0f * (float)math.PI, below);

            // Calculate the relative angle about the twist axis
            return JacobianUtilities.CalculateError(angle, MinAngle, MaxAngle);
        }
    }
}
