using System.Collections.Generic;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public sealed class MechLayoutMigration : EditorWindow
{
    private MechLoadoutDefinition source;
    private Vector2 scroll;
    [MenuItem("Tools/ACunicombat/Preview Layout Migration")]
    public static void Open() => GetWindow<MechLayoutMigration>("Layout Migration");
    public static MechLoadoutDefinition ConvertCopy(MechLoadoutDefinition source, out MechPlatformDefinition layout)
    {
        if (source == null || source.Platform != null) throw new System.ArgumentException("Migration requires a legacy loadout.");
        layout = ScriptableObject.CreateInstance<MechPlatformDefinition>();
        layout.platformId = "migrated." + source.LoadoutId;
        var assignments = new List<MechPartAssignment>();
        foreach (var role in MechSlotRules.AssemblyOrder)
        {
            source.TryGetPart(role, out var part);
            var slot = new MechSlotDefinition
            {
                id = MechSlotGraph.LegacyId(role), displayName = MechSlotRules.DisplayName(role), legacyRole = role,
                category = part != null ? part.Category : (MechSlotRules.IsWeaponSlot(role) ? MechPartCategory.Weapon :
                    role == MechPartSlot.Extension ? MechPartCategory.Utility :
                    (role == MechPartSlot.Head || role == MechPartSlot.Core || role == MechPartSlot.Arms ||
                     role == MechPartSlot.LeftArm || role == MechPartSlot.RightArm || role == MechPartSlot.Legs) ? MechPartCategory.Frame : MechPartCategory.Internal),
                hardpointType = part != null ? part.MountClass : MechSlotRules.RequiredHardpointType(role),
                required = System.Array.IndexOf(MechSlotRules.DefaultRequiredSlots, role) >= 0
            };
            layout.slots.Add(slot);
        }
        foreach (var a in source.Assignments)
            if (a != null) assignments.Add(new MechPartAssignment { slotId = a.SlotId, slot = a.slot, part = a.part, enabled = a.enabled });
        var copy = ScriptableObject.CreateInstance<MechLoadoutDefinition>();
        copy.ConfigureRuntime(source.LoadoutId, source.DisplayName, assignments);
        copy.ConfigureLayout(layout); return copy;
    }
    private void OnGUI()
    {
        source = (MechLoadoutDefinition)EditorGUILayout.ObjectField("Source (read only)", source, typeof(MechLoadoutDefinition), false);
        if (source == null) return;
        if (source.Platform != null) { EditorGUILayout.HelpBox("This loadout already has a layout.", MessageType.Info); return; }
        var copy = ConvertCopy(source, out var layout);
        try
        {
            var validation = MechLoadoutValidator.Validate(copy);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var slot in copy.GetSlots())
            { copy.TryGetPart(slot.id, out var part); EditorGUILayout.LabelField(slot.id, part != null ? part.DisplayName : "Empty"); }
            foreach (var issue in validation.Issues) EditorGUILayout.HelpBox(issue.code + ": " + issue.message, issue.severity == MechValidationSeverity.Error ? MessageType.Error : MessageType.Warning);
            EditorGUILayout.EndScrollView();
            using (new EditorGUI.DisabledScope(!validation.IsValid))
                if (GUILayout.Button("Save converted copies (original assets unchanged)"))
                {
                    var path = EditorUtility.SaveFilePanelInProject("Save migrated loadout", source.name + "_V1", "asset", "Choose a new asset path.");
                    if (!string.IsNullOrEmpty(path))
                    {
                        path = AssetDatabase.GenerateUniqueAssetPath(path);
                        var layoutPath = AssetDatabase.GenerateUniqueAssetPath(path.Replace(".asset", "_Layout.asset"));
                        AssetDatabase.CreateAsset(layout, layoutPath);
                        AssetDatabase.CreateAsset(copy, path); AssetDatabase.SaveAssets(); Selection.activeObject = copy;
                    }
                }
        }
        finally
        {
            if (!AssetDatabase.Contains(copy)) DestroyImmediate(copy);
            if (!AssetDatabase.Contains(layout)) DestroyImmediate(layout);
        }
    }
}
