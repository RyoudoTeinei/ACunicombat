using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class CpaSolverTests
    {
        [Test]
        public void ProtectedCpaWindow_UsesPropagatedUncertainty()
        {
            Assert.That(
                MissileEngagementMath.IntersectsProtectedCpaWindow(
                    40f,
                    1200f,
                    650f,
                    60f,
                    180f),
                Is.True);
        }

        [Test]
        public void ProtectedCpaWindow_RejectsPreciseMiss()
        {
            Assert.That(
                MissileEngagementMath.IntersectsProtectedCpaWindow(
                    40f,
                    1200f,
                    100f,
                    60f,
                    180f),
                Is.False);
        }

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
        public void MissileSeekerTargetRole_SeparatesAirToAirAndInterceptorTargets()
        {
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    false,
                    TrackObjectClass.AirVehicle,
                    0.9f),
                Is.True);
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    false,
                    TrackObjectClass.Missile,
                    0.9f),
                Is.False);
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    true,
                    TrackObjectClass.Missile,
                    0.9f),
                Is.True);
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    true,
                    TrackObjectClass.AirVehicle,
                    0.9f),
                Is.False);
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    false,
                    TrackObjectClass.AirVehicle,
                    0.1f),
                Is.False);
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    false,
                    TrackObjectClass.Decoy,
                    0.9f),
                Is.True);
            Assert.That(
                MissileEngagementMath.IsCompatibleSeekerTarget(
                    true,
                    TrackObjectClass.Decoy,
                    0.9f),
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
        public void SeekerGuidanceSmoothing_DoesNotCreateFuseTargetInEmptySpace()
        {
            var update = MissileEngagementMath.ResolveSeekerMeasurement(
                true,
                new float3(-53f, 0f, 0f),
                float3.zero,
                new float3(107f, 0f, 0f),
                float3.zero);

            Assert.That(update.GuidancePosition.x, Is.EqualTo(3f).Within(1e-4f));
            Assert.That(update.FusePosition.x, Is.EqualTo(107f).Within(1e-4f));
            Assert.That(
                MissileEngagementMath.TryGetProximityPass(
                    update.GuidancePosition,
                    update.GuidanceVelocity,
                    0.06f,
                    12f,
                    out _,
                    out _),
                Is.True,
                "the old smoothed estimate reproduces the false fuze trigger");
            Assert.That(
                MissileEngagementMath.TryGetProximityPass(
                    update.FusePosition,
                    update.FuseVelocity,
                    0.06f,
                    12f,
                    out _,
                    out _),
                Is.False,
                "the fuze must remain on the latest measured target state");
        }

        [Test]
        public void MissileProximityFuse_RequiresFreshSeekerEvidenceForInterceptor()
        {
            Assert.That(
                MissileEngagementMath.CanAuthorizeProximityFuse(
                    false,
                    false,
                    10.0,
                    9.95,
                    0.12),
                Is.False);
            Assert.That(
                MissileEngagementMath.CanAuthorizeProximityFuse(
                    false,
                    true,
                    10.0,
                    5.0,
                    0.12),
                Is.True,
                "the existing air-to-air fuse lock behavior must remain unchanged");
            Assert.That(
                MissileEngagementMath.CanAuthorizeProximityFuse(
                    true,
                    false,
                    10.0,
                    9.95,
                    0.12),
                Is.False);
            Assert.That(
                MissileEngagementMath.CanAuthorizeProximityFuse(
                    true,
                    true,
                    10.0,
                    9.9,
                    0.12),
                Is.True);
            Assert.That(
                MissileEngagementMath.CanAuthorizeProximityFuse(
                    true,
                    true,
                    10.0,
                    9.7,
                    0.12),
                Is.False);
        }

        [Test]
        public void InterceptorInertialEstimate_CannotAuthorizeProximityFuse()
        {
            Assert.That(
                MissileEngagementMath.TryGetProximityPass(
                    new float3(3f, 0f, 0f),
                    new float3(-100f, 0f, 0f),
                    0.06f,
                    12f,
                    out _,
                    out _),
                Is.True,
                "the stale handoff estimate reproduces the geometric trigger");
            Assert.That(
                MissileEngagementMath.CanAuthorizeProximityFuse(
                    true,
                    false,
                    31.01,
                    30.2,
                    0.12),
                Is.False,
                "inertial guidance must not authorize the interceptor fuze");
        }

        [Test]
        public void MissileEventOrdering_PreservesEarlierDirectImpact()
        {
            Assert.That(
                MissileEngagementMath.ProximityOccursBeforeImpact(
                    0.012f,
                    0.008f),
                Is.False);
        }

        [Test]
        public void MissileEventOrdering_PrefersImpactWhenEventsCoincide()
        {
            Assert.That(
                MissileEngagementMath.ProximityOccursBeforeImpact(
                    0.01f,
                    0.01005f),
                Is.False);
        }

        [Test]
        public void MissileEventOrdering_AllowsEarlierProximityBurst()
        {
            Assert.That(
                MissileEngagementMath.ProximityOccursBeforeImpact(
                    0.005f,
                    0.012f),
                Is.True);
        }

        [Test]
        public void MissileLineOfSightRate_IsZeroForPureClosingMotion()
        {
            var rate = MissileEngagementMath.LineOfSightRate(
                new float3(0f, 0f, 1000f),
                new float3(0f, 0f, -200f));

            Assert.That(math.length(rate), Is.LessThan(1e-6f));
        }

        [Test]
        public void MissileLeadPoint_AimsAheadOfCrossingTarget()
        {
            var leadTime = MissileEngagementMath.SolveLeadTime(
                new float3(0f, 0f, 1000f),
                new float3(100f, 0f, 0f),
                500f,
                10f);
            var point = MissileEngagementMath.PredictLeadPoint(
                float3.zero,
                new float3(0f, 0f, 1000f),
                new float3(100f, 0f, 0f),
                500f,
                10f);

            Assert.That(leadTime, Is.GreaterThan(2f));
            Assert.That(leadTime, Is.LessThan(2.1f));
            Assert.That(point.x, Is.GreaterThan(200f));
            Assert.That(point.z, Is.EqualTo(1000f).Within(1e-4f));
        }

        [Test]
        public void MissileLeadTime_UsesBoundedFallbackForUnreachableTarget()
        {
            var leadTime = MissileEngagementMath.SolveLeadTime(
                new float3(0f, 0f, 1000f),
                new float3(900f, 0f, 0f),
                300f,
                4f);

            Assert.That(leadTime, Is.EqualTo(4f).Within(1e-4f));
        }

        [Test]
        public void MissileReachability_AcceptsClosingCoastIntercept()
        {
            var reachable = MissileEngagementMath.HasReachableIntercept(
                new float3(0f, 0f, 1000f),
                float3.zero,
                new float3(0f, 0f, 500f),
                5f,
                3000f,
                20f);

            Assert.That(reachable, Is.True);
        }

        [Test]
        public void MissileReachability_RejectsPassedTargetWithoutTurnDeltaV()
        {
            var reachable = MissileEngagementMath.HasReachableIntercept(
                new float3(0f, 0f, -1000f),
                float3.zero,
                new float3(0f, 0f, 500f),
                8f,
                5000f,
                100f);

            Assert.That(reachable, Is.False);
        }

        [Test]
        public void MissileReachability_AllowsPassedTargetWithTurnDeltaV()
        {
            var reachable = MissileEngagementMath.HasReachableIntercept(
                new float3(0f, 0f, -1000f),
                float3.zero,
                new float3(0f, 0f, 500f),
                8f,
                5000f,
                800f);

            Assert.That(reachable, Is.True);
        }

        [Test]
        public void MissileReachability_RespectsRemainingTravelDistance()
        {
            var reachable = MissileEngagementMath.HasReachableIntercept(
                new float3(0f, 0f, 1200f),
                float3.zero,
                new float3(0f, 0f, 500f),
                5f,
                900f,
                1000f);

            Assert.That(reachable, Is.False);
        }

        [Test]
        public void MissileClosestApproachCrossing_FindsMissedPassInsideTick()
        {
            var crossed = MissileEngagementMath.TryGetClosestApproachCrossing(
                new float3(10f, 5f, 0f),
                new float3(-20f, 0f, 0f),
                1f,
                out var time,
                out var distance);

            Assert.That(crossed, Is.True);
            Assert.That(time, Is.EqualTo(0.5f).Within(1e-5f));
            Assert.That(distance, Is.EqualTo(5f).Within(1e-5f));
        }

        [Test]
        public void MissileClosestApproachCrossing_RejectsStillClosingTarget()
        {
            var crossed = MissileEngagementMath.TryGetClosestApproachCrossing(
                new float3(100f, 0f, 0f),
                new float3(-10f, 0f, 0f),
                1f,
                out _,
                out _);

            Assert.That(crossed, Is.False);
        }
    }
}
