using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using ACunicombat.Tracking;

namespace ACunicombat.FireControl
{
    public struct MissileTrackFusionConfig
    {
        public float ThreatAssociationRadians;
        public float ThreatRoleAssociationRadians;
        public float ThreatRoleAssociationSeparationRadians;
        public float ThreatRoleMinimumConfidence;
        public double ThreatMaximumAgeSeconds;
        public double ThreatRoleHoldSeconds;
        public double ElectroOpticalMaximumAgeSeconds;
        public double LaserRangeMaximumAgeSeconds;
        public float LaserRangeAssociationGateMeters;
        public float LaserRangeAssociationGateRadians;
        public float IncomingHorizonSeconds;
        public float ProtectedRadiusMeters;
        public float GuidedThreatRangeMeters;
        public float GuidedThreatMaximumCpaMeters;
        public float MinimumClosingSpeedMetersPerSecond;
        public float MinimumClassification;
        public float MinimumConfirmationConfidence;
        public float ConfirmationExitConfidence;
        public double SeekerMainlobeMaximumAgeSeconds;
        public float SeekerMainlobeContinuousEvidenceSeconds;
        public float SeekerMainlobeRisingPowerThresholdDbPerSecond;
        public float SeekerMainlobeFullPowerTrendDbPerSecond;
        public float SeekerMainlobeMinimumIncomingEvidence;
        public double CorroborationHoldSeconds;
        public double ConfirmationHoldSeconds;
        public double IncomingHoldSeconds;
        public double StateRetentionSeconds;
        public float DuplicatePositionGateMeters;
        public float DuplicateVelocityGateMetersPerSecond;
        public float MaximumTrackAccelerationMetersPerSecondSquared;
        public float AccelerationFilterTimeSeconds;
        public float MaximumTrackJerkMetersPerSecondCubed;
        public float JerkFilterTimeSeconds;

