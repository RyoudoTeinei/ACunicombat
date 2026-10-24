using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
namespace ACunicombat.Mechs
{
 // Starting sequences reflect first-known identity, not a permanent faction code.
 [DisallowMultipleComponent,DefaultExecutionOrder(450)]
 public sealed class MechTrackDisplayNames:MonoBehaviour
 {
  private readonly Dictionary<string,long> numbers=new();
  private long nextFriendly=1001,nextUnknown=7001,nextHostile=8001,nextBirth;
  private readonly Dictionary<long,long> birthOrder=new();
  private readonly HashSet<long> usedNumbers=new();
  private MechRadarController radar;
  private MechCooperativeTelemetry cooperative;
  private readonly TrackSnapshot[] tracks=new TrackSnapshot[2048];
  private readonly MissileTrackSnapshot[] missiles=new MissileTrackSnapshot[2048];
  public static MechTrackDisplayNames For(MechRadarController source)
  {return source.GetComponent<MechTrackDisplayNames>()??source.gameObject.AddComponent<MechTrackDisplayNames>();}
  private void Awake(){radar=GetComponent<MechRadarController>();Number("own",IdentityRelation.Friendly);}
  private void LateUpdate()
  {
   if(radar==null)return;
   int count=radar.CopyTracks(tracks);for(int i=0;i<count;i++)if(tracks[i].Lifecycle!=TrackLifecycle.Dropped)Label(tracks[i]);
   count=radar.CopyMissileTracks(missiles);for(int i=0;i<count;i++)Label(missiles[i]);
  }
  public static string Prefix(IdentityRelation relation)=>relation switch
  {IdentityRelation.Friendly=>"FRD",IdentityRelation.Neutral=>"NEU",IdentityRelation.SuspectedHostile=>"SUS",IdentityRelation.Hostile=>"HOS",_=>"UNK"};
  public static string HighlightHostile(string label)
   =>!string.IsNullOrEmpty(label)&&label.StartsWith("HOS-",StringComparison.Ordinal)?"<color=#FF2E1F>HOS</color>"+label.Substring(3):label;
  private static IdentityRelation Relation(TrackAffiliation affiliation)=>affiliation switch
  {TrackAffiliation.Friendly=>IdentityRelation.Friendly,TrackAffiliation.Hostile=>IdentityRelation.Hostile,_=>IdentityRelation.Unknown};
  private long Allocate(IdentityRelation relation)
  {
   long candidate=relation==IdentityRelation.Friendly?nextFriendly:relation==IdentityRelation.Hostile?nextHostile:nextUnknown;
   while(!usedNumbers.Add(candidate))candidate++;
   if(relation==IdentityRelation.Friendly)nextFriendly=candidate+1;else if(relation==IdentityRelation.Hostile)nextHostile=candidate+1;else nextUnknown=candidate+1;
   birthOrder[candidate]=nextBirth++;return candidate;
  }
  private long Number(string key,IdentityRelation initialRelation=IdentityRelation.Unknown,string alias=null)
  {
   bool exists=numbers.TryGetValue(key,out long number);
   if(alias!=null&&numbers.TryGetValue(alias,out long associated))
   {
    if(!exists)number=associated;
    else if(number!=associated)
    {
     long keep=birthOrder[number]<=birthOrder[associated]?number:associated,retired=keep==number?associated:number;
     var keys=new List<string>(numbers.Keys);foreach(var k in keys)if(numbers[k]==retired)numbers[k]=keep;
     number=keep;
    }
   }
   else if(!exists)number=Allocate(initialRelation);
   numbers[key]=number;if(alias!=null)numbers[alias]=number;return number;
  }
  public string Label(in TrackSnapshot track)
  {
   if(!track.TrackId.IsValid)return "UNK-—";
   cooperative??=GetComponent<MechCooperativeTelemetry>();
   string alias=null;
   if(cooperative!=null&&cooperative.TryGetCooperativeMember(radar.Reference(track.TrackId),out var member))alias="member/"+member;
   var relation=track.Identity.Available?track.Identity.Relation:Relation(track.Affiliation);
   long number=Number("track/"+track.TrackId.Packed,relation,alias);
   return Prefix(relation)+"-"+number.ToString("0000");
  }
  public string Label(in MissileTrackSnapshot track)
  {
   if(!track.TrackId.IsValid)return "UNK-—";
   string alias=track.AssociatedSensorTrackId.IsValid?"track/"+track.AssociatedSensorTrackId.Packed:null;
   var relation=track.IsFriendly?IdentityRelation.Friendly:Relation(track.Affiliation);
   long number=Number("missile/"+track.TrackId.Packed,relation,alias);
   return Prefix(relation)+"-"+number.ToString("0000");
  }
  public string Label(TrackId id)
  {
   if(!id.IsValid)return "UNK-—";
   if(radar.TryGetTrack(id,out var track))return Label(track);
   return "UNK-"+Number("track/"+id.Packed).ToString("0000");
  }
 }
}
