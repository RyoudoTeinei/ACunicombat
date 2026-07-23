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
