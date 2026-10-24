using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>驻留优先级（设计文档 4.2）：数值越小越优先。</summary>
    public enum DwellPriority : byte
    {
        MissileTerminal = 0,
        SingleTargetTrack = 1,
        MissileMidcourse = 2,
        AlertConfirm = 3,
        TrackWhileScan = 4,
        WideSearch = 5,
    }

    /// <summary>单个待执行驻留。blittable。</summary>
    public struct Dwell
    {
        public SensorId SensorId;
        public double DueTime;
        public float DwellTimeSeconds;
        public float3 BeamDirection;
        public float BeamWidthRadians;
        public DwellPriority Priority;
        /// <summary>传感器内单调递增的驻留序号——RNG 种子键与测量时间戳的基（21 节）。</summary>
        public ulong Sequence;
    }

    /// <summary>
    /// 驻留调度器（设计文档 4.2）。语义分两层：
    /// 1. 到期检测：按 DueTime 最小堆——未到期的驻留永远不会被消费；
    /// 2. 到期集合内：按 (Priority, DueTime, Sequence) 排序消费，预算封顶——
    ///    超预算的低优先级驻留**回队顺延**（DueTime 不变，航迹年龄如实增加，不伪造刷新）。
    /// 过载时导弹末制导恒压过更早到期的广域搜索，这正是优先级存在的意义。
    /// 全路径确定：同输入同弹出顺序。容量预分配，禁止自动扩容（20 节）。
    /// </summary>
    public sealed class DwellScheduler : IDisposable
    {
        private NativeList<Dwell> heap;
        private NativeList<Dwell> dueScratch;

        public DwellScheduler(int capacity)
        {
            heap = new NativeList<Dwell>(capacity, Allocator.Persistent);
            dueScratch = new NativeList<Dwell>(capacity, Allocator.Persistent);
        }

        public int Count => heap.Length;

        /// <summary>因预算不足被顺延的驻留次数累计（性能/过载监控指标）。</summary>
        public long DeferredCount { get; private set; }

        public void Schedule(in Dwell dwell)
        {
            if (heap.Length == heap.Capacity)
            {
                throw new InvalidOperationException("DwellScheduler capacity exhausted (20 节：禁止自动扩容).");
            }

            heap.Add(dwell);
            SiftUp(heap.Length - 1);
        }

        /// <summary>Bounded physical-device queue. Higher priority may displace queued lower priority work.</summary>
        public bool TryScheduleBounded(in Dwell dwell, int maximumPerSensor, out bool displaced)
        {
            displaced=false;int count=0,worst=-1;
            for(int i=0;i<heap.Length;i++)if(heap[i].SensorId.Equals(dwell.SensorId))
            {
                count++;
                if(worst<0 || PriorityAfter(heap[i],heap[worst]))worst=i;
            }
            if(count>=maximumPerSensor)
            {
                if(worst<0 || dwell.Priority>=heap[worst].Priority)return false;
                heap[worst]=dwell;
                // Rebuild after replacement without allocating or changing the global queue capacity.
                for(int i=heap.Length/2-1;i>=0;i--)SiftDown(i);
                displaced=true;return true;
            }
            if(heap.Length==heap.Capacity)return false;
            Schedule(dwell);return true;
        }

        /// <summary>
        /// 消费本 Tick 的到期驻留：最多 budget 个，按优先级序写入 results（调用方先 Clear）。
        /// 超预算的到期驻留回队顺延。返回实际消费数。
        /// </summary>
        public int ConsumeDue(double simTime, int budget, NativeList<Dwell> results)
        {
            dueScratch.Clear();
            while (heap.Length > 0 && SimulationTime.IsDue(simTime,heap[0].DueTime))
            {
                dueScratch.Add(heap[0]);
                PopRoot();
            }

            // 到期集合内按 (Priority, DueTime, Sequence) 插入排序——n 为单 Tick 到期数，很小。
            for (var i = 1; i < dueScratch.Length; i++)
            {
                var key = dueScratch[i];
                var j = i - 1;
                while (j >= 0 && PriorityAfter(dueScratch[j], key))
                {
                    dueScratch[j + 1] = dueScratch[j];
                    j--;
                }

                dueScratch[j + 1] = key;
            }

            var consumed = math.min(budget, dueScratch.Length);
            for (var i = 0; i < consumed; i++)
            {
                results.Add(dueScratch[i]);
            }

            for (var i = consumed; i < dueScratch.Length; i++)
            {
                Schedule(dueScratch[i]);
                DeferredCount++;
            }

            return consumed;
        }

        /// <summary>a 是否应排在 key 之后（优先级序：Priority 小者先，同则 DueTime 早者先）。</summary>
        private static bool PriorityAfter(in Dwell a, in Dwell key)
        {
            if (a.Priority != key.Priority)
            {
                return a.Priority > key.Priority;
            }

            if (a.DueTime != key.DueTime)
            {
                return a.DueTime > key.DueTime;
            }

            return a.Sequence > key.Sequence;
        }

        private static bool Before(in Dwell a, in Dwell b)
        {
            if (a.DueTime != b.DueTime)
            {
                return a.DueTime < b.DueTime;
            }

            if (a.Priority != b.Priority)
            {
                return a.Priority < b.Priority;
            }

            return a.Sequence < b.Sequence;
        }

        private void PopRoot()
        {
            heap[0] = heap[heap.Length - 1];
            heap.RemoveAt(heap.Length - 1);
            if (heap.Length > 0)
            {
                SiftDown(0);
            }
        }

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (!Before(heap[i], heap[parent]))
                {
                    return;
                }

                (heap[i], heap[parent]) = (heap[parent], heap[i]);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            while (true)
            {
                var best = i;
                var l = 2 * i + 1;
                var r = 2 * i + 2;
                if (l < heap.Length && Before(heap[l], heap[best]))
                {
                    best = l;
                }

                if (r < heap.Length && Before(heap[r], heap[best]))
                {
                    best = r;
                }

                if (best == i)
                {
                    return;
                }

                (heap[i], heap[best]) = (heap[best], heap[i]);
                i = best;
            }
        }

        public void Dispose()
        {
            if (heap.IsCreated)
            {
                heap.Dispose();
                dueScratch.Dispose();
            }
        }
    }
}
