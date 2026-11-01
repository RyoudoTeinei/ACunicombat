using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public class MechLaunchAndScenarioEntryTests
{
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 private static string IsolateCampaignStorage()
 {
  string root=System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(),"Temp","MechLaunchTests",Guid.NewGuid().ToString("N"));
  MechCampaignStore.StorageRootOverride=System.IO.Path.Combine(root,"campaign");
  MechScenarioStore.LegacyStorageRootOverride=System.IO.Path.Combine(root,"legacy");
  return root;
 }
 private static void RestoreCampaignStorage(string root)
 {
  MechCampaignStore.StorageRootOverride=null;MechScenarioStore.LegacyStorageRootOverride=null;
  if(System.IO.Directory.Exists(root))System.IO.Directory.Delete(root,true);
 }
 [Test] public void ScenarioEditorOwnshipPreviewUsesEditedCoordinates()
 {
  var host=new GameObject("Scenario preview");
  try
  {
   var map=host.AddComponent<MechBattlefieldMap>();
   var contact=new MechBattlefieldMap.Contact{Own=true,Position=new Vector3(-54957,0,-31990)};
   var mode=typeof(MechBattlefieldMap).GetField("scenarioContacts",Private);
   mode.SetValue(map,(Func<IEnumerable<MechBattlefieldMap.Contact>>)(()=>Array.Empty<MechBattlefieldMap.Contact>()));
   var present=typeof(MechBattlefieldMap).GetMethod("PresentationPosition",Private);
   Assert.AreEqual(contact.Position,(Vector3)present.Invoke(map,new object[]{contact,0d}));
   mode.SetValue(map,null);
   Assert.AreEqual(host.transform.position,(Vector3)present.Invoke(map,new object[]{contact,0d}),"Live battlefield ownship still uses local navigation.");
  }
  finally{UnityEngine.Object.DestroyImmediate(host);}
 }
 sealed class ScenarioProbe
 {
  public string Path;public Dictionary<string,Vector3> Actual;public Dictionary<string,MechPlayerRig> Actors;
  public void Loaded(Scene scene,LoadSceneMode mode)
  {
   if(scene.path!=Path)return;Actual=new Dictionary<string,Vector3>();Actors=new Dictionary<string,MechPlayerRig>();
   foreach(var actor in UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include))
   {
    if(actor.gameObject.scene!=scene)continue;
    string key=MechScenarioStore.UnitKey(actor.transform);Actual[key]=actor.transform.position;Actors[key]=actor;
   }
  }
 }
 [TestCase(false)] [TestCase(true)]
 public void ColdLaunchPolicySeparatesForwardAirToAirFromInterceptorAlignment(bool interceptor)
 {
  var owner=new GameObject("Cold launch owner");var body=new GameObject("Cold missile");
  try
  {
   Vector3 axis=Quaternion.Euler(-15,0,0)*Vector3.forward;
   body.transform.position=owner.transform.position+axis*2;body.transform.rotation=Quaternion.LookRotation(axis);
   var audio=body.AddComponent<MechMissileAudioController>();typeof(MechMissileAudioController).GetMethod("Awake",Private).Invoke(audio,null);
   var projectile=body.AddComponent<MechTestProjectile>();
   projectile.Configure(owner.transform,null,default,TestWeaponKind.Missile,axis*40,10,.2f,20,
       newInterceptorMissile:interceptor,missileProfile:MissileFlightProfile.AirToAirDefault,
       hasInitialGuidanceEstimate:true,initialGuidancePosition:new Vector3(0,0,10000),
       newPrelaunchInterceptPoint:new Vector3(0,100000,1000),newColdLaunch:true,
       newColdLaunchSafeSeparationSeconds:.2f,newColdLaunchSafeSeparationMeters:8,
       newColdLaunchAlignmentTimeoutSeconds:.6f);
   typeof(MechTestProjectile).GetField("missileAudio",Private).SetValue(projectile,null);
   var step=typeof(MechTestProjectile).GetMethod("UpdateColdLaunchSequence",Private);
   for(int i=0;i<9;i++){body.transform.position+=axis*.8f;step.Invoke(projectile,new object[]{.02f});Assert.AreEqual(MissileFlightPhase.ColdEjection,projectile.MissilePhase);}
   for(int i=0;i<20&&projectile.MissilePhase==MissileFlightPhase.ColdEjection;i++){body.transform.position+=axis*.8f;step.Invoke(projectile,new object[]{.02f});}
   Assert.AreEqual(interceptor?MissileFlightPhase.AttitudeAlignment:MissileFlightPhase.Boost,projectile.MissilePhase);
   if(!interceptor)Assert.Less(Vector3.Angle(axis,body.transform.forward),.01f,"Air-to-air cold ejection must not pitch to a far-future intercept before ignition.");
  }
  finally{UnityEngine.Object.DestroyImmediate(body);UnityEngine.Object.DestroyImmediate(owner);}
 }
 [Test] public void ScenarioDocumentSupportsAddDeleteAndFormationOrdering()
 {
  var scenario=Resources.LoadAll<MechScenarioDefinition>("Scenarios")[0];var document=MechScenarioStore.Defaults(scenario);
  var template=scenario.units.First(u=>!u.player);int originalCount=document.units.Count;var added=MechScenarioStore.AddUnit(document,template);document.units.Add(added);
  Assert.AreEqual(originalCount+1,document.units.Count);Assert.AreEqual(template.key,added.templateKey);Assert.IsTrue(added.key!=template.key);
  MechScenarioStore.AssignFormation(document,added.key,"");Assert.AreEqual(0,added.formationIndex);
  MechScenarioStore.AssignFormation(document,added.key,"team-"+added.team);int before=added.formationIndex;
  MechScenarioStore.MoveUnit(document,added.key,-1);Assert.Less(added.formationIndex,before);
  Assert.IsTrue(MechScenarioStore.DeleteUnit(document,added.key));Assert.AreEqual(originalCount,document.units.Count);Assert.IsNull(MechScenarioStore.Unit(document,added.key));
 }
 [UnityTest] public IEnumerator ScenarioAddUnitWorksInPlayMode()
 {
  yield return new EnterPlayMode();var scenario=Resources.LoadAll<MechScenarioDefinition>("Scenarios")[0];var document=MechScenarioStore.Defaults(scenario);
  var added=MechScenarioStore.AddUnit(document,scenario.units.First(u=>!u.player));document.units.Add(added);Assert.IsNotNull(added);Assert.IsTrue(document.units.Contains(added));
  yield return new ExitPlayMode();
 }
 [UnityTest] public IEnumerator AddedAndDeletedScenarioUnitsReachActualBattle()
 {
  string root=IsolateCampaignStorage();
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();yield return null;
  var scenario=Resources.LoadAll<MechScenarioDefinition>("Scenarios")[0];var document=MechScenarioStore.Defaults(scenario);
  var removed=document.units.First(u=>!u.player);string removedKey=removed.key;
  var template=MechScenarioStore.Template(scenario,removed.templateKey);Assert.IsNotNull(template);
  var added=MechScenarioStore.AddUnit(document,template);
  Assert.IsNotNull(added);
  document.units.Add(added);
  added.name="TEST_REINFORCEMENT";
  added.position=new Vector3(23456,6000,-21000);
  Assert.IsNotNull(removed);
  added.formationId=removed.formationId;
  var independent=MechScenarioStore.AddUnit(document,template);
  Assert.IsNotNull(independent);
  document.units.Add(independent);
  independent.name="TEST_INDEPENDENT";
  independent.formationId="";
  independent.position=new Vector3(25000,7000,-22000);
  document.units.Remove(removed);MechScenarioStore.NormalizeFormations(document);
  var probe=new ScenarioProbe{Path=scenario.scenePath};SceneManager.sceneLoaded+=probe.Loaded;
  try
  {
   Assert.IsTrue(MechScenarioStore.Begin(scenario,document,MechLoadoutSelectionStore.LoadCatalog(),out var beginError),beginError);SceneManager.LoadSceneAsync(scenario.scenePath);
   float timeout=Time.realtimeSinceStartup+40;while(probe.Actual==null&&Time.realtimeSinceStartup<timeout)yield return null;
   Assert.IsNotNull(probe.Actual);yield return null;probe.Loaded(SceneManager.GetActiveScene(),LoadSceneMode.Single);
   Assert.IsFalse(probe.Actors.ContainsKey(removedKey),"Deleted unit reached the battle");Assert.IsTrue(probe.Actors.ContainsKey(added.key),"Added unit did not spawn");Assert.IsTrue(probe.Actors.ContainsKey(independent.key),"Independent unit did not spawn");
   var actor=probe.Actors[added.key];Assert.IsTrue(actor.AiControlled);Assert.AreEqual(added.team,actor.GetComponent<CombatTestTarget>().TeamId);Assert.AreEqual(added.name,actor.GetComponent<CombatTestTarget>().TargetId);
   Assert.Less(Vector3.Distance(added.position,actor.transform.position),.01f);
   var fleet=actor.GetComponent<MechFleetController>();Assert.IsTrue(fleet.Configured);
   var roster=MechFleetController.Roster[fleet.MemberId];Assert.AreNotEqual(System.Guid.Empty,roster.FormationRoot);Assert.AreEqual(document.units.Count(u=>u.team==added.team&&u.formationId==added.formationId),roster.FormationSize);
   var independentRoster=MechFleetController.Roster[probe.Actors[independent.key].GetComponent<MechFleetController>().MemberId];Assert.AreEqual(1,independentRoster.FormationSize);Assert.AreEqual(independentRoster.Member,independentRoster.FormationRoot);
  }
  finally{SceneManager.sceneLoaded-=probe.Loaded;RestoreCampaignStorage(root);}
  yield return new ExitPlayMode();
 }
 [UnityTest] public IEnumerator MainMenuSceneEditsReachActualBattleActors()
 {
  string root=IsolateCampaignStorage();
  EditorSceneManager.OpenScene(MechFrontEndBuilder.MenuScene);yield return new EnterPlayMode();yield return null;
  var menu=UnityEngine.Object.FindAnyObjectByType<MechFrontEnd>();Assert.IsNotNull(menu);
  var scenarioField=typeof(MechFrontEnd).GetField("scenario",Private);
  Assert.IsNotNull(scenarioField,"Runtime fields: "+string.Join(",",typeof(MechFrontEnd).GetFields(Private|BindingFlags.Public).Select(f=>f.Name))+"; assembly="+typeof(MechFrontEnd).Assembly.Location);
  var scenario=(MechScenarioDefinition)scenarioField.GetValue(menu);
  Assert.IsNotNull(scenario,"Menu Awake has not selected a scenario; available="+Resources.LoadAll<MechScenarioDefinition>("Scenarios").Length);
  var document=(MechScenarioStore.Document)typeof(MechFrontEnd).GetField("document",Private).GetValue(menu);
  var campaign=(MechCampaignStore.Slot)typeof(MechFrontEnd).GetField("campaign",Private).GetValue(menu);
  Assert.IsNotNull(document,"Menu has no editable scenario document.");
  var expected=new Dictionary<string,Vector3>();int index=0;
  foreach(var edit in document.units){edit.position=new Vector3(12345+index*2000,5000+index*1000,-20000-index*3000);expected.Add(edit.key,edit.position);index++;}
  // A local-function closure allocated before EnterPlayMode is not restored by
  // the test runner's domain reload. Create the callback owner after entering.
  var probe=new ScenarioProbe{Path=scenario.scenePath};
  SceneManager.sceneLoaded+=probe.Loaded;
  try
  {
   typeof(MechFrontEnd).GetMethod("StartBattle",Private).Invoke(menu,null);
   Assert.IsTrue((bool)typeof(MechFrontEnd).GetField("loading",Private).GetValue(menu),(string)typeof(MechFrontEnd).GetField("message",Private).GetValue(menu));
   float timeout=Time.realtimeSinceStartup+40;
   while(probe.Actual==null&&Time.realtimeSinceStartup<timeout)yield return null;
   Assert.IsNotNull(probe.Actual,"Battle scene did not load.");Assert.AreEqual(expected.Count,probe.Actual.Count);
   foreach(var pair in expected){Assert.IsTrue(probe.Actual.ContainsKey(pair.Key),pair.Key);Assert.Less(Vector3.Distance(pair.Value,probe.Actual[pair.Key]),.01f,pair.Key);}
   yield return null;
   foreach(var actor in UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include))
    Assert.Less(Vector3.Distance(expected[MechScenarioStore.UnitKey(actor.transform)],actor.transform.position),1000,"Normal Start/Update must not restore authored spawn positions.");
   Assert.IsTrue(MechCampaignStore.TryLoad(campaign.slot,out var savedSlot,out var warning),warning);
   var saved=MechCampaignStore.Current(savedSlot).document;
   Assert.IsTrue(MechScenarioStore.Begin(scenario,saved,MechLoadoutSelectionStore.LoadCatalog(),out var error),error);
   probe.Actual=null;SceneManager.LoadSceneAsync(scenario.scenePath);
   timeout=Time.realtimeSinceStartup+40;while(probe.Actual==null&&Time.realtimeSinceStartup<timeout)yield return null;
   Assert.IsNotNull(probe.Actual);
   foreach(var edit in saved.units)Assert.Less(Vector3.Distance(edit.position,probe.Actual[edit.key]),.01f,"Saved entry: "+edit.key);
   yield return null;
   foreach(var actor in UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include))
   {
    var key=MechScenarioStore.UnitKey(actor.transform);var edit=saved.units.Find(e=>e.key==key);
    Assert.Less(Vector3.Distance(edit.position,actor.transform.position),1000,"Saved placement must survive normal startup.");
   }
  }
  finally{SceneManager.sceneLoaded-=probe.Loaded;RestoreCampaignStorage(root);}
  yield return new ExitPlayMode();
 }
 [UnityTest] public IEnumerator ColdAamHoldsEjectionAxisUntilMotorStartsAt30And60Fps()
 {
  EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
  float oldScale=Time.timeScale,oldCapture=Time.captureDeltaTime,oldFixed=Time.fixedDeltaTime;
  GameObject body=null;
  try
  {
   Time.timeScale=1;Time.fixedDeltaTime=.02f;
   foreach(int fps in new[]{30,60})
   {
    Time.captureDeltaTime=1f/fps;
    var axis=Quaternion.Euler(-15,0,0)*Vector3.forward;var inherited=Vector3.right*2000;
    body=new GameObject("Live cold AAM");body.transform.position=new Vector3(100,1000,0);body.transform.rotation=Quaternion.LookRotation(axis);
    var missile=body.AddComponent<MechTestProjectile>();
    missile.Configure(null,null,default,TestWeaponKind.Missile,inherited+axis*40,10,.2f,20,
      missileProfile:MissileFlightProfile.AirToAirDefault,hasInitialGuidanceEstimate:true,
      initialGuidancePosition:new Vector3(100,1000,10000),initialGuidanceVelocity:Vector3.up*1000,
      newPrelaunchInterceptPoint:new Vector3(100,101000,1000),newColdLaunch:true,newColdLaunchInheritedVelocity:inherited,
      newColdLaunchSafeSeparationSeconds:.2f,newColdLaunchSafeSeparationMeters:8);
    var ignition=typeof(MechTestProjectile).GetField("coldLaunchMotorIgnited",Private);double birth=Time.timeAsDouble;int steps=0;
    while(!(bool)ignition.GetValue(missile)&&steps++<50)
    {
     Assert.AreEqual(MissileFlightPhase.ColdEjection,missile.MissilePhase);
     Assert.Less(Vector3.Angle(axis,body.transform.forward),.02f);
     yield return new WaitForFixedUpdate();
    }
    Assert.IsTrue((bool)ignition.GetValue(missile));
    Assert.GreaterOrEqual(Time.fixedTimeAsDouble-birth,.44);Assert.Less(Time.fixedTimeAsDouble-birth,.6);
    TestContext.WriteLine($"{fps} FPS: ignition after {Time.fixedTimeAsDouble-birth:F3}s; ejection axis held before ignition.");
    UnityEngine.Object.DestroyImmediate(body);body=null;
   }
  }
  finally{if(body!=null)UnityEngine.Object.DestroyImmediate(body);Time.timeScale=oldScale;Time.captureDeltaTime=oldCapture;Time.fixedDeltaTime=oldFixed;}
  yield return new ExitPlayMode();
 }
}
