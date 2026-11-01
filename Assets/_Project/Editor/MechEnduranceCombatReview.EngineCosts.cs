using System;
using System.IO;
using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;
public static partial class MechEnduranceCombatReview
{
    sealed class EngineCounter {public string Name,Unit;public ProfilerRecorder Recorder;public readonly List<ProfilerRecorderSample> Samples=new List<ProfilerRecorderSample>(256);}
    static readonly List<EngineCounter> engineCounters=new List<EngineCounter>();
    static StreamWriter engineWriter;
    [Serializable] sealed class EngineRow {public double sim,mean,maximum;public int samples;public string name,unit;}
    static void BeginEngineCosts()
    {
        if(Arg("-engineProbe","0")!="1")return;
        var wanted=new HashSet<string>{"Main Thread","Render Thread","PlayerLoop","EditorLoop","GC.Collect","GC Allocated In Frame","Gfx.WaitForPresentOnGfxThread","Gfx.WaitForGfxCommandsFromMainThread","Camera.Render","GPU Frame Time","Draw Calls Count","Triangles Count"};
        var handles=new List<ProfilerRecorderHandle>();ProfilerRecorderHandle.GetAvailable(handles);
        foreach(var handle in handles)
        {
            var d=ProfilerRecorderHandle.GetDescription(handle);if(!wanted.Remove(d.Name))continue;
            var recorder=ProfilerRecorder.StartNew(d.Category,d.Name,256,ProfilerRecorderOptions.StartImmediately|ProfilerRecorderOptions.WrapAroundWhenCapacityReached|ProfilerRecorderOptions.SumAllSamplesInFrame);
            engineCounters.Add(new EngineCounter{Name=d.Name,Unit=d.UnitType.ToString(),Recorder=recorder});
        }
        File.WriteAllLines(UnityEditor.SessionState.GetString("EnduranceCombat.output","")+"/unavailable-engine-counters.txt",wanted);
    }
    static void SampleEngineCosts(double now,string directory)
    {
        if(engineCounters.Count==0)return;
        if(engineWriter==null)engineWriter=new StreamWriter(directory+"/engine-costs.jsonl");
        foreach(var counter in engineCounters)
        {
            counter.Samples.Clear();counter.Recorder.CopyTo(counter.Samples,true);double sum=0,max=0;
            foreach(var sample in counter.Samples){sum+=sample.Value;max=Math.Max(max,sample.Value);}
            engineWriter.WriteLine(JsonUtility.ToJson(new EngineRow{sim=now,name=counter.Name,unit=counter.Unit,samples=counter.Samples.Count,mean=counter.Samples.Count>0?sum/counter.Samples.Count:0,maximum=max}));
        }
        engineWriter.Flush();
    }
    static void CloseEngineCosts(){foreach(var counter in engineCounters)counter.Recorder.Dispose();engineCounters.Clear();engineWriter?.Dispose();engineWriter=null;}
}
