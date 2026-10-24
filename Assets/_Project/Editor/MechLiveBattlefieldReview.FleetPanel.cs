using System;
using System.IO;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEngine;
using UnityEditor;
using UnityEngine.InputSystem;
public static partial class MechLiveBattlefieldReview
{
 static int fleetPanelStage,fleetPanelFrames;
 public static void RunFleetPanelReview(){SessionState.SetBool("LiveBattlefieldReview.fleetPanel",true);RunRenderingFixReview();}
 static void TickFleetPanelScreen(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  if(EditorApplication.timeSinceStartup-entered>120)throw new Exception("Fleet panel capture timeout");
  if(fleetPanelStage==0)
  {
   if(Time.timeAsDouble<15)return;
   var panel=rig.GetComponent<MechFleetCommandPanel>();var fleet=rig.GetComponent<MechFleetController>();
   if(panel==null||fleet==null||!fleet.Configured)throw new Exception("Default scene has no configured fleet command panel");
   if(Keyboard.current==null)InputSystem.AddDevice<Keyboard>();
   map.SetVisible(false);
   typeof(MechFleetCommandPanel).GetField("visible",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(panel,true);
   fleetPanelStage=1;fleetPanelFrames=0;return;
  }
  if(fleetPanelStage==1)
  {
   if(++fleetPanelFrames<12)return;
   ScreenCapture.CaptureScreenshot(dir+"/fleet-panel-game.png");fleetPanelStage=2;return;
  }
  if(!File.Exists(dir+"/fleet-panel-game.png"))return;
  File.WriteAllText(dir+"/capture.txt","Actual default scene MechFleetCommandPanel, same visible state as F6. No mockup or UI redesign.");
  SessionState.SetBool("LiveBattlefieldReview.fleetPanel",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");Time.timeScale=1;EditorApplication.isPlaying=false;
 }
}
