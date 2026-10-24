using System;

namespace ACunicombat.Mechs
{
    public readonly struct MechMissileVariant
    {
        public MechMissileVariant(
            string variantId,
            string missileId,
            string displayName,
            MechWeaponPerformance performance)
        {
            VariantId = variantId;
            MissileId = missileId;
            DisplayName = displayName;
            Performance = performance;
        }

        public string VariantId { get; }
        public string MissileId { get; }
        public string DisplayName { get; }
        public MechWeaponPerformance Performance { get; }
    }

    public static class MechMissileVariantCatalog
    {
        public const string StandardVariantId = "standard";
        public const string DogfightVariantId = "dogfight";
        public const string LongRangeVariantId = "long-range";

        public static int GetVariantCount(string launcherPartId, MechWeaponPerformance performance)
        {
            if(MechMissileLauncherCatalog.UsesAmmoSelection(launcherPartId))return MechMissileLauncherCatalog.Find(launcherPartId).CompatibleAmmo.Length;
            if(MechMissileLauncherCatalog.IsJianwei(launcherPartId))return MechMissileLauncherCatalog.Find(launcherPartId).Number=="4251"?1:2;
            return IsAam4290(launcherPartId, performance) ? 3 : 1;
        }

        public static string GetDefaultVariantId(string launcherPartId, MechWeaponPerformance performance)
        {
            var d=MechMissileLauncherCatalog.Find(launcherPartId);
            if(MechMissileLauncherCatalog.UsesAmmoSelection(launcherPartId))return d.DefaultAmmo;
            if(d?.Number=="4250")return "jw7-er";
            if(d?.Number=="4251")return "jw7";
            if(d?.Number=="4252")return "jw7-mixed";
            return StandardVariantId;
        }

        public static MechMissileVariant GetVariant(
            string launcherPartId,
            MechWeaponPerformance basePerformance,
            int index)
        {
            if (basePerformance == null)
            {
                return default;
            }

            var launcher=MechMissileLauncherCatalog.Find(launcherPartId);
            if(MechMissileLauncherCatalog.UsesAmmoSelection(launcherPartId))
            {
                string ammo=launcher.CompatibleAmmo[Math.Max(0,Math.Min(index,launcher.CompatibleAmmo.Length-1))];
                var p=MechMissileLauncherCatalog.Compose(launcher,ammo);
                return new MechMissileVariant(ammo,ammo,p.missileDisplayName,p);
            }
            if(MechMissileLauncherCatalog.IsJianwei(launcherPartId))
            {
                bool er=launcher.Number=="4250"&&index==0;
                bool mixed=launcher.Number=="4252"&&index==0;
                var p=MechMissileLauncherCatalog.Compose(launcher,er||mixed?"JW7-ER":"JW7");
                return new MechMissileVariant(mixed?"jw7-mixed":er?"jw7-er":"jw7",er||mixed?"JW7-ER":"JW7",
                    mixed?"剑卫7C×4＋剑卫7×8（双4发组）":p.missileDisplayName,p);
            }

            if (!IsAam4290(launcherPartId, basePerformance))
            {
                return BuildStandard(basePerformance);
            }

            return CreateLegacy4290Variant(basePerformance,index);
        }

        public static MechMissileVariant CreateLegacy4290Variant(MechWeaponPerformance basePerformance,int index)
        {
            return index switch
            {
                1 => BuildDogfight(basePerformance),
                2 => BuildLongRange(basePerformance),
                _ => BuildStandard(basePerformance),
            };
        }

        public static MechMissileVariant ResolveVariant(
            string launcherPartId,
            MechWeaponPerformance basePerformance,
            string variantId)
        {
            var launcher=MechMissileLauncherCatalog.Find(launcherPartId);
            if(MechMissileLauncherCatalog.UsesAmmoSelection(launcherPartId))
            {
                if(variantId==StandardVariantId)variantId=launcher.DefaultAmmo;
                if(launcher.Number.StartsWith("429"))
                {if(variantId==DogfightVariantId)variantId="SIM-29M";if(variantId==LongRangeVariantId)variantId="SIM-57R";}
            }
            int count = GetVariantCount(launcherPartId, basePerformance);
            for (int i = 0; i < count; i++)
            {
                MechMissileVariant variant = GetVariant(launcherPartId, basePerformance, i);
                if (string.Equals(variant.VariantId, variantId, StringComparison.OrdinalIgnoreCase))
                {
                    return variant;
                }
            }

            return GetVariant(launcherPartId, basePerformance, 0);
        }

        public static MechWeaponPerformance ResolveSelectedPerformance(
            string launcherPartId,
            MechWeaponPerformance basePerformance,
            string variantId)
        {
            return ResolveVariant(launcherPartId, basePerformance, variantId).Performance;
        }

