using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.Core;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEngine;
namespace ACunicombat.Tests.EditMode
{
    public class CombatTelemetryFailureTests
    {
        string directory;
        [SetUp] public void Setup(){directory=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"acunicombat-telemetry-fault-"+Guid.NewGuid().ToString("N")));}
        [TearDown] public void Cleanup()
        {
            typeof(CombatTelemetrySession).GetMethod("ResetCapturePolicy",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            Assert.IsTrue(directory.StartsWith(parent,StringComparison.OrdinalIgnoreCase));
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
        sealed class FullDiskStream:Stream
        {
            public override bool CanRead=>false;public override bool CanSeek=>true;public override bool CanWrite=>true;
            public override long Length=>0;public override long Position{get=>0;set{}}
            public override void Flush()=>throw new IOException("Injected disk full");
            public override void Write(byte[] b,int o,int c)=>throw new IOException("Injected disk full");
            public override int Read(byte[] b,int o,int c)=>throw new NotSupportedException();
            public override long Seek(long o,SeekOrigin s)=>0;public override void SetLength(long v){}
        }
        [Test] public void FailedWriterStopsRetainingPayloadsAndReportsFailureOnce()
        {
            using var session=new CombatTelemetrySession(directory,"fault",64*1024*1024);
            var track=new MissileTrackSnapshot{TrackId=new TrackId(new Handle(1,1))};session.RecordMissileTrack(0,"observer",in track);Assert.Greater(session.CachedObservationCount,0);
            var field=typeof(CombatTelemetrySession).GetField("writer",BindingFlags.Instance|BindingFlags.NonPublic);
            ((StreamWriter)field.GetValue(session)).Dispose();field.SetValue(session,new StreamWriter(new FullDiskStream()));
            Assert.DoesNotThrow(()=>session.RecordDriverStart(1,"observer",1,.01,.04,.04));
            Assert.DoesNotThrow(()=>session.Flush());Assert.IsFalse(session.IsRecording);
            Assert.That(session.LastWriteFailure,Does.Contain("Injected disk full"));
            Assert.IsTrue(session.TryTakeFailure(out _));Assert.IsFalse(session.TryTakeFailure(out _));
            for(int i=0;i<10000;i++)
            {
                track.TrackId=new TrackId(new Handle(i+2,1));session.RecordMissileTrack(2+i,"observer",in track);
                session.RecordDriverStart(2+i,"ignored payload",1,.01,.04,.04);
            }
            Assert.AreEqual(0,session.BufferedCharacterCount);Assert.AreEqual(0,session.CachedObservationCount);
            Assert.DoesNotThrow(()=>session.Dispose());
        }
        [Serializable] public class Row {public long sequence;public string source;public double simTime;}
        [Test] public void ConcurrentRecordsRemainIndependentValidJsonLines()
        {
            string path;
            using(var session=new CombatTelemetrySession(directory,"concurrent",64*1024*1024))
            {
                Parallel.For(0,1000,i=>session.RecordDriverStart(i,"worker-\""+i,1,.01,.04,.04));
                session.Flush();Assert.IsTrue(session.IsRecording);path=session.CurrentFilePath;
            }
            var lines=File.ReadAllLines(path);Assert.AreEqual(1000,lines.Length);
            var seen=new bool[1000];for(int i=0;i<lines.Length;i++)
            {
                var row=JsonUtility.FromJson<Row>(lines[i]);Assert.AreEqual(i+1,row.sequence);
                int index=(int)row.simTime;Assert.AreEqual("worker-\""+index,row.source);Assert.IsFalse(seen[index]);seen[index]=true;
            }
        }
    }
}
