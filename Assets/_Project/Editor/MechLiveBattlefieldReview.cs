using UnityEditor.SceneManagement;
using System;using System.IO;using System.Linq;using System.Collections.Generic;
using ACunicombat.Mechs;using UnityEngine;using UnityEditor;
[InitializeOnLoad]
public static partial class MechLiveBattlefieldReview
{
 const string Key="LiveBattlefieldReview.stage";
 const string Scene="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
 static double entered;static bool opened;static int frames;static bool zoomed;static int adaptiveStage,adaptiveFrames;static bool adaptiveFrozen,adaptiveContextPrepared,adaptivePolicyPrepared,adaptiveCustomSpeedPrepared;
 static MechLiveBattlefieldReview(){EditorApplication.update+=Tick;}
 public static void Run()
 {
  string dir=Environment.GetEnvironmentVariable("ACUNICOMBAT_LIVE_REVIEW")??"E:/UnityDev/ACunicombat/Artifacts/LiveBattlefield-20261016";
  Directory.CreateDirectory(dir);SessionState.SetString("LiveBattlefieldReview.output",dir);
  File.Copy(Scene,dir+"/scene-before.unity",true);
  AC6PlayableSceneBuilder.UpdateLiveBattlefieldScene();
  var rigs=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include);
  var owner=rigs.Single(x=>!x.AiControlled);var targets=rigs.Where(x=>x.AiControlled).Select(x=>x.GetComponent<CombatTestTarget>()).ToArray();
  if(targets.Length!=4)throw new Exception("Expected four real AI mechs, found "+targets.Length);
  var lines=new List<string>();int friendly=0,hostile=0;
  foreach(var t in targets)
  {
   var rel=t.transform.position-owner.transform.position;
   if(rel.magnitude>150000||Mathf.Abs(rel.y)<1000||t.GetComponent<MechAiTargetController>()==null)throw new Exception("Invalid three-dimensional placement");
   if(t.TeamId==1)friendly++;else hostile++;
   lines.Add(t.TargetId+" team="+t.TeamId+" relative="+rel+" distanceKm="+(rel.magnitude/1000).ToString("0.000"));
  }
  if(friendly!=2||hostile!=2)throw new Exception("Expected two friendly and two hostile AI");
  File.WriteAllLines(dir+"/placement.txt",lines);SessionState.SetString(Key,"play");EditorApplication.isPlaying=true;
 }
 public static void RunAdaptiveReview(){SessionState.SetBool("LiveBattlefieldReview.adaptive",true);RunRenderingFixReview();}
 public static void RunRenderingFixReview()
 {
  string dir=Environment.GetEnvironmentVariable("ACUNICOMBAT_LIVE_REVIEW")??"E:/UnityDev/ACunicombat/Artifacts/LiveMapRendering-Final";
  Directory.CreateDirectory(dir);SessionState.SetString("LiveBattlefieldReview.output",dir);
  EditorSceneManager.OpenScene(Scene,OpenSceneMode.Single);
  int width=Environment.GetEnvironmentVariable("ACUNICOMBAT_REVIEW_WIDTH") is string w&&int.TryParse(w,out var parsedWidth)?parsedWidth:2560;
  int height=Environment.GetEnvironmentVariable("ACUNICOMBAT_REVIEW_HEIGHT") is string h&&int.TryParse(h,out var parsedHeight)?parsedHeight:1440;
  ConfigureGameView(width,height);
  SessionState.SetBool("LiveBattlefieldReview.rendering",true);SessionState.SetString(Key,"play");EditorApplication.isPlaying=true;
 }
 static void ConfigureGameView(int w,int h)
 {
  var asm=typeof(Editor).Assembly;var sizesType=asm.GetType("UnityEditor.GameViewSizes");
  var singleton=typeof(ScriptableSingleton<>).MakeGenericType(sizesType);var sizes=singleton.GetProperty("instance",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static).GetValue(null);
  var group=sizesType.GetMethod("GetGroup").Invoke(sizes,new[]{Enum.Parse(asm.GetType("UnityEditor.GameViewSizeGroupType"),"Standalone")});
  var size=Activator.CreateInstance(asm.GetType("UnityEditor.GameViewSize"),System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,
   new object[]{Enum.Parse(asm.GetType("UnityEditor.GameViewSizeType"),"FixedResolution"),w,h,"Live map rendering validation"},null);
  group.GetType().GetMethod("AddCustomSize").Invoke(group,new[]{size});int index=(int)group.GetType().GetMethod("GetTotalCount").Invoke(group,null)-1;
  var view=EditorWindow.GetWindow(asm.GetType("UnityEditor.GameView"));var property=view.GetType().GetProperty("selectedSizeIndex",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
  SessionState.SetInt("LiveBattlefieldReview.originalSize",(int)property.GetValue(view));property.SetValue(view,index);
 }
 static void RestoreGameView()
 {
  if(!SessionState.GetBool("LiveBattlefieldReview.rendering",false))return;
  var view=EditorWindow.GetWindow(typeof(Editor).Assembly.GetType("UnityEditor.GameView"));view.GetType().GetProperty("selectedSizeIndex",System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).SetValue(view,SessionState.GetInt("LiveBattlefieldReview.originalSize",0));
  SessionState.SetBool("LiveBattlefieldReview.rendering",false);
 }
 static bool CaptureAdaptive(MechBattlefieldMap map,string dir)
 {
  float[] scales={1,2,3,4,5,8};
  if(!adaptiveFrozen){adaptiveFrozen=true;Time.timeScale=0;map.ResetView();map.Refresh();adaptiveFrames=0;return true;}
  if(++adaptiveFrames<5)return true;
  if(adaptiveStage==4&&!adaptiveContextPrepared)
  {
   adaptiveContextPrepared=true;
   var target=map.Contacts.FirstOrDefault(c=>!c.Own);
   if(!string.IsNullOrEmpty(target.Key))
   {
    typeof(MechBattlefieldMap).GetField("contextTarget",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,target);
    typeof(MechBattlefieldMap).GetField("contextPoint",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,new Vector2(650,250));
    typeof(MechBattlefieldMap).GetField("contextOpen",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,true);map.Refresh();return true;
   }
  }
  if(adaptiveStage==5&&!adaptivePolicyPrepared)
  {
   adaptivePolicyPrepared=true;map.SelectContact("own");
   typeof(MechBattlefieldMap).GetField("contextOpen",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,false);
   typeof(MechBattlefieldMap).GetField("openPolicyMenu",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,0);
   typeof(MechBattlefieldMap).GetField("speedMenuOpen",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,true);
   var fleet=(MechFleetController)typeof(MechBattlefieldMap).GetField("commandFleet",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(map);
   typeof(MechBattlefieldMap).GetField("speedMenuMember",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,fleet.MemberId);
   map.Refresh();return true;
  }
  if(adaptiveStage==5&&!adaptiveCustomSpeedPrepared)
  {
   adaptiveCustomSpeedPrepared=true;map.Capture(dir+"/speed-menu.png");
   typeof(MechBattlefieldMap).GetField("speedCustomEditing",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(map,true);return true;
  }
  if(adaptiveStage==5)ScreenCapture.CaptureScreenshot(dir+"/custom-speed.png");
  map.Capture(dir+"/map-"+scales[adaptiveStage]+"x.png");
  if(adaptiveStage==0)
  {
   var clean=(RenderTexture)typeof(MechBattlefieldMap).GetField("overlayDisplay",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(map);
   typeof(MechBattlefieldMap).GetMethod("SaveTexture",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).Invoke(null,new object[]{clean,dir+"/source-1x.png"});
  }
  File.AppendAllText(dir+"/profiles.txt",$"zoom={map.ContentZoom} cells={map.ContentLogicalResolution} textureBuilds={map.DisplayTextureBuildCount} parameters={MechBattlefieldMap.RenderProfile(map.ContentZoom)}\n");
  adaptiveStage++;adaptiveFrames=0;
  if(adaptiveStage>=scales.Length){SessionState.SetBool("LiveBattlefieldReview.adaptive",false);return false;}
  var friend=map.Contacts.First(c=>!c.Own&&c.Affiliation==ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly);
  var anchor=map.Project(friend.Position);
  map.ZoomAt(Mathf.Log(scales[adaptiveStage]/map.ContentZoom)/.16f,anchor);
  for(int i=0;i<6;i++)map.AdvanceView(.25f);
  map.Refresh();
  return true;
 }
 static void Tick()
 {
  if(SessionState.GetString(Key,"")=="finish"&&!EditorApplication.isPlaying&&!EditorApplication.isPlayingOrWillChangePlaymode)
  {Time.timeScale=1;RestoreGameView();SessionState.EraseString(Key);EditorApplication.Exit(SessionState.GetInt("LiveBattlefieldReview.exit",0));return;}
  if(SessionState.GetString(Key,"")!="play"||!EditorApplication.isPlaying||EditorApplication.isCompiling)return;
  string dir=SessionState.GetString("LiveBattlefieldReview.output","");
  try
  {
   if(entered==0)entered=EditorApplication.timeSinceStartup;
   var rig=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude).FirstOrDefault(x=>!x.AiControlled);if(rig==null)return;
   Time.timeScale=adaptiveFrozen?0:1;
   var map=rig.GetComponent<MechBattlefieldMap>();if(map==null)return;
   if(SessionState.GetBool("LiveBattlefieldReview.tracer",false)){TickTracer(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.appearance",false)){TickAppearance(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.iffMenu",false)){TickIffMenu(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.engagement",false)){TickEngagement(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.policy",false)){TickPolicy(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.transit",false)){TickTransit(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.mapCommands",false)){TickMapCommands(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.fleetPanel",false)){TickFleetPanelScreen(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.uiScreens",false)){TickUiScreens(rig,map,dir);return;}
   if(SessionState.GetBool("LiveBattlefieldReview.combat",false)){TickCombatReview(rig,map,dir);return;}
   if(!opened){map.SetVisible(true);opened=true;}
   double elapsed=EditorApplication.timeSinceStartup-entered;
   int observed=map.Contacts.Count(c=>!c.Own&&!c.Missile);
   if(Environment.GetEnvironmentVariable("ACUNICOMBAT_TRAIL_REVIEW")=="1"&&rig.GetComponent<MechRadarController>().SimTimeSeconds<100&&elapsed<180)return;
   if((elapsed>15&&observed>=4)||elapsed>150)
   {
    if(++frames<4)return;
    var selected=map.Contacts.FirstOrDefault(c=>!c.Own&&c.Affiliation!=ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly&&!c.Missile);
    if(!string.IsNullOrEmpty(selected.Key))map.SelectContact(selected.Key);
    if(frames<8)return;
    if(SessionState.GetBool("LiveBattlefieldReview.rendering",false))
    {
     if(SessionState.GetBool("LiveBattlefieldReview.adaptive",false))
     {
      if(CaptureAdaptive(map,dir))return;
     }
     else
     {
     if(!zoomed)
     {
      map.Capture(dir+"/live-map.png");ScreenCapture.CaptureScreenshot(dir+"/game-view.png");
      var anchor=string.IsNullOrEmpty(selected.Key)?new Vector2(620,540):map.Project(selected.Position);
      map.ZoomAt(3,anchor);
      var beforePan=map.Project(selected.Position);map.BeginPan(new Vector2(500,400));map.MovePan(new Vector2(560,435));
      if(map.EndPan(new Vector2(560,435)))throw new Exception("Drag became a click");
      if(Vector2.Distance(map.Project(selected.Position),beforePan+new Vector2(60,35))>.01f)throw new Exception("Pan projection mismatch");
      zoomed=true;frames=0;
      File.WriteAllText(dir+"/rendering.txt",$"Screen={Screen.width}x{Screen.height}; source={map.SourceResolution}; viewport={map.Viewport}; output={map.Display.width}x{map.Display.height}; zoomRange={map.RangeKm}");
      if(map.SourceResolution.x!=map.Display.width*2||map.SourceResolution.y!=map.Display.height*2)throw new Exception("Source is not 2x output");
      return;
     }
     map.Capture(dir+"/zoomed-map.png");ScreenCapture.CaptureScreenshot(dir+"/zoomed-game-view.png");
     var p=map.Project(selected.Position);map.CaptureRegion(dir+"/detail-target.png",new Rect(p.x-35,p.y-35,350,220));
     var friend=map.Contacts.FirstOrDefault(c=>!c.Own&&c.Affiliation==ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly&&map.Project(c.Position).y>150&&map.Project(c.Position).y<650&&map.Project(c.Position).x>80&&map.Project(c.Position).x<800);
     if(!string.IsNullOrEmpty(friend.Key)){p=map.Project(friend.Position);map.CaptureRegion(dir+"/detail-friendly.png",new Rect(p.x-35,p.y-35,350,220));}
    }
    }
    else map.Capture(dir+"/live-map.png");
    File.WriteAllLines(dir+"/observed-contacts.txt",map.Contacts.Select(c=>$"{c.Key} {c.Name} {c.Affiliation} {c.Source} position={c.Position} velocity={c.Velocity} updated={c.Updated} confidence={c.Confidence}"));
    File.WriteAllText(dir+"/result.txt",$"Actual playable scene. Four real AI platforms validated. Map displays {observed} observed aircraft beyond ownship. Simulation time {rig.GetComponent<MechRadarController>().SimTimeSeconds:0.00}; capture wall seconds {elapsed:0.0}.");
    var ai=UnityEngine.Object.FindObjectsByType<MechAiTargetController>(FindObjectsInactive.Exclude);
    File.WriteAllLines(dir+"/ai-runtime.txt",ai.Select(a=>$"{a.name} enabled={a.enabled} position={a.transform.position} velocity={a.GetComponent<MechFlightMotionController>().Velocity}"));
    var identities=UnityEngine.Object.FindObjectsByType<MechIdentityController>(FindObjectsInactive.Exclude);
    File.WriteAllLines(dir+"/identity-runtime.txt",identities.Select(x=>$"{x.name} authenticated={x.AuthenticationCount} log={x.LogPath}"));
    foreach(var identity in identities)if(!string.IsNullOrEmpty(identity.LogPath)&&File.Exists(identity.LogPath))File.Copy(identity.LogPath,dir+"/identity-"+identity.Credential.Subject.ToString("N")+".jsonl",true);
    int moving=ai.Count(a=>a.GetComponent<MechFlightMotionController>().Velocity.magnitude>1);
    SessionState.SetInt("LiveBattlefieldReview.exit",observed>=4&&moving>=2?0:2);
    SessionState.SetString(Key,"finish");Debug.Log("LIVE_BATTLEFIELD_REVIEW_OK "+dir+" movingAI="+moving);Time.timeScale=1;EditorApplication.isPlaying=false;
   }
  }
  catch(Exception e){File.WriteAllText(dir+"/error.txt",e.ToString());SessionState.EraseString(Key);Debug.LogException(e);EditorApplication.Exit(3);}
 }
}
