using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(200)]
    [DisallowMultipleComponent]
    public sealed class MechTestWeaponSystem : MonoBehaviour
    {
        private const int CurrentAmmunitionConfigurationVersion = 2;
        private const string LeftMissileVisualResourcePath =
            "Projectiles/r27_white";
        private const string RightMissileVisualResourcePath =
            "Projectiles/r77_gridfin";
        private const string LeftMissileVisualEditorAssetPath =
            "Assets/_Project/LocalGameAssets/Resources/Projectiles/r27_white.glb";
        private const string RightMissileVisualEditorAssetPath =
            "Assets/_Project/LocalGameAssets/Resources/Projectiles/r77_gridfin.glb";
        private const float LeftMissileVisualLengthMeters = 4f;
        private const float RightMissileVisualLengthMeters = 3.6f;
        private const float RifleMuzzleVelocityMetersPerSecond = 1500f;
        private const float RifleProjectileLifetimeSeconds = 8f;
        private const float CannonMuzzleVelocityMetersPerSecond = 450f;
        private const float CannonProjectileLifetimeSeconds = 10f;
        private const float MuzzleFlashTimeScale = 0.4f;
        private const float MuzzleFlashMaximumVisibleSeconds = 0.18f;
        private const float CannonMuzzleFlashVisibleSeconds = 0.34f;

        [SerializeField] private MechPlayerInputAdapter input;
        [SerializeField] private MechRuntimeState runtimeState;
        [SerializeField] private MechPlayerRig playerRig;
        [SerializeField] private MechFlightMotionController flightMotion;
        [SerializeField] private Camera aimingCamera;
        private MechThirdPersonCamera aimController;
        [SerializeField] private CombatTestTarget target;
        [SerializeField] private MechRadarController radarController;
        [SerializeField] private MechEffectPrefabLibrary nativeEffectLibrary;
        [Header("Aiming")]
        [SerializeField] private Vector2 aimViewportPoint = new Vector2(0.5f, 0.56f);
        [SerializeField] private float gunAutoAimDegrees = 4f;
        [SerializeField] private float missileLockDegrees = 24f;
        [SerializeField, Min(60f)] private float rifleRoundsPerMinute = 600f;
        [Header("Hand weapon ammunition")]
        [SerializeField, Min(1)] private int cannonMagazineCapacity = 1;
        [SerializeField, Min(1)] private int cannonMagazineCount = 32;
        [SerializeField, Min(1)] private int rifleMagazineCapacity = 36;
        [SerializeField, Min(1)] private int rifleMagazineCount = 12;
        [SerializeField, Min(0.1f)] private float rifleReloadSeconds = 4.95f;
        [SerializeField, Min(0.1f)] private float cannonReloadSeconds = 4.25f;
        [Header("57 mm proximity fuze")]
        [SerializeField, Min(0f)]
        private float cannonProximityFuseRadiusMeters = 7.5f;
        [SerializeField, Min(0f)]
        private float cannonProximityFuseArmSeconds = 0.12f;
        [SerializeField, Min(0f)]
        private float cannonProximityFuseSafeDistanceMeters = 45f;
        [SerializeField, Min(0f)]
        private float cannonWarheadLethalRadiusMeters = 10f;
        [Header("Missile launcher ammunition")]
        [SerializeField, Min(1)] private int leftMissileMagazineCapacity = 16;
        [SerializeField, Min(1)] private int leftMissileMagazineCount = 2;
        [SerializeField, Min(1)] private int rightMissileMagazineCapacity = 4;
        [SerializeField, Min(1)] private int rightMissileMagazineCount = 6;
        [SerializeField, HideInInspector]
        private int ammunitionConfigurationVersion;
        [Header("30 mm rifle dispersion")]
        [SerializeField, Min(0f)] private float rifleBaseDispersionMilliradians = 0.07f;
        [SerializeField, Min(1)] private int rifleLowRecoilShotCount = 4;
        [SerializeField, Min(0f)] private float rifleLowRecoilPerShotMilliradians = 0.008f;
        [SerializeField, Min(0f)] private float rifleSustainedRecoilPerShotMilliradians = 0.022f;
        [SerializeField, Min(0f)] private float rifleMaximumDispersionMilliradians = 0.3f;
        [SerializeField, Min(0f)] private float rifleDispersionRecoveryMilliradiansPerSecond = 0.4f;
        [SerializeField, Min(0f)] private float rifleDispersionRecoveryDelaySeconds = 0.16f;
        [SerializeField, Min(0f)] private float rifleBurstResetSeconds = 0.45f;
        [Header("Point defense")]
        [SerializeField, Range(1f, 20f)] private float pointDefenseLockDegrees = 6f;
        [SerializeField, Min(100f)] private float pointDefenseMaximumRangeMeters = 6000f;
        [SerializeField, Min(0.1f)] private float pointDefenseMaximumLeadSeconds = 6f;
        [SerializeField, Min(0f)] private float pointDefenseLockHoldSeconds = 0.25f;
        // 专用拦截弹种的近炸半径。动能直击的有效命中半径只有 0.33 m（弹丸 0.08 + 导弹半宽
        // 0.25），而 3 km 处光电/雷达给出的航迹横向误差是 8~9 m 量级——直击在这个误差预算下
        // 几乎不可能。近炸半径是唯一能把命中窗口放大到有意义尺度的手段（现实 CIWS 同理）。
        // 只在锁定点防御目标时装填，普通射击仍为纯动能弹。
        [SerializeField, Min(0f)] private float pointDefenseRoundLethalRadiusMeters = 3.5f;
        // 单发命中概率低于此值就不开火。0.02 ≈ 50 发一中，600 RPM 下约 5 秒——大致是
        // 弹药消耗仍可接受的下界。调高会更省弹药但交战窗口更窄。
        [SerializeField, Range(0f, 0.5f)] private float pointDefenseMinimumHitProbability = 0.02f;
        [Header("Interceptor missile")]
        [SerializeField, Min(1f)] private float interceptorProtectedRadiusMeters = 180f;
        [SerializeField, Min(0.1f)] private float interceptorPredictionHorizonSeconds = 60f;
        [SerializeField, Min(0f)] private float interceptorLockHoldSeconds = 0.5f;
        [SerializeField, Min(0f)] private float interceptorResponseReserveSeconds = 0.2f;
        [Header("Interceptor cold launch")]
        [SerializeField, Min(0f)] private float interceptorSafeSeparationSeconds = 0.25f;
        [SerializeField, Min(0f)] private float interceptorSafeSeparationMeters = 8f;
        [SerializeField, Range(0.5f, 45f)] private float interceptorIgnitionAlignmentDegrees = 8f;
        [SerializeField, Min(0.05f)] private float interceptorAlignmentTimeoutSeconds = 0.6f;
        [Header("Air-to-air missile")]
        [SerializeField] private MissileFlightProfile airToAirMissileProfile =
            MissileFlightProfile.AirToAirDefault;
        [Header("Interceptor missile flight")]
        [SerializeField] private MissileFlightProfile interceptorMissileProfile =
            MissileFlightProfile.InterceptorDefault;
        [Header("Missile launcher")]
        [SerializeField, Min(0f)] private float missileLaunchClearanceMeters = 0.8f;

        private readonly Dictionary<MechPartSlot, Transform[]> muzzlesBySlot =
            new Dictionary<MechPartSlot, Transform[]>();
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
        public enum PointDefenseWithholdReason : byte
        {
            None = 0,
            /// <summary>航迹横向不确定度相对命中窗口过大。</summary>
            TrackTooCoarse = 1,
            /// <summary>距离过远，提前量误差被飞行时间放大到不可接受。</summary>
            RangeTooLong = 2,
        }

        private const float RifleProjectileCollisionRadiusMeters = 0.08f;
        // 与 MechTestProjectile.MissileHitboxHalfWidthMeters 对应。
        private const float MissileHitboxHalfWidthMeters = 0.25f;
        // 航迹速度估计的等效平滑窗口，用于把位置不确定度折算成速度不确定度。
        private const float PointDefenseVelocitySmoothingSeconds = 0.5f;

        private MissileTrackSnapshot pointDefenseTarget;
        private float pointDefenseHitProbability;
        private PointDefenseWithholdReason pointDefenseWithholdReason;
        private float pointDefenseLockUntil;
        private MissileTrackSnapshot interceptorTarget;
        private InterceptorEngagementSolution interceptorSolution;
        private float interceptorLockUntil;
        private MechMissileLaunchAudio missileLaunchAudio;
        private MechHandWeaponAudio handWeaponAudio;
        private readonly MissileTrackSnapshot[] interceptorTrackScratch =
            new MissileTrackSnapshot[MechRadarController.MaximumPublicTracks];

        private static Material rifleMaterial;
        private static Material cannonMaterial;
        private static Material missileMaterial;
        private static uint nextRifleDispersionSeed = 0x9e3779b9u;
        private static GameObject leftMissileVisualPrefab;
        private static GameObject rightMissileVisualPrefab;
        private static readonly HashSet<string> missileVisualLoadFailures =
            new HashSet<string>();
        private static readonly HashSet<string> missileVisualLoadSuccesses =
            new HashSet<string>();
        private static readonly Dictionary<Material, Material>
            missileUnlitMaterials =
                new Dictionary<Material, Material>();

        public MechRadarController RadarController => radarController;
        public TrackId PointDefenseTrackId =>
            pointDefenseTarget.TrackId;
        /// <summary>最近一次点防御火控解算出的单发命中概率，供 HUD 显示。</summary>
        public float PointDefenseHitProbability => pointDefenseHitProbability;
        /// <summary>不开火的原因；None 表示具备拦截条件。</summary>
        public PointDefenseWithholdReason PointDefenseWithhold =>
            pointDefenseWithholdReason;
        public TrackId InterceptorTrackId =>
            interceptorTarget.TrackId;
        public bool HasInterceptorLaunchSolution =>
            interceptorTarget.TrackId.IsValid &&
            interceptorSolution.Reachable;
        public int CannonRoundsRemaining => cannonRoundsRemaining;
        public int CannonMagazineCapacity => cannonMagazineCapacity;
        public int CannonMagazinesRemaining => cannonMagazinesRemaining;
        public int RifleRoundsRemaining => rifleRoundsRemaining;
        public int RifleMagazineCapacity => rifleMagazineCapacity;
        public int RifleMagazinesRemaining => rifleMagazinesRemaining;
        public int LeftMissileRoundsRemaining => leftMissileRoundsRemaining;
        public int LeftMissileMagazineCapacity =>
            leftMissileMagazineCapacity;
        public int LeftMissileMagazinesRemaining =>
            leftMissileMagazinesRemaining;
        public int RightMissileRoundsRemaining => rightMissileRoundsRemaining;
        public int RightMissileMagazineCapacity =>
            rightMissileMagazineCapacity;
        public int RightMissileMagazinesRemaining =>
            rightMissileMagazinesRemaining;
        public bool RifleReloading =>
            rifleReloading && Time.time < rifleReloadCompleteTime;
        public bool CannonReloading =>
            cannonReloading && Time.time < cannonReloadCompleteTime;
        public float RifleReloadRemainingSeconds => RifleReloading
            ? Mathf.Max(0f, rifleReloadCompleteTime - Time.time)
            : 0f;
        public float CannonReloadRemainingSeconds => CannonReloading
            ? Mathf.Max(0f, cannonReloadCompleteTime - Time.time)
            : 0f;
        public float RifleReloadDurationSeconds =>
            Mathf.Max(0.1f, rifleReloadSeconds);
        public float CannonReloadDurationSeconds =>
            Mathf.Max(0.1f, cannonReloadSeconds);

        public void Configure(
            MechPlayerInputAdapter newInput,
            MechRuntimeState newRuntimeState,
            Camera newAimingCamera,
            CombatTestTarget newTarget,
            MechEffectPrefabLibrary newNativeEffectLibrary = null)
        {
            input = newInput;
            runtimeState = newRuntimeState;
            playerRig = GetComponent<MechPlayerRig>();
            flightMotion = GetComponent<MechFlightMotionController>();
            aimingCamera = newAimingCamera;
            aimController = aimingCamera != null
                ? aimingCamera.GetComponent<MechThirdPersonCamera>()
                : null;
            target = newTarget;
            nativeEffectLibrary = newNativeEffectLibrary;
            radarController = GetComponent<MechRadarController>();
            if (radarController == null)
            {
                radarController = gameObject.AddComponent<MechRadarController>();
            }
            radarController.Configure(aimingCamera, target);
            ResolveMuzzles();
        }

        public void ConfigureAimViewport(Vector2 viewportPoint)
        {
            aimViewportPoint = new Vector2(
                Mathf.Clamp(viewportPoint.x, 0.05f, 0.95f),
                Mathf.Clamp(viewportPoint.y, 0.05f, 0.95f));
        }

        public void CommandFireHandWeapon(TestWeaponKind kind)
        {
            if (kind == TestWeaponKind.Missile)
            {
                return;
            }

            if (kind == TestWeaponKind.Cannon)
            {
                TryFireHandWeapon(
                    MechPartSlot.LeftHandWeapon,
                    kind,
                    ref leftHandMuzzleIndex);
                return;
            }

            TryFireHandWeapon(
                MechPartSlot.RightHandWeapon,
                kind,
                ref rightHandMuzzleIndex);
        }

        public bool RequestRifleReload()
        {
            InitializeAmmunition();
            UpdateReloadState();
            if (rifleReloading ||
                rifleRoundsRemaining >= rifleMagazineCapacity ||
                rifleMagazinesRemaining <= 1)
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
            if (cannonReloading ||
                cannonRoundsRemaining >= cannonMagazineCapacity ||
                cannonMagazinesRemaining <= 1)
            {
                return false;
            }

            cannonMagazinesRemaining--;
            cannonRoundsRemaining = 0;
            BeginCannonReload(GetWeaponPosition(MechPartSlot.LeftHandWeapon));
            return true;
        }

        public bool CommandFireHandWeaponAtEstimate(
            TestWeaponKind kind,
            Vector3 estimatedPosition,
            Vector3 estimatedVelocity)
        {
            if (kind == TestWeaponKind.Missile)
            {
                return false;
            }

            return kind == TestWeaponKind.Cannon
                ? TryFireHandWeapon(
                    MechPartSlot.LeftHandWeapon,
                    kind,
                    ref leftHandMuzzleIndex,
                    true,
                    estimatedPosition,
                    estimatedVelocity)
                : TryFireHandWeapon(
                    MechPartSlot.RightHandWeapon,
                    kind,
                    ref rightHandMuzzleIndex,
                    true,
                    estimatedPosition,
                    estimatedVelocity);
        }

        public void CommandFireMissile(MechPartSlot slot)
        {
            if (slot == MechPartSlot.LeftBackWeapon)
            {
                FireInterceptorMissile(ref leftBackMuzzleIndex);
            }
            else if (slot == MechPartSlot.RightBackWeapon)
            {
                FireMissileSalvo(slot, ref rightBackMuzzleIndex);
            }
        }

        public void CommandFireMissileAt(
            MechPartSlot slot,
            CombatTestTarget directTarget)
        {
            if (directTarget == null || !directTarget.IsAlive)
            {
                return;
            }

            if (slot == MechPartSlot.LeftBackWeapon)
            {
                FireInterceptorMissile(ref leftBackMuzzleIndex);
            }
            else if (slot == MechPartSlot.RightBackWeapon)
            {
                FireDirectMissileSalvo(
                    slot,
                    directTarget,
                    ref rightBackMuzzleIndex);
            }
        }

        public void CommandFireMissileAtEstimate(
            MechPartSlot slot,
            CombatTestTarget directTarget,
            Vector3 estimatedPosition,
            Vector3 estimatedVelocity,
            float uncertaintyMeters)
        {
            if (directTarget == null || !directTarget.IsAlive ||
                slot != MechPartSlot.RightBackWeapon)
            {
                return;
            }

            FireDirectMissileSalvo(
                slot,
                directTarget,
                ref rightBackMuzzleIndex,
                true,
                estimatedPosition,
                estimatedVelocity,
                uncertaintyMeters);
        }

        public void CommandFireInterceptorAt(
            MechTestProjectile incomingMissile)
        {
            if (incomingMissile == null ||
                !incomingMissile.IsInterceptableMissile ||
                incomingMissile.IsOwnedBy(transform))
            {
                return;
            }

            FireDirectInterceptorMissile(
                incomingMissile,
                ref leftBackMuzzleIndex);
        }

        private void Awake()
        {
            InitializeAmmunition();
            airToAirMissileProfile =
                ResolveProfile(
                    airToAirMissileProfile,
                    MissileFlightProfile.AirToAirDefault);
            interceptorMissileProfile =
                ResolveProfile(
                    interceptorMissileProfile,
                    MissileFlightProfile.InterceptorDefault);
            if (Mathf.Abs(
                    interceptorMissileProfile
                        .ProximityFuseRadiusMeters - 10f) <
                0.01f)
            {
                interceptorMissileProfile
                    .ProximityFuseRadiusMeters = 12f;
            }
            if (Mathf.Abs(
                    interceptorMissileProfile
                        .WarheadLethalRadiusMeters - 14f) <
                0.01f)
            {
                interceptorMissileProfile
                    .WarheadLethalRadiusMeters = 22f;
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

            // Passive AI test mechs retain the complete weapon stack in the
            // scene, but disabled systems must not initialize shared radar
            // simulation state during Awake.
            if (!enabled)
            {
                return;
            }

            if (input == null) input = GetComponent<MechPlayerInputAdapter>();
            if (runtimeState == null) runtimeState = GetComponent<MechRuntimeState>();
            if (playerRig == null) playerRig = GetComponent<MechPlayerRig>();
            if (flightMotion == null) flightMotion = GetComponent<MechFlightMotionController>();
            if (aimingCamera == null) aimingCamera = Camera.main;
            if (aimController == null && aimingCamera != null)
            {
                aimController = aimingCamera.GetComponent<MechThirdPersonCamera>();
            }
            EnsureRadarController();
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
                radarController = gameObject.AddComponent<MechRadarController>();
            }

            if (radarController != null && target != null)
            {
                radarController.Configure(aimingCamera, target);
            }
        }

        private void Update()
        {
            UpdateReloadState();
            if (input == null)
            {
                return;
            }

            UpdateInterceptorTarget();
            UpdatePointDefenseTarget();
            var frame = input.Current;
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
                TryFireHandWeapon(
                    MechPartSlot.LeftHandWeapon,
                    TestWeaponKind.Cannon,
                    ref leftHandMuzzleIndex);
            }
            if (frame.SecondaryFireHeld)
            {
                if (frame.SecondaryFireEdge ||
                    Time.time >= nextRifleFireTime)
                {
                    var fired = TryFireHandWeapon(
                        MechPartSlot.RightHandWeapon,
                        TestWeaponKind.Rifle,
                        ref rightHandMuzzleIndex);
                    if (fired)
                    {
                        nextRifleFireTime =
                            Time.time +
                            60f / Mathf.Max(rifleRoundsPerMinute, 60f);
                    }
                }
            }
            else
            {
                nextRifleFireTime = 0f;
            }
            handWeaponAudio?.SetRifleTriggerHeld(
                frame.SecondaryFireHeld &&
                !RifleReloading &&
                rifleRoundsRemaining > 0);
            if (playerRig != null &&
                playerRig.ConsumeShoulderMissileLaunch(MechPartSlot.LeftBackWeapon))
            {
                FireInterceptorMissile(ref leftBackMuzzleIndex);
            }
            if (playerRig != null &&
                playerRig.ConsumeShoulderMissileLaunch(MechPartSlot.RightBackWeapon))
            {
                FireMissileSalvo(MechPartSlot.RightBackWeapon, ref rightBackMuzzleIndex);
            }
        }

        private void ResolveMuzzles()
        {
            muzzlesBySlot.Clear();
            if (runtimeState == null)
            {
                return;
            }

            for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
            {
                var record = runtimeState.AssembledParts[i];
                if (record?.instance == null || !MechSlotRules.IsWeaponSlot(record.slot))
                {
                    continue;
                }

                var candidates = record.instance.GetComponentsInChildren<Transform>(true)
                    .Where(IsMuzzleTransform)
                    .OrderBy(candidate => candidate.name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                muzzlesBySlot[record.slot] = candidates.Length > 0
                    ? candidates
                    : new[] { record.instance.transform };
            }
        }

        private Vector3 GetWeaponPosition(MechPartSlot slot)
        {
            if (!muzzlesBySlot.TryGetValue(slot, out var muzzles) ||
                muzzles.Length == 0)
            {
                ResolveMuzzles();
            }

            return muzzlesBySlot.TryGetValue(slot, out muzzles) &&
                   muzzles.Length > 0 &&
                   muzzles[0] != null
                ? muzzles[0].position
                : transform.position;
        }

        private bool TryFireHandWeapon(
            MechPartSlot slot,
            TestWeaponKind kind,
            ref int muzzleIndex,
            bool hasExternalTrack = false,
            Vector3 externalTrackPosition = default,
            Vector3 externalTrackVelocity = default)
        {
            InitializeAmmunition();
            UpdateReloadState();
            UpdateRifleDispersion(Time.time);
            if (kind == TestWeaponKind.Rifle)
            {
                if (rifleReloading)
                {
                    return false;
                }

                if (rifleRoundsRemaining <= 0)
                {
                    if (rifleMagazinesRemaining > 0)
                    {
                        BeginRifleReload(transform.position);
                    }
                    return false;
                }
            }
            else if (kind == TestWeaponKind.Cannon)
            {
                if (cannonReloading)
                {
                    return false;
                }

                if (cannonRoundsRemaining <= 0)
                {
                    if (cannonMagazinesRemaining > 0)
                    {
                        BeginCannonReload(transform.position);
                    }
                    return false;
                }
            }

            var muzzle = GetNextMuzzle(slot, ref muzzleIndex);
            if (muzzle == null)
            {
                Debug.LogWarning($"[MechTestWeaponSystem] No muzzle for {slot}.", this);
                return false;
            }

            var projectileSpeed = kind == TestWeaponKind.Rifle
                ? RifleMuzzleVelocityMetersPerSecond
                : CannonMuzzleVelocityMetersPerSecond;
            var inheritedVelocity =
                flightMotion != null
                    ? flightMotion.Velocity
                    : Vector3.zero;
            var aimPoint = Vector3.zero;
            var locked = false;
            var pointDefenseEngaged = false;
            if (hasExternalTrack)
            {
                var relativePosition =
                    externalTrackPosition - muzzle.position;
                var relativeVelocity =
                    externalTrackVelocity - inheritedVelocity;
                locked = FireSolutionSolver.TrySolveBallisticIntercept(
                    ToDouble3(relativePosition),
                    ToDouble3(relativeVelocity),
                    double3.zero,
                    projectileSpeed,
                    kind == TestWeaponKind.Rifle ? 8f : 12f,
                    out _,
                    out var relativeInterceptPoint);
                if (locked)
                {
                    aimPoint = muzzle.position +
                               ToVector3(relativeInterceptPoint);
                }
            }
            else
            {
                locked = kind == TestWeaponKind.Rifle &&
                         TryGetPointDefenseAimPoint(
                             muzzle.position,
                             projectileSpeed,
                             out aimPoint);
                // 只有真正在拦截来袭导弹时才装填近炸拦截弹；对机体射击仍是纯动能弹。
                pointDefenseEngaged = locked;
                if (!locked)
                {
                    locked = TryGetAimPoint(
                        kind == TestWeaponKind.Rifle
                            ? gunAutoAimDegrees
                            : gunAutoAimDegrees + 2f,
                        projectileSpeed,
                        out aimPoint,
                        out _);
                }
            }
            var direction = (aimPoint - muzzle.position).normalized;
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
                SpawnProjectile(
                    muzzle.position,
                    direction,
                    kind,
                    RifleMuzzleVelocityMetersPerSecond,
                    110f,
                    0.08f,
                    RifleProjectileLifetimeSeconds,
                    launchFfxId: 6011100, projectileFfxId: 6013101, impactFfxId: 6011101,
                    launchAnchor: muzzle,
                    inheritedVelocity: inheritedVelocity,
                    interceptorRoundLethalRadius: pointDefenseEngaged
                        ? pointDefenseRoundLethalRadiusMeters
                        : 0f);
                PlayHandWeaponAudio(kind, muzzle);
                RegisterRifleRecoil(Time.time);
                rifleRoundsRemaining = Mathf.Max(0, rifleRoundsRemaining - 1);
                if (rifleRoundsRemaining == 0)
                {
                    rifleMagazinesRemaining = Mathf.Max(
                        0,
                        rifleMagazinesRemaining - 1);
                    if (rifleMagazinesRemaining > 0)
                    {
                        BeginRifleReload(muzzle.position);
                    }
                }
            }
            else
            {
                SpawnProjectile(
                    muzzle.position,
                    direction,
                    kind,
                    CannonMuzzleVelocityMetersPerSecond,
                    420f,
                    0.2f,
                    CannonProjectileLifetimeSeconds,
                    launchFfxId: 6031100, projectileFfxId: 6031101,
                    launchAnchor: muzzle,
                    proximityFuseRadius:
                        cannonProximityFuseRadiusMeters,
                    proximityFuseArmTime:
                        cannonProximityFuseArmSeconds,
                    proximityFuseSafeDistance:
                        cannonProximityFuseSafeDistanceMeters,
                    warheadLethalRadius:
                        cannonWarheadLethalRadiusMeters,
                    inheritedVelocity: inheritedVelocity,
                    cannonProximityFuseEnabled: locked);
                PlayHandWeaponAudio(kind, muzzle);
                cannonRoundsRemaining = Mathf.Max(
                    0,
                    cannonRoundsRemaining - 1);
                if (cannonRoundsRemaining == 0)
                {
                    cannonMagazinesRemaining = Mathf.Max(
                        0,
                        cannonMagazinesRemaining - 1);
                }
                if (cannonRoundsRemaining > 0 ||
                    cannonMagazinesRemaining > 0)
                {
                    BeginCannonReload(muzzle.position);
                }
            }

            return true;
        }

        private void InitializeAmmunition()
        {
            if (ammunitionInitialized)
            {
                return;
            }

            if (ammunitionConfigurationVersion <
                CurrentAmmunitionConfigurationVersion)
            {
                cannonMagazineCapacity = 1;
                cannonMagazineCount = 32;
                rifleMagazineCapacity = 36;
                rifleMagazineCount = 12;
                rightMissileMagazineCount = 6;
                ammunitionConfigurationVersion =
                    CurrentAmmunitionConfigurationVersion;
            }

            cannonMagazineCapacity = Mathf.Max(1, cannonMagazineCapacity);
            cannonMagazineCount = Mathf.Max(1, cannonMagazineCount);
            rifleMagazineCapacity = Mathf.Max(1, rifleMagazineCapacity);
            rifleMagazineCount = Mathf.Max(1, rifleMagazineCount);
            leftMissileMagazineCapacity = Mathf.Max(
                1,
                leftMissileMagazineCapacity);
            leftMissileMagazineCount = Mathf.Max(
                1,
                leftMissileMagazineCount);
            rightMissileMagazineCapacity = Mathf.Max(
                1,
                rightMissileMagazineCapacity);
            rightMissileMagazineCount = Mathf.Max(
                1,
                rightMissileMagazineCount);
            cannonRoundsRemaining = cannonMagazineCapacity;
            cannonMagazinesRemaining = cannonMagazineCount;
            rifleRoundsRemaining = rifleMagazineCapacity;
            rifleMagazinesRemaining = rifleMagazineCount;
            leftMissileRoundsRemaining = leftMissileMagazineCapacity;
            leftMissileMagazinesRemaining = leftMissileMagazineCount;
            rightMissileRoundsRemaining = rightMissileMagazineCapacity;
            rightMissileMagazinesRemaining = rightMissileMagazineCount;
            lastRifleDispersionUpdateTime = Time.time;
            rifleDispersionRandomState = nextRifleDispersionSeed;
            nextRifleDispersionSeed = unchecked(
                nextRifleDispersionSeed + 0x6d2b79f5u);
            if (rifleDispersionRandomState == 0u)
            {
                rifleDispersionRandomState = 1u;
            }
            ammunitionInitialized = true;
        }

        private void UpdateRifleDispersion(float now)
        {
            var elapsed = Mathf.Max(
                0f,
                now - lastRifleDispersionUpdateTime);
            lastRifleDispersionUpdateTime = now;
            if (elapsed <= 0f)
            {
                return;
            }

            var recoveryStart = lastRifleShotTime +
                                rifleDispersionRecoveryDelaySeconds;
            var recoveryElapsed = Mathf.Max(
                0f,
                now - Mathf.Max(now - elapsed, recoveryStart));
            rifleRecoilDispersionMilliradians = Mathf.MoveTowards(
                rifleRecoilDispersionMilliradians,
                0f,
                rifleDispersionRecoveryMilliradiansPerSecond *
                recoveryElapsed);

            if (now - lastRifleShotTime >= rifleBurstResetSeconds)
            {
                rifleBurstShotCount = 0;
            }
        }

        private void RegisterRifleRecoil(float now)
        {
            if (now - lastRifleShotTime >= rifleBurstResetSeconds)
            {
                rifleBurstShotCount = 0;
            }

            var recoilPerShot =
                rifleBurstShotCount < rifleLowRecoilShotCount
                    ? rifleLowRecoilPerShotMilliradians
                    : rifleSustainedRecoilPerShotMilliradians;
            var maximumRecoil = Mathf.Max(
                0f,
                rifleMaximumDispersionMilliradians -
                rifleBaseDispersionMilliradians);
            rifleRecoilDispersionMilliradians = Mathf.Min(
                maximumRecoil,
                rifleRecoilDispersionMilliradians + recoilPerShot);
            rifleBurstShotCount++;
            lastRifleShotTime = now;
        }

        private Vector3 ApplyRifleDispersion(Vector3 forward)
        {
            if (forward.sqrMagnitude <= 0.000001f)
            {
                return transform.forward;
            }

            forward.Normalize();
            var dispersionMilliradians = Mathf.Clamp(
                rifleBaseDispersionMilliradians +
                rifleRecoilDispersionMilliradians,
                0f,
                Mathf.Max(
                    rifleBaseDispersionMilliradians,
                    rifleMaximumDispersionMilliradians));
            if (dispersionMilliradians <= 0f)
            {
                return forward;
            }

            NextRifleGaussianPair(out var horizontalSigma, out var verticalSigma);
            const float maximumSigma = 2.5f;
            horizontalSigma = Mathf.Clamp(
                horizontalSigma,
                -maximumSigma,
                maximumSigma);
            verticalSigma = Mathf.Clamp(
                verticalSigma,
                -maximumSigma,
                maximumSigma);

            var referenceUp = Mathf.Abs(Vector3.Dot(forward, Vector3.up)) > 0.95f
                ? Vector3.right
                : Vector3.up;
            var right = Vector3.Cross(referenceUp, forward).normalized;
            var up = Vector3.Cross(forward, right).normalized;
            var dispersionRadians = dispersionMilliradians * 0.001f;
            return (
                forward +
                right * (horizontalSigma * dispersionRadians) +
                up * (verticalSigma * dispersionRadians)).normalized;
        }

        private void NextRifleGaussianPair(
            out float first,
            out float second)
        {
            var firstUniform = Mathf.Max(
                NextRifleUniform(),
                0.000001f);
            var secondUniform = NextRifleUniform();
            var radius = Mathf.Sqrt(-2f * Mathf.Log(firstUniform));
            var angle = 2f * Mathf.PI * secondUniform;
            first = radius * Mathf.Cos(angle);
            second = radius * Mathf.Sin(angle);
        }

        private float NextRifleUniform()
        {
            var state = rifleDispersionRandomState;
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            rifleDispersionRandomState = state != 0u ? state : 1u;
            return (rifleDispersionRandomState & 0x00ffffffu) /
                   16777216f;
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
                if (cannonRoundsRemaining == 0 &&
                    cannonMagazinesRemaining > 0)
                {
                    cannonRoundsRemaining = cannonMagazineCapacity;
                }
            }
        }

        private void BeginRifleReload(Vector3 weaponPosition)
        {
            if (rifleReloading || rifleMagazinesRemaining <= 0)
            {
                return;
            }

            rifleReloading = true;
            rifleReloadCompleteTime =
                Time.time + Mathf.Max(0.1f, rifleReloadSeconds);
            EnsureHandWeaponAudio().PlayRifleReload(
                weaponPosition,
                IsOwnship());
        }

        private void BeginCannonReload(Vector3 weaponPosition)
        {
            if (cannonReloading || cannonMagazinesRemaining <= 0)
            {
                return;
            }

            cannonReloading = true;
            cannonReloadCompleteTime =
                Time.time + Mathf.Max(0.1f, cannonReloadSeconds);
            EnsureHandWeaponAudio().PlayCannonReload(
                weaponPosition,
                IsOwnship());
        }

        private void PlayHandWeaponAudio(
            TestWeaponKind kind,
            Transform muzzle)
        {
            var audio = EnsureHandWeaponAudio();
            var ownship = IsOwnship();
            if (kind == TestWeaponKind.Rifle)
            {
                audio.SetRifleTriggerHeld(
                    ownship && input.Current.SecondaryFireHeld);
                audio.PlayRifleShot(
                    muzzle.position,
                    ownship);
            }
            else if (kind == TestWeaponKind.Cannon)
            {
                audio.PlayCannonShot(
                    muzzle.position,
                    ownship);
            }
        }

        private MechHandWeaponAudio EnsureHandWeaponAudio()
        {
            handWeaponAudio ??= GetComponent<MechHandWeaponAudio>();
            if (handWeaponAudio == null)
            {
                handWeaponAudio = gameObject.AddComponent<MechHandWeaponAudio>();
            }

            return handWeaponAudio;
        }

        private bool IsOwnship()
        {
            return input != null &&
                   GetComponent<MechAiTargetController>() == null;
        }

        private bool HasLeftMissileAmmunition =>
            leftMissileRoundsRemaining > 0 &&
            leftMissileMagazinesRemaining > 0;

        private bool HasRightMissileAmmunition =>
            rightMissileRoundsRemaining > 0 &&
            rightMissileMagazinesRemaining > 0;

        private static void ConsumeMissileRound(
            ref int roundsRemaining,
            int magazineCapacity,
            ref int magazinesRemaining)
        {
            roundsRemaining = Mathf.Max(0, roundsRemaining - 1);
            if (roundsRemaining > 0)
            {
                return;
            }

            magazinesRemaining = Mathf.Max(0, magazinesRemaining - 1);
            if (magazinesRemaining > 0)
            {
                roundsRemaining = Mathf.Max(1, magazineCapacity);
            }
        }

        private void FireInterceptorMissile(ref int muzzleIndex)
        {
            InitializeAmmunition();
            if (!HasLeftMissileAmmunition)
            {
                return;
            }

            UpdateInterceptorTarget();
            var incomingTrack = interceptorTarget;
            if (!incomingTrack.TrackId.IsValid ||
                !incomingTrack.IsWeaponsQuality ||
                !incomingTrack.Incoming)
            {
                Debug.LogWarning(
                    "[MechTestWeaponSystem] Interceptor launch inhibited: " +
                    "no sensor-confirmed incoming missile track.",
                    this);
                return;
            }

            const MechPartSlot slot =
                MechPartSlot.LeftBackWeapon;
            if (!muzzlesBySlot.TryGetValue(
                    slot,
                    out var muzzles) ||
                muzzles.Length == 0)
            {
                ResolveMuzzles();
            }
            if (!muzzlesBySlot.TryGetValue(
                    slot,
                    out muzzles) ||
                muzzles.Length == 0)
            {
                Debug.LogWarning(
                    "[MechTestWeaponSystem] No interceptor muzzle.",
                    this);
                return;
            }

            var muzzle = muzzles[muzzleIndex % muzzles.Length];
            if (!TrySolveInterceptorEngagement(
                    in incomingTrack,
                    muzzle,
                    out var launchSolution))
            {
                Debug.LogWarning(
                    "[MechTestWeaponSystem] Interceptor launch inhibited: " +
                    "the current threat geometry is outside the dynamic " +
                    "interceptor reachability zone.",
                    this);
                return;
            }

            interceptorSolution = launchSolution;
            var launchDirection =
                ResolveInterceptorColdLaunchDirection(muzzle);
            var launchPosition =
                muzzle.position +
                launchDirection * missileLaunchClearanceMeters;
            var inheritedVelocity =
                flightMotion != null
                    ? flightMotion.Velocity
                    : Vector3.zero;
            var refinedSolution = SolveInterceptorEngagement(
                in incomingTrack,
                launchPosition,
                inheritedVelocity,
                launchDirection,
                radarController.SimTimeSeconds);
            if (refinedSolution.Reachable)
            {
                launchSolution = refinedSolution;
                interceptorSolution = refinedSolution;
            }

            SpawnProjectile(
                launchPosition,
                launchDirection,
                TestWeaponKind.Missile,
                interceptorMissileProfile.EjectionSpeedMps,
                300f,
                0.22f,
                interceptorMissileProfile.MaximumFlightTimeSeconds,
                homingTrack: incomingTrack.TrackId,
                launchDelay: 0f,
                launchFfxId: 0,
                projectileFfxId: 6082101,
                launchAnchor: muzzle,
                maximumTravelDistance:
                    interceptorMissileProfile.MaximumRangeMeters,
                inheritedVelocity: inheritedVelocity,
                simulationRadarController: radarController,
                missileSourceSlot: slot,
                interceptorMissile: true,
                missileProfile: interceptorMissileProfile,
                hasInitialGuidanceEstimate: true,
                initialGuidancePosition: ToVector3(
                    incomingTrack.PredictPosition(
                        radarController.SimTimeSeconds)),
                initialGuidanceVelocity: ToVector3(
                    incomingTrack.Velocity),
                initialGuidanceUncertaintyMeters:
                    incomingTrack.PositionUncertaintyMeters,
                prelaunchInterceptPoint: ToVector3(
                    launchSolution.InterceptPoint),
                coldLaunch: true,
                coldLaunchSafeSeparationSeconds:
                    interceptorSafeSeparationSeconds,
                coldLaunchSafeSeparationMeters:
                    interceptorSafeSeparationMeters,
                coldLaunchIgnitionAlignmentDegrees:
                    interceptorIgnitionAlignmentDegrees,
                coldLaunchAlignmentTimeoutSeconds:
                    interceptorAlignmentTimeoutSeconds);
            ConsumeMissileRound(
                ref leftMissileRoundsRemaining,
                leftMissileMagazineCapacity,
                ref leftMissileMagazinesRemaining);

            Debug.Log(
                $"[MechTestWeaponSystem] Interceptor launched at " +
                $"{incomingTrack.TrackId}, sources=" +
                $"{incomingTrack.SourceMask}, confidence=" +
                $"{incomingTrack.Confidence:0.00}, TOF=" +
                $"{launchSolution.FlightTimeSeconds:0.00}s, margin=" +
                $"{launchSolution.ThreatTimeMarginSeconds:0.00}s.",
                this);
            muzzleIndex = (muzzleIndex + 1) % muzzles.Length;
            interceptorTarget = default;
            interceptorSolution = default;
            interceptorLockUntil = 0f;
        }

        private void FireMissileSalvo(MechPartSlot slot, ref int muzzleIndex)
        {
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

            var missileKinematics = BuildMissileKinematics();
            if (radarController == null ||
                !radarController.TryAuthorizeMissileLaunch(
                    MissileGuidanceMode.ActiveRadar,
                    missileLockDegrees,
                    missileKinematics,
                    TrackObjectClass.AirVehicle,
                    out var track,
                    out var solution,
                    out var guidanceToken))
            {
                Debug.LogWarning(
                    $"[MechTestWeaponSystem] {slot} launch inhibited: no authorized radar track, " +
                    "weapon-envelope solution, seeker handoff, or free guidance channel.",
                    this);
                return;
            }

            var trackId = track.TrackId;
            var seekerActivationDelay = Mathf.Clamp(
                (float)(solution.SeekerActivationTime -
                        radarController.SimTimeSeconds),
                0.5f,
                8f);
            var launchProfile = airToAirMissileProfile;
            launchProfile.SeekerActivationDelaySeconds =
                seekerActivationDelay;
            var inheritedVelocity =
                flightMotion != null ? flightMotion.Velocity : Vector3.zero;
            Debug.Log(
                $"[MechTestWeaponSystem] Missile launch authorized: slot={slot}, track={trackId}, " +
                $"channel={guidanceToken}, seeker={seekerActivationDelay:0.0}s.",
                this);
            const int salvoCount = 1;
            var launchedCount = 0;
            for (var i = 0; i < salvoCount; i++)
            {
                if (i > 0 && !radarController.RetainGuidanceChannel(guidanceToken))
                {
                    Debug.LogWarning(
                        $"[MechTestWeaponSystem] {slot} salvo truncated: guidance channel was released.",
                        this);
                    break;
                }

                var muzzle = muzzles[(muzzleIndex + i) % muzzles.Length];
                var clearanceDirection =
                    ResolveMissileLaunchDirection(muzzle);
                var launchDirection =
                    ResolveGuidedMissileLaunchDirection(
                        muzzle,
                        ToVector3(solution.InterceptPoint),
                        65f);
                var launchPosition =
                    muzzle.position +
                    clearanceDirection *
                    missileLaunchClearanceMeters;

                SpawnProjectile(
                    launchPosition,
                    launchDirection,
                    TestWeaponKind.Missile,
                    launchProfile.EjectionSpeedMps,
                    300f,
                    0.22f,
                    launchProfile.MaximumFlightTimeSeconds,
                    trackId,
                    i * 0.06f,
                    launchFfxId: slot == MechPartSlot.LeftBackWeapon ? 6082100 : 6083100,
                    projectileFfxId: slot == MechPartSlot.LeftBackWeapon ? 6082101 : 6083101,
                    launchAnchor: muzzle,
                    maximumTravelDistance:
                        launchProfile.MaximumRangeMeters,
                    guidanceChannelToken: guidanceToken,
                    inheritedVelocity: inheritedVelocity,
                    missileSourceSlot: slot,
                    missileProfile: launchProfile);
                ConsumeMissileRound(
                    ref rightMissileRoundsRemaining,
                    rightMissileMagazineCapacity,
                    ref rightMissileMagazinesRemaining);
                launchedCount++;
            }
            muzzleIndex = (muzzleIndex + launchedCount) % muzzles.Length;
        }

        private void FireDirectInterceptorMissile(
            MechTestProjectile incomingMissile,
            ref int muzzleIndex)
        {
            const MechPartSlot slot =
                MechPartSlot.LeftBackWeapon;
            InitializeAmmunition();
            if (!HasLeftMissileAmmunition)
            {
                return;
            }

            var muzzle = GetNextMuzzle(slot, ref muzzleIndex);
            if (muzzle == null)
            {
                Debug.LogWarning(
                    "[MechTestWeaponSystem] No AI interceptor muzzle.",
                    this);
                return;
            }

            var launchDirection =
                ResolveInterceptorColdLaunchDirection(muzzle);
            var launchPosition =
                muzzle.position +
                launchDirection * missileLaunchClearanceMeters;
            var inheritedVelocity =
                flightMotion != null
                    ? flightMotion.Velocity
                    : Vector3.zero;
            if (!incomingMissile.TryPredictThreatTo(
                    transform,
                    inheritedVelocity,
                    interceptorProtectedRadiusMeters,
                    interceptorPredictionHorizonSeconds,
                    out var timeToClosestApproach,
                    out var closestApproachDistance))
            {
                return;
            }

            var directTrack = new MissileTrackSnapshot
            {
                TrackId = new TrackId(
                    new ACunicombat.Simulation.Core.Handle(
                        1,
                        1)),
                Position = ToDouble3(
                    incomingMissile.transform.position),
                Velocity = ToDouble3(incomingMissile.Velocity),
                PositionUncertaintyMeters = 0f,
                CpaUncertaintyMeters = 0f,
                Confidence = 1f,
                SourceMask = MissileTrackSourceMask.Radar,
                Quality = MissileTrackQuality.WeaponsQuality,
                Confirmed = true,
                Incoming = true,
                TimeToClosestApproachSeconds =
                    timeToClosestApproach,
                ClosestApproachDistanceMeters =
                    closestApproachDistance,
                LastEvidenceTime = Time.timeAsDouble,
                LastUpdateTime = Time.timeAsDouble,
            };
            var interceptorKinematics =
                BuildInterceptorKinematics();
            var launchSolution =
                FireSolutionSolver.SolveInterceptor(
                    in directTrack,
                    ToDouble3(launchPosition),
                    ToDouble3(inheritedVelocity),
                    new float3(
                        launchDirection.x,
                        launchDirection.y,
                        launchDirection.z),
                    in interceptorKinematics,
                    Time.timeAsDouble,
                    interceptorMissileProfile
                        .MaximumAngularRateDegreesPerSecond,
                    interceptorMissileProfile
                        .MaximumFlightTimeSeconds,
                    interceptorMissileProfile
                        .MaximumRangeMeters,
                    interceptorResponseReserveSeconds,
                    EstimateInterceptorColdLaunchDelaySeconds());
            if (!launchSolution.Reachable)
            {
                return;
            }
            var refinedSolution = SolveInterceptorEngagement(
                in directTrack,
                launchPosition,
                inheritedVelocity,
                launchDirection,
                Time.timeAsDouble);
            if (refinedSolution.Reachable)
            {
                launchSolution = refinedSolution;
            }
            var simulationRadar = FindObjectsByType<MechRadarController>(
                    FindObjectsInactive.Exclude)
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    candidate.isActiveAndEnabled);

            SpawnProjectile(
                launchPosition,
                launchDirection,
                TestWeaponKind.Missile,
                interceptorMissileProfile.EjectionSpeedMps,
                300f,
                0.22f,
                interceptorMissileProfile.MaximumFlightTimeSeconds,
                launchDelay: 0f,
                launchFfxId: 0,
                projectileFfxId: 6082101,
                launchAnchor: muzzle,
                maximumTravelDistance:
                    interceptorMissileProfile.MaximumRangeMeters,
                inheritedVelocity: inheritedVelocity,
                simulationRadarController: simulationRadar,
                missileSourceSlot: slot,
                interceptorMissile: true,
                directProjectileTarget: incomingMissile,
                missileProfile: interceptorMissileProfile,
                hasInitialGuidanceEstimate: true,
                initialGuidancePosition:
                    incomingMissile.transform.position,
                initialGuidanceVelocity:
                    incomingMissile.Velocity,
                prelaunchInterceptPoint:
                    ToVector3(launchSolution.InterceptPoint),
                coldLaunch: true,
                coldLaunchSafeSeparationSeconds:
                    interceptorSafeSeparationSeconds,
                coldLaunchSafeSeparationMeters:
                    interceptorSafeSeparationMeters,
                coldLaunchIgnitionAlignmentDegrees:
                    interceptorIgnitionAlignmentDegrees,
                coldLaunchAlignmentTimeoutSeconds:
                    interceptorAlignmentTimeoutSeconds);
            ConsumeMissileRound(
                ref leftMissileRoundsRemaining,
                leftMissileMagazineCapacity,
                ref leftMissileMagazinesRemaining);
        }

        private void FireDirectMissileSalvo(
            MechPartSlot slot,
            CombatTestTarget directTarget,
            ref int muzzleIndex,
            bool hasInitialEstimate = false,
            Vector3 initialEstimatePosition = default,
            Vector3 initialEstimateVelocity = default,
            float initialEstimateUncertaintyMeters = 0f)
        {
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

            if (!muzzlesBySlot.TryGetValue(
                    slot,
                    out var muzzles) ||
                muzzles.Length == 0)
            {
                ResolveMuzzles();
            }
            if (!muzzlesBySlot.TryGetValue(
                    slot,
                    out muzzles) ||
                muzzles.Length == 0)
            {
                Debug.LogWarning(
                    $"[MechTestWeaponSystem] No missile muzzle for {slot}.",
                    this);
                return;
            }

            var firingCombatant = ResolveCombatantRoot(transform);
            var simulationRadar = FindObjectsByType<MechRadarController>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None)
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    candidate.isActiveAndEnabled &&
                    ResolveCombatantRoot(candidate.transform) !=
                    firingCombatant);
            var inheritedVelocity =
                flightMotion != null ? flightMotion.Velocity : Vector3.zero;
            // Direct-target launch is the AI path: one missile per fire
            // command so its cooldown represents an actual single launch.
            const int salvoCount = 1;
            for (var i = 0; i < salvoCount; i++)
            {
                var muzzle =
                    muzzles[(muzzleIndex + i) % muzzles.Length];
                var launchDirection =
                    hasInitialEstimate
                        ? ResolveDirectMissileLaunchDirection(
                            muzzle,
                            initialEstimatePosition)
                        : ResolveDirectMissileLaunchDirection(
                            muzzle,
                            directTarget);
                var launchPosition =
                    muzzle.position +
                    launchDirection * missileLaunchClearanceMeters;
                SpawnProjectile(
                    launchPosition,
                    launchDirection,
                    TestWeaponKind.Missile,
                    airToAirMissileProfile.EjectionSpeedMps,
                    300f,
                    0.22f,
                    airToAirMissileProfile.MaximumFlightTimeSeconds,
                    launchDelay: i * 0.06f,
                    launchFfxId: slot == MechPartSlot.LeftBackWeapon
                        ? 6082100
                        : 6083100,
                    projectileFfxId: slot == MechPartSlot.LeftBackWeapon
                        ? 6082101
                        : 6083101,
                    launchAnchor: muzzle,
                    maximumTravelDistance:
                        airToAirMissileProfile.MaximumRangeMeters,
                    inheritedVelocity: inheritedVelocity,
                    directGuidanceTarget: directTarget,
                    simulationRadarController: simulationRadar,
                    missileSourceSlot: slot,
                    missileProfile: airToAirMissileProfile,
                    hasInitialGuidanceEstimate: hasInitialEstimate,
                    initialGuidancePosition: initialEstimatePosition,
                    initialGuidanceVelocity: initialEstimateVelocity,
                    initialGuidanceUncertaintyMeters:
                        initialEstimateUncertaintyMeters);
                ConsumeMissileRound(
                    ref rightMissileRoundsRemaining,
                    rightMissileMagazineCapacity,
                    ref rightMissileMagazinesRemaining);
            }
            muzzleIndex = (muzzleIndex + salvoCount) % muzzles.Length;
        }

        private static Transform ResolveCombatantRoot(Transform source)
        {
            if (source == null)
            {
                return null;
            }

            var rig = source.GetComponentInParent<MechPlayerRig>();
            if (rig != null)
            {
                return rig.transform;
            }

            var target =
                source.GetComponentInParent<CombatTestTarget>();
            return target != null ? target.transform : source;
        }

        private void UpdateInterceptorTarget()
        {
            if (TryFindBestIncomingMissile(
                    out var candidate,
                    out var candidateSolution))
            {
                interceptorTarget = candidate;
                interceptorSolution = candidateSolution;
                interceptorLockUntil =
                    Time.unscaledTime +
                    interceptorLockHoldSeconds;
                return;
            }

            if (!interceptorTarget.TrackId.IsValid ||
                radarController == null ||
                !radarController.TryGetMissileTrack(
                    interceptorTarget.TrackId,
                    out var retainedTrack) ||
                !retainedTrack.IsWeaponsQuality ||
                !retainedTrack.Incoming ||
                !TryPeekInterceptorMuzzle(out var retainedMuzzle) ||
                !TrySolveInterceptorEngagement(
                    in retainedTrack,
                    retainedMuzzle,
                    out var retainedSolution) ||
                Time.unscaledTime >= interceptorLockUntil)
            {
                interceptorTarget = default;
                interceptorSolution = default;
                return;
            }

            interceptorTarget = retainedTrack;
            interceptorSolution = retainedSolution;
        }

        private bool TryFindBestIncomingMissile(
            out MissileTrackSnapshot best,
            out InterceptorEngagementSolution bestSolution)
        {
            best = default;
            bestSolution = default;
            if (radarController == null ||
                !TryPeekInterceptorMuzzle(out var muzzle))
            {
                return false;
            }

            var trackCount =
                radarController.CopyMissileTracks(
                    interceptorTrackScratch);
            var bestScore = float.PositiveInfinity;
            for (var i = 0; i < trackCount; i++)
            {
                var candidate = interceptorTrackScratch[i];
                if (!candidate.TrackId.IsValid ||
                    !candidate.IsWeaponsQuality ||
                    !candidate.Incoming ||
                    !MissileEngagementMath
                        .IntersectsProtectedCpaWindow(
                            candidate
                                .TimeToClosestApproachSeconds,
                            candidate
                                .ClosestApproachDistanceMeters,
                            candidate.CpaUncertaintyMeters,
                            interceptorPredictionHorizonSeconds,
                            interceptorProtectedRadiusMeters))
                {
                    continue;
                }

                if (!TrySolveInterceptorEngagement(
                        in candidate,
                        muzzle,
                        out var solution))
                {
                    continue;
                }

                var score =
                    solution.FlightTimeSeconds +
                    candidate.ClosestApproachDistanceMeters /
                    Mathf.Max(
                        interceptorProtectedRadiusMeters,
                        1f) +
                    candidate.PositionUncertaintyMeters /
                    1000f -
                    candidate.Confidence * 0.5f -
                    Mathf.Clamp(
                        solution.ThreatTimeMarginSeconds,
                        0f,
                        10f) *
                    0.05f;
                if (score < bestScore)
                {
                    best = candidate;
                    bestSolution = solution;
                    bestScore = score;
                }
            }

            return best.TrackId.IsValid;
        }

        private bool TryPeekInterceptorMuzzle(out Transform muzzle)
        {
            const MechPartSlot slot =
                MechPartSlot.LeftBackWeapon;
            if (!muzzlesBySlot.TryGetValue(
                    slot,
                    out var muzzles) ||
                muzzles.Length == 0)
            {
                ResolveMuzzles();
            }

            if (!muzzlesBySlot.TryGetValue(
                    slot,
                    out muzzles) ||
                muzzles.Length == 0)
            {
                muzzle = null;
                return false;
            }

            muzzle =
                muzzles[leftBackMuzzleIndex % muzzles.Length];
            return muzzle != null;
        }

        private bool TrySolveInterceptorEngagement(
            in MissileTrackSnapshot track,
            Transform muzzle,
            out InterceptorEngagementSolution solution)
        {
            solution = default;
            if (radarController == null || muzzle == null)
            {
                return false;
            }

            var launchDirection =
                ResolveInterceptorColdLaunchDirection(muzzle);
            var launchPosition =
                muzzle.position +
                launchDirection * missileLaunchClearanceMeters;
            var ownshipVelocity =
                flightMotion != null
                    ? flightMotion.Velocity
                    : Vector3.zero;
            solution = SolveInterceptorEngagement(
                in track,
                launchPosition,
                ownshipVelocity,
                launchDirection,
                radarController.SimTimeSeconds);
            return solution.Reachable;
        }

        private InterceptorEngagementSolution SolveInterceptorEngagement(
            in MissileTrackSnapshot track,
            Vector3 launchPosition,
            Vector3 ownshipVelocity,
            Vector3 launchDirection,
            double requestTime)
        {
            var kinematics = BuildInterceptorKinematics();
            return FireSolutionSolver.SolveInterceptor(
                in track,
                ToDouble3(launchPosition),
                ToDouble3(ownshipVelocity),
                new float3(
                    launchDirection.x,
                    launchDirection.y,
                    launchDirection.z),
                in kinematics,
                requestTime,
                interceptorMissileProfile
                    .MaximumAngularRateDegreesPerSecond,
                interceptorMissileProfile
                    .MaximumFlightTimeSeconds,
                interceptorMissileProfile.MaximumRangeMeters,
                interceptorResponseReserveSeconds,
                EstimateInterceptorColdLaunchDelaySeconds());
        }

        private static Vector3 ResolveDirectMissileLaunchDirection(
            Transform muzzle,
            CombatTestTarget directTarget)
        {
            if (muzzle != null &&
                directTarget != null &&
                directTarget.AimPoint != null)
            {
                var toTarget =
                    directTarget.AimPoint.position - muzzle.position;
                if (toTarget.sqrMagnitude > 0.001f)
                {
                    return toTarget.normalized;
                }
            }

            return muzzle != null
                ? muzzle.forward
                : Vector3.forward;
        }

        private static Vector3 ResolveDirectMissileLaunchDirection(
            Transform muzzle,
            Vector3 estimatedPosition)
        {
            if (muzzle != null)
            {
                var toEstimate = estimatedPosition - muzzle.position;
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
            var bodyForward = flightMotion != null
                ? flightMotion.LogicalRotation * Vector3.forward
                : transform.forward;
            if (bodyForward.sqrMagnitude <= 0.001f)
            {
                bodyForward = Vector3.forward;
            }
            bodyForward.Normalize();

            // Imported dummy transforms do not consistently use local +Z as the
            // tube axis. Select the local basis direction that actually points
            // closest to the deployed launcher's forward hemisphere.
            var candidates = new[]
            {
                muzzle.forward, -muzzle.forward,
                muzzle.up, -muzzle.up,
                muzzle.right, -muzzle.right,
            };
            var launchDirection = bodyForward;
            var bestAlignment = 0.5f;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i].normalized;
                var alignment = Vector3.Dot(candidate, bodyForward);
                if (alignment > bestAlignment)
                {
                    bestAlignment = alignment;
                    launchDirection = candidate;
                }
            }

            return launchDirection.normalized;
        }

        private Vector3 ResolveInterceptorColdLaunchDirection(
            Transform muzzle)
        {
            var bodyUp = flightMotion != null
                ? flightMotion.LogicalRotation * Vector3.up
                : transform.up;
            if (bodyUp.sqrMagnitude <= 0.001f)
            {
                bodyUp = Vector3.up;
            }
            bodyUp.Normalize();

            if (muzzle == null)
            {
                return bodyUp;
            }

            var candidates = new[]
            {
                muzzle.forward, -muzzle.forward,
                muzzle.up, -muzzle.up,
                muzzle.right, -muzzle.right,
            };
            var launchDirection = bodyUp;
            var bestAlignment = float.NegativeInfinity;
            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i].normalized;
                var alignment = Vector3.Dot(candidate, bodyUp);
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
            var ejectionSpeed = Mathf.Max(
                interceptorMissileProfile.EjectionSpeedMps,
                0.01f);
            return Mathf.Max(
                interceptorSafeSeparationSeconds,
                interceptorSafeSeparationMeters / ejectionSpeed);
        }

        private Vector3 ResolveGuidedMissileLaunchDirection(
            Transform muzzle,
            Vector3 interceptPoint,
            float maximumDeflectionDegrees)
        {
            var clearanceDirection =
                ResolveMissileLaunchDirection(muzzle);
            if (muzzle == null)
            {
                return clearanceDirection;
            }

            var toIntercept = interceptPoint - muzzle.position;
            if (toIntercept.sqrMagnitude <= 0.001f)
            {
                return clearanceDirection;
            }

            return Vector3.RotateTowards(
                    clearanceDirection,
                    toIntercept.normalized,
                    Mathf.Max(0f, maximumDeflectionDegrees) *
                    Mathf.Deg2Rad,
                    0f)
                .normalized;
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

            var muzzle = muzzles[index % muzzles.Length];
            index = (index + 1) % muzzles.Length;
            return muzzle;
        }

        private void SpawnProjectile(
            Vector3 position,
            Vector3 direction,
            TestWeaponKind kind,
            float speed,
            float damage,
            float radius,
            float lifetime,
            TrackId homingTrack = default,
            float launchDelay = 0f,
            float acceleration = 0f,
            float turnRateDegrees = 0f,
            int launchFfxId = 0,
            int projectileFfxId = 0,
            int impactFfxId = 0,
            Transform launchAnchor = null,
            float maximumTravelDistance = 0f,
            int guidanceChannelToken = 0,
            float motorBurnDuration = 2.1f,
            float seekerActivationDelay = 2.5f,
            float seekerMaximumRange = 8000f,
            float seekerHalfAngleDegrees = 35f,
            float seekerGimbalDegrees = 60f,
            float maximumLateralAcceleration = 75f,
            float maneuverDeltaV = 180f,
            float rcsLateralAcceleration = 0f,
            float rcsDeltaV = 0f,
            float proximityFuseRadius = 12f,
            float proximityFuseArmTime = 0.75f,
            float proximityFuseSafeDistance = 20f,
            float warheadLethalRadius = 18f,
            Vector3 inheritedVelocity = default,
            CombatTestTarget directGuidanceTarget = null,
            MechRadarController simulationRadarController = null,
            MechPartSlot missileSourceSlot = default,
            bool interceptorMissile = false,
            MechTestProjectile directProjectileTarget = null,
            MissileFlightProfile missileProfile = default,
            bool hasInitialGuidanceEstimate = false,
            Vector3 initialGuidancePosition = default,
            Vector3 initialGuidanceVelocity = default,
            float initialGuidanceUncertaintyMeters = 0f,
            Vector3 prelaunchInterceptPoint = default,
            bool coldLaunch = false,
            float coldLaunchSafeSeparationSeconds = 0f,
            float coldLaunchSafeSeparationMeters = 0f,
            float coldLaunchIgnitionAlignmentDegrees = 8f,
            float coldLaunchAlignmentTimeoutSeconds = 0.6f,
            float interceptorRoundLethalRadius = 0f,
            bool cannonProximityFuseEnabled = false)
        {
            EnsureMaterials();
            var ownshipMissileAudio =
                kind == TestWeaponKind.Missile &&
                input != null &&
                GetComponent<MechAiTargetController>() == null;
            if (ownshipMissileAudio)
            {
                missileLaunchAudio ??=
                    GetComponent<MechMissileLaunchAudio>();
                if (missileLaunchAudio == null)
                {
                    missileLaunchAudio =
                        gameObject.AddComponent<MechMissileLaunchAudio>();
                }
                missileLaunchAudio.PlayRelease(coldLaunch);
            }

            SpawnNativeOneShot(
                launchFfxId,
                launchAnchor,
                launchAnchor != null ? launchAnchor.position : position,
                direction,
                kind != TestWeaponKind.Missile,
                ResolveMuzzleFlashVisibleSeconds(kind));
            var projectile = GameObject.CreatePrimitive(
                kind == TestWeaponKind.Missile ? PrimitiveType.Cube : PrimitiveType.Sphere);
            projectile.name = $"Test_{kind}_Projectile";
            var upReference =
                Mathf.Abs(Vector3.Dot(direction.normalized, Vector3.up)) >
                0.95f
                    ? Vector3.forward
                    : Vector3.up;
            projectile.transform.SetPositionAndRotation(
                position,
                Quaternion.LookRotation(direction, upReference));
            projectile.transform.localScale = kind switch
            {
                TestWeaponKind.Rifle => Vector3.one * 0.12f,
                TestWeaponKind.Cannon => Vector3.one * 0.32f,
                _ => new Vector3(0.18f, 0.18f, 0.8f),
            };

            var collider = projectile.GetComponent<Collider>();
            if (kind == TestWeaponKind.Missile)
            {
                ConfigureMissileHitbox(
                    collider,
                    projectile.transform.localScale,
                    missileSourceSlot);
            }
            else if (collider != null)
            {
                Destroy(collider);
            }
            var placeholderRenderer = projectile.GetComponent<Renderer>();
            placeholderRenderer.sharedMaterial = GetProjectileMaterial(kind);
            if (kind == TestWeaponKind.Missile)
            {
                TryAttachMissileVisual(
                    projectile.transform,
                    placeholderRenderer,
                    aimingCamera,
                    missileSourceSlot);
            }

            // 步枪与火炮都改用程序化曳光弹。两者原本的 FFX 弹丸特效都栽在同一个问题上：
            // Model 发射器引用的模型没有贴图——步枪 `ffx_6013101` 用 `s88060`
            // （`g_DiffuseTexture` 为空、尺寸 `2.5×2.5×20`），火炮 `ffx_6031101` 用 `s88500`
            // （Albedo/Metallic/Normal 三张路径全空），渲染出来都是无贴图的纯色块。
            // 顺带解决旧拖尾"命中瞬间整条消失"的问题（MechTracerRound 会分离后就地淡出）。
            if (kind == TestWeaponKind.Rifle)
            {
                placeholderRenderer.enabled = false;
                projectile.AddComponent<MechTracerRound>().Build(
                    hotColor: new Color(1f, 0.93f, 0.72f),
                    // 弹头取小而极亮：靠 Bloom 把它扩成光点，而不是靠尺寸做成一个发光球。
                    headSize: 0.3f,
                    trailWidth: 0.17f,
                    // 1500 m/s × 0.07 s ≈ 105 m 亮痕。
                    streakSeconds: 0.07f);
            }
            else if (kind == TestWeaponKind.Cannon)
            {
                placeholderRenderer.enabled = false;
                // 火炮是重弹：初速只有 450 m/s（步枪 1500），所以亮痕要更粗更短、
                // 色温更低（偏橙而非近白），读起来才是一发看得见飞行过程的炮弹，
                // 而不是又一条细长曳光。450 m/s × 0.16 s ≈ 72 m。
                projectile.AddComponent<MechTracerRound>().Build(
                    hotColor: new Color(1f, 0.74f, 0.36f),
                    headSize: 0.85f,
                    trailWidth: 0.5f,
                    streakSeconds: 0.16f);
            }

            var projectileBehaviour = projectile.AddComponent<MechTestProjectile>();
            if (interceptorRoundLethalRadius > 0f)
            {
                projectileBehaviour.ConfigureInterceptorRound(
                    interceptorRoundLethalRadius);
            }
            projectileBehaviour.Configure(
                transform,
                simulationRadarController != null
                    ? simulationRadarController
                    : radarController,
                homingTrack,
                kind,
                inheritedVelocity + direction * speed,
                damage,
                radius,
                lifetime,
                launchDelay,
                acceleration,
                turnRateDegrees,
                nativeEffectLibrary,
                impactFfxId,
                maximumTravelDistance,
                guidanceChannelToken,
                motorBurnDuration,
                seekerActivationDelay,
                seekerMaximumRange,
                seekerHalfAngleDegrees,
                seekerGimbalDegrees,
                maximumLateralAcceleration,
                maneuverDeltaV,
                rcsLateralAcceleration,
                rcsDeltaV,
                proximityFuseRadius,
                proximityFuseArmTime,
                proximityFuseSafeDistance,
                warheadLethalRadius,
                directGuidanceTarget,
                interceptorMissile,
                directProjectileTarget,
                missileProfile,
                hasInitialGuidanceEstimate,
                initialGuidancePosition,
                initialGuidanceVelocity,
                initialGuidanceUncertaintyMeters,
                prelaunchInterceptPoint,
                coldLaunch,
                inheritedVelocity,
                coldLaunchSafeSeparationSeconds,
                coldLaunchSafeSeparationMeters,
                coldLaunchIgnitionAlignmentDegrees,
                coldLaunchAlignmentTimeoutSeconds,
                ownshipMissileAudio);
            if (kind == TestWeaponKind.Cannon &&
                cannonProximityFuseEnabled)
            {
                projectileBehaviour.ConfigureCannonProximityFuse(
                    proximityFuseRadius,
                    proximityFuseArmTime,
                    proximityFuseSafeDistance,
                    warheadLethalRadius);
            }
        }

        private bool TryGetAimPoint(
            float coneDegrees,
            float projectileSpeed,
            out Vector3 aimPoint,
            out TrackId trackId)
        {
            var ray = GetAimRay();
            if (radarController != null &&
                radarController.TryGetFireSolution(coneDegrees, projectileSpeed, out var track, out var solution))
            {
                aimPoint = new Vector3(
                    (float)solution.MidcourseAimPoint.x,
                    (float)solution.MidcourseAimPoint.y,
                    (float)solution.MidcourseAimPoint.z);
                trackId = track.TrackId;
                return true;
            }

            aimPoint = ray.origin + ray.direction * 100000f;
            trackId = default;
            return false;
        }

        private void UpdatePointDefenseTarget()
        {
            var aimRay = GetAimRay();
            if (TryFindBestPointDefenseTarget(
                    aimRay,
                    pointDefenseLockDegrees,
                    pointDefenseMaximumRangeMeters,
                    out var candidate))
            {
                pointDefenseTarget = candidate;
                pointDefenseLockUntil =
                    Time.unscaledTime + pointDefenseLockHoldSeconds;
                radarController.CuePointDefenseTrack(
                    candidate.TrackId);
                return;
            }

            if (!pointDefenseTarget.TrackId.IsValid ||
                radarController == null ||
                !radarController.TryGetMissileTrack(
                    pointDefenseTarget.TrackId,
                    out var retainedTrack) ||
                !retainedTrack.IsWeaponsQuality ||
                !retainedTrack.Incoming ||
                Time.unscaledTime >= pointDefenseLockUntil)
            {
                pointDefenseTarget = default;
                radarController?.CuePointDefenseTrack(default);
                return;
            }

            pointDefenseTarget = retainedTrack;
            radarController.CuePointDefenseTrack(
                retainedTrack.TrackId);
        }

        private bool TryFindBestPointDefenseTarget(
            Ray aimRay,
            float coneDegrees,
            float maximumRange,
            out MissileTrackSnapshot best)
        {
            best = default;
            if (radarController == null)
            {
                return false;
            }

            var trackCount =
                radarController.CopyMissileTracks(
                    interceptorTrackScratch);
            var bestScore = float.MaxValue;
            var rangeLimitSquared = maximumRange * maximumRange;
            for (var i = 0; i < trackCount; i++)
            {
                var candidate = interceptorTrackScratch[i];
                if (!candidate.TrackId.IsValid ||
                    !candidate.IsWeaponsQuality ||
                    !candidate.Incoming)
                {
                    continue;
                }

                var predictedPosition =
                    candidate.PredictPosition(
                        radarController.SimTimeSeconds);
                var toCandidate =
                    new Vector3(
                        (float)predictedPosition.x,
                        (float)predictedPosition.y,
                        (float)predictedPosition.z) -
                    aimRay.origin;
                var distanceSquared = toCandidate.sqrMagnitude;
                if (distanceSquared <= 0.01f ||
                    distanceSquared > rangeLimitSquared)
                {
                    continue;
                }

                var direction = toCandidate / Mathf.Sqrt(distanceSquared);
                var alignment = Vector3.Dot(aimRay.direction, direction);
                var minimumAlignment = Mathf.Cos(coneDegrees * Mathf.Deg2Rad);
                if (alignment < minimumAlignment)
                {
                    continue;
                }

                var angularPenalty = 1f - alignment;
                var rangePenalty =
                    Mathf.Sqrt(distanceSquared) /
                    Mathf.Max(maximumRange, 1f) *
                    0.015f;
                var score = angularPenalty + rangePenalty;
                if (score < bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best.TrackId.IsValid;
        }

        private bool TryGetPointDefenseAimPoint(
            Vector3 shooterPosition,
            float projectileSpeed,
            out Vector3 aimPoint)
        {
            aimPoint = default;
            if (!pointDefenseTarget.TrackId.IsValid ||
                radarController == null)
            {
                return false;
            }

            var solutionTime =
                radarController.SimTimeSeconds;
            var predictedPosition =
                pointDefenseTarget.PredictPosition(
                    solutionTime);
            var predictedVelocity =
                pointDefenseTarget.PredictVelocity(
                    solutionTime);
            var predictedAcceleration =
                pointDefenseTarget.PredictAcceleration(
                    solutionTime);
            var targetPosition = new Vector3(
                (float)predictedPosition.x,
                (float)predictedPosition.y,
                (float)predictedPosition.z);
            var targetVelocity = new Vector3(
                (float)predictedVelocity.x,
                (float)predictedVelocity.y,
                (float)predictedVelocity.z);
            var shooterVelocity =
                flightMotion != null
                    ? flightMotion.Velocity
                    : Vector3.zero;
            var relativePosition =
                targetPosition - shooterPosition;
            var relativeVelocity =
                targetVelocity - shooterVelocity;
            if (relativePosition.sqrMagnitude >
                pointDefenseMaximumRangeMeters *
                pointDefenseMaximumRangeMeters)
            {
                return false;
            }

            var hasIntercept =
                FireSolutionSolver.TrySolveBallisticIntercept(
                    ToDouble3(relativePosition),
                    ToDouble3(relativeVelocity),
                    predictedAcceleration,
                    pointDefenseTarget.Jerk,
                    MissileTrackFusionConfig.Default
                        .MaximumTrackAccelerationMetersPerSecondSquared,
                    0.6,
                    projectileSpeed,
                    pointDefenseMaximumLeadSeconds,
                    out _,
                    out var relativeInterceptPoint);
            if (!hasIntercept &&
                !FireSolutionSolver.TrySolveBallisticIntercept(
                    ToDouble3(relativePosition),
                    ToDouble3(relativeVelocity),
                    double3.zero,
                    projectileSpeed,
                    pointDefenseMaximumLeadSeconds,
                    out _,
                    out relativeInterceptPoint))
            {
                return false;
            }

            // 交战门限：此前火控解完全不消费 PositionUncertaintyMeters / CpaUncertaintyMeters，
            // 明知航迹横向误差远大于命中窗口也照样开火，结果是白耗弹药、且低拦截率无从解释。
            var interceptRange = ToVector3(relativeInterceptPoint).magnitude;
            pointDefenseHitProbability = EstimatePointDefenseHitProbability(
                in pointDefenseTarget,
                interceptRange);
            if (pointDefenseHitProbability < pointDefenseMinimumHitProbability)
            {
                pointDefenseWithholdReason = interceptRange >
                                             pointDefenseTarget
                                                 .PositionUncertaintyMeters * 400f
                    ? PointDefenseWithholdReason.TrackTooCoarse
                    : PointDefenseWithholdReason.RangeTooLong;
                return false;
            }

            pointDefenseWithholdReason = PointDefenseWithholdReason.None;
            aimPoint =
                shooterPosition +
                ToVector3(relativeInterceptPoint);
            return true;
        }

        /// <summary>
        /// 单发命中概率的一阶估计。把航迹横向不确定度、弹丸散布与提前量误差合成一个 2D 高斯
        /// 的 σ，再对命中窗口积分：<c>P = 1 - exp(-r² / (2σ²))</c>（瑞利分布）。
        /// 这不是精确弹道学，目的是给"打不打"一个可解释的判据。
        /// </summary>
        private float EstimatePointDefenseHitProbability(
            in MissileTrackSnapshot track,
            float interceptRange)
        {
            var lethalRadius = Mathf.Max(
                pointDefenseRoundLethalRadiusMeters,
                RifleProjectileCollisionRadiusMeters + MissileHitboxHalfWidthMeters);
            var flightTime = interceptRange /
                             Mathf.Max(RifleMuzzleVelocityMetersPerSecond, 1f);

            // 航迹自身的位置不确定度。
            var trackSigma = Mathf.Max(0.01f, track.PositionUncertaintyMeters);
            // 速度估计误差经提前量放大：航迹不确定度在一个滤波周期内的等效速度误差。
            var velocitySigma = trackSigma /
                                Mathf.Max(PointDefenseVelocitySmoothingSeconds, 0.05f);
            var leadSigma = velocitySigma * flightTime;
            // 枪口散布。
            var dispersionSigma =
                (rifleBaseDispersionMilliradians +
                 rifleRecoilDispersionMilliradians) * 0.001f * interceptRange;

            var sigmaSquared =
                trackSigma * trackSigma +
                leadSigma * leadSigma +
                dispersionSigma * dispersionSigma;
            if (sigmaSquared <= 1e-6f)
            {
                return 1f;
            }

            return 1f - Mathf.Exp(
                -(lethalRadius * lethalRadius) / (2f * sigmaSquared));
        }

        private static void ConfigureMissileHitbox(
            Collider collider,
            Vector3 projectileScale,
            MechPartSlot sourceSlot)
        {
            if (collider == null)
            {
                return;
            }

            collider.isTrigger = true;
            if (collider is not BoxCollider box)
            {
                return;
            }

            const float hitboxDiameterMeters = 0.5f;
            var missileLengthMeters =
                sourceSlot == MechPartSlot.LeftBackWeapon
                    ? LeftMissileVisualLengthMeters
                    : RightMissileVisualLengthMeters;
            box.center = Vector3.zero;
            box.size = new Vector3(
                hitboxDiameterMeters /
                Mathf.Max(Mathf.Abs(projectileScale.x), 0.001f),
                hitboxDiameterMeters /
                Mathf.Max(Mathf.Abs(projectileScale.y), 0.001f),
                missileLengthMeters /
                Mathf.Max(Mathf.Abs(projectileScale.z), 0.001f));
        }

        private Ray GetAimRay()
        {
            if (aimingCamera == null) aimingCamera = Camera.main;
            var viewportPoint = GetAimViewportPoint();
            return aimingCamera != null
                ? aimingCamera.ViewportPointToRay(new Vector3(viewportPoint.x, viewportPoint.y, 0f))
                : new Ray(transform.position + Vector3.up * 2f, transform.forward);
        }

        private Vector2 GetAimViewportPoint()
        {
            if (aimController == null && aimingCamera != null)
            {
                aimController = aimingCamera.GetComponent<MechThirdPersonCamera>();
            }

            var viewportPoint = aimController != null
                ? aimController.AimViewportPoint
                : aimViewportPoint;
            return new Vector2(
                Mathf.Clamp(viewportPoint.x, 0.05f, 0.95f),
                Mathf.Clamp(viewportPoint.y, 0.05f, 0.95f));
        }

        private Vector2 GetReticleGuiPosition()
        {
            var viewportPoint = GetAimViewportPoint();
            if (aimingCamera == null)
            {
                return new Vector2(
                    Screen.width * viewportPoint.x,
                    Screen.height * (1f - viewportPoint.y));
            }

            var screenPoint = aimingCamera.ViewportToScreenPoint(
                new Vector3(viewportPoint.x, viewportPoint.y, 0f));
            return new Vector2(screenPoint.x, Screen.height - screenPoint.y);
        }

        private void OnGUI()
        {
            if (GetComponent<MechRadarHudPresenter>() != null)
            {
                return;
            }

            if (aimingCamera == null || radarController == null)
            {
                return;
            }

            hudLabelStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
            };

            if (!radarController.TryGetBestTrack(out var track))
            {
                return;
            }

            var trackedPosition = radarController.PredictTrackPosition(track);
            var screen = aimingCamera.WorldToScreenPoint(trackedPosition);
            if (screen.z <= 0f)
            {
                return;
            }

            var targetCenter = new Vector2(screen.x, Screen.height - screen.y);
            var color = radarController.TryGetMissileFireSolution(
                    missileLockDegrees,
                    BuildMissileKinematics(),
                    out _,
                    out _)
                ? new Color(1f, 0.35f, 0.12f)
                : new Color(0.25f, 0.9f, 0.95f);
            const float bracket = 34f;
            DrawCornerBrackets(targetCenter, bracket, color);
            var range = Vector3.Distance(transform.position, trackedPosition);
            hudLabelStyle.normal.textColor = color;
            GUI.Label(new Rect(targetCenter.x - 90f, targetCenter.y - 65f, 180f, 22f),
                $"{track.TrackId}  {range:0} m  {track.Lifecycle}  {track.Confidence:P0}", hudLabelStyle);
        }

        private MissileKinematicProfile BuildMissileKinematics()
        {
            var profile = airToAirMissileProfile;
            return new MissileKinematicProfile
            {
                EjectionSpeed = profile.EjectionSpeedMps,
                InertMass =
                    profile.DryMassKg +
                    profile.RcsPropellantMassKg,
                MainPropellantMass =
                    profile.MainPropellantMassKg,
                MainEngineThrust =
                    profile.MainEngineThrustNewtons,
                MainMassFlow =
                    profile.MainMassFlowKgPerSecond,
            };
        }

        private MissileKinematicProfile BuildInterceptorKinematics()
        {
            var profile = interceptorMissileProfile;
            return new MissileKinematicProfile
            {
                EjectionSpeed = profile.EjectionSpeedMps,
                InertMass =
                    profile.DryMassKg +
                    profile.RcsPropellantMassKg,
                MainPropellantMass =
                    profile.MainPropellantMassKg,
                MainEngineThrust =
                    profile.MainEngineThrustNewtons,
                MainMassFlow =
                    profile.MainMassFlowKgPerSecond,
            };
        }

        private static double3 ToDouble3(Vector3 value) =>
            new double3(value.x, value.y, value.z);

        private static Vector3 ToVector3(double3 value) =>
            new Vector3(
                (float)value.x,
                (float)value.y,
                (float)value.z);

        private static MissileFlightProfile ResolveProfile(
            MissileFlightProfile serializedProfile,
            MissileFlightProfile fallback)
        {
            return serializedProfile.IsConfigured
                ? serializedProfile.Sanitized()
                : fallback.Sanitized();
        }

        public static void SpawnImpact(
            Vector3 position,
            Vector3 normal,
            TestWeaponKind kind,
            MechEffectPrefabLibrary effectLibrary = null,
            int impactFfxId = 0)
        {
            // 命中特效统一走程序化实现：可提交、可复现，且带碎片。
            // 此前只有步枪传了 impactFfxId，火炮与全部导弹都落到白球兜底，等于没有命中特效。
            //
            // FFX 命中层已停用（保留参数是因为整条调用链仍在透传，且后续可能用于别的效果）：
            // ffx_6011101 的 Model 发射器引用模型 88060、颜色 (1, 0.37, 0.05)、尺寸 1×1×20，
            // 而它的 matxml 里 g_DiffuseTexture 为空——渲染出来就是一根无贴图的纯红色加色长锥，
            // 即反馈中"命中和被命中时的红色三角"。缺贴图是提取管线的产物，不是可调的美术参数。
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

        private void SpawnNativeOneShot(
            int ffxId,
            Transform anchor,
            Vector3 position,
            Vector3 direction,
            bool shortenMuzzleFlash,
            float maximumVisibleSeconds)
        {
            if (nativeEffectLibrary == null || ffxId <= 0 ||
                !nativeEffectLibrary.TryGet(ffxId, out var prefab))
            {
                return;
            }

            var rotation = Quaternion.LookRotation(direction, Vector3.up);
            var effect = anchor != null
                ? Instantiate(prefab, position, rotation, anchor)
                : Instantiate(prefab, position, rotation);
            effect.name = $"Native_FFX_{ffxId}_OneShot";
            if (shortenMuzzleFlash)
            {
                ApplyMuzzleFlashTiming(effect, maximumVisibleSeconds);
            }
            PlayAllParticles(effect);
            Destroy(effect, GetOneShotLifetime(effect));
        }

        /// <summary>
        /// 枪口闪光的可见时长上限。必须明显短于射击间隔，否则连射时前一发的闪光还没结束
        /// 后一发就叠上来，读起来是枪口挂着一团常亮的火——`600 RPM` 的间隔只有 `0.1 s`，
        /// 而旧的固定上限是 `0.18 s`，必然重叠。真实枪口闪光只有几毫秒，屏幕上给 2～3 帧即可。
        /// </summary>
        private float ResolveMuzzleFlashVisibleSeconds(TestWeaponKind kind)
        {
            switch (kind)
            {
                case TestWeaponKind.Rifle:
                    // 连射武器：必须短于射击间隔，否则前后两发的闪光会叠成一团常亮的火。
                    var cycleSeconds = 60f / Mathf.Max(rifleRoundsPerMinute, 60f);
                    return Mathf.Min(MuzzleFlashMaximumVisibleSeconds, cycleSeconds * 0.45f);

                case TestWeaponKind.Cannon:
                    // 火炮是单发重武器，没有重叠风险，闪光可以明显长于步枪——它本来就该
                    // 读作"一声沉重的炮响"。原生 ffx_6031100 的资产是完好的（Model 81040
                    // 带贴图 s31460_a.tif，billboard 也都有图），所以只放宽时长即可。
                    return CannonMuzzleFlashVisibleSeconds;

                default:
                    return MuzzleFlashMaximumVisibleSeconds;
            }
        }

        private static void ApplyMuzzleFlashTiming(
            GameObject effect,
            float maximumVisibleSeconds)
        {
            maximumVisibleSeconds = Mathf.Max(0.016f, maximumVisibleSeconds);

            // 先量出源效果最晚结束的时刻，再求<b>统一</b>缩放比。
            // 旧实现对每个发射器各自 `Min(乘数 * 0.4, 0.18)`，混淆了无量纲乘数与秒：
            // `startLifetimeMultiplier` 通常是 1，`Min(0.4, 0.18)` 会得到 0.18，
            // 既不是想要的比例，也把各子发射器之间的相对时序压平了。
            var sourceSpan = 0f;
            foreach (var particles in
                     effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main;
                sourceSpan = Mathf.Max(
                    sourceSpan,
                    main.startDelay.constantMax + main.startLifetime.constantMax);
            }

            foreach (var timedElement in
                     effect.GetComponentsInChildren<MechTimedEffectElement>(true))
            {
                sourceSpan = Mathf.Max(sourceSpan, timedElement.LifetimeSeconds);
            }

            var scale = sourceSpan > 0.0001f
                ? Mathf.Min(MuzzleFlashTimeScale, maximumVisibleSeconds / sourceSpan)
                : MuzzleFlashTimeScale;

            foreach (var particles in
                     effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main;
                main.startDelayMultiplier *= scale;
                main.startLifetimeMultiplier *= scale;
                main.duration = Mathf.Max(0.01f, main.duration * scale);
            }

            foreach (var timedElement in
                     effect.GetComponentsInChildren<MechTimedEffectElement>(true))
            {
                timedElement.Configure(
                    Mathf.Max(0.016f, timedElement.LifetimeSeconds * scale));
            }

            var visibleLifetime = Mathf.Min(
                maximumVisibleSeconds,
                Mathf.Max(0.016f, sourceSpan * scale));
            foreach (var line in
                     effect.GetComponentsInChildren<LineRenderer>(true))
            {
                var timedElement =
                    line.GetComponent<MechTimedEffectElement>();
                if (timedElement == null)
                {
                    timedElement =
                        line.gameObject.AddComponent<MechTimedEffectElement>();
                    timedElement.Configure(visibleLifetime);
                }
            }
        }

        private static float GetOneShotLifetime(GameObject effect)
        {
            var lifetime = 0.1f;
            foreach (var particles in effect.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = particles.main;
                // These imported one-shots emit their only burst at t=0. The final
                // visible particle therefore ends after delay + particle lifetime;
                // adding the system duration again kept an already-finished root
                // alive longer than the source FXR specifies.
                lifetime = Mathf.Max(
                    lifetime,
                    main.startDelay.constantMax + main.startLifetime.constantMax);
            }

            foreach (var timedElement in effect.GetComponentsInChildren<MechTimedEffectElement>(true))
            {
                lifetime = Mathf.Max(lifetime, timedElement.LifetimeSeconds);
            }

            return lifetime + 0.1f;
        }

        private bool TryInstantiateNativeEffect(int ffxId, Transform parent, out GameObject effect)
        {
            if (nativeEffectLibrary != null && ffxId > 0 &&
                nativeEffectLibrary.TryGet(ffxId, out var prefab))
            {
                effect = Instantiate(prefab, parent, false);
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
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);
            for (var i = 0; i < systems.Length; i++) systems[i].Play(true);
        }

        private static bool IsMuzzleTransform(Transform candidate)
        {
            return candidate.name.EndsWith("_dmy_300", StringComparison.OrdinalIgnoreCase) ||
                   candidate.name.EndsWith("_dmy_301", StringComparison.OrdinalIgnoreCase) ||
                   candidate.name.EndsWith("_dmy_100", StringComparison.OrdinalIgnoreCase);
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
            rifleMaterial ??= CreateRuntimeMaterial("Test Rifle", GetWeaponColor(TestWeaponKind.Rifle));
            cannonMaterial ??= CreateRuntimeMaterial("Test Cannon", GetWeaponColor(TestWeaponKind.Cannon));
            missileMaterial ??= CreateRuntimeMaterial("Test Missile", GetWeaponColor(TestWeaponKind.Missile));
        }

        private static void TryAttachMissileVisual(
            Transform projectile,
            Renderer placeholderRenderer,
            Camera presentationCamera,
            MechPartSlot sourceSlot)
        {
            var useLeftMissile =
                sourceSlot == MechPartSlot.LeftBackWeapon;
            var resourcePath = useLeftMissile
                ? LeftMissileVisualResourcePath
                : RightMissileVisualResourcePath;
            var editorAssetPath = useLeftMissile
                ? LeftMissileVisualEditorAssetPath
                : RightMissileVisualEditorAssetPath;
            var visualLength = useLeftMissile
                ? LeftMissileVisualLengthMeters
                : RightMissileVisualLengthMeters;
            var missileName = useLeftMissile
                ? "R-27"
                : "R-77";
            var missileVisualPrefab = useLeftMissile
                ? leftMissileVisualPrefab
                : rightMissileVisualPrefab;
            if (missileVisualPrefab == null)
            {
                missileVisualPrefab = LoadMissileVisualPrefab(
                    resourcePath,
                    editorAssetPath);
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
                    Debug.LogError(
                        $"[MissileVisual] Failed to load {missileName} " +
                        $"from Resources path '{resourcePath}'.");
                }
                return;
            }

            var holder = new GameObject(
                $"{missileName}_MissileVisual");
            holder.transform.SetParent(projectile, false);
            holder.transform.localScale = ReciprocalScale(
                projectile.localScale);
            var visual = Instantiate(
                missileVisualPrefab,
                holder.transform,
                false);
            visual.name = $"{missileName}_MissileModel";
            visual.transform.SetLocalPositionAndRotation(
                Vector3.zero,
                Quaternion.Euler(-90f, 0f, 0f));
            visual.transform.localScale = Vector3.one;

            var colliders = visual.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
            {
                Destroy(colliders[i]);
            }

            var renderers = BakeRigidMissileRenderers(visual);
            if (renderers.Length == 0)
            {
                Destroy(holder);
                Debug.LogError(
                    $"[MissileVisual] The loaded {missileName} model " +
                    "contains no " +
                    "usable renderer.");
                return;
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] is SkinnedMeshRenderer skinnedRenderer)
                {
                    skinnedRenderer.updateWhenOffscreen = true;
                }
            }
            ApplyVisibleMissileMaterials(renderers);

            if (TryCalculateLocalRendererBounds(
                    holder.transform,
                    renderers,
                    out var bounds))
            {
                var longestAxis = Mathf.Max(
                    bounds.size.x,
                    bounds.size.y,
                    bounds.size.z);
                if (longestAxis > 0.0001f)
                {
                    visual.transform.localScale *=
                        visualLength / longestAxis;
                }

                if (TryCalculateLocalRendererBounds(
                        holder.transform,
                        renderers,
                        out bounds))
                {
                    visual.transform.localPosition -= bounds.center;
                }
            }

            if (TryCalculateLocalRendererBounds(
                    holder.transform,
                    renderers,
                    out var finalBounds))
            {
                holder.AddComponent<MechMissileVisualLod>().Configure(
                    presentationCamera,
                    GetCrossSectionDiameter(finalBounds.size),
                    minimumScreenDiameterPixels: 8f,
                    maximumVisualScale: 6f);

                if (missileVisualLoadSuccesses.Add(resourcePath))
                {
                    Debug.Log(
                        $"[MissileVisual] Loaded {missileName} " +
                        $"'{missileVisualPrefab.name}', renderers=" +
                        $"{renderers.Length}, size={finalBounds.size}.");
                }
            }

            placeholderRenderer.enabled = false;
        }

        private static GameObject LoadMissileVisualPrefab(
            string resourcePath,
            string editorAssetPath)
        {
            var prefab =
                Resources.Load<GameObject>(resourcePath);
            if (prefab != null)
            {
                return prefab;
            }

            var assetName = System.IO.Path.GetFileName(resourcePath);
            prefab = Resources.LoadAll<GameObject>(string.Empty)
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(
                        candidate.name,
                        assetName,
                        StringComparison.OrdinalIgnoreCase));
            if (prefab != null)
            {
                return prefab;
            }

