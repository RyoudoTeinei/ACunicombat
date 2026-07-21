using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechEquipmentAnimationPlayer : MonoBehaviour
    {
        [SerializeField] private List<MechEquipmentAnimationDefinition> actions =
            new List<MechEquipmentAnimationDefinition>();
        [SerializeField] private string defaultActionId;
        [SerializeField] private bool playDefaultActionOnEnable;
        [SerializeField] private float playbackSpeed = 1f;

        private readonly Dictionary<string, MechEquipmentAnimationDefinition> actionsById =
            new Dictionary<string, MechEquipmentAnimationDefinition>(StringComparer.OrdinalIgnoreCase);
        private Animator animator;
        private PlayableGraph graph;
        private AnimationClipPlayable clipPlayable;
        private MechEquipmentAnimationDefinition activeAction;
        private double playbackTime;
        private bool isPlaying;
        private bool sampleDirectly;

        public IReadOnlyList<MechEquipmentAnimationDefinition> Actions => actions;
        public string DefaultActionId => defaultActionId;
        public string ActiveActionId => activeAction == null ? string.Empty : activeAction.ActionId;
        public double PlaybackTime => playbackTime;
        public bool IsPlaying => isPlaying;
        public bool IsGraphValid => graph.IsValid() || (sampleDirectly && activeAction?.Clip != null);

        public void Configure(
            IEnumerable<MechEquipmentAnimationDefinition> animationActions,
            string newDefaultActionId,
            bool shouldPlayDefaultAction,
            float speed = 1f)
        {
            DestroyGraph();
            actions.Clear();
            if (animationActions != null)
            {
                foreach (var action in animationActions)
                {
                    if (action != null && action.Clip != null && !string.IsNullOrWhiteSpace(action.ActionId))
                    {
                        actions.Add(action);
                    }
                }
            }

            defaultActionId = newDefaultActionId ?? string.Empty;
            playDefaultActionOnEnable = shouldPlayDefaultAction;
            playbackSpeed = Mathf.Approximately(speed, 0f) ? 1f : speed;
            activeAction = null;
            playbackTime = 0d;
            isPlaying = false;
            RebuildActionLookup();
            PrepareAnimator();

            var initialActionId = ResolveDefaultActionId();
            if (string.IsNullOrWhiteSpace(initialActionId))
            {
                return;
            }

            if (Application.isPlaying && isActiveAndEnabled && playDefaultActionOnEnable)
            {
                PlayAction(initialActionId);
            }
            else
            {
                SampleActionAtTime(initialActionId, 0f);
            }
        }

        public bool PlayAction(string actionId, bool restart = true)
        {
            if (!TryGetAction(actionId, out var action))
            {
                return false;
            }

            if (!restart && isPlaying && activeAction == action)
            {
                return true;
            }

            activeAction = action;
            playbackTime = 0d;
            isPlaying = true;
            PrepareAnimator();

            if (!Application.isPlaying || !isActiveAndEnabled)
            {
                action.Clip.SampleAnimation(gameObject, 0f);
                isPlaying = false;
                return true;
            }

            BuildGraph();
            isPlaying = graph.IsValid();
            return isPlaying;
        }

        public bool PlayActionPreservingPose(string actionId, bool restart = true)
        {
            if (!TryGetAction(actionId, out var action))
            {
                return false;
            }

            if (!restart && isPlaying && activeAction == action)
            {
                return true;
            }

            DestroyGraph();
            activeAction = action;
            playbackTime = 0d;
            sampleDirectly = true;
            isPlaying = true;
            PrepareAnimator();

            // AC6 equipment actions are sparse state transitions. Sampling only their keyed
            // properties preserves the previous action's terminal pose for unkeyed doors,
            // rails and brackets instead of restoring the imported GLB bind pose.
            action.Clip.SampleAnimation(gameObject, 0f);
            return true;
        }

        public bool SampleActionAtTime(string actionId, float timeSeconds)
        {
            if (!TryGetAction(actionId, out var action))
            {
                return false;
            }

            DestroyGraph();
            activeAction = action;
            playbackTime = NormalizeTime(timeSeconds, action);
            PrepareAnimator();
            action.Clip.SampleAnimation(gameObject, (float)playbackTime);
            return true;
        }

        public bool SampleActionAtEnd(string actionId)
        {
            if (!TryGetAction(actionId, out var action) || action.Clip == null)
            {
                return false;
            }

            return SampleActionAtTime(actionId, action.Clip.length);
        }

        public void StopPlayback()
        {
            DestroyGraph();
            activeAction = null;
            playbackTime = 0d;
        }

        public bool HasAction(string actionId)
        {
            RebuildActionLookupIfNeeded();
            return !string.IsNullOrWhiteSpace(actionId) && actionsById.ContainsKey(actionId);
        }

        private void OnEnable()
        {
            RebuildActionLookupIfNeeded();
            PrepareAnimator();
            if (Application.isPlaying && playDefaultActionOnEnable && !isPlaying)
            {
                var initialActionId = ResolveDefaultActionId();
                if (!string.IsNullOrWhiteSpace(initialActionId))
                {
                    PlayAction(initialActionId);
                }
            }
        }

        private void OnDisable()
        {
            DestroyGraph();
        }

        private void OnDestroy()
        {
            DestroyGraph();
        }

        private void Update()
        {
            if (!isPlaying || activeAction?.Clip == null)
            {
                return;
            }

            playbackTime += Time.deltaTime * playbackSpeed;
            if (!activeAction.Loop && playbackTime >= activeAction.Clip.length)
            {
                playbackTime = activeAction.Clip.length;
                isPlaying = false;
            }
            else
            {
                playbackTime = NormalizeTime(playbackTime, activeAction);
            }

            if (sampleDirectly)
            {
                activeAction.Clip.SampleAnimation(gameObject, (float)playbackTime);
                return;
            }

            if (graph.IsValid())
            {
                ApplyPlayableTime();
                graph.Evaluate(0f);
            }
            else
            {
                isPlaying = false;
            }
        }

        private void PrepareAnimator()
        {
            if (animator == null)
            {
                animator = GetComponent<Animator>();
            }
            if (animator == null)
            {
                animator = gameObject.AddComponent<Animator>();
            }

            animator.runtimeAnimatorController = null;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            animator.updateMode = AnimatorUpdateMode.Normal;
        }

        private void BuildGraph()
        {
            DestroyGraph();
            if (activeAction?.Clip == null || animator == null)
            {
                return;
            }

            graph = PlayableGraph.Create($"{name}_EquipmentAction");
            graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
            clipPlayable = AnimationClipPlayable.Create(graph, activeAction.Clip);
            clipPlayable.SetApplyFootIK(false);
            clipPlayable.SetApplyPlayableIK(false);
            clipPlayable.SetSpeed(0d);
            var output = AnimationPlayableOutput.Create(graph, "Equipment", animator);
            output.SetSourcePlayable(clipPlayable);
            ApplyPlayableTime();
            graph.Play();
            graph.Evaluate(0f);
        }

        private void ApplyPlayableTime()
        {
            if (clipPlayable.IsValid())
            {
                clipPlayable.SetTime(playbackTime);
                clipPlayable.SetSpeed(0d);
            }
        }

        private void DestroyGraph()
        {
            if (graph.IsValid())
            {
                graph.Destroy();
            }

            clipPlayable = default;
            isPlaying = false;
            sampleDirectly = false;
        }

        private bool TryGetAction(string actionId, out MechEquipmentAnimationDefinition action)
        {
            RebuildActionLookupIfNeeded();
            if (!string.IsNullOrWhiteSpace(actionId) && actionsById.TryGetValue(actionId, out action))
            {
                return true;
            }

            action = null;
            return false;
        }

        private void RebuildActionLookupIfNeeded()
        {
            if (actionsById.Count == 0 && actions.Count > 0)
            {
                RebuildActionLookup();
            }
        }

        private void RebuildActionLookup()
        {
            actionsById.Clear();
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null || action.Clip == null || string.IsNullOrWhiteSpace(action.ActionId))
                {
                    continue;
                }

                actionsById[action.ActionId] = action;
            }
        }

        private string ResolveDefaultActionId()
        {
            RebuildActionLookupIfNeeded();
            if (!string.IsNullOrWhiteSpace(defaultActionId) && actionsById.ContainsKey(defaultActionId))
            {
                return defaultActionId;
            }

            return actions.Count > 0 ? actions[0].ActionId : string.Empty;
        }

        private static double NormalizeTime(double timeSeconds, MechEquipmentAnimationDefinition action)
        {
            if (action?.Clip == null || action.Clip.length <= 0f)
            {
                return 0d;
            }

            if (!action.Loop)
            {
                return Math.Max(0d, Math.Min(action.Clip.length, timeSeconds));
            }

            var duration = action.Clip.length;
            var wrapped = timeSeconds % duration;
            return wrapped < 0d ? wrapped + duration : wrapped;
        }
    }
}
