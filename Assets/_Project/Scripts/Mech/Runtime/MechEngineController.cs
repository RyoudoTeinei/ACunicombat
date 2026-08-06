using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechStartupHudModule
    {
        FlightReference,
        TacticalDisplay,
        RadarScan,
        ThreatWarning,
        RadarStatus,
        Weapons,
        VitalStatus,
        TrackData,
    }

    /// <summary>
    /// Owns engine startup availability and the layered propulsion mix. Flight physics remains
    /// authoritative in <see cref="MechFlightMotionController"/>; this component only gates that
    /// authority during startup and renders its output as audio.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechEngineController : MonoBehaviour
    {
        private const string AudioRoot = "Audio/Engine/";

        [Header("Startup")]
        [SerializeField, Range(0f, 7.5f)] private float startupDurationSeconds = 7.5f;
        [SerializeField, Min(0.01f)] private float startupFadeSeconds = 1.4f;

        [Header("Mix")]
        [SerializeField, Range(0f, 1f)] private float interiorStartupVolume = 0.72f;
        [SerializeField, Range(0f, 1f)] private float externalStartupVolume = 0.52f;
        [SerializeField, Range(0f, 1f)] private float turbineStartupVolume = 0.32f;
        [SerializeField, Range(0f, 1f)] private float turbineLoopVolume = 0.22f;
        [SerializeField, Range(0f, 1f)] private float rearLoopVolume = 0.3f;
        [SerializeField, Range(0f, 1f)] private float afterburnerVolume = 0.46f;
        [SerializeField, Range(0f, 0.5f)] private float idleStructureGain = 0.12f;
        [SerializeField, Min(1f)] private float fullLoadAcceleration = 35f;
        [SerializeField, Min(0.05f)] private float spoolUpSeconds = 0.7f;
        [SerializeField, Min(0.05f)] private float spoolDownSeconds = 1.8f;
        [SerializeField, Min(0.01f)] private float mixResponsePerSecond = 1.5f;

        private MechFlightMotionController motion;
        private bool useInteriorMix;
        private bool configured;
        private bool startupActive;
        private float startupElapsedSeconds;
        private bool previousAssaultBoost;
        private float afterburnerGain;
        private float engineLoad;

        private AudioSource interiorStartupSource;
        private AudioSource externalStartupSource;
        private AudioSource turbineStartupSource;
        private AudioSource turbineIdleSource;
        private AudioSource turbineCruiseSource;
        private AudioSource turbineMaximumSource;
        private AudioSource rearLowSource;
        private AudioSource rearCruiseSource;
        private AudioSource rearMaximumSource;
        private AudioSource afterburnerIgnitionSource;
        private AudioSource afterburnerBurnSource;
        private AudioSource afterburnerWepSource;
        private AudioSource jetThrustSource;
        private AudioSource nozzleThrustSource;
        private MechAudioVariantSet afterburnerIgnitionVariants;

        public bool StartupActive => startupActive;
        public float StartupProgress => startupDurationSeconds <= Mathf.Epsilon
            ? 1f
            : Mathf.Clamp01(startupElapsedSeconds / startupDurationSeconds);
        public float StartupSecondsRemaining => startupActive
            ? Mathf.Max(0f, startupDurationSeconds - startupElapsedSeconds)
            : 0f;
        public string StartupStatusText => GetStartupStatusText();

        private void Awake()
        {
            startupDurationSeconds = Mathf.Clamp(startupDurationSeconds, 0f, 7.5f);
            startupFadeSeconds = Mathf.Min(startupFadeSeconds, 1.4f);
            LoadAndCreateSources();
        }

        public float GetHudStartupAlpha(MechStartupHudModule module)
        {
            if (!startupActive)
            {
                return 1f;
            }

            return module switch
            {
                MechStartupHudModule.FlightReference =>
                    LampWarmupAlpha(0.45f, 1.25f, 0.3f, true),
                MechStartupHudModule.TacticalDisplay =>
                    LampWarmupAlpha(1.2f, 2.05f, 1.1f, true),
                MechStartupHudModule.RadarScan =>
                    LampWarmupAlpha(2.65f, 3.35f, 2.3f, false),
                MechStartupHudModule.ThreatWarning =>
                    LampWarmupAlpha(2.55f, 3.4f, 3.7f, true),
                MechStartupHudModule.RadarStatus =>
                    LampWarmupAlpha(1.75f, 2.55f, 4.6f, true),
                MechStartupHudModule.Weapons =>
                    LampWarmupAlpha(3.55f, 4.45f, 5.2f, true),
                MechStartupHudModule.VitalStatus =>
                    LampWarmupAlpha(4.15f, 5f, 6.4f, false),
                MechStartupHudModule.TrackData =>
                    LampWarmupAlpha(5.15f, 6.05f, 7.8f, true),
                _ => 1f,
            };
        }

        private float LampWarmupAlpha(
            float startSeconds,
            float stableSeconds,
            float phase,
            bool flicker)
        {
            var progress = Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(startSeconds, stableSeconds, startupElapsedSeconds));
            if (!flicker || progress <= 0f || progress >= 0.98f)
            {
                return progress;
            }

            var localTime = Mathf.Max(0f, startupElapsedSeconds - startSeconds);
            var coarsePulse = Mathf.Sin(localTime * 31f + phase) * 0.5f + 0.5f;
            var finePulse = Mathf.Sin(localTime * 67f + phase * 2.1f) * 0.5f + 0.5f;
            var lampPower = Mathf.Clamp01(coarsePulse * 0.72f + finePulse * 0.48f - 0.28f);
            var flickerStrength = 1f - Mathf.SmoothStep(0.2f, 0.82f, progress);
            return progress * Mathf.Lerp(1f, 0.18f + lampPower * 0.82f, flickerStrength);
        }

        private string GetStartupStatusText()
        {
            if (!startupActive)
            {
                return "SYSTEM READY";
            }

            var progress = StartupProgress;
            if (progress < 0.08f) return "主电源总线接通";
            if (progress < 0.18f) return "显示驱动自检";
            if (progress < 0.3f) return "飞控与姿态基准校准";
            if (progress < 0.42f) return "雷达显示器预热";
            if (progress < 0.55f) return "雷达发射机上线";
            if (progress < 0.68f) return "告警接收机联机";
            if (progress < 0.8f) return "火控与武器总线自检";
            if (progress < 0.92f) return "航迹处理器同步";
            return "全系统就绪";
        }

        public void Configure(
            MechFlightMotionController motionController,
            bool enableInteriorMix)
        {
            motion = motionController;
            useInteriorMix = enableInteriorMix;
            configured = motion != null;
            SetStructuralSpatialBlend(useInteriorMix ? 0f : 1f);
            BeginStartup();
        }

        private void BeginStartup()
        {
            if (!configured)
            {
                return;
            }

            startupElapsedSeconds = 0f;
            startupActive = startupDurationSeconds > Mathf.Epsilon;
            previousAssaultBoost = false;
            afterburnerGain = 0f;
            engineLoad = 0f;
            motion.SetEngineAvailable(!startupActive);

            StartLoopSources();
            if (!startupActive)
            {
                return;
            }

            PlayFromStart(externalStartupSource);
            PlayFromStart(turbineStartupSource);
            if (useInteriorMix)
            {
                PlayFromStart(interiorStartupSource);
            }
        }

        private void Update()
        {
            if (!configured || motion == null)
            {
                return;
            }

            var deltaTime = Mathf.Max(0f, Time.deltaTime);
            if (startupActive)
            {
                startupElapsedSeconds += deltaTime;
                UpdateStartupMix();
                if (startupElapsedSeconds >= startupDurationSeconds)
                {
                    CompleteStartup();
                }
            }

            UpdateRunningMix(deltaTime);
        }

        private void UpdateStartupMix()
        {
            var fadeStart = Mathf.Max(
                0f,
                startupDurationSeconds - Mathf.Max(0.01f, startupFadeSeconds));
            var fade = 1f - Mathf.SmoothStep(
                0f,
                1f,
                Mathf.InverseLerp(fadeStart, startupDurationSeconds, startupElapsedSeconds));
            if (interiorStartupSource != null)
            {
                interiorStartupSource.volume = useInteriorMix
                    ? interiorStartupVolume * fade
                    : 0f;
            }
            if (externalStartupSource != null)
            {
                externalStartupSource.volume = externalStartupVolume * fade;
            }
            if (turbineStartupSource != null)
            {
                turbineStartupSource.volume = turbineStartupVolume * fade;
            }
        }

        private void CompleteStartup()
        {
            startupElapsedSeconds = startupDurationSeconds;
            startupActive = false;
            StopSource(interiorStartupSource);
            StopSource(externalStartupSource);
            StopSource(turbineStartupSource);
            motion.SetEngineAvailable(true);
        }

        private void UpdateRunningMix(float deltaTime)
        {
            var ready = motion.EngineAvailable && !startupActive;
            var requestedLoad = ready
                ? Mathf.Max(
                    Mathf.Clamp01(motion.MainEngineOutput),
                    Mathf.Clamp01(
                        motion.CurrentControlAcceleration.magnitude /
                        Mathf.Max(1f, fullLoadAcceleration)))
                : 0f;
            if (ready && (motion.QuickBoostActive || motion.AssaultBoostActive))
            {
                requestedLoad = 1f;
            }
            var spoolSeconds = requestedLoad > engineLoad
                ? Mathf.Max(0.05f, spoolUpSeconds)
                : Mathf.Max(0.05f, spoolDownSeconds);
            var spoolBlend = 1f - Mathf.Exp(-deltaTime / spoolSeconds);
            engineLoad = Mathf.Lerp(engineLoad, requestedLoad, spoolBlend);
            if (engineLoad < 0.0001f && requestedLoad <= 0f)
            {
                engineLoad = 0f;
            }

            var load = engineLoad;
            var lowWeight = ready
                ? 1f - 0.72f * SmoothRange(0.12f, 0.72f, load)
                : 0f;
            var cruiseWeight = ready
                ? 1f - Mathf.Clamp01(Mathf.Abs(load - 0.52f) * 1.65f)
                : 0f;
            cruiseWeight = cruiseWeight * cruiseWeight * (3f - 2f * cruiseWeight);
            var highWeight = ready ? SmoothRange(0.5f, 0.94f, load) : 0f;
            var step = Mathf.Max(0.01f, mixResponsePerSecond) * deltaTime;
            var structureGain = ready
                ? Mathf.Lerp(Mathf.Clamp01(idleStructureGain), 1f, load)
                : 0f;
            var externalGain = ready ? load * load : 0f;

            MoveVolume(
                turbineIdleSource,
                lowWeight * turbineLoopVolume * structureGain,
                step);
            MoveVolume(
                turbineCruiseSource,
                cruiseWeight * turbineLoopVolume * structureGain,
                step);
            MoveVolume(
                turbineMaximumSource,
                highWeight * turbineLoopVolume * structureGain,
                step);
            MoveVolume(rearLowSource, lowWeight * rearLoopVolume * externalGain, step);
            MoveVolume(rearCruiseSource, cruiseWeight * rearLoopVolume * externalGain, step);
            MoveVolume(rearMaximumSource, highWeight * rearLoopVolume * externalGain, step);

            var assaultBoost = ready && motion.AssaultBoostActive;
            if (assaultBoost && !previousAssaultBoost && afterburnerIgnitionSource != null)
            {
                var ignitionClip = afterburnerIgnitionVariants.Next();
                if (ignitionClip != null)
                {
                    afterburnerIgnitionSource.PlayOneShot(
                        ignitionClip,
                        useInteriorMix ? 0.72f : 1f);
                }
            }
            previousAssaultBoost = assaultBoost;
            afterburnerGain = Mathf.MoveTowards(
                afterburnerGain,
                assaultBoost ? 1f : 0f,
                step * (assaultBoost ? 1.8f : 1f));

            MoveVolume(
                afterburnerBurnSource,
                afterburnerGain * afterburnerVolume * (useInteriorMix ? 0.72f : 0.35f),
                step);
            MoveVolume(
                afterburnerWepSource,
                afterburnerGain * afterburnerVolume * (useInteriorMix ? 0.58f : 0.25f),
                step);
            MoveVolume(
                jetThrustSource,
                afterburnerGain * afterburnerVolume,
                step);
            MoveVolume(
                nozzleThrustSource,
                (ready ? load * rearLoopVolume * 0.42f : 0f) +
                afterburnerGain * afterburnerVolume * 0.52f,
                step);

            var pitch = Mathf.Lerp(0.97f, 1.03f, load);
            SetPitch(turbineIdleSource, pitch);
            SetPitch(turbineCruiseSource, pitch);
            SetPitch(turbineMaximumSource, pitch);
        }

        private void LoadAndCreateSources()
        {
            afterburnerIgnitionVariants = MechAudioVariantSet.Load(
                AudioRoot,
                "afterburner_ignition",
                "afterburner_ignition_",
                4);
            interiorStartupSource = CreateSource(
                "Engine Startup Interior",
                Load("engine_start_cockpit"),
                false,
                0f);
            externalStartupSource = CreateSource(
                "Engine Startup Exterior",
                Load("engine_start"),
                false,
                1f);
            turbineStartupSource = CreateSource(
                "Engine Turbine Startup",
                Load("turbine_start"),
                false,
                0f);
            turbineIdleSource = CreateSource("Engine Turbine Idle", Load("turbine_idle"), true, 0f);
            turbineCruiseSource = CreateSource("Engine Turbine Cruise", Load("turbine_cruise"), true, 0f);
            turbineMaximumSource = CreateSource("Engine Turbine Maximum", Load("turbine_max"), true, 0f);
            rearLowSource = CreateSource("Engine Rear Low", Load("engine_rear_low"), true, 1f);
            rearCruiseSource = CreateSource("Engine Rear Cruise", Load("engine_rear_cruise"), true, 1f);
            rearMaximumSource = CreateSource("Engine Rear Maximum", Load("engine_rear_max"), true, 1f);
            afterburnerIgnitionSource = CreateSource(
                "Afterburner Ignition",
                afterburnerIgnitionVariants.First,
                false,
                0f);
            afterburnerBurnSource = CreateSource("Afterburner Burn", Load("afterburner_burn"), true, 0f);
            afterburnerWepSource = CreateSource("Afterburner WEP", Load("afterburner_wep"), true, 0f);
            jetThrustSource = CreateSource("Afterburner Jet Thrust", Load("jet_wep_thrust"), true, 1f);
            nozzleThrustSource = CreateSource("Engine Nozzle Thrust", Load("jet_nozzle_thrust"), true, 1f);
        }

        private AudioSource CreateSource(
            string sourceName,
            AudioClip clip,
            bool loop,
            float spatialBlend)
        {
            var holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = spatialBlend;
            source.volume = 0f;
            source.dopplerLevel = 0.15f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 18f;
            source.maxDistance = 6000f;
            return source;
        }

        private void SetStructuralSpatialBlend(float blend)
        {
            SetSpatialBlend(turbineStartupSource, blend);
            SetSpatialBlend(turbineIdleSource, blend);
            SetSpatialBlend(turbineCruiseSource, blend);
            SetSpatialBlend(turbineMaximumSource, blend);
            SetSpatialBlend(afterburnerIgnitionSource, blend);
            SetSpatialBlend(afterburnerBurnSource, blend);
            SetSpatialBlend(afterburnerWepSource, blend);
        }

        private void StartLoopSources()
        {
            PlayLoop(turbineIdleSource);
            PlayLoop(turbineCruiseSource);
            PlayLoop(turbineMaximumSource);
            PlayLoop(rearLowSource);
            PlayLoop(rearCruiseSource);
            PlayLoop(rearMaximumSource);
            PlayLoop(afterburnerBurnSource);
            PlayLoop(afterburnerWepSource);
            PlayLoop(jetThrustSource);
            PlayLoop(nozzleThrustSource);
        }

        private static AudioClip Load(string clipName)
        {
            var clip = Resources.Load<AudioClip>(AudioRoot + clipName);
            clip?.LoadAudioData();
            return clip;
        }

        private static float SmoothRange(float minimum, float maximum, float value)
        {
            var t = Mathf.InverseLerp(minimum, maximum, value);
            return t * t * (3f - 2f * t);
        }

        private static void PlayFromStart(AudioSource source)
        {
            if (source == null || source.clip == null)
            {
                return;
            }
            source.Stop();
            source.time = 0f;
            source.Play();
        }

        private static void PlayLoop(AudioSource source)
        {
            if (source != null && source.clip != null && !source.isPlaying)
            {
                source.Play();
            }
        }

        private static void MoveVolume(AudioSource source, float target, float step)
        {
            if (source != null)
            {
                source.volume = Mathf.MoveTowards(source.volume, Mathf.Clamp01(target), step);
            }
        }

        private static void SetPitch(AudioSource source, float pitch)
        {
            if (source != null)
            {
                source.pitch = pitch;
            }
        }

        private static void SetSpatialBlend(AudioSource source, float blend)
        {
            if (source != null)
            {
                source.spatialBlend = Mathf.Clamp01(blend);
            }
        }

        private static void StopSource(AudioSource source)
        {
            if (source != null)
            {
                source.Stop();
                source.volume = 0f;
            }
        }

        private void OnDisable()
        {
            if (motion != null)
            {
                motion.SetEngineAvailable(true);
            }
        }
    }
}
