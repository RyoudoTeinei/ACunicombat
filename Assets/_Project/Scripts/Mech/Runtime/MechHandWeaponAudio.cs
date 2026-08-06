using System.Collections;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum AutomaticCannonAudioProfile : byte
    {
        Flak38 = 0,
        Legacy2A42 = 1,
        Flak43 = 2,
    }

    /// <summary>
    /// Layered hand-weapon audio driven by confirmed projectile spawns.
    /// The automatic cannon treats a held trigger as one burst so its first-shot
    /// transient and stop tail are not repeated for every round.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechHandWeaponAudio : MonoBehaviour
    {
        private const float RifleBurstQuietSeconds = 0.14f;

        [Header("Legacy 30 mm automatic cannon")]
        [SerializeField] private AudioClip rifleFirstCloseClip;
        [SerializeField] private AudioClip rifleShotClip;
        [SerializeField] private AudioClip rifleLowClip;
        [SerializeField] private AudioClip rifleInteriorClip;
        [SerializeField] private AudioClip rifleStopTailClip;
        [SerializeField, Range(0f, 1f)] private float rifleFirstVolume = 0.42f;
        [SerializeField, Range(0f, 1f)] private float rifleShotVolume = 0.32f;
        [SerializeField, Range(0f, 1f)] private float rifleLowVolume = 0.12f;
        [SerializeField, Range(0f, 1f)] private float rifleInteriorVolume = 0.16f;
        [SerializeField, Range(0f, 1f)] private float rifleStopTailVolume = 0.28f;

        [Header("20 mm Flak 38 automatic cannon")]
        [SerializeField] private AudioClip flakFirstCloseClip;
        [SerializeField] private AudioClip flakShotClip;
        [SerializeField] private AudioClip flakBassClip;
        [SerializeField] private AudioClip flakFarClip;
        [SerializeField] private AudioClip flakStopTailClip;
        [SerializeField, Range(0f, 1f)] private float flakFirstVolume = 0.4f;
        [SerializeField, Range(0f, 1f)] private float flakShotVolume = 0.3f;
        [SerializeField, Range(0f, 1f)] private float flakBassVolume = 0.14f;
        [SerializeField, Range(0f, 1f)] private float flakFarVolume = 0.45f;
        [SerializeField, Range(0f, 1f)] private float flakStopTailVolume = 0.27f;
        [SerializeField, Min(1f)] private float flakFarSwitchDistanceMeters = 280f;
        [SerializeField, Min(1f)] private float flakFarMaximumDistanceMeters = 1800f;

        [Header("37 mm Flak 43 automatic cannon")]
        [SerializeField] private AudioClip flak43FirstCloseClip;
        [SerializeField] private AudioClip flak43ShotClip;
        [SerializeField] private AudioClip flak43BassClip;
        [SerializeField] private AudioClip flak43FarClip;
        [SerializeField] private AudioClip flak43InteriorClip;
        [SerializeField] private AudioClip flak43LastCloseClip;
        [SerializeField] private AudioClip flak43LastFarClip;
        [SerializeField] private AudioClip flak43LastInteriorClip;
        [SerializeField, Range(0f, 1f)] private float flak43FirstVolume = 0.44f;
        [SerializeField, Range(0f, 1f)] private float flak43ShotVolume = 0.34f;
        [SerializeField, Range(0f, 1f)] private float flak43BassVolume = 0.18f;
        [SerializeField, Range(0f, 1f)] private float flak43FarVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] private float flak43InteriorVolume = 0.18f;
        [SerializeField, Range(0f, 1f)] private float flak43LastCloseVolume = 0.3f;
        [SerializeField, Range(0f, 1f)] private float flak43LastFarVolume = 0.44f;
        [SerializeField, Range(0f, 1f)] private float flak43LastInteriorVolume = 0.2f;

        [Header("57 mm cannon")]
        [SerializeField] private AudioClip cannonShotClip;
        [SerializeField] private AudioClip cannonShellClip;
        [SerializeField, Range(0f, 1f)] private float cannonShotVolume = 0.65f;
        [SerializeField, Range(0f, 1f)] private float cannonShellVolume = 0.24f;
        [SerializeField, Min(0f)] private float cannonShellDelaySeconds = 0.48f;

        [Header("57 mm autoloader")]
        [SerializeField] private AudioClip cannonAutoloaderTurnClip;
        [SerializeField] private AudioClip cannonAutoloaderServoClip;
        [SerializeField] private AudioClip cannonAutoloaderShellPushClip;
        [SerializeField] private AudioClip cannonAutoloaderEndTurnClip;
        [SerializeField, Range(0f, 1f)] private float cannonAutoloaderVolume = 0.3f;
        [SerializeField, Range(0f, 1f)] private float cannonAutoloaderServoVolume = 0.65f;
        [SerializeField, Range(0f, 1f)] private float cannonAutoloaderEndTurnVolume = 0.5f;

        [Header("30 mm magazine reload")]
        [SerializeField] private AudioClip rifleMagazineOutClip;
        [SerializeField] private AudioClip rifleMagazineMoveClip;
        [SerializeField] private AudioClip rifleMagazineInClip;
        [SerializeField] private AudioClip rifleBoltClip;
        [SerializeField, Range(0f, 1f)] private float rifleReloadVolume = 0.32f;

        [Header("External attenuation")]
        [SerializeField, Min(0.1f)] private float minimumDistanceMeters = 8f;
        [SerializeField, Min(1f)] private float maximumDistanceMeters = 320f;

        private AudioSource rifleTransientSource;
        private AudioSource rifleShotSource;
        private AudioSource rifleLowSource;
        private AudioSource rifleTailSource;
        private AudioSource rifleFarSource;
        private AudioSource cannonSource;
        private AudioSource shellExternalSource;
        private AudioSource reloadExternalSource;
        private AudioSource structureSource;
        private AudioSource rifleInteriorTailSource;
        private bool rifleBurstActive;
        private bool rifleTriggerHeld;
        private AutomaticCannonAudioProfile rifleBurstProfile;
        private bool rifleBurstUsesFarLayer;
        private bool rifleBurstOwnship;
        private float lastRifleShotTime = float.NegativeInfinity;
        private MechAudioVariantSet rifleFirstCloseVariants;
        private MechAudioVariantSet rifleShotVariants;
        private MechAudioVariantSet rifleLowVariants;
        private MechAudioVariantSet rifleInteriorVariants;
        private MechAudioVariantSet rifleStopTailVariants;
        private MechAudioVariantSet flakFirstCloseVariants;
        private MechAudioVariantSet flakShotVariants;
        private MechAudioVariantSet flakBassVariants;
        private MechAudioVariantSet flakFarVariants;
        private MechAudioVariantSet flakStopTailVariants;
        private MechAudioVariantSet flak43FirstCloseVariants;
        private MechAudioVariantSet flak43ShotVariants;
        private MechAudioVariantSet flak43BassVariants;
        private MechAudioVariantSet flak43FarVariants;
        private MechAudioVariantSet flak43InteriorVariants;
        private MechAudioVariantSet flak43LastCloseVariants;
        private MechAudioVariantSet flak43LastFarVariants;
        private MechAudioVariantSet flak43LastInteriorVariants;
        private MechAudioVariantSet cannonShotVariants;
        private MechAudioVariantSet cannonShellVariants;
        private MechAudioVariantSet cannonAutoloaderTurnVariants;
        private MechAudioVariantSet cannonAutoloaderServoVariants;
        private MechAudioVariantSet cannonAutoloaderShellPushVariants;
        private MechAudioVariantSet cannonAutoloaderEndTurnVariants;
        private MechAudioVariantSet rifleMagazineOutVariants;
        private MechAudioVariantSet rifleMagazineMoveVariants;
        private MechAudioVariantSet rifleMagazineInVariants;
        private MechAudioVariantSet rifleBoltVariants;

        private void Awake()
        {
            LoadVariantSets();

            rifleTransientSource = CreateExternalSource("Rifle Transient Audio");
            rifleShotSource = CreateExternalSource("Rifle Shot Audio");
            rifleLowSource = CreateExternalSource("Rifle Low Audio");
            rifleTailSource = CreateExternalSource("Rifle Tail Audio");
            rifleFarSource = CreateExternalSource(
                "Rifle Far Audio",
                Mathf.Max(minimumDistanceMeters, flakFarSwitchDistanceMeters * 0.4f),
                flakFarMaximumDistanceMeters);
            cannonSource = CreateExternalSource("Cannon Shot Audio");
            shellExternalSource = CreateExternalSource("Cannon Shell Audio");
            reloadExternalSource = CreateExternalSource("Weapon Reload Audio");
            structureSource = CreateStructureSource();
            rifleInteriorTailSource = CreateStructureSource();
        }

        public void SetRifleTriggerHeld(bool held)
        {
            rifleTriggerHeld = held;
        }

        public void PlayRifleShot(
            Vector3 muzzlePosition,
            bool ownship,
            AutomaticCannonAudioProfile profile)
        {
            var now = Time.unscaledTime;
            var useFarFlak =
                profile != AutomaticCannonAudioProfile.Legacy2A42 &&
                UseFlakFarLayer(muzzlePosition, ownship);
            var profileChanged =
                rifleBurstActive &&
                (rifleBurstProfile != profile ||
                 rifleBurstUsesFarLayer != useFarFlak);
            var startsBurst =
                !rifleBurstActive ||
                profileChanged ||
                now - lastRifleShotTime > RifleBurstQuietSeconds;
            rifleBurstActive = true;
            rifleBurstProfile = profile;
            rifleBurstUsesFarLayer = useFarFlak;
            rifleBurstOwnship = ownship;
            lastRifleShotTime = now;

            MoveExternalSources(muzzlePosition);
            if (startsBurst)
            {
                rifleTailSource.Stop();
                rifleFarSource.Stop();
                rifleInteriorTailSource.Stop();
            }
            switch (profile)
            {
                case AutomaticCannonAudioProfile.Legacy2A42:
                    PlayLegacyRifleShot(startsBurst, ownship);
                    break;
                case AutomaticCannonAudioProfile.Flak43:
                    PlayFlak43Shot(startsBurst, useFarFlak, ownship);
                    break;
                default:
                    PlayFlak38Shot(startsBurst, useFarFlak);
                    break;
            }
        }

        private void PlayLegacyRifleShot(bool startsBurst, bool ownship)
        {
            if (startsBurst)
            {
                PlayOneShot(
                    rifleTransientSource,
                    rifleFirstCloseVariants.Next(),
                    rifleFirstVolume);
            }

            PlayOneShot(
                rifleShotSource,
                rifleShotVariants.Next(),
                rifleShotVolume);
            PlayOneShot(
                rifleLowSource,
                rifleLowVariants.Next(),
                rifleLowVolume);
            if (ownship)
            {
                PlayOneShot(
                    structureSource,
                    rifleInteriorVariants.Next(),
                    rifleInteriorVolume);
            }
        }

        private void PlayFlak38Shot(bool startsBurst, bool useFarLayer)
        {
            if (useFarLayer)
            {
                PlayOneShot(
                    rifleFarSource,
                    flakFarVariants.Next(),
                    flakFarVolume);
                return;
            }

            if (startsBurst)
            {
                PlayOneShot(
                    rifleTransientSource,
                    flakFirstCloseVariants.Next(),
                    flakFirstVolume);
            }
            PlayOneShot(
                rifleShotSource,
                flakShotVariants.Next(),
                flakShotVolume);
            PlayOneShot(
                rifleLowSource,
                flakBassVariants.Next(),
                flakBassVolume);
        }

        private void PlayFlak43Shot(
            bool startsBurst,
            bool useFarLayer,
            bool ownship)
        {
            if (useFarLayer)
            {
                PlayOneShot(
                    rifleFarSource,
                    flak43FarVariants.Next(),
                    flak43FarVolume);
                return;
            }

            if (startsBurst)
            {
                PlayOneShot(
                    rifleTransientSource,
                    flak43FirstCloseVariants.Next(),
                    flak43FirstVolume);
            }
            PlayOneShot(
                rifleShotSource,
                flak43ShotVariants.Next(),
                flak43ShotVolume);
            PlayOneShot(
                rifleLowSource,
                flak43BassVariants.Next(),
                flak43BassVolume);
            if (ownship)
            {
                PlayOneShot(
                    structureSource,
                    flak43InteriorVariants.Next(),
                    flak43InteriorVolume);
            }
        }

        public void PlayCannonShot(Vector3 muzzlePosition, bool ownship)
        {
            cannonSource.transform.position = muzzlePosition;
            shellExternalSource.transform.position = muzzlePosition;
            PlayOneShot(
                cannonSource,
                cannonShotVariants.Next(),
                cannonShotVolume);

            var shellClip = cannonShellVariants.Next();
            if (shellClip != null)
            {
                StartCoroutine(
                    PlayCannonShellAfterDelay(ownship, shellClip));
            }
        }

        public void PlayCannonReload(Vector3 weaponPosition, bool ownship)
        {
            StartCoroutine(PlayCannonReloadSequence(weaponPosition, ownship));
        }

        public void PlayRifleReload(Vector3 weaponPosition, bool ownship)
        {
            StartCoroutine(PlayRifleReloadSequence(weaponPosition, ownship));
        }

        private void Update()
        {
            if (!rifleBurstActive ||
                rifleTriggerHeld ||
                Time.unscaledTime - lastRifleShotTime <
                RifleBurstQuietSeconds)
            {
                return;
            }

            rifleBurstActive = false;
            switch (rifleBurstProfile)
            {
                case AutomaticCannonAudioProfile.Legacy2A42:
                    PlayOneShot(
                        rifleTailSource,
                        rifleStopTailVariants.Next(),
                        rifleStopTailVolume);
                    break;
                case AutomaticCannonAudioProfile.Flak43:
                    PlayFlak43Tail();
                    break;
                default:
                    if (!rifleBurstUsesFarLayer)
                    {
                        PlayOneShot(
                            rifleTailSource,
                            flakStopTailVariants.Next(),
                            flakStopTailVolume);
                    }
                    break;
            }
        }

        private void PlayFlak43Tail()
        {
            if (rifleBurstUsesFarLayer)
            {
                PlayOneShot(
                    rifleFarSource,
                    flak43LastFarVariants.Next(),
                    flak43LastFarVolume);
                return;
            }

            PlayOneShot(
                rifleTailSource,
                flak43LastCloseVariants.Next(),
                flak43LastCloseVolume);
            if (rifleBurstOwnship)
            {
                PlayOneShot(
                    rifleInteriorTailSource,
                    flak43LastInteriorVariants.Next(),
                    flak43LastInteriorVolume);
            }
        }

        private IEnumerator PlayCannonShellAfterDelay(
            bool ownship,
            AudioClip shellClip)
        {
            yield return new WaitForSecondsRealtime(cannonShellDelaySeconds);
            var source = ownship ? structureSource : shellExternalSource;
            PlayOneShot(source, shellClip, cannonShellVolume);
        }

        private IEnumerator PlayCannonReloadSequence(
            Vector3 weaponPosition,
            bool ownship)
        {
            PlayReloadClip(
                cannonAutoloaderTurnVariants.Next(),
                cannonAutoloaderVolume,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(0.65f);
            PlayReloadClip(
                cannonAutoloaderServoVariants.Next(),
                cannonAutoloaderServoVolume,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.6f);
            PlayReloadClip(
                cannonAutoloaderShellPushVariants.Next(),
                cannonAutoloaderVolume * 1.08f,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.49f);
            PlayReloadClip(
                cannonAutoloaderEndTurnVariants.Next(),
                cannonAutoloaderEndTurnVolume,
                weaponPosition,
                ownship);
        }

        private IEnumerator PlayRifleReloadSequence(
            Vector3 weaponPosition,
            bool ownship)
        {
            PlayReloadClip(
                rifleMagazineOutVariants.Next(),
                rifleReloadVolume,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(0.85f);
            PlayReloadClip(
                rifleMagazineMoveVariants.Next(),
                rifleReloadVolume * 0.9f,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.8f);
            PlayReloadClip(
                rifleMagazineInVariants.Next(),
                rifleReloadVolume * 1.08f,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.1f);
            PlayReloadClip(
                rifleBoltVariants.Next(),
                rifleReloadVolume * 1.15f,
                weaponPosition,
                ownship);
        }

        private void PlayReloadClip(
            AudioClip clip,
            float volume,
            Vector3 weaponPosition,
            bool ownship)
        {
            if (clip == null)
            {
                return;
            }

            var source = ownship ? structureSource : reloadExternalSource;
            if (!ownship)
            {
                source.transform.position = weaponPosition;
            }
            PlayOneShot(source, clip, volume);
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            rifleTransientSource?.Stop();
            rifleShotSource?.Stop();
            rifleLowSource?.Stop();
            rifleTailSource?.Stop();
            rifleFarSource?.Stop();
            cannonSource?.Stop();
            shellExternalSource?.Stop();
            reloadExternalSource?.Stop();
            structureSource?.Stop();
            rifleInteriorTailSource?.Stop();
            rifleBurstActive = false;
            rifleTriggerHeld = false;
            rifleBurstOwnship = false;
        }

        private void LoadVariantSets()
        {
            const string weaponRoot = "Audio/Weapons/";
            const string reloadRoot = "Audio/Weapons/Reload/";
            rifleFirstCloseVariants = MechAudioVariantSet.Load(
                weaponRoot, "2a42_first_close", "2a42_first_close_", 3,
                rifleFirstCloseClip);
            rifleShotVariants = MechAudioVariantSet.Load(
                weaponRoot, "2a42_close", "2a42_close_", 8,
                rifleShotClip);
            rifleLowVariants = MechAudioVariantSet.Load(
                weaponRoot, "2a42_low_close", "2a42_low_close_", 8,
                rifleLowClip);
            rifleInteriorVariants = MechAudioVariantSet.Load(
                weaponRoot, "2a42_first_interior", "2a42_first_interior_", 3,
                rifleInteriorClip);
            rifleStopTailVariants = MechAudioVariantSet.Load(
                weaponRoot, "2a72_last_close", "2a72_last_close_", 3,
                rifleStopTailClip);
            const string flakRoot = "Audio/Weapons/Flak38/";
            flakFirstCloseVariants = MechAudioVariantSet.Load(
                flakRoot, "flak38_first_close_001",
                "flak38_first_close_", 3, flakFirstCloseClip);
            flakShotVariants = MechAudioVariantSet.Load(
                flakRoot, "flak38_close_001",
                "flak38_close_", 12, flakShotClip);
            flakBassVariants = MechAudioVariantSet.Load(
                flakRoot, "flak38_bass_001",
                "flak38_bass_", 12, flakBassClip);
            flakFarVariants = MechAudioVariantSet.Load(
                flakRoot, "flak38_far_001",
                "flak38_far_", 8, flakFarClip);
            flakStopTailVariants = MechAudioVariantSet.Load(
                flakRoot, "flak38_last_close_001",
                "flak38_last_close_", 3, flakStopTailClip);
            const string flak43Root = "Audio/Weapons/Flak43/";
            flak43FirstCloseVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_first_close_001",
                "flak43_first_close_", 3, flak43FirstCloseClip);
            flak43ShotVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_close_001",
                "flak43_close_", 7, flak43ShotClip);
            flak43BassVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_bass_001",
                "flak43_bass_", 7, flak43BassClip);
            flak43FarVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_far_001",
                "flak43_far_", 7, flak43FarClip);
            flak43InteriorVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_interior_001",
                "flak43_interior_", 7, flak43InteriorClip);
            flak43LastCloseVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_last_close_001",
                "flak43_last_close_", 3, flak43LastCloseClip);
            flak43LastFarVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_last_far_001",
                "flak43_last_far_", 3, flak43LastFarClip);
            flak43LastInteriorVariants = MechAudioVariantSet.Load(
                flak43Root, "flak43_last_interior_001",
                "flak43_last_interior_", 3, flak43LastInteriorClip);
            cannonShotVariants = MechAudioVariantSet.Load(
                weaponRoot, "cannon_57mm_qf6_shot",
                "cannon_57mm_qf6_shot_", 3, cannonShotClip);
            cannonShellVariants = MechAudioVariantSet.Load(
                weaponRoot, "cannon_shell_drop", "cannon_shell_drop_", 7,
                cannonShellClip);
            cannonAutoloaderTurnVariants = MechAudioVariantSet.Load(
                reloadRoot, "cannon_autoloader_turn_noise",
                "cannon_autoloader_turn_noise_", 3,
                cannonAutoloaderTurnClip);
            cannonAutoloaderServoVariants = MechAudioVariantSet.Load(
                reloadRoot, "cannon_autoloader_servo",
                "cannon_autoloader_servo_", 2,
                cannonAutoloaderServoClip);
            cannonAutoloaderShellPushVariants = MechAudioVariantSet.Load(
                reloadRoot, "cannon_autoloader_shell_push",
                "cannon_autoloader_shell_push_", 4,
                cannonAutoloaderShellPushClip);
            cannonAutoloaderEndTurnVariants = MechAudioVariantSet.Load(
                reloadRoot, "cannon_autoloader_end_turn",
                "cannon_autoloader_end_turn_", 4,
                cannonAutoloaderEndTurnClip);
            rifleMagazineOutVariants = MechAudioVariantSet.Load(
                reloadRoot, "mag_out", "mag_out_", 3,
                rifleMagazineOutClip);
            rifleMagazineMoveVariants = MechAudioVariantSet.Load(
                reloadRoot, "mag_move", "mag_move_", 3,
                rifleMagazineMoveClip);
            rifleMagazineInVariants = MechAudioVariantSet.Load(
                reloadRoot, "mag_in", "mag_in_", 3,
                rifleMagazineInClip);
            rifleBoltVariants = MechAudioVariantSet.Load(
                reloadRoot, "bolt", "bolt_", 2, rifleBoltClip);
        }

        private static void PlayOneShot(
            AudioSource source,
            AudioClip clip,
            float volume)
        {
            if (source != null && clip != null)
            {
                source.PlayOneShot(clip, Mathf.Clamp01(volume));
            }
        }

        private AudioSource CreateExternalSource(string sourceName)
        {
            return CreateExternalSource(
                sourceName, minimumDistanceMeters, maximumDistanceMeters);
        }

        private AudioSource CreateExternalSource(
            string sourceName,
            float minimumDistance,
            float maximumDistance)
        {
            var holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.minDistance = Mathf.Max(0.1f, minimumDistance);
            source.maxDistance = Mathf.Max(
                source.minDistance + 0.1f, maximumDistance);
            source.SetCustomCurve(
                AudioSourceCurveType.CustomRolloff,
                CreateExternalRolloffCurve());
            source.priority = 96;
            return source;
        }

        private AudioSource CreateStructureSource()
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = false;
            source.spatialBlend = 0f;
            source.dopplerLevel = 0f;
            source.priority = 72;
            return source;
        }

        private void MoveExternalSources(Vector3 position)
        {
            rifleTransientSource.transform.position = position;
            rifleShotSource.transform.position = position;
            rifleLowSource.transform.position = position;
            rifleTailSource.transform.position = position;
            rifleFarSource.transform.position = position;
        }

        private bool UseFlakFarLayer(
            Vector3 muzzlePosition,
            bool ownship)
        {
            if (ownship)
            {
                return false;
            }

            var listenerCamera = Camera.main;
            return listenerCamera != null &&
                   Vector3.Distance(
                       listenerCamera.transform.position,
                       muzzlePosition) >= flakFarSwitchDistanceMeters;
        }

        private static AnimationCurve CreateExternalRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.22f, 0.82f),
                new Keyframe(0.65f, 0.25f),
                new Keyframe(1f, 0f));
        }
    }
}
