namespace ACunicombat.Simulation.Core
{
    /// <summary>
    /// 固定步长时钟（设计文档 4.1）：SimulationDriver 累加真实时间，while 消费整步；
    /// 每帧最大补算数封顶，超出记性能告警计数，不静默降频。
    /// </summary>
    public struct FixedTickClock
    {
        public readonly double StepSeconds;
        public readonly int MaxCatchUpTicks;

        private double accumulator;

        /// <summary>已执行的总 Tick 数；simTime = TickCount * StepSeconds。</summary>
        public long TickCount { get; private set; }

        /// <summary>因超过补算上限被丢弃的 Tick 数（性能告警指标）。</summary>
        public long OverrunTicks { get; private set; }

        public double SimTimeSeconds => TickCount * StepSeconds;

        public FixedTickClock(double stepSeconds, int maxCatchUpTicks)
        {
            StepSeconds = stepSeconds;
            MaxCatchUpTicks = maxCatchUpTicks;
            accumulator = 0;
            TickCount = 0;
            OverrunTicks = 0;
        }

        /// <summary>推进真实时间，返回本帧应执行的 Tick 数（已封顶）。</summary>
        public int Advance(double realDeltaSeconds)
        {
            accumulator += realDeltaSeconds;
            var tolerance=System.Math.Min(SimulationTime.DeadlineToleranceSeconds,StepSeconds*1e-4);
            var due = (int)((accumulator+tolerance) / StepSeconds);
            var run = due <= MaxCatchUpTicks ? due : MaxCatchUpTicks;
            OverrunTicks += due - run;
            accumulator = System.Math.Max(0,accumulator-due*StepSeconds);
            TickCount += run;
            return run;
        }
    }
}
