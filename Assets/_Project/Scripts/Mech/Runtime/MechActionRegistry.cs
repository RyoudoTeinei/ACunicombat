using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechBodyActionLayer
    {
        /// <summary>Base locomotion layer: idle, flight loops, quick boosts, landings.</summary>
        FullBody = 0,

        /// <summary>Override layer masked to the spine/arm chains: weapon raise, aim, fire, recoil.</summary>
        UpperBody = 1
    }

    /// <summary>
    /// One body action the mech can play: which layer it lives on, how it blends, and the
    /// per-frame-part clips that realize it. Slot clips exist because the four frame parts
    /// currently carry their own rigs (option 2 of the multi-action export plan); once the
    /// canonical shared-skeleton rig lands (option 3) each entry collapses to a single clip
    /// and <see cref="ResolveClip"/> keeps callers unchanged.
    /// </summary>
    [Serializable]
    public sealed class MechBodyActionDefinition
    {
        [Serializable]
        public struct SlotClip
        {
            public MechPartSlot slot;
            public AnimationClip clip;
        }

        [Tooltip("Native action id, e.g. a000_012345. TAE-derived; never invent ids by filename range.")]
        public string actionId;
        public MechBodyActionLayer layer = MechBodyActionLayer.FullBody;
        public bool loop = true;
        [Min(0f)] public float blendInSeconds = 0.15f;
        [Min(0f)] public float blendOutSeconds = 0.15f;
        [Tooltip("Per frame-part clips (option 2). Leave a single None-slot entry once canonical-rig clips exist.")]
        public List<SlotClip> slotClips = new List<SlotClip>();

        public AnimationClip ResolveClip(MechPartSlot slot)
        {
            AnimationClip fallback = null;
            for (var i = 0; i < slotClips.Count; i++)
            {
                if (slotClips[i].slot == slot && slotClips[i].clip != null)
                {
                    return slotClips[i].clip;
                }

                if (slotClips[i].slot == MechPartSlot.None && slotClips[i].clip != null)
                {
                    fallback = slotClips[i].clip;
                }
            }

            return fallback;
        }
    }

    /// <summary>
    /// Data-driven action table: actionId -> per-slot clips + layer/blend metadata. Loadouts
    /// reference a registry instead of an Animator Controller, so swapping parts never touches
    /// authored state machines. Populated by the batch action exporter; hand-editing is only
    /// expected while the TAE-derived action table is still being confirmed.
    /// </summary>
    [CreateAssetMenu(
        fileName = "MechActionRegistry",
        menuName = "ACunicombat/Mech Action Registry")]
    public sealed class MechActionRegistry : ScriptableObject
    {
        [SerializeField] private List<MechBodyActionDefinition> actions = new List<MechBodyActionDefinition>();

        private Dictionary<string, MechBodyActionDefinition> lookup;

        public IReadOnlyList<MechBodyActionDefinition> Actions => actions;

        public bool TryGetAction(string actionId, out MechBodyActionDefinition definition)
        {
            definition = null;
            if (string.IsNullOrWhiteSpace(actionId))
            {
                return false;
            }

            if (lookup == null || lookup.Count != actions.Count)
            {
                RebuildLookup();
            }

            return lookup.TryGetValue(actionId, out definition) && definition != null;
        }

        public bool HasAction(string actionId) => TryGetAction(actionId, out _);

        private void OnValidate()
        {
            lookup = null;
        }

        private void RebuildLookup()
        {
            lookup = new Dictionary<string, MechBodyActionDefinition>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];
                if (action == null || string.IsNullOrWhiteSpace(action.actionId))
                {
                    continue;
                }

                lookup[action.actionId] = action;
            }
        }
    }
}
