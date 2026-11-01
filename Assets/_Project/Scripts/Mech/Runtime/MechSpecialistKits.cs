using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace ACunicombat.Mechs
{
 public static class MechSpecialistKits
 {
  public static bool IsExtension(MechPartSlot slot)=>slot==MechPartSlot.Extension||slot==MechPartSlot.Extension2;
  public static bool IsShoulder(MechPartSlot slot)=>slot==MechPartSlot.LeftBackWeapon||slot==MechPartSlot.RightBackWeapon;
  public static int CoreCapacity(MechLoadoutDefinition loadout)
   =>loadout?.Assignments.FirstOrDefault(a=>a!=null&&a.enabled&&a.part!=null&&a.part.Slot==MechPartSlot.Core)?.part.ExtensionSlotCount??0;
  public static MechSpecialistKitKind Kind(MechLoadoutDefinition loadout)
   =>loadout?.Assignments.FirstOrDefault(a=>a!=null&&a.enabled&&a.part!=null&&a.part.SpecialistKit!=MechSpecialistKitKind.None)?.part.SpecialistKit??MechSpecialistKitKind.None;
  public static bool ShoulderBlocked(MechRuntimeState runtime,MechEquipmentSlot slot)
  {
   if(runtime==null||Kind(runtime.CurrentLoadout)==MechSpecialistKitKind.None)return false;
   if(IsShoulder(slot.LegacyRole))return true;
   var record=runtime.AssembledParts.FirstOrDefault(r=>r!=null&&r.SlotId==slot.Id);
   if(record!=null&&IsShoulder(record.slot))return true;
   var definition=runtime.FindCompiledSlot(slot.Id);
   return definition!=null&&(IsShoulder(definition.legacyRole)||definition.weaponGroup=="left-shoulder"||definition.weaponGroup=="right-shoulder");
  }
  public static bool RadarOperational(MechRuntimeState runtime)
  {
   if(runtime?.CurrentLoadout==null)return true;
   runtime.CurrentLoadout.TryGetPart(MechPartSlot.Radar,out var radar);
   if(Kind(runtime.CurrentLoadout)==MechSpecialistKitKind.EarlyWarning)
    return radar!=null&&radar.EarlyWarningRadarVariant>0&&!radar.RadarDefinitionPending&&radar.RadarPerformance.HasData;
   return radar==null||radar.EarlyWarningRadarVariant==0&&!radar.RadarDefinitionPending;
  }
  public static bool CandidateAllowed(MechPartSlot slot,MechPartDefinition part,IReadOnlyDictionary<MechPartSlot,string> selection,MechPartCatalog catalog)
  {
   if(part==null)return false;
   var equipped=selection.Select(p=>new KeyValuePair<MechPartSlot,MechPartDefinition>(p.Key,catalog.Find(p.Value)?.LoadPart())).Where(p=>p.Value!=null).ToList();
   var core=equipped.FirstOrDefault(p=>p.Key==MechPartSlot.Core).Value;int capacity=core!=null?core.ExtensionSlotCount:0;
   var kit=equipped.FirstOrDefault(p=>p.Value.SpecialistKit!=MechSpecialistKitKind.None);
   if(IsExtension(slot)&&(capacity<(slot==MechPartSlot.Extension2?2:1)||kit.Value!=null&&kit.Key!=slot))return false;
   if(IsShoulder(slot)&&kit.Value!=null)return false;
   if(slot==MechPartSlot.Radar)return (kit.Value!=null&&kit.Value.SpecialistKit==MechSpecialistKitKind.EarlyWarning)==(part.EarlyWarningRadarVariant>0);
   return true;
  }
  public static bool CandidateAllowed(MechLoadoutDefinition loadout,MechSlotDefinition slot,MechPartDefinition part)
  {
   if(part==null)return true;
   var kit=loadout.Assignments.FirstOrDefault(a=>a!=null&&a.enabled&&a.part!=null&&a.part.SpecialistKit!=MechSpecialistKitKind.None);
   if((IsExtension(slot.legacyRole)||part.Slot==MechPartSlot.Extension)&&kit!=null&&kit.SlotId!=slot.id)return false;
   if((IsShoulder(slot.legacyRole)||slot.weaponGroup=="left-shoulder"||slot.weaponGroup=="right-shoulder")&&kit!=null)return false;
   if(slot.legacyRole==MechPartSlot.Radar)return (Kind(loadout)==MechSpecialistKitKind.EarlyWarning)==(part.EarlyWarningRadarVariant>0);
   return true;
  }
  public static void NormalizeSelection(Dictionary<MechPartSlot,string> selection,MechPartCatalog catalog,MechPartSlot changed)
  {
   var kit=selection.Where(p=>catalog.Find(p.Value)?.LoadPart()?.SpecialistKit!=null&&catalog.Find(p.Value).LoadPart().SpecialistKit!=MechSpecialistKitKind.None).OrderBy(p=>p.Key==changed?0:1).FirstOrDefault();
   var part=catalog.Find(kit.Value)?.LoadPart();
   if(part!=null)
   {
    selection.Remove(MechPartSlot.LeftBackWeapon);selection.Remove(MechPartSlot.RightBackWeapon);
    foreach(var slot in new[]{MechPartSlot.Extension,MechPartSlot.Extension2})if(slot!=kit.Key)selection.Remove(slot);
    if(part.SpecialistKit==MechSpecialistKitKind.EarlyWarning)
    {selection.TryGetValue(MechPartSlot.Radar,out var radar);if(catalog.Find(radar)?.LoadPart()?.EarlyWarningRadarVariant>0)return;selection[MechPartSlot.Radar]="KIT_AEW_RADAR_01";return;}
   }
   if(selection.TryGetValue(MechPartSlot.Radar,out var id)&&catalog.Find(id)?.LoadPart()?.EarlyWarningRadarVariant>0)selection.Remove(MechPartSlot.Radar);
  }
  public static void NormalizeAssignments(List<MechPartAssignment> assignments,string changed)
  {
   var kit=assignments.Where(a=>a!=null&&a.enabled&&a.part!=null&&a.part.SpecialistKit!=MechSpecialistKitKind.None).OrderBy(a=>a.SlotId==changed?0:1).FirstOrDefault();
   if(kit!=null)assignments.RemoveAll(a=>a!=kit&&a?.part!=null&&(a.part.Slot==MechPartSlot.Extension||IsExtension(a.slot)||IsShoulder(a.slot)||IsShoulder(a.part.Slot)));
   bool early=kit?.part.SpecialistKit==MechSpecialistKitKind.EarlyWarning;
   assignments.RemoveAll(a=>a?.part!=null&&a.part.Slot==MechPartSlot.Radar&&(early!=(a.part.EarlyWarningRadarVariant>0)));
  }
  public static void Validate(MechLoadoutDefinition loadout,MechValidationResult result)
  {
   var installed=loadout.EnumerateEnabledAssignments().Where(a=>a.part!=null).ToList();
   var extensions=installed.Where(a=>IsExtension(a.slot)||a.part.Slot==MechPartSlot.Extension).ToList();int capacity=CoreCapacity(loadout);
   if(extensions.Count>capacity||installed.Any(a=>a.slot==MechPartSlot.Extension2)&&capacity<2)result.Add(MechValidationSeverity.Error,MechPartSlot.Extension,"CORE_EXTENSION_CAPACITY",$"当前核心提供 {capacity} 个扩展槽，扩展配置超出容量。");
   var kit=Kind(loadout);
   if(kit!=MechSpecialistKitKind.None)
   {
    if(extensions.Count!=1)result.Add(MechValidationSeverity.Error,MechPartSlot.Extension,"SPECIALIST_EXCLUSIVE","专用套件独占扩展系统，不能与其他扩展并装。");
    if(installed.Any(a=>IsShoulder(a.slot)||IsShoulder(a.part.Slot)))result.Add(MechValidationSeverity.Error,MechPartSlot.Extension,"SPECIALIST_SHOULDERS","专用套件占用双肩接口，请卸下左右肩武器。");
   }
   foreach(var radar in installed.Where(a=>a.part.Slot==MechPartSlot.Radar))
   {
    if((kit==MechSpecialistKitKind.EarlyWarning)!=(radar.part.EarlyWarningRadarVariant>0))result.Add(MechValidationSeverity.Error,MechPartSlot.Radar,"SPECIALIST_RADAR_FAMILY","预警机套件仅支持专用预警雷达；普通机体不能使用该雷达。");
    if(radar.part.RadarDefinitionPending)result.Add(MechValidationSeverity.Warning,MechPartSlot.Radar,"RADAR_DEFINITION_PENDING","预警雷达性能待讨论，当前不执行主动雷达探测。");
   }
  }
 }
}
