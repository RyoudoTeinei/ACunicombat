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
        private const int FullBodyLayerIndex = 0;
        private const int UpperBodyLayerIndex = 1;
        private const int RightArmLayerIndex = 2;
        private const int LeftArmLayerIndex = 3;
        private const int LayerCount = 4;
        private const int SlotCount = 2;
        private const float MinimumArmBlendSeconds = 0.12f;
        private const float MinimumOverlayFadeOutSeconds = 0.2f;

        [SerializeField] private MechActionRegistry registry;
        [SerializeField] private float playbackSpeed = 1f;
        [SerializeField] private List<string> upperBodyBoneNames = new List<string>();

        private readonly List<PartState> parts = new List<PartState>();
        private readonly List<SharedBoneBinding> sharedBoneBindings = new List<SharedBoneBinding>();
        private readonly LayerState[] layers =
        {
            new LayerState(),
            new LayerState(),
            new LayerState(),
            new LayerState(),
        };
        private PlayableGraph graph;
        private double clock;
        private int upperBodyMaskPartCount;
        private int upperBodyMaskMatchedBoneCount;
        private int upperBodyMaskActivePathCount;

        public MechActionRegistry Registry => registry;
        public bool IsGraphValid => graph.IsValid();
        public double GraphTime => clock;
        public int PartCount => parts.Count;
        public int SharedBoneBindingCount => sharedBoneBindings.Count;
        public string ActiveFullBodyActionId =>
            layers[FullBodyLayerIndex].Active?.actionId ?? string.Empty;
        public string ActiveUpperBodyActionId =>
            layers[UpperBodyLayerIndex].Active?.actionId ?? string.Empty;
        public string ActiveRightArmActionId =>
            layers[RightArmLayerIndex].Active?.actionId ?? string.Empty;
        public string ActiveLeftArmActionId =>
            layers[LeftArmLayerIndex].Active?.actionId ?? string.Empty;
        public float UpperBodyLayerWeight => layers[UpperBodyLayerIndex].LayerWeight;
        public int UpperBodyMaskPartCount => upperBodyMaskPartCount;
        public int UpperBodyMaskMatchedBoneCount => upperBodyMaskMatchedBoneCount;
        public int UpperBodyMaskActivePathCount => upperBodyMaskActivePathCount;

        public bool IsFullBodyActionFinished(string actionId)
        {
            var layer = layers[(int)MechBodyActionLayer.FullBody];
            if (layer.Active == null || layer.Active.actionId != actionId)
            {
                return true;
            }

            if (layer.Active.loop)
            {
                return false;
            }

            var clipLength = LongestActiveClipLength((int)MechBodyActionLayer.FullBody);
            return clipLength > 0f && clock - layer.ActiveStartTime >= clipLength;
        }

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
            for (var layerIndex = 0; layerIndex < LayerCount; layerIndex++)
            {
                layers[layerIndex].Reset();
            }

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
                layers[FullBodyLayerIndex].Fade = 1f;
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
        public bool PlayFullBody(string actionId) =>
            Play(actionId, MechBodyActionLayer.FullBody, FullBodyLayerIndex, true);

        /// <summary>
        /// Plays a masked upper-body action over the base layer. Non-looping actions fade the
        /// layer back out on their own once the clip ends; looping ones hold until
        /// <see cref="ClearUpperBody"/>.
        /// </summary>
        public bool PlayUpperBody(string actionId) =>
            Play(actionId, MechBodyActionLayer.UpperBody, UpperBodyLayerIndex, true);

        /// <summary>
        /// Plays a right-arm action independently of the left arm. Weapon state machines use
        /// explicit transitions, so the final pose is held until the next action is blended in.
        /// </summary>
        public bool PlayRightArm(string actionId) =>
            Play(actionId, MechBodyActionLayer.UpperBody, RightArmLayerIndex, false);

        /// <summary>Plays a left-arm action independently of the right arm.</summary>
        public bool PlayLeftArm(string actionId) =>
            Play(actionId, MechBodyActionLayer.UpperBody, LeftArmLayerIndex, false);

        public void ClearUpperBody(float fadeOutSeconds = -1f)
        {
            ClearOverlayLayer(UpperBodyLayerIndex, fadeOutSeconds);
        }

        public void ClearRightArm(float fadeOutSeconds = -1f) =>
            ClearOverlayLayer(RightArmLayerIndex, fadeOutSeconds);

        public void ClearLeftArm(float fadeOutSeconds = -1f) =>
            ClearOverlayLayer(LeftArmLayerIndex, fadeOutSeconds);

        private void ClearOverlayLayer(int layerIndex, float fadeOutSeconds)
        {
            var layer = layers[layerIndex];
            if (layer.Active == null)
            {
                return;
            }

            layer.LayerFadeTarget = 0f;
            layer.LayerFadeDuration = fadeOutSeconds >= 0f
                ? Mathf.Max(MinimumOverlayFadeOutSeconds, fadeOutSeconds)
                : Mathf.Max(
                    MinimumOverlayFadeOutSeconds,
                    layer.Active.blendOutSeconds);
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
            var fullBodyActionId = layers[FullBodyLayerIndex].Active?.actionId;
            var upperBodyActionId = layers[UpperBodyLayerIndex].Active?.actionId;
            var rightArmActionId = layers[RightArmLayerIndex].Active?.actionId;
            var leftArmActionId = layers[LeftArmLayerIndex].Active?.actionId;
            for (var layerIndex = 0; layerIndex < LayerCount; layerIndex++)
            {
                layers[layerIndex].Reset();
            }

            BuildGraph();
            if (!string.IsNullOrEmpty(fullBodyActionId) && PlayFullBody(fullBodyActionId))
            {
                layers[FullBodyLayerIndex].Fade = 1f;
            }

            if (!string.IsNullOrEmpty(upperBodyActionId))
            {
                PlayUpperBody(upperBodyActionId);
            }

            if (!string.IsNullOrEmpty(rightArmActionId))
            {
                PlayRightArm(rightArmActionId);
            }

            if (!string.IsNullOrEmpty(leftArmActionId))
            {
                PlayLeftArm(leftArmActionId);
            }

            ApplyWeightsAndTimes();
            EvaluateGraph();
        }

        private void OnDisable() => DestroyGraph();

        private void OnDestroy() => DestroyGraph();

        private bool Play(
            string actionId,
            MechBodyActionLayer definitionLayer,
            int runtimeLayerIndex,
            bool autoFade)
        {
            if (registry == null || !registry.TryGetAction(actionId, out var definition) ||
                definition.layer != definitionLayer || !graph.IsValid())
            {
                return false;
            }

            var layer = layers[runtimeLayerIndex];
            if (layer.Active == definition && layer.Fade >= 1f)
            {
                if (runtimeLayerIndex != FullBodyLayerIndex)
                {
                    layer.LayerFadeTarget = 1f;
                    layer.LayerFadeDuration = ResolveBlendInDuration(
                        runtimeLayerIndex,
                        definition.blendInSeconds);
                    layer.AutoFade = autoFade;
                }

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
                part.ReleaseSlot(graph, runtimeLayerIndex, incomingSlot);
                var clip = definition.ResolveClip(part.Source.Slot);
                if (clip != null)
                {
                    part.BindSlot(graph, runtimeLayerIndex, incomingSlot, clip);
                }
            }

            layer.Previous = layer.Active;
            layer.PreviousStartTime = layer.ActiveStartTime;
            layer.Active = definition;
            layer.ActiveSlot = incomingSlot;
            layer.ActiveStartTime = clock;
            layer.Fade = 0f;
            layer.FadeDuration = ResolveBlendInDuration(
                runtimeLayerIndex,
                definition.blendInSeconds);
            layer.AutoFade = autoFade;
            if (runtimeLayerIndex != FullBodyLayerIndex)
            {
                layer.LayerFadeTarget = 1f;
                layer.LayerFadeDuration = layer.FadeDuration;
            }

            return true;
        }

        private static float ResolveBlendInDuration(
            int runtimeLayerIndex,
            float authoredBlendInSeconds)
        {
            var minimum =
                runtimeLayerIndex == RightArmLayerIndex ||
                runtimeLayerIndex == LeftArmLayerIndex
                    ? MinimumArmBlendSeconds
                    : 0.01f;
            return Mathf.Max(minimum, authoredBlendInSeconds);
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

                if (layerIndex == FullBodyLayerIndex)
                {
                    continue;
                }

                // Non-looping upper-body actions schedule their own fade-out at clip end.
                if (layer.AutoFade &&
                    !layer.Active.loop &&
                    layer.LayerFadeTarget > 0f)
                {
                    var clipLength = LongestActiveClipLength(layerIndex);
                    var endsAt = layer.ActiveStartTime + clipLength - layer.Active.blendOutSeconds;
                    if (clipLength > 0f && clock >= endsAt)
                    {
                        ClearOverlayLayer(layerIndex, -1f);
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
                        layerIndex == FullBodyLayerIndex ? 1f : layer.LayerWeight);
                }
            }
        }

        private void EvaluateGraph()
        {
            graph.Evaluate(0f);
            StabilizeLoopingRootPositions();
            SynchronizeSharedBones();
        }

        private void StabilizeLoopingRootPositions()
        {
            // applyRootMotion only prevents Animator from moving the component GameObject; it
            // does not suppress translation curves authored on the skeleton's Root bone. The
            // movement controller owns world-space locomotion, so looping body actions must stay
            // in place or the rendered mech drifts relative to its logical transform and camera.
            if (layers[FullBodyLayerIndex].Active?.loop != true)
            {
                return;
            }

            for (var i = 0; i < parts.Count; i++)
            {
                parts[i].RestoreSkeletonRootPosition();
            }
        }

        private void BuildGraph()
        {
            graph = PlayableGraph.Create($"{name}_MechBodyActions");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            upperBodyMaskPartCount = 0;
            upperBodyMaskMatchedBoneCount = 0;
            upperBodyMaskActivePathCount = 0;

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

                var upperMask = BuildBodyMask(
                    part,
                    '\0',
                    out var matchedBoneCount,
                    out var activePathCount);
                if (upperMask != null)
                {
                    part.LayerMixer.SetLayerMaskFromAvatarMask(
                        UpperBodyLayerIndex,
                        upperMask);
                    upperBodyMaskPartCount++;
                    upperBodyMaskMatchedBoneCount += matchedBoneCount;
                    upperBodyMaskActivePathCount += activePathCount;
                }

                var rightArmMask = BuildBodyMask(part, 'R', out _, out _);
                if (rightArmMask != null)
                {
                    part.LayerMixer.SetLayerMaskFromAvatarMask(
                        RightArmLayerIndex,
                        rightArmMask);
                }

                var leftArmMask = BuildBodyMask(part, 'L', out _, out _);
                if (leftArmMask != null)
                {
                    part.LayerMixer.SetLayerMaskFromAvatarMask(
                        LeftArmLayerIndex,
                        leftArmMask);
                }

                var output = AnimationPlayableOutput.Create(graph, $"Part_{i:00}", part.Animator);
                output.SetSourcePlayable(part.LayerMixer);
            }

            graph.Play();
            Debug.Log(
                $"[MechBodyActionPlayer] Upper-body masks: parts={upperBodyMaskPartCount}/{parts.Count}, " +
                $"matchedBones={upperBodyMaskMatchedBoneCount}, activePaths={upperBodyMaskActivePathCount}.",
                this);
        }

        private AvatarMask BuildBodyMask(
            PartState part,
            char side,
            out int matchedBoneCount,
            out int activePathCount)
        {
            matchedBoneCount = 0;
            activePathCount = 0;
            if (upperBodyBoneNames.Count == 0)
            {
                return null;
            }

            var suffix = side == '\0' ? "UpperBody" : $"{side}Arm";
            var mask = new AvatarMask
            {
                name = $"{part.Source.PartRoot.name}_{suffix}Mask",
            };
            var root = part.Source.PartRoot.transform;
            var transforms = part.Source.PartRoot.GetComponentsInChildren<Transform>(true);
            var wantedNames = new HashSet<string>(System.StringComparer.Ordinal);
            for (var i = 0; i < upperBodyBoneNames.Count; i++)
            {
                var boneName = upperBodyBoneNames[i];
                if (side == '\0' || IsSideBone(boneName, side))
                {
                    wantedNames.Add(boneName);
                }
            }

            var activeTransforms = new HashSet<Transform>();
            foreach (var boneName in wantedNames)
            {
                if (string.IsNullOrWhiteSpace(boneName))
                {
                    continue;
                }

                for (var i = 0; i < transforms.Length; i++)
                {
                    if (string.Equals(transforms[i].name, boneName, System.StringComparison.Ordinal))
                    {
                        matchedBoneCount++;
                        break;
                    }
                }
            }

            if (matchedBoneCount == 0 && side == '\0')
            {
                return null;
            }

            // AvatarMask paths must be relative to the Animator GameObject. AddTransformPath
            // derives paths from the scene root, which is wrong after modular parts are nested
            // under the assembled mech. Build the complete relative path table explicitly and
            // keep the ancestor chain active so masked child curves can reach the arm bones.
            for (var i = 0; i < transforms.Length; i++)
            {
                if (!wantedNames.Contains(transforms[i].name))
                {
                    continue;
                }

                for (var current = transforms[i]; current != null; current = current.parent)
                {
                    activeTransforms.Add(current);
                    if (current == root)
                    {
                        break;
                    }
                }
            }

            mask.transformCount = transforms.Length;
            for (var i = 0; i < transforms.Length; i++)
            {
                var path = GetRelativeTransformPath(transforms[i], root);
                if (path == null)
                {
                    continue;
                }

                var active = activeTransforms.Contains(transforms[i]);
                mask.SetTransformPath(i, path);
                mask.SetTransformActive(i, active);
                if (active)
                {
                    activePathCount++;
                }
            }

            return mask;
        }

        private static bool IsSideBone(string boneName, char side)
        {
            if (string.IsNullOrEmpty(boneName))
            {
                return false;
            }

            var marker = $"{side}_";
            return boneName.StartsWith(marker, System.StringComparison.Ordinal) ||
                   boneName.Contains($"_{marker}", System.StringComparison.Ordinal);
        }

        private static string GetRelativeTransformPath(Transform target, Transform root)
        {
            if (target == root)
            {
                return string.Empty;
            }

            var segments = new Stack<string>();
            for (var current = target; current != null && current != root; current = current.parent)
            {
                segments.Push(current.name);
                if (current.parent == root)
                {
                    return string.Join("/", segments);
                }
            }

            return null;
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
            public bool AutoFade = true;

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
                AutoFade = true;
            }
        }

        private sealed class PartState
        {
            private readonly AnimationMixerPlayable[] slotMixers = new AnimationMixerPlayable[LayerCount];
            private readonly AnimationClipPlayable[,] slotPlayables = new AnimationClipPlayable[LayerCount, SlotCount];
            private readonly Transform skeletonRoot;
            private readonly Vector3 skeletonRootRestPosition;

            public PartState(MechBodyActionPart source, Animator animator)
            {
                Source = source;
                Animator = animator;
                skeletonRoot = FindSkeletonRoot(source.PartRoot);
                if (skeletonRoot != null)
                {
                    skeletonRootRestPosition = skeletonRoot.localPosition;
                }
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

            public void RestoreSkeletonRootPosition()
            {
                if (skeletonRoot != null)
                {
                    skeletonRoot.localPosition = skeletonRootRestPosition;
                }
            }

            private static Transform FindSkeletonRoot(GameObject partRoot)
            {
                if (partRoot == null)
                {
                    return null;
                }

                var direct = partRoot.transform.Find("Master/Root");
                if (direct != null)
                {
                    return direct;
                }

                var transforms = partRoot.GetComponentsInChildren<Transform>(true);
                for (var i = 0; i < transforms.Length; i++)
                {
                    if (transforms[i].name == "Root" && transforms[i].parent != null &&
                        transforms[i].parent.name == "Master")
                    {
                        return transforms[i];
                    }
                }

                return null;
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
