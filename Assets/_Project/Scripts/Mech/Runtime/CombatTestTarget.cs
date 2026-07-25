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
        private MaterialPropertyBlock propertyBlock;
        private Vector3 origin;
        private Quaternion originRotation;
        private float health;
        private float motionElapsedSeconds;
        private float flashUntil;
        private float resetAt = -1f;
        private int hitCount;
        private TestWeaponKind lastHitKind;

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
        public CombatTestTargetMotionMode MotionMode => motionMode;
        public float FlightSpeedMetersPerSecond => flightSpeedMetersPerSecond;

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

            if (health <= 0f)
            {
                SetVisibleAndCollidable(false);
                resetAt = Time.time + resetDelaySeconds;
            }
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
            origin = transform.position;
            originRotation = transform.rotation;
            health = maxHealth;
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
            colliders = GetComponentsInChildren<Collider>(true);
            propertyBlock ??= new MaterialPropertyBlock();
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
                if (renderer == null) continue;
                renderer.GetPropertyBlock(propertyBlock);
                propertyBlock.SetColor("_EmissionColor", emission);
                renderer.SetPropertyBlock(propertyBlock);
            }
        }
    }
}
