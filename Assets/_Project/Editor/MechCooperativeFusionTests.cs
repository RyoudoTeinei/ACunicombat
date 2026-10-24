using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensors;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
public sealed class MechCooperativeFusionTests
{
    private static bool AiAccepts(TrackSnapshot track)
    {
        var go=new GameObject("AI identity gate fixture");go.SetActive(false);
        try{return (bool)typeof(MechAiTargetController).GetMethod("IsUsableAirTarget",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(go.AddComponent<MechAiTargetController>(),new object[]{track});}
        finally{UnityEngine.Object.DestroyImmediate(go);}
    }

    private static TrackSnapshot Radar(int index,double3 position,double time=0,byte generation=1)
    {
        var t=new TrackSnapshot{TrackId=new TrackId(new Handle(index,generation)),Position=position,Velocity=new double3(120,0,0),
            LastUpdateTime=time,LastPredictTime=time,ObjectClass=TrackObjectClass.AirVehicle,Lifecycle=TrackLifecycle.Confirmed,Confidence=.9f,SourceMask=TrackSourceMask.Radar};
        for(int i=0;i<3;i++){t.SetCovariance(i,i,250000);t.SetCovariance(i+3,i+3,10000);}t.SourceEvidence.Record(TrackSourceMask.Radar,.9f,time);return t;
    }
    private static TrackSnapshot Report(int index,double3 position,double time=0)
    {
        var t=Radar(index,position,time);t.Velocity=double3.zero;t.Affiliation=TrackAffiliation.Friendly;t.IdentityConfidence=1;
        t.SourceMask=TrackSourceMask.DataLink;t.SourceEvidence=default;t.SourceEvidence.Record(TrackSourceMask.DataLink,1,time);
        t.IsIdentifiedMech=true;t.IdentifiedRole=ObservedCombatRole.AirDefense;
        for(int i=0;i<3;i++){t.SetCovariance(i,i,4);t.SetCovariance(i+3,i+3,25);}return t;
    }
    private static void Advance(ref TrackSnapshot t,double time){t.Position=t.PredictPosition(time);t.LastUpdateTime=time;t.LastPredictTime=time;}
    [Test] public void FourRadarPlusTwoReportsBecomeFourSharedContacts_WithFriendlyIdentity()
    {
        var fusion=new MechCooperativeTrackFusion();var local=new[]{Radar(0,new double3(400,0,60000)),Radar(1,new double3(400,0,-40000)),Radar(2,new double3(30000,0,80000)),Radar(3,new double3(-30000,0,80000))};
        var reports=new[]{Report(1001,new double3(0,0,60000)),Report(1002,new double3(0,0,-40000))};
        fusion.Update(local,4,reports,0);Assert.That(fusion.Contacts.Count,Is.EqualTo(6),"One observation is not enough to claim identity.");
        for(int i=0;i<4;i++)Advance(ref local[i],.25);for(int i=0;i<2;i++)Advance(ref reports[i],.25);
        fusion.Update(local,4,reports,.25);
        Assert.That(fusion.Contacts.Count,Is.EqualTo(4));Assert.That(fusion.Contacts.Count(x=>x.Affiliation==TrackAffiliation.Friendly),Is.EqualTo(2));
        Assert.That(fusion.TryGet(local[0].TrackId,out var friend),Is.True);
        Assert.That(friend.SourceMask.HasFlag(TrackSourceMask.Radar|TrackSourceMask.DataLink),Is.True);
        Assert.That(friend.IsIdentifiedMech,Is.True);Assert.That(friend.IdentifiedRole,Is.EqualTo(ObservedCombatRole.AirDefense));
        Assert.That(math.distance(friend.Position,reports[0].Position),Is.LessThan(10));
        
        Assert.That(AiAccepts(friend),Is.False,"AI must reject this same friendly contact.");
        Assert.That(local[0].Affiliation,Is.EqualTo(TrackAffiliation.Unknown),"Fusion must not overwrite raw sensor facts.");
    }
    [Test] public void RepeatedPacketDoesNotConfirm_AndExpiredIdentityDoesNotPersist()
    {
        var f=new MechCooperativeTrackFusion();var local=new[]{Radar(0,new double3(300,0,50000))};var reports=new[]{Report(1001,new double3(0,0,50000))};
        for(int i=0;i<5;i++)f.Update(local,1,reports,.1);
        Assert.That(f.Contacts.Count,Is.EqualTo(2));
        Advance(ref local[0],.25);Advance(ref reports[0],.25);f.Update(local,1,reports,.25);Assert.That(f.Contacts.Count,Is.EqualTo(1));
        Advance(ref local[0],4);f.Update(local,1,reports,4);
        Assert.That(f.Contacts.Count,Is.EqualTo(1));Assert.That(f.Contacts[0].Affiliation,Is.EqualTo(TrackAffiliation.Unknown));
        Assert.That(f.Contacts[0].SourceMask,Is.EqualTo(TrackSourceMask.Radar));
    }
    [Test] public void AmbiguousCrossingDoesNotIdentifyTwoAircraftFromOneReport()
    {
        var f=new MechCooperativeTrackFusion();var local=new[]{Radar(0,new double3(-50,0,50000)),Radar(1,new double3(50,0,50000))};var reports=new[]{Report(1001,new double3(0,0,50000))};
        f.Update(local,2,reports,0);for(int i=0;i<2;i++)Advance(ref local[i],.25);Advance(ref reports[0],.25);f.Update(local,2,reports,.25);
        Assert.That(f.Contacts.Count,Is.EqualTo(3));Assert.That(f.Contacts.Take(2).All(x=>x.Affiliation==TrackAffiliation.Unknown),Is.True);
    }
    [Test] public void NewTrackGenerationCannotInheritPreviousBinding()
    {
        var f=new MechCooperativeTrackFusion();var local=new[]{Radar(0,new double3(300,0,50000))};var reports=new[]{Report(1001,new double3(0,0,50000))};
        f.Update(local,1,reports,0);Advance(ref local[0],.25);Advance(ref reports[0],.25);f.Update(local,1,reports,.25);
        local[0]=Radar(0,new double3(300,0,50000),.5,2);Advance(ref reports[0],.5);f.Update(local,1,reports,.5);
        Assert.That(f.Contacts.Count,Is.EqualTo(2));Assert.That(f.Contacts[0].Affiliation,Is.EqualTo(TrackAffiliation.Unknown));
    }
    [Test] public void ObserverIsolationAndVerifiedHostileConflictAreRespected()
    {
        var a=new MechCooperativeTrackFusion();var b=new MechCooperativeTrackFusion();var local=new[]{Radar(0,new double3(300,0,50000))};var reports=new[]{Report(1001,new double3(0,0,50000))};
        a.Update(local,1,reports,0);Advance(ref local[0],.25);Advance(ref reports[0],.25);a.Update(local,1,reports,.25);
        b.Update(local,1,Array.Empty<TrackSnapshot>(),.25);Assert.That(a.Contacts[0].Affiliation,Is.EqualTo(TrackAffiliation.Friendly));Assert.That(b.Contacts[0].Affiliation,Is.EqualTo(TrackAffiliation.Unknown));
        local[0].Affiliation=TrackAffiliation.Hostile;local[0].IdentityConfidence=1;a.Update(local,1,reports,.25);
        Assert.That(a.Contacts[0].Affiliation,Is.EqualTo(TrackAffiliation.Hostile));
    }
    [Test] public void LinkOnlyContactsRemainAvailableInSharedOutput()
    {
        var f=new MechCooperativeTrackFusion();f.Update(Array.Empty<TrackSnapshot>(),0,new[]{Report(1001,new double3(0,0,50000))},0);
        Assert.That(f.Contacts.Count,Is.EqualTo(1));Assert.That(f.Contacts[0].Affiliation,Is.EqualTo(TrackAffiliation.Friendly));Assert.That(f.Contacts[0].SourceMask,Is.EqualTo(TrackSourceMask.DataLink));
    }
    [Test] public void FutureOrNonFriendlyReportsCannotCreateAuthoritativeContacts()
    {
        var f=new MechCooperativeTrackFusion();var future=Report(1001,new double3(0,0,50000),10);
        var bad=Report(1002,new double3(0,0,50000));bad.Affiliation=TrackAffiliation.Unknown;
        var invalid=Report(1003,new double3(0,0,50000));invalid.LastPredictTime=double.NaN;
        f.Update(Array.Empty<TrackSnapshot>(),0,new[]{future,bad,invalid},0);Assert.That(f.Contacts.Count,Is.Zero);
    }
    [Test] public void DuplicateReportsUseNewestEvidenceWithoutDoubleCounting()
    {
        var f=new MechCooperativeTrackFusion();var older=Report(1001,new double3(0,0,50000),0);
        var latest=Report(1001,new double3(10,0,50000),.25);
        f.Update(Array.Empty<TrackSnapshot>(),0,new[]{older,latest,older},.25);
        Assert.That(f.Contacts.Count,Is.EqualTo(1));Assert.That(f.Contacts[0].LastUpdateTime,Is.EqualTo(.25));
    }
    [UnityEngine.TestTools.UnityTest] public System.Collections.IEnumerator LiveMapAndAiReadSameFourContactsThroughReceivedDataLink()
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        yield return new UnityEngine.TestTools.EnterPlayMode();
        float old=Time.captureDeltaTime;Time.captureDeltaTime=.04f;var objects=new List<GameObject>();
        try
        {
            var positions=new[]{new Vector3(-12000,-12000,40000),new Vector3(-20000,15000,60000),new Vector3(25000,-20000,60000),new Vector3(35000,27000,90000)};
            var bodies=new CombatTestTarget[4];
            for(int i=0;i<4;i++)
            {
                var go=new GameObject("Fusion mech "+i);objects.Add(go);go.transform.position=positions[i];bodies[i]=go.AddComponent<CombatTestTarget>();
                bodies[i].ConfigureLinearFlight(go.transform,5000,Vector3.right,i%2==0?100:70,false);bodies[i].ConfigureIdentity("WING-"+i,i<2?1:2);bodies[i].ConfigureSignatures(100,1,1);
                if(i<2){var peer=go.AddComponent<MechRadarController>();peer.SetRadarPowerEnabled(false);}
            }
            var own=new GameObject("Fusion observer");objects.Add(own);own.AddComponent<CombatTestTarget>().ConfigureIdentity("OWN",1);
            var radar=own.AddComponent<MechRadarController>();var map=own.AddComponent<MechBattlefieldMap>();
            var tracks=new TrackSnapshot[32];float start=Time.time;int checkedFrames=0;ulong seq=200000;
            
            while(Time.time-start<15)
            {
                yield return new WaitForFixedUpdate();
                var driver=own.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();if(driver==null)continue;
                int index=(int)((Time.time-start)/.04f)%4;
                driver.ScheduleRadarDwell(new Dwell{SensorId=driver.PrimaryRadarIdentity,DueTime=radar.SimTimeSeconds,DwellTimeSeconds=.008f,
                    BeamDirection=(float3)bodies[index].transform.position.normalized,BeamWidthRadians=new MechRadarPerformance().Physical.BeamWidthRadians,Priority=DwellPriority.TrackWhileScan,Sequence=seq++});
                if(Time.time-start<4)continue;
                int count=radar.CopyTracks(tracks);map.Refresh();
                Assert.That(count,Is.EqualTo(4));Assert.That(map.Contacts.Count,Is.EqualTo(5),"Unchanged map should contain ownship plus four contacts, not six reports.");
                int friends=0;for(int i=0;i<count;i++)if(tracks[i].Affiliation==TrackAffiliation.Friendly)
                {
                    friends++;Assert.That(AiAccepts(tracks[i]),Is.False);
                    Assert.That(radar.TryGetTrack(tracks[i].TrackId,out var same),Is.True);Assert.That(same.Affiliation,Is.EqualTo(TrackAffiliation.Friendly));
                    Assert.That(same.SourceMask,Is.EqualTo(tracks[i].SourceMask));
                }
                Assert.That(friends,Is.EqualTo(2));
                if(checkedFrames%40==0)
                {
                    var flags=BindingFlags.NonPublic|BindingFlags.Instance;
                    typeof(MechRadarController).GetField("missileTrackCacheTime",flags).SetValue(radar,double.NaN);
                    var internalBuffer=(TrackSnapshot[])typeof(MechRadarController).GetField("trackScratch",flags).GetValue(radar);
                    int internalCount=radar.CopyTracks(internalBuffer);
                    Assert.That(internalBuffer.Take(internalCount).Count(t=>t.Affiliation==TrackAffiliation.Friendly),Is.EqualTo(2),"Internal radar callers must not lose fused identity when the missile cache refreshes.");
                }
                Assert.That(map.Contacts.Count(c=>!c.Own&&c.Affiliation==TrackAffiliation.Friendly),Is.EqualTo(2));checkedFrames++;
            }
            Assert.That(checkedFrames,Is.GreaterThan(20));TestContext.WriteLine($"Verified {checkedFrames} frames: 4 shared contacts, 2 friendly; map 5 including ownship; AI rejects both friends.");
        }
        finally{Time.captureDeltaTime=old;foreach(var go in objects)if(go!=null)UnityEngine.Object.DestroyImmediate(go);}
        yield return new UnityEngine.TestTools.ExitPlayMode();
    }
}
