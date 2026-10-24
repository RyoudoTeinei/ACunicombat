using System;
using System.Collections;
using System.IO;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using UnityEngine;
public sealed class MechSignalDefenseTests
{
    private static MissileTrackSnapshot Track(int id,float time=1)=>new MissileTrackSnapshot
    {TrackId=new TrackId(new Handle(id,1)),Quality=MissileTrackQuality.WeaponsQuality,Incoming=true,TimeToClosestApproachSeconds=time,LastEvidenceTime=10,Confidence=1};
    [UnityTest] public IEnumerator RealMissileBurnoutUpdatesThermalRendererAndInfraredTogether()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var owner=new GameObject("Thermal test launcher");var go=new GameObject("Thermal test missile");go.transform.position=Vector3.up*1000;
        var missile=go.AddComponent<MechTestProjectile>();
        missile.Configure(owner.transform,null,default,TestWeaponKind.Missile,Vector3.forward*100,10,.1f,5,
            accelerationMetersPerSecondSquared:10,newMotorBurnDuration:.05f,newSeekerActivationDelay:10);
        float hot=missile.ThermalTemperatureK,hotIr=missile.InfraredIntensityDb;
        for(int step=0;step<20;step++)yield return new WaitForFixedUpdate();
        Assert.That(missile!=null,Is.True);float cool=missile.ThermalTemperatureK,coolIr=missile.InfraredIntensityDb;
        Assert.That(cool,Is.LessThan(hot).And.GreaterThan(300));Assert.That(coolIr,Is.LessThan(hotIr));
        Assert.That(go.GetComponent<MechThermalPointEmitter>().TemperatureK,Is.EqualTo(cool));
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
        if(!string.IsNullOrEmpty(output))File.WriteAllText(Path.Combine(output,"thermal-source-demo.json"),JsonUtility.ToJson(new HeatDemo{burningK=hot,coastingK=cool,burningDb=hotIr,coastingDb=coolIr},true));
        UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(owner);yield return new ExitPlayMode();
    }
    [Serializable] private class HeatDemo{public float burningK,coastingK,burningDb,coastingDb;}
    [UnityTest] public IEnumerator PhysicalPointDefenseRoundInterceptsMissileBeforeImpact()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        var owner=new GameObject("Defense owner");var attacker=new GameObject("Attacker");
        var incoming=new GameObject("Incoming missile");incoming.transform.position=Vector3.forward*100;incoming.transform.rotation=Quaternion.LookRotation(Vector3.back);
        var missile=incoming.AddComponent<MechTestProjectile>();missile.Configure(attacker.transform,null,default,TestWeaponKind.Missile,Vector3.back*100,10,.3f,5,newSeekerActivationDelay:10);
        var roundObject=new GameObject("Physical defense round");var round=roundObject.AddComponent<MechTestProjectile>();
        round.Configure(owner.transform,null,default,TestWeaponKind.PointDefenseCannon,Vector3.forward*1000,10000,.2f,1);
        round.ConfigureInterceptorRound(3);
        float until=Time.fixedTime+.4f,deadline=Time.realtimeSinceStartup+5;
        while(Time.fixedTime<until&&Time.realtimeSinceStartup<deadline&&missile!=null&&missile.IsInterceptableMissile)yield return null;
        Assert.That(missile==null||!missile.IsInterceptableMissile,Is.True,"Physical intercept failed; missile="+(missile!=null?missile.transform.position.ToString():"destroyed")+" round="+(round!=null?round.transform.position.ToString():"destroyed"));
        if(roundObject!=null)UnityEngine.Object.DestroyImmediate(roundObject);if(incoming!=null)UnityEngine.Object.DestroyImmediate(incoming);
        UnityEngine.Object.DestroyImmediate(owner);UnityEngine.Object.DestroyImmediate(attacker);yield return new ExitPlayMode();
    }
    [Test] public void MotorBurnoutAndReignitionShareTemperatureAndInfraredState()
    {
        var heat=new MechProjectileHeat();heat.Step(true,.1f);float hot=heat.TemperatureK,signal=heat.IntensityDb;
        heat.Step(false,.5f);Assert.That(heat.TemperatureK,Is.LessThan(hot).And.GreaterThan(300));Assert.That(heat.IntensityDb,Is.LessThan(signal).And.GreaterThan(28));
        heat.Step(true,.1f);Assert.That(heat.TemperatureK,Is.EqualTo(hot));Assert.That(heat.IntensityDb,Is.EqualTo(signal));
        heat.Reset();Assert.That(heat.IntensityDb,Is.EqualTo(28));
    }
    [Test] public void CoolingIsIndependentOfStepSize()
    {
        var a=new MechProjectileHeat();var b=new MechProjectileHeat();a.Step(true,0);b.Step(true,0);a.Step(false,1);
        for(int i=0;i<10;i++)b.Step(false,.1f);Assert.That(a.TemperatureK,Is.EqualTo(b.TemperatureK).Within(.001));
    }
    [Test] public void DefenseRejectsWarningsFriendsStaleAndUnauthorizedTracks()
    {
        var module=new MechActiveDefenseModule();var track=Track(1);
        Assert.That(module.Accepts(in track,10),Is.True);
        track.Quality=MissileTrackQuality.Warning;Assert.That(module.Accepts(in track,10),Is.False);
        track=Track(1);track.Affiliation=TrackAffiliation.Friendly;track.IdentityConfidence=1;Assert.That(module.Accepts(in track,10),Is.False);
        track=Track(1);Assert.That(module.Accepts(in track,13),Is.False);
        module.Authorized=false;Assert.That(module.Accepts(in track,10),Is.False);
    }
    [Test] public void DefenseSelectionIsStableAndImmediatelyDropsInvalidHeldTrack()
    {
        var module=new MechActiveDefenseModule();var a=Track(1);var b=Track(2);
        var selected=module.Select(new[]{b,a},2,10,1,t=>true,t=>t.TimeToClosestApproachSeconds);
        Assert.That(selected.TrackId,Is.EqualTo(a.TrackId));
        a.Incoming=false;selected=module.Select(new[]{a,b},2,10.1,1,t=>true,t=>t.TimeToClosestApproachSeconds);
        Assert.That(selected.TrackId,Is.EqualTo(b.TrackId));
        selected=module.Select(new[]{b},1,10.2,1,t=>false,t=>0);Assert.That(selected.TrackId.IsValid,Is.False);
    }
}
