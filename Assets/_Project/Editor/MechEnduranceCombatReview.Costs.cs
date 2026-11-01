using System;
using System.IO;
using ACunicombat.Simulation.Core;
using UnityEngine;
public static partial class MechEnduranceCombatReview
{
    static StreamWriter costWriter;
    [Serializable] sealed class CostRow {public double sim,milliseconds,maximumMilliseconds;public long calls;public string system;}
    static void BeginCosts()
    {
        SimulationCostProbe.Reset();SimulationCostProbe.Enabled=Arg("-costProbe","0")=="1";
        SimulationCostProbe.BeginSample=name=>UnityEngine.Profiling.Profiler.BeginSample(name);SimulationCostProbe.EndSample=()=>UnityEngine.Profiling.Profiler.EndSample();
        BeginEngineCosts();
    }
    static void SampleCosts(double sim,string directory)
    {
        SampleCpuWindow(sim,directory);
        SampleEngineCosts(sim,directory);
        if(!SimulationCostProbe.Enabled)return;
        if(costWriter==null)costWriter=new StreamWriter(directory+"/system-costs.jsonl");
        for(int i=0;i<(int)SimulationCostProbe.SystemId.Count;i++)
        {
            var id=(SimulationCostProbe.SystemId)i;var r=SimulationCostProbe.Take(id);
            costWriter.WriteLine(JsonUtility.ToJson(new CostRow{sim=sim,system=id.ToString(),calls=r.Calls,milliseconds=r.Milliseconds,maximumMilliseconds=r.MaximumMilliseconds}));
        }
        costWriter.Flush();
    }
    static void CloseCosts(){StopCpuWindow();costWriter?.Dispose();costWriter=null;SimulationCostProbe.Enabled=false;SimulationCostProbe.BeginSample=null;SimulationCostProbe.EndSample=null;CloseEngineCosts();}
}
