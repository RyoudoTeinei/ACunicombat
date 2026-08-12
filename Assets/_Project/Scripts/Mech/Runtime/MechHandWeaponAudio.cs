using System.Collections;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum AutomaticCannonAudioProfile : byte
    {
        Flak38 = 0,
        Legacy2A42 = 1,
        Flak43 = 2,
        Gau8A = 3,
    }

    public enum CannonAudioProfile : byte
    {
        Cannon57Mm = 0,
        Gau8A = 1,
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
        private const float Gau8BurstQuietSeconds = 0.22f;

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

        [Header("GAU-8/A 30 mm rotary cannon")]
        [SerializeField] private AudioClip gau8LoopShotClip;
        [SerializeField] private AudioClip gau8LoopBassClip;
        [SerializeField] private AudioClip gau8RotorLoopClip;
        [SerializeField, Range(0f, 1f)] private float gau8FirstSnarlVolume = 0.46f;
        [SerializeField, Range(0f, 1f)] private float gau8LoopShotVolume = 0.42f;
        [SerializeField, Range(0f, 1f)] private float gau8LoopBassVolume = 0.25f;
        [SerializeField, Range(0f, 1f)] private float gau8LastSnarlVolume = 0.38f;
        [SerializeField, Range(0f, 1f)] private float gau8RotorStartVolume = 0.28f;
        [SerializeField, Range(0f, 1f)] private float gau8RotorLoopVolume = 0.16f;
        [SerializeField, Range(0f, 1f)] private float gau8RotorStopVolume = 0.26f;
        [SerializeField, Min(0.01f)] private float gau8LoopFadeInSeconds = 0.05f;
        [SerializeField, Min(0.01f)] private float gau8LoopFadeOutSeconds = 0.09f;

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
        private AudioSource gau8TransientSource;
        private AudioSource gau8ShotLoopSource;
        private AudioSource gau8BassLoopSource;
        private AudioSource gau8RotorLoopSource;
        private AudioSource reloadExternalSource;
        private AudioSource structureSource;
        private AudioSource rifleInteriorTailSource;
        private bool rifleBurstActive;
        private bool rifleTriggerHeld;
        private AutomaticCannonAudioProfile rifleBurstProfile;
        private bool rifleBurstUsesFarLayer;
        private bool rifleBurstOwnship;
        private float lastRifleShotTime = float.NegativeInfinity;
        private bool gau8BurstActive;
        private float lastGau8ShotTime = float.NegativeInfinity;
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
        private MechAudioVariantSet gau8FirstSnarlVariants;
        private MechAudioVariantSet gau8LastSnarlVariants;
        private MechAudioVariantSet gau8RotorStartVariants;
        private MechAudioVariantSet gau8RotorStopVariants;
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
            gau8TransientSource = CreateExternalSource("GAU-8 Transient Audio");
            gau8ShotLoopSource = CreateLoopingExternalSource(
                "GAU-8 Shot Loop Audio", gau8LoopShotClip);
            gau8BassLoopSource = CreateLoopingExternalSource(
                "GAU-8 Bass Loop Audio", gau8LoopBassClip);
            gau8RotorLoopSource = CreateLoopingExternalSource(
                "GAU-8 Rotor Loop Audio", gau8RotorLoopClip);
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
            if (profile == AutomaticCannonAudioProfile.Gau8A)
            {
                PlayGau8Shot(muzzlePosition);
                return;
            }

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

        public void PlayCannonShot(
            Vector3 muzzlePosition,
            bool ownship,
            CannonAudioProfile profile)
        {
            if (profile == CannonAudioProfile.Gau8A)
            {
                PlayGau8Shot(muzzlePosition);
                return;
            }

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

        private void PlayGau8Shot(Vector3 muzzlePosition)
        {
            var now = Time.unscaledTime;
            var startsBurst =
                !gau8BurstActive ||
                now - lastGau8ShotTime > Gau8BurstQuietSeconds;
            gau8BurstActive = true;
            lastGau8ShotTime = now;
            MoveGau8Sources(muzzlePosition);

            if (!startsBurst)
            {
                return;
            }

            gau8TransientSource.Stop();
            PlayOneShot(
                gau8TransientSource,
                gau8RotorStartVariants.Next(),
                gau8RotorStartVolume);
            PlayOneShot(
                gau8TransientSource,
                gau8FirstSnarlVariants.Next(),
                gau8FirstSnarlVolume);
        }

        public void PlayCannonReload(
            Vector3 weaponPosition,
            bool ownship,
            CannonAudioProfile profile)
        {
            if (profile == CannonAudioProfile.Gau8A)
            {
                return;
            }

            StartCoroutine(PlayCannonReloadSequence(weaponPosition, ownship));
        }

        public void PlayRifleReload(Vector3 weaponPosition, bool ownship)
        {
            StartCoroutine(PlayRifleReloadSequence(weaponPosition, ownship));
        }

        private void Update()
        {
            UpdateRifleBurst();
            UpdateGau8Burst();
            UpdateGau8Loops(Time.unscaledDeltaTime);
        }

        private void UpdateRifleBurst()
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

        private void UpdateGau8Burst()
        {
            if (!gau8BurstActive ||
                Time.unscaledTime - lastGau8ShotTime <
                Gau8BurstQuietSeconds)
            {
                return;
            }

            gau8BurstActive = false;
            PlayOneShot(
                gau8TransientSource,
                gau8LastSnarlVariants.Next(),
                gau8LastSnarlVolume);
            PlayOneShot(
                gau8TransientSource,
                gau8RotorStopVariants.Next(),
                gau8RotorStopVolume);
        }

        private void UpdateGau8Loops(float deltaTime)
        {
            UpdateLoopingSource(
                gau8ShotLoopSource,
                gau8BurstActive ? gau8LoopShotVolume : 0f,
                deltaTime);
            UpdateLoopingSource(
                gau8BassLoopSource,
                gau8BurstActive ? gau8LoopBassVolume : 0f,
                deltaTime);
            UpdateLoopingSource(
                gau8RotorLoopSource,
                gau8BurstActive ? gau8RotorLoopVolume : 0f,
                deltaTime);
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
            gau8TransientSource?.Stop();
            gau8ShotLoopSource?.Stop();
            gau8BassLoopSource?.Stop();
            gau8RotorLoopSource?.Stop();
            reloadExternalSource?.Stop();
            structureSource?.Stop();
            rifleInteriorTailSource?.Stop();
            rifleBurstActive = false;
            rifleTriggerHeld = false;
            rifleBurstOwnship = false;
            gau8BurstActive = false;
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
            const string gau8Root = "Audio/Weapons/GAU8A/";
            gau8FirstSnarlVariants = MechAudioVariantSet.Load(
                gau8Root, "gau8a_first_snarl_001",
                "gau8a_first_snarl_", 3);
            gau8LoopShotClip = LoadClip(
                gau8LoopShotClip, gau8Root + "gau8a_loop_shot");
            gau8LoopBassClip = LoadClip(
                gau8LoopBassClip, gau8Root + "gau8a_loop_bass");
            gau8LastSnarlVariants = MechAudioVariantSet.Load(
                gau8Root, "gau8a_last_snarl_001",
                "gau8a_last_snarl_", 3);
            gau8RotorStartVariants = MechAudioVariantSet.Load(
                gau8Root, "gau8a_rotor_start_001",
                "gau8a_rotor_start_", 2);
            gau8RotorLoopClip = LoadClip(
                gau8RotorLoopClip, gau8Root + "gau8a_rotor_loop");
            gau8RotorStopVariants = MechAudioVariantSet.Load(
                gau8Root, "gau8a_rotor_stop_001",
                "gau8a_rotor_stop_", 2);
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

        private static AudioClip LoadClip(
            AudioClip configuredClip,
            string resourcePath)
        {
            return configuredClip != null
                ? configuredClip
                : Resources.Load<AudioClip>(resourcePath);
        }

        private AudioSource CreateExternalSource(string sourceName)
        {
            return CreateExternalSource(
                sourceName, minimumDistanceMeters, maximumDistanceMeters);
        }

        private AudioSource CreateLoopingExternalSource(
            string sourceName,
            AudioClip clip)
        {
            var source = CreateExternalSource(sourceName);
            source.clip = clip;
            source.loop = true;
            source.volume = 0f;
            return source;
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

        private void MoveGau8Sources(Vector3 position)
        {
            gau8TransientSource.transform.position = position;
            gau8ShotLoopSource.transform.position = position;
            gau8BassLoopSource.transform.position = position;
            gau8RotorLoopSource.transform.position = position;
        }

        private void UpdateLoopingSource(
            AudioSource source,
            float targetVolume,
            float deltaTime)
        {
            if (source == null || source.clip == null)
            {
                return;
            }

            targetVolume = Mathf.Clamp01(targetVolume);
            if (targetVolume > 0f && !source.isPlaying)
            {
                source.volume = 0f;
                source.Play();
            }

            var fadeSeconds = targetVolume > source.volume
                ? gau8LoopFadeInSeconds
                : gau8LoopFadeOutSeconds;
            source.volume = Mathf.MoveTowards(
                source.volume,
                targetVolume,
                deltaTime / Mathf.Max(0.01f, fadeSeconds));

            if (targetVolume <= 0f &&
                source.volume <= 0.001f &&
                source.isPlaying)
            {
                source.Stop();
            }
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

    [DisallowMultipleComponent]
    internal sealed class MechPointDefenseAudio : MonoBehaviour
    {
        private sealed class ShoulderChannel
        {
            public GameObject Root;
            public AudioSource RotorTransient;
            public AudioSource RotorLoop;
            public AudioSource GunLoop;
            public AudioSource GunBass;
            public AudioSource ShotGrain;
            public Transform Anchor;
            public bool Spinning;
            public bool HighRateProfile;
            public float RoundsPerMinute = 3000f;
            public float LastShotTime = float.NegativeInfinity;
        }

        private const string ResourceRoot = "Audio/Weapons/M134/";
        private const float HighRateThresholdRpm = 4500f;

        [Header("M134 continuous fire")]
        [SerializeField] private AudioClip m1343000LoopClip;
        [SerializeField] private AudioClip m1343000BassClip;
        [SerializeField] private AudioClip m1346000LoopClip;
        [SerializeField] private AudioClip m1346000BassClip;
        [SerializeField] private AudioClip rotorLoopClip;
        [SerializeField, Range(0f, 1f)] private float m1343000LoopVolume = 0.42f;
        [SerializeField, Range(0f, 1f)] private float m1343000BassVolume = 0.27f;
        [SerializeField, Range(0f, 1f)] private float m1346000LoopVolume = 0.38f;
        [SerializeField, Range(0f, 1f)] private float m1346000BassVolume = 0.24f;

        [Header("M134 grains and rotor")]
        [SerializeField] private AudioClip shotGrainClip;
        [SerializeField] private AudioClip rotorStartClip;
        [SerializeField] private AudioClip rotorStopClip;
        [SerializeField, Range(0f, 1f)] private float shotGrainVolume = 0.2f;
        [SerializeField, Range(0f, 1f)] private float rotorStartVolume = 0.44f;
        [SerializeField, Range(0f, 1f)] private float rotorLoopVolume = 0.22f;
        [SerializeField, Range(0f, 1f)] private float rotorStopVolume = 0.42f;
        [SerializeField, Min(0f)] private float loopFadeInSeconds = 0.05f;
        [SerializeField, Min(0f)] private float loopFadeOutSeconds = 0.08f;
        [SerializeField, Min(0.05f)] private float minimumShotLoopHoldSeconds = 0.12f;

        private ShoulderChannel leftChannel;
        private ShoulderChannel rightChannel;
        private MechAudioVariantSet shotGrainVariants;
        private MechAudioVariantSet rotorStartVariants;
        private MechAudioVariantSet rotorStopVariants;

        private void Awake()
        {
            m1343000LoopClip ??= Resources.Load<AudioClip>(
                ResourceRoot + "m134_3000_loop");
            m1343000BassClip ??= Resources.Load<AudioClip>(
                ResourceRoot + "m134_3000_bass");
            m1346000LoopClip ??= Resources.Load<AudioClip>(
                ResourceRoot + "m134_6000_loop");
            m1346000BassClip ??= Resources.Load<AudioClip>(
                ResourceRoot + "m134_6000_bass");
            rotorLoopClip ??= Resources.Load<AudioClip>(
                ResourceRoot + "minigun_rotor_loop");

            LoadClipData(m1343000LoopClip);
            LoadClipData(m1343000BassClip);
            LoadClipData(m1346000LoopClip);
            LoadClipData(m1346000BassClip);
            LoadClipData(rotorLoopClip);

            shotGrainVariants = MechAudioVariantSet.Load(
                ResourceRoot, "m134_close_001", "m134_close_", 16,
                shotGrainClip);
            rotorStartVariants = MechAudioVariantSet.Load(
                ResourceRoot, "minigun_rotor_start_001",
                "minigun_rotor_start_", 3, rotorStartClip);
            rotorStopVariants = MechAudioVariantSet.Load(
                ResourceRoot, "minigun_rotor_stop_001",
                "minigun_rotor_stop_", 3, rotorStopClip);

            leftChannel = CreateChannel("Left Shoulder M134 Audio");
            rightChannel = CreateChannel("Right Shoulder M134 Audio");
        }

        public void SetRotorState(
            MechPartSlot slot,
            bool shouldSpin,
            Transform anchor,
            float roundsPerMinute)
        {
            var channel = ResolveChannel(slot);
            if (channel == null)
            {
                return;
            }

            if (anchor != null)
            {
                channel.Anchor = anchor;
            }
            channel.RoundsPerMinute = Mathf.Max(60f, roundsPerMinute);
            FollowAnchor(channel);
            if (channel.Spinning == shouldSpin)
            {
                return;
            }

            channel.Spinning = shouldSpin;
            if (shouldSpin)
            {
                PlayOneShot(
                    channel.RotorTransient,
                    rotorStartVariants.Next(),
                    rotorStartVolume);
                return;
            }

            channel.LastShotTime = float.NegativeInfinity;
            PlayOneShot(
                channel.RotorTransient,
                rotorStopVariants.Next(),
                rotorStopVolume);
        }

        public void PlayShot(
            MechPartSlot slot,
            Transform anchor,
            float roundsPerMinute)
        {
            var channel = ResolveChannel(slot);
            if (channel == null)
            {
                return;
            }

            if (anchor != null)
            {
                channel.Anchor = anchor;
            }
            channel.RoundsPerMinute = Mathf.Max(60f, roundsPerMinute);
            channel.HighRateProfile =
                channel.RoundsPerMinute >= HighRateThresholdRpm;
            channel.LastShotTime = Time.unscaledTime;
            FollowAnchor(channel);
            PlayOneShot(
                channel.ShotGrain,
                shotGrainVariants.Next(),
                shotGrainVolume);
        }

        private void Update()
        {
            var now = Time.unscaledTime;
            var deltaTime = Time.unscaledDeltaTime;
            UpdateChannel(leftChannel, now, deltaTime);
            UpdateChannel(rightChannel, now, deltaTime);
        }

        private void OnDisable()
        {
            StopChannel(leftChannel);
            StopChannel(rightChannel);
        }

        private void UpdateChannel(
            ShoulderChannel channel,
            float now,
            float deltaTime)
        {
            if (channel == null)
            {
                return;
            }

            FollowAnchor(channel);
            var cycleSeconds = 60f / Mathf.Max(60f, channel.RoundsPerMinute);
            var holdSeconds = Mathf.Max(
                minimumShotLoopHoldSeconds,
                cycleSeconds * 2.5f);
            var firing =
                channel.Spinning &&
                now - channel.LastShotTime <= holdSeconds;
            var gunLoopClip = channel.HighRateProfile
                ? m1346000LoopClip
                : m1343000LoopClip;
            var gunBassClip = channel.HighRateProfile
                ? m1346000BassClip
                : m1343000BassClip;
            var gunLoopVolume = channel.HighRateProfile
                ? m1346000LoopVolume
                : m1343000LoopVolume;
            var gunBassVolume = channel.HighRateProfile
                ? m1346000BassVolume
                : m1343000BassVolume;

            UpdateLoop(
                channel.RotorLoop,
                rotorLoopClip,
                channel.Spinning ? rotorLoopVolume : 0f,
                deltaTime);
            UpdateLoop(
                channel.GunLoop,
                gunLoopClip,
                firing ? gunLoopVolume : 0f,
                deltaTime);
            UpdateLoop(
                channel.GunBass,
                gunBassClip,
                firing ? gunBassVolume : 0f,
                deltaTime);
        }

        private ShoulderChannel ResolveChannel(MechPartSlot slot)
        {
            return slot switch
            {
                MechPartSlot.LeftBackWeapon => leftChannel,
                MechPartSlot.RightBackWeapon => rightChannel,
                _ => null,
            };
        }

        private ShoulderChannel CreateChannel(string name)
        {
            var root = new GameObject(name);
            root.transform.SetParent(transform, false);
            return new ShoulderChannel
            {
                Root = root,
                RotorTransient = CreateSource(root, false),
                RotorLoop = CreateSource(root, true),
                GunLoop = CreateSource(root, true),
                GunBass = CreateSource(root, true),
                ShotGrain = CreateSource(root, false),
            };
        }

        private static AudioSource CreateSource(GameObject root, bool loop)
        {
            var source = root.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.volume = loop ? 0f : 1f;
            source.spatialBlend = 1f;
            source.dopplerLevel = 0.1f;
            source.minDistance = 4f;
            source.maxDistance = 3000f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.priority = 96;
            return source;
        }

        private void UpdateLoop(
            AudioSource source,
            AudioClip clip,
            float targetVolume,
            float deltaTime)
        {
            if (source == null || clip == null)
            {
                return;
            }

            if (source.clip != clip)
            {
                source.Stop();
                source.clip = clip;
                source.volume = 0f;
            }
            if (targetVolume > 0f && !source.isPlaying)
            {
                source.Play();
            }

            var fadeSeconds = targetVolume > source.volume
                ? loopFadeInSeconds
                : loopFadeOutSeconds;
            var amplitude = Mathf.Max(
                Mathf.Max(targetVolume, source.volume),
                0.01f);
            var maxDelta = fadeSeconds > 0f
                ? amplitude * deltaTime / fadeSeconds
                : 1f;
            source.volume = Mathf.MoveTowards(
                source.volume,
                targetVolume,
                maxDelta);
            if (targetVolume <= 0f && source.volume <= 0.001f)
            {
                source.volume = 0f;
                source.Stop();
            }
        }

        private static void FollowAnchor(ShoulderChannel channel)
        {
            if (channel?.Root != null && channel.Anchor != null)
            {
                channel.Root.transform.position = channel.Anchor.position;
            }
        }

        private static void StopChannel(ShoulderChannel channel)
        {
            if (channel == null)
            {
                return;
            }

            channel.Spinning = false;
            channel.LastShotTime = float.NegativeInfinity;
            channel?.RotorTransient?.Stop();
            channel?.RotorLoop?.Stop();
            channel?.GunLoop?.Stop();
            channel?.GunBass?.Stop();
            channel?.ShotGrain?.Stop();
        }

        private static void PlayOneShot(
            AudioSource source,
            AudioClip clip,
            float volume)
        {
            if (source != null && clip != null && volume > 0f)
            {
                source.PlayOneShot(clip, volume);
            }
        }

        private static void LoadClipData(AudioClip clip)
        {
            clip?.LoadAudioData();
        }
    }
}
