using System.Collections.Generic;
using ACunicombat.Mechs;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using UnityEngine;

namespace ACunicombat.Editor.Tests
{
	public sealed class MechPartCatalogTests
	{
		private MechPartCatalog catalog;

		[SetUp]
		public void SetUp()
		{
			catalog = ScriptableObject.CreateInstance<MechPartCatalog>();
		}

		[TearDown]
		public void TearDown()
		{
			Object.DestroyImmediate(catalog);
		}

		[Test]
		public void GetEntries_FiltersBySlotAndPutsTestedWeaponsFirst()
		{
			catalog.EditorConfigure(new List<MechPartCatalogEntry>
			{
				Entry("right-a", "Alpha", MechPartSlot.RightHandWeapon, MechPartTestStatus.Untested),
				Entry("head", "Head", MechPartSlot.Head, MechPartTestStatus.Untested),
				Entry("right-z", "Zulu", MechPartSlot.RightHandWeapon, MechPartTestStatus.Tested)
			});
			List<MechPartCatalogEntry> entries = catalog.GetEntries(MechPartSlot.RightHandWeapon);
			Assert.That<List<MechPartCatalogEntry>>(entries, (IResolveConstraint)(object)((ConstraintExpression)Has.Count).EqualTo((object)2));
			Assert.That<string>(entries[0].PartId, (IResolveConstraint)(object)Is.EqualTo((object)"right-z"));
			Assert.That<string>(entries[1].PartId, (IResolveConstraint)(object)Is.EqualTo((object)"right-a"));
		}

