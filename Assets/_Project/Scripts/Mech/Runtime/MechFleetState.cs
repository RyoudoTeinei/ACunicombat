using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
namespace ACunicombat.Mechs
{
    public sealed class MechFleetTask
    {
        public FleetPacket Order;
        public int WaypointIndex;
        public FleetTaskPhase Phase;
        public FleetTaskReason Reason;
        public bool Detached;
        public double LastObservation;
        public bool Terminal => Phase==FleetTaskPhase.Completed || Phase==FleetTaskPhase.Rejected || Phase==FleetTaskPhase.Cancelled || Phase==FleetTaskPhase.Expired;
    }
    // Observer-owned protocol state. It only knows immutable received packets and mission-issued authority.
    public sealed class MechFleetState
    {
        public readonly Guid Member, Fleet;
        public Guid Leader {get;private set;}
        public readonly bool IsPlayer;
        public bool IsLeader {get;private set;}
        public Guid FormationRoot {get;private set;}
        public int FormationIndex {get;private set;}
        public int FormationSize {get;private set;}=1;
        public FleetTaskGroup TaskGroup {get;private set;}
        public FleetFormationShape FormationShape {get;private set;}
        public Unity.Mathematics.float3 FormationOffset {get;private set;}
        public string FormationName {get;private set;}="";
        public void SetInitialFormation(Guid root,int index,int size){FormationRoot=root;FormationIndex=index;FormationSize=size;}
        public void ApplyFormationConfiguration(in FleetPacket p,double now)
        {
            bool changed=Leader!=p.Leader||FormationRoot!=p.FormationRoot||TaskGroup!=p.TaskGroup;
            if(changed&&HasTask)SetPhase(FleetTaskPhase.Cancelled,FleetTaskReason.Cancelled,now);
            Leader=p.Leader;FormationRoot=p.FormationRoot;FormationIndex=p.FormationIndex;FormationSize=p.FormationSize;
            IsLeader=FormationIndex==0||(FormationIndex%2==0&&FormationIndex+1<FormationSize);
            TaskGroup=p.TaskGroup;FormationShape=p.FormationShape;FormationOffset=p.FormationOffset;FormationName=p.FormationName.ToString();
            GrantCommand(p.Sender);
        }
        public const float DefaultCruiseSpeed=300f;
        public const float MaxCruiseSpeed=800f;
        public FleetAttackMode Mode {get;private set;}
        public float CruiseSpeed {get;private set;}=DefaultCruiseSpeed;
        public bool CruiseCommandActive {get;private set;}
        public void CancelCruise()=>CruiseCommandActive=false;
        public FleetRadarControl RadarControl {get;private set;}=FleetRadarControl.Active;
        public bool SetLocalRadarControl(FleetRadarControl mode){if(mode!=FleetRadarControl.Active&&mode!=FleetRadarControl.RadiationSilence)return false;RadarControl=mode;return true;}
        public MechFleetTask Task {get;private set;}
        public readonly Dictionary<Guid,FleetPacket> Members=new Dictionary<Guid,FleetPacket>();
        public readonly Dictionary<Guid,FleetPacket> Capabilities=new Dictionary<Guid,FleetPacket>();
        public readonly Dictionary<Guid,FleetPacket> Claims=new Dictionary<Guid,FleetPacket>();
        private readonly Dictionary<Guid,long> revisions=new Dictionary<Guid,long>();
        private readonly Dictionary<Guid,double> operations=new Dictionary<Guid,double>();
        private readonly Dictionary<Guid,FleetPacket> replies=new();
        private readonly HashSet<Guid> commanders=new HashSet<Guid>();
        public event Action<FleetPacket> Feedback;
        public MechFleetState(Guid member,Guid fleet,Guid leader,bool player,bool isLeader,FleetAttackMode mode)
        {Member=member;Fleet=fleet;Leader=leader;IsPlayer=player;IsLeader=isLeader;Mode=mode;FormationRoot=leader;}
        public void GrantCommand(Guid commander){if(commander!=Guid.Empty&&commander!=Member)commanders.Add(commander);}
        public void SetLocalMode(FleetAttackMode mode){if(!IsLeader&&!IsPlayer)throw new InvalidOperationException("Only the local leader or player may set its policy.");Mode=mode;}
        public bool Authorized(Guid sender)=>sender==Member&&IsLeader || sender==Leader || sender==FormationRoot || commanders.Contains(sender);
        public bool Fresh(FleetPacket p,double now)=>p.Sender!=Guid.Empty&&double.IsFinite(p.ObservedAt)&&double.IsFinite(p.ExpiresAt)&&p.ObservedAt<=now+.001&&now<=p.ExpiresAt;
        public bool Receive(FleetPacket p,double now)=>ReceiveInternal(p,now,false);
        public bool ReceiveLocalPlayerOrder(FleetPacket p,double now)
            =>IsPlayer&&p.Sender==Member&&p.Recipient==Member&&ReceiveInternal(p,now,true);
        private bool ReceiveInternal(FleetPacket p,double now,bool localPlayer)
        {
            if(!Fresh(p,now)||p.Recipient!=Guid.Empty&&p.Recipient!=Member)return false;
            if(p.HasPlatformStatus&&(!float.IsFinite(p.HealthFraction)||p.HealthFraction<0||p.HealthFraction>1||!float.IsFinite(p.HeadingDegrees)||!Enum.IsDefined(typeof(FleetRadarControl),p.RadarControl)))return false;
            if(p.HasDamageReport&&(p.ImpairedRegions<0||p.DisabledRegions<0))return false;
            if(p.WeaponName.Length>384)return false;
            if(p.Kind==FleetPacketKind.Heartbeat)
            {if(p.Fleet!=Fleet)return false;if(!Members.TryGetValue(p.Sender,out var old)||p.ObservedAt>old.ObservedAt)Members[p.Sender]=p;return true;}
            if(p.Kind==FleetPacketKind.Capability)
            {if(p.Weapon==Guid.Empty)return false;if(!Capabilities.TryGetValue(p.Weapon,out var old)||p.ObservedAt>old.ObservedAt)Capabilities[p.Weapon]=p;return true;}
            if(p.Kind==FleetPacketKind.DefenseClaim||p.Kind==FleetPacketKind.DefenseCommitted)
            {
                if(p.Fleet!=Fleet||p.Contact==Guid.Empty||!double.IsFinite(p.LeaseUntil)||!float.IsFinite(p.EstimatedSeconds)||p.EstimatedSeconds<0||p.LeaseUntil<=now||p.LeaseUntil>p.ExpiresAt)return false;
                if(!Claims.TryGetValue(p.Contact,out var old)||old.LeaseUntil<=now||old.Sender==p.Sender&&p.Revision>=old.Revision||
                   old.Kind!=FleetPacketKind.DefenseCommitted&&(p.Kind==FleetPacketKind.DefenseCommitted||CompareClaim(p,old)<0))Claims[p.Contact]=p;
                return true;
            }
            if(p.Kind==FleetPacketKind.DefenseRelease)
            {if(p.Fleet==Fleet&&Claims.TryGetValue(p.Contact,out var old)&&old.Sender==p.Sender&&p.Revision>=old.Revision)Claims.Remove(p.Contact);return true;}
            if(p.Kind==FleetPacketKind.TargetUpdate)
            {
                if(Task==null||Task.Terminal||p.Operation!=Task.Order.Operation||p.Sender!=Task.Order.Sender||p.Revision<Task.Order.Revision||p.Target.LastUpdateTime<=Task.Order.Target.LastUpdateTime)return false;
                var order=Task.Order;order.Target=p.Target;Task.Order=order;Task.LastObservation=p.Target.LastUpdateTime;return true;
            }
            if(p.Kind!=FleetPacketKind.Order&&p.Kind!=FleetPacketKind.Cancel)return false;
            if(IsPlayer&&!localPlayer){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.PlayerIsNotSubordinate,now);return false;}
            if(!localPlayer&&!Authorized(p.Sender)){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.Unauthorized,now);return false;}
            if(p.Operation==Guid.Empty)return false;
            if(revisions.TryGetValue(p.Sender,out long revision)&&p.Revision<revision){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.StaleRevision,now);return false;}
            if(operations.ContainsKey(p.Operation)&&p.Kind==FleetPacketKind.Order)
            {Reply(p,replies.TryGetValue(p.Operation,out var previous)?previous.Phase:FleetTaskPhase.Cancelled,replies.TryGetValue(p.Operation,out previous)?previous.Reason:FleetTaskReason.StaleRevision,now);return false;}
            if(p.Kind==FleetPacketKind.Cancel&&Task!=null&&Task.Order.Operation==p.Operation&&Task.Order.Revision==p.Revision&&Task.Reason==FleetTaskReason.Cancelled){Reply(p,Task.Phase,Task.Reason,now);return false;}
            if(revisions.TryGetValue(p.Sender,out revision)&&p.Revision<=revision){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.StaleRevision,now);return false;}
            if(p.Kind==FleetPacketKind.Cancel)
            {
                revisions[p.Sender]=p.Revision;
                if(Task==null||p.Operation!=Task.Order.Operation||Task.Order.Sender!=p.Sender)return false;
                var cancelled=Task.Order;cancelled.Revision=p.Revision;Task.Order=cancelled;
                Task.Phase=FleetTaskPhase.Returning;Task.Reason=FleetTaskReason.Cancelled;Reply(p,Task.Phase,Task.Reason,now);return true;
            }
            if(operations.Count>=1024){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.StaleRevision,now);return false;}
            revisions[p.Sender]=p.Revision;operations[p.Operation]=Math.Max(p.ExpiresAt,now+120);
            if(p.Order==FleetOrderKind.ConfigureFormation)
            {
                if(p.TaskGroup==FleetTaskGroup.ElectronicWarfare||p.TaskGroup==FleetTaskGroup.EarlyWarningCommand||p.TaskGroup==FleetTaskGroup.Logistics||p.FormationRoot==Guid.Empty||p.Leader==Guid.Empty||p.FormationIndex<0||p.FormationSize<=p.FormationIndex||!Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite(p.FormationOffset))||!Enum.IsDefined(typeof(FleetTaskGroup),p.TaskGroup)||!Enum.IsDefined(typeof(FleetFormationShape),p.FormationShape))
                {Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                ApplyFormationConfiguration(p,now);Reply(p,FleetTaskPhase.Completed,FleetTaskReason.None,now);return true;
            }
            if(p.Order==FleetOrderKind.AssignMission)
            {
                if(!Enum.IsDefined(typeof(FleetTaskGroup),p.TaskGroup)||p.TaskGroup==FleetTaskGroup.ElectronicWarfare||p.TaskGroup==FleetTaskGroup.EarlyWarningCommand||p.TaskGroup==FleetTaskGroup.Logistics||!Enum.IsDefined(typeof(FleetMissionGeometry),p.MissionGeometry)||!Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite(p.MissionPoint))||!Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite(p.MissionDirection)))
                {Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                bool legalGeometry=p.TaskGroup switch{FleetTaskGroup.Strike or FleetTaskGroup.Escort or FleetTaskGroup.AirDefenseSuppression=>p.MissionGeometry==FleetMissionGeometry.Target,FleetTaskGroup.AirSuperiority=>p.MissionGeometry==FleetMissionGeometry.Area||p.MissionGeometry==FleetMissionGeometry.Point,FleetTaskGroup.Reconnaissance=>p.MissionGeometry==FleetMissionGeometry.Area||p.MissionGeometry==FleetMissionGeometry.PointDirection,_=>false};
                if(!legalGeometry){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                if(p.MissionGeometry==FleetMissionGeometry.Target&&!p.Target.TrackId.IsValid&&p.MissionProtectedMember==Guid.Empty){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.MissingObservation,now);return false;}
                if(p.MissionGeometry==FleetMissionGeometry.PointDirection&&Unity.Mathematics.math.lengthsq(p.MissionDirection)<.001f){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                if(p.MissionGeometry==FleetMissionGeometry.Area&&(p.MissionSectors.Length==0||!float.IsFinite(p.MissionCellSize)||p.MissionCellSize<=0||!Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite(p.MissionGridOrigin))||!Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite(p.MissionGridRotation.value))||Unity.Mathematics.math.abs(Unity.Mathematics.math.length(p.MissionGridRotation.value)-1)>.01f))
                {Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                foreach(var sector in p.MissionSectors)if(sector.x>sector.z||sector.y>sector.w||Unity.Mathematics.math.any(Unity.Mathematics.math.abs(sector)>1000000)){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                TaskGroup=p.TaskGroup;
            }
            if(p.Order==FleetOrderKind.SetCruiseSpeed){if(!float.IsFinite(p.CruiseSpeed)||p.CruiseSpeed<0||p.CruiseSpeed>MaxCruiseSpeed){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}CruiseSpeed=p.CruiseSpeed;CruiseCommandActive=true;Reply(p,FleetTaskPhase.Completed,FleetTaskReason.None,now);return true;}
            if(p.Order==FleetOrderKind.SetAttackMode){Mode=p.Mode;Reply(p,FleetTaskPhase.Completed,FleetTaskReason.None,now);return true;}
            if(p.Order==FleetOrderKind.SetRadarControl)
            {if(!SetLocalRadarControl(p.RadarControl)){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedRadarMode,now);return false;}Reply(p,FleetTaskPhase.Completed,FleetTaskReason.None,now);return true;}
            if(p.Order==FleetOrderKind.Move)
            {
                if(p.Waypoints.Length==0){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
                foreach(var point in p.Waypoints)if(!Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite(point))){Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.UnsupportedMovement,now);return false;}
            }
            if(p.WholeFleet&&(p.Order==FleetOrderKind.Move||p.Order==FleetOrderKind.Return)&&HasTask&&Task.Phase!=FleetTaskPhase.Returning&&(Task.Order.Order==FleetOrderKind.Attack||Task.Order.Order==FleetOrderKind.AttackWithWeapon||Task.Order.Order==FleetOrderKind.AssignMission&&Task.Order.TaskGroup==FleetTaskGroup.Strike))
            {Reply(p,FleetTaskPhase.Rejected,FleetTaskReason.ActiveAttackProtected,now);return false;}
            if(Task!=null&&!Task.Terminal)Reply(Task.Order,FleetTaskPhase.Cancelled,FleetTaskReason.Cancelled,now);
            Task=new MechFleetTask{Order=p,Phase=p.Order==FleetOrderKind.Return?FleetTaskPhase.Returning:FleetTaskPhase.Acquiring,Detached=Leader!=Member&&!p.WholeFleet,LastObservation=p.Target.LastUpdateTime};
            Reply(p,Task.Phase,FleetTaskReason.None,now);return true;
        }
        public static int CompareClaim(FleetPacket a,FleetPacket b)
        {int c=a.EstimatedSeconds.CompareTo(b.EstimatedSeconds);return c!=0?c:a.Sender.CompareTo(b.Sender);}
        public static bool TrySelectDefenseClaim(IEnumerable<FleetPacket> claims,Func<Guid,bool> sameContact,double now,out FleetPacket winner)
        {
            winner=default;bool any=false;
            foreach(var c in claims)
            {
                if(c.LeaseUntil<=now||!sameContact(c.Contact))continue;
                if(!any||c.Kind==FleetPacketKind.DefenseCommitted&&winner.Kind!=FleetPacketKind.DefenseCommitted||c.Kind==winner.Kind&&CompareClaim(c,winner)<0){winner=c;any=true;}
            }
            return any;
        }
        public bool DefenseReservedByOther(Guid contact,double now)=>Claims.TryGetValue(contact,out var c)&&c.LeaseUntil>now&&c.Sender!=Member;
        public bool HasTask=>Task!=null&&!Task.Terminal;
        public bool AllowsOffensiveMissile(Guid weapon)=>Mode==FleetAttackMode.Free || HasTask&&Task.Phase!=FleetTaskPhase.Returning&&(Task.Order.Order==FleetOrderKind.AttackWithWeapon&&Task.Order.Weapon==weapon||(Task.Order.Order==FleetOrderKind.Attack||Task.Order.Order==FleetOrderKind.AssignMission&&(Task.Order.TaskGroup==FleetTaskGroup.Strike||Task.Order.TaskGroup==FleetTaskGroup.AirSuperiority||Task.Order.TaskGroup==FleetTaskGroup.AirDefenseSuppression))&&Task.Order.AuthorizedWeaponSelection);
        public bool AllowsWeapon(Guid weapon)=>!HasTask||Task.Phase==FleetTaskPhase.Returning||Task.Order.Order!=FleetOrderKind.AttackWithWeapon||Task.Order.Weapon==weapon;
        public void SetPhase(FleetTaskPhase phase,FleetTaskReason reason,double now)
        {if(Task==null||Task.Terminal||Task.Phase==phase&&Task.Reason==reason)return;Task.Phase=phase;Task.Reason=reason;if(phase==FleetTaskPhase.Completed)Task.Detached=false;Reply(Task.Order,phase,reason,now);}
        public void Tick(double now)
        {
            var remove=new List<Guid>();foreach(var x in Claims)if(x.Value.LeaseUntil<=now)remove.Add(x.Key);foreach(var id in remove)Claims.Remove(id);
            remove.Clear();foreach(var x in operations)if(x.Value<now)remove.Add(x.Key);foreach(var id in remove){operations.Remove(id);replies.Remove(id);}
            remove.Clear();foreach(var x in Members)if(x.Value.ExpiresAt+30<now)remove.Add(x.Key);foreach(var id in remove)Members.Remove(id);
            remove.Clear();foreach(var x in Capabilities)if(x.Value.ExpiresAt+30<now)remove.Add(x.Key);foreach(var id in remove)Capabilities.Remove(id);
            if(HasTask&&Task.Phase!=FleetTaskPhase.Returning&&now>(Task.Order.TaskDeadline>0?Task.Order.TaskDeadline:Task.Order.ExpiresAt))SetPhase(FleetTaskPhase.Returning,FleetTaskReason.Expired,now);
        }
        private void Reply(FleetPacket order,FleetTaskPhase phase,FleetTaskReason reason,double now)
        {var response=new FleetPacket{Kind=FleetPacketKind.Acknowledgement,Sender=Member,Recipient=order.Sender,Fleet=Fleet,Leader=Leader,Operation=order.Operation,Revision=order.Revision,Phase=phase,Reason=reason,ObservedAt=now,ExpiresAt=now+3};if(operations.ContainsKey(order.Operation))replies[order.Operation]=response;Feedback?.Invoke(response);}
    }
}
