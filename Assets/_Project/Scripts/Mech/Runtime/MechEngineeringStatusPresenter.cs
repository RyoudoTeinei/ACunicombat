using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Compact ownship engineering strip. It deliberately reads the existing runtime state
    /// instead of maintaining a second damage, propulsion, thermal, or power model.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechEngineeringStatusPresenter : MonoBehaviour
    {
        private static readonly Color HudGreen = new(0.28f, 1f, 0.53f, 0.96f);
        private static readonly Color HudDim = new(0.10f, 0.35f, 0.20f, 0.72f);
        private static readonly Color HudCyan = new(0.24f, 0.92f, 0.82f, 0.96f);
        private static readonly Color HudAmber = new(1f, 0.68f, 0.16f, 0.98f);
        private static readonly Color HudRed = new(1f, 0.22f, 0.16f, 0.98f);
        private static readonly Color BandBackground = new(0.008f, 0.055f, 0.035f, 0.68f);
        private static readonly Color BarBackground = new(0.01f, 0.11f, 0.065f, 0.9f);

        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private MechRuntimeState runtimeState;
        [SerializeField] private CombatTestTarget protection;
        [SerializeField] private MechThermalSignature thermalSignature;
        [SerializeField] private MechRadarController radar;
        [SerializeField] private bool visible = true;

        private GUIStyle titleStyle;
        private GUIStyle labelStyle;
        private GUIStyle valueStyle;
        private GUIStyle axisStyle;

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnGUI()
        {
            if (!visible || MechAssemblyMenu.IsAnyMenuVisible)
            {
                return;
            }

            ResolveReferences();
            if (motion == null || runtimeState?.AggregatedStats == null ||
                GetComponent<MechAiTargetController>() != null)
            {
                return;
            }

            EnsureStyles();
            const float flightPanelReserve = 240f;
            var width = Mathf.Clamp(Screen.width * 0.43f, 640f, 1040f);
            width = Mathf.Min(width, Mathf.Max(480f, Screen.width - flightPanelReserve - 22f));
            var height = Mathf.Clamp(Screen.height * 0.115f, 126f, 158f);
            var strip = new Rect(
                Screen.width - width - flightPanelReserve,
                22f,
                width,
                height);

            var previousDepth = GUI.depth;
            GUI.depth = -95;
            DrawRect(strip, BandBackground);
            DrawCornerBrackets(strip, HudGreen, 13f, 1.5f);

            const float gap = 1f;
            var thrustWidth = strip.width * 0.22f;
            var remainingWidth = strip.width - thrustWidth;
            var moduleWidth = remainingWidth / 3f;
            var thrustRect = new Rect(strip.x, strip.y, thrustWidth, strip.height);
            var heatRect = new Rect(thrustRect.xMax + gap, strip.y, moduleWidth - gap, strip.height);
            var armorRect = new Rect(heatRect.xMax + gap, strip.y, moduleWidth - gap, strip.height);
            var powerRect = new Rect(armorRect.xMax + gap, strip.y, moduleWidth - gap, strip.height);

            DrawSeparator(thrustRect.xMax, strip);
            DrawSeparator(heatRect.xMax, strip);
            DrawSeparator(armorRect.xMax, strip);
            DrawThrustModule(thrustRect);
            DrawThermalModule(heatRect, runtimeState.AggregatedStats);
            DrawProtectionModule(armorRect);
            DrawPowerModule(powerRect, runtimeState.AggregatedStats);
            GUI.depth = previousDepth;
        }

        private void DrawThrustModule(Rect rect)
        {
            DrawTitle(rect, "六向推力");
            var center = new Vector2(rect.center.x, rect.y + rect.height * 0.58f);
            var radius = Mathf.Min(rect.width * 0.31f, rect.height * 0.29f);
            var forceLocal = ResolveLocalThrusterForce();
            var stats = runtimeState.AggregatedStats;

            var forward = NormalizeDirectionalForce(forceLocal.z, stats.mainThrustKn, stats.reverseThrustKn);
            var lateral = NormalizeDirectionalForce(forceLocal.x, stats.lateralThrustKn, stats.lateralThrustKn);
            var vertical = NormalizeDirectionalForce(forceLocal.y, stats.upwardThrustKn, stats.downwardThrustKn);

            DrawAxisPair(center, new Vector2(0f, -1f), radius, forward, "前", "后");
            DrawAxisPair(center, new Vector2(0.86f, 0.5f), radius, lateral, "右", "左");
            DrawAxisPair(center, new Vector2(-0.86f, 0.5f), radius, vertical, "上", "下");
            DrawCircle(center, 7f, HudGreen, 1.5f, 20);
            DrawLine(center + Vector2.left * 4f, center + Vector2.right * 4f, HudGreen, 1.2f);
            DrawLine(center + Vector2.up * 4f, center + Vector2.down * 4f, HudGreen, 1.2f);
        }

        private void DrawAxisPair(
            Vector2 center,
            Vector2 positiveDirection,
            float radius,
            float signedDemand,
            string positiveLabel,
            string negativeLabel)
        {
            positiveDirection.Normalize();
            DrawThrustPointer(center, positiveDirection, radius, Mathf.Max(0f, signedDemand));
            DrawThrustPointer(center, -positiveDirection, radius, Mathf.Max(0f, -signedDemand));

            axisStyle.alignment = TextAnchor.MiddleCenter;
            var labelDistance = radius + 13f;
            var positivePoint = center + positiveDirection * labelDistance;
            var negativePoint = center - positiveDirection * labelDistance;
            GUI.Label(new Rect(positivePoint.x - 12f, positivePoint.y - 9f, 24f, 18f), positiveLabel, axisStyle);
            GUI.Label(new Rect(negativePoint.x - 12f, negativePoint.y - 9f, 24f, 18f), negativeLabel, axisStyle);
        }

        private static void DrawThrustPointer(Vector2 center, Vector2 direction, float radius, float demand)
        {
            var clamped = Mathf.Clamp01(demand);
            var end = center + direction * radius;
            var activeEnd = center + direction * radius * Mathf.Lerp(0.32f, 1f, clamped);
            DrawLine(center + direction * 9f, end, HudDim, 1.2f);

            var activeColor = Color.Lerp(HudDim, HudCyan, Mathf.Clamp01(clamped * 1.7f));
            DrawLine(center + direction * 9f, activeEnd, activeColor, Mathf.Lerp(1.2f, 3f, clamped));
            var normal = new Vector2(-direction.y, direction.x);
            var tipSize = 5f + clamped * 3f;
            DrawLine(activeEnd, activeEnd - direction * tipSize + normal * tipSize * 0.55f, activeColor, 1.5f);
            DrawLine(activeEnd, activeEnd - direction * tipSize - normal * tipSize * 0.55f, activeColor, 1.5f);
        }

        private void DrawThermalModule(Rect rect, MechAggregatedStats stats)
        {
            DrawTitle(rect, "热管理");
            var thermalLoad = ResolveThermalLoad();
            var thermalColor = ResolveConditionColor(thermalLoad, false);
            DrawStatusBar(new Rect(rect.x + 13f, rect.y + 36f, rect.width - 26f, 10f), thermalLoad, thermalColor);

            var capacity = Mathf.Max(stats.heatCapacityMj, stats.radiatorPerformance?.thermalCapacityMj ?? 0f);
            var dissipationMw = Mathf.Max(stats.heatDissipationMw,
                (stats.radiatorPerformance?.ratedDissipationKw ?? 0f) / 1000f);
            DrawMetricRow(rect, 54f, "热负荷", $"{thermalLoad * 100f:0}%", thermalColor);
            DrawMetricRow(rect, 76f, "热容量", $"{capacity:0.0} MJ", HudGreen);
            DrawMetricRow(rect, 98f, "散热器", $"{dissipationMw:0.0} MW", HudGreen);
        }

        private void DrawProtectionModule(Rect rect)
        {
            DrawTitle(rect, "装甲 / 结构");
            if (protection == null)
            {
                DrawMetricRow(rect, 62f, "状态", "无数据", HudAmber);
                return;
            }

            var armorFraction = protection.MaxArmor > 0f
                ? Mathf.Clamp01(protection.Armor / protection.MaxArmor)
                : 0f;
            var structureFraction = protection.MaxStructure > 0f
                ? Mathf.Clamp01(protection.Structure / protection.MaxStructure)
                : 0f;
            var armorColor = ResolveConditionColor(armorFraction, true);
            var structureColor = ResolveConditionColor(structureFraction, true);

            DrawLabeledBar(rect, 39f, "装甲", armorFraction, armorColor,
                $"{protection.Armor:0}/{protection.MaxArmor:0}");
            DrawLabeledBar(rect, 77f, "结构", structureFraction, structureColor,
                $"{protection.Structure:0}/{protection.MaxStructure:0}");
        }

        private void DrawPowerModule(Rect rect, MechAggregatedStats stats)
        {
            DrawTitle(rect, "功率");
            var output = Mathf.Max(0f, stats.powerOutputMw);
            var consumption = ResolveCurrentPowerDraw(stats);
            var load = output > 0.001f ? consumption / output : 1f;
            var powerColor = load > 1f ? HudRed : load > 0.85f ? HudAmber : HudGreen;
            DrawStatusBar(new Rect(rect.x + 13f, rect.y + 36f, rect.width - 26f, 10f),
                Mathf.Clamp01(load), powerColor);
            DrawMetricRow(rect, 54f, "功耗", $"{consumption:0.0} MW", powerColor);
            DrawMetricRow(rect, 76f, "发电", $"{output:0.0} MW", HudGreen);
            var surplus = output - consumption;
            DrawMetricRow(rect, 98f, "余量", $"{surplus:+0.0;-0.0;0.0} MW",
                surplus < 0f ? HudRed : surplus < output * 0.15f ? HudAmber : HudGreen);
        }

        private Vector3 ResolveLocalThrusterForce()
        {
            var force = Vector3.zero;
            var channels = motion.ThrusterChannels;
            for (var i = 0; i < channels.Count; i++)
            {
                force += channels[i].CurrentForceNewtons;
            }
            return Quaternion.Inverse(motion.LogicalRotation) * force;
        }

        private static float NormalizeDirectionalForce(float newtons, float positiveKn, float negativeKn)
        {
            var capacity = newtons >= 0f ? positiveKn : negativeKn;
            return capacity > 0.001f ? Mathf.Clamp(newtons / (capacity * 1000f), -1f, 1f) : 0f;
        }

        private float ResolveThermalLoad()
        {
            if (thermalSignature == null)
            {
                return 0f;
            }

            var skin = Mathf.InverseLerp(285f, 650f,
                thermalSignature.GetChannelTemperature(MechThermalChannel.Skin));
            var thrusters = Mathf.InverseLerp(300f, 1200f,
                thermalSignature.GetChannelTemperature(MechThermalChannel.Thrusters));
            var leftWeapon = Mathf.InverseLerp(285f, 900f,
                thermalSignature.GetChannelTemperature(MechThermalChannel.LeftWeapon));
            var rightWeapon = Mathf.InverseLerp(285f, 900f,
                thermalSignature.GetChannelTemperature(MechThermalChannel.RightWeapon));
            return Mathf.Clamp01(Mathf.Max(skin, thrusters, leftWeapon, rightWeapon));
        }

        private float ResolveCurrentPowerDraw(MechAggregatedStats stats)
        {
            var idle = Mathf.Max(0f, stats.idlePowerDrawMw);
            var peak = Mathf.Max(idle, stats.peakPowerDrawMw);
            var thrustActivity = Mathf.Clamp01(motion.MainEngineOutput);
            var forceCapacity = Mathf.Max(
                stats.mainThrustKn,
                stats.reverseThrustKn,
                stats.lateralThrustKn,
                stats.upwardThrustKn,
                stats.downwardThrustKn) * 1000f;
            if (forceCapacity > 0.01f)
            {
                thrustActivity = Mathf.Max(thrustActivity,
                    Mathf.Clamp01(motion.CurrentControlForceNewtons.magnitude / forceCapacity));
            }
            if (motion.QuickBoostActive || motion.AssaultBoostActive)
            {
                thrustActivity = 1f;
            }

            var sensorActivity = radar == null || !radar.RadarPowerEnabled
                ? 0f
                : radar.TrackOperationMode ? 0.48f : 0.24f;
            var activity = Mathf.Clamp01(Mathf.Max(thrustActivity, sensorActivity));
            return Mathf.Lerp(idle, peak, activity);
        }

        private void DrawTitle(Rect rect, string text)
        {
            GUI.Label(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 24f, 21f), text, titleStyle);
            DrawLine(new Vector2(rect.x + 12f, rect.y + 29f),
                new Vector2(rect.xMax - 12f, rect.y + 29f), HudDim, 1f);
        }

        private void DrawMetricRow(Rect rect, float offsetY, string label, string value, Color color)
        {
            labelStyle.normal.textColor = HudGreen;
            valueStyle.normal.textColor = color;
            GUI.Label(new Rect(rect.x + 13f, rect.y + offsetY, rect.width * 0.43f, 20f), label, labelStyle);
            GUI.Label(new Rect(rect.x + rect.width * 0.38f, rect.y + offsetY, rect.width * 0.56f, 20f), value, valueStyle);
        }

        private void DrawLabeledBar(
            Rect rect,
            float offsetY,
            string label,
            float fraction,
            Color color,
            string value)
        {
            labelStyle.normal.textColor = HudGreen;
            valueStyle.normal.textColor = color;
            GUI.Label(new Rect(rect.x + 13f, rect.y + offsetY, 52f, 18f), label, labelStyle);
            GUI.Label(new Rect(rect.x + 62f, rect.y + offsetY, rect.width - 75f, 18f), value, valueStyle);
            DrawStatusBar(new Rect(rect.x + 13f, rect.y + offsetY + 20f, rect.width - 26f, 8f),
                fraction, color);
        }

        private static void DrawStatusBar(Rect rect, float fraction, Color color)
        {
            DrawRect(rect, BarBackground);
            DrawRect(new Rect(rect.x, rect.y, rect.width * Mathf.Clamp01(fraction), rect.height), color);
            for (var i = 1; i < 10; i++)
            {
                DrawRect(new Rect(rect.x + rect.width * i / 10f - 0.5f, rect.y, 1f, rect.height),
                    new Color(0.005f, 0.06f, 0.035f, 0.78f));
            }
        }

        private static Color ResolveConditionColor(float fraction, bool highIsHealthy)
        {
            var condition = highIsHealthy ? fraction : 1f - fraction;
            return condition <= 0.2f ? HudRed : condition <= 0.45f ? HudAmber : HudGreen;
        }

        private void DrawSeparator(float x, Rect strip)
        {
            DrawLine(new Vector2(x, strip.y + 11f), new Vector2(x, strip.yMax - 11f), HudDim, 1f);
        }

        private void ResolveReferences()
        {
            if (motion == null) motion = GetComponent<MechFlightMotionController>();
            if (runtimeState == null) runtimeState = GetComponent<MechRuntimeState>();
            if (protection == null) protection = GetComponent<CombatTestTarget>();
            if (thermalSignature == null) thermalSignature = GetComponent<MechThermalSignature>();
            if (radar == null) radar = GetComponent<MechRadarController>();
        }

        private void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
            titleStyle.normal.textColor = HudGreen;
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
            };
            labelStyle.normal.textColor = HudGreen;
            valueStyle = new GUIStyle(labelStyle)
            {
                alignment = TextAnchor.MiddleRight,
            };
            axisStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
            axisStyle.normal.textColor = HudGreen;
        }

        private static void DrawCornerBrackets(Rect rect, Color color, float length, float thickness)
        {
            DrawLine(rect.position, rect.position + Vector2.right * length, color, thickness);
            DrawLine(rect.position, rect.position + Vector2.down * length, color, thickness);
            DrawLine(new Vector2(rect.xMax, rect.y), new Vector2(rect.xMax - length, rect.y), color, thickness);
            DrawLine(new Vector2(rect.xMax, rect.y), new Vector2(rect.xMax, rect.y + length), color, thickness);
            DrawLine(new Vector2(rect.x, rect.yMax), new Vector2(rect.x + length, rect.yMax), color, thickness);
            DrawLine(new Vector2(rect.x, rect.yMax), new Vector2(rect.x, rect.yMax - length), color, thickness);
            DrawLine(new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMax - length, rect.yMax), color, thickness);
            DrawLine(new Vector2(rect.xMax, rect.yMax), new Vector2(rect.xMax, rect.yMax - length), color, thickness);
        }

        private static void DrawCircle(Vector2 center, float radius, Color color, float thickness, int segments)
        {
            var previous = center + Vector2.up * radius;
            for (var i = 1; i <= segments; i++)
            {
                var angle = i * Mathf.PI * 2f / segments - Mathf.PI * 0.5f;
                var current = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                DrawLine(previous, current, color, thickness);
                previous = current;
            }
        }

        private static void DrawLine(Vector2 from, Vector2 to, Color color, float thickness)
        {
            var direction = to - from;
            if (direction.sqrMagnitude <= 0.001f)
            {
                return;
            }

            var previousColor = GUI.color;
            var previousMatrix = GUI.matrix;
            GUI.color = color;
            GUIUtility.RotateAroundPivot(Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg, from);
            GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, direction.magnitude, thickness),
                Texture2D.whiteTexture);
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }

        private static void DrawRect(Rect rect, Color color)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = previousColor;
        }
    }
}
