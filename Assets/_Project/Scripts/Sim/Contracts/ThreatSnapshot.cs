using System;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    /// <summary>
    /// 告警级别。级别不来自辐射方的模式标志，而由告警相关器从观测到的
    /// 照射间隔统计中推断（搜索帧 ~1s / TWS 重访 ~0.24s / STT ~0.035s）。
    /// </summary>
    public enum EsmThreatLevel : byte
    {
        None = 0,
        /// <summary>被搜索波束周期性扫过。</summary>
        Spiked = 1,
        /// <summary>照射间隔达到 TWS 重访量级——对方已为你建航迹。</summary>
        Tracked = 2,
        /// <summary>连续照射（STT 驻留率）。</summary>
        Locked = 3,
        /// <summary>导引头波形辐射源——导弹主动雷达开机。</summary>
        MissileActive = 4,
    }

    /// <summary>波形库分类（数据文件 EsmWaveformLibrary.json 登记）。</summary>
    public enum EsmWaveformClass : byte
    {
        Unknown = 0,
        SearchRadar = 1,
        FireControlRadar = 2,
        MissileSeeker = 3,
        /// <summary>制导上行链路——听到即意味着"附近有弹在飞"，早于 pitbull 的软告警。</summary>
        DataLinkUplink = 4,
        /// <summary>红外羽焰点源（MAW 通道合成的伪波形）。</summary>
        InfraredPlume = 5,
    }

    public readonly struct EsmThreatId : IEquatable<EsmThreatId>
    {
        public readonly Handle Value;
        public EsmThreatId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(EsmThreatId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EsmThreatId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Threat{Value}";
    }

    /// <summary>
    /// 告警相关器对外发布的辐射源威胁。只有带噪方位、频段/波形与统计——
    /// 没有距离、没有任何真值身份，与 TrackSnapshot 同一匿名纪律。
    /// </summary>
    [Serializable]
    public struct ThreatSnapshot
    {
        public EsmThreatId ThreatId;
        /// <summary>接收机→辐射源的平滑方位单位向量（世界系）。被动测向无距离。</summary>
        public float3 DirectionWorld;
        public EsmThreatLevel Level;
        public EsmWaveformClass WaveformClass;
        public ushort FrequencyBand;
        public ushort WaveformId;
        /// <summary>观测到的重访间隔估计（秒；&lt;0 表示样本不足）。</summary>
        public float IlluminationIntervalSeconds;
        /// <summary>当前连续照射累计时长（秒）——锁定级的证据来源。</summary>
        public float ContinuousIlluminationSeconds;
        /// <summary>波形识别置信度（接收信噪比越低越难识别；低于门槛按 Unknown 处理）。</summary>
        public float IdentificationConfidence;
        public double FirstSeenTime;
        public double LastSeenTime;
        public float ReceivedPowerDb;
        /// <summary>接收功率变化率（dB/s）；持续上升的导引头辐射源=正在接近。</summary>
        public float PowerTrendDbPerSecond;
        public int ContactCount;
    }
}
