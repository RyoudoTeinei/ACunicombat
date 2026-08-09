using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
	[Serializable]
	public sealed class MechPartStats
	{
		[Header("Structure")]
		[Min(0f)]
		public float massKg;

		[Min(0f)]
		public float armorPoints;

		[Min(0f)]
		public float structurePoints;

		[Min(0f)]
		public float stability;

		[Header("Power and Heat")]
		public float powerOutputMw;

		public float idlePowerDrawMw;

		public float peakPowerDrawMw;

		public float heatGenerationMw;

		public float heatDissipationMw;

		public float heatCapacityMj;

		public float idleHeatMw;

		public float activeHeatMw;

		[Header("Flight")]
		public float mainThrustKn;

		public float lateralThrustKn;

		public float reverseThrustKn;

		public float upwardThrustKn;

		public float downwardThrustKn;

		public float attitudeTorqueKnM;

		[Tooltip("可用于机体转向的 RCS 单向等效推力。多个部件的贡献会在整机层汇总。")]
		public float rcsThrustKn;

		public float supportedMassKg;

		[Header("Sensors and Fire Control")]
		public float radarRangeKm;

		public float radarAperture;

		public float radarRefreshHz;

		public float sensorPrecision;

		public int trackCapacity;

		public float fireControlQuality;

		public int guidanceChannels;

		[Header("Signatures")]
		public float radarCrossSection;

		public float thermalSignature;

		public float electromagneticSignature;

		[Header("Weapons")]
		public float weaponRecoilControl;

		public float hardpointCapacityKg;

		public void Add(MechPartStats other)
		{
			if (other != null)
			{
				massKg += other.massKg;
				armorPoints += other.armorPoints;
				structurePoints += other.structurePoints;
				stability += other.stability;
				powerOutputMw += other.powerOutputMw;
				idlePowerDrawMw += other.idlePowerDrawMw;
				peakPowerDrawMw += other.peakPowerDrawMw;
				heatGenerationMw += other.heatGenerationMw;
				heatDissipationMw += other.heatDissipationMw;
				heatCapacityMj += other.heatCapacityMj;
				idleHeatMw += other.idleHeatMw;
				activeHeatMw += other.activeHeatMw;
				mainThrustKn += other.mainThrustKn;
				lateralThrustKn += other.lateralThrustKn;
				reverseThrustKn += other.reverseThrustKn;
				upwardThrustKn += other.upwardThrustKn;
				downwardThrustKn += other.downwardThrustKn;
				attitudeTorqueKnM += other.attitudeTorqueKnM;
				rcsThrustKn += other.rcsThrustKn;
				supportedMassKg += other.supportedMassKg;
				radarRangeKm += other.radarRangeKm;
				radarAperture += other.radarAperture;
				radarRefreshHz += other.radarRefreshHz;
				sensorPrecision += other.sensorPrecision;
				trackCapacity += other.trackCapacity;
				fireControlQuality += other.fireControlQuality;
				guidanceChannels += other.guidanceChannels;
				radarCrossSection += other.radarCrossSection;
				thermalSignature += other.thermalSignature;
				electromagneticSignature += other.electromagneticSignature;
				weaponRecoilControl += other.weaponRecoilControl;
				hardpointCapacityKg += other.hardpointCapacityKg;
			}
		}

		public void CopyFrom(MechPartStats other)
		{
			if (other != null)
			{
				massKg = other.massKg;
				armorPoints = other.armorPoints;
				structurePoints = other.structurePoints;
				stability = other.stability;
				powerOutputMw = other.powerOutputMw;
				idlePowerDrawMw = other.idlePowerDrawMw;
				peakPowerDrawMw = other.peakPowerDrawMw;
				heatGenerationMw = other.heatGenerationMw;
				heatDissipationMw = other.heatDissipationMw;
				heatCapacityMj = other.heatCapacityMj;
				idleHeatMw = other.idleHeatMw;
				activeHeatMw = other.activeHeatMw;
				mainThrustKn = other.mainThrustKn;
				lateralThrustKn = other.lateralThrustKn;
				reverseThrustKn = other.reverseThrustKn;
				upwardThrustKn = other.upwardThrustKn;
				downwardThrustKn = other.downwardThrustKn;
				attitudeTorqueKnM = other.attitudeTorqueKnM;
				rcsThrustKn = other.rcsThrustKn;
				supportedMassKg = other.supportedMassKg;
				radarRangeKm = other.radarRangeKm;
				radarAperture = other.radarAperture;
				radarRefreshHz = other.radarRefreshHz;
				sensorPrecision = other.sensorPrecision;
				trackCapacity = other.trackCapacity;
				fireControlQuality = other.fireControlQuality;
				guidanceChannels = other.guidanceChannels;
				radarCrossSection = other.radarCrossSection;
				thermalSignature = other.thermalSignature;
				electromagneticSignature = other.electromagneticSignature;
				weaponRecoilControl = other.weaponRecoilControl;
				hardpointCapacityKg = other.hardpointCapacityKg;
			}
		}
	}

	[Serializable]
	public sealed class MechRadarPerformance
	{
		[Min(0f)] public float referenceDetectionRangeKm;
		[Min(0f)] public float stableTrackRangeKm;
		[Range(1f, 360f)] public float searchAzimuthDeg = 100f;
		[Range(1f, 180f)] public float searchElevationDeg = 60f;
		[Range(1, RadarTrackManagerSafetyLimit)] public int trackCapacity = 8;
		[Range(1, 8)] public int sttChannelCapacity = 1;
		[Min(0f)] public float maximumTrackingRateDegPerSecond;
		[Min(0f)] public float trackMemorySeconds = 3f;
		[Min(0f)] public float rangeAccuracyM;
		[Min(0f)] public float angularAccuracyMrad;
		[Min(0f)] public float radialVelocityAccuracyMps;
		[Min(0f)] public float searchDwellRateHz;
		[Min(0f)] public float twsUpdateRateHz;
		[Min(0f)] public float sttUpdateRateHz;

		public const int RadarTrackManagerSafetyLimit = 32;

		public bool HasData => referenceDetectionRangeKm > 0f || stableTrackRangeKm > 0f;

		public float LockMaintenanceRating
		{
			get
			{
				float rangeRatio = referenceDetectionRangeKm > 0f
					? Mathf.Clamp01(stableTrackRangeKm / referenceDetectionRangeKm)
					: 0f;
				float revisit = Mathf.Clamp01(twsUpdateRateHz / 8f);
				float trackingRate = Mathf.Clamp01(maximumTrackingRateDegPerSecond / 180f);
				return (rangeRatio * 0.45f + revisit * 0.35f + trackingRate * 0.2f) * 100f;
			}
		}

		public void CopyFrom(MechRadarPerformance other)
		{
			if (other == null) return;
			referenceDetectionRangeKm = other.referenceDetectionRangeKm;
			stableTrackRangeKm = other.stableTrackRangeKm;
			searchAzimuthDeg = other.searchAzimuthDeg;
			searchElevationDeg = other.searchElevationDeg;
			trackCapacity = other.trackCapacity;
			sttChannelCapacity = other.sttChannelCapacity;
			maximumTrackingRateDegPerSecond = other.maximumTrackingRateDegPerSecond;
			trackMemorySeconds = other.trackMemorySeconds;
			rangeAccuracyM = other.rangeAccuracyM;
			angularAccuracyMrad = other.angularAccuracyMrad;
			radialVelocityAccuracyMps = other.radialVelocityAccuracyMps;
			searchDwellRateHz = other.searchDwellRateHz;
			twsUpdateRateHz = other.twsUpdateRateHz;
			sttUpdateRateHz = other.sttUpdateRateHz;
		}

		public MechRadarPerformance Clone()
		{
			var clone = new MechRadarPerformance();
			clone.CopyFrom(this);
			return clone;
		}
	}

	[Serializable]
	public sealed class MechFireControlPerformance
	{
		[Min(0f)] public float baseSolutionErrorMrad;
		[Min(0f)] public float maximumPredictionTimeS;
		[Range(1, 32)] public int simultaneousSolutionCapacity = 1;
		[Range(1, 16)] public int weaponControlChannels = 1;
		[Range(0, 16)] public int guidanceChannelCapacity;
		[Range(1, 16)] public int multiTargetEngagementCapacity = 1;
		[Range(0f, 100f)] public float pointDefenseRating;
		[Min(0f)] public float trackReacquisitionTimeS;
		[Min(0f)] public float solutionUpdateRateHz;
		[Min(0f)] public float solutionLatencyMs;

		public bool HasData => baseSolutionErrorMrad > 0f || solutionUpdateRateHz > 0f ||
			guidanceChannelCapacity > 0;

		public void CopyFrom(MechFireControlPerformance other)
		{
			if (other == null) return;
			baseSolutionErrorMrad = other.baseSolutionErrorMrad;
			maximumPredictionTimeS = other.maximumPredictionTimeS;
			simultaneousSolutionCapacity = other.simultaneousSolutionCapacity;
			weaponControlChannels = other.weaponControlChannels;
			guidanceChannelCapacity = other.guidanceChannelCapacity;
			multiTargetEngagementCapacity = other.multiTargetEngagementCapacity;
			pointDefenseRating = other.pointDefenseRating;
			trackReacquisitionTimeS = other.trackReacquisitionTimeS;
			solutionUpdateRateHz = other.solutionUpdateRateHz;
			solutionLatencyMs = other.solutionLatencyMs;
		}

		public MechFireControlPerformance Clone()
		{
			var clone = new MechFireControlPerformance();
			clone.CopyFrom(this);
			return clone;
		}
	}

	public enum MechArmThrusterRole
	{
		Lateral = 0,
		Rcs = 10
	}

	[Serializable]
	public sealed class MechArmThrusterNozzle
	{
		public string nozzleId;
		public MechArmThrusterRole role;
		public Vector3 localPositionM;
		[Tooltip("喷口对机体施加推力的方向，不是废气排出方向。另一侧会镜像 X 分量。")]
		public Vector3 localThrustDirection = Vector3.right;
		[Min(0f)] public float maximumThrustKn;
		[Min(0f)] public float gimbalLimitDeg;
		[Min(0f)] public float maximumPowerKw;
		[Min(0f)] public float maximumHeatKw;

		public MechArmThrusterNozzle Clone()
		{
			return new MechArmThrusterNozzle
			{
				nozzleId = nozzleId,
				role = role,
				localPositionM = localPositionM,
				localThrustDirection = localThrustDirection,
				maximumThrustKn = maximumThrustKn,
				gimbalLimitDeg = gimbalLimitDeg,
				maximumPowerKw = maximumPowerKw,
				maximumHeatKw = maximumHeatKw
			};
		}
	}

	[Serializable]
	public sealed class MechArmPerformance
	{
		[Min(0f)] public float weaponLoadLimitKg;
		[Min(0f)] public float recoilImpulseLimitNs;
		[Min(0f)] public float recoilRecoveryRateNsPerSecond;
		[Min(0f)] public float aimTraverseRateDegPerSecond;
		[Min(0f)] public float aimAngularAccelerationDegPerSecondSquared;
		[Min(0f)] public float weaponPointingErrorMrad;
		[Tooltip("仅保存一侧手臂的喷口模板；装配时另一侧按局部 X 轴镜像。")]
		public MechArmThrusterNozzle[] thrusterNozzles = Array.Empty<MechArmThrusterNozzle>();

		public bool HasData => weaponLoadLimitKg > 0f ||
			recoilImpulseLimitNs > 0f || aimTraverseRateDegPerSecond > 0f ||
			(thrusterNozzles != null && thrusterNozzles.Length > 0);

		public int InstalledNozzleCount => (thrusterNozzles?.Length ?? 0) * 2;

		public float LateralThrustContributionKn => SumNozzleThrust(MechArmThrusterRole.Lateral);

		public float RcsThrustContributionKn => SumNozzleThrust(MechArmThrusterRole.Rcs) * 2f;
		public float LateralPowerKw => SumNozzleResource(MechArmThrusterRole.Lateral, nozzle => nozzle.maximumPowerKw) * 2f;
		public float RcsPowerKw => SumNozzleResource(MechArmThrusterRole.Rcs, nozzle => nozzle.maximumPowerKw) * 2f;

		public MechArmPerformance Clone()
		{
			var clone = new MechArmPerformance
			{
				weaponLoadLimitKg = weaponLoadLimitKg,
				recoilImpulseLimitNs = recoilImpulseLimitNs,
				recoilRecoveryRateNsPerSecond = recoilRecoveryRateNsPerSecond,
				aimTraverseRateDegPerSecond = aimTraverseRateDegPerSecond,
				aimAngularAccelerationDegPerSecondSquared = aimAngularAccelerationDegPerSecondSquared,
				weaponPointingErrorMrad = weaponPointingErrorMrad
			};
			if (thrusterNozzles == null || thrusterNozzles.Length == 0)
			{
				clone.thrusterNozzles = Array.Empty<MechArmThrusterNozzle>();
				return clone;
			}

			clone.thrusterNozzles = new MechArmThrusterNozzle[thrusterNozzles.Length];
			for (var i = 0; i < thrusterNozzles.Length; i++)
			{
				clone.thrusterNozzles[i] = thrusterNozzles[i]?.Clone();
			}
			return clone;
		}

		private float SumNozzleThrust(MechArmThrusterRole requestedRole)
		{
			var total = 0f;
			if (thrusterNozzles == null)
			{
				return total;
			}
			for (var i = 0; i < thrusterNozzles.Length; i++)
			{
				var nozzle = thrusterNozzles[i];
				if (nozzle != null && nozzle.role == requestedRole)
				{
					total += Mathf.Max(0f, nozzle.maximumThrustKn);
				}
			}
			return total;
		}

		private float SumNozzleResource(
			MechArmThrusterRole requestedRole,
			Func<MechArmThrusterNozzle, float> selector)
		{
			var total = 0f;
			if (thrusterNozzles == null)
			{
				return total;
			}
			for (var i = 0; i < thrusterNozzles.Length; i++)
			{
				var nozzle = thrusterNozzles[i];
				if (nozzle != null && nozzle.role == requestedRole)
				{
					total += Mathf.Max(0f, selector(nozzle));
				}
			}
			return total;
		}
	}

	[Serializable]
	public sealed class MechCoreThrusterNozzle
	{
		public string nozzleId;
		public Vector3 localPositionM;
		[Tooltip("喷口的废气排出方向；机体受到的推力方向与此相反。")]
		public Vector3 localExhaustDirection = Vector3.forward;
		[Min(0f)] public float maximumThrustKn;
		[Min(0f)] public float gimbalLimitDeg;
		[Min(0f)] public float maximumPowerKw;
		[Min(0f)] public float maximumHeatKw;

		public MechCoreThrusterNozzle Clone()
		{
			return new MechCoreThrusterNozzle
			{
				nozzleId = nozzleId,
				localPositionM = localPositionM,
				localExhaustDirection = localExhaustDirection,
				maximumThrustKn = maximumThrustKn,
				gimbalLimitDeg = gimbalLimitDeg,
				maximumPowerKw = maximumPowerKw,
				maximumHeatKw = maximumHeatKw
			};
		}
	}

	[Serializable]
	public sealed class MechCorePerformance
	{
		[Tooltip("向机体前方排气，产生反向机动推力。")]
		public MechCoreThrusterNozzle[] forwardExhaustNozzles = Array.Empty<MechCoreThrusterNozzle>();
		[Tooltip("向机体上方排气，产生下向机动推力。")]
		public MechCoreThrusterNozzle[] upwardExhaustNozzles = Array.Empty<MechCoreThrusterNozzle>();

		public bool HasData => (forwardExhaustNozzles != null && forwardExhaustNozzles.Length > 0) ||
			(upwardExhaustNozzles != null && upwardExhaustNozzles.Length > 0);

		public int ForwardExhaustNozzleCount => forwardExhaustNozzles?.Length ?? 0;
		public int UpwardExhaustNozzleCount => upwardExhaustNozzles?.Length ?? 0;
		public float ReverseThrustContributionKn => SumThrust(forwardExhaustNozzles);
		public float DownwardThrustContributionKn => SumThrust(upwardExhaustNozzles);
		public float ReversePowerKw => SumResource(forwardExhaustNozzles, nozzle => nozzle.maximumPowerKw);
		public float DownwardPowerKw => SumResource(upwardExhaustNozzles, nozzle => nozzle.maximumPowerKw);
		public float MaximumHeatKw => SumResource(forwardExhaustNozzles, nozzle => nozzle.maximumHeatKw) +
			SumResource(upwardExhaustNozzles, nozzle => nozzle.maximumHeatKw);

		public MechCorePerformance Clone()
		{
			return new MechCorePerformance
			{
				forwardExhaustNozzles = CloneNozzles(forwardExhaustNozzles),
				upwardExhaustNozzles = CloneNozzles(upwardExhaustNozzles)
			};
		}

		private static float SumThrust(MechCoreThrusterNozzle[] nozzles)
		{
			var total = 0f;
			if (nozzles == null)
			{
				return total;
			}
			for (var i = 0; i < nozzles.Length; i++)
			{
				if (nozzles[i] != null)
				{
					total += Mathf.Max(0f, nozzles[i].maximumThrustKn);
				}
			}
			return total;
		}

		private static float SumResource(
			MechCoreThrusterNozzle[] nozzles,
			Func<MechCoreThrusterNozzle, float> selector)
		{
			var total = 0f;
			if (nozzles == null || selector == null)
			{
				return total;
			}
			for (var i = 0; i < nozzles.Length; i++)
			{
				if (nozzles[i] != null)
				{
					total += Mathf.Max(0f, selector(nozzles[i]));
				}
			}
			return total;
		}

		private static MechCoreThrusterNozzle[] CloneNozzles(MechCoreThrusterNozzle[] source)
		{
			if (source == null || source.Length == 0)
			{
				return Array.Empty<MechCoreThrusterNozzle>();
			}
			var clone = new MechCoreThrusterNozzle[source.Length];
			for (var i = 0; i < source.Length; i++)
			{
				clone[i] = source[i]?.Clone();
			}
			return clone;
		}
	}

	[Serializable]
	public sealed class MechLegThrusterNozzle
	{
		public string nozzleId;
		public Vector3 localPositionM;
		[Tooltip("喷口对机体施加推力的基础方向，不是废气排出方向；这里只保存单侧腿部模板，另一侧按局部 X 轴镜像。")]
		public Vector3 localThrustDirection = Vector3.up;
		[Min(0f)] public float maximumThrustKn;
		[Min(0f)] public float mainGimbalLimitDeg;
		[Min(0f)] public float reverseGimbalLimitDeg;
		[Min(0f)] public float lateralGimbalLimitDeg;
		[Min(0f)] public float maximumPowerKw;
		[Min(0f)] public float maximumHeatKw;

		public MechLegThrusterNozzle Clone()
		{
			return new MechLegThrusterNozzle
			{
				nozzleId = nozzleId,
				localPositionM = localPositionM,
				localThrustDirection = localThrustDirection,
				maximumThrustKn = maximumThrustKn,
				mainGimbalLimitDeg = mainGimbalLimitDeg,
				reverseGimbalLimitDeg = reverseGimbalLimitDeg,
				lateralGimbalLimitDeg = lateralGimbalLimitDeg,
				maximumPowerKw = maximumPowerKw,
				maximumHeatKw = maximumHeatKw
			};
		}
	}

	[Serializable]
	public sealed class MechLegPerformance
	{
		[Min(0f)] public float maximumSupportedMassKg;
		[Tooltip("单侧腿部主喷口模板；负责上升，并可向主向、反向和横向矢量偏转。")]
		public MechLegThrusterNozzle[] primaryThrusterNozzles = Array.Empty<MechLegThrusterNozzle>();
		[Tooltip("单侧腿部专用横向喷口模板。镜像喷口分别负责相反横移方向。")]
		public MechLegThrusterNozzle[] lateralThrusterNozzles = Array.Empty<MechLegThrusterNozzle>();
		[Tooltip("单侧腿部姿态控制喷口模板。")]
		public MechLegThrusterNozzle[] rcsThrusterNozzles = Array.Empty<MechLegThrusterNozzle>();

		public bool HasData => maximumSupportedMassKg > 0f ||
			(primaryThrusterNozzles != null && primaryThrusterNozzles.Length > 0) ||
			(lateralThrusterNozzles != null && lateralThrusterNozzles.Length > 0) ||
			(rcsThrusterNozzles != null && rcsThrusterNozzles.Length > 0);

		public int PrimaryNozzleCount => (primaryThrusterNozzles?.Length ?? 0) * 2;
		public int LateralNozzleCount => (lateralThrusterNozzles?.Length ?? 0) * 2;
		public int RcsNozzleCount => (rcsThrusterNozzles?.Length ?? 0) * 2;
		public int InstalledNozzleCount => PrimaryNozzleCount + LateralNozzleCount + RcsNozzleCount;
		public float UpwardThrustContributionKn => SumThrust(primaryThrusterNozzles) * 2f;
		public float MainThrustContributionKn => SumVectoredThrust(primaryThrusterNozzles, nozzle => nozzle.mainGimbalLimitDeg) * 2f;
		public float ReverseThrustContributionKn => SumVectoredThrust(primaryThrusterNozzles, nozzle => nozzle.reverseGimbalLimitDeg) * 2f;
		public float LateralThrustContributionKn =>
			SumVectoredThrust(primaryThrusterNozzles, nozzle => nozzle.lateralGimbalLimitDeg) * 2f +
			SumThrust(lateralThrusterNozzles);
		public float RcsThrustContributionKn => SumThrust(rcsThrusterNozzles) * 2f;
		public float MaximumMainGimbalDeg => MaximumGimbal(primaryThrusterNozzles, nozzle => nozzle.mainGimbalLimitDeg);
		public float MaximumReverseGimbalDeg => MaximumGimbal(primaryThrusterNozzles, nozzle => nozzle.reverseGimbalLimitDeg);
		public float MaximumLateralGimbalDeg => MaximumGimbal(primaryThrusterNozzles, nozzle => nozzle.lateralGimbalLimitDeg);
		public float MaximumPowerKw => SumNozzleResource(nozzle => nozzle.maximumPowerKw);
		public float MaximumHeatKw => SumNozzleResource(nozzle => nozzle.maximumHeatKw);
		public float PrimaryPowerKw => SumValue(primaryThrusterNozzles, nozzle => nozzle.maximumPowerKw) * 2f;
		public float LateralPowerKw => SumValue(lateralThrusterNozzles, nozzle => nozzle.maximumPowerKw) * 2f;
		public float RcsPowerKw => SumValue(rcsThrusterNozzles, nozzle => nozzle.maximumPowerKw) * 2f;

		public MechLegPerformance Clone()
		{
			return new MechLegPerformance
			{
				maximumSupportedMassKg = maximumSupportedMassKg,
				primaryThrusterNozzles = CloneNozzles(primaryThrusterNozzles),
				lateralThrusterNozzles = CloneNozzles(lateralThrusterNozzles),
				rcsThrusterNozzles = CloneNozzles(rcsThrusterNozzles)
			};
		}

		private float SumNozzleResource(Func<MechLegThrusterNozzle, float> selector)
		{
			return (SumValue(primaryThrusterNozzles, selector) +
				SumValue(lateralThrusterNozzles, selector) +
				SumValue(rcsThrusterNozzles, selector)) * 2f;
		}

		private static float SumThrust(MechLegThrusterNozzle[] nozzles)
		{
			return SumValue(nozzles, nozzle => nozzle.maximumThrustKn);
		}

		private static float SumVectoredThrust(
			MechLegThrusterNozzle[] nozzles,
			Func<MechLegThrusterNozzle, float> angleSelector)
		{
			return SumValue(nozzles, nozzle =>
				Mathf.Max(0f, nozzle.maximumThrustKn) *
				Mathf.Sin(Mathf.Clamp(angleSelector(nozzle), 0f, 90f) * Mathf.Deg2Rad));
		}

		private static float MaximumGimbal(
			MechLegThrusterNozzle[] nozzles,
			Func<MechLegThrusterNozzle, float> selector)
		{
			var maximum = 0f;
			if (nozzles == null)
			{
				return maximum;
			}
			for (var i = 0; i < nozzles.Length; i++)
			{
				if (nozzles[i] != null)
				{
					maximum = Mathf.Max(maximum, selector(nozzles[i]));
				}
			}
			return maximum;
		}

		private static float SumValue(
			MechLegThrusterNozzle[] nozzles,
			Func<MechLegThrusterNozzle, float> selector)
		{
			var total = 0f;
			if (nozzles == null)
			{
				return total;
			}
			for (var i = 0; i < nozzles.Length; i++)
			{
				if (nozzles[i] != null)
				{
					total += Mathf.Max(0f, selector(nozzles[i]));
				}
			}
			return total;
		}

		private static MechLegThrusterNozzle[] CloneNozzles(MechLegThrusterNozzle[] source)
		{
			if (source == null || source.Length == 0)
			{
				return Array.Empty<MechLegThrusterNozzle>();
			}
			var clone = new MechLegThrusterNozzle[source.Length];
			for (var i = 0; i < source.Length; i++)
			{
				clone[i] = source[i]?.Clone();
			}
			return clone;
		}
	}

	[Serializable]
	public sealed class MechBoosterPerformance
	{
		[Min(0f)] public float mainThrustKn;
		[Min(0f)] public float assaultBoostThrustKn;
		[Min(0f)] public float quickBoostThrustKn;
		[Min(0f)] public float quickBoostDurationS;
		[Min(0f)] public float quickBoostRecoveryS;
		[Range(0f, 89f)] public float horizontalVectorLimitDeg;
		[Range(0f, 89f)] public float verticalVectorLimitDeg;
		[Min(0f)] public float assaultBoostPowerDrawKw;
		[Min(0f)] public float quickBoostPowerDrawKw;

		public bool HasData => mainThrustKn > 0f || assaultBoostThrustKn > 0f ||
			quickBoostThrustKn > 0f || assaultBoostPowerDrawKw > 0f ||
			quickBoostPowerDrawKw > 0f;

		public MechBoosterPerformance Clone()
		{
			return new MechBoosterPerformance
			{
				mainThrustKn = mainThrustKn,
				assaultBoostThrustKn = assaultBoostThrustKn,
				quickBoostThrustKn = quickBoostThrustKn,
				quickBoostDurationS = quickBoostDurationS,
				quickBoostRecoveryS = quickBoostRecoveryS,
				horizontalVectorLimitDeg = horizontalVectorLimitDeg,
				verticalVectorLimitDeg = verticalVectorLimitDeg,
				assaultBoostPowerDrawKw = assaultBoostPowerDrawKw,
				quickBoostPowerDrawKw = quickBoostPowerDrawKw
			};
		}
	}

	[Serializable]
	public sealed class MechGeneratorPerformance
	{
		[Min(0f)] public float energyStorageCapacityMj;
		[Min(0f)] public float energyRechargePowerKw;
		[Min(0f)] public float energyRechargeDelayS;
		[Range(0f, 1f)] public float depletedRestartFraction = 0.2f;
		[Min(0f)] public float startupTimeS;

		public bool HasData => energyStorageCapacityMj > 0f || energyRechargePowerKw > 0f;

		public MechGeneratorPerformance Clone()
		{
			return new MechGeneratorPerformance
			{
				energyStorageCapacityMj = energyStorageCapacityMj,
				energyRechargePowerKw = energyRechargePowerKw,
				energyRechargeDelayS = energyRechargeDelayS,
				depletedRestartFraction = depletedRestartFraction,
				startupTimeS = startupTimeS
			};
		}
	}

	[Flags]
	public enum MechElectroOpticalBand
	{
		None = 0,
		Visible = 1 << 0,
		Mwir = 1 << 1,
		Lwir = 1 << 2
	}

	[Serializable]
	public sealed class MechHeadSensorPerformance
	{
		[Range(0f, 100f)] public float pointingCapability;
		[Range(0f, 100f)] public float searchCapability;
		[Range(0f, 100f)] public float trackingCapability;
		[Range(0f, 100f)] public float identificationCapability;
		[Min(1f)] public float maximumMagnification = 1f;
		[Min(0f)] public float referenceHotRangeKm;
		[Min(0f)] public float referenceColdRangeKm;
		[Min(0)] public int trackCapacity;
		[Min(0f)] public float referenceRangeErrorM;
		[Min(0f)] public float laserRangefinderRangeKm;
		[Min(0f)] public float laserRangefinderErrorM;
		public MechElectroOpticalBand supportedBands;
		public string[] allowedCharacteristicIds = Array.Empty<string>();

		public bool HasData => supportedBands != MechElectroOpticalBand.None ||
			pointingCapability > 0f || searchCapability > 0f || trackingCapability > 0f;

		public MechHeadSensorPerformance Clone()
		{
			return new MechHeadSensorPerformance
			{
				pointingCapability = pointingCapability,
				searchCapability = searchCapability,
				trackingCapability = trackingCapability,
				identificationCapability = identificationCapability,
				maximumMagnification = maximumMagnification,
				referenceHotRangeKm = referenceHotRangeKm,
				referenceColdRangeKm = referenceColdRangeKm,
				trackCapacity = trackCapacity,
				referenceRangeErrorM = referenceRangeErrorM,
				laserRangefinderRangeKm = laserRangefinderRangeKm,
				laserRangefinderErrorM = laserRangefinderErrorM,
				supportedBands = supportedBands,
				allowedCharacteristicIds = allowedCharacteristicIds == null
					? Array.Empty<string>()
					: (string[])allowedCharacteristicIds.Clone()
			};
		}
	}

	[Serializable]
	public sealed class MechAggregatedStats
	{
		[Header("Raw Totals")]
		public float totalMassKg;
		public float armorPoints;
		public float structurePoints;
		public float stability;
		public float powerOutputMw;
		public float idlePowerDrawMw;
		public float peakPowerDrawMw;
		public float heatGenerationMw;
		public float heatDissipationMw;
		public float heatCapacityMj;
		public float mainThrustKn;
		public float lateralThrustKn;
		public float reverseThrustKn;
		public float upwardThrustKn;
		public float downwardThrustKn;
		public float attitudeTorqueKnM;

		[Tooltip("可用于机体转向的 RCS 单向等效推力。多个部件的贡献会在整机层汇总。")]
		public float rcsThrustKn;
		public float supportedMassKg;
		public float radarRangeKm;
		public float radarAperture;
		public float radarRefreshHz;
		public float sensorPrecision;
		public int trackCapacity;
		public float fireControlQuality;
		public int guidanceChannels;
		public float radarCrossSection;
		public float thermalSignature;
		public float electromagneticSignature;
		public float weaponRecoilControl;
		public float hardpointCapacityKg;
		public float energyStorageCapacityMj;
		public float energyRechargePowerKw;
		public float energyRechargeDelayS;
		public float depletedEnergyRestartFraction;
		public MechRadarPerformance radarPerformance = new MechRadarPerformance();
		public MechFireControlPerformance fireControlPerformance = new MechFireControlPerformance();

		[Header("Derived")]
		public float idlePowerSurplusMw;
		public float peakPowerSurplusMw;
		public float heatSurplusMw;
		public float mainAccelerationMps2;
		public float lateralAccelerationMps2;
		public float reverseAccelerationMps2;
		public float upwardAccelerationMps2;
		public float downwardAccelerationMps2;
		public float attitudeTorquePerTon;
		public float rcsAccelerationMps2;
		public float rcsTurnRateDegreesPerSecond;
		public float supportedMassRatio;

		public void Clear()
		{
			totalMassKg = 0f;
			armorPoints = 0f;
			structurePoints = 0f;
			stability = 0f;
			powerOutputMw = 0f;
			idlePowerDrawMw = 0f;
			peakPowerDrawMw = 0f;
			heatGenerationMw = 0f;
			heatDissipationMw = 0f;
			heatCapacityMj = 0f;
			mainThrustKn = 0f;
			lateralThrustKn = 0f;
			reverseThrustKn = 0f;
			upwardThrustKn = 0f;
			downwardThrustKn = 0f;
			attitudeTorqueKnM = 0f;
			rcsThrustKn = 0f;
			supportedMassKg = 0f;
			radarRangeKm = 0f;
			radarAperture = 0f;
			radarRefreshHz = 0f;
			sensorPrecision = 0f;
			trackCapacity = 0;
			fireControlQuality = 0f;
			guidanceChannels = 0;
			radarCrossSection = 0f;
			thermalSignature = 0f;
			electromagneticSignature = 0f;
			weaponRecoilControl = 0f;
			hardpointCapacityKg = 0f;
			energyStorageCapacityMj = 0f;
			energyRechargePowerKw = 0f;
			energyRechargeDelayS = 0f;
			depletedEnergyRestartFraction = 0f;
			radarPerformance = new MechRadarPerformance();
			fireControlPerformance = new MechFireControlPerformance();
			idlePowerSurplusMw = 0f;
			peakPowerSurplusMw = 0f;
			heatSurplusMw = 0f;
			mainAccelerationMps2 = 0f;
			lateralAccelerationMps2 = 0f;
			reverseAccelerationMps2 = 0f;
			upwardAccelerationMps2 = 0f;
			downwardAccelerationMps2 = 0f;
			attitudeTorquePerTon = 0f;
			rcsAccelerationMps2 = 0f;
			rcsTurnRateDegreesPerSecond = 0f;
			supportedMassRatio = 0f;
		}

		public void Add(MechPartStats stats)
		{
			if (stats == null)
			{
				return;
			}

			totalMassKg += stats.massKg;
			armorPoints += stats.armorPoints;
			structurePoints += stats.structurePoints;
			stability += stats.stability;
			powerOutputMw += stats.powerOutputMw;
			idlePowerDrawMw += stats.idlePowerDrawMw;
			peakPowerDrawMw += stats.peakPowerDrawMw;
			heatGenerationMw += stats.heatGenerationMw;
			heatDissipationMw += stats.heatDissipationMw;
			heatCapacityMj += stats.heatCapacityMj;
			mainThrustKn += stats.mainThrustKn;
			lateralThrustKn += stats.lateralThrustKn;
			reverseThrustKn += stats.reverseThrustKn;
			upwardThrustKn += stats.upwardThrustKn;
			downwardThrustKn += stats.downwardThrustKn;
			attitudeTorqueKnM += stats.attitudeTorqueKnM;
			rcsThrustKn += stats.rcsThrustKn;
			supportedMassKg += stats.supportedMassKg;
			radarRangeKm += stats.radarRangeKm;
			radarAperture += stats.radarAperture;
			radarRefreshHz += stats.radarRefreshHz;
			sensorPrecision += stats.sensorPrecision;
			trackCapacity += stats.trackCapacity;
			fireControlQuality += stats.fireControlQuality;
			guidanceChannels += stats.guidanceChannels;
			radarCrossSection += stats.radarCrossSection;
			thermalSignature += stats.thermalSignature;
			electromagneticSignature += stats.electromagneticSignature;
			weaponRecoilControl += stats.weaponRecoilControl;
			hardpointCapacityKg += stats.hardpointCapacityKg;
		}

		public void AddArmPerformance(MechArmPerformance arms)
		{
			if (arms == null)
			{
				return;
			}

			// Mirrored lateral nozzles provide opposite directions, so one side defines
			// the maximum force in either direction. RCS reports total installed thrust.
			lateralThrustKn += arms.LateralThrustContributionKn;
			rcsThrustKn += arms.RcsThrustContributionKn;
		}

		public void AddCorePerformance(MechCorePerformance core)
		{
			if (core == null)
			{
				return;
			}

			reverseThrustKn += core.ReverseThrustContributionKn;
			downwardThrustKn += core.DownwardThrustContributionKn;
		}

		public void AddLegPerformance(MechLegPerformance legs)
		{
			if (legs == null)
			{
				return;
			}

			mainThrustKn += legs.MainThrustContributionKn;
			lateralThrustKn += legs.LateralThrustContributionKn;
			reverseThrustKn += legs.ReverseThrustContributionKn;
			upwardThrustKn += legs.UpwardThrustContributionKn;
			rcsThrustKn += legs.RcsThrustContributionKn;
			supportedMassKg += legs.maximumSupportedMassKg;
		}

		public void AddBoosterPerformance(MechBoosterPerformance booster)
		{
			if (booster == null)
			{
				return;
			}

			mainThrustKn += booster.mainThrustKn;
		}

		public void AddGeneratorPerformance(MechGeneratorPerformance generator)
		{
			if (generator == null)
			{
				return;
			}

			energyStorageCapacityMj += generator.energyStorageCapacityMj;
			energyRechargePowerKw += generator.energyRechargePowerKw;
			energyRechargeDelayS = Mathf.Max(energyRechargeDelayS, generator.energyRechargeDelayS);
			depletedEnergyRestartFraction = Mathf.Max(
				depletedEnergyRestartFraction,
				generator.depletedRestartFraction);
		}

		public void AddRadarPerformance(MechRadarPerformance radar)
		{
			if (radar == null || !radar.HasData)
			{
				return;
			}
			if (radarPerformance == null)
			{
				radarPerformance = new MechRadarPerformance();
			}
			radarPerformance.CopyFrom(radar);
		}

		public void AddFireControlPerformance(MechFireControlPerformance fireControl)
		{
			if (fireControl == null || !fireControl.HasData)
			{
				return;
			}
			if (fireControlPerformance == null)
			{
				fireControlPerformance = new MechFireControlPerformance();
			}
			fireControlPerformance.CopyFrom(fireControl);
		}

		public void RecalculateDerived()
		{
			idlePowerSurplusMw = powerOutputMw - idlePowerDrawMw;
			peakPowerSurplusMw = powerOutputMw - peakPowerDrawMw;
			heatSurplusMw = heatDissipationMw - heatGenerationMw;
			if (totalMassKg > 0f)
			{
				mainAccelerationMps2 = mainThrustKn * 1000f / totalMassKg;
				lateralAccelerationMps2 = lateralThrustKn * 1000f / totalMassKg;
				reverseAccelerationMps2 = reverseThrustKn * 1000f / totalMassKg;
				upwardAccelerationMps2 = upwardThrustKn * 1000f / totalMassKg;
				downwardAccelerationMps2 = downwardThrustKn * 1000f / totalMassKg;
				attitudeTorquePerTon = attitudeTorqueKnM / Mathf.Max(0.001f, totalMassKg / 1000f);
				rcsAccelerationMps2 = rcsThrustKn * 1000f / totalMassKg;
				// Calibrated so the 900 kN baseline RCS package preserves the existing
				// roughly 180 deg/s turn rate on the current 56.4 t test loadout.
				rcsTurnRateDegreesPerSecond = rcsAccelerationMps2 * 11.28f;
				supportedMassRatio = supportedMassKg > 0f ? totalMassKg / supportedMassKg : 0f;
			}
			else
			{
				mainAccelerationMps2 = 0f;
				lateralAccelerationMps2 = 0f;
				reverseAccelerationMps2 = 0f;
				upwardAccelerationMps2 = 0f;
				downwardAccelerationMps2 = 0f;
				attitudeTorquePerTon = 0f;
				rcsAccelerationMps2 = 0f;
				rcsTurnRateDegreesPerSecond = 0f;
				supportedMassRatio = 0f;
			}
		}
	}
}
