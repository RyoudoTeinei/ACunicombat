using System;using System.IO;using System.Linq;using UnityEngine;using UnityEditor;using UnityEditor.SceneManagement;
[InitializeOnLoad]
public static class MechMapRenderingReview
{
 const string Request="Temp/ReviewLiveMapRendering.request",Key="ACunicombat.MapRenderingReview";
 static double nextPoll,started;static int phase,frame;static bool wasPlaying,background;static string output;
 static MechMapRenderingReview(){EditorApplication.update+=Tick;}
 static void Tick()
 {
  if(Application.isBatchMode||EditorApplication.isCompiling||EditorApplication.isUpdating)return;
  try
  {
   if(string.IsNullOrEmpty(output))
   {
    if(EditorApplication.timeSinceStartup<nextPoll)return;nextPoll=EditorApplication.timeSinceStartup+1;
    if(!File.Exists(Request))return;output=File.ReadAllText(Request).Trim();File.Delete(Request);Directory.CreateDirectory(output);
    wasPlaying=EditorApplication.isPlaying;background=Application.runInBackground;Application.runInBackground=true;started=EditorApplication.timeSinceStartup;
    File.WriteAllText(output+"/status.txt","Starting actual editor map capture");
    SessionState.SetString(Key,output);SessionState.SetBool(Key+".background",background);
    if(!wasPlaying)
    {
     for(int i=0;i<UnityEngine.SceneManagement.SceneManager.sceneCount;i++)if(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty){File.WriteAllText(output+"/status.txt","BLOCKED: unsaved scene preserved");SessionState.EraseString(Key);Application.runInBackground=background;output=null;return;}
     SessionState.SetString(Key,output);SessionState.SetBool(Key+".startedPlay",true);
     EditorSceneManager.OpenScene(ACunicombatEditorScenePath,OpenSceneMode.Single);EditorApplication.isPlaying=true;return;
    }
   }
   if(!EditorApplication.isPlaying)return;
   var rig=UnityEngine.Object.FindObjectsByType<ACunicombat.Mechs.MechPlayerRig>(FindObjectsInactive.Exclude).FirstOrDefault(r=>!r.AiControlled);if(rig==null)return;
   var map=rig.GetComponent<ACunicombat.Mechs.MechBattlefieldMap>();if(map==null)return;
   if(phase==0){map.SetVisible(true);map.ResetView();frame=Time.frameCount;started=EditorApplication.timeSinceStartup;phase=1;return;}
   if(Time.frameCount-frame<8)return;
   if(phase==1)
   {
    if(map.Contacts.Count<3&&EditorApplication.timeSinceStartup-started<20)return;
    map.Capture(output+"/native-map.png");ScreenCapture.CaptureScreenshot(output+"/game-view.png");
    var contact=map.Contacts.FirstOrDefault(c=>!c.Own);var anchor=string.IsNullOrEmpty(contact.Key)?new Vector2(620,540):map.Project(contact.Position);
    if(anchor.x<40||anchor.x>1180||anchor.y<120||anchor.y>900)anchor=new Vector2(620,540);
    float before=map.RangeKm;map.ZoomAt(2,anchor);
    File.WriteAllText(output+"/render-info.txt",$"Screen {Screen.width}x{Screen.height}; viewport {map.Viewport}; source {map.SourceResolution}; output {map.Display.width}x{map.Display.height}; zoom {before} -> {map.RangeKm}; anchor {anchor}");
    frame=Time.frameCount;phase=2;return;
   }
   map.Capture(output+"/zoomed-map.png");ScreenCapture.CaptureScreenshot(output+"/zoomed-game-view.png");
   if(phase==2){frame=Time.frameCount;phase=3;return;}
   File.WriteAllText(output+"/status.txt","COMPLETE: actual current scene, native resolution and two-notch zoom captured");
   Debug.Log("LIVE_MAP_RENDERING_REVIEW_OK "+output);Application.runInBackground=background;
   bool stop=SessionState.GetBool(Key+".startedPlay",false);SessionState.EraseString(Key);SessionState.SetBool(Key+".startedPlay",false);output=null;phase=0;if(stop)EditorApplication.isPlaying=false;
  }
  catch(Exception e){if(!string.IsNullOrEmpty(output))File.WriteAllText(output+"/error.txt",e.ToString());Debug.LogException(e);output=null;phase=0;}
 }
 const string ACunicombatEditorScenePath="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
 [InitializeOnLoadMethod] static void Resume(){var pending=SessionState.GetString(Key,"");if(!string.IsNullOrEmpty(pending)){output=pending;phase=0;background=SessionState.GetBool(Key+".background",false);started=EditorApplication.timeSinceStartup;}}
}
