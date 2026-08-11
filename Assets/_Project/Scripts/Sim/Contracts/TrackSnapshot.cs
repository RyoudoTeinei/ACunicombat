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

    public enum TrackAffiliation : byte
    {
        Unknown = 0,
        Friendly = 1,
        Hostile = 2,
    }

    public enum TrackObjectClass : byte
    {
        Unknown = 0,
        AirVehicle = 1,
        Missile = 2,
    }

    public enum MissileMissionRole : byte
    {
        Unknown = 0,
        AirToAir = 1,
        Interceptor = 2,
    }

    [Flags]
    public enum TrackSourceMask : uint
    {
        None = 0,
        Radar = 1 << 0,
        ElectroOptical = 1 << 1,
        Esm = 1 << 2,
        DataLink = 1 << 3,
        LaserRangefinder = 1 << 4,
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
        public TrackObjectClass ObjectClass;
        public MissileMissionRole MissileRole;
        public float MissileRoleConfidence;
        public float Classification;
        public float IdentityConfidence;
        public TrackAffiliation Affiliation;
        public double LastUpdateTime;
        public double LastPredictTime;
        public int CoastCount;
        public int HitCount;
        public TrackSourceMask SourceMask;
        public float ConsistencyScore;
        public float InterferenceScore;
        public TrackLifecycle Lifecycle;

        public double3 PredictPosition(double time)
        {
            var propagationSeconds = math.max(
                0.0,
                time - LastPredictTime);
            return Position + Velocity * propagationSeconds;
        }

        /// <summary>6×6 对称阵上三角行主序索引：i ≤ j，行 i 的起点 = 6i − i(i−1)/2。</summary>
        public static int CovarianceIndex(int i, int j)
        {
            if (i > j)
            {
                (i, j) = (j, i);
            }

            return 6 * i - i * (i - 1) / 2 + (j - i);
        }

        public double GetCovariance(int i, int j)
        {
            var index = CovarianceIndex(i, j);
            fixed (double* values = CovarianceUpperTriangular)
            {
                return values[index];
            }
        }

        public void SetCovariance(int i, int j, double value)
        {
            var index = CovarianceIndex(i, j);
            fixed (double* values = CovarianceUpperTriangular)
            {
                values[index] = value;
            }
        }
    }
}
