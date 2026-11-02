using ACunicombat.Tracking;
using NUnit.Framework;

/// <summary>Burst filter kernels must reproduce the managed decisions on the pinned workloads.</summary>
public sealed class CmkfBurstEquivalenceTests
{
    [Test]
    public void CooperativeFusion_BurstMatchesManaged()
    {
        try
        {
            CmkfBurst.Enabled = false;
            var managed = MechCooperativeFusionBenchmark.Run();
            CmkfBurst.Enabled = true;
            MechCooperativeFusionBenchmark.Run();
            var burst = MechCooperativeFusionBenchmark.Run();
            CmkfBurst.PairScoresEnabled = false;
            var noPair = MechCooperativeFusionBenchmark.Run();
            CmkfBurst.PairScoresEnabled = true;
            TestContext.WriteLine($"BURST coop withoutPairBatch={noPair.FusionMs + noPair.MergeMs:F1}ms");
            Assert.That(noPair.Fingerprint, Is.EqualTo(managed.Fingerprint));
            TestContext.WriteLine($"BURST coop managed={managed.FusionMs + managed.MergeMs:F1}ms burst={burst.FusionMs + burst.MergeMs:F1}ms");
            Assert.That(burst.Fingerprint, Is.EqualTo(managed.Fingerprint));
        }
        finally { CmkfBurst.Enabled = true; }
    }
}
