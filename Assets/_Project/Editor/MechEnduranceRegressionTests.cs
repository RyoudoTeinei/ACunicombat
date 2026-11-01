using System;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
public class MechEnduranceRegressionTests
{
 const BindingFlags P=BindingFlags.NonPublic|BindingFlags.Instance;
 [Test] public void PulsedLockBuildsSuspicionWithoutBecomingConfirmedHostile()
 {
  var reducer=new MechIdentityEvidence();var id=new TrackId(new Handle(1,1));
  for(int i=0;i<100;i++){
   double now=10+i*.2;var threat=new ThreatSnapshot{Level=EsmThreatLevel.Locked,WaveformClass=EsmWaveformClass.FireControlRadar,MainlobeContactCount=4,LastSeenTime=now,LastMainlobeSeenTime=now-.1,ContinuousMainlobeIlluminationSeconds=.2f};
   Assert.IsTrue(MechIdentityController.IsRadarLockEvidence(threat,now));
   reducer.Submit(id,new IdentityEvidence{OriginalId="lock",Kind=IdentityEvidenceKind.FireControlIllumination,ObservedAt=now,ExpiresAt=now+3,Quality=.75f,Association=.9f},now);
   var state=reducer.Evaluate(id,now);Assert.AreNotEqual(IdentityRelation.Hostile,state.Relation);if(i>=11)Assert.AreEqual(IdentityRelation.SuspectedHostile,state.Relation);
   threat.LastMainlobeSeenTime=now-2;Assert.IsFalse(MechIdentityController.IsRadarLockEvidence(threat,now));
  }
 }
 [Test] public void StrongerWarningDoesNotErasePlatformLockEvidenceButSeekerIsNotAPlatform()
 {
  var t=new ThreatSnapshot{Level=EsmThreatLevel.MissileActive,WaveformClass=EsmWaveformClass.FireControlRadar,MainlobeContactCount=5,LastSeenTime=10,LastMainlobeSeenTime=10};
  Assert.IsTrue(MechIdentityController.IsRadarLockEvidence(t,10));t.WaveformClass=EsmWaveformClass.AirToAirMissileSeeker;Assert.IsFalse(MechIdentityController.IsRadarLockEvidence(t,10));
 }
 [Test] public void ProtectedPlatformListExcludesRawMissilesAndDecoys()
 {
  var go=new GameObject("identity list");try{
   var radar=go.AddComponent<MechRadarController>();var identity=go.AddComponent<MechIdentityController>();
   typeof(MechIdentityController).GetField("<Radar>k__BackingField",P).SetValue(identity,radar);
   var tracks=(TrackSnapshot[])typeof(MechIdentityController).GetField("tracks",P).GetValue(identity);
   var classes=new[]{TrackObjectClass.AirVehicle,TrackObjectClass.Missile,TrackObjectClass.Decoy};
   for(int i=0;i<3;i++)tracks[i]=new TrackSnapshot{TrackId=new TrackId(new Handle(i+1,1)),ObjectClass=classes[i],Affiliation=TrackAffiliation.Friendly,LastUpdateTime=radar.SimTimeSeconds};
   typeof(MechIdentityController).GetField("count",P).SetValue(identity,3);
   typeof(MechIdentityController).GetMethod("BuildProtectedContacts",P).Invoke(identity,null);
   var contacts=(List<ProtectedCombatContact>)typeof(MechIdentityController).GetField("protectedContacts",P).GetValue(identity);
   Assert.AreEqual(2,contacts.Count,"Ownship plus one platform, not raw missile/decoy duplicates");
  }finally{UnityEngine.Object.DestroyImmediate(go);}
 }
 [TestCase(800)] [TestCase(2000)] [TestCase(10000)] public void MissileMarkerAndVisualUseSameCurrentEpoch(float speed)
 {
  double now=MechMissileMarkerPresentation.RenderTime(100,100.01,100);Assert.AreEqual(100.01,now,1e-9);
  var track=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(1,1)),Position=new double3(0,5000,0),Velocity=new double3(0,speed,40),LastUpdateTime=100,FirstObservationTime=99,OwnLaunchIdentity=Guid.NewGuid()};
  var result=new MechMissileMarkerPresentation().Sample(track,now);var expected=(Vector3)(float3)track.PredictPosition(100.01);
  Assert.Less(Vector3.Distance(expected,result.Position),.002f);
 }
}
