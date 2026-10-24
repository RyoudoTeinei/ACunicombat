using System;
using System.Collections;
using System.Collections.Generic;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public sealed class MechCombatBehaviorTests
{
 static TrackSnapshot Source(int index=1){var t=new TrackSnapshot{TrackId=new TrackId(new Handle(index,1)),Position=new double3(0,0,1000),Confidence=1,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle};for(int i=0;i<6;i++)t.SetCovariance(i,i,25);return t;}
 static ProtectedCombatContact Protected(ProtectedContactKind kind=ProtectedContactKind.Ownship)=>new ProtectedCombatContact{Position=Vector3.zero,Velocity=Vector3.zero,Radius=25,Kind=kind};
 [Test] public void MotorRadianceExtendsReferenceRangeAndColdSourceDoesNot()
 {
  Assert.That(MechCombatRadiometry.TryMeasure(94000,58,32000,1,.003f,out float hotError),Is.True);
  Assert.That(MechCombatRadiometry.TryMeasure(94000,28,32000,1,.003f,out _),Is.False);
  Assert.That(MechCombatRadiometry.TryMeasure(94000,58,32000,0,.003f,out _),Is.False);
  MechCombatRadiometry.TryMeasure(94000,58,64000,1,.003f,out float improvedError);Assert.That(improvedError,Is.LessThan(hotError));
 }
 [Test] public void LateAcquisitionRequiresObservedSeparationBeforeAttribution()
 {
  var tracker=new MechCombatBehaviorTracker();var reports=new List<CombatBehaviorReport>();tracker.Report+=reports.Add;var id=Guid.NewGuid();
  Assert.That(tracker.Acquire(id,true,Vector3.forward*980,0,1,new[]{Source()},1,0),Is.True);Assert.That(reports,Is.Empty);
  tracker.Sample(id,Vector3.forward*900,.1,1,new[]{Protected()});Assert.That(reports,Is.Empty);
  tracker.Sample(id,Vector3.forward*800,.2,1,new[]{Protected()});tracker.Sample(id,Vector3.forward*700,.3,1,new[]{Protected()});
  Assert.That(reports.Exists(r=>r.Kind==CombatBehaviorKind.MissileAttack),Is.True);
 }
 [Test] public void DistantOrAmbiguousLateMissileCannotInventLauncher()
 {
  var tracker=new MechCombatBehaviorTracker();
  Assert.That(tracker.Acquire(Guid.NewGuid(),true,Vector3.forward*500,0,1,new[]{Source()},1,0),Is.False);
  Assert.That(tracker.Acquire(Guid.NewGuid(),true,Vector3.forward*980,0,1,new[]{Source(1),Source(2)},2,0),Is.False);
 }
 [Test] public void LongRangeDirectedMissileAttackDoesNotRequireExactFutureCollision()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();var source=Source();source.Position=new double3(0,0,94000);
  Assert.That(tracker.Launch(id,true,Vector3.forward*94000,0,1,new[]{source},1,0),Is.True);
  for(int i=1;i<=8;i++)tracker.Sample(id,new Vector3(i*10,0,94000-i*200),i*.2,30,new[]{Protected()});
  Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.MissileAttack));
 }
 [Test] public void DirectionOnlyCannotBlameAnAttackOnAnotherCollinearTarget()
 {
  var contacts=new[]{Protected(),new ProtectedCombatContact{Position=Vector3.forward*500,Radius=8,Kind=ProtectedContactKind.UnprotectedEquipment}};
  Assert.That(MechCombatBehaviorTracker.DirectedVictim(Vector3.forward*1000,Vector3.back*500,1,contacts),Is.EqualTo(-1));
  Assert.That(MechCombatBehaviorTracker.DirectedVictim(Vector3.forward*1000,Vector3.right*500,1,new[]{Protected()}),Is.EqualTo(-1));
 }
 [Test] public void SensorContinuationNeedsUniqueFreshIntentAndDoesNotInventUnseenLaunch()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;
  var id=Guid.NewGuid();var t=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(5,1)),Position=new double3(0,0,800),Velocity=new double3(0,0,-1000),Quality=MissileTrackQuality.Warning,LastEvidenceTime=.2,LastUpdateTime=.2,Confidence=1,TargetIntent=MissileTargetIntent.Ownship,TargetIntentConfidence=.9f,PositionUncertaintyMeters=5};
  tracker.FollowSensorTracks(new[]{t},1,.2,new[]{Protected()});Assert.That(last.OriginalId,Is.EqualTo(Guid.Empty));
  tracker.Launch(id,true,Vector3.forward*1000,0,1,new[]{Source()},1,0);tracker.Sample(id,Vector3.forward*900,.1,1,Array.Empty<ProtectedCombatContact>());
  tracker.FollowSensorTracks(new[]{t},1,.2,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch));
  tracker.FollowSensorTracks(new[]{t},1,.3,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch),"Repeated evidence is not a second observation");
  t.LastEvidenceTime=.4;t.LastUpdateTime=.4;t.Position=new double3(0,0,600);tracker.FollowSensorTracks(new[]{t},1,.4,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.MissileAttack));
 }
 [TestCase(true,CombatBehaviorKind.MissileAttack)] [TestCase(false,CombatBehaviorKind.GunfireAttack)]
 public void ObservedLaunchAndApproachingTrajectoryConfirmBeforeImpact(bool missile,CombatBehaviorKind expected)
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();
  Assert.That(tracker.Launch(id,missile,Vector3.forward*1000,0,1,new[]{Source()},1,0),Is.True);
  Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch));
  tracker.Sample(id,Vector3.forward*900,.1,1,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch));
  tracker.Sample(id,Vector3.forward*800,.2,1,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(expected));Assert.That(last.SourceTrack.TrackId,Is.EqualTo(Source().TrackId));
 }
 [TestCase(ProtectedContactKind.FriendlyPlatform,CombatBehaviorKind.MissileAttack)]
 [TestCase(ProtectedContactKind.FriendlyEquipment,CombatBehaviorKind.EquipmentAttack)]
 [TestCase(ProtectedContactKind.MissionProtected,CombatBehaviorKind.ProtectedAssetAttack)]
 public void ObservedAttackProtectsAlliedAndMissionContacts(ProtectedContactKind kind,CombatBehaviorKind expected)
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();tracker.Launch(id,true,Vector3.forward*1000,0,1,new[]{Source()},1,0);
  tracker.Sample(id,Vector3.forward*900,.1,1,new[]{Protected(kind)});tracker.Sample(id,Vector3.forward*800,.2,1,new[]{Protected(kind)});Assert.That(last.Kind,Is.EqualTo(expected));
 }
 [Test] public void NearPassDepartingAndUncertainTrajectoriesDoNotConfirmAttack()
 {
  Assert.That(MechCombatBehaviorTracker.ApproachesProtected(new Vector3(100,0,1000),Vector3.back*1000,1,Protected(),120),Is.False);
  Assert.That(MechCombatBehaviorTracker.ApproachesProtected(Vector3.forward*1000,Vector3.forward*1000,1,Protected(),120),Is.False);
  Assert.That(MechCombatBehaviorTracker.ApproachesProtected(Vector3.forward*1000,Vector3.back*1000,50,Protected(),120),Is.False);
 }
 [Test] public void MissingLaunchAndAmbiguousSourcesCannotBlameNearbyPlatform()
 {
  var tracker=new MechCombatBehaviorTracker();int reports=0;tracker.Report+=r=>reports++;
  var id=Guid.NewGuid();tracker.Sample(id,Vector3.forward*100,.1,1,new[]{Protected()});Assert.That(reports,Is.Zero);
  Assert.That(tracker.Launch(id,true,Vector3.forward*1000,0,1,new[]{Source(1),Source(2)},2,0),Is.False);Assert.That(reports,Is.Zero);
 }
 [Test] public void LongOcclusionBreaksCausalWeaponHistory()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();tracker.Launch(id,true,Vector3.forward*1000,0,1,new[]{Source()},1,0);
  tracker.Sample(id,Vector3.forward*100,1,1,new[]{Protected()});tracker.Impact(id,Vector3.zero,1.1,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch));
 }
 [Test] public void ObservedWeaponHitIsSufficientWithoutRequiringSecondHit()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();tracker.Launch(id,false,Vector3.forward*1000,0,1,new[]{Source()},1,0);
  tracker.Impact(id,Vector3.zero,.1,new[]{Protected()});Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponImpact));
 }
 [TestCase(CombatBehaviorKind.FireControlLock)] [TestCase(CombatBehaviorKind.LaserIllumination)] [TestCase(CombatBehaviorKind.DirectedJamming)] [TestCase(CombatBehaviorKind.WeaponLaunch)] [TestCase(CombatBehaviorKind.InvalidFriendlyAuthentication)]
 public void SuspiciousBehaviorsDoNotDirectlyAuthorizeHostileFire(CombatBehaviorKind kind)
 {
  var reducer=new MechIdentityEvidence();var track=Source().TrackId;reducer.Submit(track,new IdentityEvidence{OriginalId="event",Kind=MechIdentityController.BehaviorEvidenceKind(kind),Quality=.9f,Association=1,ObservedAt=0,ExpiresAt=10},0);
  Assert.That(reducer.Evaluate(track,0).Relation,Is.EqualTo(IdentityRelation.Unknown));var state=reducer.Evaluate(track,3);Assert.That(state.Relation,Is.EqualTo(IdentityRelation.SuspectedHostile));Assert.That(state.CanAttackPlatform,Is.False);
 }
 [Test] public void AuthenticatedFriendlyLaunchAloneDoesNotTurnYellowButAttackStillTurnsRed()
 {
  var reducer=new MechIdentityEvidence();var track=Source().TrackId;
  reducer.Submit(track,new IdentityEvidence{OriginalId="friend",Kind=IdentityEvidenceKind.FriendlyAuthentication,Quality=1,Association=1,ObservedAt=0,ExpiresAt=30},0);
  reducer.Submit(track,new IdentityEvidence{OriginalId="launch",Kind=IdentityEvidenceKind.WeaponLaunch,Quality=.9f,Association=1,ObservedAt=0,ExpiresAt=10},0);
  reducer.Evaluate(track,0);Assert.That(reducer.Evaluate(track,3).Relation,Is.EqualTo(IdentityRelation.Friendly));
  reducer.Submit(track,new IdentityEvidence{OriginalId="attack",Kind=IdentityEvidenceKind.ObservedAttack,Quality=1,Association=1,ObservedAt=4,ExpiresAt=15},4);
  var result=reducer.Evaluate(track,4);Assert.That(result.Relation,Is.EqualTo(IdentityRelation.Hostile));Assert.That(result.Conflict,Is.True);
 }
 [Test] public void TrustedCommandAndConfirmedFriendlyAttackOverrideFriendlyRelation()
 {
  foreach(var kind in new[]{IdentityEvidenceKind.MissionHostile,IdentityEvidenceKind.ObservedAttack})
  {var reducer=new MechIdentityEvidence();var track=Source().TrackId;reducer.Submit(track,new IdentityEvidence{OriginalId="friend",Kind=IdentityEvidenceKind.FriendlyAuthentication,Quality=1,Association=1,ObservedAt=0,ExpiresAt=30},0);
   reducer.Submit(track,new IdentityEvidence{OriginalId="attack",Kind=kind,Quality=1,Association=1,ObservedAt=0,ExpiresAt=1},0);var result=reducer.Evaluate(track,0);
   Assert.That(result.Relation,Is.EqualTo(IdentityRelation.Hostile));Assert.That(result.Conflict,Is.True);Assert.That(result.CanAttackPlatform,Is.True);Assert.That(reducer.Evaluate(track,10).Relation,Is.EqualTo(IdentityRelation.Hostile));}
 }
 [Test] public void ExpiredFutureAndUnauthorizedCommanderReportsAreRejected()
 {
  var r=new CombatBehaviorReport{OriginalId=Guid.NewGuid(),Kind=CombatBehaviorKind.CommandHostile,ObservedAt=1,ExpiresAt=5};
  Assert.That(MechIdentityController.IsCurrentTrustedBehaviorReport(r,2,false),Is.False);Assert.That(MechIdentityController.IsCurrentTrustedBehaviorReport(r,2,true),Is.True);
  Assert.That(MechIdentityController.IsCurrentTrustedBehaviorReport(r,6,true),Is.False);Assert.That(MechIdentityController.IsCurrentTrustedBehaviorReport(r,0,true),Is.False);
 }
 [Test] public void ShootingAnObservedIncomingEnemyMissileDoesNotBlameTheDefender()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();tracker.Launch(id,false,Vector3.forward*1000,0,1,new[]{Source()},1,0);
  var contacts=new[]{Protected(),new ProtectedCombatContact{Position=Vector3.forward*500,Radius=8,Kind=ProtectedContactKind.UnprotectedEquipment}};
  tracker.Sample(id,Vector3.forward*900,.1,1,contacts);tracker.Sample(id,Vector3.forward*800,.2,1,contacts);tracker.Impact(id,Vector3.forward*500,.3,contacts);
  Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch));
 }
 [Test] public void MovingProtectedContactUsesRelativeMotion()
 {
  var moving=Protected();moving.Velocity=Vector3.right*100;
  Assert.That(MechCombatBehaviorTracker.ApproachesProtected(new Vector3(100,0,1000),Vector3.back*1000,1,moving,120),Is.True);
  moving.Velocity=Vector3.left*100;Assert.That(MechCombatBehaviorTracker.ApproachesProtected(new Vector3(100,0,1000),Vector3.back*1000,1,moving,120),Is.False);
 }
 [Test] public void FriendlyWeaponCannotBeItsOwnProtectedVictim()
 {
  var tracker=new MechCombatBehaviorTracker();CombatBehaviorReport last=default;tracker.Report+=r=>last=r;var id=Guid.NewGuid();tracker.Launch(id,true,Vector3.forward*1000,0,1,new[]{Source()},1,0);
  tracker.Sample(id,Vector3.forward*900,.1,1,new[]{new ProtectedCombatContact{Position=Vector3.forward*899.5f,Velocity=Vector3.back*999,Radius=8,Kind=ProtectedContactKind.FriendlyEquipment}});
  tracker.Sample(id,Vector3.forward*800,.2,1,new[]{new ProtectedCombatContact{Position=Vector3.forward*799.5f,Velocity=Vector3.back*999,Radius=8,Kind=ProtectedContactKind.FriendlyEquipment}});
  Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponLaunch));
 }
 [Test] public void CorrectedHostilityRejectsOldForwardedAttackButAcceptsNewAttack()
 {
  var f=new MechIdentityEvidence();var id=Source().TrackId;
  var evidence=new IdentityEvidence{OriginalId="old-shot",Kind=IdentityEvidenceKind.ObservedAttack,Quality=1,Association=1,ObservedAt=0,ExpiresAt=100};
  f.Submit(id,evidence,0);f.Evaluate(id,0);f.CorrectHostility(id,10);
  Assert.That(f.Submit(id,evidence,11),Is.False);Assert.That(f.Evaluate(id,11).Relation,Is.EqualTo(IdentityRelation.Unknown));
  evidence.OriginalId="new-shot";evidence.ObservedAt=12;Assert.That(f.Submit(id,evidence,12),Is.True);Assert.That(f.Evaluate(id,12).Relation,Is.EqualTo(IdentityRelation.Hostile));
 }
 [Test] public void FriendlyRadarLockCreatesSuspicionAndConflictNotConfirmedHostility()
 {
  var f=new MechIdentityEvidence{AllowUnknownPlatformAttack=true};var id=Source().TrackId;
  f.Submit(id,new IdentityEvidence{OriginalId="auth",Kind=IdentityEvidenceKind.FriendlyAuthentication,Quality=1,Association=1,ObservedAt=0,ExpiresAt=20},0);
  f.Submit(id,new IdentityEvidence{OriginalId="lock",Kind=IdentityEvidenceKind.FireControlIllumination,Quality=1,Association=1,ObservedAt=0,ExpiresAt=20},0);
  f.Evaluate(id,0);var state=f.Evaluate(id,2);Assert.That(state.Relation,Is.EqualTo(IdentityRelation.SuspectedHostile));Assert.That(state.Conflict,Is.True);Assert.That(state.CanAttackPlatform,Is.False);
 }
 [Test] public void TrainingPermissionDoesNotOscillateWhenUnknownBecomesSuspicious()
 {
  var f=new MechIdentityEvidence{AllowUnknownPlatformAttack=true};var id=Source().TrackId;Assert.That(f.Evaluate(id,0).CanAttackPlatform,Is.True);
  f.Submit(id,new IdentityEvidence{OriginalId="lock",Kind=IdentityEvidenceKind.FireControlIllumination,Quality=1,Association=1,ObservedAt=0,ExpiresAt=20},0);f.Evaluate(id,0);
  Assert.That(f.Evaluate(id,2).CanAttackPlatform,Is.True);
 }
 [Test] public void CommunicationAndUnknownEmittersCannotBeMistakenForRadarLock()
 {
  var t=new ThreatSnapshot{Level=EsmThreatLevel.Locked,ContinuousMainlobeIlluminationSeconds=2,LastSeenTime=1,WaveformClass=EsmWaveformClass.Unknown};
  Assert.That(MechIdentityController.IsRadarLockEvidence(t,1),Is.False);t.WaveformClass=EsmWaveformClass.DataLinkUplink;Assert.That(MechIdentityController.IsRadarLockEvidence(t,1),Is.False);
  t.WaveformClass=EsmWaveformClass.FireControlRadar;Assert.That(MechIdentityController.IsRadarLockEvidence(t,1),Is.True);Assert.That(MechIdentityController.IsRadarLockEvidence(t,2),Is.False);
  t.Level=EsmThreatLevel.Tracked;Assert.That(MechIdentityController.IsRadarLockEvidence(t,1),Is.False);
 }
 [Test] public void ObservedImpactUsesCurrentOwnPositionAfterMovement()
 {
  var go=new GameObject("Moving observed victim");go.SetActive(false);
  try
  {
   var radar=go.AddComponent<MechRadarController>();var controller=go.AddComponent<MechIdentityController>();controller.LogDecisions=false;
   const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
   typeof(MechIdentityController).GetField("<Radar>k__BackingField",flags).SetValue(controller,radar);
   var tracker=(MechCombatBehaviorTracker)typeof(MechIdentityController).GetField("combat",flags).GetValue(controller);CombatBehaviorReport last=default;tracker.Report+=r=>last=r;
   var id=Guid.NewGuid();tracker.Launch(id,false,Vector3.forward*1000,0,1,new[]{Source()},1,0);
   go.transform.position=Vector3.right*100;controller.ObserveWeaponImpact(id,go.transform.position,.1);
   Assert.That(last.Kind,Is.EqualTo(CombatBehaviorKind.WeaponImpact));
  }
  finally{UnityEngine.Object.DestroyImmediate(go);}
 }
 private sealed class Observer:ICombatObservationReceiver
 {
  public readonly MechCombatBehaviorTracker Tracker=new MechCombatBehaviorTracker();public bool Visible=true;public CombatBehaviorReport Last;public string Trace="";
  public Observer(){Tracker.Report+=r=>Last=r;}
  public bool TryObserveCombatPoint(Vector3 p,bool missile,bool launch,out Vector3 m,out float e){m=p;e=.1f;return Visible;}
  public void ObserveWeaponLaunch(Guid id,bool missile,Vector3 p,double time,float e){var t=Source();t.LastUpdateTime=t.LastPredictTime=time;Tracker.Launch(id,missile,p,time,e,new[]{t},1,time);}
  public void ObserveWeaponSample(Guid id,Vector3 p,double time,float e){Trace+=$"t={time:F4} p={p} count={Tracker.Count}; ";Tracker.Sample(id,p,time,e,new[]{Protected()});}
  public void ObserveWeaponImpact(Guid id,Vector3 p,double time)=>Tracker.Impact(id,p,time,new[]{Protected()});
 }
 [UnityTest] public IEnumerator RealMissileMotionProducesAttackEvidenceBeforeImpactWithoutOwnerIdentity()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  var owner=new GameObject("Unexposed launcher");owner.transform.position=Vector3.forward*1000;
  var shot=new GameObject("Actual missile");shot.transform.position=Vector3.forward*1000;shot.transform.forward=Vector3.back;var observer=new Observer();MechCombatObservationSensor.Register(observer);
  try
  {
   var missile=shot.AddComponent<MechTestProjectile>();missile.Configure(owner.transform,null,default,TestWeaponKind.Missile,Vector3.back*1000,10,.1f,5,newSeekerActivationDelay:100);
   double deadline=Time.timeAsDouble+.65;while(Time.timeAsDouble<deadline&&observer.Last.Kind!=CombatBehaviorKind.MissileAttack){yield return null;MechCombatObservationSensor.Poll(observer,Time.timeAsDouble);}
   Assert.That(observer.Last.Kind,Is.EqualTo(CombatBehaviorKind.MissileAttack),observer.Trace+" velocity="+missile.Velocity);Assert.That(shot.transform.position.z,Is.GreaterThan(100));
  }
  finally{MechCombatObservationSensor.Unregister(observer);UnityEngine.Object.Destroy(shot);UnityEngine.Object.Destroy(owner);}
  yield return new ExitPlayMode();
 }
 sealed class DelayedHeatObserver : ICombatObservationReceiver,ICombatRadiometricReceiver
 {
  public readonly MechCombatBehaviorTracker Tracker=new MechCombatBehaviorTracker();public CombatBehaviorReport Last;public int Launches,Acquisitions;
  public DelayedHeatObserver(){Tracker.Report+=r=>Last=r;}
  TrackSnapshot Launcher(double time){var t=Source();t.Position=new double3(0,0,94000);t.LastPredictTime=t.LastUpdateTime=time;return t;}
  public bool TryObserveCombatPoint(Vector3 p,bool missile,bool launch,out Vector3 measured,out float error)=>TryObserveCombatRadiance(p,28,launch,out measured,out error);
  public bool TryObserveCombatRadiance(Vector3 p,float signature,bool launch,out Vector3 measured,out float error){measured=p;return MechCombatRadiometry.TryMeasure(p.magnitude,signature,32000,1,.003f,out error);}
  public void ObserveWeaponLaunch(Guid id,bool missile,Vector3 p,double time,float error){Launches++;Tracker.Launch(id,missile,p,time,error,new[]{Launcher(time)},1,time);}
  public void ObserveWeaponAcquisition(Guid id,bool missile,Vector3 p,double time,float error){Acquisitions++;Tracker.Acquire(id,missile,p,time,error,new[]{Launcher(time)},1,time);}
  public void ObserveWeaponSample(Guid id,Vector3 p,double time,float error)=>Tracker.Sample(id,p,time,error,new[]{Protected()});
  public void ObserveWeaponImpact(Guid id,Vector3 p,double time)=>Tracker.Impact(id,p,time,new[]{Protected()});
 }
 [UnityTest] public IEnumerator RealColdLaunchAt94KmIsAcquiredAfterMotorIgnition()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  var owner=new GameObject("Cold source");owner.transform.position=Vector3.forward*94000;var shot=new GameObject("Cold missile");shot.transform.position=owner.transform.position;shot.transform.forward=Vector3.back;
  var observer=new DelayedHeatObserver();MechCombatObservationSensor.Register(observer);
  try
  {
   var missile=shot.AddComponent<MechTestProjectile>();missile.Configure(owner.transform,null,default,TestWeaponKind.Missile,Vector3.back*100,10,.1f,10,newMotorBurnDuration:5,newSeekerActivationDelay:100,hasInitialGuidanceEstimate:true,initialGuidancePosition:Vector3.zero,initialGuidanceUncertaintyMeters:1,newColdLaunch:true,newColdLaunchSafeSeparationSeconds:.2f,newColdLaunchSafeSeparationMeters:10);
   Assert.That(observer.Launches,Is.Zero,"Cold ejection is below detection threshold at this range");
   double deadline=Time.timeAsDouble+3;while(Time.timeAsDouble<deadline&&observer.Last.Kind!=CombatBehaviorKind.MissileAttack){yield return null;MechCombatObservationSensor.Poll(observer,Time.timeAsDouble);}
   Assert.That(observer.Acquisitions,Is.GreaterThan(0));Assert.That(observer.Last.Kind,Is.EqualTo(CombatBehaviorKind.MissileAttack));Assert.That(observer.Launches,Is.Zero,"Acquisition must not masquerade as a witnessed launch");
  }
  finally{MechCombatObservationSensor.Unregister(observer);UnityEngine.Object.Destroy(shot);UnityEngine.Object.Destroy(owner);}
  yield return new ExitPlayMode();
 }
 [UnityTest] public IEnumerator UnobservedRealLaunchDoesNotCreateHostility()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  var owner=new GameObject("Hidden launcher");var shot=new GameObject("Unseen round");shot.transform.position=Vector3.forward*1000;var observer=new Observer{Visible=false};MechCombatObservationSensor.Register(observer);
  try
  {
   var projectile=shot.AddComponent<MechTestProjectile>();projectile.Configure(owner.transform,null,default,TestWeaponKind.Rifle,Vector3.back*1000,10,.1f,5);
   observer.Visible=true;double deadline=Time.timeAsDouble+.2;while(Time.timeAsDouble<deadline){yield return null;MechCombatObservationSensor.Poll(observer,Time.timeAsDouble);}
   Assert.That(observer.Tracker.Count,Is.Zero);
  }
  finally{MechCombatObservationSensor.Unregister(observer);UnityEngine.Object.Destroy(shot);UnityEngine.Object.Destroy(owner);}
  yield return new ExitPlayMode();
 }
}