		[Test]
		public void WeaponStatus_DistinguishesTestedFromVisualOnlyParts()
		{
			catalog.EditorConfigure(new MechPartCatalogEntry[2]
			{
				Entry("tested", "Tested", MechPartSlot.LeftBackWeapon, MechPartTestStatus.Tested),
				Entry("visual", "Visual", MechPartSlot.LeftBackWeapon, MechPartTestStatus.Untested)
			});
			Assert.That<bool>(catalog.Find("tested").IsWeapon, (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(catalog.Find("tested").IsTested, (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(catalog.Find("visual").IsTested, (IResolveConstraint)(object)Is.False);
		}

		[Test]
		public void OriginalInternalPartSource_ContainsEveryNamedFcsAndGenerator()
		{
			Assert.That<int>(AC6FullPartCatalogBuilder.OriginalFcsSourceCount, (IResolveConstraint)(object)Is.EqualTo((object)10));
			Assert.That<int>(AC6FullPartCatalogBuilder.OriginalGeneratorSourceCount, (IResolveConstraint)(object)Is.EqualTo((object)14));
		}

		[Test]
		public void TestedWeaponSource_IncludesAdditionalRiflesAndShaoWeiVariants()
		{
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_L_1310"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_R_1310"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_L_1610"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_R_1610"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_L_1630"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_R_1630"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_L_1720"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_R_1720"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_NATIVE_WP_L_5110"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_NATIVE_WP_R_5110"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_L_4250"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_R_4250"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_L_4290"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_FULL_WP_R_4290"), (IResolveConstraint)(object)Is.True);
		}

		[Test]
		public void WeaponRoleResolver_UsesPartTypeInsteadOfMountSide()
		{
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_L_1310"), Is.EqualTo(MechWeaponRole.HandCannon));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_R_1310"), Is.EqualTo(MechWeaponRole.HandCannon));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_L_1610"), Is.EqualTo(MechWeaponRole.AutomaticCannon));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_R_1610"), Is.EqualTo(MechWeaponRole.AutomaticCannon));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_L_4250"), Is.EqualTo(MechWeaponRole.InterceptorMissile));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_R_4250"), Is.EqualTo(MechWeaponRole.InterceptorMissile));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_L_4290"), Is.EqualTo(MechWeaponRole.AirToAirMissile));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_R_4290"), Is.EqualTo(MechWeaponRole.AirToAirMissile));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_NATIVE_WP_L_5110"), Is.EqualTo(MechWeaponRole.PointDefenseCannon));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_NATIVE_WP_R_5110"), Is.EqualTo(MechWeaponRole.PointDefenseCannon));
			Assert.That(MechWeaponRoleResolver.Resolve("AC6_FULL_WP_R_9999"), Is.EqualTo(MechWeaponRole.None));
		}

		[Test]
		public void WeaponPerformance_ExposesUnifiedGunFieldsByFireMode()
		{
			GameObject gameObject = new GameObject("weapon-performance-test");
			gameObject.SetActive(value: false);
			try
			{
				MechTestWeaponSystem mechTestWeaponSystem = gameObject.AddComponent<MechTestWeaponSystem>();
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_R_1610", out var rifle), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(rifle.IsAutomatic, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(rifle.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)95f));
				Assert.That<float>(rifle.MuzzleVelocityMetersPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)1610f));
				Assert.That<float>(rifle.FireRateRoundsPerMinute, (IResolveConstraint)(object)Is.EqualTo((object)520f));
				Assert.That<float>(rifle.BaseDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)0.08f));
				Assert.That<bool>(rifle.HasSustainedAccuracy, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(rifle.SustainedDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)0.34f));
				Assert.That<bool>(rifle.HasEffectiveRange, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(rifle.EffectiveRangeMeters, Is.GreaterThan(0f).And.LessThan(rifle.MaximumRangeMeters));
				Assert.That<float>(rifle.MaximumRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)12880f));
				Assert.That<int>(rifle.MagazineCapacity, (IResolveConstraint)(object)Is.EqualTo((object)32));
				Assert.That<int>(rifle.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)384));
				Assert.That<float>(rifle.ReloadTimeSeconds, (IResolveConstraint)(object)Is.EqualTo((object)5.25f));

				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_FULL_WP_R_1630", out var precisionCannon), (IResolveConstraint)(object)Is.True);
				Assert.That<float>(precisionCannon.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)132f));
				Assert.That<float>(precisionCannon.MuzzleVelocityMetersPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)2310f));
				Assert.That<float>(precisionCannon.FireRateRoundsPerMinute, (IResolveConstraint)(object)Is.EqualTo((object)90f));
				Assert.That<float>(precisionCannon.BaseDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)0.06f));
				Assert.That<float>(precisionCannon.SustainedDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)0.09f));
				Assert.That<int>(precisionCannon.MagazineCapacity, (IResolveConstraint)(object)Is.EqualTo((object)12));
				Assert.That<float>(precisionCannon.ReloadTimeSeconds, (IResolveConstraint)(object)Is.EqualTo((object)7f));
				Assert.That<float>(precisionCannon.ProximityFuseTriggerRadiusMeters, (IResolveConstraint)(object)Is.EqualTo((object)5f));
				Assert.That<float>(precisionCannon.WarheadDamageRadiusMeters, (IResolveConstraint)(object)Is.EqualTo((object)5f));
				AssertRiflePerformanceMatches(mechTestWeaponSystem, "AC6_FULL_WP_L_1630", precisionCannon);

				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_FULL_WP_R_1720", out var rapidCannon), (IResolveConstraint)(object)Is.True);
				Assert.That<float>(rapidCannon.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)85f));
				Assert.That<float>(rapidCannon.FireRateRoundsPerMinute, (IResolveConstraint)(object)Is.EqualTo((object)580f));
				Assert.That<float>(precisionCannon.EffectiveRangeMeters, Is.GreaterThan(rapidCannon.EffectiveRangeMeters + 800f));
				Assert.That<float>(rapidCannon.EffectiveRangeMeters, Is.GreaterThan(rifle.EffectiveRangeMeters));
				Assert.That<float>(precisionCannon.EffectiveRangeMeters, Is.LessThan(precisionCannon.MaximumRangeMeters));
				AssertRiflePerformanceMatches(mechTestWeaponSystem, "AC6_FULL_WP_L_1720", rapidCannon);
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_L_1310", out var cannon), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_R_1310", out var mirroredCannon), (IResolveConstraint)(object)Is.True);
				Assert.That<float>(mirroredCannon.DirectHitDamage, Is.EqualTo(cannon.DirectHitDamage));
				Assert.That<bool>(cannon.IsAutomatic, (IResolveConstraint)(object)Is.False);
				Assert.That<bool>(cannon.HasAccuracy, (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(cannon.HasSustainedAccuracy, (IResolveConstraint)(object)Is.False);
				Assert.That<float>(cannon.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)420f));
				Assert.That<float>(cannon.MaximumRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)4500f));
				Assert.That<int>(cannon.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)32));
				Assert.That<float>(cannon.ProximityFuseTriggerRadiusMeters, (IResolveConstraint)(object)Is.EqualTo((object)7.5f));
				Assert.That<float>(cannon.WarheadDamageRadiusMeters, (IResolveConstraint)(object)Is.EqualTo((object)10f));
			}
			finally
			{
				Object.DestroyImmediate(gameObject);
			}
		}

		private static void AssertRiflePerformanceMatches(MechTestWeaponSystem weaponSystem, string partId, MechWeaponPerformanceSnapshot expected)
		{
			Assert.That<bool>(weaponSystem.TryGetWeaponPerformance(partId, out var actual), (IResolveConstraint)(object)Is.True);
			Assert.That<string>(actual.WeaponType, (IResolveConstraint)(object)Is.EqualTo((object)expected.WeaponType));
			Assert.That<float>(actual.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)expected.DirectHitDamage));
			Assert.That<float>(actual.MaximumRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)expected.MaximumRangeMeters));
			Assert.That<float>(actual.MuzzleVelocityMetersPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)expected.MuzzleVelocityMetersPerSecond));
			Assert.That<float>(actual.FireRateRoundsPerMinute, (IResolveConstraint)(object)Is.EqualTo((object)expected.FireRateRoundsPerMinute));
			Assert.That<float>(actual.BaseDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)expected.BaseDispersionMilliradians));
			Assert.That<float>(actual.SustainedDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)expected.SustainedDispersionMilliradians));
			Assert.That<int>(actual.MagazineCapacity, (IResolveConstraint)(object)Is.EqualTo((object)expected.MagazineCapacity));
			Assert.That<int>(actual.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)expected.TotalAmmunition));
			Assert.That<float>(actual.ReloadTimeSeconds, (IResolveConstraint)(object)Is.EqualTo((object)expected.ReloadTimeSeconds));
		}

		[Test]
		public void WeaponPerformance_ExposesMissileFieldsAndRejectsUnknownParts()
		{
			GameObject gameObject = new GameObject("missile-performance-test");
			gameObject.SetActive(value: false);
			try
			{
				MechTestWeaponSystem mechTestWeaponSystem = gameObject.AddComponent<MechTestWeaponSystem>();
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_L_4250", out var interceptor), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_R_4250", out var mirroredInterceptor), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(interceptor.IsGuidedMissile, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(interceptor.EffectiveRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)15000f));
				Assert.That<int>(interceptor.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)32));
				Assert.That<float>(interceptor.SeekerRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)12000f));
				Assert.That<bool>(interceptor.HasProximityFuse, (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_R_4290", out var airToAir), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_L_4290", out var mirroredAirToAir), (IResolveConstraint)(object)Is.True);
				Assert.That<float>(airToAir.EffectiveRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)50000f));
				Assert.That<int>(airToAir.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)24));
				Assert.That<float>(airToAir.MaximumAngularRateDegreesPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)240f));
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_FULL_WP_R_9999", out var _), (IResolveConstraint)(object)Is.False);
			}
			finally
			{
				Object.DestroyImmediate(gameObject);
			}
		}

		[Test]
		public void WeaponPerformance_ExposesShaoWeiPointDefenseAndHeatFields()
		{
			GameObject gameObject = new GameObject("shao-wei-performance-test");
			gameObject.SetActive(value: false);
			try
			{
				MechTestWeaponSystem mechTestWeaponSystem = gameObject.AddComponent<MechTestWeaponSystem>();
				Assert.That<bool>(mechTestWeaponSystem.ShaoWeiPointDefenseEnabled, (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_R_5110", out var right), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(right.IsPointDefense, (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(right.IsAutomatic, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(right.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)45f));
				Assert.That<float>(right.FireRateRoundsPerMinute, (IResolveConstraint)(object)Is.EqualTo((object)858f));
				Assert.That<float>(right.MuzzleVelocityMetersPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)1800f));
				Assert.That<float>(right.ProjectileLifetimeSeconds, (IResolveConstraint)(object)Is.EqualTo((object)4f));
				Assert.That<float>(right.EffectiveRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)3000f));
				Assert.That<float>(right.MaximumRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)6000f));
				Assert.That<float>(right.BaseDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)0.1f));
				Assert.That<float>(right.SustainedDispersionMilliradians, (IResolveConstraint)(object)Is.EqualTo((object)0.35f));
				Assert.That<int>(right.MagazineCapacity, (IResolveConstraint)(object)Is.EqualTo((object)200));
				Assert.That<int>(right.MagazineCount, (IResolveConstraint)(object)Is.EqualTo((object)4));
				Assert.That<float>(right.ReloadTimeSeconds, (IResolveConstraint)(object)Is.EqualTo((object)4f));
				Assert.That<float>(right.PointDefenseMaximumRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)6000f));
				Assert.That<float>(right.PointDefenseFieldOfFireDegrees, (IResolveConstraint)(object)Is.EqualTo((object)200f));
				Assert.That<float>(right.PointDefenseTraverseRateDegreesPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)180f));
				Assert.That<float>(right.PointDefenseMaximumLeadSeconds, (IResolveConstraint)(object)Is.EqualTo((object)4f));
				Assert.That<float>(right.PointDefenseLockHoldSeconds, (IResolveConstraint)(object)Is.EqualTo((object)0.3f));
				Assert.That<float>(right.PointDefenseMinimumHitProbability, (IResolveConstraint)(object)Is.EqualTo((object)0.02f));
				Assert.That<float>(right.PointDefenseMaximumThreatTimeSeconds, (IResolveConstraint)(object)Is.EqualTo((object)12f));
				Assert.That<bool>(right.RequiresWeaponsQualityTrack, (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(right.IncomingTargetsOnly, (IResolveConstraint)(object)Is.True);
				Assert.That<int>(right.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)800));
				Assert.That<float>(right.ProximityFuseTriggerRadiusMeters, (IResolveConstraint)(object)Is.EqualTo((object)4f));
				Assert.That<float>(right.ProximityFuseArmSeconds, (IResolveConstraint)(object)Is.EqualTo((object)0.05f));
				Assert.That<float>(right.ProximityFuseSafeDistanceMeters, (IResolveConstraint)(object)Is.EqualTo((object)90f));
				Assert.That<bool>(right.UsesHeat, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(right.HeatPerShotMj, (IResolveConstraint)(object)Is.EqualTo((object)0.22f));
				Assert.That<float>(right.HeatCapacityMj, (IResolveConstraint)(object)Is.EqualTo((object)6f));
				Assert.That<float>(right.CoolingMw, (IResolveConstraint)(object)Is.EqualTo((object)1.4f));
				Assert.That<float>(right.OverheatThresholdMj, (IResolveConstraint)(object)Is.EqualTo((object)6f));
				Assert.That<float>(right.ResumeThresholdMj, (IResolveConstraint)(object)Is.EqualTo((object)2f));
				Assert.That<float>(right.SpinUpSeconds, (IResolveConstraint)(object)Is.EqualTo((object)0.5f));
				Assert.That<bool>(mechTestWeaponSystem.TryGetWeaponPerformance("AC6_NATIVE_WP_L_5110", out var left), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(left.IsPointDefense, (IResolveConstraint)(object)Is.True);
				Assert.That<int>(left.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)right.TotalAmmunition));
			}
			finally
			{
				Object.DestroyImmediate(gameObject);
			}
		}

		private static MechPartCatalogEntry Entry(string id, string displayName, MechPartSlot slot, MechPartTestStatus status)
		{
			return new MechPartCatalogEntry(id, displayName, slot, MechSlotRules.IsWeaponSlot(slot) ? MechPartCategory.Weapon : MechPartCategory.Frame, "AC6Parts/" + id, status, string.Empty);
		}
	}
}
