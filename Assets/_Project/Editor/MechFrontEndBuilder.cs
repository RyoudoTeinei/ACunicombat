using System;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class MechFrontEndBuilder
{
 public const string MenuScene="Assets/_Project/Scenes/MainMenu.unity";
 public const string BattleScene="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
 private const string ScenarioAsset="Assets/_Project/Resources/Scenarios/CurrentBattlefield.asset";
 [MenuItem("Tools/ACunicombat/Build Main Menu and Scenario Editor")]
 public static void Build()
 {
  if(!Application.isBatchMode&&!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())return;
  var battle=EditorSceneManager.OpenScene(BattleScene,OpenSceneMode.Single);
  var actors=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include).Where(r=>r.gameObject.scene==battle).OrderBy(r=>r.GetComponent<MechAiTargetController>()!=null?1:0).ThenBy(r=>r.name).ToArray();
  var definition=AssetDatabase.LoadAssetAtPath<MechScenarioDefinition>(ScenarioAsset);
  if(definition==null){Directory.CreateDirectory(Path.GetDirectoryName(ScenarioAsset));definition=ScriptableObject.CreateInstance<MechScenarioDefinition>();AssetDatabase.CreateAsset(definition,ScenarioAsset);}
  definition.scenarioId="current-battlefield";definition.displayName="当前战场";definition.scenePath=BattleScene;definition.units.Clear();
  foreach(var actor in actors)
  {
   var assembler=actor.GetComponent<MechAssembler>();var body=actor.GetComponent<CombatTestTarget>();if(assembler==null||assembler.Loadout==null)continue;
   bool player=actor.GetComponent<MechAiTargetController>()==null;
   definition.units.Add(new MechScenarioUnit{key=MechScenarioStore.UnitKey(actor.transform),name=player?"本机":body!=null?body.TargetId:actor.name,player=player,team=body!=null?body.TeamId:0,position=actor.transform.position,rotation=actor.transform.rotation,loadout=assembler.Loadout});
  }
  if(definition.units.Count(u=>u.player)!=1||definition.units.Select(u=>u.key).Distinct().Count()!=definition.units.Count)throw new InvalidOperationException("Scenario requires one player and unique unit keys.");
  EditorUtility.SetDirty(definition);AssetDatabase.SaveAssets();
  var menu=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
  new GameObject("Main Menu",typeof(MechFrontEnd));var camera=new GameObject("Main Camera",typeof(Camera),typeof(AudioListener)).GetComponent<Camera>();camera.tag="MainCamera";camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.015f,.025f,.021f);camera.transform.position=new Vector3(0,0,-10);
  Directory.CreateDirectory(Path.GetDirectoryName(MenuScene));EditorSceneManager.SaveScene(menu,MenuScene);
  var scenes=new[]{new EditorBuildSettingsScene(MenuScene,true),new EditorBuildSettingsScene(BattleScene,true)}.Concat(EditorBuildSettings.scenes.Where(s=>s.path!=MenuScene&&s.path!=BattleScene));EditorBuildSettings.scenes=scenes.ToArray();
  AssetDatabase.SaveAssets();Debug.Log("FRONTEND_BUILT units="+definition.units.Count+" scene="+MenuScene);
 }
 [MenuItem("Tools/ACunicombat/Open Main Menu")]
 public static void OpenMenu(){if(EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())EditorSceneManager.OpenScene(MenuScene);}
 [MenuItem("Tools/ACunicombat/Open Battle Directly")]
 public static void OpenBattle(){if(EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())EditorSceneManager.OpenScene(BattleScene);}
 public static void ValidateConfiguration()
 {
  var definition=AssetDatabase.LoadAssetAtPath<MechScenarioDefinition>(ScenarioAsset);
  if(definition==null)throw new InvalidOperationException("Missing scenario asset");
  var catalog=MechLoadoutSelectionStore.LoadCatalog();
  var document=MechScenarioStore.Defaults(definition);
  foreach(var unit in definition.units)
  {
   var result=MechLoadoutValidator.Validate(unit.loadout);
   if(!result.IsValid)throw new InvalidOperationException(unit.name+": "+string.Join("; ",result.Issues.Select(i=>i.message)));
   var edit=document.units.First(e=>e.key==unit.key);
   MechScenarioStore.StoreLoadout(edit,unit.loadout);
   var clone=MechScenarioStore.BuildLoadout(unit,edit,catalog);
   if(!clone.EnumerateEnabledAssignments().Select(a=>a.SlotId+":"+a.part.PartId+":"+a.missileVariantId).SequenceEqual(unit.loadout.EnumerateEnabledAssignments().Select(a=>a.SlotId+":"+a.part.PartId+":"+a.missileVariantId)))throw new InvalidOperationException("Loadout roundtrip failed: "+unit.name);
   UnityEngine.Object.DestroyImmediate(clone);
  }
  var scene=EditorSceneManager.OpenScene(BattleScene);
  // Opening a scene in Edit Mode unloads assets not referenced by that scene.
  definition=AssetDatabase.LoadAssetAtPath<MechScenarioDefinition>(ScenarioAsset);
  catalog=MechLoadoutSelectionStore.LoadCatalog();
  if(!MechScenarioStore.Begin(definition,document,catalog,out var error))throw new InvalidOperationException(error);
  foreach(var assembler in UnityEngine.Object.FindObjectsByType<MechAssembler>(FindObjectsInactive.Include).Where(a=>a.gameObject.scene==scene))
  {
   var unit=definition.units.FirstOrDefault(u=>u.key==MechScenarioStore.UnitKey(assembler.transform));if(unit==null)continue;
   assembler.transform.position+=new Vector3(123,456,789);
   bool overridden=MechScenarioStore.ApplySpawn(assembler,out var loadout);
   if(Vector3.Distance(assembler.transform.position,unit.position)>.001f||!overridden)throw new InvalidOperationException("Spawn application failed: "+unit.name+" position="+assembler.transform.position.ToString("F6")+" expected="+unit.position.ToString("F6")+" overridden="+overridden+" player="+unit.player);
   if(loadout!=null)UnityEngine.Object.DestroyImmediate(loadout);
  }
  EditorSceneManager.OpenScene(MenuScene);
  if(EditorBuildSettings.scenes[0].path!=MenuScene)throw new InvalidOperationException("Wrong startup scene");
  Debug.Log("FRONTEND_CONFIGURATION_PASS: default validation, per-unit equipment roundtrip, spawn placement and startup order");
 }
}
