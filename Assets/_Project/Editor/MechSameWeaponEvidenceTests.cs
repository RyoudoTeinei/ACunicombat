using System;
using System.Collections.Generic;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
public class MechSameWeaponEvidenceTests
{
 [Test] public void RecordedCloseParallelRoundsAreNotEvidenceOfAnEquipmentAttack()
 {
  var position=new Vector3(7721.26953f,-9962.885f,33077.72f);var velocity=new Vector3(-14.2200918f,930.9754f,-6.19673729f);
  var contact=new ProtectedCombatContact{SensorTrack=new TrackId(new Handle(2003,6)),Position=new Vector3(7722.09375f,-9963.847f,33076.957f),Velocity=new Vector3(-17.4310646f,965.273438f,-21.9148464f),Radius=8,Kind=ProtectedContactKind.FriendlyEquipment};
  Assert.IsFalse(MechCombatBehaviorTracker.ApproachesProtected(position,velocity,.2f,contact,120));
  Assert.AreEqual(-1,MechCombatBehaviorTracker.DirectedVictim(position,velocity,.2f,new[]{contact}));
  contact.Position=Vector3.zero;contact.Velocity=Vector3.zero;
  Assert.IsTrue(MechCombatBehaviorTracker.ApproachesProtected(Vector3.forward*100,Vector3.back*500,1,contact,120),"Real approach from outside still confirms.");
 }
 [Test] public void RealImpactOnEquipmentStillConfirmsWithoutApproachHistory()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;
  var source=new TrackSnapshot{TrackId=new TrackId(new Handle(1,1)),Position=new double3(0,0,1000),Confidence=1,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle};for(int i=0;i<6;i++)source.SetCovariance(i,i,25);
  var id=Guid.NewGuid();Assert.IsTrue(tracker.Launch(id,true,Vector3.forward*1000,0,1,new[]{source},1,0));
  tracker.Impact(id,Vector3.zero,.1,new[]{new ProtectedCombatContact{SensorTrack=new TrackId(new Handle(6,1)),Position=Vector3.zero,Radius=8,Kind=ProtectedContactKind.FriendlyEquipment}});
  Assert.AreEqual(CombatBehaviorKind.EquipmentAttack,last.Kind);
 }
 [TestCase(true,false,false,false)] [TestCase(false,false,false,false)]
 [TestCase(false,true,false,false)] [TestCase(false,false,true,false)] [TestCase(false,false,false,true)]
 public void BoundMissileCannotBeItsOwnProtectedVictim(bool sameSensorContact,bool sensorAlias,bool rebindCanonical,bool sharedLaunch)
 {
  var sourceId=new TrackId(new Handle(1,1));var source=new TrackSnapshot{TrackId=sourceId,Position=new double3(0,0,1000),Confidence=1,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle};for(int i=0;i<6;i++)source.SetCovariance(i,i,25);
  var reducer=new MechIdentityEvidence();reducer.Submit(sourceId,new IdentityEvidence{OriginalId="auth",Kind=IdentityEvidenceKind.FriendlyAuthentication,ObservedAt=0,ExpiresAt=10,Quality=1,Association=1},0);
  var tracker=new MechCombatBehaviorTracker();var reports=new List<CombatBehaviorReport>();tracker.Report+=r=>{reports.Add(r);if(r.Kind==CombatBehaviorKind.EquipmentAttack)reducer.Submit(sourceId,new IdentityEvidence{OriginalId="attack",Kind=IdentityEvidenceKind.ObservedAttack,ObservedAt=r.ObservedAt,ExpiresAt=10,Quality=r.Quality,Association=r.Association},r.ObservedAt);};
  var observation=Guid.NewGuid();Assert.IsTrue(tracker.Launch(observation,true,Vector3.forward*1000,0,1,new[]{source},1,0));
  tracker.Sample(observation,Vector3.forward*900,.1,1,Array.Empty<ProtectedCombatContact>());
  var launch=sharedLaunch?Guid.NewGuid():Guid.Empty;
  var track=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(5,1)),OwnLaunchIdentity=launch,Position=new double3(0,0,900),Velocity=new double3(0,0,-1000),LastEvidenceTime=.1,LastUpdateTime=.1,PositionUncertaintyMeters=5,Confidence=1,Quality=MissileTrackQuality.Warning,Affiliation=TrackAffiliation.Friendly,IdentityConfidence=1};
  tracker.FollowSensorTracks(new[]{track},1,.1,Array.Empty<ProtectedCombatContact>());
  if(rebindCanonical){track.AssociatedSensorTrackId=track.TrackId;track.TrackId=new TrackId(new Handle(6,1));tracker.FollowSensorTracks(new[]{track},1,.1,Array.Empty<ProtectedCombatContact>());}
  for(int i=2;i<=3;i++){
   float position=1000-i*100;var contact=new ProtectedCombatContact{SensorTrack=new TrackId(new Handle(sameSensorContact?5:6,1)),AssociatedSensorTrack=sensorAlias?new TrackId(new Handle(5,1)):default,LaunchIdentity=launch,Position=Vector3.forward*(position-10),Velocity=Vector3.back*900,Radius=8,Kind=ProtectedContactKind.FriendlyEquipment};
   tracker.Sample(observation,Vector3.forward*position,i*.1,1,new[]{contact});
  }
  bool same=sameSensorContact||sensorAlias||rebindCanonical||sharedLaunch;
  Assert.AreEqual(!same,reports.Exists(r=>r.Kind==CombatBehaviorKind.EquipmentAttack));
  Assert.AreEqual(IdentityRelation.Friendly,reducer.Evaluate(sourceId,.3).Relation);
 }
}
