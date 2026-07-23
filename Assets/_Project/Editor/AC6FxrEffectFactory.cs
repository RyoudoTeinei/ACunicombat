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
    private const string ModelSourceDir = "Tools/LocalOut/fxmodels";
    private const string EffectsRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Effects";
    private const string LibraryAssetPath = EffectsRoot + "/AC6_EffectPrefabLibrary.asset";

#pragma warning disable 0649
    [Serializable] private sealed class FxrJson
    {
        public int id;
        public List<int> proxyReferences;
        public List<UnityEmitter> unityEmitters;
    }
    [Serializable] private sealed class UnityEmitter
    {
        public string kind;
        public float[] color;
        public float radius;
        public float peakIntensity;
        public float lifeSeconds;
        public int texture;
        public int model;
        public int blendMode;
        public int burstCount;
        public int columns;
        public int totalFrames;
        public int frameMin;
        public int frameMax;
        public float width;
        public float length;
        public float[] size;
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

        var definitions = jsonFiles
            .Select(path => JsonUtility.FromJson<FxrJson>(File.ReadAllText(path)))
            .Where(data => data != null)
            .ToDictionary(data => data.id);
        var prefabs = new Dictionary<int, GameObject>();
        var resolving = new HashSet<int>();

        foreach (var data in definitions.Values)
        {
            var prefab = ResolveEffectPrefab(data.id, definitions, prefabs, resolving);
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

    private static GameObject ResolveEffectPrefab(
        int effectId,
        IReadOnlyDictionary<int, FxrJson> definitions,
        IDictionary<int, GameObject> cache,
        ISet<int> resolving)
    {
        if (cache.TryGetValue(effectId, out var cached)) return cached;
        if (!definitions.TryGetValue(effectId, out var data) || !resolving.Add(effectId)) return null;

        GameObject prefab = null;
        if (data.unityEmitters != null && data.unityEmitters.Count > 0)
        {
            prefab = BuildEffectPrefab(data);
        }
        else if (data.proxyReferences != null && data.proxyReferences.Count > 0)
        {
            var referenced = data.proxyReferences
                .Select(id => ResolveEffectPrefab(id, definitions, cache, resolving))
                .Where(value => value != null)
                .Distinct()
                .ToArray();
            if (referenced.Length == 1)
            {
                // A pure AC6 proxy is an alias. Keep the source effect id in the library but
                // share the resolved prefab so no extra transform is introduced.
                prefab = referenced[0];
            }
            else if (referenced.Length > 1)
            {
                prefab = BuildProxyPrefab(effectId, referenced);
            }
        }

        resolving.Remove(effectId);
        cache[effectId] = prefab;
        return prefab;
    }

    private static GameObject BuildProxyPrefab(int effectId, IEnumerable<GameObject> references)
    {
        var root = new GameObject($"FFX_{effectId}_Proxy");
        try
        {
            foreach (var reference in references)
            {
                var child = (GameObject)PrefabUtility.InstantiatePrefab(reference);
                child.transform.SetParent(root.transform, false);
            }

            return PrefabUtility.SaveAsPrefabAsset(root, $"{EffectsRoot}/FFX_{effectId}.prefab");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    private static GameObject BuildEffectPrefab(FxrJson data)
    {
        var root = new GameObject($"FFX_{data.id}");
        try
        {
            var built = 0;
            foreach (var emitter in data.unityEmitters)
            {
                if (ShouldSuppressUnsupportedModelCarrier(data.id, emitter))
                {
                    continue;
                }

                if (emitter.kind == "Billboard" && emitter.texture > 0)
                {
                    built += BuildBillboard(root.transform, data.id, emitter) ? 1 : 0;
                }
                else if (emitter.kind == "Light")
                {
                    built += BuildLight(root.transform, emitter) ? 1 : 0;
                }
                else if (emitter.kind == "Tracer")
                {
                    built += BuildTracer(root.transform, data.id, emitter) ? 1 : 0;
                }
                else if (emitter.kind == "Model")
                {
                    built += BuildModelProxy(root.transform, data.id, emitter) ? 1 : 0;
                }
                else if (emitter.kind == "Beam")
                {
                    built += BuildBeam(root.transform, data.id, emitter) ? 1 : 0;
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

    private static bool ShouldSuppressUnsupportedModelCarrier(int ffxId, UnityEmitter emitter)
    {
        // The AC6 launch flashes use s81040 as a shader-driven additive carrier. Its
        // geometry is not meant to be visible: the original shader dissolves and
        // camera-orients it. Rendering the FLVER as a normal Unity mesh exposes a
        // large hexagonal column, so retain the source billboard/light and omit only
        // this unsupported carrier for the two concrete missile launch effects.
        return emitter.kind == "Model" &&
               emitter.model == 81040 &&
               (ffxId == 6081000 || ffxId == 6081100);
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
        main.stopAction = ParticleSystemStopAction.Disable;

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
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(0f, (short)Mathf.Clamp(emitter.burstCount, 1, 64)),
        });

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

        ConfigureTextureSheetAnimation(particles, emitter);

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
        go.AddComponent<MechTimedEffectElement>().Configure(
            Mathf.Max(emitter.lifeSeconds, 0.05f));
        return true;
    }

    private static bool BuildTracer(Transform parent, int ffxId, UnityEmitter emitter)
    {
        var go = new GameObject($"Tracer_{emitter.texture}");
        go.transform.SetParent(parent, false);
        var trail = go.AddComponent<TrailRenderer>();
        trail.time = Mathf.Clamp(emitter.lifeSeconds, 0.08f, 1.5f);
        trail.startWidth = Mathf.Clamp(emitter.width, 0.015f, 1.5f);
        trail.endWidth = 0f;
        trail.minVertexDistance = 0.03f;
        trail.numCapVertices = 2;
        trail.alignment = LineAlignment.View;
        var color = ToColor(emitter.color, Color.white);
        trail.startColor = color;
        trail.endColor = new Color(color.r, color.g, color.b, 0f);
        var texture = emitter.texture > 0 ? ImportEffectTexture(emitter.texture) : null;
        trail.sharedMaterial = CreateEffectMaterial(ffxId, emitter.texture, texture, color);
        return true;
    }

    private static bool BuildModelProxy(Transform parent, int ffxId, UnityEmitter emitter)
    {
        var sourceModel = ImportEffectModel(emitter.model);
        if (sourceModel != null)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceModel);
            instance.name = $"Model_s{emitter.model:D5}";
            instance.transform.SetParent(parent, false);
            var modelScale = emitter.size != null && emitter.size.Length >= 3
                ? new Vector3(emitter.size[0], emitter.size[1], emitter.size[2])
                : Vector3.one;
            // FXR Model size fields are local scale multipliers for the referenced FLVER.
            instance.transform.localScale = new Vector3(
                Mathf.Max(Mathf.Abs(modelScale.x), 0.001f),
                Mathf.Max(Mathf.Abs(modelScale.y), 0.001f),
                Mathf.Max(Mathf.Abs(modelScale.z), 0.001f));
            foreach (var animator in instance.GetComponentsInChildren<Animator>(true))
            {
                UnityEngine.Object.DestroyImmediate(animator);
            }
            ConfigureEffectModelMaterials(instance, ffxId, emitter, ToColor(emitter.color, Color.white));
            instance.AddComponent<MechTimedEffectElement>().Configure(
                Mathf.Max(emitter.lifeSeconds, 0.05f));
            return true;
        }

        // Keep a visible fallback when a newly encountered FXR references a model that the
        // local extraction has not exported yet.
        var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = $"ModelProxy_{emitter.model}";
        go.transform.SetParent(parent, false);
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        var size = emitter.size != null && emitter.size.Length >= 3
            ? new Vector3(emitter.size[0], emitter.size[1], emitter.size[2])
            : new Vector3(0.2f, 0.2f, 0.8f);
        // Unity's capsule is two units tall on local Y; after rotation it points along +Z.
        go.transform.localScale = new Vector3(
            Mathf.Clamp(size.x, 0.02f, 4f),
            Mathf.Clamp(size.z * 0.5f, 0.05f, 12f),
            Mathf.Clamp(size.y, 0.02f, 4f));
        var collider = go.GetComponent<Collider>();
        if (collider != null) UnityEngine.Object.DestroyImmediate(collider);
        var color = ToColor(emitter.color, Color.white);
        go.GetComponent<Renderer>().sharedMaterial = CreateEffectMaterial(ffxId, -emitter.model, null, color);
        go.AddComponent<MechTimedEffectElement>().Configure(
            Mathf.Max(emitter.lifeSeconds, 0.05f));
        return true;
    }

    private static GameObject ImportEffectModel(int modelId)
    {
        if (modelId <= 0)
        {
            return null;
        }

        var modelName = $"s{modelId:D5}.glb";
        var assetPath = $"{EffectsRoot}/Models/{modelName}";
        var source = Path.Combine(ModelSourceDir, modelName);
        if (!File.Exists(source))
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        }

        EnsureFolder($"{EffectsRoot}/Models");
        var shouldCopy = !File.Exists(assetPath) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(assetPath);
        if (shouldCopy)
        {
            File.Copy(source, assetPath, overwrite: true);
        }
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        return AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
    }

    private static void ConfigureEffectModelMaterials(GameObject instance, int ffxId, UnityEmitter emitter, Color color)
    {
        // AC6 effect FLVERs are not all physical models. Models whose MTD uses NoLight_Add
        // are carrier meshes for luminous textures. Their diffuse maps commonly have an
        // opaque black background because the game shader removes it through additive
        // blending, so importing them as regular PBR materials exposes the whole mesh.
        if (!UsesAdditiveEffectMaterial(emitter.model))
        {
            return;
        }

        var renderers = instance.GetComponentsInChildren<Renderer>(true);
        for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
        {
            var renderer = renderers[rendererIndex];
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var materials = renderer.sharedMaterials;
            for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
            {
                materials[materialIndex] = CreateAdditiveModelMaterial(
                    ffxId,
                    emitter.model,
                    rendererIndex,
                    materialIndex,
                    materials[materialIndex],
                    color,
                    emitter);
            }

            renderer.sharedMaterials = materials;
        }
    }

    private static bool UsesAdditiveEffectMaterial(int modelId)
    {
        var reportPath = Path.Combine(ModelSourceDir, $"s{modelId:D5}.report.json");
        if (!File.Exists(reportPath))
        {
            return false;
        }

        var report = File.ReadAllText(reportPath);
        return report.IndexOf("NoLight_Add", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Material CreateAdditiveModelMaterial(
        int ffxId,
        int modelId,
        int rendererIndex,
        int materialIndex,
        Material source,
        Color color,
        UnityEmitter emitter)
    {
        var materialPath =
            $"{EffectsRoot}/FFX_{ffxId}_model{modelId:D5}_r{rendererIndex}_m{materialIndex}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            material = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            AssetDatabase.CreateAsset(material, materialPath);
        }

        var texture = GetSourceMaterialTexture(source);
        ConfigureAdditiveMaterial(material, texture, color);
        if (source != null)
        {
            ApplyAtlasFrame(material, source.mainTextureScale, source.mainTextureOffset, emitter);
        }

        EditorUtility.SetDirty(material);
        return material;
    }

    private static void ConfigureTextureSheetAnimation(ParticleSystem particles, UnityEmitter emitter)
    {
        var columns = Mathf.Max(emitter.columns, 1);
        var totalFrames = Mathf.Max(emitter.totalFrames, 1);
        if (columns == 1 && totalFrames == 1)
        {
            return;
        }

        var rows = Mathf.CeilToInt(totalFrames / (float)columns);
        var sheet = particles.textureSheetAnimation;
        sheet.enabled = true;
        sheet.mode = ParticleSystemAnimationMode.Grid;
        sheet.numTilesX = columns;
        sheet.numTilesY = rows;
        sheet.animation = ParticleSystemAnimationType.WholeSheet;
        sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);

        var tileCount = columns * rows;
        var minFrame = Mathf.Clamp(emitter.frameMin, 0, totalFrames - 1);
        var maxFrame = Mathf.Clamp(Mathf.Max(emitter.frameMax, minFrame), minFrame, totalFrames - 1);
        sheet.startFrame = new ParticleSystem.MinMaxCurve(
            minFrame / (float)tileCount,
            maxFrame / (float)tileCount);
        sheet.cycleCount = 1;
    }

    private static void ApplyAtlasFrame(
        Material material,
        Vector2 sourceScale,
        Vector2 sourceOffset,
        UnityEmitter emitter)
    {
        var columns = Mathf.Max(emitter.columns, 1);
        var totalFrames = Mathf.Max(emitter.totalFrames, 1);
        if (columns == 1 && totalFrames == 1)
        {
            material.mainTextureScale = sourceScale;
            material.mainTextureOffset = sourceOffset;
            return;
        }

        var rows = Mathf.CeilToInt(totalFrames / (float)columns);
        var frame = Mathf.Clamp(emitter.frameMin, 0, totalFrames - 1);
        var column = frame % columns;
        var row = frame / columns;
        var atlasScale = new Vector2(1f / columns, 1f / rows);
        var atlasOffset = new Vector2(column * atlasScale.x, (rows - 1 - row) * atlasScale.y);
        material.mainTextureScale = Vector2.Scale(sourceScale, atlasScale);
        material.mainTextureOffset = Vector2.Scale(sourceOffset, atlasScale) + atlasOffset;
    }

    private static Texture GetSourceMaterialTexture(Material source)
    {
        if (source == null)
        {
            return null;
        }

        if (source.mainTexture != null)
        {
            return source.mainTexture;
        }

        return source.HasProperty("_BaseMap") ? source.GetTexture("_BaseMap") : null;
    }

    private static bool BuildBeam(Transform parent, int ffxId, UnityEmitter emitter)
    {
        var go = new GameObject($"Beam_{emitter.texture}");
        go.transform.SetParent(parent, false);
        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.positionCount = 2;
        line.SetPosition(0, Vector3.zero);
        line.SetPosition(1, Vector3.forward * Mathf.Clamp(emitter.length, 0.1f, 20f));
        line.startWidth = Mathf.Clamp(emitter.width, 0.01f, 2f);
        line.endWidth = 0f;
        line.numCapVertices = 2;
        var color = ToColor(emitter.color, Color.white);
        line.startColor = color;
        line.endColor = new Color(color.r, color.g, color.b, 0f);
        var texture = emitter.texture > 0 ? ImportEffectTexture(emitter.texture) : null;
        line.sharedMaterial = CreateEffectMaterial(ffxId, emitter.texture, texture, color);
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
        return CreateEffectMaterial(ffxId, textureId, texture, Color.white);
    }

    private static Material CreateEffectMaterial(
        int ffxId,
        int textureId,
        Texture2D texture,
        Color color)
    {
        var materialPath = $"{EffectsRoot}/FFX_{ffxId}_tex{textureId}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            material = new Material(shader != null ? shader : Shader.Find("Sprites/Default"));
            AssetDatabase.CreateAsset(material, materialPath);
        }

        ConfigureAdditiveMaterial(material, texture, color);
        EditorUtility.SetDirty(material);
        return material;
    }

    private static void ConfigureAdditiveMaterial(Material material, Texture texture, Color color)
    {
        material.mainTexture = texture;
        material.color = color;
        if (material.HasProperty("_BaseMap"))
        {
            material.SetTexture("_BaseMap", texture);
        }
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);

        // URP Particles/Unlit additive transparent setup.
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 2f);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_SrcBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlendAlpha", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_ZWrite", 0);
            if (material.HasProperty("_Cull")) material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.SetShaderPassEnabled("DepthOnly", false);
            material.SetShaderPassEnabled("ShadowCaster", false);
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            material.SetOverrideTag("RenderType", "Transparent");
        }
    }

    private static Color ToColor(float[] rgba, Color fallback)
    {
        return rgba != null && rgba.Length >= 3
            ? new Color(rgba[0], rgba[1], rgba[2], rgba.Length > 3 ? rgba[3] : 1f)
            : fallback;
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
