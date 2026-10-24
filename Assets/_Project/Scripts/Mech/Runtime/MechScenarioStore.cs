using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
namespace ACunicombat.Mechs
{
 public static class MechScenarioStore
 {
  [Serializable] public sealed class Equipment{public string slotId,partId,missileVariantId;public MechPartSlot slot;}
  [Serializable] public sealed class UnitEdit{public string key;public Vector3 position;public Quaternion rotation=Quaternion.identity;public bool customLoadout;public List<Equipment> equipment=new();}
  [Serializable] public sealed class Document{public int version=1;public string scenarioId;public List<UnitEdit> units=new();}
  public static MechScenarioDefinition ActiveScenario {get;private set;}
  private static Document activeDocument;
  [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void Reset(){ActiveScenario=null;activeDocument=null;}
  public static string UnitKey(Transform unit){string key=unit.name;while(unit.parent!=null){unit=unit.parent;key=unit.name+"/"+key;}return key;}
  private static string PathFor(MechScenarioDefinition scenario)=>Path.Combine(Application.persistentDataPath,"ScenarioEdits",Uri.EscapeDataString(scenario.scenarioId)+".json");
  public static Document Defaults(MechScenarioDefinition scenario)=>new Document{scenarioId=scenario.scenarioId,units=scenario.units.Select(u=>new UnitEdit{key=u.key,position=u.position,rotation=u.rotation}).ToList()};
  public static Document Load(MechScenarioDefinition scenario,out string warning)
  {
   warning=null;var defaults=Defaults(scenario);var path=PathFor(scenario);if(!File.Exists(path))return defaults;
   try
   {
    var saved=JsonUtility.FromJson<Document>(File.ReadAllText(path));if(saved==null||saved.version!=1||saved.scenarioId!=scenario.scenarioId||saved.units==null)throw new InvalidDataException("场景配置版本不匹配");
    foreach(var unit in defaults.units){var edit=saved.units.FirstOrDefault(u=>u.key==unit.key);if(edit==null)continue;if(!Finite(edit.position)||!Finite(edit.rotation))throw new InvalidDataException("单位坐标无效");unit.position=edit.position;unit.rotation=edit.rotation;unit.customLoadout=edit.customLoadout;unit.equipment=edit.equipment??new();}
    return defaults;
   }catch(Exception e){warning="无法读取场景配置，使用默认值："+e.Message;return defaults;}
  }
  private static bool Finite(Vector3 v)=>float.IsFinite(v.x)&&float.IsFinite(v.y)&&float.IsFinite(v.z);
  private static bool Finite(Quaternion q)=>float.IsFinite(q.x)&&float.IsFinite(q.y)&&float.IsFinite(q.z)&&float.IsFinite(q.w)&&q.x*q.x+q.y*q.y+q.z*q.z+q.w*q.w>.001f;
  public static void Save(MechScenarioDefinition scenario,Document document)
  {
   if(document==null||document.scenarioId!=scenario.scenarioId||document.units.Any(u=>!Finite(u.position)||!Finite(u.rotation)))throw new InvalidDataException("场景配置无效");
   var path=PathFor(scenario);Directory.CreateDirectory(Path.GetDirectoryName(path));var temp=path+".tmp";File.WriteAllText(temp,JsonUtility.ToJson(document,true));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
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
   error=null;
   try{foreach(var unit in scenario.units.Where(u=>!u.player)){var edit=document.units.First(u=>u.key==unit.key);var loadout=BuildLoadout(unit,edit,catalog);var valid=MechLoadoutValidator.Validate(loadout);Release(loadout);if(!valid.IsValid){error=unit.name+" 的配装无效";return false;}}}
   catch(Exception e){error=e.Message;return false;}
   ActiveScenario=scenario;activeDocument=JsonUtility.FromJson<Document>(JsonUtility.ToJson(document));return true;
  }
  public static bool ApplySpawn(MechAssembler assembler,out MechLoadoutDefinition loadout)
  {
   loadout=null;if(ActiveScenario==null||activeDocument==null||assembler.gameObject.scene.path!=ActiveScenario.scenePath)return false;
   var key=UnitKey(assembler.transform);var unit=ActiveScenario.units.FirstOrDefault(u=>u.key==key);var edit=activeDocument.units.FirstOrDefault(u=>u.key==key);if(unit==null||edit==null)return false;
   assembler.transform.SetPositionAndRotation(edit.position,edit.rotation);
   if(unit.player||!edit.customLoadout)return false;
   loadout=BuildLoadout(unit,edit,MechLoadoutSelectionStore.LoadCatalog());return true;
  }
 }
}
