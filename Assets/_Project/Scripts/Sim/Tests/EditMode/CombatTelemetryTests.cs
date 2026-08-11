using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ACunicombat.Tests.EditMode
{
    public class CombatTelemetryTests
    {
        [Test]
        public void Writer_ProducesIndependentJsonLinesAndEscapesStrings()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "acunicombat-telemetry-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path;
                using (var session = new CombatTelemetrySession(
                           directory,
                           "test-session",
                           64 * 1024))
                {
                    session.RecordDriverStart(
                        0.0,
                        "pilot\"one\\radar",
                        7,
                        0.01,
                        0.04,
                        0.04);
                    var measurement = MeasurementAt(0.04);
                    measurement.Range = float.NaN;
                    session.RecordRadarMeasurement(
                        0.04,
                        "pilot\"one\\radar",
                        3,
                        in measurement);
                    session.Flush();
                    path = session.CurrentFilePath;
                }

                var lines = File.ReadAllLines(path);
                Assert.That(lines, Has.Length.EqualTo(2));
                Assert.That(lines[0], Does.Contain("pilot\\\"one\\\\radar"));
                Assert.That(lines[1], Does.Contain("\"rangeMeters\":null"));
                Assert.That(lines[1][0], Is.EqualTo('{'));
                Assert.That(lines[1][lines[1].Length - 1], Is.EqualTo('}'));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [Test]
        public void RadarTracker_ReportsAllocationThenAssociation()
        {
            var manager = new RadarTrackManager();
            var diagnostics = new List<RadarTrackDiagnostic>();
            manager.Diagnostic += diagnostic => diagnostics.Add(diagnostic);
            using var measurements =
                new NativeList<Measurement>(4, Allocator.Temp);

            measurements.Add(MeasurementAt(0.04));
            manager.Process(measurements, double3.zero, 0.04);
            measurements.Clear();
            measurements.Add(MeasurementAt(0.08));
            manager.Process(measurements, double3.zero, 0.08);

            Assert.That(
                diagnostics.Exists(
                    diagnostic =>
                        diagnostic.Kind == RadarTrackDiagnosticKind.TrackAllocated),
                Is.True);
            Assert.That(
                diagnostics.Exists(
                    diagnostic =>
                        diagnostic.Kind == RadarTrackDiagnosticKind.AssociationAccepted),
                Is.True);
        }

        private static Measurement MeasurementAt(double timestamp)
        {
            return new Measurement
            {
                SensorId = new SensorId(new Handle(0, 1)),
                Timestamp = timestamp,
                Range = 10_000f,
                Direction = new float3(0f, 0f, 1f),
                RadialVelocity = -250f,
                LosRate = float3.zero,
                RangeVar = 9f,
                AngleVar = 1e-6f,
                VelocityVar = 4f,
                LosRateVar = 1e-6f,
                SnrDb = 15f,
                ObjectClass = TrackObjectClass.Missile,
                ClassificationConfidence = 0.9f,
            };
        }
    }
}
