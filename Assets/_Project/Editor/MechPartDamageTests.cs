using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;

public sealed class MechPartDamageTests
{
    private readonly List<UnityEngine.Object> owned=new List<UnityEngine.Object>();
    private T Own<T>(T value)where T:UnityEngine.Object{owned.Add(value);return value;}
    [TearDown] public void Cleanup(){for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);owned.Clear();}
    private MechPartDefinition Part(MechPartSlot role,bool limbs=false)
    {
        var model=Own(new GameObject("DamageSourceModel"));model.transform.position=Vector3.one*10000;
        for(int i=0;i<(limbs?2:1);i++)
        {var cube=GameObject.CreatePrimitive(PrimitiveType.Cube);cube.transform.SetParent(model.transform,false);cube.transform.localPosition=limbs?new Vector3(i==0?-2:2,0,0):Vector3.zero;cube.transform.localScale=Vector3.one*.6f;}
        var part=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        bool weapon=MechSlotRules.IsWeaponSlot(role);
        part.EditorConfigure("damage."+role,role.ToString(),role,weapon?MechPartCategory.Weapon:MechPartCategory.Frame,model,new MechPartStats{massKg=10},null,
            newWeaponPerformance:weapon?new MechWeaponPerformance{configured=true,role=MechWeaponRole.AutomaticCannon,magazineCapacity=10,magazineCount=3,roundsPerMinute=600,reloadSeconds=1,muzzleVelocityMetersPerSecond=400,projectileLifetimeSeconds=5,maximumRangeMeters=2000,directHitDamage=10}:null);
        part.DamageProfile.functionalTolerance=100;return part;
    }
    private CombatTestTarget Fixture()
    {
        var layout=Own(ScriptableObject.CreateInstance<MechPlatformDefinition>());layout.platformId="damage.test";
        var assignments=new List<MechPartAssignment>();
        foreach(var pair in new[]{("core",MechPartSlot.Core), ("arms",MechPartSlot.Arms), ("left",MechPartSlot.LeftHandWeapon),("right",MechPartSlot.RightHandWeapon)})
        {
            var part=Part(pair.Item2,pair.Item2==MechPartSlot.Arms);
            layout.slots.Add(new MechSlotDefinition{id=pair.Item1,legacyRole=pair.Item2,category=part.Category,hardpointType=MechHardpointType.None,
                localPosition=pair.Item1=="left"?new Vector3(-2,-1,0):pair.Item1=="right"?new Vector3(2,-1,0):Vector3.zero});
            assignments.Add(new MechPartAssignment{slotId=pair.Item1,slot=pair.Item2,part=part});
        }
        var loadout=Own(ScriptableObject.CreateInstance<MechLoadoutDefinition>());loadout.ConfigureRuntime("damage","Damage test",assignments);loadout.ConfigureLayout(layout);
        var owner=Own(new GameObject("DamageFixture"));var assembler=owner.AddComponent<MechAssembler>();assembler.Loadout=loadout;assembler.Assemble();
        var target=owner.AddComponent<CombatTestTarget>();target.ConfigureProtection(0,1000);target.RefreshGeometry(true);return target;
    }
    private MechDefenseExtensionProfile InstallArmor(CombatTestTarget target)
    {
        var assembler=target.GetComponent<MechAssembler>();var loadout=assembler.Loadout;
        var part=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        part.EditorConfigure("test.armor","Armor",MechPartSlot.Extension,MechPartCategory.Utility,null,new MechPartStats{massKg=10},null);
        part.DefenseExtension.kind=MechDefenseExtensionKind.ReactiveArmor;
        loadout.Platform.slots.Add(new MechSlotDefinition{id="extension",legacyRole=MechPartSlot.Extension,category=MechPartCategory.Utility,hardpointType=MechHardpointType.Utility});
        var options=new UnityEditor.SerializedObject(assembler);options.FindProperty("createPlaceholdersForMissingModels").boolValue=false;options.ApplyModifiedPropertiesWithoutUndo();
        Assert.That(loadout.TrySetPart("extension",part,out _),Is.True);assembler.Assemble();target.ConfigureProtection(0,1000);target.RefreshGeometry(true);return part.DefenseExtension;
    }
    private static Collider ColliderFor(CombatTestTarget target,string id)=>target.PartDamage.HitRegions.First(x=>x.RegionId==id).GetComponent<Collider>();
    private static bool Hit(CombatTestTarget target,string id,float damage,Guid token=default)
    {var collider=ColliderFor(target,id);return target.TryApplyDamage(damage,collider.bounds.center,Vector3.forward,TestWeaponKind.Rifle,out _,null,collider,token);}
    [Test] public void FailedArmorExtensionSupportRemovesWholeBodyProtectionWithoutKillingMachine()
    {
        var target=Fixture();InstallArmor(target);
        Assert.That(target.PartDamage.GetReactiveArmorCells("left"),Is.EqualTo(2));
        Hit(target,"core",100);
        Assert.That(target.IsAlive,Is.True);Assert.That(target.PartDamage.GetReactiveArmorCells("left"),Is.Zero);Assert.That(target.PartDamage.GetReactiveArmorCells("right"),Is.Zero);
    }
    [Test] public void ReactiveArmorConsumesOnlyStruckRegionOnceAndKeepsSingleAP()
    {
        var target=Fixture();var profile=InstallArmor(target).armor;profile.cells=2;profile.protectsGunfire=true;
        target.ConfigureProtection(0,1000);
        var collider=ColliderFor(target,"left");var token=Guid.NewGuid();
        Assert.That(target.TryApplyDamage(100,collider.bounds.center,Vector3.back,TestWeaponKind.Rifle,out _,null,collider,token),Is.True);
        Assert.That(target.Health,Is.EqualTo(960).Within(.01));
        Assert.That(target.PartDamage.GetReactiveArmorCells("left"),Is.EqualTo(1));
        Assert.That(target.PartDamage.GetReactiveArmorCells("right"),Is.EqualTo(2));
        Assert.That(target.TryApplyDamage(100,collider.bounds.center,Vector3.back,TestWeaponKind.Rifle,out _,null,collider,token),Is.False);
        Assert.That(target.Health,Is.EqualTo(960).Within(.01));Assert.That(target.PartDamage.GetReactiveArmorCells("left"),Is.EqualTo(1));
    }
    [Test] public void ReactiveArmorRejectsRearAndWrongThreatAndWorksWhenFullyAbsorbing()
    {
        var target=Fixture();var profile=InstallArmor(target).armor;profile.coverageHalfAngle=70;profile.cells=1;profile.damageReduction=1;target.ConfigureProtection(0,1000);
        var collider=ColliderFor(target,"left");
        target.TryApplyDamage(10,collider.bounds.center,Vector3.forward,TestWeaponKind.Missile,out _,null,collider,Guid.NewGuid());
        target.TryApplyDamage(10,collider.bounds.center,Vector3.back,TestWeaponKind.Rifle,out _,null,collider,Guid.NewGuid());
        Assert.That(target.PartDamage.GetReactiveArmorCells("left"),Is.EqualTo(1));
        Assert.That(target.TryApplyDamage(10,collider.bounds.center,Vector3.back,TestWeaponKind.Missile,out _,null,collider,Guid.NewGuid()),Is.True);
        Assert.That(target.Health,Is.EqualTo(980));Assert.That(target.PartDamage.GetReactiveArmorCells("left"),Is.Zero);
        target.TryApplyDamage(10,collider.bounds.center,Vector3.back,TestWeaponKind.Missile,out _,null,collider,Guid.NewGuid());
        Assert.That(target.Health,Is.EqualTo(970));
    }
    [TestCase(24f,MechPartCondition.NormalOrLight)] [TestCase(25f,MechPartCondition.FunctionImpaired)]
    [TestCase(65f,MechPartCondition.SevereButUsable)] [TestCase(100f,MechPartCondition.Destroyed)]
    public void FourStates_AreFunctionalGrades(float damage,MechPartCondition expected)
    {var target=Fixture();Assert.That(Hit(target,"left",damage),Is.True);Assert.That(target.PartDamage.GetCondition("left"),Is.EqualTo(expected));Assert.That(target.Health,Is.EqualTo(1000-damage));}
    [Test] public void LocalFailureDoesNotKillMachine_AndLaterHitsStillReduceTotalAP()
    {
        var t=Fixture();Hit(t,"left",100);Assert.That(t.IsAlive,Is.True);Assert.That(t.Health,Is.EqualTo(900));
        Assert.That(t.PartDamage.GetEquipmentEfficiency("left"),Is.Zero);Assert.That(t.PartDamage.GetEquipmentEfficiency("right"),Is.EqualTo(1));
        Hit(t,"left",50);Assert.That(t.Health,Is.EqualTo(850));Assert.That(t.IsAlive,Is.True);
    }
    [Test] public void LeftArmFailure_AffectsOnlyItsSupportedWeapon()
    {
        var t=Fixture();Hit(t,"arms/left",100);
        Assert.That(t.PartDamage.GetCondition("arms/left"),Is.EqualTo(MechPartCondition.Destroyed));
        Assert.That(t.PartDamage.GetCondition("arms/right"),Is.EqualTo(MechPartCondition.NormalOrLight));
        Assert.That(t.PartDamage.GetEquipmentEfficiency("left"),Is.Zero);Assert.That(t.PartDamage.GetEquipmentEfficiency("right"),Is.EqualTo(1));
    }
    [Test] public void SupportFailure_DoesNotPaintIntactWeaponAsDestroyed()
    {
        var t=Fixture();Hit(t,"arms/left",100);
        Assert.That(t.PartDamage.GetEquipmentEfficiency("left"),Is.Zero);
        Assert.That(t.PartDamage.GetCondition("left"),Is.EqualTo(MechPartCondition.NormalOrLight));
        Assert.That(t.PartDamage.IsSupportLimited("left"),Is.True);
        Assert.That(t.PartDamage.IsSupportLimited("right"),Is.False);
    }
    [Test] public void DefaultTolerance_PreservesDistributedDamageButAllowsConcentratedFailure()
    {
        var t=Fixture();
        foreach(var a in t.GetComponent<MechAssembler>().Loadout.Assignments)a.part.DamageProfile.functionalTolerance=0;
        t.ConfigureProtection(0,8000);t.RefreshGeometry(true);
        foreach(var id in new[]{"core","arms/left","arms/right","left","right"})Hit(t,id,600);
        Assert.That(t.Health,Is.EqualTo(5000));
        foreach(var id in new[]{"core","arms/left","arms/right","left","right"})
            Assert.That(t.PartDamage.GetCondition(id),Is.Not.EqualTo(MechPartCondition.SevereButUsable));
        Hit(t,"left",1800);
        Assert.That(t.PartDamage.GetCondition("left"),Is.EqualTo(MechPartCondition.Destroyed));
        Assert.That(t.IsAlive,Is.True);
    }
    [Test] public void FunctionalEfficiency_IsContinuousAcrossYellowAndRedThresholds()
    {
        var p=new MechPartDamageProfile();
        var part=Part(MechPartSlot.Core);
        Assert.That(part.DamageProfile.ResolveTolerance(part,8000),Is.EqualTo(100));
        Assert.That(p.ResolveTolerance(part,8000),Is.EqualTo(2400));
        foreach(float threshold in new[]{25f,65f})
            Assert.That(p.Efficiency(threshold-.001f,100),Is.EqualTo(p.Efficiency(threshold+.001f,100)).Within(.0001f));
        Assert.That(p.Efficiency(65,100),Is.EqualTo(.75f));
        Assert.That(p.Efficiency(99.9f,100),Is.GreaterThan(.35f));
        Assert.That(p.Efficiency(100,100),Is.Zero);
    }
    [Test] public void OneProjectileAcrossManyColliders_OnlyDeductsOnce()
    {
        var t=Fixture();var id=Guid.NewGuid();Hit(t,"left",20,id);Hit(t,"arms/left",20,id);Hit(t,"right",20,id);
        Assert.That(t.Health,Is.EqualTo(980));Assert.That(t.HitCount,Is.EqualTo(1));Assert.That(t.PartDamage.GetEquipmentEfficiency("right"),Is.EqualTo(1));
    }
    [Test] public void SeparateProjectiles_AccumulateNormally()
    {var t=Fixture();Hit(t,"left",20,Guid.NewGuid());Hit(t,"left",20,Guid.NewGuid());Assert.That(t.Health,Is.EqualTo(960));Assert.That(t.HitCount,Is.EqualTo(2));}
    [Test] public void UnifiedAP_OnlyReachesDeathWhenTotalBarIsEmpty()
    {
        var t=Fixture();t.ConfigureProtection(1000,1);Hit(t,"core",100);
        Assert.That(t.Structure,Is.Zero);Assert.That(t.Health,Is.EqualTo(900));Assert.That(t.IsAlive,Is.True);
        Hit(t,"core",900);Assert.That(t.Health,Is.Zero);Assert.That(t.IsAlive,Is.False);
    }
    [Test] public void LegacyTargetsWithoutParts_RetainExistingProtectionMode()
    {
        var go=Own(new GameObject("Legacy target"));var t=go.AddComponent<CombatTestTarget>();t.ConfigureProtection(1000,1);
        t.TryApplyDamage(100,Vector3.zero,Vector3.forward,TestWeaponKind.Rifle,out _);
        Assert.That(t.UsesUnifiedAP,Is.False);Assert.That(t.IsAlive,Is.False);
    }
    [Test] public void RepairRestoresFunction_WithoutReapplyingAnOldHit()
    {
        var t=Fixture();var id=Guid.NewGuid();Hit(t,"left",100,id);t.ConfigureProtection(0,1000);
        Assert.That(t.PartDamage.GetEquipmentEfficiency("left"),Is.EqualTo(1));Assert.That(Hit(t,"left",100,id),Is.False);Assert.That(t.Health,Is.EqualTo(1000));
    }
    [Test] public void RefreshDoesNotDuplicateRegions_AndOldProxiesCannotHitReplacement()
    {
        var t=Fixture();int count=t.PartDamage.HitRegions.Count;var old=ColliderFor(t,"left");
        t.RefreshGeometry();Assert.That(t.PartDamage.HitRegions.Count,Is.EqualTo(count));
        t.GetComponent<MechAssembler>().Assemble();float hp=t.Health;
        Assert.That(t.TryApplyDamage(50,Vector3.zero,Vector3.forward,TestWeaponKind.Rifle,out _,null,old,Guid.NewGuid()),Is.False);
        Assert.That(t.Health,Is.EqualTo(hp));
    }
    [Test] public void RegionProxiesFollowTheirPart_AndBroadHitboxStaysDisabled()
    {
        var t=Fixture();Physics.SyncTransforms();var c=ColliderFor(t,"left");var before=c.bounds.center;
        var record=t.GetComponent<MechRuntimeState>().AssembledParts.Single(x=>x.SlotId=="left");record.instance.transform.position+=Vector3.up*3;
        Physics.SyncTransforms();Assert.That(Vector3.Distance(c.bounds.center,before+Vector3.up*3),Is.LessThan(.01f));
        Assert.That(t.transform.Find("Combat Target Damage Hitbox").GetComponent<Collider>().enabled,Is.False);
    }
    [Test] public void PhysicalSweepIdentifiesTheEquippedPart()
    {
        var t=Fixture();Physics.SyncTransforms();var c=ColliderFor(t,"left");var hits=Physics.SphereCastAll(c.bounds.center-Vector3.forward*5,.02f,Vector3.forward,10,-1,QueryTriggerInteraction.Collide);
        var hit=hits.Where(h=>h.collider.GetComponentInParent<CombatTestTarget>()==t).OrderBy(h=>h.distance).First();
        Assert.That(hit.collider.GetComponent<MechDamageRegion>().RegionId,Is.EqualTo("left"));
    }
    [Test] public void FailedWeaponIsRejectedByProductionActionGate()
    {
        var t=Fixture();var weapons=t.gameObject.AddComponent<MechTestWeaponSystem>();Hit(t,"left",100);
        weapons.CommandFireSlot("left",MechActionSource.Player);weapons.Actions.Tick(Time.timeAsDouble);
        var events=new List<MechActionFeedback>();weapons.Actions.CopyEvents(events);
        Assert.That(events.Last().Phase,Is.EqualTo(MechActionPhase.Failed));Assert.That(events.Last().Reason,Is.EqualTo("ComponentFailed"));
        var states=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(states);Assert.That(states.Single(x=>x.SlotId=="left").Rounds,Is.EqualTo(10));
    }
    [Test] public void DiagramRetainsFailedEquipmentAndHasIndependentLimbRegions()
    {
        if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)Assert.Ignore("Requires graphics backend.");
        var t=Fixture();using(var diagram=MechDamageDiagramRenderer.Bake(t.GetComponent<MechRuntimeState>().CurrentLoadout,128))
        {
            Assert.That(diagram.RegionIds,Does.Contain("arms/left"));Assert.That(diagram.RegionIds,Does.Contain("arms/right"));
            diagram.Recolor(t.PartDamage.GetCondition);int initial=diagram.Texture.GetPixels32().Count(c=>c.a>0);
            Hit(t,"left",100);diagram.Recolor(t.PartDamage.GetCondition);var pixels=diagram.Texture.GetPixels32();
            Assert.That(pixels.Any(c=>c.r>180&&c.g<100&&c.a>0),Is.True);Assert.That(pixels.Count(c=>c.a>0),Is.LessThan(initial));
            Assert.That(t.GetComponent<MechRuntimeState>().AssembledParts.Single(x=>x.SlotId=="left").instance.activeSelf,Is.True);
        }
    }
    [UnityTest] public IEnumerator SameFrameReassemblyRetiresOldBonesAndKeepsLiveHitRegions()
    {
        yield return new EnterPlayMode();
        var target=Fixture();var assembler=target.GetComponent<MechAssembler>();
        assembler.Assemble();assembler.Assemble();
        var regions=new List<MechDamageRegion>(target.PartDamage.HitRegions);
        Assert.That(regions.Count,Is.GreaterThan(0));
        yield return null;
        foreach(var region in regions)
        {
            Assert.That(region!=null,Is.True,"Current damage proxy was attached to a retired assembly");
            Assert.That(region.gameObject.activeInHierarchy,Is.True);
            Assert.That(region.Owner,Is.SameAs(target.PartDamage));
        }
        target.ConfigureProtection(0,1000);
        Assert.That(Hit(target,"left",30),Is.True);
        Assert.That(target.PartDamage.GetCondition("left"),Is.EqualTo(MechPartCondition.FunctionImpaired));
        Cleanup();yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator BlastAgainstMultipleRegions_DamagesTheWholeAPOnlyOnce()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var t=Fixture();Physics.SyncTransforms();var projectile=Own(new GameObject("Blast fixture")).AddComponent<MechTestProjectile>();
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        typeof(MechTestProjectile).GetField("damage",flags).SetValue(projectile,300f);
        typeof(MechTestProjectile).GetField("warheadLethalRadius",flags).SetValue(projectile,20f);
        var detonate=typeof(MechTestProjectile).GetMethod("DetonateWarhead",flags);
        detonate.Invoke(projectile,new object[]{Vector3.back*2,0f});float after=t.Health;
        Assert.That(t.HitCount,Is.EqualTo(1));Assert.That(after,Is.InRange(700,999));
        var snapshots=new List<MechPartDamageSnapshot>();t.PartDamage.CopySnapshots(snapshots);Assert.That(snapshots.Count(x=>x.Condition!=MechPartCondition.NormalOrLight),Is.GreaterThanOrEqualTo(2));
        detonate.Invoke(projectile,new object[]{Vector3.back*2,0f});Assert.That(t.Health,Is.EqualTo(after));Assert.That(t.HitCount,Is.EqualTo(1));
        Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void OccludedBlast_DoesNotDamageAPOrParts()
    {
        var t=Fixture();var wall=Own(GameObject.CreatePrimitive(PrimitiveType.Cube));wall.transform.position=new Vector3(0,0,-1);wall.transform.localScale=new Vector3(20,20,.1f);Physics.SyncTransforms();
        Assert.That(t.TryApplyDamage(300,Vector3.zero,Vector3.forward,TestWeaponKind.Missile,out _,null,null,Guid.NewGuid(),Vector3.back*3,20),Is.False);
        Assert.That(t.Health,Is.EqualTo(1000));Assert.That(t.HitCount,Is.Zero);
    }
    [UnityTest] public IEnumerator ImpairedWeaponActuallyFiresMoreSlowly()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var t=Fixture();var weapons=t.gameObject.AddComponent<MechTestWeaponSystem>();Hit(t,"left",40);
        float now=Time.time;Assert.That(weapons.CommandFireSlot("left",MechActionSource.Player),Is.True);weapons.Actions.Tick(Time.timeAsDouble);
        var states=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(states);
        var left=states.Single(x=>x.SlotId=="left");Assert.That(left.Rounds,Is.EqualTo(9));Assert.That(left.NextFireTime-now,Is.GreaterThan(.1f));
        foreach(var projectile in UnityEngine.Object.FindObjectsByType<MechTestProjectile>())UnityEngine.Object.DestroyImmediate(projectile.gameObject);
        Cleanup();yield return new ExitPlayMode();
    }
    [Test] public void InternalGeneratorUsesCoreSupportAndActuallyRechargesMoreSlowly()
    {
        var target=Fixture();var assembler=target.GetComponent<MechAssembler>();var loadout=assembler.Loadout;
        var generator=Own(ScriptableObject.CreateInstance<MechPartDefinition>());
        generator.EditorConfigure("internal-generator","Generator",MechPartSlot.Generator,MechPartCategory.Internal,null,new MechPartStats{massKg=1},null);
        loadout.Platform.slots.Add(new MechSlotDefinition{id="generator",legacyRole=MechPartSlot.Generator,category=MechPartCategory.Internal,hardpointType=MechHardpointType.None});
        Assert.That(loadout.TrySetPart("generator",generator,out _),Is.True);
        // Internal equipment has no physical placeholder in the production skinned assembly.
        var options=new UnityEditor.SerializedObject(assembler);options.FindProperty("createPlaceholdersForMissingModels").boolValue=false;options.ApplyModifiedPropertiesWithoutUndo();
        assembler.Assemble();target.ConfigureProtection(0,1000);target.RefreshGeometry(true);
        var motion=target.gameObject.AddComponent<MechFlightMotionController>();var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var energy=typeof(MechFlightMotionController).GetField("currentManeuverEnergy",flags);var recovery=typeof(MechFlightMotionController).GetMethod("UpdateManeuverEnergyRecovery",flags);
        energy.SetValue(motion,0f);recovery.Invoke(motion,new object[]{1f});float normal=(float)energy.GetValue(motion);
        Hit(target,"core",40);Assert.That(target.PartDamage.RoleEfficiency(MechPartSlot.Generator),Is.LessThan(1));
        energy.SetValue(motion,0f);recovery.Invoke(motion,new object[]{1f});Assert.That((float)energy.GetValue(motion),Is.LessThan(normal));
    }
}
