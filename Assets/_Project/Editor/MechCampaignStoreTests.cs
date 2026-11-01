using System;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public class MechCampaignStoreTests
{
    private string root;
    private string legacyRoot;
    private MechScenarioDefinition scenario;

    [SetUp]
    public void SetUp()
    {
        root = Path.Combine(Directory.GetCurrentDirectory(), "Temp", "MechCampaignStoreTests", Guid.NewGuid().ToString("N"));
        legacyRoot = Path.Combine(root, "legacy");
        MechCampaignStore.StorageRootOverride = Path.Combine(root, "campaign");
        MechScenarioStore.LegacyStorageRootOverride = legacyRoot;
        scenario = Resources.LoadAll<MechScenarioDefinition>("Scenarios").FirstOrDefault();
        Assert.IsNotNull(scenario);
    }

    [TearDown]
    public void TearDown()
    {
        MechCampaignStore.StorageRootOverride = null;
        MechScenarioStore.LegacyStorageRootOverride = null;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [Test]
    public void SixSlotsPersistAndContinueSelectsLatest()
    {
        for (var slot = 1; slot <= MechCampaignStore.SlotCount; slot++)
        {
            var data = MechCampaignStore.CreateNew(slot, scenario);
            MechCampaignStore.Current(data).name = "战役 " + slot;
            MechCampaignStore.Save(data);
            Assert.IsTrue(MechCampaignStore.TryLoad(slot, out var loaded, out var error), error);
            Assert.AreEqual("战役 " + slot, MechCampaignStore.Current(loaded).name);
        }
        Assert.AreEqual(MechCampaignStore.SlotCount, 6);
        Assert.AreEqual(6, MechCampaignStore.LoadLatest(out _).slot);
        Assert.Throws<ArgumentOutOfRangeException>(() => MechCampaignStore.CreateNew(7, scenario));
    }

    [Test]
    public void ScenarioCopiesAreIndependentAndDefaultCannotBeDeleted()
    {
        var data = MechCampaignStore.CreateNew(1, scenario);
        var original = MechCampaignStore.Current(data);
        var originalPosition = original.document.units[0].position;
        var copy = MechCampaignStore.AddScenario(data, scenario, "复制战场", original);
        copy.document.units[0].position += Vector3.one * 5000;
        Assert.AreEqual(originalPosition, original.document.units[0].position);
        copy.name = "重命名战场";
        MechCampaignStore.Save(data);

        Assert.IsTrue(MechCampaignStore.TryLoad(1, out var loaded, out var error), error);
        Assert.AreEqual("重命名战场", MechCampaignStore.Current(loaded).name);
        Assert.IsFalse(MechCampaignStore.DeleteScenario(loaded, original.id));
        Assert.IsTrue(MechCampaignStore.DeleteScenario(loaded, copy.id));
        Assert.AreEqual(original.id, MechCampaignStore.Current(loaded).id);
    }

    [Test]
    public void PlayerAndNpcLoadoutsStayInsideTheirOwnSlot()
    {
        var first = MechCampaignStore.CreateNew(1, scenario);
        var document = MechCampaignStore.Current(first).document;
        foreach (var edit in document.units)
        {
            var template = MechScenarioStore.Template(scenario, edit.templateKey);
            if (template.loadout != null) MechScenarioStore.StoreLoadout(edit, template.loadout);
        }
        MechCampaignStore.Save(first);
        MechCampaignStore.CreateNew(2, scenario);

        Assert.IsTrue(MechCampaignStore.TryLoad(1, out var loaded, out var error), error);
        Assert.IsTrue(MechCampaignStore.Current(loaded).document.units.All(unit => unit.customLoadout));
        Assert.IsTrue(MechCampaignStore.Current(loaded).document.units.All(unit => unit.equipment.Count > 0));
        Assert.IsTrue(MechCampaignStore.TryLoad(2, out loaded, out error), error);
        Assert.IsTrue(MechCampaignStore.Current(loaded).document.units.All(unit => !unit.customLoadout));
    }

    [Test]
    public void CorruptSaveIsReportedWithoutOverwritingSource()
    {
        MechCampaignStore.CreateNew(1, scenario);
        var path = Path.Combine(MechCampaignStore.StorageRootOverride, "slot-1.json");
        const string corrupt = "{not-valid-json";
        File.WriteAllText(path, corrupt);
        Assert.IsFalse(MechCampaignStore.TryLoad(1, out _, out var error));
        StringAssert.Contains("无法读取", error);
        Assert.AreEqual(corrupt, File.ReadAllText(path));
    }

    [Test]
    public void LegacyScenarioIsImportedWithoutModifyingLegacyFile()
    {
        var legacy = MechScenarioStore.Defaults(scenario);
        legacy.units[0].position += Vector3.one * 3210;
        MechScenarioStore.Save(scenario, legacy);
        var path = Path.Combine(legacyRoot, "ScenarioEdits", scenario.scenarioId + ".json");
        var before = File.ReadAllText(path);

        var working = MechCampaignStore.LoadWorking(scenario, out var warning);
        Assert.IsNull(warning);
        Assert.IsTrue(working.legacyPlayerLoadoutPending);
        Assert.AreEqual(legacy.units[0].position, MechCampaignStore.Current(working).document.units[0].position);
        Assert.AreEqual(before, File.ReadAllText(path));
    }
}
