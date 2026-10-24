using System;
using ACunicombat.Simulation.Core;
using Unity.Mathematics;

namespace ACunicombat.Sensors
{
    /// <summary>Monostatic coherent energy model, SI inputs. No authored distance limit.</summary>
    [Serializable]
    public struct RadarPhysicalParameters
    {
        public float AverageTransmitPowerWatts, ApertureDiameterMeters, ApertureEfficiency;
        public float FrequencyHz, SystemNoiseTemperatureKelvin, SystemLossDb;
        public float CoherentIntegrationEfficiency, SignalBandwidthHz;
        public float AntennaDutyCycle;
        public float ThermalNoisePowerDbw => (float)(10.0*math.log10(1.380649e-23*SystemNoiseTemperatureKelvin*SignalBandwidthHz));
        public bool IsValid => AverageTransmitPowerWatts > 0 && ApertureDiameterMeters > 0 &&
            FrequencyHz > 0 && SystemNoiseTemperatureKelvin > 0 && ApertureEfficiency > 0 &&
            CoherentIntegrationEfficiency > 0 && SignalBandwidthHz > 0;
        public float WavelengthMeters => 299792458f / FrequencyHz;
        public float GainLinear => ApertureEfficiency * math.pow(math.PI * ApertureDiameterMeters / WavelengthMeters, 2f);
        public float BeamWidthRadians => 1.22f * WavelengthMeters / ApertureDiameterMeters;
        // Receiver-input power in dBW; matched-filter integration gain B*t*eta is applied to signal SNR.
        public RadarDwellConfig BuildDwell(float seconds, float beamWidthRadians, float efficiency = 1f)
        {
            float effectiveBeam = math.max(BeamWidthRadians, beamWidthRadians);
            float gain = GainLinear * math.pow(BeamWidthRadians / effectiveBeam, 2f);
            double coefficient = AverageTransmitPowerWatts * (double)gain * gain * WavelengthMeters * WavelengthMeters /
                math.pow(4f * math.PI, 3f);
            return new RadarDwellConfig {
                ConstantsDb = (float)(10.0 * math.log10(coefficient)) - SystemLossDb,
                IntegrationGainDb = 10f * math.log10(math.max(1e-12f, SignalBandwidthHz * seconds * CoherentIntegrationEfficiency * math.clamp(efficiency,0f,1f))),
                NoisePowerDb = ThermalNoisePowerDbw, DetectThresholdLowDb = 3f, DetectThresholdHighDb = 13f,
                RangeResolutionMeters = 299792458f / (2f * SignalBandwidthHz),
                BeamWidthRadians = effectiveBeam,
                VelocityResolutionMps = WavelengthMeters / (2f * math.max(.0001f, seconds))
            };
        }
        // 50% single-dwell detection against a stated RCS, thermal noise only; UI estimate, never a gate.
        public float EstimateRangeMeters(float rcs, float seconds)
        {
            var c=BuildDwell(seconds,BeamWidthRadians);
            return math.pow(10f,(c.ConstantsDb+c.IntegrationGainDb-c.NoisePowerDb+10f*math.log10(math.max(rcs,1e-9f))-8f)/40f);
        }
    }

    /// <summary>雷达驻留常数项（10.2）。首版用归一化标定：参考探测距离反推 ConstantsDb。</summary>
    public struct RadarDwellConfig
    {
        /// <summary>发射功率dB + 2×天线增益dB + 20·log10(λ) − 系统损耗dB 的合并常数。</summary>
        public float ConstantsDb;
        public float IntegrationGainDb;
        public float NoisePowerDb;
        public float DetectThresholdLowDb;
        public float DetectThresholdHighDb;
        public float RangeResolutionMeters;
        public float BeamWidthRadians;
        public float VelocityResolutionMps;
    }

