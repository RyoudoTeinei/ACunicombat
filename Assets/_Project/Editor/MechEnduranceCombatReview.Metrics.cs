using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
public static partial class MechEnduranceCombatReview
{
 static readonly System.Collections.Generic.Dictionary<string,ContactIdentitySnapshot> priorIdentity=new();
 static readonly System.Collections.Generic.Dictionary<string,double> identityLastSeen=new();
 static readonly System.Collections.Generic.Dictionary<string,string> identityNearest=new();
 static StreamWriter endurance;static double nextEndurance;static MechRadarController[] observers;static readonly TrackSnapshot[] enduranceTracks=new TrackSnapshot[256];static double priorWall;static int priorFrame;
 [Serializable] class EnduranceRow{public double sim,wall,frameMs;public long allocated,reserved,managed;public int frames,projectiles,gc0,gc1,gc2;}
 [Serializable] class IdentityRow{public double sim,lastEvidence,uncertainty;public string observerId,observer,relation,party,reason,iff,affiliation,nearestActor;public uint track;public bool conflict;public float confidence,nearestDistance,secondDistance;public int nearestTeam;public Vector3 position;}
 static void AuditEndurance(double now,string dir)
 {
  if(now<nextEndurance)return;nextEndurance=now+1;SampleCosts(now,dir);
  if(ACunicombat.Simulation.Driver.CombatTelemetrySession.LastRecordingFailure!=null)throw new IOException("Telemetry recording failed; diagnostic run is incomplete: "+ACunicombat.Simulation.Driver.CombatTelemetrySession.LastRecordingFailure);
  if(endurance==null){endurance=new StreamWriter(dir+"/endurance.jsonl");observers=UnityEngine.Object.FindObjectsByType<MechRadarController>(FindObjectsInactive.Exclude);priorWall=Time.realtimeSinceStartupAsDouble;priorFrame=Time.frameCount;}
  SampleRadarService(now,dir);
  double wall=Time.realtimeSinceStartupAsDouble;int frames=Time.frameCount-priorFrame;
  endurance.WriteLine(JsonUtility.ToJson(new EnduranceRow{sim=now,wall=wall,frames=frames,frameMs=frames>0?(wall-priorWall)*1000/frames:0,allocated=Profiler.GetTotalAllocatedMemoryLong(),reserved=Profiler.GetTotalReservedMemoryLong(),managed=GC.GetTotalMemory(false),projectiles=MechTestProjectile.Registry.Count,gc0=GC.CollectionCount(0),gc1=GC.CollectionCount(1),gc2=GC.CollectionCount(2)}));priorWall=wall;priorFrame=Time.frameCount;
  if(captureMode!="performance")foreach(var observer in observers){if(observer==null)continue;string observerKey=observer.GetEntityId().ToString();int count=observer.CopyTracks(enduranceTracks);for(int i=0;i<count;i++){
   var t=enduranceTracks[i];if(t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy)continue;
   var pos=(Vector3)(Unity.Mathematics.float3)t.PredictPosition(observer.SimTimeSeconds);float first=float.MaxValue,second=float.MaxValue;MechRadarController nearest=null;
   foreach(var other in observers){if(other==null||other==observer)continue;float d=Vector3.Distance(pos,other.transform.position);if(d<first){second=first;first=d;nearest=other;}else if(d<second)second=d;}
   string identityKey=observerKey+"/"+t.TrackId.Packed;
   string nearestKey=nearest!=null&&first<=Math.Max(30,3*t.PredictPositionUncertainty(observer.SimTimeSeconds))&&second-first>500?nearest.GetEntityId().ToString():"";identityNearest.TryGetValue(identityKey,out var oldNearest);bool sameNearest=oldNearest==nearestKey;
   if(windowCapture!=null&&captureFrom<0&&!string.IsNullOrEmpty(oldNearest)&&!string.IsNullOrEmpty(nearestKey)&&!sameNearest)windowCapture.Trigger(Time.timeAsDouble,"association_change_review:"+identityKey);
   identityNearest[identityKey]=nearestKey;identityLastSeen[identityKey]=now;
   bool hadPrior=priorIdentity.TryGetValue(identityKey,out var previous);
   if(windowCapture!=null&&captureFrom<0&&hadPrior&&previous.Relation!=IdentityRelation.Hostile&&t.Identity.Relation==IdentityRelation.Hostile&&(previous.Relation==IdentityRelation.Friendly||previous.Party==IdentityParty.OwnAlliance))windowCapture.Trigger(Time.timeAsDouble,"friendly_to_hostile_review:"+identityKey);
   priorIdentity[identityKey]=t.Identity;
   if(!DetailedFrames&&hadPrior&&sameNearest&&previous.Relation==t.Identity.Relation&&previous.Party==t.Identity.Party&&previous.Iff==t.Identity.Iff&&previous.Conflict==t.Identity.Conflict&&previous.Reason==t.Identity.Reason)continue;
   endurance.WriteLine(JsonUtility.ToJson(new IdentityRow{sim=now,observer=observer.name,observerId=observerKey,track=t.TrackId.Packed,position=pos,affiliation=t.Affiliation.ToString(),relation=t.Identity.Relation.ToString(),party=t.Identity.Party.ToString(),reason=t.Identity.Reason.ToString(),iff=t.Identity.Iff.ToString(),conflict=t.Identity.Conflict,confidence=t.Identity.Confidence,lastEvidence=t.LastUpdateTime,uncertainty=t.PredictPositionUncertainty(observer.SimTimeSeconds),nearestActor=nearest!=null?nearest.name:"",nearestDistance=first,secondDistance=second,nearestTeam=nearest!=null&&nearest.GetComponent<CombatTestTarget>()!=null?nearest.GetComponent<CombatTestTarget>().TeamId:-1}));
  }}
  foreach(var key in identityLastSeen.Where(p=>now-p.Value>5).Select(p=>p.Key).ToArray()){identityLastSeen.Remove(key);identityNearest.Remove(key);priorIdentity.Remove(key);}
  endurance.Flush();
  double reserveGiB=UnityEditor.SessionState.GetFloat("EnduranceCombat.reserveGiB",8);
  if(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace<reserveGiB*1024*1024*1024)throw new IOException("Endurance capture stopped: free="+new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace+" reserveGiB="+reserveGiB+"; not a pass");
 }
 static void CloseEndurance(){radarService?.Dispose();radarService=null;endurance?.Dispose();endurance=null;CloseCosts();}
}
