using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed partial class MechBattlefieldMap
    {
        private static readonly Color Background = new Color32(0x04, 0x09, 0x0C, 0xFF);
        private static readonly Color PanelBackground = new Color32(0x08, 0x12, 0x15, 0x00);
        private static readonly Color PanelRaised = new Color32(0x0D, 0x1B, 0x1E, 0xFF);
        private static readonly Color PanelHover = new Color32(0x14, 0x2A, 0x2C, 0xFF);
        private static readonly Color PanelActive = new Color32(0x18, 0x35, 0x34, 0xFF);
        private static readonly Color PanelBorder = new Color32(0x35, 0x50, 0x52, 0xFF);
        private static readonly Color MutedColor = new Color32(0x32, 0x47, 0x49, 0xFF);
        private static readonly Color SecondaryText = new Color32(0x81, 0x9B, 0x9C, 0xFF);
        private static readonly Color DisabledText = new Color32(0x4B, 0x5D, 0x5E, 0xFF);
        internal static readonly Color FriendlyColor = new Color32(0x55, 0xC4, 0xD8, 0xFF);
        private static readonly Color HostileColor = new Color32(0xE0, 0x65, 0x58, 0xFF);
        private static readonly Color UnknownColor = new Color32(0xD6, 0xB6, 0x64, 0xFF);
        private static readonly Color PrimaryText = new Color32(0xD2, 0xDF, 0xDC, 0xFF);
        private static readonly Color GridMinor = new Color(.12f, .20f, .22f, .48f);
        private static readonly Color GridMajor = new Color(.22f, .34f, .35f, .72f);

        private const float PanelStroke = 1f;
        private const float ControlHeight = 32f;

        private void DrawPanelHeader(Rect bounds, string title, string trailing = null)
        {
            Fill(new Rect(bounds.x, bounds.y, bounds.width, 38), PanelRaised);
            Line(new Vector2(bounds.x, bounds.y + 38), new Vector2(bounds.xMax, bounds.y + 38), PanelBorder);
            MenuLabel(new Rect(bounds.x + 4, bounds.y, bounds.width - 8, 38), title, PrimaryText, 15);
            if (!string.IsNullOrEmpty(trailing))
            {
                BoundedLabel(new Rect(bounds.x + 100, bounds.y, bounds.width - 116, 38), trailing, SecondaryText, 11);
                texts[textCount - 1].alignment = TextAnchor.MiddleRight;
            }
        }
    }
}
