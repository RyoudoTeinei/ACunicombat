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
  private readonly HashSet<Guid> expandedMapFormations=new();
  private bool independentUnitsExpanded;
  private int formationListOffset,formationListCount;
  private Rect FormationPanelRect=>new Rect(24,24,Mathf.Min(370,layoutWidth*.34f),Mathf.Min(560,FooterRect.y-48));
  private sealed class FormationHit{public Rect Bounds;public Guid Root,Member;public int Index;public bool Task,Header;}
  private readonly List<FormationHit> formationHits=new();
  private sealed class FormationRow{public string Text,ContactKey;public bool Header,Expanded,Summary,Leader;public Guid Group,Member;public int Index;}
  private bool TryFormationContact(Guid member,out Contact contact)
  {foreach(var c in contacts)if(ResolveSelectedMember(c,out var candidate,out _)&&candidate==member){contact=c;return true;}contact=default;return false;}
  private string FormationMemberName(MechFleetRosterEntry member)=>TryFormationContact(member.Member,out var contact)?contact.Name:string.IsNullOrEmpty(member.Name)?"未定位成员":member.Name;
  private static string FormationPositionName(int index,int count)=>index==0?"编队长机":index==1?"1号机僚机":index%2==0?(index+1<count?$"第{index/2+1}双机组长机":"直属僚机"):$"{index}号机僚机";
  private static string TaskGroupName(FleetTaskGroup mode)=>mode switch{FleetTaskGroup.Support=>"支援",FleetTaskGroup.Escort=>"护航",FleetTaskGroup.AirSuperiority=>"制空",FleetTaskGroup.AirDefenseSuppression=>"防空压制",FleetTaskGroup.ElectronicWarfare=>"电子战",FleetTaskGroup.EarlyWarningCommand=>"预警与指挥",FleetTaskGroup.Logistics=>"加油与支援",FleetTaskGroup.Reconnaissance=>"侦察",_=>"打击"};
  private static string TaskGroupShortName(FleetTaskGroup mode)=>mode switch{FleetTaskGroup.AirDefenseSuppression=>"压制",FleetTaskGroup.EarlyWarningCommand=>"预警",FleetTaskGroup.Logistics=>"补给",_=>TaskGroupName(mode)};
  private static string ShapeName(FleetFormationShape shape)=>shape switch{FleetFormationShape.EchelonLeft=>"左梯队",FleetFormationShape.EchelonRight=>"右梯队",FleetFormationShape.LineAbreast=>"横队／战斗展开",FleetFormationShape.Trail=>"纵队／拖后",FleetFormationShape.Wedge=>"楔形",FleetFormationShape.FingerFour=>"指尖四机",FleetFormationShape.Diamond=>"菱形",FleetFormationShape.Custom=>"自定义",_=>"自动队形"};
  private void DrawFormationPanel()
  {
   formationHits.Clear();var panel=FormationPanelRect;MenuPanel(panel);MenuLabel(new Rect(panel.x,panel.y,panel.width,36),"编队编辑器",PrimaryText,16);
   if(commandFleet==null||!commandFleet.Configured){MenuLabel(new Rect(panel.x,panel.y+40,panel.width,30),"未收到编队资料",PrimaryText);return;}
   var known=MechFleetController.Roster.Values.Where(m=>m.Member==commandFleet.MemberId||commandFleet.CanCommand(m.Member)||commandFleet.State.Members.ContainsKey(m.Member)).ToList();
   var rows=new List<FormationRow>();var independent=new List<MechFleetRosterEntry>();
   foreach(var group in known.GroupBy(MechFleetController.GroupRoot).OrderBy(g=>g.Key==commandFleet.MemberId?0:1).ThenBy(g=>g.Key))
   {
    var members=group.OrderBy(m=>m.FormationIndex).ToList();
    if(group.Key==Guid.Empty||members.Count==1&&members[0].Member==group.Key){independent.AddRange(members);continue;}
    var lead=members.FirstOrDefault(m=>m.Member==group.Key)??members[0];
    string name=string.IsNullOrWhiteSpace(lead.FormationName)?FormationMemberName(lead)+"编队":lead.FormationName;
    bool expanded=expandedMapFormations.Contains(group.Key);
    rows.Add(new FormationRow{Header=true,Group=group.Key,Expanded=expanded,Text=name});
    rows.Add(new FormationRow{Summary=true,Group=group.Key,Text=members.Count+" × unknown · "+ShapeName(lead.FormationShape)});
    if(expanded)foreach(var member in members){bool located=TryFormationContact(member.Member,out var c);rows.Add(new FormationRow{Group=group.Key,Member=member.Member,Index=member.FormationIndex,Leader=member.IsLeader,Text=FormationMemberName(member),ContactKey=located?c.Key:null});}
   }
   rows.Add(new FormationRow{Header=true,Group=Guid.Empty,Expanded=independentUnitsExpanded,Text="独立单位  ("+independent.Count+")"});
   if(independentUnitsExpanded)foreach(var member in independent){bool located=TryFormationContact(member.Member,out var c);rows.Add(new FormationRow{Group=member.Member,Member=member.Member,Text=FormationMemberName(member),ContactKey=located?c.Key:null});}
   int capacity=Mathf.Max(1,Mathf.FloorToInt((panel.height-82)/32));formationListCount=rows.Count;formationListOffset=Mathf.Clamp(formationListOffset,0,Mathf.Max(0,rows.Count-capacity));
   for(int i=0;i<capacity&&i+formationListOffset<rows.Count;i++)
   {
    var row=rows[i+formationListOffset];var bounds=new Rect(panel.x+1,panel.y+40+i*32,panel.width-2,32);
    if(row.ContactKey==selected||bounds.Contains(mapPointer))Fill(bounds,new Color(.075f,.14f,.105f,1));
    if(row.Header)
    {
     float offset=row.Group==Guid.Empty?0:82;
     if(row.Group!=Guid.Empty)
     {
      var lead=MechFleetController.FormationMembers(row.Group).First();var button=new Rect(bounds.xMax-79,bounds.y+3,74,26);
      Fill(button,new Color(.07f,.12f,.095f,1));Box(button.x,button.y,button.width,button.height,MutedColor);MenuLabel(button,TaskGroupShortName(lead.TaskGroup),PrimaryText,12);
      formationHits.Add(new FormationHit{Bounds=button,Root=row.Group,Task=true});
     }
     MenuLabel(new Rect(bounds.x,bounds.y,bounds.width-offset,bounds.height),(row.Expanded?"▼ ":"▶ ")+row.Text,PrimaryText,13);
     formationHits.Add(new FormationHit{Bounds=new Rect(bounds.x,bounds.y,bounds.width-offset,bounds.height),Root=row.Group,Header=true});
    }
    else if(row.Summary){MenuLabel(new Rect(bounds.x+14,bounds.y,bounds.width-14,bounds.height),row.Text,new Color(.57f,.64f,.60f,1),12);formationHits.Add(new FormationHit{Bounds=bounds,Root=row.Group,Header=true});}
    else
    {
     MenuLabel(new Rect(bounds.x+12,bounds.y,bounds.width*.55f,bounds.height),row.Text,row.ContactKey!=null?PrimaryText:new Color(.42f,.49f,.45f,1),12);
     var member=MechFleetController.Roster[row.Member];string role=member.FormationSize<=1?"独立单位":FormationPositionName(row.Index,member.FormationSize);
     BoundedLabel(new Rect(bounds.x+bounds.width*.56f,bounds.y,bounds.width*.43f-8,bounds.height),role,PrimaryText,11);texts[textCount-1].alignment=TextAnchor.MiddleRight;
     if(row.Leader)DrawFormationCrown(new Vector2(bounds.x+bounds.width*.51f,bounds.center.y-6),PrimaryText);
     formationHits.Add(new FormationHit{Bounds=bounds,Root=row.Group,Member=row.Member,Index=row.Index});
    }
   }
   if(formationEditOperations.Count>0||!string.IsNullOrEmpty(formationEditMessage))MenuLabel(new Rect(panel.x,panel.yMax-36,panel.width,32),FormationEditStatus(),new Color(.65f,.72f,.67f,1),11);
  }
  private void ScrollFormationList(float delta){if(delta!=0){formationListOffset=Mathf.Clamp(formationListOffset+(delta>0?-2:2),0,Mathf.Max(0,formationListCount-1));nextRefresh=0;}}
  private void DrawFormationCrown(Vector2 origin,Color color)
  {Vector2[] points={new Vector2(0,1),new Vector2(4,5),new Vector2(7,0),new Vector2(10,5),new Vector2(14,1),new Vector2(12,10),new Vector2(2,10),new Vector2(0,1)};for(int i=1;i<points.Length;i++)Line(origin+points[i-1],origin+points[i],color,1.2f);Line(origin+new Vector2(2,7),origin+new Vector2(12,7),color,1.1f);}
 }
}
