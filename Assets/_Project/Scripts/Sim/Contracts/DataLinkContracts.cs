using System;
using ACunicombat.Simulation.Core;

namespace ACunicombat.Sensing.Contracts
{
    /// <summary>
    /// 数据链终端 Id（Handle 世代号防悬挂，同 SensorId/TrackId 纪律）。
    /// 终端 = 平台上的一台电台：机体、无人机、僚机、导弹、拦截弹各挂各的。
    /// </summary>
    public readonly struct LinkTerminalId : IEquatable<LinkTerminalId>
    {
        public readonly Handle Value;
        public LinkTerminalId(Handle value) => Value = value;
        public bool IsValid => Value.IsValid;
        public uint Packed => Value.Packed;
        public bool Equals(LinkTerminalId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is LinkTerminalId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Link{Value}";
    }

    public enum LinkMessageKind : byte
    {
        /// <summary>在网心跳（网络成员存活性）。</summary>
        Heartbeat = 0,
        /// <summary>航迹共享（僚机/无人机/拦截系统的态势分发）。</summary>
        TrackShare = 1,
        /// <summary>导弹中段制导修正上行。</summary>
        GuidanceUpdate = 2,
        /// <summary>指令（载荷为 CommandCode，语义由收发双方约定）。</summary>
        Command = 3,
        CombatBehaviorReport = 4,
        FleetCoordination = 5,
        TargetObservation = 6,
        /// <summary>发送方自身雷达或电子战辐射的几何自报，用于协同态势显示。</summary>
        EmissionReport = 7,
    }

    [Serializable]
    public struct EmissionReport
    {
        public EmissionEvent Emission;
        public double ObservedAt;
        public double ExpiresAt;
    }

    /// <summary>
    /// 数据链报文。发送方填 Kind/网络/寻址/载荷，系统在投递时补
    /// Sequence/DeliveryTime/余量。消息经物理链路（辐射事件 + 单向传播 + 灵敏度
    /// 判决）投递——可被侦听（ESM 听到 DataLink 辐射）、可因距离/遮挡/干扰丢包。
    /// 消费端用 Sequence 去重（投递箱按传感器 Tick 清空，轮询频率可能更高）。
    /// </summary>
    [Serializable]
    public struct LinkMessage
    {
        // Identity attested by the authenticated sender, never an unscoped scene/entity id.
        public Guid AuthenticatedTrackSubject;
        /// <summary>系统分配的全局单调序号（投递时有效）。</summary>
        public ulong Sequence;
        public LinkMessageKind Kind;
        public byte NetworkId;
        public LinkTerminalId SourceTerminal;
        /// <summary>无效 = 同网络组播；有效 = 单播。</summary>
        public LinkTerminalId TargetTerminal;
        public double SendTime;
        public double DeliveryTime;
        /// <summary>接收端链路余量（dB）。干扰接入后此值下降直至丢包。</summary>
        public float DeliveredMarginDb;
        public uint CommandCode;
        /// <summary>TrackShare/GuidanceUpdate 的航迹载荷。</summary>
        public TrackSnapshot TrackPayload;
        public CombatBehaviorReport BehaviorPayload;
        public FleetPacket FleetPayload;
        public EmissionReport EmissionPayload;
    }
}
