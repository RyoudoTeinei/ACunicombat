using System;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEditor;
using UnityEngine;
public static partial class MechLiveBattlefieldReview
{
 static bool combatSetup,combatShot,combatPassed;
 static int combatFrames,combatScenario;
 static double combatStarted,combatFireTime;
 static CombatTestTarget combatSource,combatVictim;
 static MechTestProjectile combatProjectile;
 static TrackId combatSourceTrack;
 public static void RunCombatReview(){SessionState.SetBool("LiveBattlefieldReview.combat",true);RunRenderingFixReview();}
 static void TickCombatReview(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  if(Environment.GetEnvironmentVariable("ACUNICOMBAT_COMBAT_CASE")=="5"){TickDefaultCombatReview(rig,map,dir);return;}
  var radar=rig.GetComponent<MechRadarController>();var identity=rig.GetComponent<MechIdentityController>();
  if(!combatSetup)
  {
   int.TryParse(Environment.GetEnvironmentVariable("ACUNICOMBAT_COMBAT_CASE"),out combatScenario);if(combatScenario==0)combatScenario=1;
   var ai=UnityEngine.Object.FindObjectsByType<MechAiTargetController>(FindObjectsInactive.Exclude);var bodies=ai.Select(x=>x.GetComponent<CombatTestTarget>()).ToArray();
   var friendly=bodies.Where(x=>x.TeamId==1).ToArray();var enemy=bodies.Where(x=>x.TeamId!=1).ToArray();
   combatSource=combatScenario==2?friendly[0]:enemy[0];combatVictim=combatScenario==3?friendly[0]:rig.GetComponent<CombatTestTarget>();
   foreach(var x in ai)x.enabled=false;
   foreach(var body in bodies.Append(rig.GetComponent<CombatTestTarget>()))
   {
    var flight=body.GetComponent<MechFlightMotionController>();if(flight!=null)flight.enabled=false;
    var weapons=body.GetComponent<MechTestWeaponSystem>();if(weapons!=null)weapons.enabled=false;
    var rb=body.GetComponent<Rigidbody>();if(rb!=null)rb.isKinematic=true;
    body.transform.position=new Vector3(60000+Array.IndexOf(bodies,body)*10000,10000,-50000);body.transform.rotation=Quaternion.identity;
    body.GetComponent<MechIdentityController>().CombatObservationsEnabled=false;
   }
   rig.transform.position=Vector3.up*10000;combatSource.transform.position=Vector3.up*10000+Vector3.forward*(combatScenario==2?3000:12000);
   if(combatScenario==3)combatVictim.transform.position=Vector3.up*10000+Vector3.forward*4000;
   combatVictim.GetComponent<MechIdentityController>().CombatObservationsEnabled=true;
   var opticalCamera=new GameObject("Controlled optical pointing").AddComponent<Camera>();opticalCamera.enabled=false;opticalCamera.transform.position=combatVictim.transform.position;opticalCamera.transform.LookAt(combatSource.transform.position);
   typeof(MechRadarController).GetField("aimingCamera",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(combatVictim.GetComponent<MechRadarController>(),opticalCamera);
   identity.CombatObservationsEnabled=combatScenario!=3;
   if(combatScenario==4){var wall=GameObject.CreatePrimitive(PrimitiveType.Cube);wall.name="Occlusion validation wall";wall.transform.position=Vector3.up*10000+Vector3.forward*6000;wall.transform.localScale=new Vector3(3000,3000,100);Physics.SyncTransforms();}
   map.SetVisible(true);map.ResetView();combatStarted=EditorApplication.timeSinceStartup;combatSetup=true;return;
  }
  if(EditorApplication.timeSinceStartup-combatStarted>80)throw new Exception("Combat identity scenario timeout. scenario="+combatScenario+" reports="+identity.ConfirmedAttackReports);
  var candidates=new TrackSnapshot[96];int count=radar.CopyTracks(candidates);
  if(!combatShot)
  {
   int match=-1;for(int i=0;i<count;i++)if(candidates[i].Lifecycle==TrackLifecycle.Confirmed&&Vector3.Distance((Vector3)(Unity.Mathematics.float3)candidates[i].PredictPosition(radar.SimTimeSeconds),combatSource.transform.position)<150)match=i;
   if(match<0||Time.timeAsDouble<4)return;
   if(combatScenario==2&&candidates[match].Affiliation!=TrackAffiliation.Friendly)return;
   if(combatScenario==3)
   {
    var victimRadar=combatVictim.GetComponent<MechRadarController>();var other=new TrackSnapshot[96];int length=victimRadar.CopyTracks(other);
    if(!other.Take(length).Any(t=>t.Lifecycle==TrackLifecycle.Confirmed&&Vector3.Distance((Vector3)(Unity.Mathematics.float3)t.PredictPosition(victimRadar.SimTimeSeconds),combatSource.transform.position)<150))return;
   }
   combatSourceTrack=candidates[match].TrackId;
   File.WriteAllText(dir+"/sensor.txt",combatVictim.GetComponent<MechRadarController>().CombatObservationDiagnostic(combatSource.transform.position));
   var direction=(combatVictim.transform.position-combatSource.transform.position).normalized;
   var go=new GameObject("Controlled actual hostile projectile");go.transform.position=combatSource.transform.position+direction*30;go.transform.forward=direction;
   combatProjectile=go.AddComponent<MechTestProjectile>();
   combatProjectile.Configure(combatSource.transform,null,default,combatScenario==2?TestWeaponKind.Rifle:TestWeaponKind.Missile,direction*(combatScenario==2?500:1500),10,.1f,30,newSeekerActivationDelay:100,hasInitialGuidanceEstimate:true,initialGuidancePosition:combatVictim.transform.position,initialGuidanceUncertaintyMeters:1);
   combatFireTime=Time.timeAsDouble;combatShot=true;
   File.WriteAllText(dir+"/launch.txt",$"scenario={combatScenario}; observedSource={combatSourceTrack}; source={combatSource.transform.position}; victim={combatVictim.transform.position}; playerOpticalEnabled={identity.CombatObservationsEnabled}");return;
  }
  if(combatScenario==4&&!combatPassed)
  {
   if(Time.timeAsDouble-combatFireTime<5)return;
   if(identity.ConfirmedAttackReports!=0||radar.TryGetTrack(combatSourceTrack,out var blocked)&&blocked.Identity.Relation==IdentityRelation.Hostile)throw new Exception("Unobserved launch incorrectly attributed through wall");
   File.WriteAllText(dir+"/result.txt","PASS case=4; actual opaque wall blocks launch observation; no hostile attribution after 5 seconds");Time.timeScale=0;combatPassed=true;combatFrames=0;return;
  }
  if(!combatPassed)
  {
   if(!radar.TryGetTrack(combatSourceTrack,out var track)||track.Identity.Relation!=IdentityRelation.Hostile)return;
   if(combatScenario==2&&!track.Identity.Conflict)throw new Exception("Friendly authentication conflict was lost");
   if(combatProjectile==null)throw new Exception("Hostility was only detected after projectile disappearance");
   float remaining=Vector3.Distance(combatProjectile.transform.position,combatVictim.transform.position);if(remaining<50)throw new Exception("Attack evidence arrived too late");
   File.WriteAllText(dir+"/result.txt",$"PASS case={combatScenario}; hostile before impact; secondsSinceLaunch={Time.timeAsDouble-combatFireTime:F3}; remainingDistance={remaining:F1}m; conflict={track.Identity.Conflict}; canAttack={track.Identity.CanAttackPlatform}; confirmedReports={identity.ConfirmedAttackReports}");
   Time.timeScale=0;map.SelectContact("track/"+combatSourceTrack.Packed);map.ZoomAt(Mathf.Log(2)/.16f,map.Project(combatSource.transform.position));map.Refresh();combatPassed=true;combatFrames=0;return;
  }
  Time.timeScale=0;if(++combatFrames<5)return;
  map.Capture(dir+"/combat-identity.png");
  foreach(var source in UnityEngine.Object.FindObjectsByType<MechIdentityController>(FindObjectsInactive.Exclude))if(!string.IsNullOrEmpty(source.LogPath)&&File.Exists(source.LogPath))File.Copy(source.LogPath,dir+"/identity-"+source.Credential.Subject.ToString("N")+".jsonl",true);
  SessionState.SetBool("LiveBattlefieldReview.combat",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");Time.timeScale=1;EditorApplication.isPlaying=false;
 }
 static double defaultNextRecord;
 static void TickDefaultCombatReview(MechPlayerRig rig,MechBattlefieldMap map,string dir)
 {
  var radar=rig.GetComponent<MechRadarController>();var identity=rig.GetComponent<MechIdentityController>();
  if(!combatSetup){combatSetup=true;combatStarted=EditorApplication.timeSinceStartup;File.WriteAllText(dir+"/fixture.txt","Unmodified default scene positions, active AI, loadouts, sensor parameters and ordinary player free-look toward AI-MECH-B. No spawned test projectiles.\n");}
  // Exercise ordinary player free-look toward an actual AI. No sensor or weapon override.
  var enemy=UnityEngine.Object.FindObjectsByType<CombatTestTarget>(FindObjectsInactive.Exclude).FirstOrDefault(x=>x.TargetId=="AI-MECH-B");
  var camera=rig.GetComponentInChildren<MechThirdPersonCamera>();
  if(camera==null)camera=UnityEngine.Object.FindFirstObjectByType<MechThirdPersonCamera>();
  if(enemy!=null&&camera!=null)
  {
   var local=camera.transform.InverseTransformDirection(enemy.transform.position-camera.transform.position);
   float yaw=Mathf.Atan2(local.x,local.z)*Mathf.Rad2Deg;float pitch=Mathf.Atan2(local.y,Mathf.Sqrt(local.x*local.x+local.z*local.z))*Mathf.Rad2Deg;
   rig.GetComponent<MechPlayerInputAdapter>().SubmitExternalFrame(new MechPlayerInputFrame{FreeLookHeld=true,MouseDelta=new Vector2(yaw,pitch)*2});
  }
  if(!File.Exists(dir+"/observer.txt"))File.WriteAllText(dir+"/observer.txt",$"player={identity.Credential?.Subject}; log={identity.LogPath}");
  var ts=new TrackSnapshot[96];int n=radar.CopyTracks(ts);
  if(Time.timeAsDouble>=defaultNextRecord)
  {
   defaultNextRecord=Time.timeAsDouble+1;
   File.AppendAllLines(dir+"/timeline.txt",ts.Take(n).Select(t=>$"t={Time.timeAsDouble:F2} track={t.TrackId} pos={t.Position} covariance={t.GetCovariance(0,0):F1} identity={t.Identity.Relation} reason={t.Identity.Reason} reports={identity.ConfirmedAttackReports}"));
  }
  if(!combatPassed)
  {
   if(Time.timeAsDouble>=30&&identity.ConfirmedAttackReports>0&&ts.Take(n).Any(t=>t.Identity.Relation==IdentityRelation.Hostile))
   {
    foreach(var body in UnityEngine.Object.FindObjectsByType<CombatTestTarget>(FindObjectsInactive.Exclude))
    {
     if(body.gameObject==rig.gameObject)continue;
     var matched=ts.Take(n).Where(t=>t.ObjectClass!=TrackObjectClass.Missile).OrderBy(t=>Vector3.Distance((Vector3)(Unity.Mathematics.float3)t.PredictPosition(radar.SimTimeSeconds),body.transform.position)).FirstOrDefault();
     if(body.TeamId==1&&matched.Identity.Relation!=IdentityRelation.Friendly)throw new Exception("Authenticated friendly platform lost friendly status: "+matched.Identity.Relation);
    }
    combatPassed=true;combatFrames=0;map.SetVisible(true);map.Refresh();
    var selected=map.Contacts.FirstOrDefault(c=>!c.Missile&&c.Affiliation==TrackAffiliation.Hostile);if(selected.Key!=null)map.SelectContact(selected.Key);
    map.BeginPan(new Vector2(700,400));map.MovePan(new Vector2(700,550));map.EndPan(new Vector2(700,550));
    File.WriteAllText(dir+"/result.txt",$"PASS default scene live AI attacks; time={Time.timeAsDouble:F2}; confirmedReports={identity.ConfirmedAttackReports}; no position/sensor/weapon overrides");
   }
   else if(Time.timeAsDouble>150||EditorApplication.timeSinceStartup-combatStarted>350)
   {
    foreach(var x in UnityEngine.Object.FindObjectsByType<MechIdentityController>(FindObjectsInactive.Exclude))if(!string.IsNullOrEmpty(x.LogPath)&&File.Exists(x.LogPath))File.Copy(x.LogPath,dir+"/identity-"+x.Credential.Subject.ToString("N")+".jsonl",true);
    throw new Exception("Default opening failed to identify live attacker; reports="+identity.ConfirmedAttackReports);
   }
   else return;
  }
  Time.timeScale=0;if(++combatFrames<5)return;
  map.Capture(dir+"/default-combat.png");
  foreach(var x in UnityEngine.Object.FindObjectsByType<MechIdentityController>(FindObjectsInactive.Exclude))if(!string.IsNullOrEmpty(x.LogPath)&&File.Exists(x.LogPath))File.Copy(x.LogPath,dir+"/identity-"+x.Credential.Subject.ToString("N")+".jsonl",true);
  SessionState.SetBool("LiveBattlefieldReview.combat",false);SessionState.SetInt("LiveBattlefieldReview.exit",0);SessionState.SetString(Key,"finish");Time.timeScale=1;EditorApplication.isPlaying=false;
 }

}
