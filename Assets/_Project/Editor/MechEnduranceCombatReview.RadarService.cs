using System;
using System.IO;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
public static partial class MechEnduranceCombatReview
{
 static StreamWriter radarService;
 static readonly MissileTrackSnapshot[] serviceMissiles=new MissileTrackSnapshot[512];
 [Serializable] class RadarServiceRow {public double sim,maxTrackAge,meanTrackAge,maxWeaponsRange,estimatedSearchSeconds;public string observer;public int capacity,tracks,missiles,weaponsQuality,radarOnlyWeaponsQuality;public long deferred;}
 static void SampleRadarService(double now,string dir)
 {
  radarService??=new StreamWriter(dir+"/radar-service.jsonl");
  foreach(var observer in observers)
  {
   if(observer==null)continue;var status=observer.GetStatusSnapshot();var r=new RadarServiceRow{sim=now,observer=observer.name,capacity=status.TrackCapacity,deferred=status.DeferredDwellCount,estimatedSearchSeconds=status.RefreshRateHz>0?1/status.RefreshRateHz:0};
   int n=observer.CopyTracks(enduranceTracks);r.tracks=n;
   for(int i=0;i<n;i++){double age=Math.Max(0,observer.SimTimeSeconds-enduranceTracks[i].LastUpdateTime);r.meanTrackAge+=age;r.maxTrackAge=Math.Max(r.maxTrackAge,age);}if(n>0)r.meanTrackAge/=n;
   n=observer.CopyMissileTracks(serviceMissiles);r.missiles=n;
   for(int i=0;i<n;i++){var m=serviceMissiles[i];if(m.IsFriendly||!m.IsWeaponsQuality)continue;r.weaponsQuality++;if(m.SourceMask==MissileTrackSourceMask.Radar)r.radarOnlyWeaponsQuality++;r.maxWeaponsRange=Math.Max(r.maxWeaponsRange,math.distance(m.PredictPosition(observer.SimTimeSeconds),(double3)(float3)observer.transform.position));}
   radarService.WriteLine(JsonUtility.ToJson(r));
  }
  radarService.Flush();
 }
}
