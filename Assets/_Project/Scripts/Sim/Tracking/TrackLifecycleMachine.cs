using ACunicombat.Sensing.Contracts;

namespace ACunicombat.Tracking
{
    /// <summary>
    /// 航迹生命周期状态机（设计文档 14.5）：
    /// Tentative →(M/N)→ Confirmed →(连续丢失)→ Coasting →(协方差超限)→ Degraded → Dropped。
    /// Dropped 后墓碑保留（TrackId + 末状态 ~5 s）由航迹管理器持有，不在本状态机内。
    /// </summary>
    public struct TrackLifecycleMachine
    {
        public TrackLifecycle State;
        public int HitCount;
        public int MissCount;
        /// <summary>M/N 确认窗口内的命中位图（低 N 位滑动）。</summary>
        public uint HitWindow;

        public int ConfirmM;
        public int ConfirmN;
        public int CoastAfterMisses;

        public static TrackLifecycleMachine Create(int confirmM = 3, int confirmN = 5, int coastAfterMisses = 3)
        {
            return new TrackLifecycleMachine
            {
                State = TrackLifecycle.Tentative,
                ConfirmM = confirmM,
                ConfirmN = confirmN,
                CoastAfterMisses = coastAfterMisses,
            };
        }

        /// <summary>驻留有关联命中。</summary>
        public void OnHit()
        {
            HitCount++;
            MissCount = 0;
            HitWindow = ((HitWindow << 1) | 1u) & ((1u << ConfirmN) - 1);

            switch (State)
            {
                case TrackLifecycle.Tentative when CountBits(HitWindow) >= ConfirmM:
                    State = TrackLifecycle.Confirmed;
                    break;
                case TrackLifecycle.Coasting:
                case TrackLifecycle.Degraded:
                    State = TrackLifecycle.Confirmed;
                    break;
            }
        }

        /// <summary>应有跟踪驻留但未命中（顺延的驻留不算 miss——航迹年龄如实增加即可）。</summary>
        public void OnMiss()
        {
            MissCount++;
            HitWindow = (HitWindow << 1) & ((1u << ConfirmN) - 1);

            if (State == TrackLifecycle.Tentative && CountBits(HitWindow) == 0 && MissCount >= ConfirmN)
            {
                State = TrackLifecycle.Dropped;
            }
            else if (State == TrackLifecycle.Confirmed && MissCount >= CoastAfterMisses)
            {
                State = TrackLifecycle.Coasting;
            }
        }

        /// <summary>Coasting 中每 Tick 用协方差判断退化/丢弃（14.5：协方差超限 → Degraded）。</summary>
        public void OnCoastCovariance(double positionVarianceTrace, double degradeLimit, double dropLimit)
        {
            if (State != TrackLifecycle.Coasting && State != TrackLifecycle.Degraded)
            {
                return;
            }

            if (positionVarianceTrace > dropLimit)
            {
                State = TrackLifecycle.Dropped;
            }
            else if (positionVarianceTrace > degradeLimit)
            {
                State = TrackLifecycle.Degraded;
            }
        }

        private static int CountBits(uint v)
        {
            var count = 0;
            while (v != 0)
            {
                count += (int)(v & 1);
                v >>= 1;
            }

            return count;
        }
    }
}
