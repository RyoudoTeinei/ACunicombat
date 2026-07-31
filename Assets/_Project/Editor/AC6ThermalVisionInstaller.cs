using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 热成像视野一键安装：创建两张材质，并把两个 URP 内置 feature 挂到当前激活
/// 管线的 Renderer 资产上（初始 inactive，由 MechThermalVisionController 在
/// `~ 键切换时开关）：
///   - "AC6ThermalObjects"：RenderObjects，AfterRenderingSkybox 用热辐射材质
///     重画全部不透明物体（逐 renderer 温度走 MPB）；
///   - "AC6ThermalScreen"：FullScreenPassRendererFeature，后处理前做冷背景/
///     噪声/像素化/极性（requirements=Depth 以区分太空背景）。
/// 不写自定义 RenderGraph pass——两个 feature 都是 URP 自带实现。重复执行幂等。
/// </summary>
public static class AC6ThermalVisionInstaller
{
    private const string MaterialFolder = "Assets/_Project/Rendering/Thermal";
    private const string RadianceMaterialPath = MaterialFolder + "/AC6ThermalRadiance.mat";
    private const string ScreenMaterialPath = MaterialFolder + "/AC6ThermalScreen.mat";
    private const string ObjectsFeatureName = "AC6ThermalObjects";
    private const string ScreenFeatureName = "AC6ThermalScreen";

    [MenuItem("Tools/ACunicombat/AC6/Install Thermal Vision")]
    public static void Install()
    {
        var report = InstallInternal();
        Debug.Log($"[AC6ThermalVision] {report}");
        EditorUtility.DisplayDialog("AC6 Thermal Vision", report, "OK");
    }

    private static string InstallInternal()
    {
        var radianceShader = Shader.Find("ACunicombat/AC6/Thermal Radiance");
        var screenShader = Shader.Find("Hidden/ACunicombat/AC6/Thermal Screen");
        if (radianceShader == null || screenShader == null)
        {
            return "shader 未找到（等待编译完成后重试）: " +
                   $"radiance={(radianceShader != null)}, screen={(screenShader != null)}";
        }

        EnsureFolder();
        var radianceMat = EnsureMaterial(RadianceMaterialPath, radianceShader);
        var screenMat = EnsureMaterial(ScreenMaterialPath, screenShader);
        // 未标记物体的环境默认温度；须与 MechThermalVisionController.defaultSceneTemperatureK 一致。
        radianceMat.SetFloat("_ThermalTemp", 240f);
        EditorUtility.SetDirty(radianceMat);
        EditorUtility.SetDirty(screenMat);

        var rendererData = ResolveActiveRendererData(out var error);
        if (rendererData == null)
        {
            return error;
        }

        var addedObjects = EnsureRenderObjectsFeature(rendererData, radianceMat);
        var addedScreen = EnsureScreenFeature(rendererData, screenMat);

        EditorUtility.SetDirty(rendererData);
        AssetDatabase.SaveAssets();
        return $"完成：renderer={rendererData.name}, " +
               $"{ObjectsFeatureName}={(addedObjects ? "新增" : "已存在,已更新")}, " +
               $"{ScreenFeatureName}={(addedScreen ? "新增" : "已存在,已更新")}。" +
               "运行时用 `~ 键在 关闭↔白热 之间切换。";
    }

    private static void EnsureFolder()
    {
        if (!AssetDatabase.IsValidFolder("Assets/_Project/Rendering"))
        {
            AssetDatabase.CreateFolder("Assets/_Project", "Rendering");
        }

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
        {
            AssetDatabase.CreateFolder("Assets/_Project/Rendering", "Thermal");
        }
    }

    private static Material EnsureMaterial(string path, Shader shader)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
        }
        else if (mat.shader != shader)
        {
            mat.shader = shader;
        }

        return mat;
    }

    private static ScriptableRendererData ResolveActiveRendererData(out string error)
    {
        error = null;
        var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (pipeline == null)
        {
            pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
        }

        if (pipeline == null)
        {
            error = "未找到激活的 URP 管线资产。";
            return null;
        }

        var so = new SerializedObject(pipeline);
        var list = so.FindProperty("m_RendererDataList");
        var defaultIndex = so.FindProperty("m_DefaultRendererIndex");
        if (list == null || list.arraySize == 0)
        {
            error = $"管线资产 {pipeline.name} 没有 Renderer 列表。";
            return null;
        }

        var index = defaultIndex != null
            ? Mathf.Clamp(defaultIndex.intValue, 0, list.arraySize - 1)
            : 0;
        var data = list.GetArrayElementAtIndex(index).objectReferenceValue as ScriptableRendererData;
        if (data == null)
        {
            error = $"管线资产 {pipeline.name} 的默认 Renderer 为空。";
        }

        return data;
    }

    private static bool EnsureRenderObjectsFeature(ScriptableRendererData data, Material overrideMat)
    {
        var existing = FindFeature(data, ObjectsFeatureName);
        var created = existing == null;
        var feature = existing as RenderObjects
                      ?? ScriptableObject.CreateInstance<RenderObjects>();
        feature.name = ObjectsFeatureName;
        feature.settings.passTag = ObjectsFeatureName;
        // 天空盒之后重画：深度相同处 LEqual 通过，颜色被热辐射覆盖；
        // 之后的透明队列（喷焰特效）保持原样叠加——热羽流免费获得。
        feature.settings.Event = RenderPassEvent.AfterRenderingSkybox;
        feature.settings.filterSettings.RenderQueueType = RenderQueueType.Opaque;
        feature.settings.filterSettings.LayerMask = ~0;
        feature.settings.overrideMaterial = overrideMat;
        feature.settings.overrideMaterialPassIndex = 0;
        feature.SetActive(false);
        EditorUtility.SetDirty(feature);

        if (created)
        {
            AttachFeature(data, feature);
        }

        return created;
    }

    private static bool EnsureScreenFeature(ScriptableRendererData data, Material screenMat)
    {
        var existing = FindFeature(data, ScreenFeatureName);
        var created = existing == null;
        var feature = existing as FullScreenPassRendererFeature
                      ?? ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
        feature.name = ScreenFeatureName;
        feature.passMaterial = screenMat;
        feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingPostProcessing;
        feature.requirements = ScriptableRenderPassInput.Depth;
        feature.fetchColorBuffer = true;
        feature.SetActive(false);
        EditorUtility.SetDirty(feature);

        if (created)
        {
            AttachFeature(data, feature);
        }

        return created;
    }

    private static ScriptableRendererFeature FindFeature(ScriptableRendererData data, string name)
    {
        foreach (var feature in data.rendererFeatures)
        {
            if (feature != null && feature.name == name)
            {
                return feature;
            }
        }

        return null;
    }

    /// <summary>
    /// Renderer 资产的 feature 列表与 localId 映射都是序列化私有字段，
    /// 按 URP Editor 自身的添加流程走 SerializedObject。
    /// </summary>
    private static void AttachFeature(ScriptableRendererData data, ScriptableRendererFeature feature)
    {
        AssetDatabase.AddObjectToAsset(feature, data);
        AssetDatabase.SaveAssets();
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

        var so = new SerializedObject(data);
        var features = so.FindProperty("m_RendererFeatures");
        var featureMap = so.FindProperty("m_RendererFeatureMap");
        features.arraySize++;
        features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
        if (featureMap != null)
        {
            featureMap.arraySize++;
            featureMap.GetArrayElementAtIndex(featureMap.arraySize - 1).longValue = localId;
        }

        so.ApplyModifiedProperties();
    }
}
