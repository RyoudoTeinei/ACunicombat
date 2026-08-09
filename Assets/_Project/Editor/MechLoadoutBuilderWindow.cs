using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public sealed class MechLoadoutBuilderWindow : EditorWindow
{
    private const string DefaultLoadoutFolder = "Assets/_Project/Data/Mechs/Loadouts";

    private static readonly MechPartSlot[] EditableSlots =
    {
        MechPartSlot.Head,
        MechPartSlot.Core,
        MechPartSlot.Arms,
        MechPartSlot.LeftArm,
        MechPartSlot.RightArm,
        MechPartSlot.Legs,
        MechPartSlot.Booster,
        MechPartSlot.Generator,
        MechPartSlot.FireControlSystem,
        MechPartSlot.Radar,
        MechPartSlot.LeftHandWeapon,
        MechPartSlot.RightHandWeapon,
        MechPartSlot.LeftBackWeapon,
        MechPartSlot.RightBackWeapon,
        MechPartSlot.Extension
    };

    private readonly List<MechPartDefinition> allParts = new List<MechPartDefinition>();
    private readonly Dictionary<MechPartSlot, List<MechPartDefinition>> partsBySlot =
        new Dictionary<MechPartSlot, List<MechPartDefinition>>();

    private MechLoadoutDefinition loadout;
    private MechAssembler targetAssembler;
    private Vector2 slotScroll;
    private Vector2 detailScroll;
    private string filterText = string.Empty;
    private MechPartSlot selectedSlot = MechPartSlot.Core;

    [MenuItem("Tools/ACunicombat/Mech/Open Loadout Builder")]
    public static void Open()
    {
        var window = GetWindow<MechLoadoutBuilderWindow>("Mech Loadout");
        window.minSize = new Vector2(860f, 520f);
        window.SyncFromSelection(false);
        window.RefreshPartCache();
        window.Show();
    }

    private void OnEnable()
    {
        Selection.selectionChanged += Repaint;
        RefreshPartCache();
        SyncFromSelection(false);
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= Repaint;
    }

    private void OnGUI()
    {
        DrawHeader();

        if (loadout == null)
        {
            EditorGUILayout.HelpBox("Choose or create a MechLoadoutDefinition to start editing.", MessageType.Info);
            return;
        }

        DrawLoadoutIdentity();

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(520f)))
            {
                DrawSlotList();
            }

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(310f)))
            {
                DrawSummaryPanel();
            }
        }
    }

    private void DrawHeader()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Loadout Builder", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                var newAssembler = EditorGUILayout.ObjectField(
                    "Target Mech",
                    targetAssembler,
                    typeof(MechAssembler),
                    true) as MechAssembler;
                if (newAssembler != targetAssembler)
                {
                    targetAssembler = newAssembler;
                    if (targetAssembler != null && targetAssembler.Loadout != null)
                    {
                        loadout = targetAssembler.Loadout;
                    }
                }

                if (GUILayout.Button("Use Selection", GUILayout.Width(110f)))
                {
                    SyncFromSelection(true);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var newLoadout = EditorGUILayout.ObjectField(
                    "Loadout",
                    loadout,
                    typeof(MechLoadoutDefinition),
                    false) as MechLoadoutDefinition;
                if (newLoadout != loadout)
                {
                    loadout = newLoadout;
                }

                if (GUILayout.Button("New", GUILayout.Width(70f)))
                {
                    CreateNewLoadout();
                }

                using (new EditorGUI.DisabledScope(loadout == null))
                {
                    if (GUILayout.Button("Duplicate", GUILayout.Width(90f)))
                    {
                        DuplicateCurrentLoadout();
                    }

                    if (GUILayout.Button("Ping", GUILayout.Width(70f)))
                    {
                        Selection.activeObject = loadout;
                        EditorGUIUtility.PingObject(loadout);
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                filterText = EditorGUILayout.TextField("Part Filter", filterText);

                if (GUILayout.Button("Refresh Parts", GUILayout.Width(110f)))
                {
                    RefreshPartCache();
                }

                using (new EditorGUI.DisabledScope(loadout == null))
                {
                    if (GUILayout.Button("Save", GUILayout.Width(70f)))
                    {
                        AssetDatabase.SaveAssets();
                    }

                    if (GUILayout.Button("Apply + Assemble", GUILayout.Width(125f)))
                    {
                        ApplyToTargetAndAssemble();
                    }
                }
            }
        }
    }

    private void DrawLoadoutIdentity()
    {
        var serializedLoadout = new SerializedObject(loadout);
        serializedLoadout.Update();

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.PropertyField(serializedLoadout.FindProperty("loadoutId"));
            EditorGUILayout.PropertyField(serializedLoadout.FindProperty("displayName"));
        }

        serializedLoadout.ApplyModifiedProperties();
    }

    private void DrawSlotList()
    {
        EditorGUILayout.LabelField("Parts", EditorStyles.boldLabel);

        slotScroll = EditorGUILayout.BeginScrollView(slotScroll);
        var assignmentMap = BuildAssignmentMap(loadout);
        for (var i = 0; i < EditableSlots.Length; i++)
        {
            DrawSlotRow(EditableSlots[i], assignmentMap);
        }

        EditorGUILayout.EndScrollView();
    }

    private void DrawSlotRow(MechPartSlot slot, Dictionary<MechPartSlot, MechPartDefinition> assignmentMap)
    {
        assignmentMap.TryGetValue(slot, out var currentPart);
        var candidates = GetFilteredCandidates(slot);
        var popupOptions = BuildPopupOptions(candidates, currentPart);
        var currentIndex = GetPopupIndex(candidates, currentPart);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Toggle(selectedSlot == slot, SlotLabel(slot), "Button", GUILayout.Width(150f)))
                {
                    selectedSlot = slot;
                }

                EditorGUI.BeginChangeCheck();
                var selectedIndex = EditorGUILayout.Popup(currentIndex, popupOptions);
                if (EditorGUI.EndChangeCheck())
                {
                    var selectedPart = selectedIndex <= 0 ? null : ResolvePopupSelection(candidates, currentPart, selectedIndex);
                    SetPart(slot, selectedPart);
                    selectedSlot = slot;
                }

                using (new EditorGUI.DisabledScope(currentPart == null))
                {
                    if (GUILayout.Button("Ping", GUILayout.Width(46f)))
                    {
                        Selection.activeObject = currentPart;
                        EditorGUIUtility.PingObject(currentPart);
                    }
                }
            }

            if (currentPart != null)
            {
                DrawMiniPartStats(currentPart);
            }
            else
            {
                EditorGUILayout.LabelField("Empty slot", EditorStyles.miniLabel);
            }
        }
    }

    private void DrawSummaryPanel()
    {
        detailScroll = EditorGUILayout.BeginScrollView(detailScroll);
        DrawValidation();
        DrawStats();
        DrawSelectedPartDetails();
        EditorGUILayout.EndScrollView();
    }

    private void DrawValidation()
    {
        var validation = MechLoadoutValidator.Validate(loadout, new MechValidationOptions());
        var messageType = validation.IsValid
            ? MessageType.Info
            : validation.ErrorCount > 0
                ? MessageType.Error
                : MessageType.Warning;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                validation.IsValid
                    ? "Loadout is valid."
                    : $"Errors: {validation.ErrorCount}, Warnings: {validation.WarningCount}",
                messageType);

            for (var i = 0; i < validation.Issues.Count; i++)
            {
                var issue = validation.Issues[i];
                EditorGUILayout.LabelField(
                    $"{issue.severity} [{issue.code}] {SlotLabel(issue.slot)}",
                    issue.message,
                    EditorStyles.wordWrappedLabel);
            }
        }
    }

    private void DrawStats()
    {
        var stats = MechStatsCalculator.Calculate(loadout);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Aggregated Stats", EditorStyles.boldLabel);
            DrawStat("Mass", $"{stats.totalMassKg:0} kg");
            DrawStat("Armor / Structure", $"{stats.armorPoints:0} / {stats.structurePoints:0}");
            DrawStat("Main Accel", $"{stats.mainAccelerationMps2:0.00} m/s^2");
            DrawStat("Lateral / Reverse", $"{stats.lateralAccelerationMps2:0.00} / {stats.reverseAccelerationMps2:0.00} m/s^2");
            DrawStat("Upward / Downward", $"{stats.upwardAccelerationMps2:0.00} / {stats.downwardAccelerationMps2:0.00} m/s^2");
            DrawStat("Power Surplus", $"{stats.peakPowerSurplusMw * 1000f:0.#} kW peak");
            DrawStat("Heat Surplus", $"{stats.heatSurplusMw * 1000f:0.#} kW");
            DrawStat("Radar", $"{stats.radarRangeKm:0.0} km / {stats.radarRefreshHz:0.0} Hz");
            DrawStat("Tracks / Guidance", $"{stats.trackCapacity} / {stats.guidanceChannels}");
            DrawStat("Signatures", $"{stats.radarCrossSection:0.00} R / {stats.thermalSignature:0.00} T / {stats.electromagneticSignature:0.00} EM");
        }
    }

    private void DrawSelectedPartDetails()
    {
        var map = BuildAssignmentMap(loadout);
        map.TryGetValue(selectedSlot, out var part);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Selected Slot", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(SlotLabel(selectedSlot));

            if (part == null)
            {
                EditorGUILayout.HelpBox("No part installed in this slot.", MessageType.Info);
                return;
            }

            EditorGUILayout.ObjectField("Part", part, typeof(MechPartDefinition), false);
            EditorGUILayout.LabelField("Category", part.Category.ToString());
            EditorGUILayout.LabelField("Manufacturer", string.IsNullOrWhiteSpace(part.Manufacturer) ? "-" : part.Manufacturer);
            DrawPartStats(part.Stats);
        }
    }

    private void DrawMiniPartStats(MechPartDefinition part)
    {
        var stats = part.Stats;
        EditorGUILayout.LabelField(
            $"{part.DisplayName} | {part.Category} | {stats.massKg:0} kg | idle {stats.idlePowerDrawMw * 1000f:0.#} kW | peak {stats.peakPowerDrawMw * 1000f:0.#} kW",
            EditorStyles.miniLabel);
    }

    private void DrawPartStats(MechPartStats stats)
    {
        if (stats == null)
        {
            return;
        }

        DrawStat("Mass", $"{stats.massKg:0} kg");
        DrawStat("Armor / Structure", $"{stats.armorPoints:0} / {stats.structurePoints:0}");
        DrawStat("Power", $"{stats.powerOutputMw:0.0} out, {stats.peakPowerDrawMw:0.0} peak draw");
        DrawStat("Thrust M/L/R", $"{stats.mainThrustKn:0} / {stats.lateralThrustKn:0} / {stats.reverseThrustKn:0} kN");
        DrawStat("Thrust Up/Down", $"{stats.upwardThrustKn:0} / {stats.downwardThrustKn:0} kN");
        DrawStat("Radar", $"{stats.radarRangeKm:0.0} km, {stats.radarRefreshHz:0.0} Hz");
        DrawStat("FCS", $"{stats.fireControlQuality:0.0} quality, {stats.guidanceChannels} channels");
    }

    private void DrawStat(string label, string value)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField(label, GUILayout.Width(120f));
            EditorGUILayout.SelectableLabel(value, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }
    }

    private void SyncFromSelection(bool warnIfMissing)
    {
        if (Selection.activeGameObject == null)
        {
            if (warnIfMissing)
            {
                EditorUtility.DisplayDialog("Loadout Builder", "Select a GameObject with a MechAssembler first.", "OK");
            }

            return;
        }

        var assembler = Selection.activeGameObject.GetComponent<MechAssembler>();
        if (assembler == null)
        {
            if (warnIfMissing)
            {
                EditorUtility.DisplayDialog("Loadout Builder", "The selected GameObject does not have a MechAssembler.", "OK");
            }

            return;
        }

        targetAssembler = assembler;
        if (assembler.Loadout != null)
        {
            loadout = assembler.Loadout;
        }

        Repaint();
    }

    private void RefreshPartCache()
    {
        allParts.Clear();
        partsBySlot.Clear();

        var guids = AssetDatabase.FindAssets("t:MechPartDefinition");
        for (var i = 0; i < guids.Length; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var part = AssetDatabase.LoadAssetAtPath<MechPartDefinition>(path);
            if (part == null)
            {
                continue;
            }

            allParts.Add(part);
            if (!partsBySlot.TryGetValue(part.Slot, out var list))
            {
                list = new List<MechPartDefinition>();
                partsBySlot.Add(part.Slot, list);
            }

            list.Add(part);
        }

        allParts.Sort(CompareParts);
        foreach (var pair in partsBySlot)
        {
            pair.Value.Sort(CompareParts);
        }

        Repaint();
    }

    private List<MechPartDefinition> GetFilteredCandidates(MechPartSlot slot)
    {
        if (!partsBySlot.TryGetValue(slot, out var source))
        {
            return new List<MechPartDefinition>();
        }

        if (string.IsNullOrWhiteSpace(filterText))
        {
            return source;
        }

        var filtered = new List<MechPartDefinition>();
        for (var i = 0; i < source.Count; i++)
        {
            var part = source[i];
            if (PartMatchesFilter(part, filterText))
            {
                filtered.Add(part);
            }
        }

        return filtered;
    }

    private void SetPart(MechPartSlot slot, MechPartDefinition part)
    {
        if (loadout == null)
        {
            return;
        }

        Undo.RecordObject(loadout, "Change Mech Loadout");

        var assignments = new List<MechPartAssignment>();
        var replaced = false;
        var existingAssignments = loadout.Assignments;
        for (var i = 0; i < existingAssignments.Count; i++)
        {
            var existing = existingAssignments[i];
            if (existing == null)
            {
                continue;
            }

            if (existing.slot == slot)
            {
                if (!replaced && part != null)
                {
                    assignments.Add(new MechPartAssignment
                    {
                        slot = slot,
                        part = part,
                        enabled = true
                    });
                    replaced = true;
                }

                continue;
            }

            assignments.Add(new MechPartAssignment
            {
                slot = existing.slot,
                part = existing.part,
                enabled = existing.enabled
            });
        }

        if (!replaced && part != null)
        {
            assignments.Add(new MechPartAssignment
            {
                slot = slot,
                part = part,
                enabled = true
            });
        }

        loadout.EditorConfigure(loadout.LoadoutId, loadout.DisplayName, assignments);
        EditorUtility.SetDirty(loadout);
    }

    private void ApplyToTargetAndAssemble()
    {
        if (targetAssembler == null)
        {
            EditorUtility.DisplayDialog("Loadout Builder", "Assign a target MechAssembler first.", "OK");
            return;
        }

        if (loadout == null)
        {
            EditorUtility.DisplayDialog("Loadout Builder", "Assign a loadout first.", "OK");
            return;
        }

        Undo.RegisterFullObjectHierarchyUndo(targetAssembler.gameObject, "Apply Mech Loadout");
        targetAssembler.Loadout = loadout;
        targetAssembler.Assemble();
        EditorUtility.SetDirty(targetAssembler);
        Selection.activeGameObject = targetAssembler.gameObject;
        SceneView.RepaintAll();
    }

    private void CreateNewLoadout()
    {
        EnsureFolder(DefaultLoadoutFolder);

        var path = AssetDatabase.GenerateUniqueAssetPath(DefaultLoadoutFolder + "/New_Space_AC_Loadout.asset");
        var asset = CreateInstance<MechLoadoutDefinition>();
        var id = Path.GetFileNameWithoutExtension(path);
        asset.EditorConfigure(id, "New Space AC Loadout", Array.Empty<MechPartAssignment>());
        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        loadout = asset;
        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }

    private void DuplicateCurrentLoadout()
    {
        if (loadout == null)
        {
            return;
        }

        var sourcePath = AssetDatabase.GetAssetPath(loadout);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(sourcePath);
        var fileName = Path.GetFileNameWithoutExtension(sourcePath) + "_Copy.asset";
        var targetPath = AssetDatabase.GenerateUniqueAssetPath((directory + "/" + fileName).Replace("\\", "/"));
        AssetDatabase.CopyAsset(sourcePath, targetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(targetPath);
        Selection.activeObject = loadout;
        EditorGUIUtility.PingObject(loadout);
    }

    private static Dictionary<MechPartSlot, MechPartDefinition> BuildAssignmentMap(MechLoadoutDefinition sourceLoadout)
    {
        var map = new Dictionary<MechPartSlot, MechPartDefinition>();
        if (sourceLoadout == null)
        {
            return map;
        }

        var assignments = sourceLoadout.Assignments;
        for (var i = 0; i < assignments.Count; i++)
        {
            var assignment = assignments[i];
            if (assignment == null || !assignment.enabled || assignment.slot == MechPartSlot.None)
            {
                continue;
            }

            map[assignment.slot] = assignment.part;
        }

        return map;
    }

    private static string[] BuildPopupOptions(List<MechPartDefinition> candidates, MechPartDefinition currentPart)
    {
        var hasCurrentOutsideFilter = currentPart != null && !candidates.Contains(currentPart);
        var extra = hasCurrentOutsideFilter ? 1 : 0;
        var options = new string[candidates.Count + 1 + extra];
        options[0] = "None";

        for (var i = 0; i < candidates.Count; i++)
        {
            options[i + 1] = PartLabel(candidates[i]);
        }

        if (hasCurrentOutsideFilter)
        {
            options[options.Length - 1] = PartLabel(currentPart) + " (current)";
        }

        return options;
    }

    private static int GetPopupIndex(List<MechPartDefinition> candidates, MechPartDefinition currentPart)
    {
        if (currentPart == null)
        {
            return 0;
        }

        var index = candidates.IndexOf(currentPart);
        return index >= 0 ? index + 1 : candidates.Count + 1;
    }

    private static MechPartDefinition ResolvePopupSelection(
        List<MechPartDefinition> candidates,
        MechPartDefinition currentPart,
        int selectedIndex)
    {
        var candidateIndex = selectedIndex - 1;
        if (candidateIndex >= 0 && candidateIndex < candidates.Count)
        {
            return candidates[candidateIndex];
        }

        return currentPart;
    }

    private static string SlotLabel(MechPartSlot slot)
    {
        switch (slot)
        {
            case MechPartSlot.FireControlSystem:
                return "FCS";
            case MechPartSlot.LeftHandWeapon:
                return "Left Hand Weapon";
            case MechPartSlot.RightHandWeapon:
                return "Right Hand Weapon";
            case MechPartSlot.LeftBackWeapon:
                return "Left Back Weapon";
            case MechPartSlot.RightBackWeapon:
                return "Right Back Weapon";
            default:
                return slot.ToString();
        }
    }

    private static string PartLabel(MechPartDefinition part)
    {
        if (part == null)
        {
            return "None";
        }

        return $"{part.DisplayName} ({part.name})";
    }

    private static bool PartMatchesFilter(MechPartDefinition part, string filter)
    {
        if (part == null)
        {
            return false;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        return part.name.IndexOf(filter, comparison) >= 0
            || part.DisplayName.IndexOf(filter, comparison) >= 0
            || part.PartId.IndexOf(filter, comparison) >= 0
            || part.Category.ToString().IndexOf(filter, comparison) >= 0;
    }

    private static int CompareParts(MechPartDefinition left, MechPartDefinition right)
    {
        return string.Compare(PartLabel(left), PartLabel(right), StringComparison.OrdinalIgnoreCase);
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
}
