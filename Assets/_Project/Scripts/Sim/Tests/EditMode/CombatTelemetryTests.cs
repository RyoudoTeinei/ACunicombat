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
using UnityEngine;

namespace ACunicombat.Tests.EditMode
{
    public class CombatTelemetryTests
    {
        [Test]
        public void ApsDecisionAndOutcomeArePersistedWithCorrelationAndProgress()
        {
            var directory=Path.Combine(Path.GetTempPath(),"aps-telemetry-"+Guid.NewGuid().ToString("N"));
            try
            {
                string file;
                using(var session=new CombatTelemetrySession(directory,"aps-test"))
                {
                    var data=new ApsTelemetryData{SlotId="extension",EquipmentId="equipment-1",ActionId="AutomaticDefense:7",ProjectileId="charge-1",Reason="Reacting",Pending=true,Ammunition=8,ReactionRemaining=.075,PreparationProgress=.5,OwnVelocity=new Vector3(120,0,0),TargetTrackId=17,HasTrack=true};
                    session.RecordAps(1,"pilot","preparation_started",in data);
                    data.Pending=false;data.Ammunition=7;data.Reason="Committed";session.RecordAps(1.1,"pilot","countermunition_launched",in data);
                    data.Hits=1;data.Kills=1;data.Reason="Intercepted";session.RecordAps(1.2,"pilot","airburst",in data);session.Flush();file=session.CurrentFilePath;
                }
                var lines=File.ReadAllLines(file);Assert.That(lines.Length,Is.EqualTo(3));
                Assert.That(lines[0],Does.Contain("\"preparationProgress\":0.5"));Assert.That(lines[0],Does.Contain("\"ammunition\":8"));
                Assert.That(lines[1],Does.Contain("\"ammunition\":7"));Assert.That(lines[2],Does.Contain("\"kills\":1"));
                foreach(var line in lines){Assert.That(line,Does.Contain("\"projectileId\":\"charge-1\""));Assert.That(line,Does.Contain("\"targetTrackId\":17"));}
            }
            finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
        }

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

        [Test]
        public void UnityLog_UsesLatestSimulationTime()
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
                        12.5,
                        "test-radar",
                        7,
                        0.01,
                        0.04,
                        0.04);
                    session.RecordUnityLog(
                        "sensor failure",
                        "test stack",
                        LogType.Error);
                    session.Flush();
                    path = session.CurrentFilePath;
                }

                var lines = File.ReadAllLines(path);
                Assert.That(lines, Has.Length.EqualTo(2));
                Assert.That(lines[1], Does.Contain("\"simTime\":12.5"));
                Assert.That(lines[1], Does.Contain("\"system\":\"application\""));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
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
