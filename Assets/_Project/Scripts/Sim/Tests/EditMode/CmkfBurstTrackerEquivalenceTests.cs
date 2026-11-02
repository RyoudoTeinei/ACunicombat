using ACunicombat.Tracking;
using NUnit.Framework;

namespace ACunicombat.Tests.EditMode
{
    /// <summary>The Burst-compiled filter kernels must reproduce the managed tracker decisions.</summary>
    public sealed class CmkfBurstTrackerEquivalenceTests
    {
        [Test]
        public void CrowdedTracker_BurstMatchesManaged()
        {
            try
            {
                CmkfBurst.Enabled = false;
                var managed = RadarTrackManagerCrowdBenchmark.Run();
                CmkfBurst.Enabled = true;
                RadarTrackManagerCrowdBenchmark.Run();
                var burst = RadarTrackManagerCrowdBenchmark.Run();
                TestContext.WriteLine($"BURST tracker managed={managed.Milliseconds:F1}ms burst={burst.Milliseconds:F1}ms");
                Assert.That(burst.Fingerprint, Is.EqualTo(managed.Fingerprint));
            }
            finally { CmkfBurst.Enabled = true; }
        }
    }
}
