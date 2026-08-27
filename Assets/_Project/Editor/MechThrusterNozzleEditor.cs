using System;
using System.Collections.Generic;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MechMainThrusterPlume))]
public sealed class MechThrusterNozzleEditor : Editor
{
    private readonly List<MechThrusterNozzleEditorState> states = new();
    private string selectedId;
    private Vector2 listScroll;
    private bool previewSelected = true;
    private bool mirrorEditing = true;
    private bool showRenderingSettings;
    private string physicalDraftId;
    private float physicalDraftRatedThrustKn;
    private bool physicalDraftVectoringEnabled;
    private float physicalDraftMaximumVectorAngleDegrees;

    [MenuItem("Tools/ACunicombat/Effects/Edit Thruster Nozzles")]
    private static void SelectRuntimeThrusterEditor()
    {
        MechThrusterNozzleEditorWindow.OpenWindow();
    }

    public static void OpenFor(GameObject preferredRoot)
    {
        MechThrusterNozzleEditorWindow.OpenWindow();
    }

    internal static MechMainThrusterPlume FindRuntimePlume()
    {
        MechMainThrusterPlume plume = null;
        var candidates = UnityEngine.Object.FindObjectsByType<MechMainThrusterPlume>(
            FindObjectsInactive.Include);
        for (var i = 0; i < candidates.Length; i++)
        {
            var rig = candidates[i].GetComponent<MechPlayerRig>();
            if (rig != null && !rig.AiControlled)
            {
                plume = candidates[i];
                break;
            }
        }

        return plume ?? (candidates.Length > 0 ? candidates[0] : null);
    }

    private void OnEnable()
    {
        SceneView.duringSceneGui += DrawSceneOverlay;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= DrawSceneOverlay;
        if (target is MechMainThrusterPlume plume)
        {
            plume.SetNozzleEditorPreview(null);
        }
    }

