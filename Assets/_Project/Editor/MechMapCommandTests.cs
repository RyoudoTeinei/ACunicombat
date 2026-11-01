using System;
using NUnit.Framework;
using Unity.Collections;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
public sealed class MechMapCommandTests
{
 [Test] public void RadarPolicyRequiresAuthorityAndPreservesExistingAttackTask()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var fleet=Guid.NewGuid();var state=new MechFleetState(wing,fleet,leader,false,false,FleetAttackMode.Confirmed);
  var attack=new FleetPacket{Kind=FleetPacketKind.Order,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=1,Order=FleetOrderKind.Attack,ObservedAt=1,ExpiresAt=5,TaskDeadline=30};
  Assert.That(state.Receive(attack,1),Is.True);var task=state.Task;
  var policy=attack;policy.Operation=Guid.NewGuid();policy.Revision=2;policy.Order=FleetOrderKind.SetRadarControl;policy.RadarControl=FleetRadarControl.RadiationSilence;
  policy.Sender=Guid.NewGuid();Assert.That(state.Receive(policy,2),Is.False);Assert.That(state.RadarControl,Is.EqualTo(FleetRadarControl.Active));
  policy.Sender=leader;Assert.That(state.Receive(policy,2),Is.True);Assert.That(state.RadarControl,Is.EqualTo(FleetRadarControl.RadiationSilence));Assert.That(state.Task,Is.SameAs(task));
 }
 [Test] public void IntelligentModeIsAcceptedAndPreservesExistingAttackTask()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var fleet=Guid.NewGuid();var state=new MechFleetState(wing,fleet,leader,false,false,FleetAttackMode.Confirmed);FleetPacket reply=default;state.Feedback+=p=>reply=p;
  Assert.That(state.Receive(new FleetPacket{Kind=FleetPacketKind.Order,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=1,Order=FleetOrderKind.Attack,ObservedAt=1,ExpiresAt=5,TaskDeadline=30},1),Is.True);var task=state.Task;
  Assert.That(state.Receive(new FleetPacket{Kind=FleetPacketKind.Order,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=2,Order=FleetOrderKind.SetRadarControl,RadarControl=FleetRadarControl.Intelligent,ObservedAt=2,ExpiresAt=6},2),Is.True);
  Assert.That(reply.Reason,Is.EqualTo(FleetTaskReason.None));Assert.That(state.RadarControl,Is.EqualTo(FleetRadarControl.Intelligent));Assert.That(state.Task,Is.SameAs(task));
 }
 [Test] public void PlatformStatusRemainsBoundToAuthenticatedFreshReport()
 {
  var member=Guid.NewGuid();var fleet=Guid.NewGuid();var state=new MechFleetState(Guid.NewGuid(),fleet,Guid.NewGuid(),true,true,FleetAttackMode.Free);
  var p=new FleetPacket{Kind=FleetPacketKind.Heartbeat,Sender=member,Fleet=fleet,ObservedAt=1,ExpiresAt=3,HasPlatformStatus=true,HealthFraction=.4f,HeadingDegrees=280,RadarControl=FleetRadarControl.Active};
  Assert.That(state.Receive(p,1),Is.True);Assert.That(state.Members[member].HealthFraction,Is.EqualTo(.4f));Assert.That(state.Fresh(p,4),Is.False);
  p.ObservedAt=2;p.HealthFraction=float.NaN;Assert.That(state.Receive(p,2),Is.False);Assert.That(state.Members[member].HealthFraction,Is.EqualTo(.4f));
 }
 [Test] public void WeaponNamesRemainNativePayloadAndConsumeBandwidth()
 {
  var p=new FleetPacket{Kind=FleetPacketKind.Capability,WeaponName=new FixedString512Bytes("24 联装导弹"),Ammunition=24};
  var packets=new NativeArray<FleetPacket>(1,Allocator.Temp);try{packets[0]=p;Assert.That(packets[0].WeaponName.ToString(),Is.EqualTo("24 联装导弹"));}finally{packets.Dispose();}
  Assert.That(p.EstimatedWireKilobits,Is.GreaterThan(2));
 }
 [Test] public void CruiseSpeedUsesSharedDefaultsAndRejectsOutOfRangeValues()
 {
  var leader=Guid.NewGuid();var own=Guid.NewGuid();var state=new MechFleetState(own,Guid.NewGuid(),leader,true,true,FleetAttackMode.Confirmed);
  FleetPacket Speed(float value,int revision)=>new FleetPacket{Kind=FleetPacketKind.Order,Sender=own,Fleet=state.Fleet,Recipient=own,Operation=Guid.NewGuid(),Revision=revision,Order=FleetOrderKind.SetCruiseSpeed,CruiseSpeed=value,ObservedAt=1,ExpiresAt=5};
  Assert.That(state.ReceiveLocalPlayerOrder(Speed(MechFleetState.MaxCruiseSpeed,1),1),Is.True);Assert.That(state.CruiseSpeed,Is.EqualTo(800));
  Assert.That(state.ReceiveLocalPlayerOrder(Speed(MechFleetState.MaxCruiseSpeed*.5f,2),1),Is.True);Assert.That(state.CruiseSpeed,Is.EqualTo(400));
  Assert.That(state.ReceiveLocalPlayerOrder(Speed(MechFleetState.MaxCruiseSpeed+1,3),1),Is.False);Assert.That(state.CruiseSpeed,Is.EqualTo(400));
  Assert.That(state.ReceiveLocalPlayerOrder(Speed(-1,4),1),Is.False);Assert.That(state.CruiseSpeed,Is.EqualTo(400));
  Assert.That(MechFleetState.DefaultCruiseSpeed,Is.EqualTo(300));
 }
 [Test] public void FormationDropMovesLeaderBehindWingWithoutDuplication()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var third=Guid.NewGuid();
  var result=MechBattlefieldMap.FormationOrderAfterDrop(new[]{leader,wing,third},leader,Guid.Empty);
  CollectionAssert.AreEqual(new[]{wing,third,leader},result);Assert.That(new System.Collections.Generic.HashSet<Guid>(result).Count,Is.EqualTo(3));
 }
 [Test] public void FormationDropInsertsWingBeforeNewLeader()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var third=Guid.NewGuid();
  var result=MechBattlefieldMap.FormationOrderAfterDrop(new[]{leader,wing,third},wing,leader);
  CollectionAssert.AreEqual(new[]{wing,leader,third},result);Assert.That(new System.Collections.Generic.HashSet<Guid>(result).Count,Is.EqualTo(3));
 }
 [Test] public void AttackPreviewAcceptsUnknownAndHostileTargetsAllowedByRulesOfEngagement()
 {
  Assert.IsTrue(MechBattlefieldMap.CanPreviewAttackTarget(false,TrackAffiliation.Unknown,default));
  Assert.IsTrue(MechBattlefieldMap.CanPreviewAttackTarget(false,TrackAffiliation.Hostile,new ContactIdentitySnapshot{Available=true,CanAttackPlatform=true}));
 }
 [Test] public void AttackPreviewRejectsFriendlyAndProtectedTargets()
 {
  Assert.IsFalse(MechBattlefieldMap.CanPreviewAttackTarget(false,TrackAffiliation.Friendly,default));
  Assert.IsFalse(MechBattlefieldMap.CanPreviewAttackTarget(false,TrackAffiliation.Unknown,new ContactIdentitySnapshot{Available=true,CanAttackPlatform=false}));
  Assert.IsFalse(MechBattlefieldMap.CanPreviewAttackTarget(true,TrackAffiliation.Hostile,new ContactIdentitySnapshot{Available=true,CanAttackPlatform=true}));
 }
}
