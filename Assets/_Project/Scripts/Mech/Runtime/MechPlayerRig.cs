using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ACunicombat.Mechs
{
	public sealed class MechPlayerRig : MonoBehaviour
	{
		private enum FullBodyOverrideKind
		{
			None,
			QuickBoost,
			AssaultBoostCruise
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
			FallbackFiring
		}

		private sealed class ShoulderLauncherState
		{
			public MechEquipmentAnimationPlayer Player;

			public MechPartSlot Slot;

			public ShoulderLauncherPhase Phase;

			public bool FireRequested;

			public bool LaunchAuthorizedThisFrame;

			public bool HasCanonicalCycle;

			public bool IsAutomaticPointDefense;

			public string FallbackFireActionId;
		}

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

		private static readonly string[] ShoulderFireSequenceActionIds = new string[5] { "a000_800110", "a000_800120", "a000_800210", "a000_800320", "a000_800310" };

		[SerializeField]
		private MechPlayerInputAdapter input;

		[SerializeField]
		private MechFlightMotionController motion;

		[SerializeField]
		private MechThrustVisualizer thrustVisualizer;

		[SerializeField]
		private MechThirdPersonCamera cam;

		[SerializeField]
		private MechSpaceDustField dustField;

		[SerializeField]
		private MechOrbitSynchronizer orbitSynchronizer;

		[SerializeField]
		private MechBehaviorGraphRunner graphRunner;

		[SerializeField]
		private MechBodyActionPlayer actionPlayer;

		[SerializeField]
		private MechActionRegistry actionRegistry;

		[SerializeField]
		private List<string> upperBodyBoneNames = new List<string>();

		[SerializeField]
		private MechWeaponArmLayer weaponLayer;

		[SerializeField]
		private MechThermalSignature thermalSignature;

		[SerializeField]
		private MechThermalVisionController thermalVision;

		public void NotifyShoulderWeaponFired(MechPartSlot slot)
		{
			if (thermalSignature != null)
			{
				thermalSignature.NotifyWeaponFired(slot == MechPartSlot.LeftBackWeapon);
			}
		}

		[SerializeField]
		private CombatTestTarget combatTarget;

		[Tooltip("キャラ制御フラグ近似：状态进入该秒数后视为全部取消窗口开放。TAE 旗标窗进 IR 后由数据替换。")]
		[SerializeField]
		private float cancelFlagUnlockSeconds = 0.15f;

		[Tooltip("QB 全身状态期间压制上半身武器层（官方图明确禁止前的保守近似）。")]
		[SerializeField]
		private bool suppressUpperDuringQuickBoost = true;

		[Tooltip("条件目录未覆盖 QB 退出条件时的最大状态时长，防止地面滑行永久卡住。")]
		[SerializeField]
		private float quickBoostStateMaxSeconds = 0.8f;

		[SerializeField]
		private bool showFlightInstruments = true;

		[SerializeField]
		private bool showDebugOverlay = true;

		[SerializeField]
		private bool aiControlled;

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

		private MechEngineController engineController;

		private MechMechanicalAudioController mechanicalAudioController;

		private ShoulderLauncherState leftShoulderLauncher;

		private ShoulderLauncherState rightShoulderLauncher;

		private bool actionPlayerAttached;

		private void Awake()
		{
			showFlightInstruments = true;
			if (input == null)
			{
				input = GetComponent<MechPlayerInputAdapter>();
			}
			if (motion == null)
			{
				motion = GetComponent<MechFlightMotionController>();
			}
			engineController = GetComponent<MechEngineController>();
			if (engineController == null)
			{
				engineController = base.gameObject.AddComponent<MechEngineController>();
			}
			engineController.Configure(motion, !aiControlled);
			mechanicalAudioController = GetComponent<MechMechanicalAudioController>();
			if (mechanicalAudioController == null)
			{
				mechanicalAudioController = base.gameObject.AddComponent<MechMechanicalAudioController>();
			}
			mechanicalAudioController.Configure(motion, !aiControlled);
			if (thrustVisualizer == null)
			{
				thrustVisualizer = GetComponent<MechThrustVisualizer>();
				if (thrustVisualizer == null)
				{
					thrustVisualizer = base.gameObject.AddComponent<MechThrustVisualizer>();
				}
			}
			thrustVisualizer.Configure(motion);
			if (weaponLayer == null)
			{
				weaponLayer = GetComponent<MechWeaponArmLayer>();
			}
			if (graphRunner == null)
			{
				graphRunner = GetComponent<MechBehaviorGraphRunner>();
			}
			runtimeState = GetComponent<MechRuntimeState>();
			radar = GetComponent<MechRadarController>();
			if (combatTarget == null)
			{
				combatTarget = GetComponent<CombatTestTarget>();
			}
			if (combatTarget != null && runtimeState?.AggregatedStats != null)
			{
				combatTarget.ConfigureProtection(
					runtimeState.AggregatedStats.armorPoints,
					runtimeState.AggregatedStats.structurePoints);
			}
			if (!aiControlled && radar != null && GetComponent<MechThreatWarningPresenter>() == null)
			{
				base.gameObject.AddComponent<MechThreatWarningPresenter>();
			}
			if (orbitSynchronizer == null)
			{
				orbitSynchronizer = GetComponent<MechOrbitSynchronizer>();
				if (orbitSynchronizer == null)
				{
					orbitSynchronizer = base.gameObject.AddComponent<MechOrbitSynchronizer>();
				}
			}
			orbitSynchronizer.Configure(motion, radar);
			thermalSignature = GetComponent<MechThermalSignature>();
			if (thermalSignature == null)
			{
				thermalSignature = base.gameObject.AddComponent<MechThermalSignature>();
			}
			thermalSignature.Configure(motion);
			if (!aiControlled && thermalVision == null)
			{
				thermalVision = UnityEngine.Object.FindAnyObjectByType<MechThermalVisionController>();
				if (thermalVision == null)
				{
					thermalVision = base.gameObject.AddComponent<MechThermalVisionController>();
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
				cam.Configure(base.transform, motion);
			}
			if (!aiControlled && GetComponent<MechAssemblyMenu>() == null)
			{
				base.gameObject.AddComponent<MechAssemblyMenu>();
			}
			if (!aiControlled && input != null && cam != null &&
				GetComponent<MechSystemsRadialMenu>() == null)
			{
				base.gameObject.AddComponent<MechSystemsRadialMenu>();
			}
			if (!aiControlled && motion != null)
			{
				motion.SetVectorThrustEnabled(true);
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

		public void ConfigureActionPlayback(MechActionRegistry registry, IEnumerable<string> maskedUpperBodyBoneNames)
		{
			actionRegistry = registry;
			actionPlayerAttached = false;
			upperBodyBoneNames.Clear();
			if (maskedUpperBodyBoneNames != null)
			{
				upperBodyBoneNames.AddRange(maskedUpperBodyBoneNames);
			}
		}

		private bool EnsureActionPlayer()
		{
			if (actionPlayer == null)
			{
				actionPlayer = GetComponentInChildren<MechBodyActionPlayer>();
			}
			if ((actionPlayer == null || !actionPlayer.IsGraphValid) && !TryCreateActionPlayer())
			{
				return false;
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
			MechSynchronizedAnimationPlayer legacyPlayer = GetComponent<MechSynchronizedAnimationPlayer>();
			if (legacyPlayer != null)
			{
				legacyPlayer.StopPlayback();
				legacyPlayer.enabled = false;
			}
			if (actionPlayer == null)
			{
				actionPlayer = base.gameObject.AddComponent<MechBodyActionPlayer>();
			}
			actionPlayer.Configure(actionRegistry, runtimeState, upperBodyBoneNames);
			if (!actionPlayer.IsGraphValid)
			{
				return false;
			}
			Debug.Log("[MechPlayerRig] Created local body action player for " + string.Format("{0} rig. parts={1}.", aiControlled ? "AI" : "player", actionPlayer.PartCount), this);
			return true;
		}

		private void RecoverActionPlaybackConfiguration()
		{
			if (actionRegistry != null && upperBodyBoneNames.Count > 0)
			{
				return;
			}
			AC6CombatShowcaseController showcase = UnityEngine.Object.FindAnyObjectByType<AC6CombatShowcaseController>();
			if (!(showcase == null) && !(showcase.ActionRegistry == null))
			{
				actionRegistry = showcase.ActionRegistry;
				upperBodyBoneNames.Clear();
				if (showcase.UpperBodyBoneNames != null)
				{
					upperBodyBoneNames.AddRange(showcase.UpperBodyBoneNames);
				}
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
			for (int i = 0; i < graph.states.Length; i++)
			{
				string name = graph.states[i].officialName ?? string.Empty;
				stateIsQuickBoost[i] = name.Contains("クイックブースト");
				if (name == "空中クイックブースト")
				{
					airQuickBoostState = i;
				}
				if (graph.states[i].animAvailable)
				{
					if (name == "待機")
					{
						idleState = i;
					}
					if (name == "地上ブースト")
					{
						groundBoostState = i;
					}
					if (name == "ブースト飛行")
					{
						flightState = i;
					}
					if (name == "クイックブースト")
					{
						quickBoostState = i;
					}
				}
			}
		}

		private void OnGraphStateChanged(int from, int to)
		{
			if (!quickBoostActivationConsumed && lastFrame.QuickBoostEdge && to >= 0 && to < stateIsQuickBoost.Length && stateIsQuickBoost[to])
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
			float dt = Time.deltaTime;
			EnsureActionPlayer();
			TryInitializeShoulderLaunchers();
			bool hasStt = HasSttTrack();
			bool interceptorReady = hasStt || HasRwrLockWarning();
			UpdateShoulderLauncher(leftShoulderLauncher, interceptorReady);
			UpdateShoulderLauncher(rightShoulderLauncher, hasStt);
			lastFrame = input.Sample();
			if (!motion.EngineAvailable)
			{
				lastFrame.MoveLocal = Vector2.zero;
				lastFrame.RiseHeld = false;
				lastFrame.DescendHeld = false;
				lastFrame.RiseEdge = false;
				lastFrame.AssaultBoostEdge = false;
				lastFrame.QuickBoostEdge = false;
				lastFrame.BrakeHeld = false;
				lastFrame.InertialThrustLockHeld = false;
			}
			quickBoostActivationConsumed = false;
			if (lastFrame.ThrustVisualizationToggleEdge)
			{
				thrustVisualizer?.ToggleVisible();
				Debug.Log("[MechPlayerRig] Thrust visualization: " + ((thrustVisualizer != null && thrustVisualizer.Visible) ? "ON" : "OFF") + ".", this);
			}
			if (lastFrame.ThermalVisionCycleEdge)
			{
				thermalVision?.CycleMode();
			}
			if (thermalSignature != null)
			{
				if (lastFrame.PrimaryFireEdge || (lastFrame.LeftShoulderFireEdge && !IsShaoWeiEquipped(MechPartSlot.LeftBackWeapon)))
				{
					thermalSignature.NotifyWeaponFired(leftHand: true);
				}
				if (lastFrame.SecondaryFireEdge || (lastFrame.RightShoulderFireEdge && !IsShaoWeiEquipped(MechPartSlot.RightBackWeapon)))
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
			if (lastFrame.AssaultBoostEdge && motion.ToggleAssaultBoost())
			{
				BeginAssaultBoostCruise();
			}
			if (lastFrame.QuickBoostEdge)
			{
				bool groundedAtTrigger = motion.Grounded;
				quickBoostActivationConsumed = true;
				if (motion.BeginQuickBoost(lastFrame.MoveLocal))
				{
					string actionId = (groundedAtTrigger ? SelectGroundQuickBoostAction(lastFrame.MoveLocal) : SelectAirQuickBoostAction(lastFrame.MoveLocal));
					int state = (groundedAtTrigger ? quickBoostState : airQuickBoostState);
					if (!BeginFullBodyOverride(FullBodyOverrideKind.QuickBoost, state, actionId))
					{
						graphRunner.TryEnterState(state);
					}
				}
			}
			orbitSynchronizer?.Tick(in lastFrame, dt);
			MechPlayerInputFrame motionFrame = lastFrame;
			if (orbitSynchronizer != null && orbitSynchronizer.Active && orbitSynchronizer.RequestsAutomaticDeparture)
			{
				motionFrame.RiseHeld = true;
			}
			motion.Tick(in motionFrame, dt);
			bool grounded = motion.Grounded;
			uint ccf = ((graphRunner.StateElapsedSeconds >= cancelFlagUnlockSeconds) ? 31u : 0u);
			MechPlayerInputFrame graphFrame = lastFrame;
			if (!motion.CanQuickBoost)
			{
				graphFrame.QuickBoostEdge = false;
			}
			MechGraphContext ctx = new MechGraphContext
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
				BodyStateType = 0
			};
			if ((motion.AssaultBoostActive || !grounded) && fullBodyOverrideKind == FullBodyOverrideKind.None)
			{
				graphRunner.TickStateOnly(in ctx, dt);
			}
			else
			{
				graphRunner.Tick(in ctx, dt);
			}
			UpdateFullBodyOverride(grounded, dt);
			int current = graphRunner.CurrentStateIndex;
			bool currentIsQuickBoost = current >= 0 && current < stateIsQuickBoost.Length && stateIsQuickBoost[current];
			if (fullBodyOverrideKind == FullBodyOverrideKind.None)
			{
				if (motion.AssaultBoostActive)
				{
					BeginAssaultBoostCruise();
				}
				else if (currentIsQuickBoost && graphRunner.StateElapsedSeconds >= quickBoostStateMaxSeconds)
				{
					if (grounded)
					{
						graphRunner.TryEnterState((lastFrame.MoveLocal.sqrMagnitude > 0.0001f) ? groundBoostState : idleState);
					}
					else
					{
						EnterSpaceFlightCruise();
					}
				}
				else if (!grounded && !currentIsQuickBoost)
				{
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
			}
			current = graphRunner.CurrentStateIndex;
			if (weaponLayer != null)
			{
				bool suppress = suppressUpperDuringQuickBoost && (fullBodyOverrideKind == FullBodyOverrideKind.QuickBoost || (current >= 0 && current < stateIsQuickBoost.Length && stateIsQuickBoost[current]));
				weaponLayer.Tick(in lastFrame, suppress, dt);
			}
		}

		private void ResetNavigationReference()
		{
			if (!(cam == null) && !(motion == null))
			{
				if (viewCamera == null)
				{
					viewCamera = cam.GetComponent<Camera>();
				}
				if (radar == null)
				{
					radar = GetComponent<MechRadarController>();
				}
				if (radar != null && radar.TryGetTrackAtViewport(viewCamera, cam.AimViewportPoint, out var selectedTrack))
				{
					motion.ResetReferencePlaneToward(radar.PredictTrackPosition(in selectedTrack));
					orbitSynchronizer?.Configure(motion, radar);
					orbitSynchronizer?.Begin(in selectedTrack);
				}
				else
				{
					orbitSynchronizer?.Cancel();
					motion.ResetReferencePlaneToRotation(cam.AimRotation);
				}
				cam.ResetToReferenceHorizon();
			}
		}

		private void LateUpdate()
		{
			if (cam != null)
			{
				cam.Follow(Time.deltaTime);
			}
			dustField?.Tick();
		}

		private bool BeginFullBodyOverride(FullBodyOverrideKind kind, int stateIndex, string actionId)
		{
			if (actionPlayer == null || !actionPlayer.HasAction(actionId))
			{
				Debug.LogWarning($"[MechPlayerRig] Transient action is unavailable: {kind} {actionId}.", this);
				return false;
			}
			graphRunner.SetActionPlaybackSuppressed(suppressed: true);
			if (!graphRunner.TryEnterStateWithActionOverride(stateIndex, actionId) && !actionPlayer.PlayFullBody(actionId))
			{
				graphRunner.SetActionPlaybackSuppressed(suppressed: false);
				return false;
			}
			fullBodyOverrideKind = kind;
			fullBodyOverrideActionId = actionId;
			fullBodyOverrideElapsed = 0f;
			Debug.Log($"[MechPlayerRig] Transient action started: {kind} {actionId}, " + $"state={stateIndex}, grounded={motion.Grounded}.", this);
			return true;
		}

		private void UpdateFullBodyOverride(bool grounded, float deltaTime)
		{
			if (fullBodyOverrideKind == FullBodyOverrideKind.None)
			{
				return;
			}
			fullBodyOverrideElapsed += deltaTime;
			if (fullBodyOverrideKind switch
			{
				FullBodyOverrideKind.QuickBoost => (actionPlayer == null || actionPlayer.IsFullBodyActionFinished(fullBodyOverrideActionId) || fullBodyOverrideElapsed >= quickBoostStateMaxSeconds) ? 1 : 0, 
				FullBodyOverrideKind.AssaultBoostCruise => (!motion.AssaultBoostActive) ? 1 : 0, 
				_ => 1, 
			} == 0)
			{
				return;
			}
			if (fullBodyOverrideKind == FullBodyOverrideKind.QuickBoost && motion.AssaultBoostActive)
			{
				BeginAssaultBoostCruise();
				return;
			}
			fullBodyOverrideKind = FullBodyOverrideKind.None;
			fullBodyOverrideActionId = string.Empty;
			fullBodyOverrideElapsed = 0f;
			graphRunner.SetActionPlaybackSuppressed(suppressed: false);
			if (grounded)
			{
				graphRunner.TryEnterState((lastFrame.MoveLocal.sqrMagnitude > 0.0001f) ? groundBoostState : idleState);
			}
			else
			{
				EnterSpaceFlightCruise();
			}
		}

		private void EnterSpaceFlightCruise()
		{
			if (!(graphRunner == null) && flightState >= 0 && (!(actionPlayer != null) || graphRunner.CurrentStateIndex != flightState || !(actionPlayer.ActiveFullBodyActionId == "a000_100002")) && !graphRunner.TryEnterStateWithActionOverride(flightState, "a000_100002"))
			{
				graphRunner.TryEnterState(flightState);
			}
		}

		private void BeginAssaultBoostCruise()
		{
			graphRunner.SetActionPlaybackSuppressed(suppressed: true);
			if (!graphRunner.TryEnterStateWithActionOverride(flightState, "a000_120200") && (actionPlayer == null || !actionPlayer.PlayFullBody("a000_120200")))
			{
				Debug.LogWarning("[MechPlayerRig] AB cruise action is unavailable: a000_120200.", this);
				motion.SetAssaultBoostActive(active: false);
				graphRunner.SetActionPlaybackSuppressed(suppressed: false);
				fullBodyOverrideKind = FullBodyOverrideKind.None;
				fullBodyOverrideActionId = string.Empty;
				fullBodyOverrideElapsed = 0f;
			}
			else
			{
				fullBodyOverrideKind = FullBodyOverrideKind.AssaultBoostCruise;
				fullBodyOverrideActionId = "a000_120200";
				fullBodyOverrideElapsed = 0f;
				Debug.Log("[MechPlayerRig] AB cruise started: a000_120200.", this);
			}
		}

		private static string SelectGroundQuickBoostAction(Vector2 moveLocal)
		{
			if (Mathf.Abs(moveLocal.x) > Mathf.Abs(moveLocal.y))
			{
				if (!(moveLocal.x < 0f))
				{
					return "a000_004243";
				}
				return "a000_004242";
			}
			if (!(moveLocal.y < 0f))
			{
				return "a000_004240";
			}
			return "a000_004241";
		}

		private static string SelectAirQuickBoostAction(Vector2 moveLocal)
		{
			if (Mathf.Abs(moveLocal.x) > Mathf.Abs(moveLocal.y))
			{
				if (!(moveLocal.x < 0f))
				{
					return "a000_122413";
				}
				return "a000_122412";
			}
			if (!(moveLocal.y < 0f))
			{
				return "a000_122410";
			}
			return "a000_122411";
		}

		public bool ConsumeShoulderMissileLaunch(MechPartSlot slot)
		{
			ShoulderLauncherState launcher = slot switch
			{
				MechPartSlot.RightBackWeapon => rightShoulderLauncher, 
				MechPartSlot.LeftBackWeapon => leftShoulderLauncher, 
				_ => null, 
			};
			if (launcher == null || !launcher.LaunchAuthorizedThisFrame)
			{
				return false;
			}
			if (launcher.IsAutomaticPointDefense)
			{
				launcher.LaunchAuthorizedThisFrame = false;
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
			MechEquipmentAnimationPlayer equipmentPlayer = ResolveEquipmentPlayer(slot);
			if (equipmentPlayer == null)
			{
				return null;
			}
			if (IsShaoWeiEquipped(slot))
			{
				ApplyShoulderRestPose(equipmentPlayer);
				return new ShoulderLauncherState
				{
					Player = equipmentPlayer,
					Slot = slot,
					Phase = ShoulderLauncherPhase.Stowed,
					IsAutomaticPointDefense = true
				};
			}
			bool hasCanonicalSequence = true;
			for (int i = 0; i < ShoulderFireSequenceActionIds.Length; i++)
			{
				if (!equipmentPlayer.HasAction(ShoulderFireSequenceActionIds[i]))
				{
					hasCanonicalSequence = false;
					break;
				}
			}
			string fallbackActionId = (hasCanonicalSequence ? string.Empty : FirstAvailableAction(equipmentPlayer, "a000_800210", "a000_000100", "a000_800410"));
			ApplyShoulderRestPose(equipmentPlayer);
			return new ShoulderLauncherState
			{
				Player = equipmentPlayer,
				Slot = slot,
				Phase = ShoulderLauncherPhase.Stowed,
				HasCanonicalCycle = hasCanonicalSequence,
				FallbackFireActionId = fallbackActionId
			};
		}

		private bool HasSttTrack()
		{
			if (radar == null)
			{
				radar = GetComponent<MechRadarController>();
			}
			if (radar != null)
			{
				return radar.HasStt;
			}
			return false;
		}

		private bool HasRwrLockWarning()
		{
			if (radar == null)
			{
				radar = GetComponent<MechRadarController>();
			}
			if (radar != null && radar.TryGetHighestThreat(out var threat))
			{
				return (int)threat.Level >= 3;
			}
			return false;
		}

		private void RequestShoulderFire(ShoulderLauncherState launcher)
		{
			if (launcher == null || launcher.Player == null)
			{
				Debug.LogWarning("[MechPlayerRig] No shoulder launcher animation player found.", this);
			}
			else
			{
				if (launcher.IsAutomaticPointDefense)
				{
					return;
				}
				switch (launcher.Phase)
				{
				case ShoulderLauncherPhase.Stowed:
					if (!launcher.HasCanonicalCycle)
					{
						StartFallbackShoulderFire(launcher);
						break;
					}
					launcher.FireRequested = true;
					PlayShoulderAction(launcher, "a000_800110", ShoulderLauncherPhase.Deploying);
					break;
				case ShoulderLauncherPhase.Deploying:
					launcher.FireRequested = true;
					break;
				case ShoulderLauncherPhase.Ready:
					PlayShoulderAction(launcher, "a000_800120", ShoulderLauncherPhase.Opening);
					break;
				}
			}
		}

		private void UpdateShoulderLauncher(ShoulderLauncherState launcher, bool hasStt)
		{
			if (launcher == null || launcher.Player == null)
			{
				return;
			}
			if (launcher.IsAutomaticPointDefense)
			{
				launcher.LaunchAuthorizedThisFrame = false;
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
					PlayShoulderAction(launcher, "a000_800110", ShoulderLauncherPhase.Deploying);
				}
				break;
			case ShoulderLauncherPhase.Deploying:
				launcher.Phase = ShoulderLauncherPhase.Ready;
				if (launcher.FireRequested)
				{
					launcher.FireRequested = false;
					PlayShoulderAction(launcher, "a000_800120", ShoulderLauncherPhase.Opening);
				}
				else if (!hasStt)
				{
					PlayShoulderAction(launcher, "a000_800310", ShoulderLauncherPhase.Stowing);
				}
				break;
			case ShoulderLauncherPhase.Ready:
				if (!hasStt)
				{
					PlayShoulderAction(launcher, "a000_800310", ShoulderLauncherPhase.Stowing);
				}
				break;
			case ShoulderLauncherPhase.Opening:
				launcher.LaunchAuthorizedThisFrame = true;
				PlayShoulderAction(launcher, "a000_800210", ShoulderLauncherPhase.Firing);
				break;
			case ShoulderLauncherPhase.Firing:
				PlayShoulderAction(launcher, "a000_800320", ShoulderLauncherPhase.Closing);
				break;
			case ShoulderLauncherPhase.Closing:
				if (hasStt)
				{
					launcher.Phase = ShoulderLauncherPhase.Ready;
				}
				else
				{
					PlayShoulderAction(launcher, "a000_800310", ShoulderLauncherPhase.Stowing);
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
			}
			else if (PlayShoulderAction(launcher, launcher.FallbackFireActionId, ShoulderLauncherPhase.FallbackFiring))
			{
				launcher.LaunchAuthorizedThisFrame = true;
			}
		}

		private bool PlayShoulderAction(ShoulderLauncherState launcher, string actionId, ShoulderLauncherPhase nextPhase)
		{
			if (launcher.Player.PlayActionPreservingPose(actionId))
			{
				launcher.Phase = nextPhase;
				return true;
			}
			Debug.LogWarning($"[MechPlayerRig] Failed to play {launcher.Slot} action {actionId}.", this);
			ApplyShoulderRestPose(launcher.Player);
			launcher.Phase = ShoulderLauncherPhase.Stowed;
			launcher.FireRequested = false;
			return false;
		}

		private static void ApplyShoulderRestPose(MechEquipmentAnimationPlayer player)
		{
			if (!(player == null))
			{
				player.StopPlayback();
				player.SampleActionAtEnd("a000_800320");
				player.SampleActionAtEnd("a000_800310");
				if (player.HasAction("a000_800000"))
				{
					player.SampleActionAtTime("a000_800000", 0f);
				}
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
			IReadOnlyList<MechAssemblyRecord> parts = runtimeState.AssembledParts;
			for (int i = 0; i < parts.Count; i++)
			{
				MechAssemblyRecord record = parts[i];
				if (record.slot == slot && record.instance != null)
				{
					return record.instance.GetComponent<MechEquipmentAnimationPlayer>();
				}
			}
			return null;
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
				if (record?.part == null || record.slot != slot)
				{
					continue;
				}
				return MechWeaponRoleResolver.Resolve(record.part) == MechWeaponRole.PointDefenseCannon;
			}
			return false;
		}

		private static string FirstAvailableAction(MechEquipmentAnimationPlayer equipmentPlayer, params string[] actionIds)
		{
			for (int i = 0; i < actionIds.Length; i++)
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
			if (MechAssemblyMenu.IsAnyMenuVisible)
			{
				return;
			}
			int depth = GUI.depth;
			GUI.depth = -100;
			DrawFlightInstruments();
			GUI.depth = depth;
			if (GetComponent<MechRadarHudPresenter>() != null || !showDebugOverlay || graphRunner == null)
			{
				return;
			}
			StringBuilder sb = new StringBuilder(512);
			sb.AppendLine($"状态: [{graphRunner.CurrentStateIndex}] {graphRunner.CurrentStateName}  t={graphRunner.StateElapsedSeconds:F2}s");
			if (actionPlayer != null)
			{
				sb.AppendLine("全身层: " + actionPlayer.ActiveFullBodyActionId);
				sb.AppendLine($"上半身层: {actionPlayer.ActiveUpperBodyActionId} ({weaponLayer?.Phase})");
				sb.AppendLine($"动作覆盖: {fullBodyOverrideKind} {fullBodyOverrideActionId}");
			}
			if (motion != null)
			{
				sb.AppendLine($"速度: {motion.Velocity.magnitude:F1} m/s {motion.Velocity}");
				sb.AppendLine($"Yaw: {motion.YawDegrees:F1}°  接地: {motion.Grounded}  AB: {motion.AssaultBoostActive}");
				sb.AppendLine($"主推输出: {motion.MainEngineOutput:P0}  技术安全速度: {motion.TechnicalSafetySpeed:F0} m/s");
				sb.AppendLine($"机动能量: {motion.CurrentManeuverEnergy:F1}/" + $"{motion.MaximumManeuverEnergy:F1}  " + $"耗尽: {motion.ManeuverEnergyDepleted}");
				sb.AppendLine($"肩部上推: {motion.CurrentShoulderLiftForceNewtons.magnitude / 1000f:F0} kN " + $"{motion.CurrentShoulderLiftForceNewtons.normalized}  " + $"腿部上推: {motion.CurrentLegLiftForceNewtons.magnitude / 1000f:F0} kN " + $"{motion.CurrentLegLiftForceNewtons.normalized}");
				sb.AppendLine($"向下配平: {motion.CurrentDescentForceNewtons.magnitude / 1000f:F0} kN " + $"{motion.CurrentDescentForceNewtons.normalized}");
				sb.AppendLine("矢量推力: " + (motion.VectorThrustEnabled ? "开启" : "关闭") + "  推力显示[2]: " + ((thrustVisualizer != null && thrustVisualizer.Visible) ? "开启" : "关闭") + "  惯性锁定[F]: " + (motion.InertialThrustLockActive ? "开启" : "关闭"));
				if (orbitSynchronizer != null && orbitSynchronizer.Active)
				{
					sb.AppendLine($"轨道同步: T{orbitSynchronizer.SynchronizedTrackId.Value.Index:00}  " + $"纵向飞控: {orbitSynchronizer.AutomaticLongitudinalCommand:P0}  " + $"纵向偏置: {orbitSynchronizer.LongitudinalVelocityBias:F1} m/s");
				}
			}
			sb.AppendLine($"Input: {lastFrame.MoveLocal}  Position: {base.transform.position}");
			Span<(int, int, float)> history = stackalloc(int, int, float)[6];
			int n = graphRunner.CopyHistory(history);
			for (int i = 0; i < n; i++)
			{
				(int, int, float) tuple = history[i];
				int f = tuple.Item1;
				int t = tuple.Item2;
				float time = tuple.Item3;
				string fromName = ((f >= 0 && graphRunner.Graph != null) ? graphRunner.Graph.states[f].officialName : "-");
				string toName = ((graphRunner.Graph != null) ? graphRunner.Graph.states[t].officialName : "?");
				sb.AppendLine($"  {time:F1}s  {fromName} → {toName}");
			}
			GUI.Label(new Rect(10f, 10f, 640f, 320f), sb.ToString());
		}

		private void DrawFlightInstruments()
		{
			if (showFlightInstruments && !(motion == null))
			{
				EnsureInstrumentStyles();
				float readoutY = Mathf.Clamp((float)Screen.height * 0.5f - 39f, 170f, (float)Screen.height - 78f - 130f);
				float sideInset = Mathf.Clamp((float)Screen.width * 0.16f, 135f, 410f);
				Rect leftReadout = new Rect(sideInset - 90f, readoutY, 180f, 78f);
				Rect rightReadout = new Rect((float)Screen.width - sideInset - 90f, readoutY, 180f, 78f);
				Color color = GUI.color;
				SetGuiAlpha(GetStartupHudAlpha(MechStartupHudModule.FlightReference));
				DrawFlightReadout(leftReadout, "速度", FormatInstrumentValue(motion.Velocity.magnitude), "m/s", pointsRight: true);
				DrawFlightReadout(rightReadout, "高度", FormatInstrumentValue(motion.Altitude), "m", pointsRight: false);
				GUI.color = color;
				DrawEngineStartupStatus();
				SetGuiAlpha(GetStartupHudAlpha(MechStartupHudModule.VitalStatus));
				DrawHealthGauge();
				DrawManeuverEnergyGauge();
				GUI.color = color;
			}
		}

		private void DrawEngineStartupStatus()
		{
			if (!(engineController == null) && engineController.StartupActive)
			{
				float width = Mathf.Clamp((float)Screen.width * 0.28f, 300f, 440f);
				Rect rect = new Rect((float)Screen.width * 0.5f - width * 0.5f, (float)Screen.height * 0.52f, width, 8f);
				float readyBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.72f, 1f, engineController.StartupProgress));
				Color color = Color.Lerp(new Color(1f, 0.62f, 0.12f, 0.98f), new Color(0.38f, 1f, 0.56f, 0.98f), readyBlend);
				DrawInstrumentRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0.015f, 0.035f, 0.018f, 0.9f));
				DrawInstrumentRect(new Rect(rect.x, rect.y, rect.width * engineController.StartupProgress, rect.height), color);
				instrumentEnergyStyle.normal.textColor = color;
				instrumentEnergyStyle.alignment = TextAnchor.MiddleCenter;
				GUI.Label(new Rect(rect.x, rect.y - 51f, rect.width, 22f), "SYSTEM STARTUP / SELF TEST", instrumentEnergyStyle);
				GUI.Label(new Rect(rect.x, rect.y - 29f, rect.width, 22f), engineController.StartupStatusText + "  " + $"{engineController.StartupSecondsRemaining:0.0}s", instrumentEnergyStyle);
				instrumentEnergyStyle.alignment = TextAnchor.MiddleRight;
				GUI.Label(new Rect(rect.x, rect.y + 11f, rect.width, 20f), $"{engineController.StartupProgress * 100f:0}%", instrumentEnergyStyle);
			}
		}

		private float GetStartupHudAlpha(MechStartupHudModule module)
		{
			if (!(engineController != null))
			{
				return 1f;
			}
			return engineController.GetHudStartupAlpha(module);
		}

		private static void SetGuiAlpha(float alpha)
		{
			Color color = GUI.color;
			color.a = Mathf.Clamp01(alpha);
			GUI.color = color;
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
			float width = Mathf.Clamp((float)Screen.width * 0.34f, 300f, 520f);
			Rect rect = new Rect((float)Screen.width * 0.5f - width * 0.5f, (float)Screen.height - 86f, width, 14f);
			float normalized = combatTarget.HealthFraction;
			Color color = ((normalized <= 0.2f) ? new Color(1f, 0.2f, 0.14f, 0.98f) : ((normalized <= 0.5f) ? new Color(1f, 0.72f, 0.18f, 0.98f) : new Color(0.34f, 0.94f, 0.82f, 0.98f)));
			DrawInstrumentRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0.01f, 0.05f, 0.045f, 0.9f));
			DrawInstrumentRect(new Rect(rect.x, rect.y, rect.width * normalized, rect.height), color);
			for (int i = 1; i < 10; i++)
			{
				DrawInstrumentRect(new Rect(rect.x + rect.width * (float)i / 10f - 0.5f, rect.y, 1f, rect.height), new Color(0.01f, 0.08f, 0.065f, 0.72f));
			}
			instrumentEnergyStyle.normal.textColor = color;
			instrumentEnergyStyle.alignment = TextAnchor.MiddleLeft;
			GUI.Label(new Rect(rect.x, rect.y - 24f, 90f, 20f), combatTarget.IsAlive ? "AP" : "AP LOST", instrumentEnergyStyle);
			instrumentEnergyStyle.alignment = TextAnchor.MiddleRight;
			GUI.Label(new Rect(rect.x + rect.width - 160f, rect.y - 24f, 160f, 20f), $"{combatTarget.Health:0} / " + $"{combatTarget.MaxHealth:0}", instrumentEnergyStyle);
		}

		private void DrawManeuverEnergyGauge()
		{
			float width = Mathf.Clamp((float)Screen.width * 0.34f, 300f, 520f);
			Rect rect = new Rect((float)Screen.width * 0.5f - width * 0.5f, (float)Screen.height - 126f, width, 12f);
			float normalized = Mathf.Clamp01(motion.ManeuverEnergyNormalized);
			Color color = (motion.ManeuverEnergyDepleted ? new Color(1f, 0.2f, 0.14f, 0.98f) : ((normalized <= 0.3f) ? new Color(1f, 0.72f, 0.18f, 0.98f) : new Color(0.38f, 1f, 0.56f, 0.98f)));
			DrawInstrumentRect(new Rect(rect.x - 2f, rect.y - 2f, rect.width + 4f, rect.height + 4f), new Color(0.01f, 0.05f, 0.035f, 0.86f));
			DrawInstrumentRect(new Rect(rect.x, rect.y, rect.width * normalized, rect.height), color);
			for (int i = 1; i < 10; i++)
			{
				DrawInstrumentRect(new Rect(rect.x + rect.width * (float)i / 10f - 0.5f, rect.y, 1f, rect.height), new Color(0.01f, 0.08f, 0.05f, 0.72f));
			}
			instrumentEnergyStyle.normal.textColor = color;
			instrumentEnergyStyle.alignment = TextAnchor.MiddleLeft;
			GUI.Label(new Rect(rect.x, rect.y - 24f, 90f, 20f), motion.ManeuverEnergyDepleted ? "EN EMPTY" : "EN", instrumentEnergyStyle);
			instrumentEnergyStyle.alignment = TextAnchor.MiddleRight;
			GUI.Label(new Rect(rect.x + rect.width - 130f, rect.y - 24f, 130f, 20f), $"{motion.CurrentManeuverEnergy:0} / {motion.MaximumManeuverEnergy:0}", instrumentEnergyStyle);
		}

		private void DrawFlightReadout(Rect rect, string caption, string value, string unit, bool pointsRight)
		{
			GUI.Label(new Rect(rect.x, rect.y, rect.width, 24f), caption, instrumentCaptionStyle);
			GUI.Label(new Rect(rect.x, rect.y + 19f, rect.width - 30f, 44f), value, instrumentValueStyle);
			GUI.Label(new Rect(rect.x + rect.width - 42f, rect.y + 39f, 42f, 22f), unit, instrumentUnitStyle);
			float lineY = rect.y + 64f;
			float x = (pointsRight ? (rect.x + rect.width - 25f) : (rect.x + 25f));
			float outerX = (pointsRight ? (rect.x + rect.width + 24f) : (rect.x - 24f));
			DrawInstrumentLine(new Vector2(x, lineY), new Vector2(outerX, lineY), 2f);
			DrawInstrumentLine(new Vector2(outerX, lineY - 5f), new Vector2(outerX, lineY + 5f), 2f);
		}

		private static string FormatInstrumentValue(float value)
		{
			if (!(Mathf.Abs(value) >= 1000f))
			{
				return value.ToString("0.0");
			}
			return value.ToString("N0");
		}

		private static void DrawInstrumentLine(Vector2 from, Vector2 to, float thickness)
		{
			Color previousColor = GUI.color;
			Vector2 direction = to - from;
			float angle = Mathf.Atan2(direction.y, direction.x) * 57.29578f;
			GUI.color = MultiplyGuiTint(new Color(0.38f, 1f, 0.56f, 0.92f), previousColor);
			Matrix4x4 matrix = GUI.matrix;
			GUIUtility.RotateAroundPivot(angle, from);
			GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, direction.magnitude, thickness), Texture2D.whiteTexture);
			GUI.matrix = matrix;
			GUI.color = previousColor;
		}

		private static void DrawInstrumentRect(Rect rect, Color color)
		{
			Color previousColor = GUI.color;
			GUI.color = MultiplyGuiTint(color, previousColor);
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			GUI.color = previousColor;
		}

		private static Color MultiplyGuiTint(Color color, Color tint)
		{
			return new Color(color.r * tint.r, color.g * tint.g, color.b * tint.b, color.a * tint.a);
		}

		private void EnsureInstrumentStyles()
		{
			if (instrumentCaptionStyle == null)
			{
				instrumentCaptionStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 18,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleCenter
				};
				instrumentCaptionStyle.normal.textColor = new Color(0.28f, 0.9f, 0.46f, 0.94f);
				instrumentValueStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 30,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleRight
				};
				instrumentValueStyle.normal.textColor = new Color(0.55f, 1f, 0.68f, 1f);
				instrumentUnitStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 13,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.LowerLeft
				};
				instrumentUnitStyle.normal.textColor = new Color(0.28f, 0.9f, 0.46f, 0.88f);
				instrumentEnergyStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 14,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleLeft
				};
				instrumentEnergyStyle.normal.textColor = new Color(0.38f, 1f, 0.56f, 0.96f);
			}
		}
	}
}
