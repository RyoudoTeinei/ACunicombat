using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public sealed class MechFleetRosterEntry
    {public Guid Member,Fleet,Leader;public string Name;public bool Player,IsLeader;public int FormationIndex,FormationSize;public Guid FormationRoot;public string FormationName="";public FleetTaskGroup TaskGroup;public FleetFormationShape FormationShape;public Vector3 FormationOffset;}
    public readonly struct MechFleetOutbound
    {public readonly long Token;public readonly FleetPacket Packet;public MechFleetOutbound(long token,FleetPacket packet){Token=token;Packet=packet;}}
    [DisallowMultipleComponent,DefaultExecutionOrder(-170)]
    public sealed partial class MechFleetController : MonoBehaviour
    {
        // Mission-issued roster is not a tactical world-state service. No transforms/health are stored here.
        public static readonly Dictionary<Guid,MechFleetRosterEntry> Roster=new Dictionary<Guid,MechFleetRosterEntry>();
        public MechFleetState State {get;private set;}
        public Guid MemberId {get;}=Guid.NewGuid();
        public bool Configured=>State!=null;
        public bool IsLeader=>State!=null&&State.IsLeader;
        public bool IsPlayer=>State!=null&&State.IsPlayer;
        public bool Detached=>State?.Task!=null&&State.Task.Detached&&!State.Task.Terminal;
        public bool HasTask=>State!=null&&State.HasTask;
        public string LastDecision {get;private set;}="WaitingForConfiguration";
        public Guid LastIssuedOrder {get;private set;}
        public IReadOnlyDictionary<Guid,FleetPacket> Results=>results;
        public bool LinkEnabled=true;
        [Range(.05f,.75f)] public float CoordinationBandwidthFraction=.25f;
        public float TaskSeconds=60,MemberReportSeconds=.5f,FormationSpacing=250;
        private MechRadarController radar;private MechTestWeaponSystem weapons;private MechFlightMotionController motion;private CombatTestTarget body;
        private readonly List<MechEquipmentWeaponSnapshot> equipment=new List<MechEquipmentWeaponSnapshot>();
        private readonly TrackSnapshot[] air=new TrackSnapshot[96];
        private readonly MissileTrackSnapshot[] missiles=new MissileTrackSnapshot[96];
        private sealed class Outbound {public long Token;public FleetPacket Packet;public readonly HashSet<Guid> Sent=new HashSet<Guid>();}
        private sealed class Pending {public FleetPacket Packet;public double NextRetry;public bool Acknowledged;}
        private readonly List<Outbound> outbox=new List<Outbound>();
        private readonly Dictionary<Guid,Pending> pending=new Dictionary<Guid,Pending>();
        private readonly Dictionary<Guid,FleetPacket> results=new Dictionary<Guid,FleetPacket>();
        private readonly Dictionary<Guid,Guid> delegation=new Dictionary<Guid,Guid>();
        private readonly Dictionary<TrackId,Guid> contactIds=new Dictionary<TrackId,Guid>();
        private readonly Dictionary<Guid,TrackId> contactTracks=new Dictionary<Guid,TrackId>();
        private double nextHeartbeat,nextInventory,nextTargetUpdate,nextLeaderDecision,lastCredit;private float credits;
        private Guid lastRecipient;private readonly Dictionary<Guid,double> receiverVisits=new();
        private long token,revision;private TrackId resolvedOrder;private double lastSeenOrder;private int taskShots;
        private StreamWriter log;
        public static string LogDirectoryOverride;
        public void ReopenLog(){log?.Dispose();log=null;}
        private double nextPrune;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void ResetRoster()=>Roster.Clear();
        public void Configure(Guid fleet,Guid leader,bool player,bool isLeader,int index=0,FleetAttackMode initialMode=FleetAttackMode.Confirmed,Guid formationRoot=default,int formationSize=1)
        {
            if(Configured)throw new InvalidOperationException("Fleet identity cannot change during a live membership.");
            State=new MechFleetState(MemberId,fleet,leader,player,isLeader,initialMode);
            State.SetInitialFormation(formationRoot==Guid.Empty?leader:formationRoot,index,Math.Max(formationSize,index+1));
            State.Feedback+=p=>{if(IsPlayer&&p.Recipient==MemberId)results[p.Operation]=p;else Send(p);};
            Roster[MemberId]=new MechFleetRosterEntry{Member=MemberId,Fleet=fleet,Leader=leader,Name=name,Player=player,IsLeader=isLeader,FormationIndex=index,FormationRoot=State.FormationRoot,FormationSize=State.FormationSize};
            radar=GetComponent<MechRadarController>();weapons=GetComponent<MechTestWeaponSystem>();motion=GetComponent<MechFlightMotionController>();body=GetComponent<CombatTestTarget>();
        }
        public void GrantAuthority(Guid commander)=>State?.GrantCommand(commander);
        public bool CanCommand(Guid recipient)
        {return IsPlayer&&recipient==MemberId||formationManagedMembers.Contains(recipient)||IsLeader&&Roster.TryGetValue(recipient,out var r)&&!r.Player&&(r.Leader==MemberId||r.FormationRoot==MemberId||authorizedLeaders.Contains(recipient));}
        private readonly HashSet<Guid> authorizedLeaders=new HashSet<Guid>();
        public void GrantSubordinateLeader(MechFleetController other)
        {if(!IsLeader||other==null||!other.IsLeader||other.IsPlayer)throw new InvalidOperationException("Invalid command hierarchy");authorizedLeaders.Add(other.MemberId);other.GrantAuthority(MemberId);}
        private void Update()
        {
            if(!Configured||radar?.TelemetryDriver==null||body!=null&&!body.IsAlive)return;
            double now=radar.SimTimeSeconds;State.Tick(now);
            for(int i=outbox.Count-1;i>=0;i--)if(outbox[i].Packet.ExpiresAt<now)outbox.RemoveAt(i);
            if(now>=nextHeartbeat){nextHeartbeat=now+MemberReportSeconds;Send(GetOwnStatus());}
            if(now>=nextInventory){nextInventory=now+1;weapons?.CopyEquipmentWeaponSnapshots(equipment);foreach(var e in equipment){weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var status);Send(new FleetPacket{Kind=FleetPacketKind.Capability,Weapon=e.InstanceId,WeaponMount=(int)GetEquipmentMount(e.SlotId),Role=(int)e.Role,Ammunition=e.Rounds,WeaponName=new Unity.Collections.FixedString512Bytes(GetEquipmentDisplayName(e.SlotId)),Available=e.Enabled&&!e.Reloading,MaximumRange=status.MaximumRangeMeters,MinimumRange=status.MinimumRangeMeters,EffectiveRange=status.EffectiveRangeMeters,ProjectileSpeed=weapons.EstimatedProjectileSpeed(e.SlotId),ObservedAt=now,ExpiresAt=now+3});}}
            foreach(var pair in pending){var p=pair.Value;if(p.Acknowledged)continue;if(p.Packet.ExpiresAt<now){p.Acknowledged=true;var failed=p.Packet;failed.ObservedAt=now;failed.Phase=FleetTaskPhase.Rejected;failed.Reason=FleetTaskReason.LinkTimeout;results[pair.Key]=failed;Log("command_timeout",failed,"NoAcknowledgement");continue;}if(now<p.NextRetry)continue;p.NextRetry=now+.5;Send(p.Packet);}
            UpdateOrder(now);UpdateFleetDefense(now);
            if(now>=nextPrune){nextPrune=now+10;var stale=new List<Guid>();foreach(var pair in pending)if(pair.Value.Packet.TaskDeadline+120<now)stale.Add(pair.Key);foreach(var id in stale){pending.Remove(id);results.Remove(id);delegation.Remove(id);}
                var oldTracks=new List<TrackId>();foreach(var id in contactIds.Keys)if(!radar.TryGetTrack(id,out _)&&!radar.TryGetMissileTrack(id,out _))oldTracks.Add(id);foreach(var id in oldTracks){contactTracks.Remove(contactIds[id]);contactIds.Remove(id);}}

            if(IsLeader&&State.Mode==FleetAttackMode.Free&&State.Leader==MemberId&&State.TaskGroup==FleetTaskGroup.Strike&&now>=nextLeaderDecision){nextLeaderDecision=now+8;CoordinateAutonomousAttack(now);}
            if(now>=nextTargetUpdate){nextTargetUpdate=now+.5;RefreshIssuedTargets(now);}
        }
        public void Send(FleetPacket p)
        {
            if(!Configured||outbox.Count>=256){LastDecision="OutboundCapacity";return;}
            p.Sender=MemberId;p.Fleet=State.Fleet;
            if(p.ObservedAt==0&&radar!=null)p.ObservedAt=radar.SimTimeSeconds;
            if(p.ExpiresAt==0)p.ExpiresAt=p.ObservedAt+3;
            bool transient=p.Kind==FleetPacketKind.Heartbeat||p.Kind==FleetPacketKind.Capability||p.Kind==FleetPacketKind.Threat||p.Kind==FleetPacketKind.DefenseClaim||p.Kind==FleetPacketKind.DefenseCommitted||p.Kind==FleetPacketKind.TargetUpdate;
            if(transient)
            {
                var previous=outbox.Find(x=>x.Packet.Kind==p.Kind&&x.Packet.Recipient==p.Recipient&&x.Packet.Contact==p.Contact&&x.Packet.Weapon==p.Weapon&&x.Packet.Operation==p.Operation);
                if(previous!=null)
                {
                    bool waiting=false;foreach(var peer in receiverVisits)if(p.ObservedAt-peer.Value<1&&!previous.Sent.Contains(peer.Key)&&(p.Recipient==Guid.Empty||p.Recipient==peer.Key)){waiting=true;break;}
                    if(waiting){if(!p.Threat.TrackId.IsValid&&previous.Packet.Threat.TrackId.IsValid){p.Threat=previous.Packet.Threat;p.ThreatTimeSeconds=Math.Max(0,previous.Packet.ThreatTimeSeconds-(float)Math.Max(0,p.ObservedAt-previous.Packet.ObservedAt));}previous.Packet=p;return;}
                    outbox.Remove(previous);
                }
            }
            outbox.Add(new Outbound{Token=++token,Packet=p});Log("queued",p,p.Kind.ToString());
        }
        // These two methods are used ONLY by the physical cooperative-radio bridge.
        public void CopyOutboundFor(Guid recipient,double now,List<MechFleetOutbound> output)
        {
            output.Clear();if(!LinkEnabled)return;
            receiverVisits[recipient]=now;
            if(lastRecipient==recipient){foreach(var peer in receiverVisits)if(peer.Key!=recipient&&now-peer.Value<1&&outbox.Exists(x=>x.Packet.ExpiresAt>=now&&!x.Sent.Contains(peer.Key)&&(x.Packet.Recipient==Guid.Empty||x.Packet.Recipient==peer.Key)))return;}
            float bandwidth=radar!=null?radar.TelemetryLink.bandwidthKbps:0;
            float largest=8;foreach(var message in outbox)largest=Mathf.Max(largest,message.Packet.EstimatedWireKilobits);
            credits=Mathf.Min(Mathf.Max(largest,bandwidth*CoordinationBandwidthFraction),credits+(float)Math.Max(0,now-lastCredit)*bandwidth*CoordinationBandwidthFraction);lastCredit=now;
            outbox.Sort((a,b)=>{int order=Priority(a.Packet.Kind).CompareTo(Priority(b.Packet.Kind));return order!=0?order:a.Token.CompareTo(b.Token);});
            int allowance=1;
            foreach(var p in outbox)if(allowance>0&&credits>=p.Packet.EstimatedWireKilobits&&p.Packet.ExpiresAt>=now&&!p.Sent.Contains(recipient)&&(p.Packet.Recipient==Guid.Empty||p.Packet.Recipient==recipient))
            {output.Add(new MechFleetOutbound(p.Token,p.Packet));allowance--;}
        }
        private static int Priority(FleetPacketKind kind)=>kind==FleetPacketKind.Order||kind==FleetPacketKind.Cancel||kind==FleetPacketKind.Acknowledgement||kind==FleetPacketKind.TaskResult?0:kind==FleetPacketKind.Heartbeat?1:kind==FleetPacketKind.DefenseCommitted||kind==FleetPacketKind.DefenseRelease||kind==FleetPacketKind.Capability?2:kind==FleetPacketKind.DefenseClaim?3:4;
        public void MarkTransportSent(long id,Guid recipient){foreach(var p in outbox)if(p.Token==id){if(p.Sent.Add(recipient)){credits=Math.Max(0,credits-p.Packet.EstimatedWireKilobits);lastRecipient=recipient;}return;}}
        public void Receive(FleetPacket p)
        {
            if(!Configured||!LinkEnabled||!Roster.ContainsKey(p.Sender))return;
            double now=radar.SimTimeSeconds;
            if(!State.Fresh(p,now)||p.Recipient!=Guid.Empty&&p.Recipient!=MemberId)return;
            if(p.Kind==FleetPacketKind.Acknowledgement||p.Kind==FleetPacketKind.TaskResult)
            {if(pending.TryGetValue(p.Operation,out var sent)&&sent.Packet.Recipient==p.Sender&&sent.Packet.Revision==p.Revision){sent.Acknowledged=true;results[p.Operation]=p;Log("task_feedback",p,p.Phase+":"+p.Reason);}return;}
            if(p.Kind==FleetPacketKind.Threat){ReceiveThreat(p,now);return;}
            if(p.Fleet!=State.Fleet&&(p.Kind==FleetPacketKind.DefenseClaim||p.Kind==FleetPacketKind.DefenseCommitted||p.Kind==FleetPacketKind.DefenseRelease))return;
            if((p.Kind==FleetPacketKind.DefenseClaim||p.Kind==FleetPacketKind.DefenseCommitted)&&p.Threat.TrackId.IsValid)ReceiveThreat(p,now);
            bool accepted=State.Receive(p,now);Log("received",p,accepted?"Accepted":"RejectedOrDuplicate");
            if(accepted&&p.Kind==FleetPacketKind.Order)
            {
                if(p.Order==FleetOrderKind.ConfigureFormation){UpdateFormationRoster();return;}
                if(p.Order==FleetOrderKind.AssignMission){resolvedOrder=default;lastSeenOrder=now;missionWaypoint=0;UpdateFormationRoster();return;}
                if(p.Order==FleetOrderKind.SetRadarControl)radar.SetRadarPowerEnabled(State.RadarControl==FleetRadarControl.Active);
                if(p.Order==FleetOrderKind.Attack||p.Order==FleetOrderKind.AttackWithWeapon||p.Order==FleetOrderKind.Return){resolvedOrder=default;lastSeenOrder=now;taskShots=0;}
                if(IsLeader){foreach(var member in Roster.Values)if(member.Leader==MemberId&&member.Member!=MemberId&&!member.Player){Guid childWeapon=Guid.Empty;
                    if(p.Order==FleetOrderKind.AttackWithWeapon){foreach(var cap in State.Capabilities.Values)if(cap.Sender==member.Member&&cap.Role==p.Role&&cap.Available&&State.Fresh(cap,now)){childWeapon=cap.Weapon;break;}
                    if(childWeapon==Guid.Empty){LastDecision="ChildWeaponUnavailable";Log("delegation_rejected",p,member.Name+":MissingWeapon");continue;}}
                    var child=IssueInternal(member.Member,p.Order,p.Target,childWeapon,p.Mode,true,p.TaskDeadline,p.RadarControl,p.Waypoints,p.AuthorizedWeaponSelection,p.CruiseSpeed,p.AutomaticEngagement);delegation[child]=p.Operation;}}
            }
            if(accepted&&p.Kind==FleetPacketKind.Cancel&&IsLeader)foreach(var d in delegation)if(d.Value==p.Operation)CancelOrder(d.Key);
        }
        public Guid Issue(Guid recipient,FleetOrderKind kind,TrackSnapshot target=default,Guid weapon=default,FleetAttackMode mode=FleetAttackMode.Confirmed)
        {
            if(!CanCommand(recipient)){LastDecision="Unauthorized";return Guid.Empty;}
            return IssueInternal(recipient,kind,target,weapon,mode,false,radar.SimTimeSeconds+TaskSeconds,authorizedWeaponSelection:IsPlayer&&kind==FleetOrderKind.Attack);
        }
        private Guid IssueInternal(Guid recipient,FleetOrderKind kind,TrackSnapshot target,Guid weapon,FleetAttackMode mode,bool whole,double until,FleetRadarControl radarControl=FleetRadarControl.Active,Unity.Collections.FixedList512Bytes<double3> waypoints=default,bool authorizedWeaponSelection=false,float cruiseSpeed=300,bool automaticEngagement=false)
        {
            var p=new FleetPacket{Kind=FleetPacketKind.Order,Recipient=recipient,Operation=Guid.NewGuid(),Revision=++revision,Order=kind,Target=target,Weapon=weapon,Mode=mode,RadarControl=radarControl,WholeFleet=whole,AutomaticEngagement=automaticEngagement,CruiseSpeed=cruiseSpeed,Waypoints=waypoints,AuthorizedWeaponSelection=authorizedWeaponSelection,ObservedAt=radar.SimTimeSeconds,ExpiresAt=radar.SimTimeSeconds+3,TaskDeadline=until,Contact=ContactFor(target.TrackId)};
            if(weapon!=Guid.Empty){if(State.Capabilities.TryGetValue(weapon,out var capability))p.Role=capability.Role;else{weapons.CopyEquipmentWeaponSnapshots(equipment);foreach(var e in equipment)if(e.InstanceId==weapon)p.Role=(int)e.Role;}}
            pending[p.Operation]=new Pending{Packet=p,NextRetry=p.ObservedAt+.5};LastIssuedOrder=p.Operation;
            if(IsPlayer&&recipient==MemberId)
            {
                p.Sender=MemberId;p.Fleet=State.Fleet;pending[p.Operation].Packet=p;pending[p.Operation].Acknowledged=true;
                if(!State.ReceiveLocalPlayerOrder(p,radar.SimTimeSeconds))return Guid.Empty;
                resolvedOrder=default;lastSeenOrder=radar.SimTimeSeconds;
                foreach(var member in Roster.Values)if(IsLeader&&member.Leader==MemberId&&member.Member!=MemberId&&!member.Player)
                {
                    Guid childWeapon=Guid.Empty;
                    if(kind==FleetOrderKind.AttackWithWeapon){foreach(var cap in State.Capabilities.Values)if(cap.Sender==member.Member&&cap.Role==p.Role&&cap.Available&&State.Fresh(cap,radar.SimTimeSeconds)){childWeapon=cap.Weapon;break;}if(childWeapon==Guid.Empty)continue;}
                    var child=IssueInternal(member.Member,kind,target,childWeapon,mode,true,until,radarControl,waypoints,authorizedWeaponSelection,cruiseSpeed);delegation[child]=p.Operation;
                }
                return p.Operation;
            }
            Send(p);return p.Operation;
        }
        public bool SetOwnRadarControl(FleetRadarControl mode)
        {if(!Configured||radar==null||!State.SetLocalRadarControl(mode))return false;radar.SetRadarPowerEnabled(mode==FleetRadarControl.Active);return true;}
        public Guid RequestCruiseSpeed(Guid recipient,float speed)
        {if(!CanCommand(recipient))return Guid.Empty;return IssueInternal(recipient,FleetOrderKind.SetCruiseSpeed,default,default,State.Mode,false,radar.SimTimeSeconds+TaskSeconds,cruiseSpeed:speed);}
        public void LimitCruise(ref MechPlayerInputFrame frame)
        {if(!Configured||!State.CruiseCommandActive||motion==null||!frame.HasAutonomousFlightCommand)return;var desired=Vector3.ClampMagnitude(motion.Velocity+frame.AutonomousAccelerationWorld*2,State.CruiseSpeed);frame.AutonomousAccelerationWorld=Vector3.ClampMagnitude((desired-motion.Velocity)/2,30);}
        public Guid RequestRadarControl(Guid recipient,FleetRadarControl mode)
        {if(!CanCommand(recipient)||mode==FleetRadarControl.Intelligent)return Guid.Empty;return IssueInternal(recipient,FleetOrderKind.SetRadarControl,default,default,State.Mode,false,radar.SimTimeSeconds+TaskSeconds,mode);}
        private readonly List<MechPartDamageSnapshot> statusDamage=new();
        public FleetPacket GetOwnStatus()
        {
            if(!Configured||radar==null)return default;
            double now=radar.SimTimeSeconds;
            var p=new FleetPacket{Kind=FleetPacketKind.Heartbeat,Sender=MemberId,Fleet=State.Fleet,Leader=State.Leader,Position=(double3)(float3)transform.position,Velocity=(double3)(float3)(motion!=null?motion.Velocity:Vector3.zero),Available=body==null||body.IsAlive,IsPlayer=IsPlayer,IsLeader=IsLeader,HasPlatformStatus=body!=null,HealthFraction=body!=null?body.HealthFraction:0,HeadingDegrees=ReportedHeading(),RadarPowered=radar.RadarPowerEnabled,RadarControl=State.RadarControl,Mode=State.Mode,CruiseSpeed=State.CruiseSpeed,Target=State.Task!=null?State.Task.Order.Target:default,Order=State.Task!=null?State.Task.Order.Order:default,Phase=State.Task!=null?State.Task.Phase:FleetTaskPhase.None,FormationRoot=State.FormationRoot,FormationIndex=State.FormationIndex,FormationSize=State.FormationSize,TaskGroup=State.TaskGroup,FormationShape=State.FormationShape,Reason=State.Task!=null?State.Task.Reason:FleetTaskReason.None,ObservedAt=now,ExpiresAt=now+3};
            var damage=GetComponent<MechPartDamageController>();
            if(damage!=null&&damage.IsBound){p.HasDamageReport=true;damage.CopySnapshots(statusDamage);foreach(var region in statusDamage){if(region.Condition==MechPartCondition.Destroyed)p.DisabledRegions++;else if(region.Condition!=MechPartCondition.NormalOrLight)p.ImpairedRegions++;}}
            var ai=GetComponent<MechAiTargetController>();p.HasActionReport=ai!=null;p.CombatAction=ai!=null?(int)ai.CurrentCombatState:0;
            p.HasNavigationGoal=TryGetNavigationGoal(out var navigationGoal);p.NavigationGoal=(double3)(float3)navigationGoal;
            return p;
        }
        private float ReportedHeading(){var forward=motion!=null?motion.LogicalRotation*Vector3.forward:transform.forward;return Mathf.Repeat(Mathf.Atan2(forward.x,forward.z)*Mathf.Rad2Deg,360);}
        public MechPartSlot GetEquipmentMount(string slotId)
        {var runtime=GetComponent<MechRuntimeState>();if(runtime!=null)foreach(var record in runtime.AssembledParts)if(record.SlotId==slotId)return record.slot;return MechPartSlot.None;}
        public string GetEquipmentDisplayName(string slotId)
        {var runtime=GetComponent<MechRuntimeState>();if(runtime!=null)foreach(var record in runtime.AssembledParts)if(record.SlotId==slotId&&record.part!=null){var name=record.part.DisplayName.Replace("\n"," ").Replace("\r"," ");return name.Length>96?name.Substring(0,96):name;}return "unknown";}
        public bool CancelOrder(Guid operation)
        {if(IsPlayer&&HasTask&&State.Task.Order.Operation==operation){CancelPlayerAutopilot();return true;}if(!pending.TryGetValue(operation,out var entry))return false;var p=entry.Packet;p.Kind=FleetPacketKind.Cancel;p.Revision=++revision;p.ObservedAt=radar.SimTimeSeconds;p.ExpiresAt=p.ObservedAt+3;entry.Packet=p;entry.Acknowledged=false;entry.NextRetry=p.ObservedAt+.5;Send(p);foreach(var child in delegation)if(child.Value==operation)CancelOrder(child.Key);return true;}
        public void SetFormationMode(FleetAttackMode mode)
        {if(!IsLeader)return;State.SetLocalMode(mode);foreach(var r in Roster.Values)if(r.Leader==MemberId&&r.Member!=MemberId&&!r.Player)Issue(r.Member,FleetOrderKind.SetAttackMode,mode:mode);}
        public Guid RequestMove(Guid recipient,double3 destination){var route=new Unity.Collections.FixedList512Bytes<double3>();route.Add(destination);return RequestRoute(recipient,route);}
        public Guid RequestRoute(Guid recipient,Unity.Collections.FixedList512Bytes<double3> route)
        {
            if(!CanCommand(recipient)||route.Length==0)return Guid.Empty;
            foreach(var point in route)if(!math.all(math.isfinite(point)))return Guid.Empty;
            return IssueInternal(recipient,FleetOrderKind.Move,new TrackSnapshot{Position=route[0]},default,State.Mode,false,radar.SimTimeSeconds+3600,FleetRadarControl.Active,route);
        }
        private Guid ContactFor(TrackId id)
        {if(!id.IsValid)return Guid.Empty;if(!contactIds.TryGetValue(id,out var key)){key=Guid.NewGuid();contactIds[id]=key;contactTracks[key]=id;}return key;}
        public void NotifyShot(Guid equipmentId,ObserverTrackRef target,MechWeaponRole role)
        {
            Log("shot_committed",new FleetPacket{Operation=State?.Task?.Order.Operation??Guid.Empty,Weapon=equipmentId,Contact=ContactFor(target.TrackId)},role+":"+(State!=null?State.Mode.ToString():"Unconfigured"));
            if(IsDefensiveRole(role)){DefenseCommitted(target.TrackId,role);return;}
            if(!HasTask||!resolvedOrder.Equals(target.TrackId)||State.Task.Phase==FleetTaskPhase.Returning)return;
            taskShots++;State.SetPhase(FleetTaskPhase.Executing,FleetTaskReason.None,radar.SimTimeSeconds);
            if(State.Task.Order.Order==FleetOrderKind.AttackWithWeapon&&equipmentId==State.Task.Order.Weapon)State.SetPhase(FleetTaskPhase.Returning,FleetTaskReason.EngagementCompleted,radar.SimTimeSeconds);
        }
        public static bool IsDefensiveRole(MechWeaponRole role)=>role==MechWeaponRole.InterceptorMissile||role==MechWeaponRole.PointDefenseCannon;
        public bool AllowsWeapon(Guid instance,MechWeaponRole role,TrackId target,out string reason)
        {
            reason=null;if(!Configured||IsPlayer||IsDefensiveRole(role))return true;
            if(HasSpatialMission&&TaskGroup==FleetTaskGroup.AirDefenseSuppression)
            {weapons.CopyEquipmentWeaponSnapshots(equipment);var chosen=equipment.Find(e=>e.InstanceId==instance);if(chosen.InstanceId==Guid.Empty||!AllowsMissionEquipment(chosen.SlotId,role)){reason="SuppressionRequiresPassiveRfWeapon";return false;}}
            if(HasSpatialMission&&!MissionAllowsOffensiveTarget(target)){reason="OutsideMissionAssignment";return false;}
            if(HasTask&&State.Task.Order.Order!=FleetOrderKind.Move&&State.Task.Phase!=FleetTaskPhase.Returning&&(!resolvedOrder.IsValid||!resolvedOrder.Equals(target))){reason="OrderedTargetOnly";return false;}
            if(HasTask&&State.Task.Phase==FleetTaskPhase.Returning&&role==MechWeaponRole.AirToAirMissile){reason="ReturningToFormation";return false;}
            if(!State.AllowsWeapon(instance)){reason="SpecifiedWeaponOnly";return false;}
            if(role==MechWeaponRole.AirToAirMissile&&!State.AllowsOffensiveMissile(instance)){reason="OffensiveMissileNotAuthorized";return false;}
            return true;
        }
        public void Log(string kind,FleetPacket p,string reason)
        {
            LastDecision=reason;
            try{if(log==null){var dir=LogDirectoryOverride??Path.Combine(Application.persistentDataPath,"FleetLogs");Directory.CreateDirectory(dir);log=new StreamWriter(Path.Combine(dir,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+MemberId.ToString("N")+".jsonl")){AutoFlush=true};}
                log.WriteLine(JsonUtility.ToJson(new LogRow{time=radar!=null?radar.SimTimeSeconds:0,actor=MemberId.ToString(),kind=kind,operation=p.Operation.ToString(),sender=p.Sender.ToString(),recipient=p.Recipient.ToString(),contact=p.Contact.ToString(),revision=p.Revision,reason=reason,weapon=p.Weapon.ToString(),phase=p.Phase.ToString()}));}catch(IOException){ }
        }
        [Serializable] private struct LogRow {public double time;public string actor,kind,operation,sender,recipient,contact,reason,weapon,phase;public long revision;}
        private void OnDisable(){log?.Dispose();log=null;}
    }
}
