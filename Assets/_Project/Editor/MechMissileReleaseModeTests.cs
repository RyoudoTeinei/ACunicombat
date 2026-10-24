using System;
using System.Collections;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using NUnit.Framework;

public class MechMissileReleaseModeTests
{
    [TestCase(false,false)] [TestCase(false,true)]
    [TestCase(true,false)] [TestCase(true,true)]
    public void RedDesignationAndReleaseSuffixAreIndependent(bool designated,bool ready)
    {
        var go=new GameObject("HUD state contract");go.SetActive(false);
        try
        {
            var radar=go.AddComponent<MechRadarController>();var hud=go.AddComponent<MechRadarHudPresenter>();
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            var t=Track(1,10);
            typeof(MechRadarController).GetField("candidateTrackId",flags).SetValue(radar,designated?t.TrackId:default(TrackId));
            typeof(MechRadarHudPresenter).GetField("radar",flags).SetValue(hud,radar);
            var cache=(System.Collections.Generic.Dictionary<TrackId,(int frame,bool ready,MechTargetTrackingMode mode)>)typeof(MechRadarHudPresenter).GetField("releaseDisplays",flags).GetValue(hud);
            cache[t.TrackId]=(Time.frameCount,ready,MechTargetTrackingMode.DataLink);
            Assert.AreEqual(designated,typeof(MechRadarHudPresenter).GetMethod("IsPlayerDesignated",flags).Invoke(hud,new object[]{t}));
            Assert.AreEqual(ready?"数据链·锁定":"数据链",typeof(MechRadarHudPresenter).GetMethod("TargetModeLabel",flags).Invoke(hud,new object[]{t}));
        }
        finally{UnityEngine.Object.DestroyImmediate(go);}
    }
    static TrackSnapshot Track(int index,double time)
    {
        var t=new TrackSnapshot{TrackId=new TrackId(new Handle(index,1)),Position=new double3(0,0,8000),
            LastUpdateTime=time,LastPredictTime=time,Confidence=1,Classification=1,ObjectClass=TrackObjectClass.AirVehicle,Lifecycle=TrackLifecycle.Confirmed};
        for(int i=0;i<6;i++)t.SetCovariance(i,i,4);return t;
    }
    [Test] public void ReleaseRequiresReachableInterceptAndUncertaintyClearOfOwnship()
    {
        Assert.IsFalse(MechMissileLaunchSource.SafeGeometry(101000,10,20,102000,90000));
        Assert.IsTrue(MechMissileLaunchSource.SafeGeometry(101000,10,20,80000,90000),"Closing target may enter a reachable intercept despite its current range.");
        Assert.IsFalse(MechMissileLaunchSource.SafeGeometry(100,50,20,100,90000));
        Assert.IsTrue(MechMissileLaunchSource.SafeGeometry(8000,10,20,8100,90000));
    }
    [Test] public void SearchDetectionAloneNeverClaimsMissileRelease()
    {
        var t=Track(1,10);t.SourceEvidence.Record(TrackSourceMask.Radar,1,10);
        var mode=MechMissileLaunchSource.Resolve(t,true,false,false,false,10);
        Assert.AreEqual(MechTargetTrackingMode.Search,mode);Assert.IsFalse(MechMissileLaunchSource.CanUse(mode,false));
        Assert.AreEqual("搜索",MechMissileLaunchSource.Label(mode));
        Assert.AreEqual(MechTargetTrackingMode.Search,MechMissileLaunchSource.Resolve(t,true,false,false,true,10),"Hearing radar does not relabel ordinary active search as a supported passive weapon mode.");
    }
    [Test] public void FreshRemoteObservationWorksWithOwnRadarOffButStaleOrFutureDoesNot()
    {
        var t=Track(1,10);t.SourceEvidence.Record(TrackSourceMask.DataLink,1,10);
        Assert.AreEqual(MechTargetTrackingMode.DataLink,MechMissileLaunchSource.Resolve(t,false,false,false,false,10.5));
        Assert.AreEqual(MechTargetTrackingMode.DataLink,MechMissileLaunchSource.Resolve(t,false,false,false,false,12));
        Assert.IsFalse(MechMissileLaunchSource.Fresh(t.SourceEvidence.DataLink,12,1));
        Assert.IsFalse(MechMissileLaunchSource.Fresh(t.SourceEvidence.DataLink,9,1));
    }
    [Test] public void PassiveReleaseRequiresCompatibleAmmunition()
    {
        Assert.IsFalse(MechMissileLaunchSource.CanUse(MechTargetTrackingMode.Passive,false));
        Assert.IsTrue(MechMissileLaunchSource.CanUse(MechTargetTrackingMode.Passive,true));
        Assert.AreEqual("被动·锁定",MechMissileLaunchSource.Label(MechTargetTrackingMode.Passive,true));
        Assert.AreEqual("数据链·锁定",MechMissileLaunchSource.Label(MechTargetTrackingMode.DataLink,true));
        Assert.AreEqual("跟踪",MechMissileLaunchSource.Label(MechTargetTrackingMode.Focused));
    }
    [Test] public void SharedTrackKeepsObservedEpochAndStableIdentityWhenLocalRadarDisappears()
    {
        var shared=new MechSharedTargetTracks();var remote=Track(4,10);var local=Track(2,10);
        var terminal=new LinkTerminalId(new Handle(3,1));Assert.IsTrue(shared.Receive(terminal,remote,10));
        var output=new TrackSnapshot[8];output[0]=local;Assert.AreEqual(1,shared.Merge(output,1,10));
        Assert.AreEqual(local.TrackId,output[0].TrackId);
        Assert.AreEqual(1,shared.Merge(output,0,10.5));Assert.AreEqual(local.TrackId,output[0].TrackId);
        Assert.AreEqual(10,output[0].SourceEvidence.DataLink.Timestamp);
        Assert.AreEqual(0,shared.Merge(output,0,12.1));
    }
    [Test] public void SharedUnknownCannotDuplicateAnAlreadyIdentifiedFriend()
    {
        var shared=new MechSharedTargetTracks();var remote=Track(4,10);var friend=Track(2,10);friend.Affiliation=TrackAffiliation.Friendly;
        shared.Receive(new LinkTerminalId(new Handle(3,1)),remote,10);
        var output=new TrackSnapshot[8];output[0]=friend;
        Assert.AreEqual(1,shared.Merge(output,1,10));Assert.AreEqual(TrackAffiliation.Friendly,output[0].Affiliation);
    }
    [Test] public void SharedReportsRejectReplayFutureAndInvalidCovariance()
    {
        var shared=new MechSharedTargetTracks();var t=Track(1,10);var sender=new LinkTerminalId(new Handle(0,1));
        Assert.IsFalse(shared.Receive(sender,t,9));Assert.IsTrue(shared.Receive(sender,t,10));Assert.IsFalse(shared.Receive(sender,t,10));
        t=Track(2,10);t.SetCovariance(0,0,-1);Assert.IsFalse(shared.Receive(sender,t,10));
    }
    [Test] public void PeerObservationOfReceiverIsNotAnotherAttackTarget()
    {
        var self=Track(1,10);self.Position=new double3(2,0,1);
        Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(self,double3.zero,double3.zero,10));
        Assert.IsTrue(MechSharedTargetTracks.MatchesOwnNavigation(self,double3.zero,double3.zero,10,true));
        var distant=Track(2,10);distant.SetCovariance(0,0,1e10);
        Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(distant,double3.zero,double3.zero,10));
        self.Affiliation=TrackAffiliation.Hostile;
        Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(self,double3.zero,double3.zero,10));
        Assert.IsTrue(MechSharedTargetTracks.MatchesOwnNavigation(self,double3.zero,double3.zero,10,true));
    }
    [Test] public void HostileLabelCannotDuplicateAuthenticatedFriendlyGeometry()
    {
        var shared=new MechSharedTargetTracks();var remote=Track(4,10);remote.Affiliation=TrackAffiliation.Hostile;
        var friend=Track(2,10);friend.Affiliation=TrackAffiliation.Friendly;
        var sender=new LinkTerminalId(new Handle(3,1));shared.Receive(sender,remote,10);
        var output=new TrackSnapshot[8];output[0]=friend;
        Assert.AreEqual(1,shared.Merge(output,1,10));Assert.AreEqual(TrackAffiliation.Friendly,output[0].Affiliation);
        shared.Remove(sender,remote.TrackId);Assert.AreEqual(0,shared.Merge(output,0,10));
    }
    [Test] public void NearbyDifferentMotionIsNotOwnNavigation()
    {
        var t=Track(1,10);t.Position=new double3(10,0,0);t.Velocity=new double3(500,0,0);t.Affiliation=TrackAffiliation.Hostile;
        Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(t,double3.zero,double3.zero,10));
        t.Velocity=double3.zero;t.Position=new double3(60,0,0);Assert.IsFalse(MechSharedTargetTracks.MatchesOwnNavigation(t,double3.zero,double3.zero,10));
    }
    [Test] public void DistinctTargetsFromOneObserverCannotCollapseIntoEachOther()
    {
        var shared=new MechSharedTargetTracks();var a=Track(1,10);var b=Track(2,10);b.Position.x=1;
        var sender=new LinkTerminalId(new Handle(0,1));shared.Receive(sender,a,10);shared.Receive(sender,b,10);
        var output=new TrackSnapshot[8];Assert.AreEqual(2,shared.Merge(output,0,10));Assert.AreNotEqual(output[0].TrackId,output[1].TrackId);
        var first=output[0].TrackId;var second=output[1].TrackId;
        Assert.AreEqual(2,shared.Merge(output,0,10.1));Assert.AreEqual(first,output[0].TrackId);Assert.AreEqual(second,output[1].TrackId);
    }
    [UnityTest] public IEnumerator RealPeerRadarSharesTargetWhileReceiverRadarIsOff()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
        float previous=Time.captureDeltaTime;Time.captureDeltaTime=.04f;
        var target=new GameObject("Remote observed aircraft");var peer=new GameObject("Remote observer");var own=new GameObject("Silent receiver");
        try
        {
            target.transform.position=new Vector3(0,0,8000);var enemy=target.AddComponent<CombatTestTarget>();enemy.ConfigureIdentity("ENEMY",2);enemy.ConfigureSignatures(100,1,1);
            peer.transform.position=new Vector3(100,0,0);peer.AddComponent<CombatTestTarget>().ConfigureIdentity("ALLY",1);var sender=peer.AddComponent<MechRadarController>();
            own.AddComponent<CombatTestTarget>().ConfigureIdentity("OWN",1);var receiver=own.AddComponent<MechRadarController>();receiver.SetRadarPowerEnabled(false);
            var tracks=new TrackSnapshot[96];TrackSnapshot observed=default;float start=Time.time;
            while(Time.time-start<20)
            {
                yield return new WaitForFixedUpdate();int n=receiver.CopyTracks(tracks);
                for(int i=0;i<n;i++)if(tracks[i].Affiliation!=TrackAffiliation.Friendly&&(tracks[i].SourceMask&TrackSourceMask.DataLink)!=0&&math.distance(tracks[i].Position,new double3(0,0,8000))<100)observed=tracks[i];
                if(observed.TrackId.IsValid)break;
            }
            Assert.IsTrue(observed.TrackId.IsValid,"Actual peer sensor and physical radio must deliver the enemy track.");
            Assert.IsFalse(receiver.RadarPowerEnabled);Assert.IsFalse(receiver.SttTrackId.IsValid);
            Assert.AreEqual(MechTargetTrackingMode.DataLink,MechMissileLaunchSource.Resolve(observed,false,false,false,false,receiver.SimTimeSeconds));
            sender.SetRadarPowerEnabled(false);
            while(sender.DataLinkPolicy!=MechDataLinkPolicy.Silent)sender.CycleDataLinkPolicy();
            double expires=receiver.SimTimeSeconds+4;
            while(receiver.SimTimeSeconds<expires)yield return new WaitForFixedUpdate();
            receiver.TryGetTrack(observed.TrackId,out var remaining);
            TestContext.WriteLine("EXPIRE now="+receiver.SimTimeSeconds+" initial="+JsonUtility.ToJson(observed)+" remaining="+JsonUtility.ToJson(remaining));
            Assert.IsFalse(receiver.TryGetTrack(observed.TrackId,out _),"Old report must expire without inventing new observation timestamps.");
        }
        finally{Time.captureDeltaTime=previous;UnityEngine.Object.DestroyImmediate(own);UnityEngine.Object.DestroyImmediate(peer);UnityEngine.Object.DestroyImmediate(target);}
        yield return new ExitPlayMode();
    }
}
