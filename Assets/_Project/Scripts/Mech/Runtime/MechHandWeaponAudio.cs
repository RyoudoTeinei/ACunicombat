using System.Collections;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Layered hand-weapon audio driven by confirmed projectile spawns.
    /// The automatic cannon treats a held trigger as one burst so its first-shot
    /// transient and stop tail are not repeated for every round.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechHandWeaponAudio : MonoBehaviour
    {
        private const float RifleBurstQuietSeconds = 0.14f;

        [Header("30 mm automatic cannon")]
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
        private AudioSource cannonSource;
        private AudioSource shellExternalSource;
        private AudioSource reloadExternalSource;
        private AudioSource structureSource;
        private bool rifleBurstActive;
        private bool rifleTriggerHeld;
        private float lastRifleShotTime = float.NegativeInfinity;

        private void Awake()
        {
            LoadDefaultClips();
            PreloadClips();

            rifleTransientSource = CreateExternalSource("Rifle Transient Audio");
            rifleShotSource = CreateExternalSource("Rifle Shot Audio");
            rifleLowSource = CreateExternalSource("Rifle Low Audio");
            rifleTailSource = CreateExternalSource("Rifle Tail Audio");
            cannonSource = CreateExternalSource("Cannon Shot Audio");
            shellExternalSource = CreateExternalSource("Cannon Shell Audio");
            reloadExternalSource = CreateExternalSource("Weapon Reload Audio");
            structureSource = CreateStructureSource();
        }

        public void SetRifleTriggerHeld(bool held)
        {
            rifleTriggerHeld = held;
        }

        public void PlayRifleShot(Vector3 muzzlePosition, bool ownship)
        {
            var now = Time.unscaledTime;
            var startsBurst =
                !rifleBurstActive ||
                now - lastRifleShotTime > RifleBurstQuietSeconds;
            rifleBurstActive = true;
            lastRifleShotTime = now;

            MoveExternalSources(muzzlePosition);
            if (startsBurst)
            {
                rifleTailSource.Stop();
            }
            if (startsBurst && rifleFirstCloseClip != null)
            {
                rifleTransientSource.PlayOneShot(
                    rifleFirstCloseClip,
                    rifleFirstVolume);
            }

            if (rifleShotClip != null)
            {
                rifleShotSource.PlayOneShot(
                    rifleShotClip,
                    rifleShotVolume);
            }
            if (rifleLowClip != null)
            {
                rifleLowSource.PlayOneShot(
                    rifleLowClip,
                    rifleLowVolume);
            }
            if (ownship && rifleInteriorClip != null)
            {
                structureSource.PlayOneShot(
                    rifleInteriorClip,
                    rifleInteriorVolume);
            }
        }

        public void PlayCannonShot(Vector3 muzzlePosition, bool ownship)
        {
            cannonSource.transform.position = muzzlePosition;
            shellExternalSource.transform.position = muzzlePosition;
            if (cannonShotClip != null)
            {
                cannonSource.PlayOneShot(
                    cannonShotClip,
                    cannonShotVolume);
            }

            if (cannonShellClip != null)
            {
                StartCoroutine(PlayCannonShellAfterDelay(ownship));
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
            if (rifleStopTailClip != null)
            {
                rifleTailSource.PlayOneShot(
                    rifleStopTailClip,
                    rifleStopTailVolume);
            }
        }

        private IEnumerator PlayCannonShellAfterDelay(bool ownship)
        {
            yield return new WaitForSecondsRealtime(cannonShellDelaySeconds);
            var source = ownship ? structureSource : shellExternalSource;
            source.PlayOneShot(cannonShellClip, cannonShellVolume);
        }

        private IEnumerator PlayCannonReloadSequence(
            Vector3 weaponPosition,
            bool ownship)
        {
            PlayReloadClip(
                cannonAutoloaderTurnClip,
                cannonAutoloaderVolume,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(0.65f);
            PlayReloadClip(
                cannonAutoloaderServoClip,
                cannonAutoloaderServoVolume,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.6f);
            PlayReloadClip(
                cannonAutoloaderShellPushClip,
                cannonAutoloaderVolume * 1.08f,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.49f);
            PlayReloadClip(
                cannonAutoloaderEndTurnClip,
                cannonAutoloaderEndTurnVolume,
                weaponPosition,
                ownship);
        }

        private IEnumerator PlayRifleReloadSequence(
            Vector3 weaponPosition,
            bool ownship)
        {
            PlayReloadClip(
                rifleMagazineOutClip,
                rifleReloadVolume,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(0.85f);
            PlayReloadClip(
                rifleMagazineMoveClip,
                rifleReloadVolume * 0.9f,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.8f);
            PlayReloadClip(
                rifleMagazineInClip,
                rifleReloadVolume * 1.08f,
                weaponPosition,
                ownship);
            yield return new WaitForSeconds(1.1f);
            PlayReloadClip(
                rifleBoltClip,
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
            source.PlayOneShot(clip, volume);
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            rifleTransientSource?.Stop();
            rifleShotSource?.Stop();
            rifleLowSource?.Stop();
            rifleTailSource?.Stop();
            cannonSource?.Stop();
            shellExternalSource?.Stop();
            reloadExternalSource?.Stop();
            structureSource?.Stop();
            rifleBurstActive = false;
            rifleTriggerHeld = false;
        }

        private void LoadDefaultClips()
        {
            rifleFirstCloseClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/2a42_first_close");
            rifleShotClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/2a42_close");
            rifleLowClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/2a42_low_close");
            rifleInteriorClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/2a42_first_interior");
            rifleStopTailClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/2a72_last_close");
            cannonShotClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/cannon_57mm_qf6_shot");
            cannonShellClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/cannon_shell_drop");
            cannonAutoloaderTurnClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/cannon_autoloader_turn_noise");
            cannonAutoloaderServoClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/cannon_autoloader_servo");
            cannonAutoloaderShellPushClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/cannon_autoloader_shell_push");
            cannonAutoloaderEndTurnClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/cannon_autoloader_end_turn");
            rifleMagazineOutClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/mag_out");
            rifleMagazineMoveClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/mag_move");
            rifleMagazineInClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/mag_in");
            rifleBoltClip ??= Resources.Load<AudioClip>(
                "Audio/Weapons/Reload/bolt");
        }

        private void PreloadClips()
        {
            rifleFirstCloseClip?.LoadAudioData();
            rifleShotClip?.LoadAudioData();
            rifleLowClip?.LoadAudioData();
            rifleInteriorClip?.LoadAudioData();
            rifleStopTailClip?.LoadAudioData();
            cannonShotClip?.LoadAudioData();
            cannonShellClip?.LoadAudioData();
            cannonAutoloaderTurnClip?.LoadAudioData();
            cannonAutoloaderServoClip?.LoadAudioData();
            cannonAutoloaderShellPushClip?.LoadAudioData();
            cannonAutoloaderEndTurnClip?.LoadAudioData();
            rifleMagazineOutClip?.LoadAudioData();
            rifleMagazineMoveClip?.LoadAudioData();
            rifleMagazineInClip?.LoadAudioData();
            rifleBoltClip?.LoadAudioData();
        }

        private AudioSource CreateExternalSource(string sourceName)
        {
            var holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
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
