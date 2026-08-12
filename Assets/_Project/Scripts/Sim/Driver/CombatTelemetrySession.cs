using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ACunicombat.DataLink;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Tracking;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ACunicombat.Simulation.Driver
{
    /// <summary>
    /// Shared JSONL writer for one Play session. Every line is independently
    /// parseable so a crash or forced editor stop does not invalidate earlier data.
    /// </summary>
    public sealed class CombatTelemetrySession : IDisposable
    {
        private const string Schema = "acunicombat.combat-telemetry.v1";
        private static readonly object SharedLock = new object();
        private static CombatTelemetrySession shared;
        private static int sharedUsers;

        private readonly object writeLock = new object();
        private readonly string outputDirectory;
        private readonly string baseFileName;
        private readonly long maximumFileBytes;
        private readonly StringBuilder line = new StringBuilder(2048);
        private readonly Dictionary<string, MissileRecordState> missileStates =
            new Dictionary<string, MissileRecordState>();
        private readonly Dictionary<string, ThreatRecordState> threatStates =
            new Dictionary<string, ThreatRecordState>();
        private readonly Dictionary<string, string> friendlyIdentityStates =
            new Dictionary<string, string>();
        private StreamWriter writer;
        private DateTime nextFlushUtc;
        private long sequence;
        private int part;
        private bool disposed;
        private bool eventOpen;
        private double latestSimTime;

        private struct MissileRecordState
        {
            public double KinematicUpdateTime;
            public MissileTargetIntent Intent;
            public float IntentConfidence;
            public MissileMissionRole MissionRole;
            public float MissionRoleConfidence;
            public MissileMissionRoleEvidence MissionRoleEvidence;
            public MissileTrackQuality Quality;
            public TrackAffiliation Affiliation;
            public bool Incoming;
            public double LastRecordTime;
        }

        private struct ThreatRecordState
        {
            public EsmThreatLevel Level;
            public EsmWaveformClass WaveformClass;
            public double LastRecordTime;
        }

        public CombatTelemetrySession(
            string directory,
            string fileName,
            long maximumBytes = 256L * 1024L * 1024L)
        {
            outputDirectory = directory;
            baseFileName = SanitizeFileName(fileName);
            maximumFileBytes = Math.Max(64L * 1024L, maximumBytes);
            Directory.CreateDirectory(outputDirectory);
            OpenWriter();
        }

        public string CurrentFilePath { get; private set; }

        public static CombatTelemetrySession Acquire(int maximumFileSizeMegabytes)
        {
            lock (SharedLock)
            {
                if (shared == null || shared.disposed)
                {
                    var root = Application.isEditor
                        ? Path.Combine(
                            Directory.GetParent(Application.dataPath)?.FullName ??
                            Application.dataPath,
                            "Logs",
                            "CombatTelemetry")
                        : Path.Combine(
                            Application.persistentDataPath,
                            "CombatTelemetry");
                    var scene = SceneManager.GetActiveScene().name;
                    var stamp = DateTime.UtcNow.ToString(
                        "yyyyMMdd_HHmmss_fff",
                        CultureInfo.InvariantCulture);
                    shared = new CombatTelemetrySession(
                        root,
                        $"combat_{stamp}_{scene}",
                        Math.Max(1, maximumFileSizeMegabytes) * 1024L * 1024L);
                    Application.logMessageReceived += shared.OnUnityLog;
                    shared.RecordSessionStart();
                }

                sharedUsers++;
                return shared;
            }
        }

        public static void Release(CombatTelemetrySession session)
        {
            lock (SharedLock)
            {
                if (session == null || !ReferenceEquals(session, shared))
                {
                    return;
                }

                sharedUsers = Math.Max(0, sharedUsers - 1);
                if (sharedUsers == 0)
                {
                    Application.logMessageReceived -= shared.OnUnityLog;
                    shared.WriteSimple(
                        shared.latestSimTime,
                        "session",
                        "telemetry",
                        "session_end");
                    shared.Dispose();
                    shared = null;
                }
            }
        }

        public static void RecordMissileProjectileEvent(
            double simTime,
            string source,
            string eventName,
            string projectileEntityId,
            int ownerTeamId,
            MissileMissionRole missionRole,
            TrackId targetTrackId,
            string outcome,
            string reason,
            Vector3 position,
            Vector3 velocity)
        {
            lock (SharedLock)
            {
                shared?.RecordMissileProjectile(
                    simTime,
                    source,
                    eventName,
                    projectileEntityId,
                    ownerTeamId,
                    missionRole,
                    targetTrackId,
                    outcome,
                    reason,
                    position,
                    velocity);
            }
        }

        public void RecordDriverStart(
            double simTime,
            string source,
            uint seed,
            double motionStep,
            double sensorStep,
            double fusionStep)
        {
            Begin(simTime, source, "simulation", "driver_start");
            Number("seed", seed);
            Number("motionStepSeconds", motionStep);
            Number("sensorStepSeconds", sensorStep);
            Number("fusionStepSeconds", fusionStep);
            End();
        }

        public void RecordRadarProfile(
            double simTime,
            string source,
            in RadarSensorProfile profile,
            bool auxiliary)
        {
            Begin(simTime, source, "radar", "sensor_registered");
            Boolean("auxiliary", auxiliary);
            Number("sensorId", profile.SensorId.Packed);
            Number("emitterId", profile.EmitterId.Packed);
            Number("platformId", profile.PlatformId.Packed);
            String("capability", profile.Capability.ToString());
            Number("maxRangeMeters", profile.MaxRangeMeters);
            Number("frequencyBand", profile.FrequencyBand);
            Number("waveformId", profile.WaveformId);
            Number("mainlobePowerDb", profile.MainlobePowerDb);
            Number("sidelobeLevelDb", profile.SidelobeLevelDb);
            Number("clutterPowerDb", profile.ClutterPowerDb);
            Number("measurementPrecisionScale", profile.MeasurementPrecisionScale);
            Number("constantsDb", profile.DwellConfig.ConstantsDb);
            Number("integrationGainDb", profile.DwellConfig.IntegrationGainDb);
            Number("noisePowerDb", profile.DwellConfig.NoisePowerDb);
            Number("detectThresholdLowDb", profile.DwellConfig.DetectThresholdLowDb);
            Number("detectThresholdHighDb", profile.DwellConfig.DetectThresholdHighDb);
            Number("rangeResolutionMeters", profile.DwellConfig.RangeResolutionMeters);
            Number("beamWidthRadians", profile.DwellConfig.BeamWidthRadians);
            Number("velocityResolutionMps", profile.DwellConfig.VelocityResolutionMps);
            Number("mergeRangeMeters", profile.Resolutions.RangeMeters);
            Number("mergeAngleRadians", profile.Resolutions.AngleRadians);
            Number("mergeVelocityMps", profile.Resolutions.VelocityMps);
            End();
        }

        public void RecordElectroOpticalProfile(
            double simTime,
            string source,
            in ElectroOpticalReceiverProfile profile)
        {
            Begin(simTime, source, "electro_optical", "receiver_registered");
            Number("sensorId", profile.SensorId.Packed);
            Vector("forward", profile.Forward);
            Number("fieldOfViewRadians", profile.FieldOfViewRadians);
            Number("maximumRangeMeters", profile.MaximumRangeMeters);
            Number("sensitivityFloorDb", profile.SensitivityFloorDb);
            Number("bearingSigmaRadians", profile.BearingSigmaRadians);
            Number("trackCapacity", profile.TrackCapacity);
            Number("classificationScale", profile.ClassificationScale);
            End();
        }

        public void RecordLaserProfile(
            double simTime,
            string source,
            in LaserRangefinderProfile profile)
        {
            Begin(simTime, source, "laser_rangefinder", "sensor_registered");
            Number("sensorId", profile.SensorId.Packed);
            Number("emitterId", profile.EmitterId.Packed);
            Number("maximumRangeMeters", profile.MaximumRangeMeters);
            Number("beamWidthRadians", profile.BeamWidthRadians);
            Number("pulseDurationSeconds", profile.PulseDurationSeconds);
            Number("mainlobePowerDb", profile.MainlobePowerDb);
            Number("sidelobeLevelDb", profile.SidelobeLevelDb);
            Number("receiveSensitivityDb", profile.ReceiveSensitivityDb);
            Number("baseRangeSigmaMeters", profile.BaseRangeSigmaMeters);
            Number("rangeErrorPartsPerMillion", profile.RangeErrorPartsPerMillion);
            Number("bearingSigmaRadians", profile.BearingSigmaRadians);
            Number("frequencyBand", profile.FrequencyBand);
            Number("waveformId", profile.WaveformId);
            End();
        }

        public void RecordEsmProfile(
            double simTime,
            string source,
            in EsmReceiverProfile profile)
        {
            Begin(simTime, source, "esm_maw", "receiver_registered");
            Number("receiverId", profile.ReceiverId.Packed);
            Number("blankedEmitterId", profile.BlankedEmitterId.Packed);
            Number("blankedEmitterId2", profile.BlankedEmitterId2.Packed);
            Number("sensitivityFloorDb", profile.SensitivityFloorDb);
            Number("bearingSigmaRadians", profile.BearingSigmaRadians);
            End();
        }

        public void RecordLinkProfile(
            double simTime,
            string source,
            in LinkTerminalProfile profile)
        {
            Begin(simTime, source, "data_link", "terminal_registered");
            Number("terminalId", profile.TerminalId.Packed);
            Number("emitterId", profile.EmitterId.Packed);
            Number("networkId", profile.NetworkId);
            Number("transmitPowerDb", profile.TransmitPowerDb);
            Number("sidelobeLevelDb", profile.SidelobeLevelDb);
            Number("beamWidthRadians", profile.BeamWidthRadians);
            Number("receiveSensitivityDb", profile.ReceiveSensitivityDb);
            Number("maxRangeMeters", profile.MaxRangeMeters);
            Number("bandwidthKilobitsPerSecond", profile.BandwidthKilobitsPerSecond);
            Number("maxNetworkNodes", profile.MaxNetworkNodes);
            Number("processingLatencySeconds", profile.ProcessingLatencySeconds);
            Number("jamResistanceDb", profile.JamResistanceDb);
            Number("frequencyBand", profile.FrequencyBand);
            Number("waveformId", profile.WaveformId);
            End();
        }

        public void RecordTrackingConfiguration(
            double simTime,
            string source,
            int capacity,
            float memorySeconds,
            float snapshotIntervalSeconds)
        {
            Begin(simTime, source, "radar_tracking", "configuration");
            Number("capacity", capacity);
            Number("trackMemorySeconds", memorySeconds);
            Number("snapshotIntervalSeconds", snapshotIntervalSeconds);
            End();
        }

        public void RecordSensorTick(
            double simTime,
            string source,
            int radarDetections,
            int radarEmissions,
            int eoObservations,
            int laserObservations,
            int esmContacts,
            int tracks,
            int threats,
            int scheduledDwells,
            long deferredDwells)
        {
            Begin(simTime, source, "simulation", "sensor_tick");
            Number("radarDetections", radarDetections);
            Number("radarEmissions", radarEmissions);
            Number("eoObservations", eoObservations);
            Number("laserObservations", laserObservations);
            Number("esmContacts", esmContacts);
            Number("tracks", tracks);
            Number("threats", threats);
            Number("scheduledDwells", scheduledDwells);
            Number("deferredDwells", deferredDwells);
            End();
        }

        public void RecordDwell(double simTime, string source, in Dwell dwell)
        {
            Begin(simTime, source, "radar", "dwell_scheduled");
            Number("sensorId", dwell.SensorId.Packed);
            Number("dueTime", dwell.DueTime);
            Number("durationSeconds", dwell.DwellTimeSeconds);
            Vector("beamDirection", dwell.BeamDirection);
            Number("beamWidthRadians", dwell.BeamWidthRadians);
            String("priority", dwell.Priority.ToString());
            Number("dwellSequence", dwell.Sequence);
            End();
        }

        public void RecordEmission(
            double simTime,
            string source,
            string system,
            in EmissionEvent emission)
        {
            Begin(simTime, source, system, "emission");
            Number("emitterId", emission.EmitterId.Packed);
            String("emissionType", emission.EmissionType.ToString());
            Number("startTime", emission.StartTime);
            Number("endTime", emission.EndTime);
            Vector("origin", emission.Origin);
            Vector("direction", emission.Direction);
            Number("beamWidthRadians", emission.BeamWidth);
            Number("maxRangeMeters", emission.MaxRangeMeters);
            Number("frequencyBand", emission.FrequencyBand);
            Number("waveformId", emission.WaveformId);
            Number("mainlobePowerDb", emission.MainlobePowerDb);
            Number("sidelobeLevelDb", emission.SidelobeLevelDb);
            End();
        }

        public void RecordRadarMeasurement(
            double simTime,
            string source,
            int index,
            in Measurement measurement)
        {
            Begin(simTime, source, "radar", "measurement");
            Number("measurementIndex", index);
            Number("sensorId", measurement.SensorId.Packed);
            Number("measurementTime", measurement.Timestamp);
            Number("rangeMeters", measurement.Range);
            Vector("direction", measurement.Direction);
            Number("radialVelocityMps", measurement.RadialVelocity);
            Vector("losRateRadPerSecond", measurement.LosRate);
            Number("rangeVariance", measurement.RangeVar);
            Number("angleVariance", measurement.AngleVar);
            Number("velocityVariance", measurement.VelocityVar);
            Number("losRateVariance", measurement.LosRateVar);
            Number("snrDb", measurement.SnrDb);
            String("objectClass", measurement.ObjectClass.ToString());
            Number("classificationConfidence", measurement.ClassificationConfidence);
            String("flags", measurement.Flags.ToString());
            End();
        }

        public void RecordRadarTrackDiagnostic(
            string source,
            in RadarTrackDiagnostic diagnostic)
        {
            Begin(diagnostic.Timestamp, source, "radar_tracking", diagnostic.Kind.ToString());
            Number("measurementIndex", diagnostic.MeasurementIndex);
            Number("sensorId", diagnostic.SensorId.Packed);
            Number("trackId", diagnostic.TrackId.Packed);
            String("objectClass", diagnostic.ObjectClass.ToString());
            String("previousLifecycle", diagnostic.PreviousLifecycle.ToString());
            String("lifecycle", diagnostic.Lifecycle.ToString());
            Vector("measurementPosition", diagnostic.MeasurementPosition);
            Vector("trackPosition", diagnostic.TrackPosition);
            Number("normalizedDistanceSquared", diagnostic.NormalizedDistanceSquared);
            Number("gateSquared", diagnostic.GateSquared);
            Number("candidateCount", diagnostic.CandidateCount);
            Number("staleRejected", diagnostic.StaleRejectedCount);
            Number("classRejected", diagnostic.ClassRejectedCount);
            Number("gateRejected", diagnostic.GateRejectedCount);
            Number("trackAgeSeconds", diagnostic.TrackAgeSeconds);
            Number("coastCount", diagnostic.CoastCount);
            String("reason", diagnostic.Reason ?? string.Empty);
            End();
        }

        public void RecordTrackSnapshot(double simTime, string source, in TrackSnapshot track)
        {
            Begin(simTime, source, "radar_tracking", "track_snapshot");
            Number("trackId", track.TrackId.Packed);
            Vector("position", track.Position);
            Vector("velocity", track.Velocity);
            Number("confidence", track.Confidence);
            String("objectClass", track.ObjectClass.ToString());
            Number("classification", track.Classification);
            String("affiliation", track.Affiliation.ToString());
            Number("identityConfidence", track.IdentityConfidence);
            String("lifecycle", track.Lifecycle.ToString());
            Number("hitCount", track.HitCount);
            Number("coastCount", track.CoastCount);
            String("sourceMask", track.SourceMask.ToString());
            Number("lastUpdateTime", track.LastUpdateTime);
            Number("lastPredictTime", track.LastPredictTime);
            Number("consistencyScore", track.ConsistencyScore);
            Number("interferenceScore", track.InterferenceScore);
            Covariance(track);
            End();
        }

        public void RecordElectroOptical(
            double simTime,
            string source,
            in ElectroOpticalObservation observation)
        {
            Begin(simTime, source, "electro_optical", "observation");
            Number("sensorId", observation.SensorId.Packed);
            Number("observationTime", observation.Timestamp);
            Vector("direction", observation.Direction);
            Number("angleVariance", observation.AngleVar);
            Number("apparentDiameterRadians", observation.ApparentDiameterRadians);
            Number("signalMarginDb", observation.SignalMarginDb);
            String("objectClass", observation.ObjectClass.ToString());
            Number("classificationConfidence", observation.ClassificationConfidence);
            End();
        }

        public void RecordLaserObservation(
            double simTime,
            string source,
            in LaserRangeObservation observation,
            bool refinementAccepted,
            double normalizedInnovationSquared)
        {
            Begin(simTime, source, "laser_rangefinder", "observation");
            Number("sensorId", observation.SensorId.Packed);
            Number("cuedTrackId", observation.CuedTrackId.Packed);
            Number("observationTime", observation.Timestamp);
            Number("rangeMeters", observation.RangeMeters);
            Number("rangeVariance", observation.RangeVar);
            Vector("direction", observation.Direction);
            Number("angleVariance", observation.AngleVar);
            Number("signalMarginDb", observation.SignalMarginDb);
            String("objectClass", observation.ObjectClass.ToString());
            Number("classificationConfidence", observation.ClassificationConfidence);
            Boolean("refinementAccepted", refinementAccepted);
            Number("normalizedInnovationSquared", normalizedInnovationSquared);
            End();
        }

        public void RecordLaserPulse(
            double simTime,
            string source,
            in LaserPulse pulse,
            bool accepted)
        {
            Begin(simTime, source, "laser_rangefinder", "pulse_scheduled");
            Boolean("accepted", accepted);
            Number("sensorId", pulse.SensorId.Packed);
            Number("cuedTrackId", pulse.CuedTrackId.Packed);
            Number("dueTime", pulse.DueTime);
            Vector("direction", pulse.Direction);
            Number("pulseSequence", pulse.Sequence);
            End();
        }

        public void RecordWarningContact(
            double simTime,
            string source,
            in WarningObservation contact)
        {
            Begin(simTime, source, "esm_maw", "contact");
            Number("receiverId", contact.ReceiverId.Packed);
            Number("observationTime", contact.Timestamp);
            Vector("direction", contact.Direction);
            Number("bearingVariance", contact.BearingVar);
            Number("frequencyBand", contact.FrequencyBand);
            Number("waveformId", contact.WaveformId);
            Number("waveformConfidence", contact.WaveformConfidence);
            String("emissionType", contact.EmissionType.ToString());
            Number("receivedPowerDb", contact.ReceivedPowerDb);
            End();
        }

        public void RecordThreat(
            double simTime,
            string source,
            in ThreatSnapshot threat,
            bool includeUnchanged = false)
        {
            var key = source + ":" +
                      threat.ThreatId.Packed.ToString(CultureInfo.InvariantCulture);
            if (!includeUnchanged &&
                threatStates.TryGetValue(key, out var previous) &&
                previous.Level == threat.Level &&
                previous.WaveformClass == threat.WaveformClass &&
                simTime - previous.LastRecordTime < 1.0)
            {
                return;
            }
            threatStates[key] = new ThreatRecordState
            {
                Level = threat.Level,
                WaveformClass = threat.WaveformClass,
                LastRecordTime = simTime,
            };
            Begin(simTime, source, "esm_maw", "threat_snapshot");
            Number("threatId", threat.ThreatId.Packed);
            Vector("direction", threat.DirectionWorld);
            String("level", threat.Level.ToString());
            String("waveformClass", threat.WaveformClass.ToString());
            Number("frequencyBand", threat.FrequencyBand);
            Number("waveformId", threat.WaveformId);
            Number("illuminationIntervalSeconds", threat.IlluminationIntervalSeconds);
            Number("continuousIlluminationSeconds", threat.ContinuousIlluminationSeconds);
            Number("identificationConfidence", threat.IdentificationConfidence);
            Number("firstSeenTime", threat.FirstSeenTime);
            Number("lastSeenTime", threat.LastSeenTime);
            Number("receivedPowerDb", threat.ReceivedPowerDb);
            Number("powerTrendDbPerSecond", threat.PowerTrendDbPerSecond);
            Number("contactCount", threat.ContactCount);
            End();
        }

        public void RecordDataLink(
            double simTime,
            string source,
            string eventName,
            in LinkMessage message,
            bool accepted)
        {
            Begin(simTime, source, "data_link", eventName);
            Boolean("accepted", accepted);
            Number("messageSequence", message.Sequence);
            String("kind", message.Kind.ToString());
            Number("networkId", message.NetworkId);
            Number("sourceTerminal", message.SourceTerminal.Packed);
            Number("targetTerminal", message.TargetTerminal.Packed);
            Number("sendTime", message.SendTime);
            Number("deliveryTime", message.DeliveryTime);
            Number("deliveredMarginDb", message.DeliveredMarginDb);
            Number("payloadTrackId", message.TrackPayload.TrackId.Packed);
            End();
        }

        public void RecordUnityLog(
            string condition,
            string stackTrace,
            LogType type)
        {
            Begin(latestSimTime, "unity", "application", "log");
            String("level", type.ToString());
            String("message", condition ?? string.Empty);
            String("stackTrace", stackTrace ?? string.Empty);
            End();
        }

        public void RecordMissileTrack(
            double simTime,
            string source,
            in MissileTrackSnapshot track)
        {
            var key = source + ":" + track.TrackId.Packed.ToString(CultureInfo.InvariantCulture);
            var current = new MissileRecordState
            {
                KinematicUpdateTime = track.KinematicUpdateTime,
                Intent = track.TargetIntent,
                IntentConfidence = track.TargetIntentConfidence,
                MissionRole = track.MissionRole,
                MissionRoleConfidence = track.MissionRoleConfidence,
                MissionRoleEvidence = track.MissionRoleEvidence,
                Quality = track.Quality,
                Affiliation = track.Affiliation,
                Incoming = track.Incoming,
                LastRecordTime = simTime,
            };
            if (missileStates.TryGetValue(key, out var previous) &&
                previous.Intent == current.Intent &&
                Math.Abs(previous.IntentConfidence - current.IntentConfidence) < 0.001f &&
                previous.MissionRole == current.MissionRole &&
                Math.Abs(
                    previous.MissionRoleConfidence -
                    current.MissionRoleConfidence) < 0.001f &&
                previous.MissionRoleEvidence ==
                    current.MissionRoleEvidence &&
                previous.Quality == current.Quality &&
                previous.Affiliation == current.Affiliation &&
                previous.Incoming == current.Incoming &&
                simTime - previous.LastRecordTime < 0.25)
            {
                return;
            }

            missileStates[key] = current;
            Begin(simTime, source, "missile_fusion", "track_snapshot");
            Number("trackId", track.TrackId.Packed);
            Vector("position", track.Position);
            Vector("velocity", track.Velocity);
            Vector("acceleration", track.Acceleration);
            Vector("jerk", track.Jerk);
            Number("positionUncertaintyMeters", track.PositionUncertaintyMeters);
            Number("cpaUncertaintyMeters", track.CpaUncertaintyMeters);
            Number("confidence", track.Confidence);
            String("affiliation", track.Affiliation.ToString());
            Number("identityConfidence", track.IdentityConfidence);
            String("quality", track.Quality.ToString());
            Boolean("incoming", track.Incoming);
            String("missionRole", track.MissionRole.ToString());
            Number("missionRoleConfidence", track.MissionRoleConfidence);
            String(
                "missionRoleEvidence",
                track.MissionRoleEvidence.ToString());
            String("targetIntent", track.TargetIntent.ToString());
            Number("targetIntentConfidence", track.TargetIntentConfidence);
            Number("intentOwnshipScore", track.IntentOwnshipScore);
            Number(
                "intentFriendlyMissileScore",
                track.IntentFriendlyMissileScore);
            Number("suspectedTargetTrackId", track.SuspectedTargetTrackId.Packed);
            Number("timeToCpaSeconds", track.TimeToClosestApproachSeconds);
            Number("closestApproachDistanceMeters", track.ClosestApproachDistanceMeters);
            String("sourceMask", track.SourceMask.ToString());
            Number("lastEvidenceTime", track.LastEvidenceTime);
            Number("kinematicUpdateTime", track.KinematicUpdateTime);
            End();
        }

        private void RecordMissileProjectile(
            double simTime,
            string source,
            string eventName,
            string projectileEntityId,
            int ownerTeamId,
            MissileMissionRole missionRole,
            TrackId targetTrackId,
            string outcome,
            string reason,
            Vector3 position,
            Vector3 velocity)
        {
            Begin(simTime, source, "missile_projectile", eventName);
            String("projectileEntityId", projectileEntityId ?? string.Empty);
            Number("ownerTeamId", ownerTeamId);
            String("missionRole", missionRole.ToString());
            Number("targetTrackId", targetTrackId.Packed);
            String("outcome", outcome ?? string.Empty);
            String("reason", reason ?? string.Empty);
            Vector("position", new double3(position.x, position.y, position.z));
            Vector("velocity", new double3(velocity.x, velocity.y, velocity.z));
            End();
        }

        public void RecordFriendlyIdentityResolution(
            double simTime,
            string source,
            LinkTerminalId sourceTerminal,
            FriendlyIdentityResolution resolution,
            TrackId associatedTrackId,
            TrackId resolvedTrackId,
            double score,
            double reportAgeSeconds)
        {
            var key = source + ":" +
                      sourceTerminal.Packed.ToString(CultureInfo.InvariantCulture);
            var state = resolution + ":" + associatedTrackId.Packed + ":" +
                        resolvedTrackId.Packed;
            if (friendlyIdentityStates.TryGetValue(key, out var previous) &&
                string.Equals(previous, state, StringComparison.Ordinal))
            {
                return;
            }

            friendlyIdentityStates[key] = state;
            Begin(simTime, source, "identity", "friendly_missile_resolution");
            Number("sourceTerminal", sourceTerminal.Packed);
            String("resolution", resolution.ToString());
            Number("associatedTrackId", associatedTrackId.Packed);
            Number("resolvedTrackId", resolvedTrackId.Packed);
            Number("associationScore", score);
            Number("reportAgeSeconds", reportAgeSeconds);
            End();
        }

        public void Flush()
        {
            lock (writeLock)
            {
                writer?.Flush();
            }
        }

        public void Dispose()
        {
            lock (writeLock)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                writer?.Flush();
                writer?.Dispose();
                writer = null;
            }
        }

        private void RecordSessionStart()
        {
            Begin(0.0, "session", "telemetry", "session_start");
            String("schema", Schema);
            String("utcStart", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            String("unityVersion", Application.unityVersion);
            String("productName", Application.productName);
            String("applicationVersion", Application.version);
            String("scene", SceneManager.GetActiveScene().name);
            String("platform", Application.platform.ToString());
            String("gitCommit", TryReadGitCommit());
            End();
        }

        private void OnUnityLog(
            string condition,
            string stackTrace,
            LogType type)
        {
            RecordUnityLog(condition, stackTrace, type);
        }

        private void WriteSimple(double simTime, string source, string system, string eventName)
        {
            Begin(simTime, source, system, eventName);
            End();
        }

        private void Begin(double simTime, string source, string system, string eventName)
        {
            lock (writeLock)
            {
                eventOpen = !disposed && writer != null;
                if (!eventOpen)
                {
                    return;
                }
                if (simTime > latestSimTime)
                {
                    latestSimTime = simTime;
                }
                line.Clear();
                line.Append('{');
                AppendRawNumber("sequence", ++sequence, false);
                AppendString("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), true);
                AppendRawNumber("simTime", simTime, true);
                AppendString("source", source, true);
                AppendString("system", system, true);
                AppendString("event", eventName, true);
            }
        }

        private void End()
        {
            lock (writeLock)
            {
                if (!eventOpen)
                {
                    return;
                }

                try
                {
                    line.Append('}');
                    EnsureCapacity(line.Length + Environment.NewLine.Length);
                    writer.WriteLine(line.ToString());
                    if (DateTime.UtcNow >= nextFlushUtc)
                    {
                        writer.Flush();
                        nextFlushUtc = DateTime.UtcNow.AddSeconds(1.0);
                    }
                }
                catch (Exception)
                {
                    disposed = true;
                    try
                    {
                        writer?.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                    writer = null;
                }
                finally
                {
                    eventOpen = false;
                }
            }
        }

        private void String(string name, string value) => AppendString(name, value, true);
        private void Boolean(string name, bool value)
        {
            line.Append(',');
            AppendQuoted(name);
            line.Append(':').Append(value ? "true" : "false");
        }

        private void Number(string name, double value)
        {
            line.Append(',');
            AppendQuoted(name);
            line.Append(':');
            AppendNumberValue(value);
        }

        private void Number(string name, float value) => Number(name, (double)value);
        private void Number(string name, int value) => Number(name, (double)value);
        private void Number(string name, long value) => Number(name, (double)value);
        private void Number(string name, uint value) => Number(name, (double)value);
        private void Number(string name, ushort value) => Number(name, (double)value);
        private void Number(string name, byte value) => Number(name, (double)value);
        private void Number(string name, ulong value)
        {
            line.Append(',');
            AppendQuoted(name);
            line.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private void Vector(string name, float3 value) =>
            Vector(name, new double3(value.x, value.y, value.z));

        private void Vector(string name, double3 value)
        {
            line.Append(',');
            AppendQuoted(name);
            line.Append(":{");
            AppendRawNumber("x", value.x, false);
            AppendRawNumber("y", value.y, true);
            AppendRawNumber("z", value.z, true);
            line.Append('}');
        }

        private void Covariance(in TrackSnapshot track)
        {
            line.Append(',');
            AppendQuoted("covarianceUpperTriangular");
            line.Append(':').Append('[');
            var first = true;
            for (var row = 0; row < 6; row++)
            {
                for (var column = row; column < 6; column++)
                {
                    if (!first) line.Append(',');
                    first = false;
                    AppendNumberValue(track.GetCovariance(row, column));
                }
            }
            line.Append(']');
        }

        private void AppendRawNumber(string name, double value, bool comma)
        {
            if (comma) line.Append(',');
            AppendQuoted(name);
            line.Append(':');
            AppendNumberValue(value);
        }

        private void AppendNumberValue(double value)
        {
            if (double.IsNaN(value)) line.Append("null");
            else if (double.IsPositiveInfinity(value)) line.Append("\"Infinity\"");
            else if (double.IsNegativeInfinity(value)) line.Append("\"-Infinity\"");
            else line.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void AppendString(string name, string value, bool comma)
        {
            if (comma) line.Append(',');
            AppendQuoted(name);
            line.Append(':');
            AppendQuoted(value ?? string.Empty);
        }

        private void AppendQuoted(string value)
        {
            line.Append('"');
            if (value != null)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var c = value[i];
                    switch (c)
                    {
                        case '"': line.Append("\\\""); break;
                        case '\\': line.Append("\\\\"); break;
                        case '\n': line.Append("\\n"); break;
                        case '\r': line.Append("\\r"); break;
                        case '\t': line.Append("\\t"); break;
                        default:
                            if (c < 0x20)
                                line.Append("\\u").Append(((int)c).ToString("x4"));
                            else
                                line.Append(c);
                            break;
                    }
                }
            }
            line.Append('"');
        }

        private void EnsureCapacity(int pendingCharacters)
        {
            if (writer == null)
            {
                OpenWriter();
                return;
            }

            var estimatedBytes = writer.BaseStream.Position + pendingCharacters * 2L;
            if (estimatedBytes < maximumFileBytes)
            {
                return;
            }

            writer.Flush();
            writer.Dispose();
            writer = null;
            part++;
            OpenWriter();
        }

        private void OpenWriter()
        {
            var suffix = part == 0 ? string.Empty : $"_part{part:000}";
            CurrentFilePath = Path.Combine(outputDirectory, baseFileName + suffix + ".jsonl");
            writer = new StreamWriter(
                new FileStream(
                    CurrentFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.SequentialScan),
                new UTF8Encoding(false),
                64 * 1024);
            nextFlushUtc = DateTime.UtcNow.AddSeconds(1.0);
        }

        private static string TryReadGitCommit()
        {
            try
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot)) return string.Empty;
                var headPath = Path.Combine(projectRoot, ".git", "HEAD");
                if (!File.Exists(headPath)) return string.Empty;
                var head = File.ReadAllText(headPath).Trim();
                if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head;
                var refPath = Path.Combine(
                    projectRoot,
                    ".git",
                    head.Substring(5).Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : head;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = new StringBuilder(value?.Length ?? 0);
            foreach (var c in value ?? "combat")
            {
                result.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            }
            return result.ToString();
        }
    }
}
