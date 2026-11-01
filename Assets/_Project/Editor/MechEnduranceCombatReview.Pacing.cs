using System;
using System.IO;
using UnityEditor;
using UnityEngine;
public static partial class MechEnduranceCombatReview
{
    [Serializable] sealed class PacingReport
    {
        public bool requestedFastLoop,headless;
        public double simulatedSeconds,wallSeconds,speedup,maximumRenderStepError,maximumUnscaledStepError;
        public int sampledFrames,missedFrames;
        public float physicsStep;
        public string scope="Full Unity player loop; fixed simulated frame duration, timeScale=1. Not graphics performance or deterministic battle equivalence proof.";
    }
    static PacingReport pacing;
    static double previousRenderTime;
    static double previousUnscaledTime;
    static int previousRenderFrame;
    static void BeginPacing()
    {
        bool fast=Arg("-enduranceFastLoop","0")=="1";
        bool headless=SessionState.GetBool("EnduranceCombat.headless",false);
        if(fast&&(!headless||captureMode=="full"))throw new InvalidOperationException("Fast loop requires headless diagnostic capture; full rendered acceptance is separate.");
        pacing=new PacingReport{requestedFastLoop=fast,headless=headless,physicsStep=Time.fixedDeltaTime};
        BeginCosts();
        // Editor-assembly runtime callbacks are not a reliable post-reset policy hook.
        // Apply after the scene's runtime static initialization has completed.
        ACunicombat.Simulation.Driver.CombatTelemetrySession.EventFocusedSnapshots=captureMode!="full";
        previousRenderTime=Time.timeAsDouble;previousUnscaledTime=Time.unscaledTimeAsDouble;previousRenderFrame=Time.frameCount;
        if(fast)Application.targetFrameRate=-1;
    }
    static void RequestNextLoop()
    {
        if(pacing?.requestedFastLoop==true)EditorApplication.QueuePlayerLoopUpdate();
    }
    static void AuditPacing()
    {
        if(pacing==null)return;
        if(ACunicombat.Simulation.Driver.CombatTelemetrySession.EventFocusedSnapshots!=(captureMode!="full"))
            throw new InvalidOperationException("Telemetry policy changed during diagnostic.");
        int frames=Time.frameCount-previousRenderFrame;
        if(frames<=0)return;
        pacing.sampledFrames++;
        pacing.missedFrames+=Math.Max(0,frames-1);
        double expected=frames*(double)Time.captureDeltaTime;
        pacing.maximumRenderStepError=Math.Max(pacing.maximumRenderStepError,Math.Abs(Time.timeAsDouble-previousRenderTime-expected));
        pacing.maximumUnscaledStepError=Math.Max(pacing.maximumUnscaledStepError,Math.Abs(Time.unscaledTimeAsDouble-previousUnscaledTime-expected));
        previousRenderTime=Time.timeAsDouble;previousUnscaledTime=Time.unscaledTimeAsDouble;previousRenderFrame=Time.frameCount;
        if(Time.timeScale!=1||Math.Abs(Time.fixedDeltaTime-pacing.physicsStep)>1e-7)
            throw new InvalidOperationException("Simulation pacing changed during diagnostic; not valid accelerated evidence.");
    }
    static void SavePacing(string directory)
    {
        if(pacing==null)return;
        pacing.simulatedSeconds=radar!=null?radar.SimTimeSeconds-started:0;
        pacing.wallSeconds=EditorApplication.timeSinceStartup-wallStarted;
        pacing.speedup=pacing.wallSeconds>0?pacing.simulatedSeconds/pacing.wallSeconds:0;
        File.WriteAllText(directory+"/pacing.json",JsonUtility.ToJson(pacing,true));
    }
}
