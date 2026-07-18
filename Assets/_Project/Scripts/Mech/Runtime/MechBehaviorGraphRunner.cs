using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    /// <summary>
    /// BHV 行为图通用解释器：任意动作间的自由衔接来自数据（8673 条官方转换），
    /// 本组件只做执行。每 Tick：
    ///   1. 全部条件原子求值成栈上位掩码（无分配、无字符串）；
    ///   2. 按存储序（= 原版裁决序）扫描当前状态的转换，首个全真且目标可用者跳转；
    ///   3. 跳转时经 MechBodyActionPlayer 播目标动作，blend-in 用 TAE 官方值。
    /// 未映射条件恒假 → 对应转换沉默不触发；无动画状态不可进入 → 转换顺延到下一候选。
    /// 覆盖率随条件目录映射与动作确认单调上升，图从第一天起可运行。
    /// 验证接口：CurrentStateIndex / CurrentStateName / StateChanged 事件 + 最近转换环形记录。
    /// </summary>
    public sealed class MechBehaviorGraphRunner : MonoBehaviour
    {
        public const int HistoryCapacity = 64;

        [SerializeField] private MechBehaviorGraphAsset graph;
        [SerializeField] private MechBodyActionPlayer actionPlayer;
        [Tooltip("单 Tick 最大连续跳转数：防止空动画/瞬时条件构成的状态环死锁一帧。")]
        [SerializeField] private int maxChainedTransitionsPerTick = 4;

        private int currentState = -1;
        private float stateElapsed;
        private bool animFinished;
        private int[] animFinishedAtoms = Array.Empty<int>();

        private readonly (int from, int to, float time)[] history = new (int, int, float)[HistoryCapacity];
        private int historyCount;

        public MechBehaviorGraphAsset Graph => graph;

        public int CurrentStateIndex => currentState;

        public string CurrentStateName =>
            graph != null && currentState >= 0 ? graph.states[currentState].officialName : string.Empty;

        public float StateElapsedSeconds => stateElapsed;

        /// <summary>(fromState, toState)。demo/测试断言状态序列用。</summary>
        public event Action<int, int> StateChanged;

        public void Configure(MechBehaviorGraphAsset graphAsset, MechBodyActionPlayer player)
        {
            graph = graphAsset;
            actionPlayer = player;
            Reset();
        }

        public void Reset()
        {
            currentState = -1;
            stateElapsed = 0f;
            animFinished = false;
            historyCount = 0;
            if (graph != null)
            {
                animFinishedAtoms = graph.BuildAnimFinishedAtomIndices();
                EnterState(graph.initialState, instant: true);
            }
        }

        private void Awake()
        {
            if (graph != null && currentState < 0)
            {
                Reset();
            }
        }

        /// <summary>
        /// 由外部驱动（玩家控制器/AI/demo 脚本各自填 ctx 后调用；也可挂在 Update 里自驱）。
        /// deltaTime 用调用方的时基，保证与运动模拟同步。
        /// </summary>
        public void Tick(in MechGraphContext ctx, float deltaTime)
        {
            if (graph == null || currentState < 0)
            {
                return;
            }

            stateElapsed += deltaTime;
            UpdateAnimFinished();

            Span<ulong> mask = stackalloc ulong[4];
            graph.EvaluateAtoms(in ctx, mask);
            for (var i = 0; i < animFinishedAtoms.Length; i++)
            {
                var idx = animFinishedAtoms[i];
                if (animFinished)
                {
                    mask[idx >> 6] |= 1UL << (idx & 63);
                }
            }

            for (var chain = 0; chain < maxChainedTransitionsPerTick; chain++)
            {
                var next = FindFirstSatisfied(mask);
                if (next < 0)
                {
                    return;
                }

                EnterState(next, instant: false);
            }
        }

        private int FindFirstSatisfied(Span<ulong> mask)
        {
            ref readonly var state = ref graph.states[currentState];
            var end = state.transitionStart + state.transitionCount;
            for (var t = state.transitionStart; t < end; t++)
            {
                ref readonly var transition = ref graph.transitions[t];
                if (!graph.states[transition.targetState].animAvailable)
                {
                    continue; // 无动画状态不可进入：顺延到下一候选，覆盖率上来后自动启用。
                }

                if (AllConditionsMet(in transition, mask))
                {
                    return transition.targetState;
                }
            }

            return -1;
        }

        private bool AllConditionsMet(in MechBehaviorGraphAsset.Transition transition, Span<ulong> mask)
        {
            var end = transition.conditionStart + transition.conditionCount;
            for (var c = transition.conditionStart; c < end; c++)
            {
                ref readonly var condition = ref graph.conditions[c];
                var bit = (mask[condition.atomIndex >> 6] >> (condition.atomIndex & 63) & 1UL) != 0;
                if (bit == condition.negate)
                {
                    return false;
                }
            }

            return true;
        }

        private void EnterState(int stateIndex, bool instant)
        {
            var from = currentState;
            currentState = stateIndex;
            stateElapsed = 0f;
            animFinished = false;

            history[historyCount % HistoryCapacity] = (from, stateIndex, Time.time);
            historyCount++;

            ref readonly var state = ref graph.states[stateIndex];
            if (actionPlayer != null && state.animAvailable && !string.IsNullOrEmpty(state.actionId)
                && actionPlayer.HasAction(state.actionId))
            {
                actionPlayer.PlayFullBody(state.actionId);
            }

            if (!instant)
            {
                StateChanged?.Invoke(from, stateIndex);
            }
        }

        private void UpdateAnimFinished()
        {
            if (animFinished || currentState < 0)
            {
                return;
            }

            ref readonly var state = ref graph.states[currentState];
            if (state.loop)
            {
                return;
            }

            // 无 clip 时长信息时的保守近似：非循环状态以动作播放器不再报告该动作为准；
            // TODO(IR v2)：编译期把 clip 时长写入 State，改为 stateElapsed >= duration。
            if (actionPlayer == null || !state.animAvailable
                || actionPlayer.ActiveFullBodyActionId != state.actionId)
            {
                animFinished = stateElapsed > 0.1f;
            }
        }

        /// <summary>最近 count 条转换（新→旧），验证/调试用。</summary>
        public int CopyHistory(Span<(int from, int to, float time)> buffer)
        {
            var n = Math.Min(buffer.Length, Math.Min(historyCount, HistoryCapacity));
            for (var i = 0; i < n; i++)
            {
                buffer[i] = history[(historyCount - 1 - i) % HistoryCapacity];
            }

            return n;
        }
    }
}
