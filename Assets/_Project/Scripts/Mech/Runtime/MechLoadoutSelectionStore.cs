using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public static class MechLoadoutSelectionStore
    {
        public const string CatalogResourcePath = "AC6FullPartCatalog";
        private const string SelectionPrefix = "acunicombat.hangar.part.";
        private const string HasSelectionKey = "acunicombat.hangar.has-selection";
        private const string SkipNextOpenKey = "acunicombat.hangar.skip-next-open";

        private static MechLoadoutDefinition runtimeLoadout;

        public static MechPartCatalog LoadCatalog()
        {
            return Resources.Load<MechPartCatalog>(CatalogResourcePath);
        }

        public static bool HasSavedSelection => PlayerPrefs.GetInt(HasSelectionKey, 0) != 0;

        public static bool ConsumeSkipNextOpen()
        {
            if (PlayerPrefs.GetInt(SkipNextOpenKey, 0) == 0)
            {
                return false;
            }

            PlayerPrefs.DeleteKey(SkipNextOpenKey);
            PlayerPrefs.Save();
            return true;
        }

        public static MechLoadoutDefinition ResolvePlayerLoadout(
            GameObject owner,
            MechLoadoutDefinition defaultLoadout)
        {
            if (owner == null || defaultLoadout == null || !IsPlayerMech(owner) || !HasSavedSelection)
            {
                return defaultLoadout;
            }

            var catalog = LoadCatalog();
            if (catalog == null)
            {
                Debug.LogWarning("[MechHangar] Saved loadout was ignored because the full part catalog is missing.");
                return defaultLoadout;
            }

            var assignments = new List<MechPartAssignment>();
            var defaults = new Dictionary<MechPartSlot, MechPartDefinition>();
            foreach (var defaultAssignment in defaultLoadout.EnumerateEnabledAssignments())
            {
                if (defaultAssignment?.part == null)
                {
                    continue;
                }

                defaults[defaultAssignment.slot] = defaultAssignment.part;
            }

            for (var i = 0; i < MechSlotRules.AssemblyOrder.Length; i++)
            {
                var slot = MechSlotRules.AssemblyOrder[i];
                defaults.TryGetValue(slot, out var defaultPart);
                var selectedPart = LoadSelectedPart(catalog, slot) ?? defaultPart;
                if (selectedPart == null)
                {
                    continue;
                }

                assignments.Add(new MechPartAssignment
                {
                    slot = slot,
                    part = selectedPart,
                    enabled = true
                });
            }

            runtimeLoadout = ScriptableObject.CreateInstance<MechLoadoutDefinition>();
            runtimeLoadout.name = "Runtime_Player_Custom_Loadout";
            runtimeLoadout.hideFlags = HideFlags.DontSave;
            runtimeLoadout.ConfigureRuntime(
                "runtime_player_custom",
                "玩家自定义机体",
                assignments);
            return runtimeLoadout;
        }

        public static Dictionary<MechPartSlot, string> ReadSelection(MechLoadoutDefinition defaultLoadout)
        {
            var result = new Dictionary<MechPartSlot, string>();
            if (defaultLoadout == null)
            {
                return result;
            }

            foreach (var assignment in defaultLoadout.EnumerateEnabledAssignments())
            {
                if (assignment?.part == null)
                {
                    continue;
                }

                var saved = PlayerPrefs.GetString(Key(assignment.slot), string.Empty);
                result[assignment.slot] = string.IsNullOrWhiteSpace(saved)
                    ? assignment.part.PartId
                    : saved;
            }

            for (var i = 0; i < MechSlotRules.AssemblyOrder.Length; i++)
            {
                var slot = MechSlotRules.AssemblyOrder[i];
                if (result.ContainsKey(slot) || !PlayerPrefs.HasKey(Key(slot)))
                {
                    continue;
                }

                var saved = PlayerPrefs.GetString(Key(slot), string.Empty);
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    result[slot] = saved;
                }
            }

            return result;
        }

        public static void SaveSelection(IReadOnlyDictionary<MechPartSlot, string> selection)
        {
            if (selection == null)
            {
                return;
            }

            foreach (var pair in selection)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                {
                    PlayerPrefs.SetString(Key(pair.Key), pair.Value);
                }
            }

            PlayerPrefs.SetInt(HasSelectionKey, 1);
            PlayerPrefs.SetInt(SkipNextOpenKey, 1);
            PlayerPrefs.Save();
        }

        public static void ResetToDefault()
        {
            foreach (MechPartSlot slot in Enum.GetValues(typeof(MechPartSlot)))
            {
                PlayerPrefs.DeleteKey(Key(slot));
            }

            PlayerPrefs.DeleteKey(HasSelectionKey);
            PlayerPrefs.SetInt(SkipNextOpenKey, 1);
            PlayerPrefs.Save();
        }

        private static MechPartDefinition LoadSelectedPart(MechPartCatalog catalog, MechPartSlot slot)
        {
            if (!PlayerPrefs.HasKey(Key(slot)))
            {
                return null;
            }

            var partId = PlayerPrefs.GetString(Key(slot), string.Empty);
            return string.IsNullOrWhiteSpace(partId) ? null : catalog.Find(partId)?.LoadPart();
        }

        private static bool IsPlayerMech(GameObject owner)
        {
            return owner.name.StartsWith("Player_", StringComparison.OrdinalIgnoreCase) ||
                   owner.name.Equals("Player", StringComparison.OrdinalIgnoreCase);
        }

        private static string Key(MechPartSlot slot)
        {
            return SelectionPrefix + (int)slot;
        }
    }
}
