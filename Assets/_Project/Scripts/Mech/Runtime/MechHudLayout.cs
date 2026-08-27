using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Shared combat-HUD coordinate system. The authored layout is 1920x1080; larger
    /// displays retain the existing pixel sizes, while smaller displays scale uniformly.
    /// </summary>
    internal static class MechHudLayout
    {
        public const float ReferenceWidth = 1920f;
        public const float ReferenceHeight = 1080f;

        public static float Scale
        {
            get
            {
                var widthScale = Screen.width / ReferenceWidth;
                var heightScale = Screen.height / ReferenceHeight;
                return Mathf.Clamp(Mathf.Min(widthScale, heightScale), 0.4f, 1f);
            }
        }

        public static float Width => Screen.width / Scale;
        public static float Height => Screen.height / Scale;

        public static Matrix4x4 BeginScaledGui()
        {
            var previous = GUI.matrix;
            var scale = Scale;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f)) * previous;
            return previous;
        }

        public static Vector2 ScreenToHudPoint(Vector3 screenPoint)
        {
            var scale = Scale;
            return new Vector2(screenPoint.x / scale, (Screen.height - screenPoint.y) / scale);
        }
    }
}
