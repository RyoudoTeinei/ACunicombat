using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private enum FormationMenu{None,Main,Task,Radar,Weapons,Shape}
  private enum FormationModal{None,Rename,Dissolve,Custom}
  private FormationMenu formationMenu;
  private FormationModal formationModal;
  private Guid editedFormation,dragFormationMember,lastFormationClick;
  private Vector2 formationMenuPoint,formationDragStart;
  private bool formationDragging,formationModalPaused;
  private float formationModalPreviousScale;
  private double lastFormationClickTime,nextFormationDragScroll;
  private string formationEditMessage="",formationEditName="",customHeight="0";
  private readonly List<Guid> formationEditOperations=new();
  private bool formationEditsAreMission;
  private List<Guid> customMembers=new();private List<Vector3> customOffsets=new();
  private int customSelected,customDragging=-1;private float customRange=2000;
  private bool FormationBusy=>formationEditOperations.Any(id=>!commandFleet.Results.ContainsKey(id));
  private string FormationEditStatus()
  {
   if(formationEditOperations.Count==0)return formationEditMessage;
   int completed=0,failed=0;foreach(var id in formationEditOperations)if(commandFleet.Results.TryGetValue(id,out var result)){if(result.Phase==FleetTaskPhase.Completed||formationEditsAreMission&&(result.Phase==FleetTaskPhase.Acquiring||result.Phase==FleetTaskPhase.Executing||result.Phase==FleetTaskPhase.Returning))completed++;else if(result.Phase==FleetTaskPhase.Rejected||result.Phase==FleetTaskPhase.Expired)failed++;}
   if(completed+failed<formationEditOperations.Count)return $"配置待回执 {completed+failed}/{formationEditOperations.Count}";
   return failed>0?$"部分配置未完成：成功 {completed}，失败 {failed}。请重试。":formationEditsAreMission?"任务已接收":"配置已生效";
  }
  private bool HandleFormationPointer(Vector2 point,Mouse mouse)
  {
   if(formationModal!=FormationModal.None)return true;
   if(formationMenu!=FormationMenu.None){if(mouse.rightButton.wasPressedThisFrame){formationMenu=FormationMenu.None;return true;}return false;}
   var hit=formationHits.LastOrDefault(h=>h.Bounds.Contains(point));
   if(mouse.rightButton.wasPressedThisFrame&&hit!=null)
   {if(hit.Root!=Guid.Empty){editedFormation=hit.Root;formationMenu=FormationMenu.Main;formationMenuPoint=point;contextOpen=false;openPolicyMenu=0;}return true;}
   if(mouse.leftButton.wasPressedThisFrame&&hit!=null)
   {
    contextOpen=false;openPolicyMenu=0;
    if(hit.Task){editedFormation=hit.Root;formationMenu=FormationMenu.Task;formationMenuPoint=new Vector2(hit.Bounds.x,hit.Bounds.yMax+3);return true;}
    if(hit.Header)
    {
     focusedMissionGroup=hit.Root;
     if(hit.Root!=Guid.Empty&&lastFormationClick==hit.Root&&Time.unscaledTimeAsDouble-lastFormationClickTime<.35){editedFormation=hit.Root;BeginFormationModal(FormationModal.Rename);}
     else if(hit.Root==Guid.Empty)independentUnitsExpanded=!independentUnitsExpanded;else if(!expandedMapFormations.Add(hit.Root))expandedMapFormations.Remove(hit.Root);
     lastFormationClick=hit.Root;lastFormationClickTime=Time.unscaledTimeAsDouble;return true;
    }
    dragFormationMember=hit.Member;formationDragStart=point;formationDragging=false;
    if(TryFormationContact(hit.Member,out var contact))selected=contact.Key;
    return true;
   }
   if(dragFormationMember==Guid.Empty)return false;
   if(mouse.leftButton.isPressed&&Vector2.Distance(point,formationDragStart)>6)formationDragging=true;
   if(formationDragging&&FormationPanelRect.Contains(point)&&Time.unscaledTimeAsDouble>=nextFormationDragScroll)
   {
    if(point.y<FormationPanelRect.y+55){ScrollFormationList(1);nextFormationDragScroll=Time.unscaledTimeAsDouble+.15;}
    else if(point.y>FormationPanelRect.yMax-45){ScrollFormationList(-1);nextFormationDragScroll=Time.unscaledTimeAsDouble+.15;}
   }
   if(mouse.leftButton.wasReleasedThisFrame)
   {
    var member=dragFormationMember;dragFormationMember=Guid.Empty;
    if(formationDragging)
    {
     if(hit!=null&&hit.Member!=member)MoveFormationMember(member,hit.Root,hit.Member);
     else if(!FormationPanelRect.Contains(point))MoveFormationMember(member,Guid.Empty,Guid.Empty);
    }
    else if(TryFormationContact(member,out var contact)){selected=contact.Key;ArmSelectedUnit();}
    formationDragging=false;
   }
   return true;
  }
  private void QueueLayout(List<Guid> members,MechFleetRosterEntry settings,string name=null,FleetTaskGroup? task=null,FleetFormationShape? shape=null,List<Vector3> offsets=null)
  {
   if(members.Count==0)return;
   formationEditsAreMission=false;
   var selectedShape=shape??settings.FormationShape;
   if(selectedShape==FleetFormationShape.Custom&&offsets==null)offsets=members.Select(id=>MechFleetController.Roster[id].FormationOffset).ToList();
   var ids=commandFleet.RequestFormationLayout(members,name??settings.FormationName,task??settings.TaskGroup,selectedShape,offsets);
   formationEditOperations.AddRange(ids);formationEditMessage=ids.Count==0?"配置未提交：权限或成员资料不足":"";
   expandedMapFormations.Add(members[0]);
  }
  private void ChangeFormation(string name=null,FleetTaskGroup? task=null,FleetFormationShape? shape=null,List<Vector3> offsets=null)
  {
   if(FormationBusy||!commandFleet.CanEditFormation(editedFormation))return;
   var members=MechFleetController.FormationMembers(editedFormation);if(members.Count==0)return;
   formationEditOperations.Clear();QueueLayout(members.Select(m=>m.Member).ToList(),members[0],name,task,shape,offsets);formationMenu=FormationMenu.None;
  }
  public static List<Guid> FormationOrderAfterDrop(IReadOnlyList<Guid> members,Guid member,Guid before)
  {
   var ids=members.Where(id=>id!=member).ToList();int index=before!=Guid.Empty?ids.IndexOf(before):-1;
   if(index<0)index=ids.Count;ids.Insert(index,member);return ids;
  }
  private void MoveFormationMember(Guid member,Guid destination,Guid before)
  {
   if(FormationBusy){formationEditMessage="上一批编队配置仍在等待回执";return;}
   if(!commandFleet.CanCommand(member)||!MechFleetController.Roster.TryGetValue(member,out var unit)){formationEditMessage="没有调整该成员的权限";return;}
   var sourceRoot=MechFleetController.GroupRoot(unit);var source=MechFleetController.FormationMembers(sourceRoot);
   var target=destination==Guid.Empty?new List<MechFleetRosterEntry>():MechFleetController.FormationMembers(destination);
   if(!source.All(m=>commandFleet.CanCommand(m.Member))||!target.All(m=>commandFleet.CanCommand(m.Member))){formationEditMessage="不能重组无指挥权限的编队";return;}
   var sourceSettings=source.FirstOrDefault()??unit;var targetSettings=target.FirstOrDefault()??unit;
   var ids=FormationOrderAfterDrop(target.Select(m=>m.Member).ToList(),member,before);
   formationEditOperations.Clear();
   if(sourceRoot!=destination)QueueLayout(source.Where(m=>m.Member!=member).Select(m=>m.Member).ToList(),sourceSettings);
   QueueLayout(ids,targetSettings);formationMenu=FormationMenu.None;
  }
  private void DrawFormationEditorOverlay()
  {
   if(formationDragging)
   {
    var drop=formationHits.LastOrDefault(h=>h.Bounds.Contains(mapPointer)&&h.Member!=Guid.Empty&&h.Member!=dragFormationMember);
    if(drop!=null)Fill(new Rect(drop.Bounds.x+3,drop.Bounds.y-1,drop.Bounds.width-6,3),FriendlyColor);
   }
   if(formationDragging&&MechFleetController.Roster.TryGetValue(dragFormationMember,out var member))
   {var box=new Rect(mapPointer.x+15,mapPointer.y+12,180,28);MenuPanel(box);MenuLabel(box,FormationMemberName(member),PrimaryText,12);}
   if(formationMenu==FormationMenu.None)return;
   string[] options=formationMenu switch
   {
    FormationMenu.Task=>MissionChoices.Select(TaskGroupName).ToArray(),FormationMenu.Radar=>new[]{"辐射管制","主动雷达","智能模式"},FormationMenu.Weapons=>new[]{"确认开火","自由开火"},
    FormationMenu.Shape=>new[]{"自动队形","左梯队","右梯队","横队／战斗展开","纵队／拖后","楔形","指尖四机","菱形","自定义"},
    _=>new[]{"雷达管制 ›","武器管制 ›","队形 ›","加入选中机体","重命名","解散编队"}
   };
   var rect=new Rect(Mathf.Clamp(formationMenuPoint.x,8,layoutWidth-220),Mathf.Clamp(formationMenuPoint.y,8,H-options.Length*34-16),212,options.Length*34+8);
   if(rect.Contains(mapPointer))hoveredHelp=null;
   MenuPanel(rect);bool editable=!FormationBusy&&commandFleet.CanEditFormation(editedFormation);
   for(int i=0;i<options.Length;i++)
   {
    int option=i;bool enabled=editable&&!(formationMenu==FormationMenu.Radar&&i==2);
    if(formationMenu==FormationMenu.Task)enabled&=MechFleetController.MissionAvailable(MissionChoices[i]);
    if(formationMenu==FormationMenu.Main&&i==3)enabled&=SelectedFormationMember()!=Guid.Empty;
    var leader=MechFleetController.FormationMembers(editedFormation).FirstOrDefault();
    bool active=leader!=null&&(formationMenu==FormationMenu.Task&&leader.TaskGroup==MissionChoices[i]||formationMenu==FormationMenu.Shape&&(int)leader.FormationShape==i);
    FleetPacket report=default;bool reported=editedFormation==commandFleet.MemberId;
    if(reported)report=commandFleet.GetOwnStatus();else reported=commandFleet.State.Members.TryGetValue(editedFormation,out report)&&commandFleet.State.Fresh(report,radar.SimTimeSeconds);
    if(reported)active|=formationMenu==FormationMenu.Radar&&(int)report.RadarControl==i||formationMenu==FormationMenu.Weapons&&(int)report.Mode==i;
    MenuItem(new Rect(rect.x+1,rect.y+4+i*34,rect.width-2,34),options[i],()=>ExecuteFormationOption(option),enabled,active);
   }
  }
  private Guid SelectedFormationMember()
  {int i=contacts.FindIndex(c=>c.Key==selected);return i>=0&&ResolveSelectedMember(contacts[i],out var id,out _)&&commandFleet.CanCommand(id)?id:Guid.Empty;}
  private void ExecuteFormationOption(int option)
  {
   if(formationMenu==FormationMenu.Task){BeginMissionAssignment(MissionChoices[option]);return;}
   if(formationMenu==FormationMenu.Shape){if(option==8)BeginFormationModal(FormationModal.Custom);else ChangeFormation(shape:(FleetFormationShape)option);return;}
   if(formationMenu==FormationMenu.Radar||formationMenu==FormationMenu.Weapons)
   {
    bool radarMode=formationMenu==FormationMenu.Radar;formationEditsAreMission=false;formationEditOperations.Clear();
    var members=MechFleetController.FormationMembers(editedFormation);
    // Set each member through its authorized endpoint, without treating submission as completion.
    foreach(var unit in members)
    {
     if(unit.Member==commandFleet.MemberId){if(radarMode)commandFleet.SetOwnRadarControl((FleetRadarControl)option);else commandFleet.State.SetLocalMode((FleetAttackMode)option);continue;}
     Guid id=radarMode?commandFleet.RequestRadarControl(unit.Member,(FleetRadarControl)option):commandFleet.Issue(unit.Member,FleetOrderKind.SetAttackMode,mode:(FleetAttackMode)option);
     if(id!=Guid.Empty)formationEditOperations.Add(id);
    }
    formationMenu=FormationMenu.None;return;
   }
   switch(option)
   {case 0:formationMenu=FormationMenu.Radar;break;case 1:formationMenu=FormationMenu.Weapons;break;case 2:formationMenu=FormationMenu.Shape;break;case 3:MoveFormationMember(SelectedFormationMember(),editedFormation,Guid.Empty);break;case 4:BeginFormationModal(FormationModal.Rename);break;case 5:BeginFormationModal(FormationModal.Dissolve);break;}
  }
  private void BeginFormationModal(FormationModal mode)
  {
   if(FormationBusy||!commandFleet.CanEditFormation(editedFormation))return;
   var members=MechFleetController.FormationMembers(editedFormation);if(members.Count==0)return;
   formationMenu=FormationMenu.None;formationModal=mode;formationEditName=members[0].FormationName;
   if(mode==FormationModal.Dissolve){formationModalPreviousScale=Time.timeScale;formationModalPaused=true;Time.timeScale=0;}
   if(mode==FormationModal.Custom)
   {customMembers=members.Select(m=>m.Member).ToList();customOffsets=members.Select(m=>m.FormationShape==FleetFormationShape.Custom?m.FormationOffset:MechFleetController.FormationSlotOffset(m.FormationShape,m.FormationIndex,m.FormationSize,commandFleet.FormationSpacing)).ToList();customSelected=0;customDragging=-1;customHeight="0";}
  }
  private void CloseFormationModal()
  {if(formationModalPaused){Time.timeScale=formationModalPreviousScale;formationModalPaused=false;}formationModal=FormationModal.None;customDragging=-1;}
  private void DrawFormationModalGui()
  {
   if(!visible||formationModal==FormationModal.None)return;
   var matrix=GUI.matrix;var tint=GUI.color;int oldDepth=GUI.depth;GUI.matrix=Matrix4x4.Scale(Vector3.one*(Screen.height/(float)H));GUI.depth=-4001;
   float w=formationModal==FormationModal.Custom?660:470,h=formationModal==FormationModal.Custom?560:180;
   var box=new Rect((layoutWidth-w)/2,(H-h)/2,w,h);
   GUI.color=new Color(0,0,0,.65f);GUI.DrawTexture(new Rect(0,0,layoutWidth,H),Texture2D.whiteTexture);
   GUI.color=new Color(.025f,.039f,.035f,1);GUI.DrawTexture(box,Texture2D.whiteTexture);
   GUI.color=new Color(.29f,.38f,.33f,1);GUI.DrawTexture(new Rect(box.x,box.y,box.width,1),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(box.x,box.yMax-1,box.width,1),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(box.x,box.y,1,box.height),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(box.xMax-1,box.y,1,box.height),Texture2D.whiteTexture);GUI.color=Color.white;
   var style=new GUIStyle(GUI.skin.label){font=font,fontSize=16};style.normal.textColor=PrimaryText;
   GUI.Label(new Rect(box.x+20,box.y+14,w-40,26),formationModal==FormationModal.Rename?"重命名编队":formationModal==FormationModal.Dissolve?"解散编队？":"自定义队形",style);
   if(formationModal==FormationModal.Rename)formationEditName=GUI.TextField(new Rect(box.x+20,box.y+58,w-40,32),formationEditName,24);
   else if(formationModal==FormationModal.Dissolve)GUI.Label(new Rect(box.x+20,box.y+55,w-40,52),"成员将成为独立单位，当前时间已暂停。\n确认后发送解散配置；取消不会改变编队。",style);
   else DrawCustomFormationEditor(box,style);
   if(GUI.Button(new Rect(box.x+w-210,box.yMax-48,90,30),"取消"))CloseFormationModal();
   if(GUI.Button(new Rect(box.x+w-110,box.yMax-48,90,30),"确认"))
   {
    var mode=formationModal;CloseFormationModal();
    if(mode==FormationModal.Rename)ChangeFormation(name:formationEditName);
    else if(mode==FormationModal.Custom)ChangeFormation(shape:FleetFormationShape.Custom,offsets:customOffsets);
    else {formationEditOperations.Clear();foreach(var unit in MechFleetController.FormationMembers(editedFormation))QueueLayout(new List<Guid>{unit.Member},unit,name:"",shape:FleetFormationShape.Automatic);independentUnitsExpanded=true;}
   }
   GUI.color=tint;GUI.matrix=matrix;GUI.depth=oldDepth;
  }
  private void DrawCustomFormationEditor(Rect box,GUIStyle style)
  {
   var plot=new Rect(box.x+20,box.y+65,420,390);var center=plot.center;float scale=plot.width/(2*customRange);
   GUI.color=new Color(.04f,.07f,.055f,1);GUI.DrawTexture(plot,Texture2D.whiteTexture);GUI.color=Color.white;
   GUI.Label(new Rect(box.x+20,box.y+40,620,24),"俯视拖动成员；长机为原点。右侧可调整高低差。",style);
   var evt=Event.current;
   for(int i=0;i<customOffsets.Count;i++)
   {
    var p=center+new Vector2(customOffsets[i].x,-customOffsets[i].z)*scale;var hit=new Rect(p.x-12,p.y-12,24,24);
    if(plot.Contains(p)){GUI.color=i==customSelected?new Color(.8f,.7f,.3f):new Color(.22f,.64f,.78f);GUI.DrawTexture(hit,Texture2D.whiteTexture);GUI.color=Color.white;GUI.Label(new Rect(p.x+15,p.y-12,100,25),(i+1).ToString(),style);}
    if(evt.type==EventType.MouseDown&&hit.Contains(evt.mousePosition)&&plot.Contains(evt.mousePosition)){customSelected=i;customDragging=i;customHeight=customOffsets[i].y.ToString("0");evt.Use();}
   }
   if(evt.type==EventType.MouseDrag&&customDragging>0){var offset=customOffsets[customDragging];offset.x=Mathf.Clamp((evt.mousePosition.x-center.x)/scale,-100000,100000);offset.z=Mathf.Clamp(-(evt.mousePosition.y-center.y)/scale,-100000,100000);customOffsets[customDragging]=offset;evt.Use();}
   if(evt.type==EventType.MouseUp)customDragging=-1;
   GUI.Label(new Rect(box.x+455,box.y+70,185,28),$"{customSelected+1}号机",style);
   GUI.Label(new Rect(box.x+455,box.y+105,185,28),"相对高度（米）",style);
   GUI.enabled=customSelected>0;customHeight=GUI.TextField(new Rect(box.x+455,box.y+140,175,30),customHeight,12);
   if(customSelected>0&&float.TryParse(customHeight,out float height)&&float.IsFinite(height)){var offset=customOffsets[customSelected];offset.y=Mathf.Clamp(height,-100000,100000);customOffsets[customSelected]=offset;}GUI.enabled=true;
   GUI.Label(new Rect(box.x+20,box.y+470,300,25),$"视图半径 {customRange/1000:0.0} km",style);customRange=GUI.HorizontalSlider(new Rect(box.x+210,box.y+477,225,20),customRange,500,10000);
  }
 }
}
