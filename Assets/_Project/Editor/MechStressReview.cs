using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Simulation.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Large-battle scaling benchmark over the production playable scene. The scenario
// document clones the template mechs into two opposed groups; nothing is saved.
// Usage: -executeMethod MechStressReview.Run -stressMechs 50 -stressSeconds 60 -stressOutput <dir>
[InitializeOnLoad]
public static class MechStressReview
{
    const string Key = "MechStress.stage";
    const string Scenario = "Scenarios/CurrentBattlefield";
    static StreamWriter costs, frames;
    static double started, nextSample, wallStarted, settleUntil;
    static readonly Stopwatch frameClock = new();
    static readonly List<double> frameMs = new();
    static readonly FrameTiming[] timingScratch = new FrameTiming[8];
    // Engine-side main-thread markers: where frame time goes outside our own probes.
    static readonly (string category, string marker)[] EngineMarkers =
    {
        ("Scripts", "FixedBehaviourUpdate"),
        ("Physics", "FixedUpdate.PhysicsFixedUpdate"),
        ("Scripts", "Update.ScriptRunBehaviourUpdate"),
        ("Scripts", "PreLateUpdate.ScriptRunBehaviourLateUpdate"),
        ("Animation", "PreLateUpdate.DirectorUpdateAnimationBegin"),
        ("Animation", "PreLateUpdate.DirectorUpdateAnimationEnd"),
        ("Render", "PostLateUpdate.FinishFrameRendering"),
        ("GUI", "PostLateUpdate.PlayerUpdateCanvases"),
        ("GUI", "GUI.Repaint"),
        ("Scripts", "PostLateUpdate.UpdateAllRenderers"),
        ("Memory", "GC.Collect"),
        ("Physics", "Physics.Simulate"),
        ("Scripts", "FixedUpdate.ScriptRunDelayedFixedFrameRate"),
    };
    static Unity.Profiling.ProfilerRecorder[] engineRecorders;
    static double[] engineTotals;
    static long gcAllocBytes; static int sampledFrames;
    static Unity.Profiling.ProfilerRecorder gcAllocRecorder;

    static MechStressReview() { EditorApplication.update += Tick; }
    static string Arg(string name, string fallback)
    { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }

    /// <summary>Two groups of formations (4 per formation) facing each other across the gap.</summary>
    public static MechScenarioStore.Document BuildDocument(MechScenarioDefinition scenario, int mechs, float gapMeters, float spacingMeters)
    {
        var document = MechScenarioStore.Defaults(scenario);
        var friendly = scenario.units.First(u => !u.player && u.team == 1);
        var hostile = scenario.units.First(u => u.team == 2);
        // Keep the player; drop template AI so the counts are exact.
        document.units.RemoveAll(u => !u.player);
        int perSide = Math.Max(1, mechs / 2);
        for (int side = 0; side < 2; side++)
        {
            var template = side == 0 ? friendly : hostile;
            for (int i = 0; i < perSide - (side == 0 ? 1 : 0); i++)
            {
                var edit = MechScenarioStore.AddUnit(document, template);
                document.units.Add(edit);
                int formation = i / 4, slot = i % 4, columns = Mathf.CeilToInt(Mathf.Sqrt(perSide / 4f + 1));
                float x = (formation % columns - columns * .5f) * spacingMeters * 6 + (slot - 1.5f) * spacingMeters;
                float y = (formation / columns) * spacingMeters * 3;
                float z = side == 0 ? -gapMeters * .5f : gapMeters * .5f;
                edit.position = new Vector3(x, 1500 + y, z);
                edit.rotation = Quaternion.LookRotation(side == 0 ? Vector3.forward : Vector3.back);
                edit.formationId = $"stress-{side}-{formation}";
                edit.name = $"{(side == 0 ? "F" : "H")}{i:D2}";
            }
        }
        var player = document.units.First(u => u.player);
        player.position = new Vector3(0, 1500, -gapMeters * .5f - spacingMeters * 2);
        player.rotation = Quaternion.LookRotation(Vector3.forward);
        MechScenarioStore.NormalizeFormations(document);
        return document;
    }

