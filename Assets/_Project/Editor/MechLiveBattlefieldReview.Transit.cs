using System;
using System.Linq;
using System.Collections.Generic;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEditor;
using Unity.Mathematics;
public static partial class MechLiveBattlefieldReview
{
 static double transitFirstGunShot=-1;
 static bool transitOrdered;static readonly Dictionary<Guid,int> transitAmmo=new();
 public static void RunTransitReview()
 {
  SessionState.SetBool("LiveBattlefieldReview.transit",true);RunRenderingFixReview();
  var rigs=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include);
  var own=rigs.First(r=>!r.AiControlled);var enemy=rigs.First(r=>r.AiControlled&&r.GetComponent<CombatTestTarget>().TeamId!=own.GetComponent<CombatTestTarget>().TeamId);
  // Unsaved test placement: an observed target inside gun range, not a forced fire solution.
  enemy.transform.position=own.transform.position+own.transform.forward*8000+Vector3.up*500;
 }
 static void TickTransit(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  var fleet=rig.GetComponent<MechFleetController>();var weapons=rig.GetComponent<MechTestWeaponSystem>();var radar=rig.GetComponent<MechRadarController>();
  if(fleet==null||!fleet.Configured||Time.timeAsDouble<12)return;
  var equipment=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(equipment);
  if(!transitOrdered)
  {
   foreach(var e in equipment)if(MechAiRulePlanner.IsOffensiveRole(e.Role))transitAmmo[e.InstanceId]=e.Rounds;
   fleet.SetFormationMode(FleetAttackMode.Free);radar.SetTrackOperationMode(false);
   var route=new Unity.Collections.FixedList512Bytes<double3>();route.Add((double3)(float3)(rig.transform.position+rig.transform.forward*20000+rig.transform.right*5000));route.Add((double3)(float3)(rig.transform.position+rig.transform.forward*30000));
   if(fleet.RequestRoute(fleet.MemberId,route)==Guid.Empty)throw new Exception("Transit route rejected");transitOrdered=true;return;
  }
  bool fired=equipment.Any(e=>transitAmmo.TryGetValue(e.InstanceId,out int start)&&e.Rounds<start);
  if(Environment.GetEnvironmentVariable("ACUNICOMBAT_REQUIRE_GUN_BURST")=="1")
  {
   int rounds=equipment.Where(e=>e.Role==MechWeaponRole.AutomaticCannon&&transitAmmo.ContainsKey(e.InstanceId)).Sum(e=>Math.Max(0,transitAmmo[e.InstanceId]-e.Rounds));
   if(rounds>0&&transitFirstGunShot<0)transitFirstGunShot=radar.SimTimeSeconds;
   if(transitFirstGunShot>=0&&radar.SimTimeSeconds-transitFirstGunShot>1.3&&rounds<4)throw new Exception("Automatic gun still throttled below four rounds per burst window");
   fired=rounds>=4;
   if(fired){if(radar.TrackOperationMode)throw new Exception("Autopilot gun enabled player Tab mode");Debug.Log("AUTOPILOT_GUN_BURST_PASS rounds="+rounds+" interval="+(radar.SimTimeSeconds-transitFirstGunShot));}
  }
  if(!fired&&EditorApplication.timeSinceStartup-entered<80)return;
  if(!fired)throw new Exception("No opportunistic fire during transit");
  if(!fleet.HasTask||fleet.State.Task.Order.Order!=FleetOrderKind.Move||fleet.State.Task.Order.Waypoints.Length!=2)throw new Exception("Opportunistic fire replaced route");
  if(Environment.GetEnvironmentVariable("ACUNICOMBAT_REQUIRE_GUN_BURST")=="1")
  {
   if(!fleet.TryGetOpportunisticTrack(out var good))throw new Exception("No valid target after gun burst");
   var bad=good;bad.Position=(double3)(float3)(rig.transform.position+rig.transform.forward*200);bad.LastPredictTime=radar.SimTimeSeconds;
   for(int axis=0;axis<6;axis++)bad.SetCovariance(axis,axis,1e12);
   bool verified=false;
   foreach(var e in equipment)
   {
    if(!MechAiRulePlanner.IsOffensiveRole(e.Role)||!weapons.PreviewOffensiveTarget(e,good,out var goodReady))continue;
    if(weapons.PreviewOffensiveTarget(e,bad,out _))throw new Exception("Invalid close track passed weapon preview");
    verified=true;break;
   }
   if(!verified)throw new Exception("No actual weapon validated target pair");
   Debug.Log("AUTOPILOT_TARGET_PAIR_PASS: actual weapon accepts observed farther target and rejects closer imprecise candidate");
  }
  Debug.Log("TRANSIT_RUNTIME_PASS: offensive ammunition expended while original two-waypoint Move task remains active");
  SessionState.SetBool("LiveBattlefieldReview.transit",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");EditorApplication.isPlaying=false;
 }
}
