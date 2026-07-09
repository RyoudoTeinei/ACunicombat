using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(MechAssembler))]
public sealed class MechAssemblerEditor : Editor
{
    private bool showStats = true;
    private bool showValidation = true;
    private bool showRuntime = true;

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var assembler = (MechAssembler)target;

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Mech Debug", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Assemble Now"))
            {
                Undo.RegisterFullObjectHierarchyUndo(assembler.gameObject, "Assemble Mech");
                assembler.Assemble();
                EditorUtility.SetDirty(assembler);
            }

            if (GUILayout.Button("Validate"))
            {
                LogValidation(assembler.ValidateCurrentLoadout());
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Add Debug Gizmos"))
            {
                AddDebugGizmos(assembler);
            }

            if (GUILayout.Button("Ping Loadout") && assembler.Loadout != null)
            {
                Selection.activeObject = assembler.Loadout;
                EditorGUIUtility.PingObject(assembler.Loadout);
            }
        }

        if (assembler.GetComponent<MechDebugGizmos>() != null)
        {
            EditorGUILayout.HelpBox(
                "Debug gizmos are installed. Keep the Scene view Gizmos toggle enabled to see part bounds and mount points.",
                MessageType.Info);
        }

        var validation = assembler.ValidateCurrentLoadout();
        var stats = assembler.CalculateCurrentStats();
        var runtime = assembler.GetComponent<MechRuntimeState>();

        DrawValidation(validation);
        DrawStats(stats);
        DrawRuntime(runtime);
    }

    private void DrawValidation(MechValidationResult validation)
    {
        showValidation = EditorGUILayout.Foldout(showValidation, "Validation", true);
        if (!showValidation)
        {
            return;
        }

        if (validation == null)
        {
            EditorGUILayout.HelpBox("No validation result.", MessageType.Info);
            return;
        }

        var messageType = validation.ErrorCount > 0
            ? MessageType.Error
            : validation.WarningCount > 0
                ? MessageType.Warning
                : MessageType.Info;

        EditorGUILayout.HelpBox(
            validation.IsValid
                ? "Loadout is valid."
                : $"Loadout has {validation.ErrorCount} error(s) and {validation.WarningCount} warning(s).",
            messageType);

        for (var i = 0; i < validation.Issues.Count; i++)
        {
            var issue = validation.Issues[i];
            EditorGUILayout.LabelField(
                $"{issue.severity} [{issue.code}] {issue.slot}",
                issue.message,
                EditorStyles.wordWrappedLabel);
        }
    }

    private void DrawStats(MechAggregatedStats stats)
    {
        showStats = EditorGUILayout.Foldout(showStats, "Aggregated Stats", true);
        if (!showStats || stats == null)
        {
            return;
        }

        DrawStat("Mass", $"{stats.totalMassKg:0} kg");
        DrawStat("Armor / Structure", $"{stats.armorPoints:0} / {stats.structurePoints:0}");
        DrawStat("Main Acceleration", $"{stats.mainAccelerationMps2:0.00} m/s^2");
        DrawStat("Lateral / Reverse Accel", $"{stats.lateralAccelerationMps2:0.00} / {stats.reverseAccelerationMps2:0.00} m/s^2");
        DrawStat("Attitude Torque", $"{stats.attitudeTorqueKnM:0.0} kN*m ({stats.attitudeTorquePerTon:0.00} per ton)");
        DrawStat("Power Output", $"{stats.powerOutputMw:0.0} MW");
        DrawStat("Idle / Peak Draw", $"{stats.idlePowerDrawMw:0.0} / {stats.peakPowerDrawMw:0.0} MW");
        DrawStat("Peak Power Surplus", $"{stats.peakPowerSurplusMw:0.0} MW");
        DrawStat("Heat Gen / Dissipation", $"{stats.heatGenerationMw:0.0} / {stats.heatDissipationMw:0.0} MW");
        DrawStat("Radar Range / Refresh", $"{stats.radarRangeKm:0.0} km / {stats.radarRefreshHz:0.0} Hz");
        DrawStat("Tracks / Guidance", $"{stats.trackCapacity} / {stats.guidanceChannels}");
        DrawStat("Radar / Thermal / EM Signature", $"{stats.radarCrossSection:0.00} / {stats.thermalSignature:0.00} / {stats.electromagneticSignature:0.00}");
    }

    private void DrawRuntime(MechRuntimeState runtime)
    {
        showRuntime = EditorGUILayout.Foldout(showRuntime, "Runtime State", true);
        if (!showRuntime)
        {
            return;
        }

        if (runtime == null)
        {
            EditorGUILayout.HelpBox("No MechRuntimeState yet. Click Assemble Now.", MessageType.Info);
            return;
        }

        DrawStat("Runtime Valid", runtime.LoadoutValid ? "Yes" : "No");
        DrawStat("Assembled Parts", runtime.AssembledParts.Count.ToString());
        DrawStat("Mount Points", runtime.MountPoints.Count.ToString());
        DrawStat("Runtime Issues", runtime.ValidationIssues.Count.ToString());
    }

    private void DrawStat(string label, string value)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, GUILayout.Width(170f));
            EditorGUILayout.SelectableLabel(value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }
    }

    private void LogValidation(MechValidationResult validation)
    {
        if (validation == null || validation.Issues.Count == 0)
        {
            Debug.Log("Mech loadout validation passed.");
            return;
        }

        for (var i = 0; i < validation.Issues.Count; i++)
        {
            var issue = validation.Issues[i];
            var text = $"Mech validation {issue.severity}: [{issue.code}] {issue.slot} - {issue.message}";
            if (issue.severity == MechValidationSeverity.Error)
            {
                Debug.LogError(text);
            }
            else if (issue.severity == MechValidationSeverity.Warning)
            {
                Debug.LogWarning(text);
            }
            else
            {
                Debug.Log(text);
            }
        }
    }

    private void AddDebugGizmos(MechAssembler assembler)
    {
        var gizmos = assembler.GetComponent<MechDebugGizmos>();
        if (gizmos == null)
        {
            gizmos = Undo.AddComponent<MechDebugGizmos>(assembler.gameObject);
        }

        gizmos.ResetToVisibleDefaults();
        EditorUtility.SetDirty(gizmos);
        EditorUtility.SetDirty(assembler.gameObject);
        Selection.activeGameObject = assembler.gameObject;
        SceneView.RepaintAll();
        Debug.Log("Mech debug gizmos enabled. If nothing is visible, turn on the Scene view Gizmos toggle.", assembler);
    }
}