        public static MissileTrackFusionConfig Default =>
            new MissileTrackFusionConfig
            {
                ThreatAssociationRadians = math.radians(20f),
                ThreatRoleAssociationRadians = math.radians(6f),
                ThreatRoleAssociationSeparationRadians =
                    math.radians(4f),
                ThreatRoleMinimumConfidence = 0.45f,
                ThreatMaximumAgeSeconds = 1.5,
                ThreatRoleHoldSeconds = 1.2,
                ElectroOpticalMaximumAgeSeconds = 0.2,
                LaserRangeMaximumAgeSeconds = 0.12,
                LaserRangeAssociationGateMeters = 400f,
                LaserRangeAssociationGateRadians =
                    math.radians(0.5f),
                IncomingHorizonSeconds = 90f,
                ProtectedRadiusMeters = 250f,
                GuidedThreatRangeMeters = 30_000f,
                GuidedThreatMaximumCpaMeters = 750f,
                MinimumClosingSpeedMetersPerSecond = 15f,
                MinimumClassification = 0.35f,
                MinimumConfirmationConfidence = 0.55f,
                ConfirmationExitConfidence = 0.42f,
                SeekerMainlobeMaximumAgeSeconds = 0.2,
                SeekerMainlobeContinuousEvidenceSeconds = 0.15f,
                SeekerMainlobeRisingPowerThresholdDbPerSecond = 0.5f,
                SeekerMainlobeFullPowerTrendDbPerSecond = 3f,
                SeekerMainlobeMinimumIncomingEvidence = 0.45f,
                CorroborationHoldSeconds = 2.5,
                ConfirmationHoldSeconds = 1.5,
                IncomingHoldSeconds = 1.0,
                StateRetentionSeconds = 5.0,
                DuplicatePositionGateMeters = 300f,
                DuplicateVelocityGateMetersPerSecond = 250f,
                MaximumTrackAccelerationMetersPerSecondSquared = 600f,
                AccelerationFilterTimeSeconds = 0.04f,
                MaximumTrackJerkMetersPerSecondCubed = 2_000f,
                JerkFilterTimeSeconds = 0.12f,
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
            public double3 PreviousVelocity;
            public double PreviousVelocityTime;
            public double PreviousVelocityVariance;
            public double3 FilteredAcceleration;
            public double3 FilteredJerk;
            public double3 PreviousRawAcceleration;
            public bool HasVelocitySample;
            public bool HasAccelerationSample;
            public bool PreviousAccelerationResolved;
            public MissileMissionRole SensorMissionRole;
            public float SensorMissionRoleConfidence;
            public double SensorMissionRoleUntil;
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

    public enum FriendlyIdentityResolution : byte
    {
        None = 0,
        Authenticated = 1,
        Coasting = 2,
        Ambiguous = 3,
    }

    public struct FriendlyMissileIdentityAssociation
    {
        public TrackId TrackId;
        public double LastAuthenticatedTime;
        public double LastSupportedTime;
        public double LastAssociationScore;

        public bool HasTrack => TrackId.IsValid;

        public void Clear()
        {
            this = default;
        }
    }

    /// <summary>
    /// Fuses anonymous sensor products into fire-control missile tracks.
    /// Bearing-only sensors may corroborate a track but cannot create a
    /// fireable three-dimensional solution without radar range and velocity.
    /// </summary>
    public static class MissileTrackFusion
    {
        private static double3x3 PropagatedPositionCovariance(in TrackSnapshot track,double age)
        {
            return track.PredictPositionCovariance(track.LastPredictTime+math.max(0,age));
        }

        // Repeated precise radar observations can independently support engagement.
        public static bool IsIndependentRadarWeaponsQuality(in TrackSnapshot track,double3 observer,double now,double3 observerVelocity=default)
        {
            if(!double.IsFinite(now)||!double.IsFinite(track.LastUpdateTime)||!double.IsFinite(track.FirstObservationTime)||!float.IsFinite(track.Confidence)||!float.IsFinite(track.Classification)||(track.SourceMask&TrackSourceMask.Radar)==0||track.ObjectClass!=TrackObjectClass.Missile||track.Lifecycle!=TrackLifecycle.Confirmed||track.HitCount<4||track.Classification<.65f||track.Confidence<.7f||track.AssociationClosed||now<track.LastUpdateTime||now-track.LastUpdateTime>.6||track.LastUpdateTime-track.FirstObservationTime<.12||!math.all(math.isfinite(track.Position))||!math.all(math.isfinite(track.Velocity)))return false;
            double p=0,v=0;for(int k=0;k<3;k++){double a=track.GetCovariance(k,k),b=track.GetCovariance(k+3,k+3);if(!double.IsFinite(a)||!double.IsFinite(b)||a<0||b<0)return false;p+=a;v+=b;}
            double gate=math.clamp(math.distance(track.PredictPosition(now),observer)*.002,60,250);
            var relative=track.Velocity-observerVelocity;
            var los=math.normalizesafe(track.PredictPosition(now)-observer);
            double radialVariance=0;
            for(int a=0;a<3;a++)for(int b=0;b<3;b++)radialVariance+=los[a]*track.GetCovariance(a+3,b+3)*los[b];
            // Range/Doppler precision and transverse uncertainty are distinct. At long range
            // lateral speed converges more slowly; bound it relative to the observed motion.
            double velocityGate=math.clamp(math.length(relative)*.15,60,200);
            return p>0&&p<=gate*gate&&v>0&&v<=velocityGate*velocityGate&&
                double.IsFinite(radialVariance)&&radialVariance>=-1e-6&&radialVariance<=20*20;
        }

        public static bool TryScoreFriendlyDataLinkAssociation(
            in MissileTrackSnapshot track,
            double3 reportPosition,
            double3 reportVelocity,
            double reportTimestamp,
            double now,
            double retentionSeconds,
            float maximumPositionGateMeters,
            float velocityGateMetersPerSecond,
            out double score)
        {
            score = double.PositiveInfinity;
            var age = math.max(0.0, now - reportTimestamp);
            // A fresh report cannot authenticate a stale, diffuse radar hypothesis.
            // LastUpdateTime is the prediction epoch; use the actual kinematic evidence.
            var trackEvidenceTime = track.KinematicUpdateTime > 0 ? track.KinematicUpdateTime
                : track.LastEvidenceTime > 0 ? track.LastEvidenceTime : track.LastUpdateTime;
            if (age > retentionSeconds || now - trackEvidenceTime > retentionSeconds)
            {
                return false;
            }

            var predictedReportPosition =
                reportPosition + reportVelocity * age;
            var positionError = math.distance(
                predictedReportPosition,
                track.PredictPosition(now));
            var positionGate = math.min(
                maximumPositionGateMeters,
                math.max(
                    12.0,
                    5.0 * math.sqrt(track.PositionUncertaintyMeters * track.PositionUncertaintyMeters + math.pow(3.0 + 300.0 * age * age, 2.0))));
            double positionScore=positionError/positionGate;
            if(track.HasPositionCovariance)
            {
                // A large bearing error is not a large range error. Preserve the ellipse
                // so telemetry can distinguish adjacent salvo rounds by measured range.
                var prior=new FilterState{Position=track.PredictPosition(now)};
                Matrix6 observationCovariance=default;
                double reportSigma=3.0+300.0*age*age;
                for(int row=0;row<3;row++)for(int col=0;col<3;col++)prior.P[row,col]=track.PositionCovariance[col][row];
                for(int axis=0;axis<3;axis++)observationCovariance[axis,axis]=reportSigma*reportSigma/3.0;
                if(!Cmkf.TryCalculatePositionInnovationNis(prior,predictedReportPosition,observationCovariance,out var nis)||nis>25)return false;
                positionScore=math.sqrt(nis/25.0);
            }
            else if(positionError>positionGate)return false;

            var velocityError = math.distance(
                reportVelocity,
                track.PredictVelocity(now));
            if(math.lengthsq(track.ObserverLineOfSight)>.5f)
            {
                double radialError=math.abs(math.dot(reportVelocity-track.PredictVelocity(now),(double3)track.ObserverLineOfSight));
                double radialGate=math.max(velocityGateMetersPerSecond,5*track.RadialVelocityUncertaintyMetersPerSecond+1500*age);
                if(radialError>radialGate)return false;
            }
            var effectiveVelocityGate=math.max(velocityGateMetersPerSecond,3*track.VelocityUncertaintyMetersPerSecond);
            if (velocityError > effectiveVelocityGate)
            {
                return false;
            }

            score = positionScore +
                    velocityError / effectiveVelocityGate;
            return true;
        }

        public static bool TrySelectFriendlyDataLinkAssociation(
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched,
            double3 reportPosition,
            double3 reportVelocity,
            double reportTimestamp,
            double now,
            double retentionSeconds,
            float maximumPositionGateMeters,
            float velocityGateMetersPerSecond,
            double minimumScoreSeparation,
            out int trackIndex,
            out double score)
        {
            trackIndex = -1;
            score = double.PositiveInfinity;
            if (tracks == null || trackAlreadyMatched == null)
            {
                return false;
            }

            var count = math.min(
                math.min(trackCount, tracks.Length),
                trackAlreadyMatched.Length);
            var secondBestScore = double.PositiveInfinity;
            for (var i = 0; i < count; i++)
            {
                if (trackAlreadyMatched[i] ||
                    !TryScoreFriendlyDataLinkAssociation(
                        in tracks[i],
                        reportPosition,
                        reportVelocity,
                        reportTimestamp,
                        now,
                        retentionSeconds,
                        maximumPositionGateMeters,
                        velocityGateMetersPerSecond,
                        out var candidateScore))
                {
                    continue;
                }

                if (candidateScore < score)
                {
                    secondBestScore = score;
                    score = candidateScore;
                    trackIndex = i;
                }
                else if (candidateScore < secondBestScore)
                {
                    secondBestScore = candidateScore;
                }
            }

            if (trackIndex < 0)
            {
                return false;
            }

            // When an interceptor is inside the same association volume as our
            // missile, proximity alone cannot establish identity. Leave both
            // tracks unknown so the warning layer cannot hide a possible threat.
            return double.IsPositiveInfinity(secondBestScore) ||
                   secondBestScore - score >= minimumScoreSeparation;
        }

        public static FriendlyIdentityResolution ResolveFriendlyDataLinkIdentity(
            ref FriendlyMissileIdentityAssociation association,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched,
            double3 reportPosition,
            double3 reportVelocity,
            double reportStateTimestamp,
            double now,
            double reportFreshnessSeconds,
            float maximumPositionGateMeters,
            float velocityGateMetersPerSecond,
            double minimumScoreSeparation,
            out int trackIndex,
            out double score)
        {
            return ResolveFriendlyDataLinkIdentity(
                ref association,
                tracks,
                trackCount,
                trackAlreadyMatched,
                reportPosition,
                reportVelocity,
                reportStateTimestamp,
                now,
                reportFreshnessSeconds,
                reportFreshnessSeconds,
                maximumPositionGateMeters,
                velocityGateMetersPerSecond,
                minimumScoreSeparation,
                out trackIndex,
                out score);
        }

        public static FriendlyIdentityResolution ResolveFriendlyDataLinkIdentity(
            ref FriendlyMissileIdentityAssociation association,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched,
            double3 reportPosition,
            double3 reportVelocity,
            double reportStateTimestamp,
            double now,
            double reportFreshnessSeconds,
            double identityCoastSeconds,
            float maximumPositionGateMeters,
            float velocityGateMetersPerSecond,
            double minimumScoreSeparation,
            out int trackIndex,
            out double score)
        {
            trackIndex = -1;
            score = double.PositiveInfinity;
            if (tracks == null || trackAlreadyMatched == null)
            {
                association.Clear();
                return FriendlyIdentityResolution.None;
            }

            var reportAge = math.max(0.0, now - reportStateTimestamp);
            if (reportAge <= reportFreshnessSeconds)
            {
                var associatedTrackIndex = FindAssociatedTrack(
                    in association,
                    tracks,
                    trackCount,
                    trackAlreadyMatched);
                var associatedCandidateIndex = FindAssociatedCandidate(
                    in association,
                    tracks,
                    trackCount,
                    trackAlreadyMatched,
                    reportPosition,
                    reportVelocity,
                    reportStateTimestamp,
                    now,
                    reportFreshnessSeconds,
                    maximumPositionGateMeters,
                    velocityGateMetersPerSecond);
                if (TrySelectFriendlyDataLinkAssociation(
                        tracks,
                        trackCount,
                        trackAlreadyMatched,
                        reportPosition,
                        reportVelocity,
                        reportStateTimestamp,
                        now,
                        reportFreshnessSeconds,
                        maximumPositionGateMeters,
                        velocityGateMetersPerSecond,
                        minimumScoreSeparation,
                        out trackIndex,
                        out score))
                {
                    if (associatedTrackIndex >= 0 &&
                        trackIndex != associatedTrackIndex)
                    {
                        RefreshAuthenticatedTrackSupport(
                            ref association,
                            associatedCandidateIndex,
                            now);
                        return TryCoastAuthenticatedIdentity(
                            ref association,
                            tracks,
                            trackCount,
                            trackAlreadyMatched,
                            now,
                            identityCoastSeconds,
                            out trackIndex,
                            out score)
                            ? FriendlyIdentityResolution.Coasting
                            : FriendlyIdentityResolution.Ambiguous;
                    }

                    association.TrackId = tracks[trackIndex].TrackId;
                    association.LastAuthenticatedTime = now;
                    association.LastSupportedTime = now;
                    association.LastAssociationScore = score;
                    return FriendlyIdentityResolution.Authenticated;
                }

                if (CountFriendlyDataLinkCandidates(
                        tracks,
                        trackCount,
                        trackAlreadyMatched,
                        reportPosition,
                        reportVelocity,
                        reportStateTimestamp,
                        now,
                        reportFreshnessSeconds,
                        maximumPositionGateMeters,
                        velocityGateMetersPerSecond) > 1)
                {
                    RefreshAuthenticatedTrackSupport(
                        ref association,
                        associatedCandidateIndex,
                        now);
                    return TryCoastAuthenticatedIdentity(
                        ref association,
                        tracks,
                        trackCount,
                        trackAlreadyMatched,
                        now,
                        identityCoastSeconds,
                        out trackIndex,
                        out score)
                        ? FriendlyIdentityResolution.Coasting
                        : FriendlyIdentityResolution.Ambiguous;
                }

                if (associatedTrackIndex >= 0 &&
                    associatedCandidateIndex < 0)
                {
                    return TryCoastAuthenticatedIdentity(
                        ref association,
                        tracks,
                        trackCount,
                        trackAlreadyMatched,
                        now,
                        identityCoastSeconds,
                        out trackIndex,
                        out score)
                        ? FriendlyIdentityResolution.Coasting
                        : FriendlyIdentityResolution.Ambiguous;
                }
            }

            // Carry identity through radio silence only while the same radar track
            // is continuously observed. Fresh contradictory reports do NOT renew it.
            double lastSupport=math.max(association.LastAuthenticatedTime,association.LastSupportedTime);
            if(reportAge>reportFreshnessSeconds && association.HasTrack && now-lastSupport<=identityCoastSeconds)
            {
                int supported=FindAssociatedTrack(in association,tracks,trackCount,trackAlreadyMatched);
                if(supported>=0 && tracks[supported].KinematicUpdateTime>0 && now-tracks[supported].KinematicUpdateTime<=.25)
                    association.LastSupportedTime=now;
            }

            if (TryCoastAuthenticatedIdentity(
                    ref association,
                    tracks,
                    trackCount,
                    trackAlreadyMatched,
                    now,
                    identityCoastSeconds,
                    out trackIndex,
                    out score))
            {
                return FriendlyIdentityResolution.Coasting;
            }

            association.Clear();
            trackIndex = -1;
            score = double.PositiveInfinity;
            return FriendlyIdentityResolution.None;
        }

        private static bool TryCoastAuthenticatedIdentity(
            ref FriendlyMissileIdentityAssociation association,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched,
            double now,
            double maximumCoastSeconds,
            out int trackIndex,
            out double score)
        {
            trackIndex = -1;
            score = double.PositiveInfinity;
            if (!association.HasTrack)
            {
                return false;
            }
            var lastSupportTime = math.max(
                association.LastAuthenticatedTime,
                association.LastSupportedTime);
            if (now - lastSupportTime > maximumCoastSeconds)
            {
                association.Clear();
                return false;
            }

            var count = math.min(
                math.min(trackCount, tracks.Length),
                trackAlreadyMatched.Length);
            for (var i = 0; i < count; i++)
            {
                if (trackAlreadyMatched[i] ||
                    !tracks[i].TrackId.Equals(association.TrackId))
                {
                    continue;
                }

                trackIndex = i;
                score = association.LastAssociationScore;
                return true;
            }

            return false;
        }

        private static void RefreshAuthenticatedTrackSupport(
            ref FriendlyMissileIdentityAssociation association,
            int associatedCandidateIndex,
            double now)
        {
            if (association.HasTrack && associatedCandidateIndex >= 0)
            {
                association.LastSupportedTime = now;
            }
        }

        private static int FindAssociatedTrack(
            in FriendlyMissileIdentityAssociation association,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched)
        {
            if (!association.HasTrack)
            {
                return -1;
            }

            var count = math.min(
                math.min(trackCount, tracks.Length),
                trackAlreadyMatched.Length);
            for (var i = 0; i < count; i++)
            {
                if (!trackAlreadyMatched[i] &&
                    tracks[i].TrackId.Equals(association.TrackId))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindAssociatedCandidate(
            in FriendlyMissileIdentityAssociation association,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched,
            double3 reportPosition,
            double3 reportVelocity,
            double reportTimestamp,
            double now,
            double retentionSeconds,
            float maximumPositionGateMeters,
            float velocityGateMetersPerSecond)
        {
            if (!association.HasTrack)
            {
                return -1;
            }

            var count = math.min(
                math.min(trackCount, tracks.Length),
                trackAlreadyMatched.Length);
            for (var i = 0; i < count; i++)
            {
                if (trackAlreadyMatched[i] ||
                    !tracks[i].TrackId.Equals(association.TrackId))
                {
                    continue;
                }

                return TryScoreFriendlyDataLinkAssociation(
                    in tracks[i],
                    reportPosition,
                    reportVelocity,
                    reportTimestamp,
                    now,
                    retentionSeconds,
                    maximumPositionGateMeters,
                    velocityGateMetersPerSecond,
                    out _)
                    ? i
                    : -1;
            }

            return -1;
        }

        private static int CountFriendlyDataLinkCandidates(
            MissileTrackSnapshot[] tracks,
            int trackCount,
            bool[] trackAlreadyMatched,
            double3 reportPosition,
            double3 reportVelocity,
            double reportTimestamp,
            double now,
            double retentionSeconds,
            float maximumPositionGateMeters,
            float velocityGateMetersPerSecond)
        {
            var count = math.min(
                math.min(trackCount, tracks.Length),
                trackAlreadyMatched.Length);
            var candidates = 0;
            for (var i = 0; i < count; i++)
            {
                if (!trackAlreadyMatched[i] &&
                    TryScoreFriendlyDataLinkAssociation(
                        in tracks[i],
                        reportPosition,
                        reportVelocity,
                        reportTimestamp,
                        now,
                        retentionSeconds,
                        maximumPositionGateMeters,
                        velocityGateMetersPerSecond,
                        out _))
                {
                    candidates++;
                }
            }

            return candidates;
        }

        public static void ApplyFriendlyDataLinkIdentity(
            ref MissileTrackSnapshot track,
            double associationScore,
            MissileMissionRole missionRole = MissileMissionRole.Unknown,
            float missionRoleConfidence = 0f)
        {
            track.Affiliation = TrackAffiliation.Friendly;
            track.IdentityConfidence = math.clamp(
                (float)(1.0 - associationScore * 0.2),
                0.6f,
                1f);
            var knownMissionRole =
                missionRole == MissileMissionRole.AirToAir ||
                missionRole == MissileMissionRole.Interceptor;
            track.MissionRole = knownMissionRole
                ? missionRole
                : MissileMissionRole.Unknown;
            track.MissionRoleConfidence = knownMissionRole
                ? math.min(
                    track.IdentityConfidence,
                    math.saturate(missionRoleConfidence))
                : 0f;
            track.MissionRoleEvidence = knownMissionRole
                ? MissileMissionRoleEvidence.AuthenticatedDataLink
                : MissileMissionRoleEvidence.None;
            track.SourceMask |= MissileTrackSourceMask.DataLink;
        }

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
            TrackSnapshot[] radarTracks,
            int radarTrackCount,
            ThreatSnapshot[] threats,
            int threatCount,
            ElectroOpticalObservation[] electroOpticalObservations,
            int electroOpticalCount,
            LaserRangeObservation[] laserRangeObservations,
            int laserRangeCount,
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
                laserRangeObservations,
                laserRangeCount,
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
            return Fuse(
                in config,
                state,
                radarTracks,
                radarTrackCount,
                threats,
                threatCount,
                electroOpticalObservations,
                electroOpticalCount,
                null,
                0,
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
            LaserRangeObservation[] laserRangeObservations,
            int laserRangeCount,
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
                // A radio refinement changes this same kinematic estimate without
                // creating a radar hit. Preserve both clocks: radar freshness still
                // uses LastUpdateTime, while position/association freshness includes
                // the authenticated telemetry that actually updated the filter.
                double kinematicEvidenceTime=track.LastUpdateTime;
                if(track.SourceEvidence.DataLink.Valid && track.SourceEvidence.DataLink.Timestamp<=now)
                    kinematicEvidenceTime=math.max(kinematicEvidenceTime,track.SourceEvidence.DataLink.Timestamp);
                var lastEvidenceTime = kinematicEvidenceTime;
                var sensorMissionRole = MissileMissionRole.Unknown;
                var sensorMissionRoleConfidence = 0f;
                var seekerOwnshipEvidence = 0f;
                var lastSeekerMainlobeTime = -1.0;
                var seekerMainlobeContinuousSeconds = 0f;
                var seekerMainlobePowerTrend = 0f;

                AssociateThreats(
                    in config,
                    i,
                    radarTracks,
                    radarTrackCount,
                    ownshipPosition,
                    relativePosition,
                    threats,
                    threatCount,
                    now,
                    ref sourceMask,
                    ref confidence,
                    ref lastEvidenceTime,
                    ref sensorMissionRole,
                    ref sensorMissionRoleConfidence,
                    ref seekerOwnshipEvidence,
                    ref lastSeekerMainlobeTime,
                    ref seekerMainlobeContinuousSeconds,
                    ref seekerMainlobePowerTrend);
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
                var laserPositionUncertainty =
                    float.PositiveInfinity;
                var laserConfirmed =
                    AssociateLaserRangeObservation(
                        in config,
                        ownshipPosition,
                        ref estimatedPosition,
                        in track,
                        laserRangeObservations,
                        laserRangeCount,
                        now,
                        ref confidence,
                        ref lastEvidenceTime,
                        out laserPositionUncertainty);
                if (laserConfirmed)
                {
                    sourceMask |=
                        MissileTrackSourceMask.LaserRangefinder;
                    relativePosition =
                        estimatedPosition - ownshipPosition;
                }
                // Independent short-range panels provide repeated, precise local range/Doppler observations.
                var closeProtectionConfirmed=(track.SourceMask&TrackSourceMask.CloseProtectionRadar)!=0 &&
                    now-track.LastCloseProtectionTime<=.15 && track.HitCount>=3 && radarMissileClassified;
                var radarIndependentlyConfirmed=IsIndependentRadarWeaponsQuality(in track,ownshipPosition,now,ownshipVelocity);
                var independentCorroborated=closeProtectionConfirmed||(sourceMask & ~MissileTrackSourceMask.Radar)!=0;
                var corroborated=radarIndependentlyConfirmed||independentCorroborated;
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
                        timeToClosestApproach + (float)math.max(0, now - track.LastPredictTime));
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
                // An active seeker can still steer after the current estimate,
                // but it must at least be on a plausible ownship-intercept path.
                // Without this gate, an enemy interceptor chasing one of our
                // outgoing missiles is mislabeled as an ownship threat merely
                // because it is closing and its seeker is active.
                var guidedThreatMaximumCpa = math.max(
                    config.ProtectedRadiusMeters,
                    config.GuidedThreatMaximumCpaMeters);
                var guidedThreatCpaGate =
                    guidedThreatMaximumCpa +
                    math.min(
                        guidedThreatMaximumCpa,
                        cpaUncertainty);
                var guidedIncoming =
                    seekerOwnshipEvidence >=
                        config.SeekerMainlobeMinimumIncomingEvidence &&
                    range <= config.GuidedThreatRangeMeters &&
                    closingSpeed >=
                        config.MinimumClosingSpeedMetersPerSecond &&
                    closestApproachDistance <=
                        guidedThreatCpaGate;
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
                    !radarIndependentlyConfirmed && corroborated && !velocityResolved;
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
                var estimatedAcceleration = double3.zero;
                var estimatedJerk = double3.zero;

                if (state != null)
                {
                    var entry = state.GetOrCreate(track.TrackId);
                    UpdateAccelerationEstimate(
                        in config,
                        entry,
                        track.Velocity,
                        kinematicEvidenceTime,
                        track.GetCovariance(3,3)+track.GetCovariance(4,4)+track.GetCovariance(5,5),
                        closeProtectionConfirmed);
                    entry.LastSeenTime = now;
                    entry.LastEvidenceTime = math.max(
                        entry.LastEvidenceTime,
                        lastEvidenceTime);
                    if (independentCorroborated)
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

                    if (sensorMissionRole !=
                            MissileMissionRole.Unknown &&
                        sensorMissionRoleConfidence >=
                            config.ThreatRoleMinimumConfidence)
                    {
                        var sameRole =
                            entry.SensorMissionRole ==
                            sensorMissionRole;
                        var roleExpired =
                            now > entry.SensorMissionRoleUntil;
                        if (sameRole || roleExpired ||
                            sensorMissionRoleConfidence >=
                            entry.SensorMissionRoleConfidence + 0.15f)
                        {
                            entry.SensorMissionRole =
                                sensorMissionRole;
                            entry.SensorMissionRoleConfidence = sameRole
                                ? math.max(
                                    entry.SensorMissionRoleConfidence,
                                    sensorMissionRoleConfidence)
                                : sensorMissionRoleConfidence;
                            entry.SensorMissionRoleUntil =
                                now + math.max(
                                    0.0,
                                    config.ThreatRoleHoldSeconds);
                        }
                    }

                    if (now <= entry.SensorMissionRoleUntil)
                    {
                        sensorMissionRole =
                            entry.SensorMissionRole;
                        sensorMissionRoleConfidence =
                            entry.SensorMissionRoleConfidence;
                    }
                    else
                    {
                        entry.SensorMissionRole =
                            MissileMissionRole.Unknown;
                        entry.SensorMissionRoleConfidence = 0f;
                    }

                    var confirmationEntry =
                        radarFireControlUsable &&
                        (radarIndependentlyConfirmed||corroborationHeld) &&
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
                              (!radarIndependentlyConfirmed&&!corroborationHeld) ||
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
                    estimatedAcceleration =
                        entry.FilteredAcceleration;
                    estimatedJerk = entry.FilteredJerk;
                }

                if (confirmed)
                {
                    quality = MissileTrackQuality.WeaponsQuality;
                }

                // Position 是 track.PredictPosition(now) 外推出来的，所以它的不确定度必须把
                // "用带噪声的速度外推了多久" 算进去。原先用的 CalculatePositionUncertainty
                // 只取位置协方差（索引 0/1/2），完全忽略速度协方差与外推时长——刚分配的航迹
                // 速度 sigma 是 InitialVelocitySigmaMetersPerSecond = 300 m/s，外推 0.3 s
                // 就有上百米的真实误差，却报出一个很小的不确定度。
                //
                // 这直接造成"来袭导弹虚影"：同一枚导弹偶尔关联失败会新建一条航迹，新航迹
                // 速度接近 0 因而几乎不外推，而已收敛的真航迹按上千 m/s 正常外推，两者位置
                // 拉开数百米；FindDuplicate 的位置门限（300 m 或两者不确定度之和）拿到的却是
                // 被低估的不确定度，于是判为两个目标，HUD 上就出现两个框。
                //
                // 同一文件里 CalculateCpaPositionUncertainty 已经写了正确的协方差外推
                // （position + 2·t·cross + t²·velocity），这里按航迹年龄复用它即可。
                // 副作用都是想要的：去重门限自动放宽、火控交战门限会正确地拒绝对粗航迹开火、
                // HUD 的红/琥珀分级也变得诚实。
                var propagationAge = (float)math.max(
                    0.0,
                    now - track.LastPredictTime);
                var propagatedPositionUncertainty =
                    CalculateCpaPositionUncertainty(
                        in track,
                        propagationAge);
                // Publish every derivative at the same epoch as Position. Rebasing
                // only position to 'now' silently discarded the acceleration accrued
                // since the filter epoch and made short-fuse interception lag turns.
                double poseAge = math.min(propagationAge,
                    closeProtectionConfirmed ? .24 : .08);
                estimatedPosition+=.5*estimatedAcceleration*poseAge*poseAge
                    +(1.0/6.0)*estimatedJerk*poseAge*poseAge*poseAge;
                var estimatedVelocity=track.Velocity+estimatedAcceleration*poseAge
                    +.5*estimatedJerk*poseAge*poseAge;
                estimatedAcceleration+=estimatedJerk*poseAge;

                var candidate =
                    new MissileTrackSnapshot
                    {
                        TrackId = track.TrackId,
                        HasPositionCovariance=true,
                        PositionCovariance=PropagatedPositionCovariance(in track,propagationAge),
                        Position = estimatedPosition,
                        ObserverLineOfSight=math.normalizesafe((float3)relativePosition,new float3(0,0,1)),
                        RadialVelocityUncertaintyMetersPerSecond=RadialVelocitySigma(track,relativePosition),
                        VelocityUncertaintyMetersPerSecond=(float)math.sqrt(math.max(0,track.GetCovariance(3,3)+track.GetCovariance(4,4)+track.GetCovariance(5,5))),
                        Velocity = estimatedVelocity,
                        Acceleration = estimatedAcceleration,
                        Jerk = estimatedJerk,
                        PositionUncertaintyMeters =
                            laserConfirmed
                                ? math.min(
                                    propagatedPositionUncertainty,
                                    laserPositionUncertainty)
                                : propagatedPositionUncertainty,
                        CpaUncertaintyMeters =
                            cpaUncertainty,
                        Confidence = confidence,
                        MissionRole = sensorMissionRole,
                        MissionRoleConfidence =
                            sensorMissionRoleConfidence,
                        MissionRoleEvidence =
                            sensorMissionRole ==
                                MissileMissionRole.Unknown
                                ? MissileMissionRoleEvidence.None
                                : MissileMissionRoleEvidence.EsmWaveform,
                        SeekerOwnshipEvidence = seekerOwnshipEvidence,
                        LastSeekerMainlobeTime =
                            lastSeekerMainlobeTime,
                        SeekerMainlobeContinuousSeconds =
                            seekerMainlobeContinuousSeconds,
                        SeekerMainlobePowerTrendDbPerSecond =
                            seekerMainlobePowerTrend,
                        SourceMask = sourceMask,
                        Quality = quality,
                        Confirmed = confirmed,
                        Incoming = incoming,
                        TimeToClosestApproachSeconds =
                            timeToClosestApproach,
                        ClosestApproachDistanceMeters =
                            closestApproachDistance,
                        // Bearing-only corroboration cannot refresh the age of a 3D position.
                        FirstObservationTime = track.FirstObservationTime,
                        LastEvidenceTime = kinematicEvidenceTime,
                        KinematicUpdateTime = kinematicEvidenceTime,
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

        private static void UpdateAccelerationEstimate(
            in MissileTrackFusionConfig config,
            MissileTrackFusionState.Entry entry,
            double3 velocity,
            double sampleTime,
            double velocityVariance,
            bool preciseLocalVelocity)
        {
            if (!entry.HasVelocitySample)
            {
                entry.PreviousVelocity = velocity;
                entry.PreviousVelocityTime = sampleTime;
                entry.PreviousVelocityVariance = velocityVariance;
                entry.HasVelocitySample = true;
                return;
            }

            var elapsed = sampleTime - entry.PreviousVelocityTime;
            if (elapsed <= 1e-4)
            {
                return;
            }

            var velocityChange = velocity - entry.PreviousVelocity;
            var resolvedChange = math.lengthsq(velocityChange) >
                9 * math.max(1e-6,
                    velocityVariance + entry.PreviousVelocityVariance);
            var rawAcceleration = velocityChange / elapsed;
            // Unresolved radar velocity noise must not become a physical turn command.
            // Precise local receivers retain their existing fast-response path.
            if (!preciseLocalVelocity && !resolvedChange) rawAcceleration = double3.zero;
            var maximumAcceleration = math.max(
                0.0,
                config.MaximumTrackAccelerationMetersPerSecondSquared);
            var magnitude = math.length(rawAcceleration);
            if (maximumAcceleration > 0.0 &&
                magnitude > maximumAcceleration)
            {
                rawAcceleration *= maximumAcceleration / magnitude;
            }

            var filterTime = math.max(
                0.0,
                config.AccelerationFilterTimeSeconds);
            var blend = filterTime <= 1e-4
                ? 1.0
                : 1.0 - math.exp(-elapsed / filterTime);
            if (preciseLocalVelocity && resolvedChange) blend = 1;
            entry.FilteredAcceleration = math.lerp(
                entry.FilteredAcceleration,
                rawAcceleration,
                blend);
            var accelerationResolved = preciseLocalVelocity || resolvedChange;
            if (entry.HasAccelerationSample)
            {
                var rawJerk =
                    (rawAcceleration -
                     entry.PreviousRawAcceleration) / elapsed;
                if (!accelerationResolved || !entry.PreviousAccelerationResolved)
                    rawJerk = double3.zero;
                var maximumJerk = math.max(
                    0.0,
                    config.MaximumTrackJerkMetersPerSecondCubed);
                var jerkMagnitude = math.length(rawJerk);
                if (maximumJerk > 0.0 &&
                    jerkMagnitude > maximumJerk)
                {
                    rawJerk *= maximumJerk / jerkMagnitude;
                }

                var jerkFilterTime = math.max(
                    0.0,
                    config.JerkFilterTimeSeconds);
                var jerkBlend = jerkFilterTime <= 1e-4
                    ? 1.0
                    : 1.0 -
                      math.exp(-elapsed / jerkFilterTime);
                entry.FilteredJerk = math.lerp(
                    entry.FilteredJerk,
                    rawJerk,
                    jerkBlend);
            }
            else
            {
                entry.HasAccelerationSample = true;
            }

            entry.PreviousAccelerationResolved = accelerationResolved;
            entry.PreviousRawAcceleration = rawAcceleration;
            entry.PreviousVelocity = velocity;
            entry.PreviousVelocityTime = sampleTime;
            entry.PreviousVelocityVariance = velocityVariance;
        }

        private static float RadialVelocitySigma(in TrackSnapshot track,double3 relativePosition)
        {
            var los=math.normalizesafe(relativePosition,new double3(0,0,1));double variance=0;
            for(int row=0;row<3;row++)for(int col=0;col<3;col++)variance+=los[row]*track.GetCovariance(row+3,col+3)*los[col];
            return (float)math.sqrt(math.max(0,variance));
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
            if (candidate.SeekerOwnshipEvidence >
                existing.SeekerOwnshipEvidence)
            {
                merged.SeekerOwnshipEvidence =
                    candidate.SeekerOwnshipEvidence;
                merged.LastSeekerMainlobeTime =
                    candidate.LastSeekerMainlobeTime;
                merged.SeekerMainlobeContinuousSeconds =
                    candidate.SeekerMainlobeContinuousSeconds;
                merged.SeekerMainlobePowerTrendDbPerSecond =
                    candidate.SeekerMainlobePowerTrendDbPerSecond;
            }
            if (existing.MissionRoleConfidence >
                candidate.MissionRoleConfidence)
            {
                merged.MissionRole = existing.MissionRole;
                merged.MissionRoleConfidence =
                    existing.MissionRoleConfidence;
                merged.MissionRoleEvidence =
                    existing.MissionRoleEvidence;
            }
            else
            {
                merged.MissionRole = candidate.MissionRole;
                merged.MissionRoleConfidence =
                    candidate.MissionRoleConfidence;
                merged.MissionRoleEvidence =
                    candidate.MissionRoleEvidence;
            }
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
            merged.KinematicUpdateTime =
                math.max(
                    existing.KinematicUpdateTime,
                    candidate.KinematicUpdateTime);
            merged.LastUpdateTime =
                math.max(
                    existing.LastUpdateTime,
                    candidate.LastUpdateTime);
            return merged;
        }

        private static void AssociateThreats(
            in MissileTrackFusionConfig config,
            int radarTrackIndex,
            TrackSnapshot[] radarTracks,
            int radarTrackCount,
            double3 ownshipPosition,
            double3 relativePosition,
            ThreatSnapshot[] threats,
            int threatCount,
            double now,
            ref MissileTrackSourceMask sourceMask,
            ref float confidence,
            ref double lastUpdate,
            ref MissileMissionRole missionRole,
            ref float missionRoleConfidence,
            ref float seekerOwnshipEvidence,
            ref double lastSeekerMainlobeTime,
            ref float seekerMainlobeContinuousSeconds,
            ref float seekerMainlobePowerTrend)
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

                if (EsmWaveformClassification.IsMissileSeeker(
                        threat.WaveformClass))
                {
                    rwrConfidence = math.max(
                        rwrConfidence,
                        threat.IdentificationConfidence);
                    rwrUpdate = math.max(
                        rwrUpdate,
                        threat.LastSeenTime);
                    if (TryAssociateThreatToRadarTrack(
                            in config,
                            radarTrackIndex,
                            radarTracks,
                            radarTrackCount,
                            ownshipPosition,
                            in threat,
                            now,
                            out var angularConfidence))
                    {
                        if (EsmWaveformClassification
                                .TryGetMissileMissionRole(
                                    threat.WaveformClass,
                                    out var observedRole))
                        {
                            var observedConfidence =
                                threat.IdentificationConfidence *
                                angularConfidence;
                            if (observedConfidence >
                                missionRoleConfidence)
                            {
                                missionRole = observedRole;
                                missionRoleConfidence =
                                    observedConfidence;
                            }
                        }

                        if (threat.LastMainlobeSeenTime >= 0.0 &&
                            now - threat.LastMainlobeSeenTime <=
                                config.SeekerMainlobeMaximumAgeSeconds)
                        {
                            var continuousEvidence = math.saturate(
                                threat.ContinuousMainlobeIlluminationSeconds /
                                math.max(
                                    0.01f,
                                    config.SeekerMainlobeContinuousEvidenceSeconds));
                            var powerTrendEvidence = math.saturate(
                                (threat.MainlobePowerTrendDbPerSecond -
                                 config.SeekerMainlobeRisingPowerThresholdDbPerSecond) /
                                math.max(
                                    0.01f,
                                    config.SeekerMainlobeFullPowerTrendDbPerSecond -
                                    config.SeekerMainlobeRisingPowerThresholdDbPerSecond));
                            var observedEvidence =
                                threat.IdentificationConfidence *
                                angularConfidence *
                                (0.45f +
                                 continuousEvidence * 0.35f +
                                 powerTrendEvidence * 0.2f);
                            if (observedEvidence > seekerOwnshipEvidence)
                            {
                                seekerOwnshipEvidence = observedEvidence;
                                lastSeekerMainlobeTime =
                                    threat.LastMainlobeSeenTime;
                                seekerMainlobeContinuousSeconds =
                                    threat.ContinuousMainlobeIlluminationSeconds;
                                seekerMainlobePowerTrend =
                                    threat.MainlobePowerTrendDbPerSecond;
                            }
                        }
                    }
                }
                else if (threat.WaveformClass ==
                         EsmWaveformClass.InfraredPlume)
                {
                    mawConfidence = math.max(
                        mawConfidence,
                        threat.IdentificationConfidence);
                    mawUpdate = math.max(
                        mawUpdate,
                        threat.LastSeenTime);
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

        private static bool TryAssociateThreatToRadarTrack(
            in MissileTrackFusionConfig config,
            int expectedTrackIndex,
            TrackSnapshot[] radarTracks,
            int radarTrackCount,
            double3 ownshipPosition,
            in ThreatSnapshot threat,
            double now,
            out float confidence)
        {
            confidence = 0f;
            if (radarTracks == null ||
                expectedTrackIndex < 0 ||
                expectedTrackIndex >= radarTrackCount)
            {
                return false;
            }

            var threatDirection = math.normalizesafe(
                threat.DirectionWorld,
                new float3(0f, 0f, 1f));
            var bestAngle = float.PositiveInfinity;
            var secondBestAngle = float.PositiveInfinity;
            var bestIndex = -1;
            var count = math.min(radarTrackCount, radarTracks.Length);
            for (var i = 0; i < count; i++)
            {
                var candidate = radarTracks[i];
                if (candidate.Lifecycle == TrackLifecycle.Dropped ||
                    (candidate.ObjectClass != TrackObjectClass.Missile &&
                     candidate.ObjectClass != TrackObjectClass.Unknown) ||
                    now - candidate.LastUpdateTime >
                        config.ThreatMaximumAgeSeconds)
                {
                    continue;
                }

                var relativePosition =
                    candidate.PredictPosition(now) -
                    ownshipPosition;
                if (math.lengthsq(relativePosition) <= 0.01)
                {
                    continue;
                }

                var candidateDirection = math.normalizesafe(
                    (float3)relativePosition,
                    new float3(0f, 0f, 1f));
                var angle = math.acos(math.clamp(
                    math.dot(candidateDirection, threatDirection),
                    -1f,
                    1f));
                if (angle < bestAngle)
                {
                    secondBestAngle = bestAngle;
                    bestAngle = angle;
                    bestIndex = i;
                }
                else if (angle < secondBestAngle)
                {
                    secondBestAngle = angle;
                }
            }

            var roleGate = config.ThreatRoleAssociationRadians > 0f
                ? config.ThreatRoleAssociationRadians
                : config.ThreatAssociationRadians;
            var gate = math.max(
                math.radians(0.1f),
                math.min(config.ThreatAssociationRadians, roleGate));
            if (bestIndex != expectedTrackIndex ||
                bestAngle > gate ||
                (math.isfinite(secondBestAngle) &&
                 secondBestAngle - bestAngle <
                    config.ThreatRoleAssociationSeparationRadians))
            {
                return false;
            }

            var angularQuality = math.saturate(
                1f - bestAngle / gate);
            confidence = math.lerp(0.7f, 1f, angularQuality);
            return true;
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
            var secondBestAngle = float.PositiveInfinity;
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
                if (angle > associationGate) continue;
                if (angle < bestAngle)
                {
                    secondBestAngle = bestAngle;
                    bestIndex = i;
                    bestAngle = angle;
                }
                else if (angle < secondBestAngle)
                {
                    secondBestAngle = angle;
                }
            }

            if (bestIndex < 0 ||
                (math.isfinite(secondBestAngle) &&
                 secondBestAngle - bestAngle < math.radians(.25f)))
            {
                return false;
            }

            var best = observations[bestIndex];
            // A monocular bearing corroborates classification; it is not a new Cartesian
            // position fix. Never rotate the radar state by a fixed confidence weight:
            // that discards its covariance and alternates positions at the EO sample rate.
            confidence = math.saturate(
                confidence + 0.1f +
                best.ClassificationConfidence * 0.08f);
            lastUpdate =
                math.max(lastUpdate, best.Timestamp);
            return true;
        }

        private static bool AssociateLaserRangeObservation(
            in MissileTrackFusionConfig config,
            double3 ownshipPosition,
            ref double3 estimatedPosition,
            in TrackSnapshot track,
            LaserRangeObservation[] observations,
            int observationCount,
            double now,
            ref float confidence,
            ref double lastUpdate,
            out float positionUncertaintyMeters)
        {
            positionUncertaintyMeters = float.PositiveInfinity;
            if (observations == null)
            {
                return false;
            }

            var relativePosition =
                estimatedPosition - ownshipPosition;
            var expectedRange = math.length(relativePosition);
            if (expectedRange <= 1.0)
            {
                return false;
            }

            var expectedDirection = math.normalizesafe(
                (float3)relativePosition,
                new float3(0f, 0f, 1f));
            var bestIndex = -1;
            var bestScore = float.PositiveInfinity;
            var count = math.min(
                observationCount,
                observations.Length);
            for (var i = 0; i < count; i++)
            {
                var observation = observations[i];
                if ((observation.CuedTrackId.IsValid && !observation.CuedTrackId.Equals(track.TrackId)) ||
                    observation.ObjectClass !=
                        TrackObjectClass.Missile ||
                    observation.ClassificationConfidence < 0.25f ||
                    now - observation.Timestamp >
                        config.LaserRangeMaximumAgeSeconds)
                {
                    continue;
                }

                var direction = math.normalizesafe(
                    observation.Direction,
                    expectedDirection);
                var angle = math.acos(math.clamp(
                    math.dot(expectedDirection, direction),
                    -1f,
                    1f));
                var angleGate = math.max(
                    config.LaserRangeAssociationGateRadians,
                    math.sqrt(math.max(
                        observation.AngleVar,
                        1e-10f)) * 4f);
                var rangeResidual = math.abs(
                    observation.RangeMeters -
                    (float)expectedRange);
                var rangeGate = math.max(
                    config.LaserRangeAssociationGateMeters,
                    math.sqrt(math.max(
                        observation.RangeVar,
                        0.01f)) * 4f);
                if (angle > angleGate ||
                    rangeResidual > rangeGate)
                {
                    continue;
                }

                var score =
                    angle / math.max(angleGate, 1e-6f) +
                    rangeResidual / math.max(rangeGate, 0.1f);
                if (score < bestScore)
                {
                    bestIndex = i;
                    bestScore = score;
                }
            }

            if (bestIndex < 0)
            {
                return false;
            }

            var best = observations[bestIndex];
            // The tracking filter already assimilates cued laser returns. Reapplying
            // an old return here would undo prediction and count the same evidence twice.
            if (track.SourceEvidence.Laser.Valid && track.SourceEvidence.Laser.Timestamp >= best.Timestamp)
            {
                lastUpdate = math.max(lastUpdate, best.Timestamp);
                return true;
            }
            var measuredDirection = math.normalizesafe(
                best.Direction,
                expectedDirection);
            estimatedPosition =
                ownshipPosition +
                (double3)measuredDirection *
                math.max(0.1f, best.RangeMeters);
            var angularSigma = math.sqrt(
                math.max(best.AngleVar, 1e-10f));
            var rangeSigma = math.sqrt(
                math.max(best.RangeVar, 0.01f));
            var transverseSigma =
                math.max(0.1f, best.RangeMeters) *
                angularSigma;
            positionUncertaintyMeters = math.sqrt(
                rangeSigma * rangeSigma +
                2f * transverseSigma * transverseSigma);
            confidence = math.saturate(
                confidence + 0.12f +
                best.ClassificationConfidence * 0.06f);
            lastUpdate = math.max(lastUpdate, best.Timestamp);
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

    public static class TrackReacquisition
    {
        public static bool TrySelectCandidate(
            in TrackSnapshot previousTrack,
            TrackSnapshot[] candidates,
            int candidateCount,
            double now,
            float basePositionGateMeters,
            out TrackSnapshot selected)
        {
            selected = default;
            if (candidates == null || !previousTrack.TrackId.IsValid)
            {
                return false;
            }

            var previousPosition = previousTrack.PredictPosition(now);
            var previousAge = math.max(
                0.0,
                now - previousTrack.LastUpdateTime);
            var positionGate = math.max(
                basePositionGateMeters,
                basePositionGateMeters +
                math.length(previousTrack.Velocity) * previousAge * 0.5);
            var positionGateSquared = positionGate * positionGate;
            var bestDistanceSquared = double.PositiveInfinity;
            var bestConfidence = float.NegativeInfinity;
            var count = math.min(candidateCount, candidates.Length);
            for (var i = 0; i < count; i++)
            {
                var candidate = candidates[i];
                if (!candidate.TrackId.IsValid ||
                    candidate.TrackId.Equals(previousTrack.TrackId) ||
                    candidate.Lifecycle == TrackLifecycle.Dropped ||
                    !IsClassCompatible(
                        previousTrack.ObjectClass,
                        candidate.ObjectClass))
                {
                    continue;
                }

                var distanceSquared = math.distancesq(
                    previousPosition,
                    candidate.PredictPosition(now));
                if (distanceSquared > positionGateSquared ||
                    distanceSquared > bestDistanceSquared ||
                    (distanceSquared == bestDistanceSquared &&
                     candidate.Confidence <= bestConfidence))
                {
                    continue;
                }

                bestDistanceSquared = distanceSquared;
                bestConfidence = candidate.Confidence;
                selected = candidate;
            }

            return selected.TrackId.IsValid;
        }

        private static bool IsClassCompatible(
            TrackObjectClass previousClass,
            TrackObjectClass candidateClass) =>
            previousClass == TrackObjectClass.Unknown ||
            previousClass == candidateClass;
    }
}
