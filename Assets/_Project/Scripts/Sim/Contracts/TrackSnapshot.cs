using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    public enum TrackLifecycle : byte
    {
        Tentative = 0,
        Confirmed = 1,
        Coasting = 2,
        Degraded = 3,
        Dropped = 4,
    }

    [Flags]
    public enum TrackSourceMask : uint
    {
        None = 0,
        Radar = 1 << 0,
        ElectroOptical = 1 << 1,
        Esm = 1 << 2,
        DataLink = 1 << 3,
    }

    /// <summary>
    /// 航迹快照（设计文档 6.2 Track 的对外只读形态）：火控/AI/UI/数据链消费的唯一状态。
    /// 协方差为 6×6 对称阵上三角 21 个 double，索引见 <see cref="CovarianceIndex"/>。
    /// </summary>
    [Serializable]
    public unsafe struct TrackSnapshot
    {
        public TrackId TrackId;
        public PlatformId OwnerPlatformId;
        public double3 Position;
        public double3 Velocity;
        public fixed double CovarianceUpperTriangular[21];
        public float Confidence;
        public float Classification;
        public float IdentityConfidence;
        public double LastUpdateTime;
        public double LastPredictTime;
        public int CoastCount;
        public int HitCount;
        public TrackSourceMask SourceMask;
        public float ConsistencyScore;
        public float InterferenceScore;
        public TrackLifecycle Lifecycle;

        /// <summary>6×6 对称阵上三角行主序索引：i ≤ j，行 i 的起点 = 6i − i(i−1)/2。</summary>
        public static int CovarianceIndex(int i, int j)
        {
            if (i > j)
            {
                (i, j) = (j, i);
            }

            return 6 * i - i * (i - 1) / 2 + (j - i);
        }
    }
}
