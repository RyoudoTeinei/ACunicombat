using System.Text;
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
        [SerializeField] private MechBehaviorGraphRunner graphRunner;
        [SerializeField] private MechBodyActionPlayer actionPlayer;
        [SerializeField] private MechWeaponArmLayer weaponLayer;

        [Tooltip("キャラ制御フラグ近似：状态进入该秒数后视为全部取消窗口开放。TAE 旗标窗进 IR 后由数据替换。")]
        [SerializeField] private float cancelFlagUnlockSeconds = 0.15f;
        [Tooltip("QB 全身状态期间压制上半身武器层（官方图明确禁止前的保守近似）。")]
        [SerializeField] private bool suppressUpperDuringQuickBoost = true;
        [Tooltip("条件目录未覆盖 QB 退出条件时的最大状态时长，防止地面滑行永久卡住。")]
        [SerializeField] private float quickBoostStateMaxSeconds = 0.8f;
        [SerializeField] private bool showFlightInstruments = true;
        [SerializeField] private bool showDebugOverlay = true;

        private GUIStyle instrumentCaptionStyle;
        private GUIStyle instrumentValueStyle;
        private GUIStyle instrumentStatusStyle;

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

        /// <summary>
        /// MechBodyActionPlayer 由 showcase 控制器在 Start() 里装配到机体根上（外部控制模式
        /// 仍保留该职责），因此这里延迟绑定：每帧尝试直到拿到。
        /// </summary>
        private bool EnsureActionPlayer()
        {
            if (actionPlayer != null)
            {
                return true;
            }

            actionPlayer = GetComponentInChildren<MechBodyActionPlayer>();
            if (actionPlayer == null)
            {
                return false;
            }

            if (weaponLayer != null)
            {
                weaponLayer.Configure(actionPlayer);
            }

            if (graphRunner != null)
            {
                graphRunner.AttachActionPlayer(actionPlayer);
            }

            return true;
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
            UpdateShoulderLauncher(leftShoulderLauncher, hasStt);
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
                motion.BeginQuickBoost(lastFrame.MoveLocal);
                quickBoostActivationConsumed = true;
                var actionId = groundedAtTrigger
                    ? SelectGroundQuickBoostAction(lastFrame.MoveLocal)
                    : SelectAirQuickBoostAction(lastFrame.MoveLocal);
                var state = groundedAtTrigger ? quickBoostState : airQuickBoostState;
                if (!BeginFullBodyOverride(FullBodyOverrideKind.QuickBoost, state, actionId))
                {
                    graphRunner.TryEnterState(state);
                }
            }

            motion.Tick(in lastFrame, dt);

            var grounded = motion.Grounded;
            var ccf = graphRunner.StateElapsedSeconds >= cancelFlagUnlockSeconds ? 0x1Fu : 0u;
            var ctx = new MechGraphContext
            {
                PadBits = input.BuildPadBits(in lastFrame, !grounded),
                CharControlFlags = ccf,
                Grounded = grounded,
                Airborne = !grounded,
                EnergyEmpty = false,
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
                graphRunner.TryEnterState(flightState);
            }
            else if (currentIsQuickBoost && graphRunner.StateElapsedSeconds >= quickBoostStateMaxSeconds)
            {
                graphRunner.TryEnterState(grounded
                    ? (lastFrame.MoveLocal.sqrMagnitude > 0.0001f ? groundBoostState : idleState)
                    : flightState);
            }
            else if (!grounded && !currentIsQuickBoost)
            {
                // 太空中即使松开推进输入仍处于飞行姿态；速度由物理层保持惯性。
                // 不允许行为图退回起跳/受击候选并反复播放。
                graphRunner.TryEnterState(flightState);
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
            }
            else
            {
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

            var target = grounded
                ? (lastFrame.MoveLocal.sqrMagnitude > 0.0001f ? groundBoostState : idleState)
                : flightState;
            graphRunner.TryEnterState(target);
            fullBodyOverrideKind = FullBodyOverrideKind.None;
            fullBodyOverrideActionId = string.Empty;
            fullBodyOverrideElapsed = 0f;
            graphRunner.SetActionPlaybackSuppressed(false);
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
                sb.AppendLine($"节流: {motion.MainThrottle:P0}  包线: {motion.OperationalSpeedEnvelope:F0} m/s  " +
                              $"限制: {motion.FlightEnvelopeLimiting}  越权: {motion.FlightEnvelopeOverrideActive}");
                sb.AppendLine($"肩部上推: {motion.CurrentShoulderLiftForceNewtons.magnitude / 1000f:F0} kN " +
                              $"{motion.CurrentShoulderLiftForceNewtons.normalized}  " +
                              $"腿部上推: {motion.CurrentLegLiftForceNewtons.magnitude / 1000f:F0} kN " +
                              $"{motion.CurrentLegLiftForceNewtons.normalized}");
                sb.AppendLine($"向下配平: {motion.CurrentDescentForceNewtons.magnitude / 1000f:F0} kN " +
                              $"{motion.CurrentDescentForceNewtons.normalized}");
                sb.AppendLine($"矢量推力: {(motion.VectorThrustEnabled ? "开启" : "关闭")}  " +
                              $"推力显示[2]: {(thrustVisualizer != null && thrustVisualizer.Visible ? "开启" : "关闭")}  " +
                              $"惯性锁定[F]: {(motion.InertialThrustLockActive ? "开启" : "关闭")}");
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
            const float width = 270f;
            const float height = 328f;
            var x = Mathf.Max(10f, Screen.width - width - 18f);
            var panel = new Rect(x, 18f, width, height);
            GUI.Box(panel, GUIContent.none);

            GUI.Label(new Rect(x + 14f, 28f, 90f, 20f), "REF SPEED", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 96f, 24f, 120f, 32f),
                $"{motion.Velocity.magnitude:0.0} m/s", instrumentValueStyle);
            GUI.Label(new Rect(x + 14f, 58f, 90f, 20f), "HORIZONTAL", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 96f, 54f, 120f, 32f),
                $"{motion.HorizontalSpeed:0.0} m/s", instrumentValueStyle);
            GUI.Label(new Rect(x + 14f, 94f, 90f, 20f), "ALTITUDE", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 96f, 90f, 120f, 32f),
                $"{motion.Altitude:0.0} m", instrumentValueStyle);

            GUI.Label(new Rect(x + 14f, 124f, 90f, 20f), "THROTTLE", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 96f, 120f, 120f, 32f),
                $"{motion.MainThrottle:P0}", instrumentValueStyle);
            GUI.Label(new Rect(x + 14f, 154f, 90f, 20f), "ENVELOPE", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 96f, 150f, 120f, 32f),
                $"{motion.OperationalSpeedEnvelope:0} m/s", instrumentValueStyle);
            GUI.Label(new Rect(x + 14f, 184f, 110f, 20f), "VECTOR THRUST", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 124f, 180f, 132f, 32f),
                motion.VectorThrustEnabled ? "ENABLED" : "DISABLED", instrumentValueStyle);
            GUI.Label(new Rect(x + 14f, 214f, 110f, 20f), "THRUST VIEW [2]", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 124f, 210f, 132f, 32f),
                thrustVisualizer != null && thrustVisualizer.Visible ? "VISIBLE" : "HIDDEN", instrumentValueStyle);
            GUI.Label(new Rect(x + 14f, 244f, 110f, 20f), "THRUST LOCK [F]", instrumentCaptionStyle);
            GUI.Label(new Rect(x + 124f, 240f, 132f, 32f),
                motion.InertialThrustLockActive ? "LOCKED" : "BODY", instrumentValueStyle);
            var status = "FLIGHT CONTROL NOMINAL";
            var statusColor = new Color(0.55f, 0.82f, 0.9f);
            if (motion.Braking)
            {
                status = "RETRO BRAKE ACTIVE";
                statusColor = new Color(1f, 0.48f, 0.18f);
            }
            else if (motion.FlightEnvelopeOverrideActive)
            {
                status = "ENVELOPE OVERRIDE";
                statusColor = new Color(1f, 0.68f, 0.2f);
            }
            else if (motion.FlightEnvelopeLimiting)
            {
                status = "ENVELOPE LIMITING";
                statusColor = new Color(1f, 0.82f, 0.2f);
            }

            instrumentStatusStyle.normal.textColor = statusColor;
            GUI.Label(new Rect(x + 14f, 284f, width - 28f, 30f), status, instrumentStatusStyle);
        }

        private void EnsureInstrumentStyles()
        {
            if (instrumentCaptionStyle != null)
            {
                return;
            }

            instrumentCaptionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
            instrumentCaptionStyle.normal.textColor = new Color(0.68f, 0.76f, 0.8f);

            instrumentValueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleRight,
            };
            instrumentValueStyle.normal.textColor = Color.white;

            instrumentStatusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }
    }
}
