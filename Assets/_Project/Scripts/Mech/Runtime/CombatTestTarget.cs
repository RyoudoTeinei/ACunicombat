using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum TestWeaponKind
    {
        Rifle,
        Cannon,
        Missile,
    }

    public enum CombatTestTargetMotionMode
    {
        Stationary,
        LateralOscillation,
        LinearFlight,
    }

    [DisallowMultipleComponent]
    public sealed class CombatTestTarget : MonoBehaviour
    {
        private static CombatTestTarget ownship;

        private const string Ac6PartsApproxShaderName =
            "ACunicombat/AC6/Parts Approx";
        private const string DamageHitboxName =
            "Combat Target Damage Hitbox";

        [SerializeField] private string targetId = "SIM-TGT-01";
        [SerializeField] private int teamId = 2;
        [SerializeField] private float maxHealth = 2400f;
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

        private Renderer[] renderers;
        private Collider[] colliders;
        private BoxCollider damageHitbox;
        private MaterialPropertyBlock propertyBlock;
        private Vector3 origin;
        private Quaternion originRotation;
        private float health;
        private float motionElapsedSeconds;
        private float flashUntil;
        private float resetAt = -1f;
        private int hitCount;
        private TestWeaponKind lastHitKind;
        private MechImpactAudioController impactAudio;
        private bool isOwnship;

        public string TargetId => targetId;
        public int TeamId => teamId;
        public bool IsAlive => health > 0f;
        public float Health => health;
        public float MaxHealth => maxHealth;
        public float HealthFraction => maxHealth > 0f ? Mathf.Clamp01(health / maxHealth) : 0f;
        public float RadarCrossSectionSquareMeters => radarCrossSectionSquareMeters;
        public float ThermalSignature => thermalSignature;
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
            maxHealth = Mathf.Max(1f, newMaxHealth);
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
            maxHealth = Mathf.Max(1f, newMaxHealth);
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

        public void ApplyDamage(
            float damage,
            Vector3 hitPoint,
            Vector3 hitDirection,
            TestWeaponKind weaponKind,
            MechEffectPrefabLibrary effectLibrary = null,
            int impactFfxId = 0)
        {
            if (!IsAlive || damage <= 0f)
            {
                return;
            }

            health = Mathf.Max(0f, health - damage);
            hitCount++;
            lastHitKind = weaponKind;
            flashUntil = Time.time + 0.11f;
            MechTestWeaponSystem.SpawnImpact(
                hitPoint, -hitDirection, weaponKind, effectLibrary, impactFfxId);
            EnsureImpactAudio();
            impactAudio.PlayArmorImpact(
                weaponKind, damage, hitPoint, isOwnship);

            if (health <= 0f)
            {
                SetVisibleAndCollidable(false);
                resetAt = Time.time + resetDelaySeconds;
            }
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
            // Migrate the airborne prototype target saved with the previous default.
            if (motionMode == CombatTestTargetMotionMode.LinearFlight &&
                Mathf.Abs(flightSpeedMetersPerSecond - 100f) < 0.01f)
            {
                flightSpeedMetersPerSecond = 180f;
            }

            CacheComponents();
            EnsureImpactAudio();
            origin = transform.position;
            originRotation = transform.rotation;
            health = maxHealth;
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
        }

        private void Update()
        {
            if (!IsAlive)
            {
                if (resetAt > 0f && Time.time >= resetAt)
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
            health = maxHealth;
            resetAt = -1f;
            flashUntil = -1f;
            motionElapsedSeconds = 0f;
            transform.position = origin;
            transform.rotation = originRotation;
            SetVisibleAndCollidable(true);
            ApplyHitFlash(false);
        }

        private void CacheComponents()
        {
            renderers = GetComponentsInChildren<Renderer>(true);
            EnsureDamageHitbox();
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
                if (colliders[i] != null) colliders[i].enabled = value;
            }
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
