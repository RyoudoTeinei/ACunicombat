using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>Runs one registry action in isolation for visual review.</summary>
    [DisallowMultipleComponent]
    public sealed class MechSingleActionPreview : MonoBehaviour
    {
        [SerializeField] private MechAssembler assembler;
        [SerializeField] private MechActionRegistry registry;
        [SerializeField] private string actionId;
        [SerializeField] private float playbackSpeed = 1f;

        public void Configure(
            MechAssembler targetAssembler,
            MechActionRegistry actionRegistry,
            string targetActionId,
            float speed = 1f)
        {
            assembler = targetAssembler;
            registry = actionRegistry;
            actionId = targetActionId;
            playbackSpeed = speed;
        }

        private void Start()
        {
            if (assembler == null)
            {
                assembler = GetComponent<MechAssembler>();
            }

            if (assembler == null || registry == null || string.IsNullOrWhiteSpace(actionId))
            {
                Debug.LogError("[MechSingleActionPreview] Preview configuration is incomplete.", this);
                return;
            }

            var runtimeState = GetComponent<MechRuntimeState>();
            if (runtimeState == null || runtimeState.AssembledParts.Count == 0)
            {
                assembler.Assemble();
                runtimeState = GetComponent<MechRuntimeState>();
            }

            var legacyPlayer = GetComponent<MechSynchronizedAnimationPlayer>();
            if (legacyPlayer != null)
            {
                legacyPlayer.StopPlayback();
                legacyPlayer.enabled = false;
            }

            var player = GetComponent<MechBodyActionPlayer>();
            if (player == null)
            {
                player = gameObject.AddComponent<MechBodyActionPlayer>();
            }

            player.Configure(registry, runtimeState, null, actionId, playbackSpeed);
            Debug.Log(
                $"[MechSingleActionPreview] Playing {actionId}: " +
                $"graph={player.IsGraphValid}, parts={player.PartCount}.",
                this);
        }
    }
}
