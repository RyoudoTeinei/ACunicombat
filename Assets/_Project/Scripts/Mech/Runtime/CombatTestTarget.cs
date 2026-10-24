using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace ACunicombat.Mechs
{
    public enum TestWeaponKind
    {
        Rifle,
        Cannon,
        Missile,
        PointDefenseCannon,
    }

    public enum CombatTestTargetMotionMode
    {
        Stationary,
        LateralOscillation,
        LinearFlight,
    }

    public readonly struct CombatDamageBreakdown
    {
        public CombatDamageBreakdown(float armorDamage, float structureDamage)
        {
            ArmorDamage = armorDamage;
            StructureDamage = structureDamage;
        }

        public float ArmorDamage { get; }
        public float StructureDamage { get; }
        public float TotalDurabilityDamage => ArmorDamage + StructureDamage;
    }

    [DisallowMultipleComponent]
    public sealed class CombatTestTarget : MonoBehaviour
    {
        public const float ArmoredStructureDamageMultiplier = 0.25f;

        private static CombatTestTarget ownship;

        public static event Action<CombatTestTarget, Transform, TestWeaponKind, float>
            DamageApplied;

        private const string Ac6PartsApproxShaderName =
            "ACunicombat/AC6/Parts Approx";
        private const string DamageHitboxName =
            "Combat Target Damage Hitbox";

        [SerializeField] private string targetId = "SIM-TGT-01";
        [SerializeField] private int teamId = 2;
        [Header("Protection")]
        [SerializeField, Min(0f)] private float maxArmor;
        [FormerlySerializedAs("maxHealth")]
        [SerializeField, Min(1f)] private float maxStructure = 2400f;
        [SerializeField] private float radarCrossSectionSquareMeters = 18f;
        [SerializeField] private float thermalSignature = 8f;
        [SerializeField] private float electromagneticSignature = 10f;
        [SerializeField] private float resetDelaySeconds = 2.5f;
        [SerializeField] private CombatTestTargetMotionMode motionMode =
            CombatTestTargetMotionMode.LateralOscillation;
        [SerializeField] private bool moveLaterally = true;
        [SerializeField] private float lateralAmplitudeMeters = 12f;
        [SerializeField] private float lateralSpeedMetersPerSecond = 3.5f;
        [SerializeField] private float flightSpeedMetersPerSecond = 180f;
        [SerializeField] private Vector3 flightDirection = Vector3.right;
        [SerializeField] private bool orientAlongFlightPath = true;
        [SerializeField] private Transform aimPoint;

        [SerializeField] private bool enablePartDamage = true;
        private bool useUnifiedAP;
        private MechPartDamageController partDamage;
        public MechPartDamageController PartDamage => partDamage;
        public bool UsesUnifiedAP => useUnifiedAP;
        private Renderer[] renderers;
        private Collider[] colliders;
        private BoxCollider damageHitbox;
        private MaterialPropertyBlock propertyBlock;
        private Vector3 origin;
        private Quaternion originRotation;
        private float armor;
        private float structure;
        private float motionElapsedSeconds;
        private float flashUntil;
        private float resetAt = -1f;
        private int hitCount;
        private TestWeaponKind lastHitKind;
        private MechImpactAudioController impactAudio;
        private bool isOwnship;

        public string TargetId => targetId;
        public int TeamId => teamId;
        public bool IsAlive => useUnifiedAP ? armor + structure > 0f : structure > 0f;
        public float Armor => armor;
        public float MaxArmor => maxArmor;
        public float Structure => structure;
        public float MaxStructure => maxStructure;
        public float Health => IsAlive ? armor + structure : 0f;
        public float MaxHealth => maxArmor + maxStructure;
        public float HealthFraction => MaxHealth > 0f ? Mathf.Clamp01(Health / MaxHealth) : 0f;
        public float RadarCrossSectionSquareMeters => radarCrossSectionSquareMeters;
        public float ThermalSignature => thermalSignature * (GetComponent<MechThermalSignature>()?.InfraredSignatureMultiplier ?? 1);
        public float ElectromagneticSignature => electromagneticSignature;
        public Transform AimPoint => aimPoint != null ? aimPoint : transform;
        public int HitCount => hitCount;
        public TestWeaponKind LastHitKind => lastHitKind;
        public static CombatTestTarget Ownship => ownship;
        public CombatTestTargetMotionMode MotionMode => motionMode;
        public float FlightSpeedMetersPerSecond => flightSpeedMetersPerSecond;
        public Vector3 ProceduralVelocity => motionMode switch
        {
            CombatTestTargetMotionMode.LinearFlight =>
                flightDirection * flightSpeedMetersPerSecond,
            CombatTestTargetMotionMode.LateralOscillation
                when moveLaterally &&
                     lateralAmplitudeMeters > 0.01f &&
                     lateralSpeedMetersPerSecond > 0.01f =>
                Vector3.right *
                (Mathf.Cos(
                     motionElapsedSeconds *
                     lateralSpeedMetersPerSecond /
                     lateralAmplitudeMeters) *
                 lateralSpeedMetersPerSecond),
            _ => Vector3.zero,
        };

        public void Configure(
            Transform newAimPoint,
            float newMaxHealth,
            bool shouldMoveLaterally,
            float movementAmplitudeMeters,
            float movementSpeedMetersPerSecond)
        {
            aimPoint = newAimPoint;
            maxArmor = 0f;
            maxStructure = Mathf.Max(1f, newMaxHealth);
            moveLaterally = shouldMoveLaterally;
            motionMode = shouldMoveLaterally
                ? CombatTestTargetMotionMode.LateralOscillation
                : CombatTestTargetMotionMode.Stationary;
            lateralAmplitudeMeters = Mathf.Max(0f, movementAmplitudeMeters);
            lateralSpeedMetersPerSecond = Mathf.Max(0f, movementSpeedMetersPerSecond);
            CacheComponents();
            origin = transform.position;
            originRotation = transform.rotation;
            ResetTarget();
        }

        public void ConfigureLinearFlight(
            Transform newAimPoint,
            float newMaxHealth,
            Vector3 direction,
            float speedMetersPerSecond,
            bool shouldOrientAlongFlightPath = true)
        {
            aimPoint = newAimPoint;
            maxArmor = 0f;
            maxStructure = Mathf.Max(1f, newMaxHealth);
            motionMode = CombatTestTargetMotionMode.LinearFlight;
            moveLaterally = false;
            flightSpeedMetersPerSecond = Mathf.Max(0f, speedMetersPerSecond);
            flightDirection = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.right;
            orientAlongFlightPath = shouldOrientAlongFlightPath;
            CacheComponents();
            origin = transform.position;
            originRotation = transform.rotation;
            ResetTarget();
        }

        public void SetTargetId(string newTargetId)
        {
            if (!string.IsNullOrWhiteSpace(newTargetId))
            {
                targetId = newTargetId;
            }
        }

        public void ConfigureIdentity(string newTargetId, int newTeamId)
        {
            SetTargetId(newTargetId);
            teamId = newTeamId;
        }

        public void ConfigureProtection(float newMaxArmor, float newMaxStructure)
        {
            maxArmor = Mathf.Max(0f, newMaxArmor);
            maxStructure = Mathf.Max(1f, newMaxStructure);
            armor = maxArmor;
            structure = maxStructure;
            resetAt = -1f;
            partDamage?.ResetFunctionalDamage();
        }

        public void RefreshGeometry(bool includePartDamageInEditor = false)
        {
            CacheComponents(includePartDamageInEditor);
        }

        public void ConfigureSignatures(
            float newRadarCrossSectionSquareMeters,
            float newThermalSignature,
            float newElectromagneticSignature)
        {
            radarCrossSectionSquareMeters = Mathf.Max(0.001f, newRadarCrossSectionSquareMeters);
            thermalSignature = Mathf.Max(0.001f, newThermalSignature);
            electromagneticSignature = Mathf.Max(0.001f, newElectromagneticSignature);
        }

        public static CombatDamageBreakdown CalculateProtectionDamage(
            float currentArmor,
            float currentStructure,
            float incomingDamage)
        {
            float sanitizedDamage = Mathf.Max(0f, incomingDamage);
            float protectedDamage = Mathf.Min(Mathf.Max(0f, currentArmor), sanitizedDamage);
            float overflowDamage = Mathf.Max(0f, sanitizedDamage - protectedDamage);
            float structureDamage = protectedDamage * ArmoredStructureDamageMultiplier + overflowDamage;
            return new CombatDamageBreakdown(
                protectedDamage,
                Mathf.Min(Mathf.Max(0f, currentStructure), structureDamage));
        }

        public bool TryApplyDamage(float damage, Vector3 hitPoint, Vector3 hitDirection, TestWeaponKind weaponKind,
            out CombatDamageBreakdown breakdown, Transform damageSource=null, Collider hitCollider=null, Guid hitEventId=default, Vector3? blastOrigin=null, float blastRadius=0)
        {
            breakdown=default;
            if (!IsAlive || !float.IsFinite(damage) || damage <= 0f)
            {
                return false;
            }

            if (partDamage != null && (!partDamage.IsCurrentHitRegion(hitCollider) || !partDamage.TryAcceptHit(hitEventId))) return false;
            if (blastOrigin.HasValue && blastRadius > 0 && partDamage != null && !partDamage.HasBlastExposure(blastOrigin.Value,blastRadius)) return false;
            bool armorTriggered=false;
            float residual=partDamage!=null?partDamage.ResolveReactiveArmor(damage,hitPoint,
                blastOrigin.HasValue?hitPoint-blastOrigin.Value:hitDirection,weaponKind,hitCollider,out armorTriggered):damage;
            breakdown = CalculateProtectionDamage(armor, structure, residual);
            armor = Mathf.Max(0f, armor - breakdown.ArmorDamage);
            structure = Mathf.Max(0f, structure - breakdown.StructureDamage);
            bool damageApplied = breakdown.TotalDurabilityDamage > 0f;
            if(damageApplied)GetComponent<MechIdentityController>()?.ObserveImpact(hitDirection,hitEventId);
            partDamage?.RecordHit(breakdown.TotalDurabilityDamage, hitPoint, hitCollider, blastOrigin, blastRadius);
            hitCount++;
            lastHitKind = weaponKind;
            DamageApplied?.Invoke(
                this,
                damageSource,
                weaponKind,
                damage);
            if (damageApplied)
            {
                flashUntil = Time.time + 0.11f;
            }
            if (!IsAlive)
            {
                SetVisibleAndCollidable(false);
                resetAt = isOwnship ? Time.time + resetDelaySeconds : -1f;
            }
            return damageApplied || armorTriggered;
        }

        public void ApplyDamage(float damage, Vector3 hitPoint, Vector3 hitDirection, TestWeaponKind weaponKind,
            MechEffectPrefabLibrary effectLibrary=null, int impactFfxId=0, Transform damageSource=null,
            Collider hitCollider=null, Guid hitEventId=default, Vector3? blastOrigin=null, float blastRadius=0)
        {
            if(!TryApplyDamage(damage,hitPoint,hitDirection,weaponKind,out var breakdown,damageSource,hitCollider,hitEventId,blastOrigin,blastRadius))return;
            MechTestWeaponSystem.SpawnImpact(hitPoint,-hitDirection,weaponKind,effectLibrary,impactFfxId);
            EnsureImpactAudio();impactAudio.PlayArmorImpact(weaponKind,damage,hitPoint,isOwnship);
        }

        public void PlayNearbyFlakAirburst(
            Vector3 detonationPoint,
            bool fragmentsReached)
        {
            EnsureImpactAudio();
            impactAudio.PlayFlakAirburst(
                detonationPoint, fragmentsReached);
        }

        private void Awake()
        {
            ACunicombat.Simulation.Driver.BattlefieldEntityIdentity.GetOrAdd(gameObject);
            CacheComponents();
            EnsureImpactAudio();
            origin = transform.position;
            originRotation = transform.rotation;
            armor = maxArmor;
            structure = maxStructure;
        }

        private void EnsureImpactAudio()
        {
            if (impactAudio == null)
            {
                impactAudio = GetComponent<MechImpactAudioController>();
                if (impactAudio == null)
                {
                    impactAudio = gameObject.AddComponent<MechImpactAudioController>();
                }
            }

            isOwnship = GetComponent<MechPlayerRig>() != null &&
                        GetComponent<MechAiTargetController>() == null;
            if (isOwnship)
            {
                ownship = this;
            }
        }

        private void OnDestroy()
        {
            if (ownship == this)
            {
                ownship = null;
            }
        }

        [RuntimeInitializeOnLoadMethod(
            RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOwnship()
        {
            ownship = null;
            DamageApplied = null;
        }

        private void Update()
        {
            if (!IsAlive)
            {
                if (isOwnship && resetAt > 0f && Time.time >= resetAt)
                {
                    ResetTarget();
                }
                return;
            }

            ApplyHitFlash(Time.time < flashUntil);
        }

        private void FixedUpdate()
        {
            if (!IsAlive)
            {
                return;
            }

            motionElapsedSeconds += Time.fixedDeltaTime;
            if (motionMode == CombatTestTargetMotionMode.LinearFlight)
            {
                var velocity = flightDirection * flightSpeedMetersPerSecond;
                transform.position = origin + velocity * motionElapsedSeconds;
                if (orientAlongFlightPath && velocity.sqrMagnitude > 0.001f)
                {
                    transform.rotation = Quaternion.LookRotation(velocity.normalized, Vector3.up);
                }
            }
            else if (motionMode == CombatTestTargetMotionMode.LateralOscillation &&
                     moveLaterally && lateralAmplitudeMeters > 0.01f &&
                     lateralSpeedMetersPerSecond > 0.01f)
            {
                var angularSpeed = lateralSpeedMetersPerSecond / lateralAmplitudeMeters;
                var offset = Mathf.Sin(motionElapsedSeconds * angularSpeed) * lateralAmplitudeMeters;
                transform.position = origin + Vector3.right * offset;
            }
        }

        private void ResetTarget()
        {
            ACunicombat.Simulation.Driver.BattlefieldEntityIdentity.GetOrAdd(gameObject).RestartLifetime();
            armor = maxArmor;
            structure = maxStructure;
            resetAt = -1f;
            flashUntil = -1f;
            motionElapsedSeconds = 0f;
            transform.position = origin;
            transform.rotation = originRotation;
            partDamage?.ResetFunctionalDamage(true);
            SetVisibleAndCollidable(true);
            ApplyHitFlash(false);
        }

        private void CacheComponents(bool includePartDamageInEditor = false)
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            EnsureDamageHitbox();
            var runtime = GetComponent<MechRuntimeState>();
            if (enablePartDamage && runtime != null && runtime.AssembledParts.Count > 0 &&
                (Application.isPlaying || includePartDamageInEditor || (partDamage != null && partDamage.IsBound)))
            {
                if (partDamage == null) partDamage = GetComponent<MechPartDamageController>();
                if (partDamage == null) partDamage = gameObject.AddComponent<MechPartDamageController>();
                partDamage.Bind(runtime, MaxHealth);
                useUnifiedAP = partDamage.IsBound;
                if (Application.isPlaying && GetComponent<MechPlayerRig>() != null && GetComponent<MechAiTargetController>() == null && GetComponent<MechDamageDiagramHud>() == null)
                    gameObject.AddComponent<MechDamageDiagramHud>();
                damageHitbox.enabled = !partDamage.HasHitRegions;
            }
            colliders = GetComponentsInChildren<Collider>(true);
            propertyBlock ??= new MaterialPropertyBlock();
        }

        private void EnsureDamageHitbox()
        {
            var hitboxTransform = transform.Find(DamageHitboxName);
            if (hitboxTransform == null)
            {
                hitboxTransform =
                    new GameObject(DamageHitboxName).transform;
                hitboxTransform.SetParent(transform, false);
            }

            damageHitbox =
                hitboxTransform.GetComponent<BoxCollider>();
            if (damageHitbox == null)
            {
                damageHitbox =
                    hitboxTransform.gameObject.AddComponent<BoxCollider>();
            }

            hitboxTransform.localPosition = Vector3.zero;
            hitboxTransform.localRotation = Quaternion.identity;
            hitboxTransform.localScale = Vector3.one;
            damageHitbox.isTrigger = true;

            if (!TryCalculateLocalRenderBounds(
                    out var center,
                    out var size))
            {
                center = new Vector3(0f, 5f, 0f);
                size = new Vector3(8f, 12f, 6f);
            }

            const float paddingMeters = 0.5f;
            damageHitbox.center = center;
            damageHitbox.size = new Vector3(
                Mathf.Max(1f, size.x + paddingMeters),
                Mathf.Max(1f, size.y + paddingMeters),
                Mathf.Max(1f, size.z + paddingMeters));
        }

        private bool TryCalculateLocalRenderBounds(
            out Vector3 center,
            out Vector3 size)
        {
            var minimum = new Vector3(
                float.PositiveInfinity,
                float.PositiveInfinity,
                float.PositiveInfinity);
            var maximum = new Vector3(
                float.NegativeInfinity,
                float.NegativeInfinity,
                float.NegativeInfinity);
            var found = false;
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null ||
                    renderer is ParticleSystemRenderer ||
                    renderer is TrailRenderer ||
                    renderer is LineRenderer)
                {
                    continue;
                }

                var bounds = renderer.bounds;
                var boundsCenter = bounds.center;
                var extents = bounds.extents;
                for (var corner = 0; corner < 8; corner++)
                {
                    var worldCorner = boundsCenter + new Vector3(
                        (corner & 1) == 0 ? -extents.x : extents.x,
                        (corner & 2) == 0 ? -extents.y : extents.y,
                        (corner & 4) == 0 ? -extents.z : extents.z);
                    var localCorner =
                        transform.InverseTransformPoint(worldCorner);
                    minimum = Vector3.Min(minimum, localCorner);
                    maximum = Vector3.Max(maximum, localCorner);
                    found = true;
                }
            }

            if (!found)
            {
                center = default;
                size = default;
                return false;
            }

            center = (minimum + maximum) * 0.5f;
            size = maximum - minimum;
            return true;
        }

        private void SetVisibleAndCollidable(bool value)
        {
            if (renderers == null || colliders == null)
            {
                CacheComponents();
            }

            for (var i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) renderers[i].enabled = value;
            }
            for (var i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null) colliders[i].enabled = value && !(colliders[i] == damageHitbox && partDamage != null && partDamage.HasHitRegions);
            }
            if(value)partDamage?.EnforceRegionColliders();
        }

        private void ApplyHitFlash(bool active)
        {
            if (renderers == null)
            {
                CacheComponents();
            }

            var emission = active ? new Color(3.5f, 0.25f, 0.08f, 1f) : Color.black;
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null ||
                    UsesAc6PartsApproxShader(renderer))
                {
                    continue;
                }

                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor("_EmissionColor", emission);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }

        private static bool UsesAc6PartsApproxShader(Renderer renderer)
        {
            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                var shader = materials[i] != null ? materials[i].shader : null;
                if (shader != null && shader.name == Ac6PartsApproxShaderName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
