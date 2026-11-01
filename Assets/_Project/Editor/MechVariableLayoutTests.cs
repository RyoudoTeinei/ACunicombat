using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEditor;

public sealed class MechVariableLayoutTests
{
    private readonly List<UnityEngine.Object> objects = new List<UnityEngine.Object>();
    private T Own<T>(T obj) where T : UnityEngine.Object { objects.Add(obj); return obj; }
    [TearDown] public void Cleanup() { foreach (var o in objects) if (o != null) UnityEngine.Object.DestroyImmediate(o); objects.Clear(); }
    private MechPartDefinition Part(string id = "gun", MechPartSlot role = MechPartSlot.LeftHandWeapon)
    {
        var p = Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        p.EditorConfigure(id, id, role, MechPartCategory.Weapon, null, new MechPartStats { massKg = 10 }, null);
        return p;
    }
    private MechLoadoutDefinition Layout(int count, MechPartDefinition part)
    {
        var layout = Own(ScriptableObject.CreateInstance<MechPlatformDefinition>()); layout.platformId = "test";
        var assignments = new List<MechPartAssignment>();
        for (int i = 0; i < count; i++)
        {
            layout.slots.Add(new MechSlotDefinition { id = "weapon." + i, localPosition = new Vector3(i, 0, 0) });
            assignments.Add(new MechPartAssignment { slotId = "weapon." + i, part = part });
        }
        var loadout = Own(ScriptableObject.CreateInstance<MechLoadoutDefinition>());
        loadout.ConfigureRuntime("test", "Test", assignments); loadout.ConfigureLayout(layout); return loadout;
    }
    private MechAssembler Assemble(MechLoadoutDefinition loadout)
    {
        var go = Own(new GameObject("LayoutTest")); var assembler = go.AddComponent<MechAssembler>();
        var serialized = new SerializedObject(assembler); serialized.FindProperty("logValidationIssues").boolValue = false; serialized.ApplyModifiedPropertiesWithoutUndo();
        assembler.Loadout = loadout; assembler.Assemble(); return assembler;
    }
    [TestCase(4)] [TestCase(6)] [TestCase(8)] [TestCase(7)]
    public void ArbitrarySlotCount_UsesRealAssemblerAndCountsEachInstanceOnce(int count)
    {
        var loadout = Layout(count, Part()); var assembler = Assemble(loadout);
        Assert.That(assembler.RuntimeState.LoadoutValid, Is.True);
        Assert.That(assembler.RuntimeState.AssembledParts.Count, Is.EqualTo(count));
        Assert.That(assembler.RuntimeState.AggregatedStats.totalMassKg, Is.EqualTo(count * 10));
        Assert.That(assembler.RuntimeState.AssembledParts.Select(x => x.InstanceId).Distinct().Count(), Is.EqualTo(count));
        foreach (var record in assembler.RuntimeState.AssembledParts)
        {
            var slot = loadout.GetSlots().Single(x => x.id == record.SlotId);
            Assert.That(record.parentMount.localPosition, Is.EqualTo(slot.localPosition));
            Assert.That(record.instance.GetComponent<MechAssembledPart>().SlotId, Is.EqualTo(slot.id));
        }
    }
    [Test] public void SaveReorderAndAppend_KeepsIdentityAndLeavesNewSlotEmpty()
    {
        var a = Part("a"); var b = Part("b"); var source = Layout(6, a);
        Assert.That(source.TrySetPart("weapon.2", b, out _), Is.True);
        Assert.That(source.TrySetPart("weapon.4", null, out _), Is.True);
        var json = MechLayoutSelection.Export(source);
        source.Platform.slots.Reverse(); source.Platform.slots.Add(new MechSlotDefinition { id = "new" });
        Assert.That(MechLayoutSelection.TryImport(json, source, id => id == "a" ? a : b, out var restored, out var error), Is.True, error); Own(restored);
        Assert.That(restored.TryGetPart("weapon.2", out var part), Is.True); Assert.That(part, Is.SameAs(b));
        Assert.That(restored.TryGetPart("weapon.4", out _), Is.False);
        Assert.That(restored.TryGetPart("new", out _), Is.False);
    }
    [Test] public void CompatibleHandWeapon_CanSwapSidesWithoutChangingAsset()
    {
        var p = Part(); var loadout = Layout(2, p);
        loadout.Platform.slots[1].legacyRole = MechPartSlot.RightHandWeapon;
        Assert.That(MechLoadoutValidator.Validate(loadout).IsValid, Is.True);
        Assert.That(p.Slot, Is.EqualTo(MechPartSlot.LeftHandWeapon));
    }
    [Test] public void LimitsAndTags_RejectIncompatibleEquipment()
    {
        var p = Part(); var l = Layout(1, p); var s = l.Platform.slots[0];
        s.maxMassKg = 5; Assert.That(s.Accepts(p), Is.False); s.maxMassKg = 20;
        s.requiredTags = new[] { "missing" }; Assert.That(s.Accepts(p), Is.False); s.requiredTags = Array.Empty<string>();
        s.hardpointType = MechHardpointType.BackWeapon; Assert.That(s.Accepts(p), Is.False);
        Assert.That(MechLoadoutValidator.Validate(l).IsValid, Is.False);
    }
    [Test] public void DuplicateUnknownAndCyclicSlots_AreRejected()
    {
        var l = Layout(2, Part()); l.Platform.slots[1].id = l.Platform.slots[0].id;
        Assert.That(MechLoadoutValidator.Validate(l).IsValid, Is.False);
        l.Platform.slots[1].id = "weapon.1";
        l.Platform.slots[0].parentSlotId = "weapon.1"; l.Platform.slots[1].parentSlotId = "weapon.0";
        Assert.That(MechLoadoutValidator.Validate(l).Issues.Any(x => x.code == "LAYOUT_CYCLE"), Is.True);
        l.Platform.slots.RemoveAt(1);
        Assert.That(MechLoadoutValidator.Validate(l).Issues.Any(x => x.code == "UNKNOWN_SLOT"), Is.True);
    }
    [Test] public void ChildSlots_StablePathAndDependencyGuardAndParentAvailability()
    {
        var rack = Part("rack"); var child = Part("child");
        var serialized = new SerializedObject(rack); var slots = serialized.FindProperty("childSlots"); slots.arraySize = 1;
        var element = slots.GetArrayElementAtIndex(0); element.FindPropertyRelative("id").stringValue = "tip";
        element.FindPropertyRelative("category").intValue = (int)MechPartCategory.Weapon;
        element.FindPropertyRelative("hardpointType").intValue = (int)MechHardpointType.HandWeapon; serialized.ApplyModifiedPropertiesWithoutUndo();
        var l = Layout(1, rack); Assert.That(l.TrySetPart("weapon.0/tip", child, out var error), Is.True, error);
        Assert.That(l.TrySetPart("weapon.0", null, out error), Is.False); StringAssert.Contains("weapon.0/tip", error);
        var assembler = Assemble(l); Assert.That(assembler.RuntimeState.AssembledParts.Count, Is.EqualTo(2));
        var capabilities = new List<MechEquipmentCapability>();
        MechEquipmentCapability.CopyFrom(assembler.RuntimeState, capabilities); Assert.That(capabilities.All(x => x.Available), Is.True);
        assembler.RuntimeState.AssembledParts[0].instance.SetActive(false);
        MechEquipmentCapability.CopyFrom(assembler.RuntimeState, capabilities); Assert.That(capabilities.All(x => !x.Available), Is.True);
        Assert.That(l.TrySetPart("weapon.0/tip", null, out _), Is.True);
        Assert.That(l.TrySetPart("weapon.0", null, out _), Is.True);
    }
    [Test] public void MissingMount_IsAnErrorAndDoesNotFallbackToAnotherSlot()
    {
        var l = Layout(1, Part()); l.Platform.slots[0].mountPath = "missing"; var assembler = Assemble(l);
        Assert.That(assembler.RuntimeState.LoadoutValid, Is.False);
        Assert.That(assembler.RuntimeState.AssembledParts, Is.Empty);
        Assert.That(assembler.RuntimeState.ValidationIssues.Any(x => x.code == "MOUNT_NOT_FOUND"), Is.True);
    }
    [Test] public void SaveRejectsUnknownPartAndWrongPlatform()
    {
        var l = Layout(1, Part()); var json = MechLayoutSelection.Export(l);
        Assert.That(MechLayoutSelection.TryImport(json, l, _ => null, out _, out _), Is.False);
        l.Platform.platformId = "other";
        Assert.That(MechLayoutSelection.TryImport(json, l, _ => Part(), out _, out _), Is.False);
    }
    [Test] public void MigrationCopiesAssignmentsWithoutMutatingLegacySource()
    {
        var l = Own(ScriptableObject.CreateInstance<MechLoadoutDefinition>()); var p = Part();
        l.ConfigureRuntime("old", "Old", new[] { new MechPartAssignment { slot = MechPartSlot.LeftHandWeapon, part = p } });
        var before = JsonUtility.ToJson(l);
        var copy = Own(MechLayoutMigration.ConvertCopy(l, out var platform)); Own(platform);
        Assert.That(copy.TryGetPart("legacy.100", out var restored), Is.True); Assert.That(restored, Is.SameAs(p));
        Assert.That(JsonUtility.ToJson(l), Is.EqualTo(before)); Assert.That(copy.SchemaVersion, Is.EqualTo(1));
    }
    [TestCase(4)] [TestCase(6)] [TestCase(8)]
    public void ShippedSampleAssets_ImportAndAssembleWithExpectedWeapons(int count)
    {
        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>("Assets/_Project/Data/Mechs/Layouts/Sample_Loadout_" + count + ".asset");
        Assert.That(loadout, Is.Not.Null); Assert.That(loadout.Platform, Is.Not.Null);
        var validation = MechLoadoutValidator.Validate(loadout);
        Assert.That(validation.IsValid, Is.True, string.Join("; ", validation.Issues.Select(x => x.message)));
        var assembler = Assemble(loadout);
        Assert.That(assembler.RuntimeState.LoadoutValid, Is.True);
        Assert.That(assembler.RuntimeState.AssembledParts.Count(x => x.part.Category == MechPartCategory.Weapon), Is.EqualTo(count));
    }
    [Test] public void AdditionalRadar_DoesNotIncreasePrimaryRangeOrChannels()
    {
        var radar = Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        radar.EditorConfigure("radar", "Radar", MechPartSlot.Radar, MechPartCategory.Internal, null,
            new MechPartStats { massKg = 10, radarRangeKm = 12, trackCapacity = 8, guidanceChannels = 2 }, null);
        var l = Layout(2, radar);
        foreach (var slot in l.Platform.slots) { slot.category = MechPartCategory.Internal; slot.hardpointType = MechHardpointType.Sensor; }
        l.Platform.slots[0].legacyRole = MechPartSlot.Radar;
        var assembler = Assemble(l); var stats = assembler.RuntimeState.AggregatedStats;
        Assert.That(stats.totalMassKg, Is.EqualTo(20)); Assert.That(stats.radarRangeKm, Is.EqualTo(12));
        Assert.That(stats.guidanceChannels, Is.EqualTo(2));
        var capabilities = new List<MechEquipmentCapability>(); MechEquipmentCapability.CopyFrom(assembler.RuntimeState, capabilities);
        Assert.That(capabilities.Count, Is.EqualTo(2));
        foreach(var capability in capabilities)
            Assert.That(capability.RadarRangeKm, Is.EqualTo(radar.RadarPerformance.EstimatedSearchRangeKm).Within(.0001f), capability.SlotId);
    }
    [Test] public void AddingUtilitySlot_UsesSameAssemblyAndSelectionApi()
    {
        var l = Layout(4, Part()); var utility = Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        // Extension modules require core capacity under the current kit rules.
        // Supply that prerequisite rather than testing an invalid bare weapon rack.
        var core=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        core.EditorConfigure("core","Core",MechPartSlot.Core,MechPartCategory.Frame,null,new MechPartStats{massKg=0},null);
        l.Platform.slots.Add(new MechSlotDefinition{id="core",legacyRole=MechPartSlot.Core,category=MechPartCategory.Frame,hardpointType=core.MountClass});
        Assert.IsTrue(l.TrySetPart("core",core,out var coreError),coreError);
        utility.EditorConfigure("defense", "Defense", MechPartSlot.Extension, MechPartCategory.Utility, null, new MechPartStats { massKg = 30 }, null);
        l.Platform.slots.Add(new MechSlotDefinition { id = "defense", category = MechPartCategory.Utility, hardpointType = MechHardpointType.None });
        Assert.That(l.TrySetPart("defense", utility, out var error), Is.True, error);
        var assembler = Assemble(l); Assert.That(assembler.RuntimeState.AssembledParts.Count, Is.EqualTo(6));
        Assert.That(assembler.RuntimeState.AggregatedStats.totalMassKg, Is.EqualTo(70));
    }
    [Test] public void SelfExpandingRack_IsBoundedAndRejected()
    {
        var rack = Part(); var serialized = new SerializedObject(rack); var slots = serialized.FindProperty("childSlots"); slots.arraySize = 1;
        var e = slots.GetArrayElementAtIndex(0); e.FindPropertyRelative("id").stringValue = "child";
        e.FindPropertyRelative("category").intValue = 20; e.FindPropertyRelative("hardpointType").intValue = 30; serialized.ApplyModifiedPropertiesWithoutUndo();
        var l = Layout(1, rack); var id = "weapon.0";
        for (int i = 1; i < MechSlotGraph.MaxDepth; i++) { id += "/child"; Assert.That(l.TrySetPart(id, rack, out _), Is.True); }
        Assert.That(MechLoadoutValidator.Validate(l).Issues.Any(x => x.code == "LAYOUT_DEPTH"), Is.True);
        Assert.That(l.GetSlots().Count, Is.EqualTo(MechSlotGraph.MaxDepth));
    }
    [Test] public void ExplicitParentPayload_IncludesMountedChildren()
    {
        var l = Layout(2, Part()); l.Platform.slots[0].maxMassKg = 15;
        l.Platform.slots[1].parentSlotId = "weapon.0";
        Assert.That(MechLoadoutValidator.Validate(l).Issues.Any(x => x.code == "MOUNT_PAYLOAD_EXCEEDED"), Is.True);
    }
    [Test] public void SavedSelection_ResolvesDefaultPartsOutsideResources()
    {
        var p = Part(); var l = Layout(2, p); l.Platform.platformId = "test." + Guid.NewGuid().ToString("N");
        var catalog = Own(ScriptableObject.CreateInstance<MechPartCatalog>());
        try
        {
            l.TrySetPart("weapon.1", null, out _); MechLayoutSelection.Save(l);
            var loaded = Own(MechLayoutSelection.Resolve(l, catalog)); Assert.That(loaded, Is.Not.SameAs(l));
            Assert.That(loaded.TryGetPart("weapon.0", out var restored), Is.True); Assert.That(restored, Is.SameAs(p));
            Assert.That(loaded.TryGetPart("weapon.1", out _), Is.False);
        }
        finally { PlayerPrefs.DeleteKey("acunicombat.hangar.layout." + l.Platform.platformId); PlayerPrefs.Save(); }
    }
    [Test] public void ReorderedLegacyRoles_KeepFrameBeforeWeaponMountCreation()
    {
        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>("Assets/_Project/Data/Mechs/Layouts/Sample_Loadout_4.asset");
        var copy = Own(UnityEngine.Object.Instantiate(loadout)); var layout = Own(UnityEngine.Object.Instantiate(loadout.Platform));
        layout.slots.Reverse(); copy.ConfigureLayout(layout);
        var sorted = copy.CreateSortedAssignmentList();
        Assert.That(sorted.FindIndex(x => x.slot == MechPartSlot.Core), Is.LessThan(sorted.FindIndex(x => x.slot == MechPartSlot.LeftHandWeapon)));
        var assembler = Assemble(copy); Assert.That(assembler.RuntimeState.LoadoutValid, Is.True);
        Assert.That(assembler.RuntimeState.AssembledParts.Single(x => x.slot == MechPartSlot.LeftHandWeapon).parentMount.name, Does.Not.Contain("Fallback"));
    }
}
