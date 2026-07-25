using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    public struct EsmReceiverProfile
    {
        public SensorId ReceiverId;
        public SimEntityId OwnerEntityId;
        /// <summary>本机雷达发射时接收机消隐（真实 RWR 与自机发射机硬件同步消隐）。
        /// 只消隐本机发射机：本机发射的导弹导引头照样会被自己听到——这是真实行为。</summary>
        public EmitterId BlankedEmitterId;
        /// <summary>第二消隐位：本机数据链终端（与雷达各自独立的 EmitterId）。</summary>
        public EmitterId BlankedEmitterId2;
        /// <summary>接收灵敏度下限（dB，与 EmissionEvent 功率同标度）。</summary>
        public float SensitivityFloorDb;
        /// <summary>RWR 测向精度（弧度，1σ）。被动测向本来就粗，量级 ~8°。</summary>
        public float BearingSigmaRadians;
    }

    /// <summary>
    /// 被动告警接收（ESM/RWR，设计文档 12）：只收不发，每传感器 Tick 反向消费
    /// EmissionEvent 环：
    ///   - 主瓣/旁瓣几何判定（接收机是否在辐射波束锥内）；
    ///   - 单向 1/R² 传播（雷达回波是 1/R⁴，因此告警距离天然远超对方探测距离，
    ///     电子战不对称性由公式差异免费成立）；
    ///   - 阈值附近概率检测，哈希链种子保确定性回放。
    /// 产出 WarningObservation：带噪方位 + 频段/波形 + 接收功率——无距离、无真值
    /// 身份。威胁级别由 Tracking 层 EsmThreatCorrelator 从照射统计推断。
    /// </summary>
    public sealed class EsmReceiverSystem : IDisposable
    {
        public const int MaxReceivers = 8;
        public const int ContactRingCapacity = 256;

        private readonly SimWorld world;
        private readonly EsmReceiverProfile[] receivers = new EsmReceiverProfile[MaxReceivers];
        private readonly bool[] receiverRegistered = new bool[MaxReceivers];
        private NativeList<WarningObservation> contactRing;

        public uint SessionSeed;

        public EsmReceiverSystem(SimWorld world)
        {
            this.world = world;
            contactRing = new NativeList<WarningObservation>(ContactRingCapacity, Allocator.Persistent);
        }

        public NativeList<WarningObservation> Contacts => contactRing;

        public void RegisterReceiver(in EsmReceiverProfile profile)
        {
            var index = profile.ReceiverId.Value.Index;
            if (!profile.ReceiverId.IsValid || (uint)index >= receivers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(profile), "ReceiverId is invalid or exceeds ESM capacity.");
            }

            receivers[index] = profile;
            receiverRegistered[index] = true;
        }

        public void UnregisterReceiver(SensorId receiverId)
        {
            var index = receiverId.Value.Index;
            if ((uint)index < receiverRegistered.Length &&
                receivers[index].ReceiverId.Equals(receiverId))
            {
                receiverRegistered[index] = false;
            }
        }

        /// <summary>每传感器 Tick 先清空接触环，再对每个辐射环调用 Consume（雷达环、
        /// 数据链环……未来激光/干扰环同理），最后交相关器处理。</summary>
        public void BeginTick()
        {
            contactRing.Clear();
        }

        public void Consume(NativeList<EmissionEvent> emissions, double simTime)
        {
            for (var r = 0; r < receivers.Length; r++)
            {
                if (!receiverRegistered[r])
                {
                    continue;
                }

                var receiver = receivers[r];
                if (!world.IsAlive(receiver.OwnerEntityId))
                {
                    continue;
                }

                var receiverPosition =
                    world.ReadSnapshot[receiver.OwnerEntityId.Value.Index].PositionWorld;
                for (var e = 0; e < emissions.Length && contactRing.Length < ContactRingCapacity; e++)
                {
                    var emission = emissions[e];
                    if (emission.EmitterId.Equals(receiver.BlankedEmitterId) ||
                        emission.EmitterId.Equals(receiver.BlankedEmitterId2))
                    {
                        continue;
                    }

                    var toReceiver = receiverPosition - emission.Origin;
                    var range = math.length(toReceiver);
                    if (range < 1.0)
                    {
                        continue;
                    }

                    var toReceiverDirection = (float3)(toReceiver / range);
                    var mainlobe = math.dot(emission.Direction, toReceiverDirection)
                                   >= math.cos(emission.BeamWidth * 0.5f);
                    var emitPowerDb = mainlobe
                        ? emission.MainlobePowerDb
                        : emission.MainlobePowerDb + emission.SidelobeLevelDb;
                    // 单向传播：20·log10(R)。对比雷达方程的 40·log10(R)——RWR 的
                    // 告警距离远超辐射方探测距离正是来自这 20 dB/decade 的差。
                    var receivedDb = emitPowerDb - 20f * (float)math.log10(range);
                    var marginDb = receivedDb - receiver.SensitivityFloorDb;
                    if (marginDb <= 0f)
                    {
                        continue;
                    }

                    // 阈值上 6 dB 内线性爬升到必检；掷骰种子含发射环索引，
                    // 同一 Tick 多条辐射互不相关。
                    var probability = math.saturate(marginDb / 6f);
                    var seed = SimHash.Chain(
                        SessionSeed,
                        receiver.ReceiverId.Packed,
                        (uint)e,
                        (uint)math.round(simTime * 400.0));
                    if (probability < 1f && !RadarMath.RollDetection(probability, seed))
                    {
                        continue;
                    }

                    var rng = new SimRng(SimHash.Combine(seed, 0x5E3D91C7u));
                    var emitterDirection = -toReceiverDirection;
                    BuildPerpendicularBasis(emitterDirection, out var tangentA, out var tangentB);
                    var noisyDirection = math.normalizesafe(
                        emitterDirection
                        + tangentA * (rng.NextGaussian() * receiver.BearingSigmaRadians)
                        + tangentB * (rng.NextGaussian() * receiver.BearingSigmaRadians),
                        emitterDirection);

                    contactRing.Add(new WarningObservation
                    {
                        ReceiverId = receiver.ReceiverId,
                        Timestamp = simTime,
                        Direction = noisyDirection,
                        BearingVar = receiver.BearingSigmaRadians * receiver.BearingSigmaRadians,
                        FrequencyBand = emission.FrequencyBand,
                        WaveformId = emission.WaveformId,
                        // 识别置信度随接收余量爬升：阈值附近只能判"有辐射"，
                        // 余量 ≥18 dB 才能可靠识别波形（相关器按此门控波形库分类）。
                        WaveformConfidence = math.saturate(marginDb / 18f),
                        EmissionType = emission.EmissionType,
                        ReceivedPowerDb = receivedDb,
                    });
                }
            }
        }

        private static void BuildPerpendicularBasis(float3 direction, out float3 tangentA, out float3 tangentB)
        {
            var reference = math.abs(direction.y) < 0.9f
                ? new float3(0f, 1f, 0f)
                : new float3(1f, 0f, 0f);
            tangentA = math.normalizesafe(math.cross(direction, reference), new float3(1f, 0f, 0f));
            tangentB = math.normalizesafe(math.cross(direction, tangentA), new float3(0f, 1f, 0f));
        }

        public void Dispose()
        {
            contactRing.Dispose();
        }
    }
}
