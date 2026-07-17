using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class ResolutionMergeTests
    {
        private static readonly ResolutionMerge.Resolutions Res = new ResolutionMerge.Resolutions
        {
            RangeMeters = 150f,
            AngleRadians = 0.03f,
            VelocityMps = 20f,
        };

        private static Measurement Make(float range, float3 dir, float radialVel, float snrDb)
        {
            return new Measurement
            {
                Range = range,
                Direction = math.normalize(dir),
                RadialVelocity = radialVel,
                LosRate = new float3(float.NaN),
                RangeVar = 25f,
                AngleVar = 1e-6f,
                VelocityVar = 4f,
                SnrDb = snrDb,
            };
        }

        [Test]
        public void CloseDetections_MergeToOne()
        {
            using var arr = new NativeArray<Measurement>(2, Allocator.Temp);
            using var scratch = new NativeArray<int>(2, Allocator.Temp);
            var native = arr;
            native[0] = Make(10000f, new float3(1, 0, 0), -500f, 20f);
            native[1] = Make(10050f, new float3(1, 0.001f, 0), -505f, 14f);

            var count = ResolutionMerge.MergeInPlace(
                native, 2, SensorCapability.Range | SensorCapability.Doppler, Res, scratch);
            Assert.That(count, Is.EqualTo(1));
            // 簇核为高 SNR 者，合并距离偏向它。
            Assert.That(native[0].Range, Is.InRange(10000f, 10025f));
        }

        [Test]
        public void NanDoppler_WithoutCapability_StillMerges()
        {
            // v1.2 字段有效性规则：无多普勒能力时 NaN 不参与判据，否则永远无法合并。
            using var arr = new NativeArray<Measurement>(2, Allocator.Temp);
            using var scratch = new NativeArray<int>(2, Allocator.Temp);
            var native = arr;
            var a = Make(10000f, new float3(1, 0, 0), float.NaN, 20f);
            var b = Make(10050f, new float3(1, 0.001f, 0), float.NaN, 14f);
            native[0] = a;
            native[1] = b;

            var count = ResolutionMerge.MergeInPlace(
                native, 2, SensorCapability.Range, Res, scratch);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(float.IsNaN(native[0].RadialVelocity), "能力外字段保持 NaN");
        }

        [Test]
        public void SeparatedDetections_DoNotMerge()
        {
            using var arr = new NativeArray<Measurement>(2, Allocator.Temp);
            using var scratch = new NativeArray<int>(2, Allocator.Temp);
            var native = arr;
            native[0] = Make(10000f, new float3(1, 0, 0), -500f, 20f);
            native[1] = Make(14000f, new float3(1, 0, 0), -500f, 14f);

            var count = ResolutionMerge.MergeInPlace(
                native, 2, SensorCapability.Range | SensorCapability.Doppler, Res, scratch);
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void MixtureVariance_CoversClusterSpread()
        {
            // v1.2：混合方差必须含均值离差项——两个分开的分量合成后，方差
            // 必须大于简单加权平均（否则过自信，NEES 超上界）。
            var naive = 0.5f * 25f + 0.5f * 25f;
            var mixture = ResolutionMerge.MixtureVariance(0.5f, 10000f, 25f, 0.5f, 10100f, 25f);
            Assert.That(mixture, Is.GreaterThan(naive));
            // 解析值：25 + (50)² = 2525。
            Assert.That(mixture, Is.EqualTo(2525f).Within(0.5f));
        }
    }
}
