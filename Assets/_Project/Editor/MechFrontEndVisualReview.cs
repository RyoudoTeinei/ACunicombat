using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[InitializeOnLoad]
public static class MechFrontEndVisualReview
{
 const string Key="FrontEndVisualReview";
 const string Output="E:/UnityDev/ACunicombat/Artifacts/FrontEnd-20261024";
 static int frames,stage;
 static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 static MechFrontEndVisualReview(){EditorApplication.update+=Tick;}
 public static void Run()
 {
  Directory.CreateDirectory(Output);
  EditorSceneManager.OpenScene(MechFrontEndBuilder.MenuScene);
  typeof(MechLiveBattlefieldReview).GetMethod("ConfigureGameView",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{1780,1000});
  SessionState.SetBool(Key,true);EditorApplication.isPlaying=true;
 }
 static void Tick()
 {
  if(!SessionState.GetBool(Key,false)||!EditorApplication.isPlaying)return;
  try
  {
   var ui=UnityEngine.Object.FindFirstObjectByType<MechFrontEnd>();if(ui==null)return;
   if(++frames<40)return;frames=0;
   var field=typeof(MechFrontEnd).GetField("page",Private);
   if(stage==0){ScreenCapture.CaptureScreenshot(Output+"/home.png");}
   else if(stage==1){field.SetValue(ui,Enum.Parse(field.FieldType,"Editor"));}
   else if(stage==2){ScreenCapture.CaptureScreenshot(Output+"/editor.png");}
   else if(stage==3){var map=UnityEngine.Object.FindAnyObjectByType<MechBattlefieldMap>();map.ZoomAt(-5,map.MapCenter);}
   else if(stage==4){ScreenCapture.CaptureScreenshot(Output+"/editor-zoom-out.png");}
   else if(stage==5){var map=UnityEngine.Object.FindAnyObjectByType<MechBattlefieldMap>();map.ZoomAt(8,map.MapCenter);}
   else if(stage==6){ScreenCapture.CaptureScreenshot(Output+"/editor-zoom-pan.png");}
   else if(stage==7){var scenario=(MechScenarioDefinition)typeof(MechFrontEnd).GetField("scenario",Private).GetValue(ui);typeof(MechFrontEnd).GetMethod("OpenEquipment",Private).Invoke(ui,new object[]{scenario.units.First(u=>!u.player),Enum.Parse(field.FieldType,"Editor")});}
   else if(stage==8){ScreenCapture.CaptureScreenshot(Output+"/npc-hangar.png");if(UnityEngine.Object.FindAnyObjectByType<MechPlayerRig>()!=null)throw new Exception("Editor started simulation");}
   else if(stage==9)
   {
    var menu=UnityEngine.Object.FindAnyObjectByType<MechAssemblyMenu>();bool saved=false;
    typeof(MechAssemblyMenu).GetField("externalSave",Private).SetValue(menu,(Action<MechLoadoutDefinition>)(value=>{if(!MechLoadoutValidator.Validate(value).IsValid)throw new Exception("Invalid external loadout");saved=true;}));
    typeof(MechAssemblyMenu).GetMethod("CommitExternalLoadout",Private).Invoke(menu,null);
    if(!saved)throw new Exception("External save callback missing");
   }
   else if(stage==10){if(!UnityEngine.Object.FindAnyObjectByType<MechBattlefieldMap>().Visible)throw new Exception("Map did not resume");ScreenCapture.CaptureScreenshot(Output+"/returned-map.png");}
   else {Debug.Log("FRONTEND_VISUAL_CAPTURE_COMPLETE: shared map, shared hangar, valid external save callback and return to map; no combat rig");SessionState.SetBool(Key,false);EditorApplication.Exit(0);}
   stage++;
  }
  catch(Exception e){Debug.LogException(e);SessionState.SetBool(Key,false);EditorApplication.Exit(1);}
 }
}