    public static void Run()
    {
        var dir = Arg("-stressOutput", Path.GetFullPath("Artifacts/Stress-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(dir);
        int mechs = int.Parse(Arg("-stressMechs", "50"));
        float gap = float.Parse(Arg("-stressGap", "25000"), System.Globalization.CultureInfo.InvariantCulture);
        var scenario = Resources.Load<MechScenarioDefinition>(Scenario) ?? throw new Exception("Scenario missing: " + Scenario);
        SessionState.SetInt("MechStress.mechs", mechs);
        SessionState.SetFloat("MechStress.gap", gap);
        SessionState.SetString("MechStress.output", dir);
        SessionState.SetFloat("MechStress.seconds", float.Parse(Arg("-stressSeconds", "60"), System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(dir + "/scope.txt", $"Stress benchmark: requested mechs={mechs} (incl. player), gap={gap} m, headless={Arg("-stressHeadless", "1")}. Production scene and AI; player idle.\n");
        EditorSceneManager.OpenScene(scenario.scenePath, OpenSceneMode.Single);
        // The scenario store resets on play-mode entry, so the document is applied
        // like the front end does: inside play mode, followed by a scene reload.
        SessionState.SetString(Key, "spawn");
        EditorApplication.isPlaying = true;
    }

    static void Spawn()
    {
        var scenario = Resources.Load<MechScenarioDefinition>(Scenario);
        var document = BuildDocument(scenario, SessionState.GetInt("MechStress.mechs", 50), SessionState.GetFloat("MechStress.gap", 25000), 300);
        if (!MechScenarioStore.Begin(scenario, document, MechLoadoutSelectionStore.LoadCatalog(), out var error)) throw new Exception("Scenario rejected: " + error);
        var dir = SessionState.GetString("MechStress.output", "");
        File.AppendAllText(dir + "/scope.txt", $"Document units={MechScenarioStore.ActiveUnits?.Count}; templates={string.Join(",", document.units.Select(u => u.templateKey).Distinct())}\n");
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
        {
            var found = UnityEngine.Object.FindObjectsByType<MechAssembler>(FindObjectsInactive.Include).Where(a => a.gameObject.scene == scene).Select(a => MechScenarioStore.UnitKey(a.transform));
            File.AppendAllText(dir + "/scope.txt", $"sceneLoaded {scene.path}: assemblers={string.Join(",", found)}; activeScenario={(MechScenarioStore.ActiveScenario != null)}\n");
        };
        // -stressDevLogs 0: release-like run without the development recorders.
        bool devLogs = Arg("-stressDevLogs", "1") != "0";
        ACunicombat.Simulation.Driver.SimulationDriver.CombatTelemetryAllowed = devLogs;
        MechIdentityController.DefaultLogDecisions = devLogs;
        File.AppendAllText(dir + "/scope.txt", $"devLogs={devLogs}\n");
        SessionState.SetString(Key, "play");
        UnityEngine.SceneManagement.SceneManager.LoadScene(scenario.scenePath);
    }

    static void Tick()
    {
        var stage = SessionState.GetString(Key, "");
        if (stage == "finish" && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
        { SessionState.EraseString(Key); EditorApplication.Exit(0); return; }
        var dir = SessionState.GetString("MechStress.output", "");
        if (stage == "spawn" && EditorApplication.isPlaying && !EditorApplication.isCompiling)
        {
            try { Spawn(); } catch (Exception e) { File.WriteAllText(dir + "/error.txt", e.ToString()); Finish(); }
            return;
        }
        if (stage != "play" || !EditorApplication.isPlaying || EditorApplication.isCompiling) return;
        try
        {
            if (costs == null)
            {
                // Wait for the reloaded scene and its cloned units to settle.
                if (settleUntil == 0) settleUntil = EditorApplication.timeSinceStartup + 3;
                if (EditorApplication.timeSinceStartup < settleUntil) return;
                if (UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude).Length == 0) return;
                Application.runInBackground = true; QualitySettings.vSyncCount = 0;
                Time.captureDeltaTime = 1f / 60f;
                SimulationCostProbe.Reset(); SimulationCostProbe.Enabled = true;
                ACunicombat.Simulation.Driver.SimulationDriver.ParallelTracking = Arg("-stressSerialTracking", "0") != "1";
                ACunicombat.Tracking.CmkfBurst.Enabled = Arg("-stressBurst", "1") != "0";
                ACunicombat.Tracking.CmkfBurst.PairScoresEnabled = Arg("-stressBurst", "1") != "2";
                File.AppendAllText(dir + "/scope.txt", $"ParallelTracking={ACunicombat.Simulation.Driver.SimulationDriver.ParallelTracking}; burst={ACunicombat.Tracking.CmkfBurst.Enabled}; graphics={SystemInfo.graphicsDeviceType}\n");
                costs = new StreamWriter(dir + "/system-costs.jsonl");
                frames = new StreamWriter(dir + "/frames.jsonl");
                started = Time.timeAsDouble; nextSample = started + 1; wallStarted = EditorApplication.timeSinceStartup;
                var rigs = UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Include);
                File.AppendAllText(dir + "/scope.txt", $"Spawned rigs active={rigs.Count(r => r.gameObject.activeInHierarchy)} total={rigs.Length}; names={string.Join(",", rigs.Select(r => r.name).Take(12))}\n");
                if (Arg("-stressGcProfile", "0") == "1")
                {
                    SimulationCostProbe.BeginSample = name => UnityEngine.Profiling.Profiler.BeginSample(name);
                    SimulationCostProbe.EndSample = () => UnityEngine.Profiling.Profiler.EndSample();
                    UnityEditorInternal.ProfilerDriver.ClearAllFrames();
                    UnityEditorInternal.ProfilerDriver.enabled = true;
                    UnityEngine.Profiling.Profiler.enableAllocationCallstacks = true;
                }
                frameClock.Restart();
                return;
            }
            frameMs.Add(frameClock.Elapsed.TotalMilliseconds); frameClock.Restart();
            if (engineRecorders == null)
            {
                engineRecorders = new Unity.Profiling.ProfilerRecorder[EngineMarkers.Length]; engineTotals = new double[EngineMarkers.Length];
                for (int i = 0; i < EngineMarkers.Length; i++)
                {
                    var category = EngineMarkers[i].category switch { "Physics" => Unity.Profiling.ProfilerCategory.Physics, "Animation" => Unity.Profiling.ProfilerCategory.Animation, "Render" => Unity.Profiling.ProfilerCategory.Render, "GUI" => Unity.Profiling.ProfilerCategory.Gui, "Memory" => Unity.Profiling.ProfilerCategory.Memory, _ => Unity.Profiling.ProfilerCategory.Scripts };
                    engineRecorders[i] = Unity.Profiling.ProfilerRecorder.StartNew(category, EngineMarkers[i].marker);
                }
                gcAllocRecorder = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Memory, "GC.Alloc");
            }
            sampledFrames++;
            for (int i = 0; i < engineRecorders.Length; i++) if (engineRecorders[i].Valid) engineTotals[i] += engineRecorders[i].LastValue / 1e6;
            if (gcAllocRecorder.Valid) gcAllocBytes += gcAllocRecorder.LastValue;
            double t = Time.timeAsDouble - started;
            if (Time.timeAsDouble >= nextSample)
            {
                nextSample += 1;
                for (int i = 0; i < (int)SimulationCostProbe.SystemId.Count; i++)
                {
                    var id = (SimulationCostProbe.SystemId)i; var r = SimulationCostProbe.Take(id);
                    costs.WriteLine($"{{\"sim\":{t:F2},\"system\":\"{id}\",\"milliseconds\":{r.Milliseconds:F3},\"maximumMilliseconds\":{r.MaximumMilliseconds:F3},\"calls\":{r.Calls}}}");
                }
                int missiles = 0; foreach (var p in MechTestProjectile.Registry) if (p != null && p.IsEngagementActive) missiles++;
                int alive = UnityEngine.Object.FindObjectsByType<CombatTestTarget>(FindObjectsInactive.Exclude).Count(b => b.IsAlive);
                frameMs.Sort();
                double p50 = frameMs.Count > 0 ? frameMs[frameMs.Count / 2] : 0, p99 = frameMs.Count > 0 ? frameMs[Math.Min(frameMs.Count - 1, (int)(frameMs.Count * .99))] : 0;
                // Split wall time into CPU main thread, render thread and GPU (when supported).
                FrameTimingManager.CaptureFrameTimings();
                uint got = FrameTimingManager.GetLatestTimings((uint)timingScratch.Length, timingScratch);
                double main = 0, render = 0, gpu = 0;
                for (int i = 0; i < got; i++) { main += timingScratch[i].cpuMainThreadFrameTime; render += timingScratch[i].cpuRenderThreadFrameTime; gpu += timingScratch[i].gpuFrameTime; }
                if (got > 0) { main /= got; render /= got; gpu /= got; }
                frames.WriteLine($"{{\"sim\":{t:F2},\"frames\":{frameMs.Count},\"p50\":{p50:F2},\"p99\":{p99:F2},\"max\":{(frameMs.Count > 0 ? frameMs[^1] : 0):F2},\"mainMs\":{main:F2},\"renderMs\":{render:F2},\"gpuMs\":{gpu:F2},\"missiles\":{missiles},\"alive\":{alive}}}");
                if (engineRecorders != null)
                {
                    for (int i = 0; i < engineRecorders.Length; i++)
                    {
                        costs.WriteLine($"{{\"sim\":{t:F2},\"system\":\"Engine:{EngineMarkers[i].marker}\",\"milliseconds\":{engineTotals[i] / System.Math.Max(1, sampledFrames):F3},\"maximumMilliseconds\":0,\"calls\":{(engineRecorders[i].Valid ? 1 : 0)}}}");
                        engineTotals[i] = 0;
                    }
                    costs.WriteLine($"{{\"sim\":{t:F2},\"system\":\"Engine:GC.AllocBytes\",\"milliseconds\":{gcAllocBytes / 1048576.0:F3},\"maximumMilliseconds\":0,\"calls\":1}}");
                    gcAllocBytes = 0; sampledFrames = 0;
                }
                frameMs.Clear(); costs.Flush(); frames.Flush();
            }
            if (t >= SessionState.GetFloat("MechStress.seconds", 60) || EditorApplication.timeSinceStartup - wallStarted > 3600) Finish();
        }
        catch (Exception e) { File.WriteAllText(dir + "/error.txt", e.ToString()); Finish(); }
    }

    // Self GC allocation per sample path (main thread), summed over the frames still in the
    // profiler buffer. Written to gc-alloc.tsv: bytes per frame, then the call path.
    static void DumpGcProfile()
    {
        if (!UnityEditorInternal.ProfilerDriver.enabled) return;
        UnityEditorInternal.ProfilerDriver.enabled = false;
        var dir = SessionState.GetString("MechStress.output", "");
        var totals = new Dictionary<string, double>(); int frameCount = 0;
        var stack = new List<(string path, int end)>();
        for (int frame = UnityEditorInternal.ProfilerDriver.firstFrameIndex; frame <= UnityEditorInternal.ProfilerDriver.lastFrameIndex; frame++)
        {
          frameCount++;
          // Every profiled thread: worker-thread allocations (trackers, prewarm) count too.
          for (int thread = 0; thread < 64; thread++)
          {
            using var view = UnityEditorInternal.ProfilerDriver.GetRawFrameDataView(frame, thread);
            if (view == null || !view.valid) { if (thread > 0) break; continue; }
            var threadName = thread == 0 ? "" : "[" + view.threadName + "] ";
            // Samples are stored depth-first; each carries its descendant count.
            stack.Clear();
            for (int i = 0; i < view.sampleCount; i++)
            {
                while (stack.Count > 0 && i > stack[^1].end) stack.RemoveAt(stack.Count - 1);
                var name = view.GetSampleName(i) ?? "?";
                if (name == "GC.Alloc")
                {
                    long bytes = view.GetSampleMetadataCount(i) > 0 ? view.GetSampleMetadataAsLong(i, 0) : 0;
                    var owner = stack.Count > 0 ? stack[^1].path : "root";
                    // With allocation call stacks, attribute to the innermost project frames.
                    var callstack = new List<ulong>(); view.GetSampleCallstack(i, callstack);
                    var frames = new List<string>();
                    foreach (var address in callstack)
                    {
                        var method = view.ResolveMethodInfo(address);
                        if (method.methodName == null || !method.methodName.Contains("ACunicombat")) continue;
                        frames.Add(method.methodName.Substring(method.methodName.LastIndexOf(':') + 1) + ":" + method.sourceFileLine);
                        if (frames.Count == 3) break;
                    }
                    if (frames.Count > 0) owner = string.Join(" < ", frames);
                    owner = threadName + owner;
                    totals[owner] = (totals.TryGetValue(owner, out var t) ? t : 0) + bytes;
                }
                var parent = stack.Count > 0 ? stack[^1].path : "";
                // Keep only the last four levels so paths stay readable.
                var parts = (parent.Length == 0 ? name : parent + " > " + name).Split(" > ");
                stack.Add((string.Join(" > ", parts.Skip(System.Math.Max(0, parts.Length - 4))), i + view.GetSampleChildrenCountRecursive(i)));
            }
          }
        }
        File.WriteAllLines(dir + "/gc-alloc.tsv", totals.OrderByDescending(p => p.Value).Take(120).Select(p => $"{p.Value / System.Math.Max(1, frameCount):F0}	{p.Key}").Prepend($"frames={frameCount}"));
    }

    static void Finish()
    {
        try { DumpGcProfile(); } catch (Exception e) { File.WriteAllText(SessionState.GetString("MechStress.output", "") + "/gc-error.txt", e.ToString()); }
        costs?.Dispose(); costs = null; frames?.Dispose(); frames = null;
        if (engineRecorders != null) { foreach (var r in engineRecorders) r.Dispose(); engineRecorders = null; }
        gcAllocRecorder.Dispose();
        SimulationCostProbe.Enabled = false; Time.captureDeltaTime = 0;
        SessionState.SetString(Key, "finish"); EditorApplication.isPlaying = false;
    }
}
