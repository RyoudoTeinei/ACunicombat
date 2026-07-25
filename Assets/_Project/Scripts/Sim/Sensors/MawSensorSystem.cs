using System;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.World;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>
    /// 导弹逼近告警（MAW）：全向红外通道，补 RF 告警的盲区——IR 弹全程静默、
    /// ARH 弹中段静默。判据：注册的红外点源辐照度过阈 + 正在接近（closing &gt; 0）。
    /// 自机发射的导弹飞离（closing &lt; 0）被自然滤掉。导弹燃尽滑翔段强度骤降 →
    /// 告警可能中断，这是真实行为，聪明的对手会利用它。
    /// 产出 WarningObservation（EmissionType.Infrared + 伪波形 id），与 RF 接触
    /// 走同一相关器；红外强度状态由场景桥（热模型）经 Driver 喂入。
    /// TODO(P4)：AnalyticOcclusion 视线判定——需要先有障碍物注册表。
    /// </summary>
    public sealed class MawSensorSystem
    {
        /// <summary>红外羽焰在波形库里的伪波形号（band 0 保留给非射频通道）。</summary>
        public const ushort PlumeWaveformId = 0xFFF0;
        public const ushort InfraredBand = 0;

        private const float MinClosingVelocity = 20f;
        private const float BearingSigmaRadians = 5f * math.PI / 180f;

        private readonly SimWorld world;
        private readonly float[] sourceIntensityDb = new float[SimWorld.MaxEntities];
        private readonly bool[] sourceRegistered = new bool[SimWorld.MaxEntities];
        private readonly SimEntityId[] sourceEntities = new SimEntityId[SimWorld.MaxEntities];

        public uint SessionSeed;
        /// <summary>接收灵敏度（dB 辐照度下限）。</summary>
        public float SensitivityFloorDb = -30f;

        public MawSensorSystem(SimWorld world)
        {
            this.world = world;
        }

        public void RegisterSource(SimEntityId entityId, float intensityDb)
        {
            var index = entityId.Value.Index;
            if (!entityId.IsValid || (uint)index >= sourceRegistered.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(entityId), "IR source entity is invalid.");
            }

            sourceEntities[index] = entityId;
            sourceIntensityDb[index] = intensityDb;
            sourceRegistered[index] = true;
        }

        public void SetSourceIntensity(SimEntityId entityId, float intensityDb)
        {
            var index = entityId.Value.Index;
            if ((uint)index < sourceRegistered.Length &&
                sourceEntities[index].Equals(entityId))
            {
                sourceIntensityDb[index] = intensityDb;
            }
        }

        public void UnregisterSource(SimEntityId entityId)
        {
            var index = entityId.Value.Index;
            if ((uint)index < sourceRegistered.Length &&
                sourceEntities[index].Equals(entityId))
            {
                sourceRegistered[index] = false;
            }
        }

        /// <summary>把本 Tick 检出的红外接触追加进（ESM 已清空并填好的）接触环。</summary>
        public void AppendContacts(
            SimEntityId receiverEntity,
            SensorId receiverId,
            NativeList<WarningObservation> contacts,
            double simTime)
        {
            if (!world.IsAlive(receiverEntity))
            {
                return;
            }

            var receiverIndex = receiverEntity.Value.Index;
            var receiverState = world.ReadSnapshot[receiverIndex];
            for (var i = 0; i < sourceRegistered.Length; i++)
            {
                if (!sourceRegistered[i] || i == receiverIndex ||
                    !world.IsAlive(sourceEntities[i]))
                {
                    continue;
                }

                var sourceState = world.ReadSnapshot[i];
                var relativePosition = sourceState.PositionWorld - receiverState.PositionWorld;
                var range = math.length(relativePosition);
                if (range < 1.0)
                {
                    continue;
                }

                var direction = (float3)(relativePosition / range);
                // 只报接近中的热源：closing = −d(range)/dt = −(v_rel · r̂)。
                var relativeVelocity = sourceState.VelocityWorld - receiverState.VelocityWorld;
                var closingVelocity = -(float)math.dot(relativeVelocity, (double3)direction);
                if (closingVelocity < MinClosingVelocity)
                {
                    continue;
                }

                var irradianceDb = sourceIntensityDb[i] - 20f * (float)math.log10(range);
                var marginDb = irradianceDb - SensitivityFloorDb;
                if (marginDb <= 0f)
                {
                    continue;
                }

                var probability = math.saturate(marginDb / 6f);
                var seed = SimHash.Chain(
                    SessionSeed,
                    receiverId.Packed,
                    (uint)i,
                    (uint)math.round(simTime * 400.0));
                if (probability < 1f && !RadarMath.RollDetection(probability, seed))
                {
                    continue;
                }

                var rng = new SimRng(SimHash.Combine(seed, 0x9B71D3A5u));
                BuildPerpendicularBasis(direction, out var tangentA, out var tangentB);
                var noisyDirection = math.normalizesafe(
                    direction
                    + tangentA * (rng.NextGaussian() * BearingSigmaRadians)
                    + tangentB * (rng.NextGaussian() * BearingSigmaRadians),
                    direction);

                if (contacts.Length >= EsmReceiverSystem.ContactRingCapacity)
                {
                    return;
                }

                contacts.Add(new WarningObservation
                {
                    ReceiverId = receiverId,
                    Timestamp = simTime,
                    Direction = noisyDirection,
                    BearingVar = BearingSigmaRadians * BearingSigmaRadians,
                    FrequencyBand = InfraredBand,
                    WaveformId = PlumeWaveformId,
                    WaveformConfidence = math.saturate(marginDb / 12f),
                    EmissionType = EmissionType.Infrared,
                    ReceivedPowerDb = irradianceDb,
                });
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
    }
}
