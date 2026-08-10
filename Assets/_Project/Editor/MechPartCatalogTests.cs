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
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_NATIVE_WP_L_4290"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedWeapon("AC6_NATIVE_WP_R_4290"), (IResolveConstraint)(object)Is.True);
		}

		[Test]
		public void TestedPartSource_MarksValidatedMelanderFramePartsAsTested()
		{
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_SKIN_HD_M_1010"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_SKIN_AM_M_1010"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_SKIN_BD_M_1010"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_FULL_AM_M_1010"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_FULL_BD_M_1010"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_SKIN_HD_M_2010"), (IResolveConstraint)(object)Is.False);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_SKIN_BD_M_2010"), (IResolveConstraint)(object)Is.False);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_NATIVE_BS_A_1010"), (IResolveConstraint)(object)Is.True);
			Assert.That<bool>(AC6FullPartCatalogBuilder.IsTestedPart("AC6_SKIN_SAMPLE_GENERATOR"), (IResolveConstraint)(object)Is.True);
		}

		[Test]
		public void BoosterPerformance_ContainsOnlyInstalledMainBoosterCapabilities()
		{
			var booster = new MechBoosterPerformance
			{
				mainThrustKn = 660f,
				assaultBoostThrustKn = 3500f,
				quickBoostThrustKn = 16000f,
				quickBoostDurationS = 0.28f,
				quickBoostRecoveryS = 0.42f,
				horizontalVectorLimitDeg = 20f,
				verticalVectorLimitDeg = 35f,
				assaultBoostPowerDrawKw = 45000f,
				quickBoostPowerDrawKw = 180000f
			};

			MechBoosterPerformance clone = booster.Clone();

			Assert.That(clone.mainThrustKn, Is.EqualTo(660f));
			Assert.That(clone.quickBoostPowerDrawKw, Is.EqualTo(180000f));
			Assert.That(typeof(MechBoosterPerformance).GetField("structuralDurability"), Is.Null);
			Assert.That(typeof(MechBoosterPerformance).GetField("armorDurability"), Is.Null);
			Assert.That(typeof(MechBoosterPerformance).GetField("thrustRampTimeS"), Is.Null);
			Assert.That(typeof(MechBoosterPerformance).GetField("gimbalRateDegS"), Is.Null);
			Assert.That(typeof(MechBoosterPerformance).GetField("thrusterNozzles"), Is.Null);
		}

		[Test]
		public void AggregatedStats_GeneratorDefinesEnergyCapacityAndRecharge()
		{
			var stats = new MechAggregatedStats();
			stats.AddGeneratorPerformance(new MechGeneratorPerformance
			{
				energyStorageCapacityMj = 300f,
				energyRechargePowerKw = 70000f,
				energyRechargeDelayS = 0.75f,
				depletedRestartFraction = 0.2f,
				startupTimeS = 7.5f
			});

			Assert.That(stats.energyStorageCapacityMj, Is.EqualTo(300f));
			Assert.That(stats.energyRechargePowerKw, Is.EqualTo(70000f));
			Assert.That(stats.energyRechargeDelayS, Is.EqualTo(0.75f));
			Assert.That(stats.depletedEnergyRestartFraction, Is.EqualTo(0.2f));
		}

		[Test]
		public void HeadSensorPerformance_CloneCopiesValuesAndCharacteristicIds()
		{
			MechHeadSensorPerformance source = new MechHeadSensorPerformance
			{
				pointingCapability = 62f,
				searchCapability = 58f,
				trackingCapability = 64f,
				identificationCapability = 60f,
				maximumMagnification = 12f,
				referenceHotRangeKm = 32f,
				referenceColdRangeKm = 18f,
				trackCapacity = 12,
				referenceRangeErrorM = 180f,
				laserRangefinderRangeKm = 20f,
				laserRangefinderErrorM = 0.75f,
				supportedBands = MechElectroOpticalBand.Visible | MechElectroOpticalBand.Mwir,
				allowedCharacteristicIds = new[] { "baseline-optics" }
			};

			MechHeadSensorPerformance clone = source.Clone();

			Assert.That(clone, Is.Not.SameAs(source));
			Assert.That(clone.pointingCapability, Is.EqualTo(62f));
			Assert.That(clone.maximumMagnification, Is.EqualTo(12f));
			Assert.That(clone.supportedBands, Is.EqualTo(MechElectroOpticalBand.Visible | MechElectroOpticalBand.Mwir));
			Assert.That(clone.allowedCharacteristicIds, Is.Not.SameAs(source.allowedCharacteristicIds));
			Assert.That(clone.allowedCharacteristicIds, Is.EqualTo(source.allowedCharacteristicIds));
		}

		[Test]
		public void ArmPerformance_UsesSingleSideNozzleTemplateAndDeepClonesIt()
		{
			var source = new MechArmPerformance
			{
				weaponLoadLimitKg = 8000f,
				weaponPointingErrorMrad = 0.18f,
				thrusterNozzles = new[]
				{
					new MechArmThrusterNozzle
					{
						nozzleId = "lateral",
						role = MechArmThrusterRole.Lateral,
						maximumThrustKn = 40f
					},
					new MechArmThrusterNozzle
					{
						nozzleId = "rcs-forward",
						role = MechArmThrusterRole.Rcs,
						maximumThrustKn = 75f
					},
					new MechArmThrusterNozzle
					{
						nozzleId = "rcs-rear",
						role = MechArmThrusterRole.Rcs,
						maximumThrustKn = 75f
					}
				}
			};

			MechArmPerformance clone = source.Clone();

			Assert.That(source.InstalledNozzleCount, Is.EqualTo(6));
			Assert.That(source.LateralThrustContributionKn, Is.EqualTo(40f));
			Assert.That(source.RcsThrustContributionKn, Is.EqualTo(300f));
			Assert.That(clone.thrusterNozzles, Is.Not.SameAs(source.thrusterNozzles));
			Assert.That(clone.thrusterNozzles[0], Is.Not.SameAs(source.thrusterNozzles[0]));
		}

		[Test]
		public void AggregatedStats_CombineArmThrustersWithOtherRcsAndLateralSources()
		{
			var stats = new MechAggregatedStats();
			stats.Add(new MechPartStats { massKg = 12000f, rcsThrustKn = 360f });
			stats.Add(new MechPartStats { massKg = 9800f, rcsThrustKn = 240f });
			stats.Add(new MechPartStats { massKg = 34580f, lateralThrustKn = 300f });
			stats.AddArmPerformance(new MechArmPerformance
			{
				thrusterNozzles = new[]
				{
					new MechArmThrusterNozzle { role = MechArmThrusterRole.Lateral, maximumThrustKn = 40f },
					new MechArmThrusterNozzle { role = MechArmThrusterRole.Rcs, maximumThrustKn = 75f },
					new MechArmThrusterNozzle { role = MechArmThrusterRole.Rcs, maximumThrustKn = 75f }
				}
			});
			stats.RecalculateDerived();

			Assert.That(stats.totalMassKg, Is.EqualTo(56380f));
			Assert.That(stats.lateralThrustKn, Is.EqualTo(340f));
			Assert.That(stats.rcsThrustKn, Is.EqualTo(900f));
			Assert.That(stats.lateralAccelerationMps2, Is.EqualTo(340000f / 56380f).Within(0.001f));
			Assert.That(stats.rcsTurnRateDegreesPerSecond, Is.EqualTo(180f).Within(0.2f));
		}

		[Test]
		public void AggregatedStats_CalculateIndependentVerticalAccelerations()
		{
			var stats = new MechAggregatedStats();
			stats.Add(new MechPartStats
			{
				massKg = 50000f,
				upwardThrustKn = 1500f,
				downwardThrustKn = 450f
			});
			stats.RecalculateDerived();

			Assert.That(stats.upwardAccelerationMps2, Is.EqualTo(30f).Within(0.001f));
			Assert.That(stats.downwardAccelerationMps2, Is.EqualTo(9f).Within(0.001f));
		}

		[Test]
		public void LegPerformance_DerivesVectoredAxisLimitsWithoutDuplicatingNozzles()
		{
			var legs = new MechLegPerformance
			{
				maximumSupportedMassKg = 65000f,
				primaryThrusterNozzles = new[]
				{
					new MechLegThrusterNozzle { maximumThrustKn = 375f, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18f, lateralGimbalLimitDeg = 0.5f },
					new MechLegThrusterNozzle { maximumThrustKn = 375f, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18f, lateralGimbalLimitDeg = 0.5f }
				},
				lateralThrusterNozzles = new[]
				{
					new MechLegThrusterNozzle { maximumThrustKn = 8f }
				},
				rcsThrusterNozzles = new[]
				{
					new MechLegThrusterNozzle { maximumThrustKn = 60f },
					new MechLegThrusterNozzle { maximumThrustKn = 60f }
				}
			};

			Assert.That(legs.UpwardThrustContributionKn, Is.EqualTo(1500f).Within(0.01f));
			Assert.That(legs.MainThrustContributionKn, Is.EqualTo(1500f * Mathf.Sin(10f * Mathf.Deg2Rad)).Within(0.01f));
			Assert.That(legs.ReverseThrustContributionKn, Is.EqualTo(1500f * Mathf.Sin(18f * Mathf.Deg2Rad)).Within(0.01f));
			Assert.That(legs.LateralThrustContributionKn, Is.EqualTo(1500f * Mathf.Sin(0.5f * Mathf.Deg2Rad) + 8f).Within(0.01f));
			Assert.That(legs.RcsThrustContributionKn, Is.EqualTo(240f).Within(0.01f));
			Assert.That(legs.InstalledNozzleCount, Is.EqualTo(10));
		}

		[Test]
		public void AggregatedStats_AddLegPerformanceContributesEveryMovementAxisAndCapacity()
		{
			var stats = new MechAggregatedStats();
			var legs = new MechLegPerformance
			{
				maximumSupportedMassKg = 65000f,
				primaryThrusterNozzles = new[]
				{
					new MechLegThrusterNozzle { maximumThrustKn = 750f, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18f }
				},
				rcsThrusterNozzles = new[]
				{
					new MechLegThrusterNozzle { maximumThrustKn = 120f }
				}
			};

			stats.Add(new MechPartStats { massKg = 50000f });
			stats.AddLegPerformance(legs);
			stats.RecalculateDerived();

			Assert.That(stats.upwardThrustKn, Is.EqualTo(1500f));
			Assert.That(stats.mainThrustKn, Is.GreaterThan(260f));
			Assert.That(stats.reverseThrustKn, Is.GreaterThan(460f));
			Assert.That(stats.rcsThrustKn, Is.EqualTo(240f));
			Assert.That(stats.supportedMassKg, Is.EqualTo(65000f));
		}

		[Test]
		public void CorePerformance_ClonesNozzleListsAndSumsReactionThrust()
		{
			var source = new MechCorePerformance
			{
				forwardExhaustNozzles = new[]
				{
					new MechCoreThrusterNozzle { nozzleId = "forward-left", maximumThrustKn = 150f, maximumPowerKw = 500f },
					new MechCoreThrusterNozzle { nozzleId = "forward-right", maximumThrustKn = 155f, maximumPowerKw = 550f }
				},
				upwardExhaustNozzles = new[]
				{
					new MechCoreThrusterNozzle { nozzleId = "up-left", maximumThrustKn = 225f },
					new MechCoreThrusterNozzle { nozzleId = "up-right", maximumThrustKn = 225f }
				}
			};

			MechCorePerformance clone = source.Clone();

			Assert.That(source.ForwardExhaustNozzleCount, Is.EqualTo(2));
			Assert.That(source.UpwardExhaustNozzleCount, Is.EqualTo(2));
			Assert.That(source.ReverseThrustContributionKn, Is.EqualTo(305f));
			Assert.That(source.DownwardThrustContributionKn, Is.EqualTo(450f));
			Assert.That(source.ReversePowerKw, Is.EqualTo(1050f));
			Assert.That(clone.forwardExhaustNozzles, Is.Not.SameAs(source.forwardExhaustNozzles));
			Assert.That(clone.forwardExhaustNozzles[0], Is.Not.SameAs(source.forwardExhaustNozzles[0]));
		}

		[Test]
		public void AggregatedStats_CoreNozzlesProvideReverseAndDownwardAcceleration()
		{
			var stats = new MechAggregatedStats();
			stats.Add(new MechPartStats { massKg = 50000f });
			stats.AddCorePerformance(new MechCorePerformance
			{
				forwardExhaustNozzles = new[]
				{
					new MechCoreThrusterNozzle { maximumThrustKn = 305f }
				},
				upwardExhaustNozzles = new[]
				{
					new MechCoreThrusterNozzle { maximumThrustKn = 450f }
				}
			});
			stats.RecalculateDerived();

			Assert.That(stats.reverseAccelerationMps2, Is.EqualTo(6.1f).Within(0.001f));
			Assert.That(stats.downwardAccelerationMps2, Is.EqualTo(9f).Within(0.001f));
		}

		[Test]
		public void AggregatedStats_BoosterVectorLimitsContributeDirectionalThrust()
		{
			var stats = new MechAggregatedStats();
			stats.AddBoosterPerformance(new MechBoosterPerformance
			{
				mainThrustKn = 1000f,
				horizontalVectorLimitDeg = 30f,
				verticalVectorLimitDeg = 20f,
				quickBoostRecoveryS = 0.45f
			});

			Assert.That(stats.mainThrustKn, Is.EqualTo(1000f).Within(0.01f));
			Assert.That(stats.lateralThrustKn, Is.EqualTo(500f).Within(0.01f));
			Assert.That(
				stats.upwardThrustKn,
				Is.EqualTo(1000f * Mathf.Sin(20f * Mathf.Deg2Rad)).Within(0.01f));
			Assert.That(stats.boosterPerformance.quickBoostRecoveryS, Is.EqualTo(0.45f));
		}

		[Test]
		public void AggregatedStats_RcsAndMomentumWheelDefineMaximumTurnRate()
		{
			var stats = new MechAggregatedStats();
			stats.Add(new MechPartStats { massKg = 50000f, rcsThrustKn = 500f });
			stats.AddCorePerformance(new MechCorePerformance
			{
				momentumWheelTurnRateDegPerSecond = 12f
			});
			stats.RecalculateDerived();

			Assert.That(stats.rcsAccelerationMps2, Is.EqualTo(10f).Within(0.001f));
			Assert.That(stats.rcsTurnRateDegreesPerSecond, Is.EqualTo(124.8f).Within(0.01f));
		}

		[TestCase(50000f, 65000f, 1f)]
		[TestCase(65000f, 65000f, 1f)]
		[TestCase(97500f, 65000f, 0.2962963f)]
		[TestCase(260000f, 65000f, 0.2f)]
		public void OverloadAccelerationScale_AppliesCubicPenaltyAboveLegLimit(
			float massKg,
			float supportedMassKg,
			float expected)
		{
			Assert.That(
				MechFlightMotionController.CalculateOverloadAccelerationScale(
					massKg,
					supportedMassKg),
				Is.EqualTo(expected).Within(0.0001f));
		}

		[Test]
		public void SignatureFallback_AssignsDifferentContributionsByPartSlot()
		{
			var core = new MechPartStats();
			var radar = new MechPartStats();
			MechPartSignatureDefaults.ApplyIfMissing(core, MechPartSlot.Core);
			MechPartSignatureDefaults.ApplyIfMissing(radar, MechPartSlot.Radar);

			Assert.That(core.radarCrossSection, Is.GreaterThan(radar.radarCrossSection));
			Assert.That(radar.electromagneticSignature, Is.GreaterThan(core.electromagneticSignature));
			Assert.That(MechPartSignatureDefaults.HasConfiguredSignature(core), Is.True);

			var partial = new MechPartStats { radarCrossSection = 9f };
			MechPartSignatureDefaults.ApplyIfMissing(partial, MechPartSlot.Head);
			Assert.That(partial.radarCrossSection, Is.EqualTo(9f));
			Assert.That(partial.thermalSignature, Is.GreaterThan(0f));
			Assert.That(partial.electromagneticSignature, Is.GreaterThan(0f));
		}

		[Test]
		public void ProtectionDamage_UsesFixedReductionAndFullOverflowDamage()
		{
			CombatDamageBreakdown armored = CombatTestTarget.CalculateProtectionDamage(100f, 100f, 40f);
			Assert.That(armored.ArmorDamage, Is.EqualTo(40f));
			Assert.That(armored.StructureDamage, Is.EqualTo(10f));

			CombatDamageBreakdown overflow = CombatTestTarget.CalculateProtectionDamage(20f, 100f, 40f);
			Assert.That(overflow.ArmorDamage, Is.EqualTo(20f));
			Assert.That(overflow.StructureDamage, Is.EqualTo(25f));

			CombatDamageBreakdown unarmored = CombatTestTarget.CalculateProtectionDamage(0f, 100f, 40f);
			Assert.That(unarmored.ArmorDamage, Is.Zero);
			Assert.That(unarmored.StructureDamage, Is.EqualTo(40f));
		}

		[Test]
		public void WeaponRoleResolver_UsesPartTypeInsteadOfMountSide()
		{
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_L_1310"), Is.EqualTo(MechWeaponRole.HandCannon));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_R_1310"), Is.EqualTo(MechWeaponRole.HandCannon));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_L_1610"), Is.EqualTo(MechWeaponRole.AutomaticCannon));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_R_1610"), Is.EqualTo(MechWeaponRole.AutomaticCannon));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_L_4250"), Is.EqualTo(MechWeaponRole.InterceptorMissile));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_R_4250"), Is.EqualTo(MechWeaponRole.InterceptorMissile));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_L_4290"), Is.EqualTo(MechWeaponRole.AirToAirMissile));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_R_4290"), Is.EqualTo(MechWeaponRole.AirToAirMissile));
			Assert.That(ResolveTestWeaponRole("AC6_NATIVE_WP_L_5110"), Is.EqualTo(MechWeaponRole.PointDefenseCannon));
			Assert.That(ResolveTestWeaponRole("AC6_NATIVE_WP_R_5110"), Is.EqualTo(MechWeaponRole.PointDefenseCannon));
			Assert.That(ResolveTestWeaponRole("AC6_FULL_WP_R_9999"), Is.EqualTo(MechWeaponRole.None));
		}

		[Test]
		public void WeaponPerformance_ExposesUnifiedGunFieldsByFireMode()
		{
			GameObject gameObject = new GameObject("weapon-performance-test");
			gameObject.SetActive(value: false);
			try
			{
				MechTestWeaponSystem mechTestWeaponSystem = gameObject.AddComponent<MechTestWeaponSystem>();
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_R_1610", out var rifle), (IResolveConstraint)(object)Is.True);
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

				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_FULL_WP_R_1630", out var precisionCannon), (IResolveConstraint)(object)Is.True);
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

				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_FULL_WP_R_1720", out var rapidCannon), (IResolveConstraint)(object)Is.True);
				Assert.That<float>(rapidCannon.DirectHitDamage, (IResolveConstraint)(object)Is.EqualTo((object)85f));
				Assert.That<float>(rapidCannon.FireRateRoundsPerMinute, (IResolveConstraint)(object)Is.EqualTo((object)580f));
				Assert.That<float>(precisionCannon.EffectiveRangeMeters, Is.GreaterThan(rapidCannon.EffectiveRangeMeters + 800f));
				Assert.That<float>(rapidCannon.EffectiveRangeMeters, Is.GreaterThan(rifle.EffectiveRangeMeters));
				Assert.That<float>(precisionCannon.EffectiveRangeMeters, Is.LessThan(precisionCannon.MaximumRangeMeters));
				AssertRiflePerformanceMatches(mechTestWeaponSystem, "AC6_FULL_WP_L_1720", rapidCannon);
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_L_1310", out var cannon), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_R_1310", out var mirroredCannon), (IResolveConstraint)(object)Is.True);
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
			Assert.That<bool>(TryGetTestWeaponPerformance(weaponSystem, partId, out var actual), (IResolveConstraint)(object)Is.True);
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
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_L_4250", out var interceptor), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_R_4250", out var mirroredInterceptor), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(interceptor.IsGuidedMissile, (IResolveConstraint)(object)Is.True);
				Assert.That<float>(interceptor.EffectiveRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)15000f));
				Assert.That<int>(interceptor.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)32));
				Assert.That<float>(interceptor.SeekerRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)12000f));
				Assert.That<bool>(interceptor.HasProximityFuse, (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_R_4290", out var airToAir), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_L_4290", out var mirroredAirToAir), (IResolveConstraint)(object)Is.True);
				Assert.That<float>(airToAir.EffectiveRangeMeters, (IResolveConstraint)(object)Is.EqualTo((object)50000f));
				Assert.That<int>(airToAir.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)24));
				Assert.That<float>(airToAir.MaximumAngularRateDegreesPerSecond, (IResolveConstraint)(object)Is.EqualTo((object)240f));
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_FULL_WP_R_9999", out var _), (IResolveConstraint)(object)Is.False);
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
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_R_5110", out var right), (IResolveConstraint)(object)Is.True);
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
				Assert.That<bool>(TryGetTestWeaponPerformance(mechTestWeaponSystem, "AC6_NATIVE_WP_L_5110", out var left), (IResolveConstraint)(object)Is.True);
				Assert.That<bool>(left.IsPointDefense, (IResolveConstraint)(object)Is.True);
				Assert.That<int>(left.TotalAmmunition, (IResolveConstraint)(object)Is.EqualTo((object)right.TotalAmmunition));
			}
			finally
			{
				Object.DestroyImmediate(gameObject);
			}
		}

		[Test]
		public void WeaponPerformance_RuntimeReadsValuesFromThePartDefinition()
		{
			GameObject gameObject = new GameObject("part-owned-weapon-performance-test");
			MechPartDefinition part = CreateTestWeaponPart("AC6_FULL_WP_R_1610");
			gameObject.SetActive(value: false);
			try
			{
				part.WeaponPerformance.directHitDamage = 777f;
				part.WeaponPerformance.magazineCapacity = 17;
				MechTestWeaponSystem weaponSystem = gameObject.AddComponent<MechTestWeaponSystem>();

				Assert.That(weaponSystem.TryGetWeaponPerformance(part, out var performance), Is.True);
				Assert.That(performance.DirectHitDamage, Is.EqualTo(777f));
				Assert.That(performance.MagazineCapacity, Is.EqualTo(17));
			}
			finally
			{
				Object.DestroyImmediate(part);
				Object.DestroyImmediate(gameObject);
			}
		}

		private static MechWeaponRole ResolveTestWeaponRole(string partId)
		{
			MechPartDefinition part = CreateTestWeaponPart(partId);
			try
			{
				return MechWeaponRoleResolver.Resolve(part);
			}
			finally
			{
				Object.DestroyImmediate(part);
			}
		}

		private static bool TryGetTestWeaponPerformance(
			MechTestWeaponSystem weaponSystem,
			string partId,
			out MechWeaponPerformanceSnapshot performance)
		{
			MechPartDefinition part = CreateTestWeaponPart(partId);
			try
			{
				return weaponSystem.TryGetWeaponPerformance(part, out performance);
			}
			finally
			{
				Object.DestroyImmediate(part);
			}
		}

		private static MechPartDefinition CreateTestWeaponPart(string partId)
		{
			MechPartDefinition part = ScriptableObject.CreateInstance<MechPartDefinition>();
			bool left = partId != null && partId.Contains("_WP_L_");
			part.EditorConfigure(
				newPartId: partId,
				newDisplayName: partId,
				newSlot: left ? MechPartSlot.LeftHandWeapon : MechPartSlot.RightHandWeapon,
				newCategory: MechPartCategory.Weapon,
				newModelPrefab: null,
				newStats: new MechPartStats(),
				newMountPoints: null,
				newWeaponPerformance: AC6TestWeaponProfileFactory.Create(partId));
			return part;
		}

		private static MechPartCatalogEntry Entry(string id, string displayName, MechPartSlot slot, MechPartTestStatus status)
		{
			return new MechPartCatalogEntry(id, displayName, slot, MechSlotRules.IsWeaponSlot(slot) ? MechPartCategory.Weapon : MechPartCategory.Frame, "AC6Parts/" + id, status, string.Empty);
		}
	}
}
