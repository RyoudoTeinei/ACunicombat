using System;
using ACunicombat.Simulation.Core;
using ACunicombat.Sensing.Contracts;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.ElectronicWarfare
{
    /// <summary>
    /// 电子战注入系统骨架（设计文档 13）：EW 不修改"是否锁定"，只在信号链注入。
    /// 本系统在自身更新阶段（传感器 Tick 之前）向 Sensors 暴露的注入缓冲写数据，
    /// 引用方向 EW→Sensors 单向、数据方向 EW 写/Sensors 读，无回调。
    /// </summary>
    public sealed class EwInjectionSystem
    {
        /// <summary>注入口拒收的合成点计数（10.6 字段有效性规则；监控指标）。</summary>
        public long RejectedSyntheticCount { get; private set; }

        /// <summary>
        /// EW 合成假目标的 RNG 种子键（21 节 v1.2）：干扰机句柄 + 受害传感器 +
        /// 驻留序号 + 注入点序号的哈希链，与候选枚举顺序无关，重放稳定。
        /// 驻留序号的高低 32 位作为两个独立字段入链——v1.2 禁止任何异或折叠
        /// （1 与 1&lt;&lt;32 折叠后同值即是反例）。
        /// </summary>
        public static uint SyntheticSeed(
            uint sessionSeed, EmitterId jammer, SensorId victim, ulong dwellSequence, uint pointIndex)
        {
            var chained = SimHash.Chain(
                sessionSeed, jammer.Packed, victim.Packed,
                (uint)dwellSequence, (uint)(dwellSequence >> 32));
            return SimHash.Combine(chained, pointIndex);
        }

        /// <summary>
        /// 合成点注入（含 10.6 强制验证）：字段有效性必须与受害传感器能力掩码一致——
        /// 能力外字段必须为 NaN，能力内字段必须有效；不合规拒收并计数，
        /// 保证合并阶段永远不会遇到维度不一致的检测点。
        /// </summary>
        public bool TryInject(
            in SyntheticDetection detection, SensorCapability victimCapability,
            NativeList<SyntheticDetection> victimInbox)
        {
            if (!Validate(detection.Measurement, victimCapability))
            {
                RejectedSyntheticCount++;
                return false;
            }

            victimInbox.Add(detection);
            return true;
        }

        public static bool Validate(in Measurement m, SensorCapability capability)
        {
            var hasRange = !float.IsNaN(m.Range);
            var hasDoppler = !float.IsNaN(m.RadialVelocity);
            var hasLosRate = !float.IsNaN(m.LosRate.x);
            return hasRange == ((capability & SensorCapability.Range) != 0)
                   && hasDoppler == ((capability & SensorCapability.Doppler) != 0)
                   && hasLosRate == ((capability & SensorCapability.LosRate) != 0);
        }

        /// <summary>
        /// Builds one physical noise contribution at the victim receiver input.
        /// ERP includes the transmit antenna; receiverGainLinear is the victim's
        /// boresight receive gain. Aspect and spectral coupling remain in Sensors.
        /// </summary>
        public static bool TryCreateNoiseContribution(
            SensorId victimSensor,
            EmitterId sourceEmitter,
            double3 jammerPosition,
            double3 victimPosition,
            float3 emissionDirection,
            float beamWidthRadians,
            float maximumRangeMeters,
            float equivalentRadiatedPowerWatts,
            float receiverGainLinear,
            ushort frequencyBand,
            float centerFrequencyHz,
            float bandwidthHz,
            out JamContribution contribution)
        {
            contribution = default;
            var relative = victimPosition - jammerPosition;
            double range = math.length(relative);
            if (!victimSensor.IsValid || range < 1.0 || range > maximumRangeMeters ||
                !math.all(math.isfinite(relative)) || !math.all(math.isfinite(emissionDirection)) ||
                math.lengthsq(emissionDirection) < 1e-8f || beamWidthRadians <= 0f ||
                equivalentRadiatedPowerWatts <= 0f || receiverGainLinear <= 0f ||
                centerFrequencyHz <= 0f || bandwidthHz <= 0f) return false;
            var toVictim = math.normalizesafe((float3)(relative / range));
            if (math.dot(math.normalize(emissionDirection), toVictim) <
                math.cos(beamWidthRadians * .5f)) return false;
            float powerDbw = 10f * math.log10(equivalentRadiatedPowerWatts * receiverGainLinear) -
                             20f * (float)math.log10(range);
            if (!float.IsFinite(powerDbw)) return false;
            contribution = new JamContribution
            {
                VictimSensorId = victimSensor,
                SourceEmitterId = sourceEmitter,
                ArrivalDirection = -toVictim,
                FrequencyBand = frequencyBand,
                PowerDb = powerDbw,
                PowerIsDbw = true,
                CenterFrequencyHz = centerFrequencyHz,
                BandwidthHz = bandwidthHz,
            };
            return true;
        }

        // TODO：距离/速度/角度欺骗（测量生成后偏移，需波形匹配成功）、诱饵实体登记、
        // 数据链丢包率注入。所有注入登记 EmissionEvent 供 ESM 反制（13 节）。
    }
}
