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
        private const float TacticalPlotWidth = 252f;
        private const float TacticalPlotHeight = 252f;
        private const float RadarStatusWidth = 306f;
        private const float RadarStatusHeight = 292f;
        private const float TrackMarkerSize = 48f;
        private const int CircleSegments = 144;
        private const int OperationRingTextureSize = 1024;

        [SerializeField] private MechRadarController radar;
        [SerializeField] private Camera viewCamera;
        [SerializeField] private Vector2 aimViewportPoint = new Vector2(0.5f, 0.56f);
        [Header("Rebindable radar controls")]
        [SerializeField] private Key operationModeKey = Key.Tab;
        [SerializeField] private Key breakLockKey = Key.T;
        [SerializeField] private Key cycleTargetKey = Key.G;

        private readonly TrackSnapshot[] tracks =
            new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly RadarEchoSnapshot[] echoes =
            new RadarEchoSnapshot[MechRadarController.MaximumPublicEchoes];
        private int trackCount;
        private int echoCount;
        private RadarStatusSnapshot radarStatus;
        private MechThirdPersonCamera aimController;
        private Texture2D operationRingTexture;
        private GUIStyle smallStyle;
        private GUIStyle compactStyle;
        private GUIStyle statusStyle;
        private static readonly Color HudGreen = new Color(0.3f, 1f, 0.5f, 1f);
        private static readonly Color HudDimGreen = new Color(0.18f, 0.78f, 0.36f, 0.76f);
        private static readonly Color HudBrightGreen = new Color(0.55f, 1f, 0.7f, 1f);
        private static readonly Color HudRed = new Color(1f, 0.18f, 0.12f, 1f);
        private static readonly Color HudAmber = new Color(1f, 0.72f, 0.18f, 0.95f);
        private static readonly Color HudPanel = new Color(0.015f, 0.055f, 0.035f, 0.76f);
        private static readonly Color HudFanFill = new Color(0.04f, 0.25f, 0.12f, 0.28f);

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
            aimViewportPoint = new Vector2(
                Mathf.Clamp(newAimViewportPoint.x, 0.05f, 0.95f),
                Mathf.Clamp(newAimViewportPoint.y, 0.05f, 0.95f));
        }

        private void Awake()
        {
            if (radar == null) radar = GetComponent<MechRadarController>();
            if (viewCamera == null) viewCamera = Camera.main;
            if (viewCamera != null)
            {
                aimController = viewCamera.GetComponent<MechThirdPersonCamera>();
            }
        }

        private void OnDestroy()
        {
            if (operationRingTexture != null)
            {
                Destroy(operationRingTexture);
                operationRingTexture = null;
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
            echoCount = radar.CopyEchoes(echoes);
            radarStatus = radar.GetStatusSnapshot();
        }

        private void OnGUI()
        {
            if (radar == null || viewCamera == null)
            {
                return;
            }

            EnsureStyles();
            DrawTacticalPlot();
            DrawRadarStatus();
            if (radar.TrackOperationMode)
            {
                DrawTransientEchoes();
                DrawTrackOperationOverlay();
                DrawWorldTrackMarkers();
            }
            else
            {
                DrawLegacyReticle();
            }
        }

        private void DrawTacticalPlot()
        {
            var plot = new Rect(18f, 18f, TacticalPlotWidth, TacticalPlotHeight);
            DrawFilledRect(plot, HudPanel);
            DrawCornerFrame(plot, HudDimGreen, 18f, 1.5f, false);

            var center = new Vector2(plot.center.x, plot.center.y + 3f);
            var radius = Mathf.Min(plot.width, plot.height) * 0.39f;
            DrawCircle(center, radius, HudGreen, 1.7f, false);
            DrawCircle(center, radius * 0.66f, HudDimGreen, 1f, false);
            DrawCircle(center, radius * 0.33f, HudDimGreen, 1f, false);
            DrawLine(center + Vector2.left * radius, center + Vector2.right * radius, HudDimGreen, 1f);
            DrawLine(center + Vector2.up * radius, center + Vector2.down * radius, HudDimGreen, 1f);

            // Heading-up ownship symbol. The tactical plot is a fused 360-degree track picture.
            DrawLine(center + new Vector2(0f, -9f), center + new Vector2(-6f, 4f), HudBrightGreen, 2f);
            DrawLine(center + new Vector2(0f, -9f), center + new Vector2(6f, 4f), HudBrightGreen, 2f);
            DrawLine(center + new Vector2(-6f, 4f), center + new Vector2(6f, 4f), HudBrightGreen, 2f);

            var range = Mathf.Max(1f, radarStatus.RangeMeters);
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped)
                {
                    continue;
                }

                var relativeWorld = ToVector3(track.Position) - transform.position;
                var relativeLocal = transform.InverseTransformDirection(relativeWorld);
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
                    : new Color(HudGreen.r, HudGreen.g, HudGreen.b, alpha);
                var dashed = track.Lifecycle == TrackLifecycle.Coasting ||
                             track.Lifecycle == TrackLifecycle.Degraded;
                DrawTrackSymbol(point, 4.5f, color, dashed, track.Affiliation);

                var velocityLocal = transform.InverseTransformDirection(ToVector3(track.Velocity));
                var velocity2D = new Vector2(velocityLocal.x, -velocityLocal.z);
                if (velocity2D.sqrMagnitude > 0.01f)
                {
                    var vectorLength = Mathf.Clamp(velocity2D.magnitude * 0.025f, 4f, 16f);
                    DrawLine(point, point + velocity2D.normalized * vectorLength, color, 1f);
                }

                if (Mathf.Abs(relativeLocal.y) > 10f)
                {
                    DrawAltitudeCue(point + new Vector2(8f, 0f), relativeLocal.y > 0f, color);
                }

                smallStyle.alignment = TextAnchor.UpperLeft;
                smallStyle.normal.textColor = color;
                GUI.Label(
                    new Rect(point.x + 7f, point.y - 10f, 50f, 20f),
                    SourceLabel(track.SourceMask),
                    smallStyle);
            }

            compactStyle.normal.textColor = HudGreen;
            GUI.Label(
                new Rect(plot.x + 10f, plot.y + 7f, plot.width - 20f, 20f),
                $"TACTICAL  {radarStatus.TrackCount:00} TRACKS",
                compactStyle);
            GUI.Label(
                new Rect(plot.x + 10f, plot.yMax - 24f, plot.width - 20f, 20f),
                $"RANGE {radarStatus.RangeMeters / 1000f:0} KM  HDG-UP",
                compactStyle);
        }

        private void DrawRadarStatus()
        {
            var rect = new Rect(
                Screen.width - RadarStatusWidth - 18f,
                Screen.height - RadarStatusHeight - 18f,
                RadarStatusWidth,
                RadarStatusHeight);
            DrawFilledRect(rect, HudPanel);
            DrawCornerFrame(rect, HudDimGreen, 16f, 1.4f, false);
            var fanApex = new Vector2(rect.center.x, rect.y + 157f);
            var fanRadius = 105f;
            var halfAngle = Mathf.Clamp(radarStatus.ScanAzimuthDegrees * 0.5f, 20f, 80f);
            for (var angle = -halfAngle; angle <= halfAngle; angle += 2f)
            {
                DrawLine(
                    fanApex,
                    fanApex + RadarDirection(angle) * fanRadius,
                    HudFanFill,
                    5f);
            }

            DrawLine(
                fanApex,
                fanApex + RadarDirection(-halfAngle) * fanRadius,
                HudGreen,
                1.7f);
            DrawLine(
                fanApex,
                fanApex + RadarDirection(halfAngle) * fanRadius,
                HudGreen,
                1.7f);
            DrawFanArc(fanApex, fanRadius, -halfAngle, halfAngle, HudGreen, 1.7f);
            DrawFanArc(fanApex, fanRadius * 0.66f, -halfAngle, halfAngle, HudDimGreen, 1f);
            DrawFanArc(fanApex, fanRadius * 0.33f, -halfAngle, halfAngle, HudDimGreen, 1f);
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
            DrawLine(
                fanApex,
                fanApex + RadarDirection(beamAngle) * fanRadius,
                HudBrightGreen,
                2.2f);
            DrawRadarFanTracks(fanApex, fanRadius, halfAngle);

            var flags = radarStatus.Flags == RadarStatusFlags.None
                ? "NOMINAL"
                : radarStatus.Flags.ToString().ToUpperInvariant();
            var sttText = radarStatus.SttTrackId.IsValid
                ? TrackLabel(radarStatus.SttTrackId)
                : "--";
            compactStyle.normal.textColor = HudGreen;
            GUI.Label(
                new Rect(rect.x + 12f, rect.y + 7f, rect.width - 24f, 22f),
                $"{ModeLabel(radarStatus.Mode)}  " +
                $"AZ {radarStatus.ScanAzimuthDegrees:0} x EL {radarStatus.ScanElevationDegrees:0}  " +
                $"{radarStatus.RangeMeters / 1000f:0} KM",
                compactStyle);

            statusStyle.normal.textColor = HudGreen;
            GUI.Label(
                new Rect(rect.x + 12f, rect.y + 174f, rect.width - 24f, 108f),
                $"TRACKS {radarStatus.TrackCount}/{radarStatus.TrackCapacity}\n" +
                $"REFRESH {radarStatus.RefreshRateHz:0.0} HZ  LOAD {radarStatus.DwellLoad * 100f:0}%\n" +
                $"GUIDANCE {radarStatus.GuidanceChannelsUsed}/{radarStatus.GuidanceChannelCapacity}\n" +
                $"STT {sttText}  DEFER {radarStatus.DeferredDwellCount}\n" +
                $"STATUS {flags}",
                statusStyle);
        }

        private void DrawRadarFanTracks(Vector2 apex, float radius, float halfAngle)
        {
            var range = Mathf.Max(1f, radarStatus.RangeMeters);
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped)
                {
                    continue;
                }

                var relativeWorld = ToVector3(track.Position) - transform.position;
                var relativeLocal = transform.InverseTransformDirection(relativeWorld);
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
                    : new Color(HudGreen.r, HudGreen.g, HudGreen.b, alpha);
                DrawTrackSymbol(
                    point,
                    4.5f,
                    color,
                    track.Lifecycle == TrackLifecycle.Coasting ||
                    track.Lifecycle == TrackLifecycle.Degraded,
                    track.Affiliation);
            }
        }

        private void DrawTrackOperationOverlay()
        {
            var center = GetReticleGuiPosition();
            var ringCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            var diameter = Mathf.Clamp(Screen.height * 0.75f, 320f, Screen.height * 0.8f);
            DrawSmoothTrackOperationRing(ringCenter, diameter * 0.5f);
            for (var i = 0; i < 4; i++)
            {
                var angle = i * Mathf.PI * 0.5f;
                var direction = new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle));
                DrawLine(
                    ringCenter + direction * (diameter * 0.5f - 9f),
                    ringCenter + direction * (diameter * 0.5f + 9f),
                    new Color(0f, 0.08f, 0.025f, 0.8f),
                    4f);
                DrawLine(
                    ringCenter + direction * (diameter * 0.5f - 7f),
                    ringCenter + direction * (diameter * 0.5f + 7f),
                    HudBrightGreen,
                    2f);
            }

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

        private void DrawWorldTrackMarkers()
        {
            for (var i = 0; i < trackCount; i++)
            {
                var track = tracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped)
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
                    if (isCandidate)
                    {
                        DrawCornerFrame(
                            new Rect(center.x - size * 0.5f - 5f, center.y - size * 0.5f - 5f, size + 10f, size + 10f),
                            HudBrightGreen,
                            8f,
                            1.4f,
                            false);
                    }
                }

                var relative = position - transform.position;
                var lineOfSight = relative.sqrMagnitude > 0.01f ? relative.normalized : transform.forward;
                var relativeVelocity =
                    ToVector3(track.Velocity - radarStatus.OwnshipVelocity);
                var closing = -Vector3.Dot(relativeVelocity, lineOfSight);
                var source = SourceLabel(track.SourceMask);
                var lifecycle = isStt ? "STT" : LifecycleLabel(track.Lifecycle);
                var classification = track.Classification >= 0.72f &&
                                     track.IdentityConfidence >= 0.7f
                    ? AffiliationLabel(track.Affiliation)
                    : "UNK";
                smallStyle.alignment = TextAnchor.UpperLeft;
                smallStyle.normal.textColor = color;
                var labelRect = new Rect(center.x + size * 0.5f + 6f, center.y - 22f, 202f, 60f);
                DrawFilledRect(labelRect, new Color(HudPanel.r, HudPanel.g, HudPanel.b, 0.64f));
                GUI.Label(
                    labelRect,
                    $"{TrackLabel(track.TrackId)} {lifecycle} {classification}\n" +
                    $"{relative.magnitude:0}m  VC {closing:+0;-0;0}  DY {relative.y:+0;-0;0}\n" +
                    $"Q {track.Confidence:0.00}  SRC {source}",
                    smallStyle);
            }
        }

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

        private static string ModeLabel(RadarOperatingMode mode) => mode switch
        {
            RadarOperatingMode.SingleTargetTrack => "STT",
            RadarOperatingMode.TrackWhileScan => "TWS",
            _ => "SEARCH",
        };

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
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
                wordWrap = false,
                richText = false,
            };
            compactStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
            };
            statusStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
            };
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

        private static void DrawDiamond(Vector2 center, float radius, Color color, bool dashed)
        {
            var a = center + new Vector2(0f, -radius);
            var b = center + new Vector2(radius, 0f);
            var c = center + new Vector2(0f, radius);
            var d = center + new Vector2(-radius, 0f);
            DrawLine(a, b, color, 1f, dashed);
            DrawLine(b, c, color, 1f, dashed);
            DrawLine(c, d, color, 1f, dashed);
            DrawLine(d, a, color, 1f, dashed);
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
            var size = radius * 2f;
            GUI.DrawTexture(
                new Rect(center.x - radius, center.y - radius, size, size),
                operationRingTexture,
                ScaleMode.StretchToFill,
                true);
        }

        private void EnsureOperationRingTexture()
        {
            if (operationRingTexture != null)
            {
                return;
            }

            const int dashCount = 28;
            const float visibleDashFraction = 0.68f;
            const float ringRadius = OperationRingTextureSize * 0.46f;
            const float coreHalfWidth = 1.45f;
            const float haloHalfWidth = 3.8f;
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
                    if (Mathf.Repeat(angle01 * dashCount, 1f) >
                        visibleDashFraction)
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
                new Rect(start.x, start.y - thickness * 0.5f, delta.magnitude, thickness),
                Texture2D.whiteTexture);
            GUI.matrix = matrix;
            GUI.color = previousColor;
        }

        private static Vector3 ToVector3(double3 value) =>
            new Vector3((float)value.x, (float)value.y, (float)value.z);
    }
}
