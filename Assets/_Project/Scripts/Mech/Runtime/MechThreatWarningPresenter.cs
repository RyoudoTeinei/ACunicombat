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
        private const float PanelSize = 190f;
        private const float PanelMargin = 18f;
        private const float RingRadius = 74f;

        [Header("音频占位（TODO：接入 AC6 解包告警音或合成音）")]
        [Tooltip("被搜索扫描单响。")]
        [SerializeField] private AudioClip spikeClip;
        [Tooltip("升级为被跟踪时单响。")]
        [SerializeField] private AudioClip trackedClip;
        [Tooltip("被锁定持续音（循环）。")]
        [SerializeField] private AudioClip lockLoopClip;
        [Tooltip("导弹主动雷达开机尖啸（循环，最高优先级）。")]
        [SerializeField] private AudioClip missileLoopClip;

        private MechRadarController radar;
        private MechFlightMotionController flightMotion;
        private AudioSource oneShotSource;
        private AudioSource loopSource;
        private readonly ThreatSnapshot[] threatScratch =
            new ThreatSnapshot[EsmThreatCorrelatorLimits.MaxThreats];
        private int threatCount;
        private EsmThreatLevel previousHighestLevel;
        private Texture2D ringTexture;
        private GUIStyle blipStyle;
        private GUIStyle captionStyle;

        // 告警历史（P2）：级别变化事件环，面板下方滚动显示。
        private const int HistoryCapacity = 4;
        private readonly string[] historyLines = new string[HistoryCapacity];
        private int historyCount;
        private readonly uint[] knownThreatKeys = new uint[EsmThreatCorrelatorLimits.MaxThreats];
        private readonly EsmThreatLevel[] knownThreatLevels =
            new EsmThreatLevel[EsmThreatCorrelatorLimits.MaxThreats];

        private static class EsmThreatCorrelatorLimits
        {
            public const int MaxThreats = 16; // 与 EsmThreatCorrelator.MaxThreats 一致（asmdef 单向依赖，不直接引用）。
        }

        private void Awake()
        {
            radar = GetComponent<MechRadarController>();
            flightMotion = GetComponent<MechFlightMotionController>();
            oneShotSource = gameObject.AddComponent<AudioSource>();
            oneShotSource.playOnAwake = false;
            oneShotSource.spatialBlend = 0f;
            loopSource = gameObject.AddComponent<AudioSource>();
            loopSource.playOnAwake = false;
            loopSource.loop = true;
            loopSource.spatialBlend = 0f;
        }

        private void OnDestroy()
        {
            if (ringTexture != null) Destroy(ringTexture);
        }

        private void Update()
        {
            if (radar == null)
            {
                radar = GetComponent<MechRadarController>();
                if (radar == null) return;
            }

            threatCount = radar.CopyThreats(threatScratch);
            UpdateHistory();
            UpdateAudio();
        }

        private void UpdateHistory()
        {
            for (var i = 0; i < threatCount; i++)
            {
                var threat = threatScratch[i];
                var slot = threat.ThreatId.Value.Index;
                if ((uint)slot >= knownThreatKeys.Length)
                {
                    continue;
                }

                if (knownThreatKeys[slot] != threat.ThreatId.Packed)
                {
                    knownThreatKeys[slot] = threat.ThreatId.Packed;
                    knownThreatLevels[slot] = EsmThreatLevel.None;
                }

                if (threat.Level == knownThreatLevels[slot])
                {
                    continue;
                }

                var rising = threat.Level > knownThreatLevels[slot];
                knownThreatLevels[slot] = threat.Level;
                if (!rising)
                {
                    continue; // 只记录升级事件，降级不刷屏。
                }

                var label = threat.WaveformClass switch
                {
                    EsmWaveformClass.DataLinkUplink => "上行链路",
                    EsmWaveformClass.InfraredPlume => "红外羽焰",
                    EsmWaveformClass.MissileSeeker => "导引头",
                    EsmWaveformClass.SearchRadar => "搜索雷达",
                    EsmWaveformClass.FireControlRadar => "火控雷达",
                    _ => "未知辐射",
                };
                var levelName = threat.Level switch
                {
                    EsmThreatLevel.MissileActive => "导弹!",
                    EsmThreatLevel.Locked => "锁定",
                    EsmThreatLevel.Tracked => "跟踪",
                    _ => "扫描",
                };
                historyLines[historyCount % HistoryCapacity] =
                    $"{Time.time:F0}s {label}→{levelName}";
                historyCount++;
            }
        }

        private void UpdateAudio()
        {
            var highest = EsmThreatLevel.None;
            for (var i = 0; i < threatCount; i++)
            {
                if (threatScratch[i].Level > highest)
                {
                    highest = threatScratch[i].Level;
                }
            }

            if (highest > previousHighestLevel)
            {
                // 升级瞬间的单响提示（占位：clip 为空则静默）。
                var clip = highest switch
                {
                    EsmThreatLevel.Spiked => spikeClip,
                    EsmThreatLevel.Tracked => trackedClip,
                    _ => null,
                };
                if (clip != null)
                {
                    oneShotSource.PlayOneShot(clip);
                }
            }

            // 锁定/导弹为持续循环音，导弹优先。
            var desiredLoop = highest switch
            {
                EsmThreatLevel.MissileActive => missileLoopClip,
                EsmThreatLevel.Locked => lockLoopClip,
                _ => null,
            };
            if (loopSource.clip != desiredLoop)
            {
                loopSource.Stop();
                loopSource.clip = desiredLoop;
                if (desiredLoop != null)
                {
                    loopSource.Play();
                }
            }

            previousHighestLevel = highest;
        }

        private void OnGUI()
        {
            EnsureAssets();
            var panel = new Rect(
                PanelMargin,
                Screen.height - PanelSize - PanelMargin,
                PanelSize,
                PanelSize);
            GUI.Box(panel, GUIContent.none);
            var center = new Vector2(panel.x + PanelSize * 0.5f, panel.y + PanelSize * 0.5f);
            GUI.DrawTexture(
                new Rect(center.x - RingRadius, center.y - RingRadius, RingRadius * 2f, RingRadius * 2f),
                ringTexture);

            captionStyle.normal.textColor = new Color(0.55f, 0.75f, 0.6f);
            GUI.Label(new Rect(panel.x + 8f, panel.y + 4f, 120f, 18f), "RWR", captionStyle);

            // 机头朝上：方位取飞控逻辑航向（与战术图同基准），退化用 transform。
            var forward = flightMotion != null
                ? flightMotion.LogicalRotation * Vector3.forward
                : transform.forward;
            var headingDegrees = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;

            // 本机中心标记。
            captionStyle.normal.textColor = new Color(0.4f, 0.9f, 0.8f);
            GUI.Label(new Rect(center.x - 6f, center.y - 9f, 20f, 18f), "Λ", captionStyle);

            var blink = Time.unscaledTime % 0.5f < 0.3f;
            for (var i = 0; i < threatCount; i++)
            {
                var threat = threatScratch[i];
                if (threat.Level == EsmThreatLevel.None)
                {
                    continue;
                }

                var azimuthDegrees = Mathf.Atan2(
                    threat.DirectionWorld.x, threat.DirectionWorld.z) * Mathf.Rad2Deg - headingDegrees;
                var azimuthRadians = azimuthDegrees * Mathf.Deg2Rad;
                var blipPosition = center + new Vector2(
                    Mathf.Sin(azimuthRadians), -Mathf.Cos(azimuthRadians)) * RingRadius;

                var (symbol, color) = threat.Level switch
                {
                    EsmThreatLevel.MissileActive => ("M", new Color(1f, 0.25f, 0.2f)),
                    EsmThreatLevel.Locked => ("L", new Color(1f, 0.45f, 0.15f)),
                    EsmThreatLevel.Tracked => ("T", new Color(1f, 0.85f, 0.25f)),
                    _ => ("S", new Color(0.5f, 0.9f, 0.5f)),
                };
                if (threat.WaveformClass == EsmWaveformClass.DataLinkUplink)
                {
                    symbol = "D"; // 制导上行：有弹在飞的软告警，级别色照常。
                }

                if (threat.Level >= EsmThreatLevel.Locked && !blink)
                {
                    continue; // 锁定/导弹级闪烁。
                }

                blipStyle.normal.textColor = color;
                GUI.Label(new Rect(blipPosition.x - 8f, blipPosition.y - 10f, 22f, 20f), symbol, blipStyle);
            }

            // 告警历史 + 对抗提示（面板下方）。
            var lineY = panel.yMax + 2f;
            var historyShown = Mathf.Min(historyCount, HistoryCapacity);
            captionStyle.normal.textColor = new Color(0.6f, 0.7f, 0.62f);
            for (var i = 0; i < historyShown; i++)
            {
                var line = historyLines[(historyCount - 1 - i) % HistoryCapacity];
                GUI.Label(new Rect(panel.x, lineY, 260f, 16f), line, captionStyle);
                lineY += 15f;
            }

            if (previousHighestLevel >= EsmThreatLevel.Locked && blink)
            {
                captionStyle.normal.textColor = previousHighestLevel == EsmThreatLevel.MissileActive
                    ? new Color(1f, 0.3f, 0.25f)
                    : new Color(1f, 0.5f, 0.2f);
                GUI.Label(
                    new Rect(panel.x, panel.y - 18f, 260f, 16f),
                    previousHighestLevel == EsmThreatLevel.MissileActive
                        ? "导弹告警 — 全力规避 / 切断视线"
                        : "被锁定 — QB 破锁 / 遮蔽机动",
                    captionStyle);
            }
        }

        private void EnsureAssets()
        {
            if (ringTexture != null)
            {
                return;
            }

            const int size = 160;
            ringTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var ringColor = new Color(0.35f, 0.65f, 0.45f, 0.9f);
            var clear = new Color(0f, 0f, 0f, 0f);
            var outer = size * 0.5f - 1f;
            var inner = outer - 2f;
            var center = new Vector2(size * 0.5f, size * 0.5f);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    ringTexture.SetPixel(x, y, d <= outer && d >= inner ? ringColor : clear);
                }
            }

            ringTexture.Apply();

            blipStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            captionStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
        }
    }
}
