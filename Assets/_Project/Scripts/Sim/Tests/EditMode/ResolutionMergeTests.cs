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
            var native = arr;
            native[0] = Make(10000f, new float3(1, 0, 0), -500f, 20f);
            native[1] = Make(10050f, new float3(1, 0.001f, 0), -505f, 14f);

            var count = ResolutionMerge.MergeInPlace(
                native, 2, SensorCapability.Range | SensorCapability.Doppler, Res);
            Assert.That(count, Is.EqualTo(1));
            // 簇核为高 SNR 者，合并距离偏向它。
            Assert.That(native[0].Range, Is.InRange(10000f, 10025f));
        }

        [Test]
        public void HighestSnrNotFirst_NothingLost()
        {
            // 回归（评审 P1）：最高 SNR 在索引 1 时，旧实现会用簇核覆盖索引 0
            // 的未处理检测点。三个互不合并的点无论输入顺序如何都必须全部存活。
            using var arr = new NativeArray<Measurement>(3, Allocator.Temp);
            var native = arr;
            native[0] = Make(10000f, new float3(1, 0, 0), -500f, 10f);
            native[1] = Make(30000f, new float3(0, 1, 0), 300f, 25f);
            native[2] = Make(50000f, new float3(0, 0, 1), 0f, 18f);

            var count = ResolutionMerge.MergeInPlace(
                native, 3, SensorCapability.Range | SensorCapability.Doppler, Res);
            Assert.That(count, Is.EqualTo(3));

            var ranges = new[] { native[0].Range, native[1].Range, native[2].Range };
            Assert.That(ranges, Is.EquivalentTo(new[] { 30000f, 50000f, 10000f }));
        }

        [Test]
        public void NanDoppler_WithoutCapability_StillMerges()
        {
            // v1.2 字段有效性规则：无多普勒能力时 NaN 不参与判据，否则永远无法合并。
            using var arr = new NativeArray<Measurement>(2, Allocator.Temp);
            var native = arr;
            var a = Make(10000f, new float3(1, 0, 0), float.NaN, 20f);
            var b = Make(10050f, new float3(1, 0.001f, 0), float.NaN, 14f);
            native[0] = a;
            native[1] = b;

            var count = ResolutionMerge.MergeInPlace(native, 2, SensorCapability.Range, Res);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(float.IsNaN(native[0].RadialVelocity), "能力外字段保持 NaN");
        }

        [Test]
        public void SeparatedDetections_DoNotMerge()
        {
            using var arr = new NativeArray<Measurement>(2, Allocator.Temp);
            var native = arr;
            native[0] = Make(10000f, new float3(1, 0, 0), -500f, 20f);
            native[1] = Make(14000f, new float3(1, 0, 0), -500f, 14f);

            var count = ResolutionMerge.MergeInPlace(
                native, 2, SensorCapability.Range | SensorCapability.Doppler, Res);
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void MixtureVariance_CoversClusterSpread()
        {
            // v1.2：混合方差必须含均值离差项——两个分开的分量合成后，方差
            // 必须大于简单加权平均（否则过自信，NEES 超上界）。中心化实现
            // 在 μ~10⁴ 下不受大数平方相减的抵消误差影响，解析值精确到 2525。
            var naive = 0.5f * 25f + 0.5f * 25f;
            var mixture = ResolutionMerge.MixtureVariance(0.5f, 10000f, 25f, 0.5f, 10100f, 25f);
            Assert.That(mixture, Is.GreaterThan(naive));
            Assert.That(mixture, Is.EqualTo(2525f).Within(0.01f));
        }
    }
}
