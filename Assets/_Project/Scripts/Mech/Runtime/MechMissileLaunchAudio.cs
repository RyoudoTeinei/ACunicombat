using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Ownship-only missile launch audio transmitted through the mech structure.
    /// Salvo releases are coalesced so four missiles do not stack four identical
    /// interior impacts in the same frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechMissileLaunchAudio : MonoBehaviour
    {
        private const float ReleaseCoalesceSeconds = 0.18f;
        private const float MechanicalDurationSeconds = 0.55f;
        private const float MechanicalFadeSeconds = 0.1f;

        [SerializeField] private AudioClip interiorLaunchClip;
        [SerializeField] private AudioClip launcherMechanicalClip;
        [SerializeField, Range(0f, 1f)] private float interiorVolume = 0.3f;
        [SerializeField, Range(0f, 1f)] private float mechanicalVolume = 0.2f;

        private AudioSource interiorSource;
        private AudioSource mechanicalSource;
        private float nextReleaseTime;
        private float mechanicalStopTime = -1f;

        private void Awake()
        {
            if (interiorLaunchClip == null)
            {
                interiorLaunchClip = Resources.Load<AudioClip>(
                    "Audio/Missile/ownship_interior_launch");
            }
            if (launcherMechanicalClip == null)
            {
                launcherMechanicalClip = Resources.Load<AudioClip>(
                    "Audio/Missile/launcher_mechanical");
            }
            interiorLaunchClip?.LoadAudioData();
            launcherMechanicalClip?.LoadAudioData();

            interiorSource = CreateStructureSource();
            mechanicalSource = CreateStructureSource();
        }

        public void PlayRelease(bool coldLaunch)
        {
            var now = Time.unscaledTime;
            if (now < nextReleaseTime)
            {
                return;
            }

            nextReleaseTime = now + ReleaseCoalesceSeconds;
            if (interiorLaunchClip != null)
            {
                interiorSource.PlayOneShot(
                    interiorLaunchClip,
                    interiorVolume);
            }

            if (!coldLaunch || launcherMechanicalClip == null)
            {
                return;
            }

            mechanicalSource.Stop();
            mechanicalSource.clip = launcherMechanicalClip;
            mechanicalSource.volume = mechanicalVolume;
            mechanicalSource.Play();
            mechanicalStopTime = now + MechanicalDurationSeconds;
        }

        private void Update()
        {
            if (mechanicalStopTime < 0f ||
                mechanicalSource == null ||
                !mechanicalSource.isPlaying)
            {
                return;
            }

            var remaining = mechanicalStopTime - Time.unscaledTime;
            if (remaining <= 0f)
            {
                mechanicalSource.Stop();
                mechanicalSource.volume = mechanicalVolume;
                mechanicalStopTime = -1f;
                return;
            }

            if (remaining < MechanicalFadeSeconds)
            {
                mechanicalSource.volume =
                    mechanicalVolume *
                    Mathf.Clamp01(
                        remaining / MechanicalFadeSeconds);
            }
        }

        private void OnDisable()
        {
            interiorSource?.Stop();
            mechanicalSource?.Stop();
            mechanicalStopTime = -1f;
        }

        private AudioSource CreateStructureSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.priority = 80;
            return source;
        }
    }
}
