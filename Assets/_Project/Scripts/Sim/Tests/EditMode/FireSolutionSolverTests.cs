using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class FireSolutionSolverTests
    {
        [Test]
        public void ConfirmedFreshTrack_ProducesAuthorizedIntercept()
        {
            var track = ConfirmedTrack(lastUpdateTime: 1.0);

            var solution = FireSolutionSolver.Solve(
                track,
                double3.zero,
                projectileSpeed: 500.0,
                requestTime: 1.0);

            Assert.That(solution.Failure, Is.EqualTo(FireSolutionFailure.None));
            Assert.That(solution.WeaponReleaseAuthorized, Is.True);
            Assert.That(solution.InterceptPoint.z, Is.EqualTo(1_000.0).Within(0.01));
            Assert.That(solution.InterceptTime, Is.EqualTo(3.0).Within(0.01));
            Assert.That(solution.Quality, Is.GreaterThan(0.8f));
        }

        [Test]
        public void StaleTrack_DeniesWeaponRelease()
        {
            var track = ConfirmedTrack(lastUpdateTime: 1.0);

            var solution = FireSolutionSolver.Solve(
                track,
                double3.zero,
                projectileSpeed: 500.0,
                requestTime: 4.1);

            Assert.That(solution.Failure, Is.EqualTo(FireSolutionFailure.TrackTooOld));
            Assert.That(solution.WeaponReleaseAuthorized, Is.False);
        }

        [Test]
        public void PredictedTrackState_IsNotPropagatedAgainFromLastMeasurement()
        {
            var track = ConfirmedTrack(lastUpdateTime: 1.0);
            track.Position = new double3(0.0, 0.0, 1_000.0);
            track.Velocity = new double3(0.0, 0.0, 100.0);
            track.LastPredictTime = 2.0;

            Assert.That(
                track.PredictPosition(2.25).z,
                Is.EqualTo(1_025.0).Within(0.01));

            var solution = FireSolutionSolver.Solve(
                track,
                double3.zero,
                projectileSpeed: 500.0,
                requestTime: 2.0);

            Assert.That(solution.Failure, Is.EqualTo(FireSolutionFailure.None));
            Assert.That(solution.InterceptPoint.z, Is.EqualTo(1_248.0).Within(0.1));
            Assert.That(solution.InterceptTime, Is.EqualTo(4.48).Within(0.01));
        }

        [Test]
        public void MissileSolution_UsesEjectionAndBoostSegments()
        {
            var track = ConfirmedTrack(lastUpdateTime: 1.0);
            var kinematics = StandardMissileKinematics();

            var solution = FireSolutionSolver.SolveMissile(
                track,
                double3.zero,
                double3.zero,
                kinematics,
                requestTime: 1.0);

            var expectedFlightTime =
                (-40.0 + math.sqrt(40.0 * 40.0 + 600.0 * 1_000.0)) /
                300.0;
            Assert.That(solution.Failure, Is.EqualTo(FireSolutionFailure.None));
            Assert.That(solution.WeaponReleaseAuthorized, Is.True);
            Assert.That(
                solution.InterceptTime - 1.0,
                Is.EqualTo(expectedFlightTime).Within(0.001));
            Assert.That(solution.InterceptPoint.z, Is.EqualTo(1_000.0).Within(0.01));
        }

        [Test]
        public void MissileSolution_IsInvariantToSharedWorldVelocity()
        {
            var stationaryTrack = ConfirmedTrack(lastUpdateTime: 1.0);
            var movingTrack = ConfirmedTrack(lastUpdateTime: 1.0);
            movingTrack.Velocity = new double3(0.0, 0.0, 200.0);
            var kinematics = StandardMissileKinematics();

            var stationarySolution = FireSolutionSolver.SolveMissile(
                stationaryTrack,
                double3.zero,
                double3.zero,
                kinematics,
                requestTime: 1.0);
            var movingSolution = FireSolutionSolver.SolveMissile(
                movingTrack,
                double3.zero,
                new double3(0.0, 0.0, 200.0),
                kinematics,
                requestTime: 1.0);

            Assert.That(movingSolution.Failure, Is.EqualTo(FireSolutionFailure.None));
            Assert.That(
                movingSolution.InterceptTime,
                Is.EqualTo(stationarySolution.InterceptTime).Within(0.001));
            Assert.That(
                movingSolution.InterceptPoint.z,
                Is.GreaterThan(stationarySolution.InterceptPoint.z));
        }

        [Test]
        public void MissileSolution_ActivatesSeekerOnAcquisitionEnvelopeEntry()
        {
            var track = ConfirmedTrack(lastUpdateTime: 1.0);
            track.Position = new double3(0.0, 0.0, 20_000.0);
            var kinematics = StandardMissileKinematics();
            kinematics.SeekerRange = 8_000.0;

            var solution = FireSolutionSolver.SolveMissile(
                track,
                double3.zero,
                double3.zero,
                kinematics,
                requestTime: 1.0);

            var activationFlightTime =
                solution.SeekerActivationTime - 1.0;
            var remainingRange =
                20_000.0 -
                kinematics.RelativeTravelDistance(activationFlightTime);
            var acquisitionRange =
                FireSolutionSolver.CalculateReliableSeekerAcquisitionRange(
                    kinematics.SeekerRange,
                    math.cmax(solution.SearchBasketSemiAxes));

            Assert.That(solution.Failure, Is.EqualTo(FireSolutionFailure.None));
            Assert.That(activationFlightTime, Is.GreaterThan(0.0));
            Assert.That(
                solution.SeekerActivationTime,
                Is.LessThan(solution.InterceptTime - 2.0));
            Assert.That(
                remainingRange,
                Is.EqualTo(acquisitionRange).Within(0.1));
        }

        [Test]
        public void MissileSolution_TargetInsideAcquisitionEnvelope_ActivatesImmediately()
        {
            var track = ConfirmedTrack(lastUpdateTime: 1.0);
            track.Position = new double3(0.0, 0.0, 6_000.0);
            var kinematics = StandardMissileKinematics();
            kinematics.SeekerRange = 8_000.0;

            var solution = FireSolutionSolver.SolveMissile(
                track,
                double3.zero,
                double3.zero,
                kinematics,
                requestTime: 1.0);

            Assert.That(solution.Failure, Is.EqualTo(FireSolutionFailure.None));
            Assert.That(solution.SeekerActivationTime, Is.EqualTo(1.0).Within(0.001));
        }

        [Test]
        public void VariableMassMissile_UsesPropellantMassAndMassFlow()
        {
            var kinematics = new MissileKinematicProfile
            {
                EjectionSpeed = 40.0,
                InertMass = 136.0,
                MainPropellantMass = 90.0,
                MainEngineThrust = 63_000.0,
                MainMassFlow = 15.0,
            };

            var burnDuration = 6.0;
            var exhaustVelocity = 4_200.0;
            var massRatio = 226.0 / 136.0;
            var expectedBurnoutSpeed =
                40.0 + exhaustVelocity * math.log(massRatio);
            var expectedBurnDistance =
                40.0 * burnDuration +
                exhaustVelocity *
                (burnDuration -
                 136.0 / 15.0 * math.log(massRatio));

            Assert.That(kinematics.UsesVariableMass, Is.True);
            Assert.That(
                kinematics.EffectiveMotorBurnDuration,
                Is.EqualTo(burnDuration).Within(0.0001));
            Assert.That(
                kinematics.BurnoutSpeed,
                Is.EqualTo(expectedBurnoutSpeed).Within(0.001));
            Assert.That(
                kinematics.RelativeTravelDistance(burnDuration),
                Is.EqualTo(expectedBurnDistance).Within(0.001));
            Assert.That(
                kinematics.RelativeTravelDistance(burnDuration + 2.0),
                Is.EqualTo(
                    expectedBurnDistance +
                    expectedBurnoutSpeed * 2.0).Within(0.001));
        }

        [Test]
        public void GunSolution_SubtractsInheritedShooterVelocity()
        {
            var stationaryTrack = ConfirmedTrack(lastUpdateTime: 1.0);
            var sharedVelocityTrack =
                ConfirmedTrack(lastUpdateTime: 1.0);
            sharedVelocityTrack.Velocity =
                new double3(0.0, 0.0, 250.0);

            var stationarySolution = FireSolutionSolver.Solve(
                stationaryTrack,
                double3.zero,
                double3.zero,
                projectileSpeed: 1500.0,
                requestTime: 1.0);
            var sharedVelocitySolution = FireSolutionSolver.Solve(
                sharedVelocityTrack,
                double3.zero,
                new double3(0.0, 0.0, 250.0),
                projectileSpeed: 1500.0,
                requestTime: 1.0);

            Assert.That(
                sharedVelocitySolution.InterceptTime,
                Is.EqualTo(
                    stationarySolution.InterceptTime)
                    .Within(0.001));
            Assert.That(
                sharedVelocitySolution.InterceptPoint.z,
                Is.GreaterThan(
                    stationarySolution.InterceptPoint.z));
            Assert.That(
                sharedVelocitySolution.MidcourseAimPoint.z,
                Is.EqualTo(
                    stationarySolution.MidcourseAimPoint.z)
                    .Within(0.001));

            var timeOfFlight =
                sharedVelocitySolution.InterceptTime - 1.0;
            var launchDirection = math.normalize(
                sharedVelocitySolution.MidcourseAimPoint);
            var projectilePositionAtIntercept =
                (new double3(0.0, 0.0, 250.0) +
                 launchDirection * 1500.0) *
                timeOfFlight;
            Assert.That(
                math.distance(
                    projectilePositionAtIntercept,
                    sharedVelocitySolution.InterceptPoint),
                Is.LessThan(0.001));
        }

        [Test]
        public void BallisticIntercept_LeadsAnAcceleratingTarget()
        {
            var relativePosition =
                new double3(0.0, 0.0, 1_500.0);
            var relativeVelocity =
                new double3(0.0, 0.0, -300.0);
            var acceleration =
                new double3(200.0, 0.0, 0.0);

            var solved =
                FireSolutionSolver.TrySolveBallisticIntercept(
                    relativePosition,
                    relativeVelocity,
                    acceleration,
                    projectileSpeed: 1_500.0,
                    maximumFlightTime: 6.0,
                    out var flightTime,
                    out var relativeInterceptPoint);

            Assert.That(solved, Is.True);
            Assert.That(flightTime, Is.GreaterThan(0.7));
            Assert.That(relativeInterceptPoint.x, Is.GreaterThan(50.0));
            Assert.That(
                math.length(relativeInterceptPoint),
                Is.EqualTo(1_500.0 * flightTime)
                    .Within(0.001));
        }

        [Test]
        public void BallisticIntercept_AccountsForGrowingRocketAcceleration()
        {
            var relativePosition =
                new double3(0.0, 0.0, 2_500.0);
            var relativeVelocity =
                new double3(0.0, 0.0, -500.0);
            var acceleration =
                new double3(257.0, 0.0, 0.0);

            FireSolutionSolver.TrySolveBallisticIntercept(
                relativePosition,
                relativeVelocity,
                acceleration,
                projectileSpeed: 1_500.0,
                maximumFlightTime: 6.0,
                out _,
                out var constantAccelerationPoint);
            var solved =
                FireSolutionSolver.TrySolveBallisticIntercept(
                    relativePosition,
                    relativeVelocity,
                    acceleration,
                    new double3(20.0, 0.0, 0.0),
                    maximumTargetAcceleration: 600.0,
                    jerkProjectionTime: 0.6,
                    projectileSpeed: 1_500.0,
                    maximumFlightTime: 6.0,
                    out var flightTime,
                    out var growingAccelerationPoint);

            Assert.That(solved, Is.True);
            Assert.That(
                growingAccelerationPoint.x,
                Is.GreaterThan(constantAccelerationPoint.x));
            Assert.That(
                math.length(growingAccelerationPoint),
                Is.EqualTo(1_500.0 * flightTime)
                    .Within(0.001));
        }

        [Test]
        public void InterceptorSolution_CanAuthorizeBeforeTwelveKilometers()
        {
            var track = new MissileTrackSnapshot
            {
                TrackId = new TrackId(new Handle(2, 1)),
                Position = new double3(0.0, 0.0, 30_000.0),
                Velocity = new double3(0.0, 0.0, -1_500.0),
                PositionUncertaintyMeters = 50f,
                CpaUncertaintyMeters = 100f,
                Confidence = 0.9f,
                Quality = MissileTrackQuality.WeaponsQuality,
                Confirmed = true,
                Incoming = true,
                TimeToClosestApproachSeconds = 20f,
                ClosestApproachDistanceMeters = 0f,
                LastEvidenceTime = 1.0,
                LastUpdateTime = 1.0,
            };
            var kinematics = new MissileKinematicProfile
            {
                EjectionSpeed = 55.0,
                InertMass = 58.0,
                MainPropellantMass = 32.0,
                MainEngineThrust = 48_000.0,
                MainMassFlow = 16.0,
            };

            var solution = FireSolutionSolver.SolveInterceptor(
                track,
                double3.zero,
                double3.zero,
                new float3(0f, 0f, 1f),
                kinematics,
                requestTime: 1.0,
                maximumAngularRateDegreesPerSecond: 600f,
                maximumFlightTimeSeconds: 40f,
                maximumTravelDistanceMeters: 15_000f);

            Assert.That(solution.Reachable, Is.True);
            Assert.That(solution.RequiredTravelMeters, Is.GreaterThan(12_000f));
            Assert.That(solution.FlightTimeSeconds, Is.LessThan(20f));
            Assert.That(solution.ThreatTimeMarginSeconds, Is.GreaterThan(0f));
        }

        [Test]
        public void InterceptorSolution_ColdLaunchDelayIncreasesFlightTime()
        {
            var track = new MissileTrackSnapshot
            {
                TrackId = new TrackId(new Handle(3, 1)),
                Position = new double3(0.0, 0.0, 8_000.0),
                Velocity = new double3(0.0, 0.0, -900.0),
                Confidence = 1f,
                Quality = MissileTrackQuality.WeaponsQuality,
                Confirmed = true,
                Incoming = true,
                TimeToClosestApproachSeconds = 8.8f,
                LastEvidenceTime = 1.0,
                LastUpdateTime = 1.0,
            };
            var kinematics = new MissileKinematicProfile
            {
                EjectionSpeed = 55.0,
                InertMass = 58.0,
                MainPropellantMass = 32.0,
                MainEngineThrust = 48_000.0,
                MainMassFlow = 16.0,
            };

            var direct = FireSolutionSolver.SolveInterceptor(
                track,
                double3.zero,
                double3.zero,
                new float3(0f, 0f, 1f),
                kinematics,
                requestTime: 1.0,
                maximumAngularRateDegreesPerSecond: 600f,
                maximumFlightTimeSeconds: 40f,
                maximumTravelDistanceMeters: 15_000f);
            var coldLaunch = FireSolutionSolver.SolveInterceptor(
                track,
                double3.zero,
                double3.zero,
                new float3(0f, 0f, 1f),
                kinematics,
                requestTime: 1.0,
                maximumAngularRateDegreesPerSecond: 600f,
                maximumFlightTimeSeconds: 40f,
                maximumTravelDistanceMeters: 15_000f,
                minimumMainEngineIgnitionDelaySeconds: 0.25f);

            Assert.That(direct.Reachable, Is.True);
            Assert.That(coldLaunch.Reachable, Is.True);
            Assert.That(
                coldLaunch.FlightTimeSeconds,
                Is.GreaterThan(direct.FlightTimeSeconds));
        }

        private static MissileKinematicProfile StandardMissileKinematics()
        {
            return new MissileKinematicProfile
            {
                EjectionSpeed = 40.0,
                BoostAcceleration = 300.0,
                MotorBurnDuration = 3.5,
            };
        }

        private static TrackSnapshot ConfirmedTrack(double lastUpdateTime)
        {
            var track = new TrackSnapshot
            {
                TrackId = new TrackId(new Handle(0, 1)),
                Position = new double3(0.0, 0.0, 1_000.0),
                Velocity = double3.zero,
                Confidence = 1f,
                LastUpdateTime = lastUpdateTime,
                LastPredictTime = lastUpdateTime,
                HitCount = 3,
                SourceMask = TrackSourceMask.Radar,
                Lifecycle = TrackLifecycle.Confirmed,
            };
            for (var axis = 0; axis < 3; axis++)
            {
                track.SetCovariance(axis, axis, 4.0);
                track.SetCovariance(axis + 3, axis + 3, 1.0);
            }

            return track;
        }
    }
}