    public override void OnInspectorGUI()
    {
        showRenderingSettings = EditorGUILayout.Foldout(
            showRenderingSettings,
            "羽流渲染参数",
            true);
        if (showRenderingSettings)
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, "m_Script");
            serializedObject.ApplyModifiedProperties();
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("喷口视觉覆盖编辑", EditorStyles.boldLabel);
        if (!EditorApplication.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "喷口绑定在运行时装配的机体骨骼上。请进入 Play Mode 后使用本面板。",
                MessageType.Info);
            return;
        }

        var plume = (MechMainThrusterPlume)target;
        plume.GetNozzleEditorStates(states);

        EditorGUI.BeginChangeCheck();
        mirrorEditing = EditorGUILayout.Toggle("左右对称编辑", mirrorEditing);
        previewSelected = EditorGUILayout.Toggle("预览所选喷口", previewSelected);
        if (EditorGUI.EndChangeCheck())
        {
            plume.SetNozzleEditorPreview(
                previewSelected ? selectedId : null,
                mirrorEditing);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("捕获当前视觉"))
            {
                var captured = plume.CaptureCurrentNozzleLayout();
                plume.ApplyNozzleLayout(captured);
                selectedId = captured.nozzles.Count > 0 ? captured.nozzles[0].id : null;
                plume.SetNozzleEditorPreview(
                    previewSelected ? selectedId : null,
                    mirrorEditing);
                RefreshScene();
            }

            if (GUILayout.Button("重新载入视觉包"))
            {
                plume.ReloadNozzleLayout();
                selectedId = null;
                plume.SetNozzleEditorPreview(null);
                RefreshScene();
            }

            if (GUILayout.Button("打包喷口配置"))
            {
                PackageVisualOverrides(plume);
            }
        }

        var disabledNozzleCount = CountDisabledNozzles(plume.ExportNozzleLayout());
        if (disabledNozzleCount > 0 &&
            GUILayout.Button($"恢复已删除喷口 ({disabledNozzleCount})"))
        {
            RestoreDisabledNozzles(plume);
        }

        EditorGUILayout.HelpBox(
            "运行时喷口始终由模型 dummy 自动发现。这里统一编辑位置、方向、羽流尺寸、额定推力与矢量能力；青色箭头是尾焰喷出方向。复制喷口会增加一个独立的物理喷口。",
            MessageType.None);
        EditorGUILayout.HelpBox(
            $"自动发现 {plume.DiscoveredAuxiliaryNozzleCount} 个 / " +
            $"视觉覆盖 {plume.AppliedVisualOverrideCount} 个 / " +
            $"失配 {plume.UnmatchedVisualOverrideCount} 个",
            plume.UnmatchedVisualOverrideCount > 0
                ? MessageType.Warning
                : MessageType.Info);

        listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.MaxHeight(260f));
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            var selected = string.Equals(selectedId, state.Id, StringComparison.Ordinal);
            var previousColor = GUI.backgroundColor;
            if (selected)
            {
                GUI.backgroundColor = new Color(0.35f, 0.8f, 1f);
            }

            if (GUILayout.Button($"{i + 1:00}  {state.DisplayName}", EditorStyles.miniButton))
            {
                EndPhysicalFieldEditing();
                selectedId = state.Id;
                ResetPhysicalDraft();
                EnsureEditableLayout(plume, selectedId);
                plume.SetNozzleEditorPreview(
                    previewSelected ? selectedId : null,
                    mirrorEditing);
                RefreshScene();
            }

            GUI.backgroundColor = previousColor;
        }
        EditorGUILayout.EndScrollView();

        if (!TryGetSelectedState(out var selectedState))
        {
            return;
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("当前喷口", selectedState.DisplayName);
        EditorGUILayout.SelectableLabel(selectedState.Id, EditorStyles.miniLabel, GUILayout.Height(18f));

        var currentLayout = plume.ExportNozzleLayout();
        if (FindEntry(currentLayout, selectedState.Id) == null)
        {
            EditorGUILayout.HelpBox(
                "当前喷口尚未进入视觉覆盖包；重新点击该喷口即可自动捕获并开始编辑。",
                MessageType.Warning);
            return;
        }

        var hasMirroredNozzle = plume.TryGetMirroredNozzleId(
            selectedState.Id,
            out var mirroredId);
        MechThrusterNozzleEditorState mirroredState = default;
        var hasMirroredState = hasMirroredNozzle &&
                               TryGetState(mirroredId, out mirroredState);
        var layoutSaved = IsLayoutSaved(currentLayout);
        EditorGUILayout.HelpBox(
            $"运行时应用：已生效\n磁盘状态：{(layoutSaved ? "已保存" : "有未保存修改")}",
            layoutSaved ? MessageType.Info : MessageType.Warning);

        if (hasMirroredState)
        {
            var mirrorSynchronized = IsMirrorSynchronized(
                plume,
                selectedState,
                mirroredState);
            EditorGUILayout.HelpBox(
                $"镜像对象：{mirroredState.DisplayName}\n" +
                $"镜像同步：{(mirrorSynchronized ? "已验证（变换、尺寸和物理参数一致）" : "当前两侧不一致")}",
                mirrorSynchronized ? MessageType.Info : MessageType.Warning);

            if (GUILayout.Button($"切换到镜像喷口：{mirroredState.DisplayName}"))
            {
                EndPhysicalFieldEditing();
                selectedId = mirroredId;
                ResetPhysicalDraft();
                plume.SetNozzleEditorPreview(
                    previewSelected ? selectedId : null,
                    mirrorEditing);
                RefreshScene();
            }
        }
        else
        {
            EditorGUILayout.HelpBox(
                "镜像对象：未找到。该喷口的修改只会作用于当前一侧。",
                MessageType.Warning);
        }

        using (new EditorGUI.DisabledScope(!mirrorEditing || !hasMirroredNozzle))
        {
            if (GUILayout.Button("将当前值镜像到另一侧"))
            {
                plume.UpdateNozzleEditorState(
                    selectedState.Id,
                    selectedState.WorldPosition,
                    selectedState.WorldRotation,
                    selectedState.RadiusScale,
                    selectedState.LengthScale,
                    selectedState.RatedThrustKn,
                    selectedState.VectoringEnabled,
                    selectedState.MaximumVectorAngleDegrees,
                    updateMirror: true);
                RefreshScene();
            }
        }

        EditorGUI.BeginChangeCheck();
        var radiusScale = EditorGUILayout.Slider(
            "宽度倍率",
            selectedState.RadiusScale,
            0.05f,
            4f);
        var lengthScale = EditorGUILayout.Slider(
            "长度倍率",
            selectedState.LengthScale,
            0.05f,
            4f);
        if (EditorGUI.EndChangeCheck())
        {
            plume.UpdateNozzleEditorState(
                selectedState.Id,
                selectedState.WorldPosition,
                selectedState.WorldRotation,
                radiusScale,
                lengthScale,
                selectedState.RatedThrustKn,
                selectedState.VectoringEnabled,
                selectedState.MaximumVectorAngleDegrees,
                mirrorEditing);
            RefreshScene();
        }

        EditorGUILayout.Space(5f);
        EditorGUILayout.LabelField("物理参数", EditorStyles.boldLabel);
        EnsurePhysicalDraft(selectedState);
        GUI.SetNextControlName($"NozzleRatedThrust:{selectedState.Id}");
        physicalDraftRatedThrustKn = Mathf.Max(
            0f,
            EditorGUILayout.FloatField("额定推力 (kN)", physicalDraftRatedThrustKn));
        physicalDraftVectoringEnabled = EditorGUILayout.Toggle(
            "启用矢量",
            physicalDraftVectoringEnabled);
        using (new EditorGUI.DisabledScope(!physicalDraftVectoringEnabled))
        {
            physicalDraftMaximumVectorAngleDegrees = EditorGUILayout.Slider(
                "最大矢量角 (°)",
                physicalDraftVectoringEnabled
                    ? physicalDraftMaximumVectorAngleDegrees
                    : 0f,
                0f,
                89f);
        }

        var physicalSettingsChanged =
            Mathf.Abs(physicalDraftRatedThrustKn - selectedState.RatedThrustKn) > 0.001f ||
            physicalDraftVectoringEnabled != selectedState.VectoringEnabled ||
            Mathf.Abs(
                (physicalDraftVectoringEnabled
                    ? physicalDraftMaximumVectorAngleDegrees
                    : 0f) - selectedState.MaximumVectorAngleDegrees) > 0.001f;
        using (new EditorGUI.DisabledScope(!physicalSettingsChanged))
        {
            var applyLabel = mirrorEditing && hasMirroredNozzle
                ? "应用到当前喷口和镜像喷口"
                : "应用到当前喷口";
            if (GUILayout.Button(applyLabel))
            {
                EndPhysicalFieldEditing();
                plume.UpdateNozzlePhysicalSettings(
                    selectedState.Id,
                    physicalDraftRatedThrustKn,
                    physicalDraftVectoringEnabled,
                    physicalDraftVectoringEnabled
                        ? physicalDraftMaximumVectorAngleDegrees
                        : 0f,
                    mirrorEditing);
                ResetPhysicalDraft();
                RefreshScene();
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("复制所选喷口"))
            {
                DuplicateSelected(plume, selectedState.Id);
            }

            if (GUILayout.Button("删除所选喷口"))
            {
                DeleteSelectedNozzle(plume, selectedState.Id);
            }
        }

        Repaint();
    }

    private void DrawSceneOverlay(SceneView sceneView)
    {
        if (!EditorApplication.isPlaying || target == null || !TryGetSelectedState(out var state))
        {
            return;
        }

        var plume = (MechMainThrusterPlume)target;
        if (FindEntry(plume.ExportNozzleLayout(), state.Id) == null)
        {
            return;
        }

        var handleSize = HandleUtility.GetHandleSize(state.WorldPosition);
        var previousZTest = Handles.zTest;
        Handles.zTest = UnityEngine.Rendering.CompareFunction.Always;
        DrawDirectionMarker(
            state,
            handleSize,
            new Color(0.2f, 0.85f, 1f, 1f),
            "当前");

        if (mirrorEditing &&
            plume.TryGetMirroredNozzleId(state.Id, out var mirroredId) &&
            TryGetState(mirroredId, out var mirroredState))
        {
            DrawDirectionMarker(
                mirroredState,
                HandleUtility.GetHandleSize(mirroredState.WorldPosition),
                new Color(1f, 0.35f, 0.8f, 1f),
                "镜像");
        }

        EditorGUI.BeginChangeCheck();
        var position = Handles.PositionHandle(state.WorldPosition, state.WorldRotation);
        var rotation = Handles.RotationHandle(state.WorldRotation, position);
        if (EditorGUI.EndChangeCheck())
        {
            plume.UpdateNozzleEditorState(
                state.Id,
                position,
                rotation,
                state.RadiusScale,
                state.LengthScale,
                state.RatedThrustKn,
                state.VectoringEnabled,
                state.MaximumVectorAngleDegrees,
                mirrorEditing);
            RefreshScene();
        }

        Handles.zTest = previousZTest;
    }

    private static void DrawDirectionMarker(
        MechThrusterNozzleEditorState state,
        float handleSize,
        Color color,
        string prefix)
    {
        Handles.color = color;
        var directionEnd = state.WorldPosition +
                           state.WorldRotation * Vector3.forward * handleSize * 1.8f;
        Handles.DrawAAPolyLine(4f, state.WorldPosition, directionEnd);
        Handles.ConeHandleCap(
            0,
            directionEnd,
            state.WorldRotation,
            handleSize * 0.16f,
            EventType.Repaint);
        Handles.ArrowHandleCap(
            0,
            state.WorldPosition,
            state.WorldRotation,
            handleSize * 1.1f,
            EventType.Repaint);
        Handles.Label(
            state.WorldPosition + state.WorldRotation * Vector3.up * handleSize * 0.25f,
            $"{prefix}：{state.DisplayName}");
    }

    private static void EnsureEditableLayout(MechMainThrusterPlume plume, string id)
    {
        if (plume == null ||
            FindEntry(plume.ExportNozzleLayout(), id) != null)
        {
            return;
        }

        plume.ApplyNozzleLayout(plume.CaptureCurrentNozzleLayout());
    }

    private bool TryGetSelectedState(out MechThrusterNozzleEditorState selectedState)
    {
        var plume = (MechMainThrusterPlume)target;
        plume.GetNozzleEditorStates(states);
        return TryGetState(selectedId, out selectedState);
    }

    private bool TryGetState(string id, out MechThrusterNozzleEditorState selectedState)
    {
        for (var i = 0; i < states.Count; i++)
        {
            if (string.Equals(states[i].Id, id, StringComparison.Ordinal))
            {
                selectedState = states[i];
                return true;
            }
        }

        selectedState = default;
        return false;
    }

    private static bool IsMirrorSynchronized(
        MechMainThrusterPlume plume,
        MechThrusterNozzleEditorState source,
        MechThrusterNozzleEditorState mirror)
    {
        var root = plume.transform;
        var localPosition = root.InverseTransformPoint(source.WorldPosition);
        localPosition.x = -localPosition.x;
        var expectedPosition = root.TransformPoint(localPosition);

        var localForward = root.InverseTransformDirection(
            source.WorldRotation * Vector3.forward);
        var localUp = root.InverseTransformDirection(
            source.WorldRotation * Vector3.up);
        localForward.x = -localForward.x;
        localUp.x = -localUp.x;
        var expectedRotation = Quaternion.LookRotation(
            root.TransformDirection(localForward),
            root.TransformDirection(localUp));

        return Vector3.Distance(expectedPosition, mirror.WorldPosition) <= 0.01f &&
               Quaternion.Angle(expectedRotation, mirror.WorldRotation) <= 0.5f &&
               Mathf.Abs(source.RadiusScale - mirror.RadiusScale) <= 0.001f &&
               Mathf.Abs(source.LengthScale - mirror.LengthScale) <= 0.001f &&
               Mathf.Abs(source.RatedThrustKn - mirror.RatedThrustKn) <= 0.001f &&
               source.VectoringEnabled == mirror.VectoringEnabled &&
               Mathf.Abs(
                   source.MaximumVectorAngleDegrees -
                   mirror.MaximumVectorAngleDegrees) <= 0.001f;
    }

    private void EnsurePhysicalDraft(MechThrusterNozzleEditorState state)
    {
        if (string.Equals(physicalDraftId, state.Id, StringComparison.Ordinal))
        {
            return;
        }

        physicalDraftId = state.Id;
        physicalDraftRatedThrustKn = state.RatedThrustKn;
        physicalDraftVectoringEnabled = state.VectoringEnabled;
        physicalDraftMaximumVectorAngleDegrees = state.MaximumVectorAngleDegrees;
    }

    private void ResetPhysicalDraft()
    {
        physicalDraftId = null;
    }

    private static void EndPhysicalFieldEditing()
    {
        GUI.FocusControl(null);
        EditorGUIUtility.editingTextField = false;
    }

    private static bool IsLayoutSaved(MechThrusterNozzleLayoutData currentLayout)
    {
        return MechThrusterNozzleVisualPackagePipeline.IsCurrentPackageSaved(
            currentLayout);
    }

    private void DuplicateSelected(MechMainThrusterPlume plume, string id)
    {
        var layout = plume.ExportNozzleLayout();
        var source = FindEntry(layout, id);
        if (source == null)
        {
            return;
        }

        string mirroredId = null;
        var hasMirror = mirrorEditing &&
                        plume.TryGetMirroredNozzleId(id, out mirroredId);
        var mirroredSource = hasMirror ? FindEntry(layout, mirroredId) : null;
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var duplicate = source.Clone(
            $"{source.id}_copy_{suffix}",
            $"{source.displayName} / 副本");
        layout.nozzles.Add(duplicate);
        if (mirroredSource != null)
        {
            layout.nozzles.Add(mirroredSource.Clone(
                $"{mirroredSource.id}_copy_{suffix}",
                $"{mirroredSource.displayName} / 副本"));
        }

        selectedId = duplicate.id;
        plume.ApplyNozzleLayout(layout);
        plume.SetNozzleEditorPreview(
            previewSelected ? selectedId : null,
            mirrorEditing);
        RefreshScene();
    }

    private void DeleteSelectedNozzle(MechMainThrusterPlume plume, string id)
    {
        string mirroredId = null;
        var hasMirror = mirrorEditing &&
                         plume.TryGetMirroredNozzleId(id, out mirroredId);
        var layout = plume.ExportNozzleLayout();
        for (var i = layout.nozzles.Count - 1; i >= 0; i--)
        {
            var entry = layout.nozzles[i];
            if (entry == null ||
                (!string.Equals(entry.id, id, StringComparison.Ordinal) &&
                 (!hasMirror || !string.Equals(
                     entry.id,
                     mirroredId,
                     StringComparison.Ordinal))))
            {
                continue;
            }

            if (entry.id.Contains("_copy_", StringComparison.Ordinal))
            {
                layout.nozzles.RemoveAt(i);
            }
            else
            {
                entry.enabled = false;
            }
        }

        EndPhysicalFieldEditing();
        ResetPhysicalDraft();
        selectedId = null;
        plume.ApplyNozzleLayout(layout);
        plume.SetNozzleEditorPreview(null);
        RefreshScene();
    }

    private static int CountDisabledNozzles(MechThrusterNozzleLayoutData layout)
    {
        if (layout?.nozzles == null)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < layout.nozzles.Count; i++)
        {
            if (layout.nozzles[i] != null && !layout.nozzles[i].enabled)
            {
                count++;
            }
        }

        return count;
    }

    private void RestoreDisabledNozzles(MechMainThrusterPlume plume)
    {
        var layout = plume.ExportNozzleLayout();
        for (var i = 0; i < layout.nozzles.Count; i++)
        {
            if (layout.nozzles[i] != null)
            {
                layout.nozzles[i].enabled = true;
            }
        }

        plume.ApplyNozzleLayout(layout);
        RefreshScene();
    }

    private static MechThrusterNozzleLayoutEntry FindEntry(
        MechThrusterNozzleLayoutData layout,
        string id)
    {
        if (layout?.nozzles == null)
        {
            return null;
        }

        for (var i = 0; i < layout.nozzles.Count; i++)
        {
            var entry = layout.nozzles[i];
            if (entry != null && string.Equals(entry.id, id, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    private static void PackageVisualOverrides(MechMainThrusterPlume plume)
    {
        var message = MechThrusterNozzleVisualPackagePipeline.PackageVisualOverrides(
            plume.ExportNozzleLayout(),
            plume);
        EditorUtility.DisplayDialog(
            "推进器喷口编辑器",
            message,
            "确定");
    }

    private void RefreshScene()
    {
        Repaint();
        SceneView.RepaintAll();
    }
}

public sealed class MechThrusterNozzleEditorWindow : EditorWindow
{
    private Editor nozzleInspector;
    private MechMainThrusterPlume connectedPlume;
    private Vector2 windowScroll;

    public static void OpenWindow()
    {
        var window = GetWindow<MechThrusterNozzleEditorWindow>();
        window.titleContent = new GUIContent("推进器喷口编辑器");
        window.minSize = new Vector2(430f, 520f);
        window.Show();
        window.Focus();
    }

    private void OnEnable()
    {
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    private void OnDisable()
    {
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        ReleaseInspector();
    }

    private void OnInspectorUpdate()
    {
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("推进器喷口编辑器", EditorStyles.boldLabel);

        if (!EditorApplication.isPlaying)
        {
            ReleaseInspector();
            EditorGUILayout.HelpBox(
                "喷口依附于运行时装配的机体骨骼，当前 Edit Mode 中没有可编辑的喷口实例。",
                MessageType.Info);

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("进入 Play Mode 并连接玩家机体", GUILayout.Height(36f)))
                {
                    EditorApplication.isPlaying = true;
                }
            }

            return;
        }

        var plume = MechThrusterNozzleEditor.FindRuntimePlume();
        if (plume == null)
        {
            ReleaseInspector();
            EditorGUILayout.HelpBox(
                "正在等待玩家机体完成运行时装配……",
                MessageType.Info);
            return;
        }

        EnsureInspector(plume);
        windowScroll = EditorGUILayout.BeginScrollView(windowScroll);
        EditorGUILayout.ObjectField("当前机体", plume, typeof(MechMainThrusterPlume), true);
        EditorGUILayout.Space(4f);
        nozzleInspector?.OnInspectorGUI();
        EditorGUILayout.EndScrollView();
    }

    private void EnsureInspector(MechMainThrusterPlume plume)
    {
        if (connectedPlume == plume && nozzleInspector != null)
        {
            return;
        }

        ReleaseInspector();
        connectedPlume = plume;
        nozzleInspector = Editor.CreateEditor(plume, typeof(MechThrusterNozzleEditor));
    }

    private void ReleaseInspector()
    {
        connectedPlume = null;
        if (nozzleInspector != null)
        {
            DestroyImmediate(nozzleInspector);
            nozzleInspector = null;
        }
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode ||
            state == PlayModeStateChange.EnteredEditMode)
        {
            ReleaseInspector();
        }

        Repaint();
    }
}
