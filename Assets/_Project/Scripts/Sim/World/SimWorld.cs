using System;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Simulation.World
{
    /// <summary>
    /// 实体注册表 + 世界快照双缓冲（设计文档 7）。
    /// 写缓冲：每运动 Tick 由积分器写入（主线程）；读缓冲：传感器 Job 只读。
    /// 传感器 Tick 开始时 SwapSnapshots，无锁。容量预分配，禁止自动扩容（20 节）。
    /// </summary>
    public sealed class SimWorld : IDisposable
    {
        public const int MaxEntities = 512;

        private NativeArray<KinematicState> bufferA;
        private NativeArray<KinematicState> bufferB;
        private NativeArray<byte> generations;
        private NativeArray<bool> alive;
        private bool aIsWrite;
        private int highWater;
        private readonly double[] sampledAt=new double[MaxEntities];
        private readonly bool[] timedStates=new bool[MaxEntities];
        private readonly KinematicState[] previousSamples=new KinematicState[MaxEntities];
        private readonly double[] previousSampleTimes=new double[MaxEntities];
        private readonly bool[] hasPreviousSample=new bool[MaxEntities];

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
                    alive[i] = true;timedStates[i]=false;hasPreviousSample[i]=false;
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
            if (!alive.IsCreated || !generations.IsCreated)
            {
                return false;
            }

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
            buffer[id.Value.Index] = state;timedStates[id.Value.Index]=false;hasPreviousSample[id.Value.Index]=false;
            return true;
        }

        // External physics poses retain their actual sampling epoch. Pure simulation states remain untimed.
        public bool TrySetTimedState(SimEntityId id,in KinematicState state,double time)
        {
            if(!double.IsFinite(time)||!IsAlive(id))return false;
            int index=id.Value.Index;var buffer=WriteBuffer;
            if(timedStates[index])
            {
                if(time+SimulationTime.DeadlineToleranceSeconds<sampledAt[index])return false;
                if(time>sampledAt[index]+SimulationTime.DeadlineToleranceSeconds)
                {
                    previousSamples[index]=buffer[index];previousSampleTimes[index]=sampledAt[index];hasPreviousSample[index]=true;
                }
                else time=sampledAt[index];
            }
            buffer[index]=state;sampledAt[index]=time;timedStates[index]=true;return true;
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
            // Preserve the raw pose in the write buffer; project only the read snapshot to its declared epoch.
            var read=ReadSnapshot;
            for(int i=0;i<highWater;i++)if(alive[i]&&timedStates[i])
            {read[i]=SampleAt(i,read[i],simTime);}
        }

        private KinematicState SampleAt(int index,KinematicState latest,double time)
        {
            double age=time-sampledAt[index];
            if(!hasPreviousSample[index])
            {latest.PositionWorld+=latest.VelocityWorld*age;return latest;}
            var previous=previousSamples[index];double span=sampledAt[index]-previousSampleTimes[index];
            if(span<=SimulationTime.DeadlineToleranceSeconds)
            {latest.PositionWorld+=latest.VelocityWorld*age;return latest;}
            if(time>=previousSampleTimes[index]&&time<=sampledAt[index])
            {
                // Rendering and physics can publish on different clocks. Hermite
                // resampling preserves both measured positions and velocities and
                // prevents a future render pose being labelled as a past sensor pose.
                double u=(time-previousSampleTimes[index])/span,u2=u*u,u3=u2*u;
                var position=(2*u3-3*u2+1)*previous.PositionWorld+(u3-2*u2+u)*span*previous.VelocityWorld
                    +(-2*u3+3*u2)*latest.PositionWorld+(u3-u2)*span*latest.VelocityWorld;
                var velocity=((6*u2-6*u)*previous.PositionWorld+(-6*u2+6*u)*latest.PositionWorld)/span
                    +(3*u2-4*u+1)*previous.VelocityWorld+(3*u2-2*u)*latest.VelocityWorld;
                if(math.lengthsq(previous.Rotation.value)>0&&math.lengthsq(latest.Rotation.value)>0)
                    latest.Rotation=math.slerp(previous.Rotation,latest.Rotation,(float)u);
                latest.PositionWorld=position;latest.VelocityWorld=velocity;return latest;
            }
            var acceleration=(latest.VelocityWorld-previous.VelocityWorld)/span;
            if(time<previousSampleTimes[index]){latest=previous;age=time-previousSampleTimes[index];}
            latest.PositionWorld+=latest.VelocityWorld*age+.5*acceleration*age*age;
            latest.VelocityWorld+=acceleration*age;
            return latest;
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
