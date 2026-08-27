using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Ownship six-axis thrust indicator. It reads the active thruster channels directly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechEngineeringStatusPresenter : MonoBehaviour
    {
        private static readonly Color HudGreen = new(0.28f, 1f, 0.53f, 0.96f);
        private static readonly Color HudDim = new(0.10f, 0.35f, 0.20f, 0.72f);
        private static readonly Color HudCyan = new(0.24f, 0.92f, 0.82f, 0.96f);
        private static readonly Color BandBackground = new(0.008f, 0.055f, 0.035f, 0.68f);

        [SerializeField] private MechFlightMotionController motion;
        [SerializeField] private MechRuntimeState runtimeState;
        [SerializeField] private bool visible = true;

        private GUIStyle titleStyle;
        private GUIStyle axisStyle;

        private void Awake()
        {
            ResolveReferences();
        }

        private void OnGUI()
        {
            if (!visible || MechSolarSystemMap.IsAnyMapVisible ||
                MechAssemblyMenu.IsAnyMenuVisible)
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
            var previousMatrix = MechHudLayout.BeginScaledGui();
            var width = Mathf.Clamp(MechHudLayout.Width * 0.16f, 280f, 360f);
            var height = Mathf.Clamp(MechHudLayout.Height * 0.22f, 210f, 290f);
            var strip = new Rect(
                MechHudLayout.Width - width - 18f,
                18f,
                width,
                height);

            var previousDepth = GUI.depth;
            GUI.depth = -95;
            DrawRect(strip, BandBackground);
            DrawCornerBrackets(strip, HudGreen, 13f, 1.5f);

            DrawThrustModule(strip);
            GUI.depth = previousDepth;
            GUI.matrix = previousMatrix;
        }

        private void DrawThrustModule(Rect rect)
        {
            DrawTitle(rect, "六向推力");
            var center = new Vector2(rect.center.x, rect.y + rect.height * 0.58f);
            var radius = Mathf.Min(rect.width * 0.32f, rect.height * 0.34f);
            var forceLocal = ResolveLocalThrusterForce();
            var stats = runtimeState.AggregatedStats;

            var forward = NormalizeDirectionalForce(forceLocal.z, stats.mainThrustKn, stats.reverseThrustKn);
            var lateral = NormalizeDirectionalForce(forceLocal.x, stats.lateralThrustKn, stats.lateralThrustKn);
            var vertical = NormalizeDirectionalForce(forceLocal.y, stats.upwardThrustKn, stats.downwardThrustKn);

            DrawAxisPair(center, new Vector2(-0.72f, -0.69f), radius, forward, "前", "后");
            DrawAxisPair(center, Vector2.right, radius, lateral, "右", "左");
            DrawAxisPair(center, Vector2.up, radius, vertical, "上", "下");
            DrawCircle(center, 10f, HudGreen, 2f, 28);
            DrawLine(center + Vector2.left * 6f, center + Vector2.right * 6f, HudGreen, 1.6f);
            DrawLine(center + Vector2.up * 6f, center + Vector2.down * 6f, HudGreen, 1.6f);
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
            var labelDistance = radius + 20f;
            var positivePoint = center + positiveDirection * labelDistance;
            var negativePoint = center - positiveDirection * labelDistance;
            GUI.Label(new Rect(positivePoint.x - 18f, positivePoint.y - 13f, 36f, 26f), positiveLabel, axisStyle);
            GUI.Label(new Rect(negativePoint.x - 18f, negativePoint.y - 13f, 36f, 26f), negativeLabel, axisStyle);
        }

        private static void DrawThrustPointer(Vector2 center, Vector2 direction, float radius, float demand)
        {
            var clamped = Mathf.Clamp01(demand);
            var end = center + direction * radius;
            var activeEnd = center + direction * radius * Mathf.Lerp(0.28f, 1f, clamped);
            DrawLine(center + direction * 13f, end, HudDim, 1.7f);

            var activeColor = Color.Lerp(HudDim, HudCyan, Mathf.Clamp01(clamped * 1.7f));
            DrawLine(center + direction * 13f, activeEnd, activeColor, Mathf.Lerp(1.7f, 4f, clamped));
            var normal = new Vector2(-direction.y, direction.x);
            var tipSize = 7f + clamped * 4f;
            DrawLine(activeEnd, activeEnd - direction * tipSize + normal * tipSize * 0.55f, activeColor, 2f);
            DrawLine(activeEnd, activeEnd - direction * tipSize - normal * tipSize * 0.55f, activeColor, 2f);
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

        private void DrawTitle(Rect rect, string text)
        {
            GUI.Label(new Rect(rect.x + 12f, rect.y + 7f, rect.width - 24f, 21f), text, titleStyle);
            DrawLine(new Vector2(rect.x + 12f, rect.y + 29f),
                new Vector2(rect.xMax - 12f, rect.y + 29f), HudDim, 1f);
        }

        private void ResolveReferences()
        {
            if (motion == null) motion = GetComponent<MechFlightMotionController>();
            if (runtimeState == null) runtimeState = GetComponent<MechRuntimeState>();
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
            axisStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
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
