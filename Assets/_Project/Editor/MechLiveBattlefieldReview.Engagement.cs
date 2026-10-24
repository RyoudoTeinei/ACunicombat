using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEditor;
public static partial class MechLiveBattlefieldReview
{
 static bool engagementStarted,engagementCaptured;static int engagementCaptureFrames;static double nextEngagementDiagnostic;static bool engagementOrdered,engagementSampled;static double engagementSampleTime;static Vector3 engagementGoal;static Dictionary<Guid,int> engagementAmmo=new();
 public static void RunEngagementReview(){SessionState.SetBool("LiveBattlefieldReview.engagement",true);RunRenderingFixReview();}
 static void TickEngagement(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  Time.timeScale=2;
  var fleet=rig.GetComponent<MechFleetController>();if(fleet==null||!fleet.Configured||Time.timeAsDouble<12)return;
  var friends=UnityEngine.Object.FindObjectsByType<MechFleetController>(FindObjectsInactive.Exclude).Where(f=>!f.IsPlayer&&f.Configured&&f.State.Fleet==fleet.State.Fleet).ToArray();
  if(!engagementStarted)
  {
   engagementStarted=true;fleet.SetFormationMode(FleetAttackMode.Free);
   foreach(var friend in friends){var equipment=new List<MechEquipmentWeaponSnapshot>();friend.GetComponent<MechTestWeaponSystem>().CopyEquipmentWeaponSnapshots(equipment);foreach(var e in equipment)if(MechAiRulePlanner.IsOffensiveRole(e.Role))engagementAmmo[e.InstanceId]=e.Rounds;}
  }
  if(!engagementCaptured&&Time.timeAsDouble>17)
  {
   map.SetVisible(true);map.SelectContact("own");map.Refresh();
   var enemy=map.Contacts.FirstOrDefault(c=>!c.Own&&!c.Missile&&c.Affiliation!=TrackAffiliation.Friendly&&c.Observation.TrackId.IsValid);
   if(!string.IsNullOrEmpty(enemy.Key))
   {
    rig.GetComponent<MechIdentityController>().MarkContact(rig.GetComponent<MechRadarController>().Reference(enemy.Observation.TrackId),IdentityRelation.Hostile);map.Refresh();
    var weapons=rig.GetComponent<MechTestWeaponSystem>();var inventory=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(inventory);
    var chosen=inventory.First(e=>e.Role==MechWeaponRole.AutomaticCannon);
    typeof(MechBattlefieldMap).GetField("commandWeapon",MapPrivate).SetValue(map,chosen.InstanceId);
    typeof(MechBattlefieldMap).GetField("weaponDirectMode",MapPrivate).SetValue(map,true);
    var point=map.Project(enemy.Position);var screen=new Vector2(map.Viewport.x+point.x/map.LayoutWidth*map.Viewport.width,Screen.height-map.Viewport.y-point.y/1000f*map.Viewport.height);
    // Map layout uses a 1000 unit vertical canvas.
    UnityEngine.InputSystem.Mouse.current.WarpCursorPosition(screen);
    typeof(MechBattlefieldMap).GetField("mapPointer",MapPrivate).SetValue(map,point);map.Refresh();if(++engagementCaptureFrames<15)return;map.Capture(dir+"/dynamic-envelope.png");
    typeof(MechBattlefieldMap).GetField("speedMenuOpen",MapPrivate).SetValue(map,true);map.Refresh();map.Capture(dir+"/speed-menu.png");engagementCaptured=true;
   }
  }
  if(Time.timeAsDouble>=nextEngagementDiagnostic)
  {
   nextEngagementDiagnostic=Time.timeAsDouble+10;
   foreach(var friend in friends)
   {
    var radar=friend.GetComponent<MechRadarController>();var tracks=new TrackSnapshot[96];int count=radar.CopyTracks(tracks);
    var items=new List<MechEquipmentWeaponSnapshot>();var weapons=friend.GetComponent<MechTestWeaponSystem>();weapons.CopyEquipmentWeaponSnapshots(items);
    File.AppendAllText(dir+"/diagnostics.txt",$"time={Time.timeAsDouble} unit={friend.name} task={friend.State.Task?.Phase} reason={friend.State.Task?.Reason} action={friend.GetComponent<MechAiTargetController>().CurrentCombatState} tracks={count}\n");
    foreach(var t in tracks.Take(count))File.AppendAllText(dir+"/diagnostics.txt",$"track={t.TrackId} {t.ObjectClass} {t.Affiliation} {t.Lifecycle} confidence={t.Confidence} attack={t.Identity.CanAttackPlatform} age={radar.SimTimeSeconds-t.LastUpdateTime} range={Vector3.Distance(friend.transform.position,radar.PredictTrackPosition(t))}\n");
    foreach(var e in items){weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var st);File.AppendAllText(dir+"/diagnostics.txt",$"weapon={e.SlotId} role={e.Role} enabled={e.Enabled} ammo={e.Rounds} range={st.MaximumRangeMeters} effective={st.EffectiveRangeMeters} speed={weapons.EstimatedProjectileSpeed(e.SlotId)}\n");}
   }
  }
  bool fired=false;
  foreach(var friend in friends)
  {
   var equipment=new List<MechEquipmentWeaponSnapshot>();friend.GetComponent<MechTestWeaponSystem>().CopyEquipmentWeaponSnapshots(equipment);
   foreach(var e in equipment)if(engagementAmmo.TryGetValue(e.InstanceId,out var before)&&e.Rounds<before)fired=true;
  }
  if(Environment.GetEnvironmentVariable("ACUNICOMBAT_ENGAGEMENT_VISUAL")=="1")
  {
   if(!engagementCaptured||++engagementCaptureFrames<35)return;
   if(!engagementOrdered)
   {
    map.Capture(dir+"/speed-menu.png");ClickMapCommand(map,"前进 500 m/s",false);
    if(fleet.State.CruiseSpeed!=500)throw new Exception("Speed menu did not set cruise speed");
    var enemy=map.Contacts.First(c=>!c.Own&&!c.Missile&&c.Affiliation==TrackAffiliation.Hostile);
    typeof(MechBattlefieldMap).GetMethod("RightMapCommand",MapPrivate).Invoke(map,new object[]{map.Project(enemy.Position),false});
    if(!fleet.HasTask||fleet.State.Task.Order.Order!=FleetOrderKind.AttackWithWeapon)throw new Exception("Direct weapon command failed");
    engagementOrdered=true;return;
   }
   if(!engagementSampled){if(!fleet.TryGetNavigationGoal(out engagementGoal))return;engagementSampleTime=rig.GetComponent<MechRadarController>().SimTimeSeconds;engagementSampled=true;return;}
   double elapsed=rig.GetComponent<MechRadarController>().SimTimeSeconds-engagementSampleTime;
   if(elapsed<1){if(fleet.TryGetNavigationGoal(out var early)&&Vector3.Distance(early,engagementGoal)>.05f)throw new Exception("Attack route changed before two-second interval");return;}
   if(elapsed<2.2)return;
   if(!fleet.TryGetNavigationGoal(out var updated)||Vector3.Distance(updated,engagementGoal)<.05f)throw new Exception("Attack route did not update after interval");
   Debug.Log("ENGAGEMENT_COMMAND_PASS: speed menu applies; specified attack accepted; route held then replanned after two seconds");fired=true;
  }
  if(!fired&&EditorApplication.timeSinceStartup-entered<100)return;
  File.WriteAllLines(dir+"/free-fire.txt",friends.Select(f=>$"{f.name}: mode={f.State.Mode}; task={f.State.Task?.Order.Order}; phase={f.State.Task?.Phase}; reason={f.State.Task?.Reason}; decision={f.LastDecision}; weapon={f.GetComponent<MechAiTargetController>()?.LastWeaponDecision}; group={f.TaskGroup}; position={f.transform.position}"));
  Debug.Log(Environment.GetEnvironmentVariable("ACUNICOMBAT_ENGAGEMENT_VISUAL")=="1"?"ENGAGEMENT_VISUAL_PASS":"ENGAGEMENT_REVIEW friendlyOffensiveShot="+fired);
  SessionState.SetBool("LiveBattlefieldReview.engagement",false);SessionState.SetInt("LiveBattlefieldReview.exit",fired?0:1);SessionState.SetString(Key,"finish");EditorApplication.isPlaying=false;
 }
}
