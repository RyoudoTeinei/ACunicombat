using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechAiManeuverState : byte
    {
        Search = 0,
        Intercept = 1,
        FiringRun = 2,
        ObliqueEngage = 3,
        PostLaunchBreak = 4,
        Extend = 5,
        Defensive = 6,
        Recover = 7,
        Navigate = 8,
        Disabled = 9,
    }

    public enum MechAiAttitudeIntent : byte
    {
        TrackTarget = 0,
        LeadWeapon = 1,
        Search = 2,
        EmergencyManeuver = 3,
    }

    public readonly struct MechAiManeuverCommand
    {
        public MechAiManeuverCommand(
            MechAiManeuverState state,
            Vector3 accelerationWorld,
            Vector3 maneuverDirection,
            MechAiAttitudeIntent attitudeIntent,
            bool urgentBraking)
        {
            State = state;
            AccelerationWorld = accelerationWorld;
            ManeuverDirection = maneuverDirection;
            AttitudeIntent = attitudeIntent;
            UrgentBraking = urgentBraking;
        }

        public MechAiManeuverState State { get; }
        public Vector3 AccelerationWorld { get; }
        public Vector3 ManeuverDirection { get; }
        public MechAiAttitudeIntent AttitudeIntent { get; }
        public bool UrgentBraking { get; }
    }

    /// <summary>
    /// Stateful tactical intent above the continuous spatial controller. Target
    /// positions and velocities always come from public tracks, never Transforms.
    /// </summary>
    public sealed class MechAiManeuverPlanner
    {
        public const float PostLaunchDurationSeconds = 0.75f;
        public const float PostLaunchRetriggerSeconds = 1.8f;

        private MechAiManeuverState state = MechAiManeuverState.Search;
        private float engagementCommitUntil;
        private float postLaunchUntil = float.NegativeInfinity;
        private float nextPostLaunchTime;
        private Vector3 postLaunchDirection;
        private bool recovering;

        public MechAiManeuverState CurrentState => state;
        public float PostLaunchRemainingSeconds =>
            Mathf.Max(0f, postLaunchUntil - lastTime);
        public float EngagementCommitRemainingSeconds =>
            Mathf.Max(0f, engagementCommitUntil - lastTime);
        public Vector3 PostLaunchDirection => postLaunchDirection;
        private float lastTime;

        public void Reset()
        {
            state = MechAiManeuverState.Disabled;
            engagementCommitUntil = 0f;
            postLaunchUntil = float.NegativeInfinity;
            nextPostLaunchTime = 0f;
            postLaunchDirection = Vector3.zero;
            recovering = false;
            lastTime = 0f;
        }

        public void ResetEngagement()
        {
            if (state != MechAiManeuverState.PostLaunchBreak)
                state = MechAiManeuverState.Search;
            engagementCommitUntil = 0f;
            recovering = false;
        }

        public bool NotifyWeaponCommitted(
            MechWeaponRole role,
            MechPartSlot slot,
            Quaternion logicalRotation,
            Vector3 directionToTarget,
            float now)
        {
            lastTime = now;
            if (role != MechWeaponRole.AirToAirMissile ||
                (slot != MechPartSlot.LeftBackWeapon &&
                 slot != MechPartSlot.RightBackWeapon) ||
                now < nextPostLaunchTime || now < postLaunchUntil)
                return false;

            var radial = directionToTarget.sqrMagnitude > 0.0001f
                ? directionToTarget.normalized
                : logicalRotation * Vector3.forward;
            var bodySide = logicalRotation * Vector3.right *
                           (slot == MechPartSlot.LeftBackWeapon ? 1f : -1f);
            var lateral = Vector3.ProjectOnPlane(bodySide, radial);
            if (lateral.sqrMagnitude < 0.001f)
                lateral = Vector3.ProjectOnPlane(logicalRotation * Vector3.up, radial);
            if (lateral.sqrMagnitude < 0.001f)
                lateral = bodySide;
            postLaunchDirection = lateral.normalized;
            postLaunchUntil = now + PostLaunchDurationSeconds;
            nextPostLaunchTime = now + PostLaunchRetriggerSeconds;
            lastTime = now;
            state = MechAiManeuverState.PostLaunchBreak;
            engagementCommitUntil = 0f;
            return true;
        }

        public MechAiManeuverCommand Resolve(
            Vector3 relativePosition,
            Vector3 relativeVelocity,
            Vector3 orbitTangent,
            MechAiTargetController.CombatState requestedState,
            in MechAiCombatDoctrine doctrine,
            FleetTaskGroup taskGroup,
            bool hasTask,
            bool hasSpatialMission,
            bool hasOffensiveWeapon,
            bool weaponReady,
            float energyNormalized,
            float towardAcceleration,
            float awayAcceleration,
            float maximumAcceleration,
            float responseSeconds,
            float now)
        {
            lastTime = now;
            var defensive = requestedState == MechAiTargetController.CombatState.Defensive;
            var postLaunch = !defensive && now < postLaunchUntil &&
                             postLaunchDirection.sqrMagnitude > 0.5f;
            var candidate = SelectState(
                requestedState,
                taskGroup,
                hasTask,
                hasSpatialMission,
                hasOffensiveWeapon,
                weaponReady,
                energyNormalized,
                defensive,
                postLaunch,
                now);
            if (candidate != state)
            {
                state = candidate;
                engagementCommitUntil = now + CommitDuration(candidate);
            }

            if (state == MechAiManeuverState.PostLaunchBreak)
            {
                var breakAcceleration = CalculatePostLaunchAcceleration(
                    relativePosition,
                    relativeVelocity,
                    postLaunchDirection,
                    awayAcceleration,
                    maximumAcceleration,
                    responseSeconds,
                    energyNormalized);
                return new MechAiManeuverCommand(
                    state,
                    breakAcceleration,
                    breakAcceleration.sqrMagnitude > 0.001f
                        ? breakAcceleration.normalized
                        : postLaunchDirection,
                    MechAiAttitudeIntent.TrackTarget,
                    false);
            }

            var maneuverDoctrine = doctrine;
            var orbitScale = OrbitScale(state, doctrine.TacticalRole);
            maneuverDoctrine = new MechAiCombatDoctrine(
                doctrine.TacticalRole,
                doctrine.EngagementBand,
                doctrine.OrbitSpeedMetersPerSecond * orbitScale);
            if (state == MechAiManeuverState.Recover)
            {
                var band = doctrine.EngagementBand;
                var preferred = Mathf.Max(
                    band.PreferredRangeMeters,
                    band.MaximumRangeMeters);
                maneuverDoctrine = new MechAiCombatDoctrine(
                    doctrine.TacticalRole,
                    new MechAiEngagementBand(
                        Mathf.Max(band.MinimumRangeMeters, preferred * 0.9f),
                        preferred,
                        Mathf.Max(band.MaximumRangeMeters, preferred * 1.15f)),
                    doctrine.OrbitSpeedMetersPerSecond * 0.45f);
            }

            var spatialState = SpatialState(state);
            var tangent = state == MechAiManeuverState.FiringRun &&
                          IsHighAngularRate(relativePosition, relativeVelocity)
                ? Vector3.ProjectOnPlane(orbitTangent, relativePosition).normalized * 0.35f
                : orbitTangent;
            var acceleration = MechAiRulePlanner.CalculateSpatialAcceleration(
                relativePosition,
                relativeVelocity,
                tangent,
                spatialState,
                in maneuverDoctrine,
                towardAcceleration,
                awayAcceleration,
                maximumAcceleration,
                responseSeconds,
                out var urgentBraking,
                EngagementLeadSeconds(state, doctrine.TacticalRole, relativePosition, relativeVelocity));
            if (urgentBraking && !defensive && orbitTangent.sqrMagnitude > 0.01f)
            {
                // A close, fast pass must not become a straight tail chase or a
                // pure retrograde stop. Keep a bounded lateral escape component.
                var range = relativePosition.magnitude;
                var closingSpeed = relativePosition.sqrMagnitude > 0.001f
                    ? -Vector3.Dot(relativeVelocity, relativePosition.normalized)
                    : 0f;
                if (closingSpeed > 20f &&
                    range < doctrine.EngagementBand.PreferredRangeMeters * 1.5f)
                {
                    var lateralEscape = orbitTangent.normalized * Mathf.Min(
                        maximumAcceleration * 0.3f,
                        closingSpeed * 0.5f);
                    acceleration = Vector3.ClampMagnitude(
                        acceleration + lateralEscape,
                        maximumAcceleration);
                }
            }

            if (energyNormalized < 0.22f && !defensive)
                acceleration *= Mathf.Lerp(0.45f, 0.8f, Mathf.Clamp01(energyNormalized / 0.22f));
            var attitude = urgentBraking || defensive
                ? MechAiAttitudeIntent.EmergencyManeuver
                : state == MechAiManeuverState.FiringRun
                    ? MechAiAttitudeIntent.LeadWeapon
                    : weaponReady || state == MechAiManeuverState.PostLaunchBreak
                        ? MechAiAttitudeIntent.TrackTarget
                        : state == MechAiManeuverState.Extend
                            ? MechAiAttitudeIntent.Search
                            : MechAiAttitudeIntent.TrackTarget;
            return new MechAiManeuverCommand(
                state,
                acceleration,
                acceleration.sqrMagnitude > 0.001f
                    ? acceleration.normalized
                    : relativePosition.normalized,
                attitude,
                urgentBraking);
        }

        public static Vector3 CalculatePostLaunchAcceleration(
            Vector3 relativePosition,
            Vector3 relativeVelocity,
            Vector3 breakDirection,
            float radialAccelerationCapacity,
            float maximumAcceleration,
            float responseSeconds,
            float energyNormalized = 1f)
        {
            var range = relativePosition.magnitude;
            if (range < 0.01f || breakDirection.sqrMagnitude < 0.001f)
                return Vector3.zero;
            var response = Mathf.Max(0.2f, responseSeconds);
            var maximum = Mathf.Max(0f, maximumAcceleration);
            var radial = relativePosition / range;
            var lateral = Vector3.ProjectOnPlane(breakDirection, radial).normalized;
            var closingSpeed = -Vector3.Dot(relativeVelocity, radial);
            var radialAcceleration = Mathf.Clamp(
                -closingSpeed / response,
                -Mathf.Max(0.1f, radialAccelerationCapacity),
                Mathf.Max(0.1f, radialAccelerationCapacity));
            radialAcceleration = Mathf.Clamp(radialAcceleration, -maximum, maximum);
            var lateralBudget = Mathf.Sqrt(Mathf.Max(0f,
                maximum * maximum - radialAcceleration * radialAcceleration));
            var breakSpeed = Mathf.Min(150f, Mathf.Sqrt(maximum * 0.3f * range));
            var relativeTangent = Vector3.ProjectOnPlane(relativeVelocity, radial);
            var lateralAcceleration = Vector3.ClampMagnitude(
                (relativeTangent + lateral * breakSpeed) / response,
                lateralBudget);
            var energyScale = energyNormalized < 0.22f
                ? Mathf.Lerp(0.45f, 0.8f, Mathf.Clamp01(energyNormalized / 0.22f))
                : 1f;
            return (radial * radialAcceleration + lateralAcceleration) * energyScale;
        }

        private MechAiManeuverState SelectState(
            MechAiTargetController.CombatState requestedState,
            FleetTaskGroup taskGroup,
            bool hasTask,
            bool hasSpatialMission,
            bool hasOffensiveWeapon,
            bool weaponReady,
            float energyNormalized,
            bool defensive,
            bool postLaunch,
            float now)
        {
            if (defensive) return MechAiManeuverState.Defensive;
            if (postLaunch) return MechAiManeuverState.PostLaunchBreak;
            if (requestedState == MechAiTargetController.CombatState.Intercept)
            {
                recovering = false;
                return MechAiManeuverState.Intercept;
            }
            if (requestedState == MechAiTargetController.CombatState.Extend)
            {
                recovering = false;
                return MechAiManeuverState.Extend;
            }
            if (energyNormalized < 0.16f) recovering = true;
            else if (energyNormalized > 0.38f) recovering = false;
            if (recovering || !hasOffensiveWeapon)
                return MechAiManeuverState.Recover;

            var passiveMission = !hasTask && !hasSpatialMission &&
                (taskGroup == FleetTaskGroup.Reconnaissance ||
                 taskGroup == FleetTaskGroup.ElectronicWarfare ||
                 taskGroup == FleetTaskGroup.EarlyWarningCommand ||
                 taskGroup == FleetTaskGroup.Logistics);
            if (passiveMission) return MechAiManeuverState.Recover;

            var candidate = weaponReady
                ? MechAiManeuverState.FiringRun
                : MechAiManeuverState.ObliqueEngage;
            if ((state == MechAiManeuverState.FiringRun ||
                 state == MechAiManeuverState.ObliqueEngage) &&
                now < engagementCommitUntil)
                return state;
            return candidate;
        }

        private static float CommitDuration(MechAiManeuverState value)
        {
            return value switch
            {
                MechAiManeuverState.FiringRun => 1.4f,
                MechAiManeuverState.ObliqueEngage => 1.8f,
                MechAiManeuverState.Recover => 2f,
                _ => 0f,
            };
        }

        private static MechAiTargetController.CombatState SpatialState(
            MechAiManeuverState value)
        {
            return value switch
            {
                MechAiManeuverState.Intercept => MechAiTargetController.CombatState.Intercept,
                MechAiManeuverState.Extend => MechAiTargetController.CombatState.Extend,
                MechAiManeuverState.Defensive => MechAiTargetController.CombatState.Defensive,
                _ => MechAiTargetController.CombatState.Engage,
            };
        }

        private static float OrbitScale(
            MechAiManeuverState value,
            MechAiTacticalWeaponRole role)
        {
            if (value != MechAiManeuverState.FiringRun) return 1f;
            return role switch
            {
                MechAiTacticalWeaponRole.CloseAutomatic => 0.85f,
                MechAiTacticalWeaponRole.GeneralAutomatic => 0.65f,
                MechAiTacticalWeaponRole.PrecisionGun => 0.25f,
                MechAiTacticalWeaponRole.HeavyCannon => 0.2f,
                MechAiTacticalWeaponRole.CloseRangeMissile => 0.65f,
                MechAiTacticalWeaponRole.StandoffMissile => 0.5f,
                _ => 0.65f,
            };
        }

        private static bool IsHighAngularRate(
            Vector3 relativePosition,
            Vector3 relativeVelocity)
        {
            var range = relativePosition.magnitude;
            return range > 1f &&
                   Vector3.ProjectOnPlane(relativeVelocity, relativePosition).magnitude / range > 0.12f;
        }

        private static float EngagementLeadSeconds(
            MechAiManeuverState state,
            MechAiTacticalWeaponRole role,
            Vector3 relativePosition,
            Vector3 relativeVelocity)
        {
            if (state == MechAiManeuverState.Intercept ||
                state == MechAiManeuverState.Defensive)
                return 0f;
            if (IsHighAngularRate(relativePosition, relativeVelocity)) return 1.2f;
            return state == MechAiManeuverState.FiringRun &&
                   (role == MechAiTacticalWeaponRole.PrecisionGun ||
                    role == MechAiTacticalWeaponRole.HeavyCannon)
                ? 0.55f
                : 0f;
        }
    }
}
