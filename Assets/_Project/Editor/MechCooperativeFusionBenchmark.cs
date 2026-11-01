using System.Diagnostics;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;

/// <summary>
/// Crowded cooperative-fusion workload (one observer in a 20-mech battle): ~50 local radar
/// tracks, 19 friendly navigation reports and shared target reports from 6 senders. The
/// decision fingerprint pins association and merge results across performance work.
/// </summary>
public sealed class MechCooperativeFusionBenchmark
{
    public const int Ticks = 120;
    public const double Step = 0.04;

    public struct Result { public double FusionMs, MergeMs; public ulong Fingerprint; public int Contacts; }

    private static TrackSnapshot Track(int id, double3 position, double3 velocity, double time, double positionVariance, double velocityVariance)
    {
        var t = new TrackSnapshot { TrackId = new TrackId(new Handle(id, 1)), Position = position, Velocity = velocity, LastUpdateTime = time, LastPredictTime = time,
            ObjectClass = TrackObjectClass.AirVehicle, Lifecycle = TrackLifecycle.Confirmed, Confidence = .9f, SourceMask = TrackSourceMask.Radar };
        for (int i = 0; i < 3; i++) { t.SetCovariance(i, i, positionVariance); t.SetCovariance(i + 3, i + 3, velocityVariance); }
        t.SourceEvidence.Record(TrackSourceMask.Radar, .9f, time);
        return t;
    }

    public static Result Run(uint seed = 20261102)
    {
        var rng = new Random(seed);
        const int friends = 19, hostiles = 31, senders = 6, sharedPerSender = 8;
        var truthPosition = new double3[friends + hostiles];
        var truthVelocity = new double3[friends + hostiles];
        for (int i = 0; i < truthPosition.Length; i++)
        {
            bool friend = i < friends;
            truthPosition[i] = new double3(rng.NextDouble(-8000, 8000), 1500 + rng.NextDouble(-300, 300), friend ? rng.NextDouble(-14000, -9000) : rng.NextDouble(9000, 14000));
            truthVelocity[i] = new double3(rng.NextDouble(-60, 60), 0, friend ? 180 : -180);
        }
        var fusion = new MechCooperativeTrackFusion();
        var shared = new MechSharedTargetTracks();
        var local = new TrackSnapshot[96];
        var output = new TrackSnapshot[96];
        var reports = new System.Collections.Generic.List<TrackSnapshot>(friends);
        var fusionWatch = new Stopwatch(); var mergeWatch = new Stopwatch();
        ulong fingerprint = 1469598103934665603UL; int lastCount = 0;
        for (int tick = 1; tick <= Ticks; tick++)
        {
            double now = tick * Step;
            int localCount = 0;
            for (int i = 0; i < truthPosition.Length; i++)
            {
                var p = truthPosition[i] + truthVelocity[i] * now + new double3(rng.NextDouble(-60, 60), rng.NextDouble(-60, 60), rng.NextDouble(-60, 60));
                local[localCount++] = Track(i, p, truthVelocity[i], now, 250000, 10000);
            }
            reports.Clear();
            for (int f = 0; f < friends; f++)
            {
                var r = Track(1000 + f, truthPosition[f] + truthVelocity[f] * now, truthVelocity[f], now, 4, 25);
                r.Affiliation = TrackAffiliation.Friendly; r.IdentityConfidence = 1; r.SourceMask = TrackSourceMask.DataLink;
                r.SourceEvidence = default; r.SourceEvidence.Record(TrackSourceMask.DataLink, 1, now); r.IsIdentifiedMech = true;
                reports.Add(r);
            }
            fusionWatch.Start();
            fusion.Update(local, localCount, reports, now);
            int count = fusion.CopyTo(output);
            fusionWatch.Stop();
            for (int s = 0; s < senders; s++)
                for (int k = 0; k < sharedPerSender; k++)
                {
                    int target = friends + (s * 5 + k) % hostiles;
                    var p = truthPosition[target] + truthVelocity[target] * now + new double3(rng.NextDouble(-80, 80), 0, rng.NextDouble(-80, 80));
                    shared.Receive(new LinkTerminalId(new Handle(s + 1, 1)), Track(500 + k, p, truthVelocity[target], now, 40000, 2500), now);
                }
            mergeWatch.Start();
            count = shared.Merge(output, count, now);
            mergeWatch.Stop();
            for (int i = 0; i < count; i++)
            {
                fingerprint = (fingerprint ^ output[i].TrackId.Packed) * 1099511628211UL;
                fingerprint = (fingerprint ^ (ulong)(long)math.round(output[i].Position.x * 10)) * 1099511628211UL;
                fingerprint = (fingerprint ^ (ulong)output[i].Affiliation ^ ((ulong)output[i].SourceMask << 8)) * 1099511628211UL;
            }
            fingerprint = (fingerprint ^ (ulong)count) * 1099511628211UL;
            lastCount = count;
        }
        return new Result { FusionMs = fusionWatch.Elapsed.TotalMilliseconds, MergeMs = mergeWatch.Elapsed.TotalMilliseconds, Fingerprint = fingerprint, Contacts = lastCount };
    }

    [Test]
    public void CrowdedCooperativeFusion_ReportsCostAndFingerprint()
    {
        var r = Run();
        TestContext.WriteLine($"COOP_BENCH fusion={r.FusionMs:F1}ms merge={r.MergeMs:F1}ms perTick={(r.FusionMs + r.MergeMs) / Ticks:F3}ms contacts={r.Contacts} fingerprint={r.Fingerprint}");
        Assert.That(r.Contacts, Is.GreaterThan(40));
        Assert.That(r.Fingerprint, Is.EqualTo(ReferenceFingerprint), "Cooperative association or merge decisions changed");
    }

    /// <summary>Output of the original LINQ / per-pair-prediction implementation (2026-11-02).</summary>
    public const ulong ReferenceFingerprint = 8387894055748096445UL;
}
