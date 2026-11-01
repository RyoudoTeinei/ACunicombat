using System;
using System.Collections.Generic;

namespace ACunicombat.Mechs
{
    /// <summary>One behaviour of an AI layer. Lower priority values preempt higher ones.</summary>
    public interface IMechAiState
    {
        string Name { get; }
        int Priority { get; }
        /// <summary>Minimum time the state holds before a non-preempting transition may leave it.</summary>
        float MinimumDwellSeconds { get; }
        bool IsFinished { get; }
        void Enter(float now);
        void Tick(float now);
        void Exit(float now);
    }

    /// <summary>Convenience base with no-op callbacks.</summary>
    public abstract class MechAiState : IMechAiState
    {
        protected MechAiState(int priority, float minimumDwellSeconds = 0f)
        { Priority = priority; MinimumDwellSeconds = minimumDwellSeconds; }
        public virtual string Name => GetType().Name;
        public int Priority { get; }
        public float MinimumDwellSeconds { get; }
        public bool IsFinished { get; protected set; }
        public virtual void Enter(float now) => IsFinished = false;
        public virtual void Tick(float now) { }
        public virtual void Exit(float now) { }
    }

    /// <summary>
    /// Priority-preemptive finite state machine (after Sea Power's aircraft FSM).
    /// "Any" transitions are evaluated first and may only preempt states with a
    /// larger priority value. Ordinary transitions honour the current state's
    /// minimum dwell unless the state reports IsFinished. A preempted base state
    /// is remembered so an interrupt can return to it instead of a default.
    /// </summary>
    public sealed class MechAiStateMachine
    {
        private readonly struct Transition
        {
            public readonly IMechAiState To;
            public readonly Func<bool> Condition;
            public readonly bool Interrupt;
            public Transition(IMechAiState to, Func<bool> condition, bool interrupt = false) { To = to; Condition = condition; Interrupt = interrupt; }
        }

        private readonly Dictionary<IMechAiState, List<Transition>> transitions = new();
        private readonly List<Transition> anyTransitions = new();
        private static readonly List<Transition> None = new(0);
        private List<Transition> current = None;
        private bool enteredByInterrupt;

        public IMechAiState Current { get; private set; }
        public IMechAiState Previous { get; private set; }
        /// <summary>Last non-interrupt state that was preempted by an Any transition.</summary>
        public IMechAiState Interrupted { get; private set; }
        public float EnteredAt { get; private set; }
        public int TransitionCount { get; private set; }
        public string CurrentName => Current?.Name ?? "Null";
        public event Action<IMechAiState, IMechAiState> Changed;

        public void Add(IMechAiState from, IMechAiState to, Func<bool> condition)
        {
            if (from == null || to == null || condition == null) throw new ArgumentNullException();
            if (!transitions.TryGetValue(from, out var list)) transitions[from] = list = new List<Transition>();
            list.Add(new Transition(to, condition));
        }

        /// <summary>
        /// A global transition that may preempt any state with a larger priority value.
        /// Mark <paramref name="interrupt"/> for transient overrides (evasion, damage
        /// reactions) so the displaced base state is remembered in <see cref="Interrupted"/>.
        /// </summary>
        public void AddAny(IMechAiState to, Func<bool> condition, bool interrupt = false)
        {
            if (to == null || condition == null) throw new ArgumentNullException();
            anyTransitions.Add(new Transition(to, condition, interrupt));
        }

        public void Start(IMechAiState state, float now)
        {
            Current?.Exit(now);
            Previous = Current; Interrupted = null; enteredByInterrupt = false;
            Enter(state, now);
        }

        /// <summary>Evaluate one transition and tick the resulting state.</summary>
        public void Tick(float now)
        {
            Evaluate(now);
            Current?.Tick(now);
        }

        public bool Evaluate(float now)
        {
            if (Current == null) return false;
            foreach (var t in anyTransitions)
            {
                if (t.To == Current || t.To.Priority >= Current.Priority || !t.Condition()) continue;
                // Remember the base behaviour the interrupt displaced. A nested
                // interrupt (preempting an interrupt) keeps the original base.
                if (t.Interrupt) { if (!enteredByInterrupt) Interrupted = Current; }
                else Interrupted = null;
                Switch(t.To, now);
                enteredByInterrupt = t.Interrupt;
                return true;
            }
            bool dwellElapsed = Current.IsFinished || now - EnteredAt >= Current.MinimumDwellSeconds;
            if (!dwellElapsed) return false;
            foreach (var t in current)
            {
                if (t.To == Current || !t.Condition()) continue;
                // Leaving an interrupt through an ordinary transition resolves it.
                Interrupted = null;
                Switch(t.To, now);
                enteredByInterrupt = false;
                return true;
            }
            return false;
        }

        private void Switch(IMechAiState to, float now)
        {
            var from = Current;
            from.Exit(now);
            Previous = from;
            Enter(to, now);
            TransitionCount++;
            Changed?.Invoke(from, to);
        }

        private void Enter(IMechAiState state, float now)
        {
            Current = state;
            EnteredAt = now;
            current = transitions.TryGetValue(state, out var list) ? list : None;
            state.Enter(now);
        }
    }
}
