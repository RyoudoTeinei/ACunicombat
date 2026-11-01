using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.FireControl;
using ACunicombat.Tracking;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Runs the shipped playable scene. No injected tracks, radar dwells, AI target orders or projectiles.
[InitializeOnLoad]
public static class MechPlayableCombatContinuityReview
{
    const string Key="PlayableCombatContinuity.stage";
    const string Scene="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity";
    static StreamWriter writer,contactWriter,radarInputWriter;static string radarCaptureDirectory;static bool radarCaptureSubscribed;static MechPlayerRig player;static MechRadarController radar;static MechRadarHudPresenter hud;
    static EditorWindow gameView;static Camera camera;static double started,wallStarted,nextImage,nextDesignation;static bool detailedConfigured;static int lastFrame=-1,markerCount,frameCount,firePhase=-1,qbPhase=-1;
    static Vector3 patrolAnchor;
    static readonly Dictionary<string,(string owner,int team)> launchOrigins=new Dictionary<string,(string,int)>();
    static readonly HashSet<string> launched=new HashSet<string>();static readonly HashSet<string> launchers=new HashSet<string>();
    static readonly TrackSnapshot[] raw=new TrackSnapshot[96];static readonly MissileTrackSnapshot[] fused=new MissileTrackSnapshot[MissileContactManager.MaximumContacts];
    static readonly FieldInfo ownerField=typeof(MechTestProjectile).GetField("owner",BindingFlags.Instance|BindingFlags.NonPublic);
    static double nextWorldAudit,nextCombatDiagnostic;
    static readonly List<ACunicombat.Simulation.Driver.BattlefieldEntityIdentity> auditOwners=new();
    static readonly List<MechActionFeedback> actionEvents=new();
    static MechPlayableCombatContinuityReview()
    {
        EditorApplication.update+=Tick;AssemblyReloadEvents.beforeAssemblyReload+=CloseRadarCapture;
        if(SessionState.GetString(Key,"")=="play")StartRadarCapture(SessionState.GetString("PlayableCombatContinuity.output",""));
    }
    static void StartRadarCapture(string directory)
    {
        if(radarCaptureSubscribed||string.IsNullOrEmpty(directory))return;
        radarCaptureDirectory=directory+"/radar-input";Directory.CreateDirectory(radarCaptureDirectory);
        RadarReplayJournal.Produced+=RadarOperation;radarCaptureSubscribed=true;
    }
    static void RadarOperation(RadarReplayOperation operation)
    {
        if(radarInputWriter==null)
        {
            int chunk=SessionState.GetInt("PlayableCombatContinuity.radarChunk",0);
            SessionState.SetInt("PlayableCombatContinuity.radarChunk",chunk+1);
            radarInputWriter=new StreamWriter(new GZipStream(File.Create(radarCaptureDirectory+"/"+chunk.ToString("D5")+".jsonl.gz"),System.IO.Compression.CompressionLevel.Fastest),new System.Text.UTF8Encoding(false),65536);
        }
        radarInputWriter.WriteLine(JsonUtility.ToJson(operation));
    }
    static void CloseRadarCapture(){RadarReplayJournal.Produced-=RadarOperation;radarCaptureSubscribed=false;radarInputWriter?.Dispose();radarInputWriter=null;}
    static string Arg(string name,string fallback){var args=Environment.GetCommandLineArgs();int i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:fallback;}
    public static void Run()
    {
        var dir=Arg("-combatReviewOutput",Path.GetFullPath("Artifacts/PlayableCombat-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")));Directory.CreateDirectory(dir);Directory.CreateDirectory(dir+"/frames");
        MechIdentityController.LogDirectoryOverride=dir+"/identity";
        SessionState.SetString("PlayableCombatContinuity.output",dir);SessionState.SetFloat("PlayableCombatContinuity.duration",float.Parse(Arg("-combatReviewDuration","120"),System.Globalization.CultureInfo.InvariantCulture));
        SessionState.SetInt("PlayableCombatContinuity.fps",int.Parse(Arg("-combatReviewFps","60")));
        File.WriteAllText(dir+"/scope.txt","Production playable scene; normal AI, radar scheduling, weapons and identity. Only player input is scripted. Truth is written to a separate record stream for offline adjudication. No scene save or rebuild.\n"+Scene);
        CaptureBuildManifest(dir);
        EditorSceneManager.OpenScene(Scene,OpenSceneMode.Single);if(Arg("-fleetDefenseReview","0")=="1")MechFleetCombatReview.PrepareDefenseSpawn();ConfigureView();UnityEngine.Random.InitState(20261018);
        SessionState.SetInt("PlayableCombatContinuity.radarChunk",0);StartRadarCapture(dir);SessionState.SetString(Key,"play");EditorApplication.isPlaying=true;
    }
    [Serializable] sealed class ManifestEntry {public string path,hash;}
    [Serializable] sealed class Manifest {public ManifestEntry[] files;}
    static string Hash(string path){using(var stream=File.OpenRead(path))using(var sha=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");}
    static void CaptureBuildManifest(string dir)
    {
        var files=Directory.GetFiles("Library/ScriptAssemblies","*.dll").Where(p=>Path.GetFileName(p).StartsWith("ACunicombat.")||Path.GetFileName(p).StartsWith("Assembly-CSharp")).Append(Scene);
        File.WriteAllText(dir+"/build-manifest.json",JsonUtility.ToJson(new Manifest{files=files.Select(p=>new ManifestEntry{path=p,hash=Hash(p)}).ToArray()},true));
    }
    static bool VerifyBuildManifest(string dir)
    {
        var manifest=JsonUtility.FromJson<Manifest>(File.ReadAllText(dir+"/build-manifest.json"));var changed=manifest.files.Where(p=>!File.Exists(p.path)||Hash(p.path)!=p.hash).Select(p=>p.path).ToArray();
        File.WriteAllText(dir+"/build-consistency.txt",changed.Length==0?"PASS: compiled assemblies and saved playable scene unchanged during run.":"FAIL: "+string.Join(",",changed));return changed.Length==0;
    }
    static void ConfigureView()
    {
        var asm=typeof(Editor).Assembly;var type=asm.GetType("UnityEditor.GameViewSizes");var sizes=typeof(ScriptableSingleton<>).MakeGenericType(type).GetProperty("instance",BindingFlags.Public|BindingFlags.Static).GetValue(null);
        var group=type.GetMethod("GetGroup").Invoke(sizes,new[]{Enum.Parse(asm.GetType("UnityEditor.GameViewSizeGroupType"),"Standalone")});
        var size=Activator.CreateInstance(asm.GetType("UnityEditor.GameViewSize"),BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{Enum.Parse(asm.GetType("UnityEditor.GameViewSizeType"),"FixedResolution"),int.Parse(Arg("-combatReviewWidth","2560")),int.Parse(Arg("-combatReviewHeight","1440")),"Combat continuity capture"},null);
        group.GetType().GetMethod("AddCustomSize").Invoke(group,new[]{size});int index=(int)group.GetType().GetMethod("GetTotalCount").Invoke(group,null)-1;
        var view=EditorWindow.GetWindow(asm.GetType("UnityEditor.GameView"));gameView=view;view.Focus();view.GetType().GetProperty("selectedSizeIndex",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).SetValue(view,index);
    }
    [Serializable] sealed class Sample
    {
        public string kind,source,style,owner,quality,intent,affiliation,launchIdentity;public int frame,team; public string entity;public uint track;public double time,evidenceTime,kinematicTime,filterTime,filterUncertainty;
        public Vector3 position,renderPosition,velocity,acceleration,cameraPosition,cameraForward;public Vector2 screen,input;public float uncertainty,velocityUncertainty,radialVelocityUncertainty,cpa,tca;public bool alive,incoming,ownship;
    }
    static Vector2 HudPoint(Vector3 world)=> (Vector2)typeof(MechRadarHudPresenter).Assembly.GetType("ACunicombat.Mechs.MechHudLayout").GetMethod("ScreenToHudPoint",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{camera.WorldToScreenPoint(world)});
    static void ContactFrame(MissileContactReplayFrame frame){contactWriter.WriteLine(JsonUtility.ToJson(frame));}
    static void Write(Sample data){writer.WriteLine(JsonUtility.ToJson(data));}
    static void AuditCombatDecisions(double now)
    {
        if(now<nextCombatDiagnostic)return;nextCombatDiagnostic=now+1;
        foreach(var rig in UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude))
        {
            var ai=rig.GetComponent<MechAiTargetController>();
            if(ai!=null)
            {
                Write(new Sample{kind="ai_status",frame=Time.frameCount,time=now,owner=rig.name,quality=ai.LastWeaponDecision,intent=ai.LastManeuverDecision,entity=ai.SelectedAirTrackId.ToString(),alive=ai.HasSelectedAirTrack});
                var consumed=new TrackSnapshot[96];int consumedCount=ai.CopyConsumedAirInputs(consumed);
                for(int i=0;i<consumedCount;i++){var track=consumed[i];Write(new Sample{kind="ai_air",frame=Time.frameCount,time=now,owner=rig.name,entity=track.TrackId.ToString(),track=track.TrackId.Packed,affiliation=track.Affiliation.ToString(),quality=track.Lifecycle.ToString(),intent=track.Identity.Relation.ToString(),source=track.Identity.Reason.ToString(),uncertainty=track.IdentityConfidence,cpa=track.Confidence,alive=track.Identity.Available,incoming=track.Identity.CanAttackPlatform});}
            }
            var weapons=rig.GetComponent<MechTestWeaponSystem>();if(weapons==null)continue;
            weapons.Actions.CopyEvents(actionEvents);
            foreach(var e in actionEvents)if(now-e.Time<=1.1)Write(new Sample{kind="weapon_feedback",frame=Time.frameCount,time=e.Time,owner=rig.name,source=e.Id.Source.ToString(),quality=e.Phase.ToString(),intent=e.Kind.ToString(),style=e.Reason,entity=e.InstanceId.ToString(),affiliation=e.SlotId});
        }
    }
    // Offline oracle only: verify the sensor world's projection against the live scene.
    // These records never enter tracking, guidance, identity, or the acceptance thresholds.
    static void AuditSensorWorld(ACunicombat.Simulation.Driver.SimulationDriver driver,double now)
    {
        if(driver==null || now<nextWorldAudit)return;nextWorldAudit=now+.2;
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
        var world=typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("world",flags).GetValue(driver) as ACunicombat.Simulation.World.SimWorld;
        var bindings=typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("sceneEntities",flags).GetValue(driver);
        if(world==null || bindings==null)return;
        ACunicombat.Simulation.Driver.BattlefieldEntityIdentity.CopyActive(auditOwners);
        var owners=new Dictionary<string,ACunicombat.Simulation.Driver.BattlefieldEntityIdentity>();
        foreach(var owner in auditOwners)if(owner!=null&&owner.isActiveAndEnabled)owners[owner.Identity.ToString()]=owner;
        var entries=(System.Collections.IDictionary)bindings.GetType().GetField("entries",flags).GetValue(bindings);
        var samples=(double[])typeof(ACunicombat.Simulation.World.SimWorld).GetField("sampledAt",flags).GetValue(world);
        foreach(var entry in entries.Values)
        {
            var type=entry.GetType();var id=(ACunicombat.Simulation.World.SimEntityId)type.GetField("Entity",flags).GetValue(entry);
            string identity=type.GetField("Identity",flags).GetValue(entry).ToString();
            if(!world.TryGetReadState(id,out var state))continue;
            owners.TryGetValue(identity,out var owner);
            Write(new Sample{kind="world_audit",frame=Time.frameCount,time=world.SnapshotTime,entity=id.Packed.ToString(),source=identity,
                owner=owner!=null?owner.name:"retired",alive=owner!=null,position=(Vector3)(float3)state.PositionWorld,
                renderPosition=owner!=null?owner.transform.position:Vector3.zero,velocity=(Vector3)(float3)state.VelocityWorld,kinematicTime=samples[id.Value.Index]});
        }
    }
    [Serializable] sealed class CameraSample { public string kind="camera";public int frame,width,height;public double time;public float scale,verticalProjection;public float[] clip; }
    static void Repaint(int frame)
    {
        Write(new Sample{kind="hud_frame",frame=frame,time=radar.SimTimeSeconds});
        // Sample the visual oracle in the same repaint as the actual HUD, after interpolation.
        foreach(var p in UnityEngine.Object.FindObjectsByType<MechTestProjectile>(FindObjectsInactive.Exclude))
            if(p.IsEngagementActive)Write(new Sample{kind="visual_truth",frame=frame,time=radar.SimTimeSeconds,entity=p.GetEntityId().ToString(),renderPosition=p.PresentationPosition});
        var matrix=camera.projectionMatrix*camera.worldToCameraMatrix;var values=new float[16];
        for(int row=0;row<4;row++)for(int col=0;col<4;col++)values[row*4+col]=matrix[row,col];
        writer.WriteLine(JsonUtility.ToJson(new CameraSample{frame=frame,time=radar.SimTimeSeconds,width=Screen.width,height=Screen.height,verticalProjection=camera.projectionMatrix.m11,scale=Mathf.Clamp(Mathf.Min(Screen.width/1920f,Screen.height/1080f),.4f,1),clip=values}));
    }
    static void Marker(MechMissileMarkerObservation m)
    {
        markerCount++;Write(new Sample{kind="hud",frame=m.frame,time=m.time,entity=m.localProjectileId,track=m.trackId,source=m.source,style=m.style,position=m.worldPosition,screen=m.hudPosition});
    }
    static void Tick()
    {
        string stage=SessionState.GetString(Key,"");if(stage=="finish"&&!EditorApplication.isPlaying&&!EditorApplication.isPlayingOrWillChangePlaymode){SessionState.EraseString(Key);EditorApplication.Exit(SessionState.GetInt("PlayableCombatContinuity.exit",0));return;}
        if(stage!="play"||!EditorApplication.isPlaying||EditorApplication.isCompiling)return;
        string dir=SessionState.GetString("PlayableCombatContinuity.output","");
        try
        {
            gameView?.Repaint();
            if(player==null)
            {
                player=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude).FirstOrDefault(p=>!p.AiControlled);if(player==null)return;
                radar=player.GetComponent<MechRadarController>();hud=player.GetComponent<MechRadarHudPresenter>();camera=Camera.main;
                if(radar==null||hud==null||camera==null)throw new Exception("Actual player radar/HUD/main camera missing");
                writer=new StreamWriter(dir+"/continuity.jsonl",false,new System.Text.UTF8Encoding(false),65536);contactWriter=new StreamWriter(new GZipStream(File.Create(dir+"/contacts-input.jsonl.gz"),System.IO.Compression.CompressionLevel.Fastest),new System.Text.UTF8Encoding(false),65536);radar.MissileContactFrameEvaluated+=ContactFrame;hud.MissileMarkerDrawn+=Marker;hud.HudRepainted+=Repaint;
                Application.runInBackground=true;QualitySettings.vSyncCount=0;UnityEngine.Rendering.OnDemandRendering.renderFrameInterval=1;
                started=radar.SimTimeSeconds;wallStarted=EditorApplication.timeSinceStartup;Time.captureDeltaTime=1f/SessionState.GetInt("PlayableCombatContinuity.fps",60);
                if(Arg("-fleetDefenseReview","0")=="1")MechFleetCombatReview.ConfigureDefenseEquipment(player);
                if(Arg("-combatReviewPlayerAam","0")=="1")
                {
                    var part=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude)
                        .SelectMany(x=>x.GetComponent<MechRuntimeState>().CurrentLoadout.EnumerateEnabledAssignments()).Select(x=>x.part)
                        .First(x=>x.Slot==MechPartSlot.LeftBackWeapon&&x.WeaponPerformance.role==MechWeaponRole.AirToAirMissile);
                    var assembler=player.GetComponent<MechAssembler>();var variant=UnityEngine.Object.Instantiate(assembler.Loadout);
                    var slot=variant.GetSlots().First(x=>x.legacyRole==MechPartSlot.LeftBackWeapon);
                    if(!variant.TrySetPart(slot.id,part,out string reason))throw new Exception("AAM loadout rejected: "+reason);
                    assembler.Loadout=variant;assembler.Assemble();File.AppendAllText(dir+"/scope.txt","Player loadout variant: left shoulder="+part.PartId+"; normal assembly, no saved preference changes.\n");
                }
                var driver=player.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();
                if(driver!=null)typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("telemetryDetail",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(driver,ACunicombat.Simulation.Driver.CombatTelemetryDetail.Detailed);
                radar.SetTrackOperationMode(true); // Same operator action as enabling the normal designation mode.

                var ais=UnityEngine.Object.FindObjectsByType<MechAiTargetController>(FindObjectsInactive.Exclude);
                foreach(var identity in UnityEngine.Object.FindObjectsByType<MechIdentityController>(FindObjectsInactive.Exclude))identity.LogDecisions=true;
                File.WriteAllLines(dir+"/platforms.txt",ais.Select(a=>$"{a.name}: enabled={a.enabled}, position={a.transform.position}, team={a.GetComponent<CombatTestTarget>()?.TeamId}"));
                if(ais.Length<4||ais.Any(a=>!a.enabled))throw new Exception("Expected four active production AI platforms");
            }
            if(File.Exists(dir+"/stop.request")){Finish(2);return;}
            if(MechAssemblyMenu.IsAnyMenuVisible){var menu=player.GetComponent<MechAssemblyMenu>();typeof(MechAssemblyMenu).GetMethod("SetVisible",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(menu,new object[]{false});File.AppendAllText(dir+"/scope.txt","Closed startup hangar through its normal SetVisible(false) transition; retained equipped loadout.\n");}
            if(lastFrame==Time.frameCount)return;lastFrame=Time.frameCount;frameCount++;
            double now=radar.SimTimeSeconds,t=now-started;
            if(t<.1&&EditorApplication.timeSinceStartup-wallStarted>20)throw new Exception("Scene simulation did not advance after startup");
            var telemetryDriver=player.GetComponent<ACunicombat.Simulation.Driver.SimulationDriver>();
            AuditSensorWorld(telemetryDriver,radar.SimTimeSeconds);
            if(!detailedConfigured&&telemetryDriver!=null){typeof(ACunicombat.Simulation.Driver.SimulationDriver).GetField("telemetryDetail",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(telemetryDriver,ACunicombat.Simulation.Driver.CombatTelemetryDetail.Detailed);detailedConfigured=true;}
            if(Arg("-fleetReview","0")=="1")MechFleetCombatReview.Step(player,t,dir);
            var motion=player.GetComponent<MechFlightMotionController>();var input=default(MechPlayerInputFrame);
            if(t<=6)patrolAnchor=player.transform.position;
            else input=MechEndurancePilot.Patrol(t-6,patrolAnchor,player.transform.position,motion.Velocity,motion.LogicalRotation);
            if(Vector3.Distance(player.transform.position,patrolAnchor)>40000)throw new Exception("Coverage failure: scripted player left the 40 km combat zone");
            int nextFire=(int)(t/.5);input.LeftShoulderFireHeld=t>10&&t%.5<.1;input.LeftShoulderFireEdge=input.LeftShoulderFireHeld&&nextFire!=firePhase;if(input.LeftShoulderFireEdge)firePhase=nextFire;
            int nextQb=(int)(t/9);input.QuickBoostEdge=t>20&&nextQb!=qbPhase;if(input.QuickBoostEdge)qbPhase=nextQb;
            int n=radar.CopyTracks(raw);var target=raw.Take(n).Where(x=>x.ObjectClass==TrackObjectClass.AirVehicle&&x.Affiliation!=TrackAffiliation.Friendly&&(!x.Identity.Available||x.Identity.CanAttackPlatform)).OrderBy(x=>math.distance(x.Position,(double3)(float3)player.transform.position)).FirstOrDefault();
            if(target.TrackId.IsValid)
            {
                if(t>6&&now>=nextDesignation){radar.SetTrackOperationMode(true);radar.TryAssignFireControlTrack(target.TrackId);nextDesignation=now+2;}
                var local=camera.transform.InverseTransformDirection((Vector3)(float3)target.PredictPosition(now)-camera.transform.position);
                var control=camera.GetComponent<MechThirdPersonCamera>();
                var center=new Vector2(.5f,.56f);
                var desired=center+new Vector2(Mathf.Clamp(Mathf.Atan2(local.x,local.z)*Mathf.Rad2Deg/60,-.3f,.3f),Mathf.Clamp(Mathf.Atan2(local.y,Mathf.Sqrt(local.x*local.x+local.z*local.z))*Mathf.Rad2Deg/60,-.25f,.25f));
                var current=control!=null?control.AimViewportPoint:center;
                input.MouseDelta=Vector2.ClampMagnitude(Vector2.Scale(desired-current,new Vector2(Screen.width,Screen.height))*.5f,20);
            }
            player.GetComponent<MechPlayerInputAdapter>().SubmitExternalFrame(input);
            Write(new Sample{kind="frame",frame=Time.frameCount,time=now,position=player.transform.position,velocity=motion.Velocity,cameraPosition=camera.transform.position,cameraForward=camera.transform.forward,input=input.MoveLocal});
            AuditCombatDecisions(now);
            for(int i=0;i<n;i++)if(raw[i].ObjectClass==TrackObjectClass.Missile)Write(new Sample{kind="raw",frame=Time.frameCount,time=now,track=raw[i].TrackId.Packed,position=(Vector3)(float3)raw[i].PredictPosition(now),velocity=(Vector3)(float3)raw[i].Velocity,quality=raw[i].Lifecycle.ToString(),affiliation=raw[i].Affiliation.ToString(),velocityUncertainty=(float)Math.Sqrt(raw[i].GetCovariance(3,3)+raw[i].GetCovariance(4,4)+raw[i].GetCovariance(5,5)),uncertainty=(float)raw[i].PredictPositionUncertainty(now),filterTime=raw[i].LastPredictTime,filterUncertainty=Math.Sqrt(raw[i].GetCovariance(0,0)+raw[i].GetCovariance(1,1)+raw[i].GetCovariance(2,2)),evidenceTime=raw[i].LastUpdateTime});
            n=radar.CopyMissileTracks(fused);for(int i=0;i<n;i++){var x=fused[i];Write(new Sample{kind="fused",frame=Time.frameCount,time=now,track=x.TrackId.Packed,position=(Vector3)(float3)x.PredictPosition(now),velocity=(Vector3)(float3)x.Velocity,acceleration=(Vector3)(float3)x.Acceleration,uncertainty=x.PositionUncertaintyMeters,velocityUncertainty=x.VelocityUncertaintyMetersPerSecond,radialVelocityUncertainty=x.RadialVelocityUncertaintyMetersPerSecond,launchIdentity=x.OwnLaunchIdentity.ToString(),evidenceTime=x.LastEvidenceTime,kinematicTime=x.KinematicUpdateTime,affiliation=x.Affiliation.ToString(),quality=x.Quality.ToString(),intent=x.TargetIntent.ToString(),incoming=x.Incoming,cpa=x.ClosestApproachDistanceMeters,tca=x.TimeToClosestApproachSeconds,source=x.SourceMask.ToString()});}
            // Oracle data is logged AFTER inputs were chosen from observed tracks. It never drives gameplay.
            foreach(var p in MechTestProjectile.Registry)
            {
                if(p==null||!p.IsMissile)continue;var owner=ownerField?.GetValue(p) as Transform;var body=owner!=null?owner.GetComponent<CombatTestTarget>():null;
                string entity=p.GetEntityId().ToString();
                if(!launchOrigins.TryGetValue(entity,out var origin))
                {
                    object cachedTeam=typeof(MechTestProjectile).GetProperty("LaunchTeam",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(p);
                    origin=(owner!=null?owner.name:"unknown",cachedTeam is int team?team:(body!=null?body.TeamId:-1));launchOrigins[entity]=origin;
                }
                if(launched.Add(entity))launchers.Add(origin.owner);
                uint reportKey=0;if(p.TryGetFriendlyReportTerminal(radar,out var reportTerminal))reportKey=new ACunicombat.Simulation.Core.Handle(2000+reportTerminal.Value.Index,reportTerminal.Value.Generation).Packed;
                Write(new Sample{kind="truth",track=reportKey,frame=Time.frameCount,time=now,entity=p.GetEntityId().ToString(),owner=origin.owner,team=origin.team,launchIdentity=p.LaunchIdentity.ToString(),position=p.transform.position,renderPosition=p.PresentationPosition,screen=HudPoint(p.PresentationPosition),velocity=p.Velocity,alive=p.IsEngagementActive,ownship=p.IsOwnedBy(player.transform)});
            }
            if(t>=nextImage){ScreenCapture.CaptureScreenshot(dir+"/frames/"+Time.frameCount.ToString("D7")+".png");nextImage=t+.1;writer.Flush();}
            if(t>=SessionState.GetFloat("PlayableCombatContinuity.duration",120)||EditorApplication.timeSinceStartup-wallStarted>900)
            {
                bool coverage=t>=110&&markerCount>100&&launched.Count>=8&&launchers.Count>=3&&launchers.Contains(player.name);
                File.WriteAllText(dir+"/coverage.txt",$"seconds={t:F2}; frames={frameCount}; hudMarkerDraws={markerCount}; missiles={launched.Count}; launchers={string.Join(",",launchers)}; coverage={coverage}; geometry/identity continuity judged separately, never inferred from coverage alone.");
                bool fleetPassed=Arg("-fleetReview","0")!="1"||MechFleetCombatReview.Finish();
                Finish(coverage&&fleetPassed?0:2);
            }
        }
        catch(Exception ex){File.WriteAllText(dir+"/error.txt",ex.ToString());Debug.LogException(ex);Finish(3);}
    }
    static void Finish(int code){string dir=SessionState.GetString("PlayableCombatContinuity.output","");if(File.Exists(dir+"/build-manifest.json")&&!VerifyBuildManifest(dir))code=4;if(hud!=null){hud.MissileMarkerDrawn-=Marker;hud.HudRepainted-=Repaint;}if(radar!=null)radar.MissileContactFrameEvaluated-=ContactFrame;contactWriter?.Dispose();contactWriter=null;CloseRadarCapture();writer?.Dispose();writer=null;MechIdentityController.LogDirectoryOverride=null;Time.captureDeltaTime=0;SessionState.SetInt("PlayableCombatContinuity.exit",code);SessionState.SetString(Key,"finish");EditorApplication.isPlaying=false;}
}
