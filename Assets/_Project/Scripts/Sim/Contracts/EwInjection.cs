using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    /// <summary>
    /// 噪声干扰功率贡献（设计文档 13）：EW 在自身更新阶段写入，Sensors 在雷达方程分母读取。
    /// EW 写、Sensors 读，无回调（5 节规则红线；Job 内禁止托管委托）。
    /// </summary>
    [Serializable]
    public struct JamContribution
    {
        public SensorId VictimSensorId;
        public EmitterId SourceEmitterId;
        /// <summary>干扰源方向（世界系单位向量），用于波束/旁瓣耦合计算。</summary>
        public float3 ArrivalDirection;
        public ushort FrequencyBand;
        /// <summary>到达受害接收机的干扰功率（dB，已含传播衰减）。</summary>
        public float PowerDb;
    }

    /// <summary>
    /// EW 合成假目标（设计文档 13 + 10.6 字段有效性规则）：检测判决前注入，
    /// 格式与真实检测点完全一致、无真值标签。注入口验证：Measurement 的字段有效性
    /// 必须与受害传感器能力掩码一致，不合规拒收并计数。
    /// </summary>
    [Serializable]
    public struct SyntheticDetection
    {
        public SensorId VictimSensorId;
        /// <summary>受害传感器的驻留序号——RNG 种子键与时序对齐用（21 节）。</summary>
        public ulong DwellSequence;
        public Measurement Measurement;
    }
}
