using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class AC6BlendBakedPartsImporter
{
    private const string ExportRoot = "Assets/_Project/ImportedFromBlend/MELANDER_1010";
    private const string ManifestPath = ExportRoot + "/AC6_BlendBaked_MELANDER_1010_manifest.json";
    private const string DataRoot = "Assets/_Project/Data/Mechs/AC6BlendBakedSamples";
    private const string MaterialRoot = ExportRoot + "/UnityMaterials";
    private const string LoadoutPath = DataRoot + "/AC6_Blend_Baked_Melander_Loadout.asset";

    [MenuItem("Tools/ACunicombat/Mech/Import AC6 Blend Baked Sample Parts")]
    public static void ImportSampleParts()
    {
        if (!File.Exists(ProjectAbsolutePath(ManifestPath)))
        {
            EditorUtility.DisplayDialog(
                "AC6 Blend Baked Import",
                $"Manifest was not found:\n{ManifestPath}\n\nRun the Blender export first.",
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
        var createdParts = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);

        var specs = BuildSpecs();
        var missing = new List<string>();
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
            createdParts[spec.id] = CreateOrUpdatePart(spec, model, materials);
        }

        CreateInternalParts(createdParts);
        CreateLoadout(createdParts);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(LoadoutPath);
        if (selectedAssembler != null && loadout != null)
        {
            Undo.RegisterFullObjectHierarchyUndo(selectedAssembler.gameObject, "Apply AC6 blend baked loadout");
            selectedAssembler.Loadout = loadout;
            selectedAssembler.Assemble();
            EditorUtility.SetDirty(selectedAssembler);
            Selection.activeGameObject = selectedAssembler.gameObject;
        }
        else
        {
            Selection.activeObject = loadout;
            EditorGUIUtility.PingObject(loadout);
        }

        var message = $"Imported {createdParts.Count} blend-baked part definitions.\nLoadout: {LoadoutPath}";
        if (missing.Count > 0)
        {
            message += "\n\nMissing source files:\n" + string.Join("\n", missing);
        }

        Debug.Log(message);
        EditorUtility.DisplayDialog("AC6 Blend Baked Import", message, "OK");
    }

    private static List<ImportSpec> BuildSpecs()
    {
        return new List<ImportSpec>
        {
            Frame(
                "AC6_BLEND_HD_M_1010",
                "AC6 Blend Melander Head",
                MechPartSlot.Head,
                "FBX/AC6_BLEND_HD_M_1010.fbx",
                new MechPartStats
                {
                    massKg = 1700f,
                    armorPoints = 260f,
                    structurePoints = 180f,
                    idlePowerDrawMw = 0.7f,
                    peakPowerDrawMw = 1.1f,
                    radarCrossSection = 0.4f,
                    thermalSignature = 0.2f
                },
                "head", "frame", "ac6-blend-baked"),
            Frame(
                "AC6_BLEND_BD_M_1010",
                "AC6 Blend Melander Core",
                MechPartSlot.Core,
                "FBX/AC6_BLEND_BD_M_1010.fbx",
                new MechPartStats
                {
                    massKg = 12000f,
                    armorPoints = 1750f,
                    structurePoints = 1450f,
                    stability = 28f,
                    idlePowerDrawMw = 2f,
                    peakPowerDrawMw = 4f,
                    heatDissipationMw = 18f,
                    heatCapacityMj = 600f,
                    radarCrossSection = 2.1f,
                    thermalSignature = 1.1f,
                    hardpointCapacityKg = 16000f
                },
                "core", "frame", "ac6-blend-baked"),
            Frame(
                "AC6_BLEND_AM_M_1010",
                "AC6 Blend Melander Arms",
                MechPartSlot.Arms,
                "FBX/AC6_BLEND_AM_M_1010.fbx",
                new MechPartStats
                {
                    massKg = 6900f,
                    armorPoints = 850f,
                    structurePoints = 680f,
                    idlePowerDrawMw = 1f,
                    peakPowerDrawMw = 2f,
                    weaponRecoilControl = 58f,
                    meleeControl = 35f,
                    radarCrossSection = 1.1f,
                    thermalSignature = 0.5f
                },
                "arms", "frame", "ac6-blend-baked"),
            Frame(
                "AC6_BLEND_LG_M_1010",
                "AC6 Blend Melander Legs",
                MechPartSlot.Legs,
                "FBX/AC6_BLEND_LG_M_1010.fbx",
                new MechPartStats
                {
                    massKg = 9800f,
                    armorPoints = 1250f,
                    structurePoints = 1050f,
                    stability = 44f,
                    idlePowerDrawMw = 0.8f,
                    peakPowerDrawMw = 1.6f,
                    supportedMassKg = 65000f,
                    radarCrossSection = 1.8f,
                    thermalSignature = 0.6f
                },
                "legs", "frame", "ac6-blend-baked")
        };
    }

    private static ImportSpec Frame(
        string id,
        string displayName,
        MechPartSlot slot,
        string sourceRelativePath,
        MechPartStats stats,
        params string[] tags)
    {
        return new ImportSpec
        {
            id = id,
            displayName = displayName,
            slot = slot,
            category = MechPartCategory.Frame,
            sourceRelativePath = sourceRelativePath,
            stats = stats,
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

    private static Material[] CreateBakedMaterials(ImportSpec spec, BlendBakeSample sample)
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

            material.name = materialName;
            material.shader = FindLitShader();
            SetTexture(material, "_BaseMap", texture);
            SetTexture(material, "_MainTex", texture);
            SetColor(material, "_BaseColor", Color.white);
            SetColor(material, "_Color", Color.white);
            SetFloat(material, "_Surface", 0f);
            SetFloat(material, "_Metallic", LooksMetallic(bakedObject.material) ? 0.45f : 0.08f);
            SetFloat(material, "_Smoothness", LooksMetallic(bakedObject.material) ? 0.38f : 0.25f);

            if (LooksEmissive(bakedObject.material))
            {
                SetTexture(material, "_EmissionMap", texture);
                SetColor(material, "_EmissionColor", new Color(0.7f, 0.95f, 1.2f, 1f));
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

    private static bool LooksMetallic(string materialName)
    {
        return !string.IsNullOrWhiteSpace(materialName) &&
            materialName.IndexOf("Prats_Marking", StringComparison.OrdinalIgnoreCase) < 0 &&
            materialName.IndexOf("Making", StringComparison.OrdinalIgnoreCase) < 0 &&
            materialName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static bool LooksEmissive(string materialName)
    {
        return !string.IsNullOrWhiteSpace(materialName) &&
            (materialName.IndexOf("Emission", StringComparison.OrdinalIgnoreCase) >= 0 ||
             materialName.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0);
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
            BuildMounts(spec),
            spec.tags,
            materials);
        return part;
    }

    private static IEnumerable<MechMountPointDefinition> BuildMounts(ImportSpec spec)
    {
        if (spec.slot == MechPartSlot.Core)
        {
            return new[]
            {
                Mount("LeftBackMount", MechPartSlot.LeftBackWeapon, MechHardpointType.BackWeapon, new Vector3(-0.65f, 1.65f, -0.75f), 7000f),
                Mount("RightBackMount", MechPartSlot.RightBackWeapon, MechHardpointType.BackWeapon, new Vector3(0.65f, 1.65f, -0.75f), 7000f)
            };
        }

        if (spec.slot == MechPartSlot.Arms)
        {
            return new[]
            {
                Mount("LeftHandMount", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, new Vector3(-1.35f, 0.95f, 0.2f), 4500f),
                Mount("RightHandMount", MechPartSlot.RightHandWeapon, MechHardpointType.HandWeapon, new Vector3(1.35f, 0.95f, 0.2f), 4500f)
            };
        }

        return null;
    }

    private static MechMountPointDefinition Mount(
        string id,
        MechPartSlot targetSlot,
        MechHardpointType hardpointType,
        Vector3 localPosition,
        float maxMassKg)
    {
        return new MechMountPointDefinition
        {
            mountId = id,
            targetSlot = targetSlot,
            hardpointType = hardpointType,
            localPosition = localPosition,
            localEulerAngles = Vector3.zero,
            localScale = Vector3.one,
            maxMassKg = maxMassKg
        };
    }

    private static void CreateInternalParts(Dictionary<string, MechPartDefinition> parts)
    {
        parts["AC6_BLEND_BST_Test_Vector"] = CreateInternal(
            "AC6_BLEND_BST_Test_Vector",
            "AC6 Blend Test Vector Booster",
            MechPartSlot.Booster,
            new MechPartStats
            {
                massKg = 3900f,
                idlePowerDrawMw = 2.4f,
                peakPowerDrawMw = 22f,
                heatGenerationMw = 6.5f,
                mainThrustKn = 500f,
                lateralThrustKn = 210f,
                reverseThrustKn = 285f,
                attitudeTorqueKnM = 175f,
                thermalSignature = 2.5f,
                electromagneticSignature = 0.8f
            },
            "internal", "booster", "ac6-blend-baked");

        parts["AC6_BLEND_GEN_Test_Reactor"] = CreateInternal(
            "AC6_BLEND_GEN_Test_Reactor",
            "AC6 Blend Test Compact Reactor",
            MechPartSlot.Generator,
            new MechPartStats
            {
                massKg = 3800f,
                powerOutputMw = 68f,
                heatGenerationMw = 4.5f,
                heatCapacityMj = 320f,
                thermalSignature = 1.4f,
                electromagneticSignature = 2.5f
            },
            "internal", "generator", "ac6-blend-baked");

        parts["AC6_BLEND_FCS_Test_Computer"] = CreateInternal(
            "AC6_BLEND_FCS_Test_Computer",
            "AC6 Blend Test Track Computer",
            MechPartSlot.FireControlSystem,
            new MechPartStats
            {
                massKg = 900f,
                idlePowerDrawMw = 1.4f,
                peakPowerDrawMw = 3f,
                fireControlQuality = 48f,
                trackCapacity = 8,
                guidanceChannels = 3,
                electromagneticSignature = 0.4f
            },
            "internal", "fcs", "ac6-blend-baked");

        parts["AC6_BLEND_RDR_Test_Array"] = CreateInternal(
            "AC6_BLEND_RDR_Test_Array",
            "AC6 Blend Test Active Radar",
            MechPartSlot.Radar,
            new MechPartStats
            {
                massKg = 1200f,
                idlePowerDrawMw = 2.8f,
                peakPowerDrawMw = 6.5f,
                heatGenerationMw = 1.2f,
                radarRangeKm = 42f,
                radarAperture = 1.3f,
                radarRefreshHz = 2.5f,
                sensorPrecision = 38f,
                electromagneticSignature = 3.5f
            },
            "internal", "radar", "active-sensor", "ac6-blend-baked");
    }

    private static MechPartDefinition CreateInternal(
        string id,
        string displayName,
        MechPartSlot slot,
        MechPartStats stats,
        params string[] tags)
    {
        var part = CreateOrLoadAsset<MechPartDefinition>(DataRoot + "/" + id + ".asset");
        part.EditorConfigure(id, displayName, slot, MechPartCategory.Internal, null, stats, null, tags);
        return part;
    }

    private static void CreateLoadout(Dictionary<string, MechPartDefinition> parts)
    {
        var loadout = CreateOrLoadAsset<MechLoadoutDefinition>(LoadoutPath);
        loadout.EditorConfigure(
            "ac6_blend_baked_melander",
            "AC6 Blend Baked Melander",
            new[]
            {
                Assignment(MechPartSlot.Head, parts, "AC6_BLEND_HD_M_1010"),
                Assignment(MechPartSlot.Core, parts, "AC6_BLEND_BD_M_1010"),
                Assignment(MechPartSlot.Arms, parts, "AC6_BLEND_AM_M_1010"),
                Assignment(MechPartSlot.Legs, parts, "AC6_BLEND_LG_M_1010"),
                Assignment(MechPartSlot.Booster, parts, "AC6_BLEND_BST_Test_Vector"),
                Assignment(MechPartSlot.Generator, parts, "AC6_BLEND_GEN_Test_Reactor"),
                Assignment(MechPartSlot.FireControlSystem, parts, "AC6_BLEND_FCS_Test_Computer"),
                Assignment(MechPartSlot.Radar, parts, "AC6_BLEND_RDR_Test_Array")
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

    private static BlendBakeManifest LoadManifest()
    {
        var json = File.ReadAllText(ProjectAbsolutePath(ManifestPath));
        return JsonUtility.FromJson<BlendBakeManifest>(json);
    }

    private static BlendBakeSample FindSample(BlendBakeManifest manifest, string partId)
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
    private sealed class BlendBakeManifest
    {
        public BlendBakeSample[] samples;
    }

    [Serializable]
    private sealed class BlendBakeSample
    {
        public string part_id;
        public BlendBakeObject[] objects;
    }

    [Serializable]
    private sealed class BlendBakeObject
    {
        public string material;
        public string texture;
    }
}
