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
