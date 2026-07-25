using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.DataLink
{
    /// <summary>
    /// 数据链终端配置。天线模型：BeamWidthRadians &lt; 2π 为定向（发射时波束指向
    /// 单播目标实体），≥2π 为全向（组播/在网广播）。功率与旁瓣决定第三方 ESM
    /// 能在多远侦听到这条链路——上行链路本身就是可被告警的辐射。
    /// </summary>
    public struct LinkTerminalProfile
    {
        public LinkTerminalId TerminalId;
        public SimEntityId OwnerEntityId;
        /// <summary>写入发射事件用；同平台的雷达/链路各自独立的 EmitterId。</summary>
        public EmitterId EmitterId;
        public byte NetworkId;
        public float TransmitPowerDb;
        public float SidelobeLevelDb;
        public float BeamWidthRadians;
        public float ReceiveSensitivityDb;
        public double MaxRangeMeters;
        public ushort FrequencyBand;
        public ushort WaveformId;
    }

    /// <summary>
    /// 数据链网络（框架级，供机体/无人机/僚机/导弹/拦截系统共用）：
    ///   - 终端注册制，NetworkId 分网（0=玩家制导网，将来 1=编队网等）；
    ///   - 报文经物理链路投递：发射写 EmissionEvent（ESM 可侦听）、单向 1/R²
    ///     传播、接收灵敏度判决、阈值附近概率丢包（哈希链种子确定性）；
    ///   - 单播（定向窄波束指向目标实体）或同网组播（全向低功率）；
    ///   - 投递箱每 Tick 清空，消费端按 Sequence 去重（轮询频率可高于 Tick）。
    /// 干扰接入点：投递余量减去 JamContribution（TODO：EW 程序集接通后启用）。
    /// 内容对齐信号级纪律：链路只搬运 TrackSnapshot 等契约载荷，不搬真值。
    /// </summary>
    public sealed class DataLinkSystem : IDisposable
    {
        public const int MaxTerminals = 64;
        public const int OutboxCapacity = 256;
        public const int InboxCapacity = 512;

        public struct DeliveredLinkMessage
        {
            public LinkTerminalId Receiver;
            public LinkMessage Message;
        }

        private readonly SimWorld world;
        private readonly LinkTerminalProfile[] terminals = new LinkTerminalProfile[MaxTerminals];
        private readonly bool[] terminalRegistered = new bool[MaxTerminals];
        private NativeList<LinkMessage> outbox;
        private NativeList<DeliveredLinkMessage> inbox;
        private NativeList<EmissionEvent> emissionRing;
        private ulong sequenceCounter;

        public uint SessionSeed;

        public DataLinkSystem(SimWorld world)
        {
            this.world = world;
            outbox = new NativeList<LinkMessage>(OutboxCapacity, Allocator.Persistent);
            inbox = new NativeList<DeliveredLinkMessage>(InboxCapacity, Allocator.Persistent);
            emissionRing = new NativeList<EmissionEvent>(OutboxCapacity, Allocator.Persistent);
        }

        /// <summary>本 Tick 的链路辐射（ESM 侦听入口，与雷达发射环并列消费）。</summary>
        public NativeList<EmissionEvent> Emissions => emissionRing;

        public void RegisterTerminal(in LinkTerminalProfile profile)
        {
            var index = profile.TerminalId.Value.Index;
            if (!profile.TerminalId.IsValid || (uint)index >= terminals.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), "TerminalId is invalid or exceeds link capacity.");
            }

            terminals[index] = profile;
            terminalRegistered[index] = true;
        }

        public void UnregisterTerminal(LinkTerminalId terminalId)
        {
            var index = terminalId.Value.Index;
            if ((uint)index < terminalRegistered.Length &&
                terminals[index].TerminalId.Equals(terminalId))
            {
                terminalRegistered[index] = false;
            }
        }

        /// <summary>入队发送；投递发生在下一个链路 Tick（一 Tick 固有时延）。</summary>
        public bool Send(in LinkMessage message)
        {
            if (!TryGetTerminal(message.SourceTerminal, out _) ||
                outbox.Length >= OutboxCapacity)
            {
                return false;
            }

            var stamped = message;
            stamped.Sequence = ++sequenceCounter;
            outbox.Add(stamped);
            return true;
        }

        public void Tick(double simTime)
        {
            emissionRing.Clear();
            inbox.Clear();
            for (var i = 0; i < outbox.Length; i++)
            {
                var message = outbox[i];
                if (!TryGetTerminal(message.SourceTerminal, out var sender) ||
                    !world.IsAlive(sender.OwnerEntityId))
                {
                    continue; // 发射机没了（载机被击落）——报文自然消失。
                }

                var senderPosition =
                    world.ReadSnapshot[sender.OwnerEntityId.Value.Index].PositionWorld;
                EmitAndDeliver(in message, in sender, senderPosition, simTime);
            }

            outbox.Clear();
        }

        private void EmitAndDeliver(
            in LinkMessage message,
            in LinkTerminalProfile sender,
            double3 senderPosition,
            double simTime)
        {
            var unicast = message.TargetTerminal.IsValid;
            var beamDirection = new float3(0f, 0f, 1f);
            var beamWidth = math.min(sender.BeamWidthRadians, 2f * math.PI);
            if (unicast &&
                TryGetTerminal(message.TargetTerminal, out var addressee) &&
                world.IsAlive(addressee.OwnerEntityId))
            {
                // 定向上行指向被寻址实体——发射方经本链路本就持续获知己方导弹位置。
                var addresseePosition =
                    world.ReadSnapshot[addressee.OwnerEntityId.Value.Index].PositionWorld;
                var toAddressee = addresseePosition - senderPosition;
                var distance = math.length(toAddressee);
                if (distance > 1.0)
                {
                    beamDirection = (float3)(toAddressee / distance);
                }
            }
            else
            {
                beamWidth = 2f * math.PI; // 组播全向。
            }

            emissionRing.Add(new EmissionEvent
            {
                EmitterId = sender.EmitterId,
                StartTime = simTime,
                EndTime = simTime + 0.005,
                Origin = senderPosition,
                Direction = beamDirection,
                BeamWidth = beamWidth,
                MaxRangeMeters = sender.MaxRangeMeters,
                FrequencyBand = sender.FrequencyBand,
                WaveformId = sender.WaveformId,
                EmissionType = EmissionType.DataLink,
                MainlobePowerDb = sender.TransmitPowerDb,
                SidelobeLevelDb = sender.SidelobeLevelDb,
            });

            for (var r = 0; r < terminals.Length && inbox.Length < InboxCapacity; r++)
            {
                if (!terminalRegistered[r])
                {
                    continue;
                }

                var receiver = terminals[r];
                if (receiver.TerminalId.Equals(message.SourceTerminal))
                {
                    continue;
                }

                // 寻址：单播只投给被寻址终端；组播投给同网络全体。
                // 第三方"听到但解不开"属于 ESM 侦听（发射事件），不在投递范围。
                if (unicast
                        ? !receiver.TerminalId.Equals(message.TargetTerminal)
                        : receiver.NetworkId != message.NetworkId)
                {
                    continue;
                }

                if (!world.IsAlive(receiver.OwnerEntityId))
                {
                    continue;
                }

                var receiverPosition =
                    world.ReadSnapshot[receiver.OwnerEntityId.Value.Index].PositionWorld;
                var range = math.distance(senderPosition, receiverPosition);
                if (range < 1.0 || range > sender.MaxRangeMeters)
                {
                    continue;
                }

                // 被寻址方处于主瓣（定向波束即指向它；全向恒主瓣）。
                // TODO(EW)：margin −= 对准该接收机的 JamContribution → 干扰致断链。
                var receivedDb = sender.TransmitPowerDb - 20f * (float)math.log10(range);
                var marginDb = receivedDb - receiver.ReceiveSensitivityDb;
                if (marginDb <= 0f)
                {
                    continue;
                }

                if (marginDb < 6f)
                {
                    // 余量不足 6 dB 线性降概率——链路边缘自然出现丢包。
                    var seed = SimHash.Chain(
                        SessionSeed,
                        receiver.TerminalId.Packed,
                        (uint)message.Sequence,
                        (uint)(message.Sequence >> 32));
                    if (new SimRng(seed).NextFloat01() >= marginDb / 6f)
                    {
                        continue;
                    }
                }

                var delivered = message;
                delivered.DeliveryTime = simTime;
                delivered.DeliveredMarginDb = marginDb;
                inbox.Add(new DeliveredLinkMessage
                {
                    Receiver = receiver.TerminalId,
                    Message = delivered,
                });
            }
        }

        /// <summary>取某终端本 Tick 收到的报文。消费端按 Sequence 去重。</summary>
        public int CopyInbox(LinkTerminalId terminalId, LinkMessage[] destination)
        {
            if (destination == null || destination.Length == 0)
            {
                return 0;
            }

            var count = 0;
            for (var i = 0; i < inbox.Length && count < destination.Length; i++)
            {
                if (inbox[i].Receiver.Equals(terminalId))
                {
                    destination[count++] = inbox[i].Message;
                }
            }

            return count;
        }

        private bool TryGetTerminal(LinkTerminalId terminalId, out LinkTerminalProfile profile)
        {
            var index = terminalId.Value.Index;
            if (terminalId.IsValid &&
                (uint)index < terminals.Length &&
                terminalRegistered[index] &&
                terminals[index].TerminalId.Equals(terminalId))
            {
                profile = terminals[index];
                return true;
            }

            profile = default;
            return false;
        }

        public void Dispose()
        {
            outbox.Dispose();
            inbox.Dispose();
            emissionRing.Dispose();
        }
    }
}
