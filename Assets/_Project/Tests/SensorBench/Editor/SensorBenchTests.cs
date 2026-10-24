using System;
using System.IO;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using ACunicombat.FireControl;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace ACunicombat.SensorBench
{
    [Serializable] public sealed class ScenarioSpec
    {
        public int schemaVersion;
        public string scenarioId;
        public string mode;
        public int[] seeds;
        public double stepSeconds, durationSeconds, warmupSeconds, jamStartSeconds, jamEndSeconds;
        public float jamToSignalDb, targetRangeMeters, targetCrossSection, projectileSpeed;
        public int observerCount;
        public string[] requiredCapabilities;
        public void Validate()
        {
            if (schemaVersion != 1 || string.IsNullOrWhiteSpace(scenarioId) || mode != "ReceiverInjection")
                throw new ArgumentException("Unsupported scenario schema or mode.");
            if (seeds == null || seeds.Length == 0 || seeds.Length > 100 || new HashSet<int>(seeds).Count != seeds.Length)
                throw new ArgumentException("Supply 1..100 unique seeds.");
            if (!(stepSeconds >= .01 && stepSeconds <= .2 && durationSeconds > 0 && durationSeconds <= 60 &&
                  warmupSeconds >= 0 && warmupSeconds < jamStartSeconds && jamStartSeconds < jamEndSeconds &&
                  jamEndSeconds < durationSeconds && observerCount >= 1 && observerCount <= 8 &&
                  targetRangeMeters >= 1000 && targetRangeMeters <= 20000 && targetCrossSection > 0 &&
                  projectileSpeed > 1 && jamToSignalDb >= -100 && jamToSignalDb <= 100))
                throw new ArgumentException("Invalid or unbounded scenario parameters.");
            if (requiredCapabilities == null || requiredCapabilities.Length == 0)
                throw new ArgumentException("Required capabilities must be explicit.");
            foreach (var item in requiredCapabilities)
                if (item != "Radar" && item != "Tracking" && item != "ReceiverInjection" && item != "FireControl")
                    throw new NotSupportedException("Required capability not implemented: " + item);
        }
    }
    [Serializable] public sealed class BenchRow
    {
        public int schemaVersion = 1, seed, step, observer;
        public string condition, kind, lifecycle, reason;
        public double time, lastUpdate, lastPredict;
        public double[] position, velocity, direction;
        public double range, radialVelocity, angleVariance, rangeVariance, snrDb, signalDb, jamDb;
        public bool covered, detected, authorized, jamActive;
        public uint trackId;
    }
    public sealed class SensorBenchTests
    {
        [Test] public void RunConfiguredScenario()
        {
            var output = Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT");
            if (string.IsNullOrEmpty(output)) Assert.Ignore("Run with Tools/SensorBench/Run-SensorBench.ps1.");
            var spec = JsonUtility.FromJson<ScenarioSpec>(File.ReadAllText(Path.Combine(output, "scenario.json")));
            spec.Validate();
            using var truth = new StreamWriter(Path.Combine(output, "truth.jsonl"));
            using var observations = new StreamWriter(Path.Combine(output, "observations.jsonl"));
            using var tracks = new StreamWriter(Path.Combine(output, "tracks.jsonl"));
            using var decisions = new StreamWriter(Path.Combine(output, "decisions.jsonl"));
            foreach (var seed in spec.seeds)
                foreach (var condition in new[] { "control", "jammed" })
                    Run(spec, seed, condition, truth, observations, tracks, decisions);
        }

        private static void Run(ScenarioSpec spec, int seed, string condition, StreamWriter truth,
            StreamWriter observations, StreamWriter tracks, StreamWriter decisions)
        {
            using var world = new SimWorld();
            using var grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
            using var scheduler = new DwellScheduler(64);
            using var radar = new RadarSensorSystem(world, grid, scheduler) { SessionSeed = (uint)seed };
            var contexts = new PlatformTrackContext[spec.observerCount];
            var profiles = new RadarSensorProfile[contexts.Length];
            var origins = new double3[contexts.Length];
            var ownerIds = new SimEntityId[contexts.Length];
            var targetVelocity = new double3(25, 0, -30);
            var targetStart = new double3(0, 0, spec.targetRangeMeters);
            var targetId = world.Register(State(targetStart, targetVelocity));
            radar.RegisterTarget(new RadarTargetProfile { EntityId = targetId,
                RadarCrossSectionSquareMeters = spec.targetCrossSection,
                ObjectClass = TrackObjectClass.AirVehicle, ClassificationSignature = 1f });
            try
            {
                for (var i = 0; i < contexts.Length; i++)
                {
                    origins[i] = new double3(i * 500, 0, 0);
                    ownerIds[i] = world.Register(State(origins[i], double3.zero));
                    profiles[i] = Profile(i, ownerIds[i]);
                    radar.RegisterSensor(profiles[i]);
                    contexts[i] = new PlatformTrackContext();
                    contexts[i].BindPrimaryRadar(in profiles[i]);
                }
                var steps = (int)Math.Round(spec.durationSeconds / spec.stepSeconds);
                for (var step = 1; step <= steps; step++)
                {
                    var time = step * spec.stepSeconds;
                    var position = targetStart + targetVelocity * time;
                    world.TrySetState(targetId, State(position, targetVelocity));
                    var rows = new BenchRow[contexts.Length];
                    for (var i = 0; i < contexts.Length; i++)
                    {
                        world.TrySetState(ownerIds[i], State(origins[i], double3.zero));
                        // Fixed scenario boresight, not truth-following beam steering.
                        var boresight = math.normalize((float3)(targetStart - origins[i]));
                        var relative = position - origins[i];
                        var range = math.length(relative);
                        var direction = relative / range;
                        var profile = profiles[i];
                        var covered = range <= profile.MaxRangeMeters &&
                            math.dot(direction, boresight) >= math.cos(profile.DwellConfig.BeamWidthRadians * .5);
                        var signal = RadarMath.ReceivedPowerDb(profile.DwellConfig,
                            10f * math.log10(spec.targetCrossSection), range);
                        // J/S is receiver-side power injection; no emitter propagation is claimed.
                        // Only observer 0 is jammed. Other observers are an isolation control.
                        var jam = condition == "jammed" && i == 0 &&
                            time >= spec.jamStartSeconds && time < spec.jamEndSeconds;
                        if (jam) radar.JamInboxForEw.Add(new JamContribution {
                            VictimSensorId = profile.SensorId, SourceEmitterId = new EmitterId(new Handle(50, 1)),
                            ArrivalDirection = boresight, FrequencyBand = profile.FrequencyBand,
                            PowerDb = signal + spec.jamToSignalDb });
                        radar.ScheduleDwell(new Dwell { SensorId = profile.SensorId, DueTime = time,
                            DwellTimeSeconds = .008f, BeamDirection = boresight,
                            BeamWidthRadians = profile.DwellConfig.BeamWidthRadians,
                            Priority = DwellPriority.TrackWhileScan, Sequence = (ulong)step });
                        rows[i] = new BenchRow { seed = seed, step = step, observer = i, condition = condition,
                            kind = "truth", time = time, position = Vec(position), velocity = Vec(targetVelocity),
                            direction = Vec(direction), range = range,
                            radialVelocity = math.dot(targetVelocity, direction), covered = covered,
                            signalDb = signal, jamDb = jam ? signal + spec.jamToSignalDb : -300,
                            jamActive = jam };
                        Write(truth, rows[i]);
                    }
                    radar.Tick(time);
                    for (var i = 0; i < contexts.Length; i++)
                    {
                        var context = contexts[i];
                        context.Process(radar.Detections, radar.Emissions, origins[i], double3.zero, time);
                        var count = 0;
                        for (var j = 0; j < radar.Detections.Length; j++)
                        {
                            var measurement = radar.Detections[j];
                            if (!measurement.SensorId.Equals(profiles[i].SensorId)) continue;
                            count++;
                            var row = Row(seed, step, i, condition, time, "measurement");
                            row.time = measurement.Timestamp;
                            row.detected = true; row.range = measurement.Range;
                            row.direction = Vec(measurement.Direction); row.radialVelocity = measurement.RadialVelocity;
                            row.angleVariance = measurement.AngleVar; row.rangeVariance = measurement.RangeVar;
                            row.snrDb = measurement.SnrDb;
                            Write(observations, row);
                        }
                        Assert.That(count, Is.LessThanOrEqualTo(1), "Single-target scenario must not silently choose the best observation.");
                        if (count == 0) Write(observations, Row(seed, step, i, condition, time, "no_detection"));
                        var trackRow = Row(seed, step, i, condition, time, "no_track");
                        var decision = Row(seed, step, i, condition, time, "fire_control");
                        decision.reason = "NoTrack";
                        Assert.That(context.Count, Is.LessThanOrEqualTo(1), "Track fragmentation requires explicit multi-track evaluation.");
                        if (context.TryGetBestTrack(out var track))
                        {
                            trackRow.kind = "track"; trackRow.trackId = track.TrackId.Packed;
                            trackRow.position = Vec(track.PredictPosition(time)); trackRow.velocity = Vec(track.Velocity);
                            trackRow.lifecycle = track.Lifecycle.ToString();
                            trackRow.lastUpdate = track.LastUpdateTime; trackRow.lastPredict = track.LastPredictTime;
                            var solution = FireSolutionSolver.Solve(in track, origins[i], spec.projectileSpeed, time);
                            decision.trackId = track.TrackId.Packed;
                            decision.authorized = solution.WeaponReleaseAuthorized;
                            decision.reason = solution.Failure.ToString();
                        }
                        Write(tracks, trackRow); Write(decisions, decision);
                    }
                }
            }
            finally { foreach (var context in contexts) context?.Dispose(); }
        }
        private static BenchRow Row(int seed, int step, int observer, string condition, double time, string kind) =>
            new BenchRow { seed = seed, step = step, observer = observer, condition = condition, time = time, kind = kind };
        private static void Write(StreamWriter writer, BenchRow row) => writer.WriteLine(JsonUtility.ToJson(row));
        private static double[] Vec(double3 value) => new[] { value.x, value.y, value.z };
        private static KinematicState State(double3 p, double3 v) =>
            new KinematicState { PositionWorld = p, VelocityWorld = v, Rotation = quaternion.identity, Mass = 1 };
        public static RadarSensorProfile Profile(int index, SimEntityId owner) => new RadarSensorProfile {
            SensorId = new SensorId(new Handle(index, 1)), EmitterId = new EmitterId(new Handle(index, 1)),
            PlatformId = new PlatformId(new Handle(index, 1)), OwnerEntityId = owner,
            DwellConfig = new RadarDwellConfig { ConstantsDb = 215f, IntegrationGainDb = 6f, NoisePowerDb = 0f,
                DetectThresholdLowDb = 3f, DetectThresholdHighDb = 10f, RangeResolutionMeters = 12f,
                BeamWidthRadians = math.radians(12f), VelocityResolutionMps = 2f },
            Capability = SensorCapability.Range | SensorCapability.Doppler | SensorCapability.LosRate,
            MaxRangeMeters = 60000, FrequencyBand = 1, WaveformId = 1, MainlobePowerDb = 70,
            SidelobeLevelDb = -25, ClutterPowerDb = -30,
            Resolutions = new ResolutionMerge.Resolutions { RangeMeters = 12f, AngleRadians = .005f, VelocityMps = 2f }
        };
    }
}