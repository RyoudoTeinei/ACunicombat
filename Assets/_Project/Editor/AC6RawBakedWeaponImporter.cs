using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class AC6RawBakedWeaponImporter
{
    private const string ExportRoot = "Assets/_Project/ImportedFromExtracted/Weapons";
    private const string ManifestPath = ExportRoot + "/AC6_ExtractedWeaponBakes_manifest.json";
    private const string DataRoot = "Assets/_Project/Data/Mechs/AC6RawBakedWeapons";
    private const string MaterialRoot = ExportRoot + "/UnityMaterials";
    private const string LoadoutPath = DataRoot + "/AC6_Raw_Baked_Weapon_Test_Loadout.asset";
    private const string BlendFrameDataRoot = "Assets/_Project/Data/Mechs/AC6BlendBakedSamples";

    [MenuItem("Tools/ACunicombat/Mech/Import AC6 Raw Baked Weapon Parts")]
    public static void ImportWeaponParts()
    {
        if (!File.Exists(ProjectAbsolutePath(ManifestPath)))
        {
            EditorUtility.DisplayDialog(
                "AC6 Raw Baked Weapon Import",
                $"Manifest was not found:\n{ManifestPath}\n\nRun the Blender weapon bake export first.",
                "OK");
            return;
        }

        EnsureFolder(DataRoot);
        EnsureFolder(MaterialRoot);
        AssetDatabase.Refresh();

        var selectedAssembler = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<MechAssembler>()
            : null;
        var manifest = LoadManifest();
        var parts = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var specs = BuildSpecs();

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var model = ImportModel(spec, missing);
            if (model == null)
            {
                continue;
            }

            var sample = FindSample(manifest, spec.id);
            var materials = CreateBakedMaterials(spec, sample);
            parts[spec.id] = CreateOrUpdatePart(spec, model, materials);
        }

        var loadout = CreateWeaponTestLoadout(parts);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        if (selectedAssembler != null && loadout != null)
        {
            Undo.RegisterFullObjectHierarchyUndo(selectedAssembler.gameObject, "Apply AC6 raw baked weapon test loadout");
            selectedAssembler.Loadout = loadout;
            selectedAssembler.Assemble();
            EditorUtility.SetDirty(selectedAssembler);
            Selection.activeGameObject = selectedAssembler.gameObject;
        }
        else if (loadout != null)
        {
            Selection.activeObject = loadout;
            EditorGUIUtility.PingObject(loadout);
        }

        var message = $"Imported {parts.Count} raw-baked weapon part definitions.\nLoadout: {LoadoutPath}";
        if (missing.Count > 0)
        {
            message += "\n\nMissing source files:\n" + string.Join("\n", missing);
        }

        Debug.Log(message);
        EditorUtility.DisplayDialog("AC6 Raw Baked Weapon Import", message, "OK");
    }

    private static List<ImportSpec> BuildSpecs()
    {
        return new List<ImportSpec>
        {
            Weapon(
                "AC6_RAW_WP_0010_Left_Rifle",
                "AC6 Raw Baked WP-0010 Rifle",
                MechPartSlot.LeftHandWeapon,
                "FBX/AC6_RAW_WP_0010_Left_Rifle.fbx",
                1650f,
                "kinetic", "rifle", "hand-weapon", "ac6-raw-baked"),
            Weapon(
                "AC6_RAW_WP_0230_Left_Rifle",
                "AC6 Raw Baked WP-0230 Rifle",
                MechPartSlot.LeftHandWeapon,
                "FBX/AC6_RAW_WP_0230_Left_Rifle.fbx",
                2100f,
                "kinetic", "rifle", "hand-weapon", "ac6-raw-baked"),
            Weapon(
                "AC6_RAW_WP_0250_Left_Heavy_Rifle",
                "AC6 Raw Baked WP-0250 Heavy Rifle",
                MechPartSlot.LeftHandWeapon,
                "FBX/AC6_RAW_WP_0250_Left_Heavy_Rifle.fbx",
                2400f,
                "kinetic", "heavy-rifle", "hand-weapon", "ac6-raw-baked"),
            Weapon(
                "AC6_RAW_WP_1310_Right_Back",
                "AC6 Raw Baked WP-1310 Right Back Weapon",
                MechPartSlot.RightBackWeapon,
                "FBX/AC6_RAW_WP_1310_Right_Back.fbx",
                3800f,
                "back-weapon", "missile", "ac6-raw-baked"),
            Weapon(
                "AC6_RAW_WP_1410_Left_Back",
                "AC6 Raw Baked WP-1410 Left Back Weapon",
                MechPartSlot.LeftBackWeapon,
                "FBX/AC6_RAW_WP_1410_Left_Back.fbx",
                4200f,
                "back-weapon", "launcher", "ac6-raw-baked"),
            Weapon(
                "AC6_RAW_WP_1410_Right_Back",
                "AC6 Raw Baked WP-1410 Right Back Weapon",
                MechPartSlot.RightBackWeapon,
                "FBX/AC6_RAW_WP_1410_Right_Back.fbx",
                4200f,
                "back-weapon", "launcher", "ac6-raw-baked")
        };
    }

    private static ImportSpec Weapon(
        string id,
        string displayName,
        MechPartSlot slot,
        string sourceRelativePath,
        float massKg,
        params string[] tags)
    {
        return new ImportSpec
        {
            id = id,
            displayName = displayName,
            slot = slot,
            category = MechPartCategory.Weapon,
            sourceRelativePath = sourceRelativePath,
            stats = new MechPartStats
            {
                massKg = massKg,
                idlePowerDrawMw = 0.2f,
                peakPowerDrawMw = 2.5f,
                heatGenerationMw = 0.8f,
                radarCrossSection = 0.55f,
                thermalSignature = 0.35f
            },
            tags = tags
        };
    }

    private static GameObject ImportModel(ImportSpec spec, List<string> missing)
    {
        var modelPath = ExportRoot + "/" + spec.sourceRelativePath;
        if (!File.Exists(ProjectAbsolutePath(modelPath)))
        {
            missing.Add(modelPath);
            return null;
        }

        AssetDatabase.ImportAsset(modelPath, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
        if (importer != null)
        {
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
    }

    private static Material[] CreateBakedMaterials(ImportSpec spec, WeaponBakeSample sample)
    {
        if (sample == null || sample.objects == null)
        {
            return Array.Empty<Material>();
        }

        var materials = new List<Material>();
        EnsureFolder(MaterialRoot + "/" + spec.id);

        for (var i = 0; i < sample.objects.Length; i++)
        {
            var bakedObject = sample.objects[i];
            var texturePath = AbsolutePathToAssetPath(bakedObject.texture);
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                continue;
            }

            ConfigureTexture(texturePath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                continue;
            }

            var materialName = Path.GetFileNameWithoutExtension(texturePath).Replace("_Color", "_Mat");
            var materialPath = MaterialRoot + "/" + spec.id + "/" + materialName + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(FindLitShader());
                AssetDatabase.CreateAsset(material, materialPath);
            }

            var emissive = LooksEmissive(bakedObject.material);
            material.name = materialName;
            material.shader = FindLitShader();
            SetTexture(material, "_BaseMap", texture);
            SetTexture(material, "_MainTex", texture);
            SetColor(material, "_BaseColor", Color.white);
            SetColor(material, "_Color", Color.white);
            SetFloat(material, "_Surface", 0f);
            SetFloat(material, "_Metallic", emissive ? 0f : 0.38f);
            SetFloat(material, "_Smoothness", emissive ? 0.25f : 0.40f);

            if (emissive)
            {
                SetTexture(material, "_EmissionMap", texture);
                SetColor(material, "_EmissionColor", new Color(1.0f, 0.15f, 1.2f, 1f));
                material.EnableKeyword("_EMISSION");
            }
            else
            {
                SetColor(material, "_EmissionColor", Color.black);
                material.DisableKeyword("_EMISSION");
            }

            EditorUtility.SetDirty(material);
            materials.Add(material);
        }

        return materials.ToArray();
    }

    private static void ConfigureTexture(string texturePath)
    {
        var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        importer.textureType = TextureImporterType.Default;
        importer.sRGBTexture = true;
        importer.mipmapEnabled = true;
        importer.SaveAndReimport();
    }

    private static bool LooksEmissive(string materialName)
    {
        return !string.IsNullOrWhiteSpace(materialName) &&
            (materialName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0 ||
             materialName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0 ||
             materialName.IndexOf("Eyecolor", StringComparison.OrdinalIgnoreCase) >= 0 ||
             materialName.IndexOf("Eye", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static MechPartDefinition CreateOrUpdatePart(ImportSpec spec, GameObject model, Material[] materials)
    {
        var part = CreateOrLoadAsset<MechPartDefinition>(DataRoot + "/" + spec.id + ".asset");
        part.EditorConfigure(
            spec.id,
            spec.displayName,
            spec.slot,
            spec.category,
            model,
            spec.stats,
            null,
            spec.tags,
            materials);
        return part;
    }

    private static MechLoadoutDefinition CreateWeaponTestLoadout(Dictionary<string, MechPartDefinition> weaponParts)
    {
        var assignments = new List<MechPartAssignment>();
        AddFrameAssignment(assignments, MechPartSlot.Head, "AC6_BLEND_HD_M_1010");
        AddFrameAssignment(assignments, MechPartSlot.Core, "AC6_BLEND_BD_M_1010");
        AddFrameAssignment(assignments, MechPartSlot.Arms, "AC6_BLEND_AM_M_1010");
        AddFrameAssignment(assignments, MechPartSlot.Legs, "AC6_BLEND_LG_M_1010");
        AddFrameAssignment(assignments, MechPartSlot.Booster, "AC6_BLEND_BST_Test_Vector");
        AddFrameAssignment(assignments, MechPartSlot.Generator, "AC6_BLEND_GEN_Test_Reactor");
        AddFrameAssignment(assignments, MechPartSlot.FireControlSystem, "AC6_BLEND_FCS_Test_Computer");
        AddFrameAssignment(assignments, MechPartSlot.Radar, "AC6_BLEND_RDR_Test_Array");

        assignments.Add(Assignment(MechPartSlot.LeftHandWeapon, weaponParts, "AC6_RAW_WP_0010_Left_Rifle"));
        assignments.Add(Assignment(MechPartSlot.LeftBackWeapon, weaponParts, "AC6_RAW_WP_1410_Left_Back"));
        assignments.Add(Assignment(MechPartSlot.RightBackWeapon, weaponParts, "AC6_RAW_WP_1310_Right_Back"));

        var loadout = CreateOrLoadAsset<MechLoadoutDefinition>(LoadoutPath);
        loadout.EditorConfigure("ac6_raw_baked_weapon_test", "AC6 Raw Baked Weapon Test", assignments);
        return loadout;
    }

    private static void AddFrameAssignment(List<MechPartAssignment> assignments, MechPartSlot slot, string partId)
    {
        var part = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(BlendFrameDataRoot + "/" + partId + ".asset");
        if (part == null)
        {
            return;
        }

        assignments.Add(new MechPartAssignment
        {
            slot = slot,
            part = part,
            enabled = true
        });
    }

    private static MechPartAssignment Assignment(
        MechPartSlot slot,
        Dictionary<string, MechPartDefinition> parts,
        string partId)
    {
        parts.TryGetValue(partId, out var part);
        return new MechPartAssignment
        {
            slot = slot,
            part = part,
            enabled = part != null
        };
    }

    private static WeaponBakeManifest LoadManifest()
    {
        var json = File.ReadAllText(ProjectAbsolutePath(ManifestPath));
        return JsonUtility.FromJson<WeaponBakeManifest>(json);
    }

    private static WeaponBakeSample FindSample(WeaponBakeManifest manifest, string partId)
    {
        if (manifest == null || manifest.samples == null)
        {
            return null;
        }

        for (var i = 0; i < manifest.samples.Length; i++)
        {
            if (string.Equals(manifest.samples[i].part_id, partId, StringComparison.OrdinalIgnoreCase))
            {
                return manifest.samples[i];
            }
        }

        return null;
    }

    private static T CreateOrLoadAsset<T>(string path) where T : ScriptableObject
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null)
        {
            return asset;
        }

        asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    private static Shader FindLitShader()
    {
        return Shader.Find("Universal Render Pipeline/Lit")
            ?? Shader.Find("Standard")
            ?? Shader.Find("Diffuse");
    }

    private static void SetTexture(Material material, string propertyName, Texture texture)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetTexture(propertyName, texture);
        }
    }

    private static void SetColor(Material material, string propertyName, Color color)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetColor(propertyName, color);
        }
    }

    private static void SetFloat(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    private static string AbsolutePathToAssetPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(path).Replace('\\', '/');
        var projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
        if (!fullPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return fullPath.Substring(projectRoot.Length + 1);
    }

    private static string ProjectAbsolutePath(string assetPath)
    {
        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
        return Path.Combine(projectRoot, assetPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
    }

    private static void EnsureFolder(string folderPath)
    {
        var parts = folderPath.Split('/');
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

    private sealed class ImportSpec
    {
        public string id;
        public string displayName;
        public MechPartSlot slot;
        public MechPartCategory category;
        public string sourceRelativePath;
        public MechPartStats stats;
        public string[] tags;
    }

    [Serializable]
    private sealed class WeaponBakeManifest
    {
        public WeaponBakeSample[] samples;
    }

    [Serializable]
    private sealed class WeaponBakeSample
    {
        public string part_id;
        public WeaponBakeObject[] objects;
    }

    [Serializable]
    private sealed class WeaponBakeObject
    {
        public string material;
        public string texture;
    }
}
