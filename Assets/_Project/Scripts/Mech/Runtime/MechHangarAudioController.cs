using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechHangarAudioController : MonoBehaviour
    {
        private const string AudioRoot = "Audio/Hangar/";
        private const float FadeInSeconds = 0.8f;
        private const float FadeOutSeconds = 0.35f;
        private const float GameplayAudioVolume = 0.18f;
        private const float GameplayLowPassCutoffHz = 1250f;

        private AudioSource warfareLowSource;
        private AudioSource warfareHighSource;
        private AudioSource distantExplosionSource;
        private readonly List<DuckedSource> duckedSources = new List<DuckedSource>();
        private AudioLowPassFilter listenerLowPass;
        private bool active;
        private bool ownsAudioState;
        private bool ownsListenerLowPass;
        private bool previousLowPassEnabled;
        private float previousLowPassCutoff = 22000f;
        private float previousLowPassResonance = 1f;
        private float blend;

        private struct DuckedSource
        {
            public AudioSource source;
            public float volume;
        }

        private void Awake()
        {
            warfareLowSource = CreateSource(
                "Hangar Warfare Low",
                Resources.Load<AudioClip>(AudioRoot + "warfare_amb_guns_low"));
            warfareHighSource = CreateSource(
                "Hangar Warfare High",
                Resources.Load<AudioClip>(AudioRoot + "warfare_amb_guns_high"));
            distantExplosionSource = CreateSource(
                "Hangar Distant Explosions",
                Resources.Load<AudioClip>(AudioRoot + "aa_explosion_take3_Stereo_far"));
        }

        private void LateUpdate()
        {
            float duration = active ? FadeInSeconds : FadeOutSeconds;
            float target = active ? 1f : 0f;
            blend = Mathf.MoveTowards(
                blend,
                target,
                Time.unscaledDeltaTime / Mathf.Max(0.01f, duration));

            UpdateMix();
            if (!active && blend <= 0.001f)
            {
                StopAmbience();
                RestoreGameplayAudio();
            }
        }

        private void OnDisable()
        {
            StopAmbience();
            RestoreGameplayAudio();
        }

        private void OnDestroy()
        {
            StopAmbience();
            RestoreGameplayAudio();
        }

        public void SetHangarActive(bool value)
        {
            if (active == value && (value == ownsAudioState))
            {
                return;
            }

            active = value;
            if (active)
            {
                AcquireGameplayAudio();
                StartAmbience();
            }
        }

        private void AcquireGameplayAudio()
        {
            if (!ownsAudioState)
            {
                CaptureGameplaySources();
                CaptureListenerLowPass();
                ownsAudioState = true;
            }
        }

        private void CaptureGameplaySources()
        {
            duckedSources.Clear();
            var sources = FindObjectsByType<AudioSource>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null || IsHangarSource(source))
                {
                    continue;
                }

                duckedSources.Add(new DuckedSource
                {
                    source = source,
                    volume = source.volume
                });
            }
        }

        private void CaptureListenerLowPass()
        {
            var listener = FindAnyObjectByType<AudioListener>();
            if (listener == null)
            {
                return;
            }

            listenerLowPass = listener.GetComponent<AudioLowPassFilter>();
            if (listenerLowPass == null)
            {
                listenerLowPass = listener.gameObject.AddComponent<AudioLowPassFilter>();
                ownsListenerLowPass = true;
                previousLowPassEnabled = false;
                previousLowPassCutoff = 22000f;
                previousLowPassResonance = 1f;
                return;
            }

            ownsListenerLowPass = false;
            previousLowPassEnabled = listenerLowPass.enabled;
            previousLowPassCutoff = listenerLowPass.cutoffFrequency;
            previousLowPassResonance = listenerLowPass.lowpassResonanceQ;
        }

        private void UpdateMix()
        {
            if (!ownsAudioState)
            {
                return;
            }

            float gameplayScale = Mathf.Lerp(1f, GameplayAudioVolume, blend);
            for (int i = 0; i < duckedSources.Count; i++)
            {
                DuckedSource state = duckedSources[i];
                if (state.source != null)
                {
                    state.source.volume = state.volume * gameplayScale;
                }
            }

            if (listenerLowPass != null)
            {
                listenerLowPass.enabled = true;
                listenerLowPass.cutoffFrequency = Mathf.Lerp(
                    previousLowPassEnabled ? previousLowPassCutoff : 22000f,
                    GameplayLowPassCutoffHz,
                    blend);
                listenerLowPass.lowpassResonanceQ = Mathf.Lerp(
                    previousLowPassResonance,
                    1.15f,
                    blend);
            }

            SetVolume(warfareLowSource, 0.62f * blend);
            SetVolume(warfareHighSource, 0.38f * blend);
            SetVolume(distantExplosionSource, 0.26f * blend);
        }

        private void RestoreGameplayAudio()
        {
            if (!ownsAudioState)
            {
                return;
            }

            for (int i = 0; i < duckedSources.Count; i++)
            {
                DuckedSource state = duckedSources[i];
                if (state.source != null)
                {
                    state.source.volume = state.volume;
                }
            }

            duckedSources.Clear();
            if (listenerLowPass != null)
            {
                if (ownsListenerLowPass)
                {
                    listenerLowPass.enabled = false;
                    Destroy(listenerLowPass);
                }
                else
                {
                    listenerLowPass.enabled = previousLowPassEnabled;
                    listenerLowPass.cutoffFrequency = previousLowPassCutoff;
                    listenerLowPass.lowpassResonanceQ = previousLowPassResonance;
                }
            }

            listenerLowPass = null;
            ownsListenerLowPass = false;
            ownsAudioState = false;
        }

        private bool IsHangarSource(AudioSource source)
        {
            return source == warfareLowSource ||
                   source == warfareHighSource ||
                   source == distantExplosionSource;
        }

        private void StartAmbience()
        {
            float warfarePhase = NextPhase(0.173f);
            StartAtNormalizedPhase(warfareLowSource, warfarePhase);
            StartAtNormalizedPhase(warfareHighSource, warfarePhase);
            StartAtNormalizedPhase(distantExplosionSource, NextPhase(0.317f));
        }

        private static float NextPhase(float multiplier)
        {
            return Mathf.Repeat((float)AudioSettings.dspTime * multiplier, 1f);
        }

        private static void StartAtNormalizedPhase(
            AudioSource source,
            float normalizedPhase)
        {
            if (source == null || source.clip == null || source.isPlaying)
            {
                return;
            }

            source.time = Mathf.Clamp01(normalizedPhase) *
                          Mathf.Max(0f, source.clip.length - 0.1f);
            source.Play();
        }

        private void StopAmbience()
        {
            Stop(warfareLowSource);
            Stop(warfareHighSource);
            Stop(distantExplosionSource);
        }

        private AudioSource CreateSource(string sourceName, AudioClip clip)
        {
            if (clip == null)
            {
                Debug.LogWarning($"[MechHangarAudio] Missing clip: {sourceName}", this);
                return null;
            }

            clip.LoadAudioData();
            var holder = new GameObject(sourceName);
            holder.transform.SetParent(transform, false);
            var source = holder.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.bypassListenerEffects = true;
            source.volume = 0f;
            source.priority = 32;
            source.clip = clip;
            return source;
        }

        private static void SetVolume(AudioSource source, float volume)
        {
            if (source != null)
            {
                source.volume = Mathf.Clamp01(volume);
            }
        }

        private static void Stop(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            source.volume = 0f;
        }
    }
}
