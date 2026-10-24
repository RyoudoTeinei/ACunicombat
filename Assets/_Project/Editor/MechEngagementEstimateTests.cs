using System;
using NUnit.Framework;
using UnityEngine;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
public sealed class MechEngagementEstimateTests
{
 [Test] public void ClosingExtendsReachAndRecedingReducesIt()
 {
  var target=Vector3.forward*100000;
  float closing=MechEngagementEstimate.Reach(Vector3.zero,Vector3.zero,target,Vector3.back*200,100000,150000,1000);
  float retreat=MechEngagementEstimate.Reach(Vector3.zero,Vector3.zero,target,Vector3.forward*200,100000,150000,1000);
  Assert.That(closing,Is.EqualTo(120000).Within(1));Assert.That(retreat,Is.EqualTo(80000).Within(1));
 }
 [Test] public void HeightAndLateralMotionReduceProjectedEnvelope()
 {
  Assert.That(MechEngagementEstimate.HorizontalRadius(50000,30000),Is.EqualTo(40000).Within(1));
  Assert.That(MechEngagementEstimate.Reach(Vector3.zero,Vector3.zero,Vector3.forward*100000,Vector3.right*2000,100000,150000,1000),Is.Zero);
 }
 [Test] public void FiringPointStopsShortAndInsideReachHoldsPosition()
 {
  var p=MechEngagementEstimate.FiringPoint(Vector3.zero,Vector3.forward*100000,50000);
  Assert.That(p.z,Is.EqualTo(52500).Within(1));
  Assert.That(MechEngagementEstimate.FiringPoint(Vector3.zero,Vector3.forward*20000,50000),Is.EqualTo(Vector3.zero));
 }
 [Test] public void SpeedOrderPreservesAttackAndRequiresAuthority()
 {
  var leader=Guid.NewGuid();var member=Guid.NewGuid();var fleet=Guid.NewGuid();
  var state=new MechFleetState(member,fleet,leader,false,false,FleetAttackMode.Free);
  var attack=new FleetPacket{Kind=FleetPacketKind.Order,Sender=leader,Recipient=member,Fleet=fleet,Operation=Guid.NewGuid(),Revision=1,Order=FleetOrderKind.Attack,ObservedAt=1,ExpiresAt=5,TaskDeadline=30};
  Assert.That(state.Receive(attack,1),Is.True);
  var speed=attack;speed.Operation=Guid.NewGuid();speed.Revision=2;speed.Order=FleetOrderKind.SetCruiseSpeed;speed.CruiseSpeed=500;
  Assert.That(state.Receive(speed,1),Is.True);Assert.That(state.CruiseSpeed,Is.EqualTo(500));Assert.That(state.Task.Order.Operation,Is.EqualTo(attack.Operation));
  speed.Sender=Guid.NewGuid();speed.Operation=Guid.NewGuid();speed.Revision=3;speed.CruiseSpeed=0;Assert.That(state.Receive(speed,1),Is.False);Assert.That(state.CruiseSpeed,Is.EqualTo(500));
 }
}
