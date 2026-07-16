using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ACunicombat.Mechs
{
    public readonly struct MechBodyActionPart
    {
        public MechBodyActionPart(
            MechPartSlot slot,
            GameObject partRoot,
            MechPartSlot ancestorDriverSlot,
            IReadOnlyList<string> ancestorDrivenBoneNames)
        {
            Slot = slot;
            PartRoot = partRoot;
            AncestorDriverSlot = ancestorDriverSlot;
            AncestorDrivenBoneNames = ancestorDrivenBoneNames;
        }

        public MechPartSlot Slot { get; }
        public GameObject PartRoot { get; }
        public MechPartSlot AncestorDriverSlot { get; }
        public IReadOnlyList<string> AncestorDrivenBoneNames { get; }
    }

    /// <summary>
    /// Registry-driven successor to <see cref="MechSynchronizedAnimationPlayer"/> for frame
    /// (body) actions. Each frame part keeps its own Animator and rig; this player gives every
    /// part a two-layer graph — layer 0 crossfades full-body actions between two slots, layer 1
    /// overrides the masked upper-body chains for raise/aim/fire — and steps all parts on one
    /// manual clock so equipment HKX players sharing that clock stay in sync. Interface bones
    /// between parts are re-synchronized after every evaluate, mirroring the contract of
    /// MechSynchronizedAnimationPlayer (whose single-clip pipeline this supersedes; the shared
    /// bone logic is intentionally kept behavior-identical until the old player retires and the
    /// helper can be extracted).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MechBodyActionPlayer : MonoBehaviour
    {
        private const int LayerCount = 2;
        private const int SlotCount = 2;

        [SerializeField] private MechActionRegistry registry;
        [SerializeField] private float playbackSpeed = 1f;
        [SerializeField] private List<string> upperBodyBoneNames = new List<string>();

        private readonly List<PartState> parts = new List<PartState>();
        private readonly List<SharedBoneBinding> sharedBoneBindings = new List<SharedBoneBinding>();
        private readonly LayerState[] layers = { new LayerState(), new LayerState() };
        private PlayableGraph graph;
        private double clock;

        public MechActionRegistry Registry => registry;
        public bool IsGraphValid => graph.IsValid();
        public double GraphTime => clock;
        public int SharedBoneBindingCount => sharedBoneBindings.Count;
        public string ActiveFullBodyActionId => layers[0].Active?.actionId ?? string.Empty;
        public string ActiveUpperBodyActionId => layers[1].Active?.actionId ?? string.Empty;

        public void Configure(
            MechActionRegistry actionRegistry,
            IEnumerable<MechBodyActionPart> bodyParts,
            IEnumerable<string> maskedUpperBodyBoneNames,
            string initialFullBodyActionId = null,
            float speed = 1f)
        {
            DestroyGraph();
            registry = actionRegistry;
            playbackSpeed = speed;
            clock = 0d;
            layers[0].Reset();
            layers[1].Reset();

            upperBodyBoneNames.Clear();
            if (maskedUpperBodyBoneNames != null)
            {
                upperBodyBoneNames.AddRange(maskedUpperBodyBoneNames);
            }

            parts.Clear();
            if (bodyParts != null)
            {
                foreach (var bodyPart in bodyParts)
                {
                    if (bodyPart.PartRoot == null ||
                        bodyPart.PartRoot.GetComponentInChildren<SkinnedMeshRenderer>(true) == null)
                    {
                        continue;
                    }

                    var animator = bodyPart.PartRoot.GetComponent<Animator>();
                    if (animator == null)
                    {
                        animator = bodyPart.PartRoot.AddComponent<Animator>();
                    }

                    animator.runtimeAnimatorController = null;
                    animator.applyRootMotion = false;
                    animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    animator.updateMode = AnimatorUpdateMode.Normal;
                    parts.Add(new PartState(bodyPart, animator));
                }
            }

            BuildSharedBoneBindings();
            if (parts.Count == 0)
            {
                return;
            }

            BuildGraph();
            if (!string.IsNullOrWhiteSpace(initialFullBodyActionId))
            {
                PlayFullBody(initialFullBodyActionId);
                // Snap the initial action instead of fading in from an empty pose.
                layers[0].Fade = 1f;
                ApplyWeightsAndTimes();
                EvaluateGraph();
            }
        }

        /// <summary>
        /// Convenience overload: binds every assembled frame part that uses the synchronized
        /// skinned rig, so callers migrating from MechSynchronizedAnimationPlayer only need the
        /// runtime state they already hold.
        /// </summary>
        public void Configure(
            MechActionRegistry actionRegistry,
            MechRuntimeState runtimeState,
            IEnumerable<string> maskedUpperBodyBoneNames,
            string initialFullBodyActionId = null,
            float speed = 1f)
        {
            var bodyParts = new List<MechBodyActionPart>();
            if (runtimeState != null)
            {
                for (var i = 0; i < runtimeState.AssembledParts.Count; i++)
                {
                    var record = runtimeState.AssembledParts[i];
                    if (record?.part == null || record.instance == null ||
                        !record.part.UsesSynchronizedSkinnedRig)
                    {
                        continue;
                    }

                    bodyParts.Add(new MechBodyActionPart(
                        record.slot,
                        record.instance,
                        record.part.AncestorDriverSlot,
                        record.part.AncestorDrivenBoneNames));
                }
            }

            Configure(actionRegistry, bodyParts, maskedUpperBodyBoneNames, initialFullBodyActionId, speed);
        }

        public bool HasAction(string actionId) => registry != null && registry.HasAction(actionId);

        /// <summary>Crossfades the base locomotion layer to the given registry action.</summary>
        public bool PlayFullBody(string actionId) => Play(actionId, MechBodyActionLayer.FullBody);

        /// <summary>
        /// Plays a masked upper-body action over the base layer. Non-looping actions fade the
        /// layer back out on their own once the clip ends; looping ones hold until
        /// <see cref="ClearUpperBody"/>.
        /// </summary>
        public bool PlayUpperBody(string actionId) => Play(actionId, MechBodyActionLayer.UpperBody);

        public void ClearUpperBody(float fadeOutSeconds = -1f)
        {
            var layer = layers[1];
            if (layer.Active == null)
            {
                return;
            }

            layer.LayerFadeTarget = 0f;
            layer.LayerFadeDuration = fadeOutSeconds >= 0f
                ? fadeOutSeconds
                : Mathf.Max(0.01f, layer.Active.blendOutSeconds);
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

        /// <summary>Manual stepping entry point for editor validation and shared-clock hosts.</summary>
        public void EvaluateAtTime(double timeSeconds)
        {
            if (!graph.IsValid())
            {
                return;
            }

            var delta = timeSeconds - clock;
            clock = timeSeconds;
            StepFades((float)System.Math.Max(0d, delta));
            ApplyWeightsAndTimes();
            EvaluateGraph();
        }

        private void Update()
        {
            if (!graph.IsValid())
            {
                return;
            }

            clock += Time.deltaTime * playbackSpeed;
            StepFades(Time.deltaTime);
            ApplyWeightsAndTimes();
            EvaluateGraph();
        }

        private void OnEnable()
        {
            if (graph.IsValid() || parts.Count == 0)
            {
                return;
            }

            // Rebuild after a disable/enable cycle and restore whatever was playing.
            var fullBodyActionId = layers[0].Active?.actionId;
            var upperBodyActionId = layers[1].Active?.actionId;
            layers[0].Reset();
            layers[1].Reset();
            BuildGraph();
            if (!string.IsNullOrEmpty(fullBodyActionId) && PlayFullBody(fullBodyActionId))
            {
                layers[0].Fade = 1f;
            }

            if (!string.IsNullOrEmpty(upperBodyActionId))
            {
                PlayUpperBody(upperBodyActionId);
            }

            ApplyWeightsAndTimes();
            EvaluateGraph();
        }

        private void OnDisable() => DestroyGraph();

        private void OnDestroy() => DestroyGraph();

        private bool Play(string actionId, MechBodyActionLayer layerKind)
        {
            if (registry == null || !registry.TryGetAction(actionId, out var definition) ||
                definition.layer != layerKind || !graph.IsValid())
            {
                return false;
            }

            var layerIndex = (int)layerKind;
            var layer = layers[layerIndex];
            if (layer.Active == definition && layer.Fade >= 1f)
            {
                return true;
            }

            // Interrupting an unfinished crossfade finalizes it first: the outgoing slot is
            // released and the current action becomes the fade-from source at full weight.
            // This bounds the slot count at two; a rapid double-interrupt can snap the
            // fade-from pose, which is accepted for the first version.
            var incomingSlot = 1 - layer.ActiveSlot;
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                part.ReleaseSlot(graph, layerIndex, incomingSlot);
                var clip = definition.ResolveClip(part.Source.Slot);
                if (clip != null)
                {
                    part.BindSlot(graph, layerIndex, incomingSlot, clip);
                }
            }

            layer.Previous = layer.Active;
            layer.PreviousStartTime = layer.ActiveStartTime;
            layer.Active = definition;
            layer.ActiveSlot = incomingSlot;
            layer.ActiveStartTime = clock;
            layer.Fade = 0f;
            layer.FadeDuration = Mathf.Max(0.01f, definition.blendInSeconds);
            if (layerKind == MechBodyActionLayer.UpperBody)
            {
                layer.LayerFadeTarget = 1f;
                layer.LayerFadeDuration = Mathf.Max(0.01f, definition.blendInSeconds);
            }

            return true;
        }

        private void StepFades(float deltaTime)
        {
            for (var layerIndex = 0; layerIndex < LayerCount; layerIndex++)
            {
                var layer = layers[layerIndex];
                if (layer.Active == null)
                {
                    continue;
                }

                if (layer.Fade < 1f)
                {
                    layer.Fade = Mathf.Min(1f, layer.Fade + deltaTime / layer.FadeDuration);
                    if (layer.Fade >= 1f && layer.Previous != null)
                    {
                        for (var i = 0; i < parts.Count; i++)
                        {
                            parts[i].ReleaseSlot(graph, layerIndex, 1 - layer.ActiveSlot);
                        }

                        layer.Previous = null;
                    }
                }

                if (layerIndex != (int)MechBodyActionLayer.UpperBody)
                {
                    continue;
                }

                // Non-looping upper-body actions schedule their own fade-out at clip end.
                if (!layer.Active.loop && layer.LayerFadeTarget > 0f)
                {
                    var clipLength = LongestActiveClipLength(layerIndex);
                    var endsAt = layer.ActiveStartTime + clipLength - layer.Active.blendOutSeconds;
                    if (clipLength > 0f && clock >= endsAt)
                    {
                        ClearUpperBody();
                    }
                }

                layer.LayerWeight = Mathf.MoveTowards(
                    layer.LayerWeight,
                    layer.LayerFadeTarget,
                    deltaTime / Mathf.Max(0.01f, layer.LayerFadeDuration));
                if (layer.LayerWeight <= 0f && layer.LayerFadeTarget <= 0f)
                {
                    for (var i = 0; i < parts.Count; i++)
                    {
                        parts[i].ReleaseSlot(graph, layerIndex, 0);
                        parts[i].ReleaseSlot(graph, layerIndex, 1);
                    }

                    layer.Reset();
                }
            }
        }

        private float LongestActiveClipLength(int layerIndex)
        {
            var length = 0f;
            for (var i = 0; i < parts.Count; i++)
            {
                var playable = parts[i].SlotPlayable(layerIndex, layers[layerIndex].ActiveSlot);
                if (playable.IsValid())
                {
                    var clip = playable.GetAnimationClip();
                    if (clip != null)
                    {
                        length = Mathf.Max(length, clip.length);
                    }
                }
            }

            return length;
        }

        private void ApplyWeightsAndTimes()
        {
            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                for (var layerIndex = 0; layerIndex < LayerCount; layerIndex++)
                {
                    var layer = layers[layerIndex];
                    var mixer = part.SlotMixer(layerIndex);
                    if (!mixer.IsValid())
                    {
                        continue;
                    }

                    for (var slot = 0; slot < SlotCount; slot++)
                    {
                        var playable = part.SlotPlayable(layerIndex, slot);
                        if (!playable.IsValid())
                        {
                            mixer.SetInputWeight(slot, 0f);
                            continue;
                        }

                        var isActiveSlot = slot == layer.ActiveSlot;
                        var startTime = isActiveSlot ? layer.ActiveStartTime : layer.PreviousStartTime;
                        var definition = isActiveSlot ? layer.Active : layer.Previous;
                        playable.SetTime(NormalizeTime(
                            clock - startTime,
                            playable.GetAnimationClip(),
                            definition?.loop ?? true));
                        playable.SetSpeed(0d);
                        mixer.SetInputWeight(slot, isActiveSlot ? layer.Fade : 1f - layer.Fade);
                    }

                    part.LayerMixer.SetInputWeight(
                        layerIndex,
                        layerIndex == 0 ? 1f : layer.LayerWeight);
                }
            }
        }

        private void EvaluateGraph()
        {
            graph.Evaluate(0f);
            SynchronizeSharedBones();
        }

        private void BuildGraph()
        {
            graph = PlayableGraph.Create($"{name}_MechBodyActions");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                part.LayerMixer = AnimationLayerMixerPlayable.Create(graph, LayerCount);
                for (var layerIndex = 0; layerIndex < LayerCount; layerIndex++)
                {
                    var mixer = AnimationMixerPlayable.Create(graph, SlotCount);
                    graph.Connect(mixer, 0, part.LayerMixer, layerIndex);
                    part.SetSlotMixer(layerIndex, mixer);
                    part.LayerMixer.SetInputWeight(layerIndex, layerIndex == 0 ? 1f : 0f);
                }

                var mask = BuildUpperBodyMask(part);
                if (mask != null)
                {
                    part.LayerMixer.SetLayerMaskFromAvatarMask(1, mask);
                }

                var output = AnimationPlayableOutput.Create(graph, $"Part_{i:00}", part.Animator);
                output.SetSourcePlayable(part.LayerMixer);
            }

            graph.Play();
        }

        private AvatarMask BuildUpperBodyMask(PartState part)
        {
            if (upperBodyBoneNames.Count == 0)
            {
                return null;
            }

            var mask = new AvatarMask { name = $"{part.Source.PartRoot.name}_UpperBodyMask" };
            var found = 0;
            var transforms = part.Source.PartRoot.GetComponentsInChildren<Transform>(true);
            for (var nameIndex = 0; nameIndex < upperBodyBoneNames.Count; nameIndex++)
            {
                var boneName = upperBodyBoneNames[nameIndex];
                if (string.IsNullOrWhiteSpace(boneName))
                {
                    continue;
                }

                for (var i = 0; i < transforms.Length; i++)
                {
                    if (string.Equals(transforms[i].name, boneName, System.StringComparison.Ordinal))
                    {
                        mask.AddTransformPath(transforms[i], false);
                        found++;
                        break;
                    }
                }
            }

            return found > 0 ? mask : null;
        }

        private void DestroyGraph()
        {
            if (graph.IsValid())
            {
                graph.Destroy();
            }

            for (var i = 0; i < parts.Count; i++)
            {
                parts[i].ClearPlayables();
            }
        }

        private static double NormalizeTime(double timeSeconds, AnimationClip clip, bool loop)
        {
            if (clip == null || clip.length <= 0f)
            {
                return 0d;
            }

            if (loop)
            {
                var wrapped = timeSeconds % clip.length;
                return wrapped < 0d ? wrapped + clip.length : wrapped;
            }

            return System.Math.Max(0d, System.Math.Min(clip.length, timeSeconds));
        }

        // ---- shared interface-bone synchronization (behavior parity with
        // ---- MechSynchronizedAnimationPlayer; extract into a shared helper when it retires) ----

        private void BuildSharedBoneBindings()
        {
            sharedBoneBindings.Clear();
            var boneMapsBySlot = new Dictionary<MechPartSlot, Dictionary<string, Transform>>();
            var driverSlotsByFollower = new Dictionary<MechPartSlot, MechPartSlot>();
            for (var i = 0; i < parts.Count; i++)
            {
                var slot = parts[i].Source.Slot;
                if (slot == MechPartSlot.None)
                {
                    continue;
                }

                boneMapsBySlot[slot] = BuildSkinnedBoneMap(parts[i].Source.PartRoot);
                driverSlotsByFollower[slot] = parts[i].Source.AncestorDriverSlot;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                var source = parts[i].Source;
                if (source.Slot == MechPartSlot.None || source.AncestorDriverSlot == MechPartSlot.None ||
                    source.AncestorDrivenBoneNames == null ||
                    !boneMapsBySlot.TryGetValue(source.AncestorDriverSlot, out var driverBones) ||
                    !boneMapsBySlot.TryGetValue(source.Slot, out var followerBones))
                {
                    continue;
                }

                var added = new HashSet<string>(System.StringComparer.Ordinal);
                for (var nameIndex = 0; nameIndex < source.AncestorDrivenBoneNames.Count; nameIndex++)
                {
                    var boneName = source.AncestorDrivenBoneNames[nameIndex];
                    if (string.IsNullOrWhiteSpace(boneName) || !added.Add(boneName))
                    {
                        continue;
                    }

                    if (driverBones.TryGetValue(boneName, out var driver) &&
                        followerBones.TryGetValue(boneName, out var follower) &&
                        driver != null && follower != null)
                    {
                        var order = GetSynchronizationDepth(source.Slot, driverSlotsByFollower);
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

        private sealed class LayerState
        {
            public MechBodyActionDefinition Active;
            public MechBodyActionDefinition Previous;
            public double ActiveStartTime;
            public double PreviousStartTime;
            public int ActiveSlot;
            public float Fade;
            public float FadeDuration = 0.15f;
            public float LayerWeight;
            public float LayerFadeTarget;
            public float LayerFadeDuration = 0.15f;

            public void Reset()
            {
                Active = null;
                Previous = null;
                ActiveStartTime = 0d;
                PreviousStartTime = 0d;
                ActiveSlot = 0;
                Fade = 0f;
                LayerWeight = 0f;
                LayerFadeTarget = 0f;
            }
        }

        private sealed class PartState
        {
            private readonly AnimationMixerPlayable[] slotMixers = new AnimationMixerPlayable[LayerCount];
            private readonly AnimationClipPlayable[,] slotPlayables = new AnimationClipPlayable[LayerCount, SlotCount];

            public PartState(MechBodyActionPart source, Animator animator)
            {
                Source = source;
                Animator = animator;
            }

            public MechBodyActionPart Source { get; }
            public Animator Animator { get; }
            public AnimationLayerMixerPlayable LayerMixer { get; set; }

            public AnimationMixerPlayable SlotMixer(int layerIndex) => slotMixers[layerIndex];

            public void SetSlotMixer(int layerIndex, AnimationMixerPlayable mixer)
                => slotMixers[layerIndex] = mixer;

            public AnimationClipPlayable SlotPlayable(int layerIndex, int slot)
                => slotPlayables[layerIndex, slot];

            public void BindSlot(PlayableGraph graph, int layerIndex, int slot, AnimationClip clip)
            {
                var playable = AnimationClipPlayable.Create(graph, clip);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                graph.Connect(playable, 0, slotMixers[layerIndex], slot);
                slotMixers[layerIndex].SetInputWeight(slot, 0f);
                slotPlayables[layerIndex, slot] = playable;
            }

            public void ReleaseSlot(PlayableGraph graph, int layerIndex, int slot)
            {
                var playable = slotPlayables[layerIndex, slot];
                if (!playable.IsValid())
                {
                    return;
                }

                if (slotMixers[layerIndex].IsValid())
                {
                    graph.Disconnect(slotMixers[layerIndex], slot);
                }

                playable.Destroy();
                slotPlayables[layerIndex, slot] = default;
            }

            public void ClearPlayables()
            {
                for (var layerIndex = 0; layerIndex < LayerCount; layerIndex++)
                {
                    slotMixers[layerIndex] = default;
                    for (var slot = 0; slot < SlotCount; slot++)
                    {
                        slotPlayables[layerIndex, slot] = default;
                    }
                }
            }
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