        public static float ResolveWarheadMassKg(MechWeaponPerformance performance)
        {
            if (performance == null)
            {
                return 0f;
            }

            if (performance.warheadMassKg > 0f)
            {
                return performance.warheadMassKg;
            }

            return performance.role == MechWeaponRole.InterceptorMissile ? 9f : 24f;
        }

        private static MechMissileVariant BuildStandard(MechWeaponPerformance source)
        {
            MechWeaponPerformance performance = source.Clone();
            performance.warheadMassKg = ResolveWarheadMassKg(source);
            string displayName = string.IsNullOrWhiteSpace(source.missileDisplayName)
                ? "制导导弹"
                : source.missileDisplayName;
            return new MechMissileVariant(
                StandardVariantId,
                displayName.ToUpperInvariant(),
                IsAam4290(null, source) ? displayName + " 标准型" : displayName,
                performance);
        }

        private static MechMissileVariant BuildDogfight(MechWeaponPerformance source)
        {
            MechWeaponPerformance performance = source.Clone();
            performance.missileDisplayName="AAM-4290C";
            MissileFlightProfile profile = ResolveBaseProfile(source);
            profile.DryMassKg = 108f;
            profile.MainPropellantMassKg = 58f;
            profile.SecondStagePropellantMassKg = 0f;
            profile.RcsPropellantMassKg = 13f;
            profile.RcsThrustNewtons = 22_000f;
            profile.RcsMassFlowKgPerSecond = 1.1f;
            profile.PitchYawInertiaKgM2 = 96f;
            profile.RcsMomentArmMeters = 1.5f;
            profile.MaximumAngularRateDegreesPerSecond = 390f;
            profile.MaximumFlightTimeSeconds = 160f;
            profile.MaximumRangeMeters = 28_000f;
            profile.SeekerSearchHalfAngleDegrees = 45f;
            profile.SeekerGimbalDegrees = 72f;
            profile.ProximityFuseRadiusMeters = 9f;
            profile.ProximityFuseSafeDistanceMeters = 16f;
            profile.WarheadLethalRadiusMeters = 13f;
            performance.directHitDamage = 2350f;
            performance.warheadMassKg = 14f;
            ApplyFlightProfile(performance, profile);
            return new MechMissileVariant(
                DogfightVariantId,
                "AAM-4290C",
                "AAM-4290C 格斗型",
                performance);
        }

        private static MechMissileVariant BuildLongRange(MechWeaponPerformance source)
        {
            MechWeaponPerformance performance = source.Clone();
            performance.missileDisplayName="AAM-4290R";
            MissileFlightProfile profile = ResolveBaseProfile(source);
            profile.DryMassKg = 120f;
            profile.MainPropellantMassKg = 145f;
            profile.MainMassFlowKgPerSecond = 13.5f;
            profile.SecondStagePropellantMassKg = 0f;
            profile.RcsPropellantMassKg = 18f;
            profile.PitchYawInertiaKgM2 = 180f;
            profile.MaximumAngularRateDegreesPerSecond = 190f;
            profile.MaximumFlightTimeSeconds = 420f;
            profile.MaximumRangeMeters = 82_000f;
            profile.SeekerRangeMeters = 10_000f;
            performance.directHitDamage = source.directHitDamage;
            performance.warheadMassKg = 24f;
            ApplyFlightProfile(performance, profile);
            return new MechMissileVariant(
                LongRangeVariantId,
                "AAM-4290R",
                "AAM-4290R 远程型",
                performance);
        }

        private static MissileFlightProfile ResolveBaseProfile(MechWeaponPerformance source)
        {
            return source.missileFlightProfile.IsConfigured
                ? source.missileFlightProfile.Sanitized()
                : MissileFlightProfile.AirToAirDefault;
        }

        private static void ApplyFlightProfile(
            MechWeaponPerformance performance,
            MissileFlightProfile profile)
        {
            profile = profile.Sanitized();
            performance.missileFlightProfile = profile;
            performance.muzzleVelocityMetersPerSecond = profile.EjectionSpeedMps;
            performance.projectileLifetimeSeconds = profile.MaximumFlightTimeSeconds;
            performance.maximumRangeMeters = profile.MaximumRangeMeters;
            performance.effectiveRangeMeters = profile.MaximumRangeMeters;
            performance.proximityFuseRadiusMeters = profile.ProximityFuseRadiusMeters;
            performance.proximityFuseArmSeconds = profile.ProximityFuseArmSeconds;
            performance.proximityFuseSafeDistanceMeters = profile.ProximityFuseSafeDistanceMeters;
            performance.warheadLethalRadiusMeters = profile.WarheadLethalRadiusMeters;
        }

        private static bool IsAam4290(string launcherPartId, MechWeaponPerformance performance)
        {
            if (!string.IsNullOrWhiteSpace(launcherPartId) &&
                launcherPartId.IndexOf("4290", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return performance != null &&
                   !string.IsNullOrWhiteSpace(performance.missileDisplayName) &&
                   performance.missileDisplayName.IndexOf("4290", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
