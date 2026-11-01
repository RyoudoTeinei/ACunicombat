using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechFlightTask : byte { Search, Navigation, Formation, Return, Combat, Evade, Disabled }

    /// <summary>A navigation objective built exclusively from local observations or received orders.</summary>
    public readonly struct MechNavigationObjective
    {
        public readonly MechFlightTask Task;
        public readonly Vector3 Position, Velocity;
        public readonly float MaximumRelativeSpeed;
        public MechNavigationObjective(MechFlightTask task, Vector3 position, Vector3 velocity, float speed)
        { Task = task; Position = position; Velocity = velocity; MaximumRelativeSpeed = speed; }
    }

    /// <summary>Priority selection happens before any controller advances its tactical state.</summary>
    public sealed class MechAiTaskDirector
    {
        public MechFlightTask Current { get; private set; } = MechFlightTask.Disabled;
        public MechFlightTask Suspended { get; private set; } = MechFlightTask.Disabled;
        public int TransitionCount { get; private set; }
        public MechFlightTask Select(bool alive, bool evade, bool hasTarget,
            bool navigationRequired, bool hasNavigation, MechFlightTask navigationTask)
        {
            var next = !alive ? MechFlightTask.Disabled : evade ? MechFlightTask.Evade :
                hasNavigation && (navigationRequired || !hasTarget) ? navigationTask :
                hasTarget ? MechFlightTask.Combat : MechFlightTask.Search;
            if (next == MechFlightTask.Evade && Current != MechFlightTask.Evade)
                Suspended = Current;
            // Re-evaluate the live order after interruption. Never resume a cancelled objective.
            if (next != MechFlightTask.Evade) Suspended = MechFlightTask.Disabled;
            if (Current != next) { Current = next; TransitionCount++; }
            return Current;
        }
        public void Reset() { Current = Suspended = MechFlightTask.Disabled; TransitionCount = 0; }
    }

    /// <summary>One translation controller shared by autonomous actors and explicit player autopilot.</summary>
    public static class MechAutonomousFlight
    {
        public static MechPlayerInputFrame Navigate(MechFlightMotionController motion,
            Vector3 position, in MechNavigationObjective objective, out Vector3 direction)
        {
            var delta = objective.Position - position;
            direction = delta.sqrMagnitude > 1f ? delta.normalized : motion.LogicalRotation * Vector3.forward;
            float toward = motion.EstimateTranslationAcceleration(direction);
            float away = motion.EstimateTranslationAcceleration(-direction);
            var requested = MechAiRulePlanner.CalculateNavigationAcceleration(delta, objective.Velocity,
                motion.Velocity, objective.MaximumRelativeSpeed, toward, away,
                Mathf.Max(toward, away, 0.1f), 2f);
            return Acceleration(motion, requested);
        }

        public static MechPlayerInputFrame Acceleration(MechFlightMotionController motion, Vector3 requested,
            bool quickBoost = false)
        {
            if (!Finite(requested)) requested = Vector3.zero;
            float capacity = requested.sqrMagnitude > 0.0001f
                ? Mathf.Max(0f, motion.EstimateTranslationAcceleration(requested.normalized)) : 0f;
            var acceleration = Vector3.ClampMagnitude(requested, capacity);
            var local = Quaternion.Inverse(motion.LogicalRotation) * acceleration / Mathf.Max(capacity, 0.1f);
            return new MechPlayerInputFrame {
                HasAutonomousFlightCommand = true, AutonomousAccelerationWorld = acceleration,
                MoveLocal = new Vector2(local.x, local.z), RiseHeld = local.y > 0.05f,
                DescendHeld = local.y < -0.05f, QuickBoostEdge = quickBoost
            };
        }
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }

    /// <summary>A stable course frame from received leader velocity, with slot rotational feedforward.</summary>
    public sealed class MechFormationCourse
    {
        private Guid leader;
        private double time;
        private Vector3 forward = Vector3.forward;
        private Vector3 previousOffset;
        private bool initialized;
        public void Resolve(Guid source, double now, Vector3 velocity, Vector3 offset,
            out Vector3 worldOffset, out Vector3 slotVelocity)
        {
            bool reset = !initialized || source != leader || now < time || now - time > 3;
            double dt = reset ? 0 : now - time;
            if (reset) forward = velocity.sqrMagnitude > 100 ? velocity.normalized : Vector3.forward;
            else if (velocity.sqrMagnitude > 100 && dt > 0)
                forward = Vector3.RotateTowards(forward, velocity.normalized, (float)dt * 20f * Mathf.Deg2Rad, 0);
            // Parallel-transport the right axis near vertical flight to avoid pole flips.
            var side = Vector3.ProjectOnPlane(right, forward);
            if (reset || side.sqrMagnitude < 0.001f) side = Vector3.Cross(Vector3.up, forward);
            if (side.sqrMagnitude < 0.001f) side = Vector3.Cross(Vector3.right, forward);
            right = side.normalized;
            worldOffset = right * offset.x + Vector3.Cross(forward, right).normalized * offset.y + forward * offset.z;
            slotVelocity = velocity;
            if (!reset && dt > 0.0001) slotVelocity += (worldOffset - previousOffset) / (float)dt;
            if (reset || dt > 0) { previousOffset = worldOffset; time = now; }
            leader = source; initialized = true;
        }
        private Vector3 right = Vector3.right;
    }
}