    /// <summary>
    /// 对数域雷达方程 + 检测判决 + 测量误差模型（设计文档 10.2–10.4）。
    /// 全部纯静态、无超越函数外仅 log10/sqrt/pow10——同平台确定性由 Burst FloatMode 默认档保证（21 节分级）。
    /// </summary>
    public static class RadarMath
    {
        /// <summary>One-way relative power gain: Gaussian mainlobe (-3 dB at half beamwidth), bounded sidelobe floor.</summary>
        public static float AntennaResponseDb(float cosine, float beamWidthRadians, float sidelobeDb)
        {
            float angle=math.acos(math.clamp(cosine,-1f,1f));
            float relative=angle/math.max(.0001f,beamWidthRadians);
            return math.max(math.min(-1f,sidelobeDb),-12f*relative*relative);
        }
        /// <summary>Fraction of flat incident power spectrum admitted by the receive passband.</summary>
        public static float SpectralOverlap(float receiverCenter, float receiverBandwidth, float sourceCenter, float sourceBandwidth)
        {
            if(receiverBandwidth<=0||sourceBandwidth<=0)return 0;
            float lower=math.max(receiverCenter-receiverBandwidth*.5f,sourceCenter-sourceBandwidth*.5f);
            float upper=math.min(receiverCenter+receiverBandwidth*.5f,sourceCenter+sourceBandwidth*.5f);
            return math.saturate((upper-lower)/sourceBandwidth);
        }

        public static float LinearFromDb(float db) => math.pow(10f, db * 0.1f);

        public static float DbFromLinear(float linear) => 10f * math.log10(math.max(linear, 1e-30f));

        /// <summary>接收功率（dB）。距离项 −40·log10(r)，r⁴ 关系是 22.3 统计验收的对象。</summary>
        public static float ReceivedPowerDb(in RadarDwellConfig cfg, float rcsDbsm, double rangeMeters)
        {
            return cfg.ConstantsDb + rcsDbsm + cfg.IntegrationGainDb
                   - 40f * (float)math.log10(math.max(rangeMeters, 1.0));
        }

        /// <summary>SNR（dB）。分母噪声/杂波/干扰先在线性域求和再转 dB——禁止直接加 dB 值。</summary>
        public static float SnrDb(float receivedPowerDb, float noiseDb, float clutterDb, float jamDb)
        {
            var denomLinear = LinearFromDb(noiseDb) + LinearFromDb(clutterDb) + LinearFromDb(jamDb);
            return receivedPowerDb - DbFromLinear(denomLinear);
        }

        /// <summary>检测概率（10.3）：S 曲线，无硬边界。参数序 (edge0, edge1, x)。</summary>
        public static float DetectionProbability(in RadarDwellConfig cfg, float snrDb) =>
            math.smoothstep(cfg.DetectThresholdLowDb, cfg.DetectThresholdHighDb, snrDb);

        /// <summary>探测掷骰：种子 = hash 链(SessionSeed, sensorId, dwellSequence, targetEntityId)，见 21 节。</summary>
        public static bool RollDetection(float pd, uint chainedSeed)
        {
            var rng = new SimRng(chainedSeed);
            return rng.NextFloat01() < pd;
        }

        // ---- 测量误差模型（10.4），snrLinear = LinearFromDb(snrDb) ----

        public static float SigmaRange(in RadarDwellConfig cfg, float snrLinear) =>
            math.max(cfg.RangeResolutionMeters / math.sqrt(2f * math.max(snrLinear, 1e-6f)),
                cfg.RangeResolutionMeters * 0.1f);

        /// <summary>单脉冲近似：波束宽度 / (1.6·√(2·SNR))。</summary>
        public static float SigmaAngle(in RadarDwellConfig cfg, float snrLinear) =>
            cfg.BeamWidthRadians / (1.6f * math.sqrt(2f * math.max(snrLinear, 1e-6f)));

        public static float SigmaVelocity(in RadarDwellConfig cfg, float snrLinear) =>
            math.max(
                cfg.VelocityResolutionMps /
                math.sqrt(2f * math.max(snrLinear, 1e-6f)),
                cfg.VelocityResolutionMps * 0.1f);
    }
}
