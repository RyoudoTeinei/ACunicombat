using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
 public sealed partial class MechFleetController
 {
  private readonly HashSet<Guid> formationManagedMembers=new();
  public FleetTaskGroup TaskGroup=>State!=null?State.TaskGroup:FleetTaskGroup.Strike;
  public static Guid GroupRoot(MechFleetRosterEntry entry)=>entry.FormationRoot!=Guid.Empty?entry.FormationRoot:entry.Leader;
  public static List<MechFleetRosterEntry> FormationMembers(Guid root)=>Roster.Values.Where(m=>GroupRoot(m)==root).OrderBy(m=>m.FormationIndex).ToList();
  public bool CanEditFormation(Guid root){var members=FormationMembers(root);return members.Count>0&&members.All(m=>CanCommand(m.Member));}
  public List<Guid> RequestFormationLayout(IReadOnlyList<Guid> members,string name,FleetTaskGroup group,FleetFormationShape shape,IReadOnlyList<Vector3> custom=null)
  {
   var operations=new List<Guid>();
   if(!Configured||!MissionAvailable(group)||members==null||members.Count==0||members.Count>64||members.Distinct().Count()!=members.Count||members.Any(m=>!CanCommand(m))||!Enum.IsDefined(typeof(FleetTaskGroup),group)||!Enum.IsDefined(typeof(FleetFormationShape),shape))return operations;
   if(shape==FleetFormationShape.Custom&&(custom==null||custom.Count!=members.Count||custom.Any(v=>!float.IsFinite(v.x)||!float.IsFinite(v.y)||!float.IsFinite(v.z))))return operations;
   name=(name??"").Replace("\n"," ").Replace("\r"," ").Trim();if(name.Length>24)name=name.Substring(0,24);
   foreach(var member in members)formationManagedMembers.Add(member);
   for(int i=0;i<members.Count;i++)
   {
    Guid immediate=i==0?members[0]:i%2==1?members[i-1]:members[0];
    var p=new FleetPacket{Kind=FleetPacketKind.Order,Order=FleetOrderKind.ConfigureFormation,Sender=MemberId,Recipient=members[i],Fleet=State.Fleet,
     Operation=Guid.NewGuid(),Revision=++revision,Leader=immediate,FormationRoot=members[0],FormationIndex=i,FormationSize=members.Count,
     FormationName=new Unity.Collections.FixedString128Bytes(name),TaskGroup=group,FormationShape=shape,
     FormationOffset=(float3)(shape==FleetFormationShape.Custom?custom[i]-custom[0]:Vector3.zero),ObservedAt=radar.SimTimeSeconds,ExpiresAt=radar.SimTimeSeconds+8,TaskDeadline=radar.SimTimeSeconds+8};
    pending[p.Operation]=new Pending{Packet=p,NextRetry=p.ObservedAt+.5};operations.Add(p.Operation);
    if(p.Recipient==MemberId&&IsPlayer){pending[p.Operation].Acknowledged=true;if(State.ReceiveLocalPlayerOrder(p,radar.SimTimeSeconds))UpdateFormationRoster();}
    else Send(p);
   }
   return operations;
  }
  private void UpdateFormationRoster()
  {
   if(!Roster.TryGetValue(MemberId,out var entry))return;
   entry.Leader=State.Leader;entry.FormationRoot=State.FormationRoot;entry.IsLeader=State.IsLeader;entry.FormationIndex=State.FormationIndex;
   entry.FormationSize=State.FormationSize;entry.FormationName=State.FormationName;entry.TaskGroup=State.TaskGroup;entry.FormationShape=State.FormationShape;entry.FormationOffset=(Vector3)State.FormationOffset;
  }
  public static Vector3 FormationSlotOffset(FleetFormationShape shape,int index,int count,float spacing)
  {
   if(index<=0)return Vector3.zero;
   if(shape==FleetFormationShape.Automatic)shape=count==4?FleetFormationShape.FingerFour:FleetFormationShape.Wedge;
   float side=index%2==1?-1:1;int rank=(index+1)/2;
   return shape switch
   {
    FleetFormationShape.EchelonLeft=>new Vector3(-index*spacing,0,-index*spacing),
    FleetFormationShape.EchelonRight=>new Vector3(index*spacing,0,-index*spacing),
    FleetFormationShape.LineAbreast=>new Vector3(side*rank*spacing,0,0),
    FleetFormationShape.Trail=>new Vector3(0,0,-index*spacing),
    FleetFormationShape.FingerFour=>((index%4) switch{1=>new Vector3(-spacing,0,-spacing),2=>new Vector3(spacing,0,-spacing*.5f),3=>new Vector3(2*spacing,0,-spacing*1.5f),_=>Vector3.zero})+new Vector3(0,0,-(index/4)*spacing*3),
    FleetFormationShape.Diamond=>((index%4) switch{1=>new Vector3(-spacing,0,-spacing),2=>new Vector3(spacing,0,-spacing),3=>new Vector3(0,0,-2*spacing),_=>Vector3.zero})+new Vector3(0,0,-(index/4)*spacing*3),
    _=>new Vector3(side*rank*spacing,0,-rank*spacing)
   };
  }
  private Vector3 OwnFormationOffset()=>State.FormationShape==FleetFormationShape.Custom?(Vector3)State.FormationOffset:FormationSlotOffset(State.FormationShape,State.FormationIndex,State.FormationSize,FormationSpacing);
 }
}
