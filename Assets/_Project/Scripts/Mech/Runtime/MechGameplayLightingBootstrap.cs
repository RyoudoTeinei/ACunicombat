using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Keeps generated gameplay scenes readable when the saved scene predates
    /// the current deep-space lighting setup.
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

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor =
                new Color(0.16f, 0.19f, 0.24f, 1f);
            RenderSettings.ambientEquatorColor =
                new Color(0.085f, 0.10f, 0.13f, 1f);
            RenderSettings.ambientGroundColor =
                new Color(0.035f, 0.04f, 0.055f, 1f);
            RenderSettings.ambientIntensity = 0.85f;

            var fillObject = GameObject.Find(FillLightName);
            if (fillObject == null)
            {
                fillObject = new GameObject(FillLightName);
            }

            var fillLight = fillObject.GetComponent<Light>();
            if (fillLight == null)
            {
                fillLight = fillObject.AddComponent<Light>();
            }

            fillLight.type = LightType.Directional;
            fillLight.color = new Color(0.46f, 0.58f, 0.72f, 1f);
            fillLight.intensity = 0.9f;
            fillLight.shadows = LightShadows.None;
            fillLight.renderMode = LightRenderMode.ForcePixel;
            fillLight.transform.rotation = Quaternion.Euler(24f, 148f, 0f);
        }
    }
}
