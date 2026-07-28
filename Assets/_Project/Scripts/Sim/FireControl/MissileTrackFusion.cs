using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    public struct MissileTrackFusionConfig
    {
        public float ThreatAssociationRadians;
        public double ThreatMaximumAgeSeconds;
        public double ElectroOpticalMaximumAgeSeconds;
        public float IncomingHorizonSeconds;
        public float ProtectedRadiusMeters;
        public float GuidedThreatRangeMeters;
        public float MinimumClosingSpeedMetersPerSecond;
        public float MinimumClassification;
        public float MinimumConfirmationConfidence;
        public float ConfirmationExitConfidence;
        public double CorroborationHoldSeconds;
        public double ConfirmationHoldSeconds;
        public double IncomingHoldSeconds;
        public double StateRetentionSeconds;
        public float DuplicatePositionGateMeters;
        public float DuplicateVelocityGateMetersPerSecond;

        public static MissileTrackFusionConfig Default =>
            new MissileTrackFusionConfig
            {
                ThreatAssociationRadians = math.radians(20f),
                ThreatMaximumAgeSeconds = 1.5,
                ElectroOpticalMaximumAgeSeconds = 0.2,
                IncomingHorizonSeconds = 90f,
                ProtectedRadiusMeters = 250f,
                GuidedThreatRangeMeters = 30_000f,
                MinimumClosingSpeedMetersPerSecond = 15f,
                MinimumClassification = 0.35f,
                MinimumConfirmationConfidence = 0.55f,
                ConfirmationExitConfidence = 0.42f,
                CorroborationHoldSeconds = 2.5,
                ConfirmationHoldSeconds = 1.5,
                IncomingHoldSeconds = 1.0,
                StateRetentionSeconds = 5.0,
                DuplicatePositionGateMeters = 300f,
                DuplicateVelocityGateMetersPerSecond = 250f,
            };
    }

    public sealed class MissileTrackFusionState
    {
        internal sealed class Entry
        {
            public bool WeaponsQuality;
            public bool Incoming;
            public double CorroborationUntil;
            public double WeaponsQualityUntil;
            public double IncomingUntil;
            public double LastSeenTime;
            public double LastEvidenceTime;
            public MissileTrackSourceMask CorroboratingSources;
        }

        internal readonly Dictionary<TrackId, Entry> Entries =
            new Dictionary<TrackId, Entry>();
        private readonly List<TrackId> staleTrackIds =
            new List<TrackId>();

        internal Entry GetOrCreate(TrackId trackId)
        {
            if (!Entries.TryGetValue(trackId, out var entry))
            {
                entry = new Entry();
                Entries.Add(trackId, entry);
            }

            return entry;
        }

        internal void Prune(double now, double retentionSeconds)
        {
            staleTrackIds.Clear();
            foreach (var pair in Entries)
            {
                if (now - pair.Value.LastSeenTime > retentionSeconds)
                {
                    staleTrackIds.Add(pair.Key);
                }
            }

            for (var i = 0; i < staleTrackIds.Count; i++)
            {
                Entries.Remove(staleTrackIds[i]);
            }
        }

        public void Clear()
        {
            Entries.Clear();
            staleTrackIds.Clear();
        }
    }

    /// <summary>
    /// Fuses anonymous sensor products into fire-control missile tracks.
    /// Bearing-only sensors may corroborate a track but cannot create a
    /// fireable three-dimensional solution without radar range and velocity.
    /// </summary>
    public static class MissileTrackFusion
    {
        public static int Fuse(
            in MissileTrackFusionConfig config,
            TrackSnapshot[] radarTracks,
            int radarTrackCount,
            ThreatSnapshot[] threats,
            int threatCount,
            ElectroOpticalObservation[] electroOpticalObservations,
            int electroOpticalCount,
            double3 ownshipPosition,
            double3 ownshipVelocity,
            double now,
            MissileTrackSnapshot[] destination)
        {
            return Fuse(
                in config,
                null,
                radarTracks,
                radarTrackCount,
                threats,
                threatCount,
                electroOpticalObservations,
                electroOpticalCount,
                ownshipPosition,
                ownshipVelocity,
                now,
                destination);
        }

        public static int Fuse(
            in MissileTrackFusionConfig config,
            MissileTrackFusionState state,
            TrackSnapshot[] radarTracks,
            int radarTrackCount,
            ThreatSnapshot[] threats,
            int threatCount,
            ElectroOpticalObservation[] electroOpticalObservations,
            int electroOpticalCount,
            double3 ownshipPosition,
            double3 ownshipVelocity,
            double now,
            MissileTrackSnapshot[] destination)
        {
            if (destination == null)
            {
                return 0;
            }

            state?.Prune(
                now,
                math.max(0.1, config.StateRetentionSeconds));
            var writeCount = 0;
            for (var i = 0;
                 i < radarTrackCount &&
                 writeCount < destination.Length;
                i++)
            {
                var track = radarTracks[i];
                if (track.Lifecycle == TrackLifecycle.Dropped)
                {
                    continue;
                }

                var radarMissileClassified =
                    track.ObjectClass == TrackObjectClass.Missile &&
                    track.Classification >=
                        config.MinimumClassification;
                var estimatedPosition = track.PredictPosition(now);
                var relativePosition =
                    estimatedPosition - ownshipPosition;
                var sourceMask = MissileTrackSourceMask.Radar;
                var confidence = math.saturate(
                    track.Confidence * 0.55f +
                    track.Classification * 0.45f);
                var lastEvidenceTime = track.LastUpdateTime;

                AssociateThreats(
                    in config,
                    relativePosition,
                    threats,
                    threatCount,
                    now,
                    ref sourceMask,
                    ref confidence,
                    ref lastEvidenceTime);
                var opticalConfirmed =
                    AssociateElectroOpticalObservation(
                        in config,
                        ownshipPosition,
                        ref estimatedPosition,
                        electroOpticalObservations,
                        electroOpticalCount,
                        now,
                        ref confidence,
                        ref lastEvidenceTime);
                if (opticalConfirmed)
                {
                    sourceMask |=
                        MissileTrackSourceMask.ElectroOptical;
                    relativePosition =
                        estimatedPosition - ownshipPosition;
                }
                var corroborated =
                    (sourceMask &
                     ~MissileTrackSourceMask.Radar) != 0;
                var radarClassCanBeMissile =
                    track.ObjectClass ==
                        TrackObjectClass.Missile ||
                    track.ObjectClass ==
                        TrackObjectClass.Unknown;
                if (!radarClassCanBeMissile ||
                    (!radarMissileClassified &&
                     !corroborated))
                {
                    continue;
                }

                CalculateClosestApproach(
                    relativePosition,
                    track.Velocity - ownshipVelocity,
                    out var timeToClosestApproach,
                    out var closestApproachDistance);
                var cpaUncertainty =
                    CalculateCpaPositionUncertainty(
                        in track,
                        timeToClosestApproach) *
                    (opticalConfirmed ? 0.7f : 1f);
                var relativeVelocity =
                    track.Velocity - ownshipVelocity;
                var range = math.length(relativePosition);
                var closingSpeed = range > 1.0
                    ? (float)-math.dot(
                        relativeVelocity,
                        relativePosition / range)
                    : 0f;
                var ballisticIncoming =
                    MissileEngagementMath
                        .IntersectsProtectedCpaWindow(
                            timeToClosestApproach,
                            closestApproachDistance,
                            cpaUncertainty,
                            config.IncomingHorizonSeconds,
                            config.ProtectedRadiusMeters);
                // An active seeker is steering, so a straight-line CPA is not
                // a valid reason to hide a closing missile at long range.
                var guidedIncoming =
                    (sourceMask & MissileTrackSourceMask.Rwr) != 0 &&
                    range <= config.GuidedThreatRangeMeters &&
                    closingSpeed >=
                        config.MinimumClosingSpeedMetersPerSecond;
                var radarFireControlUsable =
                    radarMissileClassified &&
                    (track.Lifecycle == TrackLifecycle.Confirmed ||
                     (track.Lifecycle == TrackLifecycle.Coasting &&
                      track.CoastCount <= 3));
                var velocityResolved =
                    track.Lifecycle != TrackLifecycle.Tentative &&
                    math.lengthsq(relativeVelocity) >=
                        config.MinimumClosingSpeedMetersPerSecond *
                        config.MinimumClosingSpeedMetersPerSecond;
                // A bearing cue correlated with a new radar plot must be
                // visible while its velocity estimate is still converging.
                // Once kinematics are resolved, CPA/closing-speed logic takes
                // over and a missile that has flown past is no longer held as
                // incoming indefinitely.
                var passiveCueIncoming =
                    corroborated && !velocityResolved;
                var incoming =
                    ballisticIncoming ||
                    guidedIncoming ||
                    passiveCueIncoming;
                var quality = MissileTrackQuality.Warning;
                var confirmed =
                    corroborated &&
                    radarFireControlUsable &&
                    confidence >=
                        config.MinimumConfirmationConfidence;

                if (state != null)
                {
                    var entry = state.GetOrCreate(track.TrackId);
                    entry.LastSeenTime = now;
                    entry.LastEvidenceTime = math.max(
                        entry.LastEvidenceTime,
                        lastEvidenceTime);
                    if (corroborated)
                    {
                        entry.CorroborationUntil =
                            now + math.max(
                                0.0,
                                config.CorroborationHoldSeconds);
                        entry.CorroboratingSources =
                            sourceMask &
                            ~MissileTrackSourceMask.Radar;
                    }

                    var corroborationHeld =
                        now <= entry.CorroborationUntil;
                    if (corroborationHeld)
                    {
                        sourceMask |= entry.CorroboratingSources;
                    }

                    var confirmationEntry =
                        radarFireControlUsable &&
                        corroborationHeld &&
                        confidence >=
                            config.MinimumConfirmationConfidence;
                    if (confirmationEntry)
                    {
                        entry.WeaponsQuality = true;
                        entry.WeaponsQualityUntil =
                            now + math.max(
                                0.0,
                                config.ConfirmationHoldSeconds);
                    }
                    else if (entry.WeaponsQuality &&
                             now > entry.WeaponsQualityUntil &&
                             (!radarFireControlUsable ||
                              !corroborationHeld ||
                              confidence <
                                  config.ConfirmationExitConfidence))
                    {
                        entry.WeaponsQuality = false;
                    }

                    if (incoming)
                    {
                        entry.Incoming = true;
                        entry.IncomingUntil =
                            now + math.max(
                                0.0,
                                config.IncomingHoldSeconds);
                    }
                    else if (entry.Incoming &&
                             now > entry.IncomingUntil)
                    {
                        entry.Incoming = false;
                    }

                    confirmed = entry.WeaponsQuality;
                    incoming = entry.Incoming;
                    lastEvidenceTime = entry.LastEvidenceTime;
                }

                if (confirmed)
                {
                    quality = MissileTrackQuality.WeaponsQuality;
                }

                var candidate =
                    new MissileTrackSnapshot
                    {
                        TrackId = track.TrackId,
                        Position = estimatedPosition,
                        Velocity = track.Velocity,
                        PositionUncertaintyMeters =
                            CalculatePositionUncertainty(in track) *
                            (opticalConfirmed ? 0.7f : 1f),
                        CpaUncertaintyMeters = cpaUncertainty,
                        Confidence = confidence,
                        SourceMask = sourceMask,
                        Quality = quality,
                        Confirmed = confirmed,
                        Incoming = incoming,
                        TimeToClosestApproachSeconds =
                            timeToClosestApproach,
                        ClosestApproachDistanceMeters =
                            closestApproachDistance,
                        LastEvidenceTime = lastEvidenceTime,
                        // Position has already been propagated to now. Its
                        // epoch must therefore also be now; retaining the
                        // sensor timestamp caused every consumer to apply the
                        // same velocity propagation a second time.
                        LastUpdateTime = now,
                    };

                var duplicateIndex = FindDuplicate(
                    in config,
                    destination,
                    writeCount,
                    in candidate);
                if (duplicateIndex >= 0)
                {
                    destination[duplicateIndex] =
                        MergeDuplicate(
                            in destination[duplicateIndex],
                            in candidate);
                }
                else
                {
                    destination[writeCount++] = candidate;
                }
            }

            return writeCount;
        }

        private static int FindDuplicate(
            in MissileTrackFusionConfig config,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            in MissileTrackSnapshot candidate)
        {
            var positionGate = math.max(
                25f,
                config.DuplicatePositionGateMeters);
            var velocityGate = math.max(
                10f,
                config.DuplicateVelocityGateMetersPerSecond);
            for (var i = 0; i < trackCount; i++)
            {
                var existing = tracks[i];
                var uncertaintyGate = math.min(
                    600f,
                    existing.PositionUncertaintyMeters +
                    candidate.PositionUncertaintyMeters);
                var gate = math.max(
                    positionGate,
                    uncertaintyGate);
                if (math.distancesq(
                        existing.Position,
                        candidate.Position) >
                    gate * gate)
                {
                    continue;
                }

                var existingSpeed =
                    math.length(existing.Velocity);
                var candidateSpeed =
                    math.length(candidate.Velocity);
                var eitherVelocityUnresolved =
                    existingSpeed < 30.0 ||
                    candidateSpeed < 30.0;
                if (!eitherVelocityUnresolved &&
                    math.distance(
                        existing.Velocity,
                        candidate.Velocity) >
                    velocityGate)
                {
                    continue;
                }

                return i;
            }

            return -1;
        }

        private static MissileTrackSnapshot MergeDuplicate(
            in MissileTrackSnapshot existing,
            in MissileTrackSnapshot candidate)
        {
            var preferCandidate =
                candidate.Quality > existing.Quality ||
                (candidate.Quality == existing.Quality &&
                 candidate.Incoming &&
                 !existing.Incoming) ||
                (candidate.Quality == existing.Quality &&
                 candidate.Incoming == existing.Incoming &&
                 candidate.LastEvidenceTime >
                    existing.LastEvidenceTime + 0.25) ||
                (candidate.Quality == existing.Quality &&
                 candidate.Incoming == existing.Incoming &&
                 candidate.LastEvidenceTime <=
                    existing.LastEvidenceTime + 0.25 &&
                 candidate.Confidence >
                    existing.Confidence + 0.15f);
            var merged = preferCandidate
                ? candidate
                : existing;
            merged.SourceMask =
                existing.SourceMask | candidate.SourceMask;
            merged.Quality =
                existing.Quality > candidate.Quality
                    ? existing.Quality
                    : candidate.Quality;
            merged.Confirmed =
                existing.Confirmed || candidate.Confirmed;
            merged.Incoming =
                existing.Incoming || candidate.Incoming;
            merged.Confidence =
                math.max(
                    existing.Confidence,
                    candidate.Confidence);
            merged.PositionUncertaintyMeters =
                math.min(
                    existing.PositionUncertaintyMeters,
                    candidate.PositionUncertaintyMeters);
            merged.CpaUncertaintyMeters =
                math.min(
                    existing.CpaUncertaintyMeters,
                    candidate.CpaUncertaintyMeters);
            merged.LastEvidenceTime =
                math.max(
                    existing.LastEvidenceTime,
                    candidate.LastEvidenceTime);
            merged.LastUpdateTime =
                math.max(
                    existing.LastUpdateTime,
                    candidate.LastUpdateTime);
            return merged;
        }

        private static void AssociateThreats(
            in MissileTrackFusionConfig config,
            double3 relativePosition,
            ThreatSnapshot[] threats,
            int threatCount,
            double now,
            ref MissileTrackSourceMask sourceMask,
            ref float confidence,
            ref double lastUpdate)
        {
            if (threats == null ||
                math.lengthsq(relativePosition) <= 0.01)
            {
                return;
            }

            var lineOfSight = math.normalizesafe(
                (float3)relativePosition,
                new float3(0f, 0f, 1f));
            var minimumDot =
                math.cos(config.ThreatAssociationRadians);
            var rwrConfidence = 0f;
            var mawConfidence = 0f;
            var rwrUpdate = double.MinValue;
            var mawUpdate = double.MinValue;
            var count = math.min(threatCount, threats.Length);
            for (var i = 0; i < count; i++)
            {
                var threat = threats[i];
                if (now - threat.LastSeenTime >
                        config.ThreatMaximumAgeSeconds ||
                    math.dot(
                        lineOfSight,
                        threat.DirectionWorld) <
                    minimumDot)
                {
                    continue;
                }

                switch (threat.WaveformClass)
                {
                    case EsmWaveformClass.MissileSeeker:
                        rwrConfidence = math.max(
                            rwrConfidence,
                            threat.IdentificationConfidence);
                        rwrUpdate = math.max(
                            rwrUpdate,
                            threat.LastSeenTime);
                        break;
                    case EsmWaveformClass.InfraredPlume:
                        mawConfidence = math.max(
                            mawConfidence,
                            threat.IdentificationConfidence);
                        mawUpdate = math.max(
                            mawUpdate,
                            threat.LastSeenTime);
                        break;
                }
            }

            if (rwrConfidence > 0f)
            {
                sourceMask |= MissileTrackSourceMask.Rwr;
                confidence = math.saturate(
                    confidence + 0.12f +
                    rwrConfidence * 0.08f);
                lastUpdate = math.max(lastUpdate, rwrUpdate);
            }
            if (mawConfidence > 0f)
            {
                sourceMask |= MissileTrackSourceMask.Maw;
                confidence = math.saturate(
                    confidence + 0.14f +
                    mawConfidence * 0.06f);
                lastUpdate = math.max(lastUpdate, mawUpdate);
            }
        }

        private static bool AssociateElectroOpticalObservation(
            in MissileTrackFusionConfig config,
            double3 ownshipPosition,
            ref double3 estimatedPosition,
            ElectroOpticalObservation[] observations,
            int observationCount,
            double now,
            ref float confidence,
            ref double lastUpdate)
        {
            if (observations == null)
            {
                return false;
            }

            var relativePosition =
                estimatedPosition - ownshipPosition;
            var range = math.length(relativePosition);
            if (range <= 1.0)
            {
                return false;
            }

            var radarDirection = math.normalizesafe(
                (float3)relativePosition,
                new float3(0f, 0f, 1f));
            var bestIndex = -1;
            var bestAngle = float.PositiveInfinity;
            var count =
                math.min(observationCount, observations.Length);
            for (var i = 0; i < count; i++)
            {
                var observation = observations[i];
                if (observation.ObjectClass !=
                        TrackObjectClass.Missile ||
                    observation.ClassificationConfidence < 0.25f ||
                    now - observation.Timestamp >
                        config.ElectroOpticalMaximumAgeSeconds)
                {
                    continue;
                }

                var direction = math.normalizesafe(
                    observation.Direction,
                    radarDirection);
                var angle = math.acos(
                    math.clamp(
                        math.dot(radarDirection, direction),
                        -1f,
                        1f));
                var associationGate = math.max(
                    math.radians(0.5f),
                    math.sqrt(
                        math.max(
                            observation.AngleVar,
                            1e-8f)) *
                    4f);
                if (angle <= associationGate &&
                    angle < bestAngle)
                {
                    bestIndex = i;
                    bestAngle = angle;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            var best = observations[bestIndex];
            var opticalDirection = math.normalizesafe(
                best.Direction,
                radarDirection);
            var opticalWeight = math.saturate(
                best.ClassificationConfidence * 0.45f);
            var fusedDirection = math.normalizesafe(
                math.lerp(
                    radarDirection,
                    opticalDirection,
                    opticalWeight),
                radarDirection);
            estimatedPosition =
                ownshipPosition +
                (double3)fusedDirection * range;
            confidence = math.saturate(
                confidence + 0.1f +
                best.ClassificationConfidence * 0.08f);
            lastUpdate =
                math.max(lastUpdate, best.Timestamp);
            return true;
        }

        private static void CalculateClosestApproach(
            double3 relativePosition,
            double3 relativeVelocity,
            out float time,
            out float distance)
        {
            var velocitySquared =
                math.lengthsq(relativeVelocity);
            if (velocitySquared <= 0.01)
            {
                time = 0f;
                distance = (float)math.length(relativePosition);
                return;
            }

            var closestTime = math.max(
                0.0,
                -math.dot(
                    relativePosition,
                    relativeVelocity) /
                velocitySquared);
            time = (float)closestTime;
            distance = (float)math.length(
                relativePosition +
                relativeVelocity * closestTime);
        }

        private static float CalculatePositionUncertainty(
            in TrackSnapshot track)
        {
            var variance =
                Math.Max(0.0, track.GetCovariance(0, 0)) +
                Math.Max(0.0, track.GetCovariance(1, 1)) +
                Math.Max(0.0, track.GetCovariance(2, 2));
            return (float)Math.Sqrt(variance);
        }

        private static float CalculateCpaPositionUncertainty(
            in TrackSnapshot track,
            float timeToClosestApproach)
        {
            var time = Math.Max(0.0, timeToClosestApproach);
            var variance = 0.0;
            for (var axis = 0; axis < 3; axis++)
            {
                var positionVariance =
                    track.GetCovariance(axis, axis);
                var positionVelocityCovariance =
                    track.GetCovariance(axis, axis + 3);
                var velocityVariance =
                    track.GetCovariance(axis + 3, axis + 3);
                var propagated =
                    positionVariance +
                    2.0 * time * positionVelocityCovariance +
                    time * time * velocityVariance;
                variance += Math.Max(0.0, propagated);
            }

            return (float)Math.Sqrt(variance);
        }
    }
}
