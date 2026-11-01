using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum SubtitleFaction { Player, Friendly, Hostile, Neutral }

    [DisallowMultipleComponent]
    public sealed class MechSubtitlePresenter : MonoBehaviour
    {
        private static string role = "", text = "";
        private static SubtitleFaction faction;
        private static float hideAt;
        private GUIStyle roleStyle, textStyle;

        public static void Show(string speaker, string line, SubtitleFaction side = SubtitleFaction.Neutral, float seconds = 4f)
        {
            if (string.IsNullOrWhiteSpace(speaker) || string.IsNullOrWhiteSpace(line)) { Clear(); return; }
            role = speaker.Trim(); text = line.Trim(); faction = side; hideAt = Time.unscaledTime + Mathf.Max(.1f, seconds);
        }

        public static void Clear() { role = text = ""; hideAt = 0f; }

        public static Color FactionColor(SubtitleFaction side) => side switch
        {
            SubtitleFaction.Player => new Color(.72f,.94f,1f,1f),
            SubtitleFaction.Friendly => new Color(.35f,1f,.62f,1f),
            SubtitleFaction.Hostile => new Color(1f,.36f,.30f,1f),
            _ => new Color(.76f,.79f,.77f,1f),
        };

        private void Update() { if (hideAt > 0f && Time.unscaledTime >= hideAt) Clear(); }
        private void OnDisable() { Clear(); }

        private void OnGUI()
        {
            if (string.IsNullOrEmpty(role) || string.IsNullOrEmpty(text)) return;
            if (roleStyle == null)
            {
                var font = Font.CreateDynamicFontFromOSFont(new[]{"Microsoft YaHei","SimHei","Consolas"}, 22);
                roleStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 22, wordWrap = false };
                textStyle = new GUIStyle(roleStyle) { wordWrap = true };
                textStyle.normal.textColor = Color.white;
            }
            roleStyle.normal.textColor = FactionColor(faction);
            float width = Mathf.Min(920f, Screen.width * .82f);
            float x = (Screen.width - width) * .5f, y = Screen.height - 188f;
            var roleContent = new GUIContent(role + "：");
            float roleWidth = Mathf.Ceil(roleStyle.CalcSize(roleContent).x);
            var textContent = new GUIContent(text);
            float textWidth = width - roleWidth - 30f;
            float height = Mathf.Max(46f, textStyle.CalcHeight(textContent, textWidth) + 18f);
            var background = new Rect(x, y, width, height);
            int depth = GUI.depth; GUI.depth = -4500;
            GUI.DrawTexture(background, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(.18f,.20f,.21f,.78f), 0, 0);
            GUI.Label(new Rect(x+12, y+8, roleWidth, height-16), roleContent, roleStyle);
            GUI.Label(new Rect(x+roleWidth+18, y+8, textWidth, height-16), textContent, textStyle);
            GUI.depth = depth;
        }
    }
}
