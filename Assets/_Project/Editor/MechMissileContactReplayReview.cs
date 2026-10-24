using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using ACunicombat.FireControl;
using UnityEditor;
using UnityEngine;
using ACunicombat.Tracking;
using System.Collections.Generic;

public static class MechMissileContactReplayReview
{
    [Serializable] private sealed class Result {public int frames,failures,permutationFailures;public string firstFailure;public bool passed;}
    [Serializable] private sealed class StateRecord {public string manager,operation;public double time;public RadarReplayTrack[] tracks;}
    private static string Option(string[] args,string name,string fallback="")
    {int at=Array.IndexOf(args,name);return at>=0&&at+1<args.Length?args[at+1]:fallback;}
    private static IEnumerable<string> ReadLines(string path)
    {
        var files=Directory.Exists(path)?Directory.GetFiles(path,"*.jsonl.gz").OrderBy(p=>p,StringComparer.Ordinal).ToArray():new[]{path};
        foreach(string file in files)
        {
            using(var input=File.OpenRead(file))
            using(var stream=file.EndsWith(".gz",StringComparison.OrdinalIgnoreCase)?(Stream)new GZipStream(input,CompressionMode.Decompress):input)
            using(var reader=new StreamReader(stream))
            {string line;while((line=reader.ReadLine())!=null)yield return line;}
        }
    }
    public static void Run()
    {
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-contactReplay");
        if(at<0||at+1>=args.Length){EditorApplication.Exit(2);return;}
        string path=args[at+1];var result=new Result();
        try
        {
            foreach(string line in ReadLines(path))
            {
                var frame=JsonUtility.FromJson<MissileContactReplayFrame>(line).RestoreExact();
                if(!frame.Matches(out string detail)){result.failures++;if(result.firstFailure==null)result.firstFailure=$"t={frame.time:R}: {detail}";}
                Array.Reverse(frame.observations);
                if(!frame.Matches(out detail)){result.permutationFailures++;if(result.firstFailure==null)result.firstFailure=$"reversed input t={frame.time:R}: {detail}";}
                result.frames++;
            }
            result.passed=result.frames>0&&result.failures==0&&result.permutationFailures==0;
        }
        catch(Exception ex){result.failures++;result.firstFailure=ex.ToString();}
        File.WriteAllText(path+".result.json",JsonUtility.ToJson(result,true));
        EditorApplication.Exit(result.passed?0:1);
    }

    public static void RunRadar()
    {
        var args=Environment.GetCommandLineArgs();int at=Array.IndexOf(args,"-radarReplay");
        if(at<0||at+1>=args.Length){EditorApplication.Exit(2);return;}
        string path=args[at+1];var result=new Result();var managers=new Dictionary<string,RadarTrackManager>();
        string output=Option(args,"-radarReplayResultPrefix",path),selectedManager=Option(args,"-radarReplayManager");
        StreamWriter decisions=Array.IndexOf(args,"-radarReplayAssociations")>=0?new StreamWriter(output+".associations.jsonl"):null;
        StreamWriter states=Array.IndexOf(args,"-radarReplaySnapshots")>=0?new StreamWriter(output+".snapshots.jsonl"):null;
        string replayOrigin=null;
        Action<RadarBatchDecision> record=d=>{d.manager=replayOrigin;decisions.WriteLine(JsonUtility.ToJson(d));};
        if(decisions!=null)RadarReplayJournal.BatchProduced+=record;
        try
        {
            foreach(string line in ReadLines(path))
            {
                var op=JsonUtility.FromJson<RadarReplayOperation>(line);
                if(selectedManager.Length>0&&op.manager!=selectedManager)continue;
                if(op.version!=1)throw new InvalidOperationException("Unsupported radar input schema");
                if(op.kind=="create")managers.Add(op.manager,new RadarTrackManager());
                if(!managers.TryGetValue(op.manager,out var manager))throw new InvalidOperationException("Missing initial radar state: "+op.manager);
                replayOrigin=op.manager;
                if(!op.Apply(manager,out string detail)){result.failures++;if(result.firstFailure==null)result.firstFailure=$"operation {result.frames} t={op.time:R}: {detail}";}
                states?.WriteLine(JsonUtility.ToJson(new StateRecord{manager=op.manager,operation=op.kind,time=op.time,tracks=RadarReplayJournal.Snapshot(manager)}));
                result.frames++;
            }
            result.passed=result.frames>0&&result.failures==0;
        }
        catch(Exception ex){result.failures++;result.firstFailure=ex.ToString();}
        finally {if(decisions!=null){RadarReplayJournal.BatchProduced-=record;decisions.Dispose();}states?.Dispose();}
        File.WriteAllText(output+".result.json",JsonUtility.ToJson(result,true));EditorApplication.Exit(result.passed?0:1);
    }
}
