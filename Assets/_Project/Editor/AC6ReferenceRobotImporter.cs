using System.IO;
using UnityEditor;
using UnityEngine;

public static class AC6ReferenceRobotImporter
{
    private readonly struct RobotImportConfig
    {
        public RobotImportConfig(
            string displayName,
            string instanceName,
            string root,
            string modelPath,
            string albedoPath,
            string normalPath,
            string emissionPath)
        {
            DisplayName = displayName;
            InstanceName = instanceName;
            Root = root;
            ModelPath = modelPath;
            AlbedoPath = albedoPath;
            NormalPath = normalPath;
            EmissionPath = emissionPath;
        }

        public string DisplayName { get; }
        public string InstanceName { get; }
        public string Root { get; }
        public string ModelPath { get; }
        public string AlbedoPath { get; }
        public string NormalPath { get; }
        public string EmissionPath { get; }
        public string MaterialDirectory => Root + "/Materials";
        public string MaterialPath => MaterialDirectory + "/" + InstanceName + "_Reference.mat";
    }

    private static readonly RobotImportConfig Allmind = new(
        "AC6 ALLMIND c4201",
        "AC6_ALLMIND_c4201",
        "Assets/_Imported/AC6_Reference/ALLMIND_c4201",
        "Assets/_Imported/AC6_Reference/ALLMIND_c4201/Models/c4201.fbx",
        "Assets/_Imported/AC6_Reference/ALLMIND_c4201/Textures/c4201_01_a.dds",
        "Assets/_Imported/AC6_Reference/ALLMIND_c4201/Textures/c4201_01_n.dds",
        "Assets/_Imported/AC6_Reference/ALLMIND_c4201/Textures/c4201_01_em.dds");

    private static readonly RobotImportConfig LegacyAc4 = new(
        "Legacy AC4 c8110",
        "Legacy_AC4_c8110",
        "Assets/_Imported/AC6_Reference/AC4_AC_c8110",
        "Assets/_Imported/AC6_Reference/AC4_AC_c8110/Models/c8110.fbx",
        "Assets/_Imported/AC6_Reference/AC4_AC_c8110/Textures/e0050_a.dds",
        "Assets/_Imported/AC6_Reference/AC4_AC_c8110/Textures/e0050_n.dds",
        string.Empty);

    [MenuItem("Tools/ACunicombat/Create Test Robot")]
    public static void CreateTestRobot()
    {
        CreateRobot(Allmind);
    }

    [MenuItem("Tools/ACunicombat/Create Reference Robot/AC6 ALLMIND c4201")]
    public static void CreateAllmindRobot()
    {
        CreateRobot(Allmind);
    }

    [MenuItem("Tools/ACunicombat/Create Reference Robot/Legacy AC4 c8110")]
    public static void CreateLegacyAc4Robot()
    {
        CreateRobot(LegacyAc4);
    }

    private static void CreateRobot(RobotImportConfig config)
    {
        AssetDatabase.ImportAsset(config.ModelPath, ImportAssetOptions.ForceUpdate);
        ConfigureTexture(config.AlbedoPath, false, false);
        ConfigureTexture(config.NormalPath, true, false);
        ConfigureTexture(config.EmissionPath, false, true);

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(config.ModelPath);
        if (model == null)
        {
            EditorUtility.DisplayDialog(
                "ACunicombat",
                $"The {config.DisplayName} FBX was not found. Check this path:\n{config.ModelPath}",
                "OK");
            return;
        }

        var material = CreateReferenceMaterial(config);
        var instance = PrefabUtility.InstantiatePrefab(model) as GameObject;
        if (instance == null)
        {
            instance = Object.Instantiate(model);
        }

        Undo.RegisterCreatedObjectUndo(instance, "Create reference robot");
        instance.name = config.InstanceName + "_TestRobot";
        instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        instance.transform.localScale = Vector3.one;

        foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true))
        {
            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                materials[i] = material;
            }

            renderer.sharedMaterials = materials;
        }

        Selection.activeGameObject = instance;
        EditorGUIUtility.PingObject(instance);
    }

    private static void ConfigureTexture(string path, bool normalMap, bool linear)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            importer = AssetImporter.GetAtPath(path) as TextureImporter;
        }

        if (importer == null)
        {
            return;
        }

        importer.textureType = normalMap ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = !normalMap && !linear;
        importer.SaveAndReimport();
    }

    private static Material CreateReferenceMaterial(RobotImportConfig config)
    {
        Directory.CreateDirectory(config.MaterialDirectory);

        var material = AssetDatabase.LoadAssetAtPath<Material>(config.MaterialPath);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            material = new Material(shader)
            {
                name = config.InstanceName + "_Reference"
            };

            AssetDatabase.CreateAsset(material, config.MaterialPath);
        }

        var albedo = AssetDatabase.LoadAssetAtPath<Texture2D>(config.AlbedoPath);
        var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(config.NormalPath);
        var emission = string.IsNullOrWhiteSpace(config.EmissionPath)
            ? null
            : AssetDatabase.LoadAssetAtPath<Texture2D>(config.EmissionPath);

        SetTextureIfPresent(material, "_BaseMap", albedo);
        SetTextureIfPresent(material, "_MainTex", albedo);
        SetTextureIfPresent(material, "_BumpMap", normal);
        SetTextureIfPresent(material, "_EmissionMap", emission);

        SetColorIfPresent(material, "_BaseColor", Color.white);
        SetColorIfPresent(material, "_EmissionColor", emission == null ? Color.black : Color.white);
        SetFloatIfPresent(material, "_Metallic", 0.35f);
        SetFloatIfPresent(material, "_Smoothness", 0.45f);

        if (normal != null)
        {
            material.EnableKeyword("_NORMALMAP");
        }

        if (emission != null)
        {
            material.EnableKeyword("_EMISSION");
        }

        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        return material;
    }

    private static void SetTextureIfPresent(Material material, string property, Texture texture)
    {
        if (texture != null && material.HasProperty(property))
        {
            material.SetTexture(property, texture);
        }
    }

    private static void SetFloatIfPresent(Material material, string property, float value)
    {
        if (material.HasProperty(property))
        {
            material.SetFloat(property, value);
        }
    }

    private static void SetColorIfPresent(Material material, string property, Color value)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, value);
        }
    }
}
