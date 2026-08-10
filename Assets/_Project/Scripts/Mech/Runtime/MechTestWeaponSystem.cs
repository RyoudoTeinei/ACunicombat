using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace ACunicombat.Mechs
{
	[DefaultExecutionOrder(200)]
	[DisallowMultipleComponent]
	public sealed class MechTestWeaponSystem : MonoBehaviour
	{
		private sealed class HandWeaponState
		{
			public string PartId;

			public MechWeaponRole Role;

			public MechWeaponPerformance Performance;

			public float WeaponMassKg;

			public int RoundsRemaining;

			public int MagazinesRemaining;

			public bool Reloading;

			public float ReloadCompleteTime = float.NegativeInfinity;

			public float NextFireTime;

			public float RecoilDispersionMilliradians;

			public float LastDispersionUpdateTime;

			public float LastShotTime = float.NegativeInfinity;

			public int BurstShotCount;

			public bool HasTrackedAimDirection;

			public Vector3 TrackedAimDirection;

			public float AimAngularSpeedDegreesPerSecond;

			public float LastAimUpdateTime;
		}

		private readonly struct AutomaticCannonProfile
		{
			public AutomaticCannonProfile(
				float directHitDamage,
				float muzzleVelocityMetersPerSecond,
				float projectileLifetimeSeconds,
				float roundsPerMinute,
				float baseDispersionMilliradians,
				float maximumDispersionMilliradians,
				int lowRecoilShotCount,
				float lowRecoilPerShotMilliradians,
				float sustainedRecoilPerShotMilliradians,
				float dispersionRecoveryMilliradiansPerSecond,
				float dispersionRecoveryDelaySeconds,
				float burstResetSeconds,
				int magazineCapacity,
				int magazineCount,
				float reloadSeconds,
				bool hasTargetProximityFuse = false,
				float proximityFuseRadiusMeters = 0f,
				float warheadLethalRadiusMeters = 0f)
			{
				DirectHitDamage = directHitDamage;
				MuzzleVelocityMetersPerSecond = muzzleVelocityMetersPerSecond;
				ProjectileLifetimeSeconds = projectileLifetimeSeconds;
				RoundsPerMinute = roundsPerMinute;
				BaseDispersionMilliradians = baseDispersionMilliradians;
				MaximumDispersionMilliradians = maximumDispersionMilliradians;
				LowRecoilShotCount = lowRecoilShotCount;
				LowRecoilPerShotMilliradians = lowRecoilPerShotMilliradians;
				SustainedRecoilPerShotMilliradians = sustainedRecoilPerShotMilliradians;
				DispersionRecoveryMilliradiansPerSecond = dispersionRecoveryMilliradiansPerSecond;
				DispersionRecoveryDelaySeconds = dispersionRecoveryDelaySeconds;
				BurstResetSeconds = burstResetSeconds;
				MagazineCapacity = magazineCapacity;
				MagazineCount = magazineCount;
				ReloadSeconds = reloadSeconds;
				HasTargetProximityFuse = hasTargetProximityFuse;
				ProximityFuseRadiusMeters = proximityFuseRadiusMeters;
				WarheadLethalRadiusMeters = warheadLethalRadiusMeters;
			}

			public float DirectHitDamage { get; }
			public float MuzzleVelocityMetersPerSecond { get; }
			public float ProjectileLifetimeSeconds { get; }
			public float RoundsPerMinute { get; }
			public float BaseDispersionMilliradians { get; }
			public float MaximumDispersionMilliradians { get; }
			public int LowRecoilShotCount { get; }
			public float LowRecoilPerShotMilliradians { get; }
			public float SustainedRecoilPerShotMilliradians { get; }
			public float DispersionRecoveryMilliradiansPerSecond { get; }
			public float DispersionRecoveryDelaySeconds { get; }
			public float BurstResetSeconds { get; }
			public int MagazineCapacity { get; }
			public int MagazineCount { get; }
			public float ReloadSeconds { get; }
			public bool HasTargetProximityFuse { get; }
			public float ProximityFuseRadiusMeters { get; }
			public float WarheadLethalRadiusMeters { get; }
		}

		private sealed class ShaoWeiWeaponState
		{
			public MechWeaponPerformance Performance;

			public int RoundsRemaining;

			public int MagazinesRemaining;

			public float HeatMj;

			public bool Overheated;

			public bool Reloading;

			public float ReloadCompleteTime = float.NegativeInfinity;

			public float NextFireTime;

			public float SpinReadyTime;

			public bool Spinning;

			public bool HasSlewedDirection;

			public Vector3 SlewedDirection;

			public bool HasFilteredAimDirection;

			public Vector3 FilteredAimDirection;

			public float TraverseSpeedDegreesPerSecond;

			public bool HasAimPoint;

			public Vector3 AimPoint;

			public Transform AimPivot;

			public MechEquipmentAnimationPlayer AnimationPlayer;

			public uint DispersionRandomState;
		}

		public enum PointDefenseWithholdReason : byte
		{
			None,
			TrackTooCoarse,
			RangeTooLong
		}

		private const int CurrentAmmunitionConfigurationVersion = 2;

		private const string LeftMissileVisualResourcePath = "Projectiles/r27_white";

		private const string RightMissileVisualResourcePath = "Projectiles/r77_gridfin";

		private const string LeftMissileVisualEditorAssetPath = "Assets/_Project/LocalGameAssets/Resources/Projectiles/r27_white.glb";

		private const string RightMissileVisualEditorAssetPath = "Assets/_Project/LocalGameAssets/Resources/Projectiles/r77_gridfin.glb";

		private const float LeftMissileVisualLengthMeters = 4f;

		private const float RightMissileVisualLengthMeters = 3.6f;

		private const float CannonMuzzleVelocityMetersPerSecond = 450f;

		private const float CannonProjectileLifetimeSeconds = 10f;

		private const string ShaoWeiSpinUpActionId = "a000_800200";

		private const string ShaoWeiSustainedFireActionId = "a000_800201";

		private const string ShaoWeiSpinDownActionId = "a000_800202";

		private const string ShaoWeiAimPivotSuffix = "LookAt_01";

		private const float MuzzleFlashTimeScale = 0.4f;

		private const float MuzzleFlashMaximumVisibleSeconds = 0.18f;

		private const float CannonMuzzleFlashVisibleSeconds = 0.34f;

		[SerializeField]
		private MechPlayerInputAdapter input;

		[SerializeField]
		private MechRuntimeState runtimeState;

		[SerializeField]
		private MechPlayerRig playerRig;

		private MechAssembler assembler;

		[SerializeField]
		private MechFlightMotionController flightMotion;

		[SerializeField]
		private Camera aimingCamera;

		private MechThirdPersonCamera aimController;

		[SerializeField]
		private CombatTestTarget target;

		[SerializeField]
		private MechRadarController radarController;

		[SerializeField]
		private MechEffectPrefabLibrary nativeEffectLibrary;

		[Header("Aiming")]
		[SerializeField]
		private Vector2 aimViewportPoint = new Vector2(0.5f, 0.56f);

		[SerializeField]
		private float gunAutoAimDegrees = 4f;

		[SerializeField]
		private float missileLockDegrees = 24f;

		[Header("Hand weapon ammunition")]
		[SerializeField]
		[Min(1f)]
		private int cannonMagazineCapacity = 1;

		[SerializeField]
		[Min(1f)]
		private int cannonMagazineCount = 32;

		[Header("Missile launcher ammunition")]
		[SerializeField]
		[Min(1f)]
		private int leftMissileMagazineCapacity = 16;

		[SerializeField]
		[Min(1f)]
		private int leftMissileMagazineCount = 2;

		[SerializeField]
		[Min(1f)]
		private int rightMissileMagazineCapacity = 4;

		[SerializeField]
		[Min(1f)]
		private int rightMissileMagazineCount = 6;

		[SerializeField]
		[HideInInspector]
		private int ammunitionConfigurationVersion;

		[Header("30 mm nominal effective range")]
		[SerializeField]
		[Min(0.1f)]
		private float rifleEffectiveRangeTargetRadiusMeters = 2.5f;

		[SerializeField]
		[Range(0.01f, 0.99f)]
		private float rifleEffectiveRangeHitProbability = 0.8f;

		[SerializeField]
		[Range(1, 12)]
		private int rifleDisplayedRangeNominalBurstRounds = 4;

		[Header("Point defense")]
		[SerializeField]
		[Range(1f, 20f)]
		private float pointDefenseLockDegrees = 6f;

		[SerializeField]
		[Min(100f)]
		private float pointDefenseMaximumRangeMeters = 6000f;

		[SerializeField]
		[Min(0.1f)]
		private float pointDefenseMaximumLeadSeconds = 6f;

		[SerializeField]
		[Min(0f)]
		private float pointDefenseLockHoldSeconds = 0.25f;

		[SerializeField]
		[Min(0f)]
		private float pointDefenseRoundLethalRadiusMeters = 3.5f;

		[SerializeField]
		[Range(0f, 0.5f)]
		private float pointDefenseMinimumHitProbability = 0.02f;

		[Header("DF-GA-09 SHAO-WEI point defense")]
		[SerializeField]
		private bool enableShaoWeiPointDefense = true;

		[SerializeField]
		private bool handPointDefensePriorityEnabled = true;

		[SerializeField]
		[Min(1f)]
		private int shaoWeiMagazineCapacity = 200;

		[SerializeField]
		[Min(1f)]
		private int shaoWeiMagazineCount = 4;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiHeatCapacityMj = 6f;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiResumeThresholdMj = 2f;

		[Header("Interceptor missile")]
		[SerializeField]
		[Min(1f)]
		private float interceptorProtectedRadiusMeters = 180f;

		[SerializeField]
		[Min(0.1f)]
		private float interceptorPredictionHorizonSeconds = 60f;

		[SerializeField]
		[Min(0f)]
		private float interceptorLockHoldSeconds = 0.5f;

		[SerializeField]
		[Min(0f)]
		private float interceptorResponseReserveSeconds = 0.2f;

		[Header("Interceptor cold launch")]
		[SerializeField]
		[Min(0f)]
		private float interceptorSafeSeparationSeconds = 0.25f;

		[SerializeField]
		[Min(0f)]
		private float interceptorSafeSeparationMeters = 8f;

		[SerializeField]
		[Range(0.5f, 45f)]
		private float interceptorIgnitionAlignmentDegrees = 8f;

		[SerializeField]
		[Min(0.05f)]
		private float interceptorAlignmentTimeoutSeconds = 0.6f;

		[Header("Air-to-air missile")]
		[SerializeField]
		private MissileFlightProfile airToAirMissileProfile = MissileFlightProfile.AirToAirDefault;

		[Header("Interceptor missile flight")]
		[SerializeField]
		private MissileFlightProfile interceptorMissileProfile = MissileFlightProfile.InterceptorDefault;

		[Header("Missile launcher")]
		[SerializeField]
		[Min(0f)]
		private float missileLaunchClearanceMeters = 0.8f;

		private readonly Dictionary<MechPartSlot, Transform[]> muzzlesBySlot = new Dictionary<MechPartSlot, Transform[]>();

		private readonly Dictionary<string, MechPartDefinition> weaponDefinitionCache = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);

		private MechPartCatalog weaponCatalog;

		private int leftHandMuzzleIndex;

		private int rightHandMuzzleIndex;

		private int leftBackMuzzleIndex;

		private int rightBackMuzzleIndex;

		private readonly HandWeaponState leftHandWeaponState = new HandWeaponState();

		private readonly HandWeaponState rightHandWeaponState = new HandWeaponState();

		private int leftMissileRoundsRemaining;

		private int leftMissileMagazinesRemaining;

		private int rightMissileRoundsRemaining;

		private int rightMissileMagazinesRemaining;

		private string leftShoulderPartId;

		private string rightShoulderPartId;

		private readonly ShaoWeiWeaponState leftShaoWeiState = new ShaoWeiWeaponState();

		private readonly ShaoWeiWeaponState rightShaoWeiState = new ShaoWeiWeaponState();

		private bool ammunitionInitialized;

		private uint rifleDispersionRandomState;


		private const float RifleProjectileCollisionRadiusMeters = 0.08f;

		private const float MissileHitboxHalfWidthMeters = 0.25f;

		private const float PointDefenseVelocitySmoothingSeconds = 0.5f;

		private MissileTrackSnapshot pointDefenseTarget;

		private float pointDefenseHitProbability;

		private PointDefenseWithholdReason pointDefenseWithholdReason;

		private float pointDefenseLockUntil;

		private MissileTrackSnapshot shaoWeiPointDefenseTarget;

		private float shaoWeiPointDefenseHitProbability;

		private PointDefenseWithholdReason shaoWeiPointDefenseWithholdReason;

		private float shaoWeiPointDefenseLockUntil;

		private MissileTrackSnapshot interceptorTarget;

		private InterceptorEngagementSolution interceptorSolution;

		private float interceptorLockUntil;

		private MechMissileLaunchAudio missileLaunchAudio;

		private MechHandWeaponAudio handWeaponAudio;

		private MechPointDefenseAudio pointDefenseAudio;

		private CombatTestTarget ownCombatTarget;

		private readonly MissileTrackSnapshot[] interceptorTrackScratch = new MissileTrackSnapshot[32];

		private static Material rifleMaterial;

		private static Material cannonMaterial;

		private static Material missileMaterial;

		private static uint nextRifleDispersionSeed = 2654435769u;

		private static GameObject leftMissileVisualPrefab;

		private static GameObject rightMissileVisualPrefab;

		private static readonly HashSet<string> missileVisualLoadFailures = new HashSet<string>();

		private static readonly HashSet<string> missileVisualLoadSuccesses = new HashSet<string>();

		private static readonly HashSet<string> shaoWeiMuzzleWarnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static readonly Dictionary<Material, Material> missileUnlitMaterials = new Dictionary<Material, Material>();

		public MechRadarController RadarController => radarController;

		public TrackId PointDefenseTrackId => pointDefenseTarget.TrackId;

		public float PointDefenseHitProbability => pointDefenseHitProbability;

		public PointDefenseWithholdReason PointDefenseWithhold => pointDefenseWithholdReason;

		public bool HasPointDefenseTrack => pointDefenseTarget.TrackId.IsValid;

		public TrackId ShaoWeiPointDefenseTrackId => shaoWeiPointDefenseTarget.TrackId;

		public float ShaoWeiPointDefenseHitProbability => shaoWeiPointDefenseHitProbability;

		public PointDefenseWithholdReason ShaoWeiPointDefenseWithhold => shaoWeiPointDefenseWithholdReason;

		public bool HasShaoWeiPointDefenseTrack => shaoWeiPointDefenseTarget.TrackId.IsValid;

		public TrackId InterceptorTrackId => interceptorTarget.TrackId;

		public bool HasInterceptorLaunchSolution
		{
			get
			{
				if (interceptorTarget.TrackId.IsValid)
				{
					return interceptorSolution.Reachable;
				}
				return false;
			}
		}

		public int CannonRoundsRemaining => FindHandState(MechWeaponRole.HandCannon)?.RoundsRemaining ?? 0;

		public int CannonMagazineCapacity => GetHandMagazineCapacity(FindHandState(MechWeaponRole.HandCannon));

		public int CannonMagazinesRemaining => FindHandState(MechWeaponRole.HandCannon)?.MagazinesRemaining ?? 0;

		public int RifleRoundsRemaining => FindHandState(MechWeaponRole.AutomaticCannon)?.RoundsRemaining ?? 0;

		public int RifleMagazineCapacity
		{
			get
			{
				HandWeaponState state = FindHandState(MechWeaponRole.AutomaticCannon);
				return state != null ? GetHandMagazineCapacity(state) : 0;
			}
		}

		public int RifleMagazinesRemaining => FindHandState(MechWeaponRole.AutomaticCannon)?.MagazinesRemaining ?? 0;

		public int LeftMissileRoundsRemaining => leftMissileRoundsRemaining;

		public int LeftMissileMagazineCapacity => GetShoulderMagazineCapacity(MechPartSlot.LeftBackWeapon);

		public int LeftMissileMagazinesRemaining => leftMissileMagazinesRemaining;

		public int RightMissileRoundsRemaining => rightMissileRoundsRemaining;

		public int RightMissileMagazineCapacity => GetShoulderMagazineCapacity(MechPartSlot.RightBackWeapon);

		public int RightMissileMagazinesRemaining => rightMissileMagazinesRemaining;

		public bool LeftShaoWeiInstalled => IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon);

		public bool RightShaoWeiInstalled => IsShaoWeiEquipped(MechPartSlot.RightBackWeapon);

		public bool ShaoWeiPointDefenseEnabled => enableShaoWeiPointDefense;

		public bool HandPointDefensePriorityEnabled =>
			handPointDefensePriorityEnabled;

		public void SetHandPointDefensePriorityEnabled(bool enabled)
		{
			handPointDefensePriorityEnabled = enabled;
			if (enabled)
			{
				return;
			}

			pointDefenseTarget = default;
			pointDefenseHitProbability = 0f;
			pointDefenseWithholdReason = PointDefenseWithholdReason.None;
			radarController?.CuePointDefenseTrack(default);
		}

		public int ShaoWeiMagazineCapacity => Mathf.Max(
			leftShaoWeiState.Performance?.magazineCapacity ?? 0,
			rightShaoWeiState.Performance?.magazineCapacity ?? 0);

		public int LeftShaoWeiRoundsRemaining => leftShaoWeiState.RoundsRemaining;

		public int RightShaoWeiRoundsRemaining => rightShaoWeiState.RoundsRemaining;

		public int LeftShaoWeiMagazinesRemaining => leftShaoWeiState.MagazinesRemaining;

		public int RightShaoWeiMagazinesRemaining => rightShaoWeiState.MagazinesRemaining;

		public bool LeftShaoWeiUnavailable
		{
			get
			{
				if (!leftShaoWeiState.Overheated)
				{
					return leftShaoWeiState.Reloading;
				}
				return true;
			}
		}

		public bool RightShaoWeiUnavailable
		{
			get
			{
				if (!rightShaoWeiState.Overheated)
				{
					return rightShaoWeiState.Reloading;
				}
				return true;
			}
		}

		public float LeftShaoWeiRecoveryRemainingSeconds => GetShaoWeiRecoveryRemainingSeconds(leftShaoWeiState);

		public float RightShaoWeiRecoveryRemainingSeconds => GetShaoWeiRecoveryRemainingSeconds(rightShaoWeiState);

		public float ShaoWeiRecoveryDurationSeconds => Mathf.Max(
			GetShaoWeiRecoveryDurationSeconds(leftShaoWeiState),
			GetShaoWeiRecoveryDurationSeconds(rightShaoWeiState));

		public bool RifleReloading => IsReloading(FindHandState(MechWeaponRole.AutomaticCannon));

		public bool CannonReloading => IsReloading(FindHandState(MechWeaponRole.HandCannon));

		public float RifleReloadRemainingSeconds
		{
			get
			{
				if (!RifleReloading)
				{
					return 0f;
				}
				return GetReloadRemainingSeconds(FindHandState(MechWeaponRole.AutomaticCannon));
			}
		}

		public float CannonReloadRemainingSeconds
		{
			get
			{
				if (!CannonReloading)
				{
					return 0f;
				}
				return GetReloadRemainingSeconds(FindHandState(MechWeaponRole.HandCannon));
			}
		}

		public float RifleReloadDurationSeconds
		{
			get
			{
				HandWeaponState state = FindHandState(MechWeaponRole.AutomaticCannon);
				return state != null ? GetHandReloadDuration(state) : 0f;
			}
		}

		public float CannonReloadDurationSeconds
		{
			get
			{
				HandWeaponState state = FindHandState(MechWeaponRole.HandCannon);
				return state != null ? GetHandReloadDuration(state) : 0f;
			}
		}

		private bool HasLeftMissileAmmunition
		{
			get
			{
				if (leftMissileRoundsRemaining > 0)
				{
					return leftMissileMagazinesRemaining > 0;
				}
				return false;
			}
		}

		private bool HasRightMissileAmmunition
		{
			get
			{
				if (rightMissileRoundsRemaining > 0)
				{
					return rightMissileMagazinesRemaining > 0;
				}
				return false;
			}
		}

		public bool TryGetWeaponPerformance(string partId, out MechWeaponPerformanceSnapshot performance)
		{
			performance = default(MechWeaponPerformanceSnapshot);
			return TryResolvePartDefinition(partId, out MechPartDefinition part) &&
				TryGetWeaponPerformance(part, out performance);
		}

		public bool TryGetWeaponPerformance(MechPartDefinition part, out MechWeaponPerformanceSnapshot performance)
		{
			performance = default(MechWeaponPerformanceSnapshot);
			MechWeaponPerformance profile = part != null ? part.WeaponPerformance : null;
			if (profile == null || !profile.IsConfigured)
			{
				return false;
			}

			MissileFlightProfile missile = profile.IsGuidedMissile
				? profile.missileFlightProfile.Sanitized()
				: default(MissileFlightProfile);
			float maximumRange = profile.maximumRangeMeters > 0f
				? profile.maximumRangeMeters
				: profile.muzzleVelocityMetersPerSecond * profile.projectileLifetimeSeconds;
			float effectiveRange = profile.effectiveRangeMeters;
			if (profile.calculateEffectiveRange)
			{
				effectiveRange = CalculateDisplayedRifleEffectiveRangeMeters(
					maximumRange,
					profile.muzzleVelocityMetersPerSecond,
					profile.baseDispersionMilliradians,
					profile.maximumDispersionMilliradians);
			}

			float fuzeRadius = profile.role == MechWeaponRole.PointDefenseCannon
				? profile.pointDefenseRoundLethalRadiusMeters
				: profile.proximityFuseRadiusMeters;
			float fuzeArmSeconds = profile.role == MechWeaponRole.PointDefenseCannon
				? profile.pointDefenseRoundArmSeconds
				: profile.proximityFuseArmSeconds;
			float fuzeSafeDistance = profile.role == MechWeaponRole.PointDefenseCannon
				? profile.pointDefenseRoundSafeDistanceMeters
				: profile.proximityFuseSafeDistanceMeters;
			performance = new MechWeaponPerformanceSnapshot
			{
				WeaponType = profile.weaponType,
				IsAutomatic = profile.IsAutomatic,
				IsGuidedMissile = profile.IsGuidedMissile,
				IsPointDefense = profile.role == MechWeaponRole.PointDefenseCannon,
				DirectHitDamage = profile.directHitDamage,
				HasEffectiveRange = effectiveRange > 0f,
				EffectiveRangeMeters = effectiveRange,
				MaximumRangeMeters = maximumRange,
				MuzzleVelocityMetersPerSecond = profile.IsGuidedMissile ? missile.EjectionSpeedMps : profile.muzzleVelocityMetersPerSecond,
				ProjectileLifetimeSeconds = profile.IsGuidedMissile ? missile.MaximumFlightTimeSeconds : profile.projectileLifetimeSeconds,
				HasFireRate = profile.roundsPerMinute > 0f,
				FireRateRoundsPerMinute = profile.roundsPerMinute,
				HasAccuracy = !profile.IsGuidedMissile,
				BaseDispersionMilliradians = profile.baseDispersionMilliradians,
				HasSustainedAccuracy = profile.IsAutomatic,
				SustainedDispersionMilliradians = profile.maximumDispersionMilliradians,
				HasRecoilImpulse = profile.recoilImpulseNewtonSeconds > 0f,
				RecoilImpulseNewtonSeconds = profile.recoilImpulseNewtonSeconds,
				MagazineCapacity = profile.magazineCapacity,
				MagazineCount = profile.magazineCount,
				ReloadTimeSeconds = profile.reloadSeconds,
				HasProximityFuse = profile.targetProximityFuse || profile.IsGuidedMissile || fuzeRadius > 0f,
				ProximityFuseMode = profile.proximityFuseMode,
				ProximityFuseTriggerRadiusMeters = profile.IsGuidedMissile ? missile.ProximityFuseRadiusMeters : fuzeRadius,
				WarheadDamageRadiusMeters = profile.IsGuidedMissile ? missile.WarheadLethalRadiusMeters : profile.warheadLethalRadiusMeters,
				ProximityFuseArmSeconds = profile.IsGuidedMissile ? missile.ProximityFuseArmSeconds : fuzeArmSeconds,
				ProximityFuseSafeDistanceMeters = profile.IsGuidedMissile ? missile.ProximityFuseSafeDistanceMeters : fuzeSafeDistance,
				SeekerRangeMeters = profile.IsGuidedMissile ? missile.SeekerRangeMeters : 0f,
				MaximumAngularRateDegreesPerSecond = profile.IsGuidedMissile ? missile.MaximumAngularRateDegreesPerSecond : 0f,
				PointDefenseFieldOfFireDegrees = profile.maximumOffBoresightDegrees * 2f,
				PointDefenseTraverseRateDegreesPerSecond = profile.traverseRateDegreesPerSecond,
				PointDefenseMaximumRangeMeters = maximumRange,
				PointDefenseMaximumLeadSeconds = profile.maximumLeadSeconds,
				PointDefenseLockHoldSeconds = profile.trackHoldSeconds,
				PointDefenseMinimumHitProbability = profile.minimumHitProbability,
				PointDefenseMaximumThreatTimeSeconds = profile.maximumThreatTimeSeconds,
				RequiresWeaponsQualityTrack = profile.requiresWeaponsQualityTrack,
				IncomingTargetsOnly = profile.incomingTargetsOnly,
				UsesHeat = profile.heatCapacityMj > 0f,
				HeatPerShotMj = profile.heatPerShotMj,
				HeatCapacityMj = profile.heatCapacityMj,
				CoolingMw = profile.coolingMw,
				OverheatThresholdMj = profile.heatCapacityMj,
				ResumeThresholdMj = profile.resumeThresholdMj,
				SpinUpSeconds = profile.spinUpSeconds
			};
			return true;
		}

		private bool TryResolvePartDefinition(string partId, out MechPartDefinition part)
		{
			part = null;
			if (string.IsNullOrWhiteSpace(partId))
			{
				return false;
			}
			if (weaponDefinitionCache.TryGetValue(partId, out part))
			{
				return part != null;
			}
			foreach (MechPartSlot slot in new[]
			{
				MechPartSlot.LeftHandWeapon,
				MechPartSlot.RightHandWeapon,
				MechPartSlot.LeftBackWeapon,
				MechPartSlot.RightBackWeapon
			})
			{
				MechPartDefinition equipped = GetEquippedPartDefinition(slot);
				if (equipped != null && string.Equals(equipped.PartId, partId, StringComparison.OrdinalIgnoreCase))
				{
					part = equipped;
					weaponDefinitionCache[partId] = part;
					return true;
				}
			}
			part = Resources.Load<MechPartDefinition>("AC6Parts/" + partId);
			if (part == null)
			{
				weaponCatalog ??= Resources.Load<MechPartCatalog>("AC6FullPartCatalog");
				part = weaponCatalog?.Find(partId)?.LoadPart();
			}
			weaponDefinitionCache[partId] = part;
			return part != null;
		}

		private bool IsShaoWeiEquipped(MechPartSlot slot)
		{
			return GetEquippedWeaponRole(slot) == MechWeaponRole.PointDefenseCannon;
		}

		public MechWeaponRole GetEquippedWeaponRole(MechPartSlot slot)
		{
			return MechWeaponRoleResolver.Resolve(GetEquippedPartDefinition(slot));
		}

		public bool TryGetWeaponSlot(MechWeaponRole role, out MechPartSlot slot)
		{
			MechPartSlot[] candidates = role == MechWeaponRole.HandCannon || role == MechWeaponRole.AutomaticCannon
				? new[] { MechPartSlot.LeftHandWeapon, MechPartSlot.RightHandWeapon }
				: new[] { MechPartSlot.LeftBackWeapon, MechPartSlot.RightBackWeapon };
			for (int i = 0; i < candidates.Length; i++)
			{
				if (GetEquippedWeaponRole(candidates[i]) == role)
				{
					slot = candidates[i];
					return true;
				}
			}
			slot = MechPartSlot.None;
			return false;
		}

		private static AutomaticCannonProfile ResolveAutomaticCannonProfile(MechWeaponPerformance performance)
		{
			performance ??= new MechWeaponPerformance();
			return new AutomaticCannonProfile(
				directHitDamage: performance.directHitDamage,
				muzzleVelocityMetersPerSecond: performance.muzzleVelocityMetersPerSecond,
				projectileLifetimeSeconds: performance.projectileLifetimeSeconds,
				roundsPerMinute: performance.roundsPerMinute,
				baseDispersionMilliradians: performance.baseDispersionMilliradians,
				maximumDispersionMilliradians: performance.maximumDispersionMilliradians,
				lowRecoilShotCount: performance.lowRecoilShotCount,
				lowRecoilPerShotMilliradians: performance.lowRecoilPerShotMilliradians,
				sustainedRecoilPerShotMilliradians: performance.sustainedRecoilPerShotMilliradians,
				dispersionRecoveryMilliradiansPerSecond: performance.dispersionRecoveryMilliradiansPerSecond,
				dispersionRecoveryDelaySeconds: performance.dispersionRecoveryDelaySeconds,
				burstResetSeconds: performance.burstResetSeconds,
				magazineCapacity: performance.magazineCapacity,
				magazineCount: performance.magazineCount,
				reloadSeconds: performance.reloadSeconds,
				hasTargetProximityFuse: performance.targetProximityFuse,
				proximityFuseRadiusMeters: performance.proximityFuseRadiusMeters,
				warheadLethalRadiusMeters: performance.warheadLethalRadiusMeters);
		}

		// Equipment-menu estimator only. This does not feed aiming, fire-control,
		// projectile flight, damage, or proximity-fuze behavior.
		private float CalculateDisplayedRifleEffectiveRangeMeters(
			float maximumRangeMeters,
			float muzzleVelocityMetersPerSecond,
			float baseDispersionMilliradians,
			float sustainedDispersionMilliradians)
		{
			var fireControlProfile = MechFireControlPerformanceModel.FromStats(
				runtimeState != null ? runtimeState.AggregatedStats : null);
			float fireControlErrorMilliradians = fireControlProfile.NominalAngularErrorMilliradians;
			int nominalBurstRounds = Mathf.Max(1, rifleDisplayedRangeNominalBurstRounds);
			float weaponDispersionVariance =
				(baseDispersionMilliradians * baseDispersionMilliradians
					+ (nominalBurstRounds - 1f)
					* sustainedDispersionMilliradians
					* sustainedDispersionMilliradians)
				/ nominalBurstRounds;
			float combinedAngularSigmaRadians = Mathf.Sqrt(
				weaponDispersionVariance
					+ fireControlErrorMilliradians * fireControlErrorMilliradians) * 0.001f;

			// The nominal menu estimate lets muzzle velocity reduce time-of-flight
			// lead error. It is deliberately independent of the live track solution.
			float lateralVelocityUncertainty =
				fireControlProfile.NominalLateralVelocityUncertaintyMetersPerSecond;
			float leadSigmaRadians = lateralVelocityUncertainty
				/ Mathf.Max(1f, muzzleVelocityMetersPerSecond);
			float combinedSigmaRadians = Mathf.Sqrt(
				combinedAngularSigmaRadians * combinedAngularSigmaRadians
					+ leadSigmaRadians * leadSigmaRadians);
			float probability = Mathf.Clamp(rifleEffectiveRangeHitProbability, 0.01f, 0.99f);
			float probabilitySigma = Mathf.Sqrt(-2f * Mathf.Log(1f - probability));
			float effectiveRangeMeters = Mathf.Max(0.1f, rifleEffectiveRangeTargetRadiusMeters)
				/ Mathf.Max(0.000001f, combinedSigmaRadians * probabilitySigma);
			return Mathf.Min(Mathf.Max(0f, maximumRangeMeters), effectiveRangeMeters);
		}

		private MechPartDefinition GetEquippedPartDefinition(MechPartSlot slot)
		{
			if (assembler == null)
			{
				assembler = GetComponent<MechAssembler>();
			}
			if (assembler != null && assembler.Loadout != null && assembler.Loadout.TryGetPart(slot, out var loadoutPart) && loadoutPart != null)
			{
				return loadoutPart;
			}
			if (runtimeState == null)
			{
				runtimeState = GetComponent<MechRuntimeState>();
			}
			if (runtimeState != null)
			{
				IReadOnlyList<MechAssemblyRecord> parts = runtimeState.AssembledParts;
				for (int i = 0; i < parts.Count; i++)
				{
					MechAssemblyRecord record = parts[i];
					if (record != null && record.slot == slot && record.part != null)
					{
						return record.part;
					}
				}
			}
			return null;
		}

		private string GetEquippedPartId(MechPartSlot slot)
		{
			return GetEquippedPartDefinition(slot)?.PartId ?? string.Empty;
		}

		private MechWeaponPerformance GetEquippedWeaponPerformance(MechPartSlot slot)
		{
			MechWeaponPerformance performance = GetEquippedPartDefinition(slot)?.WeaponPerformance;
			return performance != null && performance.IsConfigured ? performance : null;
		}

		private MissileFlightProfile GetEquippedMissileProfile(MechPartSlot slot, MissileFlightProfile fallback)
		{
			MechWeaponPerformance performance = GetEquippedWeaponPerformance(slot);
			return performance != null && performance.IsGuidedMissile && performance.missileFlightProfile.IsConfigured
				? performance.missileFlightProfile.Sanitized()
				: fallback.Sanitized();
		}

		private MissileFlightProfile GetInstalledMissileProfile(MechWeaponRole role, MissileFlightProfile fallback)
		{
			return TryGetWeaponSlot(role, out MechPartSlot slot)
				? GetEquippedMissileProfile(slot, fallback)
				: fallback.Sanitized();
		}

		public void Configure(MechPlayerInputAdapter newInput, MechRuntimeState newRuntimeState, Camera newAimingCamera, CombatTestTarget newTarget, MechEffectPrefabLibrary newNativeEffectLibrary = null)
		{
			input = newInput;
			runtimeState = newRuntimeState;
			playerRig = GetComponent<MechPlayerRig>();
			ownCombatTarget = GetComponent<CombatTestTarget>();
			assembler = GetComponent<MechAssembler>();
			flightMotion = GetComponent<MechFlightMotionController>();
			aimingCamera = newAimingCamera;
			aimController = ((aimingCamera != null) ? aimingCamera.GetComponent<MechThirdPersonCamera>() : null);
			target = newTarget;
			nativeEffectLibrary = newNativeEffectLibrary;
			radarController = GetComponent<MechRadarController>();
			if (radarController == null)
			{
				radarController = base.gameObject.AddComponent<MechRadarController>();
			}
			radarController.Configure(aimingCamera, target);
			ResolveMuzzles();
		}

		public void ConfigureAimViewport(Vector2 viewportPoint)
		{
			aimViewportPoint = new Vector2(Mathf.Clamp(viewportPoint.x, 0.05f, 0.95f), Mathf.Clamp(viewportPoint.y, 0.05f, 0.95f));
		}

		public void CommandFireHandWeapon(TestWeaponKind kind)
		{
			if (TryGetHandSlot(kind, out var slot))
			{
				TryFireEquippedHandWeapon(slot);
			}
		}

		private void InitializeShoulderAmmunition(MechPartSlot slot)
		{
			string partId = GetEquippedPartId(slot) ?? string.Empty;
			int capacity = GetShoulderMagazineCapacity(slot);
			int count = GetShoulderMagazineCount(slot);
			if (slot == MechPartSlot.LeftBackWeapon)
			{
				leftShoulderPartId = partId;
				leftMissileRoundsRemaining = capacity;
				leftMissileMagazinesRemaining = count;
			}
			else
			{
				rightShoulderPartId = partId;
				rightMissileRoundsRemaining = capacity;
				rightMissileMagazinesRemaining = count;
			}
		}

		private int GetShoulderMagazineCapacity(MechPartSlot slot)
		{
			return Mathf.Max(0, GetEquippedWeaponPerformance(slot)?.magazineCapacity ?? 0);
		}

		private int GetShoulderMagazineCount(MechPartSlot slot)
		{
			return Mathf.Max(0, GetEquippedWeaponPerformance(slot)?.magazineCount ?? 0);
		}

		private int GetShoulderRoundsRemaining(MechPartSlot slot)
		{
			return slot == MechPartSlot.LeftBackWeapon ? leftMissileRoundsRemaining : rightMissileRoundsRemaining;
		}

		private int GetShoulderMagazinesRemaining(MechPartSlot slot)
		{
			return slot == MechPartSlot.LeftBackWeapon ? leftMissileMagazinesRemaining : rightMissileMagazinesRemaining;
		}

		private bool HasShoulderMissileAmmunition(MechPartSlot slot)
		{
			return GetShoulderRoundsRemaining(slot) > 0 && GetShoulderMagazinesRemaining(slot) > 0;
		}

		private void ConsumeShoulderMissileRound(MechPartSlot slot)
		{
			int capacity = GetShoulderMagazineCapacity(slot);
			if (slot == MechPartSlot.LeftBackWeapon)
			{
				ConsumeMissileRound(ref leftMissileRoundsRemaining, capacity, ref leftMissileMagazinesRemaining);
			}
			else
			{
				ConsumeMissileRound(ref rightMissileRoundsRemaining, capacity, ref rightMissileMagazinesRemaining);
			}
		}

		public bool RequestRifleReload()
		{
			return TryGetWeaponSlot(MechWeaponRole.AutomaticCannon, out var slot) && RequestHandReload(slot);
		}

		public bool RequestCannonReload()
		{
			return TryGetWeaponSlot(MechWeaponRole.HandCannon, out var slot) && RequestHandReload(slot);
		}

		public bool CommandFireHandWeaponAtTrack(
			TestWeaponKind kind,
			TrackId trackId)
		{
			if (radarController == null ||
				!radarController.TryAssignFireControlTrack(trackId) ||
				!TryGetHandSlot(kind, out var slot))
			{
				return false;
			}

			return TryFireEquippedHandWeapon(slot);
		}

		public bool CommandFireMissile(MechPartSlot slot)
		{
			RefreshShoulderAmmunition(slot);
			switch (GetEquippedWeaponRole(slot))
			{
			case MechWeaponRole.InterceptorMissile:
				return FireInterceptorMissile(
					slot,
					ref GetShoulderMuzzleIndex(slot));
			case MechWeaponRole.AirToAirMissile:
				return FireMissileSalvo(
					slot,
					ref GetShoulderMuzzleIndex(slot));
			default:
				return false;
			}
		}

		private void Awake()
		{
			InitializeAmmunition();
			ownCombatTarget = GetComponent<CombatTestTarget>();
			airToAirMissileProfile = ResolveProfile(airToAirMissileProfile, MissileFlightProfile.AirToAirDefault);
			interceptorMissileProfile = ResolveProfile(interceptorMissileProfile, MissileFlightProfile.InterceptorDefault);
			if (base.enabled)
			{
				if (input == null)
				{
					input = GetComponent<MechPlayerInputAdapter>();
				}
				if (runtimeState == null)
				{
					runtimeState = GetComponent<MechRuntimeState>();
				}
				if (playerRig == null)
				{
					playerRig = GetComponent<MechPlayerRig>();
				}
				if (assembler == null)
				{
					assembler = GetComponent<MechAssembler>();
				}
				if (flightMotion == null)
				{
					flightMotion = GetComponent<MechFlightMotionController>();
				}
				if (aimingCamera == null)
				{
					aimingCamera = Camera.main;
				}
				if (aimController == null && aimingCamera != null)
				{
					aimController = aimingCamera.GetComponent<MechThirdPersonCamera>();
				}
				EnsureRadarController();
			}
		}

		private void Start()
		{
			EnsureRadarController();
			ResolveMuzzles();
		}

		private void EnsureRadarController()
		{
			if (radarController == null)
			{
				radarController = GetComponent<MechRadarController>();
			}
			if (radarController == null && target != null)
			{
				radarController = base.gameObject.AddComponent<MechRadarController>();
			}
			if (radarController != null && target != null)
			{
				radarController.Configure(aimingCamera, target);
			}
		}

		private void Update()
		{
			InitializeAmmunition();
			RefreshEquippedWeaponStates();
			UpdateReloadState();
			UpdateShaoWeiReloadAndHeat(Time.deltaTime);
			if (input == null)
			{
				return;
			}
			UpdateInterceptorTarget();
			UpdatePointDefenseTarget();
			UpdateShaoWeiPointDefenseTarget();
			UpdateShaoWeiPointDefense();
			MechPlayerInputFrame frame = input.Current;
			if (frame.PrimaryReloadEdge)
			{
				RequestHandReload(MechPartSlot.LeftHandWeapon);
			}
			if (frame.SecondaryReloadEdge)
			{
				RequestHandReload(MechPartSlot.RightHandWeapon);
			}
			UpdateHandWeaponInput(MechPartSlot.LeftHandWeapon, frame.PrimaryFireHeld, frame.PrimaryFireEdge);
			UpdateHandWeaponInput(MechPartSlot.RightHandWeapon, frame.SecondaryFireHeld, frame.SecondaryFireEdge);
			bool rifleTriggerHeld = IsAutomaticTriggerActive(MechPartSlot.LeftHandWeapon, frame.PrimaryFireHeld) ||
				IsAutomaticTriggerActive(MechPartSlot.RightHandWeapon, frame.SecondaryFireHeld);
			handWeaponAudio?.SetRifleTriggerHeld(rifleTriggerHeld);
			if (playerRig != null && playerRig.ConsumeShoulderMissileLaunch(MechPartSlot.LeftBackWeapon))
			{
				CommandFireMissile(MechPartSlot.LeftBackWeapon);
			}
			if (playerRig != null && playerRig.ConsumeShoulderMissileLaunch(MechPartSlot.RightBackWeapon))
			{
				CommandFireMissile(MechPartSlot.RightBackWeapon);
			}
		}

		private void LateUpdate()
		{
			ApplyShaoWeiVisual(MechPartSlot.LeftBackWeapon, leftShaoWeiState);
			ApplyShaoWeiVisual(MechPartSlot.RightBackWeapon, rightShaoWeiState);
		}

		private void UpdateShaoWeiServo(MechPartSlot slot, ShaoWeiWeaponState state, float deltaTime)
		{
			MechWeaponPerformance performance = state.Performance;
			if (performance == null || !IsShaoWeiEquipped(slot) || !state.Spinning || !state.HasAimPoint || state.AimPivot == null)
			{
				return;
			}
			Transform muzzle = PeekShaoWeiMuzzle(slot);
			if (muzzle == null)
			{
				return;
			}
			Vector3 currentVisualDirection = muzzle.position - state.AimPivot.position;
			Vector3 rawAimDirection = state.AimPoint - state.AimPivot.position;
			if (currentVisualDirection.sqrMagnitude <= 1E-06f || rawAimDirection.sqrMagnitude <= 1E-06f)
			{
				return;
			}

			currentVisualDirection.Normalize();
			rawAimDirection.Normalize();
			if (!state.HasFilteredAimDirection)
			{
				state.FilteredAimDirection = rawAimDirection;
				state.HasFilteredAimDirection = true;
			}
			else
			{
				float smoothingSeconds = Mathf.Max(0f, performance.aimSmoothingSeconds);
				float blend = smoothingSeconds <= 0.0001f
					? 1f
					: 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / smoothingSeconds);
				state.FilteredAimDirection = Vector3.Slerp(
					state.FilteredAimDirection,
					rawAimDirection,
					blend).normalized;
			}

			if (!state.HasSlewedDirection)
			{
				state.SlewedDirection = currentVisualDirection;
				state.HasSlewedDirection = true;
				state.TraverseSpeedDegreesPerSecond = 0f;
			}

			float remainingDegrees = Vector3.Angle(state.SlewedDirection, state.FilteredAimDirection);
			float maximumSpeed = Mathf.Max(1f, performance.traverseRateDegreesPerSecond);
			float deceleration = Mathf.Max(1f, performance.traverseDecelerationDegreesPerSecondSquared);
			float brakingSpeed = Mathf.Sqrt(2f * deceleration * remainingDegrees);
			float targetSpeed = Mathf.Min(maximumSpeed, brakingSpeed);
			float acceleration = targetSpeed >= state.TraverseSpeedDegreesPerSecond
				? Mathf.Max(1f, performance.traverseAccelerationDegreesPerSecondSquared)
				: deceleration;
			state.TraverseSpeedDegreesPerSecond = Mathf.MoveTowards(
				state.TraverseSpeedDegreesPerSecond,
				targetSpeed,
				acceleration * Mathf.Max(0f, deltaTime));

			float stepRadians = state.TraverseSpeedDegreesPerSecond * Mathf.Deg2Rad * Mathf.Max(0f, deltaTime);
			state.SlewedDirection = Vector3.RotateTowards(
				state.SlewedDirection,
				state.FilteredAimDirection,
				stepRadians,
				0f).normalized;
			ApplyShaoWeiVisual(slot, state);
		}

		private void ApplyShaoWeiVisual(MechPartSlot slot, ShaoWeiWeaponState state)
		{
			if (!IsShaoWeiEquipped(slot) || !state.Spinning || !state.HasSlewedDirection || state.AimPivot == null)
			{
				return;
			}
			Transform muzzle = PeekShaoWeiMuzzle(slot);
			if (muzzle == null)
			{
				return;
			}
			Vector3 currentVisualDirection = muzzle.position - state.AimPivot.position;
			if (currentVisualDirection.sqrMagnitude > 1E-06f && state.SlewedDirection.sqrMagnitude > 1E-06f)
			{
				Quaternion correction = Quaternion.FromToRotation(
					currentVisualDirection.normalized,
					state.SlewedDirection.normalized);
				state.AimPivot.rotation = correction * state.AimPivot.rotation;
			}
		}

		private void ResolveMuzzles()
		{
			muzzlesBySlot.Clear();
			if (runtimeState == null)
			{
				return;
			}
			for (int i = 0; i < runtimeState.AssembledParts.Count; i++)
			{
				MechAssemblyRecord record = runtimeState.AssembledParts[i];
				if (record?.instance == null || !MechSlotRules.IsWeaponSlot(record.slot))
				{
					continue;
				}
				bool isShaoWei = MechWeaponRoleResolver.Resolve(record.part) == MechWeaponRole.PointDefenseCannon;
				Transform[] transforms = record.instance.GetComponentsInChildren<Transform>(includeInactive: true);
				Transform[] candidates = transforms.Where(isShaoWei ? new Func<Transform, bool>(IsShaoWeiMuzzleTransform) : new Func<Transform, bool>(IsMuzzleTransform)).OrderBy<Transform, string>((Transform candidate) => candidate.name, StringComparer.OrdinalIgnoreCase).ToArray();
				if (isShaoWei && candidates.Length == 0)
				{
					muzzlesBySlot[record.slot] = Array.Empty<Transform>();
					string partId = record.part.PartId;
					if (shaoWeiMuzzleWarnings.Add(partId))
					{
						Debug.LogWarning("[MechTestWeaponSystem] " + partId + " has no _dmy_300 muzzle; point defense is disabled for this slot.", record.instance);
					}
				}
				else
				{
					muzzlesBySlot[record.slot] = ((candidates.Length != 0) ? candidates : new Transform[1] { record.instance.transform });
				}
				if (!isShaoWei)
				{
					continue;
				}
				ShaoWeiWeaponState state = GetShaoWeiState(record.slot);
				if (state != null)
				{
					state.AnimationPlayer = record.instance.GetComponent<MechEquipmentAnimationPlayer>();
					Transform aimPivot = transforms.FirstOrDefault((Transform candidate) => candidate.name.EndsWith("LookAt_01", StringComparison.OrdinalIgnoreCase));
					if (state.AimPivot != aimPivot)
					{
						ResetShaoWeiAimState(state);
						state.AimPivot = aimPivot;
					}
				}
			}
		}

		private Vector3 GetWeaponPosition(MechPartSlot slot)
		{
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0 || !(muzzles[0] != null))
			{
				return base.transform.position;
			}
			return muzzles[0].position;
		}

		private bool TryFireEquippedHandWeapon(MechPartSlot slot, bool hasExternalTrack = false, Vector3 externalTrackPosition = default(Vector3), Vector3 externalTrackVelocity = default(Vector3))
		{
			RefreshHandWeaponState(slot);
			HandWeaponState state = GetHandState(slot);
			MechWeaponPerformance weaponPerformance = state.Performance;
			if (weaponPerformance == null || !weaponPerformance.IsConfigured)
			{
				return false;
			}
			TestWeaponKind kind = RoleToTestWeaponKind(state.Role);
			return kind != TestWeaponKind.Missile &&
				TryFireHandWeapon(slot, kind, ref GetHandMuzzleIndex(slot), hasExternalTrack, externalTrackPosition, externalTrackVelocity);
		}

		private bool TryFireHandWeapon(MechPartSlot slot, TestWeaponKind kind, ref int muzzleIndex, bool hasExternalTrack = false, Vector3 externalTrackPosition = default(Vector3), Vector3 externalTrackVelocity = default(Vector3))
		{
			InitializeAmmunition();
			RefreshHandWeaponState(slot);
			UpdateReloadState();
			HandWeaponState state = GetHandState(slot);
			MechWeaponPerformance weaponPerformance = state.Performance;
			if (weaponPerformance == null || !weaponPerformance.IsConfigured)
			{
				return false;
			}
			if (RoleToTestWeaponKind(state.Role) != kind)
			{
				return false;
			}
			AutomaticCannonProfile rifleProfile = default(AutomaticCannonProfile);
			if (kind == TestWeaponKind.Rifle)
			{
				rifleProfile = ResolveAutomaticCannonProfile(state.Performance);
				UpdateRifleDispersion(state, in rifleProfile, Time.time);
				if (Time.time < state.NextFireTime)
				{
					return false;
				}
			}
			switch (kind)
			{
			case TestWeaponKind.Rifle:
				if (IsReloading(state))
				{
					return false;
				}
				if (state.RoundsRemaining <= 0)
				{
					if (state.MagazinesRemaining > 0)
					{
						BeginHandReload(slot, state, base.transform.position);
					}
					return false;
				}
				break;
			case TestWeaponKind.Cannon:
				if (IsReloading(state))
				{
					return false;
				}
				if (state.RoundsRemaining <= 0)
				{
					if (state.MagazinesRemaining > 0)
					{
						BeginHandReload(slot, state, base.transform.position);
					}
					return false;
				}
				break;
			}
			Transform muzzle = GetNextMuzzle(slot, ref muzzleIndex);
			if (muzzle == null)
			{
				Debug.LogWarning($"[MechTestWeaponSystem] No muzzle for {slot}.", this);
				return false;
			}
			float projectileSpeed = weaponPerformance.muzzleVelocityMetersPerSecond;
			float projectileLifetime = weaponPerformance.projectileLifetimeSeconds;
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Vector3 aimPoint = Vector3.zero;
			bool locked = false;
			bool pointDefenseEngaged = false;
			if (hasExternalTrack)
			{
				Vector3 value = externalTrackPosition - muzzle.position;
				locked = FireSolutionSolver.TrySolveBallisticIntercept(relativeVelocity: ToDouble3(externalTrackVelocity - inheritedVelocity), relativePosition: ToDouble3(value), targetAcceleration: double3.zero, projectileSpeed: projectileSpeed, maximumFlightTime: projectileLifetime, flightTime: out var _, relativeInterceptPoint: out var relativeInterceptPoint);
				if (locked)
				{
					aimPoint = muzzle.position + ToVector3(relativeInterceptPoint);
				}
			}
			else
			{
				locked = kind == TestWeaponKind.Rifle &&
					handPointDefensePriorityEnabled &&
					TryGetPointDefenseAimPoint(muzzle.position, projectileSpeed, GetCurrentRifleDispersion(state, in rifleProfile), out aimPoint);
				pointDefenseEngaged = locked;
				if (!locked)
				{
					locked = TryGetAimPoint((kind == TestWeaponKind.Rifle) ? gunAutoAimDegrees : (gunAutoAimDegrees + 2f), projectileSpeed, projectileLifetime, out aimPoint, out var _);
				}
			}
			Vector3 direction = (aimPoint - muzzle.position).normalized;
			if (!locked)
			{
				direction = GetAimRay().direction;
			}
			direction = ApplyArmAiming(state, direction, weaponPerformance);
			if (kind == TestWeaponKind.Rifle)
			{
				direction = ApplyRifleDispersion(direction, state, in rifleProfile);
			}
			else
			{
				direction = ApplyArmPointingError(direction, state);
			}
			if (kind == TestWeaponKind.Rifle)
			{
				Vector3 position = muzzle.position;
				Vector3 direction2 = direction;
				Transform launchAnchor = muzzle;
				Vector3 inheritedVelocity2 = inheritedVelocity;
				float interceptorRoundLethalRadius = pointDefenseEngaged ? pointDefenseRoundLethalRadiusMeters : 0f;
				bool targetProximityFuseEnabled = rifleProfile.HasTargetProximityFuse && locked && !pointDefenseEngaged;
				SpawnProjectile(position, direction2, kind, rifleProfile.MuzzleVelocityMetersPerSecond, rifleProfile.DirectHitDamage, 0.08f, rifleProfile.ProjectileLifetimeSeconds, default(TrackId), 0f, 0f, 0f, 6011100, 6013101, 6011101, launchAnchor, weaponPerformance.maximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, rifleProfile.ProximityFuseRadiusMeters, weaponPerformance.proximityFuseArmSeconds, weaponPerformance.proximityFuseSafeDistanceMeters, rifleProfile.WarheadLethalRadiusMeters, inheritedVelocity2, null, MechPartSlot.None, interceptorMissile: false, default(MissileFlightProfile), hasInitialGuidanceEstimate: false, default(Vector3), default(Vector3), 0f, default(Vector3), coldLaunch: false, 0f, 0f, 8f, 0.6f, interceptorRoundLethalRadius, 0f, 0f, targetProximityFuseEnabled, rifleProfile.RoundsPerMinute);
				PlayHandWeaponAudio(kind, slot, muzzle);
				RegisterRifleRecoil(state, in rifleProfile, Time.time);
				state.NextFireTime = Time.time + 60f / Mathf.Max(rifleProfile.RoundsPerMinute, 1f);
				state.RoundsRemaining = Mathf.Max(0, state.RoundsRemaining - 1);
				if (state.RoundsRemaining == 0)
				{
					state.MagazinesRemaining = Mathf.Max(0, state.MagazinesRemaining - 1);
					if (state.MagazinesRemaining > 0)
					{
						BeginHandReload(slot, state, muzzle.position);
					}
				}
			}
			else
			{
				Vector3 position2 = muzzle.position;
				Vector3 direction3 = direction;
				Transform launchAnchor = muzzle;
				float interceptorRoundLethalRadius = weaponPerformance.proximityFuseRadiusMeters;
				float proximityFuseArmTime = weaponPerformance.proximityFuseArmSeconds;
				float proximityFuseSafeDistance = weaponPerformance.proximityFuseSafeDistanceMeters;
				float warheadLethalRadius = weaponPerformance.warheadLethalRadiusMeters;
				Vector3 inheritedVelocity2 = inheritedVelocity;
				bool cannonProximityFuseEnabled = weaponPerformance.targetProximityFuse && locked;
				SpawnProjectile(position2, direction3, kind, weaponPerformance.muzzleVelocityMetersPerSecond, weaponPerformance.directHitDamage, 0.2f, weaponPerformance.projectileLifetimeSeconds, default(TrackId), 0f, 0f, 0f, 6031100, 6031101, 0, launchAnchor, weaponPerformance.maximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, interceptorRoundLethalRadius, proximityFuseArmTime, proximityFuseSafeDistance, warheadLethalRadius, inheritedVelocity2, null, MechPartSlot.None, interceptorMissile: false, default(MissileFlightProfile), hasInitialGuidanceEstimate: false, default(Vector3), default(Vector3), 0f, default(Vector3), coldLaunch: false, 0f, 0f, 8f, 0.6f, 0f, 0f, 0f, cannonProximityFuseEnabled);
				PlayHandWeaponAudio(kind, slot, muzzle);
				state.RoundsRemaining = Mathf.Max(0, state.RoundsRemaining - 1);
				if (state.RoundsRemaining == 0)
				{
					state.MagazinesRemaining = Mathf.Max(0, state.MagazinesRemaining - 1);
				}
				if (state.RoundsRemaining > 0 || state.MagazinesRemaining > 0)
				{
					BeginHandReload(slot, state, muzzle.position);
				}
			}
			return true;
		}

		private void UpdateHandWeaponInput(MechPartSlot slot, bool fireHeld, bool fireEdge)
		{
			HandWeaponState state = GetHandState(slot);
			if (state.Role == MechWeaponRole.AutomaticCannon)
			{
				if (fireHeld && Time.time >= state.NextFireTime)
				{
					TryFireEquippedHandWeapon(slot);
				}
			}
			else if (state.Role == MechWeaponRole.HandCannon && fireEdge)
			{
				TryFireEquippedHandWeapon(slot);
			}
		}

		private bool IsAutomaticTriggerActive(MechPartSlot slot, bool fireHeld)
		{
			HandWeaponState state = GetHandState(slot);
			return fireHeld && state.Role == MechWeaponRole.AutomaticCannon &&
				!IsReloading(state) && state.RoundsRemaining > 0;
		}

		private bool IsHandFireHeld(MechPartSlot slot)
		{
			if (input == null)
			{
				return false;
			}
			return slot == MechPartSlot.LeftHandWeapon
				? input.Current.PrimaryFireHeld
				: input.Current.SecondaryFireHeld;
		}

		public bool RequestHandReload(MechPartSlot slot)
		{
			InitializeAmmunition();
			RefreshHandWeaponState(slot);
			UpdateReloadState();
			HandWeaponState state = GetHandState(slot);
			int capacity = GetHandMagazineCapacity(state);
			if (capacity <= 0 || IsReloading(state) || state.RoundsRemaining >= capacity || state.MagazinesRemaining <= 1)
			{
				return false;
			}
			state.MagazinesRemaining--;
			state.RoundsRemaining = 0;
			BeginHandReload(slot, state, GetWeaponPosition(slot));
			return true;
		}

		public bool IsHandWeaponReloading(MechPartSlot slot)
		{
			return IsReloading(GetHandState(slot));
		}

		public bool TryGetWeaponHudState(MechPartSlot slot, out MechWeaponHudState hudState)
		{
			InitializeAmmunition();
			RefreshEquippedWeaponStates();
			MechWeaponRole role = GetEquippedWeaponRole(slot);
			if (slot == MechPartSlot.LeftHandWeapon || slot == MechPartSlot.RightHandWeapon)
			{
				HandWeaponState state = GetHandState(slot);
				if (role != MechWeaponRole.HandCannon && role != MechWeaponRole.AutomaticCannon)
				{
					hudState = default(MechWeaponHudState);
					return false;
				}
				float duration = GetHandReloadDuration(state);
				string hudLabel = string.IsNullOrWhiteSpace(state.Performance?.hudLabel)
					? role == MechWeaponRole.HandCannon ? "57MM" : "30MM"
					: state.Performance.hudLabel;
				hudState = new MechWeaponHudState(
					role,
					hudLabel,
					state.RoundsRemaining,
					GetHandMagazineCapacity(state),
					state.MagazinesRemaining,
					IsReloading(state),
					GetReloadRemainingSeconds(state),
					duration,
					role == MechWeaponRole.HandCannon ? "装" : "换");
				return true;
			}

			if (role == MechWeaponRole.PointDefenseCannon)
			{
				ShaoWeiWeaponState state = GetShaoWeiState(slot);
				MechWeaponPerformance performance = state.Performance;
				hudState = new MechWeaponHudState(
					role,
					string.IsNullOrWhiteSpace(performance?.hudLabel) ? "CIWS" : performance.hudLabel,
					state.RoundsRemaining,
					performance?.magazineCapacity ?? 0,
					state.MagazinesRemaining,
					state.Reloading || state.Overheated,
					GetShaoWeiRecoveryRemainingSeconds(state),
					GetShaoWeiRecoveryDurationSeconds(state),
					"恢");
				return true;
			}

			if (role == MechWeaponRole.InterceptorMissile || role == MechWeaponRole.AirToAirMissile)
			{
				hudState = new MechWeaponHudState(
					role,
					string.IsNullOrWhiteSpace(GetEquippedWeaponPerformance(slot)?.hudLabel)
						? role == MechWeaponRole.InterceptorMissile ? "INT" : "MSL"
						: GetEquippedWeaponPerformance(slot).hudLabel,
					GetShoulderRoundsRemaining(slot),
					GetShoulderMagazineCapacity(slot),
					GetShoulderMagazinesRemaining(slot),
					false,
					0f,
					0f,
					string.Empty);
				return true;
			}

			hudState = default(MechWeaponHudState);
			return false;
		}

		private static TestWeaponKind RoleToTestWeaponKind(MechWeaponRole role)
		{
			return role switch
			{
				MechWeaponRole.HandCannon => TestWeaponKind.Cannon,
				MechWeaponRole.AutomaticCannon => TestWeaponKind.Rifle,
				_ => TestWeaponKind.Missile
			};
		}

		private bool TryGetHandSlot(TestWeaponKind kind, out MechPartSlot slot)
		{
			MechWeaponRole role = kind == TestWeaponKind.Cannon
				? MechWeaponRole.HandCannon
				: kind == TestWeaponKind.Rifle ? MechWeaponRole.AutomaticCannon : MechWeaponRole.None;
			if (role != MechWeaponRole.None)
			{
				return TryGetWeaponSlot(role, out slot);
			}
			slot = MechPartSlot.None;
			return false;
		}

		private HandWeaponState GetHandState(MechPartSlot slot)
		{
			return slot == MechPartSlot.LeftHandWeapon ? leftHandWeaponState : rightHandWeaponState;
		}

		private HandWeaponState FindHandState(MechWeaponRole role)
		{
			RefreshEquippedWeaponStates();
			if (leftHandWeaponState.Role == role)
			{
				return leftHandWeaponState;
			}
			return rightHandWeaponState.Role == role ? rightHandWeaponState : null;
		}

		private void RefreshEquippedWeaponStates()
		{
			RefreshHandWeaponState(MechPartSlot.LeftHandWeapon);
			RefreshHandWeaponState(MechPartSlot.RightHandWeapon);
			RefreshShoulderAmmunition(MechPartSlot.LeftBackWeapon);
			RefreshShoulderAmmunition(MechPartSlot.RightBackWeapon);
		}

		private void RefreshShoulderAmmunition(MechPartSlot slot)
		{
			string partId = GetEquippedPartId(slot) ?? string.Empty;
			string previousPartId = slot == MechPartSlot.LeftBackWeapon ? leftShoulderPartId : rightShoulderPartId;
			if (!string.Equals(previousPartId, partId, StringComparison.OrdinalIgnoreCase))
			{
				InitializeShoulderAmmunition(slot);
				uint seed = slot == MechPartSlot.LeftBackWeapon
					? nextRifleDispersionSeed ^ 0xA5110F1
					: nextRifleDispersionSeed ^ 0xA5110F2;
				InitializeShaoWeiState(slot, GetShaoWeiState(slot), seed);
			}
		}

		private void RefreshHandWeaponState(MechPartSlot slot)
		{
			HandWeaponState state = GetHandState(slot);
			string partId = GetEquippedPartId(slot) ?? string.Empty;
			MechWeaponRole role = MechWeaponRoleResolver.Resolve(GetEquippedPartDefinition(slot));
			if (!string.Equals(state.PartId, partId, StringComparison.OrdinalIgnoreCase) || state.Role != role)
			{
				InitializeHandWeaponState(slot, state);
			}
		}

		private void InitializeHandWeaponState(MechPartSlot slot, HandWeaponState state)
		{
			var part = GetEquippedPartDefinition(slot);
			state.PartId = part?.PartId ?? string.Empty;
			state.Performance = part?.WeaponPerformance;
			state.WeaponMassKg = Mathf.Max(0f, part?.Stats?.massKg ?? 0f);
			state.Role = state.Performance != null && state.Performance.IsConfigured
				? state.Performance.role
				: MechWeaponRole.None;
			state.RoundsRemaining = GetHandMagazineCapacity(state);
			state.MagazinesRemaining = GetHandMagazineCount(state);
			state.Reloading = false;
			state.ReloadCompleteTime = float.NegativeInfinity;
			state.NextFireTime = 0f;
			state.RecoilDispersionMilliradians = 0f;
			state.LastDispersionUpdateTime = Time.time;
			state.LastShotTime = float.NegativeInfinity;
			state.BurstShotCount = 0;
			state.HasTrackedAimDirection = false;
			state.TrackedAimDirection = Vector3.zero;
			state.AimAngularSpeedDegreesPerSecond = 0f;
			state.LastAimUpdateTime = Time.time;
		}

		private int GetHandMagazineCapacity(HandWeaponState state)
		{
			if (state == null)
			{
				return 0;
			}
			return state.Role switch
			{
				MechWeaponRole.HandCannon => state.Performance?.magazineCapacity ?? 0,
				MechWeaponRole.AutomaticCannon => state.Performance?.magazineCapacity ?? 0,
				_ => 0
			};
		}

		private int GetHandMagazineCount(HandWeaponState state)
		{
			if (state == null)
			{
				return 0;
			}
			return state.Role switch
			{
				MechWeaponRole.HandCannon => state.Performance?.magazineCount ?? 0,
				MechWeaponRole.AutomaticCannon => state.Performance?.magazineCount ?? 0,
				_ => 0
			};
		}

		private float GetHandReloadDuration(HandWeaponState state)
		{
			float duration = state.Performance?.reloadSeconds ?? 0f;
			return Mathf.Max(0.1f, duration);
		}

		private static bool IsReloading(HandWeaponState state)
		{
			return state != null && state.Reloading && Time.time < state.ReloadCompleteTime;
		}

		private static float GetReloadRemainingSeconds(HandWeaponState state)
		{
			return IsReloading(state) ? Mathf.Max(0f, state.ReloadCompleteTime - Time.time) : 0f;
		}

		private ref int GetHandMuzzleIndex(MechPartSlot slot)
		{
			if (slot == MechPartSlot.LeftHandWeapon)
			{
				return ref leftHandMuzzleIndex;
			}
			return ref rightHandMuzzleIndex;
		}

		private ref int GetShoulderMuzzleIndex(MechPartSlot slot)
		{
			if (slot == MechPartSlot.LeftBackWeapon)
			{
				return ref leftBackMuzzleIndex;
			}
			return ref rightBackMuzzleIndex;
		}

		private void InitializeAmmunition()
		{
			if (!ammunitionInitialized)
			{
				if (ammunitionConfigurationVersion < 2)
				{
					cannonMagazineCapacity = 1;
					cannonMagazineCount = 32;
					rightMissileMagazineCount = 6;
					ammunitionConfigurationVersion = 2;
				}
				cannonMagazineCapacity = Mathf.Max(1, cannonMagazineCapacity);
				cannonMagazineCount = Mathf.Max(1, cannonMagazineCount);
				leftMissileMagazineCapacity = Mathf.Max(1, leftMissileMagazineCapacity);
				leftMissileMagazineCount = Mathf.Max(1, leftMissileMagazineCount);
				rightMissileMagazineCapacity = Mathf.Max(1, rightMissileMagazineCapacity);
				rightMissileMagazineCount = Mathf.Max(1, rightMissileMagazineCount);
				shaoWeiMagazineCapacity = Mathf.Max(1, shaoWeiMagazineCapacity);
				shaoWeiMagazineCount = Mathf.Max(1, shaoWeiMagazineCount);
				shaoWeiHeatCapacityMj = Mathf.Max(0.1f, shaoWeiHeatCapacityMj);
				shaoWeiResumeThresholdMj = Mathf.Clamp(shaoWeiResumeThresholdMj, 0f, shaoWeiHeatCapacityMj);
				InitializeHandWeaponState(MechPartSlot.LeftHandWeapon, leftHandWeaponState);
				InitializeHandWeaponState(MechPartSlot.RightHandWeapon, rightHandWeaponState);
				InitializeShoulderAmmunition(MechPartSlot.LeftBackWeapon);
				InitializeShoulderAmmunition(MechPartSlot.RightBackWeapon);
				InitializeShaoWeiState(MechPartSlot.LeftBackWeapon, leftShaoWeiState, nextRifleDispersionSeed ^ 0xA5110F1);
				InitializeShaoWeiState(MechPartSlot.RightBackWeapon, rightShaoWeiState, nextRifleDispersionSeed ^ 0xA5110F2);
				rifleDispersionRandomState = nextRifleDispersionSeed;
				nextRifleDispersionSeed += 1831565813u;
				if (rifleDispersionRandomState == 0)
				{
					rifleDispersionRandomState = 1u;
				}
				ammunitionInitialized = true;
			}
		}

		private void InitializeShaoWeiState(MechPartSlot slot, ShaoWeiWeaponState state, uint randomSeed)
		{
			state.Performance = GetEquippedWeaponRole(slot) == MechWeaponRole.PointDefenseCannon
				? GetEquippedWeaponPerformance(slot)
				: null;
			state.RoundsRemaining = state.Performance?.magazineCapacity ?? 0;
			state.MagazinesRemaining = state.Performance?.magazineCount ?? 0;
			state.HeatMj = 0f;
			state.Overheated = false;
			state.Reloading = false;
			state.ReloadCompleteTime = float.NegativeInfinity;
			state.NextFireTime = 0f;
			state.SpinReadyTime = 0f;
			state.Spinning = false;
			ResetShaoWeiAimState(state);
			state.DispersionRandomState = ((randomSeed == 0) ? 1u : randomSeed);
		}

		private static void ResetShaoWeiAimState(ShaoWeiWeaponState state)
		{
			state.HasSlewedDirection = false;
			state.SlewedDirection = Vector3.zero;
			state.HasFilteredAimDirection = false;
			state.FilteredAimDirection = Vector3.zero;
			state.TraverseSpeedDegreesPerSecond = 0f;
			state.HasAimPoint = false;
			state.AimPoint = Vector3.zero;
		}

		private void UpdateShaoWeiReloadAndHeat(float deltaTime)
		{
			UpdateShaoWeiState(leftShaoWeiState, deltaTime);
			UpdateShaoWeiState(rightShaoWeiState, deltaTime);
		}

		private void UpdateShaoWeiState(ShaoWeiWeaponState state, float deltaTime)
		{
			MechWeaponPerformance performance = state.Performance;
			if (performance == null)
			{
				return;
			}
			state.HeatMj = Mathf.Max(0f, state.HeatMj - Mathf.Max(0f, performance.coolingMw) * Mathf.Max(0f, deltaTime));
			if (state.Overheated && state.HeatMj <= performance.resumeThresholdMj)
			{
				state.Overheated = false;
			}
			if (state.Reloading && !(Time.time < state.ReloadCompleteTime))
			{
				state.Reloading = false;
				if (state.RoundsRemaining == 0 && state.MagazinesRemaining > 0)
				{
					state.RoundsRemaining = performance.magazineCapacity;
				}
			}
		}

		private void BeginShaoWeiReload(ShaoWeiWeaponState state)
		{
			if (!state.Reloading && state.MagazinesRemaining > 0)
			{
				state.Reloading = true;
				state.ReloadCompleteTime = Time.time + Mathf.Max(0.1f, state.Performance?.reloadSeconds ?? 0f);
			}
		}

		private float GetShaoWeiRecoveryRemainingSeconds(ShaoWeiWeaponState state)
		{
			MechWeaponPerformance performance = state.Performance;
			if (performance == null)
			{
				return 0f;
			}
			float a = (state.Reloading ? Mathf.Max(0f, state.ReloadCompleteTime - Time.time) : 0f);
			float coolingRemaining = state.Overheated
				? Mathf.Max(0f, (state.HeatMj - performance.resumeThresholdMj) / Mathf.Max(0.01f, performance.coolingMw))
				: 0f;
			return Mathf.Max(a, coolingRemaining);
		}

		private static float GetShaoWeiRecoveryDurationSeconds(ShaoWeiWeaponState state)
		{
			MechWeaponPerformance performance = state.Performance;
			if (performance == null)
			{
				return 0f;
			}
			float coolingDuration = (performance.heatCapacityMj - performance.resumeThresholdMj) /
				Mathf.Max(0.01f, performance.coolingMw);
			return Mathf.Max(performance.reloadSeconds, coolingDuration);
		}

		private void UpdateRifleDispersion(HandWeaponState state, in AutomaticCannonProfile profile, float now)
		{
			float elapsed = Mathf.Max(0f, now - state.LastDispersionUpdateTime);
			state.LastDispersionUpdateTime = now;
			if (!(elapsed <= 0f))
			{
				float recoveryStart = state.LastShotTime + profile.DispersionRecoveryDelaySeconds;
				float recoveryElapsed = Mathf.Max(0f, now - Mathf.Max(now - elapsed, recoveryStart));
				state.RecoilDispersionMilliradians = Mathf.MoveTowards(
					state.RecoilDispersionMilliradians,
					0f,
					profile.DispersionRecoveryMilliradiansPerSecond
					* ResolveArmRecoilRecoveryScale(state.Performance)
					* recoveryElapsed);
				if (now - state.LastShotTime >= profile.BurstResetSeconds)
				{
					state.BurstShotCount = 0;
				}
			}
		}

		private void RegisterRifleRecoil(HandWeaponState state, in AutomaticCannonProfile profile, float now)
		{
			if (now - state.LastShotTime >= profile.BurstResetSeconds)
			{
				state.BurstShotCount = 0;
			}
			float recoilPerShot = state.BurstShotCount < profile.LowRecoilShotCount
				? profile.LowRecoilPerShotMilliradians
				: profile.SustainedRecoilPerShotMilliradians;
			recoilPerShot *= ResolveArmRecoilLoadScale(state.Performance);
			float maximumRecoil = Mathf.Max(0f, profile.MaximumDispersionMilliradians - profile.BaseDispersionMilliradians);
			state.RecoilDispersionMilliradians = Mathf.Min(maximumRecoil, state.RecoilDispersionMilliradians + recoilPerShot);
			state.BurstShotCount++;
			state.LastShotTime = now;
		}

		private float GetCurrentRifleDispersion(HandWeaponState state, in AutomaticCannonProfile profile)
		{
			var armError = ResolveArmPointingErrorMrad(state);
			var weaponError = profile.BaseDispersionMilliradians + state.RecoilDispersionMilliradians;
			var combined = Mathf.Sqrt(weaponError * weaponError + armError * armError);
			var maximum = Mathf.Sqrt(
				profile.MaximumDispersionMilliradians * profile.MaximumDispersionMilliradians
				+ armError * armError);
			return Mathf.Clamp(combined, 0f, maximum);
		}

		private Vector3 ApplyArmAiming(
			HandWeaponState state,
			Vector3 desiredDirection,
			MechWeaponPerformance performance)
		{
			if (desiredDirection.sqrMagnitude <= 0.000001f)
			{
				return transform.forward;
			}

			desiredDirection.Normalize();
			var arm = ResolveArmPerformance();
			if (arm == null || arm.aimTraverseRateDegPerSecond <= 0f)
			{
				state.TrackedAimDirection = desiredDirection;
				state.HasTrackedAimDirection = true;
				state.LastAimUpdateTime = Time.time;
				return desiredDirection;
			}

			var now = Time.time;
			var aimDeltaTime = Mathf.Max(0f, now - state.LastAimUpdateTime);
			state.LastAimUpdateTime = now;
			var massScale = ResolveArmWeaponMassScale(state, arm);
			var maximumSpeed = arm.aimTraverseRateDegPerSecond * massScale;
			var acceleration = arm.aimAngularAccelerationDegPerSecondSquared * massScale;
			state.AimAngularSpeedDegreesPerSecond = acceleration > 0f
				? Mathf.MoveTowards(
					state.AimAngularSpeedDegreesPerSecond,
					maximumSpeed,
					acceleration * aimDeltaTime)
				: maximumSpeed;
			if (!state.HasTrackedAimDirection)
			{
				state.TrackedAimDirection = desiredDirection;
				state.HasTrackedAimDirection = true;
				return desiredDirection;
			}

			state.TrackedAimDirection = Vector3.RotateTowards(
				state.TrackedAimDirection,
				desiredDirection,
				state.AimAngularSpeedDegreesPerSecond * Mathf.Deg2Rad * aimDeltaTime,
				0f).normalized;
			return state.TrackedAimDirection;
		}

		private Vector3 ApplyArmPointingError(
			Vector3 direction,
			HandWeaponState state)
		{
			var error = ResolveArmPointingErrorMrad(state);
			if (error <= 0f || direction.sqrMagnitude <= 0.000001f)
			{
				return direction.normalized;
			}

			NextRifleGaussianPair(out var horizontalSigma, out var verticalSigma);
			var forward = direction.normalized;
			var reference = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f
				? Vector3.right
				: Vector3.up;
			var right = Vector3.Cross(reference, forward).normalized;
			var up = Vector3.Cross(forward, right).normalized;
			var radians = error * 0.001f;
			return (forward + right * horizontalSigma * radians + up * verticalSigma * radians).normalized;
		}

		private MechArmPerformance ResolveArmPerformance()
		{
			return runtimeState != null ? runtimeState.AggregatedStats?.armPerformance : null;
		}

		private float ResolveArmWeaponMassScale(
			HandWeaponState state,
			MechArmPerformance arm)
		{
			if (state == null || arm == null || arm.weaponLoadLimitKg <= 0f ||
				state.WeaponMassKg <= arm.weaponLoadLimitKg)
			{
				return 1f;
			}

			return Mathf.Clamp(arm.weaponLoadLimitKg / state.WeaponMassKg, 0.25f, 1f);
		}

		private float ResolveArmPointingErrorMrad(HandWeaponState state)
		{
			var arm = ResolveArmPerformance();
			if (arm == null)
			{
				return 0f;
			}

			return Mathf.Max(0f, arm.weaponPointingErrorMrad) /
			       ResolveArmWeaponMassScale(state, arm);
		}

		private float ResolveArmRecoilLoadScale(MechWeaponPerformance performance)
		{
			var arm = ResolveArmPerformance();
			if (arm == null || arm.recoilImpulseLimitNs <= 0f || performance == null)
			{
				return 1f;
			}

			return Mathf.Clamp(
				Mathf.Max(0f, performance.recoilImpulseNewtonSeconds) / arm.recoilImpulseLimitNs,
				0.5f,
				3f);
		}

		private float ResolveArmRecoilRecoveryScale(MechWeaponPerformance performance)
		{
			var arm = ResolveArmPerformance();
			if (arm == null || arm.recoilRecoveryRateNsPerSecond <= 0f)
			{
				return 1f;
			}

			var impulse = performance != null
				? Mathf.Max(1f, performance.recoilImpulseNewtonSeconds)
				: 22000f;
			return Mathf.Clamp(arm.recoilRecoveryRateNsPerSecond / (impulse * 4f), 0.25f, 4f);
		}

		private Vector3 ApplyRifleDispersion(Vector3 forward, HandWeaponState state, in AutomaticCannonProfile profile)
		{
			if (forward.sqrMagnitude <= 1E-06f)
			{
				return base.transform.forward;
			}
			forward.Normalize();
			float dispersionMilliradians = GetCurrentRifleDispersion(state, in profile);
			if (dispersionMilliradians <= 0f)
			{
				return forward;
			}
			NextRifleGaussianPair(out var horizontalSigma, out var verticalSigma);
			horizontalSigma = Mathf.Clamp(horizontalSigma, -2.5f, 2.5f);
			verticalSigma = Mathf.Clamp(verticalSigma, -2.5f, 2.5f);
			Vector3 right = Vector3.Cross((Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f) ? Vector3.right : Vector3.up, forward).normalized;
			Vector3 up = Vector3.Cross(forward, right).normalized;
			float dispersionRadians = dispersionMilliradians * 0.001f;
			return (forward + right * (horizontalSigma * dispersionRadians) + up * (verticalSigma * dispersionRadians)).normalized;
		}

		private static Vector3 ApplyShaoWeiDispersion(Vector3 forward, float dispersionMilliradians, ShaoWeiWeaponState state)
		{
			if (forward.sqrMagnitude <= 1E-06f || dispersionMilliradians <= 0f)
			{
				if (!(forward.sqrMagnitude > 1E-06f))
				{
					return Vector3.forward;
				}
				return forward.normalized;
			}
			forward.Normalize();
			NextShaoWeiGaussianPair(state, out var horizontalSigma, out var verticalSigma);
			horizontalSigma = Mathf.Clamp(horizontalSigma, -2.5f, 2.5f);
			verticalSigma = Mathf.Clamp(verticalSigma, -2.5f, 2.5f);
			Vector3 right = Vector3.Cross((Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f) ? Vector3.right : Vector3.up, forward).normalized;
			Vector3 up = Vector3.Cross(forward, right).normalized;
			float dispersionRadians = dispersionMilliradians * 0.001f;
			return (forward + right * (horizontalSigma * dispersionRadians) + up * (verticalSigma * dispersionRadians)).normalized;
		}

		private static void NextShaoWeiGaussianPair(ShaoWeiWeaponState state, out float first, out float second)
		{
			float firstUniform = Mathf.Max(NextShaoWeiUniform(state), 1E-06f);
			float secondUniform = NextShaoWeiUniform(state);
			float radius = Mathf.Sqrt(-2f * Mathf.Log(firstUniform));
			float angle = MathF.PI * 2f * secondUniform;
			first = radius * Mathf.Cos(angle);
			second = radius * Mathf.Sin(angle);
		}

		private static float NextShaoWeiUniform(ShaoWeiWeaponState state)
		{
			uint randomState = state.DispersionRandomState;
			randomState ^= randomState << 13;
			randomState ^= randomState >> 17;
			randomState ^= randomState << 5;
			state.DispersionRandomState = ((randomState == 0) ? 1u : randomState);
			return (float)(state.DispersionRandomState & 0xFFFFFF) / 16777216f;
		}

		private void NextRifleGaussianPair(out float first, out float second)
		{
			float firstUniform = Mathf.Max(NextRifleUniform(), 1E-06f);
			float secondUniform = NextRifleUniform();
			float radius = Mathf.Sqrt(-2f * Mathf.Log(firstUniform));
			float angle = MathF.PI * 2f * secondUniform;
			first = radius * Mathf.Cos(angle);
			second = radius * Mathf.Sin(angle);
		}

		private float NextRifleUniform()
		{
			uint state = rifleDispersionRandomState;
			state ^= state << 13;
			state ^= state >> 17;
			state ^= state << 5;
			rifleDispersionRandomState = ((state == 0) ? 1u : state);
			return (float)(rifleDispersionRandomState & 0xFFFFFF) / 16777216f;
		}

		private void UpdateReloadState()
		{
			UpdateHandReloadState(leftHandWeaponState);
			UpdateHandReloadState(rightHandWeaponState);
		}

		private void UpdateHandReloadState(HandWeaponState state)
		{
			if (state.Reloading && Time.time >= state.ReloadCompleteTime)
			{
				state.Reloading = false;
				if (state.RoundsRemaining == 0 && state.MagazinesRemaining > 0)
				{
					state.RoundsRemaining = GetHandMagazineCapacity(state);
				}
			}
		}

		private void BeginHandReload(MechPartSlot slot, HandWeaponState state, Vector3 weaponPosition)
		{
			if (state.Reloading || state.MagazinesRemaining <= 0)
			{
				return;
			}
			state.Reloading = true;
			state.ReloadCompleteTime = Time.time + GetHandReloadDuration(state);
			if (state.Role == MechWeaponRole.AutomaticCannon)
			{
				EnsureHandWeaponAudio().PlayRifleReload(weaponPosition, IsOwnship());
			}
			else if (state.Role == MechWeaponRole.HandCannon)
			{
				EnsureHandWeaponAudio().PlayCannonReload(
					weaponPosition,
					IsOwnship(),
					ResolveCannonAudioProfile(slot));
			}
		}

		private void PlayHandWeaponAudio(TestWeaponKind kind, MechPartSlot slot, Transform muzzle)
		{
			MechHandWeaponAudio audio = EnsureHandWeaponAudio();
			bool ownship = IsOwnship();
			switch (kind)
			{
			case TestWeaponKind.Rifle:
				audio.SetRifleTriggerHeld(ownship && IsHandFireHeld(slot));
				audio.PlayRifleShot(muzzle.position, ownship, ResolveRifleAudioProfile(slot));
				break;
			case TestWeaponKind.Cannon:
				audio.PlayCannonShot(
					muzzle.position,
					ownship,
					ResolveCannonAudioProfile(slot));
				break;
			}
		}

		private AutomaticCannonAudioProfile ResolveRifleAudioProfile(MechPartSlot slot)
		{
			return GetEquippedWeaponPerformance(slot)?.automaticCannonAudioProfile ??
				AutomaticCannonAudioProfile.Flak38;
		}

		private CannonAudioProfile ResolveCannonAudioProfile(MechPartSlot slot)
		{
			return GetEquippedWeaponPerformance(slot)?.cannonAudioProfile ??
				CannonAudioProfile.Cannon57Mm;
		}

		private MechHandWeaponAudio EnsureHandWeaponAudio()
		{
			if ((object)handWeaponAudio == null)
			{
				handWeaponAudio = GetComponent<MechHandWeaponAudio>();
			}
			if (handWeaponAudio == null)
			{
				handWeaponAudio = base.gameObject.AddComponent<MechHandWeaponAudio>();
			}
			return handWeaponAudio;
		}

		private MechPointDefenseAudio EnsurePointDefenseAudio()
		{
			if ((object)pointDefenseAudio == null)
			{
				pointDefenseAudio = GetComponent<MechPointDefenseAudio>();
			}
			if (pointDefenseAudio == null)
			{
				pointDefenseAudio = base.gameObject.AddComponent<MechPointDefenseAudio>();
			}
			return pointDefenseAudio;
		}

		private bool IsOwnship()
		{
			if (input != null)
			{
				return GetComponent<MechAiTargetController>() == null;
			}
			return false;
		}

		private static void ConsumeMissileRound(ref int roundsRemaining, int magazineCapacity, ref int magazinesRemaining)
		{
			roundsRemaining = Mathf.Max(0, roundsRemaining - 1);
			if (roundsRemaining <= 0)
			{
				magazinesRemaining = Mathf.Max(0, magazinesRemaining - 1);
				if (magazinesRemaining > 0)
				{
					roundsRemaining = Mathf.Max(1, magazineCapacity);
				}
			}
		}

		private bool FireInterceptorMissile(MechPartSlot slot, ref int muzzleIndex)
		{
			if (GetEquippedWeaponRole(slot) != MechWeaponRole.InterceptorMissile)
			{
				return false;
			}
			InitializeAmmunition();
			MechWeaponPerformance weaponPerformance = GetEquippedWeaponPerformance(slot);
			if (weaponPerformance == null)
			{
				return false;
			}
			MissileFlightProfile launchProfile = GetEquippedMissileProfile(slot, interceptorMissileProfile);
			if (!HasShoulderMissileAmmunition(slot))
			{
				return false;
			}
			UpdateInterceptorTarget();
			MissileTrackSnapshot incomingTrack = interceptorTarget;
			if (!incomingTrack.TrackId.IsValid || !incomingTrack.IsWeaponsQuality || incomingTrack.IsFriendly || !incomingTrack.Incoming)
			{
				Debug.LogWarning("[MechTestWeaponSystem] Interceptor launch inhibited: no sensor-confirmed incoming missile track.", this);
				return false;
			}
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
			{
				Debug.LogWarning("[MechTestWeaponSystem] No interceptor muzzle.", this);
				return false;
			}
			Transform muzzle = muzzles[muzzleIndex % muzzles.Length];
			if (!TrySolveInterceptorEngagement(in incomingTrack, muzzle, out var launchSolution))
			{
				Debug.LogWarning("[MechTestWeaponSystem] Interceptor launch inhibited: the current threat geometry is outside the dynamic interceptor reachability zone.", this);
				return false;
			}
			interceptorSolution = launchSolution;
			Vector3 launchDirection = ResolveInterceptorColdLaunchDirection(muzzle);
			Vector3 launchPosition = muzzle.position + launchDirection * missileLaunchClearanceMeters;
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			InterceptorEngagementSolution refinedSolution = SolveInterceptorEngagement(in incomingTrack, launchPosition, inheritedVelocity, launchDirection, radarController.SimTimeSeconds);
			if (refinedSolution.Reachable)
			{
				launchSolution = refinedSolution;
				interceptorSolution = refinedSolution;
			}
			SpawnProjectile(launchPosition, launchDirection, TestWeaponKind.Missile, launchProfile.EjectionSpeedMps, weaponPerformance.directHitDamage, 0.22f, launchProfile.MaximumFlightTimeSeconds, incomingTrack.TrackId, 0f, 0f, 0f, 0, 6082101, 0, muzzle, launchProfile.MaximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, weaponPerformance.proximityFuseRadiusMeters, weaponPerformance.proximityFuseArmSeconds, weaponPerformance.proximityFuseSafeDistanceMeters, weaponPerformance.warheadLethalRadiusMeters, inheritedVelocity, radarController, slot, interceptorMissile: true, launchProfile, hasInitialGuidanceEstimate: true, ToVector3(incomingTrack.PredictPosition(radarController.SimTimeSeconds)), ToVector3(incomingTrack.Velocity), incomingTrack.PositionUncertaintyMeters, ToVector3(launchSolution.InterceptPoint), coldLaunch: true, interceptorSafeSeparationSeconds, interceptorSafeSeparationMeters, interceptorIgnitionAlignmentDegrees, interceptorAlignmentTimeoutSeconds);
			ConsumeShoulderMissileRound(slot);
			Debug.Log("[MechTestWeaponSystem] Interceptor launched at " + $"{incomingTrack.TrackId}, sources=" + $"{incomingTrack.SourceMask}, confidence=" + $"{incomingTrack.Confidence:0.00}, TOF=" + $"{launchSolution.FlightTimeSeconds:0.00}s, margin=" + $"{launchSolution.ThreatTimeMarginSeconds:0.00}s.", this);
			muzzleIndex = (muzzleIndex + 1) % muzzles.Length;
			interceptorTarget = default(MissileTrackSnapshot);
			interceptorSolution = default(InterceptorEngagementSolution);
			interceptorLockUntil = 0f;
			return true;
		}

		private bool FireMissileSalvo(MechPartSlot slot, ref int muzzleIndex)
		{
			if (GetEquippedWeaponRole(slot) != MechWeaponRole.AirToAirMissile)
			{
				return false;
			}
			InitializeAmmunition();
			MechWeaponPerformance weaponPerformance = GetEquippedWeaponPerformance(slot);
			if (weaponPerformance == null)
			{
				return false;
			}
			MissileFlightProfile launchProfile = GetEquippedMissileProfile(slot, airToAirMissileProfile);
			if (!HasShoulderMissileAmmunition(slot))
			{
				return false;
			}
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
			{
				Debug.LogWarning($"[MechTestWeaponSystem] No missile muzzle for {slot}.", this);
				return false;
			}
			MissileKinematicProfile missileKinematics = BuildMissileKinematics(launchProfile);
			if (radarController == null || !radarController.TryAuthorizeMissileLaunch(MissileGuidanceMode.ActiveRadar, missileLockDegrees, in missileKinematics, TrackObjectClass.AirVehicle, out var track, out var solution, out var guidanceToken))
			{
				Debug.LogWarning($"[MechTestWeaponSystem] {slot} launch inhibited: no authorized radar track, " + "weapon-envelope solution, seeker handoff, or free guidance channel.", this);
				return false;
			}
			TrackId trackId = track.TrackId;
			float seekerActivationDelay = Mathf.Clamp((float)(solution.SeekerActivationTime - radarController.SimTimeSeconds), 0.5f, 8f);
			launchProfile.SeekerActivationDelaySeconds = seekerActivationDelay;
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Debug.Log($"[MechTestWeaponSystem] Missile launch authorized: slot={slot}, track={trackId}, " + $"channel={guidanceToken}, seeker={seekerActivationDelay:0.0}s.", this);
			int launchedCount = 0;
			for (int i = 0; i < 1; i++)
			{
				if (i > 0 && !radarController.RetainGuidanceChannel(guidanceToken))
				{
					Debug.LogWarning($"[MechTestWeaponSystem] {slot} salvo truncated: guidance channel was released.", this);
					break;
				}
				Transform muzzle = muzzles[(muzzleIndex + i) % muzzles.Length];
				Vector3 clearanceDirection = ResolveMissileLaunchDirection(muzzle);
				Vector3 launchDirection = ResolveGuidedMissileLaunchDirection(muzzle, ToVector3(solution.InterceptPoint), 65f);
				Vector3 launchPosition = muzzle.position + clearanceDirection * missileLaunchClearanceMeters;
				SpawnProjectile(launchPosition, launchDirection, TestWeaponKind.Missile, launchProfile.EjectionSpeedMps, weaponPerformance.directHitDamage, 0.22f, launchProfile.MaximumFlightTimeSeconds, trackId, (float)i * 0.06f, 0f, 0f, (slot == MechPartSlot.LeftBackWeapon) ? 6082100 : 6083100, (slot == MechPartSlot.LeftBackWeapon) ? 6082101 : 6083101, 0, muzzle, launchProfile.MaximumRangeMeters, guidanceToken, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, weaponPerformance.proximityFuseRadiusMeters, weaponPerformance.proximityFuseArmSeconds, weaponPerformance.proximityFuseSafeDistanceMeters, weaponPerformance.warheadLethalRadiusMeters, inheritedVelocity, null, slot, interceptorMissile: false, launchProfile);
				ConsumeShoulderMissileRound(slot);
				launchedCount++;
			}
			muzzleIndex = (muzzleIndex + launchedCount) % muzzles.Length;
			return launchedCount > 0;
		}

		private void UpdateInterceptorTarget()
		{
			MissileTrackSnapshot candidate;
			InterceptorEngagementSolution candidateSolution;
			MissileTrackSnapshot retainedTrack;
			Transform retainedMuzzle;
			InterceptorEngagementSolution retainedSolution;
			if (!TryGetWeaponSlot(MechWeaponRole.InterceptorMissile, out _))
			{
				interceptorTarget = default(MissileTrackSnapshot);
				interceptorSolution = default(InterceptorEngagementSolution);
			}
			else if (TryFindBestIncomingMissile(out candidate, out candidateSolution))
			{
				interceptorTarget = candidate;
				interceptorSolution = candidateSolution;
				interceptorLockUntil = Time.unscaledTime + interceptorLockHoldSeconds;
			}
			else if (!interceptorTarget.TrackId.IsValid || radarController == null || !radarController.TryGetMissileTrack(interceptorTarget.TrackId, out retainedTrack) || !retainedTrack.IsWeaponsQuality || retainedTrack.IsFriendly || !retainedTrack.Incoming || !TryPeekInterceptorMuzzle(out retainedMuzzle) || !TrySolveInterceptorEngagement(in retainedTrack, retainedMuzzle, out retainedSolution) || Time.unscaledTime >= interceptorLockUntil)
			{
				interceptorTarget = default(MissileTrackSnapshot);
				interceptorSolution = default(InterceptorEngagementSolution);
			}
			else
			{
				interceptorTarget = retainedTrack;
				interceptorSolution = retainedSolution;
			}
		}

		private bool TryFindBestIncomingMissile(out MissileTrackSnapshot best, out InterceptorEngagementSolution bestSolution)
		{
			best = default(MissileTrackSnapshot);
			bestSolution = default(InterceptorEngagementSolution);
			if (radarController == null || !TryPeekInterceptorMuzzle(out var muzzle))
			{
				return false;
			}
			int trackCount = radarController.CopyMissileTracks(interceptorTrackScratch);
			float bestScore = float.PositiveInfinity;
			for (int i = 0; i < trackCount; i++)
			{
				MissileTrackSnapshot candidate = interceptorTrackScratch[i];
				if (candidate.TrackId.IsValid && candidate.IsWeaponsQuality && !candidate.IsFriendly && candidate.Incoming && MissileEngagementMath.IntersectsProtectedCpaWindow(candidate.TimeToClosestApproachSeconds, candidate.ClosestApproachDistanceMeters, candidate.CpaUncertaintyMeters, interceptorPredictionHorizonSeconds, interceptorProtectedRadiusMeters) && TrySolveInterceptorEngagement(in candidate, muzzle, out var solution))
				{
					float score = solution.FlightTimeSeconds + candidate.ClosestApproachDistanceMeters / Mathf.Max(interceptorProtectedRadiusMeters, 1f) + candidate.PositionUncertaintyMeters / 1000f - candidate.Confidence * 0.5f - Mathf.Clamp(solution.ThreatTimeMarginSeconds, 0f, 10f) * 0.05f;
					if (score < bestScore)
					{
						best = candidate;
						bestSolution = solution;
						bestScore = score;
					}
				}
			}
			return best.TrackId.IsValid;
		}

		private bool TryPeekInterceptorMuzzle(out Transform muzzle)
		{
			if (!TryGetWeaponSlot(MechWeaponRole.InterceptorMissile, out var slot))
			{
				muzzle = null;
				return false;
			}
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
			{
				muzzle = null;
				return false;
			}
			muzzle = muzzles[GetShoulderMuzzleIndex(slot) % muzzles.Length];
			return muzzle != null;
		}

		private bool TrySolveInterceptorEngagement(in MissileTrackSnapshot track, Transform muzzle, out InterceptorEngagementSolution solution)
		{
			solution = default(InterceptorEngagementSolution);
			if (radarController == null || muzzle == null)
			{
				return false;
			}
			Vector3 launchDirection = ResolveInterceptorColdLaunchDirection(muzzle);
			Vector3 launchPosition = muzzle.position + launchDirection * missileLaunchClearanceMeters;
			Vector3 ownshipVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			solution = SolveInterceptorEngagement(in track, launchPosition, ownshipVelocity, launchDirection, radarController.SimTimeSeconds);
			return solution.Reachable;
		}

		private InterceptorEngagementSolution SolveInterceptorEngagement(in MissileTrackSnapshot track, Vector3 launchPosition, Vector3 ownshipVelocity, Vector3 launchDirection, double requestTime)
		{
			MissileFlightProfile profile = GetInstalledMissileProfile(MechWeaponRole.InterceptorMissile, interceptorMissileProfile);
			MissileKinematicProfile kinematics = BuildInterceptorKinematics(profile);
			return FireSolutionSolver.SolveInterceptor(in track, ToDouble3(launchPosition), ToDouble3(ownshipVelocity), new float3(launchDirection.x, launchDirection.y, launchDirection.z), in kinematics, requestTime, profile.MaximumAngularRateDegreesPerSecond, profile.MaximumFlightTimeSeconds, profile.MaximumRangeMeters, interceptorResponseReserveSeconds, EstimateInterceptorColdLaunchDelaySeconds(profile));
		}

		private Vector3 ResolveMissileLaunchDirection(Transform muzzle)
		{
			Vector3 bodyForward = ((flightMotion != null) ? (flightMotion.LogicalRotation * Vector3.forward) : base.transform.forward);
			if (bodyForward.sqrMagnitude <= 0.001f)
			{
				bodyForward = Vector3.forward;
			}
			bodyForward.Normalize();
			Vector3[] candidates = new Vector3[6]
			{
				muzzle.forward,
				-muzzle.forward,
				muzzle.up,
				-muzzle.up,
				muzzle.right,
				-muzzle.right
			};
			Vector3 launchDirection = bodyForward;
			float bestAlignment = 0.5f;
			for (int i = 0; i < candidates.Length; i++)
			{
				Vector3 candidate = candidates[i].normalized;
				float alignment = Vector3.Dot(candidate, bodyForward);
				if (alignment > bestAlignment)
				{
					bestAlignment = alignment;
					launchDirection = candidate;
				}
			}
			return launchDirection.normalized;
		}

		private Vector3 ResolveInterceptorColdLaunchDirection(Transform muzzle)
		{
			Vector3 bodyUp = ((flightMotion != null) ? (flightMotion.LogicalRotation * Vector3.up) : base.transform.up);
			if (bodyUp.sqrMagnitude <= 0.001f)
			{
				bodyUp = Vector3.up;
			}
			bodyUp.Normalize();
			if (muzzle == null)
			{
				return bodyUp;
			}
			Vector3[] candidates = new Vector3[6]
			{
				muzzle.forward,
				-muzzle.forward,
				muzzle.up,
				-muzzle.up,
				muzzle.right,
				-muzzle.right
			};
			Vector3 launchDirection = bodyUp;
			float bestAlignment = float.NegativeInfinity;
			for (int i = 0; i < candidates.Length; i++)
			{
				Vector3 candidate = candidates[i].normalized;
				float alignment = Vector3.Dot(candidate, bodyUp);
				if (alignment > bestAlignment)
				{
					bestAlignment = alignment;
					launchDirection = candidate;
				}
			}
			return launchDirection.normalized;
		}

		private float EstimateInterceptorColdLaunchDelaySeconds(MissileFlightProfile profile)
		{
			float ejectionSpeed = Mathf.Max(profile.EjectionSpeedMps, 0.01f);
			return Mathf.Max(interceptorSafeSeparationSeconds, interceptorSafeSeparationMeters / ejectionSpeed);
		}

		private Vector3 ResolveGuidedMissileLaunchDirection(Transform muzzle, Vector3 interceptPoint, float maximumDeflectionDegrees)
		{
			Vector3 clearanceDirection = ResolveMissileLaunchDirection(muzzle);
			if (muzzle == null)
			{
				return clearanceDirection;
			}
			Vector3 toIntercept = interceptPoint - muzzle.position;
			if (toIntercept.sqrMagnitude <= 0.001f)
			{
				return clearanceDirection;
			}
			return Vector3.RotateTowards(clearanceDirection, toIntercept.normalized, Mathf.Max(0f, maximumDeflectionDegrees) * (MathF.PI / 180f), 0f).normalized;
		}

		private Transform GetNextMuzzle(MechPartSlot slot, ref int index)
		{
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
			{
				return null;
			}
			Transform result = muzzles[index % muzzles.Length];
			index = (index + 1) % muzzles.Length;
			return result;
		}

		private void SpawnProjectile(Vector3 position, Vector3 direction, TestWeaponKind kind, float speed, float damage, float radius, float lifetime, TrackId homingTrack = default(TrackId), float launchDelay = 0f, float acceleration = 0f, float turnRateDegrees = 0f, int launchFfxId = 0, int projectileFfxId = 0, int impactFfxId = 0, Transform launchAnchor = null, float maximumTravelDistance = 0f, int guidanceChannelToken = 0, float motorBurnDuration = 2.1f, float seekerActivationDelay = 2.5f, float seekerMaximumRange = 8000f, float seekerHalfAngleDegrees = 35f, float seekerGimbalDegrees = 60f, float maximumLateralAcceleration = 75f, float maneuverDeltaV = 180f, float rcsLateralAcceleration = 0f, float rcsDeltaV = 0f, float proximityFuseRadius = 12f, float proximityFuseArmTime = 0.75f, float proximityFuseSafeDistance = 20f, float warheadLethalRadius = 18f, Vector3 inheritedVelocity = default(Vector3), MechRadarController simulationRadarController = null, MechPartSlot missileSourceSlot = MechPartSlot.None, bool interceptorMissile = false, MissileFlightProfile missileProfile = default(MissileFlightProfile), bool hasInitialGuidanceEstimate = false, Vector3 initialGuidancePosition = default(Vector3), Vector3 initialGuidanceVelocity = default(Vector3), float initialGuidanceUncertaintyMeters = 0f, Vector3 prelaunchInterceptPoint = default(Vector3), bool coldLaunch = false, float coldLaunchSafeSeparationSeconds = 0f, float coldLaunchSafeSeparationMeters = 0f, float coldLaunchIgnitionAlignmentDegrees = 8f, float coldLaunchAlignmentTimeoutSeconds = 0.6f, float interceptorRoundLethalRadius = 0f, float interceptorRoundArmSeconds = 0f, float interceptorRoundSafeDistance = 0f, bool cannonProximityFuseEnabled = false, float automaticRoundsPerMinute = 0f)
		{
			EnsureMaterials();
			bool ownshipMissileAudio = kind == TestWeaponKind.Missile && input != null && GetComponent<MechAiTargetController>() == null;
			if (ownshipMissileAudio)
			{
				if ((object)missileLaunchAudio == null)
				{
					missileLaunchAudio = GetComponent<MechMissileLaunchAudio>();
				}
				if (missileLaunchAudio == null)
				{
					missileLaunchAudio = base.gameObject.AddComponent<MechMissileLaunchAudio>();
				}
				missileLaunchAudio.PlayRelease(coldLaunch);
			}
			SpawnNativeOneShot(launchFfxId, launchAnchor, (launchAnchor != null) ? launchAnchor.position : position, direction, kind != TestWeaponKind.Missile, ResolveMuzzleFlashVisibleSeconds(kind, automaticRoundsPerMinute));
			GameObject projectile = GameObject.CreatePrimitive((kind == TestWeaponKind.Missile) ? PrimitiveType.Cube : PrimitiveType.Sphere);
			projectile.name = $"Test_{kind}_Projectile";
			Vector3 upReference = ((Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.95f) ? Vector3.forward : Vector3.up);
			projectile.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, upReference));
			Transform transform = projectile.transform;
			transform.localScale = kind switch
			{
				TestWeaponKind.Rifle => Vector3.one * 0.12f, 
				TestWeaponKind.PointDefenseCannon => Vector3.one * 0.12f,
				TestWeaponKind.Cannon => Vector3.one * 0.32f, 
				_ => new Vector3(0.18f, 0.18f, 0.8f), 
			};
			Collider collider = projectile.GetComponent<Collider>();
			if (kind == TestWeaponKind.Missile)
			{
				ConfigureMissileHitbox(collider, projectile.transform.localScale, interceptorMissile);
			}
			else if (collider != null)
			{
				UnityEngine.Object.Destroy(collider);
			}
			Renderer placeholderRenderer = projectile.GetComponent<Renderer>();
			placeholderRenderer.sharedMaterial = GetProjectileMaterial(kind);
			if (kind == TestWeaponKind.Missile)
			{
				TryAttachMissileVisual(projectile.transform, placeholderRenderer, aimingCamera, interceptorMissile);
			}
			switch (kind)
			{
			case TestWeaponKind.Rifle:
				placeholderRenderer.enabled = false;
				projectile.AddComponent<MechTracerRound>().Build(new Color(1f, 0.93f, 0.72f), 0.3f, 0.17f, 0.07f);
				break;
			case TestWeaponKind.PointDefenseCannon:
				placeholderRenderer.enabled = false;
				projectile.AddComponent<MechTracerRound>().Build(new Color(1f, 0.93f, 0.72f), 0.3f, 0.17f, 0.07f);
				break;
			case TestWeaponKind.Cannon:
				placeholderRenderer.enabled = false;
				projectile.AddComponent<MechTracerRound>().Build(new Color(1f, 0.74f, 0.36f), 0.85f, 0.5f, 0.16f);
				break;
			}
			MechTestProjectile projectileBehaviour = projectile.AddComponent<MechTestProjectile>();
			if (interceptorRoundLethalRadius > 0f)
			{
				projectileBehaviour.ConfigureInterceptorRound(interceptorRoundLethalRadius, interceptorRoundArmSeconds, interceptorRoundSafeDistance);
			}
			projectileBehaviour.Configure(base.transform, (simulationRadarController != null) ? simulationRadarController : radarController, homingTrack, kind, inheritedVelocity + direction * speed, damage, radius, lifetime, launchDelay, acceleration, turnRateDegrees, nativeEffectLibrary, impactFfxId, maximumTravelDistance, guidanceChannelToken, motorBurnDuration, seekerActivationDelay, seekerMaximumRange, seekerHalfAngleDegrees, seekerGimbalDegrees, maximumLateralAcceleration, maneuverDeltaV, rcsLateralAcceleration, rcsDeltaV, proximityFuseRadius, proximityFuseArmTime, proximityFuseSafeDistance, warheadLethalRadius, interceptorMissile, missileProfile, hasInitialGuidanceEstimate, initialGuidancePosition, initialGuidanceVelocity, initialGuidanceUncertaintyMeters, prelaunchInterceptPoint, coldLaunch, inheritedVelocity, coldLaunchSafeSeparationSeconds, coldLaunchSafeSeparationMeters, coldLaunchIgnitionAlignmentDegrees, coldLaunchAlignmentTimeoutSeconds, ownshipMissileAudio);
			if ((kind == TestWeaponKind.Cannon || kind == TestWeaponKind.Rifle) && cannonProximityFuseEnabled)
			{
				projectileBehaviour.ConfigureCannonProximityFuse(proximityFuseRadius, proximityFuseArmTime, proximityFuseSafeDistance, warheadLethalRadius);
			}
		}

		private bool TryGetAimPoint(float coneDegrees, float projectileSpeed, float maximumFlightTimeSeconds, out Vector3 aimPoint, out TrackId trackId)
		{
			Ray ray = GetAimRay();
			if (radarController != null && radarController.TryGetFireSolution(coneDegrees, projectileSpeed, maximumFlightTimeSeconds, out var track, out var solution))
			{
				aimPoint = new Vector3((float)solution.MidcourseAimPoint.x, (float)solution.MidcourseAimPoint.y, (float)solution.MidcourseAimPoint.z);
				trackId = track.TrackId;
				return true;
			}
			aimPoint = ray.origin + ray.direction * 100000f;
			trackId = default(TrackId);
			return false;
		}

		private void UpdatePointDefenseTarget()
		{
			if (!handPointDefensePriorityEnabled)
			{
				pointDefenseTarget = default;
				pointDefenseHitProbability = 0f;
				pointDefenseWithholdReason = PointDefenseWithholdReason.None;
				radarController?.CuePointDefenseTrack(default);
				return;
			}

			Ray aimRay = GetAimRay();
			MissileTrackSnapshot retainedTrack;
			if (TryFindBestPointDefenseTarget(aimRay, pointDefenseLockDegrees, pointDefenseMaximumRangeMeters, out var candidate))
			{
				pointDefenseTarget = candidate;
				pointDefenseLockUntil = Time.unscaledTime + pointDefenseLockHoldSeconds;
				radarController.CuePointDefenseTrack(candidate.TrackId);
			}
			else if (!pointDefenseTarget.TrackId.IsValid || radarController == null || !radarController.TryGetMissileTrack(pointDefenseTarget.TrackId, out retainedTrack) || !retainedTrack.IsWeaponsQuality || retainedTrack.IsFriendly || !retainedTrack.Incoming || Time.unscaledTime >= pointDefenseLockUntil)
			{
				pointDefenseTarget = default(MissileTrackSnapshot);
				radarController?.CuePointDefenseTrack(default(TrackId));
			}
			else
			{
				pointDefenseTarget = retainedTrack;
				radarController.CuePointDefenseTrack(retainedTrack.TrackId);
			}
		}

		private void UpdateShaoWeiPointDefenseTarget()
		{
			MechWeaponPerformance performance = GetShaoWeiReferencePerformance();
			if (performance == null || !IsShaoWeiPlatformOperational() || !enableShaoWeiPointDefense || (!LeftShaoWeiInstalled && !RightShaoWeiInstalled))
			{
				shaoWeiPointDefenseTarget = default(MissileTrackSnapshot);
				shaoWeiPointDefenseHitProbability = 0f;
				shaoWeiPointDefenseWithholdReason = PointDefenseWithholdReason.None;
				return;
			}
			Vector3 origin = base.transform.position + Vector3.up * 2f;
			Vector3 forward = ((flightMotion != null) ? (flightMotion.LogicalRotation * Vector3.forward) : base.transform.forward);
			forward = ((forward.sqrMagnitude > 0.001f) ? forward.normalized : Vector3.forward);
			MissileTrackSnapshot retainedTrack = default(MissileTrackSnapshot);
			bool canRetainCurrent = shaoWeiPointDefenseTarget.TrackId.IsValid
				&& radarController != null
				&& radarController.TryGetMissileTrack(shaoWeiPointDefenseTarget.TrackId, out retainedTrack)
				&& IsShaoWeiThreatEligible(in retainedTrack, origin, forward);
			if (canRetainCurrent && Time.unscaledTime < shaoWeiPointDefenseLockUntil)
			{
				shaoWeiPointDefenseTarget = retainedTrack;
			}
			else if (TryFindBestShaoWeiPointDefenseTarget(origin, forward, out var candidate))
			{
				shaoWeiPointDefenseTarget = candidate;
				shaoWeiPointDefenseLockUntil = Time.unscaledTime + performance.trackHoldSeconds;
			}
			else if (!canRetainCurrent)
			{
				shaoWeiPointDefenseTarget = default(MissileTrackSnapshot);
				shaoWeiPointDefenseHitProbability = 0f;
				shaoWeiPointDefenseWithholdReason = PointDefenseWithholdReason.None;
			}
			else
			{
				shaoWeiPointDefenseTarget = retainedTrack;
			}
		}

		private bool TryFindBestShaoWeiPointDefenseTarget(Vector3 origin, Vector3 forward, out MissileTrackSnapshot best)
		{
			best = default(MissileTrackSnapshot);
			MechWeaponPerformance performance = GetShaoWeiReferencePerformance();
			if (radarController == null || performance == null)
			{
				return false;
			}
			int trackCount = radarController.CopyMissileTracks(interceptorTrackScratch);
			float bestScore = float.MaxValue;
			for (int i = 0; i < trackCount; i++)
			{
				MissileTrackSnapshot candidate = interceptorTrackScratch[i];
				if (IsShaoWeiThreatEligible(in candidate, origin, forward))
				{
					double3 predictedPosition = candidate.PredictPosition(radarController.SimTimeSeconds);
					Vector3 position = new Vector3((float)predictedPosition.x, (float)predictedPosition.y, (float)predictedPosition.z);
					float range = Vector3.Distance(origin, position);
					float score = Mathf.Max(0f, candidate.TimeToClosestApproachSeconds) + Mathf.Max(0f, candidate.ClosestApproachDistanceMeters) / 1000f + Mathf.Max(0f, candidate.CpaUncertaintyMeters) / 500f + range / Mathf.Max(1f, performance.maximumRangeMeters) * 0.25f - candidate.Confidence * 0.1f;
					if (score < bestScore)
					{
						best = candidate;
						bestScore = score;
					}
				}
			}
			return best.TrackId.IsValid;
		}

		private bool IsShaoWeiThreatEligible(in MissileTrackSnapshot candidate, Vector3 origin, Vector3 forward)
		{
			MechWeaponPerformance performance = GetShaoWeiReferencePerformance();
			if (performance == null || !candidate.TrackId.IsValid || (performance.requiresWeaponsQualityTrack && !candidate.IsWeaponsQuality) || candidate.IsFriendly || (performance.incomingTargetsOnly && !candidate.Incoming) || candidate.TimeToClosestApproachSeconds < 0f || candidate.TimeToClosestApproachSeconds > performance.maximumThreatTimeSeconds)
			{
				return false;
			}
			double3 predictedPosition = candidate.PredictPosition((radarController != null) ? radarController.SimTimeSeconds : candidate.LastUpdateTime);
			Vector3 toCandidate = new Vector3((float)predictedPosition.x, (float)predictedPosition.y, (float)predictedPosition.z) - origin;
			float rangeSquared = toCandidate.sqrMagnitude;
			if (rangeSquared <= 0.01f || rangeSquared > performance.maximumRangeMeters * performance.maximumRangeMeters)
			{
				return false;
			}
			return Vector3.Dot(forward, toCandidate / Mathf.Sqrt(rangeSquared)) >= Mathf.Cos(performance.maximumOffBoresightDegrees * (MathF.PI / 180f));
		}

		private MechWeaponPerformance GetShaoWeiReferencePerformance()
		{
			return leftShaoWeiState.Performance ?? rightShaoWeiState.Performance;
		}

		private bool IsShaoWeiPlatformOperational()
		{
			if (ownCombatTarget == null)
			{
				ownCombatTarget = GetComponent<CombatTestTarget>();
			}
			if (!(ownCombatTarget == null))
			{
				return ownCombatTarget.IsAlive;
			}
			return true;
		}

		private void UpdateShaoWeiPointDefense()
		{
			shaoWeiPointDefenseHitProbability = 0f;
			shaoWeiPointDefenseWithholdReason = PointDefenseWithholdReason.None;
			UpdateShaoWeiPointDefenseSlot(MechPartSlot.LeftBackWeapon, leftShaoWeiState);
			UpdateShaoWeiPointDefenseSlot(MechPartSlot.RightBackWeapon, rightShaoWeiState);
		}

		private void UpdateShaoWeiPointDefenseSlot(MechPartSlot slot, ShaoWeiWeaponState state)
		{
			MechWeaponPerformance performance = state.Performance;
			bool installed = IsShaoWeiEquipped(slot);
			bool hasTarget = shaoWeiPointDefenseTarget.TrackId.IsValid;
			bool available = performance != null && enableShaoWeiPointDefense && IsShaoWeiPlatformOperational() && installed && hasTarget && state.RoundsRemaining > 0 && !state.Reloading && !state.Overheated;
			SetShaoWeiSpinning(slot, state, available);
			if (!available)
			{
				state.HasAimPoint = false;
				return;
			}
			Transform muzzle = PeekShaoWeiMuzzle(slot);
			if (muzzle == null)
			{
				ResolveMuzzles();
				muzzle = PeekShaoWeiMuzzle(slot);
				if (muzzle == null)
				{
					return;
				}
			}
			float heatFraction = Mathf.Clamp01(state.HeatMj / Mathf.Max(0.1f, performance.heatCapacityMj));
			float dispersion = Mathf.Lerp(performance.baseDispersionMilliradians, performance.maximumDispersionMilliradians, heatFraction);
			Vector3 aimPoint;
			float hitProbability;
			PointDefenseWithholdReason withholdReason;
			bool num = TryGetShaoWeiPointDefenseAimPoint(in shaoWeiPointDefenseTarget, muzzle.position, performance.muzzleVelocityMetersPerSecond, performance.maximumRangeMeters, performance.maximumLeadSeconds, dispersion, performance.pointDefenseRoundLethalRadiusMeters, performance.minimumHitProbability, out aimPoint, out hitProbability, out withholdReason);
			if (aimPoint.sqrMagnitude > 0.001f)
			{
				state.AimPoint = aimPoint;
				state.HasAimPoint = true;
				UpdateShaoWeiServo(slot, state, Time.deltaTime);
			}
			if (hitProbability >= shaoWeiPointDefenseHitProbability)
			{
				shaoWeiPointDefenseHitProbability = hitProbability;
				shaoWeiPointDefenseWithholdReason = withholdReason;
			}
			if (!num || Time.time < state.SpinReadyTime || Time.time < state.NextFireTime)
			{
				return;
			}
			Vector3 desiredDirection = (aimPoint - muzzle.position).normalized;
			if (desiredDirection.sqrMagnitude <= 0.001f)
			{
				return;
			}
			Vector3 actualDirection = state.HasSlewedDirection
				? state.SlewedDirection
				: ((state.AimPivot != null) ? (muzzle.position - state.AimPivot.position) : muzzle.forward);
			if (actualDirection.sqrMagnitude <= 0.001f)
			{
				return;
			}
			actualDirection.Normalize();
			if (Vector3.Angle(actualDirection, desiredDirection) > 2.5f)
			{
				return;
			}
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Vector3 firingDirection = ApplyShaoWeiDispersion(actualDirection, dispersion, state);
			Vector3 position = muzzle.position;
			float speed = performance.muzzleVelocityMetersPerSecond;
			float damage = performance.directHitDamage;
			float lifetime = performance.projectileLifetimeSeconds;
			Transform launchAnchor = muzzle;
			Vector3 inheritedVelocity2 = inheritedVelocity;
			float interceptorRoundLethalRadius = performance.pointDefenseRoundLethalRadiusMeters;
			float interceptorRoundArmSeconds = performance.pointDefenseRoundArmSeconds;
			float interceptorRoundSafeDistance = performance.pointDefenseRoundSafeDistanceMeters;
			SpawnProjectile(position, firingDirection, TestWeaponKind.PointDefenseCannon, speed, damage, 0.08f, lifetime, default(TrackId), 0f, 0f, 0f, 6011100, 6013101, 6011101, launchAnchor, performance.maximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity2, null, MechPartSlot.None, interceptorMissile: false, default(MissileFlightProfile), hasInitialGuidanceEstimate: false, default(Vector3), default(Vector3), 0f, default(Vector3), coldLaunch: false, 0f, 0f, 8f, 0.6f, interceptorRoundLethalRadius, interceptorRoundArmSeconds, interceptorRoundSafeDistance, cannonProximityFuseEnabled: false, automaticRoundsPerMinute: performance.roundsPerMinute);
			EnsurePointDefenseAudio().PlayShot(
				slot,
				muzzle,
				performance.roundsPerMinute);
			playerRig?.NotifyShoulderWeaponFired(slot);
			state.RoundsRemaining = Mathf.Max(0, state.RoundsRemaining - 1);
			state.HeatMj = Mathf.Min(performance.heatCapacityMj, state.HeatMj + performance.heatPerShotMj);
			state.NextFireTime = Time.time + 60f / Mathf.Max(60f, performance.roundsPerMinute);
			if (state.RoundsRemaining == 0)
			{
				state.MagazinesRemaining = Mathf.Max(0, state.MagazinesRemaining - 1);
				if (state.MagazinesRemaining > 0)
				{
					BeginShaoWeiReload(state);
				}
			}
			if (state.HeatMj >= performance.heatCapacityMj - 0.0001f)
			{
				state.Overheated = true;
				SetShaoWeiSpinning(slot, state, shouldSpin: false);
			}
		}

		private void SetShaoWeiSpinning(MechPartSlot slot, ShaoWeiWeaponState state, bool shouldSpin)
		{
			MechWeaponPerformance performance = state.Performance;
			MechEquipmentAnimationPlayer player = state.AnimationPlayer;
			if (player == null && shouldSpin)
			{
				ResolveMuzzles();
				player = state.AnimationPlayer;
			}
			if (shouldSpin)
			{
				if (!state.Spinning)
				{
					state.Spinning = true;
					state.SpinReadyTime = Time.time + Mathf.Max(0f, performance?.spinUpSeconds ?? 0f);
					if (player != null && player.HasAction("a000_800200"))
					{
						player.PlayActionPreservingPose("a000_800200");
					}
				}
				else if (player != null && Time.time >= state.SpinReadyTime && (!player.IsPlaying || (!string.Equals(player.ActiveActionId, "a000_800200", StringComparison.OrdinalIgnoreCase) && !string.Equals(player.ActiveActionId, "a000_800201", StringComparison.OrdinalIgnoreCase))))
				{
					player.PlayActionPreservingPose("a000_800201");
				}
			}
			else if (state.Spinning)
			{
				state.Spinning = false;
				ResetShaoWeiAimState(state);
				if (player != null && player.HasAction("a000_800202"))
				{
					player.PlayActionPreservingPose("a000_800202");
				}
			}

			MechPointDefenseAudio audio = shouldSpin
				? EnsurePointDefenseAudio()
				: pointDefenseAudio;
			audio?.SetRotorState(
				slot,
				shouldSpin,
				PeekShaoWeiMuzzle(slot),
				performance?.roundsPerMinute ?? 0f);
		}

		private Transform PeekShaoWeiMuzzle(MechPartSlot slot)
		{
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				return null;
			}
			return muzzles[0];
		}

		private bool TryFindBestPointDefenseTarget(Ray aimRay, float coneDegrees, float maximumRange, out MissileTrackSnapshot best)
		{
			best = default(MissileTrackSnapshot);
			if (radarController == null)
			{
				return false;
			}
			int trackCount = radarController.CopyMissileTracks(interceptorTrackScratch);
			float bestScore = float.MaxValue;
			float rangeLimitSquared = maximumRange * maximumRange;
			for (int i = 0; i < trackCount; i++)
			{
				MissileTrackSnapshot candidate = interceptorTrackScratch[i];
				if (!candidate.TrackId.IsValid || !candidate.IsWeaponsQuality || candidate.IsFriendly || !candidate.Incoming)
				{
					continue;
				}
				double3 predictedPosition = candidate.PredictPosition(radarController.SimTimeSeconds);
				Vector3 toCandidate = new Vector3((float)predictedPosition.x, (float)predictedPosition.y, (float)predictedPosition.z) - aimRay.origin;
				float distanceSquared = toCandidate.sqrMagnitude;
				if (distanceSquared <= 0.01f || distanceSquared > rangeLimitSquared)
				{
					continue;
				}
				Vector3 direction = toCandidate / Mathf.Sqrt(distanceSquared);
				float alignment = Vector3.Dot(aimRay.direction, direction);
				float minimumAlignment = Mathf.Cos(coneDegrees * (MathF.PI / 180f));
				if (!(alignment < minimumAlignment))
				{
					float num = 1f - alignment;
					float rangePenalty = Mathf.Sqrt(distanceSquared) / Mathf.Max(maximumRange, 1f) * 0.015f;
					float score = num + rangePenalty;
					if (score < bestScore)
					{
						best = candidate;
						bestScore = score;
					}
				}
			}
			return best.TrackId.IsValid;
		}

		private bool TryGetPointDefenseAimPoint(Vector3 shooterPosition, float projectileSpeed, float dispersionMilliradians, out Vector3 aimPoint)
		{
			aimPoint = default(Vector3);
			pointDefenseHitProbability = 0f;
			pointDefenseWithholdReason = PointDefenseWithholdReason.None;
			if (!pointDefenseTarget.TrackId.IsValid || radarController == null)
			{
				return false;
			}
			double solutionTime = radarController.SimTimeSeconds;
			double3 predictedPosition = pointDefenseTarget.PredictPosition(solutionTime);
			double3 predictedVelocity = pointDefenseTarget.PredictVelocity(solutionTime);
			double3 predictedAcceleration = pointDefenseTarget.PredictAcceleration(solutionTime);
			Vector3 targetPosition = ToVector3(predictedPosition);
			Vector3 targetVelocity = ToVector3(predictedVelocity);
			Vector3 shooterVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Vector3 relativePosition = targetPosition - shooterPosition;
			Vector3 relativeVelocity = targetVelocity - shooterVelocity;
			if (relativePosition.sqrMagnitude > pointDefenseMaximumRangeMeters * pointDefenseMaximumRangeMeters)
			{
				return false;
			}
			if (!FireSolutionSolver.TrySolveBallisticIntercept(ToDouble3(relativePosition), ToDouble3(relativeVelocity), predictedAcceleration, pointDefenseTarget.Jerk, MissileTrackFusionConfig.Default.MaximumTrackAccelerationMetersPerSecondSquared, 0.6, projectileSpeed, pointDefenseMaximumLeadSeconds, out var _, out var relativeInterceptPoint) && !FireSolutionSolver.TrySolveBallisticIntercept(ToDouble3(relativePosition), ToDouble3(relativeVelocity), double3.zero, projectileSpeed, pointDefenseMaximumLeadSeconds, out _, out relativeInterceptPoint))
			{
				return false;
			}
			float interceptRange = ToVector3(relativeInterceptPoint).magnitude;
			pointDefenseHitProbability = EstimateHandPointDefenseHitProbability(in pointDefenseTarget, interceptRange, projectileSpeed, dispersionMilliradians);
			if (pointDefenseHitProbability < ResolvePointDefenseProbabilityThreshold(
				pointDefenseMinimumHitProbability))
			{
				pointDefenseWithholdReason = ((interceptRange > pointDefenseTarget.PositionUncertaintyMeters * 400f) ? PointDefenseWithholdReason.TrackTooCoarse : PointDefenseWithholdReason.RangeTooLong);
				return false;
			}
			aimPoint = shooterPosition + ToVector3(relativeInterceptPoint);
			return true;
		}

		private float EstimateHandPointDefenseHitProbability(in MissileTrackSnapshot track, float interceptRange, float projectileSpeed, float dispersionMilliradians)
		{
			float lethalRadius = Mathf.Max(pointDefenseRoundLethalRadiusMeters, 0.32999998f);
			float flightTime = interceptRange / Mathf.Max(projectileSpeed, 1f);
			float trackSigma = Mathf.Max(0.01f, track.PositionUncertaintyMeters);
			float leadSigma = trackSigma / 0.5f * flightTime;
			float dispersionSigma = Mathf.Max(0f, dispersionMilliradians) * 0.001f * interceptRange;
			float sigmaSquared = trackSigma * trackSigma + leadSigma * leadSigma + dispersionSigma * dispersionSigma;
			if (sigmaSquared <= 1E-06f)
			{
				return 1f;
			}
			return 1f - Mathf.Exp((0f - lethalRadius * lethalRadius) / (2f * sigmaSquared));
		}

		private bool TryGetShaoWeiPointDefenseAimPoint(in MissileTrackSnapshot track, Vector3 shooterPosition, float projectileSpeed, float maximumRangeMeters, float maximumLeadSeconds, float dispersionMilliradians, float lethalRadiusMeters, float minimumHitProbability, out Vector3 aimPoint, out float hitProbability, out PointDefenseWithholdReason withholdReason)
		{
			aimPoint = default(Vector3);
			hitProbability = 0f;
			withholdReason = PointDefenseWithholdReason.None;
			if (!track.TrackId.IsValid || radarController == null)
			{
				return false;
			}
			double solutionTime = radarController.SimTimeSeconds;
			double3 predictedPosition = track.PredictPosition(solutionTime);
			double3 predictedVelocity = track.PredictVelocity(solutionTime);
			double3 predictedAcceleration = track.PredictAcceleration(solutionTime);
			Vector3 targetPosition = new Vector3((float)predictedPosition.x, (float)predictedPosition.y, (float)predictedPosition.z);
			Vector3 vector = new Vector3((float)predictedVelocity.x, (float)predictedVelocity.y, (float)predictedVelocity.z);
			Vector3 shooterVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Vector3 relativePosition = targetPosition - shooterPosition;
			Vector3 relativeVelocity = vector - shooterVelocity;
			if (relativePosition.sqrMagnitude > maximumRangeMeters * maximumRangeMeters)
			{
				withholdReason = PointDefenseWithholdReason.RangeTooLong;
				return false;
			}
			if (!FireSolutionSolver.TrySolveBallisticIntercept(ToDouble3(relativePosition), ToDouble3(relativeVelocity), predictedAcceleration, track.Jerk, MissileTrackFusionConfig.Default.MaximumTrackAccelerationMetersPerSecondSquared, 0.6, projectileSpeed, maximumLeadSeconds, out var flightTime, out var relativeInterceptPoint) && !FireSolutionSolver.TrySolveBallisticIntercept(ToDouble3(relativePosition), ToDouble3(relativeVelocity), double3.zero, projectileSpeed, maximumLeadSeconds, out flightTime, out relativeInterceptPoint))
			{
				return false;
			}
			float interceptRange = ToVector3(relativeInterceptPoint).magnitude;
			if (interceptRange > maximumRangeMeters)
			{
				withholdReason = PointDefenseWithholdReason.RangeTooLong;
				return false;
			}
			aimPoint = shooterPosition + ToVector3(relativeInterceptPoint);
			hitProbability = EstimateShaoWeiPointDefenseHitProbability(in track, interceptRange, projectileSpeed, dispersionMilliradians, lethalRadiusMeters);
			if (hitProbability < ResolvePointDefenseProbabilityThreshold(minimumHitProbability))
			{
				withholdReason = ((interceptRange > track.PositionUncertaintyMeters * 400f) ? PointDefenseWithholdReason.TrackTooCoarse : PointDefenseWithholdReason.RangeTooLong);
				return false;
			}
			withholdReason = PointDefenseWithholdReason.None;
			return true;
		}

		private float ResolvePointDefenseProbabilityThreshold(float configuredThreshold)
		{
			var rating01 = radarController != null
				? Mathf.Clamp01(radarController.PointDefenseRating / 100f)
				: 0.5f;
			return Mathf.Clamp01(
				Mathf.Max(0f, configuredThreshold) * Mathf.Lerp(1.5f, 0.5f, rating01));
		}

		private float EstimateShaoWeiPointDefenseHitProbability(in MissileTrackSnapshot track, float interceptRange, float projectileSpeed, float dispersionMilliradians, float configuredLethalRadiusMeters)
		{
			float lethalRadius = Mathf.Max(configuredLethalRadiusMeters, 0.32999998f);
			float flightTime = interceptRange / Mathf.Max(projectileSpeed, 1f);
			float num = Mathf.Max(0.01f, track.PositionUncertaintyMeters);
			float leadSigma = num / Mathf.Max(0.5f, 0.05f) * flightTime;
			float dispersionSigma = Mathf.Max(0f, dispersionMilliradians) * 0.001f * interceptRange;
			float sigmaSquared = num * num + leadSigma * leadSigma + dispersionSigma * dispersionSigma;
			if (sigmaSquared <= 1E-06f)
			{
				return 1f;
			}
			return 1f - Mathf.Exp((0f - lethalRadius * lethalRadius) / (2f * sigmaSquared));
		}

		private static void ConfigureMissileHitbox(Collider collider, Vector3 projectileScale, bool interceptorMissile)
		{
			if (!(collider == null))
			{
				collider.isTrigger = true;
				if (collider is BoxCollider box)
				{
					float missileLengthMeters = interceptorMissile ? LeftMissileVisualLengthMeters : RightMissileVisualLengthMeters;
					box.center = Vector3.zero;
					box.size = new Vector3(0.5f / Mathf.Max(Mathf.Abs(projectileScale.x), 0.001f), 0.5f / Mathf.Max(Mathf.Abs(projectileScale.y), 0.001f), missileLengthMeters / Mathf.Max(Mathf.Abs(projectileScale.z), 0.001f));
				}
			}
		}

		private Ray GetAimRay()
		{
			if (aimingCamera == null)
			{
				aimingCamera = Camera.main;
			}
			Vector2 viewportPoint = GetAimViewportPoint();
			if (!(aimingCamera != null))
			{
				return new Ray(base.transform.position + Vector3.up * 2f, base.transform.forward);
			}
			return aimingCamera.ViewportPointToRay(new Vector3(viewportPoint.x, viewportPoint.y, 0f));
		}

		private Vector2 GetAimViewportPoint()
		{
			if (aimController == null && aimingCamera != null)
			{
				aimController = aimingCamera.GetComponent<MechThirdPersonCamera>();
			}
			Vector2 viewportPoint = ((aimController != null) ? aimController.AimViewportPoint : aimViewportPoint);
			return new Vector2(Mathf.Clamp(viewportPoint.x, 0.05f, 0.95f), Mathf.Clamp(viewportPoint.y, 0.05f, 0.95f));
		}





		private MissileKinematicProfile BuildMissileKinematics()
		{
			MissileFlightProfile profile = GetInstalledMissileProfile(MechWeaponRole.AirToAirMissile, airToAirMissileProfile);
			return BuildMissileKinematics(profile);
		}

		private static MissileKinematicProfile BuildMissileKinematics(MissileFlightProfile profile)
		{
			return new MissileKinematicProfile
			{
				EjectionSpeed = profile.EjectionSpeedMps,
				InertMass = profile.DryMassKg + profile.RcsPropellantMassKg,
				MainPropellantMass = profile.MainPropellantMassKg,
				MainEngineThrust = profile.MainEngineThrustNewtons,
				MainMassFlow = profile.MainMassFlowKgPerSecond
			};
		}

		private static MissileKinematicProfile BuildInterceptorKinematics(MissileFlightProfile profile)
		{
			return new MissileKinematicProfile
			{
				EjectionSpeed = profile.EjectionSpeedMps,
				InertMass = profile.DryMassKg + profile.RcsPropellantMassKg,
				MainPropellantMass = profile.MainPropellantMassKg,
				MainEngineThrust = profile.MainEngineThrustNewtons,
				MainMassFlow = profile.MainMassFlowKgPerSecond
			};
		}

		private static double3 ToDouble3(Vector3 value)
		{
			return new double3(value.x, value.y, value.z);
		}

		private static Vector3 ToVector3(double3 value)
		{
			return new Vector3((float)value.x, (float)value.y, (float)value.z);
		}

		private static MissileFlightProfile ResolveProfile(MissileFlightProfile serializedProfile, MissileFlightProfile fallback)
		{
			if (!serializedProfile.IsConfigured)
			{
				return fallback.Sanitized();
			}
			return serializedProfile.Sanitized();
		}

		public static void SpawnImpact(Vector3 position, Vector3 normal, TestWeaponKind kind, MechEffectPrefabLibrary effectLibrary = null, int impactFfxId = 0)
		{
			MechImpactEffect.Spawn(position, normal, GetImpactScale(kind));
		}

		private static float GetImpactScale(TestWeaponKind kind)
		{
			return kind switch
			{
				TestWeaponKind.Missile => 1.4f, 
				TestWeaponKind.Cannon => 0.85f, 
				_ => 0.35f, 
			};
		}

		private void SpawnNativeOneShot(int ffxId, Transform anchor, Vector3 position, Vector3 direction, bool shortenMuzzleFlash, float maximumVisibleSeconds)
		{
			if (!(nativeEffectLibrary == null) && ffxId > 0 && nativeEffectLibrary.TryGet(ffxId, out var prefab))
			{
				Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);
				GameObject effect = ((anchor != null) ? UnityEngine.Object.Instantiate(prefab, position, rotation, anchor) : UnityEngine.Object.Instantiate(prefab, position, rotation));
				effect.name = $"Native_FFX_{ffxId}_OneShot";
				if (shortenMuzzleFlash)
				{
					ApplyMuzzleFlashTiming(effect, maximumVisibleSeconds);
				}
				PlayAllParticles(effect);
				UnityEngine.Object.Destroy(effect, GetOneShotLifetime(effect));
			}
		}

		private float ResolveMuzzleFlashVisibleSeconds(TestWeaponKind kind, float automaticRoundsPerMinute)
		{
			switch (kind)
			{
			case TestWeaponKind.Rifle:
			{
				float roundsPerMinute = automaticRoundsPerMinute > 0f ? automaticRoundsPerMinute : 520f;
				float cycleSeconds = 60f / Mathf.Max(roundsPerMinute, 1f);
				return Mathf.Min(0.18f, cycleSeconds * 0.45f);
			}
			case TestWeaponKind.Cannon:
				return 0.34f;
			case TestWeaponKind.PointDefenseCannon:
			{
				float roundsPerMinute = automaticRoundsPerMinute > 0f ? automaticRoundsPerMinute : 858f;
				float cycleSeconds = 60f / Mathf.Max(roundsPerMinute, 60f);
				return Mathf.Min(0.18f, cycleSeconds * 0.45f);
			}
			default:
				return 0.18f;
			}
		}

		private static void ApplyMuzzleFlashTiming(GameObject effect, float maximumVisibleSeconds)
		{
			maximumVisibleSeconds = Mathf.Max(0.016f, maximumVisibleSeconds);
			float sourceSpan = 0f;
			ParticleSystem[] componentsInChildren = effect.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
			for (int i = 0; i < componentsInChildren.Length; i++)
			{
				ParticleSystem.MainModule main = componentsInChildren[i].main;
				sourceSpan = Mathf.Max(sourceSpan, main.startDelay.constantMax + main.startLifetime.constantMax);
			}
			MechTimedEffectElement[] componentsInChildren2 = effect.GetComponentsInChildren<MechTimedEffectElement>(includeInactive: true);
			foreach (MechTimedEffectElement timedElement in componentsInChildren2)
			{
				sourceSpan = Mathf.Max(sourceSpan, timedElement.LifetimeSeconds);
			}
			float scale = ((sourceSpan > 0.0001f) ? Mathf.Min(0.4f, maximumVisibleSeconds / sourceSpan) : 0.4f);
			componentsInChildren = effect.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
			for (int i = 0; i < componentsInChildren.Length; i++)
			{
				ParticleSystem.MainModule main2 = componentsInChildren[i].main;
				main2.startDelayMultiplier *= scale;
				main2.startLifetimeMultiplier *= scale;
				main2.duration = Mathf.Max(0.01f, main2.duration * scale);
			}
			componentsInChildren2 = effect.GetComponentsInChildren<MechTimedEffectElement>(includeInactive: true);
			foreach (MechTimedEffectElement timedElement2 in componentsInChildren2)
			{
				timedElement2.Configure(Mathf.Max(0.016f, timedElement2.LifetimeSeconds * scale));
			}
			float visibleLifetime = Mathf.Min(maximumVisibleSeconds, Mathf.Max(0.016f, sourceSpan * scale));
			LineRenderer[] componentsInChildren3 = effect.GetComponentsInChildren<LineRenderer>(includeInactive: true);
			foreach (LineRenderer line in componentsInChildren3)
			{
				if (line.GetComponent<MechTimedEffectElement>() == null)
				{
					line.gameObject.AddComponent<MechTimedEffectElement>().Configure(visibleLifetime);
				}
			}
		}

		private static float GetOneShotLifetime(GameObject effect)
		{
			float lifetime = 0.1f;
			ParticleSystem[] componentsInChildren = effect.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
			for (int i = 0; i < componentsInChildren.Length; i++)
			{
				ParticleSystem.MainModule main = componentsInChildren[i].main;
				lifetime = Mathf.Max(lifetime, main.startDelay.constantMax + main.startLifetime.constantMax);
			}
			MechTimedEffectElement[] componentsInChildren2 = effect.GetComponentsInChildren<MechTimedEffectElement>(includeInactive: true);
			foreach (MechTimedEffectElement timedElement in componentsInChildren2)
			{
				lifetime = Mathf.Max(lifetime, timedElement.LifetimeSeconds);
			}
			return lifetime + 0.1f;
		}

		private bool TryInstantiateNativeEffect(int ffxId, Transform parent, out GameObject effect)
		{
			if (nativeEffectLibrary != null && ffxId > 0 && nativeEffectLibrary.TryGet(ffxId, out var prefab))
			{
				effect = UnityEngine.Object.Instantiate(prefab, parent, worldPositionStays: false);
				effect.transform.localPosition = Vector3.zero;
				effect.transform.localRotation = Quaternion.identity;
				PlayAllParticles(effect);
				return true;
			}
			effect = null;
			return false;
		}

		private static void PlayAllParticles(GameObject root)
		{
			ParticleSystem[] systems = root.GetComponentsInChildren<ParticleSystem>(includeInactive: true);
			for (int i = 0; i < systems.Length; i++)
			{
				systems[i].Play(withChildren: true);
			}
		}

		private static bool IsMuzzleTransform(Transform candidate)
		{
			if (!candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase) && !candidate.name.EndsWith("_dmy_301", StringComparison.OrdinalIgnoreCase))
			{
				return candidate.name.EndsWith("_dmy_100", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}

		private static bool IsShaoWeiMuzzleTransform(Transform candidate)
		{
			if (candidate != null)
			{
				return candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private ShaoWeiWeaponState GetShaoWeiState(MechPartSlot slot)
		{
			return slot switch
			{
				MechPartSlot.RightBackWeapon => rightShaoWeiState, 
				MechPartSlot.LeftBackWeapon => leftShaoWeiState, 
				_ => null, 
			};
		}

		private static Color GetWeaponColor(TestWeaponKind kind)
		{
			return kind switch
			{
				TestWeaponKind.Rifle => new Color(0.25f, 0.85f, 1f, 1f), 
				TestWeaponKind.Cannon => new Color(1f, 0.52f, 0.08f, 1f), 
				TestWeaponKind.PointDefenseCannon => new Color(1f, 0.82f, 0.42f, 1f),
				_ => new Color(1f, 0.18f, 0.06f, 1f), 
			};
		}

		private static Material GetProjectileMaterial(TestWeaponKind kind)
		{
			return kind switch
			{
				TestWeaponKind.Rifle => rifleMaterial, 
				TestWeaponKind.Cannon => cannonMaterial, 
				TestWeaponKind.PointDefenseCannon => rifleMaterial,
				_ => missileMaterial, 
			};
		}

		private static void EnsureMaterials()
		{
			if ((object)rifleMaterial == null)
			{
				rifleMaterial = CreateRuntimeMaterial("Test Rifle", GetWeaponColor(TestWeaponKind.Rifle));
			}
			if ((object)cannonMaterial == null)
			{
				cannonMaterial = CreateRuntimeMaterial("Test Cannon", GetWeaponColor(TestWeaponKind.Cannon));
			}
			if ((object)missileMaterial == null)
			{
				missileMaterial = CreateRuntimeMaterial("Test Missile", GetWeaponColor(TestWeaponKind.Missile));
			}
		}

		private static void TryAttachMissileVisual(Transform projectile, Renderer placeholderRenderer, Camera presentationCamera, bool interceptorMissile)
		{
			bool useLeftMissile = interceptorMissile;
			string resourcePath = (useLeftMissile ? "Projectiles/r27_white" : "Projectiles/r77_gridfin");
			string editorAssetPath = (useLeftMissile ? "Assets/_Project/LocalGameAssets/Resources/Projectiles/r27_white.glb" : "Assets/_Project/LocalGameAssets/Resources/Projectiles/r77_gridfin.glb");
			float visualLength = (useLeftMissile ? 4f : 3.6f);
			string missileName = (useLeftMissile ? "R-27" : "R-77");
			GameObject missileVisualPrefab = (useLeftMissile ? leftMissileVisualPrefab : rightMissileVisualPrefab);
			if (missileVisualPrefab == null)
			{
				missileVisualPrefab = LoadMissileVisualPrefab(resourcePath, editorAssetPath);
				if (useLeftMissile)
				{
					leftMissileVisualPrefab = missileVisualPrefab;
				}
				else
				{
					rightMissileVisualPrefab = missileVisualPrefab;
				}
			}
			if (missileVisualPrefab == null)
			{
				if (missileVisualLoadFailures.Add(resourcePath))
				{
					Debug.LogError("[MissileVisual] Failed to load " + missileName + " from Resources path '" + resourcePath + "'.");
				}
				return;
			}
			GameObject holder = new GameObject(missileName + "_MissileVisual");
			holder.transform.SetParent(projectile, worldPositionStays: false);
			holder.transform.localScale = ReciprocalScale(projectile.localScale);
			GameObject visual = UnityEngine.Object.Instantiate(missileVisualPrefab, holder.transform, worldPositionStays: false);
			visual.name = missileName + "_MissileModel";
			visual.transform.SetLocalPositionAndRotation(Vector3.zero, Quaternion.Euler(-90f, 0f, 0f));
			visual.transform.localScale = Vector3.one;
			Collider[] colliders = visual.GetComponentsInChildren<Collider>(includeInactive: true);
			for (int i = 0; i < colliders.Length; i++)
			{
				UnityEngine.Object.Destroy(colliders[i]);
			}
			Renderer[] renderers = BakeRigidMissileRenderers(visual);
			if (renderers.Length == 0)
			{
				UnityEngine.Object.Destroy(holder);
				Debug.LogError("[MissileVisual] The loaded " + missileName + " model contains no usable renderer.");
				return;
			}
			for (int j = 0; j < renderers.Length; j++)
			{
				if (renderers[j] is SkinnedMeshRenderer skinnedRenderer)
				{
					skinnedRenderer.updateWhenOffscreen = true;
				}
			}
			ApplyVisibleMissileMaterials(renderers);
			if (TryCalculateLocalRendererBounds(holder.transform, renderers, out var bounds))
			{
				float longestAxis = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
				if (longestAxis > 0.0001f)
				{
					visual.transform.localScale *= visualLength / longestAxis;
				}
				if (TryCalculateLocalRendererBounds(holder.transform, renderers, out bounds))
				{
					visual.transform.localPosition -= bounds.center;
				}
			}
			if (TryCalculateLocalRendererBounds(holder.transform, renderers, out var finalBounds))
			{
				holder.AddComponent<MechMissileVisualLod>().Configure(presentationCamera, GetCrossSectionDiameter(finalBounds.size), 8f, 6f);
				if (missileVisualLoadSuccesses.Add(resourcePath))
				{
					Debug.Log("[MissileVisual] Loaded " + missileName + " '" + missileVisualPrefab.name + "', renderers=" + $"{renderers.Length}, size={finalBounds.size}.");
				}
			}
			placeholderRenderer.enabled = false;
		}

		private static GameObject LoadMissileVisualPrefab(string resourcePath, string editorAssetPath)
		{
			GameObject prefab = Resources.Load<GameObject>(resourcePath);
			if (prefab != null)
			{
				return prefab;
			}
			string assetName = Path.GetFileName(resourcePath);
			prefab = Resources.LoadAll<GameObject>(string.Empty).FirstOrDefault((GameObject candidate) => candidate != null && string.Equals(candidate.name, assetName, StringComparison.OrdinalIgnoreCase));
			if (prefab != null)
			{
				return prefab;
			}
			return AssetDatabase.LoadAssetAtPath<GameObject>(editorAssetPath);
		}

		private static Renderer[] BakeRigidMissileRenderers(GameObject visual)
		{
			Renderer[] sourceRenderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true);
			List<Renderer> renderers = new List<Renderer>(sourceRenderers.Length);
			for (int i = 0; i < sourceRenderers.Length; i++)
			{
				if (!(sourceRenderers[i] is SkinnedMeshRenderer skinned))
				{
					renderers.Add(sourceRenderers[i]);
					continue;
				}
				Mesh bakedMesh = new Mesh
				{
					name = skinned.sharedMesh.name + "_MissileBaked"
				};
				skinned.BakeMesh(bakedMesh, useScale: false);
				if (bakedMesh.vertexCount == 0)
				{
					UnityEngine.Object.Destroy(bakedMesh);
					renderers.Add(skinned);
					continue;
				}
				GameObject obj = new GameObject(skinned.gameObject.name + "_Static");
				obj.transform.SetParent(skinned.transform.parent, worldPositionStays: false);
				obj.transform.SetLocalPositionAndRotation(skinned.transform.localPosition, skinned.transform.localRotation);
				obj.transform.localScale = skinned.transform.localScale;
				obj.AddComponent<MeshFilter>().sharedMesh = bakedMesh;
				MeshRenderer staticRenderer = obj.AddComponent<MeshRenderer>();
				staticRenderer.sharedMaterials = skinned.sharedMaterials;
				staticRenderer.shadowCastingMode = skinned.shadowCastingMode;
				staticRenderer.receiveShadows = skinned.receiveShadows;
				staticRenderer.renderingLayerMask = skinned.renderingLayerMask;
				staticRenderer.lightProbeUsage = skinned.lightProbeUsage;
				staticRenderer.reflectionProbeUsage = skinned.reflectionProbeUsage;
				obj.AddComponent<MechGeneratedMeshOwner>().Configure(bakedMesh);
				skinned.enabled = false;
				renderers.Add(staticRenderer);
			}
			return renderers.ToArray();
		}

		private static void ApplyVisibleMissileMaterials(IReadOnlyList<Renderer> renderers)
		{
			for (int rendererIndex = 0; rendererIndex < renderers.Count; rendererIndex++)
			{
				Renderer renderer = renderers[rendererIndex];
				Material[] materials = renderer.sharedMaterials;
				for (int materialIndex = 0; materialIndex < materials.Length; materialIndex++)
				{
					Material source = materials[materialIndex];
					if (!(source == null))
					{
						if (!missileUnlitMaterials.TryGetValue(source, out var visibleMaterial) || visibleMaterial == null)
						{
							visibleMaterial = CreateVisibleMissileMaterial(source);
							missileUnlitMaterials[source] = visibleMaterial;
						}
						materials[materialIndex] = visibleMaterial;
					}
				}
				renderer.sharedMaterials = materials;
			}
		}

		private static Material CreateVisibleMissileMaterial(Material source)
		{
			Material material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture") ?? Shader.Find("Unlit/Color"))
			{
				name = source.name + "_MissileUnlit"
			};
			Texture albedo = (source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : (source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null));
			if (albedo != null)
			{
				if (material.HasProperty("_BaseMap"))
				{
					material.SetTexture("_BaseMap", albedo);
				}
				if (material.HasProperty("_MainTex"))
				{
					material.SetTexture("_MainTex", albedo);
				}
			}
			Color color = (source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : (source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white));
			color.a = 1f;
			if (material.HasProperty("_BaseColor"))
			{
				material.SetColor("_BaseColor", color);
			}
			if (material.HasProperty("_Color"))
			{
				material.SetColor("_Color", color);
			}
			if (material.HasProperty("_Cull"))
			{
				material.SetFloat("_Cull", 0f);
			}
			return material;
		}

		private static Vector3 ReciprocalScale(Vector3 scale)
		{
			return new Vector3((Mathf.Abs(scale.x) > 0.0001f) ? (1f / scale.x) : 1f, (Mathf.Abs(scale.y) > 0.0001f) ? (1f / scale.y) : 1f, (Mathf.Abs(scale.z) > 0.0001f) ? (1f / scale.z) : 1f);
		}

		private static float GetCrossSectionDiameter(Vector3 size)
		{
			float[] obj = new float[3] { size.x, size.y, size.z };
			Array.Sort(obj);
			return Mathf.Max(obj[1], 0.01f);
		}

		private static bool TryCalculateLocalRendererBounds(Transform reference, IReadOnlyList<Renderer> renderers, out Bounds bounds)
		{
			bounds = default(Bounds);
			bool initialized = false;
			for (int i = 0; i < renderers.Count; i++)
			{
				Renderer renderer = renderers[i];
				if (renderer == null || !renderer.enabled)
				{
					continue;
				}
				Bounds worldBounds = renderer.bounds;
				Vector3 center = worldBounds.center;
				Vector3 extents = worldBounds.extents;
				for (int x = -1; x <= 1; x += 2)
				{
					for (int y = -1; y <= 1; y += 2)
					{
						for (int z = -1; z <= 1; z += 2)
						{
							Vector3 corner = center + Vector3.Scale(extents, new Vector3(x, y, z));
							Vector3 local = reference.InverseTransformPoint(corner);
							if (!initialized)
							{
								bounds = new Bounds(local, Vector3.zero);
								initialized = true;
							}
							else
							{
								bounds.Encapsulate(local);
							}
						}
					}
				}
			}
			return initialized;
		}

		private static Material CreateRuntimeMaterial(string materialName, Color color)
		{
			Material material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color"))
			{
				name = materialName
			};
			if (material.HasProperty("_BaseColor"))
			{
				material.SetColor("_BaseColor", color);
			}
			if (material.HasProperty("_Color"))
			{
				material.SetColor("_Color", color);
			}
			return material;
		}
	}

	public struct MechWeaponPerformanceSnapshot
	{
		public string WeaponType;
		public bool IsAutomatic;
		public bool IsGuidedMissile;
		public bool IsPointDefense;
		public float DirectHitDamage;
		public bool HasEffectiveRange;
		public float EffectiveRangeMeters;
		public float MaximumRangeMeters;
		public float MuzzleVelocityMetersPerSecond;
		public float ProjectileLifetimeSeconds;
		public bool HasFireRate;
		public float FireRateRoundsPerMinute;
		public bool HasAccuracy;
		public float BaseDispersionMilliradians;
		public bool HasSustainedAccuracy;
		public float SustainedDispersionMilliradians;
		public bool HasRecoilImpulse;
		public float RecoilImpulseNewtonSeconds;
		public int MagazineCapacity;
		public int MagazineCount;
		public float ReloadTimeSeconds;
		public bool HasProximityFuse;
		public string ProximityFuseMode;
		public float ProximityFuseTriggerRadiusMeters;
		public float WarheadDamageRadiusMeters;
		public float ProximityFuseArmSeconds;
		public float ProximityFuseSafeDistanceMeters;
		public float SeekerRangeMeters;
		public float MaximumAngularRateDegreesPerSecond;
		public float PointDefenseFieldOfFireDegrees;
		public float PointDefenseTraverseRateDegreesPerSecond;
		public float PointDefenseMaximumRangeMeters;
		public float PointDefenseMaximumLeadSeconds;
		public float PointDefenseLockHoldSeconds;
		public float PointDefenseMinimumHitProbability;
		public float PointDefenseMaximumThreatTimeSeconds;
		public bool RequiresWeaponsQualityTrack;
		public bool IncomingTargetsOnly;
		public bool UsesHeat;
		public float HeatPerShotMj;
		public float HeatCapacityMj;
		public float CoolingMw;
		public float OverheatThresholdMj;
		public float ResumeThresholdMj;
		public float SpinUpSeconds;

		public int TotalAmmunition
		{
			get
			{
				if (MagazineCapacity <= 0 || MagazineCount <= 0)
				{
					return 0;
				}

				return MagazineCapacity * MagazineCount;
			}
		}
	}

	[DisallowMultipleComponent]
	public sealed class MechMissileVisualLod : MonoBehaviour
	{
		private Camera presentationCamera;
		private Vector3 baseLocalScale;
		private float crossSectionDiameterMeters;
		private float minimumScreenDiameterPixels;
		private float maximumVisualScale;

		public void Configure(Camera camera, float crossSectionDiameter, float minimumScreenDiameterPixels, float maximumVisualScale)
		{
			presentationCamera = camera;
			baseLocalScale = transform.localScale;
			crossSectionDiameterMeters = Mathf.Max(crossSectionDiameter, 0.01f);
			this.minimumScreenDiameterPixels = Mathf.Max(minimumScreenDiameterPixels, 1f);
			this.maximumVisualScale = Mathf.Max(maximumVisualScale, 1f);
			UpdateVisualScale();
		}

		private void LateUpdate()
		{
			UpdateVisualScale();
		}

		private void UpdateVisualScale()
		{
			if (presentationCamera == null)
			{
				presentationCamera = Camera.main;
			}

			if (presentationCamera == null || presentationCamera.pixelHeight <= 0)
			{
				transform.localScale = baseLocalScale;
				return;
			}

			float distance = Vector3.Distance(presentationCamera.transform.position, transform.position);
			float worldPerPixel = 2f * distance * Mathf.Tan(presentationCamera.fieldOfView * 0.5f * Mathf.Deg2Rad) / presentationCamera.pixelHeight;
			float visualScale = Mathf.Clamp(worldPerPixel * minimumScreenDiameterPixels / crossSectionDiameterMeters, 1f, maximumVisualScale);
			transform.localScale = baseLocalScale * visualScale;
		}
	}

	[DisallowMultipleComponent]
	public sealed class MechGeneratedMeshOwner : MonoBehaviour
	{
		private Mesh ownedMesh;

		public void Configure(Mesh mesh)
		{
			ownedMesh = mesh;
		}

		private void OnDestroy()
		{
			if (ownedMesh != null)
			{
				Destroy(ownedMesh);
			}
		}
	}
}
