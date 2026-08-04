using System.Collections.Generic;
using System.Text;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 玩家控制编排（把架构六层接成正式闭环）：
    /// 输入适配 → 镜头/Yaw → 运动积分 → MechGraphContext → 行为图解释器（第 0 层全身）
    /// → 武器命令层（第 1 层上半身遮罩，与全身并行）。
    /// 玩家/AI/测试共享行为图入口，本组件只是"玩家输入源"的装配体——
    /// 没有任何按时间硬播动作的路径。QB 物理脉冲挂在行为图 StateChanged 上，
    /// 保证脉冲、全身动作、（后续的）特效由同一次状态转换驱动。
    /// </summary>
    public sealed class MechPlayerRig : MonoBehaviour
    {
        private const string ForwardQuickBoostActionId = "a000_004240";
        private const string BackwardQuickBoostActionId = "a000_004241";
        private const string LeftQuickBoostActionId = "a000_004242";
        private const string RightQuickBoostActionId = "a000_004243";
        private const string ForwardAirQuickBoostActionId = "a000_122410";
        private const string BackwardAirQuickBoostActionId = "a000_122411";
        private const string LeftAirQuickBoostActionId = "a000_122412";
        private const string RightAirQuickBoostActionId = "a000_122413";
        private const string SpaceFlightCruiseActionId = "a000_100002";
        private const string AssaultBoostCruiseActionId = "a000_120200";
        private const string ShoulderRestActionId = "a000_800000";
        private const string ShoulderDeployActionId = "a000_800110";
        private const string ShoulderOpenActionId = "a000_800120";
        private const string ShoulderFireActionId = "a000_800210";
        private const string ShoulderCloseActionId = "a000_800320";
        private const string ShoulderStowActionId = "a000_800310";

        private static readonly string[] ShoulderFireSequenceActionIds =
        {
            ShoulderDeployActionId,
            ShoulderOpenActionId,
            ShoulderFireActionId,
            ShoulderCloseActionId,
            ShoulderStowActionId,
        };

        private enum FullBodyOverrideKind
        {
            None,
            QuickBoost,
            AssaultBoostCruise,
        }

        private enum ShoulderLauncherPhase
        {
            Stowed,
            Deploying,
            Ready,
            Opening,
            Firing,
            Closing,
            Stowing,
            FallbackFiring,
        }

        [SerializeField] private MechPlayerInputAdapter input;
        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private MechThrustVisualizer thrustVisualizer;
        [SerializeField] private MechThirdPersonCamera cam;
        [SerializeField] private MechSpaceDustField dustField;
        [SerializeField] private MechOrbitSynchronizer orbitSynchronizer;
        [SerializeField] private MechBehaviorGraphRunner graphRunner;
        [SerializeField] private MechBodyActionPlayer actionPlayer;
        [SerializeField] private MechActionRegistry actionRegistry;
        [SerializeField] private List<string> upperBodyBoneNames = new List<string>();
        [SerializeField] private MechWeaponArmLayer weaponLayer;
        [SerializeField] private MechThermalSignature thermalSignature;
        [SerializeField] private MechThermalVisionController thermalVision;
        [SerializeField] private CombatTestTarget combatTarget;

        [Tooltip("キャラ制御フラグ近似：状态进入该秒数后视为全部取消窗口开放。TAE 旗标窗进 IR 后由数据替换。")]
        [SerializeField] private float cancelFlagUnlockSeconds = 0.15f;
        [Tooltip("QB 全身状态期间压制上半身武器层（官方图明确禁止前的保守近似）。")]
        [SerializeField] private bool suppressUpperDuringQuickBoost = true;
        [Tooltip("条件目录未覆盖 QB 退出条件时的最大状态时长，防止地面滑行永久卡住。")]
        [SerializeField] private float quickBoostStateMaxSeconds = 0.8f;
        [SerializeField] private bool showFlightInstruments = true;
        [SerializeField] private bool showDebugOverlay = true;
        [SerializeField] private bool aiControlled;

        private GUIStyle instrumentCaptionStyle;
        private GUIStyle instrumentValueStyle;
        private GUIStyle instrumentUnitStyle;
        private GUIStyle instrumentEnergyStyle;

        private bool[] stateIsQuickBoost = new bool[0];
        private int idleState = -1;
        private int groundBoostState = -1;
        private int flightState = -1;
        private int quickBoostState = -1;
        private int airQuickBoostState = -1;
        private bool quickBoostActivationConsumed;
        private FullBodyOverrideKind fullBodyOverrideKind;
        private string fullBodyOverrideActionId = string.Empty;
        private float fullBodyOverrideElapsed;
        private MechPlayerInputFrame lastFrame;
        private MechRuntimeState runtimeState;
        private MechRadarController radar;
        private Camera viewCamera;
        private ShoulderLauncherState leftShoulderLauncher;
        private ShoulderLauncherState rightShoulderLauncher;
        private bool actionPlayerAttached;

        private sealed class ShoulderLauncherState
        {
            public MechEquipmentAnimationPlayer Player;
            public MechPartSlot Slot;
            public ShoulderLauncherPhase Phase;
            public bool FireRequested;
            public bool LaunchAuthorizedThisFrame;
            public bool HasCanonicalCycle;
            public string FallbackFireActionId;
        }

        private void Awake()
        {
            // Upgrade generated scenes saved before the telemetry field existed.
            showFlightInstruments = true;
            if (input == null) input = GetComponent<MechPlayerInputAdapter>();
            if (motion == null) motion = GetComponent<MechFlightMotionController>();
            if (thrustVisualizer == null)
            {
                thrustVisualizer = GetComponent<MechThrustVisualizer>();
                if (thrustVisualizer == null)
                {
                    thrustVisualizer = gameObject.AddComponent<MechThrustVisualizer>();
                }
            }
            thrustVisualizer.Configure(motion);
            if (weaponLayer == null) weaponLayer = GetComponent<MechWeaponArmLayer>();
            if (graphRunner == null) graphRunner = GetComponent<MechBehaviorGraphRunner>();
            runtimeState = GetComponent<MechRuntimeState>();
            radar = GetComponent<MechRadarController>();
            if (combatTarget == null)
            {
                combatTarget = GetComponent<CombatTestTarget>();
            }
            if (!aiControlled &&
                radar != null &&
                GetComponent<MechThreatWarningPresenter>() == null)
            {
                gameObject.AddComponent<MechThreatWarningPresenter>();
            }
            if (orbitSynchronizer == null)
            {
                orbitSynchronizer = GetComponent<MechOrbitSynchronizer>();
                if (orbitSynchronizer == null)
                {
                    orbitSynchronizer = gameObject.AddComponent<MechOrbitSynchronizer>();
                }
            }
            orbitSynchronizer.Configure(motion, radar);
            thermalSignature = GetComponent<MechThermalSignature>();
            if (thermalSignature == null)
            {
                thermalSignature = gameObject.AddComponent<MechThermalSignature>();
            }
            thermalSignature.Configure(motion);
            if (!aiControlled && thermalVision == null)
            {
                thermalVision = FindAnyObjectByType<MechThermalVisionController>();
                if (thermalVision == null)
                {
                    thermalVision = gameObject.AddComponent<MechThermalVisionController>();
                }
            }
            if (cam != null)
            {
                viewCamera = cam.GetComponent<Camera>();
            }

            if (graphRunner != null && graphRunner.Graph != null)
            {
                BuildStateFlags(graphRunner.Graph);
                graphRunner.StateChanged += OnGraphStateChanged;
            }

            if (cam != null && motion != null)
            {
                cam.Configure(transform, motion);
            }
        }

        public void ConfigureAiControl()
        {
            aiControlled = true;
            cam = null;
            showFlightInstruments = false;
            showDebugOverlay = false;
            thermalVision = null;
        }

        public void ConfigureActionPlayback(
            MechActionRegistry registry,
            IEnumerable<string> maskedUpperBodyBoneNames)
        {
            actionRegistry = registry;
            actionPlayerAttached = false;
            upperBodyBoneNames.Clear();
            if (maskedUpperBodyBoneNames != null)
            {
                upperBodyBoneNames.AddRange(maskedUpperBodyBoneNames);
            }
        }

        /// <summary>
        /// Binds an existing body player or creates a local one from the rig's
        /// action configuration. Old generated scenes recover that data from
        /// the showcase controller once, then own an independent graph.
        /// </summary>
        private bool EnsureActionPlayer()
        {
            if (actionPlayer == null)
            {
                actionPlayer = GetComponentInChildren<MechBodyActionPlayer>();
            }

            if (actionPlayer == null || !actionPlayer.IsGraphValid)
            {
                if (!TryCreateActionPlayer())
                {
                    return false;
                }
            }

            if (!actionPlayerAttached)
            {
                if (weaponLayer != null)
                {
                    weaponLayer.Configure(actionPlayer);
                }

                if (graphRunner != null)
                {
                    graphRunner.AttachActionPlayer(actionPlayer);
                }

                actionPlayerAttached = true;
            }

            return true;
        }

        private bool TryCreateActionPlayer()
        {
            RecoverActionPlaybackConfiguration();
            if (actionRegistry == null || runtimeState == null)
            {
                return false;
            }

            var legacyPlayer = GetComponent<MechSynchronizedAnimationPlayer>();
            if (legacyPlayer != null)
            {
                legacyPlayer.StopPlayback();
                legacyPlayer.enabled = false;
            }

            if (actionPlayer == null)
            {
                actionPlayer = gameObject.AddComponent<MechBodyActionPlayer>();
            }

            actionPlayer.Configure(
                actionRegistry,
                runtimeState,
                upperBodyBoneNames,
                initialFullBodyActionId: null,
                speed: 1f);
            if (!actionPlayer.IsGraphValid)
            {
                return false;
            }

            Debug.Log(
                $"[MechPlayerRig] Created local body action player for " +
                $"{(aiControlled ? "AI" : "player")} rig. parts={actionPlayer.PartCount}.",
                this);
            return true;
        }

        private void RecoverActionPlaybackConfiguration()
        {
            if (actionRegistry != null && upperBodyBoneNames.Count > 0)
            {
                return;
            }

            // Migration path for generated scenes saved before each rig owned
            // its animation configuration. The showcase remains the canonical
            // source asset, but every rig creates and owns its own graph.
            var showcase = FindAnyObjectByType<AC6CombatShowcaseController>();
            if (showcase == null || showcase.ActionRegistry == null)
            {
                return;
            }

            actionRegistry = showcase.ActionRegistry;
            upperBodyBoneNames.Clear();
            if (showcase.UpperBodyBoneNames != null)
            {
                upperBodyBoneNames.AddRange(showcase.UpperBodyBoneNames);
            }
        }

        private void OnDestroy()
        {
            if (graphRunner != null)
            {
                graphRunner.StateChanged -= OnGraphStateChanged;
            }
        }

        private void BuildStateFlags(MechBehaviorGraphAsset graph)
        {
            stateIsQuickBoost = new bool[graph.states.Length];
            for (var i = 0; i < graph.states.Length; i++)
            {
                var name = graph.states[i].officialName ?? string.Empty;
                stateIsQuickBoost[i] = name.Contains("クイックブースト");
                if (name == "空中クイックブースト")
                {
                    airQuickBoostState = i;
                    // State 82 identifies the official air-QB family. Direction is
                    // selected from the verified 122410-13 TAE/HKX sibling set.
                }
                if (!graph.states[i].animAvailable)
                {
                    continue;
                }

                if (name == "待機") idleState = i;
                if (name == "地上ブースト") groundBoostState = i;
                if (name == "ブースト飛行") flightState = i;
                if (name == "クイックブースト") quickBoostState = i;
            }
        }

        private void OnGraphStateChanged(int from, int to)
        {
            // QB 物理脉冲与状态转换同源（验收 6.3：速度变化、动作、特效方向一致）。
            if (!quickBoostActivationConsumed && lastFrame.QuickBoostEdge
                && to >= 0 && to < stateIsQuickBoost.Length && stateIsQuickBoost[to])
            {
                motion.BeginQuickBoost(lastFrame.MoveLocal);
                quickBoostActivationConsumed = true;
            }
        }

        private void Update()
        {
            if (input == null || motion == null || graphRunner == null)
            {
                return;
            }

            var dt = Time.deltaTime;
            EnsureActionPlayer();
            TryInitializeShoulderLaunchers();
            var hasStt = HasSttTrack();
            var interceptorReady = hasStt || HasRwrLockWarning();
            UpdateShoulderLauncher(leftShoulderLauncher, interceptorReady);
            UpdateShoulderLauncher(rightShoulderLauncher, hasStt);
            lastFrame = input.Sample();
            quickBoostActivationConsumed = false;

            if (lastFrame.VectorThrustToggleEdge)
            {
                motion.SetVectorThrustEnabled(!motion.VectorThrustEnabled);
                Debug.Log($"[MechPlayerRig] Vector thrust: {(motion.VectorThrustEnabled ? "ON" : "OFF")}.", this);
            }

            if (lastFrame.ThrustVisualizationToggleEdge)
            {
                thrustVisualizer?.ToggleVisible();
                Debug.Log($"[MechPlayerRig] Thrust visualization: " +
                          $"{(thrustVisualizer != null && thrustVisualizer.Visible ? "ON" : "OFF")}.", this);
            }

            if (lastFrame.ThermalVisionCycleEdge)
            {
                thermalVision?.CycleMode();
            }

            if (thermalSignature != null)
            {
                // 主/副火力=解剖学左/右手；肩部发射经 RequestShoulderFire 也计入对应侧。
                if (lastFrame.PrimaryFireEdge || lastFrame.LeftShoulderFireEdge)
                {
                    thermalSignature.NotifyWeaponFired(leftHand: true);
                }

                if (lastFrame.SecondaryFireEdge || lastFrame.RightShoulderFireEdge)
                {
                    thermalSignature.NotifyWeaponFired(leftHand: false);
                }
            }

            if (lastFrame.LeftShoulderFireEdge)
            {
                RequestShoulderFire(leftShoulderLauncher);
            }

            if (lastFrame.RightShoulderFireEdge)
            {
                RequestShoulderFire(rightShoulderLauncher);
            }

            if (cam != null)
            {
                cam.UpdateLook(in lastFrame);
                if (lastFrame.ReferencePlaneResetEdge)
                {
                    ResetNavigationReference();
                }
                motion.SetAimPitch(cam.PitchDegrees);
            }
            else if (lastFrame.ReferencePlaneResetEdge)
            {
                motion.ResetReferencePlaneToRotation(motion.LogicalRotation);
            }

            if (lastFrame.AssaultBoostEdge)
            {
                var enabled = motion.ToggleAssaultBoost();
                if (enabled)
                {
                    // The former a000_020210 entry starts from its own upright bind-like pose,
                    // producing a visible reset before flight. Crossfade the current locomotion
                    // pose straight into the verified AB cruise instead.
                    BeginAssaultBoostCruise();
                }
            }

            if (lastFrame.QuickBoostEdge)
            {
                var groundedAtTrigger = motion.Grounded;
                quickBoostActivationConsumed = true;
                if (motion.BeginQuickBoost(lastFrame.MoveLocal))
                {
                    var actionId = groundedAtTrigger
                        ? SelectGroundQuickBoostAction(lastFrame.MoveLocal)
                        : SelectAirQuickBoostAction(lastFrame.MoveLocal);
                    var state = groundedAtTrigger ? quickBoostState : airQuickBoostState;
                    if (!BeginFullBodyOverride(FullBodyOverrideKind.QuickBoost, state, actionId))
                    {
                        graphRunner.TryEnterState(state);
                    }
                }
            }

            orbitSynchronizer?.Tick(in lastFrame, dt);
            var motionFrame = lastFrame;
            if (orbitSynchronizer != null && orbitSynchronizer.Active)
            {
                if (orbitSynchronizer.RequestsAutomaticDeparture)
                {
                    motionFrame.RiseHeld = true;
                }
            }
            motion.Tick(in motionFrame, dt);

            var grounded = motion.Grounded;
            var ccf = graphRunner.StateElapsedSeconds >= cancelFlagUnlockSeconds ? 0x1Fu : 0u;
            var graphFrame = lastFrame;
            if (!motion.CanQuickBoost)
            {
                graphFrame.QuickBoostEdge = false;
            }
            var ctx = new MechGraphContext
            {
                PadBits = input.BuildPadBits(in graphFrame, !grounded),
                CharControlFlags = ccf,
                Grounded = grounded,
                Airborne = !grounded,
                EnergyEmpty = motion.ManeuverEnergyDepleted,
                LockedOn = false,
                TargetAttacking = false,
                Guarding = false,
                AttackStateType = 0,
                BodyStateType = 0,
            };
            // The condition catalog is not complete enough to let an airborne graph candidate
            // replace the verified flight loop. Evaluate transitions for history/state progress,
            // but keep the visible action layer under the physical flight-mode decision below.
            // Otherwise an invalid candidate and a000_120200 crossfade against each other every
            // frame, which appears as a small fore/aft twitch while hovering.
            if ((motion.AssaultBoostActive || !grounded) &&
                fullBodyOverrideKind == FullBodyOverrideKind.None)
            {
                graphRunner.TickStateOnly(in ctx, dt);
            }
            else
            {
                graphRunner.Tick(in ctx, dt);
            }

            UpdateFullBodyOverride(grounded, dt);

            // 条件目录仍未覆盖全部官方原子时，以已经确定的物理模式约束基础移动状态。
            // 这不会按时间硬播动作，只把当前模式映射回对应的官方状态。
            var current = graphRunner.CurrentStateIndex;
            var currentIsQuickBoost = current >= 0 && current < stateIsQuickBoost.Length
                                      && stateIsQuickBoost[current];
            if (fullBodyOverrideKind != FullBodyOverrideKind.None)
            {
                // The graph keeps evaluating and recording transitions while the verified
                // transient clip owns the full-body playable.
            }
            else if (motion.AssaultBoostActive)
            {
                BeginAssaultBoostCruise();
            }
            else if (currentIsQuickBoost && graphRunner.StateElapsedSeconds >= quickBoostStateMaxSeconds)
            {
                if (grounded)
                {
                    graphRunner.TryEnterState(
                        lastFrame.MoveLocal.sqrMagnitude > 0.0001f
                            ? groundBoostState
                            : idleState);
                }
                else
                {
                    EnterSpaceFlightCruise();
                }
            }
            else if (!grounded && !currentIsQuickBoost)
            {
                // 太空中即使松开推进输入仍处于飞行姿态；速度由物理层保持惯性。
                // 不允许行为图退回起跳/受击候选并反复播放。
                EnterSpaceFlightCruise();
            }
            else if (grounded && !currentIsQuickBoost && !motion.AssaultBoostActive)
            {
                if (lastFrame.MoveLocal.sqrMagnitude > 0.0001f)
                {
                    graphRunner.TryEnterState(groundBoostState);
                }
                else if (motion.Velocity.sqrMagnitude <= 0.04f)
                {
                    graphRunner.TryEnterState(idleState);
                }
            }

            current = graphRunner.CurrentStateIndex;

            if (weaponLayer != null)
            {
                var suppress = suppressUpperDuringQuickBoost
                               && (fullBodyOverrideKind == FullBodyOverrideKind.QuickBoost ||
                                   (current >= 0 && current < stateIsQuickBoost.Length &&
                                    stateIsQuickBoost[current]));
                weaponLayer.Tick(in lastFrame, suppress, dt);
            }
        }

        private void ResetNavigationReference()
        {
            if (cam == null || motion == null)
            {
                return;
            }

            if (viewCamera == null)
            {
                viewCamera = cam.GetComponent<Camera>();
            }

            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
            }

            if (radar != null &&
                radar.TryGetTrackAtViewport(
                    viewCamera,
                    cam.AimViewportPoint,
                    out var selectedTrack))
            {
                motion.ResetReferencePlaneToward(
                    radar.PredictTrackPosition(selectedTrack));
                orbitSynchronizer?.Configure(motion, radar);
                orbitSynchronizer?.Begin(selectedTrack);
            }
            else
            {
                orbitSynchronizer?.Cancel();
                motion.ResetReferencePlaneToRotation(cam.AimRotation);
            }

            cam.ResetToReferenceHorizon();
        }

        private void LateUpdate()
        {
            // Movement and Animator evaluation both happen before the camera follows.
            // Updating the camera before motion left it one frame behind the mech and
            // made an airborne mech appear to vibrate against the background.
            if (cam != null)
            {
                cam.Follow(Time.deltaTime);
            }

            dustField?.Tick();
        }

        private bool BeginFullBodyOverride(
            FullBodyOverrideKind kind,
            int stateIndex,
            string actionId)
        {
            if (actionPlayer == null || !actionPlayer.HasAction(actionId))
            {
                Debug.LogWarning($"[MechPlayerRig] Transient action is unavailable: {kind} {actionId}.", this);
                return false;
            }

            graphRunner.SetActionPlaybackSuppressed(true);
            var entered = graphRunner.TryEnterStateWithActionOverride(stateIndex, actionId);
            if (!entered && !actionPlayer.PlayFullBody(actionId))
            {
                graphRunner.SetActionPlaybackSuppressed(false);
                return false;
            }

            fullBodyOverrideKind = kind;
            fullBodyOverrideActionId = actionId;
            fullBodyOverrideElapsed = 0f;
            Debug.Log(
                $"[MechPlayerRig] Transient action started: {kind} {actionId}, " +
                $"state={stateIndex}, grounded={motion.Grounded}.",
                this);
            return true;
        }

        private void UpdateFullBodyOverride(bool grounded, float deltaTime)
        {
            if (fullBodyOverrideKind == FullBodyOverrideKind.None)
            {
                return;
            }

            fullBodyOverrideElapsed += deltaTime;
            var finished = fullBodyOverrideKind switch
            {
                FullBodyOverrideKind.QuickBoost =>
                    actionPlayer == null ||
                    actionPlayer.IsFullBodyActionFinished(fullBodyOverrideActionId) ||
                    fullBodyOverrideElapsed >= quickBoostStateMaxSeconds,
                FullBodyOverrideKind.AssaultBoostCruise => !motion.AssaultBoostActive,
                _ => true,
            };
            if (!finished)
            {
                return;
            }

            if (fullBodyOverrideKind == FullBodyOverrideKind.QuickBoost &&
                motion.AssaultBoostActive)
            {
                BeginAssaultBoostCruise();
                return;
            }

            fullBodyOverrideKind = FullBodyOverrideKind.None;
            fullBodyOverrideActionId = string.Empty;
            fullBodyOverrideElapsed = 0f;
            graphRunner.SetActionPlaybackSuppressed(false);

            if (grounded)
            {
                graphRunner.TryEnterState(
                    lastFrame.MoveLocal.sqrMagnitude > 0.0001f
                        ? groundBoostState
                        : idleState);
            }
            else
            {
                EnterSpaceFlightCruise();
            }
        }

        private void EnterSpaceFlightCruise()
        {
            if (graphRunner == null || flightState < 0)
            {
                return;
            }

            if (actionPlayer != null &&
                graphRunner.CurrentStateIndex == flightState &&
                actionPlayer.ActiveFullBodyActionId == SpaceFlightCruiseActionId)
            {
                return;
            }

            if (!graphRunner.TryEnterStateWithActionOverride(
                    flightState,
                    SpaceFlightCruiseActionId))
            {
                graphRunner.TryEnterState(flightState);
            }
        }

        private void BeginAssaultBoostCruise()
        {
            graphRunner.SetActionPlaybackSuppressed(true);
            var entered = graphRunner.TryEnterStateWithActionOverride(
                flightState, AssaultBoostCruiseActionId);
            if (!entered && (actionPlayer == null ||
                             !actionPlayer.PlayFullBody(AssaultBoostCruiseActionId)))
            {
                Debug.LogWarning(
                    $"[MechPlayerRig] AB cruise action is unavailable: {AssaultBoostCruiseActionId}.",
                    this);
                motion.SetAssaultBoostActive(false);
                graphRunner.SetActionPlaybackSuppressed(false);
                fullBodyOverrideKind = FullBodyOverrideKind.None;
                fullBodyOverrideActionId = string.Empty;
                fullBodyOverrideElapsed = 0f;
                return;
            }

            fullBodyOverrideKind = FullBodyOverrideKind.AssaultBoostCruise;
            fullBodyOverrideActionId = AssaultBoostCruiseActionId;
            fullBodyOverrideElapsed = 0f;
            Debug.Log($"[MechPlayerRig] AB cruise started: {AssaultBoostCruiseActionId}.", this);
        }

        private static string SelectGroundQuickBoostAction(Vector2 moveLocal)
        {
            if (Mathf.Abs(moveLocal.x) > Mathf.Abs(moveLocal.y))
            {
                return moveLocal.x < 0f ? LeftQuickBoostActionId : RightQuickBoostActionId;
            }

            return moveLocal.y < 0f ? BackwardQuickBoostActionId : ForwardQuickBoostActionId;
        }

        private static string SelectAirQuickBoostAction(Vector2 moveLocal)
        {
            if (Mathf.Abs(moveLocal.x) > Mathf.Abs(moveLocal.y))
            {
                return moveLocal.x < 0f ? LeftAirQuickBoostActionId : RightAirQuickBoostActionId;
            }

            return moveLocal.y < 0f ? BackwardAirQuickBoostActionId : ForwardAirQuickBoostActionId;
        }

        public bool ConsumeShoulderMissileLaunch(MechPartSlot slot)
        {
            var launcher = slot == MechPartSlot.LeftBackWeapon
                ? leftShoulderLauncher
                : slot == MechPartSlot.RightBackWeapon
                    ? rightShoulderLauncher
                    : null;

            if (launcher == null || !launcher.LaunchAuthorizedThisFrame)
            {
                return false;
            }

            launcher.LaunchAuthorizedThisFrame = false;
            return true;
        }

        private void TryInitializeShoulderLaunchers()
        {
            if (leftShoulderLauncher == null)
            {
                leftShoulderLauncher = CreateShoulderLauncher(MechPartSlot.LeftBackWeapon);
            }

            if (rightShoulderLauncher == null)
            {
                rightShoulderLauncher = CreateShoulderLauncher(MechPartSlot.RightBackWeapon);
            }
        }

        private ShoulderLauncherState CreateShoulderLauncher(MechPartSlot slot)
        {
            var equipmentPlayer = ResolveEquipmentPlayer(slot);
            if (equipmentPlayer == null)
            {
                return null;
            }

            var hasCanonicalSequence = true;
            for (var i = 0; i < ShoulderFireSequenceActionIds.Length; i++)
            {
                if (!equipmentPlayer.HasAction(ShoulderFireSequenceActionIds[i]))
                {
                    hasCanonicalSequence = false;
                    break;
                }
            }

            var fallbackActionId = hasCanonicalSequence
                ? string.Empty
                : FirstAvailableAction(
                    equipmentPlayer,
                    ShoulderFireActionId,
                    "a000_000100",
                    "a000_800410");
            ApplyShoulderRestPose(equipmentPlayer);
            return new ShoulderLauncherState
            {
                Player = equipmentPlayer,
                Slot = slot,
                Phase = ShoulderLauncherPhase.Stowed,
                HasCanonicalCycle = hasCanonicalSequence,
                FallbackFireActionId = fallbackActionId,
            };
        }

        private bool HasSttTrack()
        {
            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
            }

            return radar != null && radar.HasStt;
        }

        private bool HasRwrLockWarning()
        {
            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
            }

            return radar != null &&
                   radar.TryGetHighestThreat(out var threat) &&
                   threat.Level >= EsmThreatLevel.Locked;
        }

        private void RequestShoulderFire(ShoulderLauncherState launcher)
        {
            if (launcher == null || launcher.Player == null)
            {
                Debug.LogWarning("[MechPlayerRig] No shoulder launcher animation player found.", this);
                return;
            }

            switch (launcher.Phase)
            {
                case ShoulderLauncherPhase.Stowed:
                    if (!launcher.HasCanonicalCycle)
                    {
                        StartFallbackShoulderFire(launcher);
                        return;
                    }

                    launcher.FireRequested = true;
                    PlayShoulderAction(launcher, ShoulderDeployActionId, ShoulderLauncherPhase.Deploying);
                    break;

                case ShoulderLauncherPhase.Deploying:
                    launcher.FireRequested = true;
                    break;

                case ShoulderLauncherPhase.Ready:
                    PlayShoulderAction(launcher, ShoulderOpenActionId, ShoulderLauncherPhase.Opening);
                    break;
            }
        }

        private void UpdateShoulderLauncher(ShoulderLauncherState launcher, bool hasStt)
        {
            if (launcher == null || launcher.Player == null)
            {
                return;
            }

            launcher.LaunchAuthorizedThisFrame = false;
            if (launcher.Player.IsPlaying)
            {
                return;
            }

            switch (launcher.Phase)
            {
                case ShoulderLauncherPhase.Stowed:
                    if (hasStt && launcher.HasCanonicalCycle)
                    {
                        PlayShoulderAction(
                            launcher,
                            ShoulderDeployActionId,
                            ShoulderLauncherPhase.Deploying);
                    }
                    break;

                case ShoulderLauncherPhase.Deploying:
                    launcher.Phase = ShoulderLauncherPhase.Ready;
                    if (launcher.FireRequested)
                    {
                        launcher.FireRequested = false;
                        PlayShoulderAction(
                            launcher,
                            ShoulderOpenActionId,
                            ShoulderLauncherPhase.Opening);
                    }
                    else if (!hasStt)
                    {
                        PlayShoulderAction(
                            launcher,
                            ShoulderStowActionId,
                            ShoulderLauncherPhase.Stowing);
                    }
                    break;

                case ShoulderLauncherPhase.Ready:
                    if (!hasStt)
                    {
                        PlayShoulderAction(
                            launcher,
                            ShoulderStowActionId,
                            ShoulderLauncherPhase.Stowing);
                    }
                    break;

                case ShoulderLauncherPhase.Opening:
                    // The opening clip has reached its terminal pose, so the cover is physically open.
                    launcher.LaunchAuthorizedThisFrame = true;
                    PlayShoulderAction(
                        launcher,
                        ShoulderFireActionId,
                        ShoulderLauncherPhase.Firing);
                    break;

                case ShoulderLauncherPhase.Firing:
                    PlayShoulderAction(
                        launcher,
                        ShoulderCloseActionId,
                        ShoulderLauncherPhase.Closing);
                    break;

                case ShoulderLauncherPhase.Closing:
                    if (hasStt)
                    {
                        launcher.Phase = ShoulderLauncherPhase.Ready;
                    }
                    else
                    {
                        PlayShoulderAction(
                            launcher,
                            ShoulderStowActionId,
                            ShoulderLauncherPhase.Stowing);
                    }
                    break;

                case ShoulderLauncherPhase.Stowing:
                    ApplyShoulderRestPose(launcher.Player);
                    launcher.Phase = ShoulderLauncherPhase.Stowed;
                    break;

                case ShoulderLauncherPhase.FallbackFiring:
                    ApplyShoulderRestPose(launcher.Player);
                    launcher.Phase = ShoulderLauncherPhase.Stowed;
                    break;
            }
        }

        private void StartFallbackShoulderFire(ShoulderLauncherState launcher)
        {
            if (string.IsNullOrEmpty(launcher.FallbackFireActionId))
            {
                Debug.LogWarning($"[MechPlayerRig] No playable fire action found for {launcher.Slot}.", this);
                return;
            }

            if (PlayShoulderAction(
                    launcher,
                    launcher.FallbackFireActionId,
                    ShoulderLauncherPhase.FallbackFiring))
            {
                launcher.LaunchAuthorizedThisFrame = true;
            }
        }

        private bool PlayShoulderAction(
            ShoulderLauncherState launcher,
            string actionId,
            ShoulderLauncherPhase nextPhase)
        {
            if (launcher.Player.PlayActionPreservingPose(actionId))
            {
                launcher.Phase = nextPhase;
                return true;
            }

            Debug.LogWarning(
                $"[MechPlayerRig] Failed to play {launcher.Slot} action {actionId}.",
                this);
            ApplyShoulderRestPose(launcher.Player);
            launcher.Phase = ShoulderLauncherPhase.Stowed;
            launcher.FireRequested = false;
            return false;
        }

        private static void ApplyShoulderRestPose(MechEquipmentAnimationPlayer player)
        {
            if (player == null)
            {
                return;
            }

            player.StopPlayback();
            player.SampleActionAtEnd(ShoulderCloseActionId);
            player.SampleActionAtEnd(ShoulderStowActionId);
            if (player.HasAction(ShoulderRestActionId))
            {
                player.SampleActionAtTime(ShoulderRestActionId, 0f);
            }
        }

        private MechEquipmentAnimationPlayer ResolveEquipmentPlayer(MechPartSlot slot)
        {
            if (runtimeState == null)
            {
                runtimeState = GetComponent<MechRuntimeState>();
            }

            if (runtimeState == null)
            {
                return null;
            }

            var parts = runtimeState.AssembledParts;
            for (var i = 0; i < parts.Count; i++)
            {
                var record = parts[i];
                if (record.slot == slot && record.instance != null)
                {
                    return record.instance.GetComponent<MechEquipmentAnimationPlayer>();
                }
            }

            return null;
        }

        private static string FirstAvailableAction(
            MechEquipmentAnimationPlayer equipmentPlayer,
            params string[] actionIds)
        {
            for (var i = 0; i < actionIds.Length; i++)
            {
                if (equipmentPlayer.HasAction(actionIds[i]))
                {
                    return actionIds[i];
                }
            }

            return equipmentPlayer.DefaultActionId;
        }

        private void OnGUI()
        {
            var previousDepth = GUI.depth;
            GUI.depth = -100;
            DrawFlightInstruments();
            GUI.depth = previousDepth;

            // The radar presenter owns the upper-left HUD region. Keep the flight instruments,
            // but suppress the legacy diagnostic text when the integrated radar HUD is active.
            if (GetComponent<MechRadarHudPresenter>() != null ||
                !showDebugOverlay ||
                graphRunner == null)
            {
                return;
            }

            var sb = new StringBuilder(512);
            sb.AppendLine($"状态: [{graphRunner.CurrentStateIndex}] {graphRunner.CurrentStateName}  t={graphRunner.StateElapsedSeconds:F2}s");
            if (actionPlayer != null)
            {
                sb.AppendLine($"全身层: {actionPlayer.ActiveFullBodyActionId}");
                sb.AppendLine($"上半身层: {actionPlayer.ActiveUpperBodyActionId} ({weaponLayer?.Phase})");
                sb.AppendLine($"动作覆盖: {fullBodyOverrideKind} {fullBodyOverrideActionId}");
            }

            if (motion != null)
            {
                sb.AppendLine($"速度: {motion.Velocity.magnitude:F1} m/s {motion.Velocity}");
                sb.AppendLine($"Yaw: {motion.YawDegrees:F1}°  接地: {motion.Grounded}  AB: {motion.AssaultBoostActive}");
                sb.AppendLine($"主推输出: {motion.MainEngineOutput:P0}  技术安全速度: {motion.TechnicalSafetySpeed:F0} m/s");
                sb.AppendLine(
                    $"机动能量: {motion.CurrentManeuverEnergy:F1}/" +
                    $"{motion.MaximumManeuverEnergy:F1}  " +
                    $"耗尽: {motion.ManeuverEnergyDepleted}");
                sb.AppendLine($"肩部上推: {motion.CurrentShoulderLiftForceNewtons.magnitude / 1000f:F0} kN " +
                              $"{motion.CurrentShoulderLiftForceNewtons.normalized}  " +
                              $"腿部上推: {motion.CurrentLegLiftForceNewtons.magnitude / 1000f:F0} kN " +
                              $"{motion.CurrentLegLiftForceNewtons.normalized}");
                sb.AppendLine($"向下配平: {motion.CurrentDescentForceNewtons.magnitude / 1000f:F0} kN " +
                              $"{motion.CurrentDescentForceNewtons.normalized}");
                sb.AppendLine($"矢量推力: {(motion.VectorThrustEnabled ? "开启" : "关闭")}  " +
                              $"推力显示[2]: {(thrustVisualizer != null && thrustVisualizer.Visible ? "开启" : "关闭")}  " +
                              $"惯性锁定[F]: {(motion.InertialThrustLockActive ? "开启" : "关闭")}");
                if (orbitSynchronizer != null && orbitSynchronizer.Active)
                {
                    sb.AppendLine(
                        $"轨道同步: T{orbitSynchronizer.SynchronizedTrackId.Value.Index:00}  " +
                        $"纵向飞控: {orbitSynchronizer.AutomaticLongitudinalCommand:P0}  " +
                        $"纵向偏置: {orbitSynchronizer.LongitudinalVelocityBias:F1} m/s");
                }
            }

            sb.AppendLine($"Input: {lastFrame.MoveLocal}  Position: {transform.position}");

            System.Span<(int from, int to, float time)> history = stackalloc (int, int, float)[6];
            var n = graphRunner.CopyHistory(history);
            for (var i = 0; i < n; i++)
            {
                var (f, t, time) = history[i];
                var fromName = f >= 0 && graphRunner.Graph != null ? graphRunner.Graph.states[f].officialName : "-";
                var toName = graphRunner.Graph != null ? graphRunner.Graph.states[t].officialName : "?";
                sb.AppendLine($"  {time:F1}s  {fromName} → {toName}");
            }

            GUI.Label(new Rect(10, 10, 640, 320), sb.ToString());
        }

        private void DrawFlightInstruments()
        {
            if (!showFlightInstruments || motion == null)
            {
                return;
            }

            EnsureInstrumentStyles();
            const float readoutWidth = 180f;
            const float readoutHeight = 78f;
            var readoutY = Mathf.Clamp(
                Screen.height * 0.5f - readoutHeight * 0.5f,
                170f,
                Screen.height - readoutHeight - 130f);
            var sideInset = Mathf.Clamp(
                Screen.width * 0.16f,
                readoutWidth * 0.75f,
                410f);
            var leftReadout = new Rect(
                sideInset - readoutWidth * 0.5f,
                readoutY,
                readoutWidth,
                readoutHeight);
            var rightReadout = new Rect(
                Screen.width - sideInset - readoutWidth * 0.5f,
                readoutY,
                readoutWidth,
                readoutHeight);

            DrawFlightReadout(
                leftReadout,
                "速度",
                FormatInstrumentValue(motion.Velocity.magnitude),
                "m/s",
                true);
            DrawFlightReadout(
                rightReadout,
                "高度",
                FormatInstrumentValue(motion.Altitude),
                "m",
                false);
            DrawHealthGauge();
            DrawManeuverEnergyGauge();
        }

        private void DrawHealthGauge()
        {
            if (combatTarget == null)
            {
                combatTarget = GetComponent<CombatTestTarget>();
                if (combatTarget == null)
                {
                    return;
                }
            }

            var width = Mathf.Clamp(
                Screen.width * 0.34f,
                300f,
                520f);
            const float height = 14f;
            var rect = new Rect(
                Screen.width * 0.5f - width * 0.5f,
                Screen.height - 86f,
                width,
                height);
            var normalized = combatTarget.HealthFraction;
            var color = normalized <= 0.2f
                ? new Color(1f, 0.2f, 0.14f, 0.98f)
                : normalized <= 0.5f
                    ? new Color(1f, 0.72f, 0.18f, 0.98f)
                    : new Color(0.34f, 0.94f, 0.82f, 0.98f);

            DrawInstrumentRect(
                new Rect(
                    rect.x - 2f,
                    rect.y - 2f,
                    rect.width + 4f,
                    rect.height + 4f),
                new Color(0.01f, 0.05f, 0.045f, 0.9f));
            DrawInstrumentRect(
                new Rect(
                    rect.x,
                    rect.y,
                    rect.width * normalized,
                    rect.height),
                color);
            for (var i = 1; i < 10; i++)
            {
                var x = rect.x + rect.width * i / 10f;
                DrawInstrumentRect(
                    new Rect(x - 0.5f, rect.y, 1f, rect.height),
                    new Color(0.01f, 0.08f, 0.065f, 0.72f));
            }

            instrumentEnergyStyle.normal.textColor = color;
            instrumentEnergyStyle.alignment = TextAnchor.MiddleLeft;
            GUI.Label(
                new Rect(rect.x, rect.y - 24f, 90f, 20f),
                combatTarget.IsAlive ? "AP" : "AP LOST",
                instrumentEnergyStyle);
            instrumentEnergyStyle.alignment = TextAnchor.MiddleRight;
            GUI.Label(
                new Rect(
                    rect.x + rect.width - 160f,
                    rect.y - 24f,
                    160f,
                    20f),
                $"{combatTarget.Health:0} / " +
                $"{combatTarget.MaxHealth:0}",
                instrumentEnergyStyle);
        }

        private void DrawManeuverEnergyGauge()
        {
            var width = Mathf.Clamp(Screen.width * 0.34f, 300f, 520f);
            const float height = 12f;
            var rect = new Rect(
                Screen.width * 0.5f - width * 0.5f,
                Screen.height - 126f,
                width,
                height);
            var normalized = Mathf.Clamp01(motion.ManeuverEnergyNormalized);
            var color = motion.ManeuverEnergyDepleted
                ? new Color(1f, 0.2f, 0.14f, 0.98f)
                : normalized <= 0.3f
                    ? new Color(1f, 0.72f, 0.18f, 0.98f)
                    : new Color(0.38f, 1f, 0.56f, 0.98f);

            DrawInstrumentRect(
                new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f),
                new Color(0.01f, 0.05f, 0.035f, 0.86f));
            DrawInstrumentRect(
                new Rect(rect.x, rect.y, rect.width * normalized, rect.height),
                color);
            for (var i = 1; i < 10; i++)
            {
                var x = rect.x + rect.width * i / 10f;
                DrawInstrumentRect(
                    new Rect(x - 0.5f, rect.y, 1f, rect.height),
                    new Color(0.01f, 0.08f, 0.05f, 0.72f));
            }

            instrumentEnergyStyle.normal.textColor = color;
            instrumentEnergyStyle.alignment = TextAnchor.MiddleLeft;
            GUI.Label(
                new Rect(rect.x, rect.y - 24f, 90f, 20f),
                motion.ManeuverEnergyDepleted ? "EN EMPTY" : "EN",
                instrumentEnergyStyle);
            instrumentEnergyStyle.alignment = TextAnchor.MiddleRight;
            GUI.Label(
                new Rect(rect.x + rect.width - 130f, rect.y - 24f, 130f, 20f),
                $"{motion.CurrentManeuverEnergy:0} / {motion.MaximumManeuverEnergy:0}",
                instrumentEnergyStyle);
        }

        private void DrawFlightReadout(
            Rect rect,
            string caption,
            string value,
            string unit,
            bool pointsRight)
        {
            GUI.Label(
                new Rect(rect.x, rect.y, rect.width, 24f),
                caption,
                instrumentCaptionStyle);
            GUI.Label(
                new Rect(rect.x, rect.y + 19f, rect.width - 30f, 44f),
                value,
                instrumentValueStyle);
            GUI.Label(
                new Rect(
                    rect.x + rect.width - 42f,
                    rect.y + 39f,
                    42f,
                    22f),
                unit,
                instrumentUnitStyle);

            var lineY = rect.y + 64f;
            var innerX = pointsRight ? rect.x + rect.width - 25f : rect.x + 25f;
            var outerX = pointsRight ? rect.x + rect.width + 24f : rect.x - 24f;
            DrawInstrumentLine(
                new Vector2(innerX, lineY),
                new Vector2(outerX, lineY),
                2f);
            DrawInstrumentLine(
                new Vector2(outerX, lineY - 5f),
                new Vector2(outerX, lineY + 5f),
                2f);
        }

        private static string FormatInstrumentValue(float value)
        {
            var magnitude = Mathf.Abs(value);
            return magnitude >= 1_000f
                ? value.ToString("N0")
                : value.ToString("0.0");
        }

        private static void DrawInstrumentLine(
            Vector2 from,
            Vector2 to,
            float thickness)
        {
            var previousColor = GUI.color;
            var direction = to - from;
            var angle = Mathf.Atan2(direction.y, direction.x) *
                        Mathf.Rad2Deg;
            GUI.color = new Color(0.38f, 1f, 0.56f, 0.92f);
            var matrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(
                new Rect(
                    from.x,
                    from.y - thickness * 0.5f,
                    direction.magnitude,
                    thickness),
                Texture2D.whiteTexture);
            GUI.matrix = matrix;
            GUI.color = previousColor;
        }

        private static void DrawInstrumentRect(Rect rect, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        private void EnsureInstrumentStyles()
        {
            if (instrumentCaptionStyle != null)
            {
                return;
            }

            instrumentCaptionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            instrumentCaptionStyle.normal.textColor =
                new Color(0.28f, 0.9f, 0.46f, 0.94f);

            instrumentValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 30,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
            };
            instrumentValueStyle.normal.textColor =
                new Color(0.55f, 1f, 0.68f, 1f);

            instrumentUnitStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.LowerLeft,
            };
            instrumentUnitStyle.normal.textColor =
                new Color(0.28f, 0.9f, 0.46f, 0.88f);

            instrumentEnergyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
            instrumentEnergyStyle.normal.textColor =
                new Color(0.38f, 1f, 0.56f, 0.96f);
        }
    }
}
