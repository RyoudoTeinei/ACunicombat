using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public class MechBeltFeedTests
{
    const BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    public static void Apply()
    {
        foreach(var guid in AssetDatabase.FindAssets("t:MechPartDefinition"))
        {
            var p=AssetDatabase.LoadAssetAtPath<MechPartDefinition>(AssetDatabase.GUIDToAssetPath(guid));
            if(!new[]{"5110","1910","1820","1720","1730","2020"}.Any(n=>p.PartId.EndsWith("_"+n)))continue;
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(AC6TestWeaponProfileFactory.Create(p.PartId)),p.WeaponPerformance);EditorUtility.SetDirty(p);
        }
        AssetDatabase.SaveAssets();
    }
    static MechPartDefinition Part(string n,string side)=>Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_"+n)??Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_"+side+"_"+n);
    [TestCase("5110",1200,1,1858,true)] [TestCase("1910",1440,1,2400,true)]
    [TestCase("1820",82,6,1420,false)] [TestCase("1720",46,6,580,false)] [TestCase("1730",42,6,580,false)] [TestCase("2020",36,6,780,false)]
    public void SavedSupplyMatchesRequest(string n,int cap,int groups,int rpm,bool belt)
    {
        foreach(string side in new[]{"L","R"})
        {
            var p=Part(n,side).WeaponPerformance;Assert.AreEqual(cap,p.magazineCapacity);Assert.AreEqual(groups,p.magazineCount);Assert.AreEqual(rpm,p.roundsPerMinute);Assert.AreEqual(belt,p.beltFed);
            if(belt){Assert.AreEqual(0,p.reloadSeconds);Assert.Greater(p.heatPerShotMj*rpm/60,p.coolingMw);}
        }
    }
    [UnityTest] public IEnumerator BeltHeatBlocksShotAndCoolingRestoresWithoutRefilling()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float scale=Time.timeScale,step=Time.captureDeltaTime;Time.timeScale=1;Time.captureDeltaTime=.02f;
        var fixture=new MechEquipmentExecutionTests();
        try
        {
            foreach(string n in new[]{"1910","5110"})foreach(string side in new[]{"L","R"})
            {
                var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",Private).Invoke(fixture,new object[]{1,Part(n,side)});var w=a.gameObject.AddComponent<MechTestWeaponSystem>();yield return null;
                var list=new List<MechEquipmentWeaponSnapshot>();w.CopyEquipmentWeaponSnapshots(list);
                var instance=typeof(MechTestWeaponSystem).GetMethod("GetInstanceState",Private).Invoke(w,new object[]{new MechEquipmentSlot("gun.0")});
                var state=instance.GetType().GetField(n=="1910"?"Hand":"Defense").GetValue(instance);var type=state.GetType();var p=Part(n,side).WeaponPerformance;
                type.GetField("RoundsRemaining").SetValue(state,17);type.GetField("HeatMj").SetValue(state,p.heatCapacityMj);type.GetField("Overheated").SetValue(state,true);
                if(n=="1910")type.GetField("LastHeatTime").SetValue(state,Time.time);
                w.CommandReloadSlot("gun.0",MechActionSource.Player);w.Actions.Tick(Time.timeAsDouble);w.CopyEquipmentWeaponSnapshots(list);Assert.IsFalse(list[0].Reloading);Assert.AreEqual(17,list[0].Rounds);
                w.CommandFireSlot("gun.0",MechActionSource.Player);w.Actions.Tick(Time.timeAsDouble);Assert.IsFalse(MechTestProjectile.Registry.Any(x=>x!=null&&x.IsOwnedBy(a.transform)));
                // Start just above the actual resume threshold; do not alter configured cooling.
                type.GetField("HeatMj").SetValue(state,p.resumeThresholdMj+.05f);
                double coolUntil=Time.timeAsDouble+.4;float deadline=Time.realtimeSinceStartup+10;
                while(Time.timeAsDouble<coolUntil&&Time.realtimeSinceStartup<deadline)yield return null;
                Assert.GreaterOrEqual(Time.timeAsDouble,coolUntil,"Cooling test must advance simulation time");
                Assert.IsFalse((bool)type.GetField("Overheated").GetValue(state),n+side+" heat="+type.GetField("HeatMj").GetValue(state)+" cooling="+p.coolingMw+" enabled="+w.enabled);w.CopyEquipmentWeaponSnapshots(list);Assert.AreEqual(17,list[0].Rounds);Assert.IsFalse(list[0].Reloading);
                if(n=="1910")
                {
                    type.GetField("HeatMj").SetValue(state,p.heatCapacityMj-.001f);type.GetField("LastHeatTime").SetValue(state,Time.time);
                    type.GetField("SpinReadyTime").SetValue(state,Time.time-1);type.GetField("LastSpinRequestTime").SetValue(state,Time.time);
                    w.CommandFireSlot("gun.0",MechActionSource.Player);w.Actions.Tick(Time.timeAsDouble);w.CopyEquipmentWeaponSnapshots(list);
                    Assert.AreEqual(16,list[0].Rounds);Assert.IsTrue((bool)type.GetField("Overheated").GetValue(state));Assert.IsTrue(MechTestProjectile.Registry.Any(x=>x!=null&&x.IsOwnedBy(a.transform)));
                }
                foreach(var b in MechTestProjectile.Registry.ToArray())if(b!=null)UnityEngine.Object.DestroyImmediate(b.gameObject);fixture.Cleanup();yield return null;
            }
        }
        finally{fixture.Cleanup();Time.timeScale=scale;Time.captureDeltaTime=step;}
        yield return new ExitPlayMode();
    }
}
