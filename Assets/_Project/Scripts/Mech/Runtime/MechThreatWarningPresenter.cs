using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// RWR 告警显示（左下方位环）+ 分级音频。只消费 ThreatSnapshot——方位、级别、
    /// 波形类、照射间隔，全部来自被动接收统计，无任何真值。
    /// 方位基准与战术图一致：飞控逻辑航向（07-24 参考系决议），机头朝上。
    /// 音频为占位：四个 AudioClip 槽位为空时静默，资产到位后在 Inspector 拖入即可。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechThreatWarningPresenter : MonoBehaviour
    {
        private const float PanelSize = 420f;
        private const float PanelMargin = 18f;
        private const float RingRadius = 176f;
        private const int RingTextureSize = 512;
        private const int SectorCount = 12;
        private const float SectorDegrees = 360f / SectorCount;
        private const float IdentificationThreshold = 0.35f;
        private const float MissileThreatAssociationDegrees = 20f;
        private const float MissileThreatMaximumCpaMeters = 750f;
        private const float MissileThreatUncertaintyAllowanceMeters = 750f;
        private const double MissileSeekerMainlobeMaximumAgeSeconds = 0.2;

        [Header("近距来弹显示")]
        [Tooltip("导弹告警接收功率达到该值且功率仍在上升时，整圈高亮。")]
        [SerializeField] private float closeMissileReceivedPowerDb = -12f;
        [Tooltip("判定来弹仍在接近所需的最小接收功率上升率。")]
        [SerializeField] private float closeMissilePowerTrendDbPerSecond = 0.5f;
        [Tooltip("接收功率超过该值时，不再要求功率趋势，直接判为近距威胁。")]
        [SerializeField] private float immediateMissileReceivedPowerDb = -6f;

        [Header("RWR 告警音频")]
        [Tooltip("RWR 全部单响、跟踪、锁定与导弹告警的总音量。")]
        [SerializeField, Range(0f, 1f)] private float rwrMasterVolume = 0.7f;
        [Tooltip("被搜索扫描单响。")]
        [SerializeField] private AudioClip spikeClip;
        [Tooltip("被跟踪期间以两声一组播放的接触音。")]
        [SerializeField] private AudioClip trackedClip;
        [Tooltip("敌跟踪同一组两声之间的停顿。")]
        [SerializeField, Min(0f)] private float trackedPulseGapSeconds = 0.06f;
        [Tooltip("敌跟踪两组提示之间的停顿。")]
        [SerializeField, Min(0f)] private float trackedCyclePauseSeconds = 0.75f;
        [Tooltip("被 STT 锁定时播放的长接触音（循环）。")]
        [SerializeField] private AudioClip lockLoopClip;
        [Tooltip("导弹主动雷达开机时播放的锁定音（循环，最高优先级）。")]
        [SerializeField] private AudioClip missileLoopClip;
        [Tooltip("Seconds before the missile warning clip ends to start its next repeat.")]
        [SerializeField, Min(0f)] private float missileRepeatOverlapSeconds = 0.05f;

        private MechRadarController radar;
        private MechFlightMotionController flightMotion;
        private MechEngineController engineController;
        private AudioSource oneShotSource;
        private AudioSource loopSource;
        private readonly ThreatSnapshot[] threatScratch =
            new ThreatSnapshot[EsmThreatCorrelatorLimits.MaxThreats];
        private readonly MissileTrackSnapshot[] missileTrackScratch =
            new MissileTrackSnapshot[64];
        private int threatCount;
        private int missileTrackCount;
        private EsmThreatLevel previousHighestLevel;
        private float nextTrackedPulseTime = -1f;
        private int trackedPulseIndex;
        private float nextMissileRepeatTime = -1f;
        private Texture2D ringTexture;
        private Texture2D discBackgroundTexture;
        private Texture2D fullRingMaskTexture;
        private Texture2D solidTexture;
        private readonly Texture2D[] sectorMaskTextures = new Texture2D[SectorCount];
        private readonly ThreatSnapshot[] sectorThreats = new ThreatSnapshot[SectorCount];
        private readonly int[] sectorThreatCounts = new int[SectorCount];
        private readonly bool[] sectorOccupied = new bool[SectorCount];
        private GUIStyle sectorLabelStyle;
        private GUIStyle statusStyle;

        private static class EsmThreatCorrelatorLimits
        {
            public const int MaxThreats = 16; // 与 EsmThreatCorrelator.MaxThreats 一致（asmdef 单向依赖，不直接引用）。
        }

        private void Awake()
        {
            radar = GetComponent<MechRadarController>();
            flightMotion = GetComponent<MechFlightMotionController>();
            engineController = GetComponent<MechEngineController>();
            LoadDefaultAudioClips();
            PreloadAudioClips();
            oneShotSource = gameObject.AddComponent<AudioSource>();
            oneShotSource.playOnAwake = false;
            oneShotSource.spatialBlend = 0f;
            loopSource = gameObject.AddComponent<AudioSource>();
            loopSource.playOnAwake = false;
            loopSource.loop = true;
            loopSource.spatialBlend = 0f;
        }

        private void LoadDefaultAudioClips()
        {
            if (spikeClip == null)
            {
                spikeClip = Resources.Load<AudioClip>("Audio/RWR/contact_oneshot");
            }

            if (trackedClip == null)
            {
                trackedClip = Resources.Load<AudioClip>("Audio/RWR/contact");
            }

            if (lockLoopClip == null)
            {
                lockLoopClip = Resources.Load<AudioClip>("Audio/RWR/contact_loop");
            }

            if (missileLoopClip == null)
            {
                missileLoopClip = Resources.Load<AudioClip>("Audio/RWR/radar_lock");
            }
        }

        private void PreloadAudioClips()
        {
            spikeClip?.LoadAudioData();
            trackedClip?.LoadAudioData();
            lockLoopClip?.LoadAudioData();
            missileLoopClip?.LoadAudioData();
        }

        private void OnDestroy()
        {
            if (ringTexture != null) Destroy(ringTexture);
            if (discBackgroundTexture != null) Destroy(discBackgroundTexture);
            if (fullRingMaskTexture != null) Destroy(fullRingMaskTexture);
            if (solidTexture != null) Destroy(solidTexture);
            for (var i = 0; i < sectorMaskTextures.Length; i++)
            {
                if (sectorMaskTextures[i] != null)
                {
                    Destroy(sectorMaskTextures[i]);
                }
            }
        }

        private void Update()
        {
            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
                if (radar == null) return;
            }

            threatCount = radar.CopyThreats(threatScratch);
            missileTrackCount = radar.CopyMissileTracks(missileTrackScratch);
            UpdateAudio();
        }

        private void UpdateAudio()
        {
            oneShotSource.volume = rwrMasterVolume;
            loopSource.volume = rwrMasterVolume;
            ResolveEngineController();
            if (engineController != null &&
                engineController.GetHudStartupAlpha(
                    MechStartupHudModule.ThreatWarning) < 0.98f)
            {
                oneShotSource?.Stop();
                loopSource?.Stop();
                previousHighestLevel = EsmThreatLevel.None;
                return;
            }

            var highest = EsmThreatLevel.None;
            for (var i = 0; i < threatCount; i++)
            {
                var alertLevel = ResolveAlertLevel(in threatScratch[i]);
                if (alertLevel > highest)
                {
                    highest = alertLevel;
                }
            }

            var enteredSpiked =
                highest == EsmThreatLevel.Spiked &&
                previousHighestLevel < EsmThreatLevel.Spiked;
            if (enteredSpiked && spikeClip != null)
            {
                oneShotSource.PlayOneShot(spikeClip);
            }

            UpdateTrackedPulseAudio(highest);

            // 跟踪使用成组短音；锁定使用持续循环音；导弹告警优先。
            if (highest == EsmThreatLevel.MissileActive &&
                missileLoopClip != null)
            {
                var repeatPeriod = Mathf.Max(
                    0.05f,
                    missileLoopClip.length - missileRepeatOverlapSeconds);
                if (loopSource.clip != missileLoopClip || loopSource.loop)
                {
                    loopSource.Stop();
                    loopSource.loop = false;
                    loopSource.clip = missileLoopClip;
                    loopSource.PlayOneShot(missileLoopClip);
                    nextMissileRepeatTime =
                        Time.unscaledTime + repeatPeriod;
                }
                else if (Time.unscaledTime >= nextMissileRepeatTime)
                {
                    loopSource.PlayOneShot(missileLoopClip);
                    nextMissileRepeatTime =
                        Time.unscaledTime + repeatPeriod;
                }
            }
            else
            {
                nextMissileRepeatTime = -1f;
                var desiredLoop =
                    highest == EsmThreatLevel.Locked ? lockLoopClip : null;
                if (loopSource.clip != desiredLoop || !loopSource.loop)
                {
                    loopSource.Stop();
                    loopSource.loop = true;
                    loopSource.clip = desiredLoop;
                    if (desiredLoop != null)
                    {
                        loopSource.Play();
                    }
                }
            }

            previousHighestLevel = highest;
        }

        private void UpdateTrackedPulseAudio(EsmThreatLevel highest)
        {
            if (highest != EsmThreatLevel.Tracked || trackedClip == null)
            {
                nextTrackedPulseTime = -1f;
                trackedPulseIndex = 0;
                return;
            }

            var now = Time.unscaledTime;
            if (previousHighestLevel != EsmThreatLevel.Tracked ||
                nextTrackedPulseTime < 0f)
            {
                oneShotSource.PlayOneShot(trackedClip);
                trackedPulseIndex = 1;
                nextTrackedPulseTime =
                    now + trackedClip.length + trackedPulseGapSeconds;
                return;
            }

            if (now < nextTrackedPulseTime)
            {
                return;
            }

            oneShotSource.PlayOneShot(trackedClip);
            if (trackedPulseIndex == 1)
            {
                trackedPulseIndex = 0;
                nextTrackedPulseTime =
                    now + trackedClip.length + trackedCyclePauseSeconds;
            }
            else
            {
                trackedPulseIndex = 1;
                nextTrackedPulseTime =
                    now + trackedClip.length + trackedPulseGapSeconds;
            }
        }

        private void OnGUI()
        {
            if (MechAssemblyMenu.IsAnyMenuVisible)
            {
                return;
            }

            ResolveEngineController();
            var startupAlpha = engineController != null
                ? engineController.GetHudStartupAlpha(
                    MechStartupHudModule.ThreatWarning)
                : 1f;
            if (startupAlpha <= 0.001f)
            {
                return;
            }

            var previousGuiColor = GUI.color;
            var startupTint = previousGuiColor;
            startupTint.a *= startupAlpha;
            GUI.color = startupTint;
            EnsureAssets();
            var panel = new Rect(
                PanelMargin,
                Screen.height - PanelSize - PanelMargin,
                PanelSize,
                PanelSize);
            var center = panel.center;
            var ringRect = new Rect(
                center.x - RingRadius,
                center.y - RingRadius,
                RingRadius * 2f,
                RingRadius * 2f);
            GUI.DrawTexture(ringRect, discBackgroundTexture);

            // 机头朝上：方位取飞控逻辑航向（与战术图同基准），退化用 transform。
            var forward = flightMotion != null
                ? flightMotion.LogicalRotation * Vector3.forward
                : transform.forward;
            var headingDegrees = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            CollectSectorThreats(headingDegrees, out var highestLevel, out var closeMissile);
            var pulse = 0.78f + Mathf.Sin(Time.unscaledTime * 8f) * 0.22f;
            if (closeMissile)
            {
                DrawTintedTexture(
                    ringRect,
                    fullRingMaskTexture,
                    new Color(0f, 1f, 0.28f, 0.72f + pulse * 0.22f));
            }
            else
            {
                for (var sector = 0; sector < SectorCount; sector++)
                {
                    if (!sectorOccupied[sector])
                    {
                        continue;
                    }

                    DrawTintedTexture(
                        ringRect,
                        sectorMaskTextures[sector],
                        SectorColor(in sectorThreats[sector], pulse));
                }
            }

            GUI.DrawTexture(ringRect, ringTexture);

            if (!closeMissile)
            {
                DrawSectorLabels(center);
            }

            var status = closeMissile
                ? "敌导弹"
                : HighestStatus(highestLevel);
            if (!string.IsNullOrEmpty(status))
            {
                var statusRect = new Rect(panel.center.x - 60f, panel.y + 5f, 120f, 30f);
                DrawOutline(statusRect, new Color(0.05f, 1f, 0.34f, 0.9f), 1f);
                statusStyle.normal.textColor = closeMissile
                    ? new Color(0.12f, 1f, 0.34f, pulse)
                    : new Color(0.18f, 0.92f, 0.34f, 0.94f);
                GUI.Label(statusRect, status, statusStyle);
            }
            GUI.color = previousGuiColor;
        }

        private void ResolveEngineController()
        {
            if (engineController == null)
            {
                engineController = GetComponent<MechEngineController>();
            }
        }

        private void EnsureAssets()
        {
            if (ringTexture != null)
            {
                return;
            }

            solidTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                name = "RWR Solid Pixel",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
            };
            solidTexture.SetPixel(0, 0, Color.white);
            solidTexture.Apply();

            ringTexture = BuildRingTexture();
            discBackgroundTexture = BuildDiscBackgroundTexture();
            fullRingMaskTexture = BuildRingMaskTexture(-1);
            for (var sector = 0; sector < SectorCount; sector++)
            {
                sectorMaskTextures[sector] = BuildRingMaskTexture(sector);
            }

            sectorLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }

        private void CollectSectorThreats(
            float headingDegrees,
            out EsmThreatLevel highestLevel,
            out bool closeMissile)
        {
            highestLevel = EsmThreatLevel.None;
            closeMissile = false;
            for (var sector = 0; sector < SectorCount; sector++)
            {
                sectorOccupied[sector] = false;
                sectorThreatCounts[sector] = 0;
            }

            for (var i = 0; i < threatCount; i++)
            {
                var threat = threatScratch[i];
                if (threat.Level == EsmThreatLevel.None)
                {
                    continue;
                }

                var alertLevel = ResolveAlertLevel(in threat);
                if (alertLevel > highestLevel)
                {
                    highestLevel = alertLevel;
                }
                if (IsCloseMissile(in threat))
                {
                    closeMissile = true;
                }

                var worldAzimuth = Mathf.Atan2(
                    threat.DirectionWorld.x,
                    threat.DirectionWorld.z) * Mathf.Rad2Deg;
                var relativeAzimuth = Mathf.Repeat(
                    worldAzimuth - headingDegrees + 360f,
                    360f);
                var sector = Mathf.FloorToInt(
                    (relativeAzimuth + SectorDegrees * 0.5f) /
                    SectorDegrees) % SectorCount;
                sectorThreatCounts[sector]++;
                if (!sectorOccupied[sector] ||
                    ThreatPriority(in threat) > ThreatPriority(in sectorThreats[sector]))
                {
                    sectorThreats[sector] = threat;
                    sectorOccupied[sector] = true;
                }
            }
        }

        private void DrawSectorLabels(Vector2 center)
        {
            const float labelRadius = 130f;
            for (var sector = 0; sector < SectorCount; sector++)
            {
                if (!sectorOccupied[sector])
                {
                    continue;
                }

                var angle = sector * SectorDegrees * Mathf.Deg2Rad;
                var position = center + new Vector2(
                    Mathf.Sin(angle),
                    -Mathf.Cos(angle)) * labelRadius;
                var label = ThreatModelLabel(in sectorThreats[sector]);
                if (sectorThreatCounts[sector] > 1)
                {
                    label += $"×{sectorThreatCounts[sector]}";
                }

                sectorLabelStyle.normal.textColor =
                    sectorThreats[sector].Level == EsmThreatLevel.MissileActive
                        ? new Color(0.25f, 1f, 0.32f)
                        : new Color(0.42f, 0.95f, 0.48f);
                GUI.Label(
                    new Rect(position.x - 44f, position.y - 12f, 88f, 24f),
                    label,
                    sectorLabelStyle);
            }
        }

        private bool IsCloseMissile(in ThreatSnapshot threat)
        {
            if (threat.Level != EsmThreatLevel.MissileActive ||
                (!EsmWaveformClassification.IsMissileSeeker(
                     threat.WaveformClass) &&
                 threat.WaveformClass != EsmWaveformClass.InfraredPlume) ||
                !HasCorrelatedIncomingMissile(in threat))
            {
                return false;
            }

            var missileSeeker =
                EsmWaveformClassification.IsMissileSeeker(
                    threat.WaveformClass);
            var receivedPower = missileSeeker
                ? threat.MainlobeReceivedPowerDb
                : threat.ReceivedPowerDb;
            var powerTrend = missileSeeker
                ? threat.MainlobePowerTrendDbPerSecond
                : threat.PowerTrendDbPerSecond;
            return receivedPower >= immediateMissileReceivedPowerDb ||
                   (receivedPower >= closeMissileReceivedPowerDb &&
                    powerTrend >= closeMissilePowerTrendDbPerSecond);
        }

        private EsmThreatLevel ResolveAlertLevel(in ThreatSnapshot threat)
        {
            if (threat.Level != EsmThreatLevel.MissileActive)
            {
                return threat.Level;
            }

            return HasCorrelatedIncomingMissile(in threat)
                ? EsmThreatLevel.MissileActive
                : EsmThreatLevel.None;
        }

        private bool HasCorrelatedIncomingMissile(in ThreatSnapshot threat)
        {
            if (!EsmWaveformClassification.IsMissileSeeker(
                    threat.WaveformClass) &&
                threat.WaveformClass != EsmWaveformClass.InfraredPlume)
            {
                return false;
            }

            var minimumDot = Mathf.Cos(
                MissileThreatAssociationDegrees * Mathf.Deg2Rad);
            var now = radar != null ? radar.SimTimeSeconds : Time.timeAsDouble;
            var missileSeeker =
                EsmWaveformClassification.IsMissileSeeker(
                    threat.WaveformClass);
            if (missileSeeker &&
                (threat.LastMainlobeSeenTime < 0.0 ||
                 now - threat.LastMainlobeSeenTime >
                    MissileSeekerMainlobeMaximumAgeSeconds))
            {
                return false;
            }

            for (var i = 0; i < missileTrackCount; i++)
            {
                var track = missileTrackScratch[i];
                if (!track.IsWarning || track.IsFriendly ||
                    (missileSeeker
                        ? !track.IsLikelyTargetingOwnship
                        : !track.Incoming))
                {
                    continue;
                }

                var cpaGate = MissileThreatMaximumCpaMeters +
                    Mathf.Min(
                        MissileThreatUncertaintyAllowanceMeters,
                        Mathf.Max(0f, track.CpaUncertaintyMeters));
                if (track.ClosestApproachDistanceMeters > cpaGate)
                {
                    continue;
                }

                var predictedPosition = track.PredictPosition(now);
                var relativePosition = new Vector3(
                    (float)predictedPosition.x - transform.position.x,
                    (float)predictedPosition.y - transform.position.y,
                    (float)predictedPosition.z - transform.position.z);
                if (relativePosition.sqrMagnitude <= 1f)
                {
                    return true;
                }

                if (Vector3.Dot(
                        relativePosition.normalized,
                        new Vector3(
                            threat.DirectionWorld.x,
                            threat.DirectionWorld.y,
                            threat.DirectionWorld.z)) >= minimumDot)
                {
                    return true;
                }
            }

            return false;
        }

        private static float ThreatPriority(in ThreatSnapshot threat)
        {
            var classPriority = threat.WaveformClass switch
            {
                EsmWaveformClass.MissileSeeker => 40f,
                EsmWaveformClass.AirToAirMissileSeeker => 42f,
                EsmWaveformClass.InterceptorMissileSeeker => 38f,
                EsmWaveformClass.InfraredPlume => 35f,
                EsmWaveformClass.FireControlRadar => 30f,
                EsmWaveformClass.DataLinkUplink => 20f,
                EsmWaveformClass.SearchRadar => 10f,
                _ => 0f,
            };
            return (int)threat.Level * 1000f +
                   classPriority +
                   threat.IdentificationConfidence * 5f +
                   threat.ReceivedPowerDb * 0.01f;
        }

        private static string ThreatModelLabel(in ThreatSnapshot threat)
        {
            if (threat.IdentificationConfidence < IdentificationThreshold &&
                threat.WaveformClass == EsmWaveformClass.Unknown)
            {
                return "UNK";
            }

            return threat.WaveformClass switch
            {
                EsmWaveformClass.SearchRadar => $"SR{threat.WaveformId:D2}",
                EsmWaveformClass.FireControlRadar => $"FCR{threat.WaveformId:D2}",
                EsmWaveformClass.MissileSeeker => "MSL",
                EsmWaveformClass.AirToAirMissileSeeker => "AAM",
                EsmWaveformClass.InterceptorMissileSeeker => "INT",
                EsmWaveformClass.DataLinkUplink => $"DL{threat.WaveformId:D2}",
                EsmWaveformClass.InfraredPlume => "IR",
                _ => $"U{threat.WaveformId:D2}",
            };
        }

        private static string HighestStatus(EsmThreatLevel level)
        {
            return level switch
            {
                EsmThreatLevel.MissileActive => "导弹逼近",
                EsmThreatLevel.Locked => "敌锁定",
                EsmThreatLevel.Tracked => "敌跟踪",
                _ => string.Empty,
            };
        }

        private static Color SectorColor(in ThreatSnapshot threat, float pulse)
        {
            return threat.Level switch
            {
                EsmThreatLevel.MissileActive =>
                    new Color(0f, 0.95f, 0.22f, 0.68f + pulse * 0.2f),
                EsmThreatLevel.Locked =>
                    new Color(0f, 0.82f, 0.22f, 0.48f + pulse * 0.12f),
                EsmThreatLevel.Tracked =>
                    new Color(0f, 0.7f, 0.22f, 0.4f),
                _ =>
                    new Color(0f, 0.52f, 0.2f, 0.28f),
            };
        }

        private Texture2D BuildRingTexture()
        {
            var texture = NewHudTexture("RWR Ring");
            var pixels = new Color[RingTextureSize * RingTextureSize];
            var center = new Vector2(RingTextureSize * 0.5f, RingTextureSize * 0.5f);
            const float outerRadius = 248f;
            const float innerRadius = 124f;
            var color = new Color(0.06f, 1f, 0.32f);

            for (var y = 0; y < RingTextureSize; y++)
            {
                for (var x = 0; x < RingTextureSize; x++)
                {
                    var delta = new Vector2(x + 0.5f, y + 0.5f) - center;
                    var distance = delta.magnitude;
                    var angle = Mathf.Repeat(
                        Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg + 360f,
                        360f);
                    var alpha = distance >= innerRadius && distance <= outerRadius
                        ? 0.055f
                        : 0f;
                    alpha = Mathf.Max(
                        alpha,
                        RingLineAlpha(distance, outerRadius, 2.5f) * 0.92f);
                    alpha = Mathf.Max(
                        alpha,
                        RingLineAlpha(distance, innerRadius, 2f) * 0.72f);

                    if (distance > innerRadius + 1f && distance < outerRadius - 1f)
                    {
                        var phase = Mathf.Repeat(
                            angle + SectorDegrees * 0.5f,
                            SectorDegrees);
                        var degreesToBoundary = Mathf.Min(phase, SectorDegrees - phase);
                        var pixelsToBoundary =
                            distance * degreesToBoundary * Mathf.Deg2Rad;
                        alpha = Mathf.Max(alpha, Mathf.Clamp01(2.4f - pixelsToBoundary) * 0.36f);
                    }

                    if (distance > 34f && distance < innerRadius - 4f)
                    {
                        var diagonalDistance = Mathf.Abs(Mathf.Abs(delta.x) - Mathf.Abs(delta.y));
                        var dash = Mathf.Repeat(distance, 28f) < 16f;
                        if (diagonalDistance < 1.8f && dash)
                        {
                            alpha = Mathf.Max(alpha, 0.42f);
                        }
                    }

                    alpha = Mathf.Max(alpha, RingLineAlpha(distance, 32f, 2.2f) * 0.9f);
                    if ((Mathf.Abs(delta.x) < 2.2f && Mathf.Abs(delta.y) < 22f) ||
                        (Mathf.Abs(delta.y) < 2.2f && Mathf.Abs(delta.x) < 22f))
                    {
                        alpha = Mathf.Max(alpha, 0.95f);
                    }

                    pixels[y * RingTextureSize + x] =
                        new Color(color.r, color.g, color.b, Mathf.Clamp01(alpha));
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D BuildDiscBackgroundTexture()
        {
            var texture = NewHudTexture("RWR Disc Background");
            var pixels = new Color[RingTextureSize * RingTextureSize];
            var center = new Vector2(RingTextureSize * 0.5f, RingTextureSize * 0.5f);
            const float radius = 248f;
            var background = new Color(0f, 0.025f, 0.02f, 0.82f);

            for (var y = 0; y < RingTextureSize; y++)
            {
                for (var x = 0; x < RingTextureSize; x++)
                {
                    var distance = Vector2.Distance(
                        new Vector2(x + 0.5f, y + 0.5f),
                        center);
                    var edgeAlpha = Mathf.Clamp01(radius - distance + 0.5f);
                    pixels[y * RingTextureSize + x] = new Color(
                        background.r,
                        background.g,
                        background.b,
                        background.a * edgeAlpha);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private Texture2D BuildRingMaskTexture(int sectorIndex)
        {
            var texture = NewHudTexture(
                sectorIndex < 0 ? "RWR Full Alert Mask" : $"RWR Sector {sectorIndex}");
            var pixels = new Color[RingTextureSize * RingTextureSize];
            var center = new Vector2(RingTextureSize * 0.5f, RingTextureSize * 0.5f);
            const float outerRadius = 248f;
            const float innerRadius = 124f;
            var sectorCenter = sectorIndex * SectorDegrees;

            for (var y = 0; y < RingTextureSize; y++)
            {
                for (var x = 0; x < RingTextureSize; x++)
                {
                    var delta = new Vector2(x + 0.5f, y + 0.5f) - center;
                    var distance = delta.magnitude;
                    var radialAlpha = RingBandAlpha(distance, innerRadius, outerRadius);
                    var alpha = radialAlpha;
                    if (sectorIndex >= 0 && alpha > 0f)
                    {
                        var angle = Mathf.Repeat(
                            Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg + 360f,
                            360f);
                        var edgeDistanceDegrees =
                            SectorDegrees * 0.5f - 1.4f -
                            Mathf.Abs(Mathf.DeltaAngle(angle, sectorCenter));
                        var edgeDistancePixels =
                            edgeDistanceDegrees * Mathf.Deg2Rad * distance;
                        alpha *= Mathf.Clamp01(edgeDistancePixels + 1.5f);
                    }

                    pixels[y * RingTextureSize + x] =
                        new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
                }
            }

            texture.SetPixels(pixels);
            texture.Apply();
            return texture;
        }

        private static Texture2D NewHudTexture(string textureName)
        {
            return new Texture2D(
                RingTextureSize,
                RingTextureSize,
                TextureFormat.RGBA32,
                false)
            {
                name = textureName,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        private static float RingBandAlpha(float distance, float innerRadius, float outerRadius)
        {
            var innerEdge = Mathf.Clamp01(distance - innerRadius + 0.5f);
            var outerEdge = Mathf.Clamp01(outerRadius - distance + 0.5f);
            return Mathf.Min(innerEdge, outerEdge);
        }

        private static float RingLineAlpha(float distance, float radius, float halfWidth)
        {
            return Mathf.Clamp01(halfWidth + 0.5f - Mathf.Abs(distance - radius));
        }

        private static void DrawTintedTexture(Rect rect, Texture texture, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = new Color(
                color.r * previousColor.r,
                color.g * previousColor.g,
                color.b * previousColor.b,
                color.a * previousColor.a);
            GUI.DrawTexture(rect, texture);
            GUI.color = previousColor;
        }

        private void DrawOutline(Rect rect, Color color, float thickness)
        {
            DrawTintedTexture(new Rect(rect.x, rect.y, rect.width, thickness), solidTexture, color);
            DrawTintedTexture(
                new Rect(rect.x, rect.yMax - thickness, rect.width, thickness),
                solidTexture,
                color);
            DrawTintedTexture(new Rect(rect.x, rect.y, thickness, rect.height), solidTexture, color);
            DrawTintedTexture(
                new Rect(rect.xMax - thickness, rect.y, thickness, rect.height),
                solidTexture,
                color);
        }
    }
}
