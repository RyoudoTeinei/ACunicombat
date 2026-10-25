using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
public class MechUncappedThrustTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [TestCase(30)] [TestCase(60)] [TestCase(120)]
    public void NormalThrustCrossesTwoKmPerSecondWithoutClamp(int hz)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
        var go=new GameObject("Uncapped thrust test");
        try
        {
            go.transform.position=Vector3.up*10000;
            var m=go.AddComponent<MechFlightMotionController>();m.ConfigureModelForwardYawOffset(0);m.ConfigureGravity(0);m.ConfigurePropulsionForceModel(false,false,0);
            Assert.AreEqual(0,m.TechnicalSafetySpeed);
            var channels=(List<MechThrusterChannelState>)typeof(MechFlightMotionController).GetField("thrusterChannels",Private).GetValue(m);channels.Clear();
            var anchor=new GameObject("Actual main nozzle").transform;anchor.SetParent(go.transform,false);anchor.localRotation=Quaternion.LookRotation(Vector3.back);
            channels.Add(new MechThrusterChannelState("main.test",MechThrusterRole.Main,MechThrusterSide.Center,anchor,Vector3.zero,false));
            typeof(MechFlightMotionController).GetField("velocity",Private).SetValue(m,Vector3.forward*1990);
            var input=new MechPlayerInputFrame{MoveLocal=Vector2.up};
            for(int i=0;i<hz*3;i++)m.Tick(in input,1f/hz);
            Assert.Greater(m.Velocity.magnitude,2000);Assert.IsTrue(float.IsFinite(m.Velocity.magnitude));
            TestContext.WriteLine(hz+"Hz normal thrust speed="+m.Velocity.magnitude);
            typeof(MechFlightMotionController).GetField("velocity",Private).SetValue(m,Vector3.forward*10000);
            m.Tick(in input,1f/hz);Assert.Greater(m.Velocity.magnitude,10000);
            m.ConfigureTechnicalSafetySpeed(2000);m.Tick(in input,1f/hz);Assert.LessOrEqual(m.Velocity.magnitude,2000.01f);
        }
        finally{UnityEngine.Object.DestroyImmediate(go);}
    }
    [Test] public void SavedSceneHasNoOldSpeedCapsAndLayoutDoubled()
    {
        string scene=File.ReadAllText("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");
        var matches=Regex.Matches(scene,@"technicalSafetySpeed: ([^\r\n]+)");Assert.GreaterOrEqual(matches.Count,5);
        foreach(Match m in matches)Assert.AreEqual(0,float.Parse(m.Groups[1].Value,CultureInfo.InvariantCulture));
        const string path="Assets/_Project/Resources/Mech/ThrusterNozzleLayout.json";
        var old=JsonUtility.FromJson<MechThrusterNozzleLayoutData>(File.ReadAllText("Artifacts/ThrustX2-Uncapped-20261025/before/"+path));
        var current=JsonUtility.FromJson<MechThrusterNozzleLayoutData>(File.ReadAllText(path));Assert.AreEqual(old.nozzles.Count,current.nozzles.Count);
        for(int i=0;i<old.nozzles.Count;i++){Assert.AreEqual(old.nozzles[i].id,current.nozzles[i].id);Assert.AreEqual(old.nozzles[i].ratedThrustKn*2,current.nozzles[i].ratedThrustKn,.001f);}
    }
    [TestCase("AC6_Skinned_Uniform1010")] [TestCase("AC6_Skinned_Uniform2010")] [TestCase("AC6_Skinned_Uniform3010")]
    public void AggregatedComponentThrustIsExactlyTwiceHalfStrengthCopy(string name)
    {
        var source=AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Loadouts/"+name+".asset");Assert.IsNotNull(source);
        var half=UnityEngine.Object.Instantiate(source);var clones=new List<MechPartDefinition>();
        try
        {
            foreach(var assignment in half.Assignments)
            {
                if(assignment.part==null)continue;var part=UnityEngine.Object.Instantiate(assignment.part);clones.Add(part);assignment.part=part;
                string json=JsonUtility.ToJson(part);
                json=Regex.Replace(json,@"(""(?:main|lateral|reverse|upward|downward|rcs|assaultBoost|quickBoost|maximum)ThrustKn"":)([-+0-9.eE]+)",m=>m.Groups[1].Value+(double.Parse(m.Groups[2].Value,CultureInfo.InvariantCulture)*.5).ToString("R",CultureInfo.InvariantCulture));
                JsonUtility.FromJsonOverwrite(json,part);
            }
            var full=MechStatsCalculator.Calculate(source);var baseline=MechStatsCalculator.Calculate(half);
            Assert.AreEqual(baseline.totalMassKg,full.totalMassKg);
            foreach(var field in new[]{"mainThrustKn","lateralThrustKn","reverseThrustKn","upwardThrustKn","downwardThrustKn","rcsThrustKn"})
            {var f=typeof(MechAggregatedStats).GetField(field);Assert.AreEqual((float)f.GetValue(baseline)*2,(float)f.GetValue(full),.01f,field);}
            Assert.AreEqual(baseline.boosterPerformance.quickBoostThrustKn*2,full.boosterPerformance.quickBoostThrustKn,.01f);
            Assert.AreEqual(baseline.boosterPerformance.assaultBoostThrustKn*2,full.boosterPerformance.assaultBoostThrustKn,.01f);
        }
        finally{foreach(var p in clones)UnityEngine.Object.DestroyImmediate(p);UnityEngine.Object.DestroyImmediate(half);}
    }
}
