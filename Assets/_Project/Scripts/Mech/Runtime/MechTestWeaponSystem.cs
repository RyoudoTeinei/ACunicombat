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
		private sealed class ShaoWeiWeaponState
		{
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

		private const float RifleMuzzleVelocityMetersPerSecond = 1500f;

		private const float RifleProjectileLifetimeSeconds = 8f;

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

		[SerializeField]
		[Min(60f)]
		private float rifleRoundsPerMinute = 600f;

		[Header("Hand weapon ammunition")]
		[SerializeField]
		[Min(1f)]
		private int cannonMagazineCapacity = 1;

		[SerializeField]
		[Min(1f)]
		private int cannonMagazineCount = 32;

		[SerializeField]
		[Min(1f)]
		private int rifleMagazineCapacity = 36;

		[SerializeField]
		[Min(1f)]
		private int rifleMagazineCount = 12;

		[SerializeField]
		[Min(0.1f)]
		private float rifleReloadSeconds = 4.95f;

		[SerializeField]
		[Min(0.1f)]
		private float cannonReloadSeconds = 4.25f;

		[Header("57 mm proximity fuze")]
		[SerializeField]
		[Min(0f)]
		private float cannonProximityFuseRadiusMeters = 7.5f;

		[SerializeField]
		[Min(0f)]
		private float cannonProximityFuseArmSeconds = 0.12f;

		[SerializeField]
		[Min(0f)]
		private float cannonProximityFuseSafeDistanceMeters = 45f;

		[SerializeField]
		[Min(0f)]
		private float cannonWarheadLethalRadiusMeters = 10f;

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

		[Header("30 mm rifle dispersion")]
		[SerializeField]
		[Min(0f)]
		private float rifleBaseDispersionMilliradians = 0.07f;

		[SerializeField]
		[Min(1f)]
		private int rifleLowRecoilShotCount = 4;

		[SerializeField]
		[Min(0f)]
		private float rifleLowRecoilPerShotMilliradians = 0.008f;

		[SerializeField]
		[Min(0f)]
		private float rifleSustainedRecoilPerShotMilliradians = 0.022f;

		[SerializeField]
		[Min(0f)]
		private float rifleMaximumDispersionMilliradians = 0.3f;

		[SerializeField]
		[Min(0f)]
		private float rifleDispersionRecoveryMilliradiansPerSecond = 0.4f;

		[SerializeField]
		[Min(0f)]
		private float rifleDispersionRecoveryDelaySeconds = 0.16f;

		[SerializeField]
		[Min(0f)]
		private float rifleBurstResetSeconds = 0.45f;

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
		[Min(100f)]
		private float shaoWeiMuzzleVelocityMetersPerSecond = 1800f;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiProjectileLifetimeSeconds = 4f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiDirectHitDamage = 45f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiInterceptionDamage = 90f;

		[SerializeField]
		[Min(60f)]
		private float shaoWeiRoundsPerMinute = 858f;

		[SerializeField]
		[Min(100f)]
		private float shaoWeiEffectiveRangeMeters = 3000f;

		[SerializeField]
		[Min(100f)]
		private float shaoWeiMaximumRangeMeters = 6000f;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiMaximumLeadSeconds = 4f;

		[SerializeField]
		[Range(1f, 179f)]
		private float shaoWeiMaximumOffBoresightDegrees = 100f;

		[SerializeField]
		[Min(1f)]
		private float shaoWeiTraverseRateDegreesPerSecond = 180f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiTrackHoldSeconds = 0.3f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiMaximumThreatTimeSeconds = 12f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiBaseDispersionMilliradians = 0.1f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiSustainedDispersionMilliradians = 0.35f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiRoundLethalRadiusMeters = 4f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiRoundArmSeconds = 0.05f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiRoundSafeDistanceMeters = 90f;

		[SerializeField]
		[Min(1f)]
		private int shaoWeiMagazineCapacity = 200;

		[SerializeField]
		[Min(1f)]
		private int shaoWeiMagazineCount = 4;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiReloadSeconds = 4f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiHeatPerShotMj = 0.22f;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiHeatCapacityMj = 6f;

		[SerializeField]
		[Min(0.01f)]
		private float shaoWeiCoolingMw = 1.4f;

		[SerializeField]
		[Min(0.1f)]
		private float shaoWeiResumeThresholdMj = 2f;

		[SerializeField]
		[Min(0f)]
		private float shaoWeiSpinUpSeconds = 0.5f;

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

		private int leftHandMuzzleIndex;

		private int rightHandMuzzleIndex;

		private int leftBackMuzzleIndex;

		private int rightBackMuzzleIndex;

		private float nextRifleFireTime;

		private int cannonRoundsRemaining;

		private int cannonMagazinesRemaining;

		private int rifleRoundsRemaining;

		private int rifleMagazinesRemaining;

		private int leftMissileRoundsRemaining;

		private int leftMissileMagazinesRemaining;

		private int rightMissileRoundsRemaining;

		private int rightMissileMagazinesRemaining;

		private readonly ShaoWeiWeaponState leftShaoWeiState = new ShaoWeiWeaponState();

		private readonly ShaoWeiWeaponState rightShaoWeiState = new ShaoWeiWeaponState();

		private float rifleReloadCompleteTime = float.NegativeInfinity;

		private float cannonReloadCompleteTime = float.NegativeInfinity;

		private bool rifleReloading;

		private bool cannonReloading;

		private bool ammunitionInitialized;

		private float rifleRecoilDispersionMilliradians;

		private float lastRifleDispersionUpdateTime;

		private float lastRifleShotTime = float.NegativeInfinity;

		private int rifleBurstShotCount;

		private uint rifleDispersionRandomState;

		private GUIStyle hudLabelStyle;

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

		public int CannonRoundsRemaining => cannonRoundsRemaining;

		public int CannonMagazineCapacity => cannonMagazineCapacity;

		public int CannonMagazinesRemaining => cannonMagazinesRemaining;

		public int RifleRoundsRemaining => rifleRoundsRemaining;

		public int RifleMagazineCapacity => rifleMagazineCapacity;

		public int RifleMagazinesRemaining => rifleMagazinesRemaining;

		public int LeftMissileRoundsRemaining => leftMissileRoundsRemaining;

		public int LeftMissileMagazineCapacity => leftMissileMagazineCapacity;

		public int LeftMissileMagazinesRemaining => leftMissileMagazinesRemaining;

		public int RightMissileRoundsRemaining => rightMissileRoundsRemaining;

		public int RightMissileMagazineCapacity => rightMissileMagazineCapacity;

		public int RightMissileMagazinesRemaining => rightMissileMagazinesRemaining;

		public bool LeftShaoWeiInstalled => IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon);

		public bool RightShaoWeiInstalled => IsShaoWeiEquipped(MechPartSlot.RightBackWeapon);

		public bool ShaoWeiPointDefenseEnabled => enableShaoWeiPointDefense;

		public int ShaoWeiMagazineCapacity => shaoWeiMagazineCapacity;

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

		public float ShaoWeiRecoveryDurationSeconds => Mathf.Max(shaoWeiReloadSeconds, (shaoWeiHeatCapacityMj - shaoWeiResumeThresholdMj) / Mathf.Max(0.01f, shaoWeiCoolingMw));

		public bool RifleReloading
		{
			get
			{
				if (rifleReloading)
				{
					return Time.time < rifleReloadCompleteTime;
				}
				return false;
			}
		}

		public bool CannonReloading
		{
			get
			{
				if (cannonReloading)
				{
					return Time.time < cannonReloadCompleteTime;
				}
				return false;
			}
		}

		public float RifleReloadRemainingSeconds
		{
			get
			{
				if (!RifleReloading)
				{
					return 0f;
				}
				return Mathf.Max(0f, rifleReloadCompleteTime - Time.time);
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
				return Mathf.Max(0f, cannonReloadCompleteTime - Time.time);
			}
		}

		public float RifleReloadDurationSeconds => Mathf.Max(0.1f, rifleReloadSeconds);

		public float CannonReloadDurationSeconds => Mathf.Max(0.1f, cannonReloadSeconds);

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
			if (PartIdMatches(partId, "WP_L_1310"))
			{
				performance = new MechWeaponPerformanceSnapshot
				{
					WeaponType = "57 mm 单发近炸火炮",
					DirectHitDamage = 420f,
					HasEffectiveRange = false,
					MaximumRangeMeters = 4500f,
					MuzzleVelocityMetersPerSecond = 450f,
					ProjectileLifetimeSeconds = 10f,
					HasFireRate = true,
					FireRateRoundsPerMinute = 60f / Mathf.Max(0.1f, cannonReloadSeconds),
					HasAccuracy = true,
					BaseDispersionMilliradians = 0f,
					HasSustainedAccuracy = false,
					HasRecoilImpulse = false,
					MagazineCapacity = cannonMagazineCapacity,
					MagazineCount = cannonMagazineCount,
					ReloadTimeSeconds = cannonReloadSeconds,
					HasProximityFuse = true,
					ProximityFuseMode = "锁定目标时启用",
					ProximityFuseTriggerRadiusMeters = cannonProximityFuseRadiusMeters,
					WarheadDamageRadiusMeters = cannonWarheadLethalRadiusMeters,
					ProximityFuseArmSeconds = cannonProximityFuseArmSeconds,
					ProximityFuseSafeDistanceMeters = cannonProximityFuseSafeDistanceMeters
				};
				return true;
			}
			if (PartIdMatches(partId, "WP_R_1610"))
			{
				performance = new MechWeaponPerformanceSnapshot
				{
					WeaponType = "30 mm 弹匣式自动炮",
					IsAutomatic = true,
					DirectHitDamage = 110f,
					HasEffectiveRange = false,
					MaximumRangeMeters = 12000f,
					MuzzleVelocityMetersPerSecond = 1500f,
					ProjectileLifetimeSeconds = 8f,
					HasFireRate = true,
					FireRateRoundsPerMinute = rifleRoundsPerMinute,
					HasAccuracy = true,
					BaseDispersionMilliradians = rifleBaseDispersionMilliradians,
					HasSustainedAccuracy = true,
					SustainedDispersionMilliradians = rifleMaximumDispersionMilliradians,
					HasRecoilImpulse = false,
					MagazineCapacity = rifleMagazineCapacity,
					MagazineCount = rifleMagazineCount,
					ReloadTimeSeconds = rifleReloadSeconds,
					HasProximityFuse = true,
					ProximityFuseMode = "点防御弹种",
					ProximityFuseTriggerRadiusMeters = pointDefenseRoundLethalRadiusMeters,
					WarheadDamageRadiusMeters = pointDefenseRoundLethalRadiusMeters
				};
				return true;
			}
			if (PartIdMatches(partId, "WP_L_5110") || PartIdMatches(partId, "WP_R_5110"))
			{
				performance = new MechWeaponPerformanceSnapshot
				{
					WeaponType = "肩载点防御加特林炮",
					IsAutomatic = true,
					IsPointDefense = true,
					DirectHitDamage = shaoWeiDirectHitDamage,
					InterceptionDamage = shaoWeiInterceptionDamage,
					HasEffectiveRange = true,
					EffectiveRangeMeters = shaoWeiEffectiveRangeMeters,
					MaximumRangeMeters = shaoWeiMaximumRangeMeters,
					MuzzleVelocityMetersPerSecond = shaoWeiMuzzleVelocityMetersPerSecond,
					ProjectileLifetimeSeconds = shaoWeiProjectileLifetimeSeconds,
					HasFireRate = true,
					FireRateRoundsPerMinute = shaoWeiRoundsPerMinute,
					HasAccuracy = true,
					BaseDispersionMilliradians = shaoWeiBaseDispersionMilliradians,
					HasSustainedAccuracy = true,
					SustainedDispersionMilliradians = shaoWeiSustainedDispersionMilliradians,
					HasRecoilImpulse = false,
					MagazineCapacity = shaoWeiMagazineCapacity,
					MagazineCount = shaoWeiMagazineCount,
					ReloadTimeSeconds = shaoWeiReloadSeconds,
					HasProximityFuse = true,
					ProximityFuseMode = "自动点防御破片云",
					ProximityFuseTriggerRadiusMeters = shaoWeiRoundLethalRadiusMeters,
					WarheadDamageRadiusMeters = shaoWeiRoundLethalRadiusMeters,
					ProximityFuseArmSeconds = shaoWeiRoundArmSeconds,
					ProximityFuseSafeDistanceMeters = shaoWeiRoundSafeDistanceMeters,
					PointDefenseFieldOfFireDegrees = shaoWeiMaximumOffBoresightDegrees * 2f,
					PointDefenseTraverseRateDegreesPerSecond = shaoWeiTraverseRateDegreesPerSecond,
					PointDefenseMaximumRangeMeters = shaoWeiMaximumRangeMeters,
					PointDefenseMaximumLeadSeconds = shaoWeiMaximumLeadSeconds,
					PointDefenseLockHoldSeconds = shaoWeiTrackHoldSeconds,
					PointDefenseMinimumHitProbability = pointDefenseMinimumHitProbability,
					PointDefenseMaximumThreatTimeSeconds = shaoWeiMaximumThreatTimeSeconds,
					RequiresWeaponsQualityTrack = true,
					IncomingTargetsOnly = true,
					UsesHeat = true,
					HeatPerShotMj = shaoWeiHeatPerShotMj,
					HeatCapacityMj = shaoWeiHeatCapacityMj,
					CoolingMw = shaoWeiCoolingMw,
					OverheatThresholdMj = shaoWeiHeatCapacityMj,
					ResumeThresholdMj = shaoWeiResumeThresholdMj,
					SpinUpSeconds = shaoWeiSpinUpSeconds
				};
				return true;
			}
			if (PartIdMatches(partId, "WP_L_4250"))
			{
				performance = CreateMissilePerformance("拦截导弹发射器", interceptorMissileProfile, leftMissileMagazineCapacity, leftMissileMagazineCount);
				return true;
			}
			if (PartIdMatches(partId, "WP_R_4290"))
			{
				performance = CreateMissilePerformance("空空导弹发射器", airToAirMissileProfile, rightMissileMagazineCapacity, rightMissileMagazineCount);
				return true;
			}
			return false;
		}

		private static MechWeaponPerformanceSnapshot CreateMissilePerformance(string weaponType, MissileFlightProfile profile, int magazineCapacity, int magazineCount)
		{
			profile = profile.Sanitized();
			return new MechWeaponPerformanceSnapshot
			{
				WeaponType = weaponType,
				IsGuidedMissile = true,
				DirectHitDamage = 300f,
				HasEffectiveRange = true,
				EffectiveRangeMeters = profile.MaximumRangeMeters,
				MaximumRangeMeters = profile.MaximumRangeMeters,
				MuzzleVelocityMetersPerSecond = profile.EjectionSpeedMps,
				ProjectileLifetimeSeconds = profile.MaximumFlightTimeSeconds,
				HasFireRate = false,
				HasAccuracy = false,
				HasSustainedAccuracy = false,
				HasRecoilImpulse = false,
				MagazineCapacity = magazineCapacity,
				MagazineCount = magazineCount,
				ReloadTimeSeconds = 0f,
				HasProximityFuse = true,
				ProximityFuseMode = "常规近炸",
				ProximityFuseTriggerRadiusMeters = profile.ProximityFuseRadiusMeters,
				WarheadDamageRadiusMeters = profile.WarheadLethalRadiusMeters,
				ProximityFuseArmSeconds = profile.ProximityFuseArmSeconds,
				ProximityFuseSafeDistanceMeters = profile.ProximityFuseSafeDistanceMeters,
				SeekerRangeMeters = profile.SeekerRangeMeters,
				MaximumAngularRateDegreesPerSecond = profile.MaximumAngularRateDegreesPerSecond
			};
		}

		private static bool PartIdMatches(string partId, string sourceKey)
		{
			if (!string.IsNullOrWhiteSpace(partId))
			{
				return partId.EndsWith(sourceKey, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}

		private bool IsShaoWeiEquipped(MechPartSlot slot)
		{
			if (runtimeState == null)
			{
				runtimeState = GetComponent<MechRuntimeState>();
			}
			if (runtimeState == null)
			{
				return false;
			}
			IReadOnlyList<MechAssemblyRecord> parts = runtimeState.AssembledParts;
			for (int i = 0; i < parts.Count; i++)
			{
				MechAssemblyRecord record = parts[i];
				if (!(record?.part == null) && record.slot == slot)
				{
					if (!PartIdMatches(record.part.PartId, "WP_L_5110"))
					{
						return PartIdMatches(record.part.PartId, "WP_R_5110");
					}
					return true;
				}
			}
			return false;
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
			switch (kind)
			{
			case TestWeaponKind.Missile:
				break;
			case TestWeaponKind.Cannon:
				TryFireHandWeapon(MechPartSlot.LeftHandWeapon, kind, ref leftHandMuzzleIndex);
				break;
			default:
				TryFireHandWeapon(MechPartSlot.RightHandWeapon, kind, ref rightHandMuzzleIndex);
				break;
			}
		}

		public bool RequestRifleReload()
		{
			InitializeAmmunition();
			UpdateReloadState();
			if (rifleReloading || rifleRoundsRemaining >= rifleMagazineCapacity || rifleMagazinesRemaining <= 1)
			{
				return false;
			}
			rifleMagazinesRemaining--;
			rifleRoundsRemaining = 0;
			BeginRifleReload(GetWeaponPosition(MechPartSlot.RightHandWeapon));
			return true;
		}

		public bool RequestCannonReload()
		{
			InitializeAmmunition();
			UpdateReloadState();
			if (cannonReloading || cannonRoundsRemaining >= cannonMagazineCapacity || cannonMagazinesRemaining <= 1)
			{
				return false;
			}
			cannonMagazinesRemaining--;
			cannonRoundsRemaining = 0;
			BeginCannonReload(GetWeaponPosition(MechPartSlot.LeftHandWeapon));
			return true;
		}

		public bool CommandFireHandWeaponAtEstimate(TestWeaponKind kind, Vector3 estimatedPosition, Vector3 estimatedVelocity)
		{
			return kind switch
			{
				TestWeaponKind.Missile => false, 
				TestWeaponKind.Cannon => TryFireHandWeapon(MechPartSlot.LeftHandWeapon, kind, ref leftHandMuzzleIndex, hasExternalTrack: true, estimatedPosition, estimatedVelocity), 
				_ => TryFireHandWeapon(MechPartSlot.RightHandWeapon, kind, ref rightHandMuzzleIndex, hasExternalTrack: true, estimatedPosition, estimatedVelocity), 
			};
		}

		public void CommandFireMissile(MechPartSlot slot)
		{
			if (!IsShaoWeiEquipped(slot))
			{
				switch (slot)
				{
				case MechPartSlot.LeftBackWeapon:
					FireInterceptorMissile(ref leftBackMuzzleIndex);
					break;
				case MechPartSlot.RightBackWeapon:
					FireMissileSalvo(slot, ref rightBackMuzzleIndex);
					break;
				}
			}
		}

		public void CommandFireMissileAt(MechPartSlot slot, CombatTestTarget directTarget)
		{
			if (!IsShaoWeiEquipped(slot) && !(directTarget == null) && directTarget.IsAlive)
			{
				switch (slot)
				{
				case MechPartSlot.LeftBackWeapon:
					FireInterceptorMissile(ref leftBackMuzzleIndex);
					break;
				case MechPartSlot.RightBackWeapon:
					FireDirectMissileSalvo(slot, directTarget, ref rightBackMuzzleIndex);
					break;
				}
			}
		}

		public void CommandFireMissileAtEstimate(MechPartSlot slot, CombatTestTarget directTarget, Vector3 estimatedPosition, Vector3 estimatedVelocity, float uncertaintyMeters)
		{
			if (!IsShaoWeiEquipped(slot) && !(directTarget == null) && directTarget.IsAlive && slot == MechPartSlot.RightBackWeapon)
			{
				FireDirectMissileSalvo(slot, directTarget, ref rightBackMuzzleIndex, hasInitialEstimate: true, estimatedPosition, estimatedVelocity, uncertaintyMeters);
			}
		}

		public void CommandFireInterceptorAt(MechTestProjectile incomingMissile)
		{
			if (!IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon) && !(incomingMissile == null) && incomingMissile.IsInterceptableMissile && !incomingMissile.IsOwnedBy(base.transform))
			{
				FireDirectInterceptorMissile(incomingMissile, ref leftBackMuzzleIndex);
			}
		}

		private void Awake()
		{
			InitializeAmmunition();
			ownCombatTarget = GetComponent<CombatTestTarget>();
			airToAirMissileProfile = ResolveProfile(airToAirMissileProfile, MissileFlightProfile.AirToAirDefault);
			interceptorMissileProfile = ResolveProfile(interceptorMissileProfile, MissileFlightProfile.InterceptorDefault);
			if (Mathf.Abs(interceptorMissileProfile.ProximityFuseRadiusMeters - 10f) < 0.01f)
			{
				interceptorMissileProfile.ProximityFuseRadiusMeters = 12f;
			}
			if (Mathf.Abs(interceptorMissileProfile.WarheadLethalRadiusMeters - 14f) < 0.01f)
			{
				interceptorMissileProfile.WarheadLethalRadiusMeters = 22f;
			}
			if (Mathf.Abs(interceptorPredictionHorizonSeconds - 30f) < 0.01f)
			{
				interceptorPredictionHorizonSeconds = 60f;
			}
			if (Mathf.Abs(pointDefenseMaximumRangeMeters - 1500f) < 0.01f)
			{
				pointDefenseMaximumRangeMeters = 6000f;
			}
			if (Mathf.Abs(pointDefenseMaximumLeadSeconds - 2.9f) < 0.01f)
			{
				pointDefenseMaximumLeadSeconds = 6f;
			}
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
				RequestCannonReload();
			}
			if (frame.SecondaryReloadEdge)
			{
				RequestRifleReload();
			}
			if (frame.PrimaryFireEdge)
			{
				TryFireHandWeapon(MechPartSlot.LeftHandWeapon, TestWeaponKind.Cannon, ref leftHandMuzzleIndex);
			}
			if (frame.SecondaryFireHeld)
			{
				if ((frame.SecondaryFireEdge || Time.time >= nextRifleFireTime) && TryFireHandWeapon(MechPartSlot.RightHandWeapon, TestWeaponKind.Rifle, ref rightHandMuzzleIndex))
				{
					nextRifleFireTime = Time.time + 60f / Mathf.Max(rifleRoundsPerMinute, 60f);
				}
			}
			else
			{
				nextRifleFireTime = 0f;
			}
			handWeaponAudio?.SetRifleTriggerHeld(frame.SecondaryFireHeld && !RifleReloading && rifleRoundsRemaining > 0);
			if (playerRig != null && !IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon) && playerRig.ConsumeShoulderMissileLaunch(MechPartSlot.LeftBackWeapon))
			{
				FireInterceptorMissile(ref leftBackMuzzleIndex);
			}
			if (playerRig != null && !IsShaoWeiEquipped(MechPartSlot.RightBackWeapon) && playerRig.ConsumeShoulderMissileLaunch(MechPartSlot.RightBackWeapon))
			{
				FireMissileSalvo(MechPartSlot.RightBackWeapon, ref rightBackMuzzleIndex);
			}
		}

		private void LateUpdate()
		{
			AimShaoWeiVisual(MechPartSlot.LeftBackWeapon, leftShaoWeiState);
			AimShaoWeiVisual(MechPartSlot.RightBackWeapon, rightShaoWeiState);
		}

		private void AimShaoWeiVisual(MechPartSlot slot, ShaoWeiWeaponState state)
		{
			if (!IsShaoWeiEquipped(slot) || !state.Spinning || !state.HasAimPoint || state.AimPivot == null)
			{
				return;
			}
			Transform muzzle = PeekShaoWeiMuzzle(slot);
			if (!(muzzle == null))
			{
				Vector3 currentDirection = muzzle.position - state.AimPivot.position;
				Vector3 desiredDirection = state.AimPoint - state.AimPivot.position;
				if (!(currentDirection.sqrMagnitude <= 1E-06f) && !(desiredDirection.sqrMagnitude <= 1E-06f))
				{
					Quaternion fullCorrection = Quaternion.FromToRotation(currentDirection.normalized, desiredDirection.normalized);
					Quaternion correction = Quaternion.RotateTowards(Quaternion.identity, fullCorrection, shaoWeiTraverseRateDegreesPerSecond * Time.deltaTime);
					state.AimPivot.rotation = correction * state.AimPivot.rotation;
				}
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
				bool isShaoWei = record.part != null && (PartIdMatches(record.part.PartId, "WP_L_5110") || PartIdMatches(record.part.PartId, "WP_R_5110"));
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
					state.AimPivot = transforms.FirstOrDefault((Transform candidate) => candidate.name.EndsWith("LookAt_01", StringComparison.OrdinalIgnoreCase));
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

		private bool TryFireHandWeapon(MechPartSlot slot, TestWeaponKind kind, ref int muzzleIndex, bool hasExternalTrack = false, Vector3 externalTrackPosition = default(Vector3), Vector3 externalTrackVelocity = default(Vector3))
		{
			InitializeAmmunition();
			UpdateReloadState();
			UpdateRifleDispersion(Time.time);
			switch (kind)
			{
			case TestWeaponKind.Rifle:
				if (rifleReloading)
				{
					return false;
				}
				if (rifleRoundsRemaining <= 0)
				{
					if (rifleMagazinesRemaining > 0)
					{
						BeginRifleReload(base.transform.position);
					}
					return false;
				}
				break;
			case TestWeaponKind.Cannon:
				if (cannonReloading)
				{
					return false;
				}
				if (cannonRoundsRemaining <= 0)
				{
					if (cannonMagazinesRemaining > 0)
					{
						BeginCannonReload(base.transform.position);
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
			float projectileSpeed = ((kind == TestWeaponKind.Rifle) ? 1500f : 450f);
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Vector3 aimPoint = Vector3.zero;
			bool locked = false;
			bool pointDefenseEngaged = false;
			if (hasExternalTrack)
			{
				Vector3 value = externalTrackPosition - muzzle.position;
				locked = FireSolutionSolver.TrySolveBallisticIntercept(relativeVelocity: ToDouble3(externalTrackVelocity - inheritedVelocity), relativePosition: ToDouble3(value), targetAcceleration: double3.zero, projectileSpeed: projectileSpeed, maximumFlightTime: (kind == TestWeaponKind.Rifle) ? 8f : 12f, flightTime: out var _, relativeInterceptPoint: out var relativeInterceptPoint);
				if (locked)
				{
					aimPoint = muzzle.position + ToVector3(relativeInterceptPoint);
				}
			}
			else
			{
				locked = kind == TestWeaponKind.Rifle && TryGetPointDefenseAimPoint(muzzle.position, projectileSpeed, out aimPoint);
				pointDefenseEngaged = locked;
				if (!locked)
				{
					locked = TryGetAimPoint((kind == TestWeaponKind.Rifle) ? gunAutoAimDegrees : (gunAutoAimDegrees + 2f), projectileSpeed, out aimPoint, out var _);
				}
			}
			Vector3 direction = (aimPoint - muzzle.position).normalized;
			if (!locked)
			{
				direction = GetAimRay().direction;
			}
			if (kind == TestWeaponKind.Rifle)
			{
				direction = ApplyRifleDispersion(direction);
			}
			if (kind == TestWeaponKind.Rifle)
			{
				Vector3 position = muzzle.position;
				Vector3 direction2 = direction;
				Transform launchAnchor = muzzle;
				Vector3 inheritedVelocity2 = inheritedVelocity;
				float interceptorRoundLethalRadius = (pointDefenseEngaged ? pointDefenseRoundLethalRadiusMeters : 0f);
				SpawnProjectile(position, direction2, kind, 1500f, 110f, 0.08f, 8f, default(TrackId), 0f, 0f, 0f, 6011100, 6013101, 6011101, launchAnchor, 0f, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity2, null, null, MechPartSlot.None, interceptorMissile: false, null, default(MissileFlightProfile), hasInitialGuidanceEstimate: false, default(Vector3), default(Vector3), 0f, default(Vector3), coldLaunch: false, 0f, 0f, 8f, 0.6f, interceptorRoundLethalRadius);
				PlayHandWeaponAudio(kind, slot, muzzle);
				RegisterRifleRecoil(Time.time);
				rifleRoundsRemaining = Mathf.Max(0, rifleRoundsRemaining - 1);
				if (rifleRoundsRemaining == 0)
				{
					rifleMagazinesRemaining = Mathf.Max(0, rifleMagazinesRemaining - 1);
					if (rifleMagazinesRemaining > 0)
					{
						BeginRifleReload(muzzle.position);
					}
				}
			}
			else
			{
				Vector3 position2 = muzzle.position;
				Vector3 direction3 = direction;
				Transform launchAnchor = muzzle;
				float interceptorRoundLethalRadius = cannonProximityFuseRadiusMeters;
				float proximityFuseArmTime = cannonProximityFuseArmSeconds;
				float proximityFuseSafeDistance = cannonProximityFuseSafeDistanceMeters;
				float warheadLethalRadius = cannonWarheadLethalRadiusMeters;
				Vector3 inheritedVelocity2 = inheritedVelocity;
				bool cannonProximityFuseEnabled = locked;
				SpawnProjectile(position2, direction3, kind, 450f, 420f, 0.2f, 10f, default(TrackId), 0f, 0f, 0f, 6031100, 6031101, 0, launchAnchor, 0f, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, interceptorRoundLethalRadius, proximityFuseArmTime, proximityFuseSafeDistance, warheadLethalRadius, inheritedVelocity2, null, null, MechPartSlot.None, interceptorMissile: false, null, default(MissileFlightProfile), hasInitialGuidanceEstimate: false, default(Vector3), default(Vector3), 0f, default(Vector3), coldLaunch: false, 0f, 0f, 8f, 0.6f, 0f, 0f, 0f, 0f, cannonProximityFuseEnabled);
				PlayHandWeaponAudio(kind, slot, muzzle);
				cannonRoundsRemaining = Mathf.Max(0, cannonRoundsRemaining - 1);
				if (cannonRoundsRemaining == 0)
				{
					cannonMagazinesRemaining = Mathf.Max(0, cannonMagazinesRemaining - 1);
				}
				if (cannonRoundsRemaining > 0 || cannonMagazinesRemaining > 0)
				{
					BeginCannonReload(muzzle.position);
				}
			}
			return true;
		}

		private void InitializeAmmunition()
		{
			if (!ammunitionInitialized)
			{
				if (ammunitionConfigurationVersion < 2)
				{
					cannonMagazineCapacity = 1;
					cannonMagazineCount = 32;
					rifleMagazineCapacity = 36;
					rifleMagazineCount = 12;
					rightMissileMagazineCount = 6;
					ammunitionConfigurationVersion = 2;
				}
				cannonMagazineCapacity = Mathf.Max(1, cannonMagazineCapacity);
				cannonMagazineCount = Mathf.Max(1, cannonMagazineCount);
				rifleMagazineCapacity = Mathf.Max(1, rifleMagazineCapacity);
				rifleMagazineCount = Mathf.Max(1, rifleMagazineCount);
				leftMissileMagazineCapacity = Mathf.Max(1, leftMissileMagazineCapacity);
				leftMissileMagazineCount = Mathf.Max(1, leftMissileMagazineCount);
				rightMissileMagazineCapacity = Mathf.Max(1, rightMissileMagazineCapacity);
				rightMissileMagazineCount = Mathf.Max(1, rightMissileMagazineCount);
				shaoWeiMagazineCapacity = Mathf.Max(1, shaoWeiMagazineCapacity);
				shaoWeiMagazineCount = Mathf.Max(1, shaoWeiMagazineCount);
				shaoWeiHeatCapacityMj = Mathf.Max(0.1f, shaoWeiHeatCapacityMj);
				shaoWeiResumeThresholdMj = Mathf.Clamp(shaoWeiResumeThresholdMj, 0f, shaoWeiHeatCapacityMj);
				cannonRoundsRemaining = cannonMagazineCapacity;
				cannonMagazinesRemaining = cannonMagazineCount;
				rifleRoundsRemaining = rifleMagazineCapacity;
				rifleMagazinesRemaining = rifleMagazineCount;
				leftMissileRoundsRemaining = leftMissileMagazineCapacity;
				leftMissileMagazinesRemaining = leftMissileMagazineCount;
				rightMissileRoundsRemaining = rightMissileMagazineCapacity;
				rightMissileMagazinesRemaining = rightMissileMagazineCount;
				InitializeShaoWeiState(leftShaoWeiState, nextRifleDispersionSeed ^ 0xA5110F1);
				InitializeShaoWeiState(rightShaoWeiState, nextRifleDispersionSeed ^ 0xA5110F2);
				lastRifleDispersionUpdateTime = Time.time;
				rifleDispersionRandomState = nextRifleDispersionSeed;
				nextRifleDispersionSeed += 1831565813u;
				if (rifleDispersionRandomState == 0)
				{
					rifleDispersionRandomState = 1u;
				}
				ammunitionInitialized = true;
			}
		}

		private void InitializeShaoWeiState(ShaoWeiWeaponState state, uint randomSeed)
		{
			state.RoundsRemaining = shaoWeiMagazineCapacity;
			state.MagazinesRemaining = shaoWeiMagazineCount;
			state.HeatMj = 0f;
			state.Overheated = false;
			state.Reloading = false;
			state.ReloadCompleteTime = float.NegativeInfinity;
			state.NextFireTime = 0f;
			state.SpinReadyTime = 0f;
			state.Spinning = false;
			state.HasSlewedDirection = false;
			state.HasAimPoint = false;
			state.DispersionRandomState = ((randomSeed == 0) ? 1u : randomSeed);
		}

		private void UpdateShaoWeiReloadAndHeat(float deltaTime)
		{
			UpdateShaoWeiState(leftShaoWeiState, deltaTime);
			UpdateShaoWeiState(rightShaoWeiState, deltaTime);
		}

		private void UpdateShaoWeiState(ShaoWeiWeaponState state, float deltaTime)
		{
			state.HeatMj = Mathf.Max(0f, state.HeatMj - Mathf.Max(0f, shaoWeiCoolingMw) * Mathf.Max(0f, deltaTime));
			if (state.Overheated && state.HeatMj <= shaoWeiResumeThresholdMj)
			{
				state.Overheated = false;
			}
			if (state.Reloading && !(Time.time < state.ReloadCompleteTime))
			{
				state.Reloading = false;
				if (state.RoundsRemaining == 0 && state.MagazinesRemaining > 0)
				{
					state.RoundsRemaining = shaoWeiMagazineCapacity;
				}
			}
		}

		private void BeginShaoWeiReload(ShaoWeiWeaponState state)
		{
			if (!state.Reloading && state.MagazinesRemaining > 0)
			{
				state.Reloading = true;
				state.ReloadCompleteTime = Time.time + Mathf.Max(0.1f, shaoWeiReloadSeconds);
			}
		}

		private float GetShaoWeiRecoveryRemainingSeconds(ShaoWeiWeaponState state)
		{
			float a = (state.Reloading ? Mathf.Max(0f, state.ReloadCompleteTime - Time.time) : 0f);
			float coolingRemaining = (state.Overheated ? Mathf.Max(0f, (state.HeatMj - shaoWeiResumeThresholdMj) / Mathf.Max(0.01f, shaoWeiCoolingMw)) : 0f);
			return Mathf.Max(a, coolingRemaining);
		}

		private void UpdateRifleDispersion(float now)
		{
			float elapsed = Mathf.Max(0f, now - lastRifleDispersionUpdateTime);
			lastRifleDispersionUpdateTime = now;
			if (!(elapsed <= 0f))
			{
				float recoveryStart = lastRifleShotTime + rifleDispersionRecoveryDelaySeconds;
				float recoveryElapsed = Mathf.Max(0f, now - Mathf.Max(now - elapsed, recoveryStart));
				rifleRecoilDispersionMilliradians = Mathf.MoveTowards(rifleRecoilDispersionMilliradians, 0f, rifleDispersionRecoveryMilliradiansPerSecond * recoveryElapsed);
				if (now - lastRifleShotTime >= rifleBurstResetSeconds)
				{
					rifleBurstShotCount = 0;
				}
			}
		}

		private void RegisterRifleRecoil(float now)
		{
			if (now - lastRifleShotTime >= rifleBurstResetSeconds)
			{
				rifleBurstShotCount = 0;
			}
			float recoilPerShot = ((rifleBurstShotCount < rifleLowRecoilShotCount) ? rifleLowRecoilPerShotMilliradians : rifleSustainedRecoilPerShotMilliradians);
			float maximumRecoil = Mathf.Max(0f, rifleMaximumDispersionMilliradians - rifleBaseDispersionMilliradians);
			rifleRecoilDispersionMilliradians = Mathf.Min(maximumRecoil, rifleRecoilDispersionMilliradians + recoilPerShot);
			rifleBurstShotCount++;
			lastRifleShotTime = now;
		}

		private Vector3 ApplyRifleDispersion(Vector3 forward)
		{
			if (forward.sqrMagnitude <= 1E-06f)
			{
				return base.transform.forward;
			}
			forward.Normalize();
			float dispersionMilliradians = Mathf.Clamp(rifleBaseDispersionMilliradians + rifleRecoilDispersionMilliradians, 0f, Mathf.Max(rifleBaseDispersionMilliradians, rifleMaximumDispersionMilliradians));
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
			if (rifleReloading && Time.time >= rifleReloadCompleteTime)
			{
				rifleReloading = false;
				if (rifleMagazinesRemaining > 0)
				{
					rifleRoundsRemaining = rifleMagazineCapacity;
				}
			}
			if (cannonReloading && Time.time >= cannonReloadCompleteTime)
			{
				cannonReloading = false;
				if (cannonRoundsRemaining == 0 && cannonMagazinesRemaining > 0)
				{
					cannonRoundsRemaining = cannonMagazineCapacity;
				}
			}
		}

		private void BeginRifleReload(Vector3 weaponPosition)
		{
			if (!rifleReloading && rifleMagazinesRemaining > 0)
			{
				rifleReloading = true;
				rifleReloadCompleteTime = Time.time + Mathf.Max(0.1f, rifleReloadSeconds);
				EnsureHandWeaponAudio().PlayRifleReload(weaponPosition, IsOwnship());
			}
		}

		private void BeginCannonReload(Vector3 weaponPosition)
		{
			if (!cannonReloading && cannonMagazinesRemaining > 0)
			{
				cannonReloading = true;
				cannonReloadCompleteTime = Time.time + Mathf.Max(0.1f, cannonReloadSeconds);
				EnsureHandWeaponAudio().PlayCannonReload(weaponPosition, IsOwnship());
			}
		}

		private void PlayHandWeaponAudio(TestWeaponKind kind, MechPartSlot slot, Transform muzzle)
		{
			MechHandWeaponAudio audio = EnsureHandWeaponAudio();
			bool ownship = IsOwnship();
			switch (kind)
			{
			case TestWeaponKind.Rifle:
				audio.SetRifleTriggerHeld(ownship && input.Current.SecondaryFireHeld);
				audio.PlayRifleShot(muzzle.position, ownship, ResolveRifleAudioProfile(slot));
				break;
			case TestWeaponKind.Cannon:
				audio.PlayCannonShot(muzzle.position, ownship);
				break;
			}
		}

		private AutomaticCannonAudioProfile ResolveRifleAudioProfile(MechPartSlot slot)
		{
			if ((object)assembler == null)
			{
				assembler = GetComponent<MechAssembler>();
			}
			if (assembler == null || assembler.Loadout == null || !assembler.Loadout.TryGetPart(slot, out var part) || part == null)
			{
				return AutomaticCannonAudioProfile.Flak38;
			}
			if (PartIdMatches(part.PartId, "WP_R_1720"))
			{
				return AutomaticCannonAudioProfile.Legacy2A42;
			}
			if (!PartIdMatches(part.PartId, "WP_R_1630"))
			{
				return AutomaticCannonAudioProfile.Flak38;
			}
			return AutomaticCannonAudioProfile.Flak43;
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

		private void FireInterceptorMissile(ref int muzzleIndex)
		{
			if (IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon))
			{
				return;
			}
			InitializeAmmunition();
			if (!HasLeftMissileAmmunition)
			{
				return;
			}
			UpdateInterceptorTarget();
			MissileTrackSnapshot incomingTrack = interceptorTarget;
			if (!incomingTrack.TrackId.IsValid || !incomingTrack.IsWeaponsQuality || !incomingTrack.Incoming)
			{
				Debug.LogWarning("[MechTestWeaponSystem] Interceptor launch inhibited: no sensor-confirmed incoming missile track.", this);
				return;
			}
			if (!muzzlesBySlot.TryGetValue(MechPartSlot.LeftBackWeapon, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(MechPartSlot.LeftBackWeapon, out muzzles) || muzzles.Length == 0)
			{
				Debug.LogWarning("[MechTestWeaponSystem] No interceptor muzzle.", this);
				return;
			}
			Transform muzzle = muzzles[muzzleIndex % muzzles.Length];
			if (!TrySolveInterceptorEngagement(in incomingTrack, muzzle, out var launchSolution))
			{
				Debug.LogWarning("[MechTestWeaponSystem] Interceptor launch inhibited: the current threat geometry is outside the dynamic interceptor reachability zone.", this);
				return;
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
			SpawnProjectile(launchPosition, launchDirection, TestWeaponKind.Missile, interceptorMissileProfile.EjectionSpeedMps, 300f, 0.22f, interceptorMissileProfile.MaximumFlightTimeSeconds, incomingTrack.TrackId, 0f, 0f, 0f, 0, 6082101, 0, muzzle, interceptorMissileProfile.MaximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity, null, radarController, MechPartSlot.LeftBackWeapon, interceptorMissile: true, null, interceptorMissileProfile, hasInitialGuidanceEstimate: true, ToVector3(incomingTrack.PredictPosition(radarController.SimTimeSeconds)), ToVector3(incomingTrack.Velocity), incomingTrack.PositionUncertaintyMeters, ToVector3(launchSolution.InterceptPoint), coldLaunch: true, interceptorSafeSeparationSeconds, interceptorSafeSeparationMeters, interceptorIgnitionAlignmentDegrees, interceptorAlignmentTimeoutSeconds);
			ConsumeMissileRound(ref leftMissileRoundsRemaining, leftMissileMagazineCapacity, ref leftMissileMagazinesRemaining);
			Debug.Log("[MechTestWeaponSystem] Interceptor launched at " + $"{incomingTrack.TrackId}, sources=" + $"{incomingTrack.SourceMask}, confidence=" + $"{incomingTrack.Confidence:0.00}, TOF=" + $"{launchSolution.FlightTimeSeconds:0.00}s, margin=" + $"{launchSolution.ThreatTimeMarginSeconds:0.00}s.", this);
			muzzleIndex = (muzzleIndex + 1) % muzzles.Length;
			interceptorTarget = default(MissileTrackSnapshot);
			interceptorSolution = default(InterceptorEngagementSolution);
			interceptorLockUntil = 0f;
		}

		private void FireMissileSalvo(MechPartSlot slot, ref int muzzleIndex)
		{
			if (IsShaoWeiEquipped(slot))
			{
				return;
			}
			if (slot == MechPartSlot.LeftBackWeapon)
			{
				FireInterceptorMissile(ref muzzleIndex);
				return;
			}
			InitializeAmmunition();
			if (!HasRightMissileAmmunition)
			{
				return;
			}
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
			{
				Debug.LogWarning($"[MechTestWeaponSystem] No missile muzzle for {slot}.", this);
				return;
			}
			MissileKinematicProfile missileKinematics = BuildMissileKinematics();
			if (radarController == null || !radarController.TryAuthorizeMissileLaunch(MissileGuidanceMode.ActiveRadar, missileLockDegrees, in missileKinematics, TrackObjectClass.AirVehicle, out var track, out var solution, out var guidanceToken))
			{
				Debug.LogWarning($"[MechTestWeaponSystem] {slot} launch inhibited: no authorized radar track, " + "weapon-envelope solution, seeker handoff, or free guidance channel.", this);
				return;
			}
			TrackId trackId = track.TrackId;
			float seekerActivationDelay = Mathf.Clamp((float)(solution.SeekerActivationTime - radarController.SimTimeSeconds), 0.5f, 8f);
			MissileFlightProfile launchProfile = airToAirMissileProfile;
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
				SpawnProjectile(launchPosition, launchDirection, TestWeaponKind.Missile, launchProfile.EjectionSpeedMps, 300f, 0.22f, launchProfile.MaximumFlightTimeSeconds, trackId, (float)i * 0.06f, 0f, 0f, (slot == MechPartSlot.LeftBackWeapon) ? 6082100 : 6083100, (slot == MechPartSlot.LeftBackWeapon) ? 6082101 : 6083101, 0, muzzle, launchProfile.MaximumRangeMeters, guidanceToken, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity, null, null, slot, interceptorMissile: false, null, launchProfile);
				ConsumeMissileRound(ref rightMissileRoundsRemaining, rightMissileMagazineCapacity, ref rightMissileMagazinesRemaining);
				launchedCount++;
			}
			muzzleIndex = (muzzleIndex + launchedCount) % muzzles.Length;
		}

		private void FireDirectInterceptorMissile(MechTestProjectile incomingMissile, ref int muzzleIndex)
		{
			if (IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon))
			{
				return;
			}
			InitializeAmmunition();
			if (!HasLeftMissileAmmunition)
			{
				return;
			}
			Transform muzzle = GetNextMuzzle(MechPartSlot.LeftBackWeapon, ref muzzleIndex);
			if (muzzle == null)
			{
				Debug.LogWarning("[MechTestWeaponSystem] No AI interceptor muzzle.", this);
				return;
			}
			Vector3 launchDirection = ResolveInterceptorColdLaunchDirection(muzzle);
			Vector3 launchPosition = muzzle.position + launchDirection * missileLaunchClearanceMeters;
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			if (!incomingMissile.TryPredictThreatTo(base.transform, inheritedVelocity, interceptorProtectedRadiusMeters, interceptorPredictionHorizonSeconds, out var timeToClosestApproach, out var closestApproachDistance))
			{
				return;
			}
			MissileTrackSnapshot directTrack = new MissileTrackSnapshot
			{
				TrackId = new TrackId(new Handle(1, 1)),
				Position = ToDouble3(incomingMissile.transform.position),
				Velocity = ToDouble3(incomingMissile.Velocity),
				PositionUncertaintyMeters = 0f,
				CpaUncertaintyMeters = 0f,
				Confidence = 1f,
				SourceMask = MissileTrackSourceMask.Radar,
				Quality = MissileTrackQuality.WeaponsQuality,
				Confirmed = true,
				Incoming = true,
				TimeToClosestApproachSeconds = timeToClosestApproach,
				ClosestApproachDistanceMeters = closestApproachDistance,
				LastEvidenceTime = Time.timeAsDouble,
				LastUpdateTime = Time.timeAsDouble
			};
			MissileKinematicProfile interceptorKinematics = BuildInterceptorKinematics();
			InterceptorEngagementSolution launchSolution = FireSolutionSolver.SolveInterceptor(in directTrack, ToDouble3(launchPosition), ToDouble3(inheritedVelocity), new float3(launchDirection.x, launchDirection.y, launchDirection.z), in interceptorKinematics, Time.timeAsDouble, interceptorMissileProfile.MaximumAngularRateDegreesPerSecond, interceptorMissileProfile.MaximumFlightTimeSeconds, interceptorMissileProfile.MaximumRangeMeters, interceptorResponseReserveSeconds, EstimateInterceptorColdLaunchDelaySeconds());
			if (launchSolution.Reachable)
			{
				InterceptorEngagementSolution refinedSolution = SolveInterceptorEngagement(in directTrack, launchPosition, inheritedVelocity, launchDirection, Time.timeAsDouble);
				if (refinedSolution.Reachable)
				{
					launchSolution = refinedSolution;
				}
				MechRadarController simulationRadar = UnityEngine.Object.FindObjectsByType<MechRadarController>(FindObjectsInactive.Exclude).FirstOrDefault((MechRadarController candidate) => candidate != null && candidate.isActiveAndEnabled);
				Vector3 direction = launchDirection;
				float ejectionSpeedMps = interceptorMissileProfile.EjectionSpeedMps;
				float maximumFlightTimeSeconds = interceptorMissileProfile.MaximumFlightTimeSeconds;
				Transform launchAnchor = muzzle;
				float maximumRangeMeters = interceptorMissileProfile.MaximumRangeMeters;
				Vector3 inheritedVelocity2 = inheritedVelocity;
				MechRadarController simulationRadarController = simulationRadar;
				MissileFlightProfile missileProfile = interceptorMissileProfile;
				Vector3 position = incomingMissile.transform.position;
				Vector3 velocity = incomingMissile.Velocity;
				Vector3 prelaunchInterceptPoint = ToVector3(launchSolution.InterceptPoint);
				float coldLaunchSafeSeparationSeconds = interceptorSafeSeparationSeconds;
				float coldLaunchSafeSeparationMeters = interceptorSafeSeparationMeters;
				float coldLaunchIgnitionAlignmentDegrees = interceptorIgnitionAlignmentDegrees;
				float coldLaunchAlignmentTimeoutSeconds = interceptorAlignmentTimeoutSeconds;
				SpawnProjectile(launchPosition, direction, TestWeaponKind.Missile, ejectionSpeedMps, 300f, 0.22f, maximumFlightTimeSeconds, default(TrackId), 0f, 0f, 0f, 0, 6082101, 0, launchAnchor, maximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity2, null, simulationRadarController, MechPartSlot.LeftBackWeapon, interceptorMissile: true, incomingMissile, missileProfile, hasInitialGuidanceEstimate: true, position, velocity, 0f, prelaunchInterceptPoint, coldLaunch: true, coldLaunchSafeSeparationSeconds, coldLaunchSafeSeparationMeters, coldLaunchIgnitionAlignmentDegrees, coldLaunchAlignmentTimeoutSeconds);
				ConsumeMissileRound(ref leftMissileRoundsRemaining, leftMissileMagazineCapacity, ref leftMissileMagazinesRemaining);
			}
		}

		private void FireDirectMissileSalvo(MechPartSlot slot, CombatTestTarget directTarget, ref int muzzleIndex, bool hasInitialEstimate = false, Vector3 initialEstimatePosition = default(Vector3), Vector3 initialEstimateVelocity = default(Vector3), float initialEstimateUncertaintyMeters = 0f)
		{
			if (IsShaoWeiEquipped(slot))
			{
				return;
			}
			if (slot == MechPartSlot.LeftBackWeapon)
			{
				FireInterceptorMissile(ref muzzleIndex);
				return;
			}
			InitializeAmmunition();
			if (!HasRightMissileAmmunition)
			{
				return;
			}
			if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(slot, out muzzles) || muzzles.Length == 0)
			{
				Debug.LogWarning($"[MechTestWeaponSystem] No missile muzzle for {slot}.", this);
				return;
			}
			Transform firingCombatant = ResolveCombatantRoot(base.transform);
			MechRadarController simulationRadar = UnityEngine.Object.FindObjectsByType<MechRadarController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).FirstOrDefault((MechRadarController candidate) => candidate != null && candidate.isActiveAndEnabled && ResolveCombatantRoot(candidate.transform) != firingCombatant);
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			for (int i = 0; i < 1; i++)
			{
				Transform muzzle = muzzles[(muzzleIndex + i) % muzzles.Length];
				Vector3 launchDirection = (hasInitialEstimate ? ResolveDirectMissileLaunchDirection(muzzle, initialEstimatePosition) : ResolveDirectMissileLaunchDirection(muzzle, directTarget));
				Vector3 launchPosition = muzzle.position + launchDirection * missileLaunchClearanceMeters;
				float ejectionSpeedMps = airToAirMissileProfile.EjectionSpeedMps;
				float maximumFlightTimeSeconds = airToAirMissileProfile.MaximumFlightTimeSeconds;
				float launchDelay = (float)i * 0.06f;
				int launchFfxId = ((slot == MechPartSlot.LeftBackWeapon) ? 6082100 : 6083100);
				int projectileFfxId = ((slot == MechPartSlot.LeftBackWeapon) ? 6082101 : 6083101);
				Transform launchAnchor = muzzle;
				float maximumRangeMeters = airToAirMissileProfile.MaximumRangeMeters;
				Vector3 inheritedVelocity2 = inheritedVelocity;
				MechRadarController simulationRadarController = simulationRadar;
				MissileFlightProfile missileProfile = airToAirMissileProfile;
				bool hasInitialGuidanceEstimate = hasInitialEstimate;
				SpawnProjectile(launchPosition, launchDirection, TestWeaponKind.Missile, ejectionSpeedMps, 300f, 0.22f, maximumFlightTimeSeconds, default(TrackId), launchDelay, 0f, 0f, launchFfxId, projectileFfxId, 0, launchAnchor, maximumRangeMeters, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity2, directTarget, simulationRadarController, slot, interceptorMissile: false, null, missileProfile, hasInitialGuidanceEstimate, initialEstimatePosition, initialEstimateVelocity, initialEstimateUncertaintyMeters);
				ConsumeMissileRound(ref rightMissileRoundsRemaining, rightMissileMagazineCapacity, ref rightMissileMagazinesRemaining);
			}
			muzzleIndex = (muzzleIndex + 1) % muzzles.Length;
		}

		private static Transform ResolveCombatantRoot(Transform source)
		{
			if (source == null)
			{
				return null;
			}
			MechPlayerRig rig = source.GetComponentInParent<MechPlayerRig>();
			if (rig != null)
			{
				return rig.transform;
			}
			CombatTestTarget target = source.GetComponentInParent<CombatTestTarget>();
			if (!(target != null))
			{
				return source;
			}
			return target.transform;
		}

		private void UpdateInterceptorTarget()
		{
			MissileTrackSnapshot candidate;
			InterceptorEngagementSolution candidateSolution;
			MissileTrackSnapshot retainedTrack;
			Transform retainedMuzzle;
			InterceptorEngagementSolution retainedSolution;
			if (IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon))
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
			else if (!interceptorTarget.TrackId.IsValid || radarController == null || !radarController.TryGetMissileTrack(interceptorTarget.TrackId, out retainedTrack) || !retainedTrack.IsWeaponsQuality || !retainedTrack.Incoming || !TryPeekInterceptorMuzzle(out retainedMuzzle) || !TrySolveInterceptorEngagement(in retainedTrack, retainedMuzzle, out retainedSolution) || Time.unscaledTime >= interceptorLockUntil)
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
				if (candidate.TrackId.IsValid && candidate.IsWeaponsQuality && candidate.Incoming && MissileEngagementMath.IntersectsProtectedCpaWindow(candidate.TimeToClosestApproachSeconds, candidate.ClosestApproachDistanceMeters, candidate.CpaUncertaintyMeters, interceptorPredictionHorizonSeconds, interceptorProtectedRadiusMeters) && TrySolveInterceptorEngagement(in candidate, muzzle, out var solution))
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
			if (IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon))
			{
				muzzle = null;
				return false;
			}
			if (!muzzlesBySlot.TryGetValue(MechPartSlot.LeftBackWeapon, out var muzzles) || muzzles.Length == 0)
			{
				ResolveMuzzles();
			}
			if (!muzzlesBySlot.TryGetValue(MechPartSlot.LeftBackWeapon, out muzzles) || muzzles.Length == 0)
			{
				muzzle = null;
				return false;
			}
			muzzle = muzzles[leftBackMuzzleIndex % muzzles.Length];
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
			MissileKinematicProfile kinematics = BuildInterceptorKinematics();
			return FireSolutionSolver.SolveInterceptor(in track, ToDouble3(launchPosition), ToDouble3(ownshipVelocity), new float3(launchDirection.x, launchDirection.y, launchDirection.z), in kinematics, requestTime, interceptorMissileProfile.MaximumAngularRateDegreesPerSecond, interceptorMissileProfile.MaximumFlightTimeSeconds, interceptorMissileProfile.MaximumRangeMeters, interceptorResponseReserveSeconds, EstimateInterceptorColdLaunchDelaySeconds());
		}

		private static Vector3 ResolveDirectMissileLaunchDirection(Transform muzzle, CombatTestTarget directTarget)
		{
			if (muzzle != null && directTarget != null && directTarget.AimPoint != null)
			{
				Vector3 toTarget = directTarget.AimPoint.position - muzzle.position;
				if (toTarget.sqrMagnitude > 0.001f)
				{
					return toTarget.normalized;
				}
			}
			if (!(muzzle != null))
			{
				return Vector3.forward;
			}
			return muzzle.forward;
		}

		private static Vector3 ResolveDirectMissileLaunchDirection(Transform muzzle, Vector3 estimatedPosition)
		{
			if (muzzle != null)
			{
				Vector3 toEstimate = estimatedPosition - muzzle.position;
				if (toEstimate.sqrMagnitude > 0.001f)
				{
					return toEstimate.normalized;
				}
				return muzzle.forward;
			}
			return Vector3.forward;
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

		private float EstimateInterceptorColdLaunchDelaySeconds()
		{
			float ejectionSpeed = Mathf.Max(interceptorMissileProfile.EjectionSpeedMps, 0.01f);
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

		private void SpawnProjectile(Vector3 position, Vector3 direction, TestWeaponKind kind, float speed, float damage, float radius, float lifetime, TrackId homingTrack = default(TrackId), float launchDelay = 0f, float acceleration = 0f, float turnRateDegrees = 0f, int launchFfxId = 0, int projectileFfxId = 0, int impactFfxId = 0, Transform launchAnchor = null, float maximumTravelDistance = 0f, int guidanceChannelToken = 0, float motorBurnDuration = 2.1f, float seekerActivationDelay = 2.5f, float seekerMaximumRange = 8000f, float seekerHalfAngleDegrees = 35f, float seekerGimbalDegrees = 60f, float maximumLateralAcceleration = 75f, float maneuverDeltaV = 180f, float rcsLateralAcceleration = 0f, float rcsDeltaV = 0f, float proximityFuseRadius = 12f, float proximityFuseArmTime = 0.75f, float proximityFuseSafeDistance = 20f, float warheadLethalRadius = 18f, Vector3 inheritedVelocity = default(Vector3), CombatTestTarget directGuidanceTarget = null, MechRadarController simulationRadarController = null, MechPartSlot missileSourceSlot = MechPartSlot.None, bool interceptorMissile = false, MechTestProjectile directProjectileTarget = null, MissileFlightProfile missileProfile = default(MissileFlightProfile), bool hasInitialGuidanceEstimate = false, Vector3 initialGuidancePosition = default(Vector3), Vector3 initialGuidanceVelocity = default(Vector3), float initialGuidanceUncertaintyMeters = 0f, Vector3 prelaunchInterceptPoint = default(Vector3), bool coldLaunch = false, float coldLaunchSafeSeparationSeconds = 0f, float coldLaunchSafeSeparationMeters = 0f, float coldLaunchIgnitionAlignmentDegrees = 8f, float coldLaunchAlignmentTimeoutSeconds = 0.6f, float interceptorRoundLethalRadius = 0f, float interceptorRoundDamage = 0f, float interceptorRoundArmSeconds = 0f, float interceptorRoundSafeDistance = 0f, bool cannonProximityFuseEnabled = false)
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
			SpawnNativeOneShot(launchFfxId, launchAnchor, (launchAnchor != null) ? launchAnchor.position : position, direction, kind != TestWeaponKind.Missile, ResolveMuzzleFlashVisibleSeconds(kind));
			GameObject projectile = GameObject.CreatePrimitive((kind == TestWeaponKind.Missile) ? PrimitiveType.Cube : PrimitiveType.Sphere);
			projectile.name = $"Test_{kind}_Projectile";
			Vector3 upReference = ((Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) > 0.95f) ? Vector3.forward : Vector3.up);
			projectile.transform.SetPositionAndRotation(position, Quaternion.LookRotation(direction, upReference));
			Transform transform = projectile.transform;
			transform.localScale = kind switch
			{
				TestWeaponKind.Rifle => Vector3.one * 0.12f, 
				TestWeaponKind.Cannon => Vector3.one * 0.32f, 
				_ => new Vector3(0.18f, 0.18f, 0.8f), 
			};
			Collider collider = projectile.GetComponent<Collider>();
			if (kind == TestWeaponKind.Missile)
			{
				ConfigureMissileHitbox(collider, projectile.transform.localScale, missileSourceSlot);
			}
			else if (collider != null)
			{
				UnityEngine.Object.Destroy(collider);
			}
			Renderer placeholderRenderer = projectile.GetComponent<Renderer>();
			placeholderRenderer.sharedMaterial = GetProjectileMaterial(kind);
			if (kind == TestWeaponKind.Missile)
			{
				TryAttachMissileVisual(projectile.transform, placeholderRenderer, aimingCamera, missileSourceSlot);
			}
			switch (kind)
			{
			case TestWeaponKind.Rifle:
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
				projectileBehaviour.ConfigureInterceptorRound(interceptorRoundLethalRadius, interceptorRoundDamage, interceptorRoundArmSeconds, interceptorRoundSafeDistance);
			}
			projectileBehaviour.Configure(base.transform, (simulationRadarController != null) ? simulationRadarController : radarController, homingTrack, kind, inheritedVelocity + direction * speed, damage, radius, lifetime, launchDelay, acceleration, turnRateDegrees, nativeEffectLibrary, impactFfxId, maximumTravelDistance, guidanceChannelToken, motorBurnDuration, seekerActivationDelay, seekerMaximumRange, seekerHalfAngleDegrees, seekerGimbalDegrees, maximumLateralAcceleration, maneuverDeltaV, rcsLateralAcceleration, rcsDeltaV, proximityFuseRadius, proximityFuseArmTime, proximityFuseSafeDistance, warheadLethalRadius, directGuidanceTarget, interceptorMissile, directProjectileTarget, missileProfile, hasInitialGuidanceEstimate, initialGuidancePosition, initialGuidanceVelocity, initialGuidanceUncertaintyMeters, prelaunchInterceptPoint, coldLaunch, inheritedVelocity, coldLaunchSafeSeparationSeconds, coldLaunchSafeSeparationMeters, coldLaunchIgnitionAlignmentDegrees, coldLaunchAlignmentTimeoutSeconds, ownshipMissileAudio);
			if (kind == TestWeaponKind.Cannon && cannonProximityFuseEnabled)
			{
				projectileBehaviour.ConfigureCannonProximityFuse(proximityFuseRadius, proximityFuseArmTime, proximityFuseSafeDistance, warheadLethalRadius);
			}
		}

		private bool TryGetAimPoint(float coneDegrees, float projectileSpeed, out Vector3 aimPoint, out TrackId trackId)
		{
			Ray ray = GetAimRay();
			if (radarController != null && radarController.TryGetFireSolution(coneDegrees, projectileSpeed, out var track, out var solution))
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
			Ray aimRay = GetAimRay();
			MissileTrackSnapshot retainedTrack;
			if (TryFindBestPointDefenseTarget(aimRay, pointDefenseLockDegrees, pointDefenseMaximumRangeMeters, out var candidate))
			{
				pointDefenseTarget = candidate;
				pointDefenseLockUntil = Time.unscaledTime + pointDefenseLockHoldSeconds;
				radarController.CuePointDefenseTrack(candidate.TrackId);
			}
			else if (!pointDefenseTarget.TrackId.IsValid || radarController == null || !radarController.TryGetMissileTrack(pointDefenseTarget.TrackId, out retainedTrack) || !retainedTrack.IsWeaponsQuality || !retainedTrack.Incoming || Time.unscaledTime >= pointDefenseLockUntil)
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
			if (!IsShaoWeiPlatformOperational() || !enableShaoWeiPointDefense || (!LeftShaoWeiInstalled && !RightShaoWeiInstalled))
			{
				shaoWeiPointDefenseTarget = default(MissileTrackSnapshot);
				shaoWeiPointDefenseHitProbability = 0f;
				shaoWeiPointDefenseWithholdReason = PointDefenseWithholdReason.None;
				radarController?.CuePointDefenseTrack(pointDefenseTarget.TrackId);
				return;
			}
			Vector3 origin = base.transform.position + Vector3.up * 2f;
			Vector3 forward = ((flightMotion != null) ? (flightMotion.LogicalRotation * Vector3.forward) : base.transform.forward);
			forward = ((forward.sqrMagnitude > 0.001f) ? forward.normalized : Vector3.forward);
			MissileTrackSnapshot retainedTrack;
			if (TryFindBestShaoWeiPointDefenseTarget(origin, forward, out var candidate))
			{
				shaoWeiPointDefenseTarget = candidate;
				shaoWeiPointDefenseLockUntil = Time.unscaledTime + shaoWeiTrackHoldSeconds;
				radarController.CuePointDefenseTrack(candidate.TrackId);
			}
			else if (!shaoWeiPointDefenseTarget.TrackId.IsValid || radarController == null || !radarController.TryGetMissileTrack(shaoWeiPointDefenseTarget.TrackId, out retainedTrack) || !IsShaoWeiThreatEligible(in retainedTrack, origin, forward) || Time.unscaledTime >= shaoWeiPointDefenseLockUntil)
			{
				shaoWeiPointDefenseTarget = default(MissileTrackSnapshot);
				shaoWeiPointDefenseHitProbability = 0f;
				shaoWeiPointDefenseWithholdReason = PointDefenseWithholdReason.None;
				radarController?.CuePointDefenseTrack(pointDefenseTarget.TrackId);
			}
			else
			{
				shaoWeiPointDefenseTarget = retainedTrack;
				radarController.CuePointDefenseTrack(retainedTrack.TrackId);
			}
		}

		private bool TryFindBestShaoWeiPointDefenseTarget(Vector3 origin, Vector3 forward, out MissileTrackSnapshot best)
		{
			best = default(MissileTrackSnapshot);
			if (radarController == null)
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
					float score = Mathf.Max(0f, candidate.TimeToClosestApproachSeconds) + Mathf.Max(0f, candidate.ClosestApproachDistanceMeters) / 1000f + Mathf.Max(0f, candidate.CpaUncertaintyMeters) / 500f + range / Mathf.Max(1f, shaoWeiMaximumRangeMeters) * 0.25f - candidate.Confidence * 0.1f;
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
			if (!candidate.TrackId.IsValid || !candidate.IsWeaponsQuality || !candidate.Incoming || candidate.TimeToClosestApproachSeconds < 0f || candidate.TimeToClosestApproachSeconds > shaoWeiMaximumThreatTimeSeconds)
			{
				return false;
			}
			double3 predictedPosition = candidate.PredictPosition((radarController != null) ? radarController.SimTimeSeconds : candidate.LastUpdateTime);
			Vector3 toCandidate = new Vector3((float)predictedPosition.x, (float)predictedPosition.y, (float)predictedPosition.z) - origin;
			float rangeSquared = toCandidate.sqrMagnitude;
			if (rangeSquared <= 0.01f || rangeSquared > shaoWeiMaximumRangeMeters * shaoWeiMaximumRangeMeters)
			{
				return false;
			}
			return Vector3.Dot(forward, toCandidate / Mathf.Sqrt(rangeSquared)) >= Mathf.Cos(shaoWeiMaximumOffBoresightDegrees * (MathF.PI / 180f));
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
			bool installed = IsShaoWeiEquipped(slot);
			bool hasTarget = shaoWeiPointDefenseTarget.TrackId.IsValid;
			bool available = enableShaoWeiPointDefense && IsShaoWeiPlatformOperational() && installed && hasTarget && state.RoundsRemaining > 0 && !state.Reloading && !state.Overheated;
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
			float heatFraction = Mathf.Clamp01(state.HeatMj / Mathf.Max(0.1f, shaoWeiHeatCapacityMj));
			float dispersion = Mathf.Lerp(shaoWeiBaseDispersionMilliradians, shaoWeiSustainedDispersionMilliradians, heatFraction);
			Vector3 aimPoint;
			float hitProbability;
			PointDefenseWithholdReason withholdReason;
			bool num = TryGetPointDefenseAimPoint(in shaoWeiPointDefenseTarget, muzzle.position, shaoWeiMuzzleVelocityMetersPerSecond, shaoWeiMaximumRangeMeters, shaoWeiMaximumLeadSeconds, dispersion, shaoWeiRoundLethalRadiusMeters, pointDefenseMinimumHitProbability, out aimPoint, out hitProbability, out withholdReason);
			if (aimPoint.sqrMagnitude > 0.001f)
			{
				state.AimPoint = aimPoint;
				state.HasAimPoint = true;
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
			Vector3 actualDirection = ((state.AimPivot != null) ? (muzzle.position - state.AimPivot.position) : muzzle.forward);
			if (actualDirection.sqrMagnitude <= 0.001f)
			{
				return;
			}
			state.SlewedDirection = actualDirection.normalized;
			state.HasSlewedDirection = true;
			if (Vector3.Angle(actualDirection, desiredDirection) > 2.5f)
			{
				return;
			}
			Vector3 inheritedVelocity = ((flightMotion != null) ? flightMotion.Velocity : Vector3.zero);
			Vector3 firingDirection = ApplyShaoWeiDispersion(state.SlewedDirection, dispersion, state);
			Vector3 position = muzzle.position;
			float speed = shaoWeiMuzzleVelocityMetersPerSecond;
			float damage = shaoWeiDirectHitDamage;
			float lifetime = shaoWeiProjectileLifetimeSeconds;
			Transform launchAnchor = muzzle;
			Vector3 inheritedVelocity2 = inheritedVelocity;
			float interceptorRoundLethalRadius = shaoWeiRoundLethalRadiusMeters;
			float interceptorRoundDamage = shaoWeiInterceptionDamage;
			float interceptorRoundArmSeconds = shaoWeiRoundArmSeconds;
			float interceptorRoundSafeDistance = shaoWeiRoundSafeDistanceMeters;
			SpawnProjectile(position, firingDirection, TestWeaponKind.Rifle, speed, damage, 0.08f, lifetime, default(TrackId), 0f, 0f, 0f, 6011100, 6013101, 6011101, launchAnchor, 0f, 0, 2.1f, 2.5f, 8000f, 35f, 60f, 75f, 180f, 0f, 0f, 12f, 0.75f, 20f, 18f, inheritedVelocity2, null, null, MechPartSlot.None, interceptorMissile: false, null, default(MissileFlightProfile), hasInitialGuidanceEstimate: false, default(Vector3), default(Vector3), 0f, default(Vector3), coldLaunch: false, 0f, 0f, 8f, 0.6f, interceptorRoundLethalRadius, interceptorRoundDamage, interceptorRoundArmSeconds, interceptorRoundSafeDistance);
			EnsureHandWeaponAudio().PlayRifleShot(muzzle.position, IsOwnship(), AutomaticCannonAudioProfile.Flak38);
			state.RoundsRemaining = Mathf.Max(0, state.RoundsRemaining - 1);
			state.HeatMj = Mathf.Min(shaoWeiHeatCapacityMj, state.HeatMj + shaoWeiHeatPerShotMj);
			state.NextFireTime = Time.time + 60f / Mathf.Max(60f, shaoWeiRoundsPerMinute);
			if (state.RoundsRemaining == 0)
			{
				state.MagazinesRemaining = Mathf.Max(0, state.MagazinesRemaining - 1);
				if (state.MagazinesRemaining > 0)
				{
					BeginShaoWeiReload(state);
				}
			}
			if (state.HeatMj >= shaoWeiHeatCapacityMj - 0.0001f)
			{
				state.Overheated = true;
				SetShaoWeiSpinning(slot, state, shouldSpin: false);
			}
		}

		private void SetShaoWeiSpinning(MechPartSlot slot, ShaoWeiWeaponState state, bool shouldSpin)
		{
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
					state.SpinReadyTime = Time.time + Mathf.Max(0f, shaoWeiSpinUpSeconds);
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
				state.HasSlewedDirection = false;
				state.HasAimPoint = false;
				if (player != null && player.HasAction("a000_800202"))
				{
					player.PlayActionPreservingPose("a000_800202");
				}
			}
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
				if (!candidate.TrackId.IsValid || !candidate.IsWeaponsQuality || !candidate.Incoming)
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

		private bool TryGetPointDefenseAimPoint(Vector3 shooterPosition, float projectileSpeed, out Vector3 aimPoint)
		{
			return TryGetPointDefenseAimPoint(in pointDefenseTarget, shooterPosition, projectileSpeed, pointDefenseMaximumRangeMeters, pointDefenseMaximumLeadSeconds, rifleBaseDispersionMilliradians + rifleRecoilDispersionMilliradians, pointDefenseRoundLethalRadiusMeters, pointDefenseMinimumHitProbability, out aimPoint, out pointDefenseHitProbability, out pointDefenseWithholdReason);
		}

		private bool TryGetPointDefenseAimPoint(in MissileTrackSnapshot track, Vector3 shooterPosition, float projectileSpeed, float maximumRangeMeters, float maximumLeadSeconds, float dispersionMilliradians, float lethalRadiusMeters, float minimumHitProbability, out Vector3 aimPoint, out float hitProbability, out PointDefenseWithholdReason withholdReason)
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
			hitProbability = EstimatePointDefenseHitProbability(in track, interceptRange, projectileSpeed, dispersionMilliradians, lethalRadiusMeters);
			if (hitProbability < minimumHitProbability)
			{
				withholdReason = ((interceptRange > track.PositionUncertaintyMeters * 400f) ? PointDefenseWithholdReason.TrackTooCoarse : PointDefenseWithholdReason.RangeTooLong);
				return false;
			}
			withholdReason = PointDefenseWithholdReason.None;
			return true;
		}

		private float EstimatePointDefenseHitProbability(in MissileTrackSnapshot track, float interceptRange, float projectileSpeed, float dispersionMilliradians, float configuredLethalRadiusMeters)
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

		private static void ConfigureMissileHitbox(Collider collider, Vector3 projectileScale, MechPartSlot sourceSlot)
		{
			if (!(collider == null))
			{
				collider.isTrigger = true;
				if (collider is BoxCollider box)
				{
					float missileLengthMeters = ((sourceSlot == MechPartSlot.LeftBackWeapon) ? 4f : 3.6f);
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

		private Vector2 GetReticleGuiPosition()
		{
			Vector2 viewportPoint = GetAimViewportPoint();
			if (aimingCamera == null)
			{
				return new Vector2((float)Screen.width * viewportPoint.x, (float)Screen.height * (1f - viewportPoint.y));
			}
			Vector3 screenPoint = aimingCamera.ViewportToScreenPoint(new Vector3(viewportPoint.x, viewportPoint.y, 0f));
			return new Vector2(screenPoint.x, (float)Screen.height - screenPoint.y);
		}

		private void OnGUI()
		{
			if (MechAssemblyMenu.IsAnyMenuVisible || GetComponent<MechRadarHudPresenter>() != null || aimingCamera == null || radarController == null)
			{
				return;
			}
			if (hudLabelStyle == null)
			{
				hudLabelStyle = new GUIStyle(GUI.skin.label)
				{
					alignment = TextAnchor.MiddleCenter,
					fontSize = 13,
					fontStyle = FontStyle.Bold
				};
			}
			if (radarController.TryGetBestTrack(out var track))
			{
				Vector3 trackedPosition = radarController.PredictTrackPosition(in track);
				Vector3 screen = aimingCamera.WorldToScreenPoint(trackedPosition);
				if (!(screen.z <= 0f))
				{
					Vector2 targetCenter = new Vector2(screen.x, (float)Screen.height - screen.y);
					TrackSnapshot track2;
					FireSolution solution;
					Color color = (radarController.TryGetMissileFireSolution(missileLockDegrees, BuildMissileKinematics(), out track2, out solution) ? new Color(1f, 0.35f, 0.12f) : new Color(0.25f, 0.9f, 0.95f));
					DrawCornerBrackets(targetCenter, 34f, color);
					float range = Vector3.Distance(base.transform.position, trackedPosition);
					hudLabelStyle.normal.textColor = color;
					GUI.Label(new Rect(targetCenter.x - 90f, targetCenter.y - 65f, 180f, 22f), $"{track.TrackId}  {range:0} m  {track.Lifecycle}  {track.Confidence:P0}", hudLabelStyle);
				}
			}
		}

		private MissileKinematicProfile BuildMissileKinematics()
		{
			MissileFlightProfile profile = airToAirMissileProfile;
			return new MissileKinematicProfile
			{
				EjectionSpeed = profile.EjectionSpeedMps,
				InertMass = profile.DryMassKg + profile.RcsPropellantMassKg,
				MainPropellantMass = profile.MainPropellantMassKg,
				MainEngineThrust = profile.MainEngineThrustNewtons,
				MainMassFlow = profile.MainMassFlowKgPerSecond
			};
		}

		private MissileKinematicProfile BuildInterceptorKinematics()
		{
			MissileFlightProfile profile = interceptorMissileProfile;
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

		private float ResolveMuzzleFlashVisibleSeconds(TestWeaponKind kind)
		{
			switch (kind)
			{
			case TestWeaponKind.Rifle:
			{
				float cycleSeconds = 60f / Mathf.Max(rifleRoundsPerMinute, 60f);
				return Mathf.Min(0.18f, cycleSeconds * 0.45f);
			}
			case TestWeaponKind.Cannon:
				return 0.34f;
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
				_ => new Color(1f, 0.18f, 0.06f, 1f), 
			};
		}

		private static Material GetProjectileMaterial(TestWeaponKind kind)
		{
			return kind switch
			{
				TestWeaponKind.Rifle => rifleMaterial, 
				TestWeaponKind.Cannon => cannonMaterial, 
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

		private static void TryAttachMissileVisual(Transform projectile, Renderer placeholderRenderer, Camera presentationCamera, MechPartSlot sourceSlot)
		{
			bool useLeftMissile = sourceSlot == MechPartSlot.LeftBackWeapon;
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

		private static void DrawCornerBrackets(Vector2 center, float extent, Color color)
		{
			DrawRect(new Rect(center.x - extent, center.y - extent, 12f, 2f), color);
			DrawRect(new Rect(center.x - extent, center.y - extent, 2f, 12f), color);
			DrawRect(new Rect(center.x + extent - 12f, center.y - extent, 12f, 2f), color);
			DrawRect(new Rect(center.x + extent - 2f, center.y - extent, 2f, 12f), color);
			DrawRect(new Rect(center.x - extent, center.y + extent - 2f, 12f, 2f), color);
			DrawRect(new Rect(center.x - extent, center.y + extent - 12f, 2f, 12f), color);
			DrawRect(new Rect(center.x + extent - 12f, center.y + extent - 2f, 12f, 2f), color);
			DrawRect(new Rect(center.x + extent - 2f, center.y + extent - 12f, 2f, 12f), color);
		}

		private static void DrawRect(Rect rect, Color color)
		{
			Color color2 = GUI.color;
			GUI.color = color;
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			GUI.color = color2;
		}
	}

	public struct MechWeaponPerformanceSnapshot
	{
		public string WeaponType;
		public bool IsAutomatic;
		public bool IsGuidedMissile;
		public bool IsPointDefense;
		public float DirectHitDamage;
		public float InterceptionDamage;
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
