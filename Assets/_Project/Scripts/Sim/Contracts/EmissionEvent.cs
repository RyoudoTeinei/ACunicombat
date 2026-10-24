using System;
using Unity.Mathematics;

namespace ACunicombat.Sensing.Contracts
{
    public enum EmissionType : byte
    {
        RadarDwell = 0,
        Jamming = 1,
        DataLink = 2,
        Seeker = 3,
        Laser = 4,
        /// <summary>红外点源（导弹羽焰等）——MAW 通道，不经射频波形库。</summary>
        Infrared = 5,
        Iff = 6,
    }

    /// <summary>
    /// 发射事件（设计文档 9.3）：所有主动发射写入发射事件环形缓冲，ESM/告警按 Tick 查询。
    /// 旁瓣电平是显式装备参数，"RWR 只听主瓣还是含旁瓣"由数值决定，不焊死在代码里。
    /// ownerEntityId 不出现在本结构：告警侧只能拿到 EmitterId 与几何。
    /// </summary>
    [Serializable]
    public struct EmissionEvent
    {
        public EmitterId EmitterId;
        public double StartTime;
        public double EndTime;
        public double3 Origin;
        public float3 Direction;
        /// <summary>弧度，主瓣全宽。</summary>
        public float BeamWidth;
        public double MaxRangeMeters;
        public ushort FrequencyBand;
        public ushort WaveformId;
        public EmissionType EmissionType;
        public float MainlobePowerDb;
        /// <summary>相对主瓣的旁瓣电平（−30 dB 量级，装备数值）。</summary>
        public float SidelobeLevelDb;
    }

    /// <summary>告警观测（设计文档 12）：方向 + 类别 + 波形识别置信度，无距离。</summary>
    [Serializable]
    public struct WarningObservation
    {
        public SensorId ReceiverId;
        public double Timestamp;
        public float3 Direction;
        /// <summary>测向误差方差（rad²）。</summary>
        public float BearingVar;
        public ushort FrequencyBand;
        public ushort WaveformId;
        public float WaveformConfidence;
        public EmissionType EmissionType;
        /// <summary>
        /// 接收机是否位于辐射主瓣内。旁瓣接触可用于发现和波形识别，
        /// 但不能单独证明辐射源正在瞄准接收机。
        /// </summary>
        public bool IsMainlobe;
        public float ReceivedPowerDb;
    }
}
