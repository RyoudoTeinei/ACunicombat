using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ACunicombat.Mechs
{
    public readonly struct MechSynchronizedAnimationBinding
    {
        public MechSynchronizedAnimationBinding(
            MechPartSlot slot,
            GameObject partRoot,
            AnimationClip clip,
            MechPartSlot ancestorDriverSlot,
            IReadOnlyList<string> ancestorDrivenBoneNames)
        {
            Slot = slot;
            PartRoot = partRoot;
            Clip = clip;
            AncestorDriverSlot = ancestorDriverSlot;
            AncestorDrivenBoneNames = ancestorDrivenBoneNames;
        }

        public MechPartSlot Slot { get; }
        public GameObject PartRoot { get; }
        public AnimationClip Clip { get; }
        public MechPartSlot AncestorDriverSlot { get; }
        public IReadOnlyList<string> AncestorDrivenBoneNames { get; }
    }

    [DisallowMultipleComponent]
    public sealed class MechSynchronizedAnimationPlayer : MonoBehaviour
    {
        [SerializeField] private AnimationClip clip;
        [SerializeField] private bool playAutomatically = true;
        [SerializeField] private bool loop = true;
        [SerializeField] private float playbackSpeed = 1f;
        [SerializeField] private float startTime;

        private readonly List<Animator> partAnimators = new List<Animator>();
        private readonly List<AnimationClip> partClips = new List<AnimationClip>();
        private readonly List<MechPartSlot> partSlots = new List<MechPartSlot>();
        private readonly List<MechPartSlot> partAncestorDriverSlots = new List<MechPartSlot>();
        private readonly List<IReadOnlyList<string>> partAncestorDrivenBoneNames = new List<IReadOnlyList<string>>();
        private readonly List<AnimationClipPlayable> clipPlayables = new List<AnimationClipPlayable>();
        private readonly List<SharedBoneBinding> sharedBoneBindings = new List<SharedBoneBinding>();
        private PlayableGraph graph;
        private double playbackTime;

        public AnimationClip Clip => clip;
        public IReadOnlyList<Animator> PartAnimators => partAnimators;
        public IReadOnlyList<AnimationClip> PartClips => partClips;
        public IReadOnlyList<MechPartSlot> PartSlots => partSlots;
        public int SharedBoneBindingCount => sharedBoneBindings.Count;
        public double PlaybackTime => playbackTime;
        public bool IsGraphValid => graph.IsValid();

        public void Configure(
            AnimationClip animationClip,
            IEnumerable<GameObject> skinnedPartRoots,
            bool shouldPlayAutomatically,
            bool shouldLoop,
            float speed,
            float initialTime = 0f)
        {
            var bindings = new List<MechSynchronizedAnimationBinding>();
            if (skinnedPartRoots != null)
            {
                foreach (var partRoot in skinnedPartRoots)
                {
                    var assembledPart = partRoot == null ? null : partRoot.GetComponent<MechAssembledPart>();
                    var slot = assembledPart == null ? MechPartSlot.None : assembledPart.Slot;
                    var definition = assembledPart == null ? null : assembledPart.Definition;
                    var driverSlot = definition == null ? MechPartSlot.None : definition.AncestorDriverSlot;
                    var drivenBones = definition == null ? null : definition.AncestorDrivenBoneNames;
                    bindings.Add(new MechSynchronizedAnimationBinding(
                        slot,
                        partRoot,
                        animationClip,
                        driverSlot,
                        drivenBones));
                }
            }

            Configure(bindings, shouldPlayAutomatically, shouldLoop, speed, initialTime);
        }

        public void Configure(
            IEnumerable<MechSynchronizedAnimationBinding> bindings,
            bool shouldPlayAutomatically,
            bool shouldLoop,
            float speed,
            float initialTime = 0f)
        {
            StopPlayback();

            playAutomatically = shouldPlayAutomatically;
            loop = shouldLoop;
            playbackSpeed = speed;
            startTime = Mathf.Max(0f, initialTime);
            partAnimators.Clear();
            partClips.Clear();
            partSlots.Clear();
            partAncestorDriverSlots.Clear();
            partAncestorDrivenBoneNames.Clear();
            sharedBoneBindings.Clear();

            if (bindings != null)
            {
                foreach (var binding in bindings)
                {
                    var partRoot = binding.PartRoot;
                    if (partRoot == null || partRoot.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                    {
                        continue;
                    }

                    if (binding.Clip == null)
                    {
                        continue;
                    }

                    var animator = partRoot.GetComponent<Animator>();
                    if (animator == null)
                    {
                        animator = partRoot.AddComponent<Animator>();
                    }

                    animator.runtimeAnimatorController = null;
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.updateMode = AnimatorUpdateMode.Normal;
                    partAnimators.Add(animator);
                    partClips.Add(binding.Clip);
                    partSlots.Add(binding.Slot);
                    partAncestorDriverSlots.Add(binding.AncestorDriverSlot);
                    partAncestorDrivenBoneNames.Add(binding.AncestorDrivenBoneNames);
                }
            }

            BuildSharedBoneBindings();
            clip = partClips.Count > 0 ? partClips[0] : null;
            playbackTime = NormalizeTime(startTime);
            if (clip == null || partAnimators.Count == 0)
            {
                return;
            }

            if (Application.isPlaying && isActiveAndEnabled && playAutomatically)
            {
                BuildGraph();
            }
            else
            {
                SampleAtTime((float)playbackTime);
            }
        }

        public void Play()
        {
            playAutomatically = true;
            if (!Application.isPlaying || graph.IsValid() || clip == null || partAnimators.Count == 0)
            {
                return;
            }

            BuildGraph();
        }

        public void StopPlayback()
        {
            if (graph.IsValid())
            {
                graph.Destroy();
            }

            clipPlayables.Clear();
        }

        public void SampleAtTime(float timeSeconds)
        {
            if (clip == null)
            {
                return;
            }

            playbackTime = NormalizeTime(timeSeconds);
            for (var i = 0; i < partAnimators.Count; i++)
            {
                var animator = partAnimators[i];
                var partClip = i < partClips.Count ? partClips[i] : null;
                if (animator != null && partClip != null)
                {
                    partClip.SampleAnimation(animator.gameObject, (float)NormalizeTime(playbackTime, partClip));
                }
            }

            SynchronizeSharedBones();
        }

        public void EvaluatePlayableGraphAtTime(float timeSeconds)
        {
            if (clip == null || partAnimators.Count == 0)
            {
                return;
            }

            if (!graph.IsValid())
            {
                BuildGraph();
            }

            playbackTime = NormalizeTime(timeSeconds);
            ApplyPlayableTime();
            graph.Evaluate(0f);
            SynchronizeSharedBones();
        }

        public void GetSharedBonePoseErrors(
            out float maxLocalPositionError,
            out float maxLocalRotationErrorDegrees,
            out float maxLocalScaleError)
        {
            maxLocalPositionError = 0f;
            maxLocalRotationErrorDegrees = 0f;
            maxLocalScaleError = 0f;

            for (var i = 0; i < sharedBoneBindings.Count; i++)
            {
                var binding = sharedBoneBindings[i];
                if (binding.Driver == null || binding.Follower == null)
                {
                    continue;
                }

                maxLocalPositionError = Mathf.Max(
                    maxLocalPositionError,
                    Vector3.Distance(binding.Driver.position, binding.Follower.position));
                maxLocalRotationErrorDegrees = Mathf.Max(
                    maxLocalRotationErrorDegrees,
                    Quaternion.Angle(binding.Driver.rotation, binding.Follower.rotation));
                maxLocalScaleError = Mathf.Max(
                    maxLocalScaleError,
                    Vector3.Distance(binding.Driver.lossyScale, binding.Follower.lossyScale));
            }
        }

        private void OnEnable()
        {
            if (Application.isPlaying && playAutomatically && !graph.IsValid() && clip != null && partAnimators.Count > 0)
            {
                BuildGraph();
            }
        }

        private void OnDisable()
        {
            StopPlayback();
        }

        private void OnDestroy()
        {
            StopPlayback();
        }

        private void Update()
        {
            if (!graph.IsValid() || clip == null)
            {
                return;
            }

            playbackTime = NormalizeTime(playbackTime + Time.deltaTime * playbackSpeed);
            ApplyPlayableTime();
            graph.Evaluate(0f);
            SynchronizeSharedBones();
        }

        private void BuildGraph()
        {
            StopPlayback();

            graph = PlayableGraph.Create($"{name}_SynchronizedMechAnimation");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            for (var i = 0; i < partAnimators.Count; i++)
            {
                var animator = partAnimators[i];
                if (animator == null)
                {
                    continue;
                }

                var partClip = i < partClips.Count ? partClips[i] : null;
                if (partClip == null)
                {
                    continue;
                }

                var playable = AnimationClipPlayable.Create(graph, partClip);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                clipPlayables.Add(playable);

                var output = AnimationPlayableOutput.Create(graph, $"Part_{i:00}", animator);
                output.SetSourcePlayable(playable);
            }

            ApplyPlayableTime();
            graph.Play();
            graph.Evaluate(0f);
            SynchronizeSharedBones();
        }

        private void BuildSharedBoneBindings()
        {
            sharedBoneBindings.Clear();
            var partIndicesBySlot = new Dictionary<MechPartSlot, int>();
            var boneMapsBySlot = new Dictionary<MechPartSlot, Dictionary<string, Transform>>();
            var driverSlotsByFollower = new Dictionary<MechPartSlot, MechPartSlot>();
            for (var partIndex = 0; partIndex < partAnimators.Count; partIndex++)
            {
                var slot = partIndex < partSlots.Count ? partSlots[partIndex] : MechPartSlot.None;
                var animator = partAnimators[partIndex];
                if (slot == MechPartSlot.None || animator == null)
                {
                    continue;
                }

                partIndicesBySlot[slot] = partIndex;
                boneMapsBySlot[slot] = BuildSkinnedBoneMap(animator.gameObject);
                driverSlotsByFollower[slot] = partIndex < partAncestorDriverSlots.Count
                    ? partAncestorDriverSlots[partIndex]
                    : MechPartSlot.None;
            }

            for (var partIndex = 0; partIndex < partAnimators.Count; partIndex++)
            {
                var followerSlot = partIndex < partSlots.Count ? partSlots[partIndex] : MechPartSlot.None;
                var driverSlot = partIndex < partAncestorDriverSlots.Count
                    ? partAncestorDriverSlots[partIndex]
                    : MechPartSlot.None;
                if (followerSlot == MechPartSlot.None || driverSlot == MechPartSlot.None ||
                    !partIndicesBySlot.ContainsKey(driverSlot) ||
                    !boneMapsBySlot.TryGetValue(driverSlot, out var driverBones) ||
                    !boneMapsBySlot.TryGetValue(followerSlot, out var followerBones))
                {
                    continue;
                }

                var requestedBoneNames = partIndex < partAncestorDrivenBoneNames.Count
                    ? partAncestorDrivenBoneNames[partIndex]
                    : null;
                if (requestedBoneNames == null)
                {
                    continue;
                }

                var addedNames = new HashSet<string>(System.StringComparer.Ordinal);
                for (var nameIndex = 0; nameIndex < requestedBoneNames.Count; nameIndex++)
                {
                    var boneName = requestedBoneNames[nameIndex];
                    if (string.IsNullOrWhiteSpace(boneName) || !addedNames.Add(boneName))
                    {
                        continue;
                    }

                    if (driverBones.TryGetValue(boneName, out var driver) &&
                        followerBones.TryGetValue(boneName, out var follower) &&
                        driver != null && follower != null)
                    {
                        var order = GetSynchronizationDepth(followerSlot, driverSlotsByFollower);
                        sharedBoneBindings.Add(new SharedBoneBinding(driver, follower, order));
                    }
                }
            }

            sharedBoneBindings.Sort((left, right) => left.Order.CompareTo(right.Order));
        }

        private static int GetSynchronizationDepth(
            MechPartSlot followerSlot,
            IReadOnlyDictionary<MechPartSlot, MechPartSlot> driverSlotsByFollower)
        {
            var depth = 0;
            var current = followerSlot;
            var visited = new HashSet<MechPartSlot>();
            while (driverSlotsByFollower.TryGetValue(current, out var driver) && driver != MechPartSlot.None)
            {
                if (!visited.Add(current))
                {
                    return int.MaxValue;
                }

                depth++;
                current = driver;
            }

            return depth;
        }

        private static Dictionary<string, Transform> BuildSkinnedBoneMap(GameObject partRoot)
        {
            var result = new Dictionary<string, Transform>(System.StringComparer.Ordinal);
            var renderers = partRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                AddBone(result, renderer.rootBone);
                var bones = renderer.bones;
                for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                {
                    AddBone(result, bones[boneIndex]);
                }
            }

            return result;
        }

        private static void AddBone(IDictionary<string, Transform> destination, Transform bone)
        {
            if (bone != null && !destination.ContainsKey(bone.name))
            {
                destination.Add(bone.name, bone);
            }
        }

        private void SynchronizeSharedBones()
        {
            for (var i = 0; i < sharedBoneBindings.Count; i++)
            {
                var binding = sharedBoneBindings[i];
                if (binding.Driver == null || binding.Follower == null)
                {
                    continue;
                }

                binding.Follower.localPosition = binding.Driver.localPosition;
                binding.Follower.localRotation = binding.Driver.localRotation;
                binding.Follower.localScale = binding.Driver.localScale;
            }
        }

        private void ApplyPlayableTime()
        {
            for (var i = 0; i < clipPlayables.Count; i++)
            {
                var partClip = i < partClips.Count ? partClips[i] : clip;
                clipPlayables[i].SetTime(NormalizeTime(playbackTime, partClip));
                clipPlayables[i].SetSpeed(0d);
            }
        }

        private double NormalizeTime(double timeSeconds)
            => NormalizeTime(timeSeconds, clip);

        private double NormalizeTime(double timeSeconds, AnimationClip targetClip)
        {
            if (targetClip == null || targetClip.length <= 0f)
            {
                return 0d;
            }

            if (loop)
            {
                var duration = targetClip.length;
                var wrapped = timeSeconds % duration;
                return wrapped < 0d ? wrapped + duration : wrapped;
            }

            return System.Math.Max(0d, System.Math.Min(targetClip.length, timeSeconds));
        }

        private readonly struct SharedBoneBinding
        {
            public SharedBoneBinding(Transform driver, Transform follower, int order)
            {
                Driver = driver;
                Follower = follower;
                Order = order;
            }

            public Transform Driver { get; }
            public Transform Follower { get; }
            public int Order { get; }
        }
    }
}
