using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public readonly struct MechEquipmentWeaponSnapshot
    {
        public readonly string SlotId;
        public readonly Guid InstanceId;
        public readonly MechWeaponRole Role;
        public readonly int Rounds, Magazines;
        public readonly bool Enabled, Reloading, Pending;
        public readonly float HeatMj, NextFireTime;
        public MechEquipmentWeaponSnapshot(string slot, Guid instance, MechWeaponRole role, int rounds, int magazines,
            bool enabled, bool reloading, bool pending, float heat, float nextFire)
        { SlotId=slot; InstanceId=instance; Role=role; Rounds=rounds; Magazines=magazines; Enabled=enabled;
          Reloading=reloading; Pending=pending; HeatMj=heat; NextFireTime=nextFire; }
    }
    public sealed partial class MechTestWeaponSystem : IMechActionExecutor
    {
        private sealed class SupportedMissile
        { public MechTestProjectile Projectile; public MechActionProposal Launch; }
        private readonly List<SupportedMissile> supportedMissiles = new List<SupportedMissile>();
        private readonly List<MechActionFeedback> supportEvents = new List<MechActionFeedback>();
        public void CopySupportEvents(List<MechActionFeedback> output) { output.Clear(); output.AddRange(supportEvents); }
        public int CancelMissileSupport(string slotId)
        {
            int cancelled = 0;
            foreach (var support in supportedMissiles)
                if (support.Launch.Slot.Id == slotId && StopSupport(support, "SupportCancelled")) cancelled++;
            return cancelled;
        }
        private bool StopSupport(SupportedMissile support, string reason)
        {
            if (support.Projectile == null || !support.Projectile.HasExternalGuidanceSupport || !support.Projectile.StopExternalGuidanceSupport()) return false;
            if (supportEvents.Count >= 512) supportEvents.RemoveAt(0);
            supportEvents.Add(new MechActionFeedback(support.Launch, MechActionPhase.Cancelled, Time.timeAsDouble, reason)); return true;
        }
        public int CancelAllMissileSupport()
        { int count=0;foreach(var support in supportedMissiles)if(StopSupport(support,"SupportComponentFailure"))count++;return count; }
        public MechDefenseExtensions DefenseExtensions => GetComponent<MechDefenseExtensions>() ?? gameObject.AddComponent<MechDefenseExtensions>();
        internal bool SubmitDefenseExtension(MechAssemblyRecord record,ObserverTrackRef target)
        {
            var profile=record.part.DefenseExtension;
            double approachWait=0;var radar=GetComponent<MechRadarController>();
            if(radar!=null&&radar.TryGetMissileTrack(target.TrackId,out var contact))
            {
                var p=contact.PredictPosition(radar.SimTimeSeconds);var v=contact.PredictVelocity(radar.SimTimeSeconds);
                var relative=new Vector3((float)p.x,(float)p.y,(float)p.z)-transform.position;
                var motion=GetComponent<MechFlightMotionController>();var relativeVelocity=new Vector3((float)v.x,(float)v.y,(float)v.z)-(motion!=null?motion.Velocity:Vector3.zero);
                float closing=-Vector3.Dot(relativeVelocity,relative.normalized);
                if(closing>1)approachWait=Math.Min(8,Math.Max(0,(relative.magnitude-profile.maximumRangeMeters)/closing));
            }
            return Actions.Submit(new MechActionProposal(new MechActionId(MechActionSource.AutomaticDefense,++actionSequences[(int)MechActionSource.AutomaticDefense]),
                record.InstanceId,new MechEquipmentSlot(record.SlotId,record.slot),MechActionKind.Fire,target,
                Time.timeAsDouble+approachWait+Math.Max(.1,profile.reactionSeconds)+Math.Max(1,profile.maximumFlightSeconds),profile.energyPerShotMj,0,MechEmissionRequirement.Radar),Time.timeAsDouble);
        }
        private MechPartDamageController partDamage;
        private readonly TrackSnapshot[] automaticSafetyContacts=new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private string automaticFireWithholdReason;
        public float GetEquipmentEfficiency(MechEquipmentSlot slot)
        {
            if(MechSpecialistKits.ShoulderBlocked(runtimeState,ResolveEquipmentSlot(slot)))return 0;
            if(partDamage==null)partDamage=GetComponent<MechPartDamageController>();
            return partDamage!=null?partDamage.GetEquipmentEfficiency(ResolveEquipmentSlot(slot).Id):1;
        }
        private float RadiatorEfficiency=>GetComponent<MechPartDamageController>()?.RoleEfficiency(MechPartSlot.Radiator)??1;
        private MechActionArbiter actionArbiter;
        private readonly long[] actionSequences = new long[3];
        private bool committingAction;
        private int committedProjectileCount;
        private MechActionProposal currentCommit;
        public MechActionArbiter Actions {get{if(actionArbiter==null){actionArbiter=new MechActionArbiter(this);actionArbiter.FeedbackReported+=OnEquipmentFeedback;}return actionArbiter;}}
        private string EventSlotName(MechEquipmentSlot slot)=>slot.LegacyRole switch{MechPartSlot.LeftHandWeapon=>"左手",MechPartSlot.RightHandWeapon=>"右手",MechPartSlot.LeftBackWeapon=>"左肩",MechPartSlot.RightBackWeapon=>"右肩",MechPartSlot.Extension or MechPartSlot.Extension2=>"APS/扩展组件",_=>runtimeState?.FindCompiledSlot(slot.Id)?.displayName??slot.Id};
        private void OnEquipmentFeedback(MechActionFeedback feedback)
        {
            if(feedback.Id.Source!=MechActionSource.Player&&!IsOwnship())return;
            var log=GetComponent<MechOwnshipEventLog>();if(log==null&&isActiveAndEnabled)log=MechOwnshipEventLog.Ensure(gameObject);if(log==null)return;
            var slot=ResolveEquipmentSlot(new MechEquipmentSlot(feedback.SlotId));var record=FindEquipmentRecord(slot);
            log.Record(feedback,EventSlotName(slot),record?.part?.WeaponPerformance.IsGuidedMissile==true);
        }
        private bool RejectEquipmentRequest(MechEquipmentSlot slot,MechActionKind kind,MechActionSource source,string reason)
        {if(source==MechActionSource.Player)MechOwnshipEventLog.Ensure(gameObject).AddEvent("reject/"+slot.Id+"/"+reason,EventSlotName(slot)+"：未"+(kind==MechActionKind.Reload?"换弹":"发射")+" · "+MechOwnshipEventLog.Reason(reason),true);return false;}
        private string lastMissileReleaseFailure;
        private bool RejectMissileCommit(string reason){lastMissileReleaseFailure=reason;return false;}
        public void ConfigureEmissionPolicy(MechEmissionMode mode, bool allowAutomaticDefense)
        { Actions.Emissions.Mode = mode; Actions.Emissions.AllowAutomaticDefense = allowAutomaticDefense; }
        private MechActionSource DefaultCommandSource => IsOwnship() ? MechActionSource.Player : MechActionSource.AI;
        public bool CommandFireSlot(string slotId, MechActionSource source, ObserverTrackRef target = default)
            => QueueEquipmentAction(new MechEquipmentSlot(slotId), MechActionKind.Fire, source, target);
        public bool CommandReloadSlot(string slotId, MechActionSource source)
            => QueueEquipmentAction(new MechEquipmentSlot(slotId), MechActionKind.Reload, source);
        public bool CancelEquipmentAction(string slotId)
        {
            var record = FindEquipmentRecord(new MechEquipmentSlot(slotId));
            return record != null && Actions.TryGetPending(record.InstanceId, out var id) && Actions.Cancel(id, Time.timeAsDouble);
        }
        public int CommandFireGroup(string group, MechActionSource source)
        {
            int accepted = 0; var loadout = runtimeState?.CurrentLoadout;
            if (loadout == null || string.IsNullOrWhiteSpace(group)) return 0;
            foreach (var slot in runtimeState.CompiledSlots)
                if (slot.weaponGroup == group && CommandFireSlot(slot.id, source)) accepted++;
            return accepted;
        }
        private bool QueueEquipmentAction(MechEquipmentSlot slot, MechActionKind kind, MechActionSource source,
            ObserverTrackRef targetRef = default, bool hasEstimate = false, Vector3 position = default, Vector3 velocity = default)
        {
            if (!Enum.IsDefined(typeof(MechActionSource), source)) return false;
            slot = ResolveEquipmentSlot(slot);
            if(source==MechActionSource.Player)MechOwnshipEventLog.Ensure(gameObject);
            if(!AreWeaponsOperational())return RejectEquipmentRequest(slot,kind,source,engineController!=null&&engineController.StartupActive?"EngineStartup":"WeaponsUnavailable");
            if(MechSpecialistKits.ShoulderBlocked(runtimeState,slot))return RejectEquipmentRequest(slot,kind,source,"ShoulderBlocked"); var record = FindEquipmentRecord(slot);
            if (record == null || record.instance == null || !record.instance.activeInHierarchy || record.part == null ||
                !record.part.WeaponPerformance.IsConfigured) return RejectEquipmentRequest(slot,kind,source,"EquipmentUnavailable");
            if(Actions.HasPending(record.InstanceId))return RejectEquipmentRequest(slot,kind,source,"PendingAction");
            RefreshHandWeaponState(slot); RefreshShoulderAmmunition(slot);
            var performance = GetEquippedWeaponPerformance(slot);
            bool missile = performance.IsGuidedMissile;
            if (kind == MechActionKind.Fire && !targetRef.IsValid && radarController != null)
            {
                var track = performance.role == MechWeaponRole.InterceptorMissile ? interceptorTarget.TrackId :
                    performance.role == MechWeaponRole.PointDefenseCannon ? shaoWeiPointDefenseTarget.TrackId :
                    hasEstimate ? pointDefenseTarget.TrackId : missile ? radarController.SttTrackId : default;
                if (missile && performance.role == MechWeaponRole.AirToAirMissile && radarController.CandidateTrackId.IsValid) track = radarController.CandidateTrackId;
                if (track.IsValid) targetRef = radarController.Reference(track);
            }
            var state = GetInstanceState(slot);
            state.HasExternalAim = hasEstimate; state.AimPosition = position; state.AimVelocity = velocity;
            ReleaseRequirements(performance,targetRef.TrackId,out int launchChannels,out var launchEmissions);
            double actionLifetime = kind == MechActionKind.Reload
                ? GetHandReloadDuration(state.Hand) + 2.0
                : missile ? 8.0 : 2.0;
            var proposal = new MechActionProposal(new MechActionId(source, ++actionSequences[(int)source]), record.InstanceId, slot, kind,
                targetRef, Time.timeAsDouble + actionLifetime, kind == MechActionKind.Fire ? performance.energyPerShotMj : 0,
                kind == MechActionKind.Fire ? launchChannels : 0,
                kind == MechActionKind.Fire ? launchEmissions : MechEmissionRequirement.None);
            bool submitted=Actions.Submit(proposal, Time.timeAsDouble);
            if(submitted&&source==MechActionSource.AutomaticDefense)GetComponent<MechFleetController>()?.NotifyDefenseProposed(proposal);
            return submitted;
        }
        MechActionBudget IMechActionExecutor.AvailableBudget
        {
            get
            {
                var radarStatus = radarController != null ? radarController.GetStatusSnapshot() : default;
                return new MechActionBudget(flightMotion != null ? flightMotion.CurrentManeuverEnergy : 0,
                    Math.Max(0, radarStatus.GuidanceChannelCapacity - radarStatus.GuidanceChannelsUsed));
            }
        }
        bool IMechActionExecutor.Check(MechActionProposal p, out string reason)
        {
            reason = "EquipmentUnavailable"; var record = FindEquipmentRecord(p.Slot);
            if (record == null || record.InstanceId != p.InstanceId || record.SlotId != p.Slot.Id || !EquipmentIsEnabled(record) ||
                !isActiveAndEnabled || !AreWeaponsOperational() || (ownCombatTarget != null && !ownCombatTarget.IsAlive)) return false;
            if (GetEquipmentEfficiency(p.Slot)<=0) { reason="ComponentFailed"; return false; }
            if (DefenseExtensions.Handles(FindEquipmentRecord(p.Slot))) return DefenseExtensions.Check(p,out reason);
            var state = GetInstanceState(p.Slot); var performance = GetEquippedWeaponPerformance(p.Slot);
            if(p.Kind==MechActionKind.Fire && !BankCanRelease(p.Slot,p.Target.TrackId)){reason=IsMissileMagazineReloading(p.Slot)?"MissileMagazineReloading":"NoCompatibleLoadedMissileBank";return false;}
            performance=BankPerformance(p.Slot,p.Target.TrackId)??performance;
            if (performance == null || !performance.IsConfigured) return false;
            if(p.Kind==MechActionKind.Fire&&NeedsInterceptorUplink(performance)&&
                (radarController==null||!radarController.DataLinkTransmitEnabled||radarController.GuidanceUplinksUsed>=radarController.GuidanceUplinkCapacity))
            {reason="InterceptorGuidanceUnavailable";return false;}
            var fleet=GetComponent<MechFleetController>();
            if(p.Kind==MechActionKind.Fire && p.Id.Source==MechActionSource.AI && fleet!=null && !fleet.AllowsWeapon(p.InstanceId,performance.role,p.Target.TrackId,out reason))return false;
            float expectedEnergy = p.Kind == MechActionKind.Fire ? performance.energyPerShotMj : 0;
            ReleaseRequirements(performance,p.Target.TrackId,out int expectedGuidance,out var expectedEmission);
            if(p.Kind!=MechActionKind.Fire){expectedGuidance=0;expectedEmission=MechEmissionRequirement.None;}
            if(p.Kind==MechActionKind.Fire&&performance.role==MechWeaponRole.AirToAirMissile)
            {
                if(radarController==null||!radarController.PreviewMissileRelease(p.Target.TrackId,BuildMissileKinematics(performance.missileFlightProfile),performance.supportsPassiveRfHoming,out var releaseTrack,out var releaseSolution,out _,out reason,PreferPassiveRelease(performance)))return false;
                if(!CheckMissileReleaseGeometry(performance,releaseTrack,releaseSolution.InterceptPoint,out reason))return false;
            }
            if (p.EnergyMj != expectedEnergy || (p.GuidanceUnits < expectedGuidance || p.GuidanceUnits > 1) || p.Emissions != expectedEmission)
            { reason = "InvalidResourceDeclaration"; return false; }
            if (p.Target.TrackId.IsValid && !p.Target.IsValid) { reason = "InvalidObserverScope"; return false; }
            if (p.Target.IsValid)
            {
                reason = "StaleObserverTrack";
                if (radarController == null || radarController.ObserverScope != p.Target.ObserverScope) return false;
                bool exists = radarController.TryGetTrack(p.Target, out _) || radarController.TryGetMissileTrack(p.Target.TrackId, out _);
                if (!exists) return false;
                if(p.Kind==MechActionKind.Fire && radarController.TryGetTrack(p.Target,out var identityTrack) && identityTrack.ObjectClass!=ACunicombat.Sensing.Contracts.TrackObjectClass.Missile)
                {
                    var identity=GetComponent<MechIdentityController>();
                    if(identity!=null&&!identity.CanAttackPlatform(identityTrack)){reason="IdentityRulesOfEngagement";return false;}
                    if(p.Id.Source==MechActionSource.AI&&MechAutomaticTargetSafety.NeedsDisambiguation(identityTrack))
                    {
                        int n=radarController.CopyTracks(automaticSafetyContacts);
                        if(MechAutomaticTargetSafety.OverlapsFriendly(identityTrack,automaticSafetyContacts,n,radarController.SimTimeSeconds,out var conflictingTrack))
                        {reason=conflictingTrack.IsValid?"FriendlyContactAmbiguity:target="+identityTrack.TrackId+";friendly="+conflictingTrack:"InvalidAutomaticTargetUncertainty";return false;}
                    }
                }
            }
            if(p.Id.Source==MechActionSource.AutomaticDefense)
            {
                if(radarController==null||!p.Target.IsValid||!radarController.TryGetMissileTrack(p.Target.TrackId,out var threat)||threat.IsFriendly){reason="SelfDefenseNotAuthorized";return false;}
                bool preparing=performance.role==MechWeaponRole.InterceptorMissile&&state.ActiveCommand.Equals(p.Id);
                if(fleet!=null&&fleet.Configured){if(!fleet.MayDefend(threat,out reason,preparing))return false;}
                else if(!threat.IsWeaponsQuality||!threat.Incoming){reason="SelfDefenseNotAuthorized";return false;}
            }
            if(p.Id.Source == MechActionSource.AutomaticDefense && performance.role == MechWeaponRole.PointDefenseCannon &&
                (!radarController.TryGetMissileTrack(p.Target.TrackId,out var localThreat)||!ActiveDefense.Accepts(in localThreat,radarController.SimTimeSeconds)))
            {reason="DefensePolicyRejected";return false;}
            if (performance.IsGuidedMissile && !p.Target.IsValid) { reason = "MissingTrack"; return false; }
            if (p.Kind == MechActionKind.Reload)
            { if (state.ActiveCommand.Equals(p.Id) && state.Hand.ArbitratedReload) { reason = null; return true; }
              reason = "ReloadUnavailable"; return !performance.beltFed && !performance.IsGuidedMissile && performance.role != MechWeaponRole.PointDefenseCannon && !state.Hand.Reloading && state.Hand.MagazinesRemaining > 1 && state.Hand.RoundsRemaining < performance.magazineCapacity; }
            reason = "AmmoOrCooldown";
            if (performance.IsGuidedMissile)
            {
                if(state.Rounds<=0||state.Magazines<=0){reason="NoAmmo";return false;}if(Time.time<state.WeaponCooldownUntil){reason="CoolingDown";return false;}
                if (state.Deployment != null && !state.Deployment.CanRequest && !state.ActiveCommand.Equals(p.Id)) { reason = "DeployingOrRecovering"; return false; }
            }
            else if (performance.role == MechWeaponRole.PointDefenseCannon)
            { if(state.Defense.Reloading){reason="Reloading";return false;}if(state.Defense.Overheated){reason="Overheated";return false;}if(state.Defense.RoundsRemaining<=0){reason="NoAmmo";return false;}if(Time.time<state.Defense.NextFireTime){reason="CoolingDown";return false;} }
            else {if(state.Hand.Reloading){reason="Reloading";return false;}if(state.Hand.Overheated){reason="Overheated";return false;}if(state.Hand.RoundsRemaining<=0){reason="NoAmmo";return false;}if(Time.time<state.Hand.NextFireTime){reason="CoolingDown";return false;}}
            reason = null; return true;
        }
        bool IMechActionExecutor.Begin(MechActionProposal p, out string reason)
        {
            if(DefenseExtensions.Handles(FindEquipmentRecord(p.Slot)))return DefenseExtensions.Begin(p,out reason);
            reason = null; var state = GetInstanceState(p.Slot); state.ActiveCommand = p.Id;
            var banks=EnsureMissileBanks(p.Slot);
            if(p.Kind==MechActionKind.Fire&&banks!=null)
            {
                state.ActiveMissileBank=SelectMissileBankForTarget(banks,p.Target.TrackId);
                if(state.ActiveMissileBank<0){reason="NoCompatibleLoadedMissileBank";return false;}
            }
            if (p.Kind == MechActionKind.Reload)
            {
                BeginHandReload(p.Slot, state.Hand, GetWeaponPosition(p.Slot));
                state.Hand.ArbitratedReload = true; return state.Hand.Reloading;
            }
            if (!GetEquippedWeaponPerformance(p.Slot).IsGuidedMissile) return true;
            if (state.Deployment == null) state.Deployment = new MechWeaponDeployment(FindEquipmentRecord(p.Slot));
            if (state.Deployment.Request(Time.timeAsDouble)) return true;
            reason = "DeploymentUnavailable"; return false;
        }
        bool IMechActionExecutor.Ready(MechActionProposal p)
        {
            if(DefenseExtensions.Handles(FindEquipmentRecord(p.Slot)))return DefenseExtensions.Ready(p);
            var state = GetInstanceState(p.Slot); var performance = GetEquippedWeaponPerformance(p.Slot);
            if (p.Kind == MechActionKind.Reload) return Time.time >= state.Hand.ReloadCompleteTime;
            if (performance.IsGuidedMissile)
            {
                if(p.Id.Source==MechActionSource.AutomaticDefense&&performance.role==MechWeaponRole.InterceptorMissile)
                {var fleet=GetComponent<MechFleetController>();if(!radarController.TryGetMissileTrack(p.Target.TrackId,out var threat)||!threat.IsWeaponsQuality||threat.IsFriendly||fleet!=null&&!fleet.MayDefend(threat,out _))return false;}
                return state.Deployment != null && state.Deployment.Ready;
            }
            if (performance.IsAutomatic && performance.role != MechWeaponRole.PointDefenseCannon)
                return IsAutomaticWeaponSpunUp(state.Hand, performance, Time.time);
            return true;
        }
        bool IMechActionExecutor.Commit(MechActionProposal p, out string reason)
        {
            if(DefenseExtensions.Handles(FindEquipmentRecord(p.Slot)))return DefenseExtensions.Commit(p,out reason);
            lastMissileReleaseFailure=null;reason = "ExecutionRejected";
            automaticFireWithholdReason=null;
            var state = GetInstanceState(p.Slot); var performance = GetEquippedWeaponPerformance(p.Slot);
            if (p.Target.IsValid && performance.role != MechWeaponRole.InterceptorMissile && performance.role != MechWeaponRole.PointDefenseCannon && performance.role != MechWeaponRole.AirToAirMissile && !state.HasExternalAim)
                if (!(GetComponent<MechFleetController>()?.PlayerAutopilotActive==true&&!radarController.TrackOperationMode ? radarController.TryAssignAutopilotFireControlTrack(p.Target.TrackId) : radarController.TryAssignFireControlTrack(p.Target.TrackId))) { reason = "TrackChanged"; return false; }
            bool committed = false; committingAction = true; currentCommit = p;
            try
            {
                if (p.Kind == MechActionKind.Reload)
                {
                    state.Hand.MagazinesRemaining--;
                    state.Hand.RoundsRemaining = GetHandMagazineCapacity(state.Hand);
                    state.Hand.Reloading = state.Hand.ArbitratedReload = false;
                    committed = true;
                }
                else if (performance.IsGuidedMissile)
                {
                    if (performance.role == MechWeaponRole.InterceptorMissile)
                    {
                        if (!radarController.TryGetMissileTrack(p.Target.TrackId, out interceptorTarget)){reason="StaleObserverTrack";return false;}
                        interceptorTargetManuallyDesignated = false;
                    }
                    committed = CommandFireMissile(p.Slot);
                    if (committed) { state.Deployment.Consume(); state.WeaponCooldownUntil=Time.time+(1-GetEquipmentEfficiency(p.Slot))*2f; }
                }
                else if (performance.role == MechWeaponRole.PointDefenseCannon)
                {
                    if (!radarController.TryGetMissileTrack(p.Target.TrackId, out shaoWeiPointDefenseTarget)) return false;
                    int before = committedProjectileCount;
                    UpdateShaoWeiPointDefenseSlot(p.Slot, state.Defense, HasShaoWeiPredeploymentWarning());
                    committed = committedProjectileCount > before;
                }
                else committed = TryFireEquippedHandWeapon(p.Slot, state.HasExternalAim, state.AimPosition, state.AimVelocity,
                    p.Target.IsValid && !state.HasExternalAim ? p.Target.TrackId : default);
                if (committed && p.Kind == MechActionKind.Fire && performance.role == MechWeaponRole.AutomaticCannon && state.Hand.TriggerSequenceActive)
                {
                    state.Hand.TriggerSequenceShotCount++;
                    state.Hand.QueuedTriggerShots = Mathf.Max(0, state.Hand.QueuedTriggerShots - 1);
                    int burst = Mathf.Max(0, performance.heldBurstShotCount);
                    if (burst > 0 && state.Hand.TriggerSequenceShotCount % burst == 0)
                        state.Hand.NextHeldBurstTime = Mathf.Max(Time.time, state.Hand.NextFireTime) + Mathf.Max(0, performance.heldBurstPauseSeconds);
                }
                if (committed && p.EnergyMj > 0 && !flightMotion.TrySpendEquipmentEnergy(p.EnergyMj))
                    throw new InvalidOperationException("Reserved equipment energy changed during atomic commit.");
                if (committed && p.Kind == MechActionKind.Fire)
                {
                    GetComponent<MechThermalSignature>()?.NotifyWeaponFired(p.Slot);
                    GetComponent<MechAiTargetController>()?.NotifyWeaponCommitted(p.InstanceId,p.Slot.Id,performance.role);
                    GetComponent<MechFleetController>()?.NotifyShot(p.InstanceId,p.Target,performance.role);
                }
                reason = committed ? "Committed" : lastMissileReleaseFailure??automaticFireWithholdReason??"ExecutionRejected"; return committed;
            }
            finally { committingAction = false; currentCommit = null; if (committed){state.ActiveCommand = default;state.ActiveMissileBank=-1;} }
        }
        void IMechActionExecutor.Cancel(MechActionProposal p)
        {
            DefenseExtensions.Cancel(p);
            if(DefenseExtensions.Handles(FindEquipmentRecord(p.Slot)))return;
            var state = GetInstanceState(p.Slot);
            if (state.InstanceId != p.InstanceId) return;
            ResetTriggerSequence(state.Hand);
            if (!state.ActiveCommand.Equals(p.Id)) return;
            state.Deployment?.Cancel();
            if (state.Hand.ArbitratedReload)
            { state.Hand.ArbitratedReload = state.Hand.Reloading = false; state.Hand.ReloadCompleteTime = float.NegativeInfinity; }
            state.ActiveCommand = default;
            state.ActiveMissileBank=-1;
        }
        private void UpdateExtraEquipmentInput(MechPlayerInputFrame frame)
        {
            var loadout = runtimeState?.CurrentLoadout; if (loadout?.Platform == null) return;
            foreach (var definition in runtimeState.CompiledSlots)
            {
                if (definition.legacyRole != MechPartSlot.None || string.IsNullOrEmpty(definition.weaponGroup)) continue;
                bool held = false, edge = false, reload = false;
                switch (definition.weaponGroup)
                {
                    case "primary": held = frame.PrimaryFireHeld; edge = frame.PrimaryFireEdge; reload = frame.PrimaryReloadEdge; break;
                    case "secondary": held = frame.SecondaryFireHeld; edge = frame.SecondaryFireEdge; reload = frame.SecondaryReloadEdge; break;
                    case "left-shoulder": edge = frame.LeftShoulderFireEdge; break;
                    case "right-shoulder": edge = frame.RightShoulderFireEdge; break;
                }
                var slot = new MechEquipmentSlot(definition.id); var p = GetEquippedWeaponPerformance(slot); if (p == null) continue;
                if (p.IsGuidedMissile) { if (edge) CommandFireSlot(definition.id, DefaultCommandSource); }
                else if (p.role != MechWeaponRole.PointDefenseCannon)
                { if (reload) CommandReloadSlot(definition.id, DefaultCommandSource); UpdateHandWeaponInput(slot, held, edge); UpdateHuBenAnimation(slot, held); }
            }
        }

        private bool IsWithinEquipmentArc(MechEquipmentSlot slot, Vector3 direction)
        {
            var loadout = runtimeState?.CurrentLoadout;
            if (loadout?.Platform == null) return true;
            var definition = runtimeState.FindCompiledSlot(ResolveEquipmentSlot(slot).Id);
            var record = FindEquipmentRecord(slot);
            if (definition == null || record?.parentMount == null || direction.sqrMagnitude < 0.000001f) return false;
            var local = Quaternion.Inverse(record.parentMount.rotation) * direction.normalized;
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Atan2(local.y, Mathf.Sqrt(local.x * local.x + local.z * local.z)) * Mathf.Rad2Deg;
            return Mathf.Abs(yaw) <= definition.yawLimitDegrees && Mathf.Abs(pitch) <= definition.pitchLimitDegrees;
        }

        private bool EquipmentIsEnabled(MechAssemblyRecord record)
        {
            for (int depth = 0; depth <= MechSlotGraph.MaxDepth; depth++)
            {
                if (record?.instance == null || !record.instance.activeInHierarchy) return false;
                var parent = runtimeState?.FindCompiledSlot(record.SlotId)?.parentSlotId;
                if (string.IsNullOrEmpty(parent)) return true;
                record = FindEquipmentRecord(new MechEquipmentSlot(parent));
            }
            return false;
        }

        private readonly List<InstanceWeaponState> deploymentTickScratch=new List<InstanceWeaponState>();
        private void TickEquipmentActions()
        {
            foreach (var support in supportedMissiles)
            {
                if (!Actions.Emissions.Allows(support.Launch)) StopSupport(support, "SupportEmissionDenied");
                else if (FindEquipmentRecord(support.Launch.Slot)?.InstanceId != support.Launch.InstanceId || !EquipmentIsEnabled(FindEquipmentRecord(support.Launch.Slot)) || GetEquipmentEfficiency(support.Launch.Slot)<=0) StopSupport(support, "SupportEquipmentUnavailable");
            }
            supportedMissiles.RemoveAll(x => x.Projectile == null || !x.Projectile.HasExternalGuidanceSupport);
            foreach(var state in instanceStates.Values)if(state.MissileBanks!=null){state.MissileBanks.Tick(Time.timeAsDouble);SyncMissileBankCounts(state);}
            deploymentTickScratch.Clear();deploymentTickScratch.AddRange(instanceStates.Values);
            foreach (var state in deploymentTickScratch)
                state.Deployment?.Tick(Time.timeAsDouble, (radarController != null && radarController.HasStt) ||
                    (state.Hand.Role == MechWeaponRole.InterceptorMissile && HasShaoWeiPredeploymentWarning()));
            if (Time.timeScale > 0) { DefenseExtensions.TickRequests(this); Actions.Tick(Time.timeAsDouble); }
        }
        private void OnDisable()
        {
            actionArbiter?.CancelAll(Time.timeAsDouble);
            foreach (var support in supportedMissiles) StopSupport(support, "SupportOwnerDisabled");
        }
        public bool TryEstimateFleetDefense(string slotId,MissileTrackSnapshot threat,out float seconds)
        {
            seconds=float.PositiveInfinity;var p=GetEquippedWeaponPerformance(new MechEquipmentSlot(slotId));if(p==null)return false;
            if(p.role==MechWeaponRole.InterceptorMissile && TryPeekInterceptorMuzzle(new MechEquipmentSlot(slotId),out var muzzle) && TrySolveInterceptorEngagement(threat,muzzle,out var intercept,new MechEquipmentSlot(slotId)))
            {seconds=intercept.FlightTimeSeconds;return true;}
            if(p.role==MechWeaponRole.PointDefenseCannon && IsShaoWeiThreatEligible(threat,transform.position))
            {seconds=Vector3.Distance(transform.position,(Vector3)(Unity.Mathematics.float3)threat.PredictPosition(radarController.SimTimeSeconds))/Mathf.Max(1,p.muzzleVelocityMetersPerSecond);return true;}
            return false;
        }
        public void CopyEquipmentWeaponSnapshots(List<MechEquipmentWeaponSnapshot> output)
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.WeaponInventory);
            output.Clear(); if (runtimeState == null) return;
            bool operational = isActiveAndEnabled && AreWeaponsOperational() && (ownCombatTarget==null||ownCombatTarget.IsAlive);
            foreach (var record in runtimeState.AssembledParts)
            {
                if (record?.part == null || !record.part.WeaponPerformance.IsConfigured) continue;
                var slot = new MechEquipmentSlot(record.SlotId, record.slot); var state = GetInstanceState(slot); var p = GetEquippedWeaponPerformance(slot);
                bool missile = p.IsGuidedMissile, defense = p.role == MechWeaponRole.PointDefenseCannon;
                output.Add(new MechEquipmentWeaponSnapshot(slot.Id, record.InstanceId, p.role,
                    missile ? state.Rounds : defense ? state.Defense.RoundsRemaining : state.Hand.RoundsRemaining,
                    missile ? state.Magazines : defense ? state.Defense.MagazinesRemaining : state.Hand.MagazinesRemaining,
                    operational && EquipmentIsEnabled(record) && GetEquipmentEfficiency(slot)>0,
                    missile ? IsMissileMagazineReloading(slot) : defense ? state.Defense.Reloading : state.Hand.Reloading, Actions.HasPending(record.InstanceId) || (state.Deployment != null && !state.Deployment.CanRequest),
                    defense ? state.Defense.HeatMj : state.Hand.HeatMj, missile ? state.WeaponCooldownUntil : defense ? state.Defense.NextFireTime : state.Hand.NextFireTime));
            }
        }
    }
}
