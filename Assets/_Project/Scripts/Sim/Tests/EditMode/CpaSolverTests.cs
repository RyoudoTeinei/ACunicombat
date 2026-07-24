using ACunicombat.FireControl;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class CpaSolverTests
    {
        [Test]
        public void LinearCrossing_FindsClosedFormCpa()
        {
            // 目标在 (1000, 100, 0)，相对速度 (-2000, 0, 0)：t* = 0.5，最近距离 100。
            var result = CpaSolver.Solve(
                new double3(1000, 100, 0), new double3(-2000, 0, 0), double3.zero, 1.0);
            Assert.That(result.Time, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(result.Distance, Is.EqualTo(100.0).Within(1e-6));
        }

        [Test]
        public void CpaOutsideInterval_ClampsToEndpoint()
        {
            // 远离中：最近点在 t=0。
            var result = CpaSolver.Solve(
                new double3(1000, 0, 0), new double3(500, 0, 0), double3.zero, 1.0);
            Assert.That(result.Time, Is.EqualTo(0.0).Within(1e-9));
            Assert.That(result.Distance, Is.EqualTo(1000.0).Within(1e-6));
        }

        [Test]
        public void AcceleratingCase_BeatsDenseSampling()
        {
            // 匀加速下解析解必须不劣于 1e5 次稠密采样找到的最小距离。
            var dp0 = new double3(800, -50, 30);
            var dv = new double3(-1500, 40, -10);
            var da = new double3(60, -30, 90);
            const double dt = 1.0;
            var result = CpaSolver.Solve(dp0, dv, da, dt);

            var sampledMin = double.MaxValue;
            for (var i = 0; i <= 100000; i++)
            {
                var t = dt * i / 100000.0;
                var p = dp0 + dv * t + 0.5 * da * (t * t);
                sampledMin = math.min(sampledMin, math.length(p));
            }

            Assert.That(result.Distance, Is.LessThanOrEqualTo(sampledMin + 1e-3));
        }

        [Test]
        public void Deterministic_SameInputSameBits()
        {
            var dp0 = new double3(123.456, -789.1, 42.0);
            var dv = new double3(-999.9, 88.8, -7.7);
            var da = new double3(1.5, -2.5, 3.5);
            var a = CpaSolver.Solve(dp0, dv, da, 0.02);
            var b = CpaSolver.Solve(dp0, dv, da, 0.02);
            Assert.That(a.Time, Is.EqualTo(b.Time));
            Assert.That(a.Distance, Is.EqualTo(b.Distance));
        }

        [Test]
        public void MissileSeekerVolume_RejectsTargetsOutsideRangeOrFieldOfView()
        {
            var origin = float3.zero;
            var forward = new float3(0f, 0f, 1f);
            var halfAngle = math.radians(30f);

            Assert.That(
                MissileEngagementMath.IsInsideSeekerVolume(
                    origin,
                    forward,
                    new float3(0f, 0f, 900f),
                    1000f,
                    halfAngle),
                Is.True);
            Assert.That(
                MissileEngagementMath.IsInsideSeekerVolume(
                    origin,
                    forward,
                    new float3(900f, 0f, 100f),
                    1000f,
                    halfAngle),
                Is.False);
            Assert.That(
                MissileEngagementMath.IsInsideSeekerVolume(
                    origin,
                    forward,
                    new float3(0f, 0f, 1100f),
                    1000f,
                    halfAngle),
                Is.False);
        }

        [Test]
        public void MissileProximityPass_DetectsNonContactCrossing()
        {
            var detected = MissileEngagementMath.TryGetProximityPass(
                new float3(20f, 8f, 0f),
                new float3(-100f, 0f, 0f),
                0.25f,
                10f,
                out var time,
                out var distance);

            Assert.That(detected, Is.True);
            Assert.That(time, Is.EqualTo(0.2f).Within(1e-5f));
            Assert.That(distance, Is.EqualTo(8f).Within(1e-4f));
        }

        [Test]
        public void MissileLineOfSightRate_IsZeroForPureClosingMotion()
        {
            var rate = MissileEngagementMath.LineOfSightRate(
                new float3(0f, 0f, 1000f),
                new float3(0f, 0f, -200f));

            Assert.That(math.length(rate), Is.LessThan(1e-6f));
        }
    }
}
