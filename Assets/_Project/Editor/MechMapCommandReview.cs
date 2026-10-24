using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEditor;
public static partial class MechLiveBattlefieldReview
{
 static int mapCommandStage,mapCommandFrames;static double mapCommandDeadline;static Guid mapCommandMember,mapCommandOperation;static string mapFriendKey,mapEnemyKey;
 const BindingFlags MapPrivate=BindingFlags.Instance|BindingFlags.NonPublic;
 [InitializeOnLoadMethod] private static void SuspendUnrelatedCatalogBuild()
 {
  if(!Application.isBatchMode||Environment.GetEnvironmentVariable("ACUNICOMBAT_MAP_COMMAND_REVIEW")!="1")return;
  var method=typeof(AC6FullPartCatalogBuilder).GetMethod("TryRunQueuedBuild",BindingFlags.Static|BindingFlags.NonPublic);
  if(method!=null)EditorApplication.update-=(EditorApplication.CallbackFunction)Delegate.CreateDelegate(typeof(EditorApplication.CallbackFunction),method);
 }
 public static void RunMapCommandReview(){SuspendUnrelatedCatalogBuild();SessionState.SetBool("LiveBattlefieldReview.mapCommands",true);RunRenderingFixReview();}
 static void ClickMapCommand(MechBattlefieldMap map,string text,bool openPolicy=true)
 {
  if(openPolicy&&(text=="辐射管制"||text=="主动雷达"||text=="自由开火"||text=="确认开火"))
  {
   string current=text switch{"辐射管制"=>"主动雷达","主动雷达"=>"辐射管制","自由开火"=>"确认开火",_=>"自由开火"};
   ClickMapCommand(map,current,false);
  }
  map.Refresh();var buttons=(IEnumerable)typeof(MechBattlefieldMap).GetField("commandButtons",MapPrivate).GetValue(map);
  foreach(var b in buttons)if((string)b.GetType().GetField("Caption").GetValue(b)==text)
  {
   var rect=(Rect)b.GetType().GetField("Bounds").GetValue(b);
   if(!(bool)typeof(MechBattlefieldMap).GetMethod("HandleCommandClick",MapPrivate).Invoke(map,new object[]{rect.center}))throw new Exception("Map click was not consumed");return;
  }
  throw new Exception("Enabled map command not found: "+text);
 }
 static Guid MapOperation(MechBattlefieldMap map)=>(Guid)typeof(MechBattlefieldMap).GetField("lastMapOperation",MapPrivate).GetValue(map);
 static void TickMapCommands(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  SuspendUnrelatedCatalogBuild();
  if(EditorApplication.timeSinceStartup-entered>240)throw new Exception("Map command scenario timed out at "+mapCommandStage);
  var fleet=rig.GetComponent<MechFleetController>();var radar=rig.GetComponent<MechRadarController>();var link=rig.GetComponent<MechCooperativeTelemetry>();
  if(MechAssemblyMenu.IsAnyMenuVisible)typeof(MechAssemblyMenu).GetMethod("CloseAllForSolarMap",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
  if(Environment.GetEnvironmentVariable("ACUNICOMBAT_MAP_SCREENSHOT_ONLY")=="1")
  {
   if(Time.timeAsDouble<12)return;
   if(mapCommandFrames++==0){map.SetVisible(true);map.SelectContact("own");map.Refresh();return;}
   if(mapCommandFrames%10!=0)return;
   if(mapCommandStage==0){map.Capture(dir+"/map.png");ClickMapCommand(map,"主动雷达",false);mapCommandStage++;return;}
   if(mapCommandStage==1){map.Capture(dir+"/radar-menu.png");ClickMapCommand(map,"确认开火",false);mapCommandStage++;return;}
   map.Capture(dir+"/weapon-menu.png");SessionState.SetBool("LiveBattlefieldReview.mapCommands",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");Time.timeScale=1;EditorApplication.isPlaying=false;return;
  }
  if(mapCommandStage==0)
  {
   if(Time.timeAsDouble<12||fleet==null||!fleet.Configured)return;
   map.SetVisible(true);map.SelectContact("own");map.Refresh();map.Capture(dir+"/ownship.png");
   ClickMapCommand(map,"辐射管制");if(radar.RadarPowerEnabled)throw new Exception("Own radar did not switch off");
   ClickMapCommand(map,"主动雷达");if(!radar.RadarPowerEnabled)throw new Exception("Own radar did not switch on");
   ClickMapCommand(map,"自由开火");if(fleet.State.Mode!=FleetAttackMode.Free)throw new Exception("Own free-fire mode failed");
   ClickMapCommand(map,"确认开火");if(fleet.State.Mode!=FleetAttackMode.Confirmed)throw new Exception("Own confirmed mode failed");
   mapCommandStage=1;return;
  }
  if(mapCommandStage==1)
  {
   map.Refresh();foreach(var c in map.Contacts)
   {
    if(c.Own||c.Missile||c.Affiliation!=TrackAffiliation.Friendly||!link.TryGetCooperativeMember(radar.Reference(c.Observation.TrackId),out var member)||!fleet.CanCommand(member))continue;
    if(!fleet.State.Members.TryGetValue(member,out var report)||!report.HasPlatformStatus||!fleet.State.Fresh(report,radar.SimTimeSeconds))continue;
    mapFriendKey=c.Key;mapCommandMember=member;break;
   }
   if(mapFriendKey==null)return;
   map.SelectContact(mapFriendKey);map.Refresh();ClickMapCommand(map,"选为指挥对象");ClickMapCommand(map,"辐射管制");mapCommandOperation=MapOperation(map);
   if(mapCommandOperation==Guid.Empty||fleet.Results.ContainsKey(mapCommandOperation))throw new Exception("Remote control bypassed transport or failed to enqueue");
   mapCommandDeadline=radar.SimTimeSeconds+15;mapCommandStage=2;return;
  }
  bool remoteReady=fleet.State.Members.TryGetValue(mapCommandMember,out var current)&&fleet.State.Fresh(current,radar.SimTimeSeconds)&&current.HasPlatformStatus;
  if(mapCommandStage>=2&&mapCommandStage<=5&&radar.SimTimeSeconds>mapCommandDeadline)throw new Exception("Remote command/report timeout at "+mapCommandStage+" / "+(fleet.Results.TryGetValue(mapCommandOperation,out var failed)?failed.Phase+":"+failed.Reason:"no receipt"));
  if(mapCommandStage==2)
  {
   if(!remoteReady||current.RadarControl!=FleetRadarControl.RadiationSilence||current.RadarPowered||!fleet.Results.TryGetValue(mapCommandOperation,out var reply)||reply.Phase!=FleetTaskPhase.Completed)return;
   map.Refresh();map.Capture(dir+"/wing-radar-silent.png");ClickMapCommand(map,"主动雷达");mapCommandOperation=MapOperation(map);mapCommandDeadline=radar.SimTimeSeconds+15;mapCommandStage=3;return;
  }
  if(mapCommandStage==3)
  {
   if(!remoteReady||current.RadarControl!=FleetRadarControl.Active||!current.RadarPowered)return;
   ClickMapCommand(map,"自由开火");mapCommandOperation=MapOperation(map);mapCommandDeadline=radar.SimTimeSeconds+15;mapCommandStage=4;return;
  }
  if(mapCommandStage==4)
  {
   if(!remoteReady||current.Mode!=FleetAttackMode.Free)return;
   ClickMapCommand(map,"确认开火");mapCommandOperation=MapOperation(map);mapCommandDeadline=radar.SimTimeSeconds+15;mapCommandStage=5;return;
  }
  if(mapCommandStage==5)
  {
   if(!remoteReady||current.Mode!=FleetAttackMode.Confirmed)return;
   map.Refresh();map.Capture(dir+"/wing.png");
   var enemy=map.Contacts.FirstOrDefault(c=>!c.Own&&!c.Missile&&c.Affiliation==TrackAffiliation.Hostile);if(enemy.Key==null)return;
   mapEnemyKey=enemy.Key;map.SelectContact(mapEnemyKey);map.Refresh();map.Capture(dir+"/enemy.png");ClickMapCommand(map,"IFF 问询");ClickMapCommand(map,"攻击选中目标");mapCommandOperation=MapOperation(map);mapCommandDeadline=radar.SimTimeSeconds+15;mapCommandStage=6;return;
  }
  if(mapCommandStage==6)
  {
   if(radar.SimTimeSeconds>mapCommandDeadline)throw new Exception("Attack command did not receive acknowledgement");
   if(!fleet.Results.TryGetValue(mapCommandOperation,out var reply))return;
   if(reply.Phase==FleetTaskPhase.Rejected)throw new Exception("Attack rejected: "+reply.Reason);
   ClickMapCommand(map,"归队");mapCommandOperation=MapOperation(map);mapCommandDeadline=radar.SimTimeSeconds+15;mapCommandStage=7;return;
  }
  if(mapCommandStage==7)
  {
   if(radar.SimTimeSeconds>mapCommandDeadline)throw new Exception("Return command did not receive acknowledgement");
   if(!fleet.Results.TryGetValue(mapCommandOperation,out var reply)||reply.Phase==FleetTaskPhase.Requested)return;
   if(reply.Phase==FleetTaskPhase.Rejected)throw new Exception("Return rejected: "+reply.Reason);
   map.SelectContact(mapFriendKey);map.Refresh();map.Capture(dir+"/wing-returning.png");File.WriteAllText(dir+"/result.txt","PASS: actual map button handlers; own radar and fire policies; remote radar off/on and fire policies through physical data link; fresh status/capability reports; IFF request; attack and return receipts. Intelligent mode remains disabled.");
   File.WriteAllText(dir+"/fleet-log.txt",fleet.LastDecision);mapCommandStage=8;mapCommandFrames=0;return;
  }
  if(mapCommandStage>=8&&mapCommandStage<=10)
  {
   if(mapCommandFrames++==0){map.SelectContact(mapCommandStage==8?"own":mapCommandStage==9?mapFriendKey:mapEnemyKey);map.Refresh();return;}
   if(mapCommandFrames<10)return;
   map.Capture(dir+"/"+(mapCommandStage==8?"own-final":mapCommandStage==9?"wing-final":"enemy-final")+".png");mapCommandStage++;mapCommandFrames=0;return;
  }

  SessionState.SetBool("LiveBattlefieldReview.mapCommands",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");Time.timeScale=1;EditorApplication.isPlaying=false;
 }
}
