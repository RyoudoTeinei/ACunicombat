using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Supplies a conservative lighting fallback for gameplay scenes that do not
    /// contain the authored deep-space fill light.
    /// </summary>
    public static class MechGameplayLightingBootstrap
    {
        private const string FillLightName = "Gameplay Fill Light";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void EnsureGameplayLighting()
        {
            if (Object.FindAnyObjectByType<MechPlayerRig>() == null)
            {
                return;
            }

            var fillObject = GameObject.Find(FillLightName);
            var fillLight = fillObject != null
                ? fillObject.GetComponent<Light>()
                : null;
            if (fillLight != null)
            {
                // Generated scenes own their lighting. Do not replace their current values with
                // fallback values every time Play Mode starts.
                return;
            }

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor =
                new Color(0.16f, 0.19f, 0.24f, 1f);
            RenderSettings.ambientEquatorColor =
                new Color(0.085f, 0.10f, 0.13f, 1f);
            RenderSettings.ambientGroundColor =
                new Color(0.035f, 0.04f, 0.055f, 1f);
            RenderSettings.ambientIntensity = 0.42f;

            if (fillObject == null)
            {
                fillObject = new GameObject(FillLightName);
            }

            if (fillLight == null)
            {
                fillLight = fillObject.AddComponent<Light>();
            }

            fillLight.type = LightType.Directional;
            fillLight.color = new Color(0.46f, 0.58f, 0.72f, 1f);
            fillLight.intensity = 0.5f;
            fillLight.shadows = LightShadows.None;
            fillLight.renderMode = LightRenderMode.ForcePixel;
            fillLight.transform.rotation = Quaternion.Euler(24f, 148f, 0f);
        }
    }
}
