using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechAiEvasionMode : byte { None, Cold, Beam, Terminal }

    /// <summary>Observed state of the most urgent incoming missile, from public missile tracks only.</summary>
    public readonly struct MechAiMissileThreat
    {
        public readonly Vector3 Position, Velocity;
        public readonly float TimeToImpactSeconds;
        public MechAiMissileThreat(Vector3 position, Vector3 velocity, float timeToImpactSeconds)
        { Position = position; Velocity = velocity; TimeToImpactSeconds = timeToImpactSeconds; }
    }

    public readonly struct MechAiEvasionPlan
    {
        public readonly MechAiEvasionMode Mode;
        /// <summary>Unit world direction the airframe should accelerate toward.</summary>
        public readonly Vector3 Direction;
        public readonly bool QuickBoost, ReleaseCountermeasures;
        public MechAiEvasionPlan(MechAiEvasionMode mode, Vector3 direction, bool quickBoost, bool countermeasures)
        { Mode = mode; Direction = direction; QuickBoost = quickBoost; ReleaseCountermeasures = countermeasures; }
        public bool Active => Mode != MechAiEvasionMode.None;
    }

    /// <summary>
    /// Timed missile defence after Sea Power's AdvancedMissileEvasion: do nothing
    /// until the remaining time-to-impact approaches what the airframe needs to
    /// build the escape, then either drag the missile cold (it has little speed
    /// advantage) or beam it (perpendicular to its velocity) on the side the
    /// airframe already drifts toward. The last moments add a QuickBoost jink
    /// and a countermeasure release.
    /// </summary>
    public static class MechAiEvasionDoctrine
    {
        public const float ColdSpeedAdvantageMetersPerSecond = 120f;
        public const float ColdMinimumImpactSeconds = 6f;
        public const float ColdMarginSeconds = 2f;
        public const float BeamMarginSeconds = 1.5f;
        public const float TerminalSeconds = 1.5f;
        public const float BeamAspectSpeedMetersPerSecond = 80f;

        /// <param name="ownSpeedCapacity">Speed the airframe can sustain in the escape direction.</param>
        /// <param name="lateralAcceleration">Available translation acceleration perpendicular to the threat.</param>
        public static MechAiEvasionPlan Evaluate(
            in MechAiMissileThreat threat,
            Vector3 ownPosition,
            Vector3 ownVelocity,
            Vector3 referenceUp,
            float ownSpeedCapacity,
            float lateralAcceleration,
            bool alreadyEvading)
        {
            var toSelf = ownPosition - threat.Position;
            float range = toSelf.magnitude;
            float tti = Mathf.Max(0f, threat.TimeToImpactSeconds);
            if (range < 0.01f || !float.IsFinite(tti)) return default;
            var lineOfSight = toSelf / range;
            var missileVelocity = threat.Velocity.sqrMagnitude > 0.01f ? threat.Velocity : -lineOfSight * 300f;
            float accel = Mathf.Max(1f, lateralAcceleration);

            if (tti <= TerminalSeconds)
                return new MechAiEvasionPlan(MechAiEvasionMode.Terminal,
                    BeamDirection(missileVelocity, lineOfSight, ownVelocity, referenceUp), true, true);

            float advantage = missileVelocity.magnitude - Mathf.Max(0f, ownSpeedCapacity);
            bool cold = advantage <= ColdSpeedAdvantageMetersPerSecond && tti >= ColdMinimumImpactSeconds;
            if (cold)
            {
                // Time to rotate the velocity vector onto the line of sight away from the missile.
                float awaySpeed = Vector3.Dot(ownVelocity, lineOfSight);
                float needed = Mathf.Max(0f, ownSpeedCapacity - awaySpeed) / accel;
                if (alreadyEvading || tti <= needed + ColdMarginSeconds)
                    return new MechAiEvasionPlan(MechAiEvasionMode.Cold, lineOfSight, false, false);
                return default;
            }

            // Beam: build enough perpendicular speed to defeat the missile's turn.
            var beam = BeamDirection(missileVelocity, lineOfSight, ownVelocity, referenceUp);
            float beamSpeed = Vector3.Dot(ownVelocity, beam);
            float beamNeeded = Mathf.Max(0f, BeamAspectSpeedMetersPerSecond - beamSpeed) / accel;
            if (alreadyEvading || tti <= beamNeeded + BeamMarginSeconds)
                return new MechAiEvasionPlan(MechAiEvasionMode.Beam, beam, false, false);
            return default;
        }

        /// <summary>Perpendicular to the missile velocity, choosing the side the airframe already moves toward.</summary>
        public static Vector3 BeamDirection(Vector3 missileVelocity, Vector3 lineOfSight, Vector3 ownVelocity, Vector3 referenceUp)
        {
            var axis = missileVelocity.sqrMagnitude > 0.01f ? missileVelocity.normalized : -lineOfSight;
            var lateral = Vector3.ProjectOnPlane(ownVelocity, axis);
            if (lateral.sqrMagnitude < 4f)
            {
                lateral = Vector3.Cross(referenceUp, axis);
                if (lateral.sqrMagnitude < 0.001f) lateral = Vector3.Cross(Vector3.right, axis);
            }
            return lateral.normalized;
        }
    }
}
