using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Truth-free radar presentation and target designation layer. It consumes only public radar,
    /// echo and track snapshots supplied by <see cref="MechRadarController"/>.
    /// </summary>
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

        [SerializeField] private MechRadarController radar;
        [SerializeField] private Camera viewCamera;
        [SerializeField] private Vector2 aimViewportPoint = new Vector2(0.5f, 0.56f);
        [Header("Rebindable radar controls")]
        [SerializeField] private Key operationModeKey = Key.Tab;
        [SerializeField] private Key breakLockKey = Key.T;
        [SerializeField] private Key cycleTargetKey = Key.G;
        [Header("Radar operator audio")]
        [Tooltip("Tab 雷达操作界面开启且未进入 STT 时循环播放。")]
        [SerializeField] private AudioClip operationScanLoopClip;
        [SerializeField, Range(0f, 1f)] private float operationScanLoopVolume = 0.0578f;
        [Tooltip("Tab 雷达操作界面开启且进入 STT 时替换扫描底噪循环播放。")]
        [SerializeField] private AudioClip sttLockLoopClip;
        [SerializeField, Range(0f, 1f)] private float sttLockLoopVolume = 0.10115f;
        [Header("Friendly missile cue")]
        [SerializeField, Min(3f)] private float friendlyMissileMarkerRadius = 7f;
        [Header("Track readout")]
        [SerializeField, Min(0.1f)] private float trackReadoutRefreshSeconds = 0.25f;
        [SerializeField, Range(0.05f, 1f)] private float trackReadoutSmoothing = 0.35f;
        [Header("Track velocity vector")]
        [SerializeField, Min(0.01f)] private float velocityVectorPixelsPerMeterPerSecond = 0.18f;
        [SerializeField, Min(1f)] private float minimumVelocityVectorLength = 8f;
        [SerializeField, Min(1f)] private float maximumVelocityVectorLength = 110f;
        [SerializeField, Min(0.5f)] private float velocityVectorThickness = 2f;

        private readonly TrackSnapshot[] tracks =
            new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly MissileTrackSnapshot[] missileTracks =
            new MissileTrackSnapshot[
                MechRadarController.MaximumPublicTracks];
        private readonly RadarEchoSnapshot[] echoes =
            new RadarEchoSnapshot[MechRadarController.MaximumPublicEchoes];
        private int trackCount;
        private int displayTrackCount;
        private int echoCount;
        private readonly uint[] trackReadoutKeys =
            new uint[MechRadarController.MaximumPublicTracks];
        private readonly float[] displayedClosingVelocities =
            new float[MechRadarController.MaximumPublicTracks];
        private readonly float[] nextTrackReadoutUpdateTimes =
            new float[MechRadarController.MaximumPublicTracks];
        private float radarDisplayRangeMeters = 20_000f;
        private RadarStatusSnapshot radarStatus;
        private MechThirdPersonCamera aimController;
        private MechFlightMotionController motion;
        private MechTestWeaponSystem weaponSystem;
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

        public bool IsTrackOperationMode => radar != null && radar.TrackOperationMode;

        public void Configure(
            Camera newViewCamera,
            MechRadarController newRadar,
            Vector2 newAimViewportPoint)
        {
            viewCamera = newViewCamera;
            aimController = viewCamera != null
                ? viewCamera.GetComponent<MechThirdPersonCamera>()
                : null;
            radar = newRadar;
            motion = radar != null
                ? radar.GetComponent<MechFlightMotionController>()
                : null;
            weaponSystem = radar != null
                ? radar.GetComponent<MechTestWeaponSystem>()
                : GetComponent<MechTestWeaponSystem>();
            aimViewportPoint = new Vector2(
                Mathf.Clamp(newAimViewportPoint.x, 0.05f, 0.95f),
                Mathf.Clamp(newAimViewportPoint.y, 0.05f, 0.95f));
        }

        private void Awake()
        {
            if (radar == null) radar = GetComponent<MechRadarController>();
            motion = radar != null
                ? radar.GetComponent<MechFlightMotionController>()
                : GetComponent<MechFlightMotionController>();
            weaponSystem = GetComponent<MechTestWeaponSystem>();
            if (viewCamera == null) viewCamera = Camera.main;
            if (viewCamera != null)
            {
                aimController = viewCamera.GetComponent<MechThirdPersonCamera>();
            }

            if (operationScanLoopClip == null)
            {
                operationScanLoopClip = Resources.Load<AudioClip>(
                    "Audio/Radar/radar_operation_scan_loop");
            }
            if (sttLockLoopClip == null)
            {
                sttLockLoopClip = Resources.Load<AudioClip>(
                    "Audio/Radar/radar_stt_lock_loop");
            }
            operationScanLoopClip?.LoadAudioData();
            sttLockLoopClip?.LoadAudioData();

            radarOperatorAudioSource = gameObject.AddComponent<AudioSource>();
            radarOperatorAudioSource.playOnAwake = false;
            radarOperatorAudioSource.loop = true;
            radarOperatorAudioSource.spatialBlend = 0f;
        }

        private void OnDisable()
        {
            radarOperatorAudioSource?.Stop();
            radar?.SetTrackOperationMode(false);
        }

        private void OnDestroy()
        {
            if (operationRingTexture != null)
            {
                Destroy(operationRingTexture);
                operationRingTexture = null;
            }
            if (tacticalBackgroundTexture != null)
            {
                Destroy(tacticalBackgroundTexture);
                tacticalBackgroundTexture = null;
            }
        }

        private void Update()
        {
            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
                if (radar == null) return;
            }

            if (viewCamera == null) viewCamera = Camera.main;
            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (WasPressed(keyboard, operationModeKey))
                {
                    radar.SetTrackOperationMode(!radar.TrackOperationMode);
                }

                if (WasPressed(keyboard, breakLockKey))
                {
                    radar.BreakSingleTargetTrack();
                }

                if (WasPressed(keyboard, cycleTargetKey))
                {
                    radar.CycleCandidate(1);
                }
            }

            radar.UpdateDesignation(
                viewCamera,
                GetAimViewportPoint(),
                Time.unscaledDeltaTime);
            trackCount = radar.CopyTracks(tracks);
            displayTrackCount = CountDisplayTracks();
            echoCount = radar.CopyEchoes(echoes);
            radarStatus = radar.GetStatusSnapshot();
            UpdateRadarOperatorAudio();
        }

        private void UpdateRadarOperatorAudio()
        {
            if (radarOperatorAudioSource == null)
            {
                return;
            }

            AudioClip desiredClip = null;
            var desiredVolume = 0f;
            if (radar.TrackOperationMode)
            {
                if (radar.HasStt)
                {
                    desiredClip = sttLockLoopClip;
                    desiredVolume = sttLockLoopVolume;
                }
                else
                {
                    desiredClip = operationScanLoopClip;
                    desiredVolume = operationScanLoopVolume;
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
            if (radar == null || viewCamera == null)
            {
                return;
            }

            EnsureStyles();
            EnsureSoftLineTexture();
            DrawMissileAttackLinks();
            DrawTacticalPlot();
            DrawRadarStatus();
            DrawFriendlyMissileMarkers();
            if (radar.TrackOperationMode)
            {
                DrawTransientEchoes();
                DrawTrackOperationOverlay();
            }
            else
            {
                DrawLegacyReticle();
            }
            DrawWorldTrackMarkers();
            DrawWeaponStatus();
            DrawIncomingMissileMarkersOnTop();
        }

        private void DrawWeaponStatus()
        {
            if (weaponSystem == null)
            {
                return;
            }

            const float columnGap = 24f;
            const float rowSpacing = 36f;
            var panelWidth = Mathf.Min(560f, Screen.width - 32f);
            var entryWidth = (panelWidth - columnGap) * 0.5f;
            var panelX = Screen.width - panelWidth - 20f;
            var firstRowY = Mathf.Max(12f, Screen.height - 226f);

            var leftMissileRect = new Rect(
                panelX,
                firstRowY,
                entryWidth,
                30f);
            var cannonRect = new Rect(
                panelX,
                firstRowY + rowSpacing,
                entryWidth,
                30f);
            var rightMissileRect = new Rect(
                leftMissileRect.xMax + columnGap,
                firstRowY,
                entryWidth,
                30f);
            var rifleRect = new Rect(
                rightMissileRect.x,
                firstRowY + rowSpacing,
                entryWidth,
                30f);

            DrawWeaponAmmoEntry(
                leftMissileRect,
                "L MSL",
                weaponSystem.LeftMissileRoundsRemaining,
                weaponSystem.LeftMissileMagazineCapacity,
                weaponSystem.LeftMissileMagazinesRemaining,
                false,
                0f,
                string.Empty);
            DrawWeaponAmmoEntry(
                cannonRect,
                "L 57MM",
                weaponSystem.CannonRoundsRemaining,
                weaponSystem.CannonMagazineCapacity,
                weaponSystem.CannonMagazinesRemaining,
                weaponSystem.CannonReloading,
                weaponSystem.CannonReloadRemainingSeconds,
                "装");
            DrawWeaponAmmoEntry(
                rightMissileRect,
                "R MSL",
                weaponSystem.RightMissileRoundsRemaining,
                weaponSystem.RightMissileMagazineCapacity,
                weaponSystem.RightMissileMagazinesRemaining,
                false,
                0f,
                string.Empty);
            DrawWeaponAmmoEntry(
                rifleRect,
                "R 30MM",
                weaponSystem.RifleRoundsRemaining,
                weaponSystem.RifleMagazineCapacity,
                weaponSystem.RifleMagazinesRemaining,
                weaponSystem.RifleReloading,
                weaponSystem.RifleReloadRemainingSeconds,
                "换");
        }

        private void DrawWeaponAmmoEntry(
            Rect bounds,
            string weaponLabel,
            int roundsRemaining,
            int magazineCapacity,
            int magazinesRemaining,
            bool reloading,
            float reloadRemainingSeconds,
            string reloadLabel)
        {
            var empty = roundsRemaining <= 0 && magazinesRemaining <= 0;
            var lowRounds = !empty &&
                            roundsRemaining < magazineCapacity &&
                            roundsRemaining <= Mathf.Max(
                                1,
                                Mathf.CeilToInt(magazineCapacity * 0.25f));
            var statusColor = empty
                ? HudRed
                : reloading
                    ? HudAmber
                    : HudGreen;
            var roundsColor = empty
                ? HudRed
                : lowRounds
                    ? HudAmber
                    : HudGreen;
            var roundsText = $"{roundsRemaining:00}";
            var suffix = $"/{magazineCapacity:00} x{magazinesRemaining}";
            if (reloading)
            {
                suffix += $" {reloadLabel}{reloadRemainingSeconds:0.0}s";
            }

            var labelWidth = weaponStatusStyle.CalcSize(
                new GUIContent(weaponLabel + " ")).x;
            var roundsWidth = Mathf.Max(
                26f,
                weaponStatusStyle.CalcSize(
                    new GUIContent(roundsText)).x + 8f);
            var suffixWidth = weaponStatusStyle.CalcSize(
                new GUIContent(suffix)).x;
            var contentWidth = labelWidth + roundsWidth + suffixWidth;
            var contentX = bounds.center.x - contentWidth * 0.5f;
            var textY = bounds.y;

            weaponStatusStyle.alignment = TextAnchor.MiddleLeft;
            weaponStatusStyle.normal.textColor = statusColor;
            GUI.Label(
                new Rect(contentX, textY, labelWidth, bounds.height),
                weaponLabel,
                weaponStatusStyle);

            var roundsRect = new Rect(
                contentX + labelWidth,
                textY + 3f,
                roundsWidth,
                bounds.height - 6f);
            var topLeft = new Vector2(roundsRect.xMin, roundsRect.yMin);
            var topRight = new Vector2(roundsRect.xMax, roundsRect.yMin);
            var bottomRight = new Vector2(roundsRect.xMax, roundsRect.yMax);
            var bottomLeft = new Vector2(roundsRect.xMin, roundsRect.yMax);
            DrawLine(topLeft, topRight, roundsColor, 1.4f);
            DrawLine(topRight, bottomRight, roundsColor, 1.4f);
            DrawLine(bottomRight, bottomLeft, roundsColor, 1.4f);
            DrawLine(bottomLeft, topLeft, roundsColor, 1.4f);
            weaponStatusStyle.alignment = TextAnchor.MiddleCenter;
            weaponStatusStyle.normal.textColor = roundsColor;
            GUI.Label(roundsRect, roundsText, weaponStatusStyle);

            weaponStatusStyle.alignment = TextAnchor.MiddleLeft;
            weaponStatusStyle.normal.textColor = statusColor;
            GUI.Label(
                new Rect(
                    roundsRect.xMax,
                    textY,
                    suffixWidth,
                    bounds.height),
                suffix,
                weaponStatusStyle);
        }

        private void DrawTacticalPlot()
        {
            var plot = new Rect(18f, 18f, TacticalPlotWidth, TacticalPlotHeight);
            var center = new Vector2(plot.center.x, plot.center.y);
            var radius = 176f;
            EnsureTacticalBackgroundTexture();
            GUI.DrawTexture(
                new Rect(
                    center.x - radius,
                    center.y - radius,
                    radius * 2f,
                    radius * 2f),
                tacticalBackgroundTexture);

            var heading = GetCompassHeadingDegrees();
            compassStyle.normal.textColor = HudDimGreen;
            for (var cardinalHeading = 0; cardinalHeading < 360; cardinalHeading += 90)
            {
                var relativeBearing = Mathf.DeltaAngle(heading, cardinalHeading);
                var labelPosition =
                    center + RadarDirection(relativeBearing) * (radius + 14f);
                GUI.Label(
                    new Rect(
                        labelPosition.x - 24f,
                        labelPosition.y - 10f,
                        48f,
                        20f),
                    FormatHeading(cardinalHeading),
                    compassStyle);
            }

            // Heading-up ownship symbol.
            DrawLine(center + new Vector2(0f, -12f), center + new Vector2(-8f, 6f), HudBrightGreen, 2.4f);
            DrawLine(center + new Vector2(0f, -12f), center + new Vector2(8f, 6f), HudBrightGreen, 2.4f);
            DrawLine(center + new Vector2(-8f, 6f), center + new Vector2(8f, 6f), HudBrightGreen, 2.4f);

            var range = GetTacticalDisplayRange();
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped || !ShouldDisplayTrack(i))
                {
                    continue;
                }

                var relativeWorld = ToVector3(track.Position) - transform.position;
                var relativeLocal = ToHeadingLocal(relativeWorld);
                var horizontal = new Vector2(relativeLocal.x, relativeLocal.z);
                if (horizontal.sqrMagnitude < 0.0001f)
                {
                    continue;
                }

                var normalizedRange = Mathf.Clamp01(horizontal.magnitude / range);
                var point = center + new Vector2(horizontal.x, -horizontal.y).normalized *
                    radius * normalizedRange;
                var alpha = GetLifecycleAlpha(track);
                var color = track.TrackId.Equals(radar.SttTrackId)
                    ? new Color(HudRed.r, HudRed.g, HudRed.b, alpha)
                    : track.TrackId.Equals(radar.CandidateTrackId)
                        ? new Color(HudAmber.r, HudAmber.g, HudAmber.b, alpha)
                        : new Color(HudCyan.r, HudCyan.g, HudCyan.b, alpha);
                var dashed = track.Lifecycle == TrackLifecycle.Coasting ||
                             track.Lifecycle == TrackLifecycle.Degraded;
                DrawTrackSymbol(point, 6f, color, dashed, track.Affiliation);

                var velocityLocal = ToHeadingLocal(ToVector3(track.Velocity));
                var velocity2D = new Vector2(velocityLocal.x, -velocityLocal.z);
                if (velocity2D.sqrMagnitude > 0.01f)
                {
                    var vectorLength = Mathf.Clamp(velocity2D.magnitude * 0.04f, 8f, 30f);
                    DrawLine(point, point + velocity2D.normalized * vectorLength, color, 1.4f);
                }

                if (Mathf.Abs(relativeLocal.y) > 10f)
                {
                    DrawAltitudeCue(point + new Vector2(8f, 0f), relativeLocal.y > 0f, color);
                }

                microStyle.alignment = TextAnchor.UpperLeft;
                microStyle.normal.textColor = color;
                var rangeKilometers = horizontal.magnitude / 1000f;
                var altitudeDelta = relativeLocal.y;
                var labelOffset = point.x > center.x
                    ? new Vector2(9f, -12f)
                    : new Vector2(-76f, -12f);
                smallStyle.normal.textColor = color;
                var trackLabelRect = ClampToRect(
                    new Rect(point.x + labelOffset.x, point.y + labelOffset.y, 70f, 34f),
                    plot,
                    5f);
                GUI.Label(
                    trackLabelRect,
                    $"{TrackLabel(track.TrackId)} {SourceLabel(track.SourceMask)}\n" +
                    $"{rangeKilometers:0.0}K {altitudeDelta:+0;-0;0}",
                    microStyle);
            }

            DrawTacticalMissiles(center, radius, range);
        }

        private void DrawTacticalMissiles(
            Vector2 center,
            float radius,
            float displayRange)
        {
            var projectiles = MechTestProjectile.Registry;
            for (var i = 0; i < projectiles.Count; i++)
            {
                var projectile = projectiles[i];
                if (!IsFriendlyEngagementMissile(projectile))
                {
                    continue;
                }
                DrawTacticalMissile(
                    center,
                    radius,
                    displayRange,
                    projectile.transform.position,
                    projectile.Velocity,
                    HudBrightGreen);
            }

            var missileTrackCount =
                radar.CopyMissileTracks(missileTracks);
            for (var i = 0; i < missileTrackCount; i++)
            {
                var track = missileTracks[i];
                if (!ShouldDisplayIncomingMissile(in track))
                {
                    continue;
                }

                DrawTacticalMissile(
                    center,
                    radius,
                    displayRange,
                    ToVector3(track.PredictPosition(
                        radar.SimTimeSeconds)),
                    ToVector3(track.Velocity),
                    track.IsWeaponsQuality
                        ? HudRed
                        : HudAmber);
            }
        }

        private void DrawTacticalMissile(
            Vector2 center,
            float radius,
            float displayRange,
            Vector3 worldPosition,
            Vector3 worldVelocity,
            Color color)
        {
            var relativeWorld =
                worldPosition - transform.position;
            var relativeLocal = ToHeadingLocal(relativeWorld);
            var horizontal =
                new Vector2(relativeLocal.x, relativeLocal.z);
            if (horizontal.magnitude > displayRange)
            {
                return;
            }

            var normalizedRange =
                horizontal.magnitude / displayRange;
            var point = horizontal.sqrMagnitude > 0.0001f
                ? center +
                  new Vector2(horizontal.x, -horizontal.y)
                      .normalized *
                  radius * normalizedRange
                : center;
            DrawDiamond(point, 5f, color, false, 1.3f);

            var velocityLocal = ToHeadingLocal(worldVelocity);
            var heading =
                new Vector2(velocityLocal.x, -velocityLocal.z);
            if (heading.sqrMagnitude <= 0.01f)
            {
                return;
            }

            var direction = heading.normalized;
            DrawLine(
                point + direction * 7f,
                point + direction * 22f,
                color,
                1.3f);
        }

        private void DrawRadarStatus()
        {
            var rect = new Rect(
                Screen.width - RadarStatusWidth - 18f,
                Screen.height - RadarStatusHeight - 18f,
                RadarStatusWidth,
                RadarStatusHeight);
            var halfAngle = Mathf.Clamp(radarStatus.ScanAzimuthDegrees * 0.5f, 20f, 80f);
            var displayRange = GetRadarDisplayRange(halfAngle);

            var fanApex = new Vector2(rect.x + 240f, rect.y + 325f);
            var fanRadius = 240f;
            for (var angle = -halfAngle; angle <= halfAngle; angle += 1f)
            {
                DrawLine(
                    fanApex,
                    fanApex + RadarDirection(angle) * fanRadius,
                    HudFanFill,
                    8f);
            }

            DrawLine(
                fanApex,
                fanApex + RadarDirection(-halfAngle) * fanRadius,
                HudGreen,
                2f);
            DrawLine(
                fanApex,
                fanApex + RadarDirection(halfAngle) * fanRadius,
                HudGreen,
                2f);
            DrawFanArc(fanApex, fanRadius, -halfAngle, halfAngle, HudGreen, 2f);
            DrawFanArc(fanApex, fanRadius * 0.75f, -halfAngle, halfAngle, HudDimGreen, 1f);
            DrawFanArc(fanApex, fanRadius * 0.5f, -halfAngle, halfAngle, HudDimGreen, 1f);
            DrawFanArc(fanApex, fanRadius * 0.25f, -halfAngle, halfAngle, HudDimGreen, 1f);
            DrawLine(fanApex, fanApex + RadarDirection(0f) * fanRadius, HudDimGreen, 1f);
            DrawLine(
                fanApex,
                fanApex + RadarDirection(-halfAngle * 0.5f) * fanRadius,
                HudDimGreen,
                1f);
            DrawLine(
                fanApex,
                fanApex + RadarDirection(halfAngle * 0.5f) * fanRadius,
                HudDimGreen,
                1f);

            var beamAngle = Mathf.Clamp(radarStatus.BeamAzimuthDegrees, -halfAngle, halfAngle);
            var beamHalfWidth = Mathf.Max(1f, radarStatus.BeamWidthDegrees * 0.5f);
            var beamFill = new Color(
                HudBrightGreen.r,
                HudBrightGreen.g,
                HudBrightGreen.b,
                0.28f);
            for (var angle = beamAngle - beamHalfWidth;
                 angle <= beamAngle + beamHalfWidth;
                 angle += 1f)
            {
                DrawLine(
                    fanApex,
                    fanApex + RadarDirection(Mathf.Clamp(angle, -halfAngle, halfAngle)) * fanRadius,
                    beamFill,
                    4f);
            }

            DrawLine(
                fanApex,
                fanApex + RadarDirection(beamAngle) * fanRadius,
                HudBrightGreen,
                2.6f);
            DrawRadarFanTracks(fanApex, fanRadius, halfAngle, displayRange);

            var sttText = radarStatus.SttTrackId.IsValid
                ? TrackLabel(radarStatus.SttTrackId)
                : "--";

            microStyle.alignment = TextAnchor.MiddleCenter;
            microStyle.normal.textColor = HudWhite;
            GUI.Label(
                new Rect(fanApex.x - 54f, fanApex.y + 3f, 108f, 18f),
                $"{-halfAngle:0}  方位  +{halfAngle:0}",
                microStyle);
            DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 1f, displayRange);
            DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 0.75f, displayRange);
            DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 0.5f, displayRange);
            DrawRadarRangeLabel(fanApex, fanRadius, halfAngle, 0.25f, displayRange);

            var elevationX = rect.x + 442f;
            var elevationTop = rect.y + 72f;
            var elevationBottom = rect.y + 310f;
            DrawLine(
                new Vector2(elevationX, elevationTop),
                new Vector2(elevationX, elevationBottom),
                HudDimGreen,
                1.3f);
            for (var tick = 0; tick <= 6; tick++)
            {
                var y = Mathf.Lerp(elevationTop, elevationBottom, tick / 6f);
                DrawLine(
                    new Vector2(elevationX - 5f, y),
                    new Vector2(elevationX + 5f, y),
                    HudDimGreen,
                    1f);
            }

            var elevationHalf = Mathf.Max(1f, radarStatus.ScanElevationDegrees * 0.5f);
            var elevationNormalized = Mathf.InverseLerp(
                elevationHalf,
                -elevationHalf,
                Mathf.Clamp(radarStatus.BeamElevationDegrees, -elevationHalf, elevationHalf));
            var elevationY = Mathf.Lerp(elevationTop, elevationBottom, elevationNormalized);
            DrawLine(
                new Vector2(elevationX - 10f, elevationY),
                new Vector2(elevationX + 10f, elevationY),
                HudBrightGreen,
                2.5f);

            statusStyle.normal.textColor = HudGreen;
            GUI.Label(
                new Rect(rect.x + 458f, rect.y + 66f, 92f, 220f),
                $"航迹 {displayTrackCount}/{radarStatus.TrackCapacity}\n" +
                $"STT {sttText}\n\n" +
                $"负载 {radarStatus.DwellLoad * 100f:0}%\n" +
                $"制导 {radarStatus.GuidanceChannelsUsed}/" +
                $"{radarStatus.GuidanceChannelCapacity}",
                statusStyle);
        }

        private void DrawRadarFanTracks(
            Vector2 apex,
            float radius,
            float halfAngle,
            float displayRange)
        {
            var range = Mathf.Max(1f, displayRange);
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped || !ShouldDisplayTrack(i))
                {
                    continue;
                }

                var relativeWorld = ToVector3(track.Position) - transform.position;
                var relativeLocal = ToHeadingLocal(relativeWorld);
                var horizontal = new Vector2(relativeLocal.x, relativeLocal.z);
                if (horizontal.sqrMagnitude < 0.0001f || horizontal.y <= 0f)
                {
                    continue;
                }

                var azimuth = Mathf.Atan2(horizontal.x, horizontal.y) * Mathf.Rad2Deg;
                if (Mathf.Abs(azimuth) > halfAngle)
                {
                    continue;
                }

                var point = apex + RadarDirection(azimuth) * radius *
                    Mathf.Clamp01(horizontal.magnitude / range);
                var alpha = GetLifecycleAlpha(track);
                var color = track.TrackId.Equals(radar.SttTrackId)
                    ? new Color(HudRed.r, HudRed.g, HudRed.b, alpha)
                    : track.TrackId.Equals(radar.CandidateTrackId)
                        ? new Color(HudAmber.r, HudAmber.g, HudAmber.b, alpha)
                        : new Color(HudCyan.r, HudCyan.g, HudCyan.b, alpha);
                DrawTrackSymbol(
                    point,
                    6f,
                    color,
                    track.Lifecycle == TrackLifecycle.Coasting ||
                    track.Lifecycle == TrackLifecycle.Degraded,
                    track.Affiliation);

                var labelOnLeft = point.x > apex.x + radius * 0.25f;
                microStyle.alignment = labelOnLeft
                    ? TextAnchor.UpperRight
                    : TextAnchor.UpperLeft;
                microStyle.normal.textColor = color;
                GUI.Label(
                    new Rect(
                        labelOnLeft ? point.x - 96f : point.x + 8f,
                        point.y - 14f,
                        88f,
                        34f),
                    $"{TrackLabel(track.TrackId)}\n" +
                    $"{horizontal.magnitude / 1000f:0.0} 公里",
                    microStyle);
            }
        }

        private void DrawRadarRangeLabel(
            Vector2 apex,
            float radius,
            float halfAngle,
            float rangeFraction,
            float displayRange)
        {
            var labelAngle = -halfAngle + Mathf.Min(6f, halfAngle * 0.2f);
            var point = apex + RadarDirection(labelAngle) * radius * rangeFraction;
            microStyle.alignment = TextAnchor.MiddleRight;
            microStyle.normal.textColor = HudDimGreen;
            GUI.Label(
                new Rect(point.x - 58f, point.y - 9f, 52f, 18f),
                FormatRange(displayRange * rangeFraction),
                microStyle);
        }

        private void DrawTrackOperationOverlay()
        {
            var center = GetReticleGuiPosition();
            var ringCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var diameter = Mathf.Clamp(Screen.height * 0.75f, 320f, Screen.height * 0.8f);
            var ringRadius = diameter * 0.5f;
            DrawSmoothTrackOperationRing(ringCenter, ringRadius);
            for (var i = 0; i < 4; i++)
            {
                var angle = i * Mathf.PI * 0.5f;
                var direction = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle));
                if (Mathf.Abs(direction.x) > 0.5f)
                {
                    continue;
                }

                const float tickLength = 18f;
                const float tickGap = 10f;
                var tickStart = ringCenter + direction * (ringRadius + tickGap);
                var tickEnd = tickStart + direction * tickLength;
                DrawLine(
                    tickStart,
                    tickEnd,
                    new Color(0f, 0.08f, 0.025f, 0.8f),
                    4.2f);
                DrawLine(
                    tickStart,
                    tickEnd,
                    HudBrightGreen,
                    2.1f);
            }
            DrawSightElevationLadder(ringCenter, ringRadius);
            DrawZoomMagnification(ringCenter, ringRadius);

            var snappedCenter = center;
            if (radar.CandidateTrackId.IsValid &&
                TryGetTrackScreenPoint(radar.CandidateTrackId, out var candidateCenter))
            {
                snappedCenter = Vector2.Lerp(center, candidateCenter, 0.08f);
                var maxOffset = 6f;
                var offset = snappedCenter - center;
                if (offset.magnitude > maxOffset)
                {
                    snappedCenter = center + offset.normalized * maxOffset;
                }
            }

            DrawCircle(snappedCenter, 16f, HudBrightGreen, 1.4f, false);
            DrawLine(snappedCenter + new Vector2(-22f, 0f), snappedCenter + new Vector2(-11f, 0f), HudBrightGreen, 1.3f);
            DrawLine(snappedCenter + new Vector2(22f, 0f), snappedCenter + new Vector2(11f, 0f), HudBrightGreen, 1.3f);
            DrawLine(snappedCenter + new Vector2(0f, -22f), snappedCenter + new Vector2(0f, -11f), HudBrightGreen, 1.3f);
            DrawLine(snappedCenter + new Vector2(0f, 22f), snappedCenter + new Vector2(0f, 11f), HudBrightGreen, 1.3f);

            if (radar.CandidateTrackId.IsValid && !radar.HasStt)
            {
                var progressWidth = 86f * radar.DesignationProgress;
                DrawLine(
                    center + new Vector2(-43f, 31f),
                    center + new Vector2(-43f + progressWidth, 31f),
                    HudBrightGreen,
                    2f);
                smallStyle.alignment = TextAnchor.MiddleCenter;
                smallStyle.normal.textColor = HudBrightGreen;
                GUI.Label(new Rect(center.x - 70f, center.y + 36f, 140f, 20f), "DESIGNATE", smallStyle);
            }
        }

        private void DrawSightElevationLadder(Vector2 center, float radius)
        {
            var cameraForward = viewCamera.transform.forward.normalized;
            var referenceUp = GetReferenceUp();
            var sightElevationDegrees = Mathf.Asin(
                Mathf.Clamp(
                    Vector3.Dot(cameraForward, referenceUp),
                    -1f,
                    1f)) * Mathf.Rad2Deg;
            var verticalFovRadians =
                Mathf.Clamp(viewCamera.fieldOfView, 1f, 179f) * Mathf.Deg2Rad;
            var focalPixels = Screen.height * 0.5f /
                              Mathf.Tan(verticalFovRadians * 0.5f);
            var previousGuiMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(
                ResolvePitchLadderRollDegrees(cameraForward, referenceUp),
                center);

            const float tickGap = 10f;
            const float referenceLength = 44f;
            DrawMirroredPitchTick(
                center,
                radius,
                0f,
                tickGap,
                referenceLength,
                HudBrightGreen,
                2.1f,
                null);

            for (var pitchDegrees = -80; pitchDegrees <= 80; pitchDegrees += 5)
            {
                var relativeDegrees = pitchDegrees - sightElevationDegrees;
                if (Mathf.Abs(relativeDegrees) >= 85f)
                {
                    continue;
                }

                var yOffset = -Mathf.Tan(relativeDegrees * Mathf.Deg2Rad) *
                              focalPixels;
                if (Mathf.Abs(yOffset) > radius - 28f ||
                    Mathf.Abs(yOffset) < 3f)
                {
                    continue;
                }

                var major = pitchDegrees % 10 == 0;
                var zero = pitchDegrees == 0;
                var tickLength = zero ? 44f : major ? 34f : 22f;
                var color = zero
                    ? HudBrightGreen
                    : major
                        ? new Color(HudBrightGreen.r, HudBrightGreen.g, HudBrightGreen.b, 0.9f)
                        : new Color(HudDimGreen.r, HudDimGreen.g, HudDimGreen.b, 0.7f);
                var label = major
                    ? pitchDegrees == 0
                        ? "00"
                        : pitchDegrees > 0
                            ? $"+{pitchDegrees:00}"
                            : $"{pitchDegrees:00}"
                    : null;
                DrawMirroredPitchTick(
                    center,
                    radius,
                    yOffset,
                    tickGap,
                    tickLength,
                    color,
                    major ? 1.7f : 1.2f,
                    label);
            }

            GUI.matrix = previousGuiMatrix;
        }

        private void DrawZoomMagnification(Vector2 center, float radius)
        {
            if (aimController == null && viewCamera != null)
            {
                aimController =
                    viewCamera.GetComponent<MechThirdPersonCamera>();
            }

            if (aimController == null || !aimController.IsZoomActive)
            {
                return;
            }

            zoomStyle.normal.textColor = HudBrightGreen;
            GUI.Label(
                new Rect(
                    center.x + radius * 0.52f,
                    center.y - radius * 0.76f,
                    96f,
                    28f),
                $"{aimController.ZoomMagnification}X",
                zoomStyle);
        }

        private float ResolvePitchLadderRollDegrees(
            Vector3 cameraForward,
            Vector3 referenceUp)
        {
            var projectedUp = Vector3.ProjectOnPlane(
                referenceUp,
                cameraForward);
            if (projectedUp.sqrMagnitude <= 0.000001f)
            {
                return pitchLadderRollDegrees;
            }

            projectedUp.Normalize();
            pitchLadderRollDegrees = Mathf.Atan2(
                Vector3.Dot(projectedUp, viewCamera.transform.right),
                Vector3.Dot(projectedUp, viewCamera.transform.up)) *
                Mathf.Rad2Deg;
            return pitchLadderRollDegrees;
        }

        private void DrawMirroredPitchTick(
            Vector2 center,
            float radius,
            float yOffset,
            float gap,
            float length,
            Color color,
            float thickness,
            string label)
        {
            var halfChord = Mathf.Sqrt(
                Mathf.Max(0f, radius * radius - yOffset * yOffset));
            var y = center.y + yOffset;
            var leftStart = new Vector2(center.x - halfChord - gap, y);
            var leftEnd = leftStart + Vector2.left * length;
            var rightStart = new Vector2(center.x + halfChord + gap, y);
            var rightEnd = rightStart + Vector2.right * length;
            DrawLine(leftStart, leftEnd, color, thickness);
            DrawLine(rightStart, rightEnd, color, thickness);

            if (string.IsNullOrEmpty(label))
            {
                return;
            }

            pitchStyle.normal.textColor = color;
            pitchStyle.alignment = TextAnchor.MiddleRight;
            GUI.Label(
                new Rect(leftEnd.x - 56f, y - 11f, 48f, 22f),
                label,
                pitchStyle);
            pitchStyle.alignment = TextAnchor.MiddleLeft;
            GUI.Label(
                new Rect(rightEnd.x + 8f, y - 11f, 48f, 22f),
                label,
                pitchStyle);
        }

        private void DrawWorldTrackMarkers()
        {
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped || !ShouldDisplayTrack(i))
                {
                    continue;
                }

                var position = radar.PredictTrackPosition(track);
                var screen = viewCamera.WorldToScreenPoint(position);
                if (screen.z <= 0f)
                {
                    continue;
                }

                var center = new Vector2(screen.x, Screen.height - screen.y);
                var alpha = GetLifecycleAlpha(track);
                var isCandidate = track.TrackId.Equals(radar.CandidateTrackId);
                var isStt = track.TrackId.Equals(radar.SttTrackId);
                var isTentative = track.Lifecycle == TrackLifecycle.Tentative;
                var markerVisible = !isTentative ||
                                    isCandidate ||
                                    isStt ||
                                    Mathf.Repeat(Time.unscaledTime * 3f, 1f) <= 0.55f;

                var color = isStt
                    ? new Color(HudRed.r, HudRed.g, HudRed.b, alpha)
                    : isCandidate
                        ? new Color(HudAmber.r, HudAmber.g, HudAmber.b, alpha)
                        : new Color(HudGreen.r, HudGreen.g, HudGreen.b, alpha);
                var dashed = track.Lifecycle == TrackLifecycle.Coasting ||
                             track.Lifecycle == TrackLifecycle.Degraded;
                var size = isCandidate || isStt ? TrackMarkerSize + 12f : TrackMarkerSize;
                if (markerVisible)
                {
                    DrawCornerFrame(
                        new Rect(center.x - size * 0.5f, center.y - size * 0.5f, size, size),
                        color,
                        isTentative ? 7f : 10f,
                        isStt ? 2.8f : 1.7f,
                        dashed);
                    DrawTrackVelocityVector(
                        center,
                        size,
                        position,
                        ToVector3(track.Velocity),
                        alpha);
                }

                var relative = position - transform.position;
                var lineOfSight = relative.sqrMagnitude > 0.01f ? relative.normalized : transform.forward;
                var relativeVelocity =
                    ToVector3(track.Velocity - radarStatus.OwnshipVelocity);
                var closing = -Vector3.Dot(relativeVelocity, lineOfSight);
                var lifecycle = isStt ? "STT" : LifecycleLabel(track.Lifecycle);
                var classification = track.Classification >= 0.72f &&
                                     track.IdentityConfidence >= 0.7f
                    ? AffiliationLabel(track.Affiliation)
                    : "UNK";
                GetTrackReadout(
                    in track,
                    closing,
                    out var displayedClosing);
                smallStyle.normal.textColor = color;
                smallStyle.alignment = TextAnchor.MiddleCenter;
                var distanceRect = ClampToScreen(
                    new Rect(
                        center.x - 105f,
                        center.y - size * 0.5f - 25f,
                        210f,
                        22f),
                    8f);
                GUI.Label(
                    distanceRect,
                    $"{relative.magnitude / 1000f:0.00} km  " +
                    $"{displayedClosing:+0;-0;0} m/s",
                    smallStyle);

                var informationRect = ClampToScreen(
                    new Rect(
                        center.x - 100f,
                        center.y + size * 0.5f + 4f,
                        200f,
                        22f),
                    8f);
                GUI.Label(
                    informationRect,
                    $"{TrackLabel(track.TrackId)}  {lifecycle}  {classification}",
                    smallStyle);

            }
        }

        private void DrawTrackVelocityVector(
            Vector2 center,
            float markerSize,
            Vector3 worldPosition,
            Vector3 worldVelocity,
            float alpha)
        {
            var speed = worldVelocity.magnitude;
            if (speed < 0.5f || viewCamera == null)
            {
                return;
            }

            var probeDistance = Mathf.Clamp(
                Vector3.Distance(viewCamera.transform.position, worldPosition) * 0.08f,
                20f,
                500f);
            var projectedTip = viewCamera.WorldToScreenPoint(
                worldPosition + worldVelocity.normalized * probeDistance);
            if (projectedTip.z <= 0f)
            {
                return;
            }

            var screenDirection = new Vector2(
                projectedTip.x - center.x,
                Screen.height - projectedTip.y - center.y);
            if (screenDirection.sqrMagnitude < 0.25f)
            {
                return;
            }

            screenDirection.Normalize();
            var length = Mathf.Clamp(
                speed * velocityVectorPixelsPerMeterPerSecond,
                minimumVelocityVectorLength,
                maximumVelocityVectorLength);
            var start = center +
                        screenDirection * (markerSize * 0.5f + 4f);
            DrawLine(
                start,
                start + screenDirection * length,
                new Color(
                    HudBrightGreen.r,
                    HudBrightGreen.g,
                    HudBrightGreen.b,
                    Mathf.Clamp01(alpha)),
                velocityVectorThickness);
        }

        private void GetTrackReadout(
            in TrackSnapshot track,
            float closingVelocity,
            out float displayedClosing)
        {
            var slot = track.TrackId.Value.Index;
            if ((uint)slot >= trackReadoutKeys.Length)
            {
                displayedClosing = closingVelocity;
                return;
            }

            var key = track.TrackId.Packed;
            var now = Time.unscaledTime;
            if (trackReadoutKeys[slot] != key)
            {
                trackReadoutKeys[slot] = key;
                displayedClosingVelocities[slot] = closingVelocity;
                nextTrackReadoutUpdateTimes[slot] =
                    now + trackReadoutRefreshSeconds;
            }
            else if (now >= nextTrackReadoutUpdateTimes[slot])
            {
                displayedClosingVelocities[slot] = Mathf.Lerp(
                    displayedClosingVelocities[slot],
                    closingVelocity,
                    trackReadoutSmoothing);
                nextTrackReadoutUpdateTimes[slot] =
                    now + trackReadoutRefreshSeconds;
            }

            displayedClosing = displayedClosingVelocities[slot];
        }

        private void DrawFriendlyMissileMarkers()
        {
            var projectiles = MechTestProjectile.Registry;
            var radius = Mathf.Max(3f, friendlyMissileMarkerRadius);
            for (var i = 0; i < projectiles.Count; i++)
            {
                var projectile = projectiles[i];
                if (!IsFriendlyEngagementMissile(projectile))
                {
                    continue;
                }
                var screen = viewCamera.WorldToScreenPoint(
                    projectile.transform.position);
                if (screen.z <= 0f ||
                    screen.x < radius ||
                    screen.x > Screen.width - radius ||
                    screen.y < radius ||
                    screen.y > Screen.height - radius)
                {
                    continue;
                }

                var markerCenter =
                    new Vector2(screen.x, Screen.height - screen.y);
                DrawDiamond(
                    markerCenter,
                    radius,
                    HudBrightGreen,
                    false,
                    1.4f);
            }
        }

        private void DrawIncomingMissileMarkersOnTop()
        {
            var previousDepth = GUI.depth;
            GUI.depth = -1000;
            DrawIncomingMissileMarkers();
            GUI.depth = previousDepth;
        }

        private void DrawIncomingMissileMarkers()
        {
            var radius = Mathf.Max(3f, friendlyMissileMarkerRadius);
            var missileTrackCount =
                radar.CopyMissileTracks(missileTracks);
            for (var i = 0; i < missileTrackCount; i++)
            {
                var track = missileTracks[i];
                if (!ShouldDisplayIncomingMissile(in track))
                {
                    continue;
                }

                var position = ToVector3(
                    track.PredictPosition(
                        radar.SimTimeSeconds));
                var relative = position - transform.position;
                var weaponsQuality =
                    track.IsWeaponsQuality;
                var markerRadius = weaponsQuality
                    ? radius + 2f
                    : radius;
                var markerColor = weaponsQuality
                    ? HudRed
                    : HudAmber;
                var screen =
                    viewCamera.WorldToScreenPoint(position);
                if (screen.z <= 0f ||
                    screen.x < markerRadius ||
                    screen.x > Screen.width - markerRadius ||
                    screen.y < markerRadius ||
                    screen.y > Screen.height - markerRadius)
                {
                    continue;
                }

                var markerCenter =
                    new Vector2(
                        screen.x,
                        Screen.height - screen.y);
                DrawDiamond(
                    markerCenter,
                    markerRadius,
                    markerColor,
                    !weaponsQuality,
                    weaponsQuality ? 1.6f : 1.2f);

                if (weaponSystem != null &&
                    weaponSystem.HasInterceptorLaunchSolution &&
                    weaponSystem.InterceptorTrackId.Equals(
                        track.TrackId))
                {
                    var lockExtent = markerRadius + 8f;
                    DrawCornerFrame(
                        new Rect(
                            markerCenter.x - lockExtent,
                            markerCenter.y - lockExtent,
                            lockExtent * 2f,
                            lockExtent * 2f),
                        HudAmber,
                        7f,
                        1.6f,
                        false);
                }

                var distanceKilometers =
                    relative.magnitude / 1000f;
                var distanceText = distanceKilometers < 10f
                    ? $"导弹 {distanceKilometers:0.00} km"
                    : $"导弹 {distanceKilometers:0.0} km";
                var labelCenter = new Vector2(
                    screen.x,
                    Screen.height - screen.y +
                    markerRadius + 12f);
                var labelRect = ClampToScreen(
                    new Rect(
                        labelCenter.x - 50f,
                        labelCenter.y,
                        100f,
                        24f),
                    4f);
                smallStyle.alignment =
                    TextAnchor.UpperCenter;
                smallStyle.normal.textColor = markerColor;
                GUI.Label(labelRect, distanceText, smallStyle);
            }
        }

        private void DrawMissileAttackLinks()
        {
            var projectiles = MechTestProjectile.Registry;
            var missileRadius = Mathf.Max(3f, friendlyMissileMarkerRadius);
            const float targetFrameClearance = TrackMarkerSize * 0.5f + 4f;
            for (var i = 0; i < projectiles.Count; i++)
            {
                var projectile = projectiles[i];
                if (!IsFriendlyEngagementMissile(projectile) ||
                    !TryGetTrackScreenPoint(
                        projectile.GuidanceTrackId,
                        out var targetPoint))
                {
                    continue;
                }

                var missileScreen =
                    viewCamera.WorldToScreenPoint(projectile.transform.position);
                var missilePoint =
                    new Vector2(missileScreen.x, Screen.height - missileScreen.y);
                if (missileScreen.z <= 0f ||
                    !IsInsideScreen(missilePoint, missileRadius) ||
                    !IsInsideScreen(targetPoint, targetFrameClearance))
                {
                    continue;
                }

                var delta = targetPoint - missilePoint;
                var distance = delta.magnitude;
                if (distance <= missileRadius + targetFrameClearance + 4f)
                {
                    continue;
                }

                var direction = delta / distance;
                DrawLine(
                    missilePoint + direction * (missileRadius + 3f),
                    targetPoint - direction * targetFrameClearance,
                    HudMissileLink,
                    1f,
                    true);
            }
        }

        private bool IsFriendlyEngagementMissile(
            MechTestProjectile projectile) =>
            projectile != null &&
            projectile.IsMissile &&
            projectile.IsEngagementActive &&
            projectile.IsOwnedBy(transform);

        private bool ShouldDisplayIncomingMissile(
            in MissileTrackSnapshot track)
        {
            if (!track.IsWarning || !track.Incoming)
            {
                return false;
            }

            var evidenceAge =
                radar.SimTimeSeconds - track.LastEvidenceTime;
            return evidenceAge >= -0.05 &&
                   evidenceAge <= MissileMarkerEvidenceHoldSeconds;
        }

        private static bool IsInsideScreen(Vector2 point, float margin) =>
            point.x >= margin &&
            point.x <= Screen.width - margin &&
            point.y >= margin &&
            point.y <= Screen.height - margin;

        private void DrawTransientEchoes()
        {
            var now = radar.SimTimeSeconds;
            for (var i = 0; i < echoCount; i++)
            {
                var echo = echoes[i];
                var age = now - echo.Timestamp;
                if (age < 0.0 || age > 0.25 || IsEchoAlreadyTracked(echo))
                {
                    continue;
                }

                var screen = viewCamera.WorldToScreenPoint(ToVector3(echo.EstimatedPosition));
                if (screen.z <= 0f)
                {
                    continue;
                }

                var alpha = (float)(1.0 - age / 0.25);
                var center = new Vector2(screen.x, Screen.height - screen.y);
                DrawLine(
                    center + new Vector2(-4f, 0f),
                    center + new Vector2(4f, 0f),
                    new Color(HudGreen.r, HudGreen.g, HudGreen.b, alpha * 0.75f),
                    1f);
            }
        }

        private bool IsEchoAlreadyTracked(in RadarEchoSnapshot echo)
        {
            var echoPosition = ToVector3(echo.EstimatedPosition);
            var echoRange = Vector3.Distance(transform.position, echoPosition);
            var mergeDistance = Mathf.Max(12f, echoRange * 0.0075f);
            var mergeDistanceSquared = mergeDistance * mergeDistance;
            for (var i = 0; i < trackCount; i++)
            {
                if (tracks[i].Lifecycle == TrackLifecycle.Dropped)
                {
                    continue;
                }

                var trackPosition = radar.PredictTrackPosition(tracks[i]);
                if ((trackPosition - echoPosition).sqrMagnitude <= mergeDistanceSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private int CountDisplayTracks()
        {
            var count = 0;
            for (var i = 0; i < trackCount; i++)
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
            if ((uint)index >= (uint)trackCount ||
                !IsTrackPresentationEligible(index))
            {
                return false;
            }

            var track = tracks[index];
            var position = radar.PredictTrackPosition(track);
            var range = Vector3.Distance(transform.position, position);
            var mergeDistance = Mathf.Max(30f, range * 0.004f);
            var mergeDistanceSquared = mergeDistance * mergeDistance;
            var velocity = ToVector3(track.Velocity);
            var score = DisplayTrackScore(track);

            for (var otherIndex = 0; otherIndex < trackCount; otherIndex++)
            {
                if (otherIndex == index ||
                    !IsTrackPresentationEligible(otherIndex))
                {
                    continue;
                }

                var other = tracks[otherIndex];
                var otherPosition = radar.PredictTrackPosition(other);
                if ((otherPosition - position).sqrMagnitude > mergeDistanceSquared ||
                    (ToVector3(other.Velocity) - velocity).sqrMagnitude > 6_400f)
                {
                    continue;
                }

                var otherScore = DisplayTrackScore(other);
                if (otherScore > score ||
                    (Mathf.Approximately(otherScore, score) && otherIndex < index))
                {
                    return false;
                }
            }

            return true;
        }

        private bool IsTrackPresentationEligible(int index)
        {
            var track = tracks[index];
            if (track.ObjectClass == TrackObjectClass.Missile)
            {
                return false;
            }

            if (track.TrackId.Equals(radar.SttTrackId) ||
                track.TrackId.Equals(radar.CandidateTrackId))
            {
                return true;
            }

            return track.Lifecycle switch
            {
                TrackLifecycle.Confirmed => track.Confidence >= 0.35f,
                TrackLifecycle.Tentative =>
                    track.HitCount >= 2 && track.Confidence >= 0.4f,
                TrackLifecycle.Coasting =>
                    track.CoastCount <= 4 && track.Confidence >= 0.35f,
                _ => false,
            };
        }

        private float DisplayTrackScore(in TrackSnapshot track)
        {
            var score = track.Lifecycle switch
            {
                TrackLifecycle.Confirmed => 400f,
                TrackLifecycle.Coasting => 300f,
                TrackLifecycle.Degraded => 200f,
                TrackLifecycle.Tentative => 100f,
                _ => 0f,
            };

            if (track.TrackId.Equals(radar.SttTrackId))
            {
                score += 10_000f;
            }
            else if (track.TrackId.Equals(radar.CandidateTrackId))
            {
                score += 5_000f;
            }

            return score + track.Confidence * 100f + (float)(track.LastUpdateTime * 0.001);
        }

        private float GetTacticalDisplayRange()
        {
            var farthest = 0f;
            for (var i = 0; i < trackCount; i++)
            {
                if (!ShouldDisplayTrack(i))
                {
                    continue;
                }

                var relative = radar.PredictTrackPosition(tracks[i]) - transform.position;
                var horizontalRange = new Vector2(relative.x, relative.z).magnitude;
                farthest = Mathf.Max(farthest, horizontalRange);
            }

            var required = farthest * 1.25f;
            if (required <= 2_000f) return 2_000f;
            if (required <= 5_000f) return 5_000f;
            if (required <= 10_000f) return 10_000f;
            if (required <= 25_000f) return 25_000f;
            if (required <= 50_000f) return 50_000f;
            return Mathf.Max(100_000f, radarStatus.RangeMeters);
        }

        private float GetRadarDisplayRange(float halfAngle)
        {
            var farthest = 0f;
            for (var i = 0; i < trackCount; i++)
            {
                if (!ShouldDisplayTrack(i))
                {
                    continue;
                }

                var relative = ToVector3(tracks[i].Position) - transform.position;
                var local = ToHeadingLocal(relative);
                var horizontal = new Vector2(local.x, local.z);
                if (horizontal.sqrMagnitude < 0.0001f || horizontal.y <= 0f)
                {
                    continue;
                }

                var azimuth = Mathf.Atan2(horizontal.x, horizontal.y) * Mathf.Rad2Deg;
                if (Mathf.Abs(azimuth) <= halfAngle)
                {
                    farthest = Mathf.Max(farthest, horizontal.magnitude);
                }
            }

            var maximumRange = Mathf.Max(5_000f, radarStatus.RangeMeters);
            radarDisplayRangeMeters = Mathf.Clamp(
                radarDisplayRangeMeters,
                5_000f,
                maximumRange);
            if (farthest <= 0f)
            {
                return radarDisplayRangeMeters;
            }

            var requiredRange = farthest * 1.25f;
            var selectedRange = SelectRadarDisplayRange(requiredRange, maximumRange);
            if (selectedRange > radarDisplayRangeMeters &&
                requiredRange > radarDisplayRangeMeters * 0.88f)
            {
                radarDisplayRangeMeters = selectedRange;
            }
            else if (selectedRange < radarDisplayRangeMeters &&
                     requiredRange < radarDisplayRangeMeters * 0.4f)
            {
                radarDisplayRangeMeters = selectedRange;
            }

            return radarDisplayRangeMeters;
        }

        private static float SelectRadarDisplayRange(float requiredRange, float maximumRange)
        {
            if (requiredRange <= 5_000f) return 5_000f;
            if (requiredRange <= 10_000f) return Mathf.Min(10_000f, maximumRange);
            if (requiredRange <= 20_000f) return Mathf.Min(20_000f, maximumRange);
            if (requiredRange <= 40_000f) return Mathf.Min(40_000f, maximumRange);
            if (requiredRange <= 80_000f) return Mathf.Min(80_000f, maximumRange);
            return maximumRange;
        }

        private Vector3 ToHeadingLocal(Vector3 worldVector)
        {
            var up = GetReferenceUp();
            var forward = GetHeadingForward();
            var right = Vector3.Cross(up, forward).normalized;
            return new Vector3(
                Vector3.Dot(worldVector, right),
                Vector3.Dot(worldVector, up),
                Vector3.Dot(worldVector, forward));
        }

        private Vector3 GetHeadingForward()
        {
            var up = GetReferenceUp();
            var forward = aimController != null
                ? aimController.AimRotation * Vector3.forward
                : viewCamera != null
                    ? viewCamera.transform.forward
                    : transform.forward;
            forward = Vector3.ProjectOnPlane(forward, up);
            return forward.sqrMagnitude > 0.001f
                ? forward.normalized
                : motion != null
                    ? motion.ReferenceRotation * Vector3.forward
                    : Vector3.forward;
        }

        private float GetCompassHeadingDegrees()
        {
            var referenceRotation =
                motion != null ? motion.ReferenceRotation : Quaternion.identity;
            var referenceLocalForward =
                Quaternion.Inverse(referenceRotation) * GetHeadingForward();
            return Mathf.Repeat(
                Mathf.Atan2(
                    referenceLocalForward.x,
                    referenceLocalForward.z) * Mathf.Rad2Deg,
                360f);
        }

        private static string FormatHeading(float headingDegrees)
        {
            var rounded = Mathf.RoundToInt(Mathf.Repeat(headingDegrees, 360f)) % 360;
            return rounded.ToString("000");
        }

        private Vector3 GetReferenceUp() =>
            motion != null ? motion.ReferenceUp : Vector3.up;

        private static string FormatRange(float rangeMeters) =>
            rangeMeters < 10_000f
                ? $"{rangeMeters / 1000f:0.0}K"
                : $"{rangeMeters / 1000f:0}K";

        private void DrawLegacyReticle()
        {
            var center = GetReticleGuiPosition();
            DrawLine(center + new Vector2(-11f, 0f), center + new Vector2(-3f, 0f), HudGreen, 1.4f);
            DrawLine(center + new Vector2(11f, 0f), center + new Vector2(3f, 0f), HudGreen, 1.4f);
            DrawLine(center + new Vector2(0f, -11f), center + new Vector2(0f, -3f), HudGreen, 1.4f);
            DrawLine(center + new Vector2(0f, 11f), center + new Vector2(0f, 3f), HudGreen, 1.4f);
        }

        private Vector2 GetReticleGuiPosition()
        {
            var viewportPoint = GetAimViewportPoint();
            var screen = viewCamera.ViewportToScreenPoint(
                new Vector3(viewportPoint.x, viewportPoint.y, 0f));
            return new Vector2(screen.x, Screen.height - screen.y);
        }

        private Vector2 GetAimViewportPoint()
        {
            if (aimController == null && viewCamera != null)
            {
                aimController = viewCamera.GetComponent<MechThirdPersonCamera>();
            }

            var point = aimController != null
                ? aimController.AimViewportPoint
                : aimViewportPoint;
            return new Vector2(
                Mathf.Clamp(point.x, 0.05f, 0.95f),
                Mathf.Clamp(point.y, 0.05f, 0.95f));
        }

        private bool TryGetTrackScreenPoint(TrackId trackId, out Vector2 point)
        {
            for (var i = 0; i < trackCount; i++)
            {
                if (!tracks[i].TrackId.Equals(trackId))
                {
                    continue;
                }

                var screen = viewCamera.WorldToScreenPoint(radar.PredictTrackPosition(tracks[i]));
                if (screen.z > 0f)
                {
                    point = new Vector2(screen.x, Screen.height - screen.y);
                    return true;
                }
            }

            point = default;
            return false;
        }

        private static bool WasPressed(Keyboard keyboard, Key key)
        {
            var control = keyboard[key];
            return control != null && control.wasPressedThisFrame;
        }

        private static float GetLifecycleAlpha(in TrackSnapshot track)
        {
            return track.Lifecycle switch
            {
                TrackLifecycle.Coasting => Mathf.Clamp(0.72f - track.CoastCount * 0.08f, 0.2f, 0.72f),
                TrackLifecycle.Degraded => Mathf.Clamp(0.5f - track.CoastCount * 0.05f, 0.14f, 0.5f),
                _ => 0.95f,
            };
        }

        private static string LifecycleLabel(TrackLifecycle lifecycle) => lifecycle switch
        {
            TrackLifecycle.Tentative => "INIT",
            TrackLifecycle.Confirmed => "TWS",
            TrackLifecycle.Coasting => "COAST",
            TrackLifecycle.Degraded => "DEGRADED",
            _ => "LOST",
        };

        private static string SourceLabel(TrackSourceMask source)
        {
            return ((int)source & 0x0f) switch
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

        private static string AffiliationLabel(TrackAffiliation affiliation) => affiliation switch
        {
            TrackAffiliation.Friendly => "FRIEND",
            TrackAffiliation.Hostile => "HOSTILE",
            _ => "UNK",
        };

        private static string TrackLabel(TrackId id) => $"T{id.Value.Index:00}";

        private void EnsureStyles()
        {
            smallStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = false,
            };
            weaponStatusStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                richText = false,
            };
            statusStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
            };
            microStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
            };
            compassStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            };
            pitchStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
            };
            zoomStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 19,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = false,
            };
        }

        private static Rect ClampToScreen(Rect rect, float margin)
        {
            rect.x = Mathf.Clamp(rect.x, margin, Mathf.Max(margin, Screen.width - rect.width - margin));
            rect.y = Mathf.Clamp(rect.y, margin, Mathf.Max(margin, Screen.height - rect.height - margin));
            return rect;
        }

        private static Rect ClampToRect(Rect rect, Rect bounds, float margin)
        {
            rect.x = Mathf.Clamp(
                rect.x,
                bounds.xMin + margin,
                Mathf.Max(bounds.xMin + margin, bounds.xMax - rect.width - margin));
            rect.y = Mathf.Clamp(
                rect.y,
                bounds.yMin + margin,
                Mathf.Max(bounds.yMin + margin, bounds.yMax - rect.height - margin));
            return rect;
        }

        private static Vector2 RadarDirection(float angleDegrees)
        {
            var radians = angleDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Sin(radians), -Mathf.Cos(radians));
        }

        private static void DrawFanArc(
            Vector2 center,
            float radius,
            float startDegrees,
            float endDegrees,
            Color color,
            float thickness)
        {
            const int segments = 36;
            var previous = center + RadarDirection(startDegrees) * radius;
            for (var i = 1; i <= segments; i++)
            {
                var angle = Mathf.Lerp(startDegrees, endDegrees, i / (float)segments);
                var current = center + RadarDirection(angle) * radius;
                DrawLine(previous, current, color, thickness);
                previous = current;
            }
        }

        private static void DrawAltitudeCue(Vector2 center, bool above, Color color)
        {
            var sign = above ? -1f : 1f;
            DrawLine(center + new Vector2(-3f, 3f * sign), center + new Vector2(0f, 0f), color, 1f);
            DrawLine(center + new Vector2(3f, 3f * sign), center + new Vector2(0f, 0f), color, 1f);
        }

        private static void DrawArrowHead(
            Vector2 tip,
            Vector2 forward,
            Color color,
            float thickness)
        {
            var side = new Vector2(-forward.y, forward.x);
            var basePoint = tip - forward * 5f;
            DrawLine(tip, basePoint + side * 3f, color, thickness);
            DrawLine(tip, basePoint - side * 3f, color, thickness);
        }

        private static void DrawDiamond(
            Vector2 center,
            float radius,
            Color color,
            bool dashed,
            float thickness = 1f)
        {
            var a = center + new Vector2(0f, -radius);
            var b = center + new Vector2(radius, 0f);
            var c = center + new Vector2(0f, radius);
            var d = center + new Vector2(-radius, 0f);
            DrawLine(a, b, color, thickness, dashed);
            DrawLine(b, c, color, thickness, dashed);
            DrawLine(c, d, color, thickness, dashed);
            DrawLine(d, a, color, thickness, dashed);
        }

        private static void DrawTrackSymbol(
            Vector2 center,
            float radius,
            Color color,
            bool dashed,
            TrackAffiliation affiliation)
        {
            if (affiliation == TrackAffiliation.Friendly)
            {
                DrawCircle(center, radius, color, 1f, dashed);
                return;
            }

            if (affiliation == TrackAffiliation.Hostile)
            {
                var top = center + new Vector2(0f, -radius);
                var left = center + new Vector2(-radius, radius);
                var right = center + new Vector2(radius, radius);
                DrawLine(top, left, color, 1f, dashed);
                DrawLine(left, right, color, 1f, dashed);
                DrawLine(right, top, color, 1f, dashed);
                return;
            }

            DrawDiamond(center, radius, color, dashed);
        }

        private static void DrawCircle(
            Vector2 center,
            float radius,
            Color color,
            float thickness,
            bool dashed)
        {
            var previous = center + new Vector2(0f, -radius);
            for (var i = 1; i <= CircleSegments; i++)
            {
                var angle = i * Mathf.PI * 2f / CircleSegments;
                var current = center + new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * radius;
                if (!dashed || (i / 3) % 2 == 0)
                {
                    DrawLine(previous, current, color, thickness);
                }
                previous = current;
            }
        }

        private void DrawSmoothTrackOperationRing(Vector2 center, float radius)
        {
            EnsureOperationRingTexture();
            var textureHalfExtent =
                radius / (OperationRingTextureRadiusFraction * 2f);
            var size = textureHalfExtent * 2f;
            GUI.DrawTexture(
                new Rect(
                    center.x - textureHalfExtent,
                    center.y - textureHalfExtent,
                    size,
                    size),
                operationRingTexture,
                ScaleMode.StretchToFill,
                true);
        }

        private void EnsureTacticalBackgroundTexture()
        {
            if (tacticalBackgroundTexture != null)
            {
                return;
            }

            var pixels = new Color32[
                TacticalBackgroundTextureSize * TacticalBackgroundTextureSize];
            var center = (TacticalBackgroundTextureSize - 1) * 0.5f;
            var outerRadius = center - 5f;
            var discRadius = outerRadius + 3.5f;
            const float outerRingHalfWidth = 3.5f;
            const float innerRingHalfWidth = 2.2f;
            var backgroundColor = new Color(0f, 0.008f, 0.002f, 0.78f);

            for (var y = 0; y < TacticalBackgroundTextureSize; y++)
            {
                var dy = y - center;
                for (var x = 0; x < TacticalBackgroundTextureSize; x++)
                {
                    var dx = x - center;
                    var distance = Mathf.Sqrt(dx * dx + dy * dy);
                    var discAlpha = Mathf.Clamp01(discRadius - distance + 0.7f);
                    var outerRingAlpha = Mathf.Clamp01(
                        outerRingHalfWidth + 0.7f -
                        Mathf.Abs(distance - outerRadius));
                    if (discAlpha <= 0f && outerRingAlpha <= 0f)
                    {
                        continue;
                    }

                    var color = backgroundColor;
                    color.a *= discAlpha;
                    for (var ringIndex = 1; ringIndex <= 3; ringIndex++)
                    {
                        var ringRadius = outerRadius * ringIndex * 0.25f;
                        var ringAlpha = Mathf.Clamp01(
                            innerRingHalfWidth + 0.7f -
                            Mathf.Abs(distance - ringRadius));
                        if (ringAlpha <= 0f)
                        {
                            continue;
                        }

                        color = Color.Lerp(color, HudDimGreen, ringAlpha);
                        color.a = Mathf.Max(color.a, HudDimGreen.a * ringAlpha);
                    }

                    if (outerRingAlpha > 0f)
                    {
                        color = Color.Lerp(color, HudGreen, outerRingAlpha);
                        color.a = Mathf.Max(color.a, HudGreen.a * outerRingAlpha);
                    }
                    pixels[y * TacticalBackgroundTextureSize + x] = color;
                }
            }

            tacticalBackgroundTexture = new Texture2D(
                TacticalBackgroundTextureSize,
                TacticalBackgroundTextureSize,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "Tactical Plot Disc And Rings AA",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            tacticalBackgroundTexture.SetPixels32(pixels);
            tacticalBackgroundTexture.Apply(false, true);
        }

        private void EnsureOperationRingTexture()
        {
            if (operationRingTexture != null)
            {
                return;
            }

            const int dashCount = 32;
            const float visibleDashFraction = 0.62f;
            const float ringRadius =
                OperationRingTextureSize * OperationRingTextureRadiusFraction;
            const float coreHalfWidth = 2.2f;
            const float haloHalfWidth = 5.2f;
            var pixels = new Color32[
                OperationRingTextureSize * OperationRingTextureSize];
            var center = (OperationRingTextureSize - 1) * 0.5f;

            for (var y = 0; y < OperationRingTextureSize; y++)
            {
                var dy = y - center;
                for (var x = 0; x < OperationRingTextureSize; x++)
                {
                    var dx = x - center;
                    var radialDistance =
                        Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - ringRadius);
                    if (radialDistance > haloHalfWidth + 1.5f)
                    {
                        continue;
                    }

                    var angle01 = Mathf.Repeat(
                        Mathf.Atan2(dx, -dy) / (Mathf.PI * 2f),
                        1f);
                    var centeredDashPhase =
                        Mathf.Repeat(angle01 * dashCount + 0.5f, 1f) - 0.5f;
                    if (Mathf.Abs(centeredDashPhase) >
                        visibleDashFraction * 0.5f)
                    {
                        continue;
                    }

                    var haloAlpha = Mathf.Clamp01(
                        haloHalfWidth + 1f - radialDistance) * 0.26f;
                    var coreAlpha = Mathf.Clamp01(
                        coreHalfWidth + 0.9f - radialDistance);
                    var alpha = Mathf.Clamp01(haloAlpha + coreAlpha * 0.92f);
                    pixels[y * OperationRingTextureSize + x] =
                        new Color(0.36f, 1f, 0.55f, alpha);
                }
            }

            operationRingTexture = new Texture2D(
                OperationRingTextureSize,
                OperationRingTextureSize,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "RadarOperationRing_AA",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            operationRingTexture.SetPixels32(pixels);
            operationRingTexture.Apply(false, true);
        }

        private static void DrawCornerFrame(
            Rect rect,
            Color color,
            float cornerLength,
            float thickness,
            bool dashed)
        {
            var topLeft = new Vector2(rect.xMin, rect.yMin);
            var topRight = new Vector2(rect.xMax, rect.yMin);
            var bottomLeft = new Vector2(rect.xMin, rect.yMax);
            var bottomRight = new Vector2(rect.xMax, rect.yMax);
            DrawLine(topLeft, topLeft + Vector2.right * cornerLength, color, thickness, dashed);
            DrawLine(topLeft, topLeft + new Vector2(0f, cornerLength), color, thickness, dashed);
            DrawLine(topRight, topRight + Vector2.left * cornerLength, color, thickness, dashed);
            DrawLine(topRight, topRight + new Vector2(0f, cornerLength), color, thickness, dashed);
            DrawLine(bottomLeft, bottomLeft + Vector2.right * cornerLength, color, thickness, dashed);
            DrawLine(bottomLeft, bottomLeft + new Vector2(0f, -cornerLength), color, thickness, dashed);
            DrawLine(bottomRight, bottomRight + Vector2.left * cornerLength, color, thickness, dashed);
            DrawLine(bottomRight, bottomRight + new Vector2(0f, -cornerLength), color, thickness, dashed);
        }

        private static void DrawFilledRect(Rect rect, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }

        private static void EnsureSoftLineTexture()
        {
            if (softLineTexture != null)
            {
                return;
            }

            softLineTexture = new Texture2D(
                1,
                SoftLineTextureHeight,
                TextureFormat.RGBA32,
                false,
                true)
            {
                name = "HUD Soft Line",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var pixels = new[]
            {
                new Color(1f, 1f, 1f, 0f),
                new Color(1f, 1f, 1f, 0.12f),
                new Color(1f, 1f, 1f, 0.62f),
                Color.white,
                new Color(1f, 1f, 1f, 0.62f),
                new Color(1f, 1f, 1f, 0.12f),
                new Color(1f, 1f, 1f, 0f),
            };
            softLineTexture.SetPixels(pixels);
            softLineTexture.Apply(false, true);
        }

        private static void DrawLine(
            Vector2 start,
            Vector2 end,
            Color color,
            float thickness,
            bool dashed = false)
        {
            var delta = end - start;
            var length = delta.magnitude;
            if (length < 0.01f)
            {
                return;
            }

            if (dashed && length > 5f)
            {
                var direction = delta / length;
                const float segment = 4f;
                for (var cursor = 0f; cursor < length; cursor += segment * 2f)
                {
                    var segmentEnd = Mathf.Min(cursor + segment, length);
                    DrawSolidLine(start + direction * cursor, start + direction * segmentEnd, color, thickness);
                }
                return;
            }

            DrawSolidLine(start, end, color, thickness);
        }

        private static void DrawSolidLine(Vector2 start, Vector2 end, Color color, float thickness)
        {
            var delta = end - start;
            var angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            var matrix = GUI.matrix;
            var previousColor = GUI.color;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, start);
            GUI.DrawTexture(
                new Rect(
                    start.x,
                    start.y - thickness * 0.5f - 1f,
                    delta.magnitude,
                    thickness + 2f),
                softLineTexture);
            GUI.matrix = matrix;
            GUI.color = previousColor;
        }

        private static Vector3 ToVector3(double3 value) =>
            new Vector3((float)value.x, (float)value.y, (float)value.z);
    }
}
