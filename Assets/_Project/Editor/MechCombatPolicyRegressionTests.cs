using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Unity.Mathematics;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
public sealed class MechCombatPolicyRegressionTests
{
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static void Set(object o,string name,object v)=>o.GetType().GetField(name,Private).SetValue(o,v);
 static T Get<T>(object o,string name)=>(T)o.GetType().GetField(name,Private).GetValue(o);
 [TestCase(FleetOrderKind.Move)][TestCase(FleetOrderKind.Return)]
 public void ForwardedMovementDoesNotCancelAttackButDirectOrderCan(FleetOrderKind kind)
 {
  var leader=Guid.NewGuid();var wing=Guid.NewGuid();var fleet=Guid.NewGuid();var state=new MechFleetState(wing,fleet,leader,false,false,FleetAttackMode.Free);
  var attack=new FleetPacket{Kind=FleetPacketKind.Order,Order=FleetOrderKind.AttackWithWeapon,Sender=leader,Recipient=wing,Fleet=fleet,Operation=Guid.NewGuid(),Revision=1,ObservedAt=1,ExpiresAt=10,TaskDeadline=60};
  Assert.That(state.Receive(attack,1),Is.True);
  var move=attack;move.Order=kind;move.WholeFleet=true;move.Operation=Guid.NewGuid();move.Revision=2;move.Waypoints.Add(new double3(1000,0,0));
  Assert.That(state.Receive(move,2),Is.False);Assert.That(state.Task.Order.Operation,Is.EqualTo(attack.Operation));
  move.WholeFleet=false;move.Operation=Guid.NewGuid();move.Revision=3;
  Assert.That(state.Receive(move,3),Is.True);Assert.That(state.Task.Order.Order,Is.EqualTo(kind));
 }
 [Test] public void AutomaticRangeUsesSlantDistanceAndIncludesBoundary()
 {
  Assert.That(MechFleetController.WithinAutomaticAttackRange(Vector3.zero,new Vector3(30000,40000,0),50000),Is.True);
  Assert.That(MechFleetController.WithinAutomaticAttackRange(Vector3.zero,new Vector3(30000,40001,0),50000),Is.False);
  Assert.That(MechFleetController.WithinAutomaticAttackRange(Vector3.zero,Vector3.zero,0),Is.False);
 }
 [TestCase(false)][TestCase(true)]
 public void OnlyOwnshipSchedulesAutomaticReset(bool own)
 {
  var go=new GameObject("reset policy");try{
   var body=go.AddComponent<CombatTestTarget>();Set(body,"structure",1f);Set(body,"armor",0f);Set(body,"isOwnship",own);Set(body,"renderers",Array.Empty<Renderer>());Set(body,"colliders",Array.Empty<Collider>());
   body.TryApplyDamage(100,Vector3.zero,Vector3.forward,default,out _);
   Assert.That(body.IsAlive,Is.False);Assert.That(Get<float>(body,"resetAt")>0,Is.EqualTo(own));
  }finally{UnityEngine.Object.DestroyImmediate(go);}
 }
 [Test] public void RepeatedBulletHitsExtendVisibilityWithoutRestartingFadeIn()
 {
  var shooter=new GameObject("shooter");var target=new GameObject("target");try{
   var hud=shooter.AddComponent<MechRadarHudPresenter>();var body=target.AddComponent<CombatTestTarget>();Set(body,"structure",100f);
   Set(hud,"hitConfirmationStartedAt",Time.unscaledTime-.2f);Set(hud,"hitConfirmationUntil",Time.unscaledTime+1f);
   float began=Get<float>(hud,"hitConfirmationStartedAt");
   for(int i=0;i<5;i++)typeof(MechRadarHudPresenter).GetMethod("HandleDamageApplied",Private).Invoke(hud,new object[]{body,shooter.transform,default(TestWeaponKind),1f});
   Assert.That(Get<float>(hud,"hitConfirmationStartedAt"),Is.EqualTo(began));Assert.That(Get<float>(hud,"hitConfirmationUntil"),Is.GreaterThan(Time.unscaledTime));
  }finally{UnityEngine.Object.DestroyImmediate(shooter);UnityEngine.Object.DestroyImmediate(target);}
 }
}
