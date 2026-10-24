using System;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
public sealed class MechMissileWarningTests
{
    static MissileTrackSnapshot Incoming(double time,int id=0)=>new MissileTrackSnapshot{TrackId=new TrackId(new Handle(id,1)),Position=new double3(0,0,32000),Velocity=new double3(0,0,-2000),Incoming=true,Quality=MissileTrackQuality.Warning,SourceMask=MissileTrackSourceMask.Radar,ClosestApproachDistanceMeters=20,CpaUncertaintyMeters=5,TimeToClosestApproachSeconds=16,LastEvidenceTime=time};
    [Test] public void SilentSeekerIncomingTrackOutranksLock(){var state=new MechMissileWarningState();Assert.That(state.Evaluate(new[]{Incoming(10)},1,10,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.MissileActive));}
    [TestCase(30)] [TestCase(60)] public void SalvoStaysAboveLockAcrossRevisitAndBriefIntentGaps(int fps)
    {
        var state=new MechMissileWarningState();var a=Incoming(0);var b=Incoming(0,1);
        for(int frame=0;frame<fps*10;frame++){double now=frame/(double)fps;if(frame%(fps/5)==0){a=Incoming(now);b=Incoming(now,1);}a.Incoming=frame%7!=0;b.Incoming=frame%9!=0;if(frame==0)a.Incoming=true;
            Assert.That(state.Evaluate(new[]{a,b},2,now,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.MissileActive),"frame="+frame);}
    }
    [Test] public void StaleRepeatedSnapshotsCannotRefreshWarningForever(){var state=new MechMissileWarningState();var t=Incoming(10);state.Evaluate(new[]{t},1,10,EsmThreatLevel.Locked);Assert.That(state.Evaluate(new[]{t},1,11.01,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.Locked));}
    [Test] public void MissingTrackBridgesShortGapButExpires(){var s=new MechMissileWarningState();s.Evaluate(new[]{Incoming(10)},1,10,EsmThreatLevel.Locked);Assert.That(s.Evaluate(Array.Empty<MissileTrackSnapshot>(),0,10.4,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.MissileActive));Assert.That(s.Evaluate(Array.Empty<MissileTrackSnapshot>(),0,11.1,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.Locked));}
    [Test] public void FriendlyReidentificationRevokesOnlyThatMissile(){var s=new MechMissileWarningState();var a=Incoming(0);var b=Incoming(0,1);s.Evaluate(new[]{a,b},2,0,EsmThreatLevel.Locked);a.Affiliation=TrackAffiliation.Friendly;a.IdentityConfidence=1;Assert.That(s.Evaluate(new[]{a,b},2,.1,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.MissileActive));b.Affiliation=TrackAffiliation.Friendly;b.IdentityConfidence=1;Assert.That(s.Evaluate(new[]{a,b},2,.2,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.Locked));}
    [Test] public void PassingAndInterceptorTracksDoNotCreateWarning(){var s=new MechMissileWarningState();var a=Incoming(0);a.ClosestApproachDistanceMeters=10000;var b=Incoming(0,1);b.TargetIntent=MissileTargetIntent.FriendlyMissile;b.TargetIntentConfidence=1;Assert.That(s.Evaluate(new[]{a,b},2,0,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.Locked));}
    [Test] public void PassedMissileWithLingeringOwnshipIntentDoesNotRenewWarning(){var s=new MechMissileWarningState();var t=Incoming(0);t.TargetIntent=MissileTargetIntent.Ownship;t.TargetIntentConfidence=1;s.Evaluate(new[]{t},1,0,EsmThreatLevel.Locked);t.LastEvidenceTime=2;t.TimeToClosestApproachSeconds=0;t.Incoming=false;Assert.That(s.Evaluate(new[]{t},1,2,EsmThreatLevel.Locked),Is.EqualTo(EsmThreatLevel.Locked));}
    [Test] public void TimeResetClearsPreviousSession(){var s=new MechMissileWarningState();s.Evaluate(new[]{Incoming(10)},1,10,EsmThreatLevel.Locked);Assert.That(s.Evaluate(Array.Empty<MissileTrackSnapshot>(),0,0,EsmThreatLevel.None),Is.EqualTo(EsmThreatLevel.None));}
    [Test] public void PresenterUsesFusedTrackWithoutAnyReceiverContact()
    {
        var go=new GameObject("Warning integration");try{var p=go.AddComponent<MechThreatWarningPresenter>();var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;var data=(MissileTrackSnapshot[])typeof(MechThreatWarningPresenter).GetField("missileTrackScratch",flags).GetValue(p);data[0]=Incoming(Time.timeAsDouble);typeof(MechThreatWarningPresenter).GetField("missileTrackCount",flags).SetValue(p,1);typeof(MechThreatWarningPresenter).GetMethod("RefreshWarningState",flags).Invoke(p,null);Assert.That(p.CurrentWarningLevel,Is.EqualTo(EsmThreatLevel.MissileActive));}finally{UnityEngine.Object.DestroyImmediate(go);}
    }
    [UnityEngine.TestTools.UnityTest] public System.Collections.IEnumerator RealSilentSeekerMissileTriggersWarningFromRadar()
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);yield return new UnityEngine.TestTools.EnterPlayMode();
        var own=new GameObject("Warning observer");var launcher=new GameObject("Hostile launcher");var shot=new GameObject("Silent seeker incoming");float old=Time.captureDeltaTime;Time.captureDeltaTime=.04f;
        try
        {
            own.AddComponent<AudioListener>();own.AddComponent<CombatTestTarget>().ConfigureIdentity("OWN",1);var radar=own.AddComponent<MechRadarController>();radar.SetTrackOperationMode(true);var warning=own.AddComponent<MechThreatWarningPresenter>();
            launcher.transform.position=new Vector3(0,0,40000);launcher.AddComponent<CombatTestTarget>().ConfigureIdentity("HOSTILE",2);shot.transform.position=launcher.transform.position;shot.transform.rotation=Quaternion.LookRotation(Vector3.back);
            shot.AddComponent<MechTestProjectile>().Configure(launcher.transform,null,default,TestWeaponKind.Missile,Vector3.back*2000,10,.1f,30,accelerationMetersPerSecondSquared:1,newSeekerActivationDelay:100,newMaximumTravelDistance:100000);
            var tracks=new MissileTrackSnapshot[64];float start=Time.time;int verified=0,maxTracks=0;bool configured=false;string last="";ulong seq=900000;
            while(Time.time-start<12)
            {
                yield return null;var driver=own.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();if(driver==null)continue;if(!configured){driver.ConfigureRadarTracking(16,3);configured=true;}
                if(shot!=null)driver.ScheduleRadarDwell(new ACunicombat.Sensors.Dwell{SensorId=driver.PrimaryRadarIdentity,DueTime=radar.SimTimeSeconds,DwellTimeSeconds=.008f,BeamDirection=(float3)shot.transform.position.normalized,BeamWidthRadians=new MechRadarPerformance().Physical.BeamWidthRadians,Priority=ACunicombat.Sensors.DwellPriority.TrackWhileScan,Sequence=seq++});
                int n=radar.CopyMissileTracks(tracks);maxTracks=Math.Max(maxTracks,n);if(n>0){var t=tracks[0];last=$"quality={t.Quality} incoming={t.Incoming} intent={t.TargetIntent} tca={t.TimeToClosestApproachSeconds} cpa={t.ClosestApproachDistanceMeters} source={t.SourceMask} age={radar.SimTimeSeconds-t.LastEvidenceTime} friendly={t.IsFriendly}";}bool incoming=false;for(int i=0;i<n;i++)incoming|=MechMissileWarningState.IsIncomingEvidence(tracks[i],radar.SimTimeSeconds);
                if(incoming){typeof(MechThreatWarningPresenter).GetMethod("Update",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(warning,null);Assert.That(warning.CurrentWarningLevel,Is.EqualTo(EsmThreatLevel.MissileActive));verified++;}
            }
            Assert.That(verified,Is.GreaterThan(30),$"maxTracks={maxTracks} last={last}");TestContext.WriteLine("Real silent seeker incoming warning frames="+verified);
        }
        finally{Time.captureDeltaTime=old;UnityEngine.Object.DestroyImmediate(shot);UnityEngine.Object.DestroyImmediate(launcher);UnityEngine.Object.DestroyImmediate(own);}
        yield return new UnityEngine.TestTools.ExitPlayMode();
    }
}
