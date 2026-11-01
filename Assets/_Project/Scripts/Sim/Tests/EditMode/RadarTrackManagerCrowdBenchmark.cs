using System.Diagnostics;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    /// <summary>
    /// Crowded-battle tracker workload: one sensor, ~30 targets in formations, every dwell
    /// returning a resolved batch (shared DwellSequence) as in large stress scenes.
    /// The decision fingerprint pins association behaviour across performance work.
    /// </summary>
    public sealed class RadarTrackManagerCrowdBenchmark
    {
        public const int Targets = 30;
        public const int Ticks = 200;
        public const double Step = 0.04;

        public struct Result { public double Milliseconds; public ulong Fingerprint; public int Tracks; }

        public static Result Run(int seed = 20261101)
        {
            var rng = new Random((uint)seed);
            var manager = new RadarTrackManager();
            var positions = new double3[Targets];
            var velocities = new double3[Targets];
            var classes = new TrackObjectClass[Targets];
            for (int i = 0; i < Targets; i++)
            {
                int formation = i / 4, slot = i % 4;
                positions[i] = new double3(formation * 1800 - 6000 + slot * 120, 1500 + slot * 40, 20000 + formation * 900);
                velocities[i] = new double3(rng.NextDouble(-40, 40), 0, -220);
                classes[i] = i % 3 == 0 ? TrackObjectClass.Missile : TrackObjectClass.AirVehicle;
                if (classes[i] == TrackObjectClass.Missile) velocities[i] = new double3(rng.NextDouble(-60, 60), 0, -700);
            }
            var sensor = new SensorId(new Handle(0, 1));
            using var measurements = new NativeList<Measurement>(Targets, Allocator.Persistent);
            ulong fingerprint = 1469598103934665603UL;
            var snapshot = new TrackSnapshot[RadarTrackManager.MaxTracks];
            var watch = new Stopwatch();
            for (int tick = 1; tick <= Ticks; tick++)
            {
                double time = tick * Step;
                measurements.Clear();
                for (int i = 0; i < Targets; i++)
                {
                    var p = positions[i] + velocities[i] * time + new double3(rng.NextDouble(-15, 15), rng.NextDouble(-15, 15), rng.NextDouble(-15, 15));
                    double range = math.length(p);
                    var direction = (float3)(p / range);
                    measurements.Add(new Measurement
                    {
                        SensorId = sensor, Timestamp = time, Range = (float)range, Direction = direction,
                        RadialVelocity = (float)math.dot(velocities[i], p / range), LosRate = float3.zero,
                        RangeVar = 25f, AngleVar = 4e-6f, VelocityVar = 4f, LosRateVar = 1e-6f, SnrDb = 18f,
                        ObjectClass = classes[i], ClassificationConfidence = .9f, DwellSequence = (uint)tick,
                        Flags = MeasurementFlags.None,
                    });
                }
                watch.Start();
                manager.Process(measurements, double3.zero, time);
                watch.Stop();
                int count = manager.CopyTracks(snapshot);
                for (int t = 0; t < count; t++)
                {
                    fingerprint = (fingerprint ^ snapshot[t].TrackId.Packed) * 1099511628211UL;
                    fingerprint = (fingerprint ^ (ulong)(long)math.round(snapshot[t].Position.x)) * 1099511628211UL;
                    fingerprint = (fingerprint ^ (ulong)snapshot[t].Lifecycle) * 1099511628211UL;
                }
                fingerprint = (fingerprint ^ (ulong)count) * 1099511628211UL;
            }
            int finalCount = manager.CopyTracks(snapshot);
            return new Result { Milliseconds = watch.Elapsed.TotalMilliseconds, Fingerprint = fingerprint, Tracks = finalCount };
        }

        /// <summary>
        /// Many independent observers advanced in parallel on worker threads must produce the
        /// same decisions as one observer alone: trackers share no mutable state.
        /// </summary>
        [Test]
        public void IndependentTrackersInParallel_MatchSerialFingerprint()
        {
            const int observers = 16;
            var results = new Result[observers];
            var watch = Stopwatch.StartNew();
            System.Threading.Tasks.Parallel.For(0, observers, i => results[i] = Run());
            watch.Stop();
            double serial = 0;
            for (int i = 0; i < observers; i++)
            {
                Assert.That(results[i].Fingerprint, Is.EqualTo(ReferenceFingerprint), $"Observer {i} diverged on a worker thread");
                serial += results[i].Milliseconds;
            }
            TestContext.WriteLine($"PARALLEL_BENCH observers={observers} wall={watch.Elapsed.TotalMilliseconds:F0}ms summedTracker={serial:F0}ms speedup={serial / watch.Elapsed.TotalMilliseconds:F1}x");
        }

        /// <summary>Decisions of the reference managed tracker (commit 2026-11-02). Any tracker
        /// rewrite (Burst, parallel) must reproduce it: same tracks, lifecycles and positions.</summary>
        public const ulong ReferenceFingerprint = 17645715689144348346UL;

        [Test]
        public void CrowdedBatchWorkload_ReportsCostAndFingerprint()
        {
            var r = Run();
            TestContext.WriteLine($"CROWD_BENCH ms={r.Milliseconds:F1} perTick={r.Milliseconds / Ticks:F3} tracks={r.Tracks} fingerprint={r.Fingerprint}");
            Assert.That(r.Tracks, Is.GreaterThan(Targets / 2), "Tracker should hold most crowded targets");
            Assert.That(r.Fingerprint, Is.EqualTo(ReferenceFingerprint), "Association decisions changed");
        }
    }
}
