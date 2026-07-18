using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public sealed class AC6ActionCatalogWindow : EditorWindow
{
    private const string CatalogRelativePath = "Tools/AssetManifests/c0000_action_catalog.json";
    private const string LabelsRelativePath = "Tools/AssetManifests/c0000_action_labels.json";
    private const string CatalogScriptRelativePath = "Tools/BuildAC6ActionCatalog.ps1";
    private const string ClipScriptRelativePath = "Tools/BuildAC6ActionClips.ps1";
    private const string DefaultRawRoot = @"E:\UnityDev\Resources\AC6_ExtractedAssets\chr\AC - c0000";
    private const string RawRootEditorPref = "ACunicombat.AC6ActionCatalog.RawRoot";
    private const string GeneratedActionsRoot =
        "Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions";

    private readonly string[] reviewStates = { "NeedsReview", "OfficialMapped", "Confirmed", "Rejected" };
    private readonly string[] confidenceStates = { "Low", "Medium", "High" };
    private readonly string[] layerStates = { "FullBody", "UpperBody" };
    private readonly string[] sourceFilters = { "全部来源", "有物理 HKX", "缺物理 HKX", "Pose 已生成", "Unity Clip 已生成" };

    private CatalogDocument catalog;
    private LabelDocument labels;
    private CatalogEntry selected;
    private Vector2 listScroll;
    private Vector2 detailScroll;
    private string search = string.Empty;
    private string[] categoryFilters = { "全部分类" };
    private int categoryFilter;
    private int sourceFilter;
    private int reviewFilter;
    private int noteFilter;
    private string rawRoot;
    private float previewTime;
    private float previewLength = 1f;
    private bool isPreviewing;
    private bool isPreviewPlaying;
    private double previewPlaybackOrigin;
    private MechAssembler previewTarget;
    private string previewStatus = "尚未应用预览。";

    [MenuItem("Tools/ACunicombat/AC6/Action Catalog")]
    public static void Open()
    {
        var window = GetWindow<AC6ActionCatalogWindow>("AC6 动作资产表");
        window.minSize = new Vector2(1100f, 650f);
        window.Show();
    }

    private string RepoRoot => Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
    private string CatalogPath => Path.Combine(RepoRoot, CatalogRelativePath);
    private string LabelsPath => Path.Combine(RepoRoot, LabelsRelativePath);

    private void OnEnable()
    {
        rawRoot = EditorPrefs.GetString(RawRootEditorPref, DefaultRawRoot);
        EditorApplication.update += UpdatePreviewPlayback;
        LoadDocuments();
    }

    private void OnDisable()
    {
        EditorApplication.update -= UpdatePreviewPlayback;
        StopPreview();
    }

    private void OnGUI()
    {
        DrawToolbar();
        EditorGUILayout.BeginHorizontal();
        DrawList();
        DrawDetails();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        search = GUILayout.TextField(search, GUI.skin.FindStyle("ToolbarSearchTextField"), GUILayout.Width(260f));
        categoryFilter = EditorGUILayout.Popup(categoryFilter, categoryFilters, EditorStyles.toolbarPopup, GUILayout.Width(180f));
        sourceFilter = EditorGUILayout.Popup(sourceFilter, sourceFilters, EditorStyles.toolbarPopup, GUILayout.Width(140f));
        reviewFilter = EditorGUILayout.Popup(
            reviewFilter,
            new[] { "全部状态", "官方映射", "待确认", "人工确认", "已排除" },
            EditorStyles.toolbarPopup,
            GUILayout.Width(100f));
        noteFilter = EditorGUILayout.Popup(
            noteFilter,
            new[] { "全部备注", "有人工备注", "无人工备注" },
            EditorStyles.toolbarPopup,
            GUILayout.Width(110f));
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("重载", EditorStyles.toolbarButton)) LoadDocuments();
        if (GUILayout.Button("重建总表", EditorStyles.toolbarButton)) RebuildCatalog();
        if (GUILayout.Button("一键补全全部 H", EditorStyles.toolbarButton)) BuildAllPhysicalForReview();
        if (GUILayout.Button("构建已确认动作库", EditorStyles.toolbarButton))
        {
            EditorUtility.DisplayDialog("AC6 动作库", AC6ActionLibraryBuilder.Build(), "确定");
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawList()
    {
        EditorGUILayout.BeginVertical(GUILayout.Width(520f));
        if (catalog?.entries == null)
        {
            EditorGUILayout.HelpBox($"未找到动作总表：{CatalogRelativePath}", MessageType.Warning);
            EditorGUILayout.EndVertical();
            return;
        }

        var filtered = FilteredEntries().ToList();
        EditorGUILayout.LabelField($"显示 {filtered.Count} / {catalog.entries.Length}", EditorStyles.miniLabel);
        listScroll = EditorGUILayout.BeginScrollView(listScroll);
        foreach (var entry in filtered)
        {
            var oldColor = GUI.backgroundColor;
            GUI.backgroundColor = ReviewColor(entry.reviewStatus);
            var marker = entry.sourceAvailable ? (entry.poseAvailable ? "P" : "H") : "-";
            var noteMarker = string.IsNullOrWhiteSpace(entry.manualNoteZh) ? string.Empty : "  [已人工查看]";
            var label = $"[{marker}] {entry.actionId}  {entry.displayNameZh}{noteMarker}";
            if (GUILayout.Toggle(selected == entry, label, "Button", GUILayout.Height(24f)))
            {
                if (selected != entry)
                {
                    selected = entry;
                    previewTime = 0f;
                    UpdatePreviewLength();
                    StopPreview();
                }
            }
            GUI.backgroundColor = oldColor;
        }
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private void DrawDetails()
    {
        EditorGUILayout.BeginVertical();
        detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
        if (selected == null)
        {
            EditorGUILayout.HelpBox("从左侧选择一个动作。H=有 HKX，P=已有 Pose，-=缺物理 HKX。", MessageType.Info);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
            return;
        }

        EditorGUILayout.LabelField(selected.actionId, EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Bank: {selected.bank}    TAE 引用: {selected.taeReferenceCount}    事件: {string.Join("、", selected.eventTypes ?? Array.Empty<string>())}", EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.Space(4f);

        using (new EditorGUI.DisabledScope(true))
        {
            selected.displayNameZh = EditorGUILayout.TextField("中文名称", selected.displayNameZh);
            selected.categoryZh = EditorGUILayout.TextField("中文分类", selected.categoryZh);
            selected.categoryKey = EditorGUILayout.TextField("分类键", selected.categoryKey);
            selected.layer = layerStates[EditorGUILayout.Popup("动画层", IndexOf(layerStates, selected.layer), layerStates)];
            selected.loop = EditorGUILayout.Toggle("循环动作", selected.loop);
            selected.reviewStatus = reviewStates[EditorGUILayout.Popup("语义状态", IndexOf(reviewStates, selected.reviewStatus), reviewStates)];
            selected.confidence = confidenceStates[EditorGUILayout.Popup("置信度", IndexOf(confidenceStates, selected.confidence), confidenceStates)];
            EditorGUILayout.LabelField("中文说明");
            selected.descriptionZh = EditorGUILayout.TextArea(selected.descriptionZh ?? string.Empty, GUILayout.MinHeight(50f));
        }
        EditorGUILayout.HelpBox("人工检查时只需填写下面的备注并保存。名称、分类、状态和说明由后续统一整理。", MessageType.Info);
        EditorGUILayout.LabelField("人工备注");
        selected.manualNoteZh = EditorGUILayout.TextArea(selected.manualNoteZh ?? string.Empty, GUILayout.MinHeight(70f));

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("管线状态", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            $"物理 HKX: {selected.sourceAvailable}    Pose: {selected.poseAvailable}    Unity Clip: {selected.unityClipCount}/4    解码状态: {selected.decodeStatus}");
        if (selected.evidenceZh != null)
        {
            foreach (var evidence in selected.evidenceZh)
            {
                EditorGUILayout.LabelField("• " + evidence, EditorStyles.wordWrappedLabel);
            }
        }

        EditorGUILayout.Space(8f);
        if (GUILayout.Button("保存人工备注", GUILayout.Height(28f))) SaveManualNote();

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("解码与预览", EditorStyles.boldLabel);
        rawRoot = EditorGUILayout.TextField("原始 HKX 目录", rawRoot);
        EditorPrefs.SetString(RawRootEditorPref, rawRoot);
        EditorGUILayout.BeginHorizontal();
        previewTarget = (MechAssembler)EditorGUILayout.ObjectField(
            "预览机体", previewTarget, typeof(MechAssembler), true);
        if (GUILayout.Button("使用当前选择", GUILayout.Width(100f)))
        {
            previewTarget = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<MechAssembler>()
                : null;
            previewStatus = previewTarget != null
                ? $"预览目标：{previewTarget.name}"
                : "当前选择不属于带 MechAssembler 的机体。";
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.HelpBox(
            "动作会应用到 Scene 窗口中的预览机体；本窗口不直接渲染模型。请将动作资产表与 Scene 窗口并排。",
            MessageType.Info);
        if (string.Equals(selected.actionId, "a000_000000", StringComparison.OrdinalIgnoreCase))
        {
            EditorGUILayout.HelpBox(
                "a000_000000 是幅度极小的基础动作：10 秒内最大关节旋转约 0.98°，主要位移约 1.5 cm。" +
                "它看起来接近静止；建议先搜索 a000_010100 验证预览功能。",
                MessageType.Warning);
        }
        EditorGUILayout.BeginHorizontal();
        GUI.enabled = selected.sourceAvailable;
        if (GUILayout.Button("生成 Pose + 四部件 Clip", GUILayout.Height(28f))) BuildSelectedForReview();
        GUI.enabled = selected.unityClipCount > 0;
        if (GUILayout.Button(isPreviewPlaying ? "暂停播放" : "播放预览", GUILayout.Height(28f)))
        {
            TogglePreviewPlayback();
        }
        if (GUILayout.Button("应用当前帧", GUILayout.Height(28f))) SamplePreview();
        if (GUILayout.Button("停止预览", GUILayout.Height(28f))) StopPreview();
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
        var nextTime = EditorGUILayout.Slider("预览时间", previewTime, 0f, Mathf.Max(0.01f, previewLength));
        if (!Mathf.Approximately(nextTime, previewTime))
        {
            previewTime = nextTime;
            if (isPreviewPlaying)
            {
                previewPlaybackOrigin = EditorApplication.timeSinceStartup - previewTime;
            }
            if (isPreviewing) SamplePreview();
        }
        EditorGUILayout.HelpBox(previewStatus, isPreviewing ? MessageType.Info : MessageType.None);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("TAE 引用", EditorStyles.boldLabel);
        if (selected.taeReferences != null)
        {
            foreach (var reference in selected.taeReferences.Take(30))
            {
                EditorGUILayout.LabelField(
                    $"{reference.taeFile} / 状态 {reference.taeActionId} / 事件 {reference.eventCount}: {string.Join("、", reference.eventTypes ?? Array.Empty<string>())}",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();
    }

    private IEnumerable<CatalogEntry> FilteredEntries()
    {
        IEnumerable<CatalogEntry> result = catalog.entries;
        if (!string.IsNullOrWhiteSpace(search))
        {
            result = result.Where(entry =>
                Contains(entry.actionId, search) || Contains(entry.displayNameZh, search) ||
                Contains(entry.categoryZh, search) || Contains(entry.manualNoteZh, search));
        }
        if (categoryFilter > 0)
        {
            var category = categoryFilters[categoryFilter];
            result = result.Where(entry => string.Equals(entry.categoryZh, category, StringComparison.Ordinal));
        }
        result = sourceFilter switch
        {
            1 => result.Where(entry => entry.sourceAvailable),
            2 => result.Where(entry => !entry.sourceAvailable),
            3 => result.Where(entry => entry.poseAvailable),
            4 => result.Where(entry => entry.unityClipCount > 0),
            _ => result
        };
        result = reviewFilter switch
        {
            1 => result.Where(entry => string.Equals(entry.reviewStatus, "OfficialMapped", StringComparison.OrdinalIgnoreCase)),
            2 => result.Where(entry => string.Equals(entry.reviewStatus, "NeedsReview", StringComparison.OrdinalIgnoreCase)),
            3 => result.Where(entry => string.Equals(entry.reviewStatus, "Confirmed", StringComparison.OrdinalIgnoreCase)),
            4 => result.Where(entry => string.Equals(entry.reviewStatus, "Rejected", StringComparison.OrdinalIgnoreCase)),
            _ => result
        };
        result = noteFilter switch
        {
            1 => result.Where(entry => !string.IsNullOrWhiteSpace(entry.manualNoteZh)),
            2 => result.Where(entry => string.IsNullOrWhiteSpace(entry.manualNoteZh)),
            _ => result
        };
        return result.OrderBy(entry => entry.bank).ThenBy(entry => entry.actionId);
    }

    private void SaveManualNote()
    {
        if (selected == null) return;

        var list = labels.entries?.ToList() ?? new List<ActionLabel>();
        var label = list.FirstOrDefault(item => item.actionId == selected.actionId);
        if (label == null)
        {
            label = new ActionLabel
            {
                actionId = selected.actionId
            };
            list.Add(label);
        }

        // Notes are the only user-authored field in this window. Semantic fields come
        // from the aligned BHV map or an explicit external adjudication.
        label.displayNameZh = null;
        label.categoryKey = null;
        label.categoryZh = null;
        label.descriptionZh = null;
        label.reviewStatus = null;
        label.confidence = null;
        label.layer = null;
        label.loop = false;
        label.manualNoteZh = selected.manualNoteZh?.Trim() ?? string.Empty;
        labels.entries = list.OrderBy(item => item.actionId).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(LabelsPath) ?? RepoRoot);
        File.WriteAllText(LabelsPath, JsonUtility.ToJson(labels, true), new UTF8Encoding(false));
        RebuildCatalog();
    }

    private void LoadDocuments()
    {
        try
        {
            catalog = File.Exists(CatalogPath)
                ? JsonUtility.FromJson<CatalogDocument>(File.ReadAllText(CatalogPath, Encoding.UTF8))
                : null;
            labels = File.Exists(LabelsPath)
                ? JsonUtility.FromJson<LabelDocument>(File.ReadAllText(LabelsPath, Encoding.UTF8))
                : new LabelDocument();
            labels ??= new LabelDocument();
            labels.entries ??= Array.Empty<ActionLabel>();
            categoryFilters = new[] { "全部分类" }
                .Concat(catalog?.entries?.Select(entry => entry.categoryZh)
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().OrderBy(value => value)
                    ?? Enumerable.Empty<string>())
                .ToArray();
            categoryFilter = Mathf.Clamp(categoryFilter, 0, categoryFilters.Length - 1);
            if (selected != null && catalog?.entries != null)
            {
                selected = catalog.entries.FirstOrDefault(entry => entry.actionId == selected.actionId);
            }
            Repaint();
        }
        catch (Exception e)
        {
            Debug.LogError($"[AC6ActionCatalog] Load failed: {e}");
            catalog = null;
        }
    }

    private void RebuildCatalog()
    {
        var script = Path.Combine(RepoRoot, CatalogScriptRelativePath);
        var result = RunProcess(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -RawRoot \"{rawRoot}\"",
            RepoRoot);
        if (result.exitCode != 0)
        {
            EditorUtility.DisplayDialog("动作总表生成失败", result.output, "确定");
            return;
        }
        LoadDocuments();
        Debug.Log("[AC6ActionCatalog] " + result.output);
    }

    private void BuildSelectedForReview()
    {
        if (selected == null || !selected.sourceAvailable) return;
        var exporter = Path.Combine(RepoRoot, "Tools/DotNet/AC6HkxPoseExporter/bin/Release/net9.0/AC6HkxPoseExporter.dll");
        var workspaceRoot = Directory.GetParent(RepoRoot)?.FullName ?? string.Empty;
        var dotnetHost = FirstExisting(Path.Combine(workspaceRoot, "Tools/dotnet9sdk/dotnet.exe"));
        var skeleton = FirstExisting(
            Path.Combine(RepoRoot, "Tools/LocalOut/Melander1010Pipeline/anibnd/c0000/4000000_Skeleton.hkx"),
            @"E:\UnityDev\tmp\ac6_anim_tests\from_game_c0000\4000000_Skeleton.hkx");
        var compendium = FirstExisting(
            Path.Combine(RepoRoot, "Tools/LocalOut/Melander1010Pipeline/anibnd/c0000_a000_00/7000000_a000_00.compendium"),
            @"E:\UnityDev\tmp\ac6_anim_tests\from_game_c0000\7000000_a000_00.compendium");
        var animation = Path.Combine(rawRoot, selected.hkxFile);
        var output = Path.Combine(RepoRoot, $"Tools/LocalOut/actions/{selected.actionId}_pose.json");
        if (dotnetHost == null || !File.Exists(exporter) || skeleton == null || compendium == null || !File.Exists(animation))
        {
            EditorUtility.DisplayDialog(
                "缺少解码输入",
                $".NET 9: {dotnetHost}\nExporter: {File.Exists(exporter)}\nSkeleton: {skeleton}\nCompendium: {compendium}\nAnimation: {animation}",
                "确定");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output) ?? RepoRoot);
        var result = RunProcess(
            dotnetHost,
            $"\"{exporter}\" --skeleton \"{skeleton}\" --anim \"{animation}\" --compendium \"{compendium}\" --out \"{output}\"",
            RepoRoot);
        if (result.exitCode != 0)
        {
            EditorUtility.DisplayDialog("HKX 解码失败", result.output, "确定");
            return;
        }

        var clipReport = AC6ActionLibraryBuilder.BuildReviewAction(selected.actionId, selected.layer, selected.loop);
        RebuildCatalog();
        EditorUtility.DisplayDialog("动作预览资源", result.output + "\n" + clipReport, "确定");
    }

    private void BuildAllPhysicalForReview()
    {
        if (!EditorUtility.DisplayDialog(
                "批量补全物理动作",
                "将解码全部物理 HKX，并为每个动作生成头、核心、手臂、腿四个预览 Clip。" +
                "\n\n这不会把未经确认的动作加入正式动作库。继续吗？",
                "开始", "取消"))
        {
            return;
        }

        var script = Path.Combine(RepoRoot, ClipScriptRelativePath);
        var poseResult = RunProcess(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -AllPhysical -RawRoot \"{rawRoot}\"",
            RepoRoot);
        if (poseResult.exitCode != 0)
        {
            EditorUtility.DisplayDialog("批量 HKX 解码失败", poseResult.output, "确定");
            return;
        }

        var clipReport = AC6ActionLibraryBuilder.BuildAllPhysicalPreviewClips();
        RebuildCatalog();
        EditorUtility.DisplayDialog(
            "批量动作预览资源",
            poseResult.output + "\n" + clipReport,
            "确定");
    }

    private bool SamplePreview(bool showDialogs = true)
    {
        if (selected == null || selected.unityClipCount <= 0) return false;
        var assembler = ResolvePreviewTarget(showDialogs);
        if (assembler == null)
        {
            previewStatus = "没有明确的预览机体。请在 Scene/Hierarchy 选择一台机体，再点“使用当前选择”。";
            return false;
        }
        if (assembler.RuntimeState == null || assembler.RuntimeState.AssembledParts.Count == 0)
        {
            assembler.Assemble();
        }
        if (assembler.RuntimeState == null || assembler.RuntimeState.AssembledParts.Count == 0)
        {
            previewStatus = $"{assembler.name} 装配失败或没有已装配部件。";
            if (showDialogs) EditorUtility.DisplayDialog("预览机体未装配", previewStatus, "确定");
            return false;
        }

        if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
        var sampledClips = 0;
        var matchedBindings = 0;
        var totalBindings = 0;
        AnimationMode.BeginSampling();
        try
        {
            foreach (var record in assembler.RuntimeState.AssembledParts)
            {
                if (record?.instance == null) continue;
                var token = SlotToken(record.slot);
                if (token == null) continue;
                var clipPath = $"{GeneratedActionsRoot}/{selected.actionId}/AC6_{selected.actionId}_{token}.anim";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip == null) continue;

                var hierarchyPaths = record.instance.GetComponentsInChildren<Transform>(true)
                    .Select(transform => AnimationUtility.CalculateTransformPath(
                        transform, record.instance.transform))
                    .ToHashSet(StringComparer.Ordinal);
                var bindings = AnimationUtility.GetCurveBindings(clip);
                var partMatches = bindings.Count(binding => hierarchyPaths.Contains(binding.path));
                totalBindings += bindings.Length;
                matchedBindings += partMatches;
                if (partMatches > 0)
                {
                    AnimationMode.SampleAnimationClip(record.instance, clip, Mathf.Min(previewTime, clip.length));
                    sampledClips++;
                }
            }
        }
        finally
        {
            AnimationMode.EndSampling();
        }
        isPreviewing = sampledClips > 0 && matchedBindings > 0;
        previewTarget = assembler;
        previewStatus = isPreviewing
            ? $"已应用到 {assembler.name}：{sampledClips}/4 个部件，命中 {matchedBindings}/{totalBindings} 条曲线，时间 {previewTime:0.000}s。"
            : $"预览失败：{assembler.name} 没有任何曲线路径与动作 Clip 匹配。请重新生成该动作的四部件 Clip。";
        if (!isPreviewing && AnimationMode.InAnimationMode())
        {
            AnimationMode.StopAnimationMode();
        }
        if (!isPreviewing && showDialogs)
        {
            EditorUtility.DisplayDialog("动作预览失败", previewStatus, "确定");
        }
        SceneView.RepaintAll();
        Repaint();
        return isPreviewing;
    }

    private void StopPreview()
    {
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        isPreviewing = false;
        isPreviewPlaying = false;
        previewStatus = "预览已停止。";
        SceneView.RepaintAll();
        Repaint();
    }

    private MechAssembler ResolvePreviewTarget(bool showDialogs)
    {
        if (previewTarget != null) return previewTarget;

        var selectedAssembler = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<MechAssembler>()
            : null;
        if (selectedAssembler != null)
        {
            previewTarget = selectedAssembler;
            return previewTarget;
        }

        var assemblers = FindObjectsByType<MechAssembler>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (assemblers.Length == 1)
        {
            previewTarget = assemblers[0];
            return previewTarget;
        }

        if (showDialogs)
        {
            var reason = assemblers.Length == 0
                ? "当前场景没有带 MechAssembler 的机体。"
                : $"当前场景有 {assemblers.Length} 台机体，无法安全地自动选择。";
            EditorUtility.DisplayDialog(
                "请选择预览机体",
                reason + "\n请在 Hierarchy 选择目标机体，再点“使用当前选择”。",
                "确定");
        }
        return null;
    }

    private void TogglePreviewPlayback()
    {
        if (isPreviewPlaying)
        {
            isPreviewPlaying = false;
            previewStatus = $"已暂停在 {previewTime:0.000}s。";
            Repaint();
            return;
        }

        if (!SamplePreview()) return;
        isPreviewPlaying = true;
        previewPlaybackOrigin = EditorApplication.timeSinceStartup - previewTime;
        previewStatus = $"正在播放 {selected.actionId}，目标：{previewTarget.name}。";
        Repaint();
    }

    private void UpdatePreviewPlayback()
    {
        if (!isPreviewPlaying || selected == null) return;
        var length = Mathf.Max(0.01f, previewLength);
        previewTime = (float)((EditorApplication.timeSinceStartup - previewPlaybackOrigin) % length);
        if (!SamplePreview(false))
        {
            isPreviewPlaying = false;
        }
        Repaint();
    }

    private void UpdatePreviewLength()
    {
        previewLength = 1f;
        if (selected == null) return;
        foreach (var token in new[] { "HD", "BD", "AM", "LG" })
        {
            var path = $"{GeneratedActionsRoot}/{selected.actionId}/AC6_{selected.actionId}_{token}.anim";
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip != null) previewLength = Mathf.Max(previewLength, clip.length);
        }
    }

    private static string SlotToken(MechPartSlot slot) => slot switch
    {
        MechPartSlot.Head => "HD",
        MechPartSlot.Core => "BD",
        MechPartSlot.Arms => "AM",
        MechPartSlot.Legs => "LG",
        _ => null
    };

    private static (int exitCode, string output) RunProcess(string executable, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        if (process == null) return (-1, "Process failed to start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static string FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
    private static bool Contains(string value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return 0;
    }
    private static Color ReviewColor(string status) => status switch
    {
        "OfficialMapped" => new Color(0.55f, 0.75f, 1f),
        "Confirmed" => new Color(0.55f, 0.9f, 0.6f),
        "Rejected" => new Color(0.9f, 0.55f, 0.55f),
        _ => Color.white
    };

    [Serializable]
    private sealed class CatalogDocument
    {
        public CatalogEntry[] entries;
    }

    [Serializable]
    private sealed class CatalogEntry
    {
        public string actionId;
        public string hkxFile;
        public string bank;
        public string displayNameZh;
        public string categoryKey;
        public string categoryZh;
        public string descriptionZh;
        public string reviewStatus;
        public string confidence;
        public string semanticSource;
        public string layer;
        public bool loop;
        public bool sourceAvailable;
        public int taeReferenceCount;
        public TaeReference[] taeReferences;
        public string[] eventTypes;
        public bool poseAvailable;
        public int unityClipCount;
        public string decodeStatus;
        public string[] evidenceZh;
        public string manualNoteZh;
    }

    [Serializable]
    private sealed class TaeReference
    {
        public string taeFile;
        public long taeActionId;
        public int eventCount;
        public string[] eventTypes;
    }

    [Serializable]
    private sealed class LabelDocument
    {
        public int schemaVersion = 1;
        public string comment = "人工标注覆盖表。自动扫描不会改写本文件。";
        public ActionLabel[] entries = Array.Empty<ActionLabel>();
    }

    [Serializable]
    private sealed class ActionLabel
    {
        public string actionId;
        public string displayNameZh;
        public string categoryKey;
        public string categoryZh;
        public string descriptionZh;
        public string reviewStatus;
        public string confidence;
        public string layer;
        public bool loop;
        public string manualNoteZh;
    }
}