#if UNITY_EDITOR
            return UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                editorAssetPath);
#else
            return null;
#endif
        }

        private static Renderer[] BakeRigidMissileRenderers(
            GameObject visual)
        {
            var sourceRenderers =
                visual.GetComponentsInChildren<Renderer>(true);
            var renderers = new List<Renderer>(sourceRenderers.Length);
            for (var i = 0; i < sourceRenderers.Length; i++)
            {
                if (sourceRenderers[i] is not SkinnedMeshRenderer skinned)
                {
                    renderers.Add(sourceRenderers[i]);
                    continue;
                }

                var bakedMesh = new Mesh
                {
                    name = $"{skinned.sharedMesh.name}_MissileBaked",
                };
                skinned.BakeMesh(bakedMesh, false);
                if (bakedMesh.vertexCount == 0)
                {
                    Destroy(bakedMesh);
                    renderers.Add(skinned);
                    continue;
                }

                var staticVisual = new GameObject(
                    $"{skinned.gameObject.name}_Static");
                staticVisual.transform.SetParent(
                    skinned.transform.parent,
                    false);
                staticVisual.transform.SetLocalPositionAndRotation(
                    skinned.transform.localPosition,
                    skinned.transform.localRotation);
                staticVisual.transform.localScale =
                    skinned.transform.localScale;

                staticVisual.AddComponent<MeshFilter>().sharedMesh =
                    bakedMesh;
                var staticRenderer =
                    staticVisual.AddComponent<MeshRenderer>();
                staticRenderer.sharedMaterials =
                    skinned.sharedMaterials;
                staticRenderer.shadowCastingMode =
                    skinned.shadowCastingMode;
                staticRenderer.receiveShadows =
                    skinned.receiveShadows;
                staticRenderer.renderingLayerMask =
                    skinned.renderingLayerMask;
                staticRenderer.lightProbeUsage =
                    skinned.lightProbeUsage;
                staticRenderer.reflectionProbeUsage =
                    skinned.reflectionProbeUsage;
                staticVisual.AddComponent<MechGeneratedMeshOwner>()
                    .Configure(bakedMesh);

                skinned.enabled = false;
                renderers.Add(staticRenderer);
            }

            return renderers.ToArray();
        }

        private static void ApplyVisibleMissileMaterials(
            IReadOnlyList<Renderer> renderers)
        {
            for (var rendererIndex = 0;
                 rendererIndex < renderers.Count;
                 rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                var materials = renderer.sharedMaterials;
                for (var materialIndex = 0;
                     materialIndex < materials.Length;
                     materialIndex++)
                {
                    var source = materials[materialIndex];
                    if (source == null)
                    {
                        continue;
                    }

                    if (!missileUnlitMaterials.TryGetValue(
                            source,
                            out var visibleMaterial) ||
                        visibleMaterial == null)
                    {
                        visibleMaterial =
                            CreateVisibleMissileMaterial(source);
                        missileUnlitMaterials[source] = visibleMaterial;
                    }
                    materials[materialIndex] = visibleMaterial;
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static Material CreateVisibleMissileMaterial(
            Material source)
        {
            var shader =
                Shader.Find("Universal Render Pipeline/Unlit") ??
                Shader.Find("Unlit/Texture") ??
                Shader.Find("Unlit/Color");
            var material = new Material(shader)
            {
                name = $"{source.name}_MissileUnlit",
            };
            var albedo = source.HasProperty("_BaseMap")
                ? source.GetTexture("_BaseMap")
                : source.HasProperty("_MainTex")
                    ? source.GetTexture("_MainTex")
                    : null;
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

            var color = source.HasProperty("_BaseColor")
                ? source.GetColor("_BaseColor")
                : source.HasProperty("_Color")
                    ? source.GetColor("_Color")
                    : Color.white;
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
            return new Vector3(
                Mathf.Abs(scale.x) > 0.0001f ? 1f / scale.x : 1f,
                Mathf.Abs(scale.y) > 0.0001f ? 1f / scale.y : 1f,
                Mathf.Abs(scale.z) > 0.0001f ? 1f / scale.z : 1f);
        }

        private static float GetCrossSectionDiameter(Vector3 size)
        {
            var dimensions = new[] { size.x, size.y, size.z };
            Array.Sort(dimensions);
            return Mathf.Max(dimensions[1], 0.01f);
        }

        private static bool TryCalculateLocalRendererBounds(
            Transform reference,
            IReadOnlyList<Renderer> renderers,
            out Bounds bounds)
        {
            bounds = default;
            var initialized = false;
            for (var i = 0; i < renderers.Count; i++)
            {
                var renderer = renderers[i];
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                var worldBounds = renderer.bounds;
                var center = worldBounds.center;
                var extents = worldBounds.extents;
                for (var x = -1; x <= 1; x += 2)
                {
                    for (var y = -1; y <= 1; y += 2)
                    {
                        for (var z = -1; z <= 1; z += 2)
                        {
                            var corner = center + Vector3.Scale(
                                extents,
                                new Vector3(x, y, z));
                            var local = reference.InverseTransformPoint(corner);
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
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = materialName };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            return material;
        }

        private static void DrawCornerBrackets(Vector2 center, float extent, Color color)
        {
            const float length = 12f;
            const float width = 2f;
            DrawRect(new Rect(center.x - extent, center.y - extent, length, width), color);
            DrawRect(new Rect(center.x - extent, center.y - extent, width, length), color);
            DrawRect(new Rect(center.x + extent - length, center.y - extent, length, width), color);
            DrawRect(new Rect(center.x + extent - width, center.y - extent, width, length), color);
            DrawRect(new Rect(center.x - extent, center.y + extent - width, length, width), color);
            DrawRect(new Rect(center.x - extent, center.y + extent - length, width, length), color);
            DrawRect(new Rect(center.x + extent - length, center.y + extent - width, length, width), color);
            DrawRect(new Rect(center.x + extent - width, center.y + extent - length, width, length), color);
        }

        private static void DrawRect(Rect rect, Color color)
        {
            var previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previous;
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

        public void Configure(
            Camera camera,
            float crossSectionDiameter,
            float minimumScreenDiameterPixels,
            float maximumVisualScale)
        {
            presentationCamera = camera;
            baseLocalScale = transform.localScale;
            crossSectionDiameterMeters =
                Mathf.Max(crossSectionDiameter, 0.01f);
            this.minimumScreenDiameterPixels =
                Mathf.Max(minimumScreenDiameterPixels, 1f);
            this.maximumVisualScale =
                Mathf.Max(maximumVisualScale, 1f);
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
            if (presentationCamera == null ||
                presentationCamera.pixelHeight <= 0)
            {
                transform.localScale = baseLocalScale;
                return;
            }

            var distance = Vector3.Distance(
                presentationCamera.transform.position,
                transform.position);
            var verticalSpanAtDistance =
                2f * distance *
                Mathf.Tan(
                    presentationCamera.fieldOfView *
                    0.5f * Mathf.Deg2Rad);
            var metersPerPixel =
                verticalSpanAtDistance / presentationCamera.pixelHeight;
            var requiredDiameter =
                metersPerPixel * minimumScreenDiameterPixels;
            var visualScale = Mathf.Clamp(
                requiredDiameter / crossSectionDiameterMeters,
                1f,
                maximumVisualScale);

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
