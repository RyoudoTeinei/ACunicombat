using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace ACunicombat.Mechs
{
 public static class MechScenarioStore
 {
  [Serializable] public sealed class Equipment{public string slotId,partId,missileVariantId;public MechPartSlot slot;}
  [Serializable] public sealed class UnitEdit
  {
   public string key,templateKey,name,formationId="";public bool player;public int team,formationIndex;
   public Vector3 position;public Quaternion rotation=Quaternion.identity;public bool customLoadout;public List<Equipment> equipment=new();
  }
  [Serializable] public sealed class Document{public int version=2;public string scenarioId;public List<UnitEdit> units=new();}
  public static MechScenarioDefinition ActiveScenario {get;private set;}
  public static IReadOnlyList<UnitEdit> ActiveUnits=>activeDocument?.units;
  private static Document activeDocument;
#if UNITY_EDITOR
  public static string LegacyStorageRootOverride
  {
   get{var value=UnityEditor.SessionState.GetString("ACunicombat.ScenarioStore.TestRoot","");return string.IsNullOrEmpty(value)?null:value;}
   set=>UnityEditor.SessionState.SetString("ACunicombat.ScenarioStore.TestRoot",value??"");
  }
#else
  public static string LegacyStorageRootOverride {get;set;}
#endif
  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void Reset()
  {
   ActiveScenario=null;activeDocument=null;SceneManager.sceneLoaded-=ReconcileScene;SceneManager.sceneLoaded+=ReconcileScene;
  }
  public static string UnitKey(Transform unit){string key=unit.name;while(unit.parent!=null){unit=unit.parent;key=unit.name+"/"+key;}return key;}
  private static string PathFor(MechScenarioDefinition scenario)=>Path.Combine(LegacyStorageRootOverride??Application.persistentDataPath,"ScenarioEdits",Uri.EscapeDataString(scenario.scenarioId)+".json");
  public static bool HasSavedDocument(MechScenarioDefinition scenario)=>scenario!=null&&File.Exists(PathFor(scenario));
  public static MechScenarioUnit Template(MechScenarioDefinition scenario,string key)=>scenario?.units.FirstOrDefault(u=>u.key==key);
  public static UnitEdit Unit(Document document,string key)=>document?.units.FirstOrDefault(u=>u.key==key);
  public static Document Defaults(MechScenarioDefinition scenario)
  {
   var document=new Document{scenarioId=scenario.scenarioId};
   foreach(var team in scenario.units.GroupBy(u=>u.team))
   {
    int index=0;string formation="team-"+team.Key;
    foreach(var unit in team.OrderBy(u=>u.player?0:1).ThenBy(u=>u.name,StringComparer.Ordinal))
     document.units.Add(new UnitEdit{key=unit.key,templateKey=unit.key,name=unit.name,player=unit.player,team=unit.team,position=unit.position,rotation=unit.rotation,formationId=formation,formationIndex=index++});
   }
   return document;
  }
  public static Document Load(MechScenarioDefinition scenario,out string warning)
  {
   warning=null;var path=PathFor(scenario);if(!File.Exists(path))return Defaults(scenario);
   try
   {
    var saved=JsonUtility.FromJson<Document>(File.ReadAllText(path));if(saved==null||saved.scenarioId!=scenario.scenarioId||saved.units==null)throw new InvalidDataException("场景配置版本不匹配");
    if(saved.version==1)
    {
     var migrated=Defaults(scenario);
     foreach(var edit in migrated.units)
     {
      var old=saved.units.FirstOrDefault(u=>u.key==edit.key);if(old==null)continue;
      if(!Finite(old.position)||!Finite(old.rotation))throw new InvalidDataException("单位坐标无效");
      edit.position=old.position;edit.rotation=old.rotation;edit.customLoadout=old.customLoadout;edit.equipment=old.equipment??new List<Equipment>();
     }
     return migrated;
    }
    if(saved.version!=2)throw new InvalidDataException("场景配置版本不匹配");
    foreach(var edit in saved.units)if(string.IsNullOrWhiteSpace(edit.templateKey))edit.templateKey=edit.key;
    string error=Validate(scenario,saved,null,false);if(error!=null)throw new InvalidDataException(error);
    return saved;
   }catch(Exception e){warning="无法读取场景配置，使用默认值："+e.Message;return Defaults(scenario);}
  }
  private static bool Finite(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
  private static bool Finite(Quaternion q)=>float.IsFinite(q.x)&&float.IsFinite(q.y)&&float.IsFinite(q.z)&&float.IsFinite(q.w)&&q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w>.001f;
  private static string Validate(MechScenarioDefinition scenario,Document document,MechPartCatalog catalog,bool loadouts)
  {
   if(document==null||document.scenarioId!=scenario.scenarioId||document.units==null||document.units.Count==0)return "场景没有有效单位";
   var keys=new HashSet<string>();int players=0;
   foreach(var edit in document.units)
   {
    if(edit==null||string.IsNullOrWhiteSpace(edit.key)||!keys.Add(edit.key))return "单位标识为空或重复";
    var template=Template(scenario,edit.templateKey);if(template==null)return "单位模板不存在："+edit.key;
    if(!Finite(edit.position)||!Finite(edit.rotation))return "单位坐标无效："+edit.key;
    if(edit.team<=0)return "队伍编号无效："+edit.key;
    if(edit.player)players++;
    if(string.IsNullOrEmpty(edit.formationId)&&edit.formationIndex!=0)return "独立单位顺位必须为 0";
    if(loadouts&&!edit.player)
    {
     var loadout=BuildLoadout(template,edit,catalog);var valid=MechLoadoutValidator.Validate(loadout);Release(loadout);if(!valid.IsValid)return template.name+" 的配装无效";
    }
   }
   if(players!=1)return "场景必须且只能有一个玩家单位";
   foreach(var group in document.units.Where(u=>!string.IsNullOrEmpty(u.formationId)).GroupBy(u=>new{u.team,u.formationId}))
   {
    var indices=group.Select(u=>u.formationIndex).OrderBy(i=>i).ToArray();
    for(int i=0;i<indices.Length;i++)if(indices[i]!=i)return "编队顺位不连续："+group.Key.formationId;
   }
   return null;
  }
  public static void Save(MechScenarioDefinition scenario,Document document)
  {
   string error=Validate(scenario,document,null,false);if(error!=null)throw new InvalidDataException(error);
   var path=PathFor(scenario);Directory.CreateDirectory(Path.GetDirectoryName(path));var temp=path+".tmp";File.WriteAllText(temp,JsonUtility.ToJson(document,true));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
  }
  public static UnitEdit AddUnit(Document document,MechScenarioUnit template)
  {
   if(document?.units==null)throw new ArgumentNullException(nameof(document));
   if(template==null)throw new ArgumentNullException(nameof(template));
   if(template.player)throw new InvalidOperationException("玩家单位不能通过模板重复添加");
   var edit=new UnitEdit();
   edit.key=template.key+"/scenario-"+Guid.NewGuid().ToString("N");edit.templateKey=template.key;edit.name=template.name+" 增援";
   edit.team=template.team;edit.position=template.position+Vector3.right*2000;edit.rotation=template.rotation;edit.formationId="team-"+template.team;
   int formationIndex=0;foreach(var unit in document.units)if(unit!=null&&unit.team==edit.team&&unit.formationId==edit.formationId)formationIndex++;
   edit.formationIndex=formationIndex;return edit;
  }
  public static bool DeleteUnit(Document document,string key)
  {
   var edit=Unit(document,key);if(edit==null||edit.player)return false;document.units.Remove(edit);NormalizeFormations(document);return true;
  }
  public static void NormalizeFormations(Document document)
  {
   foreach(var group in document.units.Where(u=>!string.IsNullOrEmpty(u.formationId)).GroupBy(u=>new{u.team,u.formationId}))
    foreach(var pair in group.OrderBy(u=>u.formationIndex).Select((u,i)=>(u,i)))pair.u.formationIndex=pair.i;
   foreach(var unit in document.units.Where(u=>string.IsNullOrEmpty(u.formationId)))unit.formationIndex=0;
  }
  public static void AssignFormation(Document document,string key,string formationId)
  {
   var edit=Unit(document,key);if(edit==null)return;
   edit.formationId=formationId??"";NormalizeFormations(document);edit.formationIndex=string.IsNullOrEmpty(edit.formationId)?0:document.units.Count(u=>u.team==edit.team&&u.formationId==edit.formationId&&u.formationIndex<edit.formationIndex);
   NormalizeFormations(document);
  }
  public static void MoveUnit(Document document,string key,int delta)
  {
   var edit=Unit(document,key);if(edit==null||string.IsNullOrEmpty(edit.formationId))return;
   var group=document.units.Where(u=>u.team==edit.team&&u.formationId==edit.formationId).OrderBy(u=>u.formationIndex).ToList();
   int at=group.IndexOf(edit),target=Mathf.Clamp(at+delta,0,group.Count-1);if(at==target)return;
   group.RemoveAt(at);group.Insert(target,edit);for(int i=0;i<group.Count;i++)group[i].formationIndex=i;
  }
  public static MechLoadoutDefinition BuildLoadout(MechScenarioUnit unit,UnitEdit edit,MechPartCatalog catalog)
  {
   var result=UnityEngine.Object.Instantiate(unit.loadout);result.hideFlags=HideFlags.DontSave;
   if(!edit.customLoadout)return result;
   var known=unit.loadout.EnumerateEnabledAssignments().Where(a=>a.part!=null).GroupBy(a=>a.part.PartId).ToDictionary(g=>g.Key,g=>g.First().part);
   var assignments=new List<MechPartAssignment>();
   foreach(var entry in edit.equipment)
   {
    var part=known.TryGetValue(entry.partId,out var original)?original:catalog.Find(entry.partId)?.LoadPart();
    if(part==null){Release(result);throw new InvalidDataException("找不到配装部件："+entry.partId);}
    assignments.Add(new MechPartAssignment{slotId=entry.slotId,slot=entry.slot,part=part,missileVariantId=entry.missileVariantId});
   }
   result.ConfigureRuntime(unit.key+"-scenario",unit.name,assignments);return result;
  }
  public static void StoreLoadout(UnitEdit edit,MechLoadoutDefinition loadout)
  {
   edit.customLoadout=true;edit.equipment=loadout.EnumerateEnabledAssignments().Where(a=>a.part!=null).Select(a=>new Equipment{slotId=a.SlotId,slot=a.slot,partId=a.part.PartId,missileVariantId=a.missileVariantId}).ToList();
  }
  private static void Release(UnityEngine.Object value){if(Application.isPlaying)UnityEngine.Object.Destroy(value);else UnityEngine.Object.DestroyImmediate(value);}
  public static bool Begin(MechScenarioDefinition scenario,Document document,MechPartCatalog catalog,out string error)
  {
   error=Validate(scenario,document,catalog,true);if(error!=null)return false;
   ActiveScenario=scenario;activeDocument=JsonUtility.FromJson<Document>(JsonUtility.ToJson(document));return true;
  }
  public static bool ApplySpawn(MechAssembler assembler,out MechLoadoutDefinition loadout)
  {
   loadout=null;if(ActiveScenario==null||activeDocument==null||assembler.gameObject.scene.path!=ActiveScenario.scenePath)return false;
   var key=UnitKey(assembler.transform);var edit=Unit(activeDocument,key);if(edit==null)return false;
   var template=Template(ActiveScenario,edit.templateKey);if(template==null)return false;
   assembler.transform.SetPositionAndRotation(edit.position,edit.rotation);
   if(!edit.customLoadout)return false;
   loadout=BuildLoadout(template,edit,MechLoadoutSelectionStore.LoadCatalog());return true;
  }
  private static void ReconcileScene(Scene scene,LoadSceneMode mode)
  {
   if(ActiveScenario==null||activeDocument==null||scene.path!=ActiveScenario.scenePath)return;
   var catalog=MechLoadoutSelectionStore.LoadCatalog();
   var originals=UnityEngine.Object.FindObjectsByType<MechAssembler>(FindObjectsInactive.Exclude).Where(a=>a.gameObject.scene==scene).ToArray();
   var existing=originals.ToDictionary(a=>UnitKey(a.transform),a=>a);
   foreach(var edit in activeDocument.units)
   {
    var template=Template(ActiveScenario,edit.templateKey);if(template==null)continue;
    if(!existing.TryGetValue(edit.key,out var assembler))
    {
     var source=originals.FirstOrDefault(a=>UnitKey(a.transform)==edit.templateKey);if(source==null)continue;
     bool active=source.gameObject.activeSelf;source.gameObject.SetActive(false);
     assembler=UnityEngine.Object.Instantiate(source,source.transform.parent,true);source.gameObject.SetActive(active);
     assembler.name=edit.key.Substring(edit.key.LastIndexOf('/')+1);assembler.transform.SetPositionAndRotation(edit.position,edit.rotation);
     assembler.Loadout=BuildLoadout(template,edit,catalog);existing.Add(edit.key,assembler);
    }
    var rig=assembler.GetComponent<MechPlayerRig>();if(rig!=null&&!edit.player)rig.ConfigureAiControl();
    var body=assembler.GetComponent<CombatTestTarget>();if(body!=null)body.ConfigureIdentity(edit.name,edit.team);
    assembler.transform.SetPositionAndRotation(edit.position,edit.rotation);assembler.gameObject.SetActive(true);
   }
   foreach(var assembler in originals)
   {
    string key=UnitKey(assembler.transform);if(Unit(activeDocument,key)!=null)continue;
    UnityEngine.Object.Destroy(assembler.gameObject);
   }
  }
 }
}
