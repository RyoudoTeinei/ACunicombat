using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.Rendering;

public sealed class MechPlatformBaselineTests
{
    [UnityTest] public IEnumerator PlayableSceneStartsMovesAndRendersWithoutManualInput()
    {
        if(Environment.GetEnvironmentVariable("ACUNICOMBAT_PLATFORM_BASELINE")!="1")
            Assert.Ignore("Full scene baseline is enabled by -Mode Baseline.");
        const string scene="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
        Assert.That(File.Exists(scene),Is.True,"Required local playable scene is missing; do not replace it with a synthetic pass.");
        Assert.That(SystemInfo.graphicsDeviceType,Is.Not.EqualTo(GraphicsDeviceType.Null));
        Time.timeScale=1f;
        EditorSceneManager.OpenScene(scene,OpenSceneMode.Single);
        yield return new EnterPlayMode();
        yield return null;
        foreach(var menu in UnityEngine.Object.FindObjectsByType<MechAssemblyMenu>())
            typeof(MechAssemblyMenu).GetMethod("SetVisible",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(menu,new object[]{false});
        var warmupDeadline=Time.realtimeSinceStartup+.25f;
        while(Time.realtimeSinceStartup<warmupDeadline)yield return null;
        var rig=UnityEngine.Object.FindObjectsByType<MechPlayerRig>().FirstOrDefault(x=>x.isActiveAndEnabled && x.GetComponent<MechAiTargetController>()==null);
        Assert.That(rig,Is.Not.Null,"Playable sample must contain a player platform.");
        var motion=rig.GetComponent<MechFlightMotionController>();
        var input=rig.GetComponent<MechPlayerInputAdapter>();
        var engine=rig.GetComponent<MechEngineController>();
        Assert.That(motion,Is.Not.Null);Assert.That(input,Is.Not.Null);
        var startupDeadline=Time.realtimeSinceStartup+30f;
        while(engine!=null && engine.StartupActive && Time.realtimeSinceStartup<startupDeadline)yield return null;
        Assert.That(motion.EngineAvailable,Is.True,"Normal startup did not complete on "+rig.name+"; remaining="+(engine!=null?engine.StartupSecondsRemaining:-1f)+"; scale="+Time.timeScale);
        CaptureConfiguration(scene);
        input.SetExternalControl(true);
        var initial=rig.transform.position;
        var command=new MechPlayerInputFrame {MoveLocal=Vector2.up};
        input.SubmitExternalFrame(in command);
        var movementUntil=Time.time+1.2f;var movementDeadline=Time.realtimeSinceStartup+30f;var lastRecordedTick=Time.fixedTime;
        while(Time.time<movementUntil && Time.realtimeSinceStartup<movementDeadline)
        {
            yield return null;
            if(Time.fixedTime>lastRecordedTick){RecordPlatformObservations();lastRecordedTick=Time.fixedTime;}
        }
        Assert.That(Time.time,Is.GreaterThanOrEqualTo(movementUntil),"Gameplay clock stalled during movement.");
        Assert.That(motion.CurrentControlForceNewtons.sqrMagnitude,Is.GreaterThan(1f),"Command must drive real thrusters, not merely coast.");
        input.SubmitExternalFrame(default);
        var distance=Vector3.Distance(initial,rig.transform.position);
        Assert.That(distance,Is.GreaterThan(.05f),"Normal input did not produce movement.");
        // A declared, controlled weapon phase: keep the original loadout, move the opponent into a useful test envelope.
        foreach(var opponent in UnityEngine.Object.FindObjectsByType<MechAiTargetController>())
            opponent.transform.position=rig.transform.position+motion.LogicalRotation*Vector3.forward*8000f;
        var weapons=rig.GetComponent<MechTestWeaponSystem>();Assert.That(weapons,Is.Not.Null);
        var gunSlot=MechPartSlot.LeftHandWeapon;
        Assert.That(weapons.TryGetWeaponSlotStatus(gunSlot,out var gunBefore),Is.True);
        var gunCommand=new MechPlayerInputFrame {PrimaryFireHeld=true,PrimaryFireEdge=true};
        input.SubmitExternalFrame(in gunCommand);yield return null;gunCommand.PrimaryFireEdge=false;input.SubmitExternalFrame(in gunCommand);
        var fireUntil=Time.time+3f;var fireDeadline=Time.realtimeSinceStartup+30f;
        while(Time.time<fireUntil && Time.realtimeSinceStartup<fireDeadline){yield return null;RecordPlatformObservations();}
        input.SubmitExternalFrame(default);weapons.TryGetWeaponSlotStatus(gunSlot,out var gunAfter);
        var gunFired=gunAfter.RoundsRemaining<gunBefore.RoundsRemaining;
        Assert.That(gunFired,Is.True,"Normal hand-weapon input did not consume a round.");
        var playerRadar=rig.GetComponent<MechRadarController>();playerRadar.SetRadarPowerEnabled(true);playerRadar.SetTrackOperationMode(true);
        var known=new TrackSnapshot[128];var missileFired=false;var missileReason="NoCompatibleTrack";
        var missileDeadline=Time.realtimeSinceStartup+15f;
        var actionEvents=new System.Collections.Generic.List<MechActionFeedback>();
        while(Time.realtimeSinceStartup<missileDeadline && !missileFired)
        {
            var knownCount=playerRadar.CopyTracks(known);
            if(weapons.TryGetWeaponSlot(MechWeaponRole.AirToAirMissile,out var slot))
                for(var i=0;i<knownCount;i++)
                    if(known[i].ObjectClass==TrackObjectClass.AirVehicle && known[i].Lifecycle==TrackLifecycle.Confirmed &&
                       playerRadar.TryAssignFireControlTrack(known[i].TrackId))
                    {weapons.CommandFireMissile(slot);break;}
            weapons.Actions.CopyEvents(actionEvents);
            foreach(var feedback in actionEvents)
                if(feedback.Kind==MechActionKind.Fire && feedback.Phase==MechActionPhase.Succeeded &&
                   rig.GetComponent<MechRuntimeState>().AssembledParts.Any(r=>r.InstanceId==feedback.InstanceId && r.part.WeaponPerformance.IsGuidedMissile))
                    {missileFired=true;missileReason="Committed";}
            if(!missileFired)yield return null;
        }
        var observationUntil=Time.time+1.2f;var observationDeadline=Time.realtimeSinceStartup+15f;
        while(Time.time<observationUntil && Time.realtimeSinceStartup<observationDeadline){yield return null;RecordPlatformObservations();}
        var warningSamples=new ThreatSnapshot[32];var warningCount=playerRadar.CopyThreats(warningSamples);
        var playerCamera=UnityEngine.Object.FindObjectsByType<Camera>().FirstOrDefault(c=>c.GetComponent<MechThirdPersonCamera>()!=null);
        Assert.That(playerCamera,Is.Not.Null);
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
        Assert.That(File.Exists(Path.Combine(output,"platform-observers.jsonl")),Is.True,"Playable sample must provide actual AI observation records.");
        var rt=new RenderTexture(1280,720,24);var pixels=new Texture2D(1280,720,TextureFormat.RGB24,false);
        var oldTarget=playerCamera.targetTexture;var oldActive=RenderTexture.active;
        try
        {
            playerCamera.targetTexture=rt;playerCamera.Render();RenderTexture.active=rt;
            pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();
            File.WriteAllBytes(Path.Combine(output,"playable-scene.png"),pixels.EncodeToPNG());
            var thermal=UnityEngine.Object.FindAnyObjectByType<MechThermalVisionController>();
            Assert.That(thermal,Is.Not.Null);
            var colors=pixels.GetPixels32();var visible=colors.Count(c=>c.r>20||c.g>20||c.b>20);
            Assert.That(visible,Is.GreaterThan(colors.Length/100),"Playable scene render is effectively empty.");
            try
            {
                thermal.SetMode(MechThermalVisionMode.WhiteHot);playerCamera.Render();
                RenderTexture.active=rt;pixels.ReadPixels(new Rect(0,0,1280,720),0,0);pixels.Apply();
                File.WriteAllBytes(Path.Combine(output,"playable-thermal.png"),pixels.EncodeToPNG());
                var thermalColors=pixels.GetPixels32();var changed=0;
                for(var i=0;i<colors.Length;i++)if(Math.Abs(colors[i].r-thermalColors[i].r)>10 || Math.Abs(colors[i].g-thermalColors[i].g)>10)changed++;
                Assert.That(changed,Is.GreaterThan(colors.Length/100),"Thermal mode did not change the actual render.");
            }
            finally{thermal.SetMode(MechThermalVisionMode.Off);}
            File.WriteAllText(Path.Combine(output,"platform-baseline.json"),
                JsonUtility.ToJson(new Baseline {scene=scene,movedMeters=distance,platforms=UnityEngine.Object.FindObjectsByType<MechRadarController>().Length,
                    graphicsDevice=SystemInfo.graphicsDeviceType.ToString(),gunFired=gunFired,missileFired=missileFired,missileReason=missileReason,warningCount=warningCount,
                    engineAvailable=motion.EngineAvailable,bodyPlayback=rig.GetComponent<MechBodyActionPlayer>()!=null && rig.GetComponent<MechBodyActionPlayer>().IsGraphValid},true));
        }
        finally
        {
            playerCamera.targetTexture=oldTarget;RenderTexture.active=oldActive;rt.Release();
            UnityEngine.Object.Destroy(rt);UnityEngine.Object.Destroy(pixels);
        }
        // A separate controlled damage stage follows the unchanged startup/fire/render checks.
        // Stop new hostile shots so the three grading samples are not mixed with unrelated incoming hits.
        foreach(var other in UnityEngine.Object.FindObjectsByType<MechTestWeaponSystem>())
            if(other!=weapons){other.Actions.CancelAll(Time.timeAsDouble);other.enabled=false;}
        var combat=rig.GetComponent<CombatTestTarget>();var damage=rig.GetComponent<MechPartDamageController>();var damageHud=rig.GetComponent<MechDamageDiagramHud>();
        Assert.That(damage,Is.Not.Null,"Playable mech must have functional damage regions");
        Assert.That(damageHud,Is.Not.Null,"Player must have the damage diagram HUD");
        combat.ConfigureProtection(combat.MaxArmor,combat.MaxStructure);yield return null;
        Assert.That(damageHud.DiagramTexture,Is.Not.Null,"Runtime outline cache was not generated");
        File.WriteAllBytes(Path.Combine(output,"damage-hud-normal.png"),damageHud.DiagramTexture.EncodeToPNG());
        var damagedRecord=rig.GetComponent<MechRuntimeState>().AssembledParts.First(r=>r.slot==MechPartSlot.RightBackWeapon);
        MechDamageRegion region=null;
        foreach(var candidate in damage.HitRegions)
            if(candidate.SlotId==damagedRecord.SlotId){region=candidate;break;}
        Assert.That(region,Is.Not.Null);var regionCollider=region.GetComponent<Collider>();
        var tolerance=damagedRecord.part.DamageProfile.ResolveTolerance(damagedRecord.part,combat.MaxHealth);
        var expected=new[]{MechPartCondition.FunctionImpaired,MechPartCondition.SevereButUsable,MechPartCondition.Destroyed};
        var imageNames=new[]{"damage-hud-impaired.png","damage-hud-severe.png","damage-hud-destroyed.png"};
        var damageSamples=new System.Collections.Generic.List<DamageSample>();
        for(int stage=0;stage<3;stage++)
        {
            float beforeAP=combat.Health;
            Assert.That(combat.TryApplyDamage(tolerance*.3f,regionCollider.bounds.center,Vector3.forward,TestWeaponKind.Rifle,out _,null,regionCollider,Guid.NewGuid()),Is.True);
            yield return null;
            Assert.That(damage.GetCondition(region.RegionId),Is.EqualTo(expected[stage]));
            Assert.That(combat.Health,Is.LessThan(beforeAP));Assert.That(combat.IsAlive,Is.True,"Part failure must not kill the complete mech");
            Assert.That(damagedRecord.instance.activeSelf,Is.True,"Destroyed equipment must retain its model/diagram identity");
            File.WriteAllBytes(Path.Combine(output,imageNames[stage]),damageHud.DiagramTexture.EncodeToPNG());
            damageSamples.Add(new DamageSample{state=damage.GetCondition(region.RegionId).ToString(),region=region.RegionId,totalAP=combat.Health,maximumAP=combat.MaxHealth,efficiency=damage.GetEquipmentEfficiency(region.SlotId)});
            RecordPlatformObservations();
        }
        Assert.That(weapons.GetEquipmentEfficiency(new MechEquipmentSlot(damagedRecord.SlotId)),Is.Zero);
        File.WriteAllText(Path.Combine(output,"part-damage-demo.json"),JsonUtility.ToJson(new DamageSamples{samples=damageSamples.ToArray()},true));
        yield return new ExitPlayMode();
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
    }
    private static void CaptureConfiguration(string scene)
    {
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
        var snapshot=new ConfigurationSnapshot {scene=scene,sceneSha256=HashFile(scene),
            commandSequence="startup; forward 1.2s; opponent at 8km; primary held 3s; sensor-track missile request; observe 1.2s; normal/thermal capture",
            platforms=UnityEngine.Object.FindObjectsByType<MechRuntimeState>().Where(s=>s.GetComponent<MechPlayerRig>()!=null)
                .Select(state=>new PlatformConfiguration {name=state.name,loadout=state.CurrentLoadout!=null?state.CurrentLoadout.LoadoutId:"",
                    parts=state.CurrentLoadout!=null?state.CurrentLoadout.EnumerateEnabledAssignments().Where(a=>a.part!=null)
                        .Select(a=>new PartConfiguration {slot=a.slot.ToString(),partId=a.part.PartId,
                            assetPath=UnityEditor.AssetDatabase.GetAssetPath(a.part),
                            assetSha256=HashFile(UnityEditor.AssetDatabase.GetAssetPath(a.part)),
                            loadedDefinition=JsonUtility.ToJson(a.part)}).ToArray():Array.Empty<PartConfiguration>()}).ToArray()};
        File.WriteAllText(Path.Combine(output,"platform-configuration.json"),JsonUtility.ToJson(snapshot,true));
    }
    private static string HashFile(string path)
    {
        if(string.IsNullOrEmpty(path)||!File.Exists(path))return "runtime-only";
        using(var stream=File.OpenRead(path))using(var hash=System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","");
    }
    [Serializable] private class DamageSample {public string state,region;public float totalAP,maximumAP,efficiency;}
    [Serializable] private class DamageSamples {public DamageSample[] samples;}
    [Serializable] private class PartConfiguration {public string slot,partId,assetPath,assetSha256,loadedDefinition;}
    [Serializable] private class PlatformConfiguration {public string name,loadout;public PartConfiguration[] parts;}
    [Serializable] private class ConfigurationSnapshot {public string scene,sceneSha256,commandSequence;public PlatformConfiguration[] platforms;}
    private static readonly System.Collections.Generic.HashSet<string> recordedActions=new System.Collections.Generic.HashSet<string>();
    [Serializable] private class ActionRow { public string actor,source,slotId,instanceId,phase,kind,reason;public long sequence;public double time; }
    private static void RecordActionFeedback(string output)
    {
        var events=new System.Collections.Generic.List<MechActionFeedback>();
        foreach(var weapons in UnityEngine.Object.FindObjectsByType<MechTestWeaponSystem>())
        {
            for(int channel=0;channel<2;channel++)
            {
                if(channel==0)weapons.Actions.CopyEvents(events);else weapons.CopySupportEvents(events);
                foreach(var e in events)
                {
                    var key=weapons.GetEntityId()+":"+channel+":"+e.Id.Source+":"+e.Id.Sequence+":"+e.Phase;
                    if(!recordedActions.Add(key))continue;
                    var row=new ActionRow {actor=weapons.name,source=e.Id.Source.ToString(),slotId=e.SlotId,instanceId=e.InstanceId.ToString(),
                        sequence=e.Id.Sequence,phase=e.Phase.ToString(),kind=e.Kind.ToString(),reason=e.Reason,time=e.Time};
                    File.AppendAllText(Path.Combine(output,channel==0?"platform-actions.jsonl":"platform-support.jsonl"),JsonUtility.ToJson(row)+"\n");
                }
            }
        }
    }
    private static int lastRecordedFrame=-1;
    private static void RecordPlatformObservations()
    {
        if(lastRecordedFrame==Time.frameCount)return;lastRecordedFrame=Time.frameCount;
        var output=Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
        RecordActionFeedback(output);
        foreach(var ai in UnityEngine.Object.FindObjectsByType<MechAiTargetController>())
        {
            // Read the snapshot actually retained by the decision component, not a reconstructed oracle target.
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var type=typeof(MechAiTargetController);
            var hasTrack=(bool)type.GetField("hasLastAirTrack",flags).GetValue(ai);
            var track=(TrackSnapshot)type.GetField("lastAirTrack",flags).GetValue(ai);
            var radar=ai.GetComponent<MechRadarController>();
            var row=new ObserverRow {observer=ai.name,captureTime=Time.time,simulationTime=radar!=null?radar.SimTimeSeconds:0,
                state=ai.CurrentCombatState.ToString(),hasSelectedTrack=hasTrack,
                trackId=hasTrack?track.TrackId.Packed:0,confidence=hasTrack?track.Confidence:0,
                evidenceTime=hasTrack?track.LastUpdateTime:-1,predictTime=hasTrack?track.LastPredictTime:-1,
                estimatedPosition=hasTrack?new double[]{track.Position.x,track.Position.y,track.Position.z}:null,
                estimatedVelocity=hasTrack?new double[]{track.Velocity.x,track.Velocity.y,track.Velocity.z}:null,
                affiliation=hasTrack?track.Affiliation.ToString():"Unknown"};
            var air=new TrackSnapshot[128];var missiles=new MissileTrackSnapshot[128];var warnings=new ThreatSnapshot[32];
            var airCount=ai.CopyConsumedAirInputs(air);var missileCount=ai.CopyConsumedMissileInputs(missiles);var warningCount=ai.CopyConsumedThreatInputs(warnings);
            row.airInputsTime=ai.LastAirInputsTime;row.missileInputsTime=ai.LastMissileInputsTime;row.warningInputsTime=ai.LastThreatInputsTime;
            row.airInputs=air.Take(airCount).Select(t=>new InputRow {id=t.TrackId.Packed,confidence=t.Confidence,
                timestamp=t.LastUpdateTime,kind=t.ObjectClass.ToString(),position=new[]{t.Position.x,t.Position.y,t.Position.z},
                velocity=new[]{t.Velocity.x,t.Velocity.y,t.Velocity.z},quality=t.Lifecycle.ToString(),sourceMask=t.SourceMask.ToString(),uncertainty=new[]{System.Math.Sqrt(System.Math.Max(0,t.GetCovariance(0,0)+t.GetCovariance(1,1)+t.GetCovariance(2,2)))}}).ToArray();
            row.missileInputs=missiles.Take(missileCount).Select(t=>new InputRow {id=t.TrackId.Packed,confidence=t.Confidence,
                timestamp=t.LastEvidenceTime,kind=t.TargetIntent.ToString(),quality=t.Quality.ToString(),sourceMask=t.SourceMask.ToString(),
                position=t.IsWeaponsQuality?new[]{t.Position.x,t.Position.y,t.Position.z}:Array.Empty<double>(),
                velocity=t.IsWeaponsQuality?new[]{t.Velocity.x,t.Velocity.y,t.Velocity.z}:Array.Empty<double>(),
                uncertainty=float.IsFinite(t.PositionUncertaintyMeters)?new[]{(double)t.PositionUncertaintyMeters}:Array.Empty<double>()}).ToArray();
            row.warningInputs=warnings.Take(warningCount).Select(t=>new WarningInputRow {id=t.ThreatId.Packed,confidence=t.IdentificationConfidence,
                timestamp=t.LastSeenTime,kind=t.Level.ToString(),direction=new double[]{t.DirectionWorld.x,t.DirectionWorld.y,t.DirectionWorld.z}}).ToArray();
            File.AppendAllText(Path.Combine(output,"platform-observers.jsonl"),JsonUtility.ToJson(row)+"\n");
        }
        foreach(var body in UnityEngine.Object.FindObjectsByType<CombatTestTarget>())
        {
            var position=body.AimPoint.position;var motion=body.GetComponent<MechFlightMotionController>();var velocity=motion!=null?motion.Velocity:Vector3.zero;
            var truth=new TruthRow {entity=body.name,time=Time.time,team=body.TeamId,alive=body.IsAlive,
                position=new float[]{position.x,position.y,position.z},velocity=new float[]{velocity.x,velocity.y,velocity.z}};
            File.AppendAllText(Path.Combine(output,"platform-truth.jsonl"),JsonUtility.ToJson(truth)+"\n");
        }
    }
    [Serializable] private class ObserverRow
    {
        public string observer,state,affiliation;public double captureTime,simulationTime,evidenceTime,predictTime;
        public double airInputsTime,missileInputsTime,warningInputsTime;public InputRow[] airInputs,missileInputs;public WarningInputRow[] warningInputs;
        public bool hasSelectedTrack;public uint trackId;public float confidence;public double[] estimatedPosition,estimatedVelocity;
    }
    [Serializable] private class InputRow {public uint id;public string kind,quality,sourceMask;public float confidence;public double timestamp;public double[] uncertainty,position,velocity,direction;}
    [Serializable] private class WarningInputRow {public uint id;public string kind;public float confidence;public double timestamp;public double[] direction;}
    [Serializable] private class TruthRow {public string entity;public float time;public int team;public bool alive;public float[] position,velocity;}
    [Serializable] private class Baseline {public string scene;public float movedMeters;public int platforms;public bool engineAvailable,bodyPlayback,gunFired,missileFired;public string missileReason,graphicsDevice;public int warningCount;}
}