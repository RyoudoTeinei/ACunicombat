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
        private MechAudioVariantSet interiorLaunchVariants;
        private MechAudioVariantSet launcherMechanicalVariants;

        private void Awake()
        {
            const string root = "Audio/Missile/";
            interiorLaunchVariants = MechAudioVariantSet.Load(
                root, "ownship_interior_launch", "ownship_interior_launch_", 6,
                interiorLaunchClip);
            launcherMechanicalVariants = MechAudioVariantSet.Load(
                root, "launcher_mechanical", "launcher_mechanical_", 3,
                launcherMechanicalClip);

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
            var interiorClip = interiorLaunchVariants.Next();
            if (interiorClip != null)
            {
                interiorSource.PlayOneShot(
                    interiorClip,
                    interiorVolume);
            }

            if (!coldLaunch)
            {
                return;
            }

            var mechanicalClip = launcherMechanicalVariants.Next();
            if (mechanicalClip == null)
            {
                return;
            }
            mechanicalSource.Stop();
            mechanicalSource.clip = mechanicalClip;
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
