using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public static class MechLoadoutSelectionStore
    {
        public const string CatalogResourcePath = "AC6FullPartCatalog";
        private const string SelectionPrefix = "acunicombat.hangar.part.";
        private const string MissileVariantPrefix = "acunicombat.hangar.missile-variant.";
        private const string HasSelectionKey = "acunicombat.hangar.has-selection";
        private const string SkipNextOpenKey = "acunicombat.hangar.skip-next-open";

        private static MechLoadoutDefinition runtimeLoadout;

        private static MechPartCatalog combinedCatalog;
        public static MechPartCatalog LoadCatalog()
        {
            if(combinedCatalog==null)combinedCatalog=MechPartCatalog.Combine(MechPartCatalog.Combine(Resources.Load<MechPartCatalog>(CatalogResourcePath),Resources.Load<MechPartCatalog>("DefenseExtensions/Catalog")),Resources.Load<MechPartCatalog>("SpecialistKits/Catalog"));
            return combinedCatalog;
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
            if(owner==null||!IsPlayerMech(owner))return defaultLoadout;
            return ResolveSavedPlayerLoadout(defaultLoadout);
        }

        public static MechLoadoutDefinition ResolveSavedPlayerLoadout(MechLoadoutDefinition defaultLoadout)
        {
            if (defaultLoadout != null && defaultLoadout.Platform != null)
            {
                var layoutCatalog = LoadCatalog();
                return layoutCatalog == null ? defaultLoadout : MechLayoutSelection.Resolve(defaultLoadout, layoutCatalog);
            }
            if (defaultLoadout == null || !HasSavedSelection)
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
                var selectedPart = PlayerPrefs.HasKey(Key(slot)) ? LoadSelectedPart(catalog, slot) : defaultPart;
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
                if(PlayerPrefs.HasKey(Key(assignment.slot))){if(!string.IsNullOrWhiteSpace(saved))result[assignment.slot]=saved;}
                else result[assignment.slot]=assignment.part.PartId;
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

            foreach(var slot in MechSlotRules.AssemblyOrder)
                PlayerPrefs.SetString(Key(slot),selection.TryGetValue(slot,out var id)?id??string.Empty:string.Empty);

            PlayerPrefs.SetInt(HasSelectionKey, 1);
            PlayerPrefs.SetInt(SkipNextOpenKey, 1);
            PlayerPrefs.Save();
        }

        public static string LoadMissileVariantId(
            MechPartSlot slot,
            string launcherPartId,
            string defaultVariantId)
        {
            string encoded = PlayerPrefs.GetString(MissileVariantKey(slot), string.Empty);
            int separator = encoded.IndexOf('|');
            if (separator <= 0 || separator >= encoded.Length - 1)
            {
                return defaultVariantId;
            }

            string savedLauncherId = encoded.Substring(0, separator);
            string selected = string.Equals(savedLauncherId, launcherPartId, StringComparison.OrdinalIgnoreCase)
                ? encoded.Substring(separator + 1)
                : defaultVariantId;
            var performance = MechMissileLauncherCatalog.CreateDefaultPerformance(launcherPartId);
            return performance == null ? selected : MechMissileVariantCatalog.ResolveVariant(launcherPartId, performance, selected).VariantId;
        }

        public static void SaveMissileVariants(
            IReadOnlyDictionary<MechPartSlot, string> launcherSelection,
            IReadOnlyDictionary<MechPartSlot, string> variantSelection)
        {
            if (launcherSelection == null || variantSelection == null)
            {
                return;
            }

            foreach (MechPartSlot slot in Enum.GetValues(typeof(MechPartSlot)))
            {
                if (launcherSelection.TryGetValue(slot, out string launcherPartId) &&
                    variantSelection.TryGetValue(slot, out string variantId) &&
                    !string.IsNullOrWhiteSpace(launcherPartId) &&
                    !string.IsNullOrWhiteSpace(variantId))
                {
                    PlayerPrefs.SetString(
                        MissileVariantKey(slot),
                        launcherPartId + "|" + variantId);
                }
                else
                {
                    PlayerPrefs.DeleteKey(MissileVariantKey(slot));
                }
            }

            PlayerPrefs.Save();
        }

        public static void ResetToDefault()
        {
            foreach (MechPartSlot slot in Enum.GetValues(typeof(MechPartSlot)))
            {
                PlayerPrefs.DeleteKey(Key(slot));
                PlayerPrefs.DeleteKey(MissileVariantKey(slot));
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

        public static bool IsPlayerMech(GameObject owner)
        {
            if (owner == null)
            {
                return false;
            }

            if(owner.GetComponent<MechAiTargetController>()!=null)return false;
            return owner.GetComponent<MechPlayerInputAdapter>() != null ||
                   owner.name.StartsWith("Player_", StringComparison.OrdinalIgnoreCase) ||
                   owner.name.Equals("Player", StringComparison.OrdinalIgnoreCase) ||
                   owner.name.IndexOf("PlayerControl", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Key(MechPartSlot slot)
        {
            return SelectionPrefix + (int)slot;
        }

        private static string MissileVariantKey(MechPartSlot slot)
        {
            return MissileVariantPrefix + (int)slot;
        }
    }
}
