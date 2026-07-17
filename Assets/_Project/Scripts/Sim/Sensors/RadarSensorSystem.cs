using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.World;
using Unity.Collections;

namespace ACunicombat.Sensors
{
    /// <summary>
    /// 雷达传感器系统（设计文档 10.1 单驻留流程 + 19.1 Job 管线）的编排骨架。
    /// 本类型是唯一可读 World 真值的消费层；产出只有 Contracts 结构体。
    ///
    /// 每传感器 Tick（19.1 次序，逐步实现）：
    ///   1. Complete 上一批 JobHandle（存于 pendingJobs，不在 Schedule 后立即 Complete）
    ///   2. 检测点交付融合层（DetectionRing 由 Driver 转交 Tracking）
    ///   3. world.SwapSnapshots + grid.Rebuild
    ///   4. scheduler.TryConsumeDue 收集到期驻留（主线程）
    ///   5. 候选筛选 Job：grid.QueryBeamBroadphase → 距离筛 → 波束点积筛
    ///   6. 解析遮挡 Job（AnalyticOcclusion）∥ 气泡内 RaycastCommand 批次
    ///   7. 回波/SNR/判决/测量生成 Job（RadarMath；消费 EW 预先写入的 JamContribution/
    ///      SyntheticDetection 缓冲——Job 内禁止托管委托，无回调路径）
    ///   8. ResolutionMerge.MergeInPlace
    ///   9. 检测点入环形缓冲，EmissionEvent 登记
    ///
    /// TODO(阶段 1)：5–7 的 IJob 实现与 JobHandle 链。数学核（RadarMath、
    /// ResolutionMerge、AnalyticOcclusion）已完成并具备单测，Job 只做搬运与并行化。
    /// </summary>
    public sealed class RadarSensorSystem : IDisposable
    {
        public const int DetectionRingCapacity = 4096;
        public const int CandidateCapacity = 2048;

        private readonly SimWorld world;
        private readonly SpatialHashGrid grid;
        private readonly DwellScheduler scheduler;

        private NativeList<Measurement> detectionRing;
        private NativeList<EmissionEvent> emissionRing;
        private NativeArray<Measurement> dwellScratch;
        private NativeArray<int> mergeScratch;

        /// <summary>EW 在自身更新阶段写入、本系统只读的注入缓冲（5 节规则红线）。</summary>
        private NativeList<JamContribution> jamInbox;
        private NativeList<SyntheticDetection> syntheticInbox;

        public uint SessionSeed;

        public RadarSensorSystem(SimWorld world, SpatialHashGrid grid, DwellScheduler scheduler)
        {
            this.world = world;
            this.grid = grid;
            this.scheduler = scheduler;
            detectionRing = new NativeList<Measurement>(DetectionRingCapacity, Allocator.Persistent);
            emissionRing = new NativeList<EmissionEvent>(1024, Allocator.Persistent);
            dwellScratch = new NativeArray<Measurement>(256, Allocator.Persistent);
            mergeScratch = new NativeArray<int>(256, Allocator.Persistent);
            jamInbox = new NativeList<JamContribution>(256, Allocator.Persistent);
            syntheticInbox = new NativeList<SyntheticDetection>(256, Allocator.Persistent);
        }

        /// <summary>EW 系统获取写入口（Driver 装配时注入，运行期只有 EW 写）。</summary>
        public NativeList<JamContribution> JamInboxForEw => jamInbox;

        public NativeList<SyntheticDetection> SyntheticInboxForEw => syntheticInbox;

        /// <summary>本 Tick 产出的检测点（融合层消费后 Clear）。</summary>
        public NativeList<Measurement> Detections => detectionRing;

        public NativeList<EmissionEvent> Emissions => emissionRing;

        public void Tick(double simTime)
        {
            world.SwapSnapshots(simTime);
            grid.Rebuild(world.ReadSnapshot, world.EntityCapacityInUse);

            while (scheduler.TryConsumeDue(simTime, out var dwell))
            {
                ProcessDwell(in dwell);
            }
        }

        private void ProcessDwell(in Dwell dwell)
        {
            // TODO(阶段 1)：候选筛选/遮挡/回波三个 Job 的调度与 JobHandle 链（19.1 步骤 5–7）。
            // 单驻留数学路径（主线程参考实现，Job 化前用于 EditMode 验收）：
            //   grid.QueryBeamBroadphase → 点积筛 → AnalyticOcclusion →
            //   RadarMath.ReceivedPowerDb/SnrDb(+jamInbox 汇总) → RollDetection(种子=21 节哈希链)
            //   → 误差模型出 Measurement → syntheticInbox 中匹配本驻留的合成点追加
            //   → ResolutionMerge.MergeInPlace(dwellScratch, n, capability, res, mergeScratch)
            //   → detectionRing.AddRange + emissionRing 登记
            throw new NotImplementedException("阶段 1 垂直切片实现点——见类注释与设计文档 10.1/19.1。");
        }

        public void Dispose()
        {
            detectionRing.Dispose();
            emissionRing.Dispose();
            dwellScratch.Dispose();
            mergeScratch.Dispose();
            jamInbox.Dispose();
            syntheticInbox.Dispose();
        }
    }
}
