using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public static class MechCampaignStore
    {
        public const int SlotCount = 6;
        private const int CurrentVersion = 1;

        [Serializable]
        public sealed class ScenarioEntry
        {
            public string id;
            public string name;
            public bool isDefault;
            public MechScenarioStore.Document document;
        }

        [Serializable]
        public sealed class Slot
        {
            public int version = CurrentVersion;
            public int slot;
            public string createdUtc;
            public string updatedUtc;
            public string selectedEntryId;
            public bool legacyPlayerLoadoutPending;
            public List<ScenarioEntry> scenarios = new List<ScenarioEntry>();
        }

        // SessionState keeps the editor-test isolation intact across play-mode domain reloads.
#if UNITY_EDITOR
        public static string StorageRootOverride
        {
            get
            {
                var value = UnityEditor.SessionState.GetString("ACunicombat.CampaignStore.TestRoot", "");
                return string.IsNullOrEmpty(value) ? null : value;
            }
            set => UnityEditor.SessionState.SetString("ACunicombat.CampaignStore.TestRoot", value ?? "");
        }
#else
        public static string StorageRootOverride { get; set; }
#endif
        private static string Root => StorageRootOverride ?? Path.Combine(Application.persistentDataPath, "CampaignSaves");
        private static string PathFor(int slot) => Path.Combine(Root, "slot-" + slot.ToString(CultureInfo.InvariantCulture) + ".json");
        private static string Now() => DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        public static bool Exists(int slot)
        {
            return slot >= 1 && slot <= SlotCount && File.Exists(PathFor(slot));
        }

        public static bool HasAnySave
        {
            get
            {
                for (var slot = 1; slot <= SlotCount; slot++)
                    if (Exists(slot)) return true;
                return false;
            }
        }

        public static bool TryLoad(int slot, out Slot result, out string error)
        {
            result = null;
            error = null;
            if (slot < 1 || slot > SlotCount)
            {
                error = "存档位编号无效";
                return false;
            }
            var path = PathFor(slot);
            if (!File.Exists(path)) return false;
            try
            {
                result = JsonUtility.FromJson<Slot>(File.ReadAllText(path));
                Validate(result, slot);
                return true;
            }
            catch (Exception exception)
            {
                result = null;
                error = "存档无法读取：" + exception.Message;
                return false;
            }
        }

        public static Slot LoadLatest(out string error)
        {
            error = null;
            Slot latest = null;
            DateTime latestTime = DateTime.MinValue;
            foreach (var slot in Enumerable.Range(1, SlotCount))
            {
                if (!TryLoad(slot, out var candidate, out var loadError))
                {
                    if (!string.IsNullOrEmpty(loadError) && string.IsNullOrEmpty(error)) error = loadError;
                    continue;
                }
                var time = ParseTime(candidate.updatedUtc);
                if (latest != null && time <= latestTime) continue;
                latest = candidate;
                latestTime = time;
            }
            return latest;
        }

        public static Slot LoadWorking(MechScenarioDefinition scenario, out string warning)
        {
            warning = null;
            var latest = LoadLatest(out warning);
            if (latest != null) return latest;
            if (HasAnySave) return CreateInMemory(1, scenario);

            var slot = CreateInMemory(1, scenario);
            if (!MechScenarioStore.HasSavedDocument(scenario)) return slot;
            var document = MechScenarioStore.Load(scenario, out warning);
            if (!string.IsNullOrEmpty(warning)) return slot;
            slot.scenarios[0].document = document;
            slot.legacyPlayerLoadoutPending = true;
            Save(slot);
            return slot;
        }

        public static Slot CreateNew(int slotNumber, MechScenarioDefinition scenario)
        {
            var slot = CreateInMemory(slotNumber, scenario);
            Save(slot);
            return slot;
        }

        private static Slot CreateInMemory(int slotNumber, MechScenarioDefinition scenario)
        {
            if (slotNumber < 1 || slotNumber > SlotCount) throw new ArgumentOutOfRangeException(nameof(slotNumber));
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            var time = Now();
            var entry = new ScenarioEntry
            {
                id = Guid.NewGuid().ToString("N"),
                name = scenario.displayName,
                isDefault = true,
                document = MechScenarioStore.Defaults(scenario)
            };
            return new Slot
            {
                slot = slotNumber,
                createdUtc = time,
                updatedUtc = time,
                selectedEntryId = entry.id,
                scenarios = new List<ScenarioEntry> { entry }
            };
        }

        public static ScenarioEntry Current(Slot slot)
        {
            if (slot?.scenarios == null || slot.scenarios.Count == 0) return null;
            return slot.scenarios.FirstOrDefault(entry => entry.id == slot.selectedEntryId) ?? slot.scenarios[0];
        }

        public static ScenarioEntry AddScenario(Slot slot, MechScenarioDefinition scenario, string name, ScenarioEntry source = null)
        {
            if (slot == null) throw new ArgumentNullException(nameof(slot));
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            var entry = new ScenarioEntry
            {
                id = Guid.NewGuid().ToString("N"),
                name = string.IsNullOrWhiteSpace(name) ? "未命名场景" : name.Trim(),
                isDefault = false,
                document = source?.document != null ? Clone(source.document) : MechScenarioStore.Defaults(scenario)
            };
            entry.document.scenarioId = scenario.scenarioId;
            slot.scenarios.Add(entry);
            slot.selectedEntryId = entry.id;
            return entry;
        }

        public static bool DeleteScenario(Slot slot, string entryId)
        {
            var entry = slot?.scenarios?.FirstOrDefault(item => item.id == entryId);
            if (entry == null || entry.isDefault) return false;
            slot.scenarios.Remove(entry);
            if (slot.selectedEntryId == entryId)
                slot.selectedEntryId = slot.scenarios.FirstOrDefault(item => item.isDefault)?.id ?? slot.scenarios[0].id;
            return true;
        }

        public static T Clone<T>(T value)
        {
            return JsonUtility.FromJson<T>(JsonUtility.ToJson(value));
        }

        public static void Save(Slot slot)
        {
            Validate(slot, slot?.slot ?? 0);
            if (string.IsNullOrEmpty(slot.createdUtc)) slot.createdUtc = Now();
            slot.updatedUtc = Now();
            Directory.CreateDirectory(Root);
            var path = PathFor(slot.slot);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(slot, true));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        public static string DisplayTime(string value)
        {
            var time = ParseTime(value);
            return time == DateTime.MinValue ? "时间未知" : time.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static DateTime ParseTime(string value)
        {
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                ? time : DateTime.MinValue;
        }

        private static void Validate(Slot slot, int expectedSlot)
        {
            if (slot == null || slot.slot != expectedSlot || expectedSlot < 1 || expectedSlot > SlotCount)
                throw new InvalidDataException("存档位数据无效");
            if (slot.version != CurrentVersion) throw new InvalidDataException("存档版本不兼容");
            if (slot.scenarios == null || slot.scenarios.Count == 0) throw new InvalidDataException("存档没有场景");
            var ids = new HashSet<string>();
            foreach (var entry in slot.scenarios)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.id) || !ids.Add(entry.id))
                    throw new InvalidDataException("场景标识为空或重复");
                if (entry.document?.units == null || entry.document.units.Count == 0)
                    throw new InvalidDataException("场景没有有效单位");
            }
            if (Current(slot) == null) throw new InvalidDataException("当前场景无效");
        }
    }
}
