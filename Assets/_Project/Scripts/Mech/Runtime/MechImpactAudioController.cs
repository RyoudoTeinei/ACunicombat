using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Plays local armor impact transients and, for the player mech, a second
    /// structure-borne layer. Ricochet audio is exposed separately so armor
    /// resolution can drive it later without guessing inside the audio system.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechImpactAudioController : MonoBehaviour
    {
        private const string AudioRoot = "Audio/Impacts/";
        private const string MissileImpactAudioRoot = "Audio/Impacts/Missile/";
        private const string FlakAudioRoot = "Audio/Impacts/Flak/";
        private AudioSource missileHitWarningSource;
        private int missileHitWarningRemaining;
        private double nextMissileHitWarningTime;
        private const float MissileHitWarningVolume = 0.65f;
        private const double MissileHitWarningGap = 0.1;
        private const int ExternalSourceCount = 4;
        private const int StructureSourceCount = 3;
        private const int FlakSourceCount = 16;

        [Header("Clips")]
        [SerializeField] private AudioClip bulletHitClip;
        [SerializeField] private AudioClip bulletInteriorClip;
        [SerializeField] private AudioClip bulletRicochetClip;
        [SerializeField] private AudioClip bulletIncomingClip;
        [SerializeField] private AudioClip heavyImpactExternalClip;
        [SerializeField] private AudioClip heavyImpactInteriorClip;
        [SerializeField] private AudioClip missileAttackTransientClip;
        [SerializeField] private AudioClip missileExplosionCloseClip;
        [SerializeField] private AudioClip missileMetalDebrisClip;
        [SerializeField] private AudioClip flakAirburstCloseClip;
        [SerializeField] private AudioClip flakAirburstFarClip;
        [SerializeField] private AudioClip flakDebrisClip;

        [Header("Levels")]
        [SerializeField, Range(0f, 1f)] private float bulletExternalVolume = 0.3f;
        [SerializeField, Range(0f, 1f)] private float bulletInteriorVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] private float ricochetVolume = 0.28f;
        [SerializeField, Range(0f, 1f)] private float incomingVolume = 0.24f;
        [SerializeField, Range(0f, 1f)] private float heavyExternalVolume = 0.44f;
        [SerializeField, Range(0f, 1f)] private float heavyInteriorVolume = 0.68f;
        [SerializeField, Range(0f, 1f)] private float missileAttackVolume = 0.48f;
        [SerializeField, Range(0f, 1f)] private float missileExplosionVolume = 0.7f;
        [SerializeField, Range(0f, 1f)] private float missileDebrisVolume = 0.34f;
        [SerializeField, Range(0f, 1f)] private float missileInteriorVolume = 0.72f;
        [SerializeField, Range(0f, 1f)] private float flakCloseVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] private float flakFarVolume = 0.34f;
        [SerializeField, Range(0f, 1f)] private float flakDebrisVolume = 0.3f;
        [SerializeField, Min(0f)] private float heavyImpactDamageThreshold = 250f;

        [Header("External attenuation")]
        [SerializeField, Min(0.1f)] private float minimumDistanceMeters = 6f;
        [SerializeField, Min(1f)] private float maximumDistanceMeters = 220f;
        [SerializeField, Min(0f)] private float externalRetriggerSeconds = 0.025f;
        [SerializeField, Min(0f)] private float interiorRetriggerSeconds = 0.04f;
        [SerializeField, Min(0f)] private float missileImpactRetriggerSeconds = 0.04f;

        [Header("Flak attenuation")]
        [SerializeField, Min(1f)] private float flakCloseDistanceMeters = 180f;
        [SerializeField, Min(1f)] private float flakMaximumDistanceMeters = 1800f;
        [SerializeField, Min(0f)] private float flakRetriggerSeconds = 0.025f;

        private readonly float[] pitchSequence = { 0.97f, 1.015f, 0.99f, 1.035f };
        private AudioSource[] externalSources;
        private AudioSource[] structureSources;
        private AudioSource[] flakSources;
        private int nextExternalSource;
        private int nextStructureSource;
        private int nextFlakSource;
        private int pitchStep;
        private float nextExternalTime;
        private float nextInteriorTime;
        private float nextMissileImpactTime;
        private float nextFlakTime;
        private MechAudioVariantSet bulletHitVariants;
        private MechAudioVariantSet bulletInteriorVariants;
        private MechAudioVariantSet bulletRicochetVariants;
        private MechAudioVariantSet bulletIncomingVariants;
        private MechAudioVariantSet heavyExternalVariants;
        private MechAudioVariantSet heavyInteriorVariants;
        private MechAudioVariantSet missileAttackVariants;
        private MechAudioVariantSet missileExplosionVariants;
        private MechAudioVariantSet missileDebrisVariants;
        private MechAudioVariantSet flakCloseVariants;
        private MechAudioVariantSet flakFarVariants;
        private MechAudioVariantSet flakDebrisVariants;

        private void Awake()
        {
            LoadVariantSets();
            var warningObject=new GameObject("Missile Hit Warning");warningObject.transform.SetParent(transform,false);
            missileHitWarningSource=warningObject.AddComponent<AudioSource>();
            missileHitWarningSource.clip=Resources.Load<AudioClip>("Audio/Warnings/critical_event_warning");
            missileHitWarningSource.playOnAwake=false;missileHitWarningSource.loop=false;
            missileHitWarningSource.spatialBlend=0;missileHitWarningSource.dopplerLevel=0;
            missileHitWarningSource.volume=MissileHitWarningVolume;missileHitWarningSource.priority=16;
            externalSources = CreateSourcePool(ExternalSourceCount, true);
            structureSources = CreateSourcePool(StructureSourceCount, false);
            flakSources = CreateFlakSourcePool();
        }

        public void PlayArmorImpact(
            TestWeaponKind weaponKind,
            float damage,
            Vector3 hitPoint,
            bool ownship)
        {
            if (weaponKind == TestWeaponKind.Missile)
            {
                if(ownship&&damage>0)BeginMissileHitWarning();
                PlayMissileImpact(hitPoint, ownship);
                return;
            }

            var heavy = damage >= heavyImpactDamageThreshold;
            var externalClip = heavy
                ? heavyExternalVariants.Next()
                : bulletHitVariants.Next();
            var interiorClip = heavy
                ? heavyInteriorVariants.Next()
                : bulletInteriorVariants.Next();
            var externalVolume = heavy ? heavyExternalVolume : bulletExternalVolume;
            var interiorVolume = heavy ? heavyInteriorVolume : bulletInteriorVolume;

            PlayExternal(externalClip, externalVolume, hitPoint);
            if (ownship)
            {
                PlayInterior(interiorClip, interiorVolume);
            }
        }

        public void PlayRicochet(Vector3 hitPoint)
        {
            PlayExternal(
                bulletRicochetVariants.Next(), ricochetVolume, hitPoint);
        }

        public void PlayIncoming(Vector3 closestPoint)
        {
            PlayExternal(
                bulletIncomingVariants.Next(), incomingVolume, closestPoint);
        }

        public void PlayFlakAirburst(
            Vector3 detonationPoint,
            bool fragmentsReached)
        {
            if (flakSources == null ||
                Time.unscaledTime < nextFlakTime)
            {
                return;
            }

            var distance = Vector3.Distance(
                transform.position, detonationPoint);
            if (distance > flakMaximumDistanceMeters)
            {
                return;
            }

            nextFlakTime = Time.unscaledTime + flakRetriggerSeconds;
            var eventPitch = NextPitch();
            var close = distance <= flakCloseDistanceMeters;
            PlayFlakLayer(
                close ? flakCloseVariants.Next() : flakFarVariants.Next(),
                close ? flakCloseVolume : flakFarVolume,
                detonationPoint,
                eventPitch);
            if (fragmentsReached)
            {
                PlayFlakLayer(
                    flakDebrisVariants.Next(), flakDebrisVolume,
                    detonationPoint, eventPitch);
            }
        }

        private void BeginMissileHitWarning()
        {
            if(missileHitWarningSource==null||missileHitWarningSource.clip==null)return;
            // Refresh a bounded three-pulse burst instead of stacking alarms during a salvo.
            missileHitWarningSource.Stop();missileHitWarningRemaining=3;
            nextMissileHitWarningTime=Time.unscaledTimeAsDouble;
            TickMissileHitWarning(Time.unscaledTimeAsDouble);
        }
        private void Update()=>TickMissileHitWarning(Time.unscaledTimeAsDouble);
        private void TickMissileHitWarning(double now)
        {
            if(missileHitWarningRemaining<=0||missileHitWarningSource==null||missileHitWarningSource.clip==null||now<nextMissileHitWarningTime)return;
            missileHitWarningSource.Play();missileHitWarningRemaining--;
            nextMissileHitWarningTime=now+missileHitWarningSource.clip.length+MissileHitWarningGap;
        }

        private void PlayMissileImpact(Vector3 hitPoint, bool ownship)
        {
            if (externalSources == null ||
                Time.unscaledTime < nextMissileImpactTime)
            {
                return;
            }

            nextMissileImpactTime =
                Time.unscaledTime + missileImpactRetriggerSeconds;
            var eventPitch = NextPitch();
            PlayExternalLayer(
                missileAttackVariants.Next(), missileAttackVolume,
                hitPoint, eventPitch);
            PlayExternalLayer(
                missileExplosionVariants.Next(), missileExplosionVolume,
                hitPoint, eventPitch);
            PlayExternalLayer(
                missileDebrisVariants.Next(), missileDebrisVolume,
                hitPoint, eventPitch);
            if (ownship)
            {
                PlayInteriorLayer(
                    bulletInteriorVariants.Next(), missileInteriorVolume,
                    eventPitch);
            }
        }

        private void PlayExternal(AudioClip clip, float volume, Vector3 hitPoint)
        {
            if (clip == null || externalSources == null ||
                Time.unscaledTime < nextExternalTime)
            {
                return;
            }

            nextExternalTime = Time.unscaledTime + externalRetriggerSeconds;
            PlayExternalLayer(clip, volume, hitPoint);
        }

        private void PlayExternalLayer(
            AudioClip clip,
            float volume,
            Vector3 hitPoint,
            float pitch = -1f)
        {
            if (clip == null || externalSources == null)
            {
                return;
            }

            var source = externalSources[nextExternalSource];
            nextExternalSource = (nextExternalSource + 1) % externalSources.Length;
            source.transform.position = hitPoint;
            Play(source, clip, volume, pitch);
        }

        private void PlayInterior(AudioClip clip, float volume)
        {
            if (clip == null || structureSources == null ||
                Time.unscaledTime < nextInteriorTime)
            {
                return;
            }

            nextInteriorTime = Time.unscaledTime + interiorRetriggerSeconds;
            PlayInteriorLayer(clip, volume);
        }

        private void PlayInteriorLayer(
            AudioClip clip,
            float volume,
            float pitch = -1f)
        {
            if (clip == null || structureSources == null)
            {
                return;
            }

            var source = structureSources[nextStructureSource];
            nextStructureSource = (nextStructureSource + 1) % structureSources.Length;
            Play(source, clip, volume, pitch);
        }

        private void PlayFlakLayer(
            AudioClip clip,
            float volume,
            Vector3 detonationPoint,
            float pitch)
        {
            if (clip == null || flakSources == null)
            {
                return;
            }

            var source = flakSources[nextFlakSource];
            nextFlakSource = (nextFlakSource + 1) % flakSources.Length;
            source.transform.position = detonationPoint;
            Play(source, clip, volume, pitch);
        }

        private void Play(
            AudioSource source,
            AudioClip clip,
            float volume,
            float pitch = -1f)
        {
            source.Stop();
            source.clip = clip;
            source.volume = Mathf.Clamp01(volume);
            source.pitch = pitch > 0f ? pitch : NextPitch();
            source.Play();
        }

        private float NextPitch()
        {
            var pitch = pitchSequence[pitchStep];
            pitchStep = (pitchStep + 1) % pitchSequence.Length;
            return pitch;
        }

        private AudioSource[] CreateSourcePool(int count, bool spatial)
        {
            var sources = new AudioSource[count];
            for (var i = 0; i < count; i++)
            {
                var holder = new GameObject(
                    spatial ? $"Armor Impact External {i + 1}" : $"Armor Impact Interior {i + 1}");
                holder.transform.SetParent(transform, false);
                var source = holder.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = spatial ? 1f : 0f;
                source.dopplerLevel = 0f;
                source.priority = spatial ? 112 : 64;
                if (spatial)
                {
                    source.rolloffMode = AudioRolloffMode.Custom;
                    source.minDistance = minimumDistanceMeters;
                    source.maxDistance = maximumDistanceMeters;
                    source.SetCustomCurve(
                        AudioSourceCurveType.CustomRolloff,
                        CreateExternalRolloffCurve());
                }
                sources[i] = source;
            }
            return sources;
        }

        private AudioSource[] CreateFlakSourcePool()
        {
            var sources = new AudioSource[FlakSourceCount];
            for (var i = 0; i < sources.Length; i++)
            {
                var holder = new GameObject($"Flak Airburst {i + 1}");
                holder.transform.SetParent(transform, false);
                var source = holder.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;
                source.spatialBlend = 1f;
                source.dopplerLevel = 0f;
                source.priority = 96;
                source.rolloffMode = AudioRolloffMode.Custom;
                source.minDistance = minimumDistanceMeters;
                source.maxDistance = flakMaximumDistanceMeters;
                source.SetCustomCurve(
                    AudioSourceCurveType.CustomRolloff,
                    CreateFlakRolloffCurve());
                sources[i] = source;
            }
            return sources;
        }

        private void LoadVariantSets()
        {
            bulletHitVariants = MechAudioVariantSet.Load(
                AudioRoot, "bullet_hit_001", "bullet_hit_", 16, bulletHitClip);
            bulletInteriorVariants = MechAudioVariantSet.Load(
                AudioRoot, "bullet_interior_001", "bullet_interior_", 12,
                bulletInteriorClip);
            bulletRicochetVariants = MechAudioVariantSet.Load(
                AudioRoot, "bullet_rico_001", "bullet_rico_", 16,
                bulletRicochetClip);
            bulletIncomingVariants = MechAudioVariantSet.Load(
                AudioRoot, "bullet_incoming_001", "bullet_incoming_", 16,
                bulletIncomingClip);
            heavyExternalVariants = MechAudioVariantSet.Load(
                AudioRoot, "tank_bullet_impact_ext_001",
                "tank_bullet_impact_ext_", 6, heavyImpactExternalClip);
            heavyInteriorVariants = MechAudioVariantSet.Load(
                AudioRoot, "tank_bullet_impact_int_001",
                "tank_bullet_impact_int_", 6, heavyImpactInteriorClip);
            missileAttackVariants = MechAudioVariantSet.Load(
                MissileImpactAudioRoot, "slow_attack_mid_001",
                "slow_attack_mid_", 5, missileAttackTransientClip);
            missileExplosionVariants = MechAudioVariantSet.Load(
                MissileImpactAudioRoot, "explosion_air_close_001",
                "explosion_air_close_", 10, missileExplosionCloseClip);
            missileDebrisVariants = MechAudioVariantSet.Load(
                MissileImpactAudioRoot, "metal_debris_001",
                "metal_debris_", 35, missileMetalDebrisClip);
            flakCloseVariants = MechAudioVariantSet.Load(
                FlakAudioRoot, "flak_airburst_close_001",
                "flak_airburst_close_", 9, flakAirburstCloseClip);
            flakFarVariants = MechAudioVariantSet.Load(
                FlakAudioRoot, "flak_airburst_far_001",
                "flak_airburst_far_", 6, flakAirburstFarClip);
            flakDebrisVariants = MechAudioVariantSet.Load(
                FlakAudioRoot, "flak_debris_001",
                "flak_debris_", 3, flakDebrisClip);
        }

        private static AnimationCurve CreateExternalRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.22f, 0.76f),
                new Keyframe(0.62f, 0.2f),
                new Keyframe(1f, 0f));
        }

        private static AnimationCurve CreateFlakRolloffCurve()
        {
            return new AnimationCurve(
                new Keyframe(0f, 1f),
                new Keyframe(0.12f, 0.86f),
                new Keyframe(0.48f, 0.36f),
                new Keyframe(1f, 0f));
        }

        private void OnDisable()
        {
            missileHitWarningRemaining=0;if(missileHitWarningSource!=null)missileHitWarningSource.Stop();
            StopAll(externalSources);
            StopAll(structureSources);
            StopAll(flakSources);
        }

        private static void StopAll(AudioSource[] sources)
        {
            if (sources == null)
            {
                return;
            }

            for (var i = 0; i < sources.Length; i++)
            {
                sources[i]?.Stop();
            }
        }
    }
}
