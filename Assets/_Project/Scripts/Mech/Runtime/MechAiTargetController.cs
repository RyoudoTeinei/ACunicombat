using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public sealed partial class MechAiTargetController : MonoBehaviour
    {
        public enum CombatState : byte
        {
            Search = 0,
            Intercept = 1,
            Engage = 2,
            Extend = 3,
            Defensive = 4,
            Disabled = 5,
        }

        private const string Ac6PartsApproxShaderName =
            "ACunicombat/AC6/Parts Approx";
        private const float InterceptorRadarRevisitSeconds = 0.04f;
        private const float InterceptorTrackMaximumAgeSeconds = 0.6f;
        private const int MaximumPassiveThreats = 16;

        [SerializeField] private CombatTestTarget objective;
        [SerializeField] private Camera sensorCamera;
        [SerializeField, Min(100f)] private float minimumCombatRangeMeters = 3000f;
        [SerializeField, Min(100f)] private float maximumCombatRangeMeters = 5000f;
        [SerializeField, Min(1f)] private float missileCooldownSeconds = 8f;
        [SerializeField, Min(0.5f)] private float interceptorCooldownSeconds = 4f;
        [SerializeField] private float lateralWeavePeriodSeconds = 5f;
        [Header("Defensive doctrine")]
        [SerializeField, Min(0.5f)] private float defensiveThreatTimeSeconds = 8f;
        [SerializeField, Min(0.1f)] private float defensiveQuickBoostCooldownSeconds = 1.2f;
        [Header("Track steering stabilization")]
        [SerializeField, Min(0.02f)] private float targetPositionSmoothingSeconds = 0.28f;
        [SerializeField, Min(0.02f)] private float targetVelocitySmoothingSeconds = 0.4f;
        [SerializeField, Min(1f)] private float maximumTrackCorrectionSpeed = 180f;
        [Header("Spatial maneuvering")]
        [SerializeField, Min(0.5f)] private float maximumManeuverAcceleration = 30f;
        [SerializeField, Min(0.2f)] private float maneuverVelocityResponseSeconds = 1.5f;
        [SerializeField, Min(0.1f)] private float lostTrackSearchMemorySeconds = 4f;
        [Header("Passive search cue")]
        [SerializeField, Min(0.2f)] private float passiveCueMemorySeconds = 3f;
        [SerializeField, Min(0.1f)] private float passiveCueRadarHoldSeconds = 0.65f;
        [SerializeField, Range(0.01f, 1f)] private float passiveCueDirectionBlend = 0.35f;
        [SerializeField] private bool stationaryTestMode;

        private readonly List<Material> ownedPaintMaterials = new();
        private readonly TrackSnapshot[] airTrackScratch =
            new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly MissileTrackSnapshot[] missileTrackScratch =
            new MissileTrackSnapshot[MechRadarController.MaximumMissileContacts];
        private readonly ThreatSnapshot[] passiveThreatScratch =
            new ThreatSnapshot[MaximumPassiveThreats];
        private MechWeaponSlotStatus[] weaponStatusScratch = new MechWeaponSlotStatus[8];
        private WeaponCandidate[] weaponCandidateScratch = new WeaponCandidate[8];
        private MechEquipmentWeaponSnapshot[] weaponInstances = new MechEquipmentWeaponSnapshot[8];
        private readonly List<MechEquipmentWeaponSnapshot> equipmentInventory = new();
        private readonly Dictionary<System.Guid, WeaponRuleState> weaponRuleStates = new();
        private MechFleetController Fleet => GetComponent<MechFleetController>();
        private MechPlayerInputAdapter input;
        private MechPlayerRig rig;
        private MechFlightMotionController motion;
        private MechRadarController radar;
        private MechTestRadarEmitter radarEmitter;
        private MechTestWeaponSystem weapons;
        private MechWeaponArmLayer weaponAnimationLayer;
        private CombatTestTarget selfTarget;
        private int consumedAirCount,consumedMissileCount,consumedThreatCount;
        private int currentOffensiveWeaponCount;
        public double LastAirInputsTime {get;private set;}=-1;
        public double LastMissileInputsTime {get;private set;}=-1;
        public double LastThreatInputsTime {get;private set;}=-1;
        public int CopyConsumedAirInputs(TrackSnapshot[] destination)=>CopyRecorded(airTrackScratch,consumedAirCount,destination);
        public int CopyConsumedMissileInputs(MissileTrackSnapshot[] destination)=>CopyRecorded(missileTrackScratch,consumedMissileCount,destination);
        public int CopyConsumedThreatInputs(ThreatSnapshot[] destination)=>CopyRecorded(passiveThreatScratch,consumedThreatCount,destination);
        private static int CopyRecorded<T>(T[] source,int count,T[] destination)
        {
            if(destination==null)return 0;
            var copied=System.Math.Min(count,destination.Length);System.Array.Copy(source,destination,copied);return copied;
        }
        private ObserverTrackRef selectedAirTrackId;
        private MechTrackControlFilter targetControlFilter;
        private TrackSnapshot lastAirTrack;
        private bool hasLastAirTrack;
        private float nextTargetSwitchTime;
        public bool HasSelectedAirTrack => hasLastAirTrack && selectedAirTrackId.IsValid;
        public TrackId SelectedAirTrackId => selectedAirTrackId.TrackId;
        private float nextInterceptorTime;
        private float nextInterceptorRadarSweepTime;
        private float nextDefensiveQuickBoostTime;
        private float lastThreatSeenTime = float.NegativeInfinity;
        private Vector3 orbitTangent;
        private TrackId maneuverTrackId;
        private bool urgentBraking;
        private float lastTrackedTime = float.NegativeInfinity;
        private Vector3 lastTrackedPosition;
        private Vector3 lastTrackedVelocity;
        private float defensiveDodgeSign = 1f;
        private bool incomingThreat;
        private float incomingThreatTime = float.PositiveInfinity;
        private bool hasIncomingThreatObservation;
        private Vector3 incomingThreatPosition;
        private Vector3 incomingThreatVelocity;
        private float nextOffensiveMissileTime;
        private readonly MechAiManeuverPlanner maneuverPlanner = new();
        private MechAiAttitudeIntent currentAttitudeIntent;
        public string LastWeaponDecision {get;private set;}="Idle";
        public string LastManeuverDecision {get;private set;}="Idle";
        public MechAiManeuverState CurrentManeuverState => maneuverPlanner.CurrentState;
        public float ManeuverCommitRemainingSeconds => maneuverPlanner.EngagementCommitRemainingSeconds;
        private float nextWeaponDecisionReport;
        private void ReportWeaponDecision(string reason)
        {LastWeaponDecision=reason;if(Time.unscaledTime<nextWeaponDecisionReport)return;nextWeaponDecisionReport=Time.unscaledTime+1;Fleet?.Log("ai_weapon_decision",Fleet.State.Task?.Order??default,reason);}
        private void ReportManeuverDecision(MechAiManeuverState state)
        {
            LastManeuverDecision=state.ToString();
            Fleet?.Log("ai_maneuver_decision",Fleet.State.Task?.Order??default,LastManeuverDecision);
        }
        private CombatState combatState;
        private EsmThreatId passiveCueThreatId;
        private Vector3 passiveCueDirection;
        private double passiveCueLastSeenTime = double.NegativeInfinity;
        private bool hasPassiveCue;
        private MechAiTacticalWeaponRole activeTacticalRole;

        public static bool GlobalAiEnabled { get; private set; } = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetGlobalAiState()
        {
            GlobalAiEnabled = true;
        }

        private struct WeaponCandidate
        {
            public MechWeaponSlotStatus Status;
            public MechEquipmentWeaponSnapshot Equipment;
            public float Score;
        }

        private sealed class WeaponRuleState
        {
            public int BurstShotsFired;
            public float NextBurstTime;
            public float NextAttemptTime;
        }

        public CombatState CurrentCombatState => combatState;
        private Vector3 plannedEngagementPosition;
        private bool hasPlannedEngagementPosition;
        public bool TryGetObservedNavigationGoal(out Vector3 point)
        {
            point=plannedEngagementPosition;
            return enabled&&hasPlannedEngagementPosition&&combatState!=CombatState.Disabled&&Time.time-lastTrackedTime<1f;
        }

        public static bool ToggleGlobalAiEnabled()
        {
            SetGlobalAiEnabled(!GlobalAiEnabled);
            return GlobalAiEnabled;
        }

        public static void SetGlobalAiEnabled(bool value)
        {
            GlobalAiEnabled = value;
            var controllers = FindObjectsByType<MechAiTargetController>(
                FindObjectsInactive.Include);
            for (var i = 0; i < controllers.Length; i++)
            {
                controllers[i].SetRuntimeAiEnabled(value);
            }
        }

        public void Configure(CombatTestTarget newObjective, Camera newSensorCamera)
        {
            objective = newObjective;
            sensorCamera = newSensorCamera;
            radar ??= GetComponent<MechRadarController>();
            radar?.Configure(sensorCamera, objective);
            EnsureRadarEmitterConfigured();
        }

        public void ConfigureCombatBehavior(
            float minimumRangeMeters,
            float maximumRangeMeters,
            float missileIntervalSeconds)
        {
            minimumCombatRangeMeters = Mathf.Max(100f, minimumRangeMeters);
            maximumCombatRangeMeters = Mathf.Max(
                minimumCombatRangeMeters + 100f,
                maximumRangeMeters);
            missileCooldownSeconds = Mathf.Max(1f, missileIntervalSeconds);
            stationaryTestMode = false;
        }

        private void Awake()
        {
            input = GetComponent<MechPlayerInputAdapter>();
            rig = GetComponent<MechPlayerRig>();
            motion = GetComponent<MechFlightMotionController>();
            radar = GetComponent<MechRadarController>();
            if (radar != null)
            {
                radar.enabled = true;
                radar.Configure(sensorCamera, objective);
                radar.SetRadarPowerEnabled(true);
                radar.SetTrackOperationMode(true);
            }
            EnsureRadarEmitterConfigured();
            weapons = GetComponent<MechTestWeaponSystem>();
            weaponAnimationLayer = GetComponent<MechWeaponArmLayer>();
            selfTarget = GetComponent<CombatTestTarget>();
            var trajectoryPredictor = GetComponent<MechTrajectoryPredictor>();
            if (trajectoryPredictor != null)
            {
                trajectoryPredictor.enabled = false;
            }
            input?.SetExternalControl(true);
            if (stationaryTestMode &&
                TryGetComponent<Rigidbody>(out var body))
            {
                body.constraints =
                    RigidbodyConstraints.FreezePosition |
                    RigidbodyConstraints.FreezeRotation;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            nextInterceptorTime = Time.time + 1f;
            nextInterceptorRadarSweepTime = Time.time;
        }

        private void Start()
        {
            AlignInitialAttitudeToObjective();

            if (rig != null)
            {
                rig.ConfigureAiControl();
                rig.enabled = true;
            }

            ApplyIdentificationTint();
            if (!GlobalAiEnabled)
            {
                SetRuntimeAiEnabled(false);
            }
        }

        private void OnDisable()
        {
            input?.SubmitExternalFrame(default);
            motion?.SetYawTurnInput(0f);
            if (motion != null) motion.SetAimPitch(motion.LogicalPitchDegrees);
            combatState = CombatState.Disabled;
            maneuverPlanner.Reset();
            LastManeuverDecision = MechAiManeuverState.Disabled.ToString();
        }

        private void SetRuntimeAiEnabled(bool value)
        {
            input ??= GetComponent<MechPlayerInputAdapter>();
            if (!value)
            {
                input?.SubmitExternalFrame(default);
                combatState = CombatState.Disabled;
                enabled = false;
                return;
            }

            input?.SetExternalControl(true);
            enabled = true;
        }

        private void AlignInitialAttitudeToObjective()
        {
            if (motion == null || objective == null || Fleet != null)
            {
                return;
            }

            var targetPosition = objective.AimPoint.position;
            if ((targetPosition - transform.position).sqrMagnitude <= 0.0001f)
            {
                return;
            }

            motion.ResetReferencePlaneToward(targetPosition);
            UpdateSensorCamera(
                (targetPosition - transform.position).normalized);
        }

        private void OnDestroy()
        {
            for (var i = 0; i < ownedPaintMaterials.Count; i++)
            {
                if (ownedPaintMaterials[i] != null)
                {
                    Destroy(ownedPaintMaterials[i]);
                }
            }
            ownedPaintMaterials.Clear();
        }

        private CombatState SelectCombatState(
            float range,
            in MechAiEngagementBand engagementBand)
        {
            if (incomingThreat &&
                incomingThreatTime <= defensiveThreatTimeSeconds)
            {
                return CombatState.Defensive;
            }

            if (combatState == CombatState.Intercept &&
                range > engagementBand.MaximumRangeMeters * 0.9f)
            {
                return CombatState.Intercept;
            }
            if (combatState == CombatState.Extend &&
                range < engagementBand.MinimumRangeMeters * 1.1f)
            {
                return CombatState.Extend;
            }
            if (range > engagementBand.MaximumRangeMeters)
            {
                return CombatState.Intercept;
            }
            if (range < engagementBand.MinimumRangeMeters)
            {
                return CombatState.Extend;
            }
            return CombatState.Engage;
        }

        private void UpdateWeapons(
            in TrackSnapshot track,
            Vector3 estimatedPosition,
            float range,
            int weaponCount)
        {
            if(Fleet!=null&&Fleet.TaskGroup==FleetTaskGroup.Reconnaissance&&!Fleet.HasTask)return;
            if(weapons==null||radar==null)return;
            // Designation is not launch authorization. Passive and data-link shots
            // must not depend on acquiring an active-radar STT first.
            if(radar.RadarPowerEnabled&&radar.TrackOperationMode&&!(Fleet!=null&&Fleet.HasSpatialMission&&Fleet.TaskGroup==FleetTaskGroup.AirDefenseSuppression))radar.TryAssignFireControlTrack(track.TrackId);

            var bodyForward = motion.LogicalRotation * Vector3.forward;
            var toTarget = estimatedPosition - transform.position;
            var angularError = toTarget.sqrMagnitude > 0.01f
                ? Vector3.Angle(bodyForward, toTarget)
                : 180f;
            var uncertaintyMeters = CalculatePositionUncertainty(in track);
            var candidateCount = 0;
            for (var i = 0; i < weaponCount; i++)
            {
                var status = weaponStatusScratch[i];
                if(status.Role==MechWeaponRole.AirToAirMissile)
                {
                    bool pendingMissile=false;foreach(var e in equipmentInventory)if(e.Role==MechWeaponRole.AirToAirMissile&&e.Pending){pendingMissile=true;break;}
                    if(pendingMissile||Time.time<nextOffensiveMissileTime||!status.CanAttemptFire||range<status.MinimumRangeMeters||range>status.MaximumRangeMeters||angularError>55)continue;
                    if(!weapons.CanReleaseMissileAt(weaponInstances[i].SlotId,track.TrackId,out var mode,out var reason)){ReportWeaponDecision(weaponInstances[i].SlotId+":"+reason);continue;}
                    ReportWeaponDecision(weaponInstances[i].SlotId+":ReleaseReady:"+mode);
                }
                else if(!MechAiRulePlanner.IsEligibleForAirTarget(in status,range,angularError,track.Confidence,uncertaintyMeters))continue;

                weaponCandidateScratch[candidateCount++] =
                    new WeaponCandidate
                    {
                        Status = status,
                        Equipment = weaponInstances[i],
                        Score = MechAiRulePlanner.ScoreWeapon(
                            in status,
                            range),
                    };
            }

            SortWeaponCandidates(candidateCount);
            for (var i = 0; i < candidateCount; i++)
            {
                if(TryFireCandidate(in weaponCandidateScratch[i],track.TrackId))break;
            }
        }

        private int CopyOffensiveWeaponStatuses()
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.AiInventory);
            if (weapons == null)
            {
                return 0;
            }

            weapons.CopyEquipmentWeaponSnapshots(equipmentInventory);
            if (weaponStatusScratch.Length < equipmentInventory.Count)
            { System.Array.Resize(ref weaponStatusScratch,equipmentInventory.Count); System.Array.Resize(ref weaponCandidateScratch,equipmentInventory.Count); System.Array.Resize(ref weaponInstances,equipmentInventory.Count); }
            var count = 0;
            foreach (var equipment in equipmentInventory)
            {
                if(equipment.Enabled && equipment.Rounds==0 && equipment.Magazines>1 && !equipment.Reloading && !equipment.Pending && equipment.Role!=MechWeaponRole.AirToAirMissile && !MechFleetController.IsDefensiveRole(equipment.Role))weapons.CommandReloadSlot(equipment.SlotId,MechActionSource.AI);
                if (!equipment.Enabled || !weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(equipment.SlotId),out var status) || !MechAiRulePlanner.IsOffensiveRole(status.Role)) continue;
                var fleet=Fleet;
                if(fleet!=null&&!fleet.AllowsMissionEquipment(equipment.SlotId,status.Role))continue;
                if (fleet != null && fleet.Configured && (!fleet.State.AllowsWeapon(equipment.InstanceId) || status.Role==MechWeaponRole.AirToAirMissile && !fleet.State.AllowsOffensiveMissile(equipment.InstanceId))) continue;
                weaponInstances[count]=equipment; weaponStatusScratch[count++]=status;
            }
            return count;
        }

        private bool HasReadyWeaponForDoctrine(
            in MechAiCombatDoctrine doctrine,
            float rangeMeters,
            int weaponCount)
        {
            for (var i = 0; i < weaponCount; i++)
            {
                var status = weaponStatusScratch[i];
                if (!status.CanAttemptFire ||
                    !MechAiRulePlanner.MatchesDoctrine(in status, in doctrine))
                {
                    continue;
                }

                if (rangeMeters >= status.MinimumRangeMeters &&
                    rangeMeters <= status.MaximumRangeMeters)
                {
                    return true;
                }
            }

            return false;
        }

        private void SortWeaponCandidates(int candidateCount)
        {
            for (var i = 1; i < candidateCount; i++)
            {
                var candidate = weaponCandidateScratch[i];
                var j = i - 1;
                while (j >= 0 &&
                       weaponCandidateScratch[j].Score < candidate.Score)
                {
                    weaponCandidateScratch[j + 1] =
                        weaponCandidateScratch[j];
                    j--;
                }
                weaponCandidateScratch[j + 1] = candidate;
            }
        }

        private bool TryFireCandidate(
            in WeaponCandidate candidate,
            TrackId trackId)
        {
            var status=candidate.Status; var equipment=candidate.Equipment;
            if (Fleet != null && !Fleet.AllowsWeapon(equipment.InstanceId,status.Role,trackId,out _)) return false;
            if (!weaponRuleStates.TryGetValue(equipment.InstanceId,out var state))
                weaponRuleStates[equipment.InstanceId]=state=new WeaponRuleState();
            if (Time.time < state.NextAttemptTime)
            {
                return false;
            }

            if (status.Role == MechWeaponRole.AutomaticCannon &&
                Time.time < state.NextBurstTime)
            {
                return false;
            }

            bool accepted=weapons.CommandFireSlot(equipment.SlotId, MechActionSource.AI, radar.Reference(trackId));
            state.NextAttemptTime=Time.time+(status.Role==MechWeaponRole.AutomaticCannon?.035f:.2f);
            if(status.Reloading||!status.HasAmmunition)state.BurstShotsFired=0;
            return accepted;
        }

        public void NotifyWeaponCommitted(System.Guid instance,string slotId,MechWeaponRole role)
        {
            if(role==MechWeaponRole.InterceptorMissile){nextInterceptorTime=Time.time+interceptorCooldownSeconds;return;}
            if(!weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(slotId),out var status))return;
            var trackedDirection = hasLastAirTrack
                ? (lastTrackedPosition - transform.position).normalized
                : motion != null ? motion.LogicalRotation * Vector3.forward : transform.forward;
            var logicalRotation = motion != null ? motion.LogicalRotation : transform.rotation;
            if (maneuverPlanner.NotifyWeaponCommitted(
                    role, status.Slot, logicalRotation, trackedDirection, Time.time))
                ReportManeuverDecision(MechAiManeuverState.PostLaunchBreak);
            if(!weaponRuleStates.TryGetValue(instance,out var state))weaponRuleStates[instance]=state=new WeaponRuleState();
            var shotInterval = status.RoundsPerMinute > 0f
                ? 60f / status.RoundsPerMinute
                : 0.25f;
            state.NextAttemptTime = Time.time + Mathf.Max(0.01f, shotInterval);
            if (status.Role == MechWeaponRole.AutomaticCannon)
            {
                state.BurstShotsFired++;
                var burstSize = ResolveAutomaticBurstSize(in status);
                if (state.BurstShotsFired >= burstSize)
                {
                    state.BurstShotsFired = 0;
                    state.NextBurstTime = Time.time + Mathf.Max(
                        status.HeldBurstPauseSeconds,
                        status.BurstResetSeconds,
                        shotInterval);
                }
            }
            else if (status.Role == MechWeaponRole.AirToAirMissile)
            {
                state.NextAttemptTime = Time.time + Mathf.Max(shotInterval,missileCooldownSeconds);
                nextOffensiveMissileTime=state.NextAttemptTime;
            }

            if (status.Slot == MechPartSlot.LeftHandWeapon ||
                status.Slot == MechPartSlot.RightHandWeapon)
            {
                weaponAnimationLayer?.NotifyExternalFire(
                    rightHand: status.Slot == MechPartSlot.RightHandWeapon,
                    holdSeconds: Mathf.Clamp(shotInterval + 0.04f, 0.1f, 0.3f));
            }
        }

        private static int ResolveAutomaticBurstSize(
            in MechWeaponSlotStatus status)
        {
            if (status.HeldBurstShotCount > 0)
            {
                return status.HeldBurstShotCount;
            }

            var roundsPerSecond = Mathf.Max(1f, status.RoundsPerMinute / 60f);
            var derivedBurstSeconds = Mathf.Max(0.45f, status.BurstResetSeconds);
            return Mathf.Clamp(
                Mathf.CeilToInt(roundsPerSecond * derivedBurstSeconds),
                2,
                12);
        }


        private Vector3 GetSearchDirection()
        {
            var referenceUp = motion.ReferenceUp;
            var forward = motion.LogicalRotation * Vector3.forward;
            var scanAngle = Mathf.Sin(Time.time * 0.55f) * 42f;
            return Quaternion.AngleAxis(scanAngle, referenceUp) * forward;
        }

        private bool TryGetPassiveAirSearchCue(out Vector3 direction)
        {
            direction = Vector3.zero;
            if (radar == null)
            {
                hasPassiveCue = false;
                return false;
            }

            var now = radar.SimTimeSeconds;
            var count = radar.CopyThreats(passiveThreatScratch);
            consumedThreatCount=count;LastThreatInputsTime=radar.SimTimeSeconds;
            var bestScore = float.NegativeInfinity;
            var bestThreat = default(ThreatSnapshot);
            var found = false;

            for (var i = 0; i < count; i++)
            {
                var threat = passiveThreatScratch[i];
                if (!IsPassiveAirSearchThreat(in threat, now))
                {
                    continue;
                }

                var score = ScorePassiveAirSearchThreat(in threat, now);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestThreat = threat;
                found = true;
            }

            if (found)
            {
                var observedDirection = ToVector3(bestThreat.DirectionWorld).normalized;
                if (hasPassiveCue &&
                    passiveCueThreatId.Equals(bestThreat.ThreatId))
                {
                    passiveCueDirection = Vector3.Slerp(
                        passiveCueDirection,
                        observedDirection,
                        passiveCueDirectionBlend).normalized;
                }
                else
                {
                    passiveCueDirection = observedDirection;
                }

                passiveCueThreatId = bestThreat.ThreatId;
                passiveCueLastSeenTime = bestThreat.LastSeenTime;
                hasPassiveCue = true;
            }
            else if (!hasPassiveCue ||
                     now - passiveCueLastSeenTime > passiveCueMemorySeconds)
            {
                hasPassiveCue = false;
                return false;
            }

            direction = passiveCueDirection;
            radar.CueAirSearchDirection(direction, passiveCueRadarHoldSeconds);
            return true;
        }

        private static bool IsPassiveAirSearchThreat(
            in ThreatSnapshot threat,
            double now)
        {
            if (threat.Level == EsmThreatLevel.None ||
                now - threat.LastSeenTime > 2.5 ||
                EsmWaveformClassification.IsMissileSeeker(
                    threat.WaveformClass) ||
                threat.WaveformClass == EsmWaveformClass.InfraredPlume)
            {
                return false;
            }

            var direction = ToVector3(threat.DirectionWorld);
            return direction.sqrMagnitude > 0.5f;
        }

        private static float ScorePassiveAirSearchThreat(
            in ThreatSnapshot threat,
            double now)
        {
            var waveformScore = threat.WaveformClass switch
            {
                EsmWaveformClass.LaserRangefinder => 5f,
                EsmWaveformClass.FireControlRadar => 4f,
                EsmWaveformClass.SearchRadar => 3f,
                EsmWaveformClass.DataLinkUplink => 2f,
                _ => 1f,
            };
            var freshness = 1f - Mathf.Clamp01(
                (float)(now - threat.LastSeenTime) / 2.5f);
            return waveformScore +
                   (int)threat.Level * 1.5f +
                   Mathf.Clamp01(threat.IdentificationConfidence) * 2f +
                   freshness;
        }

        private void CommandSearchAttitude()
        {
            // Continue sweeping azimuth while covering high elevations, instead
            // of repeatedly searching the same narrow strip around the horizon.
            motion.SetYawTurnInput(0.18f);
            motion.SetAimPitch(Mathf.Sin(Time.time * 0.23f) * 75f);
        }

        private bool TryGetLastTrackSearchDirection(out Vector3 direction)
        {
            var age = Time.time - lastTrackedTime;
            direction = Vector3.zero;
            if (age < 0f || age > lostTrackSearchMemorySeconds) return false;
            var predicted = lastTrackedPosition + lastTrackedVelocity * Mathf.Min(age, 3f);
            var relative = predicted - transform.position;
            if (relative.sqrMagnitude < 0.0001f) return false;
            var center = relative.normalized;
            var tangent = MechAiRulePlanner.ResolveOrbitTangent(center, orbitTangent, motion.ReferenceUp);
            var bitangent = Vector3.Cross(center, tangent).normalized;
            var phase = age * Mathf.PI * 2f / Mathf.Max(1f, lateralWeavePeriodSeconds);
            var sweep = Mathf.Tan(Mathf.Lerp(2f, 28f,
                Mathf.Clamp01(age / lostTrackSearchMemorySeconds)) * Mathf.Deg2Rad);
            direction = (center + (tangent * Mathf.Cos(phase) + bitangent * Mathf.Sin(phase)) * sweep).normalized;
            return true;
        }

        private MechPlayerInputFrame BuildSearchFrame()
        {
            var defensive = combatState == CombatState.Defensive;
            var quickBoost = defensive &&
                             motion.CanQuickBoost &&
                             Time.time >= nextDefensiveQuickBoostTime;
            if (quickBoost)
            {
                nextDefensiveQuickBoostTime =
                    Time.time + defensiveQuickBoostCooldownSeconds;
            }
            return new MechPlayerInputFrame
            {
                MoveLocal = defensive
                    ? new Vector2(defensiveDodgeSign, -0.25f)
                    : new Vector2(
                        Mathf.Sin(Time.time * 0.7f) * 0.18f,
                        0.18f),
                QuickBoostEdge = quickBoost,
            };
        }

        private void EnsureRadarEmitterConfigured()
        {
            if (radarEmitter == null)
            {
                radarEmitter = GetComponent<MechTestRadarEmitter>();
            }
            if (radarEmitter == null)
            {
                radarEmitter =
                    gameObject.AddComponent<MechTestRadarEmitter>();
            }
        }

        private void UpdateMissileDefense()
        {
            if (Time.time - lastThreatSeenTime >
                InterceptorTrackMaximumAgeSeconds)
            {
                incomingThreat = false;
                incomingThreatTime = float.PositiveInfinity;
                hasIncomingThreatObservation = false;
            }
            if (radar == null ||
                weapons == null ||
                Time.time < nextInterceptorRadarSweepTime)
            {
                return;
            }

            nextInterceptorRadarSweepTime =
                Time.time + InterceptorRadarRevisitSeconds;
            var trackCount = radar.CopyMissileTracks(missileTrackScratch);
            consumedMissileCount=trackCount;LastMissileInputsTime=radar.SimTimeSeconds;
            var hasBestThreat = false;
            var bestThreat = default(MissileTrackSnapshot);
            var bestTime = float.PositiveInfinity;
            var now = radar.SimTimeSeconds;
            for (var i = 0; i < trackCount; i++)
            {
                var candidate = missileTrackScratch[i];
                if (!candidate.TrackId.IsValid ||
                    candidate.IsFriendly ||
                    !candidate.IsWeaponsQuality ||
                    !candidate.Incoming ||
                    now - candidate.LastEvidenceTime >
                    InterceptorTrackMaximumAgeSeconds ||
                    candidate.ClosestApproachDistanceMeters > 180f)
                {
                    continue;
                }

                var urgency = Mathf.Max(
                    0f,
                    candidate.TimeToClosestApproachSeconds);
                if (urgency < bestTime)
                {
                    bestTime = urgency;
                    bestThreat = candidate;
                    hasBestThreat = true;
                }
            }

            if (!hasBestThreat)
            {
                if (Fleet != null && radar.TryGetMissileTrack(Fleet.DefenseTrack,out var support)) TryFleetInterceptor(support);
                return;
            }

            incomingThreat = true;
            incomingThreatTime = bestTime;
            lastThreatSeenTime = Time.time;
            radar.CuePointDefenseTrack(bestThreat.TrackId);
            var predictedThreatPosition = ToVector3(
                bestThreat.PredictPosition(now));
            incomingThreatPosition = predictedThreatPosition;
            incomingThreatVelocity = ToVector3(bestThreat.PredictVelocity(now));
            hasIncomingThreatObservation = true;
            var localThreat = transform.InverseTransformPoint(
                predictedThreatPosition);
            if (Mathf.Abs(localThreat.x) > 0.1f)
            {
                defensiveDodgeSign = localThreat.x > 0f ? -1f : 1f;
            }

            if (Time.time < nextInterceptorTime)
            {
                return;
            }

            TryFleetInterceptor(bestThreat);
            if(Fleet!=null&&!Fleet.DefenseTrack.Equals(bestThreat.TrackId)&&radar.TryGetMissileTrack(Fleet.DefenseTrack,out var sharedThreat))TryFleetInterceptor(sharedThreat);
        }

        private void TryFleetInterceptor(MissileTrackSnapshot threat)
        {
            if(Time.time<nextInterceptorTime || Fleet!=null && !Fleet.MayDefend(threat,out _)) return;
            weapons.CopyEquipmentWeaponSnapshots(equipmentInventory);
            foreach(var e in equipmentInventory)
                if(e.Enabled && !e.Pending && e.Role==MechWeaponRole.InterceptorMissile && e.Rounds>0 && weapons.TryEstimateFleetDefense(e.SlotId,threat,out _) &&
                   weapons.CommandFireSlot(e.SlotId,MechActionSource.AutomaticDefense,radar.Reference(threat.TrackId)))
                { nextInterceptorTime=Time.time+.2f; break; }
        }

        private bool TryGetAirTargetTrack(int weaponCount, out TrackSnapshot selected)
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.AiTargetSelection);
            selected = default;
            bool screening = Fleet != null && Fleet.AllowsScreeningEngagement;
            if (!screening && Fleet != null && Fleet.HasTask && Fleet.State.Task.Order.Order!=FleetOrderKind.Move && Fleet.State.Task.Phase != FleetTaskPhase.Returning)
            {
                if (!Fleet.TryGetOrderedTrack(out selected)) return false;
                selectedAirTrackId = radar.Reference(selected.TrackId);
                lastAirTrack = selected;
                hasLastAirTrack = true;
                return true;
            }
            if(!screening&&Fleet!=null&&(Fleet.TaskGroup==FleetTaskGroup.Support||Fleet.TaskGroup==FleetTaskGroup.Escort||Fleet.TaskGroup==FleetTaskGroup.AirDefenseSuppression||Fleet.TaskGroup==FleetTaskGroup.AirSuperiority&&!Fleet.HasSpatialMission))return false;
            if (radar == null || !radar.isActiveAndEnabled)
            {
                return false;
            }

            if (selectedAirTrackId.IsValid && selectedAirTrackId.ObserverScope != radar.ObserverScope)
            {
                selectedAirTrackId=default;hasLastAirTrack=false;
            }
            // Inventory does not change while scoring this batch. Do not rebuild
            // the equipment/slot graph for every candidate (including missiles).
            float automaticRange = Fleet != null && (screening || !Fleet.HasTask || Fleet.State.Task.Order.Order == FleetOrderKind.Move)
                ? Fleet.MaximumOffensiveRange() : float.PositiveInfinity;
            var retained = default(TrackSnapshot);
            var hasCurrent = selectedAirTrackId.IsValid &&
                             radar.TryGetTrack(selectedAirTrackId, out retained) &&
                             IsUsableAirTargetWithinRange(in retained, automaticRange);
            if (!hasCurrent && hasLastAirTrack &&
                radar.TryReacquireTrack(in lastAirTrack, 500f, out var reacquired) &&
                IsUsableAirTargetWithinRange(in reacquired, automaticRange))
            {
                selectedAirTrackId = radar.Reference(reacquired.TrackId);
                retained = reacquired;
                hasCurrent = true;
            }

            var count = radar.CopyTracks(airTrackScratch);
            consumedAirCount=count;LastAirInputsTime=radar.SimTimeSeconds;
            var best = default(TrackSnapshot);
            var bestScore = float.PositiveInfinity;
            var bestCanEngage = false;
            for (var i = 0; i < count; i++)
            {
                var candidate = airTrackScratch[i];
                if (!IsUsableAirTargetWithinRange(in candidate, automaticRange) || screening && Fleet.IsStrikeTarget(candidate.TrackId)) continue;
                var range = Vector3.Distance(transform.position, radar.PredictTrackPosition(in candidate));
                var canEngage = CanEngageAirTarget(in candidate, range, weaponCount);
                var score = MechAiTargetSelection.Score(in candidate, range, canEngage, radar.SimTimeSeconds) + DeconflictionPenalty(in candidate);
                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
                bestCanEngage = canEngage;
            }

            var currentScore = 0f;
            var currentCanEngage = false;
            if (hasCurrent)
            {
                var currentRange = Vector3.Distance(transform.position, radar.PredictTrackPosition(in retained));
                currentCanEngage = CanEngageAirTarget(in retained, currentRange, weaponCount);
                currentScore = MechAiTargetSelection.Score(in retained, currentRange, currentCanEngage, radar.SimTimeSeconds) + DeconflictionPenalty(in retained);
            }

            var hasAlternative = best.TrackId.IsValid;
            var alternativeIsCurrent = hasCurrent && best.TrackId.Equals(retained.TrackId);
            if (hasCurrent && (!hasAlternative || alternativeIsCurrent ||
                !MechAiTargetSelection.ShouldSwitch(
                    true, currentCanEngage, currentScore,
                    hasAlternative, bestCanEngage, bestScore,
                    Time.time, nextTargetSwitchTime)))
            {
                selected = retained;
            }
            else if (hasAlternative)
            {
                selected = best;
                nextTargetSwitchTime = Time.time + MechAiTargetSelection.MinimumSwitchIntervalSeconds;
            }
            else
            {
                selectedAirTrackId = default;
                hasLastAirTrack = false;
                return false;
            }

            selectedAirTrackId = radar.Reference(selected.TrackId);
            lastAirTrack = selected;
            hasLastAirTrack = true;
            return true;
        }

        private bool CanEngageAirTarget(in TrackSnapshot track, float range, int weaponCount)
        {
            var uncertaintyMeters = CalculatePositionUncertainty(in track);
            for (var i = 0; i < weaponCount; i++)
            {
                if (MechAiRulePlanner.IsEligibleForAirTarget(
                    in weaponStatusScratch[i], range, 0f,
                    track.Confidence, uncertaintyMeters)) return true;
            }
            return false;
        }

        private bool IsUsableAirTarget(in TrackSnapshot track)
        {
            float range = Fleet != null && (!Fleet.HasTask || Fleet.State.Task.Order.Order == FleetOrderKind.Move)
                ? Fleet.MaximumOffensiveRange() : float.PositiveInfinity;
            return IsUsableAirTargetWithinRange(in track, range);
        }
        private bool IsUsableAirTargetWithinRange(in TrackSnapshot track, float automaticRange)
        {
            if(!float.IsPositiveInfinity(automaticRange)&&!MechFleetController.WithinAutomaticAttackRange(transform.position,radar.PredictTrackPosition(in track),automaticRange))return false;
            return track.TrackId.IsValid &&
                   track.ObjectClass != TrackObjectClass.Missile &&
                   track.ObjectClass != TrackObjectClass.Decoy &&
                   track.Affiliation != TrackAffiliation.Friendly &&
                   (!track.Identity.Available || track.Identity.CanAttackPlatform || Fleet!=null&&Fleet.TaskGroup==FleetTaskGroup.Reconnaissance&&!Fleet.HasTask) &&
                   track.Lifecycle != TrackLifecycle.Tentative &&
                   track.Lifecycle != TrackLifecycle.Dropped &&
                   track.Confidence >= 0.25f;
        }

        private static float CalculatePositionUncertainty(
            in TrackSnapshot track)
        {
            var variance = System.Math.Max(
                0.0,
                track.GetCovariance(0, 0) +
                track.GetCovariance(1, 1) +
                track.GetCovariance(2, 2));
            return Mathf.Sqrt((float)variance);
        }

        private static Vector3 ToVector3(
            Unity.Mathematics.double3 value)
        {
            return new Vector3(
                (float)value.x,
                (float)value.y,
                (float)value.z);
        }

        private static Vector3 ToVector3(
            Unity.Mathematics.float3 value)
        {
            return new Vector3(value.x, value.y, value.z);
        }

        private MechPlayerInputFrame BuildIncomingThreatFlightFrame(
            int weaponCount,
            out Vector3 threatDirection,
            out Vector3 maneuverDirection)
        {
            var delta = incomingThreatPosition - transform.position;
            var range = delta.magnitude;
            threatDirection = range > 0.01f
                ? delta / range
                : incomingThreatVelocity.sqrMagnitude > 0.01f
                    ? -incomingThreatVelocity.normalized
                    : motion.ReferenceUp;
            var doctrine = MechAiRulePlanner.ResolveCombatDoctrine(
                weaponStatusScratch,
                weaponCount,
                range,
                activeTacticalRole,
                minimumCombatRangeMeters,
                maximumCombatRangeMeters);
            return BuildFlightFrame(
                range,
                threatDirection,
                incomingThreatVelocity,
                CombatState.Defensive,
                in doctrine,
                out maneuverDirection);
        }

        private MechPlayerInputFrame BuildFlightFrame(
            float range,
            Vector3 directionToTarget,
            Vector3 targetVelocity,
            CombatState state,
            in MechAiCombatDoctrine doctrine,
            out Vector3 maneuverDirection)
        {
            var radial = directionToTarget.normalized;
            orbitTangent = MechAiRulePlanner.ResolveOrbitTangent(radial, orbitTangent, motion.ReferenceUp);
            var defensive = state == CombatState.Defensive;
            var tangent = defensive ? orbitTangent * defensiveDodgeSign : orbitTangent;
            var previousManeuverState = maneuverPlanner.CurrentState;
            var weaponReady = HasReadyWeaponForDoctrine(
                in doctrine, range, currentOffensiveWeaponCount);
            var command = maneuverPlanner.Resolve(
                radial * range,
                targetVelocity - motion.Velocity,
                tangent,
                state,
                in doctrine,
                Fleet != null ? Fleet.TaskGroup : FleetTaskGroup.AirSuperiority,
                Fleet != null && Fleet.HasTask,
                Fleet != null && Fleet.HasSpatialMission,
                currentOffensiveWeaponCount > 0,
                weaponReady,
                motion.ManeuverEnergyNormalized,
                motion.EstimateTranslationAcceleration(radial),
                motion.EstimateTranslationAcceleration(-radial),
                maximumManeuverAcceleration,
                maneuverVelocityResponseSeconds,
                Time.time);
            if (command.State != previousManeuverState)
                ReportManeuverDecision(command.State);
            currentAttitudeIntent = command.AttitudeIntent;
            urgentBraking = command.UrgentBraking;
            var acceleration = command.AccelerationWorld;
            if (motion.Grounded)
            {
                // Air combat must first leave the launch platform. The allocator
                // still decides whether the installed nozzles can supply lift.
                var climb = Vector3.Dot(acceleration, motion.ReferenceUp);
                acceleration += motion.ReferenceUp * Mathf.Max(0f, 3f - climb);
                acceleration = Vector3.ClampMagnitude(acceleration, maximumManeuverAcceleration);
            }
            maneuverDirection = acceleration.sqrMagnitude > 0.01f ? acceleration.normalized : radial;
            var localCommand = Quaternion.Inverse(motion.LogicalRotation) * acceleration /
                               Mathf.Max(1f, maximumManeuverAcceleration);
            var quickBoost = defensive &&
                             motion.CanQuickBoost &&
                             motion.ManeuverEnergyNormalized > 0.35f &&
                             Time.time >= nextDefensiveQuickBoostTime;
            if (quickBoost)
            {
                nextDefensiveQuickBoostTime =
                    Time.time + defensiveQuickBoostCooldownSeconds;
            }
            return new MechPlayerInputFrame
            {
                HasAutonomousFlightCommand = true,
                AutonomousAccelerationWorld = acceleration,
                MoveLocal = new Vector2(localCommand.x, localCommand.z),
                RiseHeld = motion.Grounded || localCommand.y > 0.05f,
                DescendHeld = !motion.Grounded && localCommand.y < -0.05f,
                QuickBoostEdge = quickBoost,
                BrakeHeld = false,
            };
        }

        private Vector3 ResolveAttitudeDirection(
            Vector3 directionToTarget,
            Vector3 maneuverDirection,
            CombatState state,
            bool weaponReady)
        {
            if (maneuverDirection.sqrMagnitude <= 0.0001f)
            {
                return directionToTarget;
            }

            if (currentAttitudeIntent == MechAiAttitudeIntent.EmergencyManeuver ||
                urgentBraking || (state == CombatState.Defensive && incomingThreatTime < 3f))
                return maneuverDirection;
            if (currentAttitudeIntent == MechAiAttitudeIntent.TrackTarget ||
                currentAttitudeIntent == MechAiAttitudeIntent.LeadWeapon || weaponReady)
                return directionToTarget;
            if (currentAttitudeIntent == MechAiAttitudeIntent.Search &&
                state == CombatState.Extend)
                return maneuverDirection;
            // Keep a searchable forward view while repositioning/reloading.
            return Vector3.RotateTowards(directionToTarget, maneuverDirection,
                20f * Mathf.Deg2Rad, 0f).normalized;
        }

        private void CommandAttitude(Vector3 direction)
        {
            if (direction.sqrMagnitude < 0.0001f)
            {
                motion.SetYawTurnInput(0f);
                return;
            }
            var local = Quaternion.Inverse(motion.ReferenceRotation) * direction.normalized;
            var horizontal = Mathf.Sqrt(local.x * local.x + local.z * local.z);
            var yawError = horizontal > 0.001f
                ? Mathf.DeltaAngle(motion.LogicalYawDegrees, Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg)
                : 0f;
            motion.SetYawTurnInput(Mathf.Clamp(yawError / 35f, -1f, 1f));
            // Positive rotation around local +X pitches the nose DOWN.
            motion.SetAimPitch(-Mathf.Atan2(local.y, horizontal) * Mathf.Rad2Deg);
        }

        private void UpdateSensorCamera(Vector3 direction)
        {
            if (sensorCamera == null)
            {
                return;
            }

            sensorCamera.transform.position =
                transform.position + motion.ReferenceUp * 6f;
            var up = Vector3.ProjectOnPlane(motion.ReferenceUp, direction);
            if (up.sqrMagnitude < 0.0001f)
                up = Vector3.ProjectOnPlane(motion.LogicalRotation * Vector3.forward, direction);
            if (up.sqrMagnitude < 0.0001f)
                up = MechAiRulePlanner.ResolveOrbitTangent(direction, Vector3.zero, motion.ReferenceUp);
            sensorCamera.transform.rotation = Quaternion.LookRotation(direction, up.normalized);
        }

        private void ApplyIdentificationTint()
        {
            var palette = MechAppearanceSettings.CreateNpcDefault();
            var materialCopies = new Dictionary<Material, Material>();
            var block = new MaterialPropertyBlock();
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;
                var materials = renderer.sharedMaterials;
                var usesAc6PartsApprox = false;
                var materialsChanged = false;
                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    var sourceMaterial = materials[materialIndex];
                    var shader = sourceMaterial != null ? sourceMaterial.shader : null;
                    if (shader == null || shader.name != Ac6PartsApproxShaderName) continue;
                    usesAc6PartsApprox = true;
                    if (!materialCopies.TryGetValue(sourceMaterial, out var paintMaterial))
                    {
                        paintMaterial = new Material(sourceMaterial) { name = $"{sourceMaterial.name}_NpcPaint_Runtime" };
                        ApplyNpcPaintPreset(paintMaterial, palette);
                        materialCopies.Add(sourceMaterial, paintMaterial);
                        ownedPaintMaterials.Add(paintMaterial);
                    }
                    materials[materialIndex] = paintMaterial;
                    materialsChanged = true;
                }
                if (materialsChanged) renderer.sharedMaterials = materials;
                if (usesAc6PartsApprox) continue;
                renderer.GetPropertyBlock(block);
                block.SetColor("_PaintColor", palette.baseColor);
                block.SetFloat("_PaintInfluence", 0.34f);
                renderer.SetPropertyBlock(block);
            }
        }

        private static void ApplyNpcPaintPreset(Material material, MechAppearanceSettings palette)
        {
            if (material == null) return;
            SetColor(material, "_Zone1Color", palette.baseColor);
            SetColor(material, "_Zone2Color", palette.subColor);
            SetColor(material, "_Zone3Color", palette.supportColor);
            SetColor(material, "_Zone4Color", palette.optionalColor);
            SetColor(material, "_Zone5Color", palette.deviceColor);
            SetColor(material, "_Zone6Color", Color.Lerp(palette.subColor, palette.supportColor, .35f));
            SetColor(material, "_Zone7Color", Color.Lerp(Color.black, palette.optionalColor, .22f));

            // Apply a real material preset instead of a white tint over the source texture.
            SetFloat(material, "_UseSourceAlbedo", 0f);
            SetFloat(material, "_ZoneMode", 0f);
            SetFloat(material, "_PaintInfluence", 1f);
            SetFloat(material, "_AlbedoDetailStrength", 0.18f);
            SetColor(material, "_BaseColor", palette.baseColor);
            SetColor(material, "_PaintColor", palette.baseColor);
        }

        private static void SetColor(
            Material material,
            string propertyName,
            Color color)
        {
            if (material.HasProperty(propertyName))
            {
                material.SetColor(propertyName, color);
            }
        }

        private static void SetFloat(
            Material material,
            string propertyName,
            float value)
        {
            if (material.HasProperty(propertyName))
            {
                material.SetFloat(propertyName, value);
            }
        }
    }
}
