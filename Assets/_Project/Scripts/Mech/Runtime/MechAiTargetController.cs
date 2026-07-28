using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public sealed class MechAiTargetController : MonoBehaviour
    {
        private const string Ac6PartsApproxShaderName =
            "ACunicombat/AC6/Parts Approx";
        private const float InterceptorRadarRevisitSeconds = 0.02f;
        private const int InterceptorClassificationHits = 2;
        private const int InterceptorConfirmationHits = 4;
        private const float InterceptorConfirmationConfidence = 0.55f;
        private const float InterceptorMawRangeMeters = 8000f;
        private const float InterceptorTrackMaximumAgeSeconds = 0.6f;

        [SerializeField] private CombatTestTarget objective;
        [SerializeField] private Camera sensorCamera;
        [SerializeField, Min(100f)] private float minimumCombatRangeMeters = 3000f;
        [SerializeField, Min(100f)] private float maximumCombatRangeMeters = 5000f;
        [SerializeField, Min(1000f)] private float radarTrackingRangeMeters = 50000f;
        [SerializeField, Min(1000f)] private float missileEngagementRangeMeters = 30000f;
        [SerializeField] private float handWeaponIntervalSeconds = 1.4f;
        [SerializeField, Min(1f)] private float missileCooldownSeconds = 8f;
        [SerializeField, Min(0.5f)] private float interceptorCooldownSeconds = 4f;
        [SerializeField] private float lateralWeavePeriodSeconds = 5f;
        [SerializeField] private bool stationaryTestMode;
        [SerializeField] private Color identificationTint =
            new Color(0.96f, 0.97f, 0.98f, 1f);

        private readonly List<Material> ownedPaintMaterials = new();
        private readonly Dictionary<MechTestProjectile, InterceptorContact>
            interceptorContacts = new();
        private readonly List<MechTestProjectile>
            staleInterceptorContacts = new();
        private MechPlayerInputAdapter input;
        private MechPlayerRig rig;
        private MechFlightMotionController motion;
        private MechFlightMotionController objectiveMotion;
        private MechRadarController radar;
        private MechTestRadarEmitter radarEmitter;
        private MechTestWeaponSystem weapons;
        private float nextHandWeaponTime;
        private float nextMissileTime;
        private float nextInterceptorTime;
        private float nextInterceptorRadarSweepTime;
        private float longitudinalCommand;
        private bool fireCannonNext;

        public void Configure(CombatTestTarget newObjective, Camera newSensorCamera)
        {
            objective = newObjective;
            sensorCamera = newSensorCamera;
            objectiveMotion = objective != null
                ? objective.GetComponent<MechFlightMotionController>()
                : null;
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
            objectiveMotion = objective != null
                ? objective.GetComponent<MechFlightMotionController>()
                : null;
            radar = GetComponent<MechRadarController>();
            EnsureRadarEmitterConfigured();
            weapons = GetComponent<MechTestWeaponSystem>();
            var trajectoryPredictor = GetComponent<MechTrajectoryPredictor>();
            if (trajectoryPredictor != null)
            {
                trajectoryPredictor.enabled = false;
            }
            input?.SetExternalControl(true);
            if (radar != null && radar.isActiveAndEnabled)
            {
                radar.SetTrackOperationMode(true);
            }
            if (stationaryTestMode &&
                TryGetComponent<Rigidbody>(out var body))
            {
                body.constraints =
                    RigidbodyConstraints.FreezePosition |
                    RigidbodyConstraints.FreezeRotation;
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            nextHandWeaponTime = Time.time + Random.Range(0.8f, 1.8f);
            if (Mathf.Abs(missileCooldownSeconds - 20f) < 0.01f)
            {
                missileCooldownSeconds = 8f;
            }
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
            if (input == null || motion == null || objective == null ||
                !objective.IsAlive)
            {
                return;
            }

            var aimPoint = objective.AimPoint.position;
            var toTarget = aimPoint - transform.position;
            var range = toTarget.magnitude;
            if (range <= 0.01f)
            {
                return;
            }

            var direction = toTarget / range;
            UpdateSensorCamera(direction);
            if (!stationaryTestMode)
            {
                CommandAttitude(direction);
            }

            var frame = stationaryTestMode
                ? default
                : BuildFlightFrame(range, direction);
            input.SubmitExternalFrame(in frame);
            UpdateMissileDefense();

            if (radar != null &&
                radar.isActiveAndEnabled &&
                sensorCamera != null)
            {
                radar.UpdateDesignation(
                    sensorCamera,
                    new Vector2(0.5f, 0.5f),
                    Time.deltaTime);
            }

            if (radarEmitter == null)
            {
                EnsureRadarEmitterConfigured();
            }
            var hasFireControlTrack =
                radar != null &&
                radar.isActiveAndEnabled &&
                radar.HasStt;
            var hasEmitterLock =
                radarEmitter != null &&
                radarEmitter.HasPlayerLock;
            if (weapons == null ||
                (!hasFireControlTrack && !hasEmitterLock))
            {
                return;
            }

            if (Time.time >= nextHandWeaponTime && range <= 5000f)
            {
                weapons.CommandFireHandWeapon(
                    fireCannonNext
                        ? TestWeaponKind.Cannon
                        : TestWeaponKind.Rifle);
                fireCannonNext = !fireCannonNext;
                nextHandWeaponTime =
                    Time.time + handWeaponIntervalSeconds;
            }

            if (Time.time >= nextMissileTime &&
                range <= missileEngagementRangeMeters)
            {
                weapons.CommandFireMissileAt(
                    MechPartSlot.RightBackWeapon,
                    objective);
                nextMissileTime = Time.time + missileCooldownSeconds;
            }
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

            radarEmitter.ConfigureTarget(
                objective != null ? objective.transform : null,
                radarTrackingRangeMeters);
        }

        private void UpdateMissileDefense()
        {
            if (weapons == null ||
                Time.time < nextInterceptorRadarSweepTime)
            {
                return;
            }

            nextInterceptorRadarSweepTime =
                Time.time + InterceptorRadarRevisitSeconds;
            var projectiles = MechTestProjectile.Registry;
            MechTestProjectile bestThreat = null;
            var bestTime = float.PositiveInfinity;
            for (var i = 0; i < projectiles.Count; i++)
            {
                var candidate = projectiles[i];
                if (candidate == null ||
                    candidate.IsOwnedBy(transform) ||
                    !candidate.IsInterceptableMissile)
                {
                    continue;
                }

                var range = Vector3.Distance(
                    transform.position,
                    candidate.transform.position);
                var direction =
                    candidate.transform.position - transform.position;
                if (range > radarTrackingRangeMeters ||
                    !IsWithinInterceptorRadarFrame(direction))
                {
                    continue;
                }

                if (!interceptorContacts.TryGetValue(
                        candidate,
                        out var contact))
                {
                    contact = new InterceptorContact();
                    interceptorContacts.Add(candidate, contact);
                }

                contact.RadarHitCount++;
                contact.ClassificationConfidence = Mathf.Clamp01(
                    contact.RadarHitCount * 0.16f);
                contact.ClassifiedAsMissile =
                    contact.RadarHitCount >=
                    InterceptorClassificationHits;
                contact.Confirmed =
                    contact.RadarHitCount >=
                    InterceptorConfirmationHits &&
                    contact.ClassificationConfidence >=
                    InterceptorConfirmationConfidence;
                contact.RwrOrMawCorrelated |=
                    candidate.HasActiveGuidance ||
                    range <= InterceptorMawRangeMeters;
                contact.LastUpdateTime = Time.time;

                if (Time.time < nextInterceptorTime ||
                    !contact.ClassifiedAsMissile ||
                    !contact.Confirmed ||
                    !contact.RwrOrMawCorrelated ||
                    !candidate.TryPredictThreatTo(
                        transform,
                        motion.Velocity,
                        protectedRadius: 180f,
                        predictionHorizon: 60f,
                        out var timeToClosestApproach,
                        out _))
                {
                    continue;
                }

                if (timeToClosestApproach < bestTime)
                {
                    bestTime = timeToClosestApproach;
                    bestThreat = candidate;
                }
            }

            RemoveStaleInterceptorContacts();
            if (bestThreat == null)
            {
                return;
            }

            weapons.CommandFireInterceptorAt(bestThreat);
            interceptorContacts.Remove(bestThreat);
            nextInterceptorTime =
                Time.time + interceptorCooldownSeconds;
        }

        private bool IsWithinInterceptorRadarFrame(Vector3 worldDirection)
        {
            if (worldDirection.sqrMagnitude < 0.001f)
            {
                return false;
            }

            var localDirection =
                transform.InverseTransformDirection(
                    worldDirection.normalized);
            if (localDirection.z <= 0f)
            {
                return false;
            }

            var azimuth = Mathf.Atan2(
                    localDirection.x,
                    localDirection.z) *
                Mathf.Rad2Deg;
            var elevation = Mathf.Asin(
                    Mathf.Clamp(localDirection.y, -1f, 1f)) *
                Mathf.Rad2Deg;
            return Mathf.Abs(azimuth) <= 50f &&
                   Mathf.Abs(elevation) <= 30f;
        }

        private void RemoveStaleInterceptorContacts()
        {
            staleInterceptorContacts.Clear();
            foreach (var pair in interceptorContacts)
            {
                if (Time.time - pair.Value.LastUpdateTime >
                    InterceptorTrackMaximumAgeSeconds)
                {
                    staleInterceptorContacts.Add(pair.Key);
                }
            }

            for (var i = 0; i < staleInterceptorContacts.Count; i++)
            {
                interceptorContacts.Remove(
                    staleInterceptorContacts[i]);
            }
        }

        private sealed class InterceptorContact
        {
            public int RadarHitCount;
            public float ClassificationConfidence;
            public float LastUpdateTime;
            public bool ClassifiedAsMissile;
            public bool Confirmed;
            public bool RwrOrMawCorrelated;
        }

        private MechPlayerInputFrame BuildFlightFrame(
            float range,
            Vector3 directionToTarget)
        {
            var relativeRangeRate = Vector3.Dot(
                (objectiveMotion != null
                    ? objectiveMotion.Velocity
                    : Vector3.zero) - motion.Velocity,
                directionToTarget);
            float desiredLongitudinal;
            if (range > maximumCombatRangeMeters)
            {
                desiredLongitudinal = Mathf.Clamp(
                    0.35f +
                    (range - maximumCombatRangeMeters) / 2000f,
                    0.35f,
                    1f);
            }
            else if (range < minimumCombatRangeMeters)
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
            return new MechPlayerInputFrame
            {
                MoveLocal = new Vector2(
                    weave * 0.24f,
                    longitudinalCommand),
                MainThrottle = 0f,
                BrakeHeld = false,
                FlightEnvelopeOverrideHeld = false,
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
