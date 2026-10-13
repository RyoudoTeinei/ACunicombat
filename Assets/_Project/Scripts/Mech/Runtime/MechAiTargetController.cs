using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public sealed class MechAiTargetController : MonoBehaviour
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
        [SerializeField] private Color identificationTint =
            new Color(0.96f, 0.97f, 0.98f, 1f);

        private readonly List<Material> ownedPaintMaterials = new();
        private readonly TrackSnapshot[] airTrackScratch =
            new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly MissileTrackSnapshot[] missileTrackScratch =
            new MissileTrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly ThreatSnapshot[] passiveThreatScratch =
            new ThreatSnapshot[MaximumPassiveThreats];
        private readonly MechWeaponSlotStatus[] weaponStatusScratch =
            new MechWeaponSlotStatus[4];
        private readonly WeaponCandidate[] weaponCandidateScratch =
            new WeaponCandidate[4];
        private readonly WeaponRuleState[] weaponRuleStates =
            new WeaponRuleState[4];
        private MechPlayerInputAdapter input;
        private MechPlayerRig rig;
        private MechFlightMotionController motion;
        private MechRadarController radar;
        private MechTestRadarEmitter radarEmitter;
        private MechTestWeaponSystem weapons;
        private MechWeaponArmLayer weaponAnimationLayer;
        private CombatTestTarget selfTarget;
        private TrackId selectedAirTrackId;
        private MechTrackControlFilter targetControlFilter;
        private TrackSnapshot lastAirTrack;
        private bool hasLastAirTrack;
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

        private static readonly MechPartSlot[] WeaponSlots =
        {
            MechPartSlot.LeftHandWeapon,
            MechPartSlot.RightHandWeapon,
            MechPartSlot.LeftBackWeapon,
            MechPartSlot.RightBackWeapon,
        };

        private struct WeaponCandidate
        {
            public MechWeaponSlotStatus Status;
            public float Score;
        }

        private sealed class WeaponRuleState
        {
            public int BurstShotsFired;
            public float NextBurstTime;
            public float NextAttemptTime;
        }

        public CombatState CurrentCombatState => combatState;

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
            for (var i = 0; i < weaponRuleStates.Length; i++)
            {
                weaponRuleStates[i] = new WeaponRuleState
                {
                    NextBurstTime = Time.time + Random.Range(0.35f, 0.9f),
                };
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
            if (motion == null || objective == null)
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

        private void Update()
        {
            if (input == null || motion == null || objective == null)
            {
                input?.SubmitExternalFrame(default);
                motion?.SetYawTurnInput(0f);
                return;
            }

            if ((selfTarget != null && !selfTarget.IsAlive) ||
                !objective.IsAlive)
            {
                combatState = CombatState.Disabled;
                input.SubmitExternalFrame(default);
                motion.SetYawTurnInput(0f);
                motion.SetAimPitch(motion.LogicalPitchDegrees);
                return;
            }

            UpdateMissileDefense();

            if (!TryGetAirTargetTrack(out var targetTrack))
            {
                targetControlFilter.Reset();
                combatState = incomingThreat
                    ? CombatState.Defensive
                    : CombatState.Search;
                var hasBearingCue = TryGetPassiveAirSearchCue(
                    out var searchDirection);
                if (!hasBearingCue)
                    hasBearingCue = TryGetLastTrackSearchDirection(out searchDirection);
                if (!hasBearingCue)
                {
                    searchDirection = GetSearchDirection();
                }
                UpdateSensorCamera(searchDirection);
                if (!stationaryTestMode)
                {
                    if (hasBearingCue)
                    {
                        CommandAttitude(searchDirection);
                    }
                    else
                    {
                        CommandSearchAttitude();
                    }
                }
                var searchFrame = stationaryTestMode
                    ? default
                    : BuildSearchFrame();
                input.SubmitExternalFrame(in searchFrame);
                return;
            }

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
            }
            UpdateSensorCamera(direction);
            var weaponCount = CopyOffensiveWeaponStatuses();
            var doctrine = MechAiRulePlanner.ResolveCombatDoctrine(
                weaponStatusScratch,
                weaponCount,
                range,
                activeTacticalRole,
                minimumCombatRangeMeters,
                maximumCombatRangeMeters);
            activeTacticalRole = doctrine.TacticalRole;
            var engagementBand = doctrine.EngagementBand;
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
            if (weapons == null || radar == null ||
                !radar.TryAssignFireControlTrack(track.TrackId))
            {
                return;
            }

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
                if (!MechAiRulePlanner.IsEligibleForAirTarget(
                        in status,
                        range,
                        angularError,
                        track.Confidence,
                        uncertaintyMeters))
                {
                    continue;
                }

                weaponCandidateScratch[candidateCount++] =
                    new WeaponCandidate
                    {
                        Status = status,
                        Score = MechAiRulePlanner.ScoreWeapon(
                            in status,
                            range),
                    };
            }

            SortWeaponCandidates(candidateCount);
            for (var i = 0; i < candidateCount; i++)
            {
                TryFireCandidate(
                    in weaponCandidateScratch[i].Status,
                    track.TrackId);
            }
        }

        private int CopyOffensiveWeaponStatuses()
        {
            if (weapons == null)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < WeaponSlots.Length; i++)
            {
                if (!weapons.TryGetWeaponSlotStatus(
                        WeaponSlots[i],
                        out var status) ||
                    !MechAiRulePlanner.IsOffensiveRole(status.Role))
                {
                    continue;
                }
                weaponStatusScratch[count++] = status;
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

        private void TryFireCandidate(
            in MechWeaponSlotStatus status,
            TrackId trackId)
        {
            var state = weaponRuleStates[GetWeaponSlotIndex(status.Slot)];
            if (Time.time < state.NextAttemptTime)
            {
                return;
            }

            if (status.Role == MechWeaponRole.AutomaticCannon &&
                Time.time < state.NextBurstTime)
            {
                return;
            }

            var fired = weapons.CommandFireWeaponAtTrack(
                status.Slot,
                trackId);
            if (!fired)
            {
                state.NextAttemptTime = Time.time +
                    (status.Role == MechWeaponRole.AutomaticCannon
                        ? 0.035f
                        : 0.2f);
                if (status.Reloading || !status.HasAmmunition)
                {
                    state.BurstShotsFired = 0;
                }
                return;
            }

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
                state.NextAttemptTime = Time.time +
                    (status.RoundsPerMinute > 0f
                        ? Mathf.Max(shotInterval, 0.5f)
                        : missileCooldownSeconds);
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

        private static int GetWeaponSlotIndex(MechPartSlot slot)
        {
            return slot switch
            {
                MechPartSlot.LeftHandWeapon => 0,
                MechPartSlot.RightHandWeapon => 1,
                MechPartSlot.LeftBackWeapon => 2,
                MechPartSlot.RightBackWeapon => 3,
                _ => 0,
            };
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
            if (radar == null || !radar.RadarPowerEnabled)
            {
                hasPassiveCue = false;
                return false;
            }

            var now = radar.SimTimeSeconds;
            var count = radar.CopyThreats(passiveThreatScratch);
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
                return;
            }

            incomingThreat = true;
            incomingThreatTime = bestTime;
            lastThreatSeenTime = Time.time;
            radar.CuePointDefenseTrack(bestThreat.TrackId);
            var predictedThreatPosition = ToVector3(
                bestThreat.PredictPosition(now));
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

            if (weapons.TryGetWeaponSlot(
                    MechWeaponRole.InterceptorMissile,
                    out var interceptorSlot) &&
                weapons.CommandFireMissile(interceptorSlot))
            {
                nextInterceptorTime =
                    Time.time + interceptorCooldownSeconds;
            }
        }

        private bool TryGetAirTargetTrack(out TrackSnapshot selected)
        {
            selected = default;
            if (radar == null || !radar.isActiveAndEnabled)
            {
                return false;
            }

            if (selectedAirTrackId.IsValid &&
                radar.TryGetTrack(selectedAirTrackId, out var retained) &&
                IsUsableAirTarget(in retained))
            {
                selected = retained;
                lastAirTrack = retained;
                hasLastAirTrack = true;
                return true;
            }

            if (hasLastAirTrack &&
                radar.TryReacquireTrack(
                    in lastAirTrack,
                    500f,
                    out var reacquired) &&
                IsUsableAirTarget(in reacquired))
            {
                selectedAirTrackId = reacquired.TrackId;
                lastAirTrack = reacquired;
                selected = reacquired;
                return true;
            }

            selectedAirTrackId = default;
            var count = radar.CopyTracks(airTrackScratch);
            var bestScore = float.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                var candidate = airTrackScratch[i];
                if (!IsUsableAirTarget(in candidate))
                {
                    continue;
                }

                var range = Vector3.Distance(
                    transform.position,
                    radar.PredictTrackPosition(in candidate));
                var classPenalty = candidate.ObjectClass ==
                                   TrackObjectClass.AirVehicle
                    ? 0f
                    : 3f;
                var score = classPenalty +
                            range / 10000f +
                            (1f - candidate.Confidence) * 2f +
                            candidate.InterferenceScore;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                selected = candidate;
            }

            if (!selected.TrackId.IsValid)
            {
                hasLastAirTrack = false;
                return false;
            }

            selectedAirTrackId = selected.TrackId;
            lastAirTrack = selected;
            hasLastAirTrack = true;
            return true;
        }

        private static bool IsUsableAirTarget(in TrackSnapshot track)
        {
            return track.TrackId.IsValid &&
                   track.ObjectClass != TrackObjectClass.Missile &&
                   track.ObjectClass != TrackObjectClass.Decoy &&
                   track.Affiliation != TrackAffiliation.Friendly &&
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
            var acceleration = MechAiRulePlanner.CalculateSpatialAcceleration(
                radial * range, targetVelocity - motion.Velocity, tangent, state, in doctrine,
                motion.EstimateTranslationAcceleration(radial),
                motion.EstimateTranslationAcceleration(-radial),
                maximumManeuverAcceleration, maneuverVelocityResponseSeconds, out urgentBraking);
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

            if (urgentBraking || (state == CombatState.Defensive && incomingThreatTime < 3f))
            {
                return maneuverDirection;
            }
            if (weaponReady)
            {
                return directionToTarget;
            }

            if (state == CombatState.Extend) return maneuverDirection;
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
            var materialCopies = new Dictionary<Material, Material>();
            var block = new MaterialPropertyBlock();
            var renderers = GetComponentsInChildren<Renderer>(true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                var usesAc6PartsApprox = false;
                var materialsChanged = false;
                for (var materialIndex = 0;
                     materialIndex < materials.Length;
                     materialIndex++)
                {
                    var sourceMaterial = materials[materialIndex];
                    var shader = sourceMaterial != null
                        ? sourceMaterial.shader
                        : null;
                    if (shader != null &&
                        shader.name == Ac6PartsApproxShaderName)
                    {
                        usesAc6PartsApprox = true;
                        if (!materialCopies.TryGetValue(
                                sourceMaterial,
                                out var paintMaterial))
                        {
                            paintMaterial = new Material(sourceMaterial)
                            {
                                name =
                                    $"{sourceMaterial.name}_EnemyWhite_Runtime",
                            };
                            ApplyWhitePaintPreset(paintMaterial);
                            materialCopies.Add(
                                sourceMaterial,
                                paintMaterial);
                            ownedPaintMaterials.Add(paintMaterial);
                        }

                        materials[materialIndex] = paintMaterial;
                        materialsChanged = true;
                    }
                }
                if (materialsChanged)
                {
                    renderer.sharedMaterials = materials;
                }
                if (usesAc6PartsApprox)
                {
                    continue;
                }

                renderer.GetPropertyBlock(block);
                block.SetColor("_PaintColor", identificationTint);
                block.SetFloat("_PaintInfluence", 0.34f);
                renderer.SetPropertyBlock(block);
            }
        }

        private void ApplyWhitePaintPreset(Material material)
        {
            if (material == null)
            {
                return;
            }

            var armorWhite = identificationTint;
            armorWhite.a = 1f;
            SetColor(material, "_Zone1Color", armorWhite);
            SetColor(
                material,
                "_Zone2Color",
                new Color(0.62f, 0.65f, 0.68f, 1f));
            SetColor(
                material,
                "_Zone3Color",
                new Color(0.92f, 0.94f, 0.96f, 1f));
            SetColor(
                material,
                "_Zone4Color",
                new Color(0.10f, 0.12f, 0.14f, 1f));
            SetColor(
                material,
                "_Zone5Color",
                new Color(0.46f, 0.50f, 0.54f, 1f));
            SetColor(
                material,
                "_Zone6Color",
                new Color(0.24f, 0.27f, 0.30f, 1f));
            SetColor(
                material,
                "_Zone7Color",
                new Color(0.035f, 0.045f, 0.055f, 1f));

            // Enemy paint is a true material preset, not a tint layered over
            // the source texture. The source-albedo branch otherwise bypasses
            // every paint and zone color on many imported AC6 sub-materials.
            SetFloat(material, "_UseSourceAlbedo", 0f);
            SetFloat(material, "_ZoneMode", 0f);
            SetFloat(material, "_PaintInfluence", 1f);
            SetFloat(material, "_AlbedoDetailStrength", 0.18f);
            SetColor(material, "_BaseColor", armorWhite);
            SetColor(material, "_PaintColor", armorWhite);
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
