using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class ElectroOpticalCuePolicyTests
    {
        [Test]
        public void EarliestFreshNonFriendlyCpaWinsOverFriendlyAndLaterThreats()
        {
            var tracks = new[]
            {
                Track(1, 1, true),
                Track(2, 5, false),
                Track(3, 3, false),
            };

            Assert.That(ElectroOpticalCuePolicy.TrySelectThreat(
                tracks, tracks.Length, double3.zero, 10, 5000,
                out var selected, out var position), Is.True);
            Assert.That(selected, Is.EqualTo(tracks[2].TrackId));
            Assert.That(position.z, Is.EqualTo(1000).Within(.001));
        }

        [Test]
        public void StaleOrNonInterceptGeometryDoesNotCaptureOpticalCue()
        {
            var stale = Track(1, 1, false);
            stale.LastEvidenceTime = 8;
            var missed = Track(2, 2, false);
            missed.ClosestApproachDistanceMeters = 5000;
            var tracks = new[] { stale, missed };

            Assert.That(ElectroOpticalCuePolicy.TrySelectThreat(
                tracks, tracks.Length, double3.zero, 10, 5000,
                out _, out _), Is.False);
        }

        private static MissileTrackSnapshot Track(int id, float cpa, bool friendly)
        {
            return new MissileTrackSnapshot
            {
                TrackId = new TrackId(new Handle(id, 1)),
                Position = new double3(0, 0, 1000),
                Affiliation = friendly ? TrackAffiliation.Friendly : TrackAffiliation.Unknown,
                IdentityConfidence = friendly ? 1f : 0f,
                Incoming = true,
                TimeToClosestApproachSeconds = cpa,
                ClosestApproachDistanceMeters = 100,
                LastEvidenceTime = 10,
                KinematicUpdateTime = 10,
                LastUpdateTime = 10,
            };
        }
    }
}
