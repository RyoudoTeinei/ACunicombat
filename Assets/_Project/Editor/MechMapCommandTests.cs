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
 [Test] public void IntelligentModeIsExplicitlyRejectedNotPretendedActive()
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var fleet=Guid.NewGuid();var state=new MechFleetState(wing,fleet,leader,false,false,FleetAttackMode.Confirmed);FleetPacket reply=default;state.Feedback+=p=>reply=p;
  Assert.That(state.Receive(new FleetPacket{Kind=FleetPacketKind.Order,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=1,Order=FleetOrderKind.SetRadarControl,RadarControl=FleetRadarControl.Intelligent,ObservedAt=1,ExpiresAt=5},1),Is.False);
  Assert.That(reply.Reason,Is.EqualTo(FleetTaskReason.UnsupportedRadarMode));Assert.That(state.RadarControl,Is.EqualTo(FleetRadarControl.Active));
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
}
