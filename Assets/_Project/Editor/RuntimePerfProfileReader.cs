using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
public static class RuntimePerfProfileReader
{
 [Serializable] public class Row {public string name;public long calls,bytes;public double inclusiveMs,selfMs,maximumMs;}
 [Serializable] public class Report {public int frames;public double frameMs;public Row[] samples,allocations;}
 public static void Run()
 {
  var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-cpuProfile");string path=args[at+1];if(!ProfilerDriver.LoadProfile(path,false))throw new Exception("Cannot load profile");
  var sums=new Dictionary<string,Row>();var alloc=new Dictionary<string,Row>();var report=new Report();
  for(int frame=ProfilerDriver.firstFrameIndex;frame<=ProfilerDriver.lastFrameIndex;frame++)
  using(var data=ProfilerDriver.GetRawFrameDataView(frame,0))
  {
   if(!data.valid)continue;report.frames++;var stack=new Stack<(int end,string name)>();
   if(data.sampleCount>0)report.frameMs+=data.GetSampleTimeMs(0);
   for(int i=0;i<data.sampleCount;i++)
   {
    while(stack.Count>0&&i>stack.Peek().end)stack.Pop();string name=data.GetSampleName(i);double duration=data.GetSampleTimeMs(i);int end=i+data.GetSampleChildrenCountRecursive(i);double own=duration;
    for(int child=i+1;child<=end;child+=1+data.GetSampleChildrenCountRecursive(child))own-=data.GetSampleTimeMs(child);
    if(!sums.TryGetValue(name,out var row)){row=new Row{name=name};sums.Add(name,row);}row.calls++;row.inclusiveMs+=duration;row.selfMs+=Math.Max(0,own);row.maximumMs=Math.Max(row.maximumMs,duration);
    if(name=="GC.Alloc"&&data.GetSampleMetadataCount(i)>0)
    {
     string parent=string.Join(" > ",stack.Take(4).Reverse().Select(x=>x.name));if(!alloc.TryGetValue(parent,out var a)){a=new Row{name=parent};alloc.Add(parent,a);}a.calls++;a.bytes+=data.GetSampleMetadataAsLong(i,0);
    }
    if(end>i)stack.Push((end,name));
   }
  }
  report.samples=sums.Values.OrderByDescending(x=>x.selfMs).Take(150).ToArray();report.allocations=alloc.Values.OrderByDescending(x=>x.bytes).Take(80).ToArray();
  File.WriteAllText(Path.ChangeExtension(path,"summary.json"),JsonUtility.ToJson(report,true));EditorApplication.Exit(0);
 }
}
