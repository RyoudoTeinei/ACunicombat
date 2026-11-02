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
    // Decision evidence contains observer estimates; outcome fields are physical post-event results.
    public struct ApsTelemetryData
    {
        public string SlotId,EquipmentId,ActionId,ProjectileId,Reason,ObserverScope,Phase,TrackQuality,Detail;
        public uint TargetTrackId;
        public int Ammunition,ActiveShots,Hits,Kills;
        public bool Pending,HasTrack,Incoming,TranslationInput;
        public double ReactionRemaining,PreparationProgress,CooldownRemaining,TrackTime,StateSampleTime;
        public float Uncertainty,TimeToCpa,FlightSeconds,OwnerDistance;
        public Vector3 OwnPosition,OwnVelocity,OwnAcceleration,TrackPosition,TrackVelocity,BurstPosition,FragmentDirection;
    }

    /// <summary>
    /// Shared JSONL writer for one Play session. Every line is independently
    /// parseable so a crash or forced editor stop does not invalidate earlier data.
    /// </summary>
    public sealed class CombatTelemetrySession : IDisposable
    {
        public static bool EventFocusedSnapshots {get;set;}
        private static volatile string lastRecordingFailure;
        public static string LastRecordingFailure=>lastRecordingFailure;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void ResetCapturePolicy(){EventFocusedSnapshots=false;lastRecordingFailure=null;}
        private static int ConfidenceBand(float v)=>v<.5f?0:v<.65f?1:v<.75f?2:v<.9f?3:4;
        private const string Schema = "acunicombat.combat-telemetry.v1";
        private static readonly object SharedLock = new object();
        private static CombatTelemetrySession shared;
        private static int sharedUsers;

        private readonly object writeLock = new object();
        private readonly string outputDirectory;
        private readonly string baseFileName;
        private readonly long maximumFileBytes;
        private readonly StringBuilder line = new StringBuilder(2048);
        private char[] lineBuffer = new char[4096];
        private readonly char[] numberBuffer = new char[64];
        private readonly Dictionary<string, MissileRecordState> missileStates =
            new Dictionary<string, MissileRecordState>();
        private readonly Dictionary<string, ThreatRecordState> threatStates =
            new Dictionary<string, ThreatRecordState>();
        private readonly Dictionary<string, FriendlyRecordState> friendlyIdentityStates =
            new Dictionary<string, FriendlyRecordState>();
        private struct FriendlyRecordState {public string Value;public double Seen;}
        private readonly List<string> expiredCacheKeys=new List<string>();
        private double nextCachePrune;
        public int CachedObservationCount=>missileStates.Count+threatStates.Count+friendlyIdentityStates.Count;
        private void PruneDiagnosticCaches(double now)
        {
            if(!double.IsFinite(now)||now<nextCachePrune)return;
            nextCachePrune=now+30;
            expiredCacheKeys.Clear();foreach(var pair in missileStates)if(now-pair.Value.LastRecordTime>120)expiredCacheKeys.Add(pair.Key);
            foreach(var key in expiredCacheKeys)missileStates.Remove(key);
            expiredCacheKeys.Clear();foreach(var pair in threatStates)if(now-pair.Value.LastRecordTime>120)expiredCacheKeys.Add(pair.Key);
            foreach(var key in expiredCacheKeys)threatStates.Remove(key);
            expiredCacheKeys.Clear();foreach(var pair in friendlyIdentityStates)if(now-pair.Value.Seen>120)expiredCacheKeys.Add(pair.Key);
            foreach(var key in expiredCacheKeys)friendlyIdentityStates.Remove(key);
            expiredCacheKeys.Clear();
        }
        private StreamWriter writer;
        private DateTime nextFlushUtc;
        private long sequence;
        private int part;
        private volatile bool disposed;
        private bool eventOpen;
        private double latestSimTime;
        private bool failureReported;
        public bool IsRecording=>!disposed&&writer!=null;
        public string LastWriteFailure {get;private set;}
        public int BufferedCharacterCount=>line.Length;
        public bool TryTakeFailure(out string failure)
        {
            lock(writeLock){failure=LastWriteFailure;if(failure==null||failureReported)return false;failureReported=true;return true;}
        }
        private void StopRecording(Exception failure=null)
        {
            disposed=true;eventOpen=false;
            if(failure!=null)LastWriteFailure=failure.GetType().Name+": "+failure.Message;
            var closing=writer;writer=null;
            var stream=closing?.BaseStream;
            try{closing?.Dispose();}catch(Exception error){LastWriteFailure??=error.GetType().Name+": "+error.Message;}
            finally{try{stream?.Dispose();}catch(Exception error){LastWriteFailure??=error.GetType().Name+": "+error.Message;}}
            if(LastWriteFailure!=null)lastRecordingFailure=LastWriteFailure;
            line.Clear();if(line.Capacity>8192)line.Capacity=2048;
            missileStates.Clear();threatStates.Clear();friendlyIdentityStates.Clear();expiredCacheKeys.Clear();
        }

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
                    // Batch runners may collocate diagnostics with a review on another
                    // volume. Ordinary play keeps the existing project/persistent path.
                    var outputOverride=Environment.GetEnvironmentVariable("ACUNICOMBAT_COMBAT_TELEMETRY_OUTPUT");
                    if(!string.IsNullOrWhiteSpace(outputOverride))root=Path.GetFullPath(outputOverride);
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

        public static void RecordOwnshipEvent(double time,string source,string key,string text,bool warning)
        {
            lock(SharedLock){shared?.WriteOwnshipEvent(time,source,key,text,warning);}
        }
        private void WriteOwnshipEvent(double time,string source,string key,string text,bool warning)
        {
            lock(writeLock){if(!IsRecording)return;Begin(time,source,"ownship_events","event");String("key",key);String("message",text);Boolean("warning",warning);End();}
        }

        public static void RecordApsEvent(double time,string source,string eventName,in ApsTelemetryData data)
        {
            lock(SharedLock){shared?.RecordAps(time,source,eventName,in data);}
        }

        public void RecordAps(double time,string source,string eventName,in ApsTelemetryData data)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(time,source,"aps",eventName);
            String("slotId",data.SlotId);String("equipmentId",data.EquipmentId);String("actionId",data.ActionId);
            String("projectileId",data.ProjectileId);String("reason",data.Reason);String("observerScope",data.ObserverScope);String("phase",data.Phase);
            Number("stateSampleTime",data.StateSampleTime);Number("targetTrackId",data.TargetTrackId);Number("ammunition",data.Ammunition);Number("activeShots",data.ActiveShots);
            Boolean("pending",data.Pending);Number("reactionRemainingSeconds",data.ReactionRemaining);Number("preparationProgress",data.PreparationProgress);Number("cooldownRemainingSeconds",data.CooldownRemaining);
            Boolean("translationInput",data.TranslationInput);
            Vector("ownPosition",new double3(data.OwnPosition.x,data.OwnPosition.y,data.OwnPosition.z));
            Vector("ownVelocity",new double3(data.OwnVelocity.x,data.OwnVelocity.y,data.OwnVelocity.z));
            Vector("ownAcceleration",new double3(data.OwnAcceleration.x,data.OwnAcceleration.y,data.OwnAcceleration.z));
            Boolean("hasTrack",data.HasTrack);Number("trackSampleTime",data.TrackTime);String("trackQuality",data.TrackQuality);Boolean("incoming",data.Incoming);
            Vector("trackPosition",new double3(data.TrackPosition.x,data.TrackPosition.y,data.TrackPosition.z));
            Vector("trackVelocity",new double3(data.TrackVelocity.x,data.TrackVelocity.y,data.TrackVelocity.z));
            Number("positionUncertaintyMeters",data.Uncertainty);Number("timeToCpaSeconds",data.TimeToCpa);
            Number("flightSeconds",data.FlightSeconds);Vector("burstPosition",new double3(data.BurstPosition.x,data.BurstPosition.y,data.BurstPosition.z));
            Vector("fragmentDirection",new double3(data.FragmentDirection.x,data.FragmentDirection.y,data.FragmentDirection.z));
            Number("ownerDistanceMeters",data.OwnerDistance);Number("hits",data.Hits);Number("kills",data.Kills);String("detail",data.Detail);
            End();
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
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "simulation", "driver_start");
            Number("seed", seed);
            Number("motionStepSeconds", motionStep);
            Number("sensorStepSeconds", sensorStep);
            Number("fusionStepSeconds", fusionStep);
            End();
                    }
        }

        public void RecordRadarProfile(
            double simTime,
            string source,
            in RadarSensorProfile profile,
            bool auxiliary)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "radar", "sensor_registered");
            Boolean("auxiliary", auxiliary);
            Number("sensorId", profile.SensorId.Packed);
            Number("emitterId", profile.EmitterId.Packed);
            Number("platformId", profile.PlatformId.Packed);
            String("capability", EnumName.Of(profile.Capability));
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
        }

        public void RecordElectroOpticalProfile(
            double simTime,
            string source,
            in ElectroOpticalReceiverProfile profile)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
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
        }

        public void RecordLaserProfile(
            double simTime,
            string source,
            in LaserRangefinderProfile profile)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
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
        }

        public void RecordEsmProfile(
            double simTime,
            string source,
            in EsmReceiverProfile profile)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "esm_maw", "receiver_registered");
            Number("receiverId", profile.ReceiverId.Packed);
            Number("blankedEmitterId", profile.BlankedEmitterId.Packed);
            Number("blankedEmitterId2", profile.BlankedEmitterId2.Packed);
            Number("sensitivityFloorDb", profile.SensitivityFloorDb);
            Number("bearingSigmaRadians", profile.BearingSigmaRadians);
            End();
                    }
        }

        public void RecordLinkProfile(
            double simTime,
            string source,
            in LinkTerminalProfile profile)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
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
        }

        public void RecordTrackingConfiguration(
            double simTime,
            string source,
            int capacity,
            float memorySeconds,
            float snapshotIntervalSeconds)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "radar_tracking", "configuration");
            Number("capacity", capacity);
            Number("trackMemorySeconds", memorySeconds);
            Number("snapshotIntervalSeconds", snapshotIntervalSeconds);
            End();
                    }
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
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
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
        }

        public void RecordDwell(double simTime, string source, in Dwell dwell)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "radar", "dwell_scheduled");
            Number("sensorId", dwell.SensorId.Packed);
            Number("dueTime", dwell.DueTime);
            Number("durationSeconds", dwell.DwellTimeSeconds);
            Vector("beamDirection", dwell.BeamDirection);
            Number("beamWidthRadians", dwell.BeamWidthRadians);
            String("priority", EnumName.Of(dwell.Priority));
            Number("dwellSequence", dwell.Sequence);
            End();
                    }
        }

        public void RecordEmission(
            double simTime,
            string source,
            string system,
            in EmissionEvent emission)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, system, "emission");
            Number("emitterId", emission.EmitterId.Packed);
            String("emissionType", EnumName.Of(emission.EmissionType));
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
        }

        public void RecordRadarMeasurement(
            double simTime,
            string source,
            int index,
            in Measurement measurement)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
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
            String("objectClass", EnumName.Of(measurement.ObjectClass));
            Number("classificationConfidence", measurement.ClassificationConfidence);
            String("flags", EnumName.Of(measurement.Flags));
            End();
                    }
        }

        public void RecordRadarTrackDiagnostic(
            string source,
            in RadarTrackDiagnostic diagnostic)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(diagnostic.Timestamp, source, "radar_tracking", EnumName.Of(diagnostic.Kind));
            Number("measurementIndex", diagnostic.MeasurementIndex);
            Number("sensorId", diagnostic.SensorId.Packed);
            Number("trackId", diagnostic.TrackId.Packed);
            String("objectClass", EnumName.Of(diagnostic.ObjectClass));
            String("previousLifecycle", EnumName.Of(diagnostic.PreviousLifecycle));
            String("lifecycle", EnumName.Of(diagnostic.Lifecycle));
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
        }

        public void RecordTrackSnapshot(double simTime, string source, in TrackSnapshot track)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            if(EventFocusedSnapshots)return;
            Begin(simTime, source, "radar_tracking", "track_snapshot");
            Number("trackId", track.TrackId.Packed);
            Vector("position", track.Position);
            Vector("velocity", track.Velocity);
            Number("confidence", track.Confidence);
            String("objectClass", EnumName.Of(track.ObjectClass));
            Number("classification", track.Classification);
            String("affiliation", EnumName.Of(track.Affiliation));
            Number("identityConfidence", track.IdentityConfidence);
            String("lifecycle", EnumName.Of(track.Lifecycle));
            Number("hitCount", track.HitCount);
            Number("coastCount", track.CoastCount);
            String("sourceMask", EnumName.Of(track.SourceMask));
            Number("firstObservationTime", track.FirstObservationTime);
            Number("lastUpdateTime", track.LastUpdateTime);
            Number("lastPredictTime", track.LastPredictTime);
            Number("consistencyScore", track.ConsistencyScore);
            Number("interferenceScore", track.InterferenceScore);
            Covariance(track);
            End();
                    }
        }

        public void RecordElectroOptical(
            double simTime,
            string source,
            in ElectroOpticalObservation observation)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "electro_optical", "observation");
            Number("sensorId", observation.SensorId.Packed);
            Number("observationTime", observation.Timestamp);
            Vector("direction", observation.Direction);
            Number("angleVariance", observation.AngleVar);
            Number("apparentDiameterRadians", observation.ApparentDiameterRadians);
            Number("signalMarginDb", observation.SignalMarginDb);
            String("objectClass", EnumName.Of(observation.ObjectClass));
            Number("classificationConfidence", observation.ClassificationConfidence);
            End();
                    }
        }

        public void RecordLaserObservation(
            double simTime,
            string source,
            in LaserRangeObservation observation,
            bool refinementAccepted,
            double normalizedInnovationSquared)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "laser_rangefinder", "observation");
            Number("sensorId", observation.SensorId.Packed);
            Number("cuedTrackId", observation.CuedTrackId.Packed);
            Number("observationTime", observation.Timestamp);
            Number("rangeMeters", observation.RangeMeters);
            Number("rangeVariance", observation.RangeVar);
            Vector("direction", observation.Direction);
            Number("angleVariance", observation.AngleVar);
            Number("signalMarginDb", observation.SignalMarginDb);
            String("objectClass", EnumName.Of(observation.ObjectClass));
            Number("classificationConfidence", observation.ClassificationConfidence);
            Boolean("refinementAccepted", refinementAccepted);
            Number("normalizedInnovationSquared", normalizedInnovationSquared);
            End();
                    }
        }

        public void RecordLaserPulse(
            double simTime,
            string source,
            in LaserPulse pulse,
            bool accepted)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "laser_rangefinder", "pulse_scheduled");
            Boolean("accepted", accepted);
            Number("sensorId", pulse.SensorId.Packed);
            Number("cuedTrackId", pulse.CuedTrackId.Packed);
            Number("dueTime", pulse.DueTime);
            Vector("direction", pulse.Direction);
            Number("pulseSequence", pulse.Sequence);
            End();
                    }
        }

        public void RecordWarningContact(
            double simTime,
            string source,
            in WarningObservation contact)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "esm_maw", "contact");
            Number("receiverId", contact.ReceiverId.Packed);
            Number("observationTime", contact.Timestamp);
            Vector("direction", contact.Direction);
            Number("bearingVariance", contact.BearingVar);
            Number("frequencyBand", contact.FrequencyBand);
            Number("waveformId", contact.WaveformId);
            Number("waveformConfidence", contact.WaveformConfidence);
            String("emissionType", EnumName.Of(contact.EmissionType));
            Boolean("isMainlobe", contact.IsMainlobe);
            Number("receivedPowerDb", contact.ReceivedPowerDb);
            End();
                    }
        }

        public void RecordThreat(
            double simTime,
            string source,
            in ThreatSnapshot threat,
            bool includeUnchanged = false)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            var key = source + ":" +
                      threat.ThreatId.Packed.ToString(CultureInfo.InvariantCulture);
            if ((!includeUnchanged || EventFocusedSnapshots) &&
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
            String("level", EnumName.Of(threat.Level));
            String("waveformClass", EnumName.Of(threat.WaveformClass));
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
            Number("lastMainlobeSeenTime", threat.LastMainlobeSeenTime);
            Number(
                "continuousMainlobeIlluminationSeconds",
                threat.ContinuousMainlobeIlluminationSeconds);
            Number("mainlobeReceivedPowerDb", threat.MainlobeReceivedPowerDb);
            Number(
                "mainlobePowerTrendDbPerSecond",
                threat.MainlobePowerTrendDbPerSecond);
            Number("mainlobeContactCount", threat.MainlobeContactCount);
            End();
                    }
        }

        public void RecordDataLink(
            double simTime,
            string source,
            string eventName,
            in LinkMessage message,
            bool accepted)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "data_link", eventName);
            Boolean("accepted", accepted);
            Number("messageSequence", message.Sequence);
            String("kind", EnumName.Of(message.Kind));
            Number("networkId", message.NetworkId);
            Number("sourceTerminal", message.SourceTerminal.Packed);
            Number("targetTerminal", message.TargetTerminal.Packed);
            Number("sendTime", message.SendTime);
            Number("deliveryTime", message.DeliveryTime);
            Number("deliveredMarginDb", message.DeliveredMarginDb);
            Number("payloadTrackId", message.TrackPayload.TrackId.Packed);
            if(message.TrackPayload.HasMissileLaunchTime)
                Number("missileLaunchTime", message.TrackPayload.MissileLaunchTime);
            End();
                    }
        }

        public void RecordUnityLog(
            string condition,
            string stackTrace,
            LogType type)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(latestSimTime, "unity", "application", "log");
            String("level", EnumName.Of(type));
            String("message", condition ?? string.Empty);
            String("stackTrace", stackTrace ?? string.Empty);
            End();
                    }
        }

        public void RecordMissileTrack(
            double simTime,
            string source,
            in MissileTrackSnapshot track)
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
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
                (EventFocusedSnapshots?ConfidenceBand(previous.IntentConfidence)==ConfidenceBand(current.IntentConfidence):Math.Abs(previous.IntentConfidence - current.IntentConfidence) < 0.001f) &&
                previous.MissionRole == current.MissionRole &&
                (EventFocusedSnapshots?ConfidenceBand(previous.MissionRoleConfidence)==ConfidenceBand(current.MissionRoleConfidence):Math.Abs(previous.MissionRoleConfidence-current.MissionRoleConfidence)<.001f) &&
                previous.MissionRoleEvidence ==
                    current.MissionRoleEvidence &&
                previous.Quality == current.Quality &&
                previous.Affiliation == current.Affiliation &&
                previous.Incoming == current.Incoming &&
                simTime - previous.LastRecordTime < (EventFocusedSnapshots?1.0:.25))
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
            String("affiliation", EnumName.Of(track.Affiliation));
            Number("identityConfidence", track.IdentityConfidence);
            String("quality", EnumName.Of(track.Quality));
            Boolean("incoming", track.Incoming);
            String("missionRole", EnumName.Of(track.MissionRole));
            Number("missionRoleConfidence", track.MissionRoleConfidence);
            String(
                "missionRoleEvidence",
                track.MissionRoleEvidence.ToString());
            String("targetIntent", EnumName.Of(track.TargetIntent));
            Number("targetIntentConfidence", track.TargetIntentConfidence);
            Number("intentOwnshipScore", track.IntentOwnshipScore);
            Number(
                "intentFriendlyMissileScore",
                track.IntentFriendlyMissileScore);
            Number("seekerOwnshipEvidence", track.SeekerOwnshipEvidence);
            Number(
                "lastSeekerMainlobeTime",
                track.LastSeekerMainlobeTime);
            Number(
                "seekerMainlobeContinuousSeconds",
                track.SeekerMainlobeContinuousSeconds);
            Number(
                "seekerMainlobePowerTrendDbPerSecond",
                track.SeekerMainlobePowerTrendDbPerSecond);
            Number("suspectedTargetTrackId", track.SuspectedTargetTrackId.Packed);
            Number("timeToCpaSeconds", track.TimeToClosestApproachSeconds);
            Number("closestApproachDistanceMeters", track.ClosestApproachDistanceMeters);
            String("sourceMask", EnumName.Of(track.SourceMask));
            Number("lastEvidenceTime", track.LastEvidenceTime);
            Number("kinematicUpdateTime", track.KinematicUpdateTime);
            End();
                    }
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
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(simTime, source, "missile_projectile", eventName);
            String("projectileEntityId", projectileEntityId ?? string.Empty);
            Number("ownerTeamId", ownerTeamId);
            String("missionRole", EnumName.Of(missionRole));
            Number("targetTrackId", targetTrackId.Packed);
            String("outcome", outcome ?? string.Empty);
            String("reason", reason ?? string.Empty);
            Vector("position", new double3(position.x, position.y, position.z));
            Vector("velocity", new double3(velocity.x, velocity.y, velocity.z));
            End();
                    }
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
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            var key = source + ":" +
                      sourceTerminal.Packed.ToString(CultureInfo.InvariantCulture);
            var state = resolution + ":" + associatedTrackId.Packed + ":" +
                        resolvedTrackId.Packed;
            bool same=friendlyIdentityStates.TryGetValue(key,out var previous)&&string.Equals(previous.Value,state,StringComparison.Ordinal);
            friendlyIdentityStates[key]=new FriendlyRecordState{Value=state,Seen=simTime};
            if (same)
            {
                return;
            }

            Begin(simTime, source, "identity", "friendly_missile_resolution");
            Number("sourceTerminal", sourceTerminal.Packed);
            String("resolution", EnumName.Of(resolution));
            Number("associatedTrackId", associatedTrackId.Packed);
            Number("resolvedTrackId", resolvedTrackId.Packed);
            Number("associationScore", score);
            Number("reportAgeSeconds", reportAgeSeconds);
            End();
                    }
        }

        public void Flush()
        {
            lock (writeLock)
            {
                try{writer?.Flush();}catch(Exception error){StopRecording(error);}
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

                StopRecording();
            }
        }

        private void RecordSessionStart()
        {
            lock (writeLock) // Whole record, including de-duplication and JSON construction.
            {
                if(disposed)return;
            Begin(0.0, "session", "telemetry", "session_start");
            String("schema", Schema);
            String("utcStart", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            String("unityVersion", Application.unityVersion);
            String("productName", Application.productName);
            String("applicationVersion", Application.version);
            String("scene", SceneManager.GetActiveScene().name);
            String("platform", EnumName.Of(Application.platform));
            String("gitCommit", TryReadGitCommit());
            End();
                    }
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
                PruneDiagnosticCaches(latestSimTime);
                line.Clear();
                line.Append('{');
                AppendRawNumber("sequence", ++sequence, false);
                // ISO round-trip time contains no characters that need JSON escaping.
                if (DateTime.UtcNow.TryFormat(numberBuffer, out var utcLength, "O", CultureInfo.InvariantCulture))
                { line.Append(",\"utc\":\"").Append(numberBuffer, 0, utcLength).Append('"'); }
                else AppendString("utc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), true);
                AppendRawNumber("simTime", simTime, true);
                AppendString("source", source, true);
                AppendString("system", system, true);
                AppendString("event", eventName, true);
            }
        }

        private void End()
        {
            using var costScope = ACunicombat.Simulation.Core.SimulationCostProbe.Measure(ACunicombat.Simulation.Core.SimulationCostProbe.SystemId.Telemetry);
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
                    // Same text as WriteLine(line.ToString()), without a string per event.
                    if (lineBuffer.Length < line.Length) lineBuffer = new char[Math.Max(line.Length, lineBuffer.Length * 2)];
                    line.CopyTo(0, lineBuffer, 0, line.Length);
                    writer.Write(lineBuffer, 0, line.Length);
                    writer.WriteLine();
                    if (DateTime.UtcNow >= nextFlushUtc)
                    {
                        writer.Flush();
                        nextFlushUtc = DateTime.UtcNow.AddSeconds(1.0);
                    }
                }
                catch (Exception error)
                {
                    StopRecording(error);
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
            if(!eventOpen)return;
            line.Append(',');
            AppendQuoted(name);
            line.Append(':').Append(value ? "true" : "false");
        }

        private void Number(string name, double value)
        {
            if(!eventOpen)return;
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
            if(!eventOpen)return;
            line.Append(',');
            AppendQuoted(name);
            line.Append(':').Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private void Vector(string name, float3 value) =>
            Vector(name, new double3(value.x, value.y, value.z));

        private void Vector(string name, double3 value)
        {
            if(!eventOpen)return;
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
            if(!eventOpen)return;
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
            if(!eventOpen)return;
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
            else if (value.TryFormat(numberBuffer, out var written, "R", CultureInfo.InvariantCulture)) line.Append(numberBuffer, 0, written);
            else line.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        private void AppendString(string name, string value, bool comma)
        {
            if(!eventOpen)return;
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

namespace ACunicombat.Simulation.Driver
{
    /// <summary>Enum.ToString() cached per value (flag combinations included): same text, no per-event string.</summary>
    internal static class EnumName
    {
        public static string Of<T>(T value) where T : struct, Enum => Cache<T>.Get(value);
        private static class Cache<T> where T : struct, Enum
        {
            private static readonly Dictionary<T, string> names = new Dictionary<T, string>();
            public static string Get(T value)
            {
                lock (names)
                {
                    if (!names.TryGetValue(value, out var name)) { name = value.ToString(); if (names.Count < 4096) names[value] = name; }
                    return name;
                }
            }
        }
    }
}
