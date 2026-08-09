using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class AC6SkinnedModularSampleBuilder
{
	private const string WeatheringMaskAssetPath = "Assets/_Project/Resources/AC6Materials/SYSTEX_camouflage01.png";

	[Serializable]
	private sealed class PaintPresetRecord
	{
		public string source;

		public int rowId;

		public string displayName;

		public string baseColor;

		public string subColor;

		public string supportColor;

		public string optionalColor;

		public string deviceColor;

		public string[] zoneColors;
	}

	[Serializable]
	private sealed class MaterialCatalogRecord
	{
		public int schemaVersion;

		public string sourceArchive;

		public MaterialProfileRecord[] materials;
	}

	[Serializable]
	private sealed class MaterialProfileRecord
	{
		public string sourcePath;

		public string shaderPath;

		public string surfaceClass;

		public int blendMode;

		public bool disableDepthWrite;

		public bool cullNone;

		public bool paintDecal;

		public bool emissive;

		public string albedoMap;

		public string normalMap;

		public string mask3Map;

		public string metallicMap;

		public string emissionMap;

		public string mask3Role;

		public float recommendedMetallic;

		public float recommendedSmoothness;
	}

	[Serializable]
	private sealed class NativeEquipmentManifestWrapper
	{
		public List<NativeEquipmentManifestEntry> items;
	}

	[Serializable]
	private sealed class NativeEquipmentManifestEntry
	{
		public string key;

		public int animationCount;
	}

	private sealed class NativeWeaponSpec
	{
		public string Key { get; }

		public string DisplayName { get; }

		public MechPartSlot Slot { get; }

		public bool IsMelee { get; }

		public bool IsPointDefense { get; }

		public string PreferredActionId { get; }

		public float MassKg { get; }

		public NativeWeaponSpec(string key, string displayName, MechPartSlot slot, string preferredActionId, float massKg, bool isMelee = false, bool isPointDefense = false)
		{
			Key = key;
			DisplayName = displayName;
			Slot = slot;
			IsMelee = isMelee;
			IsPointDefense = isPointDefense;
			PreferredActionId = preferredActionId;
			MassKg = massKg;
		}
	}

	private sealed class FrameSlotSpec
	{
		public string Token { get; }

		public string DisplayName { get; }

		public MechPartSlot Slot { get; }

		public FrameSlotSpec(string token, string displayName, MechPartSlot slot)
		{
			Token = token;
			DisplayName = displayName;
			Slot = slot;
		}
	}

	private sealed class BuildResult
	{
		public AnimationClip AnimationClip { get; }

		public Dictionary<string, MechPartDefinition> FrameParts { get; }

		public Dictionary<string, MechLoadoutDefinition> Loadouts { get; }

		public BuildResult(AnimationClip animationClip, Dictionary<string, MechPartDefinition> frameParts, Dictionary<string, MechLoadoutDefinition> loadouts)
		{
			AnimationClip = animationClip;
			FrameParts = frameParts;
			Loadouts = loadouts;
		}
	}

	private sealed class ValidationReport
	{
		public StringBuilder Log { get; } = new StringBuilder();

		public int Passes { get; set; }

		public int Failures { get; set; }

		public string Summary { get; private set; } = string.Empty;

		public void Finish()
		{
			Summary = ((Failures == 0) ? string.Format("Validation passed for {0} loadouts.\n\nScene: {1}\nReport: {2}/skinned_modular_validation.txt", Passes, "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity", "Assets/_Project/LocalSamples/AC6SkinnedModular/Reports") : string.Format("Validation found {0} failure(s) across {1} passing loadouts.\n\nSee: {2}/skinned_modular_validation.txt", Failures, Passes, "Assets/_Project/LocalSamples/AC6SkinnedModular/Reports"));
			Log.AppendLine();
			Log.AppendLine(Summary);
		}
	}

	private const string ValidationRequestFileName = "RunAC6SkinnedModularValidation.request";

	private const string RawGltfRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf";

	private const string GeneratedRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated";

	private const string PartRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts";

	private const string LoadoutRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts";

	private const string SceneRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes";

	private const string MaterialRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Materials/TestNavy";

	private const string ColorMaskRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/ColorMasks";

	private const string ActivePaintPresetPath = "Assets/_Project/Data/Mechs/AC6PaintPresets/acunicombat_test_navy.json";

	private const string MaterialCatalogPath = "Assets/_Project/Data/Mechs/AC6Materials/ac6_frame_material_catalog.json";

	private const string ReportRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Reports";

	private const string ValidationScenePath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity";

	private const string AnimationSourcePath = "Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb";

	private const string NativeEquipmentRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment";

	private const string BoosterMountedDisplayActionId = "a000_000001";

	private static readonly Vector3 NativeBoosterInstallEuler = new Vector3(180f, 0f, 0f);

	private static readonly Vector3 NativeWeaponInstallEuler = new Vector3(0f, 180f, 0f);

	private static readonly Regex EquipmentActionPattern = new Regex("a\\d{3}_\\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

	private static readonly string[] FrameCodes = new string[3] { "1010", "2010", "3010" };

	private static readonly string[] HeadCoreDrivenBones = new string[10] { "Master", "Root", "Root_Rot_XYZ", "BD_Root", "Spine", "Offset_SpineLink", "SpineLink", "Offset_Spine1", "Spine1", "Neck" };

	private static readonly string[] ArmsCoreDrivenBones = new string[20]
	{
		"Master", "Root", "Root_Rot_XYZ", "BD_Root", "Spine", "Offset_SpineLink", "SpineLink", "Offset_Spine1", "Spine1", "Offset_ClavicleLink",
		"L_ClavicleLink", "L_ClavicleLink_EX", "Offset_L_Clavicle", "L_Clavicle", "L_ArmJoint", "R_ClavicleLink", "Offset_R_Clavicle", "R_Clavicle", "R_ArmJoint", "R_ClavicleLink_EX"
	};

	private static readonly string[] CoreLegsDrivenBones = new string[4] { "Master", "Root", "Root_Rot_XYZ", "BD_Root" };

	private static readonly FrameSlotSpec[] FrameSlots = new FrameSlotSpec[4]
	{
		new FrameSlotSpec("HD", "Head", MechPartSlot.Head),
		new FrameSlotSpec("BD", "Core", MechPartSlot.Core),
		new FrameSlotSpec("AM", "Arms", MechPartSlot.Arms),
		new FrameSlotSpec("LG", "Legs", MechPartSlot.Legs)
	};

	private static List<NativeEquipmentManifestEntry> _nativeEquipmentManifest;

	[MenuItem("Tools/ACunicombat/AC6/Build Skinned Modular Sample Assets")]
	public static void BuildSampleAssetsMenu()
	{
		if (!TryBuildSampleAssets(out var result, out var error))
		{
			EditorUtility.DisplayDialog("AC6 Skinned Modular Sample", error, "OK");
		}
		else
		{
			EditorUtility.DisplayDialog("AC6 Skinned Modular Sample", $"Built {result.FrameParts.Count} skinned frame parts and {result.Loadouts.Count} loadouts.\n\n" + "Parts: Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts\nLoadouts: Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts", "OK");
		}
	}

	[MenuItem("Tools/ACunicombat/AC6/Build And Validate Skinned Modular Samples")]
	public static void BuildAndValidateMenu()
	{
		ValidationReport report = BuildAndValidate(batchMode: false);
		EditorUtility.DisplayDialog("AC6 Skinned Modular Validation", report.Summary, "OK");
	}

	public static void RunBatch()
	{
		ValidationReport report = BuildAndValidate(batchMode: true);
		if (Application.isBatchMode)
		{
			EditorApplication.Exit((report.Failures != 0) ? 1 : 0);
		}
	}

	public static bool BuildSampleAssetsForPipeline(out string report)
	{
		if (!TryBuildSampleAssets(out var result, out var error))
		{
			report = error;
			return false;
		}
		AC6FullPartCatalogBuilder.BuildAvailableCatalog(out var catalogReport);
		report = $"Built {result.FrameParts.Count} skinned frame parts and {result.Loadouts.Count} loadouts. {catalogReport}";
		return true;
	}

	internal static bool RefreshGeneratedFrameGameplayData(out int refreshedCount)
	{
		refreshedCount = 0;
		const string generatedPartsRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts";
		if (!AssetDatabase.IsValidFolder(generatedPartsRoot))
		{
			return false;
		}

		for (var codeIndex = 0; codeIndex < FrameCodes.Length; codeIndex++)
		{
			string code = FrameCodes[codeIndex];
			float scale = MassScale(code);
			for (var slotIndex = 0; slotIndex < FrameSlots.Length; slotIndex++)
			{
				FrameSlotSpec slot = FrameSlots[slotIndex];
				string id = "AC6_SKIN_" + slot.Token + "_M_" + code;
				string path = generatedPartsRoot + "/" + id + ".asset";
				MechPartDefinition part = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(path);
				if (part == null)
				{
					continue;
				}

				part.EditorConfigure(
					part.PartId,
					part.DisplayName,
					part.Slot,
					part.Category,
					part.ModelPrefab,
					BuildFrameStats(slot.Slot, scale, code),
					part.MountPoints,
					part.Tags,
					part.MaterialOverrides,
					part.VisualRigMode,
					part.SkinnedAnimationClip,
					part.AncestorDriverSlot,
					part.AncestorDrivenBoneNames,
					part.InstantiateVisualAtAllMounts,
					part.EquipmentAnimations,
					part.DefaultEquipmentActionId,
					part.PlayDefaultEquipmentActionOnAssemble,
					part.LocalPosition,
					part.LocalEulerAngles,
					part.LocalScale,
					part.PreviewModelPrefab,
					part.PreviewImage,
					part.InternalPerformance,
					BuildHeadSensorPerformance(slot.Slot, code),
					BuildArmPerformance(slot.Slot, scale),
					BuildCorePerformance(slot.Slot, scale, code),
					BuildLegPerformance(slot.Slot, scale),
					part.BoosterPerformance,
					part.GeneratorPerformance);
				refreshedCount++;
			}
		}

		for (var codeIndex = 0; codeIndex < FrameCodes.Length; codeIndex++)
		{
			string code = FrameCodes[codeIndex];
			string id = "AC6_NATIVE_BS_A_" + code;
			string path = generatedPartsRoot + "/" + id + ".asset";
			MechPartDefinition part = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(path);
			if (part == null)
			{
				continue;
			}

			part.EditorConfigure(
				part.PartId,
				part.DisplayName,
				part.Slot,
				part.Category,
				part.ModelPrefab,
				BuildBoosterStats(code),
				part.MountPoints,
				part.Tags,
				part.MaterialOverrides,
				part.VisualRigMode,
				part.SkinnedAnimationClip,
				part.AncestorDriverSlot,
				part.AncestorDrivenBoneNames,
				part.InstantiateVisualAtAllMounts,
				part.EquipmentAnimations,
				part.DefaultEquipmentActionId,
				part.PlayDefaultEquipmentActionOnAssemble,
				part.LocalPosition,
				part.LocalEulerAngles,
				part.LocalScale,
				part.PreviewModelPrefab,
				part.PreviewImage,
				part.InternalPerformance,
				part.HeadSensorPerformance,
				part.ArmPerformance,
				part.CorePerformance,
				part.LegPerformance,
				BuildBoosterPerformance(code),
				part.GeneratorPerformance);
			refreshedCount++;
		}

		const string sampleGeneratorId = "AC6_SKIN_SAMPLE_GENERATOR";
		MechPartDefinition sampleGenerator = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(
			generatedPartsRoot + "/" + sampleGeneratorId + ".asset");
		if (sampleGenerator != null)
		{
			sampleGenerator.EditorConfigure(
				sampleGenerator.PartId,
				sampleGenerator.DisplayName,
				sampleGenerator.Slot,
				sampleGenerator.Category,
				sampleGenerator.ModelPrefab,
				BuildSampleGeneratorStats(),
				sampleGenerator.MountPoints,
				sampleGenerator.Tags,
				sampleGenerator.MaterialOverrides,
				sampleGenerator.VisualRigMode,
				sampleGenerator.SkinnedAnimationClip,
				sampleGenerator.AncestorDriverSlot,
				sampleGenerator.AncestorDrivenBoneNames,
				sampleGenerator.InstantiateVisualAtAllMounts,
				sampleGenerator.EquipmentAnimations,
				sampleGenerator.DefaultEquipmentActionId,
				sampleGenerator.PlayDefaultEquipmentActionOnAssemble,
				sampleGenerator.LocalPosition,
				sampleGenerator.LocalEulerAngles,
				sampleGenerator.LocalScale,
				sampleGenerator.PreviewModelPrefab,
				sampleGenerator.PreviewImage,
				sampleGenerator.InternalPerformance,
				sampleGenerator.HeadSensorPerformance,
				sampleGenerator.ArmPerformance,
				sampleGenerator.CorePerformance,
				sampleGenerator.LegPerformance,
				sampleGenerator.BoosterPerformance,
				BuildSampleGeneratorPerformance());
			refreshedCount++;
		}

		MechPartDefinition sampleFcs = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(
			generatedPartsRoot + "/AC6_SKIN_SAMPLE_FCS.asset");
		if (sampleFcs != null)
		{
			sampleFcs.EditorConfigure(
				sampleFcs.PartId,
				sampleFcs.DisplayName,
				sampleFcs.Slot,
				sampleFcs.Category,
				sampleFcs.ModelPrefab,
				BuildSampleFireControlStats(),
				sampleFcs.MountPoints,
				sampleFcs.Tags,
				newFireControlPerformance: BuildSampleFireControlPerformance());
			refreshedCount++;
		}

		MechPartDefinition sampleRadar = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(
			generatedPartsRoot + "/AC6_SKIN_SAMPLE_RADAR.asset");
		if (sampleRadar != null)
		{
			sampleRadar.EditorConfigure(
				sampleRadar.PartId,
				sampleRadar.DisplayName,
				sampleRadar.Slot,
				sampleRadar.Category,
				sampleRadar.ModelPrefab,
				BuildSampleRadarStats(),
				sampleRadar.MountPoints,
				sampleRadar.Tags,
				newRadarPerformance: BuildSampleRadarPerformance());
			refreshedCount++;
		}

		if (refreshedCount > 0)
		{
			AssetDatabase.SaveAssets();
		}
		return refreshedCount > 0;
	}

	[InitializeOnLoadMethod]
	private static void RunRequestedValidationAfterReload()
	{
		string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
		if (string.IsNullOrWhiteSpace(projectRoot))
		{
			return;
		}
		string requestPath = Path.Combine(projectRoot, "Temp", "RunAC6SkinnedModularValidation.request");
		if (!File.Exists(requestPath))
		{
			return;
		}
		File.Delete(requestPath);
		EditorApplication.delayCall = (EditorApplication.CallbackFunction)Delegate.Combine(EditorApplication.delayCall, (EditorApplication.CallbackFunction)delegate
		{
			if (EditorApplication.isPlayingOrWillChangePlaymode)
			{
				Debug.LogError("AC6 skinned modular validation request was skipped because Unity is in Play Mode.");
			}
			else
			{
				Debug.Log(BuildAndValidate(batchMode: false).Summary);
			}
		});
	}

	private static ValidationReport BuildAndValidate(bool batchMode)
	{
		ValidationReport report = new ValidationReport();
		try
		{
			if (!TryBuildSampleAssets(out var result, out var error))
			{
				report.Failures++;
				report.Log.AppendLine("BUILD FAIL: " + error);
				report.Finish();
				WriteReport(report);
				return report;
			}
			BuildValidationSceneAndRunChecks(result, report, batchMode);
		}
		catch (Exception ex)
		{
			report.Failures++;
			report.Log.AppendLine("EXCEPTION: " + ex);
		}
		report.Finish();
		WriteReport(report);
		Debug.Log("[AC6SkinnedModularValidation]\n" + report.Log);
		return report;
	}

	private static bool TryBuildSampleAssets(out BuildResult result, out string error)
	{
		result = null;
		error = string.Empty;
		EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated");
		EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts");
		EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts");
		EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes");
		EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Materials/TestNavy");
		Directory.CreateDirectory("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports");
		AssetDatabase.Refresh();
		if (!TryLoadPaintPreset("Assets/_Project/Data/Mechs/AC6PaintPresets/acunicombat_test_navy.json", out var paintPreset, out error))
		{
			return false;
		}
		if (!TryLoadMaterialCatalog("Assets/_Project/Data/Mechs/AC6Materials/ac6_frame_material_catalog.json", out var materialCatalog, out error))
		{
			return false;
		}
		AssetDatabase.ImportAsset("Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb", ImportAssetOptions.ForceUpdate);
		AnimationClip animationClip = (from candidate in AssetDatabase.LoadAllAssetsAtPath("Assets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb").OfType<AnimationClip>()
			where candidate != null && candidate.length > 0.01f
			orderby candidate.length descending
			select candidate).FirstOrDefault();
		if (animationClip == null)
		{
			error = "AnimationClip was not found in:\nAssets/_Project/LocalSamples/AC6MelanderCase/Raw/Gltf/Melander_1010_Rerun.glb\n\nRun the Melander GLB pipeline first.";
			return false;
		}
		Dictionary<string, MechPartDefinition> frameParts = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
		List<string> missing = new List<string>();
		for (int codeIndex = 0; codeIndex < FrameCodes.Length; codeIndex++)
		{
			string code = FrameCodes[codeIndex];
			for (int slotIndex = 0; slotIndex < FrameSlots.Length; slotIndex++)
			{
				FrameSlotSpec slot = FrameSlots[slotIndex];
				string glbPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf/" + slot.Token + "_M_" + code + ".glb";
				AssetDatabase.ImportAsset(glbPath, ImportAssetOptions.ForceUpdate);
				GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
				AnimationClip partClip = (from candidate in AssetDatabase.LoadAllAssetsAtPath(glbPath).OfType<AnimationClip>()
					where candidate != null && candidate.length > 0.01f
					orderby candidate.length descending
					select candidate).FirstOrDefault();
				if (model == null || partClip == null)
				{
					missing.Add(glbPath + ((model == null) ? " (model missing)" : " (animation clip missing)"));
					continue;
				}
				string id = "AC6_SKIN_" + slot.Token + "_M_" + code;
				MechPartDefinition part = LoadOrCreateAsset<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts/" + id + ".asset");
				Material[] materialOverrides = BuildPaintMaterialOverrides(model, slot.Token, code, paintPreset, materialCatalog);
				part.EditorConfigure(id, "AC6 " + SetDisplayName(code) + " " + slot.DisplayName, slot.Slot, MechPartCategory.Frame, model, BuildFrameStats(slot.Slot, MassScale(code), code), BuildFrameMountPoints(slot.Slot), new string[4]
				{
					"frame",
					"ac6-skinned",
					"set-" + code,
					slot.Token.ToLowerInvariant()
				}, materialOverrides, MechVisualRigMode.SynchronizedSkinned, partClip, AncestorDriverForSlot(slot.Slot), AncestorDrivenBonesForSlot(slot.Slot), newHeadSensorPerformance: BuildHeadSensorPerformance(slot.Slot, code), newArmPerformance: BuildArmPerformance(slot.Slot, MassScale(code)), newCorePerformance: BuildCorePerformance(slot.Slot, MassScale(code), code), newLegPerformance: BuildLegPerformance(slot.Slot, MassScale(code)));
				frameParts[FrameKey(slot.Slot, code)] = part;
			}
		}
		if (missing.Count > 0)
		{
			error = "The following modular GLBs are missing or failed to import:\n" + string.Join("\n", missing);
			return false;
		}
		Dictionary<MechPartSlot, MechPartDefinition> internals = BuildInternalParts();
		Dictionary<string, MechPartDefinition> boosters = BuildBoosterParts(missing);
		Dictionary<string, MechPartDefinition> weapons = LoadWeaponParts(missing, paintPreset, materialCatalog);
		if (missing.Count > 0)
		{
			error = "The following equipment assets are missing:\n" + string.Join("\n", missing);
			return false;
		}
		Dictionary<string, MechLoadoutDefinition> loadouts = new Dictionary<string, MechLoadoutDefinition>(StringComparer.OrdinalIgnoreCase);
		CreateLoadout(loadouts, "Uniform1010", "Melander 1010", frameParts, internals, boosters, weapons, "1010", "1010", "1010", "1010");
		CreateLoadout(loadouts, "Uniform2010", "Nacht 2010", frameParts, internals, boosters, weapons, "2010", "2010", "2010", "2010");
		CreateLoadout(loadouts, "Uniform3010", "Tian-Qiang 3010", frameParts, internals, boosters, weapons, "3010", "3010", "3010", "3010");
		CreateLoadout(loadouts, "MixedA", "Mixed A 2010/1010/3010/2010", frameParts, internals, boosters, weapons, "2010", "1010", "3010", "2010", "WP_L_1410");
		CreateLoadout(loadouts, "MixedB", "Mixed B 3010/2010/1010/3010", frameParts, internals, boosters, weapons, "3010", "2010", "1010", "3010", "WP_L_1410");
		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
		result = new BuildResult(animationClip, frameParts, loadouts);
		return true;
	}

	private static bool TryLoadPaintPreset(string assetPath, out PaintPresetRecord preset, out string error)
	{
		preset = null;
		error = string.Empty;
		TextAsset text = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
		if (text == null)
		{
			error = "AC6 paint preset was not found:\n" + assetPath;
			return false;
		}
		preset = JsonUtility.FromJson<PaintPresetRecord>(text.text);
		if (preset == null || string.IsNullOrWhiteSpace(preset.baseColor))
		{
			error = "AC6 paint preset is invalid:\n" + assetPath;
			return false;
		}
		return true;
	}

	private static Material[] BuildPaintMaterialOverrides(GameObject model, string slotToken, string code, PaintPresetRecord preset, MaterialCatalogRecord catalog)
	{
		string modelPath = AssetDatabase.GetAssetPath(model);
		UnityEngine.Object[] source = AssetDatabase.LoadAllAssetsAtPath(modelPath);
		Material[] sourceMaterials = source.OfType<Material>().ToArray();
		Texture2D[] embeddedTextures = source.OfType<Texture2D>().ToArray();
		if (sourceMaterials.Length == 0)
		{
			Debug.LogWarning("[AC6Paint] No paintable material was found in " + modelPath + ".");
			return Array.Empty<Material>();
		}
		string maskPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/ColorMasks/" + slotToken + "_M_" + code + "_01_3m.png";
		ConfigureMaskImporter(maskPath);
		Texture2D mask = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
		if (mask == null)
		{
			Debug.LogWarning("[AC6Paint] Zone mask is missing: " + maskPath + ". Run Tools/ImportAC6FrameColorMasks.ps1, then rebuild the sample assets.");
		}
		Shader shader = Shader.Find("ACunicombat/AC6/Parts Approx");
		if (shader == null)
		{
			Debug.LogError("[AC6Paint] Shader ACunicombat/AC6/Parts Approx was not found.");
			return Array.Empty<Material>();
		}
		List<Material> overrides = new List<Material>();
		foreach (Material source2 in sourceMaterials)
		{
			string safeName = Regex.Replace(source2.name, "[^A-Za-z0-9_-]+", "_");
			string materialPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Materials/TestNavy/" + slotToken + "_M_" + code + "_" + safeName + ".mat";
			Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
			if (material == null)
			{
				material = new Material(shader);
				AssetDatabase.CreateAsset(material, materialPath);
			}
			else
			{
				material.shader = shader;
			}
			material.name = source2.name;
			MaterialProfileRecord profile = ResolveMaterialProfile(catalog, source2, slotToken, code);
			ConfigurePaintMaterial(material, source2, embeddedTextures, mask, preset, profile);
			EditorUtility.SetDirty(material);
			overrides.Add(material);
		}
		Debug.Log($"[AC6Paint] Built {overrides.Count}/{sourceMaterials.Length} opaque material overrides " + "for " + slotToken + "_M_" + code + ".");
		return overrides.ToArray();
	}

	private static Material[] BuildWeaponMaterialOverrides(GameObject model, string weaponCode, PaintPresetRecord preset, MaterialCatalogRecord catalog)
	{
		string modelPath = AssetDatabase.GetAssetPath(model);
		UnityEngine.Object[] source = AssetDatabase.LoadAllAssetsAtPath(modelPath);
		Material[] sourceMaterials = source.OfType<Material>().ToArray();
		Texture2D[] embeddedTextures = source.OfType<Texture2D>().ToArray();
		if (sourceMaterials.Length == 0)
		{
			Debug.LogWarning("[AC6Paint] No weapon material was found in " + modelPath + ".");
			return Array.Empty<Material>();
		}
		Shader shader = Shader.Find("ACunicombat/AC6/Parts Approx");
		if (shader == null)
		{
			Debug.LogError("[AC6Paint] Shader ACunicombat/AC6/Parts Approx was not found.");
			return Array.Empty<Material>();
		}
		List<Material> overrides = new List<Material>();
		foreach (Material source2 in sourceMaterials)
		{
			string safeName = Regex.Replace(source2.name, "[^A-Za-z0-9_-]+", "_");
			string materialPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Materials/TestNavy/WP_" + weaponCode + "_" + safeName + ".mat";
			Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
			if (material == null)
			{
				material = new Material(shader);
				AssetDatabase.CreateAsset(material, materialPath);
			}
			else
			{
				material.shader = shader;
			}
			material.name = source2.name;
			MaterialProfileRecord profile = ResolveMaterialProfile(catalog, source2, "WP", weaponCode);
			Texture2D mask = LoadCatalogMask(profile?.mask3Map);
			ConfigurePaintMaterial(material, source2, embeddedTextures, mask, preset, profile, ResolveWeaponTint(preset, source2, profile));
			EditorUtility.SetDirty(material);
			overrides.Add(material);
		}
		Debug.Log($"[AC6Paint] Built {overrides.Count}/{sourceMaterials.Length} weapon material overrides " + "for WP_" + weaponCode + ".");
		return overrides.ToArray();
	}

	private static Texture2D LoadCatalogMask(string catalogPath)
	{
		if (string.IsNullOrWhiteSpace(catalogPath))
		{
			return null;
		}
		string maskName = Path.GetFileNameWithoutExtension(catalogPath);
		string maskPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/ColorMasks/" + maskName + ".png";
		ConfigureMaskImporter(maskPath);
		Texture2D texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
		if (texture2D == null)
		{
			Debug.LogWarning("[AC6Paint] Catalog zone mask is missing: " + maskPath + ". Run Tools/ImportAC6FrameColorMasks.ps1, then rebuild the sample assets.");
		}
		return texture2D;
	}

	private static bool IsPaintSurface(Material material)
	{
		if (material == null)
		{
			return false;
		}
		string name = material.name;
		if (name.IndexOf("EYE", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Prats", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Limiter", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Lmiter", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Marking", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return name.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) < 0;
		}
		return false;
	}

	private static void ConfigureMaskImporter(string assetPath)
	{
		TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
		if (!(importer == null))
		{
			bool num = !importer.sRGBTexture || !importer.mipmapEnabled || importer.textureType != TextureImporterType.Default;
			importer.sRGBTexture = true;
			importer.mipmapEnabled = true;
			importer.textureType = TextureImporterType.Default;
			if (num)
			{
				importer.SaveAndReimport();
			}
		}
	}

	private static void ConfigurePaintMaterial(Material destination, Material source, IReadOnlyList<Texture2D> embeddedTextures, Texture2D mask, PaintPresetRecord preset, MaterialProfileRecord profile, Color? unmaskedPaintTint = null)
	{
		bool isPaintSurface = ((profile != null) ? string.Equals(profile.surfaceClass, "PaintedArmor", StringComparison.Ordinal) : IsPaintSurface(source));
		bool isEmissionSurface = ((profile != null) ? string.Equals(profile.surfaceClass, "Emissive", StringComparison.Ordinal) : IsEmissionSurface(source));
		Texture baseMap = FindTexture(source, "_BaseColorTexture", "_BaseMap", "_MainTex") ?? FindEmbeddedTextureByCatalogPath(embeddedTextures, profile?.albedoMap);
		if (baseMap == null && !IsMarkingSurface(source))
		{
			baseMap = FindEmbeddedTexture(embeddedTextures, "_a");
		}
		Texture normalMap = FindTexture(source, "_NormalTexture", "_BumpMap", "_NormalMap") ?? FindEmbeddedTextureByCatalogPath(embeddedTextures, profile?.normalMap) ?? FindEmbeddedTexture(embeddedTextures, "_n");
		Texture emissionMap = FindTexture(source, "_EmissiveTexture", "_EmissionMap") ?? FindEmbeddedTextureByCatalogPath(embeddedTextures, profile?.emissionMap) ?? FindEmbeddedTexture(embeddedTextures, "_em");
		destination.SetTexture("_BaseMap", baseMap);
		destination.SetTexture("_BumpMap", normalMap);
		destination.SetTexture("_Mask3Map", mask);
		destination.SetTexture("_EmissionMap", emissionMap);
		Texture2D weatheringMask = AssetDatabase.LoadAssetAtPath<Texture2D>(WeatheringMaskAssetPath);
		destination.SetTexture("_WeatheringMaskMap", weatheringMask);
		destination.SetFloat("_UseWeatheringMask", weatheringMask != null ? 1f : 0f);
		bool hasZoneMask = isPaintSurface && mask != null;
		destination.SetColor("_BaseColor", (!isPaintSurface || hasZoneMask) ? Color.white : (unmaskedPaintTint ?? ParseHtmlColor(preset.baseColor, Color.white)));
		destination.SetFloat("_UseSourceAlbedo", hasZoneMask ? 0f : 1f);
		destination.SetFloat("_ZoneMode", hasZoneMask ? 1f : 0f);
		destination.SetFloat("_ZoneMaskStrength", hasZoneMask ? 1f : 0f);
		Color[] zoneColors = ResolveZoneColors(preset);
		for (int zoneIndex = 0; zoneIndex < zoneColors.Length; zoneIndex++)
		{
			destination.SetColor($"_Zone{zoneIndex + 1}Color", zoneColors[zoneIndex]);
		}
		destination.SetFloat("_AlbedoDetailStrength", isPaintSurface ? 0.32f : 0.40f);
		destination.SetFloat("_Metallic", profile?.recommendedMetallic ?? 0.12f);
		destination.SetFloat("_Smoothness", profile?.recommendedSmoothness ?? 0.3f);
		destination.SetFloat("_BumpScale", 0.8f);
		destination.SetFloat("_OcclusionStrength", 0f);
		destination.SetFloat("_MaskMetallicInfluence", 0f);
		destination.SetFloat("_MaskSmoothnessInfluence", 0f);
		destination.SetFloat("_PaintInfluence", 0f);
		destination.renderQueue = 2000;
		destination.SetOverrideTag("RenderType", "Opaque");
		if (normalMap != null)
		{
			destination.EnableKeyword("_NORMALMAP");
		}
		else
		{
			destination.DisableKeyword("_NORMALMAP");
		}
		if (emissionMap != null && isEmissionSurface)
		{
			destination.SetColor("_EmissionColor", Color.white);
			destination.SetFloat("_EmissionStrength", 2f);
			destination.EnableKeyword("_EMISSION");
		}
		else
		{
			destination.SetColor("_EmissionColor", Color.black);
			destination.SetFloat("_EmissionStrength", 0f);
			destination.DisableKeyword("_EMISSION");
		}
	}

	private static Color ResolveWeaponTint(PaintPresetRecord preset, Material source, MaterialProfileRecord profile)
	{
		if (profile != null && string.Equals(profile.surfaceClass, "Emissive", StringComparison.Ordinal))
		{
			return Color.white;
		}
		string name = ((source != null) ? (source.name ?? string.Empty) : string.Empty);
		if (name.IndexOf("Mag", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_02", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Material #", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			return ParseHtmlColor(preset.supportColor, Color.gray);
		}
		return ParseHtmlColor(preset.baseColor, Color.white);
	}

	private static bool TryLoadMaterialCatalog(string assetPath, out MaterialCatalogRecord catalog, out string error)
	{
		catalog = null;
		error = string.Empty;
		TextAsset text = AssetDatabase.LoadAssetAtPath<TextAsset>(assetPath);
		if (text == null)
		{
			error = "AC6 MATBIN material catalog was not found:\n" + assetPath + "\n\nRun Tools/ExportAC6MaterialCatalog.ps1, then refresh Unity.";
			return false;
		}
		catalog = JsonUtility.FromJson<MaterialCatalogRecord>(text.text);
		if (catalog == null || catalog.materials == null || catalog.materials.Length == 0)
		{
			error = "AC6 MATBIN material catalog is invalid or empty:\n" + assetPath;
			return false;
		}
		return true;
	}

	private static MaterialProfileRecord ResolveMaterialProfile(MaterialCatalogRecord catalog, Material source, string slotToken, string code)
	{
		if (catalog == null || catalog.materials == null || source == null)
		{
			return null;
		}
		string sourceName = source.name ?? string.Empty;
		bool isMarking = IsMarkingSurface(source);
		bool isEmission = IsEmissionSurface(source);
		string desiredClass = (isMarking ? "Opaque" : (isEmission ? "Emissive" : "PaintedArmor"));
		string partToken = slotToken + "_M_" + code;
		if (isMarking)
		{
			partToken = "Prats_Marking";
		}
		MaterialProfileRecord[] candidates = catalog.materials.Where((MaterialProfileRecord candidate) => candidate != null && !string.IsNullOrWhiteSpace(candidate.sourcePath) && candidate.sourcePath.IndexOf(partToken, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
		if (candidates.Length == 0)
		{
			return null;
		}
		string normalizedSourceName = Regex.Replace(sourceName, "[^A-Za-z0-9_]+", string.Empty);
		MaterialProfileRecord exactCandidate = candidates.FirstOrDefault(delegate(MaterialProfileRecord candidate)
		{
			string value = Regex.Replace((Path.GetFileNameWithoutExtension(candidate.sourcePath) ?? string.Empty).Replace("P[", string.Empty).Replace("]", string.Empty), "[^A-Za-z0-9_]+", string.Empty);
			return !string.IsNullOrEmpty(value) && normalizedSourceName.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
		});
		if (exactCandidate != null)
		{
			return exactCandidate;
		}
		if (slotToken.Equals("AM", StringComparison.OrdinalIgnoreCase))
		{
			string sideToken = ((sourceName.IndexOf("#30#", StringComparison.OrdinalIgnoreCase) >= 0 || sourceName.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0) ? "_left" : ((sourceName.IndexOf("#40#", StringComparison.OrdinalIgnoreCase) >= 0 || sourceName.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0) ? "_right" : string.Empty));
			if (!string.IsNullOrEmpty(sideToken))
			{
				MaterialProfileRecord sideCandidate = candidates.FirstOrDefault((MaterialProfileRecord candidate) => candidate.sourcePath.IndexOf(sideToken, StringComparison.OrdinalIgnoreCase) >= 0 && string.Equals(candidate.surfaceClass, desiredClass, StringComparison.Ordinal));
				if (sideCandidate != null)
				{
					return sideCandidate;
				}
			}
		}
		if (sourceName.IndexOf("Limiter", StringComparison.OrdinalIgnoreCase) >= 0 || sourceName.IndexOf("Lmiter", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			MaterialProfileRecord limiter = candidates.FirstOrDefault((MaterialProfileRecord candidate) => candidate.sourcePath.IndexOf("Limiter", StringComparison.OrdinalIgnoreCase) >= 0);
			if (limiter != null)
			{
				return limiter;
			}
		}
		return candidates.FirstOrDefault((MaterialProfileRecord candidate) => string.Equals(candidate.surfaceClass, desiredClass, StringComparison.Ordinal)) ?? candidates[0];
	}

	private static bool IsMarkingSurface(Material material)
	{
		if (material == null)
		{
			return false;
		}
		string name = material.name;
		if (name.IndexOf("Prats", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return name.IndexOf("Marking", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return true;
	}

	private static bool IsEmissionSurface(Material material)
	{
		if (material == null)
		{
			return false;
		}
		string name = material.name;
		if (name.IndexOf("EYE", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Limiter", StringComparison.OrdinalIgnoreCase) < 0 && name.IndexOf("Lmiter", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return name.IndexOf("Emissive", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return true;
	}

	private static Texture2D FindEmbeddedTexture(IReadOnlyList<Texture2D> textures, string suffix)
	{
		if (textures == null || string.IsNullOrWhiteSpace(suffix))
		{
			return null;
		}
		for (int i = 0; i < textures.Count; i++)
		{
			Texture2D texture = textures[i];
			if (texture != null && texture.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return texture;
			}
		}
		return null;
	}

	private static Texture2D FindEmbeddedTextureByCatalogPath(IReadOnlyList<Texture2D> textures, string catalogPath)
	{
		if (textures == null || string.IsNullOrWhiteSpace(catalogPath))
		{
			return null;
		}
		string expectedName = Path.GetFileNameWithoutExtension(catalogPath);
		for (int i = 0; i < textures.Count; i++)
		{
			Texture2D texture = textures[i];
			if (texture != null && string.Equals(texture.name, expectedName, StringComparison.OrdinalIgnoreCase))
			{
				return texture;
			}
		}
		return null;
	}

	private static Texture FindTexture(Material material, params string[] propertyNames)
	{
		foreach (string propertyName in propertyNames)
		{
			if (material.HasProperty(propertyName))
			{
				Texture texture = material.GetTexture(propertyName);
				if (texture != null)
				{
					return texture;
				}
			}
		}
		return null;
	}

	private static Color ParseHtmlColor(string html, Color fallback)
	{
		if (!ColorUtility.TryParseHtmlString(html, out var color))
		{
			return fallback;
		}
		return color;
	}

	private static Color[] ResolveZoneColors(PaintPresetRecord preset)
	{
		Color[] fallbacks = new Color[7]
		{
			ParseHtmlColor(preset.baseColor, new Color(0.14f, 0.36f, 0.66f, 1f)),
			ParseHtmlColor(preset.subColor, new Color(0.15f, 0.17f, 0.21f, 1f)),
			ParseHtmlColor(preset.supportColor, new Color(0.35f, 0.49f, 0.67f, 1f)),
			ParseHtmlColor(preset.optionalColor, new Color(0.09f, 0.19f, 0.32f, 1f)),
			ParseHtmlColor(preset.deviceColor, new Color(0.42f, 0.47f, 0.52f, 1f)),
			new Color(0.19f, 0.22f, 0.27f, 1f),
			new Color(0.04f, 0.055f, 0.08f, 1f)
		};
		if (preset.zoneColors == null || preset.zoneColors.Length != fallbacks.Length)
		{
			return fallbacks;
		}
		Color[] resolved = new Color[fallbacks.Length];
		for (int i = 0; i < resolved.Length; i++)
		{
			resolved[i] = ParseHtmlColor(preset.zoneColors[i], fallbacks[i]);
		}
		return resolved;
	}

	private static void BuildValidationSceneAndRunChecks(BuildResult result, ValidationReport report, bool batchMode)
	{
		string animationAssetPath = AssetDatabase.GetAssetPath(result.AnimationClip);
		string animationClipName = result.AnimationClip.name;
		List<KeyValuePair<string, string>> loadoutPaths = result.Loadouts.Select((KeyValuePair<string, MechLoadoutDefinition> keyValuePair) => new KeyValuePair<string, string>(keyValuePair.Key, AssetDatabase.GetAssetPath(keyValuePair.Value))).OrderBy((KeyValuePair<string, string> keyValuePair) => keyValuePair.Key, StringComparer.Ordinal).ToList();
		Scene previousActiveScene = SceneManager.GetActiveScene();
		bool isOpenValidationScene = !batchMode && string.Equals(previousActiveScene.path, "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity", StringComparison.OrdinalIgnoreCase);
		bool isPreviewScene = !batchMode && !isOpenValidationScene;
		Scene validationScene;
		if (batchMode)
		{
			if (string.IsNullOrWhiteSpace(previousActiveScene.path))
			{
				EditorSceneManager.SaveScene(previousActiveScene, "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/BatchBootstrap.unity");
			}
			validationScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
			SceneManager.SetActiveScene(validationScene);
		}
		else if (isOpenValidationScene)
		{
			validationScene = previousActiveScene;
			GameObject[] oldRoots = validationScene.GetRootGameObjects();
			for (int i = 0; i < oldRoots.Length; i++)
			{
				UnityEngine.Object.DestroyImmediate(oldRoots[i]);
			}
		}
		else
		{
			validationScene = EditorSceneManager.NewPreviewScene();
		}
		GameObject validationRoot = new GameObject("AC6_Skinned_Modular_Validation");
		if (isPreviewScene)
		{
			SceneManager.MoveGameObjectToScene(validationRoot, validationScene);
		}
		List<GameObject> mechs = new List<GameObject>();
		AnimationClip animationClip = AssetDatabase.LoadAllAssetsAtPath(animationAssetPath).OfType<AnimationClip>().FirstOrDefault((AnimationClip candidate) => candidate.name == animationClipName);
		if (animationClip == null)
		{
			throw new InvalidOperationException("Animation clip '" + animationClipName + "' could not be reloaded from " + animationAssetPath + ".");
		}
		for (int i2 = 0; i2 < loadoutPaths.Count; i2++)
		{
			KeyValuePair<string, string> pair = loadoutPaths[i2];
			MechLoadoutDefinition loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(pair.Value);
			if (loadout == null)
			{
				throw new InvalidOperationException("Loadout '" + pair.Key + "' could not be reloaded from " + pair.Value + ".");
			}
			GameObject mech = new GameObject("Validation_" + pair.Key);
			mech.transform.SetParent(validationRoot.transform, worldPositionStays: false);
			mech.transform.position = new Vector3(((float)i2 - (float)(loadoutPaths.Count - 1) * 0.5f) * 14f, 0f, 0f);
			MechAssembler mechAssembler = mech.AddComponent<MechAssembler>();
			mechAssembler.Loadout = loadout;
			mechAssembler.EditorConfigureSynchronizedSkinnedParts(animationClip);
			mechAssembler.Assemble();
			GameObject gameObject = new GameObject("Label");
			gameObject.transform.SetParent(mech.transform, worldPositionStays: false);
			gameObject.transform.localPosition = new Vector3(0f, 7.2f, 0f);
			TextMesh textMesh = gameObject.AddComponent<TextMesh>();
			textMesh.text = loadout.DisplayName;
			textMesh.anchor = TextAnchor.MiddleCenter;
			textMesh.alignment = TextAlignment.Center;
			textMesh.characterSize = 0.25f;
			textMesh.fontSize = 36;
			mechs.Add(mech);
			ValidateMech(pair.Key, mech, animationClip, report);
		}
		CreateSceneLighting(validationRoot.transform, mechs);
		if (batchMode || isOpenValidationScene)
		{
			for (int i3 = 0; i3 < mechs.Count; i3++)
			{
				SampleValidationPose(mechs[i3], animationClip, 0f);
			}
			EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes");
			EditorSceneManager.SaveScene(validationScene, "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6SkinnedModularValidation.unity");
		}
		for (int i4 = 0; i4 < mechs.Count; i4++)
		{
			for (int j = 0; j < mechs.Count; j++)
			{
				mechs[j].SetActive(i4 == j);
			}
			Transform label = mechs[i4].transform.Find("Label");
			if (label != null)
			{
				label.gameObject.SetActive(value: false);
			}
			Bounds bounds = CombinedBounds(mechs[i4]);
			SampleValidationPose(mechs[i4], animationClip, 0f);
			bounds = CombinedBounds(mechs[i4]);
			Screenshot(mechs[i4], bounds, Path.Combine("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports", mechs[i4].name + "_rest.png"));
			SampleValidationPose(mechs[i4], animationClip, 0.5f);
			bounds = CombinedBounds(mechs[i4]);
			Screenshot(mechs[i4], bounds, Path.Combine("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports", mechs[i4].name + "_action.png"));
			if (label != null)
			{
				label.gameObject.SetActive(value: true);
			}
		}
		for (int i5 = 0; i5 < mechs.Count; i5++)
		{
			mechs[i5].SetActive(value: true);
		}
		if (isPreviewScene && previousActiveScene.IsValid() && previousActiveScene.isLoaded)
		{
			SceneManager.SetActiveScene(previousActiveScene);
		}
		if (isPreviewScene)
		{
			EditorSceneManager.ClosePreviewScene(validationScene);
		}
	}

	private static void ValidateMech(string key, GameObject mech, AnimationClip clip, ValidationReport report)
	{
		MechRuntimeState runtime = mech.GetComponent<MechRuntimeState>();
		MechSynchronizedAnimationPlayer player = mech.GetComponent<MechSynchronizedAnimationPlayer>();
		List<MechAssemblyRecord> frameRecords = ((runtime == null) ? new List<MechAssemblyRecord>() : runtime.AssembledParts.Where((MechAssemblyRecord mechAssemblyRecord) => mechAssemblyRecord?.part != null && mechAssemblyRecord.part.UsesSynchronizedSkinnedRig).ToList());
		List<MechAssemblyRecord> equipmentRecords = ((runtime == null) ? new List<MechAssemblyRecord>() : runtime.AssembledParts.Where((MechAssemblyRecord mechAssemblyRecord) => mechAssemblyRecord?.part != null && (mechAssemblyRecord.slot == MechPartSlot.Booster || MechSlotRules.IsWeaponSlot(mechAssemblyRecord.slot))).ToList());
		List<MechAssemblyRecord> weaponRecords = equipmentRecords.Where((MechAssemblyRecord mechAssemblyRecord) => MechSlotRules.IsWeaponSlot(mechAssemblyRecord.slot)).ToList();
		MechAssemblyRecord boosterRecord = equipmentRecords.FirstOrDefault((MechAssemblyRecord mechAssemblyRecord) => mechAssemblyRecord.slot == MechPartSlot.Booster);
		MechAssemblyRecord coreRecord = frameRecords.FirstOrDefault((MechAssemblyRecord mechAssemblyRecord) => mechAssemblyRecord.slot == MechPartSlot.Core);
		bool ok = true;
		ok &= Check(frameRecords.Count == 4, key, $"expected 4 skinned frame records, got {frameRecords.Count}", report);
		ok &= Check(runtime != null && runtime.LoadoutValid, key, "assembled equipment loadout is invalid", report);
		ok &= Check(boosterRecord?.part?.ModelPrefab != null, key, "visible booster model is missing", report);
		ok &= Check(weaponRecords.Count == 4, key, $"expected 4 mounted weapons, got {weaponRecords.Count}", report);
		List<MechMountPoint> boosterMounts = ((runtime == null) ? new List<MechMountPoint>() : runtime.MountPoints.Where((MechMountPoint mount) => mount != null && mount.TargetSlot == MechPartSlot.Booster).ToList());
		ok &= Check(boosterMounts.Count == 2, key, $"expected 2 rear booster mounts, got {boosterMounts.Count}", report);
		for (int i = 0; i < boosterMounts.Count; i++)
		{
			Transform source = boosterMounts[i].transform.parent;
			ok &= Check(source != null && source.name.IndexOf("_dmy_800", StringComparison.OrdinalIgnoreCase) >= 0, key, $"booster mount {i} is not attached to a _dmy_800 node", report);
			float sourceLocalZ = ((source != null && coreRecord?.instance != null) ? coreRecord.instance.transform.InverseTransformPoint(source.position).z : float.MinValue);
			ok &= Check(sourceLocalZ > 0.001f, key, $"booster mount {i} uses a front/effect-only ref 800 at local z={sourceLocalZ:0.###}", report);
		}
		List<Transform> boosterVisuals = (from candidate in mech.GetComponentsInChildren<Transform>(includeInactive: true)
			where candidate != null && candidate.name.StartsWith("Booster_", StringComparison.Ordinal) && candidate.GetComponentInChildren<Renderer>(includeInactive: true) != null
			select candidate).ToList();
		ok &= Check(boosterVisuals.Count == 2, key, $"expected 2 visible rear booster instances, got {boosterVisuals.Count}", report);
		float boosterInstallRotationError = ((boosterRecord?.part == null) ? float.MaxValue : Quaternion.Angle(Quaternion.Euler(boosterRecord.part.LocalEulerAngles), Quaternion.Euler(NativeBoosterInstallEuler)));
		ok &= Check(boosterInstallRotationError < 0.05f, key, string.Format("booster install rotation is {0}; expected {1}", boosterRecord?.part?.LocalEulerAngles.ToString() ?? "<none>", NativeBoosterInstallEuler), report);
		for (int i2 = 0; i2 < boosterVisuals.Count; i2++)
		{
			Transform visual = boosterVisuals[i2];
			Transform socket = ((visual.parent != null) ? visual.parent.parent : null);
			Transform flame = FindChildBySuffix(visual, "_dmy_31");
			float rootError = ((socket != null) ? Vector3.Distance(visual.position, socket.position) : float.MaxValue);
			float rotationError = Quaternion.Angle(visual.localRotation, Quaternion.Euler(NativeBoosterInstallEuler));
			float scaleError = (visual.localScale - Vector3.one).magnitude;
			float flameError = ((flame != null && socket != null) ? Vector3.Distance(flame.position, socket.position) : float.MaxValue);
			report.Log.AppendLine($"[{key}] booster {i2}: rootToSocket={rootError:0.####}m flameFromSocket={flameError:0.####}m rootOffset={Format(visual.localPosition)} drot={rotationError:0.###}deg dscale={scaleError:0.####}");
			ok &= Check(flame != null, key, $"booster visual {i2} has no _dmy_31 flame anchor", report);
			ok &= Check(rootError < 0.005f, key, $"booster visual {i2} root is {rootError:0.####}m away from its ref 800 socket", report);
			ok &= Check(rotationError < 0.05f, key, $"booster visual {i2} root rotation error is {rotationError:0.###} degrees", report);
			ok &= Check(scaleError < 0.0001f, key, $"booster visual {i2} root scale error is {scaleError:0.####}", report);
			if (flame != null)
			{
				Vector3 exhaustDir = flame.forward.normalized;
				float backDot = Vector3.Dot(exhaustDir, mech.transform.forward);
				float pitchDeg = Mathf.Asin(Mathf.Clamp(exhaustDir.y, -1f, 1f)) * 57.29578f;
				report.Log.AppendLine($"[{key}] booster {i2}: exhaustDir={Format(exhaustDir)} backDot={backDot:0.###} pitchDeg={pitchDeg:0.##}");
				ok &= Check(backDot > 0.6f, key, $"booster visual {i2} exhaust axis is not pointing backward (backDot {backDot:0.###})", report);
				ok &= Check(exhaustDir.y <= 0.1f, key, $"booster visual {i2} exhaust axis points upward (y {exhaustDir.y:0.###})", report);
				if (boosterRecord?.part != null && boosterRecord.part.EquipmentAnimations.Any((MechEquipmentAnimationDefinition mechEquipmentAnimationDefinition) => string.Equals(mechEquipmentAnimationDefinition.ActionId, "a000_000001", StringComparison.OrdinalIgnoreCase)))
				{
					ok &= Check(pitchDeg >= -35f && pitchDeg <= -5f, key, $"booster visual {i2} display-pose pitch is {pitchDeg:0.##} degrees; expected a rear-down angle", report);
				}
			}
		}
		for (int i3 = 0; i3 < weaponRecords.Count; i3++)
		{
			MechAssemblyRecord record = weaponRecords[i3];
			MechMountPoint mountPoint = ((record.parentMount != null) ? record.parentMount.GetComponent<MechMountPoint>() : null);
			Transform mountSource = ((record.parentMount != null) ? record.parentMount.parent : null);
			string expectedMountSuffix = ExpectedEquipmentMountSuffix(record.slot, record.part);
			string modelPath = ((record.part == null) ? string.Empty : AssetDatabase.GetAssetPath(record.part.ModelPrefab));
			ok &= Check(record.instance != null && record.instance.GetComponentInChildren<Renderer>(includeInactive: true) != null, key, $"{record.slot} has no visible renderer", report);
			ok &= Check(mountPoint != null && mountPoint.TargetSlot == record.slot, key, $"{record.slot} is not attached to its animated hardpoint", report);
			ok &= Check(mountSource != null && mountSource.name.EndsWith(expectedMountSuffix, StringComparison.OrdinalIgnoreCase), key, string.Format("{0} expected mount {1}, got {2}", record.slot, expectedMountSuffix, mountSource?.name ?? "<none>"), report);
			ok &= Check(modelPath.StartsWith("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment", StringComparison.OrdinalIgnoreCase), key, $"{record.slot} still references a legacy or Blender-baked asset: {modelPath}", report);
			float installRotationError = ((record.part == null) ? float.MaxValue : Quaternion.Angle(Quaternion.Euler(record.part.LocalEulerAngles), Quaternion.Euler(NativeWeaponInstallEuler)));
			ok &= Check(installRotationError < 0.05f, key, string.Format("{0} native weapon install rotation is {1}; expected {2}", record.slot, record.part?.LocalEulerAngles.ToString() ?? "<none>", NativeWeaponInstallEuler), report);
			if (record.slot == MechPartSlot.LeftHandWeapon || record.slot == MechPartSlot.RightHandWeapon)
			{
				float gripDistance = ((mountSource != null && record.instance != null) ? MinDistanceToWeaponSurface(record.instance, mountSource.position) : float.MaxValue);
				report.Log.AppendLine($"[{key}] {record.slot} gripDistance={gripDistance:0.####}m");
				ok &= Check(gripDistance < 0.15f, key, $"{record.slot} grip point is {gripDistance:0.###}m away from the weapon surface", report);
			}
			int expectedActionCount = ExpectedNativeActionCount(record.part.PartId);
			ok &= Check(expectedActionCount < 0 || record.part.EquipmentAnimations.Count == expectedActionCount, key, $"{record.slot} expected {expectedActionCount} native actions, got {record.part.EquipmentAnimations.Count}", report);
			MechEquipmentAnimationPlayer equipmentPlayer = record.instance.GetComponent<MechEquipmentAnimationPlayer>();
			ok &= Check((record.part.EquipmentAnimations.Count == 0) ? (equipmentPlayer == null) : (equipmentPlayer != null), key, $"{record.slot} independent equipment animation player state is incorrect", report);
			if (record.part.HasTag("melee"))
			{
				ok &= Check(record.part.EquipmentAnimations.Count == 0 || string.Equals(record.part.DefaultEquipmentActionId, "a000_000000", StringComparison.OrdinalIgnoreCase), key, $"{record.slot} melee default must be native idle a000_000000, got {record.part.DefaultEquipmentActionId}", report);
				ok &= Check(!record.part.PlayDefaultEquipmentActionOnAssemble, key, $"{record.slot} melee attack must not autoplay on assembly", report);
			}
		}
		string boosterModelPath = ((boosterRecord?.part == null) ? string.Empty : AssetDatabase.GetAssetPath(boosterRecord.part.ModelPrefab));
		ok &= Check(boosterModelPath.StartsWith("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment", StringComparison.OrdinalIgnoreCase), key, "booster still references a legacy or Blender-baked asset: " + boosterModelPath, report);
		int expectedBoosterActions = ExpectedNativeActionCount(boosterRecord?.part?.PartId);
		ok &= Check(expectedBoosterActions < 0 || (boosterRecord != null && boosterRecord.part?.EquipmentAnimations.Count == expectedBoosterActions), key, $"booster expected {expectedBoosterActions} native actions, got {boosterRecord?.part?.EquipmentAnimations.Count ?? (-1)}", report);
		ok &= Check(boosterRecord?.part != null && (boosterRecord.part.EquipmentAnimations.Count == 0 || string.Equals(boosterRecord.part.DefaultEquipmentActionId, "a000_000001", StringComparison.OrdinalIgnoreCase)), key, "booster default must be native mounted display a000_000001, got " + (boosterRecord?.part?.DefaultEquipmentActionId ?? "<none>"), report);
		ok &= Check(boosterRecord?.part != null && !boosterRecord.part.PlayDefaultEquipmentActionOnAssemble, key, "booster thrust/gimbal action must not autoplay on assembly", report);
		int expectedPlayers = weaponRecords.Count((MechAssemblyRecord mechAssemblyRecord) => mechAssemblyRecord.part.EquipmentAnimations.Count > 0) + ((boosterRecord != null && boosterRecord.part.EquipmentAnimations.Count > 0) ? boosterVisuals.Count : 0);
		MechEquipmentAnimationPlayer[] equipmentPlayers = mech.GetComponentsInChildren<MechEquipmentAnimationPlayer>(includeInactive: true);
		ok &= Check(equipmentPlayers.Length == expectedPlayers, key, $"expected {expectedPlayers} independent native equipment players, got {equipmentPlayers.Length}", report);
		foreach (MechEquipmentAnimationPlayer equipmentPlayer2 in equipmentPlayers)
		{
			ok &= Check(equipmentPlayer2.Actions.Count > 0, key, equipmentPlayer2.name + " has an empty native action list", report);
			if (equipmentPlayer2.Actions.Count > 0)
			{
				MechEquipmentAnimationDefinition action = equipmentPlayer2.Actions.FirstOrDefault((MechEquipmentAnimationDefinition candidate) => string.Equals(candidate.ActionId, equipmentPlayer2.DefaultActionId, StringComparison.OrdinalIgnoreCase)) ?? equipmentPlayer2.Actions[0];
				ok &= Check(equipmentPlayer2.SampleActionAtTime(action.ActionId, action.Clip.length * 0.5f), key, equipmentPlayer2.name + " failed to sample native action " + action.ActionId, report);
			}
		}
		ok &= Check(player != null, key, "synchronized animation player is missing", report);
		if (player != null)
		{
			ok &= Check(player.PartAnimators.Count == 4, key, $"expected 4 synchronized Animators, got {player.PartAnimators.Count}", report);
			ok &= Check(player.PartClips.Count == 4, key, $"expected 4 part-specific clips, got {player.PartClips.Count}", report);
			ok &= Check(player.SharedBoneBindingCount == 34, key, $"expected exactly 34 interface-driven shared bone links, got {player.SharedBoneBindingCount}", report);
		}
		HashSet<Transform> skeletonRoots = new HashSet<Transform>();
		for (int i5 = 0; i5 < frameRecords.Count; i5++)
		{
			MechAssemblyRecord record2 = frameRecords[i5];
			SkinnedMeshRenderer[] renderers = record2.instance.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true).Where(delegate(SkinnedMeshRenderer skinnedMeshRenderer)
			{
				MechAssembledPart componentInParent = skinnedMeshRenderer.GetComponentInParent<MechAssembledPart>();
				return skinnedMeshRenderer.GetComponentInParent<MechMountPoint>() == null && componentInParent != null && componentInParent.gameObject == record2.instance;
			}).ToArray();
			ok &= Check(renderers.Length != 0, key, $"{record2.slot} has no SkinnedMeshRenderer", report);
			foreach (SkinnedMeshRenderer renderer in renderers)
			{
				ok &= Check(renderer.bones.Length != 0, key, $"{record2.slot}/{renderer.name} has no bones", report);
				ok &= Check(renderer.bones.All((Transform bone) => bone != null), key, $"{record2.slot}/{renderer.name} has null bone references", report);
				if (renderer.rootBone != null)
				{
					skeletonRoots.Add(renderer.rootBone);
				}
			}
		}
		ok &= Check(skeletonRoots.Count == 4, key, $"expected 4 independent part skeleton roots, got {skeletonRoots.Count}", report);
		if (string.Equals(key, "Uniform1010", StringComparison.OrdinalIgnoreCase))
		{
			ok &= WithRawAssetInstance("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf/AM_M_1010.glb", key, report, (GameObject instance) => CheckGoldenSocket(instance, "_dmy_20", new Vector3(-2.271f, 7.906f, -1.971f), key, report) & CheckGoldenSocket(instance, "_dmy_23", new Vector3(2.27f, 7.904f, -1.974f), key, report) & CheckGoldenSocket(instance, "_dmy_25", new Vector3(2.895f, 7.655f, -0.746f), key, report) & CheckAnatomicalSocketSide(instance, "_dmy_20", expectRight: true, key, report) & CheckAnatomicalSocketSide(instance, "_dmy_23", expectRight: false, key, report) & CheckAnatomicalSocketSide(instance, "_dmy_25", expectRight: false, key, report));
			ok &= WithRawAssetInstance("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf/BD_M_1010.glb", key, report, (GameObject instance) => CheckGoldenSocket(instance, "_dmy_13", new Vector3(1.458f, 10.013f, 1.856f), key, report) & CheckGoldenSocket(instance, "_dmy_14", new Vector3(-1.458f, 10.013f, 1.856f), key, report) & CheckGoldenSocketSet(instance, "_dmy_800", new Vector3[3]
			{
				new Vector3(0f, 8.622f, -2.225f),
				new Vector3(0.413f, 8.944f, 2.402f),
				new Vector3(-0.413f, 8.944f, 2.402f)
			}, key, report));
		}
		if (string.Equals(key, "Uniform2010", StringComparison.OrdinalIgnoreCase))
		{
			ok &= WithRawAssetInstance("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf/BD_M_2010.glb", key, report, (GameObject instance) => CheckGoldenSocket(instance, "_dmy_13", new Vector3(1.525f, 10.309f, 1.938f), key, report) & CheckGoldenSocket(instance, "_dmy_14", new Vector3(-1.525f, 10.309f, 1.938f), key, report) & CheckGoldenSocketSet(instance, "_dmy_800", new Vector3[3]
			{
				new Vector3(0f, 8.763f, -2.225f),
				new Vector3(0.459f, 9.155f, 2.469f),
				new Vector3(-0.459f, 9.155f, 2.469f)
			}, key, report));
		}
		if (string.Equals(key, "Uniform3010", StringComparison.OrdinalIgnoreCase))
		{
			ok &= WithRawAssetInstance("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/Gltf/BD_M_3010.glb", key, report, (GameObject instance) => CheckGoldenSocket(instance, "_dmy_13", new Vector3(1.18f, 10.747f, 1.798f), key, report) & CheckGoldenSocket(instance, "_dmy_14", new Vector3(-1.18f, 10.747f, 1.798f), key, report) & CheckGoldenSocketSet(instance, "_dmy_800", new Vector3[3]
			{
				new Vector3(0f, 9.081f, -2.225f),
				new Vector3(0.459f, 9.472f, 2.469f),
				new Vector3(-0.459f, 9.472f, 2.469f)
			}, key, report));
		}
		Dictionary<MechAssemblyRecord, Matrix4x4> installBaselines = new Dictionary<MechAssemblyRecord, Matrix4x4>();
		foreach (MechAssemblyRecord record3 in equipmentRecords)
		{
			if (!(record3.instance == null) && !(record3.parentMount == null) && !(record3.parentMount.parent == null))
			{
				installBaselines[record3] = record3.parentMount.parent.worldToLocalMatrix * record3.instance.transform.localToWorldMatrix;
			}
		}
		MechActionEffectTimeline effectTimeline = LoadOrCreateEffectTimeline("a000_010900");
		MechActionEffectPlayer effectPlayer = null;
		if (effectTimeline != null && runtime != null && player != null)
		{
			effectPlayer = mech.GetComponent<MechActionEffectPlayer>();
			if (effectPlayer == null)
			{
				effectPlayer = mech.AddComponent<MechActionEffectPlayer>();
			}
			effectPlayer.Configure(effectTimeline, runtime, player, AC6FxrEffectFactory.BuildLibrary());
		}
		float lastEffectTime = -1f;
		float[] sampleTimes = new float[2]
		{
			0f,
			clip.length * 0.5f
		};
		for (int i6 = 0; i6 < sampleTimes.Length; i6++)
		{
			player?.EvaluatePlayableGraphAtTime(sampleTimes[i6]);
			if (effectPlayer != null)
			{
				effectPlayer.EvaluateWindow(lastEffectTime, sampleTimes[i6]);
				lastEffectTime = sampleTimes[i6];
			}
			if (player != null)
			{
				player.GetSharedBonePoseErrors(out var positionError, out var rotationError2, out var scaleError2);
				report.Log.AppendLine($"[{key}] t={sampleTimes[i6]:0.###} shared ancestors={player.SharedBoneBindingCount} " + $"posError={positionError:0.######}m rotError={rotationError2:0.######}deg scaleError={scaleError2:0.######}");
				ok &= Check(positionError < 0.0001f, key, $"shared ancestor position mismatch at t={sampleTimes[i6]:0.###}: {positionError:0.######}m", report);
				ok &= Check(rotationError2 < 0.001f, key, $"shared ancestor rotation mismatch at t={sampleTimes[i6]:0.###}: {rotationError2:0.######}deg", report);
				ok &= Check(scaleError2 < 0.0001f, key, $"shared ancestor scale mismatch at t={sampleTimes[i6]:0.###}: {scaleError2:0.######}", report);
			}
			Bounds bounds = CombinedBounds(mech);
			float magnitude = bounds.size.magnitude;
			report.Log.AppendLine($"[{key}] t={sampleTimes[i6]:0.###} bounds center={Format(bounds.center)} size={Format(bounds.size)}");
			ok &= Check(!float.IsNaN(magnitude) && magnitude > 2f && magnitude < 30f, key, $"invalid combined bounds at t={sampleTimes[i6]:0.###}: {magnitude}", report);
			for (int r2 = 0; r2 < frameRecords.Count; r2++)
			{
				float distance = Vector3.Distance(CombinedBounds(frameRecords[r2].instance).center, bounds.center);
				ok &= Check(distance < 12f, key, $"{frameRecords[r2].slot} drifted {distance:0.###}m from assembly center", report);
			}
			Bounds frameBounds = CombinedBounds(frameRecords[0].instance);
			for (int r3 = 1; r3 < frameRecords.Count; r3++)
			{
				frameBounds.Encapsulate(CombinedBounds(frameRecords[r3].instance));
			}
			for (int r4 = 0; r4 < equipmentRecords.Count; r4++)
			{
				Bounds equipmentBounds = CombinedBounds(equipmentRecords[r4].instance);
				float distance2 = Mathf.Sqrt(frameBounds.SqrDistance(equipmentBounds.center));
				report.Log.AppendLine($"[{key}] t={sampleTimes[i6]:0.###} {equipmentRecords[r4].slot} center={Format(equipmentBounds.center)} distToFrame={distance2:0.###}m");
				ok &= Check(distance2 < 4f, key, $"{equipmentRecords[r4].slot} floats {distance2:0.###}m away from the frame bounds", report);
			}
			foreach (KeyValuePair<MechAssemblyRecord, Matrix4x4> pair in installBaselines)
			{
				MechAssemblyRecord record4 = pair.Key;
				if (!(record4.instance == null) && !(record4.parentMount == null) && !(record4.parentMount.parent == null))
				{
					Matrix4x4 current = record4.parentMount.parent.worldToLocalMatrix * record4.instance.transform.localToWorldMatrix;
					float positionDrift = ((Vector3)current.GetColumn(3) - (Vector3)pair.Value.GetColumn(3)).magnitude;
					float rotationDrift = Quaternion.Angle(current.rotation, pair.Value.rotation);
					ok &= Check(positionDrift < 0.001f && rotationDrift < 0.05f, key, $"{record4.slot} installation transform drifted at t={sampleTimes[i6]:0.###} (pos {positionDrift:0.####}m, rot {rotationDrift:0.###}deg)", report);
				}
			}
			for (int r5 = 0; r5 < weaponRecords.Count; r5++)
			{
				MechAssemblyRecord record5 = weaponRecords[r5];
				if (record5.instance == null)
				{
					continue;
				}
				Transform mountSource2 = ((record5.parentMount != null) ? record5.parentMount.parent : null);
				Vector3 rootLocal = mech.transform.InverseTransformPoint(record5.instance.transform.position);
				bool isMelee = record5.part.HasTag("melee");
				Transform muzzle = FindMuzzleTransform(record5.instance.transform);
				report.Log.AppendLine($"[{key}] t={sampleTimes[i6]:0.###} DIAG {record5.slot} part={record5.part.PartId} " + "mount=" + ((mountSource2 != null) ? GetTransformPath(mountSource2, mech.transform) : "<none>") + " " + string.Format("rootLocal={0} melee={1} muzzle={2}", Format(rootLocal), isMelee, (muzzle != null) ? muzzle.name : "<none>"));
				if (muzzle != null)
				{
					Vector3 muzzleLocal = mech.transform.InverseTransformPoint(muzzle.position);
					Vector3 muzzleDir = muzzle.forward;
					Vector3 mechForward = -mech.transform.forward;
					float forwardness = Vector3.Dot(muzzleDir.normalized, mechForward.normalized);
					report.Log.AppendLine($"[{key}] t={sampleTimes[i6]:0.###} DIAG {record5.slot} muzzleLocal={Format(muzzleLocal)} muzzleDir={Format(muzzleDir)} forwardness={forwardness:0.###}");
					if (i6 == 0)
					{
						ok &= Check(forwardness > 0.1f, key, $"{record5.slot} muzzle points away from mech forward at rest (dot {forwardness:0.###}, muzzle {muzzle.name})", report);
					}
				}
				else if (i6 == 0)
				{
					ok &= Check(condition: false, key, $"{record5.slot} has no muzzle/working-end dummy to verify its installed direction", report);
				}
				Screenshot(mech, GrowBounds(CombinedBounds(record5.instance), 1.35f), Path.Combine("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports", $"Closeup_{key}_{record5.slot}_t{sampleTimes[i6]:000}.png"));
			}
			if (boosterVisuals.Count > 0)
			{
				Bounds boosterBounds = CombinedBounds(boosterVisuals[0].gameObject);
				for (int b = 1; b < boosterVisuals.Count; b++)
				{
					boosterBounds.Encapsulate(CombinedBounds(boosterVisuals[b].gameObject));
				}
				Screenshot(mech, GrowBounds(boosterBounds, 1.6f), Path.Combine("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports", $"Closeup_{key}_Boosters_t{sampleTimes[i6]:000}.png"), new Vector3(0.55f, 0.3f, 1f));
				Screenshot(mech, GrowBounds(boosterBounds, 1.6f), Path.Combine("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports", $"Closeup_{key}_Boosters_side_t{sampleTimes[i6]:000}.png"), new Vector3(1f, 0.05f, 0.15f));
			}
		}
		MechActionEffectTimeline fireTimeline = LoadOrCreateEffectTimeline("a000_040010");
		if (fireTimeline != null)
		{
			MechAssemblyRecord fireRecord = weaponRecords.FirstOrDefault((MechAssemblyRecord mechAssemblyRecord) => mechAssemblyRecord.instance != null && mechAssemblyRecord.part.PartId.EndsWith("WP_L_0010", StringComparison.OrdinalIgnoreCase) && mechAssemblyRecord.part.EquipmentAnimations.Count > 0);
			MechEquipmentAnimationPlayer firePlayer = fireRecord?.instance.GetComponent<MechEquipmentAnimationPlayer>();
			if (firePlayer != null && firePlayer.HasAction("a000_040010"))
			{
				MechActionEffectPlayer weaponEffects = fireRecord.instance.GetComponent<MechActionEffectPlayer>();
				if (weaponEffects == null)
				{
					weaponEffects = fireRecord.instance.AddComponent<MechActionEffectPlayer>();
				}
				weaponEffects.ConfigureForEquipment(fireTimeline, fireRecord.instance.transform, firePlayer, AC6FxrEffectFactory.BuildLibrary());
				firePlayer.SampleActionAtTime("a000_040010", 2.2f);
				weaponEffects.EvaluateWindow(-1f, 2.2f);
				Bounds weaponBounds = GrowBounds(CombinedBounds(fireRecord.instance), 1.5f);
				foreach (MechActionEffectSpawnRecord spawn in weaponEffects.SpawnRecords)
				{
					bool inside = weaponBounds.Contains(spawn.WorldPosition);
					report.Log.AppendLine($"[{key}] WEAPONFX t={spawn.Time:0.###} ffx={spawn.FfxId} node={spawn.DummyPath} " + $"pos={Format(mech.transform.InverseTransformPoint(spawn.WorldPosition))} fwd={Format(spawn.WorldForward)} insideWeaponBounds={inside}");
					ok &= Check(inside, key, $"weapon effect ffx={spawn.FfxId} spawned outside the weapon bounds", report);
				}
				foreach (string unresolved in weaponEffects.UnresolvedEvents)
				{
					ok &= Check(condition: false, key, "weapon effect unresolved: " + unresolved, report);
				}
				ok &= Check(weaponEffects.SpawnRecords.Count == fireTimeline.events.Count, key, $"weapon fire expected {fireTimeline.events.Count} effect spawns, got {weaponEffects.SpawnRecords.Count}", report);
				report.Log.AppendLine($"[{key}] WEAPONFX summary: {weaponEffects.SpawnRecords.Count}/{fireTimeline.events.Count} spawns on {fireRecord.part.PartId}");
				weaponEffects.ResetPlayback();
				firePlayer.SampleActionAtTime(firePlayer.DefaultActionId, 0f);
			}
		}
		if (effectPlayer != null)
		{
			Bounds frameBoundsForEffects = CombinedBounds(frameRecords[0].instance);
			for (int r6 = 1; r6 < frameRecords.Count; r6++)
			{
				frameBoundsForEffects.Encapsulate(CombinedBounds(frameRecords[r6].instance));
			}
			foreach (MechActionEffectSpawnRecord spawn2 in effectPlayer.SpawnRecords)
			{
				float distance3 = Mathf.Sqrt(frameBoundsForEffects.SqrDistance(spawn2.WorldPosition));
				report.Log.AppendLine($"[{key}] EFFECT t={spawn2.Time:0.###} ffx={spawn2.FfxId} dummy={spawn2.DummyPolyId} slot={spawn2.Slot} " + $"node={spawn2.DummyPath} pos={Format(mech.transform.InverseTransformPoint(spawn2.WorldPosition))} fwd={Format(spawn2.WorldForward)} distToFrame={distance3:0.###}m");
				ok &= Check(distance3 < 2f, key, $"effect ffx={spawn2.FfxId} spawned {distance3:0.###}m away from the frame (dummy {spawn2.DummyPolyId})", report);
			}
			foreach (string unresolved2 in effectPlayer.UnresolvedEvents)
			{
				ok &= Check(condition: false, key, "effect event routing bug: " + unresolved2, report);
			}
			foreach (string skipped in effectPlayer.OptionalSkips)
			{
				report.Log.AppendLine("[" + key + "] EFFECT optional-skip (dummy absent on this loadout): " + skipped);
			}
			ok &= Check(effectPlayer.SpawnRecords.Count > 0, key, "effect timeline produced no spawns", report);
			report.Log.AppendLine($"[{key}] EFFECT summary: {effectPlayer.SpawnRecords.Count} spawns, {effectPlayer.OptionalSkips.Count} optional skips, {effectPlayer.UnresolvedEvents.Count} routing bugs (timeline {effectPlayer.Timeline.actionId})");
			effectPlayer.ResetPlayback();
		}
		if (ok)
		{
			report.Passes++;
			report.Log.AppendLine("[" + key + "] PASS: frame animation and native reference-space equipment actions stay assembled.");
		}
		player?.StopPlayback();
		for (int i7 = 0; i7 < equipmentPlayers.Length; i7++)
		{
			equipmentPlayers[i7].StopPlayback();
		}
	}

	private static int ExpectedNativeActionCount(string partId)
	{
		if (string.IsNullOrWhiteSpace(partId))
		{
			return -1;
		}
		if (_nativeEquipmentManifest == null)
		{
			_nativeEquipmentManifest = LoadNativeEquipmentManifest();
		}
		foreach (NativeEquipmentManifestEntry entry in _nativeEquipmentManifest)
		{
			if (partId.EndsWith(entry.key, StringComparison.OrdinalIgnoreCase))
			{
				return entry.animationCount;
			}
		}
		return -1;
	}

	private static List<NativeEquipmentManifestEntry> LoadNativeEquipmentManifest()
	{
		string manifestPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment/AC6NativeEquipmentManifest.json";
		if (!File.Exists(manifestPath))
		{
			return new List<NativeEquipmentManifestEntry>();
		}
		return JsonUtility.FromJson<NativeEquipmentManifestWrapper>("{\"items\":" + File.ReadAllText(manifestPath) + "}")?.items ?? new List<NativeEquipmentManifestEntry>();
	}

	private static float MinDistanceToWeaponSurface(GameObject weapon, Vector3 point)
	{
		float best = float.MaxValue;
		SkinnedMeshRenderer[] componentsInChildren = weapon.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
		foreach (SkinnedMeshRenderer skinned in componentsInChildren)
		{
			Mesh baked = new Mesh();
			try
			{
				skinned.BakeMesh(baked);
				best = Mathf.Min(best, MinDistance(baked.vertices, skinned.transform.localToWorldMatrix, point));
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(baked);
			}
		}
		MeshFilter[] componentsInChildren2 = weapon.GetComponentsInChildren<MeshFilter>(includeInactive: true);
		foreach (MeshFilter filter in componentsInChildren2)
		{
			if (filter.sharedMesh != null)
			{
				best = Mathf.Min(best, MinDistance(filter.sharedMesh.vertices, filter.transform.localToWorldMatrix, point));
			}
		}
		return best;
	}

	private static float MinDistance(Vector3[] vertices, Matrix4x4 localToWorld, Vector3 point)
	{
		float best = float.MaxValue;
		for (int i = 0; i < vertices.Length; i++)
		{
			float d = (localToWorld.MultiplyPoint3x4(vertices[i]) - point).sqrMagnitude;
			if (d < best)
			{
				best = d;
			}
		}
		return Mathf.Sqrt(best);
	}

	private static MechActionEffectTimeline LoadOrCreateEffectTimeline(string actionId)
	{
		string sourcePath = "Tools/LocalOut/effect_timelines/" + actionId + ".json";
		string assetPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Effects/AC6_" + actionId + "_Effects.asset";
		if (!File.Exists(sourcePath))
		{
			return AssetDatabase.LoadAssetAtPath<MechActionEffectTimeline>(assetPath);
		}
		EnsureAssetFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Effects");
		MechActionEffectTimeline timeline = LoadOrCreateAsset<MechActionEffectTimeline>(assetPath);
		JsonUtility.FromJsonOverwrite(File.ReadAllText(sourcePath), timeline);
		EditorUtility.SetDirty(timeline);
		return timeline;
	}

	private static Transform FindChildBySuffix(Transform root, string suffix)
	{
		Transform[] transforms = root.GetComponentsInChildren<Transform>(includeInactive: true);
		for (int i = 0; i < transforms.Length; i++)
		{
			if (transforms[i].name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
			{
				return transforms[i];
			}
		}
		return null;
	}

	private static Transform FindMuzzleTransform(Transform weaponRoot)
	{
		Transform[] transforms = weaponRoot.GetComponentsInChildren<Transform>(includeInactive: true);
		string[] array = new string[3] { "_dmy_10", "_dmy_300", "_dmy_200" };
		foreach (string suffix in array)
		{
			Transform[] array2 = transforms;
			foreach (Transform candidate in array2)
			{
				if (candidate.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
				{
					return candidate;
				}
			}
		}
		return null;
	}

	private static string GetTransformPath(Transform target, Transform root)
	{
		if (target == null)
		{
			return "<none>";
		}
		string path = target.name;
		Transform current = target.parent;
		while (current != null && current != root)
		{
			path = current.name + "/" + path;
			current = current.parent;
		}
		return path;
	}

	private static Bounds GrowBounds(Bounds bounds, float factor)
	{
		bounds.size *= factor;
		return bounds;
	}

	private static bool WithRawAssetInstance(string assetPath, string key, ValidationReport report, Func<GameObject, bool> check)
	{
		GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
		if (asset == null)
		{
			return Check(condition: false, key, "golden source asset missing: " + assetPath, report);
		}
		GameObject instance = UnityEngine.Object.Instantiate(asset);
		try
		{
			return check(instance);
		}
		finally
		{
			UnityEngine.Object.DestroyImmediate(instance);
		}
	}

	private static bool CheckGoldenSocket(GameObject mech, string suffix, Vector3 expectedLocal, string key, ValidationReport report)
	{
		Transform socket = mech.GetComponentsInChildren<Transform>(includeInactive: true).FirstOrDefault((Transform candidate) => candidate.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || candidate.name.Contains(suffix + "_", StringComparison.OrdinalIgnoreCase) || candidate.name.Contains(suffix + " ", StringComparison.OrdinalIgnoreCase));
		if (socket == null)
		{
			return Check(condition: false, key, "golden socket " + suffix + " was not found", report);
		}
		Vector3 actual = mech.transform.InverseTransformPoint(socket.position);
		float distance = Vector3.Distance(actual, expectedLocal);
		report.Log.AppendLine($"[{key}] golden {suffix}: expected={Format(expectedLocal)} actual={Format(actual)} d={distance:0.####}m");
		return Check(distance < 0.02f, key, $"socket {suffix} rest position is off by {distance:0.###}m (expected {Format(expectedLocal)}, got {Format(actual)})", report);
	}

	private static bool CheckAnatomicalSocketSide(GameObject mech, string suffix, bool expectRight, string key, ValidationReport report)
	{
		Transform socket = mech.GetComponentsInChildren<Transform>(includeInactive: true).FirstOrDefault((Transform candidate) => candidate.name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || candidate.name.Contains(suffix + "_", StringComparison.OrdinalIgnoreCase) || candidate.name.Contains(suffix + " ", StringComparison.OrdinalIgnoreCase));
		if (socket == null)
		{
			return Check(condition: false, key, "anatomical socket " + suffix + " was not found", report);
		}
		Vector3 visibleForward = -mech.transform.forward;
		Vector3 visibleRight = Vector3.Cross(mech.transform.up, visibleForward).normalized;
		float sideDistance = Vector3.Dot(socket.position - mech.transform.position, visibleRight);
		bool correctSide = (expectRight ? (sideDistance > 0.1f) : (sideDistance < -0.1f));
		report.Log.AppendLine(string.Format("[{0}] anatomical {1}: expected={2} signedDistance={3:0.###}m", key, suffix, expectRight ? "right" : "left", sideDistance));
		return Check(correctSide, key, "socket " + suffix + " is on the visible " + ((sideDistance >= 0f) ? "right" : "left") + " but is defined as anatomical " + (expectRight ? "right" : "left"), report);
	}

	private static bool CheckGoldenSocketSet(GameObject mech, string suffix, Vector3[] expectedLocals, string key, ValidationReport report)
	{
		List<Transform> sockets = (from candidate in mech.GetComponentsInChildren<Transform>(includeInactive: true)
			where candidate.GetComponent<MechMountPoint>() == null && candidate.name.Contains(suffix, StringComparison.OrdinalIgnoreCase)
			select candidate).ToList();
		if (sockets.Count < expectedLocals.Length)
		{
			return Check(condition: false, key, $"expected {expectedLocals.Length} sockets matching {suffix}, found {sockets.Count}", report);
		}
		bool ok = true;
		List<Vector3> unmatched = sockets.Select((Transform socket) => mech.transform.InverseTransformPoint(socket.position)).ToList();
		foreach (Vector3 expected in expectedLocals)
		{
			int bestIndex = -1;
			float bestDistance = float.MaxValue;
			for (int i = 0; i < unmatched.Count; i++)
			{
				float distance = Vector3.Distance(unmatched[i], expected);
				if (distance < bestDistance)
				{
					bestDistance = distance;
					bestIndex = i;
				}
			}
			report.Log.AppendLine(string.Format("[{0}] golden {1}: expected={2} best={3} d={4:0.####}m", key, suffix, Format(expected), (bestIndex >= 0) ? Format(unmatched[bestIndex]) : "<none>", bestDistance));
			ok &= Check(bestDistance < 0.02f, key, $"no socket matching {suffix} within 0.02m of {Format(expected)} (best {bestDistance:0.###}m)", report);
			if (bestIndex >= 0)
			{
				unmatched.RemoveAt(bestIndex);
			}
		}
		return ok;
	}

	private static string ExpectedEquipmentMountSuffix(MechPartSlot slot, MechPartDefinition part)
	{
		switch (slot)
		{
		case MechPartSlot.LeftHandWeapon:
			if (!(part != null) || !part.HasTag("melee"))
			{
				return "_dmy_23";
			}
			return "_dmy_25";
		case MechPartSlot.RightHandWeapon:
			return "_dmy_20";
		case MechPartSlot.LeftBackWeapon:
			return "_dmy_13";
		case MechPartSlot.RightBackWeapon:
			return "_dmy_14";
		default:
			return string.Empty;
		}
	}

	private static void SampleValidationPose(GameObject mech, AnimationClip bodyClip, float normalizedTime)
	{
		MechSynchronizedAnimationPlayer bodyPlayer = mech.GetComponent<MechSynchronizedAnimationPlayer>();
		if (bodyPlayer != null && bodyClip != null)
		{
			bodyPlayer.SampleAtTime(bodyClip.length * Mathf.Clamp01(normalizedTime));
		}
		MechEquipmentAnimationPlayer[] equipmentPlayers = mech.GetComponentsInChildren<MechEquipmentAnimationPlayer>(includeInactive: true);
		foreach (MechEquipmentAnimationPlayer player in equipmentPlayers)
		{
			if (player.Actions.Count != 0)
			{
				MechEquipmentAnimationDefinition action = player.Actions.FirstOrDefault((MechEquipmentAnimationDefinition candidate) => string.Equals(candidate.ActionId, player.DefaultActionId, StringComparison.OrdinalIgnoreCase)) ?? player.Actions[0];
				player.SampleActionAtTime(action.ActionId, action.Clip.length * Mathf.Clamp01(normalizedTime));
			}
		}
	}

	private static bool Check(bool condition, string key, string failure, ValidationReport report)
	{
		if (condition)
		{
			return true;
		}
		report.Failures++;
		report.Log.AppendLine("[" + key + "] FAIL: " + failure);
		return false;
	}

	private static Dictionary<MechPartSlot, MechPartDefinition> BuildInternalParts()
	{
		return new Dictionary<MechPartSlot, MechPartDefinition>
		{
			[MechPartSlot.Generator] = CreateInternalPart(
				"AC6_SKIN_SAMPLE_GENERATOR",
				"Sample Compact Reactor",
				MechPartSlot.Generator,
				BuildSampleGeneratorStats(),
				BuildSampleGeneratorPerformance()),
			[MechPartSlot.FireControlSystem] = CreateInternalPart("AC6_SKIN_SAMPLE_FCS", "Sample Fire Control System", MechPartSlot.FireControlSystem, BuildSampleFireControlStats(), fireControlPerformance: BuildSampleFireControlPerformance()),
			[MechPartSlot.Radar] = CreateInternalPart("AC6_SKIN_SAMPLE_RADAR", "Sample Active Radar", MechPartSlot.Radar, BuildSampleRadarStats(), radarPerformance: BuildSampleRadarPerformance())
		};
	}

	private static MechPartStats BuildSampleFireControlStats() => new MechPartStats
	{
		massKg = 700f,
		idlePowerDrawMw = 0.12f,
		peakPowerDrawMw = 0.65f,
		idleHeatMw = 0.08f,
		activeHeatMw = 0.45f,
		heatCapacityMj = 18f,
		fireControlQuality = 65f,
		guidanceChannels = 3
	};

	private static MechPartStats BuildSampleRadarStats() => new MechPartStats
	{
		massKg = 900f,
		idlePowerDrawMw = 0.25f,
		peakPowerDrawMw = 1.8f,
		idleHeatMw = 0.18f,
		activeHeatMw = 1.2f,
		heatCapacityMj = 25f,
		radarRangeKm = 42f,
		radarRefreshHz = 2.5f,
		trackCapacity = 8
	};

	private static MechRadarPerformance BuildSampleRadarPerformance() =>
		new MechRadarPerformance
		{
			referenceDetectionRangeKm = 42f,
			stableTrackRangeKm = 30f,
			searchAzimuthDeg = 100f,
			searchElevationDeg = 60f,
			trackCapacity = 8,
			sttChannelCapacity = 1,
			maximumTrackingRateDegPerSecond = 140f,
			trackMemorySeconds = 3f,
			rangeAccuracyM = 12f,
			angularAccuracyMrad = 26.17994f,
			radialVelocityAccuracyMps = 2f,
			searchDwellRateHz = 25f,
			twsUpdateRateHz = 4.166667f,
			sttUpdateRateHz = 28.57143f
		};

	private static MechFireControlPerformance BuildSampleFireControlPerformance() =>
		new MechFireControlPerformance
		{
			baseSolutionErrorMrad = 0.262f,
			maximumPredictionTimeS = 6f,
			simultaneousSolutionCapacity = 8,
			weaponControlChannels = 4,
			guidanceChannelCapacity = 3,
			multiTargetEngagementCapacity = 3,
			pointDefenseRating = 65f,
			trackReacquisitionTimeS = 0.6f,
			solutionUpdateRateHz = 30f,
			solutionLatencyMs = 35f
		};

	private static Dictionary<string, MechPartDefinition> BuildBoosterParts(ICollection<string> missing)
	{
		Dictionary<string, MechPartDefinition> result = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < FrameCodes.Length; i++)
		{
			string code = FrameCodes[i];
			string key = "BS_A_" + code;
			string sourcePath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment/" + key + "_NATIVE.glb";
			if (!TryLoadNativeEquipment(sourcePath, loopBoosterAction: true, loopSustainedWeaponFireAction: false, out var model, out var actions))
			{
				missing.Add(sourcePath + " (native booster model failed to import)");
				continue;
			}
			if (FindChildBySuffix(model.transform, "_dmy_31") == null)
			{
				missing.Add(sourcePath + " (booster flame anchor _dmy_31 not found)");
				continue;
			}
			string id = "AC6_NATIVE_" + key;
			MechPartDefinition part = LoadOrCreateAsset<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts/" + id + ".asset");
			part.EditorConfigure(id, "AC6 Native Booster " + code, MechPartSlot.Booster, MechPartCategory.Internal, model, BuildBoosterStats(code), Array.Empty<MechMountPointDefinition>(), new string[4]
			{
				"internal",
				"booster",
				"ac6-native",
				"set-" + code
			}, null, MechVisualRigMode.MountedPrefab, null, MechPartSlot.None, null, shouldInstantiateVisualAtAllMounts: true, actions, SelectDefaultAction(actions, "a000_000001"), shouldPlayDefaultEquipmentActionOnAssemble: false, Vector3.zero, NativeBoosterInstallEuler, Vector3.one, newBoosterPerformance: BuildBoosterPerformance(code));
			result[code] = part;
		}
		return result;
	}

	private static Dictionary<string, MechPartDefinition> LoadWeaponParts(ICollection<string> missing, PaintPresetRecord paintPreset, MaterialCatalogRecord materialCatalog)
	{
		NativeWeaponSpec[] specs = new NativeWeaponSpec[15]
		{
			new NativeWeaponSpec("WP_L_0010", "Native Pile Bunker ASHMEAD 0010", MechPartSlot.LeftHandWeapon, "a000_000000", 4180f, isMelee: true),
			new NativeWeaponSpec("WP_L_0230", "Native Left Laser Blade 0230", MechPartSlot.LeftHandWeapon, "a000_000000", 4700f, isMelee: true),
			new NativeWeaponSpec("WP_L_0250", "Native Left Blade 0250", MechPartSlot.LeftHandWeapon, "a000_000000", 6100f, isMelee: true),
			new NativeWeaponSpec("WP_L_1310", "Native Left Grenade Launcher 1310", MechPartSlot.LeftHandWeapon, "a000_800210", 4900f),
			new NativeWeaponSpec("WP_R_1310", "Native Right Grenade Launcher 1310", MechPartSlot.RightHandWeapon, "a000_000000", 4900f),
			new NativeWeaponSpec("WP_R_1610", "MA-J-200 RANSETSU-RF", MechPartSlot.RightHandWeapon, "a000_800210", 4310f),
			new NativeWeaponSpec("WP_L_1410", "Native Left Bazooka 1410", MechPartSlot.LeftHandWeapon, "a000_000000", 4600f),
			new NativeWeaponSpec("WP_L_4210", "Native Left Plasma Missile 4210", MechPartSlot.LeftBackWeapon, "a000_800000", 4200f),
			new NativeWeaponSpec("WP_R_4210", "Native Right Plasma Missile 4210", MechPartSlot.RightBackWeapon, "a000_800000", 4200f),
			new NativeWeaponSpec("WP_L_4250", "BML-G1/P07VTC-12", MechPartSlot.LeftBackWeapon, "a000_800000", 5010f),
			new NativeWeaponSpec("WP_L_4290", "BML-G2/P03MLT-06", MechPartSlot.LeftBackWeapon, "a000_800000", 3840f),
			new NativeWeaponSpec("WP_R_4290", "BML-G2/P03MLT-06", MechPartSlot.RightBackWeapon, "a000_800000", 3840f),
			new NativeWeaponSpec("WP_R_4410", "Native Right Songbirds 4410", MechPartSlot.RightBackWeapon, "a000_800000", 5500f),
			new NativeWeaponSpec("WP_L_5110", "DF-GA-09 SHAO-WEI", MechPartSlot.LeftBackWeapon, "a000_800000", 3960f, isMelee: false, isPointDefense: true),
			new NativeWeaponSpec("WP_R_5110", "DF-GA-09 SHAO-WEI", MechPartSlot.RightBackWeapon, "a000_800000", 3960f, isMelee: false, isPointDefense: true)
		};
		Dictionary<string, MechPartDefinition> result = new Dictionary<string, MechPartDefinition>(StringComparer.OrdinalIgnoreCase);
		foreach (NativeWeaponSpec spec in specs)
		{
			string path = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment/" + spec.Key + "_NATIVE.glb";
			if (!TryLoadNativeEquipment(path, loopBoosterAction: false, spec.IsPointDefense, out var model, out var actions))
			{
				missing.Add(path + " (native weapon model failed to import)");
				continue;
			}
			string id = "AC6_NATIVE_" + spec.Key;
			MechPartDefinition part = LoadOrCreateAsset<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts/" + id + ".asset");
			string weaponCode = spec.Key.Substring(spec.Key.LastIndexOf('_') + 1);
			Material[] materialOverrides = BuildWeaponMaterialOverrides(model, weaponCode, paintPreset, materialCatalog);
			part.EditorConfigure(id, spec.DisplayName, spec.Slot, MechPartCategory.Weapon, model, new MechPartStats
			{
				massKg = spec.MassKg,
				idlePowerDrawMw = (spec.IsPointDefense ? 0.6f : 0f),
				peakPowerDrawMw = (spec.IsPointDefense ? 6f : 4f),
				heatGenerationMw = (spec.IsPointDefense ? 3.2f : 2f),
				heatDissipationMw = (spec.IsPointDefense ? 1.4f : 0f),
				heatCapacityMj = (spec.IsPointDefense ? 6f : 0f)
			}, Array.Empty<MechMountPointDefinition>(), (!spec.IsMelee) ? ((!spec.IsPointDefense) ? new string[3]
			{
				"weapon",
				"ac6-native",
				spec.Key.ToLowerInvariant()
			} : new string[7]
			{
				"weapon",
				"ac6-native",
				"point-defense",
				"gatling",
				"kinetic",
				"automatic",
				spec.Key.ToLowerInvariant()
			}) : new string[4]
			{
				"weapon",
				"ac6-native",
				"melee",
				spec.Key.ToLowerInvariant()
			}, materialOverrides, MechVisualRigMode.MountedPrefab, null, MechPartSlot.None, null, shouldInstantiateVisualAtAllMounts: false, actions, SelectDefaultAction(actions, spec.PreferredActionId), actions.Count > 0 && !spec.IsMelee && spec.Slot != MechPartSlot.LeftBackWeapon && spec.Slot != MechPartSlot.RightBackWeapon, Vector3.zero, NativeWeaponInstallEuler, Vector3.one, newWeaponPerformance: AC6TestWeaponProfileFactory.Create(id));
			result[spec.Key] = part;
		}
		return result;
	}

	private static bool TryLoadNativeEquipment(string glbPath, bool loopBoosterAction, bool loopSustainedWeaponFireAction, out GameObject model, out List<MechEquipmentAnimationDefinition> actions)
	{
		AssetDatabase.ImportAsset(glbPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
		model = AssetDatabase.LoadAssetAtPath<GameObject>(glbPath);
		actions = new List<MechEquipmentAnimationDefinition>();
		if (model == null)
		{
			return false;
		}
		HashSet<string> seenActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (AnimationClip clip in (from candidate in AssetDatabase.LoadAllAssetsAtPath(glbPath).OfType<AnimationClip>()
			where candidate != null && candidate.length > 0.001f
			select candidate).OrderBy((AnimationClip candidate) => candidate.name, StringComparer.OrdinalIgnoreCase))
		{
			Match match = EquipmentActionPattern.Match(clip.name);
			if (match.Success)
			{
				string actionId = match.Value.ToLowerInvariant();
				if (seenActionIds.Add(actionId))
				{
					bool shouldLoop = (loopBoosterAction && actionId == "a000_000001") || (loopSustainedWeaponFireAction && actionId == "a000_800201");
					actions.Add(new MechEquipmentAnimationDefinition(actionId, clip, shouldLoop));
				}
			}
		}
		return true;
	}

	private static string SelectDefaultAction(IReadOnlyList<MechEquipmentAnimationDefinition> actions, string preferredActionId)
	{
		if (actions == null || actions.Count == 0)
		{
			return string.Empty;
		}
		for (int i = 0; i < actions.Count; i++)
		{
			if (string.Equals(actions[i].ActionId, preferredActionId, StringComparison.OrdinalIgnoreCase))
			{
				return actions[i].ActionId;
			}
		}
		return actions[0].ActionId;
	}

	private static bool TryGetEquipmentRootRotation(IReadOnlyList<MechEquipmentAnimationDefinition> actions, string actionId, float timeSeconds, out Quaternion rotation)
	{
		rotation = Quaternion.identity;
		MechEquipmentAnimationDefinition action = actions?.FirstOrDefault((MechEquipmentAnimationDefinition candidate) => candidate?.Clip != null && string.Equals(candidate.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
		if (action?.Clip == null)
		{
			return false;
		}
		foreach (IGrouping<string, EditorCurveBinding> item in from editorCurveBinding in AnimationUtility.GetCurveBindings(action.Clip)
			where editorCurveBinding.propertyName.StartsWith("m_LocalRotation.", StringComparison.Ordinal)
			group editorCurveBinding by editorCurveBinding.path into @group
			orderby (!string.IsNullOrEmpty(@group.Key)) ? 1 : 0, @group.Key?.Length ?? 0
			select @group)
		{
			float[] components = new float[4];
			bool[] found = new bool[4];
			foreach (EditorCurveBinding binding in item)
			{
				int component = RotationComponentIndex(binding.propertyName);
				AnimationCurve curve = ((component >= 0) ? AnimationUtility.GetEditorCurve(action.Clip, binding) : null);
				if (curve != null)
				{
					components[component] = curve.Evaluate(timeSeconds);
					found[component] = true;
				}
			}
			if (found.All((bool value) => value))
			{
				float magnitude = Mathf.Sqrt(components[0] * components[0] + components[1] * components[1] + components[2] * components[2] + components[3] * components[3]);
				if (!(magnitude < 0.0001f))
				{
					rotation = new Quaternion(components[0] / magnitude, components[1] / magnitude, components[2] / magnitude, components[3] / magnitude);
					return true;
				}
			}
		}
		return false;
	}

	private static int RotationComponentIndex(string propertyName)
	{
		if (propertyName.EndsWith(".x", StringComparison.Ordinal))
		{
			return 0;
		}
		if (propertyName.EndsWith(".y", StringComparison.Ordinal))
		{
			return 1;
		}
		if (propertyName.EndsWith(".z", StringComparison.Ordinal))
		{
			return 2;
		}
		if (propertyName.EndsWith(".w", StringComparison.Ordinal))
		{
			return 3;
		}
		return -1;
	}

	private static MechPartDefinition CreateInternalPart(
		string id,
		string displayName,
		MechPartSlot slot,
		MechPartStats stats,
		MechGeneratorPerformance generatorPerformance = null,
		MechRadarPerformance radarPerformance = null,
		MechFireControlPerformance fireControlPerformance = null)
	{
		MechPartDefinition part = LoadOrCreateAsset<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts/" + id + ".asset");
		part.EditorConfigure(id, displayName, slot, MechPartCategory.Internal, null, stats, Array.Empty<MechMountPointDefinition>(), new string[2] { "internal", "sample" }, newGeneratorPerformance: generatorPerformance, newRadarPerformance: radarPerformance, newFireControlPerformance: fireControlPerformance);
		return part;
	}

	private static void CreateLoadout(IDictionary<string, MechLoadoutDefinition> destination, string key, string displayName, IReadOnlyDictionary<string, MechPartDefinition> frameParts, IReadOnlyDictionary<MechPartSlot, MechPartDefinition> internals, IReadOnlyDictionary<string, MechPartDefinition> boosters, IReadOnlyDictionary<string, MechPartDefinition> weapons, string headCode, string coreCode, string armsCode, string legsCode, string leftHandWeaponKey = null)
	{
		List<MechPartAssignment> assignments = new List<MechPartAssignment>
		{
			Assignment(MechPartSlot.Head, frameParts[FrameKey(MechPartSlot.Head, headCode)]),
			Assignment(MechPartSlot.Core, frameParts[FrameKey(MechPartSlot.Core, coreCode)]),
			Assignment(MechPartSlot.Arms, frameParts[FrameKey(MechPartSlot.Arms, armsCode)]),
			Assignment(MechPartSlot.Legs, frameParts[FrameKey(MechPartSlot.Legs, legsCode)]),
			Assignment(MechPartSlot.Booster, boosters[coreCode]),
			Assignment(MechPartSlot.Generator, internals[MechPartSlot.Generator]),
			Assignment(MechPartSlot.FireControlSystem, internals[MechPartSlot.FireControlSystem]),
			Assignment(MechPartSlot.Radar, internals[MechPartSlot.Radar]),
			Assignment(MechPartSlot.LeftHandWeapon, weapons[(key == "Uniform1010") ? "WP_L_1310" : (leftHandWeaponKey ?? LeftHandWeaponForCode(armsCode))]),
			Assignment(MechPartSlot.RightHandWeapon, weapons[(key == "Uniform1010") ? "WP_R_1610" : "WP_R_1310"]),
			Assignment(MechPartSlot.LeftBackWeapon, weapons[(key == "Uniform1010") ? "WP_L_4250" : "WP_L_4210"]),
			Assignment(MechPartSlot.RightBackWeapon, weapons[(key == "Uniform1010") ? "WP_R_4290" : RightBackWeaponForCode(coreCode)])
		};
		MechLoadoutDefinition loadout = LoadOrCreateAsset<MechLoadoutDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts/AC6_Skinned_" + key + ".asset");
		loadout.EditorConfigure("ac6_skinned_" + key.ToLowerInvariant(), displayName, assignments);
		destination[key] = loadout;
	}

	private static MechPartAssignment Assignment(MechPartSlot slot, MechPartDefinition part)
	{
		return new MechPartAssignment
		{
			slot = slot,
			part = part,
			enabled = true
		};
	}

	private static string LeftHandWeaponForCode(string code)
	{
		if (!(code == "2010"))
		{
			if (code == "3010")
			{
				return "WP_L_0250";
			}
			return "WP_L_0010";
		}
		return "WP_L_0230";
	}

	private static string RightBackWeaponForCode(string code)
	{
		if (!(code == "2010"))
		{
			return "WP_R_4210";
		}
		return "WP_R_4410";
	}

	private static IReadOnlyList<MechMountPointDefinition> BuildFrameMountPoints(MechPartSlot slot)
	{
		return slot switch
		{
			MechPartSlot.Core => new MechMountPointDefinition[3]
			{
				Mount("Booster Nozzles", MechPartSlot.Booster, MechHardpointType.Internal, "_dmy_800", matchAll: true, 10000f, null, null, MechMountSourceRegion.Rear),
				Mount("Left Back", MechPartSlot.LeftBackWeapon, MechHardpointType.BackWeapon, "_dmy_13", matchAll: false, 10000f),
				Mount("Right Back", MechPartSlot.RightBackWeapon, MechHardpointType.BackWeapon, "_dmy_14", matchAll: false, 10000f)
			}, 
			MechPartSlot.Arms => new MechMountPointDefinition[3]
			{
				Mount("Left Hand", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_23", matchAll: false, 8000f, null, new string[1] { "melee" }),
				Mount("Left Melee", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_25", matchAll: false, 8000f, new string[1] { "melee" }),
				Mount("Right Hand", MechPartSlot.RightHandWeapon, MechHardpointType.HandWeapon, "_dmy_20", matchAll: false, 8000f)
			}, 
			_ => Array.Empty<MechMountPointDefinition>(), 
		};
	}

	private static MechMountPointDefinition Mount(string id, MechPartSlot targetSlot, MechHardpointType hardpointType, string sourceTransformNameSuffix, bool matchAll, float maxMassKg, string[] requiredTags = null, string[] blockedTags = null, MechMountSourceRegion sourceRegion = MechMountSourceRegion.Any)
	{
		return new MechMountPointDefinition
		{
			mountId = id,
			targetSlot = targetSlot,
			hardpointType = hardpointType,
			maxMassKg = maxMassKg,
			sourceTransformNameSuffix = sourceTransformNameSuffix,
			matchAllSourceTransforms = matchAll,
			sourceTransformRegion = sourceRegion,
			localScale = Vector3.one,
			requiredTags = (requiredTags ?? Array.Empty<string>()),
			blockedTags = (blockedTags ?? Array.Empty<string>())
		};
	}

	private static MechPartStats BuildFrameStats(MechPartSlot slot, float scale, string code)
	{
		return slot switch
		{
			MechPartSlot.Head => BuildHeadStats(scale, code),
			MechPartSlot.Core => BuildCoreStats(scale),
			MechPartSlot.Arms => new MechPartStats
			{
				massKg = 6900f * scale,
				armorPoints = 850f * scale,
				structurePoints = 680f * scale,
				idlePowerDrawMw = 0.55f * scale,
				peakPowerDrawMw = 2.6f * scale,
				idleHeatMw = 0.14f * scale,
				activeHeatMw = 0.95f * scale,
				heatGenerationMw = 0.95f * scale,
				heatCapacityMj = 80f * scale,
				weaponRecoilControl = 58f * scale
			}, 
			MechPartSlot.Legs => new MechPartStats
			{
				massKg = 9800f * scale,
				armorPoints = 1250f * scale,
				structurePoints = 1050f * scale,
				stability = 44f * scale,
				idlePowerDrawMw = 0.8f * scale,
				peakPowerDrawMw = 1.6f * scale,
				idleHeatMw = 0.22f * scale,
				activeHeatMw = 0.72f * scale,
				heatGenerationMw = 0.72f * scale,
				heatDissipationMw = 2.4f * scale,
				heatCapacityMj = 160f * scale
			}, 
			_ => new MechPartStats(), 
		};
	}

	private static MechPartStats BuildCoreStats(float scale)
	{
		return new MechPartStats
		{
			massKg = 12000f * scale,
			armorPoints = 1750f * scale,
			structurePoints = 1450f * scale,
			stability = 26f * scale,
			idlePowerDrawMw = 0.95f * scale,
			peakPowerDrawMw = 3.8f * scale,
			idleHeatMw = 0.28f * scale,
			activeHeatMw = 1.25f * scale,
			heatGenerationMw = 1.25f * scale,
			heatDissipationMw = 18f * scale,
			heatCapacityMj = 240f * scale,
			hardpointCapacityKg = 16000f * scale,
			rcsThrustKn = 360f * scale
		};
	}

	private static MechPartStats BuildBoosterStats(string code)
	{
		return new MechPartStats
		{
			massKg = code == "3010" ? 4800f : code == "2010" ? 3400f : 3900f,
			idlePowerDrawMw = code == "3010" ? 0.35f : code == "2010" ? 0.22f : 0.25f,
			peakPowerDrawMw = code == "3010" ? 28f : code == "2010" ? 19f : 22f,
			idleHeatMw = code == "3010" ? 0.18f : code == "2010" ? 0.1f : 0.12f,
			activeHeatMw = code == "3010" ? 9.5f : code == "2010" ? 6.8f : 8f,
			heatGenerationMw = code == "3010" ? 9.5f : code == "2010" ? 6.8f : 8f,
			heatCapacityMj = code == "3010" ? 110f : code == "2010" ? 75f : 90f
		};
	}

	private static MechBoosterPerformance BuildBoosterPerformance(string code)
	{
		return new MechBoosterPerformance
		{
			mainThrustKn = code == "3010" ? 720f : code == "2010" ? 610f : 660f,
			assaultBoostThrustKn = code == "3010" ? 3900f : code == "2010" ? 3300f : 3500f,
			quickBoostThrustKn = code == "3010" ? 17500f : code == "2010" ? 15500f : 16000f,
			quickBoostDurationS = code == "3010" ? 0.3f : code == "2010" ? 0.25f : 0.28f,
			quickBoostRecoveryS = code == "3010" ? 0.5f : code == "2010" ? 0.36f : 0.42f,
			horizontalVectorLimitDeg = code == "3010" ? 17f : code == "2010" ? 23f : 20f,
			verticalVectorLimitDeg = code == "3010" ? 31f : code == "2010" ? 39f : 35f,
			assaultBoostPowerDrawKw = code == "3010" ? 52000f : code == "2010" ? 40000f : 45000f,
			quickBoostPowerDrawKw = code == "3010" ? 205000f : code == "2010" ? 165000f : 180000f
		};
	}

	private static MechPartStats BuildSampleGeneratorStats()
	{
		return new MechPartStats
		{
			massKg = 3800f,
			powerOutputMw = 70f,
			idleHeatMw = 4f,
			activeHeatMw = 12f,
			heatGenerationMw = 12f,
			heatCapacityMj = 180f
		};
	}

	private static MechGeneratorPerformance BuildSampleGeneratorPerformance()
	{
		return new MechGeneratorPerformance
		{
			energyStorageCapacityMj = 300f,
			energyRechargePowerKw = 70000f,
			energyRechargeDelayS = 0.75f,
			depletedRestartFraction = 0.2f,
			startupTimeS = 7.5f
		};
	}

	private static MechCorePerformance BuildCorePerformance(MechPartSlot slot, float scale, string code)
	{
		if (slot != MechPartSlot.Core)
		{
			return null;
		}

		float reverseThrust = (code == "3010" ? 280f : code == "2010" ? 330f : 305f) * scale;
		float downwardThrust = 450f * scale;
		return new MechCorePerformance
		{
			forwardExhaustNozzles = new[]
			{
				new MechCoreThrusterNozzle { nozzleId = "core-forward-left", localPositionM = new Vector3(-0.72f, 0.25f, 0.58f), localExhaustDirection = Vector3.forward, maximumThrustKn = reverseThrust * 0.5f, gimbalLimitDeg = 6f, maximumPowerKw = 550f * scale, maximumHeatKw = 220f * scale },
				new MechCoreThrusterNozzle { nozzleId = "core-forward-right", localPositionM = new Vector3(0.72f, 0.25f, 0.58f), localExhaustDirection = Vector3.forward, maximumThrustKn = reverseThrust * 0.5f, gimbalLimitDeg = 6f, maximumPowerKw = 550f * scale, maximumHeatKw = 220f * scale }
			},
			upwardExhaustNozzles = new[]
			{
				new MechCoreThrusterNozzle { nozzleId = "core-up-front-left", localPositionM = new Vector3(-0.68f, 0.74f, 0.42f), localExhaustDirection = Vector3.up, maximumThrustKn = downwardThrust * 0.25f, gimbalLimitDeg = 5f, maximumPowerKw = 300f * scale, maximumHeatKw = 120f * scale },
				new MechCoreThrusterNozzle { nozzleId = "core-up-front-right", localPositionM = new Vector3(0.68f, 0.74f, 0.42f), localExhaustDirection = Vector3.up, maximumThrustKn = downwardThrust * 0.25f, gimbalLimitDeg = 5f, maximumPowerKw = 300f * scale, maximumHeatKw = 120f * scale },
				new MechCoreThrusterNozzle { nozzleId = "core-up-rear-left", localPositionM = new Vector3(-0.68f, 0.74f, -0.42f), localExhaustDirection = Vector3.up, maximumThrustKn = downwardThrust * 0.25f, gimbalLimitDeg = 5f, maximumPowerKw = 300f * scale, maximumHeatKw = 120f * scale },
				new MechCoreThrusterNozzle { nozzleId = "core-up-rear-right", localPositionM = new Vector3(0.68f, 0.74f, -0.42f), localExhaustDirection = Vector3.up, maximumThrustKn = downwardThrust * 0.25f, gimbalLimitDeg = 5f, maximumPowerKw = 300f * scale, maximumHeatKw = 120f * scale }
			}
		};
	}

	private static MechArmPerformance BuildArmPerformance(MechPartSlot slot, float scale)
	{
		if (slot != MechPartSlot.Arms)
		{
			return null;
		}

		return new MechArmPerformance
		{
			weaponLoadLimitKg = 8000f * scale,
			recoilImpulseLimitNs = 22000f * scale,
			recoilRecoveryRateNsPerSecond = 85000f * scale,
			aimTraverseRateDegPerSecond = 105f / Mathf.Sqrt(Mathf.Max(0.5f, scale)),
			aimAngularAccelerationDegPerSecondSquared = 420f / Mathf.Max(0.5f, scale),
			weaponPointingErrorMrad = 0.18f * Mathf.Max(0.8f, scale),
			thrusterNozzles = new[]
			{
				new MechArmThrusterNozzle { nozzleId = "arm-lateral", role = MechArmThrusterRole.Lateral, localPositionM = new Vector3(0.82f, 0.08f, 0f), localThrustDirection = Vector3.left, maximumThrustKn = 40f * scale, gimbalLimitDeg = 8f, maximumPowerKw = 420f * scale, maximumHeatKw = 180f * scale },
				new MechArmThrusterNozzle { nozzleId = "arm-rcs-forward", role = MechArmThrusterRole.Rcs, localPositionM = new Vector3(0.78f, 0.32f, 0.28f), localThrustDirection = Vector3.forward, maximumThrustKn = 75f * scale, maximumPowerKw = 260f * scale, maximumHeatKw = 120f * scale },
				new MechArmThrusterNozzle { nozzleId = "arm-rcs-rear", role = MechArmThrusterRole.Rcs, localPositionM = new Vector3(0.78f, -0.18f, -0.24f), localThrustDirection = Vector3.back, maximumThrustKn = 75f * scale, maximumPowerKw = 260f * scale, maximumHeatKw = 120f * scale }
			}
		};
	}

	private static MechLegPerformance BuildLegPerformance(MechPartSlot slot, float scale)
	{
		if (slot != MechPartSlot.Legs)
		{
			return null;
		}

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

	private static MechPartStats BuildHeadStats(float scale, string code)
	{
		MechPartStats stats = new MechPartStats
		{
			massKg = 1700f * scale,
			armorPoints = 260f * scale,
			structurePoints = 180f * scale,
			peakPowerDrawMw = 1.1f
		};
		if (string.Equals(code, "1010", StringComparison.OrdinalIgnoreCase))
		{
			stats.idlePowerDrawMw = 0.7f;
			stats.heatCapacityMj = 24f;
			stats.idleHeatMw = 0.18f;
			stats.activeHeatMw = 0.72f;
			stats.heatGenerationMw = stats.activeHeatMw;
			stats.radarCrossSection = 0.4f;
			stats.thermalSignature = 0.2f;
		}
		return stats;
	}

	private static MechHeadSensorPerformance BuildHeadSensorPerformance(MechPartSlot slot, string code)
	{
		if (slot != MechPartSlot.Head || !string.Equals(code, "1010", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return new MechHeadSensorPerformance
		{
			pointingCapability = 62f,
			searchCapability = 58f,
			trackingCapability = 64f,
			identificationCapability = 60f,
			maximumMagnification = 12f,
			referenceHotRangeKm = 32f,
			referenceColdRangeKm = 18f,
			trackCapacity = 12,
			referenceRangeErrorM = 180f,
			laserRangefinderRangeKm = 20f,
			laserRangefinderErrorM = 0.75f,
			supportedBands = MechElectroOpticalBand.Visible | MechElectroOpticalBand.Mwir
		};
	}

	private static float MassScale(string code)
	{
		if (!(code == "2010"))
		{
			if (code == "3010")
			{
				return 1.22f;
			}
			return 1f;
		}
		return 0.82f;
	}

	private static string SetDisplayName(string code)
	{
		if (!(code == "2010"))
		{
			if (code == "3010")
			{
				return "Tian-Qiang 3010";
			}
			return "Melander 1010";
		}
		return "Nacht 2010";
	}

	private static string FrameKey(MechPartSlot slot, string code)
	{
		return slot.ToString() + "_" + code;
	}

	private static MechPartSlot AncestorDriverForSlot(MechPartSlot slot)
	{
		switch (slot)
		{
		case MechPartSlot.Core:
			return MechPartSlot.Legs;
		case MechPartSlot.Head:
		case MechPartSlot.Arms:
			return MechPartSlot.Core;
		default:
			return MechPartSlot.None;
		}
	}

	private static IReadOnlyList<string> AncestorDrivenBonesForSlot(MechPartSlot slot)
	{
		return slot switch
		{
			MechPartSlot.Core => CoreLegsDrivenBones, 
			MechPartSlot.Head => HeadCoreDrivenBones, 
			MechPartSlot.Arms => ArmsCoreDrivenBones, 
			_ => Array.Empty<string>(), 
		};
	}

	private static T LoadOrCreateAsset<T>(string path) where T : ScriptableObject
	{
		T asset = AssetDatabase.LoadAssetAtPath<T>(path);
		if (asset != null)
		{
			return asset;
		}
		asset = ScriptableObject.CreateInstance<T>();
		AssetDatabase.CreateAsset(asset, path);
		return asset;
	}

	private static void EnsureAssetFolder(string assetPath)
	{
		string normalized = assetPath.Replace('\\', '/').TrimEnd('/');
		if (!AssetDatabase.IsValidFolder(normalized))
		{
			string obj = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
			string folderName = Path.GetFileName(normalized);
			if (string.IsNullOrWhiteSpace(obj) || string.IsNullOrWhiteSpace(folderName))
			{
				throw new InvalidOperationException("Invalid asset folder path: " + assetPath);
			}
			EnsureAssetFolder(obj);
			AssetDatabase.CreateFolder(obj, folderName);
		}
	}

	private static Bounds CombinedBounds(GameObject root)
	{
		Bounds result = default(Bounds);
		bool initialized = false;
		Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
		for (int i = 0; i < renderers.Length; i++)
		{
			if (renderers[i].GetComponent<TextMesh>() != null)
			{
				continue;
			}
			Bounds rendererBounds = renderers[i].bounds;
			if (renderers[i] is SkinnedMeshRenderer skinned)
			{
				skinned.updateWhenOffscreen = true;
				Mesh baked = new Mesh();
				try
				{
					skinned.BakeMesh(baked);
					rendererBounds = TransformBounds(skinned.transform.localToWorldMatrix, baked.bounds);
				}
				finally
				{
					UnityEngine.Object.DestroyImmediate(baked);
				}
			}
			if (!initialized)
			{
				result = rendererBounds;
				initialized = true;
			}
			else
			{
				result.Encapsulate(rendererBounds);
			}
		}
		return result;
	}

	private static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
	{
		Vector3 center = matrix.MultiplyPoint3x4(localBounds.center);
		Vector3 extents = localBounds.extents;
		Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0f, 0f));
		Vector3 axisY = matrix.MultiplyVector(new Vector3(0f, extents.y, 0f));
		Vector3 axisZ = matrix.MultiplyVector(new Vector3(0f, 0f, extents.z));
		extents = new Vector3(Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x), Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y), Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
		return new Bounds(center, extents * 2f);
	}

	private static void CreateSceneLighting(Transform parent, IReadOnlyList<GameObject> mechs)
	{
		GameObject gameObject = new GameObject("Directional Light");
		gameObject.transform.SetParent(parent, worldPositionStays: false);
		gameObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
		Light light = gameObject.AddComponent<Light>();
		light.type = LightType.Directional;
		light.intensity = 1.6f;
		GameObject gameObject2 = new GameObject("Validation Camera");
		gameObject2.transform.SetParent(parent, worldPositionStays: false);
		gameObject2.transform.position = new Vector3(0f, 7f, -42f);
		gameObject2.transform.rotation = Quaternion.Euler(8f, 0f, 0f);
		Camera camera = gameObject2.AddComponent<Camera>();
		camera.clearFlags = CameraClearFlags.Color;
		camera.backgroundColor = new Color(0.14f, 0.16f, 0.2f);
		camera.nearClipPlane = 0.1f;
		camera.farClipPlane = 250f;
	}

	private static void Screenshot(GameObject subject, Bounds bounds, string filePath, Vector3? viewDirection = null)
	{
		GameObject cameraObject = new GameObject("ValidationScreenshotCamera");
		GameObject lightObject = new GameObject("ValidationScreenshotLight");
		Transform[] subjectTransforms = subject.GetComponentsInChildren<Transform>(includeInactive: true);
		int[] originalLayers = new int[subjectTransforms.Length];
		try
		{
			for (int i = 0; i < subjectTransforms.Length; i++)
			{
				originalLayers[i] = subjectTransforms[i].gameObject.layer;
				subjectTransforms[i].gameObject.layer = 31;
			}
			Light light = lightObject.AddComponent<Light>();
			light.type = LightType.Directional;
			light.intensity = 1.8f;
			light.cullingMask = int.MinValue;
			lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.clearFlags = CameraClearFlags.Color;
			camera.backgroundColor = new Color(0.14f, 0.16f, 0.2f);
			camera.cullingMask = int.MinValue;
			float distance = Mathf.Max(Mathf.Max(bounds.size.magnitude, 1f) * 1.1f, 2.5f);
			Vector3 direction = (viewDirection ?? new Vector3(0.7f, 0.2f, -1f)).normalized;
			camera.transform.position = bounds.center + direction * distance;
			camera.transform.LookAt(bounds.center);
			camera.nearClipPlane = Mathf.Clamp(distance * 0.01f, 0.01f, 0.3f);
			camera.farClipPlane = distance * 10f;
			RenderTexture renderTexture = (camera.targetTexture = new RenderTexture(900, 1100, 24));
			camera.Render();
			RenderTexture active = RenderTexture.active;
			RenderTexture.active = renderTexture;
			Texture2D texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, mipChain: false);
			texture.ReadPixels(new Rect(0f, 0f, renderTexture.width, renderTexture.height), 0, 0);
			texture.Apply();
			RenderTexture.active = active;
			camera.targetTexture = null;
			Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? "Assets/_Project/LocalSamples/AC6SkinnedModular/Reports");
			File.WriteAllBytes(filePath, texture.EncodeToPNG());
			UnityEngine.Object.DestroyImmediate(texture);
			renderTexture.Release();
			UnityEngine.Object.DestroyImmediate(renderTexture);
		}
		finally
		{
			for (int j = 0; j < subjectTransforms.Length; j++)
			{
				if (subjectTransforms[j] != null)
				{
					subjectTransforms[j].gameObject.layer = originalLayers[j];
				}
			}
			UnityEngine.Object.DestroyImmediate(cameraObject);
			UnityEngine.Object.DestroyImmediate(lightObject);
		}
	}

	private static void WriteReport(ValidationReport report)
	{
		Directory.CreateDirectory("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports");
		File.WriteAllText(Path.Combine("Assets/_Project/LocalSamples/AC6SkinnedModular/Reports", "skinned_modular_validation.txt"), report.Log.ToString());
		AssetDatabase.Refresh();
	}

	private static string Format(Vector3 value)
	{
		return $"({value.x:0.###},{value.y:0.###},{value.z:0.###})";
	}
}
