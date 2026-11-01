using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>What a formation slot is for. Like Sea Power's StationRole, the role selects unit behaviour.</summary>
    public enum MechStationRole : byte { Default, Attacker, Escort, Cover, Shooter, Bait, Jammer, Scout }

    /// <summary>
    /// Station-keeping character. Airliner: wide, loose, smooth (air defence,
    /// early warning, logistics). Assault: tight, constantly weaving laterally,
    /// boosts back into the slot (strike, air superiority, suppression).
    /// </summary>
    public enum MechFormationStyle : byte { Airliner, Assault }

    /// <summary>Distance band of a member relative to its slot.</summary>
    public enum MechStationBand : byte { Far, Joining, OnStation }

    public readonly struct MechStationStyleParameters
    {
        public readonly float SpacingScale, JoinRadius, OnStationRadius, ReleaseRadius;
        public readonly float GainSeconds, CatchUpSpeed, WeaveAmplitude, WeavePeriodSeconds;
        public readonly bool BoostToRejoin;
        public MechStationStyleParameters(float spacingScale, float joinRadius, float onStationRadius, float releaseRadius,
            float gainSeconds, float catchUpSpeed, float weaveAmplitude, float weavePeriodSeconds, bool boostToRejoin)
        {
            SpacingScale = spacingScale; JoinRadius = joinRadius; OnStationRadius = onStationRadius; ReleaseRadius = releaseRadius;
            GainSeconds = gainSeconds; CatchUpSpeed = catchUpSpeed; WeaveAmplitude = weaveAmplitude;
            WeavePeriodSeconds = weavePeriodSeconds; BoostToRejoin = boostToRejoin;
        }
    }

    /// <summary>Pure formation rules: role assignment, style defaults and shooter deconfliction.</summary>
    public static class MechFormationDoctrine
    {
        public static MechFormationStyle DefaultStyle(FleetTaskGroup group) => group switch
        {
            FleetTaskGroup.Strike or FleetTaskGroup.AirSuperiority or FleetTaskGroup.AirDefenseSuppression => MechFormationStyle.Assault,
            _ => MechFormationStyle.Airliner,
        };

        public static MechStationStyleParameters Parameters(MechFormationStyle style) => style switch
        {
            MechFormationStyle.Assault => new MechStationStyleParameters(
                spacingScale: 1f, joinRadius: 900f, onStationRadius: 60f, releaseRadius: 160f,
                gainSeconds: 1.1f, catchUpSpeed: 220f, weaveAmplitude: 45f, weavePeriodSeconds: 6.5f, boostToRejoin: true),
            _ => new MechStationStyleParameters(
                spacingScale: 2.5f, joinRadius: 2000f, onStationRadius: 150f, releaseRadius: 350f,
                gainSeconds: 3f, catchUpSpeed: 160f, weaveAmplitude: 0f, weavePeriodSeconds: 1f, boostToRejoin: false),
        };

        /// <summary>
        /// Automatic slot role from the task group and the member's own loadout.
        /// A role forced by the commander (non-Default) always wins.
        /// </summary>
        public static MechStationRole ResolveRole(FleetTaskGroup group, int formationIndex, bool isLeader,
            bool hasHeavyOffensiveWeapon, bool hasPassiveRfWeapon, bool hasJammer, MechStationRole forced = MechStationRole.Default)
        {
            if (forced != MechStationRole.Default) return forced;
            switch (group)
            {
                case FleetTaskGroup.Strike:
                    return hasHeavyOffensiveWeapon ? MechStationRole.Attacker : MechStationRole.Escort;
                case FleetTaskGroup.Escort:
                    return MechStationRole.Escort;
                case FleetTaskGroup.AirDefenseSuppression:
                    return hasPassiveRfWeapon ? MechStationRole.Shooter : MechStationRole.Bait;
                case FleetTaskGroup.ElectronicWarfare:
                    return hasJammer ? MechStationRole.Jammer : MechStationRole.Escort;
                case FleetTaskGroup.Reconnaissance:
                    return isLeader || formationIndex == 0 ? MechStationRole.Scout : MechStationRole.Cover;
                default:
                    return MechStationRole.Default;
            }
        }

        /// <summary>Band with hysteresis: entering OnStation needs OnStationRadius, leaving it needs ReleaseRadius.</summary>
        public static MechStationBand ResolveBand(float slotError, float relativeSpeed, MechStationBand previous, in MechStationStyleParameters p)
        {
            if (slotError > p.JoinRadius) return MechStationBand.Far;
            if (previous == MechStationBand.OnStation && slotError <= p.ReleaseRadius) return MechStationBand.OnStation;
            if (slotError <= p.OnStationRadius && relativeSpeed < 25f) return MechStationBand.OnStation;
            return MechStationBand.Joining;
        }

        /// <summary>
        /// Station-keeping acceleration: velocity feed-forward from the slot plus
        /// a closure speed that grows with distance and is limited by braking capacity.
        /// </summary>
        public static Vector3 StationAcceleration(Vector3 slotError, Vector3 slotVelocity, Vector3 ownVelocity,
            MechStationBand band, in MechStationStyleParameters p, float brakingAcceleration, float maximumAcceleration)
        {
            float distance = slotError.magnitude;
            var direction = distance > 0.01f ? slotError / distance : Vector3.zero;
            float brake = Mathf.Max(0.1f, brakingAcceleration) * 0.65f;
            float closure = band switch
            {
                MechStationBand.Far => p.CatchUpSpeed,
                MechStationBand.Joining => Mathf.Lerp(20f, p.CatchUpSpeed * 0.6f, Mathf.Clamp01(distance / p.JoinRadius)),
                _ => distance / Mathf.Max(0.5f, p.GainSeconds),
            };
            closure = Mathf.Min(closure, Mathf.Sqrt(2f * brake * distance));
            var desired = slotVelocity + direction * closure;
            float response = band == MechStationBand.OnStation ? p.GainSeconds : 1f;
            return Vector3.ClampMagnitude((desired - ownVelocity) / response, maximumAcceleration);
        }

        /// <summary>Lateral weave offset along the slot's right axis (assault style).</summary>
        public static float WeaveOffset(in MechStationStyleParameters p, float time, int formationIndex)
            => p.WeaveAmplitude <= 0f ? 0f
                : p.WeaveAmplitude * Mathf.Sin((time / p.WeavePeriodSeconds + formationIndex * 0.37f) * Mathf.PI * 2f);

        /// <summary>
        /// Sea Power's CloserAlliedShooterHolds: yield a target that a healthier-placed
        /// friendly is already engaging from closer. Ties break on member order so
        /// exactly one shooter keeps the target.
        /// </summary>
        public static bool ShouldYieldTarget(float ownRange, float friendRange, int ownOrder, int friendOrder, float margin = 0.9f)
            => friendRange < ownRange * margin || Mathf.Abs(friendRange - ownRange) <= ownRange * (1f - margin) && friendOrder < ownOrder;

        /// <summary>Sea Power's ShouldAbandonAirChase: out of reach and not closing.</summary>
        public static bool ShouldAbandonChase(float range, float maximumWeaponRange, float closingSpeed, float marginMeters = 2000f)
            => maximumWeaponRange > 0f && range > maximumWeaponRange + marginMeters && closingSpeed <= 0f;
    }
}
