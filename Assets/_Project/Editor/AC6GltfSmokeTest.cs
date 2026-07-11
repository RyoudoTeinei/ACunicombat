using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Batchmode smoke test for the FLVER->glTF pipeline output.
/// Loads the locally generated GLBs (glTFast scripted importer), instantiates the Melander
/// assembly, samples its HKX-derived animation at several times, verifies bounds stay sane,
/// and renders screenshots to Assets/_Project/LocalSamples/AC6MelanderCase/Reports/.
///
/// Run with:
///   Unity.exe -batchmode -projectPath . -executeMethod AC6GltfSmokeTest.Run -quit -logFile smoke.log
/// </summary>
public static class AC6GltfSmokeTest
{
    const string GltfFolder = "Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf";
    const string ReportFolder = "Assets/_Project/LocalSamples/AC6MelanderCase/Reports";

    public static void Run()
    {
        var failures = 0;
        var log = new StringBuilder();
        try
        {
            Directory.CreateDirectory(ReportFolder);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var glbPaths = Directory.GetFiles(GltfFolder, "*.glb").Select(p => p.Replace('\\', '/')).ToList();
            log.AppendLine($"GLBs found: {glbPaths.Count}");

            foreach (var path in glbPaths)
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                failures += TestOne(path, log) ? 0 : 1;
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("EXCEPTION: " + ex);
            failures++;
        }

        File.WriteAllText(Path.Combine(ReportFolder, "gltf_smoke_report.txt"), log.ToString());
        Debug.Log("[AC6GltfSmokeTest]\n" + log);
        if (Application.isBatchMode)
            EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    static bool TestOne(string assetPath, StringBuilder log)
    {
        var name = Path.GetFileNameWithoutExtension(assetPath);
        var main = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (main == null)
        {
            var importer = AssetImporter.GetAtPath(assetPath);
            var all = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            var mainObj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            log.AppendLine($"[{name}] FAIL: main asset did not import as GameObject");
            log.AppendLine($"    importer={importer?.GetType().FullName ?? "<null>"} mainAsset={mainObj?.GetType().FullName ?? "<null>"} subAssets={all.Length}");
            foreach (var group in all.GroupBy(a => a.GetType().Name))
                log.AppendLine($"    {group.Key} x{group.Count()}");
            return false;
        }

        var clips = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<AnimationClip>().ToList();
        var textures = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Texture2D>().ToList();
        var instance = UnityEngine.Object.Instantiate(main);
        try
        {
            var skinned = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var meshRenderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            var boneCounts = string.Join(",", skinned.Select(s => s.bones.Length));
            log.AppendLine($"[{name}] skinnedRenderers={skinned.Length} (bones: {boneCounts}) meshRenderers={meshRenderers.Length} clips={clips.Count} textures={textures.Count}");

            if (skinned.Length == 0)
            {
                log.AppendLine($"[{name}] FAIL: no skinned meshes");
                return false;
            }

            if (name.Contains("Melander", StringComparison.OrdinalIgnoreCase) && clips.Count == 0)
            {
                log.AppendLine($"[{name}] FAIL: animated Melander GLB imported without AnimationClip sub-assets");
                return false;
            }

            if (name.Contains("Melander", StringComparison.OrdinalIgnoreCase) && textures.Count == 0)
            {
                log.AppendLine($"[{name}] FAIL: textured Melander GLB imported without Texture2D sub-assets");
                return false;
            }

            var ok = true;
            var restBounds = CombinedBounds(instance);
            log.AppendLine($"[{name}] rest bounds center={Fmt(restBounds.center)} size={Fmt(restBounds.size)}");
            ok &= CheckBounds(name, "rest", restBounds, log);
            Screenshot(instance, restBounds, Path.Combine(ReportFolder, $"{name}_rest.png"));

            var clip = clips.FirstOrDefault(c => c.length > 1f) ?? clips.FirstOrDefault();
            if (clip != null)
            {
                foreach (var t in new[] { 0f, clip.length * 0.5f })
                {
                    clip.SampleAnimation(instance, t);
                    var b = CombinedBounds(instance);
                    log.AppendLine($"[{name}] '{clip.name}' t={t:0.##}s bounds center={Fmt(b.center)} size={Fmt(b.size)}");
                    ok &= CheckBounds(name, $"t={t:0.##}", b, log);
                    Screenshot(instance, b, Path.Combine(ReportFolder, $"{name}_t{t:000}.png"));
                }
            }

            return ok;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    static bool CheckBounds(string name, string tag, Bounds b, StringBuilder log)
    {
        // An AC is ~11 units tall; anything beyond 100 means skinning blew up, anything under
        // 0.05 means nothing rendered.
        var s = b.size.magnitude;
        if (s > 100f || s < 0.05f || float.IsNaN(s))
        {
            log.AppendLine($"[{name}] FAIL: {tag} bounds size {s} out of range");
            return false;
        }
        return true;
    }

    static Bounds CombinedBounds(GameObject root)
    {
        var bounds = new Bounds();
        var first = true;
        foreach (var r in root.GetComponentsInChildren<Renderer>(true))
        {
            var rendererBounds = r.bounds;
            if (r is SkinnedMeshRenderer smr)
            {
                smr.updateWhenOffscreen = true;
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked);
                    rendererBounds = TransformBounds(smr.transform.localToWorldMatrix, baked.bounds);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(baked);
                }
            }

            if (first) { bounds = rendererBounds; first = false; }
            else bounds.Encapsulate(rendererBounds);
        }
        return bounds;
    }

    static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
    {
        var center = matrix.MultiplyPoint3x4(localBounds.center);
        var extents = localBounds.extents;
        var axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
        var axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
        var axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
        extents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
        return new Bounds(center, extents * 2f);
    }

    static void Screenshot(GameObject subject, Bounds bounds, string filePath)
    {
        var camGo = new GameObject("SmokeTestCamera");
        var lightGo = new GameObject("SmokeTestLight");
        try
        {
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.6f;
            lightGo.transform.rotation = Quaternion.Euler(50, 30, 0);

            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.16f, 0.18f, 0.22f);
            var dist = Mathf.Max(bounds.size.magnitude, 0.5f) * 1.15f;
            cam.transform.position = bounds.center + new Vector3(0.6f, 0.25f, -1f).normalized * dist;
            cam.transform.LookAt(bounds.center);
            cam.nearClipPlane = dist * 0.01f;
            cam.farClipPlane = dist * 10f;

            var rt = new RenderTexture(900, 1100, 24);
            cam.targetTexture = rt;
            cam.Render();

            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            cam.targetTexture = null;
            rt.Release();

            File.WriteAllBytes(filePath, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            UnityEngine.Object.DestroyImmediate(rt);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(camGo);
            UnityEngine.Object.DestroyImmediate(lightGo);
        }
    }

    static string Fmt(Vector3 v) => $"({v.x:0.##},{v.y:0.##},{v.z:0.##})";
}
