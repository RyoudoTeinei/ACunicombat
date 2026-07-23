using System;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using Unity.Collections;

namespace ACunicombat.Simulation.World
{
    /// <summary>
    /// 实体注册表 + 世界快照双缓冲（设计文档 7）。
    /// 写缓冲：每运动 Tick 由积分器写入（主线程）；读缓冲：传感器 Job 只读。
    /// 传感器 Tick 开始时 SwapSnapshots，无锁。容量预分配，禁止自动扩容（20 节）。
    /// </summary>
    public sealed class SimWorld : IDisposable
    {
        public const int MaxEntities = 256;

        private NativeArray<KinematicState> bufferA;
        private NativeArray<KinematicState> bufferB;
        private NativeArray<byte> generations;
        private NativeArray<bool> alive;
        private bool aIsWrite;
        private int highWater;

        /// <summary>读缓冲的生成时刻；所有由该快照生成的测量以驻留时刻为时间戳（第 7 节）。</summary>
        public double SnapshotTime { get; private set; }

        public SimWorld()
        {
            bufferA = new NativeArray<KinematicState>(MaxEntities, Allocator.Persistent);
            bufferB = new NativeArray<KinematicState>(MaxEntities, Allocator.Persistent);
            generations = new NativeArray<byte>(MaxEntities, Allocator.Persistent);
            alive = new NativeArray<bool>(MaxEntities, Allocator.Persistent);
            aIsWrite = true;
            // Generation 从 1 起：Handle.Packed == 0 保留为无效值。
            for (var i = 0; i < MaxEntities; i++)
            {
                generations[i] = 1;
            }
        }

        /// <summary>积分器写入目标（主线程 only）。</summary>
        public NativeArray<KinematicState> WriteBuffer => aIsWrite ? bufferA : bufferB;

        /// <summary>传感器 Job 只读快照。</summary>
        public NativeArray<KinematicState> ReadSnapshot => aIsWrite ? bufferB : bufferA;

        /// <summary>存活掩码（只读消费）：空间索引重建必须据此过滤，已注销实体不得进入候选。</summary>
        public NativeArray<bool> AliveMask => alive;

        public int EntityCapacityInUse => highWater;

        public SimEntityId Register(in KinematicState initial)
        {
            for (var i = 0; i < MaxEntities; i++)
            {
                if (!alive[i])
                {
                    alive[i] = true;
                    var buf = WriteBuffer;
                    buf[i] = initial;
                    if (i >= highWater)
                    {
                        highWater = i + 1;
                    }

                    return new SimEntityId(new Handle(i, generations[i]));
                }
            }

            throw new InvalidOperationException($"SimWorld capacity {MaxEntities} exhausted (20 节：禁止自动扩容).");
        }

        public void Unregister(SimEntityId id)
        {
            if (!IsAlive(id))
            {
                return;
            }

            var index = id.Value.Index;
            alive[index] = false;
            // 注销递增 Generation，跳过 0 使 Packed==0 永远无效。
            var next = (byte)(generations[index] + 1);
            generations[index] = next == 0 ? (byte)1 : next;
        }

        public bool IsAlive(SimEntityId id)
        {
            var index = id.Value.Index;
            return id.IsValid && index < MaxEntities && alive[index]
                   && generations[index] == id.Value.Generation;
        }

        public bool TrySetState(SimEntityId id, in KinematicState state)
        {
            if (!IsAlive(id))
            {
                return false;
            }

            var buffer = WriteBuffer;
            buffer[id.Value.Index] = state;
            return true;
        }

        public bool TryGetWriteState(SimEntityId id, out KinematicState state)
        {
            if (!IsAlive(id))
            {
                state = default;
                return false;
            }

            state = WriteBuffer[id.Value.Index];
            return true;
        }

        public bool TryGetReadState(SimEntityId id, out KinematicState state)
        {
            if (!IsAlive(id))
            {
                state = default;
                return false;
            }

            state = ReadSnapshot[id.Value.Index];
            return true;
        }

        public SimEntityId GetEntityIdAt(int index)
        {
            if ((uint)index >= MaxEntities || !alive[index])
            {
                return default;
            }

            return new SimEntityId(new Handle(index, generations[index]));
        }

        /// <summary>传感器 Tick 开始时调用（主线程，所有 Job 已 Complete）。</summary>
        public void SwapSnapshots(double simTime)
        {
            aIsWrite = !aIsWrite;
            SnapshotTime = simTime;
            // 新写缓冲以旧状态为基（积分器每 Tick 全量覆写活跃实体，此拷贝保证未覆写槽位一致）。
            ReadSnapshot.CopyTo(WriteBuffer);
        }

        public void Dispose()
        {
            if (bufferA.IsCreated)
            {
                bufferA.Dispose();
                bufferB.Dispose();
                generations.Dispose();
                alive.Dispose();
            }
        }
    }
}
