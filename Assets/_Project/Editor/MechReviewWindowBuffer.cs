using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
public sealed class MechReviewWindowBuffer : IDisposable
{
 sealed class Entry {public double Time;public string Stream,Line;public int Bytes;}
 sealed class Command {public int Window;public string Stream,Line,Manifest;public bool Close;}
 [Serializable] public sealed class Stats {public int windows,droppedPreRoll;public long queuedBytes;public bool budgetExhausted;public string error;}
 [Serializable] sealed class WindowInfo {public double trigger,actualStart,requestedPreSeconds;public int droppedPreRoll;public string reason;}
 readonly Queue<Entry> ring=new();readonly BlockingCollection<Command> queue=new();readonly Task worker;readonly string directory;readonly double pre,post;readonly long memoryLimit,byteLimit;readonly int windowLimit;
 long ringBytes;double until=-1,windowOpened;int active;bool disposed;public Stats Statistics{get;}=new Stats();public bool Active=>active!=0;
 public MechReviewWindowBuffer(string directory,double pre=5,double post=5,long memoryLimit=64L*1024*1024,long byteLimit=128L*1024*1024,int windowLimit=8)
 {this.directory=directory;this.pre=pre;this.post=post;this.memoryLimit=memoryLimit;this.byteLimit=byteLimit;this.windowLimit=windowLimit;Directory.CreateDirectory(directory);worker=Task.Run(WriteLoop);}
 public void Add(double time,string stream,string line){if(disposed)throw new ObjectDisposedException(nameof(MechReviewWindowBuffer));if(stream!="continuity"&&stream!="contacts")throw new ArgumentException("Unknown capture stream");Advance(time);var e=new Entry{Time=time,Stream=stream,Line=line,Bytes=Encoding.UTF8.GetByteCount(line)+1};ring.Enqueue(e);ringBytes+=e.Bytes;while(ring.Count>0&&(time-ring.Peek().Time>pre||ringBytes>memoryLimit)){var removed=ring.Dequeue();ringBytes-=removed.Bytes;if(time-removed.Time<=pre)Statistics.droppedPreRoll++;}if(Active)Enqueue(e);}
 public void Trigger(double time,string reason){Advance(time);if(Statistics.budgetExhausted)return;if(Active){until=Math.Min(windowOpened+20,Math.Max(until,time+post));return;}if(Statistics.windows>=windowLimit){Statistics.budgetExhausted=true;return;}active=++Statistics.windows;windowOpened=time;until=time+post;queue.Add(new Command{Window=active,Manifest=JsonUtility.ToJson(new WindowInfo{trigger=time,actualStart=ring.Count>0?ring.Peek().Time:time,requestedPreSeconds=pre,droppedPreRoll=Statistics.droppedPreRoll,reason=reason})});foreach(var e in ring){if(!Active)break;Enqueue(e);}}
 void Enqueue(Entry e){if(Statistics.queuedBytes+e.Bytes>byteLimit){Statistics.budgetExhausted=true;CloseWindow();return;}Statistics.queuedBytes+=e.Bytes;queue.Add(new Command{Window=active,Stream=e.Stream,Line=e.Line});}
 public void Advance(double now){if(Active&&now>until)CloseWindow();}
 void CloseWindow(){if(!Active)return;queue.Add(new Command{Window=active,Close=true});active=0;}
 void WriteLoop(){var writers=new Dictionary<string,StreamWriter>();try{foreach(var c in queue.GetConsumingEnumerable()){string folder=Path.Combine(directory,"window-"+c.Window.ToString("D3"));if(c.Manifest!=null){Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"capture.json"),c.Manifest);continue;}if(c.Close){foreach(var w in writers.Values)w.Dispose();writers.Clear();continue;}if(!writers.TryGetValue(c.Stream,out var writer)){Directory.CreateDirectory(folder);writer=new StreamWriter(new GZipStream(File.Create(Path.Combine(folder,c.Stream+".jsonl.gz")),System.IO.Compression.CompressionLevel.Fastest),new UTF8Encoding(false),65536);writer.NewLine="\n";writers[c.Stream]=writer;}writer.WriteLine(c.Line);}}catch(Exception e){Statistics.error=e.ToString();}finally{foreach(var w in writers.Values)w.Dispose();}}
 public void Dispose(){if(disposed)return;disposed=true;CloseWindow();queue.CompleteAdding();try{if(!worker.Wait(TimeSpan.FromSeconds(30)))Statistics.error="Capture writer timeout";}catch(Exception e){Statistics.error=e.ToString();}File.WriteAllText(Path.Combine(directory,"capture-stats.json"),JsonUtility.ToJson(Statistics,true));if(worker.IsCompleted)queue.Dispose();}
}
