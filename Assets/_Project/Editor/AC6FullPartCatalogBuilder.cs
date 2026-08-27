using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class AC6FullPartCatalogBuilder
{
	private const string RadiatorMigrationSessionKey =
		"ACunicombat.StandardRadiatorMigration.v1";

	[InitializeOnLoadMethod]
	private static void ScheduleStandardRadiatorMigration()
	{
		if (SessionState.GetBool(RadiatorMigrationSessionKey, false))
		{
			return;
		}

		EditorApplication.delayCall += () =>
		{
			if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
			{
				return;
			}

			SessionState.SetBool(RadiatorMigrationSessionKey, true);
			const string partPath =
				"Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts/AC6_SKIN_SAMPLE_RADIATOR.asset";
			MechPartCatalog catalog = AssetDatabase.LoadAssetAtPath<MechPartCatalog>(
				"Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6FullPartCatalog.asset");
			if (AssetDatabase.LoadAssetAtPath<MechPartDefinition>(partPath) != null &&
				catalog?.Find("AC6_SKIN_SAMPLE_RADIATOR") != null)
			{
				return;
			}

			if (!BuildAvailableCatalog(out string report))
			{
				Debug.LogError("[AC6FullPartCatalog] 标准散热器自动迁移失败：" + report);
			}
			else
			{
				Debug.Log("[AC6FullPartCatalog] 标准散热器已自动加入目录与默认配装。");
			}
		};
	}

	[Serializable]
	private sealed class FullManifest
	{
		public FullManifestEntry[] entries;
	}

	[Serializable]
	private sealed class FullManifestEntry
	{
		public string key;

		public string kind;

		public int slot;

		public string glbAssetPath;

		public bool success;

		public float maxPositionError;

		public float maxRotationErrorDeg;
	}

	private sealed class FcsPartSource
	{
		public readonly string ParamId;

		public readonly string Name;

		public readonly int IconId;

		public readonly float Weight;

		public readonly float EnergyLoad;

		public readonly float MissileLockCorrection;

		public readonly float MultiLockCorrection;

		public readonly float CloseRangeAssist;

		public readonly float MediumRangeAssist;

		public readonly float LongRangeAssist;

		public FcsPartSource(string paramId, string name, int iconId, float weight, float energyLoad, float missileLockCorrection, float multiLockCorrection, float closeRangeAssist, float mediumRangeAssist, float longRangeAssist)
		{
			ParamId = paramId;
			Name = name;
			IconId = iconId;
			Weight = weight;
			EnergyLoad = energyLoad;
			MissileLockCorrection = missileLockCorrection;
			MultiLockCorrection = multiLockCorrection;
			CloseRangeAssist = closeRangeAssist;
			MediumRangeAssist = mediumRangeAssist;
			LongRangeAssist = longRangeAssist;
		}
	}

	private sealed class GeneratorPartSource
	{
		public readonly string ParamId;

		public readonly string Name;

		public readonly int IconId;

		public readonly float Weight;

		public readonly float EnergyCapacity;

		public readonly float EnergyOutput;

		public readonly float EnergyRecharge;

		public readonly float SupplyRecovery;

		public readonly float PostRecoveryEnergySupply;

		public readonly float EnergyWeaponSpecialization;

		public readonly int BoosterEmitterColorId;

		public GeneratorPartSource(string paramId, string name, int iconId, float weight, float energyCapacity, float energyOutput, float energyRecharge, float supplyRecovery, float postRecoveryEnergySupply, float energyWeaponSpecialization, int boosterEmitterColorId)
		{
			ParamId = paramId;
			Name = name;
			IconId = iconId;
			Weight = weight;
			EnergyCapacity = energyCapacity;
			EnergyOutput = energyOutput;
			EnergyRecharge = energyRecharge;
			SupplyRecovery = supplyRecovery;
			PostRecoveryEnergySupply = postRecoveryEnergySupply;
			EnergyWeaponSpecialization = energyWeaponSpecialization;
			BoosterEmitterColorId = boosterEmitterColorId;
		}
	}

	private const string GeneratedRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated";

	private const string SamplePartRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts";

	private const string ResourceRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources";

	private const string DefinitionRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6Parts";

	private const string CatalogPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6FullPartCatalog.asset";

	private const string FullManifestPath = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/FullCatalog/AC6FullPartManifest.json";

	private const string InternalThumbnailRoot = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/InternalParts/Thumbnails";

	private static readonly FcsPartSource[] OriginalFcsParts = new FcsPartSource[10]
	{
		new FcsPartSource("70000000", "FCS-G1/P01", 8960, 80f, 198f, 1.2f, 1.6f, 0.38f, 0.27f, 0.2f),
		new FcsPartSource("70010000", "FCS-G2/P05", 8961, 100f, 232f, 0.95f, 1.4f, 0.42f, 0.8f, 0.26f),
		new FcsPartSource("70010100", "FCS-G2/P10SLT", 8962, 120f, 209f, 0.72f, 1.1f, 0.31f, 0.41f, 0.29f),
		new FcsPartSource("70010200", "FCS-G2/P12SML", 8963, 130f, 278f, 0.82f, 0.8f, 0.28f, 0.53f, 0.3f),
		new FcsPartSource("70010300", "FC-006 ABBOT", 8964, 90f, 266f, 1.26f, 1.54f, 0.74f, 0.38f, 0.05f),
		new FcsPartSource("70010400", "FC-008 TALBOT", 8965, 140f, 312f, 0.97f, 1.38f, 0.65f, 0.57f, 0.32f),
		new FcsPartSource("70020000", "VE-21A", 8966, 85f, 320f, 1.35f, 1.2f, 0.45f, 0.72f, 0.92f),
		new FcsPartSource("70020100", "VE-21B", 8967, 160f, 388f, 1.03f, 1.3f, 0.15f, 0.85f, 0.9f),
		new FcsPartSource("70030000", "IA-C01F: OCELLUS", 8968, 130f, 292f, 1.55f, 1.5f, 0.81f, 0.12f, 0.03f),
		new FcsPartSource("70030100", "IB-C03F: WLT 001", 8969, 150f, 486f, 0.98f, 1.34f, 0.58f, 0.77f, 0.54f)
	};

	private static readonly GeneratorPartSource[] OriginalGeneratorParts = new GeneratorPartSource[14]
	{
		new GeneratorPartSource("65000000", "AG-J-098 JOSO", 8980, 3420f, 2300f, 2600f, 1.3f, 2f, 400f, 72f, 109),
		new GeneratorPartSource("65010400", "AG-E-013 YABA", 8985, 5080f, 3060f, 3160f, 0.78f, 1.8f, 1000f, 95f, 109),
		new GeneratorPartSource("65040200", "AG-T-005 HOKUSHI", 8990, 7080f, 3420f, 3810f, 0.95f, 2.7f, 1020f, 90f, 109),
		new GeneratorPartSource("65010000", "DF-GN-02 LING-TAI", 8981, 3860f, 2240f, 2340f, 0.5f, 1.2f, 280f, 61f, 109),
		new GeneratorPartSource("65010100", "DF-GN-06 MING-TANG", 8982, 6320f, 3140f, 3310f, 0.69f, 1.5f, 440f, 76f, 109),
		new GeneratorPartSource("65040000", "DF-GN-08 SAN-TAI", 8991, 10060f, 4420f, 3210f, 0.98f, 1.9f, 620f, 88f, 109),
		new GeneratorPartSource("65010200", "VP-20S", 8983, 3800f, 2620f, 3400f, 1.12f, 2.3f, 1200f, 94f, 65645),
		new GeneratorPartSource("65010300", "VP-20C", 8984, 5320f, 2870f, 3670f, 1.02f, 2.2f, 1230f, 100f, 65645),
		new GeneratorPartSource("65040100", "VP-20D", 8992, 11030f, 3840f, 4430f, 1.21f, 2.35f, 1400f, 108f, 65645),
		new GeneratorPartSource("65020000", "VE-20A", 8986, 3590f, 2570f, 3180f, 1.27f, 2.4f, 600f, 120f, 65645),
		new GeneratorPartSource("65020100", "VE-20B", 8987, 5860f, 3300f, 2890f, 1.31f, 2.55f, 800f, 150f, 65645),
		new GeneratorPartSource("65040300", "VE-20C", 8993, 10130f, 3690f, 4090f, 1.8f, 2.65f, 720f, 128f, 65645),
		new GeneratorPartSource("65030000", "IA-C01G: AORTA", 8988, 4330f, 3000f, 3500f, 4.2f, 3f, 2000f, 105f, 131182),
		new GeneratorPartSource("65030100", "IB-C03G: NGI 000", 8989, 8950f, 4400f, 4340f, 4f, 3.2f, 3100f, 102f, 131182)
	};

	private static readonly HashSet<string> TestedWeaponKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"WP_L_1310",
		"WP_R_1310",
		"WP_L_1610",
		"WP_R_1610",
		"WP_L_1630",
		"WP_R_1630",
		"WP_L_1720",
		"WP_R_1720",
		"WP_L_1810",
		"WP_R_1810",
		"WP_L_1820",
		"WP_R_1820",
		"WP_L_1830",
		"WP_R_1830",
		"WP_L_1910",
		"WP_R_1910",
		"WP_L_2020",
		"WP_R_2020",
		"WP_L_4250",
		"WP_R_4250",
		"WP_L_4290",
		"WP_R_4290",
		"WP_L_4300",
		"WP_R_4300",
		"WP_L_4301",
		"WP_R_4301",
		"WP_L_4302",
		"WP_R_4302",
		"WP_L_4340",
		"WP_R_4340",
		"WP_L_5110",
		"WP_R_5110"
	};

	private static readonly HashSet<string> TestedFrameKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"HD_M_1010",
		"HD_M_2010",
		"AM_M_1010",
		"AM_M_3010",
		"BD_M_1010",
		"LG_M_1010",
		"LG_M_2010"
	};

	private static readonly HashSet<string> TestedInternalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"BS_A_1010",
		"AC6_SKIN_SAMPLE_GENERATOR",
		"AC6_SKIN_SAMPLE_RADIATOR",
		"AC6_SKIN_SAMPLE_FCS",
		"AC6_SKIN_SAMPLE_RADAR",
		"AC6_SKIN_SAMPLE_DATALINK"
	};

	private static readonly Dictionary<string, string> KnownPartKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["AC6_NATIVE_WP_L_1310"] = "WP_L_1310",
		["AC6_NATIVE_WP_R_1610"] = "WP_R_1610",
		["AC6_NATIVE_WP_L_4250"] = "WP_L_4250",
		["AC6_NATIVE_WP_L_4290"] = "WP_L_4290",
		["AC6_NATIVE_WP_R_4290"] = "WP_R_4290",
		["AC6_NATIVE_WP_L_4300"] = "WP_L_4300",
		["AC6_NATIVE_WP_R_4300"] = "WP_R_4300",
		["AC6_NATIVE_WP_L_4301"] = "WP_L_4301",
		["AC6_NATIVE_WP_R_4301"] = "WP_R_4301",
		["AC6_NATIVE_WP_L_5110"] = "WP_L_5110",
		["AC6_NATIVE_WP_R_5110"] = "WP_R_5110"
	};

	private static bool buildQueued;

	internal static int OriginalFcsSourceCount => OriginalFcsParts.Length;

	internal static int OriginalGeneratorSourceCount => OriginalGeneratorParts.Length;

	internal static bool IsTestedWeapon(string partId)
	{
		return TestedWeaponKeys.Contains(ResolveSourceKey(partId));
	}

	internal static bool IsTestedPart(string partId)
	{
		string sourceKey = ResolveSourceKey(partId);
		return IsTestedWeapon(partId) || TestedFrameKeys.Contains(sourceKey) ||
			TestedInternalKeys.Contains(sourceKey) || TestedInternalKeys.Contains(partId);
	}

	[InitializeOnLoadMethod]
	private static void ScheduleInitialCatalogBuild()
	{
		QueueBuild();
	}

	[MenuItem("Tools/ACunicombat/AC6/Build Full Part Catalog")]
	public static void BuildMenu()
	{
		if (BuildAvailableCatalog(out var report))
		{
			Debug.Log("[AC6FullPartCatalog] " + report);
			EditorUtility.DisplayDialog("AC6 Full Part Catalog", report, "OK");
		}
		else
		{
			Debug.LogError("[AC6FullPartCatalog] " + report);
			EditorUtility.DisplayDialog("AC6 Full Part Catalog", report, "OK");
		}
	}

	public static void RunBatch()
	{
		bool success = BuildAvailableCatalog(out var report);
		if (success)
		{
			Debug.Log("[AC6FullPartCatalog] " + report);
		}
		else
		{
			Debug.LogError("[AC6FullPartCatalog] " + report);
		}

		if (Application.isBatchMode)
		{
			EditorApplication.Exit(success ? 0 : 1);
		}
	}

	public static bool BuildAvailableCatalog(out string report)
	{
		report = string.Empty;
		AC6SkinnedModularSampleBuilder.RefreshGeneratedFrameGameplayData(out var refreshedFrameCount);
		EnsureFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources");
		EnsureFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6Parts");
		List<MechPartCatalogEntry> entries = new List<MechPartCatalogEntry>();
		HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<MechPartDefinition> sampleParts = (from mechPartDefinition in LoadAssets<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts")
			orderby mechPartDefinition.Slot
			select mechPartDefinition).ThenBy((MechPartDefinition mechPartDefinition) => mechPartDefinition.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
		for (int i = 0; i < sampleParts.Count; i++)
		{
			MechPartDefinition source = sampleParts[i];
			if (!(source == null) && ids.Add(source.PartId))
			{
				MechPartSignatureDefaults.ApplyIfMissing(source.Stats, source.Slot);
				EditorUtility.SetDirty(source);
				MechPartDefinition clone = ClonePartToResources(source);
				string key = ResolveSourceKey(source.PartId);
				if (!string.IsNullOrWhiteSpace(key))
				{
					sourceKeys.Add(key);
				}
				bool tested = IsTestedPart(source.PartId);
				string note = tested
					? BuildTestedNote(source)
					: (MechSlotRules.IsWeaponSlot(source.Slot)
						? "已导入视觉资源，但尚未完成当前战斗系统的功能验证。"
						: "视觉资源已导入；结构、热管理与部件专属性能尚未完成战斗验证。");
				entries.Add(CreateEntry(clone, tested ? MechPartTestStatus.Tested : MechPartTestStatus.Untested, note));
			}
		}
		int internalCount = AddOriginalInternalParts(entries, ids);
		int importedCount = 0;
		int skippedCount = 0;
		FullManifest manifest = LoadManifest();
		if (manifest?.entries != null)
		{
			for (int i2 = 0; i2 < manifest.entries.Length; i2++)
			{
				FullManifestEntry item = manifest.entries[i2];
				if (item == null || !item.success || string.IsNullOrWhiteSpace(item.key) || sourceKeys.Contains(item.key))
				{
					skippedCount++;
					continue;
				}
				GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(item.glbAssetPath);
				if (model == null)
				{
					skippedCount++;
					continue;
				}
				string id = "AC6_FULL_" + item.key;
				if (!ids.Add(id))
				{
					skippedCount++;
					continue;
				}
				GameObject previewModel = LoadAssembledWeaponPreview(item) ?? model;
				MechPartDefinition part = BuildImportedPart(item, id, model, previewModel);
				bool needsRigReview = IsRigReviewRequired(item);
				bool tested = IsTestedPart(item.key);
				MechPartTestStatus status = tested
					? MechPartTestStatus.Tested
					: (needsRigReview ? MechPartTestStatus.NeedsReview : MechPartTestStatus.Untested);
				string note = tested
					? BuildTestedNote(part)
					: (MechSlotRules.IsWeaponSlot(part.Slot)
						? "视觉资源已批量导入；枪口、动作、弹药和火控适配尚未验证。"
						: (needsRigReview ? $"统一骨架对齐偏差较大：位置 {item.maxPositionError:0.000} m，旋转 {item.maxRotationErrorDeg:0.0}°；允许实验安装，但动画与挂点需要复核。" : "视觉资源已导入；结构、热管理与部件专属性能尚未完成战斗验证。"));
				entries.Add(CreateEntry(part, status, note));
				importedCount++;
			}
		}
		MechPartCatalog catalog = AssetDatabase.LoadAssetAtPath<MechPartCatalog>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6FullPartCatalog.asset");
		if (catalog == null)
		{
			catalog = ScriptableObject.CreateInstance<MechPartCatalog>();
			AssetDatabase.CreateAsset(catalog, "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6FullPartCatalog.asset");
		}
		catalog.EditorConfigure(entries);
		EditorUtility.SetDirty(catalog);
		AssetDatabase.SaveAssets();
		AssetDatabase.Refresh();
		report = $"目录已生成：{entries.Count} 个部件，刷新框架玩法数据 {refreshedFrameCount} 个，当前测试部件 {sampleParts.Count} 个，原版内部件 {internalCount} 个，全量三维部件新增 {importedCount} 个，跳过 {skippedCount} 项。";
		return entries.Count > 0;
	}

	private static string BuildTestedNote(MechPartDefinition part)
	{
		return part.Slot switch
		{
			MechPartSlot.Head => "标准头部基准数值已录入，已验证视觉装配，并作为当前光电、红外与激光测距测试基线。",
			MechPartSlot.Arms => "标准手臂基准数值已录入，已验证视觉装配、武器挂载、瞄准动作与内置喷口数据。",
			MechPartSlot.Core => "标准核心基准数值已录入，已验证视觉装配、热管理、姿态控制与内置喷口数据。",
			MechPartSlot.Legs => "标准腿部基准数值已录入，已验证视觉装配、承载、上升、反向、横向与 RCS 喷口数据。",
			MechPartSlot.Booster => "标准主推进器基准数值已录入，已验证视觉装配、主向推进、AB、QB 与装配驱动功耗。",
			MechPartSlot.Generator => "标准发电机基准数值已录入，储能容量、回充功率与启动参数已接入机动能量系统。",
			MechPartSlot.Radiator => "标准真空散热器基准数值已录入，额定散热能力已接入机体运行时热特征冷却。",
			MechPartSlot.FireControlSystem => "当前火控基准数值已录入，已验证航迹解算、武器授权、制导通道与点防御流程。",
			MechPartSlot.Radar => "当前雷达基准数值已录入，已验证搜索、TWS、STT、导弹识别与航迹维持流程。",
			MechPartSlot.DataLinkTerminal => "标准数据链终端参数已录入，已接入友军识别、航迹共享、制导上行、带宽与通信延迟。",
			_ => "当前战斗原型正在使用，已验证安装、开火、弹药与装填流程。"
		};
	}

	private static int AddOriginalInternalParts(ICollection<MechPartCatalogEntry> entries, ISet<string> ids)
	{
		int added = 0;
		for (int i = 0; i < OriginalFcsParts.Length; i++)
		{
			FcsPartSource source = OriginalFcsParts[i];
			string id = "AC6_FCS_" + source.ParamId;
			if (ids.Add(id))
			{
				MechInternalPartPerformance performance = new MechInternalPartPerformance
				{
					kind = MechInternalPartPerformanceKind.FireControlSystem,
					sourceParamId = source.ParamId,
					sourceIconId = source.IconId,
					sourceEnergyLoad = source.EnergyLoad,
					missileLockCorrection = source.MissileLockCorrection,
					multiLockCorrection = source.MultiLockCorrection,
					closeRangeAssist = source.CloseRangeAssist,
					mediumRangeAssist = source.MediumRangeAssist,
					longRangeAssist = source.LongRangeAssist
				};
				MechPartDefinition part = BuildOriginalInternalPart(id, source.Name, MechPartSlot.FireControlSystem, source.Weight, source.IconId, performance);
				entries.Add(CreateEntry(part, MechPartTestStatus.NeedsReview, "原版参数与菜单图已导入；尚未映射到本项目的航迹、锁定和制导仿真，暂不作为可用战斗部件。"));
				added++;
			}
		}
		for (int j = 0; j < OriginalGeneratorParts.Length; j++)
		{
			GeneratorPartSource source2 = OriginalGeneratorParts[j];
			string id2 = "AC6_GEN_" + source2.ParamId;
			if (ids.Add(id2))
			{
				MechInternalPartPerformance performance2 = new MechInternalPartPerformance
				{
					kind = MechInternalPartPerformanceKind.Generator,
					sourceParamId = source2.ParamId,
					sourceIconId = source2.IconId,
					energyCapacity = source2.EnergyCapacity,
					energyOutput = source2.EnergyOutput,
					energyRecharge = source2.EnergyRecharge,
					supplyRecovery = source2.SupplyRecovery,
					postRecoveryEnergySupply = source2.PostRecoveryEnergySupply,
					energyWeaponSpecialization = source2.EnergyWeaponSpecialization,
					boosterEmitterColorId = source2.BoosterEmitterColorId
				};
				MechPartDefinition part2 = BuildOriginalInternalPart(id2, source2.Name, MechPartSlot.Generator, source2.Weight, source2.IconId, performance2);
				entries.Add(CreateEntry(part2, MechPartTestStatus.NeedsReview, "原版参数与菜单图已导入；EN 容量和输出尚未换算为本项目的功率与热容量模型，暂不作为可用战斗部件。"));
				added++;
			}
		}
		return added;
	}

	private static MechPartDefinition BuildOriginalInternalPart(string id, string displayName, MechPartSlot slot, float sourceWeight, int iconId, MechInternalPartPerformance performance)
	{
		MechPartDefinition part = LoadOrCreate<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6Parts/" + Sanitize(id) + ".asset");
		string thumbnailPath = string.Format("{0}/WP_A_{1}.png", "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/InternalParts/Thumbnails", iconId);
		if (File.Exists(thumbnailPath) && string.IsNullOrWhiteSpace(AssetDatabase.AssetPathToGUID(thumbnailPath)))
		{
			AssetDatabase.ImportAsset(thumbnailPath, ImportAssetOptions.ForceSynchronousImport);
		}
		Texture2D previewImage = AssetDatabase.LoadAssetAtPath<Texture2D>(thumbnailPath);
		MechPartStats newStats = new MechPartStats
		{
			massKg = sourceWeight
		};
		MechMountPointDefinition[] newMountPoints = Array.Empty<MechMountPointDefinition>();
		string[] newTags = new string[3] { "internal", "ac6-original-data", "reference-only" };
		Texture2D newPreviewImage = previewImage;
		part.EditorConfigure(id, displayName, slot, MechPartCategory.Internal, null, newStats, newMountPoints, newTags, null, MechVisualRigMode.MountedPrefab, null, MechPartSlot.None, null, shouldInstantiateVisualAtAllMounts: false, null, null, shouldPlayDefaultEquipmentActionOnAssemble: false, null, null, null, null, newPreviewImage, performance);
		return part;
	}

	private static MechPartDefinition BuildImportedPart(FullManifestEntry item, string id, GameObject model, GameObject previewModel)
	{
		MechPartDefinition mechPartDefinition = LoadOrCreate<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6Parts/" + Sanitize(id) + ".asset");
		MechPartSlot slot = (MechPartSlot)item.slot;
		bool isFrame = item.kind.StartsWith("frame", StringComparison.OrdinalIgnoreCase);
		bool isBooster = slot == MechPartSlot.Booster;
		string[] tags = BuildTags(item, slot);
		AnimationClip clip = (isFrame ? (from candidate in AssetDatabase.LoadAllAssetsAtPath(item.glbAssetPath).OfType<AnimationClip>()
			where candidate != null && candidate.length > 0.01f
			orderby candidate.length descending
			select candidate).FirstOrDefault() : null);
		string newDisplayName = DisplayName(item.key);
		int newCategory = ((!isFrame) ? (isBooster ? 10 : 20) : 0);
		MechPartStats newStats = BuildStats(slot);
		MechPartSignatureDefaults.ApplyIfMissing(newStats, slot);
		if (string.Equals(item.key, "WP_L_1610", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(item.key, "WP_R_1610", StringComparison.OrdinalIgnoreCase))
		{
			newStats.massKg = 4310f;
		}
		else if (string.Equals(item.key, "WP_L_1910", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(item.key, "WP_R_1910", StringComparison.OrdinalIgnoreCase))
		{
			newStats.massKg = 5800f;
			newStats.idlePowerDrawMw = 0.45f;
			newStats.peakPowerDrawMw = 7.8f;
			newStats.idleHeatMw = 0.18f;
			newStats.activeHeatMw = 4.6f;
			newStats.heatGenerationMw = 4.6f;
			newStats.heatCapacityMj = 18f;
		}
		IReadOnlyList<MechMountPointDefinition> newMountPoints;
		if (!isFrame)
		{
			IReadOnlyList<MechMountPointDefinition> readOnlyList = Array.Empty<MechMountPointDefinition>();
			newMountPoints = readOnlyList;
		}
		else
		{
			newMountPoints = BuildFrameMountPoints(slot);
		}
		IReadOnlyList<MechEquipmentAnimationDefinition> equipmentAnimations = null;
		string defaultEquipmentActionId = null;
		if (TryLoadNativeEquipment(item.key, out GameObject nativeModel, out List<MechEquipmentAnimationDefinition> nativeActions))
		{
			model = nativeModel;
			previewModel = nativeModel;
			equipmentAnimations = nativeActions;
			defaultEquipmentActionId = "a000_800000";
		}
		mechPartDefinition.EditorConfigure(id, newDisplayName, slot, (MechPartCategory)newCategory, model, newStats, newMountPoints, tags, null, isFrame ? MechVisualRigMode.SynchronizedSkinned : MechVisualRigMode.MountedPrefab, clip, AncestorDriver(slot), AncestorBones(slot), isBooster, equipmentAnimations, defaultEquipmentActionId, shouldPlayDefaultEquipmentActionOnAssemble: false, Vector3.zero, isBooster ? new Vector3(180f, 0f, 0f) : (MechSlotRules.IsWeaponSlot(slot) ? new Vector3(0f, 180f, 0f) : Vector3.zero), Vector3.one, previewModel, newHeadSensorPerformance: BuildHeadSensorPerformance(slot), newArmPerformance: BuildArmPerformance(slot, 1f, item.key), newCorePerformance: BuildCorePerformance(slot, 1f, item.key), newLegPerformance: BuildLegPerformance(slot, 1f), newBoosterPerformance: BuildBoosterPerformance(slot, item.key), newWeaponPerformance: AC6TestWeaponProfileFactory.Create(id));
		EditorUtility.SetDirty(mechPartDefinition);
		return mechPartDefinition;
	}

	private static bool TryLoadNativeEquipment(
		string key,
		out GameObject model,
		out List<MechEquipmentAnimationDefinition> actions)
	{
		model = null;
		actions = null;
		// Prefer the animated native export whenever the pipeline produced one.
		string path = "Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/NativeEquipment/" +
			key.ToUpperInvariant() + "_NATIVE.glb";
		if (!File.Exists(path))
		{
			return false;
		}

		AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
		model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
		if (model == null)
		{
			return false;
		}

		var seenActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		actions = new List<MechEquipmentAnimationDefinition>();
		foreach (AnimationClip candidate in AssetDatabase.LoadAllAssetsAtPath(path)
			.OfType<AnimationClip>()
			.Where(candidate => candidate != null && candidate.length > 0.001f)
			.OrderBy(candidate => candidate.name, StringComparer.OrdinalIgnoreCase))
		{
			var match = System.Text.RegularExpressions.Regex.Match(
				candidate.name,
				"a\\d{3}_\\d+",
				System.Text.RegularExpressions.RegexOptions.IgnoreCase |
				System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			if (!match.Success)
			{
				continue;
			}

			string actionId = match.Value.ToLowerInvariant();
			if (seenActionIds.Add(actionId))
			{
				actions.Add(new MechEquipmentAnimationDefinition(
					actionId,
					candidate,
					string.Equals(actionId, "a000_800201", StringComparison.OrdinalIgnoreCase)));
			}
		}
		return actions.Count > 0;
	}

	private static MechPartDefinition ClonePartToResources(MechPartDefinition source)
	{
		MechPartDefinition mechPartDefinition = LoadOrCreate<MechPartDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Resources/AC6Parts/" + Sanitize(source.PartId) + ".asset");
		MechWeaponPerformance factoryPerformance = AC6TestWeaponProfileFactory.Create(source.PartId);
		MechWeaponPerformance weaponPerformance = factoryPerformance != null && factoryPerformance.IsConfigured
			? factoryPerformance
			: source.WeaponPerformance;
		mechPartDefinition.EditorConfigure(source.PartId, source.DisplayName, source.Slot, source.Category, source.ModelPrefab, source.Stats, source.MountPoints, source.Tags, source.MaterialOverrides, source.VisualRigMode, source.SkinnedAnimationClip, source.AncestorDriverSlot, source.AncestorDrivenBoneNames, source.InstantiateVisualAtAllMounts, source.EquipmentAnimations, source.DefaultEquipmentActionId, source.PlayDefaultEquipmentActionOnAssemble, source.LocalPosition, source.LocalEulerAngles, source.LocalScale, source.PreviewModelPrefab, source.PreviewImage, source.InternalPerformance, source.HeadSensorPerformance, source.ArmPerformance, source.CorePerformance, source.LegPerformance, source.BoosterPerformance, source.GeneratorPerformance, source.RadarPerformance, source.FireControlPerformance, weaponPerformance, source.DataLinkPerformance, source.RadiatorPerformance);
		EditorUtility.SetDirty(mechPartDefinition);
		return mechPartDefinition;
	}

	private static GameObject LoadAssembledWeaponPreview(FullManifestEntry item)
	{
		if (item == null || string.IsNullOrWhiteSpace(item.key) || !item.key.StartsWith("WP_", StringComparison.OrdinalIgnoreCase) || item.key.Length < 9)
		{
			return null;
		}
		string code = item.key.Substring(item.key.Length - 4);
		return AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/FullCatalog/Equipment/WP_A_" + code + "_FULL.glb");
	}

	private static MechPartCatalogEntry CreateEntry(MechPartDefinition part, MechPartTestStatus status, string note)
	{
		string path = AssetDatabase.GetAssetPath(part);
		string marker = "/Resources/";
		int index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
		string resourcePath = ((index >= 0) ? Path.ChangeExtension(path.Substring(index + marker.Length), null).Replace('\\', '/') : string.Empty);
		return new MechPartCatalogEntry(part.PartId, part.DisplayName, part.Slot, part.Category, resourcePath, status, note);
	}

	private static string[] BuildTags(FullManifestEntry item, MechPartSlot slot)
	{
		List<string> tags = new List<string> { "ac6-full-catalog", item.kind };
		if (MechSlotRules.IsWeaponSlot(slot))
		{
			tags.Add("weapon");
			if (slot == MechPartSlot.LeftHandWeapon && TryCode(item.key, out var code) && code < 1000)
			{
				tags.Add("melee");
			}
		}
		return tags.ToArray();
	}

	private static MechPartStats BuildStats(MechPartSlot slot)
	{
		switch (slot)
		{
		case MechPartSlot.Head:
			return new MechPartStats
			{
				massKg = 1700f,
				armorPoints = 260f,
				structurePoints = 180f,
				peakPowerDrawMw = 1.1f
			};
		case MechPartSlot.Core:
			return new MechPartStats
			{
				massKg = 12000f,
				armorPoints = 1750f,
				structurePoints = 1450f,
				idlePowerDrawMw = 0.95f,
				peakPowerDrawMw = 3.8f,
				idleHeatMw = 0.28f,
				activeHeatMw = 1.25f,
				heatGenerationMw = 1.25f,
				heatDissipationMw = 1.5f,
				heatCapacityMj = 240f,
				hardpointCapacityKg = 16000f,
				rcsThrustKn = 360f
			};
		case MechPartSlot.Arms:
			return new MechPartStats
			{
				massKg = 6900f,
				armorPoints = 850f,
				structurePoints = 680f,
				idlePowerDrawMw = 0.55f,
				peakPowerDrawMw = 2.6f,
				idleHeatMw = 0.14f,
				activeHeatMw = 0.95f,
				heatGenerationMw = 0.95f,
				heatCapacityMj = 80f,
				weaponRecoilControl = 58f
			};
		case MechPartSlot.Legs:
			return new MechPartStats
			{
				massKg = 9800f,
				armorPoints = 1250f,
				structurePoints = 1050f,
				idlePowerDrawMw = 0.8f,
				peakPowerDrawMw = 1.6f,
				idleHeatMw = 0.22f,
				activeHeatMw = 0.72f,
				heatGenerationMw = 0.72f,
				heatDissipationMw = 0.5f,
				heatCapacityMj = 160f
			};
		case MechPartSlot.Booster:
			return new MechPartStats
			{
				massKg = 2600f,
				idlePowerDrawMw = 0.3f,
				peakPowerDrawMw = 24f,
				idleHeatMw = 0.14f,
				activeHeatMw = 8.5f,
				heatGenerationMw = 8.5f,
				heatCapacityMj = 90f
			};
		default:
			if (!MechSlotRules.IsWeaponSlot(slot))
			{
				return new MechPartStats();
			}
			return new MechPartStats
			{
				massKg = 4500f,
				peakPowerDrawMw = 4f
			};
		}
	}

	private static MechBoosterPerformance BuildBoosterPerformance(MechPartSlot slot, string key)
	{
		if (slot != MechPartSlot.Booster)
		{
			return null;
		}

		TryCode(key, out var code);
		return new MechBoosterPerformance
		{
			mainThrustKn = code == 3010 ? 720f : code == 2010 ? 610f : 660f,
			assaultBoostThrustKn = code == 3010 ? 3900f : code == 2010 ? 3300f : 3500f,
			quickBoostThrustKn = code == 3010 ? 17500f : code == 2010 ? 15500f : 16000f,
			quickBoostDurationS = code == 3010 ? 0.3f : code == 2010 ? 0.25f : 0.28f,
			quickBoostRecoveryS = code == 3010 ? 0.5f : code == 2010 ? 0.36f : 0.42f,
			horizontalVectorLimitDeg = code == 3010 ? 17f : code == 2010 ? 23f : 20f,
			verticalVectorLimitDeg = code == 3010 ? 31f : code == 2010 ? 39f : 35f,
			assaultBoostPowerDrawKw = code == 3010 ? 52000f : code == 2010 ? 40000f : 45000f,
			quickBoostPowerDrawKw = code == 3010 ? 205000f : code == 2010 ? 165000f : 180000f
		};
	}

	private static MechHeadSensorPerformance BuildHeadSensorPerformance(MechPartSlot slot)
	{
		if (slot != MechPartSlot.Head)
		{
			return null;
		}

		return new MechHeadSensorPerformance
		{
			pointingCapability = 68f,
			searchCapability = 62f,
			trackingCapability = 72f,
			identificationCapability = 65f,
			maximumMagnification = 20f,
			referenceHotRangeKm = 30f,
			referenceColdRangeKm = 12f,
			trackCapacity = 8,
			referenceRangeErrorM = 120f,
			laserRangefinderRangeKm = 12f,
			laserRangefinderErrorM = 0.75f,
			supportedBands = MechElectroOpticalBand.Visible |
				MechElectroOpticalBand.Mwir |
				MechElectroOpticalBand.Lwir
		};
	}

	private static MechArmPerformance BuildArmPerformance(MechPartSlot slot, float scale, string partKey)
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
			weaponPointingErrorMrad = string.Equals(partKey, "AM_M_1010", StringComparison.OrdinalIgnoreCase)
				? 0.27f
				: 0.18f * Mathf.Max(0.8f, scale),
			thrusterNozzles = new[]
			{
				new MechArmThrusterNozzle
				{
					nozzleId = "arm-lateral",
					role = MechArmThrusterRole.Lateral,
					localPositionM = new Vector3(0.82f, 0.08f, 0f),
					localThrustDirection = Vector3.left,
					maximumThrustKn = 132f * scale,
					gimbalLimitDeg = 8f,
					maximumPowerKw = 420f * scale,
					maximumHeatKw = 180f * scale
				},
				new MechArmThrusterNozzle
				{
					nozzleId = "arm-rcs-forward",
					role = MechArmThrusterRole.Rcs,
					localPositionM = new Vector3(0.78f, 0.32f, 0.28f),
					localThrustDirection = Vector3.forward,
					maximumThrustKn = 75f * scale,
					maximumPowerKw = 260f * scale,
					maximumHeatKw = 120f * scale
				},
				new MechArmThrusterNozzle
				{
					nozzleId = "arm-rcs-rear",
					role = MechArmThrusterRole.Rcs,
					localPositionM = new Vector3(0.78f, -0.18f, -0.24f),
					localThrustDirection = Vector3.back,
					maximumThrustKn = 75f * scale,
					maximumPowerKw = 260f * scale,
					maximumHeatKw = 120f * scale
				}
			}
		};
	}

	private static MechCorePerformance BuildCorePerformance(MechPartSlot slot, float scale, string sourceKey)
	{
		if (slot != MechPartSlot.Core)
		{
			return null;
		}

		float reverseThrust = sourceKey != null && sourceKey.EndsWith("3010", StringComparison.OrdinalIgnoreCase)
			? 280f * scale
			: sourceKey != null && sourceKey.EndsWith("2010", StringComparison.OrdinalIgnoreCase)
				? 330f * scale
				: 375f * scale;
		float downwardThrust = 573f * scale;
		return new MechCorePerformance
		{
			momentumWheelTurnRateDegPerSecond = 24f / Mathf.Sqrt(Mathf.Max(0.5f, scale)),
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
				new MechLegThrusterNozzle { nozzleId = "leg-primary-front", localPositionM = new Vector3(0.42f, -0.72f, 0.3f), localThrustDirection = Vector3.up, maximumThrustKn = 375f * scale, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18.82427f, lateralGimbalLimitDeg = 0.5f, maximumPowerKw = 250f * scale, maximumHeatKw = 100f * scale },
				new MechLegThrusterNozzle { nozzleId = "leg-primary-rear", localPositionM = new Vector3(0.42f, -0.78f, -0.24f), localThrustDirection = Vector3.up, maximumThrustKn = 375f * scale, mainGimbalLimitDeg = 10f, reverseGimbalLimitDeg = 18.82427f, lateralGimbalLimitDeg = 0.5f, maximumPowerKw = 250f * scale, maximumHeatKw = 100f * scale }
			},
			lateralThrusterNozzles = new[]
			{
				new MechLegThrusterNozzle { nozzleId = "leg-lateral", localPositionM = new Vector3(0.48f, -0.35f, 0f), localThrustDirection = Vector3.left, maximumThrustKn = 67.9102f * scale, maximumPowerKw = 60f * scale, maximumHeatKw = 20f * scale }
			},
			rcsThrusterNozzles = new[]
			{
				new MechLegThrusterNozzle { nozzleId = "leg-rcs-forward", localPositionM = new Vector3(0.44f, -0.18f, 0.32f), localThrustDirection = Vector3.forward, maximumThrustKn = 60f * scale, maximumPowerKw = 60f * scale, maximumHeatKw = 25f * scale },
				new MechLegThrusterNozzle { nozzleId = "leg-rcs-rear", localPositionM = new Vector3(0.44f, -0.58f, -0.3f), localThrustDirection = Vector3.back, maximumThrustKn = 60f * scale, maximumPowerKw = 60f * scale, maximumHeatKw = 25f * scale }
			}
		};
	}

	private static IReadOnlyList<MechMountPointDefinition> BuildFrameMountPoints(MechPartSlot slot)
	{
		return slot switch
		{
			MechPartSlot.Core => new MechMountPointDefinition[4]
			{
				Mount("Left Booster", MechPartSlot.Booster, MechHardpointType.Internal, "_dmy_150", all: false, 10000f),
				Mount("Right Booster", MechPartSlot.Booster, MechHardpointType.Internal, "_dmy_151", all: false, 10000f),
				Mount("Left Back", MechPartSlot.LeftBackWeapon, MechHardpointType.BackWeapon, "_dmy_13", all: false, 10000f),
				Mount("Right Back", MechPartSlot.RightBackWeapon, MechHardpointType.BackWeapon, "_dmy_14", all: false, 10000f)
			}, 
			MechPartSlot.Arms => new MechMountPointDefinition[3]
			{
				Mount("Left Hand", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_23", all: false, 8000f, MechMountSourceRegion.Any, null, new string[1] { "melee" }),
				Mount("Left Melee", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, "_dmy_25", all: false, 8000f, MechMountSourceRegion.Any, new string[1] { "melee" }),
				Mount("Right Hand", MechPartSlot.RightHandWeapon, MechHardpointType.HandWeapon, "_dmy_20", all: false, 8000f)
			}, 
			_ => Array.Empty<MechMountPointDefinition>(), 
		};
	}

	private static MechMountPointDefinition Mount(string id, MechPartSlot target, MechHardpointType type, string suffix, bool all, float mass, MechMountSourceRegion region = MechMountSourceRegion.Any, string[] required = null, string[] blocked = null)
	{
		return new MechMountPointDefinition
		{
			mountId = id,
			targetSlot = target,
			hardpointType = type,
			sourceTransformNameSuffix = suffix,
			matchAllSourceTransforms = all,
			sourceTransformRegion = region,
			maxMassKg = mass,
			requiredTags = (required ?? Array.Empty<string>()),
			blockedTags = (blocked ?? Array.Empty<string>()),
			localScale = Vector3.one
		};
	}

	private static MechPartSlot AncestorDriver(MechPartSlot slot)
	{
		switch (slot)
		{
		default:
			return MechPartSlot.None;
		case MechPartSlot.Head:
		case MechPartSlot.Arms:
			return MechPartSlot.Core;
		case MechPartSlot.Core:
			return MechPartSlot.Legs;
		}
	}

	private static string[] AncestorBones(MechPartSlot slot)
	{
		return slot switch
		{
			MechPartSlot.Core => new string[4] { "Master", "Root", "Root_Rot_XYZ", "BD_Root" }, 
			MechPartSlot.Head => new string[10] { "Master", "Root", "Root_Rot_XYZ", "BD_Root", "Spine", "Offset_SpineLink", "SpineLink", "Offset_Spine1", "Spine1", "Neck" }, 
			MechPartSlot.Arms => new string[20]
			{
				"Master", "Root", "Root_Rot_XYZ", "BD_Root", "Spine", "Offset_SpineLink", "SpineLink", "Offset_Spine1", "Spine1", "Offset_ClavicleLink",
				"L_ClavicleLink", "L_ClavicleLink_EX", "Offset_L_Clavicle", "L_Clavicle", "L_ArmJoint", "R_ClavicleLink", "Offset_R_Clavicle", "R_Clavicle", "R_ArmJoint", "R_ClavicleLink_EX"
			}, 
			_ => Array.Empty<string>(), 
		};
	}

	private static FullManifest LoadManifest()
	{
		if (!File.Exists("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/FullCatalog/AC6FullPartManifest.json"))
		{
			return null;
		}
		return JsonUtility.FromJson<FullManifest>(File.ReadAllText("Assets/_Project/LocalSamples/AC6SkinnedModular/Raw/FullCatalog/AC6FullPartManifest.json"));
	}

	private static string ResolveSourceKey(string partId)
	{
		if (KnownPartKeys.TryGetValue(partId, out var known))
		{
			return known;
		}
		string[] markers = new string[6] { "WP_", "BS_A_", "HD_M_", "BD_M_", "AM_M_", "LG_M_" };
		for (int i = 0; i < markers.Length; i++)
		{
			int marker = partId.IndexOf(markers[i], StringComparison.OrdinalIgnoreCase);
			if (marker >= 0)
			{
				return partId.Substring(marker);
			}
		}
		return string.Empty;
	}

	private static string DisplayName(string key)
	{
		if (string.Equals(key, "WP_L_1910", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(key, "WP_R_1910", StringComparison.OrdinalIgnoreCase))
		{
			return "DF-GA-08 HU-BEN";
		}
		if (!key.StartsWith("WP_", StringComparison.OrdinalIgnoreCase))
		{
			if (!key.StartsWith("BS_A_", StringComparison.OrdinalIgnoreCase))
			{
				return "AC6 " + key;
			}
			return "AC6 推进器 " + key;
		}
		return "AC6 武器 " + key;
	}

	private static bool TryCode(string key, out int code)
	{
		code = 0;
		return int.TryParse(key.Split('_', StringSplitOptions.None).LastOrDefault(), out code);
	}

	private static bool IsRigReviewRequired(FullManifestEntry item)
	{
		if (item != null && item.kind != null && item.kind.StartsWith("frame", StringComparison.OrdinalIgnoreCase))
		{
			if (!(item.maxPositionError > 0.02f))
			{
				return item.maxRotationErrorDeg > 5f;
			}
			return true;
		}
		return false;
	}

	private static string Sanitize(string value)
	{
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		foreach (char invalid in invalidFileNameChars)
		{
			value = value.Replace(invalid, '_');
		}
		return value;
	}

	private static List<T> LoadAssets<T>(string folder) where T : UnityEngine.Object
	{
		return (from asset in AssetDatabase.FindAssets("t:" + typeof(T).Name, new string[1] { folder }).Select(AssetDatabase.GUIDToAssetPath).Select(AssetDatabase.LoadAssetAtPath<T>)
			where asset != null
			select asset).ToList();
	}

	private static T LoadOrCreate<T>(string path) where T : ScriptableObject
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

	private static void EnsureFolder(string path)
	{
		string[] pieces = path.Split('/', StringSplitOptions.None);
		string current = pieces[0];
		for (int i = 1; i < pieces.Length; i++)
		{
			string text = current + "/" + pieces[i];
			if (!AssetDatabase.IsValidFolder(text))
			{
				AssetDatabase.CreateFolder(current, pieces[i]);
			}
			current = text;
		}
	}

	internal static void QueueBuild()
	{
		if (!buildQueued)
		{
			buildQueued = true;
			EditorApplication.update = (EditorApplication.CallbackFunction)Delegate.Remove(EditorApplication.update, new EditorApplication.CallbackFunction(TryRunQueuedBuild));
			EditorApplication.update = (EditorApplication.CallbackFunction)Delegate.Combine(EditorApplication.update, new EditorApplication.CallbackFunction(TryRunQueuedBuild));
		}
	}

	private static void TryRunQueuedBuild()
	{
		if (!EditorApplication.isCompiling && !EditorApplication.isPlayingOrWillChangePlaymode)
		{
			EditorApplication.update = (EditorApplication.CallbackFunction)Delegate.Remove(EditorApplication.update, new EditorApplication.CallbackFunction(TryRunQueuedBuild));
			buildQueued = false;
			if (AssetDatabase.IsValidFolder("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Parts"))
			{
				BuildAvailableCatalog(out var _);
			}
		}
	}
}
