using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechCountermeasureController : MonoBehaviour
    {
        private const string AudioRoot = "Audio/Countermeasures/";

        [Header("Countermeasure dispenser")]
        [SerializeField, Min(1)] private int burstCapacity = 24;
        [SerializeField, Min(0.05f)] private float releaseCooldownSeconds = 0.35f;
        [SerializeField, Min(0.5f)] private float chaffLifetimeSeconds = 7f;
        [SerializeField, Min(0.01f)] private float chaffInitialRcsSquareMeters = 18f;
        [SerializeField, Min(0.5f)] private float flareLifetimeSeconds = 4.5f;

        [Header("Ejection kinematics")]
        [SerializeField] private Vector3 chaffEjectionDirectionLocal = Vector3.back;
        [SerializeField] private Vector3 leftFlareEjectionDirectionLocal =
            new Vector3(-0.45f, 0f, -1f);
        [SerializeField] private Vector3 rightFlareEjectionDirectionLocal =
            new Vector3(0.45f, 0f, -1f);
        [SerializeField, Min(0f)] private float chaffEjectionSpeedMetersPerSecond = 72f;
        [SerializeField, Min(0f)] private float flareEjectionSpeedMetersPerSecond = 96f;
        [SerializeField, Min(0f)] private float chaffCloudVelocitySpreadMetersPerSecond = 3f;
        [SerializeField, Min(0f)] private float flareVelocitySpreadMetersPerSecond = 2.5f;
        [SerializeField, Min(0f)] private float visualChaffVelocitySpreadMetersPerSecond = 7f;

        [Header("Flare smoke")]
        [SerializeField, Min(1f)] private float flareSmokeEmissionRatePerSecond = 32f;
        [SerializeField, Min(0.1f)] private float flareSmokeLifetimeSeconds = 1.8f;
        [SerializeField, Min(0f)] private float flareSmokeRelativeExhaustMetersPerSecond = 18f;
        [SerializeField, Min(0f)] private float flareSmokeExpansionMetersPerSecond = 2.2f;

        [Header("Countermeasure audio")]
        [SerializeField] private bool useInteriorAudioMix;
        [SerializeField, Range(0f, 1f)] private float launchImpactVolume = 0.49f;
        [SerializeField, Range(0f, 1f)] private float pyropatronVolume = 0.39f;
        [SerializeField, Range(0f, 1f)] private float chaffRustleVolume = 0.29f;
        [SerializeField, Range(0f, 1f)] private float flareTailVolume = 0.34f;
        [SerializeField, Range(0f, 1f)] private float farReleaseVolume = 0.29f;
        [SerializeField, Range(0f, 1f)] private float interiorReleaseVolume = 0.32f;

        private MechFlightMotionController motion;
        private MechRadarController ownRadar;
        private CombatTestTarget ownTarget;
        private int remainingBursts;
        private float nextReleaseTime;
        private Material flareMaterial;
        private Material flareSmokeMaterial;
        private Material chaffMaterial;
        private MechAudioVariantSet launchImpactVariants;
        private MechAudioVariantSet pyropatronVariants;
        private MechAudioVariantSet chaffRustleVariants;
        private MechAudioVariantSet flareTailVariants;
        private MechAudioVariantSet farReleaseVariants;
        private MechAudioVariantSet interiorReleaseVariants;

        public int RemainingBursts => remainingBursts;
        public int BurstCapacity => burstCapacity;

        public void Configure(
            MechFlightMotionController newMotion,
            MechRadarController newRadar)
        {
            motion = newMotion;
            ownRadar = newRadar;
            ownTarget = GetComponentInParent<CombatTestTarget>();
            if (remainingBursts <= 0)
            {
                remainingBursts = Mathf.Max(1, burstCapacity);
            }
        }

        private void Awake()
        {
            remainingBursts = Mathf.Max(1, burstCapacity);
            ownTarget = GetComponentInParent<CombatTestTarget>();
            LoadAudioVariants();
        }

        public bool ReleaseCombinedBurst()
        {
            if (remainingBursts <= 0 || Time.unscaledTime < nextReleaseTime)
            {
                return false;
            }

            remainingBursts--;
            nextReleaseTime = Time.unscaledTime +
                              Mathf.Max(0.05f, releaseCooldownSeconds);

            var inheritedVelocity = motion != null
                ? motion.Velocity
                : Vector3.zero;
            var ejectionRotation = motion != null
                ? motion.LogicalRotation
                : transform.rotation;
            var backward = ejectionRotation * Vector3.back;
            var right = ejectionRotation * Vector3.right;
            var origin = transform.position + backward * 2.8f;

            var leftFlareDirection = ResolveEjectionDirection(
                leftFlareEjectionDirectionLocal,
                ejectionRotation,
                (backward - right * 0.45f).normalized);
            var rightFlareDirection = ResolveEjectionDirection(
                rightFlareEjectionDirectionLocal,
                ejectionRotation,
                (backward + right * 0.45f).normalized);
            var chaffDirection = ResolveEjectionDirection(
                chaffEjectionDirectionLocal,
                ejectionRotation,
                backward);

            SpawnFlareVisual(
                origin - right * 1.6f,
                inheritedVelocity +
                leftFlareDirection * flareEjectionSpeedMetersPerSecond +
                Random.insideUnitSphere * flareVelocitySpreadMetersPerSecond);
            SpawnFlareVisual(
                origin + right * 1.6f,
                inheritedVelocity +
                rightFlareDirection * flareEjectionSpeedMetersPerSecond +
                Random.insideUnitSphere * flareVelocitySpreadMetersPerSecond);

            var chaffVelocity = inheritedVelocity +
                chaffDirection * chaffEjectionSpeedMetersPerSecond +
                Random.insideUnitSphere *
                chaffCloudVelocitySpreadMetersPerSecond;
            SpawnChaffVisual(origin, chaffVelocity);
            RegisterChaffCloud(origin, chaffVelocity);
            PlayReleaseAudio(origin);
            return true;
        }

        private Vector3 ResolveEjectionDirection(
            Vector3 localDirection,
            Quaternion ejectionRotation,
            Vector3 fallbackWorldDirection)
        {
            return localDirection.sqrMagnitude > 0.0001f
                ? (ejectionRotation * localDirection).normalized
                : fallbackWorldDirection.normalized;
        }

        private void RegisterChaffCloud(Vector3 position, Vector3 velocity)
        {
            var signalObject=new GameObject("Radar chaff signal");
            signalObject.AddComponent<MechRadarDecoySignal>().Launch(position,velocity,
                Mathf.Max(.01f,chaffInitialRcsSquareMeters),chaffLifetimeSeconds,
                ownTarget!=null?(int?)ownTarget.TeamId:null,ownRadar);
        }
        private void SpawnFlareVisual(Vector3 position, Vector3 velocity)
        {
            var root = new GameObject("Flare Countermeasure");
            root.transform.position = position;
            flareMaterial ??= MechEffectResources.CreateAdditiveParticleMaterial(
                "AC6 Flare Countermeasure (Runtime)",
                new Color(5.5f, 3.8f, 1.8f, 1f));
            flareSmokeMaterial ??= MechEffectResources.CreateAlphaParticleMaterial(
                "AC6 Flare Smoke (Runtime)",
                new Color(0.68f, 0.66f, 0.64f, 1f));

            root.AddComponent<MechFlareVisual>().Configure(
                velocity,
                flareLifetimeSeconds,
                flareSmokeEmissionRatePerSecond,
                flareSmokeLifetimeSeconds,
                flareSmokeRelativeExhaustMetersPerSecond,
                flareSmokeExpansionMetersPerSecond,
                flareMaterial,
                flareSmokeMaterial);
        }

        private void SpawnChaffVisual(Vector3 position, Vector3 velocity)
        {
            var root = new GameObject("Chaff Countermeasure");
            root.transform.position = Vector3.zero;
            var particles = root.AddComponent<ParticleSystem>();
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particles.main;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 96;
            main.startLifetime = Mathf.Max(0.5f, chaffLifetimeSeconds);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.09f);
            var renderer = root.GetComponent<ParticleSystemRenderer>();
            chaffMaterial ??= MechEffectResources.CreateLitDebrisMaterial(
                "AC6 Chaff Countermeasure (Runtime)",
                new Color(0.82f, 0.88f, 0.92f, 1f));
            renderer.sharedMaterial = chaffMaterial;
            renderer.renderMode = ParticleSystemRenderMode.Mesh;
            renderer.mesh = MechEffectResources.DebrisShardMesh;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            for (var index = 0; index < 72; index++)
            {
                particles.Emit(new ParticleSystem.EmitParams
                {
                    position = position + Random.insideUnitSphere * 0.35f,
                    velocity = velocity + Random.insideUnitSphere *
                        visualChaffVelocitySpreadMetersPerSecond,
                    startLifetime = Mathf.Max(0.5f, chaffLifetimeSeconds) *
                                    Random.Range(0.78f, 1f),
                    startSize = Random.Range(0.025f, 0.09f),
                    rotation3D = Random.insideUnitSphere * 360f,
                    angularVelocity3D = Random.insideUnitSphere * 280f,
                }, 1);
            }
            Object.Destroy(root, chaffLifetimeSeconds + 0.5f);
        }

        private void LoadAudioVariants()
        {
            launchImpactVariants = MechAudioVariantSet.Load(
                AudioRoot, "flare-001", "flare-", 4);
            pyropatronVariants = MechAudioVariantSet.Load(
                AudioRoot, "pyropatron-001", "pyropatron-", 4);
            chaffRustleVariants = MechAudioVariantSet.Load(
                AudioRoot, "rustle-001", "rustle-", 4);
            flareTailVariants = MechAudioVariantSet.Load(
                AudioRoot, "flare_tail-001", "flare_tail-", 4);
            farReleaseVariants = MechAudioVariantSet.Load(
                AudioRoot, "flare_far-001", "flare_far-", 3);
            interiorReleaseVariants = MechAudioVariantSet.LoadNumbered(
                AudioRoot,
                "shooting_interior-",
                5,
                4);
        }

        private void PlayReleaseAudio(Vector3 position)
        {
            var root = new GameObject("Countermeasure Release Audio");
            root.transform.position = position;
            var maximumDuration = 0f;

            maximumDuration = Mathf.Max(
                maximumDuration,
                PlayAudioLayer(
                    root,
                    "Launch Impact",
                    launchImpactVariants?.Next(),
                    launchImpactVolume,
                    useInteriorAudioMix));

            if (useInteriorAudioMix)
            {
                maximumDuration = Mathf.Max(
                    maximumDuration,
                    PlayAudioLayer(
                        root,
                        "Interior Structure",
                        interiorReleaseVariants?.Next(),
                        interiorReleaseVolume,
                        true));
            }
            else
            {
                maximumDuration = Mathf.Max(
                    maximumDuration,
                    PlayAudioLayer(
                        root,
                        "Pyropatron",
                        pyropatronVariants?.Next(),
                        pyropatronVolume,
                        false));
                maximumDuration = Mathf.Max(
                    maximumDuration,
                    PlayAudioLayer(
                        root,
                        "Chaff Rustle",
                        chaffRustleVariants?.Next(),
                        chaffRustleVolume,
                        false));
                maximumDuration = Mathf.Max(
                    maximumDuration,
                    PlayAudioLayer(
                        root,
                        "Flare Tail",
                        flareTailVariants?.Next(),
                        flareTailVolume,
                        false));
                maximumDuration = Mathf.Max(
                    maximumDuration,
                    PlayAudioLayer(
                        root,
                        "Distant Release",
                        farReleaseVariants?.Next(),
                        farReleaseVolume,
                        false,
                        true));
            }

            Object.Destroy(root, Mathf.Max(0.25f, maximumDuration + 0.25f));
        }

        private static float PlayAudioLayer(
            GameObject root,
            string sourceName,
            AudioClip clip,
            float volume,
            bool interior,
            bool farLayer = false)
        {
            if (clip == null || volume <= 0f)
            {
                return 0f;
            }

            var holder = new GameObject(sourceName);
            holder.transform.SetParent(root.transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = interior ? 0f : 1f;
            source.dopplerLevel = 0f;
            source.priority = interior ? 48 : farLayer ? 144 : 92;
            if (!interior)
            {
                source.rolloffMode = AudioRolloffMode.Custom;
                source.minDistance = farLayer ? 120f : 8f;
                source.maxDistance = farLayer ? 6000f : 1800f;
                source.SetCustomCurve(
                    AudioSourceCurveType.CustomRolloff,
                    farLayer
                        ? CreateFarRolloffCurve()
                        : CreateCloseRolloffCurve());
            }

            source.PlayOneShot(clip, Mathf.Clamp01(volume));
            return clip.length;
        }

        private static AnimationCurve CreateCloseRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.08f, 0.86f),
                new Keyframe(0.35f, 0.34f),
                new Keyframe(1f, 0f));
        }

        private static AnimationCurve CreateFarRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 0f),
                new Keyframe(0.04f, 0f),
                new Keyframe(0.12f, 0.72f),
                new Keyframe(0.35f, 1f),
                new Keyframe(0.7f, 0.42f),
                new Keyframe(1f, 0f));
        }

    }
}
