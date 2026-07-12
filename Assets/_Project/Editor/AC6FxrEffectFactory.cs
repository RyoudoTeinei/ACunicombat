using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Translates FXR effect definitions (extracted to a flat JSON schema by
/// Tools/FxrTranslator/extract-fxr.mjs) into Unity effect prefabs and a
/// MechEffectPrefabLibrary. Curve interpolation and polymorphic property resolution already
/// happened on the JS side; this factory only builds ParticleSystems, materials and lights.
/// Source JSON, textures and the generated assets all carry game-derived data and stay
/// machine-local (Generated/ and Tools/LocalOut are gitignored).
/// </summary>
public static class AC6FxrEffectFactory
{
    private const string JsonSourceDir = "Tools/LocalOut/fxr_json";
    private const string TextureSourceDir = "Tools/LocalOut/fxtex";
    private const string EffectsRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Effects";
    private const string LibraryAssetPath = EffectsRoot + "/AC6_EffectPrefabLibrary.asset";

#pragma warning disable 0649
    [Serializable] private sealed class FxrJson { public int id; public List<UnityEmitter> unityEmitters; }
    [Serializable] private sealed class UnityEmitter
    {
        public string kind;
        public float[] color;
        public float radius;
        public float peakIntensity;
        public float lifeSeconds;
        public int texture;
        public int blendMode;
        public List<CurveSample> sizeCurve;
        public float heightScale;
        public List<GradientSample> gradient;
    }
    [Serializable] private sealed class CurveSample { public float t; public float value; }
    [Serializable] private sealed class GradientSample { public float t; public float[] rgba; }
#pragma warning restore 0649

    /// <summary>
    /// Builds prefabs for every ffx_*.json found locally and returns the library, or null
    /// when no extraction exists on this machine.
    /// </summary>
    public static MechEffectPrefabLibrary BuildLibrary()
    {
        if (!Directory.Exists(JsonSourceDir))
        {
            return AssetDatabase.LoadAssetAtPath<MechEffectPrefabLibrary>(LibraryAssetPath);
        }

        var jsonFiles = Directory.GetFiles(JsonSourceDir, "ffx_*.json");
        if (jsonFiles.Length == 0)
        {
            return AssetDatabase.LoadAssetAtPath<MechEffectPrefabLibrary>(LibraryAssetPath);
        }

        EnsureFolder(EffectsRoot);
        var library = AssetDatabase.LoadAssetAtPath<MechEffectPrefabLibrary>(LibraryAssetPath);
        if (library == null)
        {
            library = ScriptableObject.CreateInstance<MechEffectPrefabLibrary>();
            AssetDatabase.CreateAsset(library, LibraryAssetPath);
        }

        foreach (var jsonPath in jsonFiles)
        {
            var data = JsonUtility.FromJson<FxrJson>(File.ReadAllText(jsonPath));
            if (data == null || data.unityEmitters == null || data.unityEmitters.Count == 0)
            {
                continue;
            }

            var prefab = BuildEffectPrefab(data);
            if (prefab == null)
            {
                continue;
            }

            var entry = library.entries.FirstOrDefault(e => e.ffxId == data.id);
            if (entry == null)
            {
                entry = new MechEffectPrefabLibrary.Entry { ffxId = data.id };
                library.entries.Add(entry);
            }

            entry.prefab = prefab;
        }

        EditorUtility.SetDirty(library);
        AssetDatabase.SaveAssets();
        return library;
    }

    private static GameObject BuildEffectPrefab(FxrJson data)
    {
        var root = new GameObject($"FFX_{data.id}");
        try
        {
            var built = 0;
            foreach (var emitter in data.unityEmitters)
            {
                if (emitter.kind == "Billboard" && emitter.texture > 0)
                {
                    built += BuildBillboard(root.transform, data.id, emitter) ? 1 : 0;
                }
                else if (emitter.kind == "Light")
                {
                    built += BuildLight(root.transform, emitter) ? 1 : 0;
                }
            }

            if (built == 0)
            {
                return null;
            }

            var prefabPath = $"{EffectsRoot}/FFX_{data.id}.prefab";
            return PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static bool BuildBillboard(Transform parent, int ffxId, UnityEmitter emitter)
    {
        var texture = ImportEffectTexture(emitter.texture);
        if (texture == null)
        {
            return false;
        }

        var go = new GameObject($"Billboard_{emitter.texture}");
        go.transform.SetParent(parent, false);

        var particles = go.AddComponent<ParticleSystem>();
        var main = particles.main;
        var life = Mathf.Max(emitter.lifeSeconds, 0.15f);
        main.duration = life;
        main.loop = false;
        main.startLifetime = life;
        main.startSpeed = 0.6f;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        var maxSize = 0.2f;
        if (emitter.sizeCurve != null)
        {
            foreach (var sample in emitter.sizeCurve)
            {
                maxSize = Mathf.Max(maxSize, sample.value);
            }
        }

        main.startSize = maxSize * Mathf.Max(emitter.heightScale, 0.25f);

        var emission = particles.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 5) });

        var shape = particles.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 12f;
        shape.radius = 0.03f;

        if (emitter.sizeCurve != null && emitter.sizeCurve.Count > 1)
        {
            var curve = new AnimationCurve();
            foreach (var sample in emitter.sizeCurve)
            {
                curve.AddKey(sample.t, maxSize > 0f ? sample.value / maxSize : 1f);
            }

            var sizeOverLifetime = particles.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, curve);
        }

        if (emitter.gradient != null && emitter.gradient.Count > 0)
        {
            var keys = emitter.gradient.Take(8).ToArray();
            var gradient = new Gradient();
            gradient.SetKeys(
                keys.Select(k => new GradientColorKey(new Color(k.rgba[0], k.rgba[1], k.rgba[2]), k.t)).ToArray(),
                keys.Select(k => new GradientAlphaKey(k.rgba.Length > 3 ? k.rgba[3] : 1f, k.t)).ToArray());
            var colorOverLifetime = particles.colorOverLifetime;
            colorOverLifetime.enabled = true;
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);
        }

        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sharedMaterial = CreateAdditiveMaterial(ffxId, emitter.texture, texture);
        return true;
    }

    private static bool BuildLight(Transform parent, UnityEmitter emitter)
    {
        var go = new GameObject("Light");
        go.transform.SetParent(parent, false);
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.range = Mathf.Clamp(emitter.radius, 1f, 25f);
        light.color = emitter.color != null && emitter.color.Length >= 3
            ? new Color(emitter.color[0], emitter.color[1], emitter.color[2])
            : Color.white;
        // FromSoft multipliers are HDR-scale; compress into a sane punctual light intensity.
        light.intensity = Mathf.Clamp(emitter.peakIntensity / 60f, 0.5f, 8f);
        return true;
    }

    private static Texture2D ImportEffectTexture(int textureId)
    {
        var assetPath = $"{EffectsRoot}/Tex/s{textureId}_a.png";
        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
        if (existing != null)
        {
            return existing;
        }

        var source = Path.Combine(TextureSourceDir, $"s{textureId}_a.png");
        if (!File.Exists(source))
        {
            return null;
        }

        EnsureFolder($"{EffectsRoot}/Tex");
        File.Copy(source, assetPath, overwrite: true);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    private static Material CreateAdditiveMaterial(int ffxId, int textureId, Texture2D texture)
    {
        var materialPath = $"{EffectsRoot}/FFX_{ffxId}_tex{textureId}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            material = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            AssetDatabase.CreateAsset(material, materialPath);
        }

        material.mainTexture = texture;
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", texture);
        }

        // URP Particles/Unlit additive transparent setup.
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_ZWrite", 0);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    private static void EnsureFolder(string assetPath)
    {
        var parts = assetPath.Split('/');
        var current = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }
}
