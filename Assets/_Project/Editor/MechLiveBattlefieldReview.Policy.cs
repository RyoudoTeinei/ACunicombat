using System;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
public static partial class MechLiveBattlefieldReview
{
 static int policyStage,policyMapFrames;static double policyAt;static MechFleetController policyWing;static Guid policyAttack;
 public static void RunPolicyReview(){SessionState.SetBool("LiveBattlefieldReview.policy",true);RunRenderingFixReview();}
 static void TickPolicy(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  if(EditorApplication.timeSinceStartup-entered>80)throw new Exception("Policy runtime review timeout at "+policyStage);
  var fleet=rig.GetComponent<MechFleetController>();var radar=rig.GetComponent<MechRadarController>();if(fleet==null||!fleet.Configured||Time.timeAsDouble<12)return;
  if(policyStage==0){fleet.SetFormationMode(FleetAttackMode.Free);policyAt=Time.timeAsDouble;policyStage++;return;}
  if(policyStage==1)
  {
   if(Time.timeAsDouble-policyAt<6)return;
   var friends=UnityEngine.Object.FindObjectsByType<MechFleetController>(FindObjectsInactive.Exclude).Where(f=>!f.IsPlayer&&f.Configured&&f.State.Fleet==fleet.State.Fleet).ToArray();
   foreach(var f in friends)if(f.HasTask&&f.State.Task.Order.AutomaticEngagement)throw new Exception("Out-of-range autonomous attack assigned");
   policyWing=friends.First();var targets=new TrackSnapshot[96];int n=radar.CopyTracks(targets);var enemy=targets.Take(n).First(t=>t.Affiliation!=TrackAffiliation.Friendly&&t.ObjectClass==TrackObjectClass.AirVehicle);
   policyAttack=fleet.Issue(policyWing.MemberId,FleetOrderKind.Attack,enemy);if(policyAttack==Guid.Empty)throw new Exception("Explicit attack not issued");policyAt=Time.timeAsDouble;policyStage++;return;
  }
  if(policyStage==2)
  {
   if(!policyWing.HasTask||policyWing.State.Task.Order.Operation!=policyAttack)return;
   var route=new Unity.Collections.FixedList512Bytes<double3>();route.Add((double3)(float3)(rig.transform.position+Vector3.right*10000));
   if(fleet.RequestRoute(fleet.MemberId,route)==Guid.Empty)throw new Exception("Leader move not issued");policyAt=Time.timeAsDouble;policyStage++;return;
  }
  if(Time.timeAsDouble-policyAt<3)return;
  if(!policyWing.HasTask||policyWing.State.Task.Order.Operation!=policyAttack||policyWing.State.Task.Phase==FleetTaskPhase.Returning)throw new Exception("Leader route interrupted wing attack");
  if(fleet.State.Task.Order.Order!=FleetOrderKind.Move)throw new Exception("Leader did not accept new route");
  map.SetVisible(true);map.Refresh();if(policyMapFrames++<10)return;map.Capture(dir+"/route.png");
  Debug.Log("COMBAT_POLICY_RUNTIME_PASS: outside-range free mode creates no attack; explicit attack accepted; leader new route preserves wing attack");
  SessionState.SetBool("LiveBattlefieldReview.policy",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");EditorApplication.isPlaying=false;
 }
}
