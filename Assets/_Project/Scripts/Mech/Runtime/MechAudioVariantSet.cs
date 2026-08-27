using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    internal sealed class MechAudioVariantSet
    {
        private static readonly Dictionary<string, MechAudioVariantSet> SharedSets =
            new Dictionary<string, MechAudioVariantSet>();

        private readonly AudioClip[] clips;
        private int previousIndex = -1;

        private MechAudioVariantSet(AudioClip[] loadedClips)
        {
            clips = loadedClips;
            for (var i = 0; i < clips.Length; i++)
            {
                clips[i]?.LoadAudioData();
            }
        }

        public AudioClip First => clips.Length > 0 ? clips[0] : null;

        public AudioClip Next()
        {
            if (clips.Length == 0)
            {
                return null;
            }

            if (clips.Length == 1)
            {
                previousIndex = 0;
                return clips[0];
            }

            int index;
            if (previousIndex < 0 || previousIndex >= clips.Length)
            {
                index = Random.Range(0, clips.Length);
            }
            else
            {
                index = Random.Range(0, clips.Length - 1);
                if (index >= previousIndex)
                {
                    index++;
                }
            }

            previousIndex = index;
            return clips[index];
        }

        public static MechAudioVariantSet Load(
            string resourceRoot,
            string firstResourceName,
            string numberedResourcePrefix,
            int count,
            AudioClip firstOverride = null)
        {
            var cacheKey =
                resourceRoot + "|" + firstResourceName + "|" +
                numberedResourcePrefix + "|" + count;
            if (firstOverride == null &&
                SharedSets.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var loaded = new List<AudioClip>(count);
            var first = firstOverride != null
                ? firstOverride
                : Resources.Load<AudioClip>(resourceRoot + firstResourceName);
            if (first != null)
            {
                loaded.Add(first);
            }

            for (var index = 2; index <= count; index++)
            {
                var clip = Resources.Load<AudioClip>(
                    resourceRoot +
                    numberedResourcePrefix +
                    index.ToString("000"));
                if (clip != null)
                {
                    loaded.Add(clip);
                }
            }

            var result = new MechAudioVariantSet(loaded.ToArray());
            if (firstOverride == null)
            {
                SharedSets[cacheKey] = result;
            }
            return result;
        }

        public static MechAudioVariantSet LoadNumbered(
            string resourceRoot,
            string numberedResourcePrefix,
            int firstIndex,
            int count)
        {
            var safeFirstIndex = Mathf.Max(0, firstIndex);
            var safeCount = Mathf.Max(0, count);
            var cacheKey =
                resourceRoot + "|" + numberedResourcePrefix + "|" +
                safeFirstIndex + "|" + safeCount;
            if (SharedSets.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var loaded = new List<AudioClip>(safeCount);
            for (var offset = 0; offset < safeCount; offset++)
            {
                var clip = Resources.Load<AudioClip>(
                    resourceRoot +
                    numberedResourcePrefix +
                    (safeFirstIndex + offset).ToString("000"));
                if (clip != null)
                {
                    loaded.Add(clip);
                }
            }

            var result = new MechAudioVariantSet(loaded.ToArray());
            SharedSets[cacheKey] = result;
            return result;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSharedSets()
        {
            SharedSets.Clear();
        }
    }
}
