using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    // One versioned, layout-scoped document. Empty entries mean intentionally unequipped.
    public static class MechLayoutSelection
    {
        [Serializable] public sealed class Entry { public string slotId; public string partId; }
        [Serializable] public sealed class Document
        {
            public int version = 1;
            public string platformId;
            public List<Entry> entries = new List<Entry>();
        }
        private static string Key(MechPlatformDefinition layout) => "acunicombat.hangar.layout." + layout.platformId;
        public static string Export(MechLoadoutDefinition loadout)
        {
            var document = new Document { platformId = loadout.Platform.platformId };
            foreach (var slot in loadout.GetSlots())
            {
                loadout.TryGetPart(slot.id, out var part);
                document.entries.Add(new Entry { slotId = slot.id, partId = part != null ? part.PartId : "" });
            }
            return JsonUtility.ToJson(document);
        }
        public static bool TryImport(string json, MechLoadoutDefinition defaults, Func<string, MechPartDefinition> resolve,
            out MechLoadoutDefinition loaded, out string error)
        {
            loaded = null; error = null;
            Document doc;
            try { doc = JsonUtility.FromJson<Document>(json); }
            catch (Exception e) { error = "Invalid loadout document: " + e.Message; return false; }
            if (doc == null || doc.version != 1 || doc.platformId != defaults.Platform.platformId || doc.entries == null)
            { error = "Unsupported version or mismatched platform."; return false; }
            var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var assignments = new List<MechPartAssignment>();
            foreach (var e in doc.entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.slotId) || entries.ContainsKey(e.slotId))
                { error = "Empty or duplicate saved slot ID."; return false; }
                entries.Add(e.slotId, e);
                if (string.IsNullOrEmpty(e.partId)) continue;
                var part = resolve(e.partId);
                if (part == null) { error = "Missing saved part: " + e.partId; return false; }
                assignments.Add(new MechPartAssignment { slotId = e.slotId, part = part });
            }
            // Missing IDs are new slots: intentionally empty, never populated by list position.
            var candidate = ScriptableObject.CreateInstance<MechLoadoutDefinition>();
            candidate.hideFlags = HideFlags.DontSave;
            candidate.ConfigureRuntime(defaults.LoadoutId, defaults.DisplayName, assignments);
            candidate.ConfigureLayout(defaults.Platform);
            var result = MechLoadoutValidator.Validate(candidate);
            if (!result.IsValid)
            {
                error = string.Join("; ", new List<MechValidationIssue>(result.Issues).ConvertAll(x => x.message));
                UnityEngine.Object.DestroyImmediate(candidate); return false;
            }
            loaded = candidate; return true;
        }
        public static void Save(MechLoadoutDefinition loadout)
        {
            if (!MechLoadoutValidator.Validate(loadout).IsValid) throw new InvalidOperationException("Cannot save invalid loadout.");
            PlayerPrefs.SetString(Key(loadout.Platform), Export(loadout)); PlayerPrefs.Save();
        }
        public static MechLoadoutDefinition Resolve(MechLoadoutDefinition defaults, MechPartCatalog catalog)
        {
            if (!PlayerPrefs.HasKey(Key(defaults.Platform))) return defaults;
            var knownParts = new Dictionary<string, MechPartDefinition>(StringComparer.Ordinal);
            foreach (var assignment in defaults.EnumerateEnabledAssignments())
                if (assignment.part != null) knownParts[assignment.part.PartId] = assignment.part;
            if (TryImport(PlayerPrefs.GetString(Key(defaults.Platform)), defaults,
                id => knownParts.TryGetValue(id, out var part) ? part : catalog.Find(id)?.LoadPart(), out var loaded, out var error)) return loaded;
            Debug.LogWarning("[MechHangar] Saved layout was not loaded: " + error); return defaults;
        }
    }
}
