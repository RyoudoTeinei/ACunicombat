using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// State-driven projectile audio. Motor sound follows the same motor-active
    /// transition as thrust and exhaust visuals, so cold launch, delayed salvo
    /// ignition and burnout cannot drift away from missile physics.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMissileAudioController : MonoBehaviour
    {
        private const float EjectionAudibleSeconds = 0.65f;
        private const float EjectionFadeSeconds = 0.1f;
        private const float MotorLoopDelaySeconds = 0.18f;
        private const float MotorFadeInSeconds = 0.12f;
        private const float MotorFadeOutSeconds = 0.15f;
        private const float IgnitionFadeOutSeconds = 0.12f;
        private const float FlybyArmDelaySeconds = 0.25f;
        private const float FlybyMaximumDistanceMeters = 35f;
        private const float FlybyApproachEpsilonMeters = 0.05f;
        private const float OwnshipIgnitionCoalesceSeconds = 0.2f;

        private static float nextOwnshipIgnitionTime;

        [Header("Clips")]
        [SerializeField] private AudioClip coldEjectionClip;
        [SerializeField] private AudioClip coldIgnitionClip;
        [SerializeField] private AudioClip hotIgnitionClip;
        [SerializeField] private AudioClip motorLoopClip;
        [SerializeField] private AudioClip flybyClip;

        [Header("Levels")]
        [SerializeField, Range(0f, 1f)] private float coldEjectionVolume = 0.22f;
        [SerializeField, Range(0f, 1f)] private float coldIgnitionVolume = 0.32f;
        [SerializeField, Range(0f, 1f)] private float hotIgnitionVolume = 0.48f;
        [SerializeField, Range(0f, 1f)] private float motorLoopVolume = 0.2f;
        [SerializeField, Range(0f, 1f)] private float flybyVolume = 0.16f;

        [Header("Limited external feedback")]
        [SerializeField, Min(0.1f)] private float minimumDistanceMeters = 10f;
        [SerializeField, Min(1f)] private float maximumDistanceMeters = 120f;
        [SerializeField, Min(0.1f)] private float motorMinimumDistanceMeters = 24f;
        [SerializeField, Min(1f)] private float motorMaximumDistanceMeters = 260f;

        private AudioSource ejectionSource;
        private AudioSource ignitionSource;
        private AudioSource motorSource;
        private AudioSource flybySource;
        private Transform listenerTransform;
        private bool coldLaunch;
        private bool ownshipLaunch;
        private bool motorStateInitialized;
        private bool motorActive;
        private bool ignitionFadingOut;
        private float ignitionBaseVolume;
        private float motorLoopDelayRemaining;
        private float ejectionStopTime = -1f;
        private float configuredTime;
        private float previousListenerDistance;
        private float minimumListenerDistance = float.PositiveInfinity;
        private bool hasListenerDistance;
        private bool flybyArmed;
        private bool flybyPlayed;

        private void Awake()
        {
            LoadDefaultClips();
            PreloadClips();
            ejectionSource = CreateWorldSource();
            ignitionSource = CreateWorldSource();
            motorSource = CreateWorldSource();
            motorSource.loop = true;
            motorSource.minDistance = motorMinimumDistanceMeters;
            motorSource.maxDistance = motorMaximumDistanceMeters;
            motorSource.SetCustomCurve(
                AudioSourceCurveType.CustomRolloff,
                CreateMotorRolloffCurve());
            flybySource = CreateWorldSource();
            flybySource.maxDistance =
                Mathf.Min(maximumDistanceMeters, 55f);
        }

        public void Configure(bool isColdLaunch, bool isOwnshipLaunch)
        {
            coldLaunch = isColdLaunch;
            ownshipLaunch = isOwnshipLaunch;
            ignitionSource.spatialBlend = ownshipLaunch ? 0f : 1f;
            configuredTime = Time.unscaledTime;
            listenerTransform = FindListenerTransform();
            ResetFlybyTracking();

            if (!coldLaunch || coldEjectionClip == null)
            {
                return;
            }

            ejectionSource.Stop();
            ejectionSource.clip = coldEjectionClip;
            ejectionSource.volume = coldEjectionVolume;
            ejectionSource.pitch = InstancePitch();
            ejectionSource.Play();
            ejectionStopTime =
                Time.unscaledTime + EjectionAudibleSeconds;
        }

        public void SetMotorActive(bool active)
        {
            if (motorStateInitialized && motorActive == active)
            {
                return;
            }

            var wasActive = motorStateInitialized && motorActive;
            motorStateInitialized = true;
            motorActive = active;
            if (motorActive)
            {
                StartMotorAudio();
            }
            else if (wasActive)
            {
                ignitionFadingOut = ignitionSource.isPlaying;
            }
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;
            UpdateEjectionFade();
            UpdateIgnitionFade(dt);
            UpdateMotorLoop(dt);
            UpdateFlyby();
        }

        private void StartMotorAudio()
        {
            var ignitionClip =
                coldLaunch ? coldIgnitionClip : hotIgnitionClip;
            ignitionBaseVolume =
                coldLaunch
                    ? coldIgnitionVolume
                    : hotIgnitionVolume;
            ignitionFadingOut = false;
            var playIgnition = ignitionClip != null;
            if (ownshipLaunch)
            {
                var now = Time.unscaledTime;
                playIgnition &= now >= nextOwnshipIgnitionTime;
                if (playIgnition)
                {
                    nextOwnshipIgnitionTime =
                        now + OwnshipIgnitionCoalesceSeconds;
                    ignitionBaseVolume = Mathf.Min(
                        1f,
                        ignitionBaseVolume * 1.25f);
                }
            }

            if (playIgnition)
            {
                ignitionSource.Stop();
                ignitionSource.clip = ignitionClip;
                ignitionSource.volume = ignitionBaseVolume;
                ignitionSource.pitch = InstancePitch();
                ignitionSource.Play();
            }

            if (motorLoopClip == null)
            {
                return;
            }

            motorSource.Stop();
            motorSource.clip = motorLoopClip;
            motorSource.volume = 0f;
            motorSource.pitch = InstancePitch();
            motorSource.loop = true;
            motorSource.Play();
            motorLoopDelayRemaining = MotorLoopDelaySeconds;
        }

        private void UpdateEjectionFade()
        {
            if (ejectionStopTime < 0f ||
                !ejectionSource.isPlaying)
            {
                return;
            }

            var remaining =
                ejectionStopTime - Time.unscaledTime;
            if (remaining <= 0f)
            {
                ejectionSource.Stop();
                ejectionSource.volume = coldEjectionVolume;
                ejectionStopTime = -1f;
                return;
            }

            if (remaining < EjectionFadeSeconds)
            {
                ejectionSource.volume =
                    coldEjectionVolume *
                    Mathf.Clamp01(
                        remaining / EjectionFadeSeconds);
            }
        }

        private void UpdateIgnitionFade(float dt)
        {
            if (!ignitionFadingOut ||
                !ignitionSource.isPlaying)
            {
                return;
            }

            ignitionSource.volume = Mathf.MoveTowards(
                ignitionSource.volume,
                0f,
                ignitionBaseVolume /
                IgnitionFadeOutSeconds * dt);
            if (ignitionSource.volume > 0.0001f)
            {
                return;
            }

            ignitionSource.Stop();
            ignitionSource.volume = ignitionBaseVolume;
            ignitionFadingOut = false;
        }

        private void UpdateMotorLoop(float dt)
        {
            if (motorSource == null)
            {
                return;
            }

            if (motorActive)
            {
                motorLoopDelayRemaining = Mathf.Max(
                    0f,
                    motorLoopDelayRemaining - dt);
            }

            var targetVolume =
                motorActive &&
                motorLoopDelayRemaining <= 0f
                    ? motorLoopVolume
                    : 0f;
            var fadeSeconds =
                targetVolume > motorSource.volume
                    ? MotorFadeInSeconds
                    : MotorFadeOutSeconds;
            motorSource.volume = Mathf.MoveTowards(
                motorSource.volume,
                targetVolume,
                motorLoopVolume /
                fadeSeconds * dt);

            if (!motorActive &&
                motorSource.isPlaying &&
                motorSource.volume <= 0.0001f)
            {
                motorSource.Stop();
                motorSource.volume = 0f;
            }
        }

        private void UpdateFlyby()
        {
            if (flybyPlayed ||
                flybyClip == null ||
                Time.unscaledTime - configuredTime <
                FlybyArmDelaySeconds)
            {
                return;
            }

            if (listenerTransform == null)
            {
                listenerTransform = FindListenerTransform();
                if (listenerTransform == null)
                {
                    return;
                }
                hasListenerDistance = false;
            }

            var distance = Vector3.Distance(
                transform.position,
                listenerTransform.position);
            if (!hasListenerDistance)
            {
                previousListenerDistance = distance;
                minimumListenerDistance = distance;
                hasListenerDistance = true;
                return;
            }

            if (distance <
                previousListenerDistance -
                FlybyApproachEpsilonMeters)
            {
                flybyArmed = true;
                minimumListenerDistance = Mathf.Min(
                    minimumListenerDistance,
                    distance);
            }
            else if (flybyArmed &&
                     distance >
                     previousListenerDistance +
                     FlybyApproachEpsilonMeters &&
                     minimumListenerDistance <=
                     FlybyMaximumDistanceMeters)
            {
                flybySource.clip = flybyClip;
                flybySource.volume = flybyVolume;
                flybySource.pitch = InstancePitch();
                flybySource.Play();
                flybyPlayed = true;
            }

            previousListenerDistance = distance;
        }

        private void OnDisable()
        {
            ejectionSource?.Stop();
            ignitionSource?.Stop();
            motorSource?.Stop();
            flybySource?.Stop();
        }

        private void LoadDefaultClips()
        {
            coldEjectionClip ??= Resources.Load<AudioClip>(
                "Audio/Missile/cold_ejection");
            coldIgnitionClip ??= Resources.Load<AudioClip>(
                "Audio/Missile/cold_ignition");
            hotIgnitionClip ??= Resources.Load<AudioClip>(
                "Audio/Missile/hot_ignition_medium");
            motorLoopClip ??= Resources.Load<AudioClip>(
                "Audio/Missile/motor_loop_close");
            flybyClip ??= Resources.Load<AudioClip>(
                "Audio/Missile/flyby_close");
        }

        private void PreloadClips()
        {
            coldEjectionClip?.LoadAudioData();
            coldIgnitionClip?.LoadAudioData();
            hotIgnitionClip?.LoadAudioData();
            motorLoopClip?.LoadAudioData();
            flybyClip?.LoadAudioData();
        }

        private AudioSource CreateWorldSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.minDistance = minimumDistanceMeters;
            source.maxDistance = maximumDistanceMeters;
            source.SetCustomCurve(
                AudioSourceCurveType.CustomRolloff,
                CreateExternalRolloffCurve());
            source.priority = 160;
            return source;
        }

        private static AnimationCurve CreateExternalRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.25f, 0.78f),
                new Keyframe(0.65f, 0.22f),
                new Keyframe(1f, 0f));
        }

        private static AnimationCurve CreateMotorRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.45f, 0.85f),
                new Keyframe(0.75f, 0.34f),
                new Keyframe(1f, 0f));
        }

        private static Transform FindListenerTransform()
        {
            var listener =
                FindAnyObjectByType<AudioListener>();
            if (listener != null)
            {
                return listener.transform;
            }

            return Camera.main != null
                ? Camera.main.transform
                : null;
        }

        private void ResetFlybyTracking()
        {
            hasListenerDistance = false;
            flybyArmed = false;
            flybyPlayed = false;
            minimumListenerDistance = float.PositiveInfinity;
        }

        private float InstancePitch()
        {
            var step =
                Mathf.Abs(GetEntityId().GetHashCode()) % 9 - 4;
            return 1f + step * 0.003f;
        }
    }
}
