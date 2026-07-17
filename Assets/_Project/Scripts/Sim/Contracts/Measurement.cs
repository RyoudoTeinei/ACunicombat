using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    /// <summary>
    /// 传感器能力掩码（设计文档 10.6 字段有效性规则）：合并判据与加权只在传感器声明的
    /// 维度上进行；EW 合成点注入时必须与受害传感器的掩码一致，否则在注入口拒收。
    /// 角度测量始终存在，不进掩码。
    /// </summary>
    [Flags]
    public enum SensorCapability : uint
    {
        None = 0,
        Range = 1 << 0,
        Doppler = 1 << 1,
        LosRate = 1 << 2,
    }

    /// <summary>可测状态位（6.2）。禁止出现"是诱饵/合成"等真值标签。</summary>
    [Flags]
    public enum MeasurementFlags : uint
    {
        None = 0,
        Saturated = 1 << 0,
        SuspectedClutter = 1 << 1,
        Jammed = 1 << 2,
        Passive = 1 << 3,
    }

    /// <summary>
    /// 检测点（设计文档 6.2）。blittable，NativeArray 存储。能力外字段填 NaN。
    /// 不含真实目标 ID、阵营等真值字段——SimEntityId 对本程序集类型不可见。
    /// </summary>
    [Serializable]
    public struct Measurement
    {
        public SensorId SensorId;
        /// <summary>驻留时刻（由生成快照推得，不是消费帧时刻，见第 7 节）。</summary>
        public double Timestamp;
        /// <summary>米；无测距能力为 NaN。</summary>
        public float Range;
        /// <summary>世界系单位向量。</summary>
        public float3 Direction;
        /// <summary>米/秒；无多普勒能力为 NaN。</summary>
        public float RadialVelocity;
        /// <summary>视线角速率向量 Ω（世界系，rad/s）；无此能力为 NaN。</summary>
        public float3 LosRate;
        public float RangeVar;
        public float AngleVar;
        public float VelocityVar;
        public float LosRateVar;
        public float SnrDb;
        public MeasurementFlags Flags;
    }
}
