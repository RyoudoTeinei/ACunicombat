using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 机体全黑问题自动诊断（交接日志"优先排查"清单的 1/2/5 条自动化）：
/// 打开可玩场景 → 相机对准机体渲染到 RT 采样亮度 → 转储每个 renderer 的
/// 材质/Shader/MPB/层配置与关键 albedo 属性 → 全部换成白色 URP/Lit 再渲染
/// 对比。批处理入口（需要 GPU，不要 -nographics）：
///   -executeMethod AC6BlackMechDiagnostic.RunBatch
/// 不保存场景，A/B 换材质只在内存中发生。
/// </summary>
public static class AC6BlackMechDiagnostic
{
    private const string ScenePath =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
    private const string MechRootName = "Player_Melander_1010";
    private const int RenderSize = 512;

    public static void RunBatch()
    {
        var report = Run();
        Debug.Log($"[AC6BlackMechDiag]\n{report}");
        if (Application.isBatchMode)
        {
            EditorApplication.Exit(0);
        }
    }

    [MenuItem("Tools/ACunicombat/AC6/Diagnose Black Mech")]
    public static void RunMenu()
    {
        Debug.Log($"[AC6BlackMechDiag]\n{Run()}");
    }

    private static string Run()
    {
        var log = new StringBuilder(8192);
        if (!System.IO.File.Exists(ScenePath))
        {
            return $"scene missing: {ScenePath}（先跑 Build Playable Player Scene）";
        }

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var mech = GameObject.Find(MechRootName);
        if (mech == null)
        {
            return "mech root not found";
        }

        // ---- 1. 逐 renderer 转储配置 ----
        var renderers = mech.GetComponentsInChildren<Renderer>(true);
        log.AppendLine($"renderers={renderers.Length}");
        foreach (var r in renderers)
        {
            if (r is not MeshRenderer && r is not SkinnedMeshRenderer) continue;
            var m = r.sharedMaterial;
            log.Append($"  {r.name}: enabled={r.enabled} layer={r.gameObject.layer} ")
               .Append($"renderingLayerMask={r.renderingLayerMask:X} hasMPB={r.HasPropertyBlock()} ")
               .AppendLine($"mats={r.sharedMaterials.Length} shader={(m != null ? m.shader.name : "NULL")}");
            if (m != null && m.shader.name.Contains("Parts Approx"))
            {
                log.Append($"    _BaseColor={SafeColor(m, "_BaseColor")} _ZoneMode={SafeFloat(m, "_ZoneMode")} ")
                   .Append($"_UseSourceAlbedo={SafeFloat(m, "_UseSourceAlbedo")} _ZoneMaskStrength={SafeFloat(m, "_ZoneMaskStrength")} ")
                   .AppendLine($"_BaseMap={(m.HasTexture("_BaseMap") && m.GetTexture("_BaseMap") != null ? "set" : "NULL")} " +
                               $"_Mask3Map={(m.HasTexture("_Mask3Map") && m.GetTexture("_Mask3Map") != null ? "set" : "NULL")}");
                log.AppendLine($"    zones: 1={SafeColor(m, "_Zone1Color")} 2={SafeColor(m, "_Zone2Color")} " +
                               $"4={SafeColor(m, "_Zone4Color")} 7={SafeColor(m, "_Zone7Color")}");
                log.AppendLine($"    keywords: [{string.Join(",", m.shaderKeywords)}] passes={m.passCount} " +
                               $"renderQueue={m.renderQueue}");
            }
        }

        // ---- 2. 灯光/环境现状 ----
        foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Include))
        {
            log.AppendLine($"  light {light.name}: type={light.type} intensity={light.intensity} " +
                           $"enabled={light.enabled && light.gameObject.activeInHierarchy} cullingMask={light.cullingMask:X} " +
                           $"renderingLayerMask={light.renderingLayerMask:X}");
        }
        log.AppendLine($"  ambient: mode={RenderSettings.ambientMode} sky={RenderSettings.ambientSkyColor} " +
                       $"intensity={RenderSettings.ambientIntensity}");
        var rpAsset = GraphicsSettings.currentRenderPipeline;
        log.AppendLine($"  pipeline={(rpAsset != null ? rpAsset.name : "NULL")}");

        // ---- 3. A/B 渲染：原材质 vs 白色 URP/Lit ----
        var luminanceOriginal = RenderMechLuminance(mech, renderers, log, "original");

        var urpLit = new Material(Shader.Find("Universal Render Pipeline/Lit")) { color = Color.white };
        var saved = new Material[renderers.Length][];
        for (var i = 0; i < renderers.Length; i++)
        {
            saved[i] = renderers[i].sharedMaterials;
            var replaced = new Material[saved[i].Length];
            for (var j = 0; j < replaced.Length; j++) replaced[j] = urpLit;
            renderers[i].sharedMaterials = replaced;
        }

        var luminanceLit = RenderMechLuminance(mech, renderers, log, "urplit");
        for (var i = 0; i < renderers.Length; i++)
        {
            renderers[i].sharedMaterials = saved[i];
        }
        Object.DestroyImmediate(urpLit);

        log.AppendLine($"DIAG_RESULT original={luminanceOriginal:F4} urplit={luminanceLit:F4} " +
                       $"verdict={(luminanceOriginal < 0.02f && luminanceLit > 0.08f ? "MATERIAL_OR_SHADER" : luminanceOriginal < 0.02f ? "LIGHTING_OR_RENDERER" : "MECH_RENDERS_LIT_HERE")}");
        return log.ToString();
    }

    /// <summary>相机从机体包围盒外斜上方看向机体渲染一帧，返回画面中心区域平均亮度。</summary>
    private static float RenderMechLuminance(GameObject mech, Renderer[] renderers, StringBuilder log, string label)
    {
        var bounds = new Bounds(mech.transform.position, Vector3.one);
        var hasBounds = false;
        foreach (var r in renderers)
        {
            if (r is not MeshRenderer && r is not SkinnedMeshRenderer) continue;
            if (!hasBounds) { bounds = r.bounds; hasBounds = true; }
            else bounds.Encapsulate(r.bounds);
        }

        var camGo = new GameObject("DiagCamera");
        try
        {
            var cam = camGo.AddComponent<Camera>();
            var distance = Mathf.Max(bounds.extents.magnitude * 2.5f, 4f);
            cam.transform.position = bounds.center + new Vector3(0.6f, 0.4f, -1f).normalized * distance;
            cam.transform.LookAt(bounds.center);
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = distance * 4f;

            var rt = RenderTexture.GetTemporary(RenderSize, RenderSize, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            cam.Render();

            var previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            cam.targetTexture = null;
            RenderTexture.ReleaseTemporary(rt);

            // 机体占画面中心：采样中心 40% 区域平均亮度（含少量背景，趋势足够判别）。
            var sum = 0f;
            var count = 0;
            var lo = (int)(RenderSize * 0.3f);
            var hi = (int)(RenderSize * 0.7f);
            for (var y = lo; y < hi; y++)
            {
                for (var x = lo; x < hi; x++)
                {
                    var p = tex.GetPixel(x, y);
                    sum += 0.299f * p.r + 0.587f * p.g + 0.114f * p.b;
                    count++;
                }
            }

            Object.DestroyImmediate(tex);
            var mean = count > 0 ? sum / count : 0f;
            log.AppendLine($"  render[{label}]: centerLuminance={mean:F4} bounds={bounds.size}");
            return mean;
        }
        finally
        {
            Object.DestroyImmediate(camGo);
        }
    }

    private static string SafeColor(Material m, string name) =>
        m.HasColor(name) ? m.GetColor(name).ToString() : "n/a";

    private static string SafeFloat(Material m, string name) =>
        m.HasFloat(name) ? m.GetFloat(name).ToString("F2") : "n/a";
}
