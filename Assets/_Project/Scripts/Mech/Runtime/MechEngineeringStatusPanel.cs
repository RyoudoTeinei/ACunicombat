using System;
using UnityEngine;
using UnityEngine.UI;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// 右上角工程状态面板（UGUI，运行时全代码构建，无场景/预制体依赖）。
    /// 四个信息维度：
    /// 1. 六向推力指针仪 —— 老式航天器风格的六指针平动推力指示，读实际喷口推力；
    /// 2. 热管理 —— 当前配装热容量、散热能力与红外通道温度（只读现有仿真，不自建热模型）；
    /// 3. 装甲 —— 整体装甲/结构当前值 + 头/核心/手臂/腿的配装容量分条；
    /// 4. 功耗 —— 发电机输出对当前推进+雷达功耗，含余量。
    /// 视觉语言沿用现有 HUD 的绿色荧光风格，并接入发动机启动的 VitalStatus 渐入。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechEngineeringStatusPanel : MonoBehaviour
    {
        // ---- 配色（与 MechRadarHudPresenter 的荧光绿 HUD 一致）----
        private static readonly Color HudGreen = new(0.30f, 1f, 0.50f, 1f);
        private static readonly Color HudDimGreen = new(0.18f, 0.78f, 0.36f, 0.76f);
        private static readonly Color HudCyan = new(0.12f, 0.88f, 0.94f, 0.96f);
        private static readonly Color HudAmber = new(1f, 0.72f, 0.18f, 0.95f);
        private static readonly Color HudRed = new(1f, 0.18f, 0.12f, 1f);
        private static readonly Color HudWhite = new(0.82f, 0.92f, 0.86f, 0.95f);
        private static readonly Color PanelBackground = new(0.008f, 0.025f, 0.022f, 0.90f);
        private static readonly Color BarBackground = new(0.01f, 0.11f, 0.065f, 0.95f);
        private static readonly Color BarTick = new(0.005f, 0.06f, 0.035f, 0.85f);

        // ---- 面板几何（Canvas 1920x1080 参考分辨率下的单位）----
        private const float PanelWidth = 880f;
        private const float PanelHeight = 344f;
        private const float PanelMargin = 18f;
        private const float GaugeCellWidth = 344f;
        private const float ColumnX = 360f;      // 右列起点（相对面板右上角）
        private const float ColumnWidth = PanelWidth - ColumnX - 18f;

        // ---- 指针仪纹理 ----
        private const int GaugeTextureSize = 1024;
        private const float GaugeDisplaySize = 296f;   // RawImage 显示尺寸
        private const float GaugeRadiusTexture = 408f; // 纹理空间外环半径

        // ---- 雷达功耗占峰值比例（占位系数；雷达零件暂无功耗字段）----
        private const float RadarScanShareOfPeak = 0.03f;
        private const float RadarTrackShareOfPeak = 0.06f;

        // ---- 刷新节流 ----
        private const float TextRefreshIntervalSeconds = 0.1f;
        private const float GaugeSmoothing = 8f;
        private const float BarSmoothing = 7f;

        // 六个轴向在屏幕空间（y 向下）的方向，绕六边形顺时针：前/右/下/后/左/上。
        private static readonly Vector2[] AxisDirections =
        {
            new(0f, -1f),
            new(0.866f, -0.5f),
            new(0.866f, 0.5f),
            new(0f, 1f),
            new(-0.866f, 0.5f),
            new(-0.866f, -0.5f),
        };

        private static readonly string[] AxisLabels = { "前", "右", "下", "后", "左", "上" };

        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private MechRuntimeState runtimeState;
        [SerializeField] private CombatTestTarget protection;
        [SerializeField] private MechThermalSignature thermalSignature;
        [SerializeField] private MechRadarController radar;
        [SerializeField] private bool visible = true;

        private GameObject canvasRoot;
        private CanvasGroup canvasGroup;
        private RawImage gaugeImage;
        private Texture2D gaugeTexture;
        private Color32[] gaugePixels;
        private Font hudFont;

        private readonly float[] axisDemands = new float[6];
        private readonly float[] displayedDemands = new float[6];
        private readonly Text[] axisForceTexts = new Text[6];

        private Text thermalCapacityValue;
        private Text thermalDissipationValue;
        private Text thermalChannelsValue;

        private Text integrityArmorValue;
        private Text integrityStructureValue;
        private BarWidget integrityArmorBar;
        private BarWidget integrityStructureBar;
        private readonly PartArmorRow[] partRows = new PartArmorRow[4];

        private Text powerLoadValue;
        private Text powerOutputValue;
        private Text powerSurplusValue;
        private BarWidget powerBar;
        private float displayedPowerLoad = -1f;

        private MechEngineController engineController;
        private float nextTextRefreshTime;
        private float displayedIntegrityArmor = 1f;
        private float displayedIntegrityStructure = 1f;
        private bool gaugeRenderPending = true;
        private Sprite frameSprite;
        private Sprite barFrameSprite;

        private readonly struct PartArmorRow
        {
            public readonly Text Label;
            public readonly Text ArmorValue;
            public readonly Text StructureValue;
            public readonly BarWidget ArmorBar;
            public readonly BarWidget StructureBar;

            public PartArmorRow(Text label, Text armorValue, Text structureValue,
                BarWidget armorBar, BarWidget structureBar)
            {
                Label = label;
                ArmorValue = armorValue;
                StructureValue = structureValue;
                ArmorBar = armorBar;
                StructureBar = structureBar;
            }
        }

        private sealed class BarWidget
        {
            private readonly Image fillImage;

            public BarWidget(Image fillImage)
            {
                this.fillImage = fillImage;
            }

            public void SetFraction(float fraction, Color color)
            {
                fillImage.fillAmount = Mathf.Clamp01(fraction);
                fillImage.color = color;
            }
        }

        // ------------------------------------------------------------------
        // 生命周期与引用解析
        // ------------------------------------------------------------------

        private void Awake()
        {
            ResolveReferences();
            BuildUi();
        }

        private void OnDestroy()
        {
            if (canvasRoot != null)
            {
                Destroy(canvasRoot);
                canvasRoot = null;
            }

            if (gaugeTexture != null)
            {
                Destroy(gaugeTexture);
                gaugeTexture = null;
            }

            if (frameSprite != null)
            {
                Destroy(frameSprite);
                Destroy(frameSprite.texture);
                frameSprite = null;
            }

            if (barFrameSprite != null)
            {
                Destroy(barFrameSprite);
                Destroy(barFrameSprite.texture);
                barFrameSprite = null;
            }
        }

        private void ResolveReferences()
        {
            if (motion == null) motion = GetComponent<MechFlightMotionController>();
            if (runtimeState == null) runtimeState = GetComponent<MechRuntimeState>();
            if (protection == null) protection = GetComponent<CombatTestTarget>();
            if (thermalSignature == null) thermalSignature = GetComponent<MechThermalSignature>();
            if (radar == null) radar = GetComponent<MechRadarController>();
            if (engineController == null) engineController = GetComponent<MechEngineController>();
        }

        private void Update()
        {
            ResolveReferences();
            var shouldShow = visible &&
                             !MechAssemblyMenu.IsAnyMenuVisible &&
                             !MechSystemsRadialMenu.IsAnyMenuVisible &&
                             GetComponent<MechAiTargetController>() == null;
            if (canvasGroup == null)
            {
                return;
            }

            var startupAlpha = engineController != null
                ? engineController.GetHudStartupAlpha(MechStartupHudModule.VitalStatus)
                : 1f;
            canvasGroup.alpha = shouldShow ? startupAlpha : 0f;
            if (!shouldShow || startupAlpha <= 0.001f)
            {
                return;
            }

            SampleThrustAxes();
            UpdateGauge();
            UpdateBars();
            if (Time.unscaledTime >= nextTextRefreshTime)
            {
                nextTextRefreshTime = Time.unscaledTime + TextRefreshIntervalSeconds;
                RefreshTexts();
            }
        }

        // ------------------------------------------------------------------
        // UI 构建
        // ------------------------------------------------------------------

        private void BuildUi()
        {
            if (canvasRoot != null)
            {
                return;
            }

            hudFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            canvasRoot = new GameObject("__EngineeringStatusCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
            canvasRoot.transform.SetParent(transform, false);
            var canvas = canvasRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 60;
            var scaler = canvasRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGroup = canvasRoot.GetComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;

            var panel = CreateRect("Panel", canvasRoot.transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-PanelMargin, -PanelMargin),
                new Vector2(PanelWidth, PanelHeight));
            AddImage(panel, PanelBackground, BuildFrameSprite(), Image.Type.Sliced);

            BuildGaugeCell(panel);
            BuildThermalModule(panel);
            BuildArmorModule(panel);
            BuildPowerModule(panel);
            BuildDividers(panel);
        }

        private void BuildGaugeCell(RectTransform panel)
        {
            var header = CreateText(panel, "HeaderThrust", new Vector2(-24f, -18f),
                new Vector2(200f, 22f), "六向推力  THRUST VECTOR", 14, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen);
            header.raycastTarget = false;

            var gaugeCenter = new Vector2(GaugeCellWidth * 0.5f, 20f + GaugeCellWidth * 0.5f - 4f);
            var gaugeRect = CreateRect("Gauge", panel,
                new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                new Vector2(-gaugeCenter.x, -gaugeCenter.y),
                new Vector2(GaugeDisplaySize, GaugeDisplaySize));
            gaugeImage = gaugeRect.gameObject.AddComponent<RawImage>();
            gaugeImage.raycastTarget = false;
            gaugeImage.texture = EnsureGaugeTexture();

            // 六个轴向的标签与实时推力读数。
            for (var i = 0; i < 6; i++)
            {
                var direction = AxisDirections[i];
                var cellCenter = new Vector2(gaugeCenter.x, gaugeCenter.y);
                var labelPos = cellCenter + direction * (GaugeDisplaySize * 0.5f - 18f);
                var label = CreateText(panel, $"AxisLabel{i}", 
                    new Vector2(-labelPos.x, -labelPos.y), new Vector2(44f, 20f),
                    AxisLabels[i], 15, FontStyle.Bold, TextAnchor.MiddleCenter, HudGreen);
                label.raycastTarget = false;

                var forcePos = cellCenter + direction * (GaugeDisplaySize * 0.5f - 56f);
                axisForceTexts[i] = CreateText(panel, $"AxisForce{i}",
                    new Vector2(-forcePos.x, -forcePos.y), new Vector2(88f, 18f),
                    "0 kN", 12, FontStyle.Bold, TextAnchor.MiddleCenter, HudDimGreen);
                axisForceTexts[i].raycastTarget = false;
            }
        }

        private void BuildThermalModule(RectTransform panel)
        {
            CreateText(panel, "HeaderThermal", new Vector2(-ColumnX, -18f),
                new Vector2(ColumnWidth, 22f), "热管理  THERMAL", 14, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen).raycastTarget = false;

            const float rowWidth = ColumnWidth - 12f;
            thermalCapacityValue = CreateRowValue(panel, "ThermalCapacity",
                46f, "热容量", HudGreen);
            thermalDissipationValue = CreateRowValue(panel, "ThermalDissipation",
                70f, "散热", HudGreen);
            thermalChannelsValue = CreateRowValue(panel, "ThermalChannels",
                94f, "通道温度", HudGreen);
            void DrawUnderline(float y)
            {
                var line = CreateRect("ThermalLine", panel,
                    new Vector2(1f, 1f), new Vector2(1f, 1f),
                    new Vector2(-ColumnX, -y), new Vector2(rowWidth, 1f));
                AddImage(line, HudDimGreen, null, Image.Type.Simple);
            }
            DrawUnderline(32f);
        }

        private Text CreateRowValue(RectTransform panel, string name, float y,
            string labelText, Color color)
        {
            CreateText(panel, name + "Label", new Vector2(-ColumnX, -y),
                new Vector2(88f, 20f), labelText, 13, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen).raycastTarget = false;
            var value = CreateText(panel, name, new Vector2(-ColumnX - 96f, -y),
                new Vector2(ColumnWidth - 108f, 20f), "--", 13, FontStyle.Bold,
                TextAnchor.MiddleRight, color);
            value.raycastTarget = false;
            return value;
        }

        private void BuildArmorModule(RectTransform panel)
        {
            const float top = 122f;
            CreateText(panel, "HeaderArmor", new Vector2(-ColumnX, -top),
                new Vector2(ColumnWidth, 22f), "装甲  ARMOR", 14, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen).raycastTarget = false;
            var underline = CreateRect("ArmorLine", panel,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-ColumnX, -(top + 14f)), new Vector2(ColumnWidth - 12f, 1f));
            AddImage(underline, HudDimGreen, null, Image.Type.Simple);

            // 整体行：当前装甲与结构两条实时条。
            const float integrityY = top + 24f;
            CreateText(panel, "IntegrityLabel", new Vector2(-ColumnX, -integrityY - 4f),
                new Vector2(48f, 20f), "整体", 13, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen).raycastTarget = false;
            integrityArmorValue = CreateText(panel, "IntegrityArmorValue",
                new Vector2(-ColumnX - 56f, -integrityY - 6f), new Vector2(70f, 18f),
                "--", 12, FontStyle.Bold, TextAnchor.MiddleRight, HudGreen);
            integrityArmorValue.raycastTarget = false;
            integrityArmorBar = CreateBar(panel, "IntegrityArmorBar",
                new Vector2(-ColumnX - 136f, -integrityY), new Vector2(170f, 10f));
            integrityStructureValue = CreateText(panel, "IntegrityStructureValue",
                new Vector2(-ColumnX - 314f, -integrityY - 6f), new Vector2(70f, 18f),
                "--", 12, FontStyle.Bold, TextAnchor.MiddleRight, HudGreen);
            integrityStructureValue.raycastTarget = false;
            integrityStructureBar = CreateBar(panel, "IntegrityStructureBar",
                new Vector2(-ColumnX - 394f, -integrityY), new Vector2(170f, 10f));

            // 分部行：头 / 核心 / 手臂 / 腿 的配装容量分条。
            var labels = new[] { "头部", "核心", "手臂", "腿部" };
            for (var i = 0; i < partRows.Length; i++)
            {
                var y = integrityY + 30f + i * 25f;
                var label = CreateText(panel, $"PartLabel{i}", new Vector2(-ColumnX, -y - 4f),
                    new Vector2(48f, 20f), labels[i], 13, FontStyle.Bold,
                    TextAnchor.MiddleLeft, HudDimGreen);
                label.raycastTarget = false;
                var armorValue = CreateText(panel, $"PartArmorValue{i}",
                    new Vector2(-ColumnX - 56f, -y - 6f), new Vector2(70f, 18f),
                    "--", 12, FontStyle.Bold, TextAnchor.MiddleRight, HudDimGreen);
                armorValue.raycastTarget = false;
                var armorBar = CreateBar(panel, $"PartArmorBar{i}",
                    new Vector2(-ColumnX - 136f, -y), new Vector2(170f, 10f));
                var structureValue = CreateText(panel, $"PartStructureValue{i}",
                    new Vector2(-ColumnX - 314f, -y - 6f), new Vector2(70f, 18f),
                    "--", 12, FontStyle.Bold, TextAnchor.MiddleRight, HudDimGreen);
                structureValue.raycastTarget = false;
                var structureBar = CreateBar(panel, $"PartStructureBar{i}",
                    new Vector2(-ColumnX - 394f, -y), new Vector2(170f, 10f));
                partRows[i] = new PartArmorRow(label, armorValue, structureValue, armorBar, structureBar);
            }
        }

        private void BuildPowerModule(RectTransform panel)
        {
            const float top = 268f;
            CreateText(panel, "HeaderPower", new Vector2(-ColumnX, -top),
                new Vector2(ColumnWidth, 22f), "功耗  POWER", 14, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen).raycastTarget = false;
            var underline = CreateRect("PowerLine", panel,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-ColumnX, -(top + 14f)), new Vector2(ColumnWidth - 12f, 1f));
            AddImage(underline, HudDimGreen, null, Image.Type.Simple);

            powerBar = CreateBar(panel, "PowerBar",
                new Vector2(-ColumnX, -(top + 22f)), new Vector2(ColumnWidth - 12f, 12f));

            // 单行三列：功耗 / 发电 / 余量。
            const float rowY = top + 44f;
            const float columnStep = 168f;
            powerLoadValue = CreateInlineValue(panel, "PowerLoad", 0f, rowY, "功耗", HudGreen);
            powerOutputValue = CreateInlineValue(panel, "PowerOutput", columnStep, rowY, "发电", HudGreen);
            powerSurplusValue = CreateInlineValue(panel, "PowerSurplus", columnStep * 2f, rowY, "余量", HudGreen);
        }

        private Text CreateInlineValue(RectTransform panel, string name, float xOffset,
            float y, string labelText, Color color)
        {
            CreateText(panel, name + "Label", new Vector2(-ColumnX - xOffset, -y),
                new Vector2(46f, 20f), labelText, 13, FontStyle.Bold,
                TextAnchor.MiddleLeft, HudGreen).raycastTarget = false;
            var value = CreateText(panel, name, new Vector2(-ColumnX - xOffset - 52f, -y),
                new Vector2(108f, 20f), "--", 13, FontStyle.Bold,
                TextAnchor.MiddleRight, color);
            value.raycastTarget = false;
            return value;
        }

        private void BuildDividers(RectTransform panel)
        {
            // 指针仪与右列之间的竖向分隔线。
            var divider = CreateRect("ColumnDivider", panel,
                new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-GaugeCellWidth, -20f), new Vector2(1f, PanelHeight - 40f));
            AddImage(divider, HudDimGreen, null, Image.Type.Simple);
        }

        private BarWidget CreateBar(RectTransform panel, string name, Vector2 pos, Vector2 size)
        {
            var background = CreateRect(name + "Bg", panel,
                new Vector2(1f, 1f), new Vector2(1f, 1f), pos, size);
            AddImage(background, BarBackground, BuildBarFrameSprite(), Image.Type.Sliced);

            for (var i = 1; i < 10; i++)
            {
                var tick = CreateRect(name + "Tick" + i, background,
                    new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(size.x * i / 10f, 0f), new Vector2(1f, size.y - 2f));
                AddImage(tick, BarTick, null, Image.Type.Simple);
            }

            var fill = CreateRect(name + "Fill", background,
                new Vector2(0f, 0f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            fill.anchorMax = Vector2.one;
            fill.offsetMin = new Vector2(1f, 1f);
            fill.offsetMax = new Vector2(-1f, -1f);
            var fillImage = AddImage(fill, HudGreen, null, Image.Type.Filled);
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = 0;
            fillImage.fillAmount = 0f;
            return new BarWidget(fillImage);
        }

        // ------------------------------------------------------------------
        // 每帧数据采样与显示刷新
        // ------------------------------------------------------------------

        private void SampleThrustAxes()
        {
            for (var i = 0; i < axisDemands.Length; i++)
            {
                axisDemands[i] = 0f;
            }

            if (motion == null || runtimeState?.AggregatedStats == null)
            {
                return;
            }

            var stats = runtimeState.AggregatedStats;
            var localForce = Vector3.zero;
            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                localForce += channels[i].CurrentForceNewtons;
            }
            localForce = Quaternion.Inverse(motion.LogicalRotation) * localForce;

            // 机体局部系：+z 前、-z 后；+x 右、-x 左；+y 上、-y 下。
            axisDemands[0] = NormalizeAxis(localForce.z, stats.mainThrustKn);      // 前
            axisDemands[3] = NormalizeAxis(-localForce.z, stats.reverseThrustKn);  // 后
            axisDemands[1] = NormalizeAxis(localForce.x, stats.lateralThrustKn);   // 右
            axisDemands[4] = NormalizeAxis(-localForce.x, stats.lateralThrustKn);  // 左
            axisDemands[2] = NormalizeAxis(-localForce.y, stats.downwardThrustKn); // 下
            axisDemands[5] = NormalizeAxis(localForce.y, stats.upwardThrustKn);    // 上
        }

        private static float NormalizeAxis(float newtons, float capacityKn)
        {
            var capacityNewtons = capacityKn * 1000f;
            return capacityNewtons > 0.001f
                ? Mathf.Clamp(newtons / capacityNewtons, -1f, 1.5f)
                : 0f;
        }

        private void UpdateGauge()
        {
            if (gaugeImage == null)
            {
                return;
            }

            EnsureGaugeTexture();
            var delta = Time.unscaledDeltaTime;
            var step = 1f - Mathf.Exp(-GaugeSmoothing * delta);
            var dirty = false;
            for (var i = 0; i < 6; i++)
            {
                var previous = displayedDemands[i];
                var next = Mathf.Lerp(previous, axisDemands[i], step);
                if (Mathf.Abs(next - previous) > 0.0005f)
                {
                    dirty = true;
                }
                displayedDemands[i] = next;
            }

            if (gaugeRenderPending || dirty || gaugeImage.texture == null)
            {
                gaugeRenderPending = false;
                RenderGaugeTexture();
            }
        }

        private Texture2D EnsureGaugeTexture()
        {
            if (gaugeTexture == null)
            {
                gaugeTexture = new Texture2D(GaugeTextureSize, GaugeTextureSize,
                    TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                gaugePixels = new Color32[GaugeTextureSize * GaugeTextureSize];
                gaugeImage.texture = gaugeTexture;
            }

            return gaugeTexture;
        }

        private void UpdateBars()
        {
            if (runtimeState?.AggregatedStats == null)
            {
                return;
            }

            var delta = Time.unscaledDeltaTime;
            var step = 1f - Mathf.Exp(-BarSmoothing * delta);
            UpdateIntegrityBars(step);
            UpdatePowerBar(step);
        }

        private void UpdateIntegrityBars(float step)
        {
            float armorFraction = 0f;
            float structureFraction = 0f;
            if (protection != null && protection.MaxArmor > 0f)
            {
                armorFraction = Mathf.Clamp01(protection.Armor / protection.MaxArmor);
            }
            if (protection != null && protection.MaxStructure > 0f)
            {
                structureFraction = Mathf.Clamp01(protection.Structure / protection.MaxStructure);
            }

            displayedIntegrityArmor = Mathf.Lerp(displayedIntegrityArmor, armorFraction, step);
            displayedIntegrityStructure = Mathf.Lerp(displayedIntegrityStructure, structureFraction, step);
            integrityArmorBar.SetFraction(displayedIntegrityArmor,
                ConditionColor(displayedIntegrityArmor, highIsHealthy: true));
            integrityStructureBar.SetFraction(displayedIntegrityStructure,
                ConditionColor(displayedIntegrityStructure, highIsHealthy: true));
        }

        private void UpdatePowerBar(float step)
        {
            var stats = runtimeState.AggregatedStats;
            var output = Mathf.Max(0f, stats.powerOutputMw);
            var draw = ResolveCurrentPowerDraw(stats);
            var load = output > 0.001f ? draw / output : 1f;
            displayedPowerLoad = displayedPowerLoad < 0f
                ? load
                : Mathf.Lerp(displayedPowerLoad, load, step);
            var color = displayedPowerLoad > 1.02f
                ? HudRed
                : displayedPowerLoad > 0.85f
                    ? HudAmber
                    : HudGreen;
            powerBar.SetFraction(displayedPowerLoad, color);
        }

        private float ResolveCurrentPowerDraw(MechAggregatedStats stats)
        {
            var idle = Mathf.Max(0f, stats.idlePowerDrawMw);
            var peak = Mathf.Max(idle, stats.peakPowerDrawMw);
            var propulsionMw = motion != null ? Mathf.Max(0f, motion.CurrentPropulsionPowerMw) : 0f;
            var radarMw = radar != null && radar.RadarPowerEnabled
                ? peak * (radar.TrackOperationMode ? RadarTrackShareOfPeak : RadarScanShareOfPeak)
                : 0f;
            return idle + propulsionMw + radarMw;
        }

        private void RefreshTexts()
        {
            var stats = runtimeState?.AggregatedStats;
            RefreshThrustTexts();
            RefreshThermalTexts(stats);
            RefreshArmorTexts();
            RefreshPowerTexts(stats);
        }

        private void RefreshThrustTexts()
        {
            if (motion == null || runtimeState?.AggregatedStats == null)
            {
                for (var i = 0; i < axisForceTexts.Length; i++)
                {
                    axisForceTexts[i].text = "--";
                }
                return;
            }

            var stats = runtimeState.AggregatedStats;
            var localForce = Vector3.zero;
            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                localForce += channels[i].CurrentForceNewtons;
            }
            localForce = Quaternion.Inverse(motion.LogicalRotation) * localForce;

            var forces = new[]
            {
                localForce.z,    // 前
                localForce.x,    // 右
                -localForce.y,   // 下
                -localForce.z,   // 后
                -localForce.x,   // 左
                localForce.y,    // 上
            };
            var capacities = new[]
            {
                stats.mainThrustKn, stats.lateralThrustKn, stats.downwardThrustKn,
                stats.reverseThrustKn, stats.lateralThrustKn, stats.upwardThrustKn,
            };
            for (var i = 0; i < 6; i++)
            {
                axisForceTexts[i].text = FormatForce(forces[i]);
                axisForceTexts[i].color = AxisValueColor(forces[i], capacities[i]);
            }
        }

        private static string FormatForce(float newtons)
        {
            var kn = newtons / 1000f;
            if (Mathf.Abs(kn) >= 999.5f)
            {
                return $"{kn / 1000f:0.00} MN";
            }
            return $"{kn:0} kN";
        }

        private static Color AxisValueColor(float newtons, float capacityKn)
        {
            var capacity = capacityKn * 1000f;
            if (capacity <= 0.001f)
            {
                return HudDimGreen;
            }
            var ratio = Mathf.Abs(newtons) / capacity;
            return ratio > 1.02f ? HudRed : ratio > 0.9f ? HudAmber : ratio > 0.05f ? HudCyan : HudDimGreen;
        }

        private void RefreshThermalTexts(MechAggregatedStats stats)
        {
            if (stats == null)
            {
                thermalCapacityValue.text = "--";
                thermalDissipationValue.text = "--";
                thermalChannelsValue.text = "--";
                return;
            }

            var capacity = Mathf.Max(stats.heatCapacityMj,
                stats.radiatorPerformance?.thermalCapacityMj ?? 0f);
            var dissipationMw = Mathf.Max(stats.heatDissipationMw,
                (stats.radiatorPerformance?.ratedDissipationKw ?? 0f) / 1000f);
            thermalCapacityValue.text = capacity > 0.01f ? $"{capacity:0.0} MJ" : "--";
            thermalDissipationValue.text = dissipationMw > 0.001f ? $"{dissipationMw:0.0} MW" : "--";

            if (thermalSignature == null)
            {
                thermalChannelsValue.text = "--";
                thermalChannelsValue.color = HudDimGreen;
                return;
            }

            var skin = thermalSignature.GetChannelTemperature(MechThermalChannel.Skin);
            var thrusters = thermalSignature.GetChannelTemperature(MechThermalChannel.Thrusters);
            var leftWeapon = thermalSignature.GetChannelTemperature(MechThermalChannel.LeftWeapon);
            var rightWeapon = thermalSignature.GetChannelTemperature(MechThermalChannel.RightWeapon);
            var hottest = Mathf.Max(skin, thrusters, leftWeapon, rightWeapon);
            thermalChannelsValue.text = $"蒙皮 {skin:0}K  推进 {thrusters:0}K  武器 {leftWeapon:0}/{rightWeapon:0}K";
            thermalChannelsValue.color = hottest > 950f
                ? HudRed
                : hottest > 700f
                    ? HudAmber
                    : HudGreen;
        }

        private void RefreshArmorTexts()
        {
            if (protection != null && protection.MaxArmor > 0f)
            {
                integrityArmorValue.text = $"{protection.Armor:0}/{protection.MaxArmor:0}";
                integrityArmorValue.color =
                    ConditionColor(protection.Armor / protection.MaxArmor, highIsHealthy: true);
            }
            else
            {
                integrityArmorValue.text = "--";
                integrityArmorValue.color = HudDimGreen;
            }

            if (protection != null && protection.MaxStructure > 0f)
            {
                integrityStructureValue.text = $"{protection.Structure:0}/{protection.MaxStructure:0}";
                integrityStructureValue.color =
                    ConditionColor(protection.Structure / protection.MaxStructure, highIsHealthy: true);
            }
            else
            {
                integrityStructureValue.text = "--";
                integrityStructureValue.color = HudDimGreen;
            }

            RefreshPartArmorTexts();
        }

        private void RefreshPartArmorTexts()
        {
            var slots = new[] { MechPartSlot.Head, MechPartSlot.Core, MechPartSlot.Arms, MechPartSlot.Legs };
            var armorCapacities = new float[slots.Length];
            var structureCapacities = new float[slots.Length];
            var maxArmor = 0f;
            var maxStructure = 0f;
            var records = runtimeState?.AssembledParts;
            if (records != null)
            {
                for (var i = 0; i < slots.Length; i++)
                {
                    for (var r = 0; r < records.Count; r++)
                    {
                        var record = records[r];
                        if (record == null || record.slot != slots[i] || record.part?.Stats == null)
                        {
                            continue;
                        }
                        armorCapacities[i] = Mathf.Max(0f, record.part.Stats.armorPoints);
                        structureCapacities[i] = Mathf.Max(0f, record.part.Stats.structurePoints);
                        break;
                    }
                    maxArmor = Mathf.Max(maxArmor, armorCapacities[i]);
                    maxStructure = Mathf.Max(maxStructure, structureCapacities[i]);
                }
            }

            for (var i = 0; i < partRows.Length; i++)
            {
                var armor = armorCapacities[i];
                var structure = structureCapacities[i];
                partRows[i].ArmorValue.text = armor > 0f ? $"{armor:0}" : "--";
                partRows[i].StructureValue.text = structure > 0f ? $"{structure:0}" : "--";
                partRows[i].ArmorBar.SetFraction(
                    maxArmor > 0f ? armor / maxArmor : 0f, HudDimGreen);
                partRows[i].StructureBar.SetFraction(
                    maxStructure > 0f ? structure / maxStructure : 0f, HudDimGreen);
            }
        }

        private void RefreshPowerTexts(MechAggregatedStats stats)
        {
            if (stats == null)
            {
                powerLoadValue.text = "--";
                powerOutputValue.text = "--";
                powerSurplusValue.text = "--";
                return;
            }

            var output = Mathf.Max(0f, stats.powerOutputMw);
            var draw = ResolveCurrentPowerDraw(stats);
            var surplus = output - draw;
            powerLoadValue.text = $"{draw:0.0} MW";
            powerLoadValue.color = draw > output ? HudRed : draw > output * 0.85f ? HudAmber : HudGreen;
            powerOutputValue.text = $"{output:0.0} MW";
            powerSurplusValue.text = $"{surplus:+0.0;-0.0;0.0} MW";
            powerSurplusValue.color = surplus < 0f
                ? HudRed
                : surplus < output * 0.15f
                    ? HudAmber
                    : HudGreen;
        }

        private static Color ConditionColor(float fraction, bool highIsHealthy)
        {
            var condition = highIsHealthy ? fraction : 1f - fraction;
            return condition <= 0.2f ? HudRed : condition <= 0.45f ? HudAmber : HudGreen;
        }

        // ------------------------------------------------------------------
        // 指针仪纹理渲染（1024²，纹理空间 y 向下）
        // ------------------------------------------------------------------

        private void RenderGaugeTexture()
        {
            Array.Clear(gaugePixels, 0, gaugePixels.Length);
            var center = new Vector2(GaugeTextureSize * 0.5f, GaugeTextureSize * 0.5f);

            // 外圈刻度环。
            DrawCircleStroke(center, GaugeRadiusTexture - 4f, 2.5f, Dim(GaugeGreen, 0.55f), 256);
            for (var tick = 0; tick < 24; tick++)
            {
                var angle = tick * Mathf.PI / 12f - Mathf.PI * 0.5f;
                var major = tick % 6 == 0;
                var inner = GaugeRadiusTexture - (major ? 30f : 20f);
                var outer = GaugeRadiusTexture - 4f;
                var a = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * inner;
                var b = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * outer;
                DrawLine(a, b, Dim(GaugeGreen, major ? 0.8f : 0.45f), major ? 3.5f : 2f);
            }

            // 六边形轮廓。
            var hexRadius = GaugeRadiusTexture - 36f;
            for (var i = 0; i < 6; i++)
            {
                var a = center + AxisDirections[i] * hexRadius;
                var b = center + AxisDirections[(i + 1) % 6] * hexRadius;
                DrawLine(a, b, Dim(GaugeGreen, 0.4f), 2.5f);
            }

            // 中心基准环与机头标记。
            DrawCircleStroke(center, 46f, 2.5f, Dim(GaugeGreen, 0.75f), 96);
            var nose = center + AxisDirections[0] * 68f;
            DrawLine(nose + new Vector2(-8f, 12f), nose, Dim(GaugeGreen, 0.8f), 2.5f);
            DrawLine(nose + new Vector2(8f, 12f), nose, Dim(GaugeGreen, 0.8f), 2.5f);

            // 六条轴向指针。
            var innerRadius = 72f;
            var armRadius = hexRadius - 16f;
            for (var i = 0; i < 6; i++)
            {
                DrawAxisArm(center, AxisDirections[i], innerRadius, armRadius, displayedDemands[i]);
            }

            gaugeTexture.SetPixels32(gaugePixels);
            gaugeTexture.Apply(false);
        }

        private void DrawAxisArm(Vector2 center, Vector2 direction, float innerRadius,
            float armRadius, float demand)
        {
            var restEnd = center + direction * armRadius;
            DrawLine(center + direction * innerRadius, restEnd,
                Dim(GaugeGreen, 0.42f), 4.5f);
            DrawArrowHead(restEnd, direction, Dim(GaugeGreen, 0.5f), 13f, 3f);

            var active = Mathf.Clamp01(demand);
            if (active <= 0.004f)
            {
                return;
            }

            var overRange = demand > 1.001f;
            var color = overRange
                ? GaugeAmber
                : Blend(GaugeGreen, GaugeCyan, active);
            var tip = center + direction * (innerRadius + active * (armRadius - innerRadius));
            DrawLine(center + direction * innerRadius, tip, Fade(color, 0.22f), 9f);
            DrawLine(center + direction * innerRadius, tip, Fade(color, 0.95f), 4.5f);
            DrawArrowHead(tip, direction, color, 16f + active * 8f, 4.5f);

            if (overRange)
            {
                var marker = center + direction * (armRadius + 14f);
                DrawLine(marker - Perpendicular(direction) * 9f,
                    marker + Perpendicular(direction) * 9f, Fade(GaugeRed, 0.95f), 4f);
            }
        }

        private void DrawArrowHead(Vector2 tip, Vector2 direction, Color32 color,
            float size, float thickness)
        {
            var normal = Perpendicular(direction);
            var basePoint = tip - direction * size;
            DrawLine(tip, basePoint + normal * size * 0.55f, color, thickness);
            DrawLine(tip, basePoint - normal * size * 0.55f, color, thickness);
        }

        private void DrawCircleStroke(Vector2 center, float radius, float thickness,
            Color32 color, int segments)
        {
            var previous = center + new Vector2(radius, 0f);
            for (var i = 1; i <= segments; i++)
            {
                var angle = i * Mathf.PI * 2f / segments;
                var current = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                DrawLine(previous, current, color, thickness);
                previous = current;
            }
        }

        private void DrawLine(Vector2 from, Vector2 to, Color32 color, float thickness)
        {
            var delta = to - from;
            var length = delta.magnitude;
            if (length < 0.001f)
            {
                return;
            }

            var radius = Mathf.Max(0.5f, thickness * 0.5f);
            var steps = Mathf.CeilToInt(length * 2f) + 1;
            var dx = delta.x / steps;
            var dy = delta.y / steps;
            var x = from.x;
            var y = from.y;
            for (var i = 0; i <= steps; i++)
            {
                StampDisc(x, y, radius, color);
                x += dx;
                y += dy;
            }
        }

        private void StampDisc(float x, float y, float radius, Color32 color)
        {
            var minX = Mathf.Max(0, Mathf.FloorToInt(x - radius));
            var maxX = Mathf.Min(GaugeTextureSize - 1, Mathf.CeilToInt(x + radius));
            var minY = Mathf.Max(0, Mathf.FloorToInt(y - radius));
            var maxY = Mathf.Min(GaugeTextureSize - 1, Mathf.CeilToInt(y + radius));
            var radiusSquared = radius * radius;
            for (var py = minY; py <= maxY; py++)
            {
                var dy = py - y;
                // 纹理 v=0 在 RawImage 底部，而绘制坐标 y 向下；写入时翻转行。
                var row = (GaugeTextureSize - 1 - py) * GaugeTextureSize;
                for (var px = minX; px <= maxX; px++)
                {
                    var dx = px - x;
                    var distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared >= radiusSquared)
                    {
                        continue;
                    }

                    // 1px 软边：越靠近圆心越接近全 alpha。
                    var edge = Mathf.Clamp01(radius - Mathf.Sqrt(distanceSquared));
                    var alpha = color.a * Mathf.Clamp01(edge + 0.6f);
                    BlendPixel(row + px, new Color32(color.r, color.g, color.b, (byte)alpha));
                }
            }
        }

        private void BlendPixel(int index, Color32 source)
        {
            var destination = gaugePixels[index];
            var sourceAlpha = source.a / 255f;
            var destinationAlpha = destination.a / 255f;
            var outAlpha = sourceAlpha + destinationAlpha * (1f - sourceAlpha);
            if (outAlpha <= 0.0001f)
            {
                gaugePixels[index] = default;
                return;
            }

            var r = (source.r * sourceAlpha + destination.r * destinationAlpha * (1f - sourceAlpha)) / outAlpha;
            var g = (source.g * sourceAlpha + destination.g * destinationAlpha * (1f - sourceAlpha)) / outAlpha;
            var b = (source.b * sourceAlpha + destination.b * destinationAlpha * (1f - sourceAlpha)) / outAlpha;
            gaugePixels[index] = new Color32(
                (byte)Mathf.RoundToInt(r),
                (byte)Mathf.RoundToInt(g),
                (byte)Mathf.RoundToInt(b),
                (byte)Mathf.RoundToInt(outAlpha * 255f));
        }

        private static Vector2 Perpendicular(Vector2 v) => new(-v.y, v.x);

        private static Color32 GaugeGreen => new(77, 255, 128, 255);
        private static Color32 GaugeCyan => new(31, 224, 240, 255);
        private static Color32 GaugeAmber => new(255, 184, 46, 255);
        private static Color32 GaugeRed => new(255, 46, 31, 255);

        private static Color32 Dim(Color32 color, float alpha) =>
            new(color.r, color.g, color.b, (byte)(color.a * Mathf.Clamp01(alpha)));

        private static Color32 Fade(Color32 color, float alpha) =>
            new(color.r, color.g, color.b, (byte)(color.a * Mathf.Clamp01(alpha)));

        private static Color32 Blend(Color32 a, Color32 b, float t) =>
            new(
                (byte)Mathf.Lerp(a.r, b.r, t),
                (byte)Mathf.Lerp(a.g, b.g, t),
                (byte)Mathf.Lerp(a.b, b.b, t),
                (byte)Mathf.Lerp(a.a, b.a, t));

        // ------------------------------------------------------------------
        // 控件与精灵辅助
        // ------------------------------------------------------------------

        private static RectTransform CreateRect(string name, Transform parent,
            Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            return rect;
        }

        private Text CreateText(RectTransform parent, string name, Vector2 anchoredPosition,
            Vector2 size, string content, int fontSize, FontStyle fontStyle,
            TextAnchor alignment, Color color)
        {
            var rect = CreateRect(name, parent,
                new Vector2(1f, 1f), new Vector2(1f, 1f), anchoredPosition, size);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = hudFont;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.alignment = alignment;
            text.color = color;
            text.text = content;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        private static Image AddImage(RectTransform rect, Color color, Sprite sprite,
            Image.Type type)
        {
            var image = rect.gameObject.AddComponent<Image>();
            image.color = color;
            image.sprite = sprite;
            image.type = type;
            image.raycastTarget = false;
            return image;
        }

        private Sprite BuildFrameSprite()
        {
            if (frameSprite != null)
            {
                return frameSprite;
            }

            // 64² 九宫格：1px 边框 + 四个 16px 直角括号角。
            const int size = 64;
            const int border = 17;
            var pixels = new Color32[size * size];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.white;
            }

            Color32 corner = new(77, 255, 128, 255);
            var edge = new Color32(77, 255, 128, 96);
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    var left = x < border;
                    var right = x >= size - border;
                    var top = y >= size - border;
                    var bottom = y < border;
                    var onHorizontal = y == 0 || y == size - 1;
                    var onVertical = x == 0 || x == size - 1;
                    if (onHorizontal || onVertical)
                    {
                        // 边框：转角处保留亮色，直边用暗一点的颜色。
                        pixels[y * size + x] = (left || right) && (top || bottom)
                            ? corner
                            : edge;
                    }
                    else if ((left || right) && (top || bottom))
                    {
                        // 直角括号：靠近角的两条 2px 亮线。
                        var armX = left ? x < 2 : x >= size - 2;
                        var armY = top ? y >= size - 2 : y < 2;
                        if (armX || armY)
                        {
                            pixels[y * size + x] = corner;
                        }
                        else
                        {
                            pixels[y * size + x] = Color.clear;
                        }
                    }
                    else
                    {
                        pixels[y * size + x] = Color.clear;
                    }
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            frameSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            return frameSprite;
        }

        private Sprite BuildBarFrameSprite()
        {
            if (barFrameSprite != null)
            {
                return barFrameSprite;
            }

            // 16x16 九宫格：1px 边框。
            const int size = 16;
            const int border = 3;
            var pixels = new Color32[size * size];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Color.clear;
            }

            var edge = new Color32(77, 255, 128, 110);
            for (var x = 0; x < size; x++)
            {
                for (var y = 0; y < size; y++)
                {
                    if (x == 0 || x == size - 1 || y == 0 || y == size - 1)
                    {
                        pixels[y * size + x] = edge;
                    }
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            barFrameSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            return barFrameSprite;
        }
    }
}
