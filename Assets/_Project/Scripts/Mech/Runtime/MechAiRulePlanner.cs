using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechAiTacticalWeaponRole : byte
    {
        None = 0,
        CloseAutomatic = 1,
        GeneralAutomatic = 2,
        PrecisionGun = 3,
        HeavyCannon = 4,
        CloseRangeMissile = 5,
        StandoffMissile = 6,
    }

    public readonly struct MechAiEngagementBand
    {
        public MechAiEngagementBand(
            float minimumRangeMeters,
            float preferredRangeMeters,
            float maximumRangeMeters)
        {
            MinimumRangeMeters = minimumRangeMeters;
            PreferredRangeMeters = preferredRangeMeters;
            MaximumRangeMeters = maximumRangeMeters;
        }

        public float MinimumRangeMeters { get; }
        public float PreferredRangeMeters { get; }
        public float MaximumRangeMeters { get; }
    }

    public readonly struct MechAiCombatDoctrine
    {
        public MechAiCombatDoctrine(
            MechAiTacticalWeaponRole tacticalRole,
            MechAiEngagementBand engagementBand,
            float orbitSpeedMetersPerSecond)
        {
            TacticalRole = tacticalRole;
            EngagementBand = engagementBand;
            OrbitSpeedMetersPerSecond = orbitSpeedMetersPerSecond;
        }

        public MechAiTacticalWeaponRole TacticalRole { get; }
        public MechAiEngagementBand EngagementBand { get; }
        public float OrbitSpeedMetersPerSecond { get; }
        public bool IsConfigured => TacticalRole != MechAiTacticalWeaponRole.None;
    }

    public static class MechAiRulePlanner
    {
        public static Vector3 ResolveOrbitTangent(
            Vector3 radial, Vector3 previousTangent, Vector3 preferredUp)
        {
            if (radial.sqrMagnitude < 0.0001f) return Vector3.zero;
            radial.Normalize();
            // Transport the previous orbit direction onto the new line-of-sight
            // plane. Passing above/below the reference poles must not flip sides.
            var tangent = Vector3.ProjectOnPlane(previousTangent, radial);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.Cross(preferredUp, radial);
            if (tangent.sqrMagnitude < 0.0001f)
                tangent = Vector3.ProjectOnPlane(
                    Mathf.Abs(radial.x) < 0.8f ? Vector3.right : Vector3.forward, radial);
            return tangent.normalized;
        }

        /// <summary>
        /// Both relative vectors are target minus ownship. Returns a force-derived
        /// acceleration request, not a velocity assignment or a projected 2D path.
        /// </summary>
        public static Vector3 CalculateSpatialAcceleration(
            Vector3 relativePosition,
            Vector3 relativeVelocity,
            Vector3 orbitTangent,
            MechAiTargetController.CombatState state,
            in MechAiCombatDoctrine doctrine,
            float towardAcceleration,
            float awayAcceleration,
            float maximumAcceleration,
            float responseSeconds,
            out bool urgentBraking)
        {
            urgentBraking = false;
            if (!IsFinite(relativePosition) || !IsFinite(relativeVelocity) ||
                !IsFinite(orbitTangent)) return Vector3.zero;
            var response = Mathf.Max(0.2f, responseSeconds);
            var maxAcceleration = Mathf.Max(0f, maximumAcceleration);
            var range = relativePosition.magnitude;
            if (range < 0.01f)
                return Vector3.ClampMagnitude(relativeVelocity / response, maxAcceleration);
            var radial = relativePosition / range;
            var preferredRange = Mathf.Max(100f, doctrine.EngagementBand.PreferredRangeMeters);
            if (state == MechAiTargetController.CombatState.Defensive)
                preferredRange = Mathf.Max(preferredRange, range + Mathf.Max(300f, range * 0.15f));
            var rangeError = range - preferredRange;
            var holdRadius = Mathf.Clamp(preferredRange * 0.025f, 20f, 120f);
            var remainingDistance = Mathf.Max(0f, Mathf.Abs(rangeError) - holdRadius);
            var closingSpeed = -Vector3.Dot(relativeVelocity, radial);
            var brakingAcceleration = rangeError >= 0f ? awayAcceleration : towardAcceleration;
            // Margin reserves authority for turns, allocation losses and track
            // latency. The small floor permits attitude acquisition with weak RCS.
            var safeBrakingAcceleration = Mathf.Max(0.1f, brakingAcceleration) * 0.65f;
            var stoppingSpeed = Mathf.Sqrt(2f * safeBrakingAcceleration * remainingDistance);
            var cruiseSpeed = state switch
            {
                MechAiTargetController.CombatState.Intercept => 320f,
                MechAiTargetController.CombatState.Extend => 280f,
                MechAiTargetController.CombatState.Defensive => 160f,
                _ => 170f,
            };
            var desiredRadialSpeed = Mathf.Sign(rangeError) * Mathf.Min(
                cruiseSpeed, stoppingSpeed, remainingDistance * 0.35f);
            urgentBraking = Mathf.Abs(closingSpeed) > 20f &&
                (closingSpeed * rangeError > 0f || remainingDistance <= 0.01f) &&
                Mathf.Abs(closingSpeed) > stoppingSpeed + 10f;

            var orbitFactor = state switch
            {
                MechAiTargetController.CombatState.Intercept => 0.2f,
                MechAiTargetController.CombatState.Extend => 0.35f,
                MechAiTargetController.CombatState.Defensive => 1.25f,
                _ => 1f,
            };
            var orbitSpeed = Mathf.Min(doctrine.OrbitSpeedMetersPerSecond * orbitFactor,
                Mathf.Sqrt(maxAcceleration * 0.35f * range));
            if (urgentBraking) orbitSpeed *= 0.25f;
            var tangent = Vector3.ProjectOnPlane(orbitTangent, radial).normalized;
            var relativeTangentialVelocity = Vector3.ProjectOnPlane(relativeVelocity, radial);
            var centripetalAcceleration = orbitSpeed > 0.01f
                ? Mathf.Min(relativeTangentialVelocity.sqrMagnitude / range, maxAcceleration * 0.35f)
                : 0f;
            var radialAcceleration = Mathf.Clamp(
                (desiredRadialSpeed - closingSpeed) / response + centripetalAcceleration,
                -maxAcceleration, maxAcceleration);
            var transverseBudget = Mathf.Sqrt(Mathf.Max(0f,
                maxAcceleration * maxAcceleration - radialAcceleration * radialAcceleration));
            var transverseAcceleration = Vector3.ClampMagnitude(
                (relativeTangentialVelocity + tangent * orbitSpeed) / response, transverseBudget);
            return radial * radialAcceleration + transverseAcceleration;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        public static bool IsOffensiveRole(MechWeaponRole role)
        {
            return role == MechWeaponRole.HandCannon ||
                   role == MechWeaponRole.AutomaticCannon ||
                   role == MechWeaponRole.AirToAirMissile;
        }

        public static MechAiEngagementBand ResolveEngagementBand(
            MechWeaponSlotStatus[] weapons,
            int weaponCount,
            float[] preferredRangeScratch,
            float fallbackMinimumRangeMeters,
            float fallbackMaximumRangeMeters)
        {
            var count = 0;
            var longestUsableRange = 0f;
            for (var i = 0; i < weaponCount && i < weapons.Length; i++)
            {
                var weapon = weapons[i];
                if (!IsOffensiveRole(weapon.Role) || !weapon.HasAmmunition)
                {
                    continue;
                }

                var effectiveRange = Mathf.Max(
                    weapon.MinimumRangeMeters + 100f,
                    weapon.EffectiveRangeMeters);
                var preferredRatio = weapon.Role switch
                {
                    MechWeaponRole.AutomaticCannon => 0.68f,
                    MechWeaponRole.HandCannon => 0.78f,
                    MechWeaponRole.AirToAirMissile => 0.55f,
                    _ => 0.7f,
                };
                if (count >= preferredRangeScratch.Length)
                {
                    break;
                }
                preferredRangeScratch[count++] = Mathf.Clamp(
                    effectiveRange * preferredRatio,
                    weapon.MinimumRangeMeters + 100f,
                    Mathf.Max(
                        weapon.MinimumRangeMeters + 100f,
                        weapon.MaximumRangeMeters * 0.9f));
                longestUsableRange = Mathf.Max(
                    longestUsableRange,
                    weapon.MaximumRangeMeters);
            }

            if (count == 0)
            {
                var fallbackMin = Mathf.Max(100f, fallbackMinimumRangeMeters);
                var fallbackMax = Mathf.Max(
                    fallbackMin + 100f,
                    fallbackMaximumRangeMeters);
                return new MechAiEngagementBand(
                    fallbackMin,
                    (fallbackMin + fallbackMax) * 0.5f,
                    fallbackMax);
            }

            for (var i = 1; i < count; i++)
            {
                var value = preferredRangeScratch[i];
                var j = i - 1;
                while (j >= 0 && preferredRangeScratch[j] > value)
                {
                    preferredRangeScratch[j + 1] = preferredRangeScratch[j];
                    j--;
                }
                preferredRangeScratch[j + 1] = value;
            }

            // The lower median keeps a mixed gun/missile loadout close enough
            // for its shorter-ranged weapons instead of orbiting at missile range.
            var preferred = preferredRangeScratch[(count - 1) / 2];
            var minimum = Mathf.Max(250f, preferred * 0.72f);
            var maximum = Mathf.Min(
                Mathf.Max(minimum + 250f, preferred * 1.24f),
                Mathf.Max(minimum + 250f, longestUsableRange));
            return new MechAiEngagementBand(minimum, preferred, maximum);
        }

        public static MechAiCombatDoctrine ResolveCombatDoctrine(
            MechWeaponSlotStatus[] weapons,
            int weaponCount,
            float currentRangeMeters,
            MechAiTacticalWeaponRole previousRole,
            float fallbackMinimumRangeMeters,
            float fallbackMaximumRangeMeters)
        {
            var bestGun = default(MechAiCombatDoctrine);
            var bestMissile = default(MechAiCombatDoctrine);
            var bestGunScore = float.NegativeInfinity;
            var bestMissileScore = float.NegativeInfinity;

            for (var i = 0; i < weaponCount && i < weapons.Length; i++)
            {
                var weapon = weapons[i];
                if (!IsOffensiveRole(weapon.Role) ||
                    !weapon.HasReserveAmmunition)
                {
                    continue;
                }

                var role = ClassifyTacticalRole(in weapon);
                var doctrine = BuildDoctrine(in weapon, role);
                var fit = ScoreDoctrineFit(
                    in doctrine,
                    currentRangeMeters,
                    role == previousRole);
                if (IsMissileRole(role))
                {
                    if (fit > bestMissileScore)
                    {
                        bestMissileScore = fit;
                        bestMissile = doctrine;
                    }
                }
                else if (fit > bestGunScore)
                {
                    bestGunScore = fit;
                    bestGun = doctrine;
                }
            }

            if (bestGun.IsConfigured && bestMissile.IsConfigured)
            {
                // Missiles define the opening phase. Once the target enters the
                // gun envelope, the AI stops orbiting at missile range and uses
                // the gun doctrine instead.
                var gunTransitionRange = Mathf.Max(
                    bestGun.EngagementBand.MaximumRangeMeters * 1.08f,
                    bestGun.EngagementBand.PreferredRangeMeters * 1.3f);
                var hysteresis = previousRole == bestMissile.TacticalRole
                    ? 0.9f
                    : 1f;
                return currentRangeMeters > gunTransitionRange * hysteresis
                    ? bestMissile
                    : bestGun;
            }

            if (bestGun.IsConfigured)
            {
                return bestGun;
            }
            if (bestMissile.IsConfigured)
            {
                return bestMissile;
            }

            var fallbackMin = Mathf.Max(100f, fallbackMinimumRangeMeters);
            var fallbackMax = Mathf.Max(
                fallbackMin + 100f,
                fallbackMaximumRangeMeters);
            return new MechAiCombatDoctrine(
                MechAiTacticalWeaponRole.GeneralAutomatic,
                new MechAiEngagementBand(
                    fallbackMin,
                    (fallbackMin + fallbackMax) * 0.5f,
                    fallbackMax),
                120f);
        }

        public static MechAiTacticalWeaponRole ClassifyTacticalRole(
            in MechWeaponSlotStatus weapon)
        {
            if (weapon.Role == MechWeaponRole.AirToAirMissile)
            {
                return weapon.EffectiveRangeMeters >= 9000f ||
                       weapon.MaximumRangeMeters >= 18000f
                    ? MechAiTacticalWeaponRole.StandoffMissile
                    : MechAiTacticalWeaponRole.CloseRangeMissile;
            }

            var precisionGun =
                weapon.MuzzleVelocityMetersPerSecond >= 1800f &&
                weapon.MaximumDispersionMilliradians <= 0.4f;
            if (precisionGun)
            {
                return MechAiTacticalWeaponRole.PrecisionGun;
            }

            if (weapon.Role == MechWeaponRole.HandCannon ||
                weapon.RoundsPerMinute <= 180f)
            {
                return MechAiTacticalWeaponRole.HeavyCannon;
            }

            if (weapon.EffectiveRangeMeters <= 2500f ||
                weapon.MaximumDispersionMilliradians >= 0.65f ||
                weapon.RoundsPerMinute >= 1200f)
            {
                return MechAiTacticalWeaponRole.CloseAutomatic;
            }

            return MechAiTacticalWeaponRole.GeneralAutomatic;
        }

        public static bool MatchesDoctrine(
            in MechWeaponSlotStatus weapon,
            in MechAiCombatDoctrine doctrine)
        {
            return doctrine.IsConfigured &&
                   ClassifyTacticalRole(in weapon) == doctrine.TacticalRole;
        }

        private static MechAiCombatDoctrine BuildDoctrine(
            in MechWeaponSlotStatus weapon,
            MechAiTacticalWeaponRole role)
        {
            var effective = Mathf.Max(
                weapon.MinimumRangeMeters + 150f,
                weapon.EffectiveRangeMeters);
            var preferredRatio = role switch
            {
                MechAiTacticalWeaponRole.CloseAutomatic => 0.5f,
                MechAiTacticalWeaponRole.GeneralAutomatic => 0.68f,
                MechAiTacticalWeaponRole.PrecisionGun => 0.84f,
                MechAiTacticalWeaponRole.HeavyCannon => 0.76f,
                MechAiTacticalWeaponRole.CloseRangeMissile => 0.48f,
                MechAiTacticalWeaponRole.StandoffMissile => 0.58f,
                _ => 0.7f,
            };
            var preferred = Mathf.Clamp(
                effective * preferredRatio,
                weapon.MinimumRangeMeters + 150f,
                Mathf.Max(
                    weapon.MinimumRangeMeters + 150f,
                    weapon.MaximumRangeMeters * 0.9f));
            var minimumRatio = IsMissileRole(role) ? 0.68f : 0.7f;
            var maximumRatio = role == MechAiTacticalWeaponRole.PrecisionGun
                ? 1.2f
                : 1.3f;
            var minimum = Mathf.Max(
                weapon.MinimumRangeMeters,
                preferred * minimumRatio);
            var maximum = Mathf.Min(
                weapon.MaximumRangeMeters,
                Mathf.Max(minimum + 250f, preferred * maximumRatio));
            var orbitSpeed = role switch
            {
                MechAiTacticalWeaponRole.CloseAutomatic => 210f,
                MechAiTacticalWeaponRole.GeneralAutomatic => 165f,
                MechAiTacticalWeaponRole.PrecisionGun => 95f,
                MechAiTacticalWeaponRole.HeavyCannon => 115f,
                MechAiTacticalWeaponRole.CloseRangeMissile => 180f,
                MechAiTacticalWeaponRole.StandoffMissile => 125f,
                _ => 140f,
            };
            return new MechAiCombatDoctrine(
                role,
                new MechAiEngagementBand(minimum, preferred, maximum),
                orbitSpeed);
        }

        private static float ScoreDoctrineFit(
            in MechAiCombatDoctrine doctrine,
            float currentRangeMeters,
            bool retainedRole)
        {
            var preferred = Mathf.Max(
                100f,
                doctrine.EngagementBand.PreferredRangeMeters);
            var rangeError = Mathf.Abs(currentRangeMeters - preferred) / preferred;
            return 100f - rangeError * 45f + (retainedRole ? 8f : 0f);
        }

        private static bool IsMissileRole(MechAiTacticalWeaponRole role)
        {
            return role == MechAiTacticalWeaponRole.CloseRangeMissile ||
                   role == MechAiTacticalWeaponRole.StandoffMissile;
        }

        public static bool IsEligibleForAirTarget(
            in MechWeaponSlotStatus weapon,
            float rangeMeters,
            float angularErrorDegrees,
            float trackConfidence,
            float positionUncertaintyMeters)
        {
            if (!IsOffensiveRole(weapon.Role) ||
                !weapon.CanAttemptFire ||
                rangeMeters < weapon.MinimumRangeMeters ||
                rangeMeters > weapon.MaximumRangeMeters)
            {
                return false;
            }

            switch (weapon.Role)
            {
            case MechWeaponRole.AutomaticCannon:
                return angularErrorDegrees <= 7f &&
                       trackConfidence >= 0.55f &&
                       rangeMeters <= Mathf.Max(
                           weapon.EffectiveRangeMeters * 1.25f,
                           weapon.EffectiveRangeMeters + 500f);
            case MechWeaponRole.HandCannon:
                return angularErrorDegrees <= 9f &&
                       trackConfidence >= 0.66f &&
                       rangeMeters <= Mathf.Max(
                           weapon.EffectiveRangeMeters * 1.15f,
                           weapon.EffectiveRangeMeters + 350f);
            case MechWeaponRole.AirToAirMissile:
                return angularErrorDegrees <= 55f &&
                       trackConfidence >= 0.72f &&
                       positionUncertaintyMeters <=
                       Mathf.Max(120f, rangeMeters * 0.025f);
            default:
                return false;
            }
        }

        public static float ScoreWeapon(
            in MechWeaponSlotStatus weapon,
            float rangeMeters)
        {
            var effectiveRange = Mathf.Max(100f, weapon.EffectiveRangeMeters);
            var normalizedRange = rangeMeters / effectiveRange;
            var idealRatio = weapon.Role switch
            {
                MechWeaponRole.AutomaticCannon => 0.68f,
                MechWeaponRole.HandCannon => 0.78f,
                MechWeaponRole.AirToAirMissile => 0.55f,
                _ => 1f,
            };
            var roleScore = weapon.Role switch
            {
                MechWeaponRole.AirToAirMissile => 320f,
                MechWeaponRole.HandCannon => 255f,
                MechWeaponRole.AutomaticCannon => 235f,
                _ => 0f,
            };
            var rangeFit = 120f - Mathf.Abs(normalizedRange - idealRatio) * 95f;
            var readiness = weapon.Reloading ? -250f : 20f;
            return roleScore + rangeFit + readiness;
        }
    }
}
