using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// L1 unit brain: a priority-preemptive state machine replaces the former
    /// single if/else Update chain. Perception (threat sweep, weapon inventory,
    /// target selection, fleet navigation) runs once per frame into a snapshot;
    /// each state only turns that snapshot into one input frame and attitude.
    /// </summary>
    public sealed partial class MechAiTargetController
    {
        public const int PriorityDisabled = 0, PriorityEvade = 1, PriorityEngage = 3, PriorityNavigate = 6, PrioritySearch = 9;

        /// <summary>Per-frame perception shared by all unit states.</summary>
        private struct Perception
        {
            public int WeaponCount;
            public bool HasTarget;
            public TrackSnapshot Target;
            public bool HasNavigation;
            public Vector3 NavigationDirection;
            public MechPlayerInputFrame NavigationFrame;
            public bool NavigationRequired;
            public MechAiEvasionPlan Evasion;
        }

        private Perception perception;
        private MechAiStateMachine brain;
        private MechAiState stateDisabled, stateEvade, stateEngage, stateNavigate, stateSearch;
        private MechAiEvasionMode evasionMode;
        private float nextCountermeasureTime;
        public string CurrentBrainState => brain?.CurrentName ?? "Null";
        public MechAiEvasionMode CurrentEvasionMode => evasionMode;
        public int BrainTransitionCount => brain?.TransitionCount ?? 0;

        private bool Alive => selfTarget == null || selfTarget.IsAlive;
        /// <summary>An observed defensive situation that needs an evasive frame now.</summary>
        private bool EvadeNow => incomingThreat && (!hasIncomingThreatObservation || perception.Evasion.Active ||
                                                    !perception.HasTarget && incomingThreatTime <= defensiveThreatTimeSeconds);

        private void BuildBrain()
        {
            brain = new MechAiStateMachine();
            stateDisabled = new DisabledState(this);
            stateEvade = new EvadeState(this);
            stateEngage = new EngageState(this);
            stateNavigate = new NavigateState(this);
            stateSearch = new SearchState(this);

            brain.AddAny(stateDisabled, () => !Alive, interrupt: true);
            brain.Add(stateDisabled, stateSearch, () => Alive);
            brain.AddAny(stateEvade, () => EvadeNow, interrupt: true);
            brain.Add(stateEvade, stateEngage, () => !EvadeNow && perception.HasTarget && !perception.NavigationRequired);
            brain.Add(stateEvade, stateNavigate, () => !EvadeNow && perception.HasNavigation && (perception.NavigationRequired || !perception.HasTarget));
            brain.Add(stateEvade, stateSearch, () => !EvadeNow);
            brain.AddAny(stateEngage, () => perception.HasTarget && !perception.NavigationRequired);
            brain.Add(stateEngage, stateNavigate, () => perception.HasNavigation && (perception.NavigationRequired || !perception.HasTarget));
            brain.Add(stateEngage, stateSearch, () => !perception.HasTarget);
            brain.AddAny(stateNavigate, () => perception.HasNavigation && (perception.NavigationRequired || !perception.HasTarget));
            brain.Add(stateNavigate, stateEngage, () => perception.HasTarget && !perception.NavigationRequired);
            brain.Add(stateNavigate, stateSearch, () => !perception.HasNavigation);
            brain.Add(stateSearch, stateEngage, () => perception.HasTarget);
            brain.Start(stateSearch, Time.time);
            brain.Changed += (from, to) => Fleet?.Log("ai_brain_state", Fleet.State.Task?.Order ?? default, from.Name + "->" + to.Name);
        }

        private void Update()
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.AI);
            if (input == null || motion == null)
            {
                input?.SubmitExternalFrame(default);
                motion?.SetYawTurnInput(0f);
                return;
            }
            if (brain == null) BuildBrain();
            Perceive();
            brain.Tick(Time.time);
        }

        private void Perceive()
        {
            if (!Alive) { perception = default; return; }
            UpdateMissileDefense();
            perception.WeaponCount = currentOffensiveWeaponCount = CopyOffensiveWeaponStatuses();
            perception.HasTarget = TryGetAirTargetTrack(perception.WeaponCount, out perception.Target);
            if (perception.HasTarget && ShouldAbandonChase(in perception.Target))
            {
                perception.HasTarget = false;
                selectedAirTrackId = default;
                hasLastAirTrack = false;
            }
            if (Fleet != null)
            {
                if (perception.HasTarget) Fleet.ReportEngagementTarget(in perception.Target);
                else Fleet.ClearEngagementTarget();
            }
            perception.Evasion = EvaluateEvasion();
            perception.HasNavigation = !stationaryTestMode && Fleet != null &&
                                       Fleet.TryNavigation(out perception.NavigationDirection, out perception.NavigationFrame);
            perception.NavigationRequired = perception.HasNavigation && Fleet.HasTask &&
                (MechAiRulePlanner.ShouldYieldCombatToNavigation(
                    Fleet.HasTask,
                    Fleet.State.Task.Phase == FleetTaskPhase.Returning,
                    Fleet.State.Task.Order.Order == FleetOrderKind.Move,
                    Fleet.HasSpatialMission && Fleet.TaskGroup == FleetTaskGroup.Escort) ||
                 Fleet.StrikeRequiresNavigation);
        }

        /// <summary>
        /// Shooter deconfliction (formation layer): a contact a closer member is
        /// already engaging scores worse, so free members spread across targets.
        /// It is a penalty, not a veto, so a lone target is still engaged.
        /// </summary>
        public const float DeconflictionScorePenalty = 2.5f;

        /// <summary>
        /// Drop a self-selected chase that is out of every weapon's reach and not
        /// closing. Commanded targets are never abandoned here; the task layer owns them.
        /// </summary>
        private bool ShouldAbandonChase(in TrackSnapshot track)
        {
            if (Fleet == null || Fleet.HasTask || perception.WeaponCount == 0) return false;
            var relative = radar.PredictTrackPosition(in track) - transform.position;
            float range = relative.magnitude;
            if (range < 1f) return false;
            float maximum = 0f;
            for (var i = 0; i < perception.WeaponCount; i++) maximum = Mathf.Max(maximum, weaponStatusScratch[i].MaximumRangeMeters);
            float closing = -Vector3.Dot(ToVector3(track.Velocity) - motion.Velocity, relative / range);
            return MechFormationDoctrine.ShouldAbandonChase(range, maximum, closing);
        }
        private float DeconflictionPenalty(in TrackSnapshot track)
            => Fleet != null && Fleet.FriendlyHoldsTarget(in track, radar.PredictTrackPosition(in track)) ? DeconflictionScorePenalty : 0f;

        private MechAiEvasionPlan EvaluateEvasion()
        {
            if (!incomingThreat || !hasIncomingThreatObservation) { evasionMode = MechAiEvasionMode.None; return default; }
            var threat = new MechAiMissileThreat(incomingThreatPosition, incomingThreatVelocity, incomingThreatTime);
            var away = (transform.position - incomingThreatPosition).normalized;
            var lateral = MechAiEvasionDoctrine.BeamDirection(incomingThreatVelocity, away, motion.Velocity, motion.ReferenceUp);
            // Escape speed the airframe can reach before impact (speed is not hard-capped in vacuum).
            float awayAcceleration = motion.EstimateTranslationAcceleration(away);
            float escapeSpeed = motion.Velocity.magnitude + awayAcceleration * Mathf.Min(incomingThreatTime, 10f);
            var plan = MechAiEvasionDoctrine.Evaluate(in threat, transform.position, motion.Velocity, motion.ReferenceUp,
                escapeSpeed,
                motion.EstimateTranslationAcceleration(lateral),
                evasionMode != MechAiEvasionMode.None);
            evasionMode = plan.Mode;
            return plan;
        }

        // ---------------------------------------------------------------- states

        private abstract class UnitState : MechAiState
        {
            protected readonly MechAiTargetController ai;
            protected UnitState(MechAiTargetController owner, int priority, float dwell) : base(priority, dwell) { ai = owner; }
            public override void Enter(float now) { base.Enter(now); ai.LastBrainDecision = Name; }
        }

        private sealed class DisabledState : UnitState
        {
            public DisabledState(MechAiTargetController o) : base(o, PriorityDisabled, 0f) { }
            public override void Tick(float now)
            {
                ai.combatState = CombatState.Disabled;
                ai.input.SubmitExternalFrame(default);
                ai.motion.SetYawTurnInput(0f);
                ai.motion.SetAimPitch(ai.motion.LogicalPitchDegrees);
            }
        }

        private sealed class EvadeState : UnitState
        {
            public EvadeState(MechAiTargetController o) : base(o, PriorityEvade, 0f) { }
            public override void Tick(float now) => ai.TickEvade();
            public override void Exit(float now) => ai.evasionMode = MechAiEvasionMode.None;
        }

        private sealed class EngageState : UnitState
        {
            public EngageState(MechAiTargetController o) : base(o, PriorityEngage, 0.6f) { }
            public override void Tick(float now)
            {
                if (ai.perception.HasTarget) ai.TickEngage(in ai.perception.Target);
                else IsFinished = true;
            }
        }

        private sealed class NavigateState : UnitState
        {
            public NavigateState(MechAiTargetController o) : base(o, PriorityNavigate, 0.4f) { }
            public override void Tick(float now) => ai.TickNavigate();
        }

        private sealed class SearchState : UnitState
        {
            public SearchState(MechAiTargetController o) : base(o, PrioritySearch, 0f) { }
            public override void Tick(float now) => ai.TickSearch();
        }

        public string LastBrainDecision { get; private set; } = "Idle";

        // ------------------------------------------------------- state outputs

        private void TickEvade()
        {
            combatState = CombatState.Defensive;
            if (!hasIncomingThreatObservation || stationaryTestMode)
            {
                // Warning without a usable track: blind lateral jink (legacy behaviour).
                var blind = stationaryTestMode ? default : BuildSearchFrame();
                if (!stationaryTestMode) CommandSearchAttitude();
                input.SubmitExternalFrame(in blind);
                return;
            }
            var plan = perception.Evasion;
            Vector3 threatDirection, maneuverDirection;
            MechPlayerInputFrame frame;
            if (plan.Active && plan.Mode != MechAiEvasionMode.Terminal)
            {
                var delta = incomingThreatPosition - transform.position;
                threatDirection = delta.sqrMagnitude > 0.01f ? delta.normalized : -plan.Direction;
                frame = MechAutonomousFlight.Acceleration(motion, plan.Direction * maximumManeuverAcceleration);
                maneuverDirection = plan.Direction;
                urgentBraking = false;
                currentAttitudeIntent = MechAiAttitudeIntent.EmergencyManeuver;
            }
            else
            {
                // Terminal (or no timed plan yet but inside the defensive window): existing jink controller.
                frame = BuildIncomingThreatFlightFrame(perception.WeaponCount, out threatDirection, out maneuverDirection);
                if (plan.Mode == MechAiEvasionMode.Terminal && plan.Direction.sqrMagnitude > 0.5f)
                {
                    frame = MechAutonomousFlight.Acceleration(motion, plan.Direction * maximumManeuverAcceleration,
                        frame.QuickBoostEdge || TryConsumeDefensiveQuickBoost());
                    maneuverDirection = plan.Direction;
                }
            }
            if (plan.ReleaseCountermeasures && Time.time >= nextCountermeasureTime)
            {
                frame.CountermeasureReleaseEdge = true;
                nextCountermeasureTime = Time.time + 2.5f;
            }
            UpdateSensorCamera(threatDirection);
            CommandAttitude(ResolveAttitudeDirection(threatDirection, maneuverDirection, CombatState.Defensive, false));
            input.SubmitExternalFrame(in frame);
            if (perception.HasTarget)
                UpdateWeapons(in perception.Target, radar.PredictTrackPosition(in perception.Target),
                    Vector3.Distance(transform.position, radar.PredictTrackPosition(in perception.Target)), perception.WeaponCount);
        }

        private bool TryConsumeDefensiveQuickBoost()
        {
            if (!motion.CanQuickBoost || Time.time < nextDefensiveQuickBoostTime) return false;
            nextDefensiveQuickBoostTime = Time.time + defensiveQuickBoostCooldownSeconds;
            return true;
        }

        private void TickSearch()
        {
            targetControlFilter.Reset();
            combatState = CombatState.Search;
            var direction = ResolveSearchLook(out bool hasBearingCue);
            UpdateSensorCamera(direction);
            if (!stationaryTestMode)
            {
                if (hasBearingCue) CommandAttitude(direction);
                else CommandSearchAttitude();
            }
            var frame = stationaryTestMode ? default : BuildSearchFrame();
            input.SubmitExternalFrame(in frame);
        }

        private void TickNavigate()
        {
            if (!perception.HasNavigation) { TickSearch(); return; }
            if (perception.HasTarget)
            {
                // Ordered movement keeps tracking (and shooting) the target while flying the route.
                combatState = CombatState.Search;
                var predicted = radar.PredictTrackPosition(in perception.Target);
                var direction = (predicted - transform.position).normalized;
                UpdateSensorCamera(direction);
                CommandAttitude(direction);
                input.SubmitExternalFrame(in perception.NavigationFrame);
                UpdateWeapons(in perception.Target, predicted, Vector3.Distance(transform.position, predicted), perception.WeaponCount);
                return;
            }
            targetControlFilter.Reset();
            combatState = CombatState.Search;
            var look = ResolveSearchLook(out bool hasBearingCue);
            UpdateSensorCamera(look);
            CommandAttitude(hasBearingCue ? look : perception.NavigationDirection);
            input.SubmitExternalFrame(in perception.NavigationFrame);
        }

        private Vector3 ResolveSearchLook(out bool hasBearingCue)
        {
            hasBearingCue = TryGetPassiveAirSearchCue(out var direction);
            if (!hasBearingCue) hasBearingCue = TryGetLastTrackSearchDirection(out direction);
            if (!hasBearingCue) direction = GetSearchDirection();
            if (Fleet != null && Fleet.TryMissionLook(out var missionLook)) { direction = missionLook.normalized; hasBearingCue = true; }
            if (Fleet != null && Fleet.TryDefenseLook(out var defenseLook)) { direction = defenseLook.normalized; hasBearingCue = true; }
            return direction;
        }

        private void TickEngage(in TrackSnapshot targetTrack)
        {
            var measuredVelocity = ToVector3(targetTrack.Velocity);
            var estimatedPosition = targetControlFilter.Update(
                                        targetTrack.TrackId,
                                        radar.PredictTrackPosition(in targetTrack),
                                        measuredVelocity,
                                        Time.deltaTime,
                                        targetPositionSmoothingSeconds,
                                        targetVelocitySmoothingSeconds,
                                        maximumTrackCorrectionSpeed) +
                                    targetControlFilter.Velocity * 0.12f;
            var toTarget = estimatedPosition - transform.position;
            var range = toTarget.magnitude;
            if (range <= 0.01f)
            {
                input.SubmitExternalFrame(default);
                return;
            }

            var direction = toTarget / range;
            lastTrackedPosition = estimatedPosition;
            lastTrackedVelocity = targetControlFilter.Velocity;
            lastTrackedTime = Time.time;
            if (!maneuverTrackId.Equals(targetTrack.TrackId))
            {
                maneuverTrackId = targetTrack.TrackId;
                orbitTangent = Vector3.ProjectOnPlane(motion.Velocity - lastTrackedVelocity, direction);
                maneuverPlanner.ResetEngagement();
            }
            UpdateSensorCamera(direction);
            var weaponCount = perception.WeaponCount;
            var doctrine = MechAiRulePlanner.ResolveCombatDoctrine(
                weaponStatusScratch,
                weaponCount,
                range,
                activeTacticalRole,
                minimumCombatRangeMeters,
                maximumCombatRangeMeters);
            doctrine = MechAiRulePlanner.ApplyMissionDoctrine(
                in doctrine,
                Fleet != null ? Fleet.TaskGroup : FleetTaskGroup.AirSuperiority,
                Fleet != null && Fleet.HasTask,
                Fleet != null && Fleet.HasSpatialMission);
            activeTacticalRole = doctrine.TacticalRole;
            var engagementBand = doctrine.EngagementBand;
            // The movement controller uses this same preferred standoff range.
            // Publish the estimated firing position, never the target's position.
            float plannedRange = Mathf.Max(100f, engagementBand.PreferredRangeMeters);
            plannedEngagementPosition = estimatedPosition - direction * plannedRange;
            hasPlannedEngagementPosition = weaponCount > 0 && float.IsFinite(plannedRange);
            combatState = SelectCombatState(range, in engagementBand);
            var weaponReady = HasReadyWeaponForDoctrine(
                in doctrine,
                range,
                weaponCount);
            var maneuverDirection = direction;
            var frame = stationaryTestMode
                ? default
                : BuildFlightFrame(
                    range,
                    direction,
                    targetControlFilter.Velocity,
                    combatState,
                    in doctrine,
                    out maneuverDirection);
            if (!stationaryTestMode && maneuverPlanner.CurrentState == MechAiManeuverState.PostLaunchBreak)
                plannedEngagementPosition = transform.position + maneuverDirection *
                    Mathf.Clamp(range * 0.08f, 40f, 240f);
            if (!stationaryTestMode)
            {
                CommandAttitude(ResolveAttitudeDirection(
                    direction,
                    maneuverDirection,
                    combatState,
                    weaponReady));
            }
            input.SubmitExternalFrame(in frame);

            UpdateWeapons(
                in targetTrack,
                estimatedPosition,
                range,
                weaponCount);
        }
    }
}
