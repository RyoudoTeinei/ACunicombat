using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ACunicombat.Mechs
{
	[DefaultExecutionOrder(400)]
	[DisallowMultipleComponent]
	public sealed class MechRadarHudPresenter : MonoBehaviour
	{
		private const float TacticalPlotWidth = 420f;

		private const float TacticalPlotHeight = 420f;

		private const float RadarStatusWidth = 560f;

		private const float RadarStatusHeight = 440f;

		private const float TrackMarkerSize = 54f;

		private const int CircleSegments = 256;

		private const int OperationRingTextureSize = 2048;

		private const float OperationRingTextureRadiusFraction = 0.46f;

		private const int SoftLineTextureHeight = 7;

		private const int TacticalBackgroundTextureSize = 1024;

		private const double MissileMarkerEvidenceHoldSeconds = 0.6;

		[SerializeField]
		private MechRadarController radar;

		[SerializeField]
		private Camera viewCamera;

		[SerializeField]
		private Vector2 aimViewportPoint = new Vector2(0.5f, 0.56f);

		[Header("Rebindable radar controls")]
		[SerializeField]
		private Key operationModeKey = Key.Tab;

		[SerializeField]
		private Key cycleTargetKey = Key.T;

		[Header("Radar operator audio")]
		[SerializeField]
		[Range(0f, 1f)]
		private float radarAudioMasterVolume = 0.7f;

		[Tooltip("Tab 雷达操作界面开启且未进入 STT 时循环播放。")]
		[SerializeField]
		private AudioClip operationScanLoopClip;

		[SerializeField]
		[Range(0f, 1f)]
		private float operationScanLoopVolume = 0.0578f;

		[Tooltip("Tab 雷达操作界面开启且进入 STT 时替换扫描底噪循环播放。")]
		[SerializeField]
		private AudioClip sttLockLoopClip;

		[SerializeField]
		[Range(0f, 1f)]
		private float sttLockLoopVolume = 0.10115f;

		[Header("Friendly missile cue")]
		[SerializeField]
		[Min(3f)]
		private float friendlyMissileMarkerRadius = 7f;

		[Header("Direct hit confirmation")]
		[SerializeField]
		[Min(0.1f)]
		private float hitConfirmationDurationSeconds = 0.56f;

		[SerializeField]
		[Min(0.1f)]
		private float missileHitConfirmationDurationSeconds = 1.05f;

		[SerializeField]
		[Min(0.1f)]
		private float targetDestroyedConfirmationDurationSeconds = 1.35f;

		[SerializeField]
		private AudioClip missileHitConfirmationClip;

		[SerializeField]
		private AudioClip targetDestroyedConfirmationClip;

		[SerializeField]
		[Range(0f, 1f)]
		private float missileHitConfirmationVolume = 0.65f;

		[SerializeField]
		[Range(0f, 1f)]
		private float targetDestroyedConfirmationVolume = 0.55f;

		[Header("Track readout")]
		[SerializeField]
		[Min(0.1f)]
		private float trackReadoutRefreshSeconds = 0.25f;

		[SerializeField]
		[Range(0.05f, 1f)]
		private float trackReadoutSmoothing = 0.35f;

		[Header("Track velocity vector")]
		[SerializeField]
		[Min(0.01f)]
		private float velocityVectorPixelsPerMeterPerSecond = 0.18f;

		[SerializeField]
		[Min(1f)]
		private float minimumVelocityVectorLength = 8f;

		[SerializeField]
		[Min(1f)]
		private float maximumVelocityVectorLength = 110f;

		[SerializeField]
		[Min(0.5f)]
		private float velocityVectorThickness = 2f;

        [Header("Enemy direction indicator")]
        [SerializeField] private bool showEnemyDirectionIndicator = true;
        [SerializeField, Range(18f, 48f)] private float enemyEdgeIndicatorSize = 36f;
        private readonly Dictionary<TrackId, EnemyDirectionState> directionStates = new();
        private readonly EnemyDirectionState[] preparedDirectionStates = new EnemyDirectionState[32];
        private readonly List<TrackId> staleDirectionIds = new();
        private readonly List<EnemyDirectionState> edgeLayoutScratch = new();
        private int directionPreparedFrame = -1;
        private int directionPreparationSerial;
        private Rect directionViewport;
        private Material directionArrowMaterial;
        private readonly DirectionArrowEdge[] directionArrowEdges = new DirectionArrowEdge[8];
        private readonly Vector3[] directionArrowVertices = new Vector3[5];
        private int directionArrowEdgeCount;
        private struct DirectionArrowEdge
        {
            public Vector3 A, B;
        }
        // Per-track state: each normal frame is replaced by its own edge cue.
        private sealed class EnemyDirectionState
        {
            public TrackId TrackId;
            public Vector2 Direction = Vector2.right;
            public bool IsRear;
            public bool BoxVisible;
            public Quaternion Orientation = Quaternion.identity;
            public Vector2 Anchor;
            public Rect InfoRect;
            public int Edge; // left, right, top, bottom
            public int TrackIndex;
            public int SeenSerial;
            public bool FullDetails = true;
            public float RenderSize;
            private bool initialized;
            private bool rearPoleHeld;
            private float angle;
            private float angularVelocity;

            public void Reset()
            {
                TrackId = default;
                Direction = Vector2.right;
                IsRear = BoxVisible = initialized = rearPoleHeld = false;
                angle = angularVelocity = 0f;
                Orientation = Quaternion.identity;
            }

            public void Update(TrackId id, Vector3 cameraDirection, Vector2 screenDirection,
                Rect frame, bool frameEligible, Rect viewport, float deltaTime)
            {
                if (!id.IsValid || !float.IsFinite(cameraDirection.sqrMagnitude) ||
                    cameraDirection.sqrMagnitude < 0.000001f)
                {
                    Reset();
                    return;
                }
                var changed = !initialized || !TrackId.Equals(id);
                if (changed) Reset();
                TrackId = id;
                var local = cameraDirection.normalized;
                if (changed) IsRear = local.z < 0f;
                else if (local.z < -0.04f) IsRear = true;
                else if (local.z > 0.04f) IsRear = false;

                // Visibility follows the target center, not a large inset around
                // the frame. Only six HUD pixels of re-entry hysteresis are used.
                var inset = BoxVisible ? 0f : 6f;
                var center = frame.center;
                BoxVisible = frameEligible && local.z > 0f &&
                    center.x >= viewport.xMin + inset && center.x <= viewport.xMax - inset &&
                    center.y >= viewport.yMin + inset && center.y <= viewport.yMax - inset;
                if (changed)
                {
                    var up = Vector3.ProjectOnPlane(Vector3.up, local);
                    if (up.sqrMagnitude < 0.0001f) up = Vector3.ProjectOnPlane(Vector3.forward, local);
                    Orientation = Quaternion.LookRotation(local, up.normalized);
                }
                else
                {
                    // Transport the 3D orientation continuously. The actual
                    // camera-relative Z component controls foreshortening.
                    var previousForward = Orientation * Vector3.forward;
                    var nextForward = Vector3.RotateTowards(previousForward, local,
                        12f * Mathf.Clamp(deltaTime, 0f, 0.1f), 0f);
                    Orientation = Quaternion.Normalize(
                        Quaternion.FromToRotation(previousForward, nextForward) * Orientation);
                }

                var transverse = new Vector2(local.x, -local.y);
                var poleThreshold = rearPoleHeld ? 0.208f : 0.139f; // 12 / 8 degrees
                rearPoleHeld = IsRear && local.z < 0f && transverse.magnitude < poleThreshold;
                var desired = local.z <= 0f ? new Vector2(transverse.x / Mathf.Max(0.1f, viewport.width / viewport.height), transverse.y) : screenDirection;
                if (rearPoleHeld || desired.sqrMagnitude < 0.000001f ||
                    !float.IsFinite(desired.sqrMagnitude))
                    desired = Direction;
                desired.Normalize();
                var desiredAngle = Mathf.Atan2(desired.y, desired.x) * Mathf.Rad2Deg;
                angle = changed ? desiredAngle : Mathf.SmoothDampAngle(
                    angle, desiredAngle, ref angularVelocity, 0.08f, 720f,
                    Mathf.Clamp(deltaTime, 0f, 0.1f));
                Direction = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad));
                initialized = true;
            }
        }

		private readonly TrackSnapshot[] tracks = new TrackSnapshot[32];

		private readonly MissileTrackSnapshot[] missileTracks = new MissileTrackSnapshot[32];

		private readonly RadarEchoSnapshot[] echoes = new RadarEchoSnapshot[64];

		private int trackCount;

		private int displayTrackCount;

		private int echoCount;

		private readonly uint[] trackReadoutKeys = new uint[32];

		private readonly float[] displayedClosingVelocities = new float[32];

		private readonly float[] nextTrackReadoutUpdateTimes = new float[32];

		private float radarDisplayRangeMeters = 20000f;

		private RadarStatusSnapshot radarStatus;

		private float radarSweepVisualPhase;

		private MechThirdPersonCamera aimController;

		private MechFlightMotionController motion;

		private MechTestWeaponSystem weaponSystem;

		private MechEngineController engineController;

		private AudioSource radarOperatorAudioSource;

		private Texture2D operationRingTexture;

		private Texture2D tacticalBackgroundTexture;

		private static Texture2D softLineTexture;

		private GUIStyle smallStyle;

		private GUIStyle weaponStatusStyle;

		private GUIStyle statusStyle;

		private GUIStyle microStyle;

		private GUIStyle compassStyle;

		private GUIStyle pitchStyle;

		private GUIStyle zoomStyle;

		private GUIStyle hitConfirmationStyle;

		private float hitConfirmationStartedAt = float.NegativeInfinity;

		private float hitConfirmationUntil = float.NegativeInfinity;

		private string hitConfirmationText = "命中";

		private Color hitConfirmationColor;

		private AudioSource combatFeedbackAudioSource;

		private float pitchLadderRollDegrees;

		private static readonly Color HudGreen = new Color(0.3f, 1f, 0.5f, 1f);

		private static readonly Color HudDimGreen = new Color(0.18f, 0.78f, 0.36f, 0.76f);

		private static readonly Color HudBrightGreen = new Color(0.55f, 1f, 0.7f, 1f);

		private static readonly Color HudCyan = new Color(0.12f, 0.88f, 0.94f, 0.96f);

		private static readonly Color HudWhite = new Color(0.82f, 0.92f, 0.86f, 0.95f);

		private static readonly Color HudRed = new Color(1f, 0.18f, 0.12f, 1f);

		private static readonly Color HudAmber = new Color(1f, 0.72f, 0.18f, 0.95f);

		private static readonly Color HudMissileLink = new Color(1f, 0.48f, 0.12f, 0.9f);

		private static readonly Color HudPanel = new Color(0.008f, 0.025f, 0.022f, 0.88f);

		private static readonly Color HudPanelGrid = new Color(0.08f, 0.34f, 0.19f, 0.24f);

		private static readonly Color HudFanFill = new Color(0.04f, 0.25f, 0.12f, 0.36f);

		public bool IsTrackOperationMode
		{
			get
			{
				if (radar != null)
				{
					return radar.TrackOperationMode;
				}
				return false;
			}
		}

		public void Configure(Camera newViewCamera, MechRadarController newRadar, Vector2 newAimViewportPoint)
		{
			viewCamera = newViewCamera;
			aimController = ((viewCamera != null) ? viewCamera.GetComponent<MechThirdPersonCamera>() : null);
			radar = newRadar;
			motion = ((radar != null) ? radar.GetComponent<MechFlightMotionController>() : null);
			weaponSystem = ((radar != null) ? radar.GetComponent<MechTestWeaponSystem>() : GetComponent<MechTestWeaponSystem>());
			engineController = GetComponent<MechEngineController>();
			aimViewportPoint = new Vector2(Mathf.Clamp(newAimViewportPoint.x, 0.05f, 0.95f), Mathf.Clamp(newAimViewportPoint.y, 0.05f, 0.95f));
		}

		private void Awake()
		{
			if (radar == null)
			{
				radar = GetComponent<MechRadarController>();
			}
			motion = ((radar != null) ? radar.GetComponent<MechFlightMotionController>() : GetComponent<MechFlightMotionController>());
			weaponSystem = GetComponent<MechTestWeaponSystem>();
			engineController = GetComponent<MechEngineController>();
			if (viewCamera == null)
			{
				viewCamera = Camera.main;
			}
			if (viewCamera != null)
			{
				aimController = viewCamera.GetComponent<MechThirdPersonCamera>();
			}
			if (operationScanLoopClip == null)
			{
				operationScanLoopClip = Resources.Load<AudioClip>("Audio/Radar/radar_operation_scan_loop");
			}
			if (sttLockLoopClip == null)
			{
				sttLockLoopClip = Resources.Load<AudioClip>("Audio/Radar/radar_stt_lock_loop");
			}
			if (missileHitConfirmationClip == null)
			{
				missileHitConfirmationClip = Resources.Load<AudioClip>(
					"Audio/UI/CombatFeedback/bonus");
			}
			if (targetDestroyedConfirmationClip == null)
			{
				targetDestroyedConfirmationClip = Resources.Load<AudioClip>(
					"Audio/UI/CombatFeedback/enemy_destroyed");
			}
			operationScanLoopClip?.LoadAudioData();
			sttLockLoopClip?.LoadAudioData();
			missileHitConfirmationClip?.LoadAudioData();
			targetDestroyedConfirmationClip?.LoadAudioData();
			radarOperatorAudioSource = base.gameObject.AddComponent<AudioSource>();
			radarOperatorAudioSource.playOnAwake = false;
			radarOperatorAudioSource.loop = true;
			radarOperatorAudioSource.spatialBlend = 0f;
			combatFeedbackAudioSource = base.gameObject.AddComponent<AudioSource>();
			combatFeedbackAudioSource.playOnAwake = false;
			combatFeedbackAudioSource.loop = false;
			combatFeedbackAudioSource.spatialBlend = 0f;
			combatFeedbackAudioSource.priority = 24;
		}

		private void OnEnable()
		{
			CombatTestTarget.DamageApplied += HandleDamageApplied;
		}

		private void OnDisable()
		{
			CombatTestTarget.DamageApplied -= HandleDamageApplied;
			radarOperatorAudioSource?.Stop();
			combatFeedbackAudioSource?.Stop();
			radar?.SetTrackOperationMode(enabled: false);
		}

		private void OnDestroy()
		{
            if (directionArrowMaterial != null)
            {
                if (Application.isPlaying) Destroy(directionArrowMaterial);
                else DestroyImmediate(directionArrowMaterial);
                directionArrowMaterial = null;
            }
			if (operationRingTexture != null)
			{
				UnityEngine.Object.Destroy(operationRingTexture);
				operationRingTexture = null;
			}
			if (tacticalBackgroundTexture != null)
			{
				UnityEngine.Object.Destroy(tacticalBackgroundTexture);
				tacticalBackgroundTexture = null;
			}
		}

		private void Update()
		{
			if (radar == null)
			{
				radar = GetComponent<MechRadarController>();
				if (radar == null)
				{
					return;
				}
			}
			if (viewCamera == null)
			{
				viewCamera = Camera.main;
			}
			Keyboard keyboard = Keyboard.current;
			bool systemsMenuVisible = MechSystemsRadialMenu.IsAnyMenuVisible;
			if (!systemsMenuVisible && keyboard != null)
			{
				if (WasPressed(keyboard, operationModeKey))
				{
					radar.SetTrackOperationMode(!radar.TrackOperationMode);
				}
				if (WasPressed(keyboard, cycleTargetKey))
				{
					radar.CycleCandidate(1);
				}
			}
			if (!systemsMenuVisible &&
				(aimController == null || !aimController.FreeLookViewOffsetActive))
			{
				radar.UpdateDesignation(viewCamera, GetAimViewportPoint(), Time.unscaledDeltaTime);
			}
			trackCount = radar.CopyTracks(tracks);
			displayTrackCount = CountDisplayTracks();
			echoCount = radar.CopyEchoes(echoes);
			radarStatus = radar.GetStatusSnapshot();
			UpdateRadarSweepVisual();
			UpdateRadarOperatorAudio();
		}

		private void UpdateRadarOperatorAudio()
		{
			if (radarOperatorAudioSource == null)
			{
				return;
			}
			AudioClip desiredClip = null;
			float desiredVolume = 0f;
			if (radar.RadarPowerEnabled && radar.TrackOperationMode &&
				IsStartupModuleOnline(MechStartupHudModule.RadarScan))
			{
				if (radar.HasStt)
				{
					desiredClip = sttLockLoopClip;
					desiredVolume =
						sttLockLoopVolume * radarAudioMasterVolume;
				}
				else
				{
					desiredClip = operationScanLoopClip;
					desiredVolume =
						operationScanLoopVolume * radarAudioMasterVolume;
				}
			}
			if (radarOperatorAudioSource.clip != desiredClip)
			{
				radarOperatorAudioSource.Stop();
				radarOperatorAudioSource.clip = desiredClip;
			}
			radarOperatorAudioSource.volume = desiredVolume;
			if (desiredClip != null && !radarOperatorAudioSource.isPlaying)
			{
				radarOperatorAudioSource.Play();
			}
			else if (desiredClip == null && radarOperatorAudioSource.isPlaying)
			{
				radarOperatorAudioSource.Stop();
			}
		}

		private void OnGUI()
		{
			if (MechSolarSystemMap.IsAnyMapVisible ||
				MechAssemblyMenu.IsAnyMenuVisible || radar == null || viewCamera == null)
			{
				return;
			}
			EnsureStyles();
			EnsureSoftLineTexture();
			Matrix4x4 previousGuiMatrix = MechHudLayout.BeginScaledGui();
			Color previousColor = GUI.color;
			SetStartupGuiAlpha(MechStartupHudModule.TrackData);
			DrawMissileAttackLinks();
			GUI.color = previousColor;
			SetStartupGuiAlpha(MechStartupHudModule.TacticalDisplay);
			DrawTacticalPlot(IsStartupModuleOnline(MechStartupHudModule.TrackData));
			GUI.color = previousColor;
			SetStartupGuiAlpha(MechStartupHudModule.RadarStatus);
			DrawRadarStatus(
				radar.RadarPowerEnabled && IsStartupModuleOnline(MechStartupHudModule.RadarScan),
				IsStartupModuleOnline(MechStartupHudModule.TrackData));
			GUI.color = previousColor;
			SetStartupGuiAlpha(MechStartupHudModule.TrackData);
			DrawFriendlyMissileMarkers();
			GUI.color = previousColor;
			if (radar.TrackOperationMode)
			{
				if (IsStartupModuleOnline(MechStartupHudModule.RadarScan))
				{
					SetStartupGuiAlpha(MechStartupHudModule.RadarScan);
					DrawTransientEchoes();
					GUI.color = previousColor;
				}
				SetStartupGuiAlpha(MechStartupHudModule.FlightReference);
				DrawTrackOperationOverlay();
				GUI.color = previousColor;
			}
			else
			{
				SetStartupGuiAlpha(MechStartupHudModule.FlightReference);
				DrawLegacyReticle();
				GUI.color = previousColor;
			}
			SetStartupGuiAlpha(MechStartupHudModule.TrackData);
            if (Event.current.type == EventType.Repaint)
                PrepareEnemyDirectionIndicator(Time.unscaledDeltaTime);
			DrawWorldTrackMarkers();
            DrawEnemyDirectionIndicator();
			GUI.color = previousColor;
			SetStartupGuiAlpha(MechStartupHudModule.Weapons);
			DrawWeaponStatus();
			GUI.color = previousColor;
			SetStartupGuiAlpha(MechStartupHudModule.TrackData);
			DrawIncomingMissileMarkersOnTop();
			GUI.color = previousColor;
			DrawHitConfirmation();
			GUI.color = previousColor;
			GUI.matrix = previousGuiMatrix;
		}

		private void HandleDamageApplied(
			CombatTestTarget target,
			Transform damageSource,
			TestWeaponKind weaponKind,
			float appliedDamage)
		{
			if (target == null || appliedDamage <= 0f || !IsDamageSourceThisMech(damageSource))
			{
				return;
			}

			CombatTestTarget localTarget = GetComponent<CombatTestTarget>();
			if (target == localTarget ||
				(localTarget != null && target.TeamId == localTarget.TeamId))
			{
				return;
			}

			bool targetDestroyed = !target.IsAlive;
			bool missileHit = weaponKind == TestWeaponKind.Missile;
			if (missileHit)
			{
				PlayCombatFeedback(
					missileHitConfirmationClip,
					missileHitConfirmationVolume);
			}
			if (targetDestroyed)
			{
				PlayCombatFeedback(
					targetDestroyedConfirmationClip,
					targetDestroyedConfirmationVolume);
			}

			hitConfirmationStartedAt = Time.unscaledTime;
			float confirmationDuration = targetDestroyed
				? targetDestroyedConfirmationDurationSeconds
				: missileHit
					? missileHitConfirmationDurationSeconds
					: hitConfirmationDurationSeconds;
			hitConfirmationUntil =
				hitConfirmationStartedAt + Mathf.Max(0.1f, confirmationDuration);
			hitConfirmationText = targetDestroyed
				? "目标击毁"
				: missileHit
					? "导弹命中"
					: "命中";
			hitConfirmationColor = targetDestroyed
				? new Color(1f, 0.78f, 0.28f, 1f)
				: HudBrightGreen;
		}

		private void PlayCombatFeedback(AudioClip clip, float volume)
		{
			if (combatFeedbackAudioSource != null && clip != null)
			{
				combatFeedbackAudioSource.PlayOneShot(
					clip,
					Mathf.Clamp01(volume));
			}
		}

		private bool IsDamageSourceThisMech(Transform damageSource)
		{
			if (damageSource == null)
			{
				return false;
			}

			MechPlayerRig localRig = GetComponent<MechPlayerRig>();
			MechPlayerRig sourceRig = damageSource.GetComponentInParent<MechPlayerRig>();
			if (localRig != null && sourceRig != null)
			{
				return localRig == sourceRig;
			}

			return damageSource == transform ||
				damageSource.IsChildOf(transform) ||
				transform.IsChildOf(damageSource);
		}

		private void DrawHitConfirmation()
		{
			float now = Time.unscaledTime;
			if (now >= hitConfirmationUntil)
			{
				return;
			}

			float duration = Mathf.Max(0.1f, hitConfirmationUntil - hitConfirmationStartedAt);
			float age = Mathf.Max(0f, now - hitConfirmationStartedAt);
			float remaining = Mathf.Max(0f, hitConfirmationUntil - now);
			float alpha = Mathf.Min(
				Mathf.Clamp01(age / 0.045f),
				Mathf.Clamp01(remaining / Mathf.Min(0.14f, duration * 0.5f)));
			float pulse = 1f + 0.08f * Mathf.Exp(-age * 18f);
			Vector2 center = new Vector2(MechHudLayout.Width * 0.5f, MechHudLayout.Height * 0.39f);
			Rect labelRect = new Rect(
				center.x - 76f * pulse,
				center.y - 19f * pulse,
				152f * pulse,
				38f * pulse);
			Color baseColor = hitConfirmationColor.a > 0f
				? hitConfirmationColor
				: HudBrightGreen;
			Color color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
			Color markerColor = new Color(baseColor.r, baseColor.g, baseColor.b, alpha * 0.9f);
			DrawLine(
				new Vector2(labelRect.x - 18f, center.y),
				new Vector2(labelRect.x - 5f, center.y),
				markerColor,
				2f);
			DrawLine(
				new Vector2(labelRect.xMax + 5f, center.y),
				new Vector2(labelRect.xMax + 18f, center.y),
				markerColor,
				2f);
			Color previousColor = GUI.color;
			GUI.color = MultiplyGuiTint(color, previousColor);
			GUI.Label(labelRect, hitConfirmationText, hitConfirmationStyle);
			GUI.color = previousColor;
		}

		private bool IsStartupModuleOnline(MechStartupHudModule module)
		{
			ResolveEngineController();
			if (!(engineController == null))
			{
				return engineController.GetHudStartupAlpha(module) >= 0.98f;
			}
			return true;
		}

		private void SetStartupGuiAlpha(MechStartupHudModule module)
		{
			ResolveEngineController();
			Color color = GUI.color;
			color.a = ((engineController != null) ? engineController.GetHudStartupAlpha(module) : 1f);
			GUI.color = color;
		}

		private void ResolveEngineController()
		{
			if (engineController == null)
			{
				engineController = GetComponent<MechEngineController>();
			}
		}

		private void DrawWeaponStatus()
		{
			if (!(weaponSystem == null))
			{
				float centerGaugeWidth = Mathf.Clamp(MechHudLayout.Width * 0.34f, 300f, 520f);
				float availableLeft = MechHudLayout.Width * 0.5f + centerGaugeWidth * 0.5f + 28f;
				float availableWidth = MechHudLayout.Width - 560f - 34f - availableLeft;
				bool num = availableWidth >= 300f;
				float panelWidth = (num ? Mathf.Min(420f, availableWidth) : Mathf.Min(420f, MechHudLayout.Width - 32f));
				float entryWidth = (panelWidth - 20f) * 0.5f;
				float panelX = (num ? (availableLeft + (availableWidth - panelWidth) * 0.5f) : (MechHudLayout.Width - 560f - panelWidth - 42f));
				float firstRowY = (num ? Mathf.Max(12f, MechHudLayout.Height - 148f) : Mathf.Max(12f, MechHudLayout.Height - 440f - 126f));
				Rect leftMissileRect = new Rect(panelX, firstRowY, entryWidth, 40f);
				Rect cannonRect = new Rect(panelX, firstRowY + 44f, entryWidth, 40f);
				Rect rightMissileRect = new Rect(leftMissileRect.xMax + 20f, firstRowY, entryWidth, 40f);
				Rect rifleRect = new Rect(rightMissileRect.x, firstRowY + 44f, entryWidth, 40f);
				DrawWeaponSlotAmmo(leftMissileRect, MechPartSlot.LeftBackWeapon, "L");
				DrawWeaponSlotAmmo(cannonRect, MechPartSlot.LeftHandWeapon, "L");
				DrawWeaponSlotAmmo(rightMissileRect, MechPartSlot.RightBackWeapon, "R");
				DrawWeaponSlotAmmo(rifleRect, MechPartSlot.RightHandWeapon, "R");
			}
		}

		private void DrawWeaponSlotAmmo(Rect bounds, MechPartSlot slot, string sideLabel)
		{
			if (weaponSystem == null || !weaponSystem.TryGetWeaponHudState(slot, out var state))
			{
				return;
			}
			DrawWeaponAmmoEntry(
				bounds,
				$"{sideLabel} {state.Label}",
				state.RoundsRemaining,
				state.MagazineCapacity,
				state.MagazinesRemaining,
				state.Reloading,
				state.ReloadRemainingSeconds,
				state.ReloadDurationSeconds,
				state.ReloadLabel);
		}

		private void DrawWeaponAmmoEntry(Rect bounds, string weaponLabel, int roundsRemaining, int magazineCapacity, int magazinesRemaining, bool reloading, float reloadRemainingSeconds, float reloadDurationSeconds, string reloadLabel)
		{
			int num;
			if (roundsRemaining <= 0)
			{
				num = ((magazinesRemaining <= 0) ? 1 : 0);
				if (num != 0)
				{
					goto IL_0033;
				}
			}
			else
			{
				num = 0;
			}
			if (roundsRemaining >= magazineCapacity)
			{
				goto IL_0033;
			}
			int num2 = ((roundsRemaining <= Mathf.Max(1, Mathf.CeilToInt((float)magazineCapacity * 0.25f))) ? 1 : 0);
			goto IL_0034;
			IL_0034:
			bool lowRounds = (byte)num2 != 0;
			Color statusColor = ((num != 0) ? HudRed : (reloading ? HudAmber : HudGreen));
			Color roundsColor = ((num != 0) ? HudRed : (lowRounds ? HudAmber : HudGreen));
			string roundsText = $"{roundsRemaining:00}";
			string suffix = $" x{magazinesRemaining}";
			if (reloading)
			{
				suffix += $" {reloadLabel}{reloadRemainingSeconds:0.0}s";
			}
			float ammoFraction = ((magazineCapacity > 0) ? Mathf.Clamp01((float)roundsRemaining / (float)magazineCapacity) : 0f);
			float reloadFraction = ((reloading && reloadDurationSeconds > 0f) ? Mathf.Clamp01(1f - reloadRemainingSeconds / reloadDurationSeconds) : ammoFraction);
			float progress = (reloading ? reloadFraction : ammoFraction);
			Color progressColor = ((num != 0) ? HudRed : (reloading ? HudAmber : HudGreen));
			float labelWidth = weaponStatusStyle.CalcSize(new GUIContent(weaponLabel + " ")).x;
			float roundsWidth = Mathf.Max(26f, weaponStatusStyle.CalcSize(new GUIContent(roundsText)).x + 8f);
			float suffixWidth = weaponStatusStyle.CalcSize(new GUIContent(suffix)).x;
			float contentWidth = labelWidth + roundsWidth + suffixWidth;
			float contentX = bounds.center.x - contentWidth * 0.5f;
			float textY = bounds.y;
			weaponStatusStyle.alignment = TextAnchor.MiddleLeft;
			weaponStatusStyle.normal.textColor = statusColor;
			GUI.Label(new Rect(contentX, textY, labelWidth, 28f), weaponLabel, weaponStatusStyle);
			Rect roundsRect = new Rect(contentX + labelWidth, textY + 3f, roundsWidth, 22f);
			Vector2 topLeft = new Vector2(roundsRect.xMin, roundsRect.yMin);
			Vector2 topRight = new Vector2(roundsRect.xMax, roundsRect.yMin);
			Vector2 bottomRight = new Vector2(roundsRect.xMax, roundsRect.yMax);
			Vector2 bottomLeft = new Vector2(roundsRect.xMin, roundsRect.yMax);
			DrawLine(topLeft, topRight, roundsColor, 1.4f);
			DrawLine(topRight, bottomRight, roundsColor, 1.4f);
			DrawLine(bottomRight, bottomLeft, roundsColor, 1.4f);
			DrawLine(bottomLeft, topLeft, roundsColor, 1.4f);
			weaponStatusStyle.alignment = TextAnchor.MiddleCenter;
			weaponStatusStyle.normal.textColor = roundsColor;
			GUI.Label(roundsRect, roundsText, weaponStatusStyle);
			weaponStatusStyle.alignment = TextAnchor.MiddleLeft;
			weaponStatusStyle.normal.textColor = statusColor;
			GUI.Label(new Rect(roundsRect.xMax, textY, suffixWidth, 28f), suffix, weaponStatusStyle);
			float barWidth = Mathf.Min(170f, bounds.width - 36f);
			Vector2 barStart = new Vector2(bounds.center.x - barWidth * 0.5f, bounds.y + 35f);
			DrawLine(end: new Vector2(barStart.x + barWidth, barStart.y), color: new Color(HudDimGreen.r, HudDimGreen.g, HudDimGreen.b, 0.32f), start: barStart, thickness: 3f);
			if (progress > 0.001f)
			{
				DrawLine(barStart, new Vector2(barStart.x + barWidth * progress, barStart.y), progressColor, 3f);
			}
			return;
			IL_0033:
			num2 = 0;
			goto IL_0034;
		}

		private void DrawTacticalPlot(bool showTrackData)
		{
			Rect plot = new Rect(18f, 18f, 420f, 420f);
			Vector2 center = new Vector2(plot.center.x, plot.center.y);
			float radius = 176f;
			EnsureTacticalBackgroundTexture();
			GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f), tacticalBackgroundTexture);
			float heading = GetCompassHeadingDegrees();
			compassStyle.normal.textColor = HudDimGreen;
			for (int cardinalHeading = 0; cardinalHeading < 360; cardinalHeading += 90)
			{
				float relativeBearing = Mathf.DeltaAngle(heading, cardinalHeading);
				Vector2 labelPosition = center + RadarDirection(relativeBearing) * (radius + 14f);
				GUI.Label(new Rect(labelPosition.x - 24f, labelPosition.y - 10f, 48f, 20f), FormatHeading(cardinalHeading), compassStyle);
			}
			DrawLine(center + new Vector2(0f, -12f), center + new Vector2(-8f, 6f), HudBrightGreen, 2.4f);
			DrawLine(center + new Vector2(0f, -12f), center + new Vector2(8f, 6f), HudBrightGreen, 2.4f);
			DrawLine(center + new Vector2(-8f, 6f), center + new Vector2(8f, 6f), HudBrightGreen, 2.4f);
			if (!showTrackData)
			{
				return;
			}
			float range = GetTacticalDisplayRange();
			for (int i = 0; i < trackCount; i++)
			{
				TrackSnapshot track = tracks[i];
				if (track.Lifecycle == TrackLifecycle.Dropped || !ShouldDisplayTrack(i))
				{
					continue;
				}
				Vector3 relativeWorld = ToVector3(track.Position) - base.transform.position;
				Vector3 relativeLocal = ToHeadingLocal(relativeWorld);
				Vector2 horizontal = new Vector2(relativeLocal.x, relativeLocal.z);
				if (!(horizontal.sqrMagnitude < 0.0001f))
				{
					float normalizedRange = Mathf.Clamp01(horizontal.magnitude / range);
					Vector2 point = center + new Vector2(horizontal.x, 0f - horizontal.y).normalized * radius * normalizedRange;
					float alpha = GetLifecycleAlpha(in track);
					Color color = (track.TrackId.Equals(radar.SttTrackId) ? new Color(HudRed.r, HudRed.g, HudRed.b, alpha) : (track.TrackId.Equals(radar.CandidateTrackId) ? new Color(HudAmber.r, HudAmber.g, HudAmber.b, alpha) : new Color(HudCyan.r, HudCyan.g, HudCyan.b, alpha)));
					bool dashed = track.Lifecycle == TrackLifecycle.Coasting || track.Lifecycle == TrackLifecycle.Degraded;
					DrawTrackSymbol(point, 6f, color, dashed, track.Affiliation);
					Vector3 velocityLocal = ToHeadingLocal(ToVector3(track.Velocity));
					Vector2 velocity2D = new Vector2(velocityLocal.x, 0f - velocityLocal.z);
					if (velocity2D.sqrMagnitude > 0.01f)
					{
						float vectorLength = Mathf.Clamp(velocity2D.magnitude * 0.04f, 8f, 30f);
						DrawLine(point, point + velocity2D.normalized * vectorLength, color, 1.4f);
					}
					if (Mathf.Abs(relativeLocal.y) > 10f)
					{
						DrawAltitudeCue(point + new Vector2(8f, 0f), relativeLocal.y > 0f, color);
					}
					microStyle.alignment = TextAnchor.UpperLeft;
					microStyle.normal.textColor = color;
					float rangeKilometers = horizontal.magnitude / 1000f;
					float altitudeDelta = relativeLocal.y;
					Vector2 labelOffset = ((point.x > center.x) ? new Vector2(9f, -12f) : new Vector2(-76f, -12f));
					smallStyle.normal.textColor = color;
					GUI.Label(ClampToRect(new Rect(point.x + labelOffset.x, point.y + labelOffset.y, 70f, 34f), plot, 5f), TrackLabel(track.TrackId) + " " + SourceLabel(track.SourceMask) + "\n" + $"{rangeKilometers:0.0}K {altitudeDelta:+0;-0;0}", microStyle);
				}
			}
			DrawTacticalMissiles(center, radius, range);
		}

		private void DrawTacticalMissiles(Vector2 center, float radius, float displayRange)
		{
			int missileTrackCount = radar.CopyMissileTracks(missileTracks);
			IReadOnlyList<MechTestProjectile> projectiles = MechTestProjectile.Registry;
			for (int i = 0; i < projectiles.Count; i++)
			{
				MechTestProjectile projectile = projectiles[i];
				if (IsFriendlyEngagementMissile(projectile) &&
					!HasFriendlyMissileTrackNear(
						projectile.transform.position,
						missileTrackCount))
				{
					DrawTacticalMissile(center, radius, displayRange, projectile.transform.position, projectile.Velocity, HudBrightGreen, false, false);
				}
			}
			for (int j = 0; j < missileTrackCount; j++)
			{
				MissileTrackSnapshot track = missileTracks[j];
				if (ShouldDisplayFriendlyMissile(in track))
				{
					DrawTacticalMissile(center, radius, displayRange, ToVector3(track.PredictPosition(radar.SimTimeSeconds)), ToVector3(track.Velocity), HudBrightGreen, false, false);
				}
				else if (ShouldDisplayIncomingMissile(in track))
				{
					bool confirmedOwnshipThreat =
						track.IsLikelyTargetingOwnship;
					bool identifiedAirToAir = track.IsLikelyAirToAir;
					bool emphasizedThreat =
						confirmedOwnshipThreat || identifiedAirToAir;
					Color color = emphasizedThreat
						? HudRed
						: HudAmber;
					DrawTacticalMissile(center, radius, displayRange, ToVector3(track.PredictPosition(radar.SimTimeSeconds)), ToVector3(track.Velocity), color, emphasizedThreat, dashed: false);
				}
			}
		}

		private void DrawTacticalMissile(Vector2 center, float radius, float displayRange, Vector3 worldPosition, Vector3 worldVelocity, Color color, bool emphasized, bool dashed)
		{
			Vector3 relativeWorld = worldPosition - base.transform.position;
			Vector3 relativeLocal = ToHeadingLocal(relativeWorld);
			Vector2 horizontal = new Vector2(relativeLocal.x, relativeLocal.z);
			if (!(horizontal.magnitude > displayRange))
			{
				float normalizedRange = horizontal.magnitude / displayRange;
				Vector2 point = ((horizontal.sqrMagnitude > 0.0001f) ? (center + new Vector2(horizontal.x, 0f - horizontal.y).normalized * radius * normalizedRange) : center);
				DrawDiamond(point, emphasized ? 6.5f : 5f, color, dashed, emphasized ? 2f : 1.3f);
				Vector3 velocityLocal = ToHeadingLocal(worldVelocity);
				Vector2 heading = new Vector2(velocityLocal.x, 0f - velocityLocal.z);
				if (!(heading.sqrMagnitude <= 0.01f))
				{
					Vector2 direction = heading.normalized;
					DrawLine(point + direction * 7f, point + direction * 22f, color, 1.3f);
				}
			}
		}

		private void DrawRadarStatus(bool showScan, bool showTrackData)
		{
			Rect rect = new Rect(MechHudLayout.Width - 560f - 18f, MechHudLayout.Height - 440f - 18f, 560f, 440f);
			float halfAngle = Mathf.Clamp(radarStatus.ScanAzimuthDegrees * 0.5f, 20f, 80f);
			float displayRange = GetRadarDisplayRange(halfAngle);
			Vector2 fanApex = new Vector2(rect.x + 240f, rect.y + 325f);
			float fanRadius = 240f;
			for (float angle = 0f - halfAngle; angle <= halfAngle; angle += 1f)
			{
				DrawLine(fanApex, fanApex + RadarDirection(angle) * fanRadius, HudFanFill, 8f);
			}
			DrawLine(fanApex, fanApex + RadarDirection(0f - halfAngle) * fanRadius, HudGreen, 2f);
			DrawLine(fanApex, fanApex + RadarDirection(halfAngle) * fanRadius, HudGreen, 2f);
			DrawFanArc(fanApex, fanRadius, 0f - halfAngle, halfAngle, HudGreen, 2f);
			DrawFanArc(fanApex, fanRadius * 0.75f, 0f - halfAngle, halfAngle, HudDimGreen, 1f);
			DrawFanArc(fanApex, fanRadius * 0.5f, 0f - halfAngle, halfAngle, HudDimGreen, 1f);
			DrawFanArc(fanApex, fanRadius * 0.25f, 0f - halfAngle, halfAngle, HudDimGreen, 1f);
			DrawLine(fanApex, fanApex + RadarDirection(0f) * fanRadius, HudDimGreen, 1f);
			DrawLine(fanApex, fanApex + RadarDirection((0f - halfAngle) * 0.5f) * fanRadius, HudDimGreen, 1f);
			DrawLine(fanApex, fanApex + RadarDirection(halfAngle * 0.5f) * fanRadius, HudDimGreen, 1f);
			if (showScan)
			{
				ResolveDisplayedRadarBeam(
					halfAngle,
					out float beamAngle,
					out _,
					out float beamWidth);
				float beamHalfWidth = Mathf.Max(1f, beamWidth * 0.5f);
				Color beamFill = new Color(
					HudBrightGreen.r,
					HudBrightGreen.g,
					HudBrightGreen.b,
					0.28f);
				for (float angle2 = beamAngle - beamHalfWidth; angle2 <= beamAngle + beamHalfWidth; angle2 += 1f)
				{
					DrawLine(
						fanApex,
						fanApex + RadarDirection(Mathf.Clamp(angle2, 0f - halfAngle, halfAngle)) * fanRadius,
						beamFill,
						4f);
				}
				DrawLine(
					fanApex,
					fanApex + RadarDirection(beamAngle) * fanRadius,
					HudBrightGreen,
					2.6f);
			}
			if (showTrackData)
			{
				DrawRadarFanTracks(fanApex, fanRadius, halfAngle, displayRange);
			}
			string sttText = ((showTrackData && radarStatus.SttTrackId.IsValid) ? TrackLabel(radarStatus.SttTrackId) : "--");
			microStyle.alignment = TextAnchor.MiddleCenter;
			microStyle.normal.textColor = HudWhite;
			GUI.Label(new Rect(fanApex.x - 54f, fanApex.y + 3f, 108f, 18f), $"{0f - halfAngle:0}  方位  +{halfAngle:0}", microStyle);
			DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 1f, displayRange);
			DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 0.75f, displayRange);
			DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 0.5f, displayRange);
			DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 0.25f, displayRange);
			float elevationX = rect.x + 442f;
			float elevationTop = rect.y + 72f;
			float elevationBottom = rect.y + 310f;
			DrawLine(new Vector2(elevationX, elevationTop), new Vector2(elevationX, elevationBottom), HudDimGreen, 1.3f);
			for (int tick = 0; tick <= 6; tick++)
			{
				float y = Mathf.Lerp(elevationTop, elevationBottom, (float)tick / 6f);
				DrawLine(new Vector2(elevationX - 5f, y), new Vector2(elevationX + 5f, y), HudDimGreen, 1f);
			}
			if (showScan)
			{
				ResolveDisplayedRadarBeam(
					halfAngle,
					out _,
					out float beamElevation,
					out _);
				float elevationHalf = Mathf.Max(1f, radarStatus.ScanElevationDegrees * 0.5f);
				float elevationNormalized = Mathf.InverseLerp(elevationHalf, 0f - elevationHalf, Mathf.Clamp(beamElevation, 0f - elevationHalf, elevationHalf));
				float elevationY = Mathf.Lerp(elevationTop, elevationBottom, elevationNormalized);
				DrawLine(
					new Vector2(elevationX - 10f, elevationY),
					new Vector2(elevationX + 10f, elevationY),
					HudBrightGreen,
					2.5f);
			}
			statusStyle.normal.textColor = HudGreen;
			string radarStatusText = !radar.RadarPowerEnabled
				? $"航迹 {(showTrackData ? displayTrackCount : 0)}/{radarStatus.TrackCapacity}\nSTT OFF\n\n雷达 OFF\n被动接收"
				: ((!showScan) ? "航迹 --/--\nSTT OFF\n\n发射机 BIT\n制导 OFF" : ((!showTrackData) ? "航迹 --/--\nSTT OFF\n\n扫描 ON\n处理器 BIT" : ($"航迹 {displayTrackCount}/{radarStatus.TrackCapacity}\n" + "STT " + sttText + "\n\n" + $"负载 {radarStatus.DwellLoad * 100f:0}%\n" + $"制导 {radarStatus.GuidanceChannelsUsed}/" + $"{radarStatus.GuidanceChannelCapacity}")));
			GUI.Label(new Rect(rect.x + 458f, rect.y + 66f, 92f, 220f), radarStatusText, statusStyle);
		}

		private void UpdateRadarSweepVisual()
		{
			if (!radar.RadarPowerEnabled ||
				radarStatus.Mode == RadarOperatingMode.SingleTargetTrack)
			{
				return;
			}

			float traversalRate = Mathf.Max(0.05f, radarStatus.RefreshRateHz) * 0.5f;
			radarSweepVisualPhase = Mathf.Repeat(
				radarSweepVisualPhase + Time.unscaledDeltaTime * traversalRate,
				2f);
		}

		private void ResolveDisplayedRadarBeam(
			float halfAngle,
			out float azimuthDegrees,
			out float elevationDegrees,
			out float widthDegrees)
		{
			if (radarStatus.Mode == RadarOperatingMode.SingleTargetTrack &&
				TryGetSttRadarAngles(out azimuthDegrees, out elevationDegrees))
			{
				azimuthDegrees = Mathf.Clamp(azimuthDegrees, -halfAngle, halfAngle);
				widthDegrees = radarStatus.BeamWidthDegrees;
				return;
			}

			float normalizedSweep = Mathf.PingPong(radarSweepVisualPhase, 1f);
			azimuthDegrees = Mathf.Lerp(-halfAngle, halfAngle, normalizedSweep);
			elevationDegrees = radarStatus.BeamElevationDegrees;
			widthDegrees = radarStatus.BeamWidthDegrees;
		}

		private bool TryGetSttRadarAngles(
			out float azimuthDegrees,
			out float elevationDegrees)
		{
			azimuthDegrees = 0f;
			elevationDegrees = 0f;
			if (!radarStatus.SttTrackId.IsValid)
			{
				return false;
			}

			for (int i = 0; i < trackCount; i++)
			{
				if (!tracks[i].TrackId.Equals(radarStatus.SttTrackId))
				{
					continue;
				}

				Vector3 direction =
					radar.PredictTrackPosition(in tracks[i]) - transform.position;
				if (direction.sqrMagnitude < 0.0001f)
				{
					return false;
				}

				Quaternion bodyRotation = motion != null
					? motion.LogicalRotation
					: transform.rotation;
				Vector3 local = Quaternion.Inverse(bodyRotation) * direction.normalized;
				azimuthDegrees = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
				float horizontal = Mathf.Sqrt(local.x * local.x + local.z * local.z);
				elevationDegrees = Mathf.Atan2(local.y, horizontal) * Mathf.Rad2Deg;
				return true;
			}

			return false;
		}

		private void DrawRadarFanTracks(Vector2 apex, float radius, float halfAngle, float displayRange)
		{
			float range = Mathf.Max(1f, displayRange);
			for (int i = 0; i < trackCount; i++)
			{
				TrackSnapshot track = tracks[i];
				if (track.Lifecycle == TrackLifecycle.Dropped || !ShouldDisplayTrack(i))
				{
					continue;
				}
				Vector3 relativeWorld = ToVector3(track.Position) - base.transform.position;
				Vector3 relativeLocal = ToHeadingLocal(relativeWorld);
				Vector2 horizontal = new Vector2(relativeLocal.x, relativeLocal.z);
				if (!(horizontal.sqrMagnitude < 0.0001f) && !(horizontal.y <= 0f))
				{
					float azimuth = Mathf.Atan2(horizontal.x, horizontal.y) * 57.29578f;
					if (!(Mathf.Abs(azimuth) > halfAngle))
					{
						Vector2 point = apex + RadarDirection(azimuth) * radius * Mathf.Clamp01(horizontal.magnitude / range);
						float alpha = GetLifecycleAlpha(in track);
						Color color = (track.TrackId.Equals(radar.SttTrackId) ? new Color(HudRed.r, HudRed.g, HudRed.b, alpha) : (track.TrackId.Equals(radar.CandidateTrackId) ? new Color(HudAmber.r, HudAmber.g, HudAmber.b, alpha) : new Color(HudCyan.r, HudCyan.g, HudCyan.b, alpha)));
						DrawTrackSymbol(point, 6f, color, track.Lifecycle == TrackLifecycle.Coasting || track.Lifecycle == TrackLifecycle.Degraded, track.Affiliation);
						bool labelOnLeft = point.x > apex.x + radius * 0.25f;
						microStyle.alignment = (labelOnLeft ? TextAnchor.UpperRight : TextAnchor.UpperLeft);
						microStyle.normal.textColor = color;
						GUI.Label(new Rect(labelOnLeft ? (point.x - 96f) : (point.x + 8f), point.y - 14f, 88f, 34f), TrackLabel(track.TrackId) + "\n" + $"{horizontal.magnitude / 1000f:0.0} 公里", microStyle);
					}
				}
			}
		}

		private void DrawRadarRangeLabel(Vector2 apex, float radius, float halfAngle, float rangeFraction, float displayRange)
		{
			float labelAngle = 0f - halfAngle + Mathf.Min(6f, halfAngle * 0.2f);
			Vector2 point = apex + RadarDirection(labelAngle) * radius * rangeFraction;
			microStyle.alignment = TextAnchor.MiddleRight;
			microStyle.normal.textColor = HudDimGreen;
			GUI.Label(new Rect(point.x - 58f, point.y - 9f, 52f, 18f), FormatRange(displayRange * rangeFraction), microStyle);
		}

		private void DrawTrackOperationOverlay()
		{
			Vector2 center = GetReticleGuiPosition();
			float ringRadius = Mathf.Clamp(MechHudLayout.Height * 0.75f, 320f, MechHudLayout.Height * 0.8f) * 0.5f;
			Vector2 ringCenter = GetBodyForwardGuiPosition(ringRadius);
			DrawSmoothTrackOperationRing(ringCenter, ringRadius);
			for (int i = 0; i < 4; i++)
			{
				float angle = (float)i * MathF.PI * 0.5f;
				Vector2 direction = new Vector2(Mathf.Sin(angle), 0f - Mathf.Cos(angle));
				if (!(Mathf.Abs(direction.x) > 0.5f))
				{
					Vector2 vector = ringCenter + direction * (ringRadius + 10f);
					Vector2 tickEnd = vector + direction * 18f;
					DrawLine(vector, tickEnd, new Color(0f, 0.08f, 0.025f, 0.8f), 4.2f);
					DrawLine(vector, tickEnd, HudBrightGreen, 2.1f);
				}
			}
			DrawSightElevationLadder(ringCenter, ringRadius);
			DrawZoomMagnification(ringCenter, ringRadius);
			Vector2 snappedCenter = center;
			if (radar.CandidateTrackId.IsValid && TryGetTrackScreenPoint(radar.CandidateTrackId, out var candidateCenter))
			{
				snappedCenter = Vector2.Lerp(center, candidateCenter, 0.08f);
				float maxOffset = 6f;
				Vector2 offset = snappedCenter - center;
				if (offset.magnitude > maxOffset)
				{
					snappedCenter = center + offset.normalized * maxOffset;
				}
			}
			DrawCircle(snappedCenter, 16f, HudBrightGreen, 1.4f, dashed: false);
			DrawLine(snappedCenter + new Vector2(-22f, 0f), snappedCenter + new Vector2(-11f, 0f), HudBrightGreen, 1.3f);
			DrawLine(snappedCenter + new Vector2(22f, 0f), snappedCenter + new Vector2(11f, 0f), HudBrightGreen, 1.3f);
			DrawLine(snappedCenter + new Vector2(0f, -22f), snappedCenter + new Vector2(0f, -11f), HudBrightGreen, 1.3f);
			DrawLine(snappedCenter + new Vector2(0f, 22f), snappedCenter + new Vector2(0f, 11f), HudBrightGreen, 1.3f);
			if (radar.CandidateTrackId.IsValid && !radar.HasStt)
			{
				float progressWidth = 86f * radar.DesignationProgress;
				DrawLine(center + new Vector2(-43f, 31f), center + new Vector2(-43f + progressWidth, 31f), HudBrightGreen, 2f);
				smallStyle.alignment = TextAnchor.MiddleCenter;
				smallStyle.normal.textColor = HudBrightGreen;
				GUI.Label(new Rect(center.x - 70f, center.y + 36f, 140f, 20f), "DESIGNATE", smallStyle);
			}
		}

		private void DrawSightElevationLadder(Vector2 center, float radius)
		{
            var sightForward = viewCamera.transform.forward;
			Vector3 referenceUp = GetReferenceUp();
            var visibility = ResolveHorizonVisibility(sightForward, referenceUp);
            if (visibility <= 0f) return;
            var previousTint = GUI.color;
            GUI.color = new Color(previousTint.r, previousTint.g, previousTint.b, previousTint.a * visibility);
			float sightElevationDegrees = Mathf.Asin(Mathf.Clamp(Vector3.Dot(sightForward, referenceUp), -1f, 1f)) * 57.29578f;
			float verticalFovRadians = Mathf.Clamp(viewCamera.fieldOfView, 1f, 179f) * (MathF.PI / 180f);
			float focalPixels = MechHudLayout.Height * 0.5f / Mathf.Tan(verticalFovRadians * 0.5f);
			Matrix4x4 previousGuiMatrix = GUI.matrix;
			GUIUtility.RotateAroundPivot(ResolvePitchLadderRollDegrees(sightForward, referenceUp), center);
			DrawMirroredPitchTick(center, radius, 0f, 10f, 44f, HudBrightGreen, 2.1f, null);
			for (int pitchDegrees = -80; pitchDegrees <= 80; pitchDegrees += 5)
			{
				float relativeDegrees = (float)pitchDegrees - sightElevationDegrees;
				if (!(Mathf.Abs(relativeDegrees) >= 85f))
				{
					float yOffset = (0f - Mathf.Tan(relativeDegrees * (MathF.PI / 180f))) * focalPixels;
					if (!(Mathf.Abs(yOffset) > radius - 28f) && !(Mathf.Abs(yOffset) < 3f))
					{
						bool major = pitchDegrees % 10 == 0;
						bool num = pitchDegrees == 0;
						float tickLength = (num ? 44f : (major ? 34f : 22f));
						Color color = (num ? HudBrightGreen : (major ? new Color(HudBrightGreen.r, HudBrightGreen.g, HudBrightGreen.b, 0.9f) : new Color(HudDimGreen.r, HudDimGreen.g, HudDimGreen.b, 0.7f)));
						string label = ((!major) ? null : ((pitchDegrees == 0) ? "00" : ((pitchDegrees > 0) ? $"+{pitchDegrees:00}" : $"{pitchDegrees:00}")));
						DrawMirroredPitchTick(center, radius, yOffset, 10f, tickLength, color, major ? 1.7f : 1.2f, label);
					}
				}
			}
			GUI.matrix = previousGuiMatrix;
            GUI.color = previousTint;
		}

        // At a navigation pole the projected horizon has no stable direction.
        // Fade the ladder there; never turn this display ambiguity into camera rotation.
        private static float ResolveHorizonVisibility(Vector3 sightForward, Vector3 referenceUp) =>
            Mathf.InverseLerp(0.03f, 0.15f,
                Vector3.ProjectOnPlane(referenceUp.normalized, sightForward.normalized).magnitude);

		private void DrawZoomMagnification(Vector2 center, float radius)
		{
			if (aimController == null && viewCamera != null)
			{
				aimController = viewCamera.GetComponent<MechThirdPersonCamera>();
			}
			if (!(aimController == null) && aimController.IsZoomActive)
			{
				zoomStyle.normal.textColor = HudBrightGreen;
				GUI.Label(new Rect(center.x + radius * 0.52f, center.y - radius * 0.76f, 96f, 28f), $"{aimController.ZoomMagnification}X", zoomStyle);
			}
		}

		private float ResolvePitchLadderRollDegrees(Vector3 cameraForward, Vector3 referenceUp)
		{
			Vector3 projectedUp = Vector3.ProjectOnPlane(referenceUp, cameraForward);
			if (projectedUp.sqrMagnitude <= 1E-06f)
			{
				return pitchLadderRollDegrees;
			}
			projectedUp.Normalize();
			pitchLadderRollDegrees = Mathf.Atan2(Vector3.Dot(projectedUp, viewCamera.transform.right), Vector3.Dot(projectedUp, viewCamera.transform.up)) * 57.29578f;
			return pitchLadderRollDegrees;
		}

		private void DrawMirroredPitchTick(Vector2 center, float radius, float yOffset, float gap, float length, Color color, float thickness, string label)
		{
			float halfChord = Mathf.Sqrt(Mathf.Max(0f, radius * radius - yOffset * yOffset));
			float y = center.y + yOffset;
			Vector2 leftStart = new Vector2(center.x - halfChord - gap, y);
			Vector2 leftEnd = leftStart + Vector2.left * length;
			Vector2 vector = new Vector2(center.x + halfChord + gap, y);
			Vector2 rightEnd = vector + Vector2.right * length;
			DrawLine(leftStart, leftEnd, color, thickness);
			DrawLine(vector, rightEnd, color, thickness);
			if (!string.IsNullOrEmpty(label))
			{
				pitchStyle.normal.textColor = color;
				pitchStyle.alignment = TextAnchor.MiddleRight;
				GUI.Label(new Rect(leftEnd.x - 56f, y - 11f, 48f, 22f), label, pitchStyle);
				pitchStyle.alignment = TextAnchor.MiddleLeft;
				GUI.Label(new Rect(rightEnd.x + 8f, y - 11f, 48f, 22f), label, pitchStyle);
			}
		}

        private static bool CanIndicateEnemy(in TrackSnapshot track)
        {
            return track.TrackId.IsValid && track.Affiliation != TrackAffiliation.Friendly &&
                track.ObjectClass != TrackObjectClass.Missile && track.ObjectClass != TrackObjectClass.Decoy &&
                track.Lifecycle != TrackLifecycle.Dropped && track.Lifecycle != TrackLifecycle.Tentative &&
                track.Confidence >= 0.25f;
        }

        private EnemyDirectionState GetDirectionState(TrackId id) =>
            directionStates.TryGetValue(id, out var state) ? state : null;

        private Rect GetDirectionViewport()
        {
            var pixels = viewCamera.pixelRect;
            var scale = MechHudLayout.Scale;
            return new Rect(pixels.x / scale, (Screen.height - pixels.yMax) / scale,
                pixels.width / scale, pixels.height / scale);
        }

        private void ResetEnemyDirectionIndicator()
        {
            Array.Clear(preparedDirectionStates, 0, preparedDirectionStates.Length);
            directionStates.Clear();
        }

        private void PrepareEnemyDirectionIndicator(float deltaTime)
        {
            if (directionPreparedFrame == Time.frameCount) return;
            directionPreparedFrame = Time.frameCount;
            directionPreparationSerial++;
            directionViewport = GetDirectionViewport();
            Array.Clear(preparedDirectionStates, 0, preparedDirectionStates.Length);
            if (!showEnemyDirectionIndicator || !IsStartupModuleOnline(MechStartupHudModule.TrackData))
            {
                ResetEnemyDirectionIndicator();
                return;
            }
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (!CanIndicateEnemy(in track) || IsMissileTrack(in track) || !ShouldDisplayTrack(i) ||
                    radar.IsRetiredFriendlyMissileTrack(track.TrackId)) continue;
                if (!directionStates.TryGetValue(track.TrackId, out var state))
                {
                    state = new EnemyDirectionState();
                    directionStates.Add(track.TrackId, state);
                }
                state.SeenSerial = directionPreparationSerial;
                state.TrackIndex = i;
                var position = radar.PredictTrackPosition(in track);
                var screen = viewCamera.WorldToScreenPoint(position);
                var center = MechHudLayout.ScreenToHudPoint(screen);
                var local = viewCamera.transform.InverseTransformDirection(position - viewCamera.transform.position);
                var focused = track.TrackId.Equals(radar.SttTrackId) || track.TrackId.Equals(radar.CandidateTrackId);
                var halfSize = focused ? 33f : 27f;
                // Positive depth projection is safe; behind the camera, use the
                // unmirrored camera-space bearing with the same aspect correction.
                var bearing = screen.z > 0f ? center - directionViewport.center :
                    new Vector2(local.x / Mathf.Max(0.1f, viewCamera.aspect), -local.y);
                state.Update(track.TrackId, local, bearing,
                    new Rect(center.x - halfSize, center.y - halfSize, halfSize * 2f, halfSize * 2f),
                    screen.z > viewCamera.nearClipPlane, directionViewport, deltaTime);
                if (!state.TrackId.IsValid) continue;
                state.RenderSize = enemyEdgeIndicatorSize;
                state.Anchor = ResolveEdgeAnchor(state.Direction, directionViewport,
                    enemyEdgeIndicatorSize * 1.3f + 10f, out state.Edge);
                preparedDirectionStates[i] = state;
            }
            staleDirectionIds.Clear();
            foreach (var pair in directionStates)
                if (pair.Value.SeenSerial != directionPreparationSerial) staleDirectionIds.Add(pair.Key);
            foreach (var id in staleDirectionIds) directionStates.Remove(id);
            ArrangeEdgeIndicators();
        }

        private static Vector2 ResolveEdgeAnchor(Vector2 direction, Rect viewport, float margin, out int edge)
        {
            if (direction.sqrMagnitude < 0.000001f) direction = Vector2.right;
            direction.Normalize();
            var half = new Vector2(Mathf.Max(1f, viewport.width * 0.5f - margin),
                Mathf.Max(1f, viewport.height * 0.5f - margin));
            var tx = Mathf.Abs(direction.x) > 0.000001f ? half.x / Mathf.Abs(direction.x) : float.PositiveInfinity;
            var ty = Mathf.Abs(direction.y) > 0.000001f ? half.y / Mathf.Abs(direction.y) : float.PositiveInfinity;
            edge = tx <= ty ? (direction.x < 0f ? 0 : 1) : (direction.y < 0f ? 2 : 3);
            return viewport.center + direction * Mathf.Min(tx, ty);
        }

        private void ArrangeEdgeIndicators()
        {
            var margin = enemyEdgeIndicatorSize * 1.3f + 10f;
            for (var edge = 0; edge < 4; edge++)
            {
                edgeLayoutScratch.Clear();
                for (var i = 0; i < trackCount; i++)
                {
                    var state = preparedDirectionStates[i];
                    if (state != null && !state.BoxVisible && state.Edge == edge) edgeLayoutScratch.Add(state);
                }
                var vertical = edge < 2;
                var lower = (vertical ? directionViewport.yMin : directionViewport.xMin) + margin;
                var upper = (vertical ? directionViewport.yMax : directionViewport.xMax) - margin;
                var preferredSpacing = vertical ? Mathf.Max(64f, enemyEdgeIndicatorSize * 2.6f + 12f) : 212f;
                var spacing = edgeLayoutScratch.Count > 1
                    ? Mathf.Min(preferredSpacing, (upper - lower) / (edgeLayoutScratch.Count - 1))
                    : preferredSpacing;
                for (var i = 1; i < edgeLayoutScratch.Count; i++)
                {
                    var entry = edgeLayoutScratch[i];
                    var coordinate = vertical ? entry.Anchor.y : entry.Anchor.x;
                    var j = i - 1;
                    while (j >= 0)
                    {
                        var other = edgeLayoutScratch[j];
                        var otherCoordinate = vertical ? other.Anchor.y : other.Anchor.x;
                        if (otherCoordinate < coordinate ||
                            (Mathf.Approximately(otherCoordinate, coordinate) && other.TrackId.Packed < entry.TrackId.Packed)) break;
                        edgeLayoutScratch[j + 1] = other; j--;
                    }
                    edgeLayoutScratch[j + 1] = entry;
                }
                var previous = lower - spacing;
                for (var i = 0; i < edgeLayoutScratch.Count; i++)
                {
                    var state = edgeLayoutScratch[i];
                    var coordinate = Mathf.Max(vertical ? state.Anchor.y : state.Anchor.x, previous + spacing);
                    if (vertical) state.Anchor.y = coordinate; else state.Anchor.x = coordinate;
                    previous = coordinate;
                }
                var next = upper + spacing;
                for (var i = edgeLayoutScratch.Count - 1; i >= 0; i--)
                {
                    var state = edgeLayoutScratch[i];
                    var coordinate = Mathf.Min(vertical ? state.Anchor.y : state.Anchor.x, next - spacing);
                    if (vertical) state.Anchor.y = coordinate; else state.Anchor.x = coordinate;
                    next = coordinate;
                    state.FullDetails = spacing >= preferredSpacing - 0.1f;
                    state.RenderSize = Mathf.Min(enemyEdgeIndicatorSize, Mathf.Max(10f, (spacing - 8f) * 0.4f));
                    state.InfoRect = ResolveEdgeInfoRect(state.Anchor, edge, state.RenderSize,
                        state.FullDetails ? 200f : 48f, state.FullDetails ? 44f : 22f, directionViewport);
                }
            }
        }

        private static Rect ResolveEdgeInfoRect(Vector2 anchor, int edge, float size,
            float width, float height, Rect viewport)
        {
            var gap = size * 1.3f + 8f;
            var origin = edge switch
            {
                0 => new Vector2(anchor.x + gap, anchor.y - height * 0.5f),
                1 => new Vector2(anchor.x - gap - width, anchor.y - height * 0.5f),
                2 => new Vector2(anchor.x - width * 0.5f, anchor.y + gap),
                _ => new Vector2(anchor.x - width * 0.5f, anchor.y - gap - height),
            };
            return new Rect(Mathf.Clamp(origin.x, viewport.xMin + 8f, viewport.xMax - width - 8f),
                Mathf.Clamp(origin.y, viewport.yMin + 8f, viewport.yMax - height - 8f), width, height);
        }

        private bool ShouldDrawDirectionTargetFrame(int index)
        {
            var state = preparedDirectionStates[index];
            if (state != null) return state.BoxVisible;
            // Do not clamp labels for an off-screen frame independently: it
            // produces the floating distance/ID fragments shown in the report.
            var screen = viewCamera.WorldToScreenPoint(radar.PredictTrackPosition(in tracks[index]));
            return screen.z > viewCamera.nearClipPlane &&
                GetDirectionViewport().Contains(MechHudLayout.ScreenToHudPoint(screen));
        }

        private void DrawEnemyDirectionIndicator()
        {
            // Unlike GUI.Label, immediate GL drawing is not automatically
            // ignored during layout/input events.
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            for (var i = 0; i < trackCount; i++)
            {
                var state = preparedDirectionStates[i];
                if (state == null || state.BoxVisible) continue;
                var track = tracks[i];
                var stale = track.Lifecycle == TrackLifecycle.Coasting || track.Lifecycle == TrackLifecycle.Degraded;
                var color = HudGreen;
                color.a = stale ? Mathf.Min(0.55f, GetLifecycleAlpha(in track)) : GetLifecycleAlpha(in track);
                var tint = color; tint.a *= GUI.color.a;
                BuildDirectionArrow(state.Orientation);
                RenderDirectionArrow(state.Anchor, state.RenderSize, tint, GUI.matrix, Screen.width, Screen.height);
                DrawEdgeTrackInfo(in track, state, color);
            }
        }

        private void DrawEdgeTrackInfo(in TrackSnapshot track, EnemyDirectionState state, Color color)
        {
            smallStyle.normal.textColor = color;
            smallStyle.alignment = state.Edge == 0 ? TextAnchor.MiddleLeft :
                state.Edge == 1 ? TextAnchor.MiddleRight : TextAnchor.MiddleCenter;
            if (!state.FullDetails)
            {
                GUI.Label(state.InfoRect, TrackLabel(track.TrackId), smallStyle);
                return;
            }
            var relative = radar.PredictTrackPosition(in track) - transform.position;
            var closing = relative.sqrMagnitude > 0.01f
                ? -Vector3.Dot(ToVector3(track.Velocity - radarStatus.OwnshipVelocity), relative.normalized) : 0f;
            GetTrackReadout(in track, closing, out var displayedClosing);
            var first = state.InfoRect; first.height = 22f;
            GUI.Label(first, $"{relative.magnitude / 1000f:0.00} km  {displayedClosing:+0;-0;0} m/s", smallStyle);
            var lifecycle = track.TrackId.Equals(radar.SttTrackId) ? "STT" : LifecycleLabel(track.Lifecycle);
            var classification = track.Classification >= 0.72f && track.IdentityConfidence >= 0.7f
                ? AffiliationLabel(track.Affiliation) : "UNK";
            first.y += 22f;
            GUI.Label(first, TrackLabel(track.TrackId) + "  " + lifecycle + "  " + classification, smallStyle);
        }

        private void BuildDirectionArrow(Quaternion rotation)
        {
            // Full camera-relative orientation, with no artificial depth tilt.
            // One square-based pyramid: four base edges and four tip edges.
            directionArrowVertices[0] = rotation * new Vector3(-0.38f, -0.38f, -0.55f);
            directionArrowVertices[1] = rotation * new Vector3(0.38f, -0.38f, -0.55f);
            directionArrowVertices[2] = rotation * new Vector3(0.38f, 0.38f, -0.55f);
            directionArrowVertices[3] = rotation * new Vector3(-0.38f, 0.38f, -0.55f);
            directionArrowVertices[4] = rotation * new Vector3(0f, 0f, 0.95f);
            directionArrowEdgeCount = 0;
            for (var i = 0; i < 4; i++)
            {
                directionArrowEdges[directionArrowEdgeCount++] = new DirectionArrowEdge
                {
                    A = directionArrowVertices[i], B = directionArrowVertices[(i + 1) % 4]
                };
                directionArrowEdges[directionArrowEdgeCount++] = new DirectionArrowEdge
                {
                    A = directionArrowVertices[i], B = directionArrowVertices[4]
                };
            }
        }

        private static Vector3 ProjectArrowVertex(Vector3 vertex, Vector2 center, float size, Matrix4x4 matrix)
        {
            var perspective = 4f / (4f + vertex.z);
            return matrix.MultiplyPoint3x4(new Vector3(
                center.x + vertex.x * size * perspective, center.y - vertex.y * size * perspective, 0f));
        }

        private void RenderDirectionArrow(Vector2 center, float size, Color color, Matrix4x4 matrix,
            float canvasWidth, float canvasHeight)
        {
            if (directionArrowMaterial == null)
            {
                var shader = Resources.Load<Shader>("Mech/EnemyDirectionArrow");
                if (shader == null) return;
                directionArrowMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (!directionArrowMaterial.SetPass(0)) return;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, canvasWidth, canvasHeight, 0f);
            GL.Begin(GL.TRIANGLES);
            GL.Color(color);
            var halfWidth = Mathf.Max(0.6f, matrix.MultiplyVector(Vector3.right).magnitude * 1.1f);
            for (var i = 0; i < directionArrowEdgeCount; i++)
            {
                var edge = directionArrowEdges[i];
                var a = ProjectArrowVertex(edge.A, center, size, matrix);
                var b = ProjectArrowVertex(edge.B, center, size, matrix);
                var delta = b - a;
                if (delta.sqrMagnitude < 0.0001f) continue;
                var side = new Vector3(-delta.y, delta.x, 0f).normalized * halfWidth;
                // Thin screen-space ribbons keep wire width readable at all
                // HUD scales. The pyramid's faces remain completely transparent.
                GL.Vertex(a + side); GL.Vertex(a - side); GL.Vertex(b - side);
                GL.Vertex(a + side); GL.Vertex(b - side); GL.Vertex(b + side);
            }
            GL.End();
            GL.PopMatrix();
        }

		private void DrawWorldTrackMarkers()
		{
			for (int i = 0; i < trackCount; i++)
			{
				TrackSnapshot track = tracks[i];
				if (track.Lifecycle == TrackLifecycle.Dropped || !ShouldDisplayTrack(i))
				{
					continue;
				}
                if (!ShouldDrawDirectionTargetFrame(i))
                    continue;
				Vector3 position = radar.PredictTrackPosition(in track);
				Vector3 screen = viewCamera.WorldToScreenPoint(position);
				if (!(screen.z <= 0f))
				{
					Vector2 center = MechHudLayout.ScreenToHudPoint(screen);
					float alpha = GetLifecycleAlpha(in track);
					bool isCandidate = track.TrackId.Equals(radar.CandidateTrackId);
					bool isStt = track.TrackId.Equals(radar.SttTrackId);
					bool isTentative = track.Lifecycle == TrackLifecycle.Tentative;
					bool num = !isTentative || isCandidate || isStt || Mathf.Repeat(Time.unscaledTime * 3f, 1f) <= 0.55f;
					Color color = (isStt ? new Color(HudRed.r, HudRed.g, HudRed.b, alpha) : (isCandidate ? new Color(HudAmber.r, HudAmber.g, HudAmber.b, alpha) : new Color(HudGreen.r, HudGreen.g, HudGreen.b, alpha)));
					bool dashed = track.Lifecycle == TrackLifecycle.Coasting || track.Lifecycle == TrackLifecycle.Degraded;
					float size = ((isCandidate || isStt) ? 66f : 54f);
					if (num)
					{
						DrawCornerFrame(new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size), color, isTentative ? 7f : 10f, isStt ? 2.8f : 1.7f, dashed);
						if (!IsMissileTrack(in track))
						{
							DrawTrackVelocityVector(
								center,
								size,
								position,
								ToVector3(track.Velocity - radarStatus.OwnshipVelocity),
								alpha);
						}
					}
					Vector3 relative = position - base.transform.position;
					Vector3 lineOfSight = ((relative.sqrMagnitude > 0.01f) ? relative.normalized : base.transform.forward);
					float closing = 0f - Vector3.Dot(ToVector3(track.Velocity - radarStatus.OwnshipVelocity), lineOfSight);
					string lifecycle = (isStt ? "STT" : LifecycleLabel(track.Lifecycle));
					string classification = ((track.Classification >= 0.72f && track.IdentityConfidence >= 0.7f) ? AffiliationLabel(track.Affiliation) : "UNK");
					GetTrackReadout(in track, closing, out var displayedClosing);
					smallStyle.normal.textColor = color;
					smallStyle.alignment = TextAnchor.MiddleCenter;
					GUI.Label(ClampToScreen(new Rect(center.x - 105f, center.y - size * 0.5f - 25f, 210f, 22f), 8f), $"{relative.magnitude / 1000f:0.00} km  " + $"{displayedClosing:+0;-0;0} m/s", smallStyle);
					GUI.Label(ClampToScreen(new Rect(center.x - 100f, center.y + size * 0.5f + 4f, 200f, 22f), 8f), TrackLabel(track.TrackId) + "  " + lifecycle + "  " + classification, smallStyle);
					if (track.ObjectClass == TrackObjectClass.AirVehicle &&
						track.Lifecycle != TrackLifecycle.Dropped)
					{
						DrawTargetDamageAssessment(center, size, in track);
					}
				}
			}
		}

		private void DrawTargetDamageAssessment(
			Vector2 center,
			float markerSize,
			in TrackSnapshot track)
		{
			string label = "损伤评估中";
			Color color = HudWhite;
			if (radar != null &&
				radar.TryGetTargetDamageAssessment(track.TrackId, out var assessment))
			{
				if (assessment.Condition == MechTargetDamageCondition.Nominal)
				{
					return;
				}

				label = DamageConditionLabel(assessment.Condition);
				color = DamageConditionColor(assessment.Condition);
			}

			smallStyle.normal.textColor = color;
			smallStyle.alignment = TextAnchor.MiddleRight;
			var frameLeft = center.x - markerSize * 0.5f;
			var frameTop = center.y - markerSize * 0.5f;
			var rect = new Rect(
				frameLeft - 172f,
				frameTop + 1f,
				160f,
				22f);
			GUI.Label(ClampToScreen(rect, 8f), label, smallStyle);
		}

		private static string DamageConditionLabel(MechTargetDamageCondition condition)
		{
			return condition switch
			{
				MechTargetDamageCondition.Nominal => "无明显损伤",
				MechTargetDamageCondition.LightDamage => "轻度受损",
				MechTargetDamageCondition.ModerateDamage => "中度受损",
				MechTargetDamageCondition.HeavyDamage => "重度受损",
				MechTargetDamageCondition.CriticalDamage => "濒临失效",
				_ => "损伤不明",
			};
		}

		private static Color DamageConditionColor(MechTargetDamageCondition condition)
		{
			return condition switch
			{
				MechTargetDamageCondition.Nominal => HudGreen,
				MechTargetDamageCondition.LightDamage => HudAmber,
				MechTargetDamageCondition.ModerateDamage => new Color(1f, 0.48f, 0.12f, 0.95f),
				MechTargetDamageCondition.HeavyDamage => HudRed,
				MechTargetDamageCondition.CriticalDamage => HudRed,
				_ => HudWhite,
			};
		}

		private bool IsMissileTrack(in TrackSnapshot track)
		{
			return track.ObjectClass == TrackObjectClass.Missile ||
				(radar != null && radar.TryGetMissileTrack(track.TrackId, out _));
		}

		private void DrawTrackVelocityVector(Vector2 center, float markerSize, Vector3 worldPosition, Vector3 worldVelocity, float alpha)
		{
			float speed = worldVelocity.magnitude;
			if (speed < 0.5f || viewCamera == null)
			{
				return;
			}
			float probeDistance = Mathf.Clamp(Vector3.Distance(viewCamera.transform.position, worldPosition) * 0.08f, 20f, 500f);
			Vector3 projectedTip = viewCamera.WorldToScreenPoint(worldPosition + worldVelocity.normalized * probeDistance);
			if (!(projectedTip.z <= 0f))
			{
				Vector2 screenDirection = MechHudLayout.ScreenToHudPoint(projectedTip) - center;
				if (!(screenDirection.sqrMagnitude < 0.25f))
				{
					screenDirection.Normalize();
					float length = Mathf.Clamp(speed * velocityVectorPixelsPerMeterPerSecond, minimumVelocityVectorLength, maximumVelocityVectorLength);
					Vector2 vector = center + screenDirection * (markerSize * 0.5f + 4f);
					DrawLine(vector, vector + screenDirection * length, new Color(HudBrightGreen.r, HudBrightGreen.g, HudBrightGreen.b, Mathf.Clamp01(alpha)), velocityVectorThickness);
				}
			}
		}

		private void GetTrackReadout(in TrackSnapshot track, float closingVelocity, out float displayedClosing)
		{
			int slot = track.TrackId.Value.Index;
			if ((uint)slot >= trackReadoutKeys.Length)
			{
				displayedClosing = closingVelocity;
				return;
			}
			uint key = track.TrackId.Packed;
			float now = Time.unscaledTime;
			if (trackReadoutKeys[slot] != key)
			{
				trackReadoutKeys[slot] = key;
				displayedClosingVelocities[slot] = closingVelocity;
				nextTrackReadoutUpdateTimes[slot] = now + trackReadoutRefreshSeconds;
			}
			else if (now >= nextTrackReadoutUpdateTimes[slot])
			{
				displayedClosingVelocities[slot] = Mathf.Lerp(displayedClosingVelocities[slot], closingVelocity, trackReadoutSmoothing);
				nextTrackReadoutUpdateTimes[slot] = now + trackReadoutRefreshSeconds;
			}
			displayedClosing = displayedClosingVelocities[slot];
		}

		private void DrawFriendlyMissileMarkers()
		{
			int missileTrackCount = radar.CopyMissileTracks(missileTracks);
			IReadOnlyList<MechTestProjectile> projectiles = MechTestProjectile.Registry;
			float radius = Mathf.Max(3f, friendlyMissileMarkerRadius);
			for (int i = 0; i < projectiles.Count; i++)
			{
				MechTestProjectile projectile = projectiles[i];
				if (IsFriendlyEngagementMissile(projectile) &&
					!HasFriendlyMissileTrackNear(
						projectile.transform.position,
						missileTrackCount))
				{
					Vector3 screen = viewCamera.WorldToScreenPoint(projectile.transform.position);
					Vector2 point = MechHudLayout.ScreenToHudPoint(screen);
					if (!(screen.z <= 0f) && IsInsideScreen(point, radius))
					{
						DrawDiamond(point, radius, HudBrightGreen, dashed: false, 1.4f);
					}
				}
			}

			for (int i = 0; i < missileTrackCount; i++)
			{
				MissileTrackSnapshot track = missileTracks[i];
				if (!ShouldDisplayFriendlyMissile(in track))
				{
					continue;
				}

				Vector3 position = ToVector3(
					track.PredictPosition(radar.SimTimeSeconds));
				Vector3 screen = viewCamera.WorldToScreenPoint(position);
				Vector2 point = MechHudLayout.ScreenToHudPoint(screen);
				if (screen.z <= 0f || !IsInsideScreen(point, radius))
				{
					continue;
				}

				DrawDiamond(
					point,
					radius,
					HudBrightGreen,
					dashed: false,
					1.4f);
			}
		}

		private bool HasFriendlyMissileTrackNear(
			Vector3 position,
			int missileTrackCount)
		{
			for (var i = 0; i < missileTrackCount; i++)
			{
				var track = missileTracks[i];
				if (!ShouldDisplayFriendlyMissile(in track))
				{
					continue;
				}

				var trackPosition = ToVector3(
					track.PredictPosition(radar.SimTimeSeconds));
				var gate = Mathf.Max(
					50f,
					track.PositionUncertaintyMeters * 3f);
				if ((trackPosition - position).sqrMagnitude <= gate * gate)
				{
					return true;
				}
			}

			return false;
		}

		private void DrawIncomingMissileMarkersOnTop()
		{
			int depth = GUI.depth;
			GUI.depth = -1000;
			DrawIncomingMissileMarkers();
			GUI.depth = depth;
		}

		private void DrawIncomingMissileMarkers()
		{
			float radius = Mathf.Max(3f, friendlyMissileMarkerRadius);
			int missileTrackCount = radar.CopyMissileTracks(missileTracks);
			for (int i = 0; i < missileTrackCount; i++)
			{
				MissileTrackSnapshot track = missileTracks[i];
				if (!ShouldDisplayWorldMissileMarker(in track))
				{
					continue;
				}
				Vector3 position = ToVector3(track.PredictPosition(radar.SimTimeSeconds));
				bool confirmedOwnshipThreat =
					track.IsLikelyTargetingOwnship;
				bool identifiedAirToAir = track.IsLikelyAirToAir;
				bool emphasizedThreat =
					confirmedOwnshipThreat || identifiedAirToAir;
				bool inferredInterceptor = IsInferredInterceptor(in track);
				bool interceptorAssigned = HasFriendlyInterceptorAssigned(track.TrackId);
				bool interceptorSolutionAvailable =
					weaponSystem != null &&
					weaponSystem.HasInterceptorLaunchSolution &&
					weaponSystem.InterceptorTrackId.Equals(track.TrackId);
				float markerRadius =
					emphasizedThreat ? radius + 2f : radius;
				Color markerColor = emphasizedThreat
					? HudRed
					: HudAmber;
				Vector3 screen = viewCamera.WorldToScreenPoint(position);
				Vector2 markerCenter = MechHudLayout.ScreenToHudPoint(screen);
				if (screen.z <= 0f || !IsInsideScreen(markerCenter, markerRadius))
				{
					continue;
				}
				DrawDiamond(
					markerCenter,
					markerRadius,
					markerColor,
					dashed: false,
					emphasizedThreat ? 2.2f : 1.6f);
				bool shaoWeiTrackSelected = weaponSystem != null && weaponSystem.HasShaoWeiPointDefenseTrack && weaponSystem.ShaoWeiPointDefenseTrackId.Equals(track.TrackId);
				bool handPointDefenseTrackSelected = weaponSystem != null && weaponSystem.HasPointDefenseTrack && weaponSystem.PointDefenseTrackId.Equals(track.TrackId);
				if (interceptorAssigned)
				{
					float assignedExtent = markerRadius + 9f;
					DrawCornerFrame(new Rect(markerCenter.x - assignedExtent, markerCenter.y - assignedExtent, assignedExtent * 2f, assignedExtent * 2f), HudBrightGreen, 7f, 1.8f, dashed: false);
				}
				else if (interceptorSolutionAvailable)
				{
					float lockExtent2 = markerRadius + 8f;
					DrawCornerFrame(new Rect(markerCenter.x - lockExtent2, markerCenter.y - lockExtent2, lockExtent2 * 2f, lockExtent2 * 2f), HudAmber, 7f, 1.6f, dashed: false);
				}
				else if (shaoWeiTrackSelected || handPointDefenseTrackSelected)
				{
					Color pointDefenseColor = (shaoWeiTrackSelected && weaponSystem.ShaoWeiPointDefenseWithhold == MechTestWeaponSystem.PointDefenseWithholdReason.None) ? HudBrightGreen : HudAmber;
					float lockExtent = markerRadius + 13f;
					DrawCornerFrame(new Rect(markerCenter.x - lockExtent, markerCenter.y - lockExtent, lockExtent * 2f, lockExtent * 2f), pointDefenseColor, 8f, 1.8f, dashed: false);
				}
				string missileLabel = confirmedOwnshipThreat
					? "导弹来袭"
					: identifiedAirToAir
						? "导弹"
						: inferredInterceptor
							? "拦截弹"
							: string.Empty;
				if (interceptorAssigned)
				{
					missileLabel = string.IsNullOrEmpty(missileLabel)
						? "拦截中"
						: missileLabel + "  拦截中";
				}
				if (!string.IsNullOrEmpty(missileLabel))
				{
					Vector2 labelCenter = markerCenter + new Vector2(0f, markerRadius + 12f);
					smallStyle.alignment = TextAnchor.UpperCenter;
					smallStyle.normal.textColor = markerColor;
					float measuredLabelWidth = smallStyle.CalcSize(new GUIContent(missileLabel)).x + 12f;
					float labelWidth = Mathf.Min(
						Mathf.Max(72f, measuredLabelWidth),
						Mathf.Max(1f, MechHudLayout.Width - 8f));
					Rect position2 = ClampToScreen(new Rect(labelCenter.x - labelWidth * 0.5f, labelCenter.y, labelWidth, 24f), 4f);
					GUI.Label(position2, missileLabel, smallStyle);
				}
			}
		}

		private void DrawMissileAttackLinks()
		{
			IReadOnlyList<MechTestProjectile> projectiles = MechTestProjectile.Registry;
			float missileRadius = Mathf.Max(3f, friendlyMissileMarkerRadius);
			for (int i = 0; i < projectiles.Count; i++)
			{
				MechTestProjectile projectile = projectiles[i];
				if (!IsFriendlyEngagementMissile(projectile) || !TryGetTrackScreenPoint(projectile.GuidanceTrackId, out var targetPoint))
				{
					continue;
				}
				Vector3 missileScreen = viewCamera.WorldToScreenPoint(projectile.transform.position);
				Vector2 missilePoint = MechHudLayout.ScreenToHudPoint(missileScreen);
				if (!(missileScreen.z <= 0f) && IsInsideScreen(missilePoint, missileRadius) && IsInsideScreen(targetPoint, 31f))
				{
					Vector2 delta = targetPoint - missilePoint;
					float distance = delta.magnitude;
					if (!(distance <= missileRadius + 31f + 4f))
					{
						Vector2 direction = delta / distance;
						DrawLine(missilePoint + direction * (missileRadius + 3f), targetPoint - direction * 31f, HudMissileLink, 1f, dashed: true);
					}
				}
			}
		}

		private bool IsFriendlyEngagementMissile(MechTestProjectile projectile)
		{
			if (projectile != null && projectile.IsMissile && projectile.IsEngagementActive)
			{
				return projectile.IsOwnedBy(base.transform);
			}
			return false;
		}

		private bool ShouldDisplayFriendlyMissile(in MissileTrackSnapshot track)
		{
			return track.IsFriendly && !radar.IsRetiredFriendlyMissileTrack(track.TrackId);
		}

		private bool ShouldDisplayIncomingMissile(in MissileTrackSnapshot track)
		{
			if (!track.IsWarning || track.IsFriendly ||
				radar.IsRetiredFriendlyMissileTrack(track.TrackId))
			{
				return false;
			}
			return IsFreshMissileEvidence(in track);
		}

		private bool ShouldDisplayWorldMissileMarker(
			in MissileTrackSnapshot track)
		{
			return ShouldDisplayIncomingMissile(in track);
		}

        private static bool IsInferredInterceptor(
            in MissileTrackSnapshot track)
        {
            return track.IsLikelyInterceptor;
        }

		private bool HasFriendlyInterceptorAssigned(TrackId trackId)
		{
			if (!trackId.IsValid)
			{
				return false;
			}

			IReadOnlyList<MechTestProjectile> projectiles =
				MechTestProjectile.Registry;
			for (int i = 0; i < projectiles.Count; i++)
			{
				MechTestProjectile projectile = projectiles[i];
				if (projectile != null &&
					projectile.IsInterceptorMissile &&
					projectile.IsEngagementActive &&
					projectile.IsOwnedBy(base.transform) &&
					projectile.GuidanceTrackId.Equals(trackId))
				{
					return true;
				}
			}

			return false;
		}

		private bool IsFreshMissileEvidence(in MissileTrackSnapshot track)
		{
			double evidenceAge = radar.SimTimeSeconds - track.LastEvidenceTime;
			return evidenceAge >= -0.05 && evidenceAge <= 0.6;
		}

		private static bool IsInsideScreen(Vector2 point, float margin)
		{
			if (point.x >= margin && point.x <= MechHudLayout.Width - margin && point.y >= margin)
			{
				return point.y <= MechHudLayout.Height - margin;
			}
			return false;
		}

		private void DrawTransientEchoes()
		{
			double now = radar.SimTimeSeconds;
			for (int i = 0; i < echoCount; i++)
			{
				RadarEchoSnapshot echo = echoes[i];
				double age = now - echo.Timestamp;
				if (!(age < 0.0) && !(age > 0.25) && !IsEchoAlreadyTracked(in echo))
				{
					Vector3 screen = viewCamera.WorldToScreenPoint(ToVector3(echo.EstimatedPosition));
					if (!(screen.z <= 0f))
					{
						float alpha = (float)(1.0 - age / 0.25);
						Vector2 center = MechHudLayout.ScreenToHudPoint(screen);
						DrawLine(center + new Vector2(-4f, 0f), center + new Vector2(4f, 0f), new Color(HudGreen.r, HudGreen.g, HudGreen.b, alpha * 0.75f), 1f);
					}
				}
			}
		}

		private bool IsEchoAlreadyTracked(in RadarEchoSnapshot echo)
		{
			Vector3 echoPosition = ToVector3(echo.EstimatedPosition);
			float echoRange = Vector3.Distance(base.transform.position, echoPosition);
			float num = Mathf.Max(12f, echoRange * 0.0075f);
			float mergeDistanceSquared = num * num;
			for (int i = 0; i < trackCount; i++)
			{
				if (tracks[i].Lifecycle != TrackLifecycle.Dropped && (radar.PredictTrackPosition(in tracks[i]) - echoPosition).sqrMagnitude <= mergeDistanceSquared)
				{
					return true;
				}
			}
			return false;
		}

		private int CountDisplayTracks()
		{
			int count = 0;
			for (int i = 0; i < trackCount; i++)
			{
				if (ShouldDisplayTrack(i))
				{
					count++;
				}
			}
			return count;
		}

		private bool ShouldDisplayTrack(int index)
		{
			if ((uint)index >= (uint)trackCount || !IsTrackPresentationEligible(index))
			{
				return false;
			}
			TrackSnapshot track = tracks[index];
			Vector3 position = radar.PredictTrackPosition(in track);
			float range = Vector3.Distance(base.transform.position, position);
			float num = Mathf.Max(30f, range * 0.004f);
			float mergeDistanceSquared = num * num;
			Vector3 velocity = ToVector3(track.Velocity);
			float score = DisplayTrackScore(in track);
			for (int otherIndex = 0; otherIndex < trackCount; otherIndex++)
			{
				if (otherIndex == index || !IsTrackPresentationEligible(otherIndex))
				{
					continue;
				}
				TrackSnapshot other = tracks[otherIndex];
				if (!((radar.PredictTrackPosition(in other) - position).sqrMagnitude > mergeDistanceSquared) && !((ToVector3(other.Velocity) - velocity).sqrMagnitude > 6400f))
				{
					float otherScore = DisplayTrackScore(in other);
					if (otherScore > score || (Mathf.Approximately(otherScore, score) && otherIndex < index))
					{
						return false;
					}
				}
			}
			return true;
		}

		private bool IsTrackPresentationEligible(int index)
		{
			TrackSnapshot track = tracks[index];
			if (radar.IsRetiredFriendlyMissileTrack(track.TrackId) ||
				track.ObjectClass == TrackObjectClass.Missile)
			{
				return false;
			}
			if (radar.TryGetMissileTrack(track.TrackId, out var missileTrack) &&
				missileTrack.IsWarning &&
				IsFreshMissileEvidence(in missileTrack))
			{
				return false;
			}
			if (track.TrackId.Equals(radar.SttTrackId) || track.TrackId.Equals(radar.CandidateTrackId))
			{
				return true;
			}
			return track.Lifecycle switch
			{
				TrackLifecycle.Confirmed => track.Confidence >= 0.35f, 
				TrackLifecycle.Tentative => track.HitCount >= 2 && track.Confidence >= 0.4f, 
				TrackLifecycle.Coasting => track.CoastCount <= 4 && track.Confidence >= 0.35f, 
				_ => false, 
			};
		}

		private float DisplayTrackScore(in TrackSnapshot track)
		{
			float score = track.Lifecycle switch
			{
				TrackLifecycle.Confirmed => 400f, 
				TrackLifecycle.Coasting => 300f, 
				TrackLifecycle.Degraded => 200f, 
				TrackLifecycle.Tentative => 100f, 
				_ => 0f, 
			};
			if (track.TrackId.Equals(radar.SttTrackId))
			{
				score += 10000f;
			}
			else if (track.TrackId.Equals(radar.CandidateTrackId))
			{
				score += 5000f;
			}
			return score + track.Confidence * 100f + (float)(track.LastUpdateTime * 0.001);
		}

		private float GetTacticalDisplayRange()
		{
			float farthest = 0f;
			for (int i = 0; i < trackCount; i++)
			{
				if (ShouldDisplayTrack(i))
				{
					Vector3 relative = radar.PredictTrackPosition(in tracks[i]) - base.transform.position;
					float horizontalRange = new Vector2(relative.x, relative.z).magnitude;
					farthest = Mathf.Max(farthest, horizontalRange);
				}
			}
			float required = farthest * 1.25f;
			if (required <= 2000f)
			{
				return 2000f;
			}
			if (required <= 5000f)
			{
				return 5000f;
			}
			if (required <= 10000f)
			{
				return 10000f;
			}
			if (required <= 25000f)
			{
				return 25000f;
			}
			if (required <= 50000f)
			{
				return 50000f;
			}
			return Mathf.Max(100000f, radarStatus.RangeMeters);
		}

		private float GetRadarDisplayRange(float halfAngle)
		{
			float farthest = 0f;
			for (int i = 0; i < trackCount; i++)
			{
				if (ShouldDisplayTrack(i))
				{
					Vector3 relative = ToVector3(tracks[i].Position) - base.transform.position;
					Vector3 local = ToHeadingLocal(relative);
					Vector2 horizontal = new Vector2(local.x, local.z);
					if (!(horizontal.sqrMagnitude < 0.0001f) && !(horizontal.y <= 0f) && Mathf.Abs(Mathf.Atan2(horizontal.x, horizontal.y) * 57.29578f) <= halfAngle)
					{
						farthest = Mathf.Max(farthest, horizontal.magnitude);
					}
				}
			}
			float maximumRange = Mathf.Max(5000f, radarStatus.RangeMeters);
			radarDisplayRangeMeters = Mathf.Clamp(radarDisplayRangeMeters, 5000f, maximumRange);
			if (farthest <= 0f)
			{
				return radarDisplayRangeMeters;
			}
			float requiredRange = farthest * 1.25f;
			float selectedRange = SelectRadarDisplayRange(requiredRange, maximumRange);
			if (selectedRange > radarDisplayRangeMeters && requiredRange > radarDisplayRangeMeters * 0.88f)
			{
				radarDisplayRangeMeters = selectedRange;
			}
			else if (selectedRange < radarDisplayRangeMeters && requiredRange < radarDisplayRangeMeters * 0.4f)
			{
				radarDisplayRangeMeters = selectedRange;
			}
			return radarDisplayRangeMeters;
		}

		private static float SelectRadarDisplayRange(float requiredRange, float maximumRange)
		{
			if (requiredRange <= 5000f)
			{
				return 5000f;
			}
			if (requiredRange <= 10000f)
			{
				return Mathf.Min(10000f, maximumRange);
			}
			if (requiredRange <= 20000f)
			{
				return Mathf.Min(20000f, maximumRange);
			}
			if (requiredRange <= 40000f)
			{
				return Mathf.Min(40000f, maximumRange);
			}
			if (requiredRange <= 80000f)
			{
				return Mathf.Min(80000f, maximumRange);
			}
			return maximumRange;
		}

		private Vector3 ToHeadingLocal(Vector3 worldVector)
		{
			Vector3 up = GetReferenceUp();
			Vector3 forward = GetHeadingForward();
			Vector3 right = Vector3.Cross(up, forward).normalized;
			return new Vector3(Vector3.Dot(worldVector, right), Vector3.Dot(worldVector, up), Vector3.Dot(worldVector, forward));
		}

		private Vector3 GetHeadingForward()
		{
			Vector3 up = GetReferenceUp();
			Vector3 forward = GetBodyForwardWorld();
			forward = Vector3.ProjectOnPlane(forward, up);
			if (!(forward.sqrMagnitude > 0.001f))
			{
				if (!(motion != null))
				{
					return Vector3.forward;
				}
				return motion.ReferenceRotation * Vector3.forward;
			}
			return forward.normalized;
		}

		private float GetCompassHeadingDegrees()
		{
			Vector3 referenceLocalForward = Quaternion.Inverse((motion != null) ? motion.ReferenceRotation : Quaternion.identity) * GetHeadingForward();
			return Mathf.Repeat(Mathf.Atan2(referenceLocalForward.x, referenceLocalForward.z) * 57.29578f, 360f);
		}

		private static string FormatHeading(float headingDegrees)
		{
			return (Mathf.RoundToInt(Mathf.Repeat(headingDegrees, 360f)) % 360).ToString("000");
		}

		private Vector3 GetReferenceUp()
		{
			if (!(motion != null))
			{
				return Vector3.up;
			}
			return motion.ReferenceUp;
		}

		private static string FormatRange(float rangeMeters)
		{
			if (!(rangeMeters < 10000f))
			{
				return $"{rangeMeters / 1000f:0}K";
			}
			return $"{rangeMeters / 1000f:0.0}K";
		}

		private void DrawLegacyReticle()
		{
			Vector2 center = GetReticleGuiPosition();
			DrawLine(center + new Vector2(-11f, 0f), center + new Vector2(-3f, 0f), HudGreen, 1.4f);
			DrawLine(center + new Vector2(11f, 0f), center + new Vector2(3f, 0f), HudGreen, 1.4f);
			DrawLine(center + new Vector2(0f, -11f), center + new Vector2(0f, -3f), HudGreen, 1.4f);
			DrawLine(center + new Vector2(0f, 11f), center + new Vector2(0f, 3f), HudGreen, 1.4f);
		}

		private Vector2 GetReticleGuiPosition()
		{
			Vector2 viewportPoint = GetAimViewportPoint();
			Vector3 screen = viewCamera.ViewportToScreenPoint(new Vector3(viewportPoint.x, viewportPoint.y, 0f));
			return MechHudLayout.ScreenToHudPoint(screen);
		}

		private Vector2 GetBodyForwardGuiPosition(float ringRadius)
		{
			if (viewCamera == null)
			{
				return new Vector2(MechHudLayout.Width * 0.5f, MechHudLayout.Height * 0.5f);
			}

			Vector3 bodyForward = GetBodyForwardWorld();
			Vector3 screen = viewCamera.WorldToScreenPoint(
				viewCamera.transform.position + bodyForward * 1000f);
			if (screen.z <= 0f)
			{
				return new Vector2(MechHudLayout.Width * 0.5f, MechHudLayout.Height * 0.5f);
			}

			Vector2 point = MechHudLayout.ScreenToHudPoint(screen);
			float visibleMargin = Mathf.Clamp(ringRadius * 0.12f, 24f, 64f);
			point.x = Mathf.Clamp(point.x, visibleMargin, MechHudLayout.Width - visibleMargin);
			point.y = Mathf.Clamp(point.y, visibleMargin, MechHudLayout.Height - visibleMargin);
			return point;
		}

		private Vector3 GetBodyForwardWorld()
		{
			Vector3 forward = motion != null
				? motion.LogicalRotation * Vector3.forward
				: base.transform.forward;
			return forward.sqrMagnitude > 0.001f
				? forward.normalized
				: Vector3.forward;
		}

		private Vector2 GetAimViewportPoint()
		{
			if (aimController == null && viewCamera != null)
			{
				aimController = viewCamera.GetComponent<MechThirdPersonCamera>();
			}
			Vector2 point = ((aimController != null) ? aimController.AimViewportPoint : aimViewportPoint);
			return new Vector2(Mathf.Clamp(point.x, 0.05f, 0.95f), Mathf.Clamp(point.y, 0.05f, 0.95f));
		}

		private bool TryGetTrackScreenPoint(TrackId trackId, out Vector2 point)
		{
			for (int i = 0; i < trackCount; i++)
			{
				if (tracks[i].TrackId.Equals(trackId))
				{
					Vector3 screen = viewCamera.WorldToScreenPoint(radar.PredictTrackPosition(in tracks[i]));
					if (screen.z > 0f)
					{
						point = MechHudLayout.ScreenToHudPoint(screen);
						return true;
					}
				}
			}
			point = default(Vector2);
			return false;
		}

		private static bool WasPressed(Keyboard keyboard, Key key)
		{
			return keyboard[key]?.wasPressedThisFrame ?? false;
		}

		private static float GetLifecycleAlpha(in TrackSnapshot track)
		{
			return track.Lifecycle switch
			{
				TrackLifecycle.Coasting => Mathf.Clamp(0.72f - (float)track.CoastCount * 0.08f, 0.2f, 0.72f), 
				TrackLifecycle.Degraded => Mathf.Clamp(0.5f - (float)track.CoastCount * 0.05f, 0.14f, 0.5f), 
				_ => 0.95f, 
			};
		}

		private static string LifecycleLabel(TrackLifecycle lifecycle)
		{
			return lifecycle switch
			{
				TrackLifecycle.Tentative => "INIT", 
				TrackLifecycle.Confirmed => "TWS", 
				TrackLifecycle.Coasting => "COAST", 
				TrackLifecycle.Degraded => "DEGRADED", 
				_ => "LOST", 
			};
		}

		private static string SourceLabel(TrackSourceMask source)
		{
			return (int)(source & (TrackSourceMask.Radar | TrackSourceMask.ElectroOptical | TrackSourceMask.Esm | TrackSourceMask.DataLink)) switch
			{
				0 => "-", 
				1 => "R", 
				2 => "O", 
				3 => "R/O", 
				4 => "E", 
				5 => "R/E", 
				6 => "O/E", 
				7 => "R/O/E", 
				8 => "D", 
				9 => "R/D", 
				10 => "O/D", 
				11 => "R/O/D", 
				12 => "E/D", 
				13 => "R/E/D", 
				14 => "O/E/D", 
				_ => "R/O/E/D", 
			};
		}

		private static string AffiliationLabel(TrackAffiliation affiliation)
		{
			return affiliation switch
			{
				TrackAffiliation.Friendly => "FRIEND", 
				TrackAffiliation.Hostile => "HOSTILE", 
				_ => "UNK", 
			};
		}

		private static string TrackLabel(TrackId id)
		{
			return $"T{id.Value.Index:00}";
		}

		private void EnsureStyles()
		{
			if (smallStyle == null)
			{
				smallStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 14,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.UpperLeft,
					wordWrap = false,
					richText = false
				};
			}
			if (weaponStatusStyle == null)
			{
				weaponStatusStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 16,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleLeft,
					wordWrap = false,
					richText = false
				};
			}
			if (statusStyle == null)
			{
				statusStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 14,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.UpperLeft
				};
			}
			if (microStyle == null)
			{
				microStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 11,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.UpperLeft,
					wordWrap = false
				};
			}
			if (compassStyle == null)
			{
				compassStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 13,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleCenter,
					wordWrap = false
				};
			}
			if (pitchStyle == null)
			{
				pitchStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 15,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleLeft,
					wordWrap = false
				};
			}
			if (zoomStyle == null)
			{
				zoomStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 19,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleCenter,
					wordWrap = false
				};
			}
			if (hitConfirmationStyle == null)
			{
				hitConfirmationStyle = new GUIStyle(GUI.skin.label)
				{
					fontSize = 24,
					fontStyle = FontStyle.Bold,
					alignment = TextAnchor.MiddleCenter,
					wordWrap = false,
					richText = false
				};
			}
		}

		private static Rect ClampToScreen(Rect rect, float margin)
		{
			rect.x = Mathf.Clamp(rect.x, margin, Mathf.Max(margin, MechHudLayout.Width - rect.width - margin));
			rect.y = Mathf.Clamp(rect.y, margin, Mathf.Max(margin, MechHudLayout.Height - rect.height - margin));
			return rect;
		}

		private static Rect ClampToRect(Rect rect, Rect bounds, float margin)
		{
			rect.x = Mathf.Clamp(rect.x, bounds.xMin + margin, Mathf.Max(bounds.xMin + margin, bounds.xMax - rect.width - margin));
			rect.y = Mathf.Clamp(rect.y, bounds.yMin + margin, Mathf.Max(bounds.yMin + margin, bounds.yMax - rect.height - margin));
			return rect;
		}

		private static Vector2 RadarDirection(float angleDegrees)
		{
			float radians = angleDegrees * (MathF.PI / 180f);
			return new Vector2(Mathf.Sin(radians), 0f - Mathf.Cos(radians));
		}

		private static void DrawFanArc(Vector2 center, float radius, float startDegrees, float endDegrees, Color color, float thickness)
		{
			Vector2 previous = center + RadarDirection(startDegrees) * radius;
			for (int i = 1; i <= 36; i++)
			{
				float angle = Mathf.Lerp(startDegrees, endDegrees, (float)i / 36f);
				Vector2 current = center + RadarDirection(angle) * radius;
				DrawLine(previous, current, color, thickness);
				previous = current;
			}
		}

		private static void DrawAltitudeCue(Vector2 center, bool above, Color color)
		{
			float sign = (above ? (-1f) : 1f);
			DrawLine(center + new Vector2(-3f, 3f * sign), center + new Vector2(0f, 0f), color, 1f);
			DrawLine(center + new Vector2(3f, 3f * sign), center + new Vector2(0f, 0f), color, 1f);
		}

		private static void DrawArrowHead(Vector2 tip, Vector2 forward, Color color, float thickness)
		{
			Vector2 side = new Vector2(0f - forward.y, forward.x);
			Vector2 basePoint = tip - forward * 5f;
			DrawLine(tip, basePoint + side * 3f, color, thickness);
			DrawLine(tip, basePoint - side * 3f, color, thickness);
		}

		private static void DrawDiamond(Vector2 center, float radius, Color color, bool dashed, float thickness = 1f)
		{
			Vector2 a = center + new Vector2(0f, 0f - radius);
			Vector2 b = center + new Vector2(radius, 0f);
			Vector2 c = center + new Vector2(0f, radius);
			Vector2 d = center + new Vector2(0f - radius, 0f);
			DrawLine(a, b, color, thickness, dashed);
			DrawLine(b, c, color, thickness, dashed);
			DrawLine(c, d, color, thickness, dashed);
			DrawLine(d, a, color, thickness, dashed);
		}

		private static void DrawTrackSymbol(Vector2 center, float radius, Color color, bool dashed, TrackAffiliation affiliation)
		{
			switch (affiliation)
			{
			case TrackAffiliation.Friendly:
				DrawCircle(center, radius, color, 1f, dashed);
				break;
			case TrackAffiliation.Hostile:
			{
				Vector2 top = center + new Vector2(0f, 0f - radius);
				Vector2 left = center + new Vector2(0f - radius, radius);
				Vector2 right = center + new Vector2(radius, radius);
				DrawLine(top, left, color, 1f, dashed);
				DrawLine(left, right, color, 1f, dashed);
				DrawLine(right, top, color, 1f, dashed);
				break;
			}
			default:
				DrawDiamond(center, radius, color, dashed);
				break;
			}
		}

		private static void DrawCircle(Vector2 center, float radius, Color color, float thickness, bool dashed)
		{
			Vector2 previous = center + new Vector2(0f, 0f - radius);
			for (int i = 1; i <= 256; i++)
			{
				float angle = (float)i * MathF.PI * 2f / 256f;
				Vector2 current = center + new Vector2(Mathf.Sin(angle), 0f - Mathf.Cos(angle)) * radius;
				if (!dashed || i / 3 % 2 == 0)
				{
					DrawLine(previous, current, color, thickness);
				}
				previous = current;
			}
		}

		private void DrawSmoothTrackOperationRing(Vector2 center, float radius)
		{
			EnsureOperationRingTexture();
			float textureHalfExtent = radius / 0.92f;
			float size = textureHalfExtent * 2f;
			GUI.DrawTexture(new Rect(center.x - textureHalfExtent, center.y - textureHalfExtent, size, size), operationRingTexture, ScaleMode.StretchToFill, alphaBlend: true);
		}

		private void EnsureTacticalBackgroundTexture()
		{
			if (tacticalBackgroundTexture != null)
			{
				return;
			}
			Color32[] pixels = new Color32[1048576];
			float center = 511.5f;
			float outerRadius = center - 5f;
			float discRadius = outerRadius + 3.5f;
			Color backgroundColor = new Color(0f, 0.008f, 0.002f, 0.78f);
			for (int y = 0; y < 1024; y++)
			{
				float dy = (float)y - center;
				for (int x = 0; x < 1024; x++)
				{
					float num = (float)x - center;
					float distance = Mathf.Sqrt(num * num + dy * dy);
					float discAlpha = Mathf.Clamp01(discRadius - distance + 0.7f);
					float outerRingAlpha = Mathf.Clamp01(4.2f - Mathf.Abs(distance - outerRadius));
					if (discAlpha <= 0f && outerRingAlpha <= 0f)
					{
						continue;
					}
					Color color = backgroundColor;
					color.a *= discAlpha;
					for (int ringIndex = 1; ringIndex <= 3; ringIndex++)
					{
						float ringRadius = outerRadius * (float)ringIndex * 0.25f;
						float ringAlpha = Mathf.Clamp01(2.9f - Mathf.Abs(distance - ringRadius));
						if (!(ringAlpha <= 0f))
						{
							color = Color.Lerp(color, HudDimGreen, ringAlpha);
							color.a = Mathf.Max(color.a, HudDimGreen.a * ringAlpha);
						}
					}
					if (outerRingAlpha > 0f)
					{
						color = Color.Lerp(color, HudGreen, outerRingAlpha);
						color.a = Mathf.Max(color.a, HudGreen.a * outerRingAlpha);
					}
					pixels[y * 1024 + x] = color;
				}
			}
			tacticalBackgroundTexture = new Texture2D(1024, 1024, TextureFormat.RGBA32, mipChain: false, linear: true)
			{
				name = "Tactical Plot Disc And Rings AA",
				filterMode = FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave
			};
			tacticalBackgroundTexture.SetPixels32(pixels);
			tacticalBackgroundTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		}

		private void EnsureOperationRingTexture()
		{
			if (operationRingTexture != null)
			{
				return;
			}
			Color32[] pixels = new Color32[4194304];
			float center = 1023.5f;
			for (int y = 0; y < 2048; y++)
			{
				float dy = (float)y - center;
				for (int x = 0; x < 2048; x++)
				{
					float dx = (float)x - center;
					float radialDistance = Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - 942.08f);
					if (!(radialDistance > 6.7f) && !(Mathf.Abs(Mathf.Repeat(Mathf.Repeat(Mathf.Atan2(dx, 0f - dy) / (MathF.PI * 2f), 1f) * 32f + 0.5f, 1f) - 0.5f) > 0.31f))
					{
						float num = Mathf.Clamp01(6.2f - radialDistance) * 0.26f;
						float coreAlpha = Mathf.Clamp01(3.1f - radialDistance);
						float alpha = Mathf.Clamp01(num + coreAlpha * 0.92f);
						pixels[y * 2048 + x] = new Color(0.36f, 1f, 0.55f, alpha);
					}
				}
			}
			operationRingTexture = new Texture2D(2048, 2048, TextureFormat.RGBA32, mipChain: false, linear: true)
			{
				name = "RadarOperationRing_AA",
				filterMode = FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave
			};
			operationRingTexture.SetPixels32(pixels);
			operationRingTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
		}

		private static void DrawCornerFrame(Rect rect, Color color, float cornerLength, float thickness, bool dashed)
		{
			Vector2 vector = new Vector2(rect.xMin, rect.yMin);
			Vector2 topRight = new Vector2(rect.xMax, rect.yMin);
			Vector2 bottomLeft = new Vector2(rect.xMin, rect.yMax);
			Vector2 bottomRight = new Vector2(rect.xMax, rect.yMax);
			DrawLine(vector, vector + Vector2.right * cornerLength, color, thickness, dashed);
			DrawLine(vector, vector + new Vector2(0f, cornerLength), color, thickness, dashed);
			DrawLine(topRight, topRight + Vector2.left * cornerLength, color, thickness, dashed);
			DrawLine(topRight, topRight + new Vector2(0f, cornerLength), color, thickness, dashed);
			DrawLine(bottomLeft, bottomLeft + Vector2.right * cornerLength, color, thickness, dashed);
			DrawLine(bottomLeft, bottomLeft + new Vector2(0f, 0f - cornerLength), color, thickness, dashed);
			DrawLine(bottomRight, bottomRight + Vector2.left * cornerLength, color, thickness, dashed);
			DrawLine(bottomRight, bottomRight + new Vector2(0f, 0f - cornerLength), color, thickness, dashed);
		}

		private static void DrawFilledRect(Rect rect, Color color)
		{
			Color previousColor = GUI.color;
			GUI.color = MultiplyGuiTint(color, previousColor);
			GUI.DrawTexture(rect, Texture2D.whiteTexture);
			GUI.color = previousColor;
		}

		private static void EnsureSoftLineTexture()
		{
			if (!(softLineTexture != null))
			{
				softLineTexture = new Texture2D(1, 7, TextureFormat.RGBA32, mipChain: false, linear: true)
				{
					name = "HUD Soft Line",
					filterMode = FilterMode.Bilinear,
					wrapMode = TextureWrapMode.Clamp,
					hideFlags = HideFlags.HideAndDontSave
				};
				Color[] pixels = new Color[7]
				{
					new Color(1f, 1f, 1f, 0f),
					new Color(1f, 1f, 1f, 0.12f),
					new Color(1f, 1f, 1f, 0.62f),
					Color.white,
					new Color(1f, 1f, 1f, 0.62f),
					new Color(1f, 1f, 1f, 0.12f),
					new Color(1f, 1f, 1f, 0f)
				};
				softLineTexture.SetPixels(pixels);
				softLineTexture.Apply(updateMipmaps: false, makeNoLongerReadable: true);
			}
		}

		private static void DrawLine(Vector2 start, Vector2 end, Color color, float thickness, bool dashed = false)
		{
			Vector2 delta = end - start;
			float length = delta.magnitude;
			if (length < 0.01f)
			{
				return;
			}
			if (dashed && length > 5f)
			{
				Vector2 direction = delta / length;
				for (float cursor = 0f; cursor < length; cursor += 8f)
				{
					float segmentEnd = Mathf.Min(cursor + 4f, length);
					DrawSolidLine(start + direction * cursor, start + direction * segmentEnd, color, thickness);
				}
			}
			else
			{
				DrawSolidLine(start, end, color, thickness);
			}
		}

		private static void DrawSolidLine(Vector2 start, Vector2 end, Color color, float thickness)
		{
			Vector2 delta = end - start;
			float angle = Mathf.Atan2(delta.y, delta.x) * 57.29578f;
			Matrix4x4 matrix = GUI.matrix;
			Color previousColor = GUI.color;
			GUI.color = MultiplyGuiTint(color, previousColor);
			GUIUtility.RotateAroundPivot(angle, start);
			GUI.DrawTexture(new Rect(start.x, start.y - thickness * 0.5f - 1f, delta.magnitude, thickness + 2f), softLineTexture);
			GUI.matrix = matrix;
			GUI.color = previousColor;
		}

		private static Color MultiplyGuiTint(Color color, Color tint)
		{
			return new Color(color.r * tint.r, color.g * tint.g, color.b * tint.b, color.a * tint.a);
		}

		private static Vector3 ToVector3(double3 value)
		{
			return new Vector3((float)value.x, (float)value.y, (float)value.z);
		}
	}
}
