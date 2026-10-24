using System;
using System.IO;
using ACunicombat.Mechs;
using UnityEngine;
using UnityEditor;
public static partial class MechLiveBattlefieldReview
{
 static int uiStage,uiFrames;
 static Camera bankCamera;static Quaternion bankRotation;
 static void ApplyBank(UnityEngine.Rendering.ScriptableRenderContext context,Camera camera){if(camera==bankCamera)camera.transform.rotation=bankRotation;}
 public static void RunUiScreensReview(){SessionState.SetBool("LiveBattlefieldReview.uiScreens",true);RunRenderingFixReview();}
 static void TickUiScreens(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  if(EditorApplication.timeSinceStartup-entered>180)throw new Exception("UI screenshots timed out at stage "+uiStage);
  if(MechAssemblyMenu.IsAnyMenuVisible)
   typeof(MechAssemblyMenu).GetMethod("CloseAllForSolarMap",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,null);
  var radar=rig.GetComponent<MechRadarController>();
  if(uiStage==0)
  {
   if(Time.timeAsDouble<15)return;
   adaptiveFrozen=true;Time.timeScale=0;map.SetVisible(true);map.ResetView();map.Refresh();uiStage=1;uiFrames=0;return;
  }
  if(uiStage==1)
  {
   if(++uiFrames<10)return;
   if(Mathf.Abs(map.HorizontalSpanKm-250)>.01f)throw new Exception("Map is not 250 km at 1x");
   map.Capture(dir+"/map.png");ScreenCapture.CaptureScreenshot(dir+"/map-game.png");
   File.WriteAllText(dir+"/capture.txt",$"Actual playable scene, frozen at {Time.timeAsDouble:F2}s. Map width={map.HorizontalSpanKm:F2}km, zoom={map.ContentZoom:F2}. GameView={Screen.width}x{Screen.height}.");
   uiStage=2;return;
  }
  if(uiStage==2)
  {
   if(!File.Exists(dir+"/map-game.png"))return;
   map.SetVisible(false);radar.SetTrackOperationMode(false);uiStage=3;uiFrames=0;return;
  }
  if(uiStage==3)
  {
   if(++uiFrames<10)return;
   ScreenCapture.CaptureScreenshot(dir+"/reticle-tab-off.png");uiStage=4;return;
  }
  if(uiStage==4)
  {
   if(!File.Exists(dir+"/reticle-tab-off.png"))return;
   radar.SetTrackOperationMode(true);uiStage=5;uiFrames=0;return;
  }
  if(uiStage==5)
  {
   if(++uiFrames<10)return;
   ScreenCapture.CaptureScreenshot(dir+"/reticle-tab-on.png");uiStage=6;return;
  }
  if(!File.Exists(dir+"/reticle-tab-on.png"))return;
  if(uiStage==6)
  {
   var camera=Camera.main;
   if(camera==null)throw new Exception("No gameplay camera for banked HUD capture");
   var controller=camera.GetComponent<MechThirdPersonCamera>();if(controller!=null)controller.enabled=false;
   bankCamera=camera;bankRotation=camera.transform.rotation*Quaternion.Euler(15,0,40);
   UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering+=ApplyBank;
   uiStage=7;uiFrames=0;return;
  }
  if(uiStage==7)
  {
   if(++uiFrames<10)return;
   ScreenCapture.CaptureScreenshot(dir+"/reticle-bank.png");uiStage=8;return;
  }
  if(!File.Exists(dir+"/reticle-bank.png"))return;
  UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering-=ApplyBank;
  File.AppendAllText(dir+"/capture.txt","\nPASS: map and both Tab reticle states captured from actual game rendering.");
  SessionState.SetBool("LiveBattlefieldReview.uiScreens",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");adaptiveFrozen=false;Time.timeScale=1;EditorApplication.isPlaying=false;
 }
}
