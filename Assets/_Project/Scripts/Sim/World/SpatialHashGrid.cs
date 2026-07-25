using System;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Simulation.World
{
    /// <summary>
    /// 全域稀疏哈希网格（设计文档 8.1）：双精度坐标，单元 10 km，实体注册 1~2 格。
    /// 波束查询 = 波束锥包围体遍历相交单元 → 距离筛 → 波束点积筛（点积筛在 Sensors 层 Job 里做）。
    /// </summary>
    public sealed class SpatialHashGrid : IDisposable
    {
        public const double CellSizeMeters = 10_000.0;

        private NativeParallelMultiHashMap<int3, int> cells;

        public SpatialHashGrid(int capacity)
        {
            cells = new NativeParallelMultiHashMap<int3, int>(capacity, Allocator.Persistent);
        }

        public static int3 CellOf(double3 positionWorld) =>
            (int3)math.floor(positionWorld / CellSizeMeters);

        /// <summary>
        /// 每传感器 Tick 增量重建（首版全量重建，实体数百级开销可忽略）。
        /// 必须按存活掩码过滤：已注销实体的槽位数据仍留在快照里，不过滤就会被继续探测。
        /// </summary>
        public void Rebuild(
            NativeArray<Kinematics.KinematicState> snapshot, NativeArray<bool> aliveMask, int count)
        {
            cells.Clear();
            for (var i = 0; i < count; i++)
            {
                if (aliveMask[i])
                {
                    cells.Add(CellOf(snapshot[i].PositionWorld), i);
                }
            }
        }

        /// <summary>
        /// 收集波束锥包围体触及的候选实体索引（粗筛，保守：锥 ⊂ conv(顶点, 端面圆盘)
        /// ⊂ 此 AABB）。原退化版按最大探测距离全域 AABB 遍历（100 km/10 km 单元 =
        /// 21³≈9261 次查询/驻留）；锥体 AABB 在窄波束下小 1~2 个数量级。
        /// 波束方向点积精筛仍在 Sensors 层做。
        /// </summary>
        public void QueryBeamBroadphase(
            double3 origin, float3 direction, double maxRange, float halfAngleRadians,
            NativeList<int> results)
        {
            var axis = math.normalizesafe((double3)direction, new double3(0.0, 0.0, 1.0));
            var endCenter = origin + axis * maxRange;
            var endRadius = maxRange * math.tan(math.clamp(halfAngleRadians, 0f, math.PI * 0.49f));
            var minCell = CellOf(math.min(origin, endCenter - endRadius));
            var maxCell = CellOf(math.max(origin, endCenter + endRadius));
            for (var x = minCell.x; x <= maxCell.x; x++)
            {
                for (var y = minCell.y; y <= maxCell.y; y++)
                {
                    for (var z = minCell.z; z <= maxCell.z; z++)
                    {
                        if (cells.TryGetFirstValue(new int3(x, y, z), out var entity, out var it))
                        {
                            do
                            {
                                results.Add(entity);
                            }
                            while (cells.TryGetNextValue(out entity, ref it));
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            if (cells.IsCreated)
            {
                cells.Dispose();
            }
        }
    }
}
