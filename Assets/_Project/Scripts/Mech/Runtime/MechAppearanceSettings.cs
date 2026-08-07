using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechAppearanceSettings
    {
        public Color baseColor = new Color(0.141f, 0.361f, 0.659f, 1f);
        public Color subColor = new Color(0.145f, 0.173f, 0.212f, 1f);
        public Color supportColor = new Color(0.345f, 0.490f, 0.667f, 1f);
        public Color optionalColor = new Color(0.086f, 0.184f, 0.322f, 1f);
        public Color deviceColor = new Color(0.420f, 0.467f, 0.522f, 1f);
        [Range(0, 5)] public int reflectivenessLevel = 2;
        [Range(0, 5)] public int lusterLevel = 3;
        [Range(0f, 1f)] public float weatheringAmount = 0.24f;

        public MechAppearanceSettings Clone()
        {
            return new MechAppearanceSettings
            {
                baseColor = baseColor,
                subColor = subColor,
                supportColor = supportColor,
                optionalColor = optionalColor,
                deviceColor = deviceColor,
                reflectivenessLevel = reflectivenessLevel,
                lusterLevel = lusterLevel,
                weatheringAmount = weatheringAmount
            };
        }

        public void Normalize()
        {
            baseColor = Opaque(baseColor);
            subColor = Opaque(subColor);
            supportColor = Opaque(supportColor);
            optionalColor = Opaque(optionalColor);
            deviceColor = Opaque(deviceColor);
            reflectivenessLevel = Mathf.Clamp(reflectivenessLevel, 0, 5);
            lusterLevel = Mathf.Clamp(lusterLevel, 0, 5);
            weatheringAmount = Mathf.Clamp01(weatheringAmount);
        }

        public Color GetRegionColor(int index)
        {
            return index switch
            {
                0 => baseColor,
                1 => subColor,
                2 => supportColor,
                3 => optionalColor,
                4 => deviceColor,
                _ => baseColor
            };
        }

        public void SetRegionColor(int index, Color value)
        {
            value = Opaque(value);
            switch (index)
            {
                case 0: baseColor = value; break;
                case 1: subColor = value; break;
                case 2: supportColor = value; break;
                case 3: optionalColor = value; break;
                case 4: deviceColor = value; break;
            }
        }

        private static Color Opaque(Color color)
        {
            color.r = Mathf.Clamp01(color.r);
            color.g = Mathf.Clamp01(color.g);
            color.b = Mathf.Clamp01(color.b);
            color.a = 1f;
            return color;
        }
    }

    public static class MechAppearanceSelectionStore
    {
        private const string AppearanceKey = "acunicombat.hangar.appearance.v1";

        public static MechAppearanceSettings Load()
        {
            var json = PlayerPrefs.GetString(AppearanceKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new MechAppearanceSettings();
            }

            try
            {
                var settings = JsonUtility.FromJson<MechAppearanceSettings>(json) ?? new MechAppearanceSettings();
                settings.Normalize();
                return settings;
            }
            catch (ArgumentException)
            {
                return new MechAppearanceSettings();
            }
        }

        public static void Save(MechAppearanceSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            var normalized = settings.Clone();
            normalized.Normalize();
            PlayerPrefs.SetString(AppearanceKey, JsonUtility.ToJson(normalized));
            PlayerPrefs.Save();
        }

        public static void ResetToDefault()
        {
            PlayerPrefs.DeleteKey(AppearanceKey);
            PlayerPrefs.Save();
        }
    }

    public static class MechAppearanceApplicator
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int BaseColorFactorId = Shader.PropertyToID("_BaseColorFactor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
        private static readonly int MetallicFactorId = Shader.PropertyToID("_MetallicFactor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int RoughnessFactorId = Shader.PropertyToID("_RoughnessFactor");
        private static readonly int WeatheringAmountId = Shader.PropertyToID("_WeatheringAmount");
        private static readonly int WeatheringColorId = Shader.PropertyToID("_WeatheringColor");
        private static readonly int ZoneModeId = Shader.PropertyToID("_ZoneMode");
        private static readonly int[] ZoneColorIds = CreatePropertyIds("_Zone", "Color", 7);
        private static readonly int[] ZoneMetallicIds = CreatePropertyIds("_Zone", "Metallic", 7);
        private static readonly int[] ZoneSmoothnessIds = CreatePropertyIds("_Zone", "Smoothness", 7);

        public static void Apply(GameObject root, MechAppearanceSettings source)
        {
            if (root == null)
            {
                return;
            }

            var settings = source?.Clone() ?? new MechAppearanceSettings();
            settings.Normalize();
            var adapter = root.GetComponent<MechAppearanceMaterialAdapter>();
            if (adapter == null)
            {
                adapter = root.AddComponent<MechAppearanceMaterialAdapter>();
            }
            adapter.Synchronize(root);

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            var block = new MaterialPropertyBlock();
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                var materials = renderer.sharedMaterials;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    var material = materials[materialIndex];
                    if (material == null)
                    {
                        continue;
                    }

                    renderer.GetPropertyBlock(block, materialIndex);
                    if (material.HasProperty(ZoneModeId))
                    {
                        ApplyAc6Material(block, material, settings);
                    }
                    else
                    {
                        ApplyGenericMaterial(block, material, settings);
                    }

                    renderer.SetPropertyBlock(block, materialIndex);
                    block.Clear();
                }
            }
        }

        private static void ApplyAc6Material(
            MaterialPropertyBlock block,
            Material material,
            MechAppearanceSettings settings)
        {
            var zoneColors = new[]
            {
                settings.baseColor,
                settings.subColor,
                settings.supportColor,
                settings.optionalColor,
                settings.deviceColor,
                Color.Lerp(settings.subColor, settings.supportColor, 0.35f),
                Color.Lerp(Color.black, settings.optionalColor, 0.22f)
            };
            for (var i = 0; i < zoneColors.Length; i++)
            {
                block.SetColor(ZoneColorIds[i], zoneColors[i]);
            }

            if (material.GetFloat(ZoneModeId) < 0.5f && IsPaintableMaterial(material))
            {
                block.SetColor(BaseColorId, settings.baseColor);
            }

            // AC6 exposes six steps on each axis. These calibrated URP values preserve the
            // same two independent controls without pretending its proprietary BRDF is Unity's.
            var reflection = settings.reflectivenessLevel / 5f;
            var luster = settings.lusterLevel / 5f;
            var baseMetallic = Mathf.Lerp(0.04f, 0.72f, reflection);
            var baseSmoothness = Mathf.Lerp(0.16f, 0.88f, luster);
            var metallicOffsets = new[] { -0.08f, 0.04f, -0.04f, 0.14f, 0.09f, 0.02f, -0.35f };
            var smoothnessOffsets = new[] { -0.06f, 0.04f, -0.02f, 0.10f, 0.06f, 0.01f, -0.28f };
            for (var i = 0; i < ZoneMetallicIds.Length; i++)
            {
                block.SetFloat(ZoneMetallicIds[i], Mathf.Clamp01(baseMetallic + metallicOffsets[i]));
                block.SetFloat(ZoneSmoothnessIds[i], Mathf.Clamp01(baseSmoothness + smoothnessOffsets[i]));
            }

            block.SetFloat(MetallicId, baseMetallic);
            block.SetFloat(SmoothnessId, baseSmoothness);
            block.SetFloat(WeatheringAmountId, settings.weatheringAmount);
            block.SetColor(WeatheringColorId, Color.Lerp(new Color(0.055f, 0.060f, 0.065f), settings.deviceColor, 0.12f));
        }

        private static void ApplyGenericMaterial(
            MaterialPropertyBlock block,
            Material material,
            MechAppearanceSettings settings)
        {
            if (!IsPaintableMaterial(material))
            {
                return;
            }

            var reflection = settings.reflectivenessLevel / 5f;
            var luster = settings.lusterLevel / 5f;
            var metallic = Mathf.Lerp(0.04f, 0.72f, reflection);
            var smoothness = Mathf.Lerp(0.16f, 0.88f, luster);

            if (material.HasProperty(BaseColorFactorId)) block.SetColor(BaseColorFactorId, settings.baseColor);
            if (material.HasProperty(BaseColorId)) block.SetColor(BaseColorId, settings.baseColor);
            if (material.HasProperty(ColorId)) block.SetColor(ColorId, settings.baseColor);
            if (material.HasProperty(MetallicFactorId)) block.SetFloat(MetallicFactorId, metallic);
            if (material.HasProperty(MetallicId)) block.SetFloat(MetallicId, metallic);
            if (material.HasProperty(SmoothnessId)) block.SetFloat(SmoothnessId, smoothness);
            if (material.HasProperty(RoughnessFactorId)) block.SetFloat(RoughnessFactorId, 1f - smoothness);
        }

        internal static bool IsPaintableMaterial(Material material)
        {
            if (material == null ||
                material.IsKeywordEnabled("_EMISSION") ||
                material.IsKeywordEnabled("_EMISSIVE") ||
                material.renderQueue > (int)RenderQueue.GeometryLast)
            {
                return false;
            }

            var name = material.name ?? string.Empty;
            return name.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Limiter", StringComparison.OrdinalIgnoreCase) < 0 &&
                   name.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) < 0 &&
                   !IsMarkingMaterial(material);
        }

        internal static bool IsMarkingMaterial(Material material)
        {
            if (material == null)
            {
                return false;
            }

            var name = material.name ?? string.Empty;
            var shaderName = material.shader != null ? material.shader.name : string.Empty;
            return ContainsMarkingToken(name) || ContainsMarkingToken(shaderName);
        }

        private static bool ContainsMarkingToken(string value)
        {
            return value.IndexOf("Marking", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Making", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int[] CreatePropertyIds(string prefix, string suffix, int count)
        {
            var result = new int[count];
            for (var i = 0; i < count; i++)
            {
                result[i] = Shader.PropertyToID($"{prefix}{i + 1}{suffix}");
            }

            return result;
        }
    }

    [DisallowMultipleComponent]
    internal sealed class MechAppearanceMaterialAdapter : MonoBehaviour
    {
        private const string AppearanceShaderName = "ACunicombat/AC6/Parts Approx";
        private static readonly string[] BaseTextureNames =
        {
            "baseColorTexture", "_BaseColorTexture", "_BaseMap", "_MainTex"
        };
        private static readonly string[] NormalTextureNames =
        {
            "normalTexture", "_NormalTexture", "_BumpMap", "_NormalMap"
        };
        private static readonly string[] EmissionTextureNames =
        {
            "emissiveTexture", "_EmissiveTexture", "_EmissionMap"
        };

        private readonly Dictionary<Material, Material> convertedMaterials = new Dictionary<Material, Material>();
        private readonly List<Material> ownedMaterials = new List<Material>();
        private Material hiddenOverlayMaterial;

        public void Synchronize(GameObject root)
        {
            if (root == null)
            {
                return;
            }

            var appearanceShader = Shader.Find(AppearanceShaderName);

            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                if (renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer)
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    var source = materials[materialIndex];
                    if (source == hiddenOverlayMaterial)
                    {
                        continue;
                    }

                    if (MechAppearanceApplicator.IsMarkingMaterial(source))
                    {
                        var hiddenMaterial = GetOrCreateHiddenOverlayMaterial();
                        if (hiddenMaterial != null)
                        {
                            materials[materialIndex] = hiddenMaterial;
                            changed = true;
                        }
                        continue;
                    }

                    if (appearanceShader == null || !ShouldConvert(source, appearanceShader))
                    {
                        continue;
                    }

                    if (!convertedMaterials.TryGetValue(source, out var converted) || converted == null)
                    {
                        converted = ConvertMaterial(source, appearanceShader);
                        if (converted == null)
                        {
                            continue;
                        }
                        convertedMaterials[source] = converted;
                        ownedMaterials.Add(converted);
                    }

                    materials[materialIndex] = converted;
                    changed = true;
                }

                if (changed)
                {
                    renderer.sharedMaterials = materials;
                }
            }
        }

        private Material GetOrCreateHiddenOverlayMaterial()
        {
            if (hiddenOverlayMaterial != null)
            {
                return hiddenOverlayMaterial;
            }

            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Unlit/Color");
            if (shader == null)
            {
                return null;
            }

            hiddenOverlayMaterial = new Material(shader)
            {
                name = "AC6_HiddenOverlaySlot",
                hideFlags = HideFlags.DontSave,
                renderQueue = (int)RenderQueue.Transparent
            };
            if (hiddenOverlayMaterial.HasProperty("_BaseColor")) hiddenOverlayMaterial.SetColor("_BaseColor", Color.clear);
            if (hiddenOverlayMaterial.HasProperty("_Color")) hiddenOverlayMaterial.SetColor("_Color", Color.clear);
            if (hiddenOverlayMaterial.HasProperty("_Surface")) hiddenOverlayMaterial.SetFloat("_Surface", 1f);
            if (hiddenOverlayMaterial.HasProperty("_Blend")) hiddenOverlayMaterial.SetFloat("_Blend", 0f);
            if (hiddenOverlayMaterial.HasProperty("_SrcBlend")) hiddenOverlayMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            if (hiddenOverlayMaterial.HasProperty("_DstBlend")) hiddenOverlayMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            if (hiddenOverlayMaterial.HasProperty("_ZWrite")) hiddenOverlayMaterial.SetInt("_ZWrite", 0);
            hiddenOverlayMaterial.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            hiddenOverlayMaterial.DisableKeyword("_ALPHATEST_ON");
            hiddenOverlayMaterial.SetShaderPassEnabled("DepthOnly", false);
            hiddenOverlayMaterial.SetShaderPassEnabled("ShadowCaster", false);
            hiddenOverlayMaterial.SetOverrideTag("RenderType", "Transparent");
            ownedMaterials.Add(hiddenOverlayMaterial);
            return hiddenOverlayMaterial;
        }

        private static bool ShouldConvert(Material source, Shader appearanceShader)
        {
            return source != null &&
                   source.shader != appearanceShader &&
                   MechAppearanceApplicator.IsPaintableMaterial(source);
        }

        private static Material ConvertMaterial(Material source, Shader appearanceShader)
        {
            if (!TryGetTexture(source, BaseTextureNames, out var baseTexture, out var baseTextureProperty))
            {
                // An unknown imported shader must keep its own material instead of becoming a flat
                // player-coloured surface. Add its actual texture property to the adapter first.
                return null;
            }

            var converted = new Material(appearanceShader)
            {
                name = source.name + "_PlayerAppearance",
                hideFlags = HideFlags.DontSave
            };

            converted.SetTexture("_BaseMap", baseTexture);
            converted.SetTextureScale("_BaseMap", source.GetTextureScale(baseTextureProperty));
            converted.SetTextureOffset("_BaseMap", source.GetTextureOffset(baseTextureProperty));

            if (TryGetTexture(source, NormalTextureNames, out var normalTexture, out _))
            {
                converted.SetTexture("_BumpMap", normalTexture);
                converted.SetFloat("_BumpScale", ReadNormalScale(source));
                converted.EnableKeyword("_NORMALMAP");
            }

            if (TryGetTexture(source, EmissionTextureNames, out var emissionTexture, out _))
            {
                converted.SetTexture("_EmissionMap", emissionTexture);
                converted.SetColor("_EmissionColor", Color.white);
                converted.SetFloat("_EmissionStrength", 1f);
                converted.EnableKeyword("_EMISSION");
            }

            // Full-catalog GLBs omit AC6's _3m zone mask but retain a neutral-grey structural
            // albedo. Multiplying that texture by the player's paint preserves panels and recesses
            // without restoring the imported material's white appearance.
            converted.SetFloat("_UseSourceAlbedo", 1f);
            converted.SetFloat("_ZoneMode", 0f);
            converted.SetFloat("_ZoneMaskStrength", 0f);
            converted.SetFloat("_PaintInfluence", 0f);
            converted.SetFloat("_AlbedoDetailStrength", 1f);
            converted.SetFloat("_Metallic", 0.38f);
            converted.SetFloat("_Smoothness", 0.38f);
            converted.renderQueue = (int)RenderQueue.Geometry;
            converted.SetOverrideTag("RenderType", "Opaque");
            return converted;
        }

        private static float ReadNormalScale(Material material)
        {
            if (material.HasProperty("normalTexture_scale"))
            {
                return Mathf.Clamp(material.GetFloat("normalTexture_scale"), 0f, 2f);
            }

            if (material.HasProperty("_BumpScale"))
            {
                return Mathf.Clamp(material.GetFloat("_BumpScale"), 0f, 2f);
            }

            return 0.8f;
        }

        private static bool TryGetTexture(
            Material material,
            IReadOnlyList<string> propertyNames,
            out Texture texture,
            out string propertyName)
        {
            for (var i = 0; i < propertyNames.Count; i++)
            {
                var candidate = propertyNames[i];
                if (!material.HasProperty(candidate))
                {
                    continue;
                }

                var value = material.GetTexture(candidate);
                if (value != null)
                {
                    texture = value;
                    propertyName = candidate;
                    return true;
                }
            }

            texture = null;
            propertyName = string.Empty;
            return false;
        }

        private void OnDestroy()
        {
            for (var i = 0; i < ownedMaterials.Count; i++)
            {
                var material = ownedMaterials[i];
                if (material == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    Destroy(material);
                }
                else
                {
                    DestroyImmediate(material);
                }
            }

            ownedMaterials.Clear();
            convertedMaterials.Clear();
            hiddenOverlayMaterial = null;
        }
    }
}
