using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class AC6ModularPartsImporter
{
    private const string SourceRoot = "E:/UnityDev/Resources/AC6_ExtractedAssets";
    private const string ImportRoot = "Assets/_Imported/AC6_ModularParts";
    private const string DataRoot = "Assets/_Project/Data/Mechs/AC6ImportedSamples";
    private const string LoadoutPath = DataRoot + "/AC6_Modular_Test_Loadout.asset";
    private const string WeatheringMaskAssetPath = "Assets/_Project/Resources/AC6Materials/SYSTEX_camouflage01.png";

    [MenuItem("Tools/ACunicombat/Legacy/Mech Imports/Import AC6 Modular Sample Parts")]
    public static void ImportSampleParts()
    {
        if (!Directory.Exists(SourceRoot))
        {
            EditorUtility.DisplayDialog(
                "AC6 Modular Import",
                $"Source folder was not found:\n{SourceRoot}",
                "OK");
            return;
        }

        var selectedAssembler = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<MechAssembler>()
            : null;

        EnsureFolder(ImportRoot);
        EnsureFolder(DataRoot);

        var createdParts = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
        var specs = BuildSpecs();
        var missing = new List<string>();
        var materialCount = 0;

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (!TryImportModel(spec, out var model, out var sourcePath, out var missingPath))
            {
                missing.Add(missingPath);
                continue;
            }

            var materialOverrides = ImportTexturesAndCreateMaterials(spec, model, sourcePath);
            materialCount += materialOverrides.Length;

            var part = CreateOrUpdatePart(spec, model, materialOverrides);
            createdParts[spec.id] = part;
        }

        CreateInternalParts(createdParts);
        CreateLoadout(createdParts);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(LoadoutPath);
        if (selectedAssembler != null && loadout != null)
        {
            Undo.RegisterFullObjectHierarchyUndo(selectedAssembler.gameObject, "Apply AC6 modular sample loadout");
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

        var message = $"Imported {createdParts.Count} sample part definitions and {materialCount} material overrides.\nLoadout: {LoadoutPath}";
        if (missing.Count > 0)
        {
            message += "\n\nMissing source files:\n" + string.Join("\n", missing);
        }

        Debug.Log(message);
        EditorUtility.DisplayDialog("AC6 Modular Import", message, "OK");
    }

    private static List<ImportSpec> BuildSpecs()
    {
        var specs = new List<ImportSpec>();
        AddFrameSet(specs, "1010", "Melander", 1.00f);
        AddFrameSet(specs, "2010", "Nacht", 0.82f);
        AddFrameSet(specs, "3010", "Tian-Qiang", 1.22f);

        specs.Add(Weapon(
            "AC6_WP_0010_Left_Rifle",
            "AC6 WP-0010 Left Rifle",
            MechPartSlot.LeftHandWeapon,
            "wp/wp_0010/WP_L_0010.fbx",
            1650f,
            "kinetic", "rifle"));
        specs.Add(Weapon(
            "AC6_WP_0250_Left_Heavy_Rifle",
            "AC6 WP-0250 Left Heavy Rifle",
            MechPartSlot.LeftHandWeapon,
            "wp/wp_0250/WP_L_0250.fbx",
            2400f,
            "kinetic", "heavy-rifle"));
        specs.Add(Weapon(
            "AC6_WP_1310_Right_Back",
            "AC6 WP-1310 Right Back Weapon",
            MechPartSlot.RightBackWeapon,
            "wp/wp_1310/WP_R_1310.fbx",
            3800f,
            "back-weapon", "missile"));
        specs.Add(Weapon(
            "AC6_WP_1410_Left_Back",
            "AC6 WP-1410 Left Back Weapon",
            MechPartSlot.LeftBackWeapon,
            "wp/wp_1410/WP_L_1410.fbx",
            4200f,
            "back-weapon", "launcher"));

        return specs;
    }

    private static void AddFrameSet(List<ImportSpec> specs, string code, string label, float massScale)
    {
        specs.Add(Frame(
            $"AC6_HD_M_{code}",
            $"AC6 {label} Head",
            MechPartSlot.Head,
            $"parts/hd_m_{code}/HD_M_{code}.fbx",
            new MechPartStats
            {
                massKg = 1700f * massScale,
                armorPoints = 260f * massScale,
                structurePoints = 180f * massScale,
                idlePowerDrawMw = 0.7f,
                peakPowerDrawMw = 1.1f,
                radarCrossSection = 0.4f * massScale,
                thermalSignature = 0.2f * massScale
            },
            "head", "frame", "ac6-imported"));

        specs.Add(Frame(
            $"AC6_BD_M_{code}",
            $"AC6 {label} Core",
            MechPartSlot.Core,
            $"parts/bd_m_{code}/BD_M_{code}.fbx",
            new MechPartStats
            {
                massKg = 12000f * massScale,
                armorPoints = 1750f * massScale,
                structurePoints = 1450f * massScale,
                idlePowerDrawMw = 2f,
                peakPowerDrawMw = 4f,
                heatDissipationMw = 1.5f * massScale,
                heatCapacityMj = 600f * massScale,
                radarCrossSection = 2.1f * massScale,
                thermalSignature = 1.1f * massScale,
                hardpointCapacityKg = 16000f * massScale,
                rcsThrustKn = 360f * massScale
            },
            "core", "frame", "ac6-imported"));

        specs.Add(Frame(
            $"AC6_AM_M_{code}",
            $"AC6 {label} Arms",
            MechPartSlot.Arms,
            $"parts/am_m_{code}/AM_M_{code}.fbx",
            new MechPartStats
            {
                massKg = 6900f * massScale,
                armorPoints = 850f * massScale,
                structurePoints = 680f * massScale,
                idlePowerDrawMw = 0.55f * massScale,
                peakPowerDrawMw = 2.6f * massScale,
                idleHeatMw = 0.14f * massScale,
                activeHeatMw = 0.95f * massScale,
                heatGenerationMw = 0.95f * massScale,
                heatCapacityMj = 80f * massScale,
                weaponRecoilControl = 58f * massScale,
                radarCrossSection = 1.1f * massScale,
                thermalSignature = 0.5f * massScale
            },
            "arms", "frame", "ac6-imported"));

        specs.Add(Frame(
            $"AC6_LG_M_{code}",
            $"AC6 {label} Legs",
            MechPartSlot.Legs,
            $"parts/lg_m_{code}/LG_M_{code}.fbx",
            new MechPartStats
            {
                massKg = 9800f * massScale,
                armorPoints = 1250f * massScale,
                structurePoints = 1050f * massScale,
                idlePowerDrawMw = 0.8f,
                peakPowerDrawMw = 1.6f,
                idleHeatMw = 0.22f * massScale,
                activeHeatMw = 0.72f * massScale,
                heatGenerationMw = 0.72f * massScale,
                heatDissipationMw = 0.5f * massScale,
                heatCapacityMj = 160f * massScale,
                radarCrossSection = 1.8f * massScale,
                thermalSignature = 0.6f * massScale
            },
            "legs", "frame", "ac6-imported"));

        specs.Add(Frame(
            $"AC6_BS_A_{code}",
            $"AC6 {label} Booster",
            MechPartSlot.Booster,
            $"parts/bs_a_{code}/BS_A_{code}.fbx",
            new MechPartStats
            {
                massKg = 3900f * massScale,
                idlePowerDrawMw = 2.4f,
                peakPowerDrawMw = 22f * massScale,
                heatGenerationMw = 6.5f * massScale,
                mainThrustKn = 500f / Mathf.Max(0.7f, massScale),
                lateralThrustKn = 210f / Mathf.Max(0.7f, massScale) - 40f * massScale,
                reverseThrustKn = 285f / Mathf.Max(0.7f, massScale),
                attitudeTorqueKnM = 175f / Mathf.Max(0.7f, massScale),
                thermalSignature = 2.5f * massScale,
                electromagneticSignature = 0.8f
            },
            "booster", "internal", "ac6-imported"));
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
            category = slot == MechPartSlot.Booster ? MechPartCategory.Internal : MechPartCategory.Frame,
            sourceRelativePath = sourceRelativePath,
			stats = stats,
			tags = tags,
			legPerformance = slot == MechPartSlot.Legs
				? BuildLegPerformance(stats.massKg / 9800f)
				: null
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

    private static bool TryImportModel(ImportSpec spec, out GameObject model, out string sourcePath, out string missingPath)
    {
        model = null;
        sourcePath = Path.Combine(SourceRoot, spec.sourceRelativePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        missingPath = sourcePath;
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        EnsureFolder(ImportRoot + "/" + spec.id);
        var destinationAssetPath = ImportRoot + "/" + spec.id + "/" + Path.GetFileName(sourcePath);
        var destinationAbsolutePath = ProjectAbsolutePath(destinationAssetPath);

        if (!File.Exists(destinationAbsolutePath))
        {
            FileUtil.CopyFileOrDirectory(sourcePath, destinationAbsolutePath);
        }

        AssetDatabase.ImportAsset(destinationAssetPath, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(destinationAssetPath) as ModelImporter;
        if (importer != null)
        {
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.SaveAndReimport();
        }

        model = AssetDatabase.LoadAssetAtPath<GameObject>(destinationAssetPath);
        return model != null;
    }

    private static Material[] ImportTexturesAndCreateMaterials(ImportSpec spec, GameObject model, string sourceModelPath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceModelPath);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return Array.Empty<Material>();
        }

        var textureSet = ImportTextureSet(spec, sourceDirectory);
        if (!textureSet.HasAnyTexture)
        {
            return Array.Empty<Material>();
        }

        var slots = GetMaterialSlots(model);
        if (slots.Count == 0)
        {
            slots.Add(MaterialSlotInfo.Fallback(spec.id));
        }

        var slotCount = slots.Count;
        var materials = new Material[slotCount];
        var materialFolder = ImportRoot + "/" + spec.id + "/Materials";
        EnsureFolder(materialFolder);

        for (var i = 0; i < slotCount; i++)
        {
            var slot = slots[i];
            var materialPath = materialFolder + "/" + spec.id + "_" + i.ToString("00") + "_" + SanitizeAssetFileName(slot.ShortName) + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                material = new Material(FindAc6PartsShader());
                AssetDatabase.CreateAsset(material, materialPath);
            }

            ConfigureMaterial(material, textureSet, slot, spec);
            EditorUtility.SetDirty(material);
            materials[i] = material;
        }

        return materials;
    }

    private static TextureSet ImportTextureSet(ImportSpec spec, string sourceDirectory)
    {
        var modelStem = Path.GetFileNameWithoutExtension(spec.sourceRelativePath);
        var textureFolder = ImportRoot + "/" + spec.id + "/Textures";
        EnsureFolder(textureFolder);

        return new TextureSet
        {
            baseMap = ImportTexture(PickTexture(sourceDirectory, modelStem, "_a"), textureFolder, TextureKind.Base),
            normalMap = ImportTexture(PickTexture(sourceDirectory, modelStem, "_n"), textureFolder, TextureKind.Normal),
            maskMap = ImportTexture(
                PickTexture(sourceDirectory, modelStem, "_3m") ?? PickTexture(sourceDirectory, modelStem, "_m"),
                textureFolder,
                TextureKind.Mask),
            emissionMap = ImportTexture(PickTexture(sourceDirectory, modelStem, "_em"), textureFolder, TextureKind.Emission),
            paintAlbedoMap = ImportTexture(PickNamedTexture(sourceDirectory, "ac_01_a_u"), textureFolder, TextureKind.PaintBase),
            paintNormalMap = ImportTexture(PickNamedTexture(sourceDirectory, "ac_01_n_u"), textureFolder, TextureKind.PaintNormal),
            paintRoughnessMap = ImportTexture(PickNamedTexture(sourceDirectory, "ac_01_r_u"), textureFolder, TextureKind.PaintMask),
            paintSecondaryAlbedoMap = ImportTexture(PickNamedTexture(sourceDirectory, "ac_02_a_u"), textureFolder, TextureKind.PaintBase),
            paintTertiaryAlbedoMap = ImportTexture(PickNamedTexture(sourceDirectory, "ac_03_a_u"), textureFolder, TextureKind.PaintBase)
        };
    }

    private static string PickNamedTexture(string sourceDirectory, string fileStem)
    {
        var path = Path.Combine(sourceDirectory, fileStem + ".dds");
        return File.Exists(path) ? path : null;
    }

    private static string PickTexture(string sourceDirectory, string modelStem, string suffix)
    {
        var files = Directory.GetFiles(sourceDirectory, "*.dds", SearchOption.TopDirectoryOnly);
        var normalizedModelStem = NormalizeTextureStem(modelStem);
        var modelCode = ExtractTrailingCode(modelStem);
        string bestPath = null;
        var bestScore = int.MinValue;

        for (var i = 0; i < files.Length; i++)
        {
            var fileStem = Path.GetFileNameWithoutExtension(files[i]);
            var normalizedFileStem = NormalizeTextureStem(fileStem);
            if (!normalizedFileStem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = 0;
            if (normalizedFileStem.StartsWith(normalizedModelStem, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            if (!string.IsNullOrWhiteSpace(modelCode) &&
                normalizedFileStem.IndexOf(modelCode, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 60;
            }

            if (normalizedFileStem.IndexOf("_l" + suffix, StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedFileStem.EndsWith(suffix + "_l", StringComparison.OrdinalIgnoreCase))
            {
                score -= 25;
            }

            if (normalizedFileStem.IndexOf("_u" + suffix, StringComparison.OrdinalIgnoreCase) >= 0 ||
                normalizedFileStem.EndsWith(suffix + "_u", StringComparison.OrdinalIgnoreCase))
            {
                score -= 10;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestPath = files[i];
            }
        }

        return bestPath;
    }

    private static Texture2D ImportTexture(string sourcePath, string destinationFolder, TextureKind kind)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        var destinationAssetPath = destinationFolder + "/" + Path.GetFileName(sourcePath);
        var destinationAbsolutePath = ProjectAbsolutePath(destinationAssetPath);
        if (!File.Exists(destinationAbsolutePath))
        {
            FileUtil.CopyFileOrDirectory(sourcePath, destinationAbsolutePath);
        }

        AssetDatabase.ImportAsset(destinationAssetPath, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(destinationAssetPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = kind == TextureKind.Normal || kind == TextureKind.PaintNormal
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.sRGBTexture = kind == TextureKind.Base ||
                kind == TextureKind.Emission ||
                kind == TextureKind.PaintBase;
            importer.mipmapEnabled = true;
            importer.SaveAndReimport();
        }

        return AssetDatabase.LoadAssetAtPath<Texture2D>(destinationAssetPath);
    }

    private static void ConfigureMaterial(Material material, TextureSet textures, MaterialSlotInfo slot, ImportSpec spec)
    {
        material.shader = FindAc6PartsShader();
        material.name = slot.SourceName;
        material.SetOverrideTag("RenderType", "Opaque");
        material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Geometry;

        SetTexture(material, "_BaseMap", textures.baseMap);
        SetTexture(material, "_MainTex", textures.baseMap);
        SetTexture(material, "_BumpMap", textures.normalMap);
        SetTexture(material, "_Mask3Map", textures.maskMap);
        SetTexture(material, "_EmissionMap", textures.emissionMap);
        SetTexture(material, "_PaintAlbedoMap", textures.paintAlbedoMap);
        var weatheringMask = AssetDatabase.LoadAssetAtPath<Texture2D>(WeatheringMaskAssetPath);
        SetTexture(material, "_WeatheringMaskMap", weatheringMask);
        SetFloat(material, "_UseWeatheringMask", weatheringMask != null ? 1f : 0f);

        if (textures.normalMap != null)
        {
            material.EnableKeyword("_NORMALMAP");
        }
        else
        {
            material.DisableKeyword("_NORMALMAP");
        }

        var zonePalette = ZonePaletteForSlot(slot);
        var useZoneMode = slot.Kind == Ac6MaterialKind.ColorMaterial && textures.maskMap != null;
        var baseColor = useZoneMode ? zonePalette.zone1 : new Color(0.78f, 0.80f, 0.80f, 1f);
        var paintColor = PaintColorForSpec(spec);
        var metallic = 0.38f;
        var smoothness = 0.36f;
        var bumpScale = 0.70f;
        var paintInfluence = 0.45f;
        var albedoDetailStrength = 0.24f;
        var maskMetallicInfluence = 0.14f;
        var maskSmoothnessInfluence = 0.18f;
        var occlusionStrength = 0.10f;
        var emissionColor = Color.black;
        var emissionStrength = 0f;

        if (slot.Kind == Ac6MaterialKind.ColorMaterial)
        {
            paintColor = zonePalette.zone2;
            paintInfluence = 0f;
            albedoDetailStrength = 0.32f;
            metallic = 0.10f;
            smoothness = 0.38f;
            bumpScale = 0.85f;
            maskMetallicInfluence = 0.05f;
            maskSmoothnessInfluence = 0.08f;
            occlusionStrength = 0.18f;
        }
        else if (slot.Kind == Ac6MaterialKind.Limiter)
        {
            baseColor = new Color(0.05f, 0.07f, 0.08f, 1f);
            paintColor = baseColor;
            paintInfluence = 0f;
            albedoDetailStrength = 0.10f;
            metallic = 0.25f;
            smoothness = 0.55f;
            bumpScale = 0.45f;
            emissionColor = new Color(0.45f, 0.72f, 1.0f, 1f);
            emissionStrength = 2.6f;
        }
        else if (slot.Kind == Ac6MaterialKind.Emissive)
        {
            baseColor = new Color(0.03f, 0.05f, 0.06f, 1f);
            paintColor = baseColor;
            paintInfluence = 0f;
            albedoDetailStrength = 0.08f;
            metallic = 0.15f;
            smoothness = 0.50f;
            bumpScale = 0.35f;
            emissionColor = new Color(0.55f, 0.85f, 1.45f, 1f);
            emissionStrength = 3.4f;
        }
        else if (slot.Kind == Ac6MaterialKind.Marking)
        {
            baseColor = new Color(0.86f, 0.86f, 0.82f, 1f);
            paintColor = Color.white;
            paintInfluence = 0.30f;
            albedoDetailStrength = 0.16f;
            metallic = 0.05f;
            smoothness = 0.22f;
            bumpScale = 0.25f;
            maskMetallicInfluence = 0f;
            maskSmoothnessInfluence = 0.10f;
            occlusionStrength = 0.20f;
        }

        SetColor(material, "_BaseColor", baseColor);
        SetColor(material, "_Color", baseColor);
        SetColor(material, "_PaintColor", paintColor);
        SetColor(material, "_Zone1Color", zonePalette.zone1);
        SetColor(material, "_Zone2Color", zonePalette.zone2);
        SetColor(material, "_Zone3Color", zonePalette.zone3);
        SetColor(material, "_Zone4Color", zonePalette.zone4);
        SetColor(material, "_Zone5Color", zonePalette.zone5);
        SetColor(material, "_Zone6Color", zonePalette.zone6);
        SetColor(material, "_Zone7Color", zonePalette.zone7);
        SetFloat(material, "_Surface", 0f);
        SetFloat(material, "_AlphaClip", 0f);
        SetFloat(material, "_ZoneMode", useZoneMode ? 1f : 0f);
        SetFloat(material, "_ZoneMaskStrength", useZoneMode ? 1f : 0f);
        SetFloat(material, "_Metallic", metallic);
        SetFloat(material, "_Smoothness", smoothness);
        SetFloat(material, "_BumpScale", bumpScale);
        SetFloat(material, "_PaintInfluence", paintInfluence);
        SetFloat(material, "_AlbedoDetailStrength", albedoDetailStrength);
        SetFloat(material, "_MaskMetallicInfluence", maskMetallicInfluence);
        SetFloat(material, "_MaskSmoothnessInfluence", maskSmoothnessInfluence);
        SetFloat(material, "_OcclusionStrength", occlusionStrength);
        material.DisableKeyword("_METALLICSPECGLOSSMAP");

        if (slot.Kind == Ac6MaterialKind.Limiter || slot.Kind == Ac6MaterialKind.Emissive)
        {
            SetColor(material, "_EmissionColor", emissionColor);
            SetFloat(material, "_EmissionStrength", emissionStrength);
            material.EnableKeyword("_EMISSION");
        }
        else
        {
            SetColor(material, "_EmissionColor", Color.black);
            SetFloat(material, "_EmissionStrength", 0f);
            material.DisableKeyword("_EMISSION");
        }
    }

    private static Ac6ZonePalette ZonePaletteForSlot(MaterialSlotInfo slot)
    {
        var palette = Ac6ZonePalette.Default;
        var source = slot != null
            ? (slot.SourceName + " " + slot.ShortName + " " + slot.MatXmlName)
            : string.Empty;

        if (source.IndexOf("LG_M", StringComparison.OrdinalIgnoreCase) >= 0 ||
            source.IndexOf("LEG", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            palette.zone2 = new Color(0.074f, 0.072f, 0.069f, 1f);
        }

        return palette;
    }

    private static Color PaintColorForSpec(ImportSpec spec)
    {
        if (spec == null || string.IsNullOrWhiteSpace(spec.id))
        {
            return new Color(0.58f, 0.63f, 0.66f, 1f);
        }

        if (spec.id.IndexOf("_2010", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new Color(0.35f, 0.42f, 0.48f, 1f);
        }

        if (spec.id.IndexOf("_3010", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return new Color(0.58f, 0.53f, 0.46f, 1f);
        }

        return new Color(0.56f, 0.62f, 0.67f, 1f);
    }

    private struct Ac6ZonePalette
    {
        public Color zone1;
        public Color zone2;
        public Color zone3;
        public Color zone4;
        public Color zone5;
        public Color zone6;
        public Color zone7;

        public static Ac6ZonePalette Default => new Ac6ZonePalette
        {
            zone1 = new Color(0.420f, 0.375f, 0.320f, 1f),
            zone2 = new Color(0.389f, 0.389f, 0.389f, 1f),
            zone3 = new Color(0.420f, 0.375f, 0.320f, 1f),
            zone4 = new Color(0.091f, 0.091f, 0.091f, 1f),
            zone5 = new Color(0.263f, 0.263f, 0.263f, 1f),
            zone6 = new Color(0.214f, 0.214f, 0.214f, 1f),
            zone7 = Color.black
        };
    }


    private static List<MaterialSlotInfo> GetMaterialSlots(GameObject model)
    {
        var slots = new List<MaterialSlotInfo>();
        if (model == null)
        {
            return slots;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renderers = model.GetComponentsInChildren<Renderer>(true);
        for (var i = 0; i < renderers.Length; i++)
        {
            var materials = renderers[i].sharedMaterials;
            for (var m = 0; m < materials.Length; m++)
            {
                if (materials[m] == null)
                {
                    continue;
                }

                var sourceName = CanonicalMaterialName(materials[m].name);
                if (string.IsNullOrWhiteSpace(sourceName) || seen.Contains(sourceName))
                {
                    continue;
                }

                seen.Add(sourceName);
                slots.Add(MaterialSlotInfo.FromSourceName(sourceName));
            }
        }

        return slots;
    }

    private static string CanonicalMaterialName(string materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
        {
            return string.Empty;
        }

        return materialName
            .Replace(" (Instance)", string.Empty)
            .Trim();
    }

    private static Shader FindAc6PartsShader()
    {
        return Shader.Find("ACunicombat/AC6/Parts Approx")
            ?? FindLitShader();
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

    private static string SanitizeAssetFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Material";
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = value.ToCharArray();
        for (var i = 0; i < characters.Length; i++)
        {
            if (Array.IndexOf(invalidCharacters, characters[i]) >= 0 ||
                characters[i] == '[' ||
                characters[i] == ']' ||
                characters[i] == '|')
            {
                characters[i] = '_';
            }
        }

        var sanitized = new string(characters).Trim('_', ' ');
        return sanitized.Length > 56
            ? sanitized.Substring(0, 56)
            : sanitized;
    }

    private static string NormalizeTextureStem(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.ToLowerInvariant()
            .Replace("_l_", "_")
            .Replace("_r_", "_")
            .Replace("_e_", "_")
            .Replace("_a_", "_");
    }

    private static string ExtractTrailingCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var digits = string.Empty;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (!char.IsDigit(value[i]))
            {
                break;
            }

            digits = value[i] + digits;
        }

        return digits;
    }

    private static MechPartDefinition CreateOrUpdatePart(ImportSpec spec, GameObject model, Material[] materialOverrides)
    {
        var partPath = DataRoot + "/" + spec.id + ".asset";
        var part = CreateOrLoadAsset<MechPartDefinition>(partPath);
        part.EditorConfigure(
            spec.id,
            spec.displayName,
            spec.slot,
            spec.category,
            model,
            spec.stats,
            BuildMounts(spec),
            spec.tags,
            materialOverrides,
            newArmPerformance: spec.slot == MechPartSlot.Arms
				? BuildArmPerformance(spec.stats.massKg / 6900f)
				: null,
			newLegPerformance: spec.legPerformance);
        return part;
    }

	private static MechArmPerformance BuildArmPerformance(float scale)
	{
		scale = Mathf.Max(0.5f, scale);
		return new MechArmPerformance
		{
			weaponLoadLimitKg = 8000f * scale,
			recoilImpulseLimitNs = 22000f * scale,
			recoilRecoveryRateNsPerSecond = 85000f * scale,
			aimTraverseRateDegPerSecond = 105f / Mathf.Sqrt(scale),
			aimAngularAccelerationDegPerSecondSquared = 420f / scale,
			weaponPointingErrorMrad = 0.18f * Mathf.Max(0.8f, scale),
			thrusterNozzles = new[]
			{
				new MechArmThrusterNozzle { nozzleId = "arm-lateral", role = MechArmThrusterRole.Lateral, localPositionM = new Vector3(0.82f, 0.08f, 0f), localThrustDirection = Vector3.left, maximumThrustKn = 40f * scale, gimbalLimitDeg = 8f, maximumPowerKw = 420f * scale, maximumHeatKw = 180f * scale },
				new MechArmThrusterNozzle { nozzleId = "arm-rcs-forward", role = MechArmThrusterRole.Rcs, localPositionM = new Vector3(0.78f, 0.32f, 0.28f), localThrustDirection = Vector3.forward, maximumThrustKn = 75f * scale, maximumPowerKw = 260f * scale, maximumHeatKw = 120f * scale },
				new MechArmThrusterNozzle { nozzleId = "arm-rcs-rear", role = MechArmThrusterRole.Rcs, localPositionM = new Vector3(0.78f, -0.18f, -0.24f), localThrustDirection = Vector3.back, maximumThrustKn = 75f * scale, maximumPowerKw = 260f * scale, maximumHeatKw = 120f * scale }
			}
		};
	}

	private static MechLegPerformance BuildLegPerformance(float scale)
	{
		scale = Mathf.Max(0.5f, scale);
		return new MechLegPerformance
		{
			maximumSupportedMassKg = 65000f * scale,
			primaryThrusterNozzles = new[]
			{
				new MechLegThrusterNozzle { nozzleId = "leg-primary-front", localPositionM = new Vector3(0.42f, -0.72f, 0.3f), localThrustDirection = Vector3.up, maximumThrustKn = 375f * scale, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18f, lateralGimbalLimitDeg = 0.5f, maximumPowerKw = 250f * scale, maximumHeatKw = 100f * scale },
				new MechLegThrusterNozzle { nozzleId = "leg-primary-rear", localPositionM = new Vector3(0.42f, -0.78f, -0.24f), localThrustDirection = Vector3.up, maximumThrustKn = 375f * scale, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18f, lateralGimbalLimitDeg = 0.5f, maximumPowerKw = 250f * scale, maximumHeatKw = 100f * scale }
			},
			lateralThrusterNozzles = new[]
			{
				new MechLegThrusterNozzle { nozzleId = "leg-lateral", localPositionM = new Vector3(0.48f, -0.35f, 0f), localThrustDirection = Vector3.left, maximumThrustKn = 8f * scale, maximumPowerKw = 60f * scale, maximumHeatKw = 20f * scale }
			},
			rcsThrusterNozzles = new[]
			{
				new MechLegThrusterNozzle { nozzleId = "leg-rcs-forward", localPositionM = new Vector3(0.44f, -0.18f, 0.32f), localThrustDirection = Vector3.forward, maximumThrustKn = 60f * scale, maximumPowerKw = 60f * scale, maximumHeatKw = 25f * scale },
				new MechLegThrusterNozzle { nozzleId = "leg-rcs-rear", localPositionM = new Vector3(0.44f, -0.58f, -0.3f), localThrustDirection = Vector3.back, maximumThrustKn = 60f * scale, maximumPowerKw = 60f * scale, maximumHeatKw = 25f * scale }
			}
		};
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

    private static void CreateInternalParts(Dictionary<string, MechPartDefinition> createdParts)
    {
        createdParts["AC6_GEN_Test_Reactor"] = CreateInternal(
            "AC6_GEN_Test_Reactor",
            "AC6 Test Compact Reactor",
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
            "internal", "generator", "ac6-imported");

        createdParts["AC6_RAD_Test_Radiator"] = CreateInternal(
            "AC6_RAD_Test_Radiator",
            "AC6 Test Vacuum Radiator",
            MechPartSlot.Radiator,
            new MechPartStats
            {
                massKg = 2800f,
                idlePowerDrawMw = 0.25f,
                peakPowerDrawMw = 1.2f,
                idleHeatMw = 0.06f,
                activeHeatMw = 0.22f,
                heatGenerationMw = 0.22f,
                heatDissipationMw = 18.4f,
                heatCapacityMj = 120f,
                thermalSignature = 3f
            },
            "internal", "radiator", "ac6-imported");

        createdParts["AC6_FCS_Test_Computer"] = CreateInternal(
            "AC6_FCS_Test_Computer",
            "AC6 Test Track Computer",
            MechPartSlot.FireControlSystem,
            new MechPartStats
            {
                massKg = 900f,
                idlePowerDrawMw = 1.4f,
                peakPowerDrawMw = 3f,
                fireControlQuality = 48f,
                trackCapacity = 16,
                guidanceChannels = 3,
                electromagneticSignature = 0.4f
            },
            "internal", "fcs", "ac6-imported");

        createdParts["AC6_RDR_Test_Array"] = CreateInternal(
            "AC6_RDR_Test_Array",
            "AC6 Test Active Radar",
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
            "internal", "radar", "active-sensor", "ac6-imported");
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
            "ac6_modular_test",
            "AC6 Modular Test",
            new[]
            {
                Assignment(MechPartSlot.Head, parts, "AC6_HD_M_1010"),
                Assignment(MechPartSlot.Core, parts, "AC6_BD_M_1010"),
                Assignment(MechPartSlot.Arms, parts, "AC6_AM_M_1010"),
                Assignment(MechPartSlot.Legs, parts, "AC6_LG_M_1010"),
                Assignment(MechPartSlot.Booster, parts, "AC6_BS_A_1010"),
                Assignment(MechPartSlot.Generator, parts, "AC6_GEN_Test_Reactor"),
                Assignment(MechPartSlot.Radiator, parts, "AC6_RAD_Test_Radiator"),
                Assignment(MechPartSlot.FireControlSystem, parts, "AC6_FCS_Test_Computer"),
                Assignment(MechPartSlot.Radar, parts, "AC6_RDR_Test_Array"),
                Assignment(MechPartSlot.LeftHandWeapon, parts, "AC6_WP_0010_Left_Rifle"),
                Assignment(MechPartSlot.LeftBackWeapon, parts, "AC6_WP_1410_Left_Back"),
                Assignment(MechPartSlot.RightBackWeapon, parts, "AC6_WP_1310_Right_Back")
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
		public MechLegPerformance legPerformance;
        public string[] tags;
    }

    private enum TextureKind
    {
        Base,
        Normal,
        Mask,
        Emission,
        PaintBase,
        PaintNormal,
        PaintMask
    }

    private enum Ac6MaterialKind
    {
        Generic,
        ColorMaterial,
        Limiter,
        Emissive,
        Marking
    }

    private sealed class TextureSet
    {
        public Texture2D baseMap;
        public Texture2D normalMap;
        public Texture2D maskMap;
        public Texture2D emissionMap;
        public Texture2D paintAlbedoMap;
        public Texture2D paintNormalMap;
        public Texture2D paintRoughnessMap;
        public Texture2D paintSecondaryAlbedoMap;
        public Texture2D paintTertiaryAlbedoMap;

        public bool HasAnyTexture => baseMap != null ||
            normalMap != null ||
            maskMap != null ||
            emissionMap != null ||
            paintAlbedoMap != null;
    }

    private sealed class MaterialSlotInfo
    {
        public string SourceName { get; private set; }
        public string ShortName { get; private set; }
        public string MatXmlName { get; private set; }
        public Ac6MaterialKind Kind { get; private set; }

        public static MaterialSlotInfo Fallback(string sourceName)
        {
            return new MaterialSlotInfo
            {
                SourceName = string.IsNullOrWhiteSpace(sourceName) ? "FallbackMaterial" : sourceName,
                ShortName = "FallbackMaterial",
                MatXmlName = string.Empty,
                Kind = Ac6MaterialKind.Generic
            };
        }

        public static MaterialSlotInfo FromSourceName(string sourceName)
        {
            var shortName = sourceName;
            var matXmlName = string.Empty;
            var pieces = sourceName.Split('|');
            if (pieces.Length > 0 && !string.IsNullOrWhiteSpace(pieces[0]))
            {
                shortName = pieces[0].Trim();
            }

            for (var i = 0; i < pieces.Length; i++)
            {
                if (pieces[i].IndexOf(".matxml", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matXmlName = pieces[i].Trim();
                    break;
                }
            }

            return new MaterialSlotInfo
            {
                SourceName = sourceName,
                ShortName = shortName,
                MatXmlName = matXmlName,
                Kind = ClassifyMaterial(sourceName, matXmlName)
            };
        }

        private static Ac6MaterialKind ClassifyMaterial(string sourceName, string matXmlName)
        {
            var combined = (sourceName + " " + matXmlName).ToLowerInvariant();
            if (combined.Contains("limiter") || combined.Contains("lmiter"))
            {
                return Ac6MaterialKind.Limiter;
            }

            if (combined.Contains("emissive") ||
                combined.Contains("eye_") ||
                combined.Contains("eye"))
            {
                return Ac6MaterialKind.Emissive;
            }

            if (combined.Contains("marking") || combined.Contains("making"))
            {
                return Ac6MaterialKind.Marking;
            }

            if (combined.Contains("[cm]") ||
                combined.Contains("colormaterial") ||
                combined.Contains("_mat"))
            {
                return Ac6MaterialKind.ColorMaterial;
            }

            return Ac6MaterialKind.Generic;
        }
    }
}
