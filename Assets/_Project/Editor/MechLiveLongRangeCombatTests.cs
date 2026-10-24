using System;
using System.Collections;
using System.Collections.Generic;
using ACunicombat.Mechs;
using ACunicombat.Sensors;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Driver;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public sealed class MechLiveLongRangeCombatTests
{
    [UnityTest] public IEnumerator RealMissileSalvosAt100Km()=>Salvos(100000);
    [UnityTest] public IEnumerator RealMissileSalvosAt250Km()=>Salvos(250000);
    private IEnumerator Salvos(float range)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float old=Time.captureDeltaTime;Time.captureDeltaTime=.04f;var objects=new List<GameObject>();
        try
        {
            var own=new GameObject("Moving observer");objects.Add(own);own.AddComponent<CombatTestTarget>().ConfigureLinearFlight(own.transform,10000,Vector3.right,100,false);
            own.GetComponent<CombatTestTarget>().ConfigureIdentity("OWN",1);
            var radar=own.AddComponent<MechRadarController>();radar.SetTrackOperationMode(true);
            var targets=new CombatTestTarget[4];var shots=new MechTestProjectile[8];
            for(int i=0;i<4;i++){var go=new GameObject("Long range launcher "+i);objects.Add(go);go.transform.position=new Vector3((i-1.5f)*range*.15f,(i%2==0?1:-1)*range*.12f,range);targets[i]=go.AddComponent<CombatTestTarget>();targets[i].ConfigureLinearFlight(go.transform,10000,Vector3.right,80+i*15,false);targets[i].ConfigureIdentity("HOSTILE"+i,2);targets[i].ConfigureSignatures(100,1,1);}
            var tracks=new TrackSnapshot[64];float start=Time.time;int maxAir=0,maxMissile=0;ulong seq=900000;int launched=0;int step=0;
            while(Time.time-start<range/2200+25)
            {
                yield return new WaitForFixedUpdate();float elapsed=Time.time-start;step++;
                for(int m=0;m<8;m++)if(shots[m]==null&&elapsed>=8+m*.3f&&launched==m)
                {
                    var go=new GameObject("Actual salvo "+m);objects.Add(go);go.transform.position=targets[m%4].transform.position;
                    var velocity=(own.transform.position-go.transform.position).normalized*2200;go.transform.rotation=Quaternion.LookRotation(velocity);
                    shots[m]=go.AddComponent<MechTestProjectile>();shots[m].Configure(targets[m%4].transform,null,default,TestWeaponKind.Missile,velocity,10,.1f,180,accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:180,newMaximumTravelDistance:1000000);launched++;
                }
                var driver=own.GetComponent<SimulationDriver>();if(driver==null)continue;
                for(int b=0;b<2;b++)
                {
                    int index=(step*2+b)%12;Transform target=index<4?targets[index].transform:(shots[index-4]!=null?shots[index-4].transform:null);if(target==null)continue;
                    var direction=(float3)(target.position-own.transform.position).normalized;if(step%5==0)direction=math.mul(quaternion.RotateY(.12f),direction);
                    driver.ScheduleRadarDwell(new Dwell{SensorId=driver.PrimaryRadarIdentity,DueTime=radar.SimTimeSeconds,DwellTimeSeconds=.008f,BeamDirection=direction,BeamWidthRadians=new MechRadarPerformance().Physical.BeamWidthRadians,Priority=DwellPriority.TrackWhileScan,Sequence=seq++});
                }
                int count=radar.CopyTracks(tracks),air=0,missile=0;
                for(int i=0;i<count;i++){Assert.That(math.all(math.isfinite(tracks[i].Velocity)),Is.True);if(tracks[i].ObjectClass==TrackObjectClass.AirVehicle){air++;Assert.That(math.length(tracks[i].Velocity),Is.LessThan(1200));}if(tracks[i].ObjectClass==TrackObjectClass.Missile)missile++;}
                maxAir=Math.Max(maxAir,air);maxMissile=Math.Max(maxMissile,missile);
                Assert.That(air,Is.LessThanOrEqualTo(4),$"t={elapsed} range={range}");Assert.That(missile,Is.LessThanOrEqualTo(launched),$"missile duplicates t={elapsed}");
            }
            Assert.That(launched,Is.EqualTo(8));Assert.That(maxAir,Is.EqualTo(4));Assert.That(maxMissile,Is.GreaterThan(0));TestContext.WriteLine($"Actual projectile run range={range} aircraft={maxAir} launched={launched} peakMissiles={maxMissile}");
        }
        finally{Time.captureDeltaTime=old;foreach(var go in objects)if(go!=null)UnityEngine.Object.DestroyImmediate(go);}
        yield return new ExitPlayMode();
    }
    [UnityTest] public IEnumerator OwnRadarOffKeepsReceivedFriendNavigationAt100Km()=>SilentLink(100000,true);
    [UnityTest] public IEnumerator OwnRadarOffDoesNotInventReportsOutsideLinkRange()=>SilentLink(250000,false);
    private IEnumerator SilentLink(float range,bool expectsReport)
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float old=Time.captureDeltaTime;Time.captureDeltaTime=.04f;var objects=new List<GameObject>();
        try
        {
            var peer=new GameObject("Remote friendly sender");objects.Add(peer);peer.transform.position=new Vector3(0,0,range);
            var body=peer.AddComponent<CombatTestTarget>();body.ConfigureLinearFlight(peer.transform,10000,Vector3.right,100,false);body.ConfigureIdentity("FRIEND",1);peer.AddComponent<MechRadarController>().SetRadarPowerEnabled(false);
            var own=new GameObject("Radar silent receiver");objects.Add(own);own.AddComponent<CombatTestTarget>().ConfigureIdentity("OWN",1);var radar=own.AddComponent<MechRadarController>();radar.SetRadarPowerEnabled(false);
            var tracks=new TrackSnapshot[32];var emissions=new EmissionEvent[128];float start=Time.time;int checkedFrames=0;double firstX=double.NaN,lastX=0;
            while(Time.time-start<15)
            {
                yield return new WaitForFixedUpdate();if(Time.time-start<4)continue;
                var driver=own.GetComponent<SimulationDriver>();Assert.That(driver,Is.Not.Null);
                Assert.That(driver.CopyRadarEmissions(driver.PrimaryRadarIdentity,emissions),Is.Zero,"Own primary radar must remain silent.");
                int count=radar.CopyTracks(tracks);if(!expectsReport){Assert.That(count,Is.Zero);checkedFrames++;continue;}Assert.That(count,Is.EqualTo(1));Assert.That(tracks[0].Affiliation,Is.EqualTo(TrackAffiliation.Friendly));Assert.That(tracks[0].SourceMask,Is.EqualTo(TrackSourceMask.DataLink));
                Assert.That(math.distance(tracks[0].Position,(double3)(float3)peer.transform.position),Is.LessThan(100));if(double.IsNaN(firstX))firstX=tracks[0].Position.x;lastX=tracks[0].Position.x;checkedFrames++;
            }
            Assert.That(checkedFrames,Is.GreaterThan(50));if(expectsReport)Assert.That(lastX-firstX,Is.GreaterThan(500));
        }
        finally{Time.captureDeltaTime=old;foreach(var go in objects)if(go!=null)UnityEngine.Object.DestroyImmediate(go);}
        yield return new ExitPlayMode();
    }
}
