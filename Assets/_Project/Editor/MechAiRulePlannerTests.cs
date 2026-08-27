using ACunicombat.Mechs;
using NUnit.Framework;

namespace ACunicombat.Editor.Tests
{
    public sealed class MechAiRulePlannerTests
    {
        [Test]
        public void DuplicateRoles_AreEvaluatedPerSlot()
        {
            var left = Weapon(
                MechPartSlot.LeftHandWeapon,
                MechWeaponRole.AutomaticCannon,
                32,
                4500f);
            var right = Weapon(
                MechPartSlot.RightHandWeapon,
                MechWeaponRole.AutomaticCannon,
                32,
                4500f);

            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in left,
                    2500f,
                    2f,
                    0.8f,
                    20f),
                Is.True);
            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in right,
                    2500f,
                    2f,
                    0.8f,
                    20f),
                Is.True);
        }

        [Test]
        public void EmptyFirstWeapon_DoesNotSuppressReadySecondWeapon()
        {
            var empty = Weapon(
                MechPartSlot.LeftHandWeapon,
                MechWeaponRole.AutomaticCannon,
                0,
                4500f);
            var ready = Weapon(
                MechPartSlot.RightHandWeapon,
                MechWeaponRole.AutomaticCannon,
                20,
                4500f);

            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in empty,
                    2000f,
                    1f,
                    0.9f,
                    5f),
                Is.False);
            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in ready,
                    2000f,
                    1f,
                    0.9f,
                    5f),
                Is.True);
        }

        [Test]
        public void LeftRightSwap_DoesNotChangeEngagementDoctrine()
        {
            var first = new[]
            {
                Weapon(MechPartSlot.LeftHandWeapon, MechWeaponRole.HandCannon, 12, 6200f),
                Weapon(MechPartSlot.RightHandWeapon, MechWeaponRole.AutomaticCannon, 32, 4500f),
            };
            var swapped = new[]
            {
                Weapon(MechPartSlot.LeftHandWeapon, MechWeaponRole.AutomaticCannon, 32, 4500f),
                Weapon(MechPartSlot.RightHandWeapon, MechWeaponRole.HandCannon, 12, 6200f),
            };

            var firstBand = MechAiRulePlanner.ResolveEngagementBand(
                first,
                first.Length,
                new float[4],
                3000f,
                5000f);
            var swappedBand = MechAiRulePlanner.ResolveEngagementBand(
                swapped,
                swapped.Length,
                new float[4],
                3000f,
                5000f);

            Assert.That(
                swappedBand.PreferredRangeMeters,
                Is.EqualTo(firstBand.PreferredRangeMeters).Within(0.001f));
            Assert.That(
                swappedBand.MinimumRangeMeters,
                Is.EqualTo(firstBand.MinimumRangeMeters).Within(0.001f));
            Assert.That(
                swappedBand.MaximumRangeMeters,
                Is.EqualTo(firstBand.MaximumRangeMeters).Within(0.001f));
        }

        [Test]
        public void MissileLoadout_SelectsLongerPreferredRangeThanGunLoadout()
        {
            var guns = new[]
            {
                Weapon(MechPartSlot.LeftHandWeapon, MechWeaponRole.AutomaticCannon, 32, 4500f),
            };
            var missiles = new[]
            {
                Weapon(MechPartSlot.LeftBackWeapon, MechWeaponRole.AirToAirMissile, 4, 50000f, 800f),
            };

            var gunBand = MechAiRulePlanner.ResolveEngagementBand(
                guns, 1, new float[4], 3000f, 5000f);
            var missileBand = MechAiRulePlanner.ResolveEngagementBand(
                missiles, 1, new float[4], 3000f, 5000f);

            Assert.That(
                missileBand.PreferredRangeMeters,
                Is.GreaterThan(gunBand.PreferredRangeMeters * 3f));
        }

        [Test]
        public void MissileEligibility_RespectsDerivedEnvelopeAndTrackQuality()
        {
            var missile = Weapon(
                MechPartSlot.RightBackWeapon,
                MechWeaponRole.AirToAirMissile,
                4,
                50000f,
                800f);

            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in missile, 500f, 5f, 0.95f, 5f),
                Is.False);
            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in missile, 12000f, 15f, 0.9f, 100f),
                Is.True);
            Assert.That(
                MechAiRulePlanner.IsEligibleForAirTarget(
                    in missile, 12000f, 15f, 0.6f, 100f),
                Is.False);
        }

        [Test]
        public void TacticalRole_IsDerivedFromBallistics_NotSlotOrPartName()
        {
            var precision = Weapon(
                MechPartSlot.LeftHandWeapon,
                MechWeaponRole.AutomaticCannon,
                12,
                12000f,
                muzzleVelocity: 2310f,
                dispersion: 0.22f,
                roundsPerMinute: 90f);
            var closeAutomatic = Weapon(
                MechPartSlot.RightHandWeapon,
                MechWeaponRole.AutomaticCannon,
                120,
                3200f,
                muzzleVelocity: 1300f,
                dispersion: 0.85f,
                roundsPerMinute: 2400f);

            Assert.That(
                MechAiRulePlanner.ClassifyTacticalRole(in precision),
                Is.EqualTo(MechAiTacticalWeaponRole.PrecisionGun));
            Assert.That(
                MechAiRulePlanner.ClassifyTacticalRole(in closeAutomatic),
                Is.EqualTo(MechAiTacticalWeaponRole.CloseAutomatic));
        }

        [Test]
        public void MixedLoadout_UsesMissileOpeningThenTransitionsToGun()
        {
            var weapons = new[]
            {
                Weapon(
                    MechPartSlot.LeftHandWeapon,
                    MechWeaponRole.AutomaticCannon,
                    32,
                    5000f,
                    muzzleVelocity: 1610f,
                    dispersion: 0.48f,
                    roundsPerMinute: 520f),
                Weapon(
                    MechPartSlot.RightBackWeapon,
                    MechWeaponRole.AirToAirMissile,
                    4,
                    50000f,
                    800f,
                    roundsPerMinute: 10f),
            };

            var opening = MechAiRulePlanner.ResolveCombatDoctrine(
                weapons,
                weapons.Length,
                18000f,
                MechAiTacticalWeaponRole.None,
                3000f,
                5000f);
            var close = MechAiRulePlanner.ResolveCombatDoctrine(
                weapons,
                weapons.Length,
                2500f,
                opening.TacticalRole,
                3000f,
                5000f);

            Assert.That(
                opening.TacticalRole,
                Is.EqualTo(MechAiTacticalWeaponRole.StandoffMissile));
            Assert.That(
                close.TacticalRole,
                Is.EqualTo(MechAiTacticalWeaponRole.GeneralAutomatic));
            Assert.That(
                opening.EngagementBand.PreferredRangeMeters,
                Is.GreaterThan(close.EngagementBand.PreferredRangeMeters));
        }

        private static MechWeaponSlotStatus Weapon(
            MechPartSlot slot,
            MechWeaponRole role,
            int rounds,
            float maximumRange,
            float minimumRange = 0f,
            float muzzleVelocity = 1500f,
            float dispersion = 0.3f,
            float roundsPerMinute = 600f)
        {
            return new MechWeaponSlotStatus(
                slot,
                role,
                rounds,
                rounds,
                1,
                false,
                0f,
                minimumRange,
                maximumRange * 0.75f,
                maximumRange,
                roundsPerMinute,
                dispersion,
                0.6f,
                4,
                0.4f,
                0f,
                muzzleVelocityMetersPerSecond: muzzleVelocity);
        }
    }
}
