using System;
using ACunicombat.Sensing.Contracts;
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
    /// 驻留调度器（设计文档 4.2）：到期队列 = (DueTime, Priority, Sequence) 三键最小堆，
    /// 保证同输入同弹出顺序（确定性）。超预算时低优先级驻留顺延——调用方少弹几个即可，
    /// 航迹年龄如实增加，不伪造刷新。容量预分配，禁止自动扩容（20 节）。
    /// </summary>
    public sealed class DwellScheduler : IDisposable
    {
        private NativeList<Dwell> heap;

        public DwellScheduler(int capacity)
        {
            heap = new NativeList<Dwell>(capacity, Allocator.Persistent);
        }

        public int Count => heap.Length;

        public void Schedule(in Dwell dwell)
        {
            if (heap.Length == heap.Capacity)
            {
                throw new InvalidOperationException("DwellScheduler capacity exhausted (20 节：禁止自动扩容).");
            }

            heap.Add(dwell);
            SiftUp(heap.Length - 1);
        }

        /// <summary>弹出一个已到期驻留；无到期驻留返回 false。一个传感器 Tick 可弹 0~k 个。</summary>
        public bool TryConsumeDue(double simTime, out Dwell dwell)
        {
            if (heap.Length == 0 || heap[0].DueTime > simTime)
            {
                dwell = default;
                return false;
            }

            dwell = heap[0];
            heap[0] = heap[heap.Length - 1];
            heap.RemoveAt(heap.Length - 1);
            if (heap.Length > 0)
            {
                SiftDown(0);
            }

            return true;
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
            }
        }
    }
}
