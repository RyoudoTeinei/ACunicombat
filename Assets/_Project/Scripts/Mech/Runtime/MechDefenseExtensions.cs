using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.Core;
namespace ACunicombat.Mechs
{
    public readonly struct MechDefenseExtensionSnapshot
    {
        public readonly string SlotId,Reason;public readonly Guid InstanceId;public readonly MechDefenseExtensionKind Kind;
        public readonly int Ammunition,ActiveShots;public readonly double ReadyAt,NextShot;public readonly bool Pending,Enabled;
        public MechDefenseExtensionSnapshot(string slot,Guid instance,MechDefenseExtensionKind kind,int ammo,int shots,double ready,double next,bool pending,string reason,bool enabled)
        {SlotId=slot;InstanceId=instance;Kind=kind;Ammunition=ammo;ActiveShots=shots;ReadyAt=ready;NextShot=next;Pending=pending;Reason=reason;Enabled=enabled;}
    }
    [DisallowMultipleComponent]
    public sealed class MechDefenseExtensions : MonoBehaviour
    {
        private sealed class State
        {
            public MissileTrackSnapshot SelectedTrack;public bool HasSelectedTrack;
            public readonly Dictionary<string,(string reason,double time)> LoggedPlans=new();
            public string LoggedStatus,LoggedGate;public double NextStatus,NextGate;
            public SensorId Sensor;public MechRadarController SensorOwner;public ulong Sequence;public double NextScan;
            public readonly Dictionary<string,int> Rejections=new();public readonly Queue<string> History=new();
            public MechAssemblyRecord Record;public int Ammo;public double ReadyAt,NextShot;public bool Pending;public MechActionId Action;
            public string LastRejection="";public double LastRejectionAt,LastValidPreparationTime;public string LastPlanningSample="";public string Reason="Ready";public readonly Dictionary<string,int> Armor=new();public readonly List<MechApsProjectile> Shots=new();
            public MechDefenseExtensionProfile Profile=>Record.part.DefenseExtension;
        }
        private readonly Dictionary<Guid,State> states=new();private readonly List<Guid> retired=new();
        private readonly MissileTrackSnapshot[] tracks=new MissileTrackSnapshot[256];
        public bool AutomaticProtectionEnabled=true;
        public void Sync()
        {
            var runtime=GetComponent<MechRuntimeState>();retired.Clear();foreach(var id in states.Keys)retired.Add(id);
            if(runtime!=null)foreach(var record in runtime.AssembledParts)
            {
                if(record?.part==null||record.part.Category!=MechPartCategory.Utility||record.part.DefenseExtension.kind==MechDefenseExtensionKind.None)continue;
                retired.Remove(record.InstanceId);
                if(!states.ContainsKey(record.InstanceId))states.Add(record.InstanceId,new State{Record=record,Ammo=Mathf.Max(0,record.part.DefenseExtension.ammunition)});
            }
            foreach(var id in retired){Record(states[id],"equipment_removed","Unequipped","lifecycle");ReleaseSensor(states[id]);states.Remove(id);}
        }
        private bool Operational(State state)
        {
            var target=GetComponent<CombatTestTarget>();var damage=GetComponent<MechPartDamageController>();
            return isActiveAndEnabled&&(target==null||target.IsAlive)&&state.Record.instance!=null&&state.Record.instance.activeInHierarchy&&
                (damage==null||damage.GetEquipmentEfficiency(state.Record.SlotId)>0);
        }
        public bool Handles(MechAssemblyRecord record)=>record?.part!=null&&record.part.Category==MechPartCategory.Utility&&record.part.DefenseExtension.kind==MechDefenseExtensionKind.ActiveProtection;
        public void ResetEquipment()
        {
            Sync();foreach(var state in states.Values){state.Ammo=Mathf.Max(0,state.Profile.ammunition);state.Armor.Clear();state.Pending=false;state.ReadyAt=state.NextShot=0;state.Reason="Ready";}
        }
        public int ArmorCells(string region)
        {
            Sync();int count=0;foreach(var state in states.Values)if(state.Profile.kind==MechDefenseExtensionKind.ReactiveArmor&&Operational(state))
                count+=state.Armor.TryGetValue(region,out int cells)?cells:Mathf.Max(0,state.Profile.armor.cells);
            return count;
        }
        public float Absorb(string region,float damage,Vector3 direction,TestWeaponKind kind,out bool triggered)
        {
            Sync();triggered=false;if(string.IsNullOrEmpty(region))return damage;
            State selected=null;
            foreach(var state in states.Values)
            {
                if(state.Profile.kind!=MechDefenseExtensionKind.ReactiveArmor||!Operational(state))continue;
                var profile=state.Profile.armor;
                int cells=state.Armor.TryGetValue(region,out int remaining)?remaining:Mathf.Max(0,profile.cells);
                if(cells<=0||damage<profile.minimumTriggerDamage||!profile.Accepts(kind))continue;
                if(profile.coverageHalfAngle<180&&(direction.sqrMagnitude<.000001f||Vector3.Dot(transform.TransformDirection(profile.localFacing.normalized),-direction.normalized)<Mathf.Cos(profile.coverageHalfAngle*Mathf.Deg2Rad)))continue;
                if(selected==null||string.CompareOrdinal(state.Record.SlotId,selected.Record.SlotId)<0)selected=state;
            }
            if(selected==null)return damage;
            selected.Armor[region]=(selected.Armor.TryGetValue(region,out int left)?left:Mathf.Max(0,selected.Profile.armor.cells))-1;
            triggered=true;return damage*(1-Mathf.Clamp01(selected.Profile.armor.damageReduction));
        }
        public static bool TryPlan(in MissileTrackSnapshot track,double simTime,Vector3 origin,Vector3 inheritedVelocity,Quaternion orientation,
            MechDefenseExtensionProfile profile,float remainingReaction,out Vector3 direction,out float flightTime)
            =>TryPlan(in track,simTime,origin,inheritedVelocity,orientation,profile,remainingReaction,out direction,out flightTime,out _);
        public static bool TryPlan(in MissileTrackSnapshot track,double simTime,Vector3 origin,Vector3 inheritedVelocity,Quaternion orientation,
            MechDefenseExtensionProfile profile,float remainingReaction,out Vector3 direction,out float flightTime,out string reason,bool preparation=false)
        {
            direction=default;flightTime=0;
            reason="MissingTrack";if(!track.TrackId.IsValid)return false;
            reason="Friendly";if(track.IsFriendly)return false;
            reason="TrackQuality";if(preparation?!track.IsWarning:!track.IsWeaponsQuality)return false;
            reason="NotIncoming";if(!track.Incoming)return false;
            reason="StaleEvidence";if(simTime<track.LastEvidenceTime||simTime-track.LastEvidenceTime>profile.maximumEvidenceAgeSeconds)return false;
            var estimate=track.PredictPosition(simTime);var r=new Vector3((float)estimate.x,(float)estimate.y,(float)estimate.z)-origin;
            float range=r.magnitude;reason="InsideSafetyRange";if(range<profile.minimumRangeMeters||range<.001f)return false;
            reason="OutsideLaunchRange";if(range>(preparation?profile.detectionRangeMeters:profile.maximumRangeMeters))return false;
            reason="OutsideCoverage";if(profile.coverageHalfAngle<180&&Vector3.Dot(orientation*profile.localFacing.normalized,r/range)<Mathf.Cos(Mathf.Clamp(profile.coverageHalfAngle,0,180)*Mathf.Deg2Rad))return false;
            // Prepare as soon as a valid incoming track is detected. Release remains strictly gated.
            var predictedVelocity=track.PredictVelocity(simTime);
            var v=new Vector3((float)predictedVelocity.x,(float)predictedVelocity.y,(float)predictedVelocity.z)-inheritedVelocity;
            if(preparation)
            {
                reason="NotIncoming";if(Vector3.Dot(r,v)>=0||track.TimeToClosestApproachSeconds<=0)return false;
                reason="Prepared";return true;
            }
            r+=v*Mathf.Max(0,remainingReaction);
            reason="TooLateAfterReaction";if(r.magnitude<profile.minimumRangeMeters)return false;
            reason="PositionUncertainty";if(!float.IsFinite(track.PositionUncertaintyMeters)||track.PositionUncertaintyMeters>=profile.blastRadiusMeters)return false;
            // Solve arrival at the forward fragment volume, not a point collision followed by a guessed fuse offset.
            // Reserve radial and angular margin for the observed positional uncertainty.
            float coneSin=Mathf.Sin(Mathf.Clamp(profile.fragmentHalfAngle,1,89)*Mathf.Deg2Rad);
            float reach=profile.blastRadiusMeters-track.PositionUncertaintyMeters;
            float standOff=Mathf.Min(Mathf.Max(profile.burstLeadMeters,track.PositionUncertaintyMeters/coneSin*1.05f),reach*.9f);
            reason="PositionUncertainty";if(track.PositionUncertaintyMeters>standOff*coneSin)return false;
            double speed=Math.Max(1,profile.projectileSpeed);
            double a=Vector3.Dot(v,v)-speed*speed,b=2*(Vector3.Dot(r,v)-speed*standOff),c=Vector3.Dot(r,r)-standOff*standOff,t=double.NaN;
            bool curved=Unity.Mathematics.math.lengthsq(track.Acceleration)+Unity.Mathematics.math.lengthsq(track.Jerk)>1e-6;
            reason="NoInterceptSolution";
            if(!curved)
            {
            if(Math.Abs(a)<1e-6){if(Math.Abs(b)<1e-6)return false;t=-c/b;}
            else
            {
                double d=b*b-4*a*c;if(d<0)return false;
                double q=-.5*(b+(b>=0?1:-1)*Math.Sqrt(d));
                double t1=q/a,t2=Math.Abs(q)>1e-12?c/q:double.PositiveInfinity;
                t=t1>0&&(t1<t2||t2<=0)?t1:t2;
            }
            if(!double.IsFinite(t)||t<=0)return false;
            }
            // Guided missiles turn while the ballistic charge is in flight. Use the same
            // acceleration/jerk estimate as the fused track, not a frozen old velocity.
            if(curved)
            {
                double lo=0,hi=0;bool bracket=false;
                for(int i=1;i<=128;i++)
                {
                    hi=profile.maximumFlightSeconds*i/128.0;
                    var relative=track.PredictPosition(simTime+remainingReaction+hi)-new Unity.Mathematics.double3(origin.x,origin.y,origin.z)-new Unity.Mathematics.double3(inheritedVelocity.x,inheritedVelocity.y,inheritedVelocity.z)*(remainingReaction+hi);
                    double gap=Unity.Mathematics.math.length(relative)-speed*hi-standOff;
                    if(double.IsFinite(gap)&&gap<=0){bracket=true;break;}lo=hi;
                }
                reason="NoInterceptSolution";if(!bracket)return false;
                for(int i=0;i<20;i++)
                {
                    double mid=(lo+hi)*.5;
                    var relative=track.PredictPosition(simTime+remainingReaction+mid)-new Unity.Mathematics.double3(origin.x,origin.y,origin.z)-new Unity.Mathematics.double3(inheritedVelocity.x,inheritedVelocity.y,inheritedVelocity.z)*(remainingReaction+mid);
                    if(Unity.Mathematics.math.length(relative)>speed*mid+standOff)lo=mid;else hi=mid;
                }
                t=hi;
            }
            reason="FlightTimeLimit";if(t>profile.maximumFlightSeconds)return false;
            reason="InsufficientTime";if(!curved&&track.TimeToClosestApproachSeconds<remainingReaction+t)return false;
            var predicted=track.PredictPosition(simTime+remainingReaction+t);
            direction=(new Vector3((float)predicted.x,(float)predicted.y,(float)predicted.z)-origin-inheritedVelocity*(remainingReaction+(float)t)).normalized;flightTime=(float)t;
            reason="BurstTooClose";if(speed*flightTime<profile.minimumBurstDistanceMeters)return false;
            reason="Feasible";return true;
        }
        private ApsTelemetryData Telemetry(State state,string reason,string phase)
        {
            var motion=GetComponent<MechFlightMotionController>();var radar=GetComponent<MechRadarController>();var track=state.SelectedTrack;
            double remaining=state.Pending?Math.Max(0,state.ReadyAt-Time.timeAsDouble):0;
            return new ApsTelemetryData{StateSampleTime=Time.timeAsDouble,SlotId=state.Record.SlotId,EquipmentId=state.Record.InstanceId.ToString(),ActionId=(phase=="selection"||(phase=="prepare"&&!state.Pending))?"":state.Action.Source+":"+state.Action.Sequence,
                Reason=reason,Phase=phase,ObserverScope=radar!=null?radar.ObserverScope.ToString():"",TargetTrackId=track.TrackId.Packed,
                Ammunition=state.Ammo,ActiveShots=state.Shots.FindAll(x=>x!=null&&x.IsFlying).Count,Pending=state.Pending,
                ReactionRemaining=remaining,PreparationProgress=state.Pending?1-Math.Min(1,remaining/Math.Max(.0001,state.Profile.reactionSeconds)):0,CooldownRemaining=Math.Max(0,state.NextShot-Time.timeAsDouble),
                OwnPosition=transform.position,OwnVelocity=motion!=null?motion.Velocity:Vector3.zero,OwnAcceleration=motion!=null?motion.CurrentControlAcceleration:Vector3.zero,TranslationInput=motion!=null&&motion.TranslationInputActive,
                HasTrack=state.HasSelectedTrack,TrackTime=track.LastUpdateTime,TrackQuality=track.Quality.ToString(),Incoming=track.Incoming,Uncertainty=track.PositionUncertaintyMeters,TimeToCpa=track.TimeToClosestApproachSeconds,
                TrackPosition=new Vector3((float)track.Position.x,(float)track.Position.y,(float)track.Position.z),TrackVelocity=new Vector3((float)track.Velocity.x,(float)track.Velocity.y,(float)track.Velocity.z)};
        }
        private void Record(State state,string eventName,string reason,string phase,float flightSeconds=0,Vector3 direction=default)
        {
            if(state.Profile.kind!=MechDefenseExtensionKind.ActiveProtection)return;
            var data=Telemetry(state,reason,phase);
            data.FlightSeconds=flightSeconds;data.FragmentDirection=direction;
            if(flightSeconds>0)data.BurstPosition=LaunchPosition(state)+(data.OwnVelocity+direction*state.Profile.projectileSpeed)*flightSeconds;
            CombatTelemetrySession.RecordApsEvent(Time.timeAsDouble,name+"#"+GetEntityId(),eventName,in data);
        }
        private void LateUpdate()
        {
            foreach(var state in states.Values)if(state.Profile.kind==MechDefenseExtensionKind.ActiveProtection)
            {
                string key=state.Reason+":"+state.Ammo+":"+state.Pending;
                if(key==state.LoggedStatus&&Time.timeAsDouble<state.NextStatus)continue;
                Record(state,"state_snapshot",state.Reason,"state");state.LoggedStatus=key;state.NextStatus=Time.timeAsDouble+(state.Pending?.05:.25);
            }
        }
        private static void ReleaseSensor(State state)
        {if(state.Sensor.IsValid&&state.SensorOwner!=null)state.SensorOwner.ReleaseProtectionRadar(state.Sensor);state.Sensor=default;}
        private void OnDisable(){foreach(var state in states.Values){Record(state,"component_disabled","Disabled","lifecycle");ReleaseSensor(state);}}
        private void OnDestroy(){foreach(var state in states.Values)ReleaseSensor(state);}
        private void FixedUpdate()
        {
            Sync();var radar=GetComponent<MechRadarController>();var weapons=GetComponent<MechTestWeaponSystem>();
            bool hasProtection=false;
            foreach(var state in states.Values)
            {
                if(state.Profile.kind!=MechDefenseExtensionKind.ActiveProtection)continue;
                hasProtection=true;
                bool allowed=weapons!=null&&(weapons.Actions.Emissions.Mode==MechEmissionMode.Unrestricted||
                    (weapons.Actions.Emissions.Mode==MechEmissionMode.AuthorizedSelfDefense&&weapons.Actions.Emissions.AllowAutomaticDefense));
                if(!AutomaticProtectionEnabled||!Operational(state)||state.Ammo<=0||radar==null||!allowed)
                {ReleaseSensor(state);if(!allowed)state.Reason="SensorEmissionDenied";continue;}
                if(!state.Sensor.IsValid)
                {if(!radar.TryCreateProtectionRadar(state.Profile.detectionRangeMeters,out state.Sensor)){state.Reason="SensorCapacity";continue;}state.SensorOwner=radar;}
                if(SimulationTime.IsDue(radar.SimTimeSeconds,state.NextScan))
                {radar.ScanProtectionRadar(state.Sensor,ref state.Sequence);state.NextScan=radar.SimTimeSeconds+Mathf.Max(.02f,state.Profile.scanIntervalSeconds);}
            }
            // Consume fresh sensor updates on the physics clock. A render-frame delay
            // can otherwise consume most of a 2 km/s threat's safe firing window.
            if(hasProtection&&weapons!=null&&weapons.isActiveAndEnabled)
            {TickRequests(weapons);weapons.Actions.Tick(Time.fixedTimeAsDouble,true);}
        }
        [Serializable] public sealed class Diagnostic {public string slot,reason;public int ammunition,activeShots;public List<string> rejected=new();public List<string> history=new();public string planningSample;}
        public void CopyDiagnostics(List<Diagnostic> output)
        {
            Sync();output.Clear();foreach(var state in states.Values)if(state.Profile.kind==MechDefenseExtensionKind.ActiveProtection)
            {var row=new Diagnostic{slot=state.Record.SlotId,reason=state.Reason,ammunition=state.Ammo,activeShots=state.Shots.Count};foreach(var pair in state.Rejections)row.rejected.Add(pair.Key+":"+pair.Value);row.planningSample=state.LastPlanningSample;row.history.AddRange(state.History);output.Add(row);}
        }
        private void Reject(State state,string reason,bool record=true)
        {if(record&&(state.LoggedGate!=reason||Time.timeAsDouble>=state.NextGate)){Record(state,"gate_rejected",reason,"gate");state.LoggedGate=reason;state.NextGate=Time.timeAsDouble+.25;}state.LastRejection=reason;state.LastRejectionAt=Time.timeAsDouble;if(state.Reason!=reason){state.History.Enqueue(Time.timeAsDouble.ToString("F3")+":"+reason);while(state.History.Count>32)state.History.Dequeue();}state.Reason=reason;state.Rejections.TryGetValue(reason,out var count);state.Rejections[reason]=count+1;}
        private Vector3 LaunchPosition(State state)=>state.Record.instance!=null?state.Record.instance.transform.position:transform.position;
        private Quaternion LaunchRotation(State state)=>state.Record.instance!=null?state.Record.instance.transform.rotation:transform.rotation;
        private bool Plan(State state,in MissileTrackSnapshot track,float delay,out Vector3 direction,out float flight,bool preparation=false)
        {
            var radar=GetComponent<MechRadarController>();var motion=GetComponent<MechFlightMotionController>();
            bool result=TryPlan(in track,radar!=null?radar.SimTimeSeconds+Math.Max(0,Time.timeAsDouble-Time.fixedTimeAsDouble):track.LastUpdateTime,LaunchPosition(state),
                motion!=null?motion.Velocity:Vector3.zero,LaunchRotation(state),state.Profile,delay,out direction,out flight,out var reason,preparation);
            if(result&&!preparation&&Vector3.Distance(transform.position,LaunchPosition(state)+direction*state.Profile.projectileSpeed*flight)<state.Profile.minimumBurstDistanceMeters)
            {result=false;reason="BurstTooClose";}
            if(result&&preparation)state.LastValidPreparationTime=Time.timeAsDouble;
            string key=track.TrackId.Packed+":"+preparation;
            if(!state.LoggedPlans.TryGetValue(key,out var logged)||logged.reason!=reason||Time.timeAsDouble-logged.time>=.25)
            {
                var saved=state.SelectedTrack;bool had=state.HasSelectedTrack;state.SelectedTrack=track;state.HasSelectedTrack=true;
                Record(state,result?"plan_accepted":"plan_rejected",reason,preparation?"prepare":"release",flight,direction);state.SelectedTrack=saved;state.HasSelectedTrack=had;
                if(state.LoggedPlans.Count>=512)state.LoggedPlans.Clear();state.LoggedPlans[key]=(reason,Time.timeAsDouble);
            }
            if(!result){state.LastPlanningSample=$"t={Time.timeAsDouble:F3} track={track.TrackId.Packed} range={Vector3.Distance(LaunchPosition(state),new Vector3((float)track.Position.x,(float)track.Position.y,(float)track.Position.z)):F1} speed={track.Velocity} sigma={track.PositionUncertaintyMeters:F2} reaction={delay:F3} reason={reason}";Reject(state,reason,false);}return result;
        }
        public void TickRequests(MechTestWeaponSystem weapons)
        {
            Sync();var radar=GetComponent<MechRadarController>();if(radar==null){foreach(var state in states.Values)state.Reason="NoSensor";return;}
            int count=radar.CopyMissileTracks(tracks);
            foreach(var state in states.Values)
            {
                if(state.Profile.kind!=MechDefenseExtensionKind.ActiveProtection)continue;
                state.Shots.RemoveAll(s=>s==null||!s.IsFlying);
                if(!AutomaticProtectionEnabled){state.Reason="Disabled";continue;}
                if(weapons.Actions.Emissions.Mode==MechEmissionMode.Silent || (weapons.Actions.Emissions.Mode==MechEmissionMode.AuthorizedSelfDefense&&!weapons.Actions.Emissions.AllowAutomaticDefense))
                {state.Reason="SensorEmissionDenied";continue;}
                if(!Operational(state)){state.Reason="ComponentFailed";continue;}
                if(state.Ammo<=0){state.Reason="NoAmmo";continue;}
                if(Time.timeAsDouble<state.NextShot){state.Reason="CoolingDown";continue;}
                if(state.Shots.Count>=Mathf.Max(1,state.Profile.maximumConcurrentShots)){state.Reason="ConcurrencyLimit";continue;}
                if(weapons.Actions.HasPending(state.Record.InstanceId))
                {
                    bool preempt=false;
                    if(state.Pending&&state.HasSelectedTrack)for(int i=0;i<count;i++)
                    {
                        var candidate=tracks[i];
                        if(candidate.TrackId.Equals(state.SelectedTrack.TrackId)||!candidate.Incoming||candidate.IsFriendly)continue;
                        float switchCost=state.Profile.reactionSeconds+state.Profile.scanIntervalSeconds;
                        if(candidate.TimeToClosestApproachSeconds+switchCost<state.SelectedTrack.TimeToClosestApproachSeconds&&Plan(state,in candidate,state.Profile.reactionSeconds,out _,out _,true))
                        {state.Reason="MoreUrgentThreat";weapons.Actions.Cancel(state.Action,Time.timeAsDouble);preempt=true;break;}
                    }
                    if(!preempt){if(!state.Pending||Time.timeAsDouble<state.ReadyAt)state.Reason="Reacting";continue;}
                }
                if(count==0){state.HasSelectedTrack=false;state.Reason="NoLocalTrack";continue;}
                var best=default(MissileTrackSnapshot);float score=float.PositiveInfinity;
                float nearest=float.PositiveInfinity;int bestRank=2;string closestReason="NoLocalTrack";
                for(int i=0;i<count;i++)
                {
                    bool feasible=Plan(state,in tracks[i],state.Profile.reactionSeconds,out _,out _,true);
                    if(feasible&&(tracks[i].TimeToClosestApproachSeconds<score||(tracks[i].TimeToClosestApproachSeconds==score&&tracks[i].TrackId.Packed<best.TrackId.Packed)))
                    {best=tracks[i];score=tracks[i].TimeToClosestApproachSeconds;}
                    else if(!feasible)
                    {
                        var pos=tracks[i].PredictPosition(radar.SimTimeSeconds);float range=Vector3.Distance(LaunchPosition(state),new Vector3((float)pos.x,(float)pos.y,(float)pos.z));
                        int rank=tracks[i].Incoming&&!tracks[i].IsFriendly&&radar.SimTimeSeconds-tracks[i].LastEvidenceTime<=state.Profile.maximumEvidenceAgeSeconds?0:1;
                        if(rank<bestRank||(rank==bestRank&&range<nearest)){bestRank=rank;nearest=range;closestReason=state.Reason;}
                    }
                }
                if(best.TrackId.IsValid){state.SelectedTrack=best;state.HasSelectedTrack=true;Record(state,"target_selected","Selected","selection");weapons.SubmitDefenseExtension(state.Record,radar.Reference(best.TrackId));state.Reason="Reacting";}
                else state.Reason=closestReason;
            }
        }
        private static bool IsTransientPlanningFailure(string reason)=>reason=="PositionUncertainty"||reason=="TrackQuality"||reason=="StaleEvidence"||reason=="OutsideLaunchRange"||reason=="OutsideCoverage"||reason=="NoInterceptSolution"||reason=="NotIncoming";
        public bool Check(MechActionProposal p,out string reason)
        {
            Sync();reason="ExtensionUnavailable";
            if(!states.TryGetValue(p.InstanceId,out var state)||state.Record.SlotId!=p.Slot.Id||!Operational(state)||!AutomaticProtectionEnabled||state.Profile.kind!=MechDefenseExtensionKind.ActiveProtection)return false;
            if(p.Kind!=MechActionKind.Fire||p.Id.Source!=MechActionSource.AutomaticDefense||p.EnergyMj!=state.Profile.energyPerShotMj||p.GuidanceUnits!=0||p.Emissions!=MechEmissionRequirement.Radar)return false;
            state.Shots.RemoveAll(s=>s==null||!s.IsFlying);
            reason="NoAmmo";if(state.Ammo<=0){Reject(state,reason);return false;}
            reason="CoolingDown";if(Time.timeAsDouble<state.NextShot){Reject(state,reason);return false;}
            reason="ConcurrencyLimit";if(state.Shots.Count>=Mathf.Max(1,state.Profile.maximumConcurrentShots)){Reject(state,reason);return false;}
            var radar=GetComponent<MechRadarController>();reason="MissingTrack";
            if(radar==null||!p.Target.IsValid||p.Target.ObserverScope!=radar.ObserverScope||!radar.TryGetMissileTrack(p.Target.TrackId,out var track))
            {state.HasSelectedTrack=false;Reject(state,reason);return false;}
            state.SelectedTrack=track;state.HasSelectedTrack=true;
            if(!Plan(state,in track,state.Pending?(float)Math.Max(0,state.ReadyAt-Time.timeAsDouble):state.Profile.reactionSeconds,out _,out _,true))
            {
                // Transient observation changes delay release without restarting reaction.
                // Ready and Commit still require a currently valid firing solution; the action deadline bounds this wait.
                if(state.Pending&&state.Action.Equals(p.Id)&&Time.timeAsDouble-state.LastValidPreparationTime<=.2&&IsTransientPlanningFailure(state.Reason)){reason=null;return true;}
                reason=state.Reason;return false;
            }
            reason=null;return true;
        }
        public bool Begin(MechActionProposal p,out string reason)
        {
            if(!Check(p,out reason))return false;var state=states[p.InstanceId];state.Pending=true;state.Action=p.Id;
            state.ReadyAt=Time.timeAsDouble+Mathf.Max(0,state.Profile.reactionSeconds);state.Reason="Reacting";Record(state,"preparation_started",state.Reason,"prepare");return true;
        }
        public bool Ready(MechActionProposal p)
        {
            if(!states.TryGetValue(p.InstanceId,out var state)||!state.Pending||!state.Action.Equals(p.Id)||Time.timeAsDouble<state.ReadyAt)return false;
            var radar=GetComponent<MechRadarController>();
            return radar!=null&&radar.TryGetMissileTrack(p.Target.TrackId,out var track)&&Plan(state,in track,0,out _,out _);
        }
        public bool Commit(MechActionProposal p,out string reason)
        {
            if(!Ready(p)){reason=states.TryGetValue(p.InstanceId,out var waiting)?waiting.Reason:"ExtensionUnavailable";return false;}
            if(!Check(p,out reason))return false;
            var state=states[p.InstanceId];var radar=GetComponent<MechRadarController>();radar.TryGetMissileTrack(p.Target.TrackId,out var track);
            if(!Plan(state,in track,0,out var direction,out float flight)){reason=state.Reason;return false;}
            var motion=GetComponent<MechFlightMotionController>();if(p.EnergyMj>0&&(motion==null||!motion.TrySpendEquipmentEnergy(p.EnergyMj))){reason="EnergyUnavailable";return false;}
            var go=new GameObject("APS Countermunition");go.transform.position=LaunchPosition(state);go.transform.rotation=Quaternion.LookRotation(direction);
            MechApsEffects.AttachCylinder(go);MechApsEffects.Launch(go.transform.position,direction,GetComponent<MechPlayerRig>()!=null&&GetComponent<MechAiTargetController>()==null);
            var shot=go.AddComponent<MechApsProjectile>();
            shot.Configure(transform,(motion!=null?motion.Velocity:Vector3.zero)+direction*state.Profile.projectileSpeed,flight,state.Profile.blastRadiusMeters,state.Profile.blastDamage,direction,state.Profile.fragmentHalfAngle,state.Profile.minimumBurstDistanceMeters,state.Profile.smokeRingScale);
            GetComponent<MechFleetController>()?.NotifyApsDefense(p.Target.TrackId);
            state.Shots.Add(shot);state.Ammo--;state.Pending=false;state.NextShot=Time.timeAsDouble+Mathf.Max(0,state.Profile.cooldownSeconds);state.Reason=reason="Committed";
            var data=Telemetry(state,reason,"launch");data.PreparationProgress=1;data.FlightSeconds=flight;data.BurstPosition=go.transform.position+((motion!=null?motion.Velocity:Vector3.zero)+direction*state.Profile.projectileSpeed)*flight;data.FragmentDirection=direction;
            shot.BeginTelemetry(name+"#"+GetEntityId(),data);return true;
        }
        public void Cancel(MechActionProposal p){if(states.TryGetValue(p.InstanceId,out var state)&&state.Action.Equals(p.Id)){Record(state,"action_cancelled",Time.timeAsDouble>=p.Deadline?"DeadlineExpired":state.Reason,"cancel");state.Pending=false;if(state.Reason=="Reacting")state.Reason="Cancelled";}}
        private static string DisplayReason(string reason)=>reason switch
        {
            "NoLocalTrack"=>"未发现近距航迹", "TrackQuality"=>"航迹尚未确认", "PositionUncertainty"=>"定位精度不足",
            "OutsideLaunchRange"=>"目标尚未进入拦截范围", "InsideSafetyRange"=>"目标过近", "BurstTooClose"=>"预计引爆点距机体过近", "TooLateAfterReaction"=>"反应时间不足",
            "InsufficientTime"=>"来不及拦截", "StaleEvidence"=>"航迹过期", "OutsideCoverage"=>"超出射界", "NotIncoming"=>"未判定为来袭",
            "MoreUrgentThreat"=>"切换至更紧急威胁", "CoolingDown"=>"冷却中", "NoAmmo"=>"弹药耗尽", "ConcurrencyLimit"=>"拦截弹在飞", "Reacting"=>"准备发射",
            "SensorEmissionDenied"=>"禁止主动探测", "Committed"=>"已发射", "ComponentFailed"=>"组件失效", "Disabled"=>"已关闭", "MissingTrack"=>"航迹丢失", "Friendly"=>"友方目标", "FlightTimeLimit"=>"超出弹射航程", "NoInterceptSolution"=>"无法形成拦截解", "SensorCapacity"=>"探测器资源不足", "NoSensor"=>"探测器未就绪", _=>reason
        };
        private void OnGUI()
        {
            if(GetComponent<MechPlayerRig>()==null||GetComponent<MechAiTargetController>()!=null||MechAssemblyMenu.IsAnyMenuVisible||MechSolarSystemMap.IsAnyMapVisible)return;
            var matrix=MechHudLayout.BeginScaledGui();var color=GUI.color;GUI.color=new Color(.4f,1,.65f);
            var statusRect=MechHudLayout.OwnshipDefenseStatusRect;float y=statusRect.y;
            foreach(var state in states.Values)if(state.Profile.kind==MechDefenseExtensionKind.ActiveProtection)
            {GUI.Label(new Rect(statusRect.x,y,statusRect.width,25),$"APS {state.Ammo}/{state.Profile.ammunition}  {DisplayReason(state.Reason)}");y+=24;if(!string.IsNullOrEmpty(state.LastRejection)&&state.LastRejection!=state.Reason&&Time.timeAsDouble-state.LastRejectionAt<2){GUI.Label(new Rect(statusRect.x,y,statusRect.width,25),"最近限制："+DisplayReason(state.LastRejection));y+=24;}}
            GUI.color=color;GUI.matrix=matrix;
        }
        public void CopySnapshots(List<MechDefenseExtensionSnapshot> destination)
        {
            Sync();destination.Clear();foreach(var state in states.Values)destination.Add(new MechDefenseExtensionSnapshot(state.Record.SlotId,state.Record.InstanceId,state.Profile.kind,state.Profile.kind==MechDefenseExtensionKind.ActiveProtection?state.Ammo:0,state.Shots.FindAll(s=>s!=null&&s.IsFlying).Count,state.ReadyAt,state.NextShot,state.Pending,state.Reason,Operational(state)));
        }
    }
}
