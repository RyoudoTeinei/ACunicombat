using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
[InitializeOnLoad]
public static class MechColdLaunchLiveReview
{
 const string Key="ColdLaunchLive.phase";const BindingFlags P=BindingFlags.NonPublic|BindingFlags.Instance;
 static MechAssembler actor;static MechAssemblyRecord record;static Camera camera;static MechTestProjectile shot;static int frames;static double started;static string dir=>"Artifacts/ColdLaunchPlacement-20261026/"+(Environment.GetEnvironmentVariable("COLD_REVIEW_TAG")??"before");
 static MechColdLaunchLiveReview(){EditorApplication.update+=Tick;}
 public static void Run(){Directory.CreateDirectory(dir);EditorSceneManager.OpenScene("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");SessionState.SetString(Key,"play");EditorApplication.isPlaying=true;}
 static void Tick(){string phase=SessionState.GetString(Key,"");if(phase=="")return;if(phase=="exit"){if(!EditorApplication.isPlaying){SessionState.SetString(Key,"");EditorApplication.Exit(0);}return;}if(!EditorApplication.isPlaying)return;
 try{
 Time.timeScale=1;Time.captureDeltaTime=.02f;if(Time.timeAsDouble<5)return;
 if(actor==null){
  var all=Resources.FindObjectsOfTypeAll<MechAssembler>().Where(a=>a!=null&&a.gameObject.scene.IsValid()).ToArray();File.WriteAllLines(dir+"/live-actors.txt",all.Select(a=>a.name+" ai="+(a.GetComponent<MechAiTargetController>()!=null)));
  actor=all.First(a=>a.GetComponent<MechTestWeaponSystem>()!=null&&a.GetComponent<MechAiTargetController>()==null);
  typeof(MechAssemblyMenu).GetMethod("CloseAllForSolarMap",BindingFlags.NonPublic|BindingFlags.Static)?.Invoke(null,null);
  File.WriteAllLines(dir+"/original-parts.txt",actor.RuntimeState.AssembledParts.Select(r=>r.SlotId+" "+r.part.PartId));
  if(Environment.GetEnvironmentVariable("COLD_REVIEW_SAVED")!="1"){var loadout=UnityEngine.Object.Instantiate(actor.Loadout);var slot=loadout.GetSlots().First(x=>x.legacyRole==MechPartSlot.LeftBackWeapon);var assignment=loadout.Assignments.First(x=>x!=null&&x.SlotId==slot.id);assignment.part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_L_4302");actor.Loadout=loadout;actor.Assemble();}
  foreach(var ai in Resources.FindObjectsOfTypeAll<MechAiTargetController>())if(ai!=null&&ai.gameObject.scene.IsValid())ai.enabled=false;
  record=actor.RuntimeState.AssembledParts.First(r=>r.slot==MechPartSlot.LeftBackWeapon);camera=new GameObject("Launch review camera").AddComponent<Camera>();camera.enabled=false;camera.nearClipPlane=.1f;camera.farClipPlane=1000000;camera.fieldOfView=52;started=Time.realtimeSinceStartupAsDouble;return;
 }
 var motion=actor.GetComponent<MechFlightMotionController>();
 if(Environment.GetEnvironmentVariable("COLD_REVIEW_MOVE")=="1"){actor.GetComponent<MechPlayerInputAdapter>().SubmitExternalFrame(new MechPlayerInputFrame{RiseHeld=true});if(Time.realtimeSinceStartupAsDouble-started<4)return;}
 var radar=actor.GetComponent<MechRadarController>();radar.SetTrackOperationMode(true);typeof(MechEquipmentExecutionTests).GetMethod("SeedAirMeasurements",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{radar});
 if(shot==null){if(Time.realtimeSinceStartupAsDouble-started>60)throw new Exception("Launch timeout");if(radar.TryGetBestTrack(out var t)){radar.TryAssignFireControlTrack(t.TrackId);actor.GetComponent<MechTestWeaponSystem>().CommandFireSlot(record.SlotId,MechActionSource.Player,radar.Reference(t.TrackId));}shot=MechTestProjectile.Registry.FirstOrDefault(p=>p!=null&&p.IsOwnedBy(actor.transform));if(shot==null)return;}
 camera.transform.position=actor.transform.position+actor.transform.rotation*new Vector3(23,12,-20);camera.transform.LookAt(actor.transform.position+actor.transform.up*7);
 File.AppendAllText(dir+"/live-positions.txt",frames+" "+shot.MissilePhase+" velocity="+(motion!=null?motion.Velocity.ToString("F3"):"NONE")+" root="+actor.transform.position.ToString("F3")+" physics="+actor.transform.InverseTransformPoint(shot.transform.position).ToString("F3")+" visual="+actor.transform.InverseTransformPoint(shot.PresentationPosition).ToString("F3")+"\n");
 if(frames==0)foreach(var t in record.instance.GetComponentsInChildren<Transform>(true).Where(t=>t.name.Contains("dmy")))File.AppendAllText(dir+"/live-positions.txt",t.name+" "+actor.transform.InverseTransformPoint(t.position).ToString("F3")+"\n");
 if(frames%3==0)typeof(MechColdLaunchPlacementV2Tests).GetMethod("Capture",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{camera,dir+"/live-"+frames.ToString("D2")+".png"});
 if(++frames>=30){SessionState.SetString(Key,"exit");EditorApplication.isPlaying=false;}
 }catch(Exception e){File.WriteAllText(dir+"/error.txt",e.ToString());SessionState.SetString(Key,"");EditorApplication.Exit(1);}}
}
