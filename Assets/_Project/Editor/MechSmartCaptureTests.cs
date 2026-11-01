using System;
using System.IO;
using System.IO.Compression;
using ACunicombat.Simulation.Driver;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
public class MechSmartCaptureTests
{
 [Test] public void ClosedLogsAreVerifiedBeforeRemovingOriginals()
 {
  string dir=Folder();Directory.CreateDirectory(dir);string path=dir+"/sample.jsonl";
  File.WriteAllText(path,"{\"目标\":\"友方\"}\n");
  MechReviewLogArchive.CompressClosedRun(dir);
  Assert.IsFalse(File.Exists(path));Assert.AreEqual("{\"目标\":\"友方\"}\n",Read(path+".gz"));
  var manifest=UnityEngine.JsonUtility.FromJson<MechReviewLogArchive.Manifest>(File.ReadAllText(dir+"/log-archive.json"));
  Assert.AreEqual(1,manifest.files.Length);Assert.IsTrue(string.IsNullOrEmpty(manifest.files[0].error));Assert.AreEqual(64,manifest.files[0].sha256.Length);
 }
 [Test] public void ExistingArchiveNeverCausesSourceDeletion()
 {
  string dir=Folder();Directory.CreateDirectory(dir);string path=dir+"/sample.jsonl";
  File.WriteAllText(path,"new evidence");File.WriteAllText(path+".gz","previous archive");
  MechReviewLogArchive.CompressClosedRun(dir);
  Assert.AreEqual("new evidence",File.ReadAllText(path));Assert.AreEqual("previous archive",File.ReadAllText(path+".gz"));
 }

 [Serializable] sealed class Envelope {public int version=1;public double time;public string lossless;}
 [Test] public void CompactEnvelopeRetainsExactRecordedNumericAndIdentityPayload()
 {
  var frame=new ACunicombat.FireControl.MissileContactReplayFrame{time=1.234567890123456,observations=new[]{new ACunicombat.FireControl.MissileReplayTrack{id=16777217,launch=Guid.NewGuid().ToString(),state=new MissileTrackSnapshot{KnownModel=new Unity.Collections.FixedString128Bytes("火石17"),Position=new Unity.Mathematics.double3(1.0/3,123456.789012345,0),PositionUncertaintyMeters=float.PositiveInfinity}}}};
  frame.lossless=ReplayValueCodec.Encode(frame);
  string json=UnityEngine.JsonUtility.ToJson(new Envelope{time=frame.time,lossless=frame.lossless});
  var restored=UnityEngine.JsonUtility.FromJson<ACunicombat.FireControl.MissileContactReplayFrame>(json).RestoreExact();
  Assert.AreEqual(frame.lossless,ReplayValueCodec.Encode(restored));
 }
 static string Folder()=>Path.Combine("Artifacts/SmartTestCapture-20261027/unit",Guid.NewGuid().ToString("N"));
 static string Read(string path){using var stream=new GZipStream(File.OpenRead(path),CompressionMode.Decompress);using var r=new StreamReader(stream);return r.ReadToEnd();}
 [Test] public void TriggerKeepsPrehistoryAndCoalescesWithoutDuplicatingIt()
 {
  string dir=Folder();using(var b=new MechReviewWindowBuffer(dir,2,2)){
   b.Add(0,"continuity","zero");b.Add(1,"continuity","one");b.Add(2,"contacts","two");b.Trigger(2,"first");b.Add(3,"continuity","three");b.Trigger(3,"same incident");b.Add(4,"continuity","four");b.Add(6,"continuity","outside");Assert.AreEqual(1,b.Statistics.windows);
  }
  string text=Read(dir+"/window-001/continuity.jsonl.gz");Assert.AreEqual("zero\none\nthree\nfour\n",text.Replace("\r\n","\n"));Assert.AreEqual("two",Read(dir+"/window-001/contacts.jsonl.gz").Trim());
 }
 [Test] public void MemoryAndDiskBudgetsAreExplicitAndBounded()
 {
  string dir=Folder();using(var b=new MechReviewWindowBuffer(dir,5,5,100,120,1)){
   b.Add(0,"continuity",new string('a',80));b.Add(1,"continuity",new string('b',80));Assert.Greater(b.Statistics.droppedPreRoll,0);b.Trigger(1,"limited");b.Add(2,"continuity",new string('c',80));Assert.IsTrue(b.Statistics.budgetExhausted);Assert.LessOrEqual(b.Statistics.queuedBytes,120);Assert.Throws<ArgumentException>(()=>b.Add(3,"../escape","x"));
  }
  Assert.IsTrue(File.Exists(dir+"/capture-stats.json"));
 }
 [Test] public void CompactTelemetryThrottlesNumericChurnButKeepsSemanticChanges()
 {
  string dir=Folder();bool prior=CombatTelemetrySession.EventFocusedSnapshots;
  try{
   CombatTelemetrySession.EventFocusedSnapshots=true;
   using(var s=new CombatTelemetrySession(dir,"events")){
    var t=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(1,1)),TargetIntentConfidence=.51f};
    for(int i=0;i<100;i++){t.TargetIntentConfidence=.51f+i*.0001f;s.RecordMissileTrack(i*.01,"observer",t);}
    t.TargetIntent=MissileTargetIntent.Ownship;s.RecordMissileTrack(.99,"observer",t);
   }
   int count=0;foreach(var f in Directory.GetFiles(dir,"*.jsonl"))foreach(var line in File.ReadLines(f))if(line.Contains("\"system\":\"missile_fusion\""))count++;
   Assert.AreEqual(2,count,"Initial snapshot and immediate intent change, not every confidence fluctuation");
  }finally{CombatTelemetrySession.EventFocusedSnapshots=prior;}
 }
}
