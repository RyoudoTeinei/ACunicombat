using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    // Layout-driven hangar view; all mutations pass through the same dependency/compatibility API as the editor.
    public sealed class MechLayoutMenu
    {
        private MechLoadoutDefinition draft;
        private readonly List<MechPartDefinition> parts = new List<MechPartDefinition>();
        private Vector2 scroll;
        private string selectedId;
        private string message;
        public void Dispose() { if (draft != null) Object.Destroy(draft); draft = null; parts.Clear(); }
        public void Draw(MechAssembler assembler, MechPartCatalog catalog, System.Action close, System.Action apply, System.Action<MechLoadoutDefinition> save = null)
        {
            if (draft == null)
            {
                draft = Object.Instantiate(assembler.Loadout); draft.hideFlags = HideFlags.DontSave;
                foreach (var assignment in draft.EnumerateEnabledAssignments())
                    if (assignment.part != null && !parts.Contains(assignment.part)) parts.Add(assignment.part);
                foreach (var entry in catalog.Entries)
                { var part = entry.LoadPart(); if (part != null && !parts.Contains(part)) parts.Add(part); }
            }
            GUILayout.BeginArea(new Rect(28, 28, Screen.width - 56, Screen.height - 56), GUI.skin.box);
            GUILayout.Label(draft.DisplayName + " — 配装");
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var slot in draft.GetSlots())
            {
                draft.TryGetPart(slot.id, out var current);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(slot.Label + " : " + (current != null ? current.DisplayName : "空置"), GUILayout.MinHeight(36))) selectedId = slot.id;
                if (current != null && GUILayout.Button("卸下", GUILayout.Width(70))) draft.TrySetPart(slot.id, null, out message);
                GUILayout.EndHorizontal();
                if (selectedId != slot.id) continue;
                foreach (var part in parts)
                    if (slot.Accepts(part) && MechSpecialistKits.CandidateAllowed(draft,slot,part) && GUILayout.Button("    " + part.DisplayName))
                    { draft.TrySetPart(slot.id, part, out message); selectedId = null; }
            }
            GUILayout.EndScrollView();
            var validation = MechLoadoutValidator.Validate(draft);
            foreach (var issue in validation.Issues) GUILayout.Label(issue.message);
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(message);
            GUILayout.Label("总质量 " + MechStatsCalculator.Calculate(draft).totalMassKg.ToString("0") + " kg");
            GUILayout.BeginHorizontal();
            GUI.enabled = validation.IsValid;
            if (GUILayout.Button("保存并应用", GUILayout.Height(40)))
            {
                if(save!=null){save(draft);close();}
                else{MechLayoutSelection.Save(draft);apply();}
            }
            GUI.enabled = true;
            if (GUILayout.Button("返回", GUILayout.Height(40))) close();
            GUILayout.EndHorizontal(); GUILayout.EndArea();
        }
    }
}
