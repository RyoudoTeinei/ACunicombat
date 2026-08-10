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

        [SerializeField] private CombatTestTarget objective;
        [SerializeField] private Camera sensorCamera;
        [SerializeField, Min(100f)] private float minimumCombatRangeMeters = 3000f;
        [SerializeField, Min(100f)] private float maximumCombatRangeMeters = 5000f;
        [SerializeField, Min(1f)] private float missileCooldownSeconds = 8f;
        [SerializeField, Min(0.5f)] private float interceptorCooldownSeconds = 4f;
        [SerializeField] private float lateralWeavePeriodSeconds = 5f;
        [Header("Weapon doctrine")]
        [SerializeField, Min(1)] private int rifleBurstSize = 4;
        [SerializeField, Min(0.05f)] private float rifleShotIntervalSeconds = 0.1f;
        [SerializeField, Min(0.1f)] private float rifleBurstCooldownSeconds = 1.15f;
        [SerializeField, Min(100f)] private float rifleMaximumRangeMeters = 4500f;
        [SerializeField, Min(100f)] private float cannonMaximumRangeMeters = 3500f;
        [SerializeField, Min(100f)] private float missileMinimumRangeMeters = 2500f;
        [SerializeField, Min(1000f)] private float missileMaximumRangeMeters = 20000f;
        [Header("Defensive doctrine")]
        [SerializeField, Min(0.5f)] private float defensiveThreatTimeSeconds = 8f;
        [SerializeField, Min(0.1f)] private float defensiveQuickBoostCooldownSeconds = 1.2f;
        [SerializeField] private bool stationaryTestMode;
        [SerializeField] private Color identificationTint =
            new Color(0.96f, 0.97f, 0.98f, 1f);

        private readonly List<Material> ownedPaintMaterials = new();
        private readonly TrackSnapshot[] airTrackScratch =
            new TrackSnapshot[MechRadarController.MaximumPublicTracks];
        private readonly MissileTrackSnapshot[] missileTrackScratch =
            new MissileTrackSnapshot[MechRadarController.MaximumPublicTracks];
        private MechPlayerInputAdapter input;
        private MechPlayerRig rig;
        private MechFlightMotionController motion;
        private MechRadarController radar;
        private MechTestRadarEmitter radarEmitter;
        private MechTestWeaponSystem weapons;
        private MechWeaponArmLayer weaponAnimationLayer;
        private CombatTestTarget selfTarget;
        private TrackId selectedAirTrackId;
        private TrackSnapshot lastAirTrack;
        private bool hasLastAirTrack;
        private float nextHandWeaponTime;
        private float nextMissileTime;
        private float nextMissileAttemptTime;
        private float nextInterceptorTime;
        private float nextInterceptorRadarSweepTime;
        private float nextRifleShotTime;
        private float nextRifleBurstTime;
        private float nextDefensiveQuickBoostTime;
        private float lastThreatSeenTime = float.NegativeInfinity;
        private int rifleBurstShotsRemaining;
        private float longitudinalCommand;
        private float defensiveDodgeSign = 1f;
        private bool incomingThreat;
        private float incomingThreatTime = float.PositiveInfinity;
        private CombatState combatState;

        public CombatState CurrentCombatState => combatState;

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
            nextHandWeaponTime = Time.time + Random.Range(1.5f, 2.5f);
            nextRifleBurstTime = Time.time + Random.Range(0.6f, 1.2f);
            nextMissileTime = Time.time + missileCooldownSeconds;
            nextInterceptorTime = Time.time + 1f;
            nextInterceptorRadarSweepTime = Time.time;
        }

        private void Start()
        {
            if (rig != null)
            {
                rig.ConfigureAiControl();
                rig.enabled = true;
            }

            ApplyIdentificationTint();
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
                return;
            }

            if ((selfTarget != null && !selfTarget.IsAlive) ||
                !objective.IsAlive)
            {
                combatState = CombatState.Disabled;
                input.SubmitExternalFrame(default);
                return;
            }

            UpdateMissileDefense();

            if (!TryGetAirTargetTrack(out var targetTrack))
            {
                combatState = incomingThreat
                    ? CombatState.Defensive
                    : CombatState.Search;
                var searchDirection = GetSearchDirection();
                UpdateSensorCamera(searchDirection);
                if (!stationaryTestMode)
                {
                    CommandSearchAttitude();
                }
                var searchFrame = stationaryTestMode
                    ? default
                    : BuildSearchFrame();
                input.SubmitExternalFrame(in searchFrame);
                return;
            }

            var estimatedPosition = radar.PredictTrackPosition(in targetTrack) +
                                    ToVector3(targetTrack.Velocity) * 0.12f;
            var toTarget = estimatedPosition - transform.position;
            var range = toTarget.magnitude;
            if (range <= 0.01f)
            {
                return;
            }

            var direction = toTarget / range;
            UpdateSensorCamera(direction);
            combatState = SelectCombatState(range);
            if (!stationaryTestMode)
            {
                CommandAttitude(direction);
            }
            var frame = stationaryTestMode
                ? default
                : BuildFlightFrame(
                    range,
                    direction,
                    ToVector3(targetTrack.Velocity),
                    combatState);
            input.SubmitExternalFrame(in frame);

            if (combatState != CombatState.Defensive)
            {
                UpdateWeapons(in targetTrack, estimatedPosition, range);
            }
        }

        private CombatState SelectCombatState(float range)
        {
            if (incomingThreat &&
                incomingThreatTime <= defensiveThreatTimeSeconds)
            {
                return CombatState.Defensive;
            }

            if (range > maximumCombatRangeMeters)
            {
                return CombatState.Intercept;
            }
            if (range < minimumCombatRangeMeters)
            {
                return CombatState.Extend;
            }
            return CombatState.Engage;
        }

        private void UpdateWeapons(
            in TrackSnapshot track,
            Vector3 estimatedPosition,
            float range)
        {
            if (weapons == null || radar == null ||
                !radar.TryAssignFireControlTrack(track.TrackId))
            {
                rifleBurstShotsRemaining = 0;
                return;
            }

            var bodyForward = motion.LogicalRotation * Vector3.forward;
            var toTarget = estimatedPosition - transform.position;
            var angularError = toTarget.sqrMagnitude > 0.01f
                ? Vector3.Angle(bodyForward, toTarget)
                : 180f;

            if (range <= rifleMaximumRangeMeters && angularError <= 6f)
            {
                if (rifleBurstShotsRemaining <= 0 &&
                    Time.time >= nextRifleBurstTime)
                {
                    rifleBurstShotsRemaining = Mathf.Max(1, rifleBurstSize);
                    nextRifleShotTime = Time.time;
                }

                if (rifleBurstShotsRemaining > 0 &&
                    Time.time >= nextRifleShotTime)
                {
                    var fired = weapons.CommandFireHandWeaponAtTrack(
                        TestWeaponKind.Rifle,
                        track.TrackId);
                    nextRifleShotTime = Time.time + rifleShotIntervalSeconds;
                    if (fired)
                    {
                        rifleBurstShotsRemaining--;
                        weapons.TryGetWeaponSlot(
                            MechWeaponRole.AutomaticCannon,
                            out var rifleSlot);
                        weaponAnimationLayer?.NotifyExternalFire(
                            rightHand: rifleSlot == MechPartSlot.RightHandWeapon,
                            holdSeconds: rifleShotIntervalSeconds + 0.04f);
                    }
                    else if (weapons.RifleReloading)
                    {
                        rifleBurstShotsRemaining = 0;
                    }

                    if (rifleBurstShotsRemaining <= 0)
                    {
                        nextRifleBurstTime =
                            Time.time + rifleBurstCooldownSeconds;
                    }
                }
            }
            else
            {
                rifleBurstShotsRemaining = 0;
            }

            if (range <= cannonMaximumRangeMeters &&
                angularError <= 8f &&
                track.Confidence >= 0.66f &&
                Time.time >= nextHandWeaponTime)
            {
                var fired = weapons.CommandFireHandWeaponAtTrack(
                    TestWeaponKind.Cannon,
                    track.TrackId);
                nextHandWeaponTime = Time.time + (fired ? 4.4f : 0.25f);
                if (fired)
                {
                    weapons.TryGetWeaponSlot(
                        MechWeaponRole.HandCannon,
                        out var cannonSlot);
                    weaponAnimationLayer?.NotifyExternalFire(
                        rightHand: cannonSlot == MechPartSlot.RightHandWeapon,
                        holdSeconds: 0.14f);
                }
            }

            var uncertaintyMeters = CalculatePositionUncertainty(in track);
            var missileUncertaintyLimit = Mathf.Max(120f, range * 0.025f);
            if (range >= missileMinimumRangeMeters &&
                range <= missileMaximumRangeMeters &&
                angularError <= 24f &&
                track.Confidence >= 0.72f &&
                uncertaintyMeters <= missileUncertaintyLimit &&
                Time.time >= nextMissileTime &&
                Time.time >= nextMissileAttemptTime)
            {
                nextMissileAttemptTime = Time.time + 0.25f;
                if (weapons.TryGetWeaponSlot(
                    MechWeaponRole.AirToAirMissile,
                    out var missileSlot) &&
                    weapons.CommandFireMissile(missileSlot))
                {
                    nextMissileTime =
                        Time.time + missileCooldownSeconds;
                }
            }
        }

        private Vector3 GetSearchDirection()
        {
            var referenceUp = motion.ReferenceUp;
            var forward = motion.LogicalRotation * Vector3.forward;
            var scanAngle = Mathf.Sin(Time.time * 0.55f) * 42f;
            return Quaternion.AngleAxis(scanAngle, referenceUp) * forward;
        }

        private void CommandSearchAttitude()
        {
            motion.SetYawTurnInput(Mathf.Sin(Time.time * 0.38f) * 0.42f);
            motion.SetAimPitch(Mathf.Sin(Time.time * 0.27f) * 12f);
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

        private MechPlayerInputFrame BuildFlightFrame(
            float range,
            Vector3 directionToTarget,
            Vector3 targetVelocity,
            CombatState state)
        {
            var relativeRangeRate = Vector3.Dot(
                targetVelocity - motion.Velocity,
                directionToTarget);
            float desiredLongitudinal;
            if (state == CombatState.Intercept)
            {
                desiredLongitudinal = Mathf.Clamp(
                    0.35f +
                    (range - maximumCombatRangeMeters) / 2000f,
                    0.35f,
                    1f);
            }
            else if (state == CombatState.Extend)
            {
                desiredLongitudinal = -Mathf.Clamp(
                    0.35f +
                    (minimumCombatRangeMeters - range) / 2000f,
                    0.35f,
                    1f);
            }
            else
            {
                // 在交战距离带内只抵消径向漂移，不强迫收敛到单一半径。
                desiredLongitudinal = Mathf.Clamp(
                    relativeRangeRate / 120f,
                    -0.35f,
                    0.35f);
            }

            longitudinalCommand = Mathf.MoveTowards(
                longitudinalCommand,
                desiredLongitudinal,
                Time.deltaTime * 1.4f);
            var weave = Mathf.Sin(
                Time.time * Mathf.PI * 2f /
                Mathf.Max(1f, lateralWeavePeriodSeconds));
            var defensive = state == CombatState.Defensive;
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
                MoveLocal = new Vector2(
                    defensive
                        ? defensiveDodgeSign
                        : weave * (state == CombatState.Engage ? 0.36f : 0.2f),
                    defensive ? -0.2f : longitudinalCommand),
                QuickBoostEdge = quickBoost,
                BrakeHeld = false,
            };
        }

        private void CommandAttitude(Vector3 direction)
        {
            var referenceUp = motion.ReferenceUp;
            var flatDirection =
                Vector3.ProjectOnPlane(direction, referenceUp);
            if (flatDirection.sqrMagnitude > 0.0001f)
            {
                var currentForward = Vector3.ProjectOnPlane(
                    motion.LogicalRotation * Vector3.forward,
                    referenceUp);
                var yawError = Vector3.SignedAngle(
                    currentForward,
                    flatDirection,
                    referenceUp);
                motion.SetYawTurnInput(Mathf.Clamp(yawError / 35f, -1f, 1f));
            }

            var elevation = Mathf.Asin(
                Mathf.Clamp(Vector3.Dot(direction, referenceUp), -1f, 1f)) *
                Mathf.Rad2Deg;
            motion.SetAimPitch(elevation);
        }

        private void UpdateSensorCamera(Vector3 direction)
        {
            if (sensorCamera == null)
            {
                return;
            }

            sensorCamera.transform.position =
                transform.position + motion.ReferenceUp * 6f;
            sensorCamera.transform.rotation = Quaternion.LookRotation(
                direction,
                motion.ReferenceUp);
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
