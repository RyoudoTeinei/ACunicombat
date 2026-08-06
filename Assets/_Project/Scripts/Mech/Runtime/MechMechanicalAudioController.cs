using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Renders actual articulated motion as layered servo, hydraulic, and joint audio.
    /// It samples after the pose and weapon layers, so aim, recoil, reload, and launcher
    /// animation contribute even when the flight controller is idle.
    /// </summary>
    [DefaultExecutionOrder(300)]
    [DisallowMultipleComponent]
    public sealed class MechMechanicalAudioController : MonoBehaviour
    {
        private const string AudioRoot = "Audio/Mechanical/";
        private const int MaximumMonitoredTransforms = 160;
        private const int ActivitySampleCount = 8;

        [Header("Motion sensing")]
        [SerializeField, Min(1f)] private float fullJointAngularSpeed = 75f;
        [SerializeField, Min(0.01f)] private float fullJointLinearSpeed = 0.8f;
        [SerializeField, Min(1f)] private float fullYawSpeed = 120f;
        [SerializeField, Min(1f)] private float fullControlAcceleration = 35f;
        [SerializeField, Min(0.05f)] private float attackSeconds = 0.14f;
        [SerializeField, Min(0.05f)] private float releaseSeconds = 0.65f;

        [Header("Activity thresholds")]
        [SerializeField, Range(0f, 1f)] private float servoStartThreshold = 0.1f;
        [SerializeField, Range(0f, 1f)] private float servoStopThreshold = 0.045f;
        [SerializeField, Range(0f, 1f)] private float hydraulicStartThreshold = 0.24f;
        [SerializeField, Range(0f, 1f)] private float hydraulicStopThreshold = 0.09f;

        [Header("Mix")]
        [SerializeField, Range(0f, 1f)] private float slowServoVolume = 0.12f;
        [SerializeField, Range(0f, 1f)] private float fastServoVolume = 0.16f;
        [SerializeField, Range(0f, 1f)] private float hydraulicVolume = 0.1f;
        [SerializeField, Range(0f, 1f)] private float servoTransientVolume = 0.18f;
        [SerializeField, Range(0f, 1f)] private float mechanicalTransientVolume = 0.16f;
        [SerializeField, Range(0f, 1f)] private float hydraulicTransientVolume = 0.2f;
        [SerializeField, Range(0f, 1f)] private float hydraulicStopVolume = 0.13f;
        [SerializeField, Range(0f, 1f)] private float pneumaticVolume = 0.18f;

        private readonly List<MonitoredTransform> monitoredTransforms =
            new List<MonitoredTransform>(MaximumMonitoredTransforms);
        private readonly float[] strongestActivities = new float[ActivitySampleCount];

        private MechFlightMotionController motion;
        private MechRuntimeState runtimeState;
        private bool configured;
        private bool servoActive;
        private bool hydraulicActive;
        private bool previousHighLoad;
        private bool pneumaticReleasedForCycle;
        private int cachedPartCount = -1;
        private float nextRecacheTime;
        private float nextMechanicalTime;
        private float nextPneumaticTime;
        private float smoothedServoActivity;
        private float smoothedHydraulicActivity;
        private float previousRawJointActivity;
        private float hydraulicPeak;

        private AudioSource servoStartSource;
        private AudioSource servoSlowSource;
        private AudioSource servoFastSource;
        private AudioSource servoStopSource;
        private AudioSource jointMechanicalSource;
        private AudioSource hydraulicStartSource;
        private AudioSource hydraulicLoopSource;
        private AudioSource hydraulicStopSource;
        private AudioSource pneumaticSource;
        private MechAudioVariantSet jointMechanicalVariants;
        private MechAudioVariantSet hydraulicStartVariants;
        private MechAudioVariantSet hydraulicStopVariants;
        private MechAudioVariantSet pneumaticVariants;

        private sealed class MonitoredTransform
        {
            public Transform Transform;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
        }

        private void Awake()
        {
            LoadAndCreateSources();
        }

        public void Configure(
            MechFlightMotionController motionController,
            bool useInteriorMix)
        {
            motion = motionController;
            runtimeState = GetComponent<MechRuntimeState>();
            configured = motion != null;
            SetSpatialBlend(useInteriorMix ? 0.12f : 1f);
            RecacheTransforms();
            StartLoop(servoSlowSource);
            StartLoop(servoFastSource);
            StartLoop(hydraulicLoopSource);
        }

        private void LateUpdate()
        {
            if (!configured || motion == null)
            {
                SilenceLoops();
                return;
            }

            var deltaTime = Time.deltaTime;
            if (deltaTime <= Mathf.Epsilon)
            {
                return;
            }

            if (Time.unscaledTime >= nextRecacheTime && PartCountChanged())
            {
                RecacheTransforms();
            }

            var jointActivity = MeasureJointActivity(deltaTime);
            var yawActivity = Mathf.Clamp01(
                Mathf.Abs(motion.YawRateDegreesPerSecond) /
                Mathf.Max(1f, fullYawSpeed));
            var accelerationActivity = Mathf.Clamp01(
                motion.CurrentControlAcceleration.magnitude /
                Mathf.Max(1f, fullControlAcceleration));
            var highLoad = motion.Braking || motion.QuickBoostActive || motion.AssaultBoostActive;

            var rawServoActivity = Mathf.Max(jointActivity, yawActivity * 0.72f);
            var rawHydraulicActivity = Mathf.Max(
                jointActivity * 0.8f,
                accelerationActivity * 0.58f,
                highLoad ? 0.92f : 0f);

            smoothedServoActivity = SmoothActivity(
                smoothedServoActivity,
                rawServoActivity,
                deltaTime);
            smoothedHydraulicActivity = SmoothActivity(
                smoothedHydraulicActivity,
                rawHydraulicActivity,
                deltaTime);

            UpdateServoState(jointActivity);
            UpdateHydraulicState(highLoad);
            UpdateLoopMix(deltaTime);
            previousRawJointActivity = jointActivity;
            previousHighLoad = highLoad;
        }

        private float MeasureJointActivity(float deltaTime)
        {
            for (var i = 0; i < strongestActivities.Length; i++)
            {
                strongestActivities[i] = 0f;
            }

            var validCount = 0;
            for (var i = 0; i < monitoredTransforms.Count; i++)
            {
                var sample = monitoredTransforms[i];
                var target = sample.Transform;
                if (target == null)
                {
                    continue;
                }

                var angularSpeed = Quaternion.Angle(
                    sample.LocalRotation,
                    target.localRotation) / deltaTime;
                var linearSpeed = Vector3.Distance(
                    sample.LocalPosition,
                    target.localPosition) / deltaTime;
                sample.LocalRotation = target.localRotation;
                sample.LocalPosition = target.localPosition;

                var activity = Mathf.Max(
                    angularSpeed / Mathf.Max(1f, fullJointAngularSpeed),
                    linearSpeed / Mathf.Max(0.01f, fullJointLinearSpeed));
                activity = Mathf.Clamp01(activity);
                if (activity < 0.01f)
                {
                    continue;
                }

                InsertStrongestActivity(activity);
                validCount++;
            }

            if (validCount == 0)
            {
                return 0f;
            }

            var usedCount = Mathf.Min(validCount, strongestActivities.Length);
            var sum = 0f;
            for (var i = 0; i < usedCount; i++)
            {
                sum += strongestActivities[i];
            }

            var strongest = strongestActivities[0];
            var average = sum / usedCount;
            return Mathf.Clamp01(strongest * 0.62f + average * 0.38f);
        }

        private void InsertStrongestActivity(float activity)
        {
            for (var i = 0; i < strongestActivities.Length; i++)
            {
                if (activity <= strongestActivities[i])
                {
                    continue;
                }

                for (var j = strongestActivities.Length - 1; j > i; j--)
                {
                    strongestActivities[j] = strongestActivities[j - 1];
                }
                strongestActivities[i] = activity;
                break;
            }
        }

        private void UpdateServoState(float jointActivity)
        {
            if (!servoActive && smoothedServoActivity >= servoStartThreshold)
            {
                servoActive = true;
                PlayOneShot(servoStartSource, servoTransientVolume);
                PlayMechanicalTransient(mechanicalTransientVolume * 0.65f);
            }
            else if (servoActive && smoothedServoActivity <= servoStopThreshold)
            {
                servoActive = false;
                PlayOneShot(servoStopSource, servoTransientVolume * 0.9f);
            }

            var sharpJointChange = jointActivity - previousRawJointActivity;
            if (jointActivity >= 0.32f && sharpJointChange >= 0.18f)
            {
                PlayMechanicalTransient(
                    mechanicalTransientVolume * Mathf.Lerp(0.7f, 1f, jointActivity));
            }
        }

        private void UpdateHydraulicState(bool highLoad)
        {
            if (!hydraulicActive &&
                smoothedHydraulicActivity >= hydraulicStartThreshold)
            {
                hydraulicActive = true;
                hydraulicPeak = smoothedHydraulicActivity;
                pneumaticReleasedForCycle = false;
                PlayOneShot(
                    hydraulicStartSource,
                    hydraulicStartVariants,
                    hydraulicTransientVolume);
            }
            else if (hydraulicActive)
            {
                hydraulicPeak = Mathf.Max(hydraulicPeak, smoothedHydraulicActivity);
                if (smoothedHydraulicActivity <= hydraulicStopThreshold)
                {
                    hydraulicActive = false;
                    PlayOneShot(
                        hydraulicStopSource,
                        hydraulicStopVariants,
                        hydraulicStopVolume);
                    if (hydraulicPeak >= 0.55f && !pneumaticReleasedForCycle)
                    {
                        PlayPneumaticRelease();
                        pneumaticReleasedForCycle = true;
                    }
                    hydraulicPeak = 0f;
                }
            }

            if (previousHighLoad && !highLoad && hydraulicPeak >= 0.72f)
            {
                PlayPneumaticRelease();
                pneumaticReleasedForCycle = true;
            }
        }

        private void UpdateLoopMix(float deltaTime)
        {
            var servoEnvelope = SmoothRange(
                servoStopThreshold,
                Mathf.Max(servoStartThreshold + 0.08f, 0.22f),
                smoothedServoActivity);
            var fastWeight = SmoothRange(0.38f, 0.82f, smoothedServoActivity);
            var slowWeight = servoEnvelope * (1f - fastWeight * 0.68f);
            var hydraulicEnvelope = SmoothRange(
                hydraulicStopThreshold,
                Mathf.Max(hydraulicStartThreshold + 0.12f, 0.4f),
                smoothedHydraulicActivity);
            var step = deltaTime * 0.75f;

            MoveVolume(servoSlowSource, slowWeight * slowServoVolume, step);
            MoveVolume(
                servoFastSource,
                servoEnvelope * fastWeight * fastServoVolume,
                step);
            MoveVolume(
                hydraulicLoopSource,
                hydraulicEnvelope * hydraulicVolume,
                step * 0.8f);

            if (servoSlowSource != null)
            {
                servoSlowSource.pitch = Mathf.Lerp(0.98f, 1.015f, smoothedServoActivity);
            }
            if (servoFastSource != null)
            {
                servoFastSource.pitch = Mathf.Lerp(0.985f, 1.025f, smoothedServoActivity);
            }
        }

        private float SmoothActivity(float current, float target, float deltaTime)
        {
            var response = target > current ? attackSeconds : releaseSeconds;
            var blend = 1f - Mathf.Exp(-deltaTime / Mathf.Max(0.05f, response));
            return Mathf.Lerp(current, target, blend);
        }

        private void PlayMechanicalTransient(float volume)
        {
            if (Time.unscaledTime < nextMechanicalTime)
            {
                return;
            }
            nextMechanicalTime = Time.unscaledTime + 0.18f;
            PlayOneShot(
                jointMechanicalSource, jointMechanicalVariants, volume);
        }

        private void PlayPneumaticRelease()
        {
            if (Time.unscaledTime < nextPneumaticTime)
            {
                return;
            }
            nextPneumaticTime = Time.unscaledTime + 1.1f;
            PlayOneShot(pneumaticSource, pneumaticVariants, pneumaticVolume);
        }

        private bool PartCountChanged()
        {
            nextRecacheTime = Time.unscaledTime + 1f;
            runtimeState = runtimeState != null
                ? runtimeState
                : GetComponent<MechRuntimeState>();
            var partCount = ResolvePartCount();
            return partCount != cachedPartCount;
        }

        private void RecacheTransforms()
        {
            monitoredTransforms.Clear();
            runtimeState = runtimeState != null
                ? runtimeState
                : GetComponent<MechRuntimeState>();
            cachedPartCount = ResolvePartCount();
            nextRecacheTime = Time.unscaledTime + 1f;

            if (runtimeState != null && runtimeState.AssembledParts.Count > 0)
            {
                for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
                {
                    var record = runtimeState.AssembledParts[i];
                    if (record?.instance != null)
                    {
                        AddTransform(record.instance.transform);
                    }
                }
            }

            var descendants = GetComponentsInChildren<Transform>(true);
            for (var i = 0;
                 i < descendants.Length && monitoredTransforms.Count < MaximumMonitoredTransforms;
                 i++)
            {
                var candidate = descendants[i];
                if (candidate == transform || candidate.GetComponent<AudioSource>() != null)
                {
                    continue;
                }
                if (IsMechanicalTransformName(candidate.name))
                {
                    AddTransform(candidate);
                }
            }
        }

        private int ResolvePartCount()
        {
            return runtimeState != null && runtimeState.AssembledParts.Count > 0
                ? runtimeState.AssembledParts.Count
                : transform.childCount;
        }

        private void AddTransform(Transform candidate)
        {
            if (candidate == null || monitoredTransforms.Count >= MaximumMonitoredTransforms)
            {
                return;
            }
            for (var i = 0; i < monitoredTransforms.Count; i++)
            {
                if (monitoredTransforms[i].Transform == candidate)
                {
                    return;
                }
            }
            monitoredTransforms.Add(new MonitoredTransform
            {
                Transform = candidate,
                LocalPosition = candidate.localPosition,
                LocalRotation = candidate.localRotation,
            });
        }

        private static bool IsMechanicalTransformName(string transformName)
        {
            var name = transformName.ToLowerInvariant();
            return name.Contains("arm") || name.Contains("shoulder") ||
                   name.Contains("elbow") || name.Contains("wrist") ||
                   name.Contains("hand") || name.Contains("leg") ||
                   name.Contains("knee") || name.Contains("ankle") ||
                   name.Contains("hip") || name.Contains("spine") ||
                   name.Contains("core") || name.Contains("weapon") ||
                   name.Contains("launcher") || name.Contains("pod") ||
                   name.Contains("booster") || name.Contains("joint");
        }

        private void LoadAndCreateSources()
        {
            jointMechanicalVariants = MechAudioVariantSet.Load(
                AudioRoot, "joint_mechanical", "joint_mechanical_", 6);
            hydraulicStartVariants = MechAudioVariantSet.Load(
                AudioRoot, "hydraulic_start", "hydraulic_start_", 3);
            hydraulicStopVariants = MechAudioVariantSet.Load(
                AudioRoot, "hydraulic_stop", "hydraulic_stop_", 2);
            pneumaticVariants = MechAudioVariantSet.Load(
                AudioRoot, "pneumatic_release", "pneumatic_release_", 8);

            servoStartSource = CreateSource("Servo Start", Load("servo_start"), false);
            servoSlowSource = CreateSource("Servo Slow Loop", Load("servo_slow_loop"), true);
            servoFastSource = CreateSource("Servo Fast Loop", Load("servo_fast_loop"), true);
            servoStopSource = CreateSource("Servo Stop", Load("servo_stop"), false);
            jointMechanicalSource = CreateSource(
                "Joint Mechanical Transient",
                jointMechanicalVariants.First,
                false);
            hydraulicStartSource = CreateSource(
                "Hydraulic Start",
                hydraulicStartVariants.First,
                false);
            hydraulicLoopSource = CreateSource(
                "Hydraulic Loop",
                Load("hydraulic_loop"),
                true);
            hydraulicStopSource = CreateSource(
                "Hydraulic Stop",
                hydraulicStopVariants.First,
                false);
            pneumaticSource = CreateSource(
                "Pneumatic Release",
                pneumaticVariants.First,
                false);
        }

        private AudioSource CreateSource(string sourceName, AudioClip clip, bool loop)
        {
            var holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = loop;
            source.playOnAwake = false;
            source.volume = loop ? 0f : 1f;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0.1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 12f;
            source.maxDistance = 1800f;
            return source;
        }

        private static AudioClip Load(string clipName)
        {
            var clip = Resources.Load<AudioClip>(AudioRoot + clipName);
            clip?.LoadAudioData();
            return clip;
        }

        private void SetSpatialBlend(float blend)
        {
            var clampedBlend = Mathf.Clamp01(blend);
            SetSourceSpatialBlend(servoStartSource, clampedBlend);
            SetSourceSpatialBlend(servoSlowSource, clampedBlend);
            SetSourceSpatialBlend(servoFastSource, clampedBlend);
            SetSourceSpatialBlend(servoStopSource, clampedBlend);
            SetSourceSpatialBlend(jointMechanicalSource, clampedBlend);
            SetSourceSpatialBlend(hydraulicStartSource, clampedBlend);
            SetSourceSpatialBlend(hydraulicLoopSource, clampedBlend);
            SetSourceSpatialBlend(hydraulicStopSource, clampedBlend);
            SetSourceSpatialBlend(pneumaticSource, clampedBlend);
        }

        private static void SetSourceSpatialBlend(AudioSource source, float blend)
        {
            if (source != null)
            {
                source.spatialBlend = blend;
            }
        }

        private static float SmoothRange(float minimum, float maximum, float value)
        {
            var t = Mathf.InverseLerp(minimum, maximum, value);
            return t * t * (3f - 2f * t);
        }

        private static void StartLoop(AudioSource source)
        {
            if (source != null && source.clip != null && !source.isPlaying)
            {
                source.Play();
            }
        }

        private static void PlayOneShot(AudioSource source, float volume)
        {
            if (source != null && source.clip != null)
            {
                source.PlayOneShot(source.clip, Mathf.Clamp01(volume));
            }
        }

        private static void PlayOneShot(
            AudioSource source,
            MechAudioVariantSet variants,
            float volume)
        {
            var clip = variants?.Next();
            if (source != null && clip != null)
            {
                source.PlayOneShot(clip, Mathf.Clamp01(volume));
            }
        }

        private static void MoveVolume(AudioSource source, float target, float step)
        {
            if (source != null)
            {
                source.volume = Mathf.MoveTowards(
                    source.volume,
                    Mathf.Clamp01(target),
                    Mathf.Max(0f, step));
            }
        }

        private void SilenceLoops()
        {
            MoveVolume(servoSlowSource, 0f, 1f);
            MoveVolume(servoFastSource, 0f, 1f);
            MoveVolume(hydraulicLoopSource, 0f, 1f);
        }

        private void OnDisable()
        {
            SilenceLoops();
            servoActive = false;
            hydraulicActive = false;
        }
    }
}
