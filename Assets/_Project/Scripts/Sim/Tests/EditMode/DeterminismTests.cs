using ACunicombat.Simulation.Core;
using ACunicombat.Sensors;
using NUnit.Framework;

namespace ACunicombat.Tests.EditMode
{
    public class DeterminismTests
    {
        [Test]
        public void HashChain_IsOrderSensitive()
        {
            // v1.2：种子构成禁止异或组合（可交换）。哈希链必须顺序敏感。
            Assert.That(SimHash.Chain(1u, 2u, 3u), Is.Not.EqualTo(SimHash.Chain(1u, 3u, 2u)));
            Assert.That(SimHash.Combine(5u, 7u), Is.Not.EqualTo(SimHash.Combine(7u, 5u)));
        }

        [Test]
        public void Rng_SameSeedSameStream()
        {
            var a = new SimRng(SimHash.Chain(42u, 1u, 2u, 3u));
            var b = new SimRng(SimHash.Chain(42u, 1u, 2u, 3u));
            for (var i = 0; i < 100; i++)
            {
                Assert.That(a.NextUInt(), Is.EqualTo(b.NextUInt()));
            }
        }

        [Test]
        public void Rng_Float01_InRange()
        {
            var rng = new SimRng(123u);
            for (var i = 0; i < 1000; i++)
            {
                var v = rng.NextFloat01();
                Assert.That(v, Is.GreaterThanOrEqualTo(0f).And.LessThan(1f));
            }
        }

        [Test]
        public void RadarEquation_FollowsR4()
        {
            // 22.3 统计验收的单测形态：距离翻倍 → 接收功率 −40·log10(2) ≈ −12.04 dB。
            var cfg = new RadarDwellConfig { ConstantsDb = 200f };
            var near = RadarMath.ReceivedPowerDb(cfg, 0f, 10_000.0);
            var far = RadarMath.ReceivedPowerDb(cfg, 0f, 20_000.0);
            Assert.That(near - far, Is.EqualTo(12.0412f).Within(0.001f));
        }

        [Test]
        public void SnrDenominator_SumsInLinearDomain()
        {
            // 10.2：分母先线性求和再转 dB，禁止直接加 dB。三个等功率源 → +4.77 dB。
            var snrSingle = RadarMath.SnrDb(50f, 10f, -300f, -300f);
            var snrTriple = RadarMath.SnrDb(50f, 10f, 10f, 10f);
            Assert.That(snrSingle - snrTriple, Is.EqualTo(4.7712f).Within(0.001f));
        }
    }
}
