using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Presentation-only benchmark: N burning missile plumes flying past a camera in an empty
// scene. Measures the main-thread, render-thread and GPU cost of the effects alone.
// Usage: -executeMethod MechEffectsStressReview.Run -fxMissiles 300 -fxSeconds 10 -fxOutput <file>
[InitializeOnLoad]
public static class MechEffectsStressReview
{
    const string Key = "MechFx.stage";
    static readonly List<Transform> missiles = new();
    static readonly List<double> mainMs = new(), renderMs = new(), gpuMs = new(), frameMs = new();
    static readonly FrameTiming[] timings = new FrameTiming[1];
    static double started, last; static bool captured, closeUp;
    static Unity.Profiling.ProfilerRecorder renderers, finish, lateUpdate;
    static readonly List<double> renderersMs = new(), finishMs = new(), lateMs = new();

    static MechEffectsStressReview() { EditorApplication.update += Tick; }
    static string Arg(string name, string fallback)
    { var a = Environment.GetCommandLineArgs(); int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback; }

    public static void Run()
    {
        SessionState.SetInt("MechFx.count", int.Parse(Arg("-fxMissiles", "300")));
        SessionState.SetFloat("MechFx.seconds", float.Parse(Arg("-fxSeconds", "10"), System.Globalization.CultureInfo.InvariantCulture));
        SessionState.SetInt("MechFx.close", int.Parse(Arg("-fxClose", "0")));
        SessionState.SetInt("MechFx.gpu", int.Parse(Arg("-fxGpu", "1")));
        SessionState.SetInt("MechFx.lights", int.Parse(Arg("-fxLights", "1")));
        SessionState.SetInt("MechFx.particles", int.Parse(Arg("-fxParticles", "1")));
        SessionState.SetString("MechFx.output", Arg("-fxOutput", Path.GetFullPath("Artifacts/fx.txt")));
        EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        SessionState.SetString(Key, "spawn");
        EditorApplication.isPlaying = true;
    }

    static void Spawn()
    {
        int count = SessionState.GetInt("MechFx.count", 300);
        var camera = Camera.main;
        camera.transform.SetPositionAndRotation(new Vector3(0, 0, -400), Quaternion.identity);
        camera.farClipPlane = 20000;
        MechGpuPlumeSystem.Enabled = SessionState.GetInt("MechFx.gpu", 1) == 1;
        var rng = new System.Random(20261102);
        missiles.Clear(); closeUp = false;
        for (int i = 0; i < count; i++)
        {
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());
            body.transform.localScale = new Vector3(.3f, 1.8f, .3f);
            var root = new GameObject("FxMissile").transform;
            body.transform.SetParent(root, false);
            body.transform.localRotation = Quaternion.Euler(90, 0, 0);
            root.position = new Vector3((float)(rng.NextDouble() - .5) * 1200, (float)(rng.NextDouble() - .5) * 600, (float)rng.NextDouble() * 2000);
            root.rotation = Quaternion.LookRotation(new Vector3((float)(rng.NextDouble() - .5), (float)(rng.NextDouble() - .5) * .3f, 1));
            var plume = root.gameObject.AddComponent<MechMissilePlume>();
            plume.Build(1.8f, 1f, root);
            plume.SetBurning(true);
            if (SessionState.GetInt("MechFx.lights", 1) == 0) foreach (var l in root.GetComponentsInChildren<Light>()) l.gameObject.SetActive(false);
            if (SessionState.GetInt("MechFx.particles", 1) == 0) foreach (var ps in root.GetComponentsInChildren<ParticleSystem>()) ps.gameObject.SetActive(false);
            missiles.Add(root);
        }
        if (SessionState.GetInt("MechFx.close", 0) == 1)
        {
            // Close-up: a few missiles crossing 40-120 m in front of the camera, no wrap-around.
            for (int i = 0; i < missiles.Count; i++) missiles[i].gameObject.SetActive(i < 6);
            for (int i = 0; i < 6 && i < missiles.Count; i++)
            { missiles[i].SetPositionAndRotation(new Vector3(-60 + i * 4, -6 + i * 3, -360 + i * 12), Quaternion.LookRotation(new Vector3(1, .05f * i, .15f))); }
            closeUp = true;
        }
        renderers = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Scripts, "PostLateUpdate.UpdateAllRenderers");
        finish = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "PostLateUpdate.FinishFrameRendering");
        lateUpdate = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Scripts, "PreLateUpdate.ScriptRunBehaviourLateUpdate");
        Application.runInBackground = true; QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
        started = -1; captured = false;
        SessionState.SetString(Key, "play");
    }

    static void Tick()
    {
        var stage = SessionState.GetString(Key, "");
        if (stage == "finish" && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
        { SessionState.EraseString(Key); EditorApplication.Exit(0); return; }
        if (stage == "spawn" && EditorApplication.isPlaying && !EditorApplication.isCompiling) { Spawn(); return; }
        if (stage != "play" || !EditorApplication.isPlaying) return;
        double now = EditorApplication.timeSinceStartup;
        float dt = Time.deltaTime;
        foreach (var m in missiles)
        {
            m.position += m.forward * (closeUp ? 60f : 300f) * dt;
            if (closeUp) continue;
            if (m.position.z > 2500) m.position -= m.forward * 2600f;
        }
        if (started < 0) { started = now; last = now; return; }
        if (now - started < 2) { last = now; return; } // warm-up: plumes reach steady particle counts
        if (!captured && now - started >= 4) { captured = true; ScreenCapture.CaptureScreenshot(Path.ChangeExtension(SessionState.GetString("MechFx.output", "fx.txt"), ".png")); }
        frameMs.Add((now - last) * 1000); last = now;
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
        { mainMs.Add(timings[0].cpuMainThreadFrameTime); renderMs.Add(timings[0].cpuRenderThreadFrameTime); gpuMs.Add(timings[0].gpuFrameTime); }
        if (renderers.Valid) renderersMs.Add(renderers.LastValue / 1e6);
        if (finish.Valid) finishMs.Add(finish.LastValue / 1e6);
        if (lateUpdate.Valid) lateMs.Add(lateUpdate.LastValue / 1e6);
        if (now - started >= 2 + SessionState.GetFloat("MechFx.seconds", 10))
        {
            static double Median(List<double> v) { if (v.Count == 0) return 0; v.Sort(); return v[v.Count / 2]; }
            File.WriteAllText(SessionState.GetString("MechFx.output", "fx.txt"),
                $"gpu={MechGpuPlumeSystem.Enabled} missiles={missiles.Count} frames={frameMs.Count} frameMs={Median(frameMs):F2} mainMs={Median(mainMs):F2} renderMs={Median(renderMs):F2} gpuMs={Median(gpuMs):F2} " +
                $"updateAllRenderers={Median(renderersMs):F2} finishFrameRendering={Median(finishMs):F2} lateUpdate={Median(lateMs):F2}\n");
            renderers.Dispose(); finish.Dispose(); lateUpdate.Dispose();
            SessionState.SetString(Key, "finish"); EditorApplication.isPlaying = false;
        }
    }
}
