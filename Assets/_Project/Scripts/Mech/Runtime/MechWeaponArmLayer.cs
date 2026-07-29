using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// Independent right/left weapon animation state machines layered over locomotion.
    /// Each hand owns its own raise, hold, fire and lower transitions.
    /// </summary>
    public sealed class MechWeaponArmLayer : MonoBehaviour
    {
        public enum ArmPhase : byte
        {
            Lowered = 0,
            Raising = 1,
            Holding = 2,
            Firing = 3,
        }

        [Serializable]
        public struct ArmActionSet
        {
            public string raiseActionId;
            public string holdActionId;
            public string fireActionId;
            [Min(0f)] public float raiseSeconds;
            [Min(0f)] public float fireSeconds;
            public bool repeatFireWhileHeld;
        }

        [Tooltip("Anatomical right-arm actions: 03002x/03003x.")]
        [SerializeField] private ArmActionSet rightArm =
            CreateRightArmActionSet();

        [Tooltip("Anatomical left-arm actions: 03004x/03005x.")]
        [SerializeField] private ArmActionSet leftArm =
            CreateLeftArmActionSet();

        [Tooltip("Delay after releasing an arm before it smoothly lowers.")]
        [SerializeField, Min(0f)] private float lowerTimeoutSeconds = 1.5f;

        private readonly ArmRuntimeState rightState = new ArmRuntimeState();
        private readonly ArmRuntimeState leftState = new ArmRuntimeState();
        private MechBodyActionPlayer player;
        private bool activeSideIsRight = true;

        public ArmPhase Phase =>
            activeSideIsRight ? rightState.Phase : leftState.Phase;

        public ArmPhase RightPhase => rightState.Phase;

        public ArmPhase LeftPhase => leftState.Phase;

        public bool ActiveSideIsRight => activeSideIsRight;

        public string CurrentActionId =>
            player == null
                ? string.Empty
                : activeSideIsRight
                    ? player.ActiveRightArmActionId
                    : player.ActiveLeftArmActionId;

        public string RightFireActionId => rightArm.fireActionId;

        public string LeftFireActionId => leftArm.fireActionId;

        public bool HasVerifiedSideMapping =>
            rightArm.raiseActionId == "a200_030020" &&
            rightArm.holdActionId == "a200_030022" &&
            rightArm.fireActionId == "a200_030030" &&
            leftArm.raiseActionId == "a200_030040" &&
            leftArm.holdActionId == "a200_030042" &&
            leftArm.fireActionId == "a200_030050";

        private void Awake() => ApplyVerifiedSideMapping();

        public void ApplyVerifiedSideMapping()
        {
            rightArm = CreateRightArmActionSet();
            leftArm = CreateLeftArmActionSet();
        }

        public void Configure(MechBodyActionPlayer actionPlayer)
        {
            ApplyVerifiedSideMapping();
            player = actionPlayer;
            rightState.Reset();
            leftState.Reset();
        }

        public void Tick(
            in MechPlayerInputFrame frame,
            bool suppress,
            float deltaTime)
        {
            if (player == null)
            {
                return;
            }

            if (suppress)
            {
                SuppressArm(rightState, true);
                SuppressArm(leftState, false);
                return;
            }

            if (frame.SecondaryFireEdge)
            {
                activeSideIsRight = true;
            }
            else if (frame.PrimaryFireEdge)
            {
                activeSideIsRight = false;
            }

            TickArm(
                rightState,
                in rightArm,
                true,
                frame.SecondaryFireHeld,
                frame.SecondaryFireEdge,
                deltaTime);
            TickArm(
                leftState,
                in leftArm,
                false,
                frame.PrimaryFireHeld,
                frame.PrimaryFireEdge,
                deltaTime);
        }

        private void SuppressArm(ArmRuntimeState state, bool isRight)
        {
            if (state.Phase == ArmPhase.Lowered)
            {
                return;
            }

            state.Reset();
            ClearArm(isRight);
        }

        private void TickArm(
            ArmRuntimeState state,
            in ArmActionSet set,
            bool isRight,
            bool fireHeld,
            bool fireEdge,
            float deltaTime)
        {
            state.PhaseElapsed += deltaTime;
            state.IdleElapsed = fireHeld
                ? 0f
                : state.IdleElapsed + deltaTime;
            state.FireQueued |= fireEdge;

            switch (state.Phase)
            {
                case ArmPhase.Lowered:
                    if (fireHeld || fireEdge)
                    {
                        PlayArm(isRight, set.raiseActionId);
                        SetPhase(state, ArmPhase.Raising);
                    }

                    break;

                case ArmPhase.Raising:
                    if (state.PhaseElapsed >= set.raiseSeconds)
                    {
                        if (fireHeld || state.FireQueued)
                        {
                            state.FireQueued = false;
                            PlayArm(isRight, set.fireActionId);
                            SetPhase(state, ArmPhase.Firing);
                        }
                        else
                        {
                            PlayArm(isRight, set.holdActionId);
                            SetPhase(state, ArmPhase.Holding);
                        }
                    }

                    break;

                case ArmPhase.Holding:
                    if (fireEdge)
                    {
                        state.FireQueued = false;
                        PlayArm(isRight, set.fireActionId);
                        SetPhase(state, ArmPhase.Firing);
                    }
                    else if (!fireHeld &&
                             state.IdleElapsed >= lowerTimeoutSeconds)
                    {
                        ClearArm(isRight);
                        state.Reset();
                    }

                    break;

                case ArmPhase.Firing:
                    if (state.PhaseElapsed >= set.fireSeconds)
                    {
                        if ((fireHeld && set.repeatFireWhileHeld) ||
                            state.FireQueued)
                        {
                            state.FireQueued = false;
                            ReplayArm(isRight, set.fireActionId);
                            SetPhase(state, ArmPhase.Firing);
                        }
                        else
                        {
                            PlayArm(isRight, set.holdActionId);
                            SetPhase(state, ArmPhase.Holding);
                        }
                    }

                    break;
            }
        }

        private void PlayArm(bool isRight, string actionId)
        {
            if (string.IsNullOrEmpty(actionId) ||
                !player.HasAction(actionId))
            {
                return;
            }

            if (isRight)
            {
                player.PlayRightArm(actionId);
            }
            else
            {
                player.PlayLeftArm(actionId);
            }
        }

        private void ClearArm(bool isRight)
        {
            if (isRight)
            {
                player.ClearRightArm(0.25f);
            }
            else
            {
                player.ClearLeftArm(0.25f);
            }
        }

        private void ReplayArm(bool isRight, string actionId)
        {
            if (string.IsNullOrEmpty(actionId) ||
                !player.HasAction(actionId))
            {
                return;
            }

            if (isRight)
            {
                player.ReplayRightArm(actionId);
            }
            else
            {
                player.ReplayLeftArm(actionId);
            }
        }

        private static void SetPhase(
            ArmRuntimeState state,
            ArmPhase next)
        {
            state.Phase = next;
            state.PhaseElapsed = 0f;
        }

        private static ArmActionSet CreateRightArmActionSet()
        {
            return new ArmActionSet
            {
                raiseActionId = "a200_030020",
                holdActionId = "a200_030022",
                fireActionId = "a200_030030",
                raiseSeconds = 0.5f,
                fireSeconds = 0.4f,
                repeatFireWhileHeld = true,
            };
        }

        private static ArmActionSet CreateLeftArmActionSet()
        {
            return new ArmActionSet
            {
                raiseActionId = "a200_030040",
                holdActionId = "a200_030042",
                fireActionId = "a200_030050",
                raiseSeconds = 0.5f,
                fireSeconds = 0.4f,
                repeatFireWhileHeld = false,
            };
        }

        private sealed class ArmRuntimeState
        {
            public ArmPhase Phase;
            public float PhaseElapsed;
            public float IdleElapsed;
            public bool FireQueued;

            public void Reset()
            {
                Phase = ArmPhase.Lowered;
                PhaseElapsed = 0f;
                IdleElapsed = 0f;
                FireQueued = false;
            }
        }
    }
}
