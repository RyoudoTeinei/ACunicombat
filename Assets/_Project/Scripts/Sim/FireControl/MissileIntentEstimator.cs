using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;

namespace ACunicombat.FireControl
{
    public struct FriendlyMissileIntentCandidate
    {
        public TrackId TrackId;
        public double3 Position;
        public double3 Velocity;
        public double LastEvidenceTime;

        public readonly double3 PredictPosition(double now) =>
            Position + Velocity * math.max(0.0, now - LastEvidenceTime);
    }

    public struct MissileIntentEstimatorConfig
    {
        public double MaximumEvidenceAgeSeconds;
        public double StateRetentionSeconds;
        public float MaximumCandidateRangeMeters;
        public float MaximumLeadTimeSeconds;
        public float MinimumClosingSpeedMetersPerSecond;
        public float FullHeadingEvidenceDegrees;
        public float ZeroHeadingEvidenceDegrees;
        public float MinimumGuidanceAccelerationMetersPerSecondSquared;
        public float GuidanceDirectionToleranceDegrees;
        public float EvidenceFilterTimeSeconds;
        public float MinimumEvidenceDurationSeconds;
        public int MinimumEvidenceSamples;
        public float MinimumCandidateScore;
        public float ScoreSeparationForFullConfidence;
        public float EnterConfidence;
        public float ExitConfidence;
        public float SwitchConfidence;
        public float SwitchScoreMargin;
        public int ExitEvidenceSamples;

        public static MissileIntentEstimatorConfig Default =>
            new MissileIntentEstimatorConfig
            {
                MaximumEvidenceAgeSeconds = 0.65,
                StateRetentionSeconds = 5.0,
                MaximumCandidateRangeMeters = 60_000f,
                MaximumLeadTimeSeconds = 12f,
                MinimumClosingSpeedMetersPerSecond = 15f,
                FullHeadingEvidenceDegrees = 3f,
                ZeroHeadingEvidenceDegrees = 30f,
                MinimumGuidanceAccelerationMetersPerSecondSquared = 8f,
                GuidanceDirectionToleranceDegrees = 65f,
                EvidenceFilterTimeSeconds = 0.45f,
                MinimumEvidenceDurationSeconds = 0.45f,
                MinimumEvidenceSamples = 4,
                MinimumCandidateScore = 0.48f,
                ScoreSeparationForFullConfidence = 0.24f,
                EnterConfidence = 0.75f,
                ExitConfidence = 0.48f,
                SwitchConfidence = 0.9f,
                SwitchScoreMargin = 0.18f,
                ExitEvidenceSamples = 3,
            };
    }

    public sealed class MissileIntentEstimatorState
    {
        internal sealed class Entry
        {
            public MissileTargetIntent Intent;
            public float Confidence;
            public TrackId SuspectedTargetTrackId;
            public float OwnshipScore;
            public float FriendlyMissileScore;
            public double FirstEvidenceTime;
            public double LastKinematicUpdateTime = double.NegativeInfinity;
            public double LastSeenTime;
            public int EvidenceSamples;
            public int ExitEvidenceCount;
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

    public static class MissileIntentEstimator
    {
        public static void Evaluate(
            in MissileIntentEstimatorConfig config,
            MissileIntentEstimatorState state,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            double3 ownshipPosition,
            double3 ownshipVelocity,
            double now)
        {
            Evaluate(
                in config,
                state,
                tracks,
                trackCount,
                null,
                0,
                ownshipPosition,
                ownshipVelocity,
                now);
        }

        public static void Evaluate(
            in MissileIntentEstimatorConfig config,
            MissileIntentEstimatorState state,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            FriendlyMissileIntentCandidate[] friendlyMissileCandidates,
            int friendlyMissileCandidateCount,
            double3 ownshipPosition,
            double3 ownshipVelocity,
            double now)
        {
            if (state == null || tracks == null || trackCount <= 0)
            {
                return;
            }

            var count = math.min(trackCount, tracks.Length);
            state.Prune(now, math.max(0.0, config.StateRetentionSeconds));
            for (var i = 0; i < count; i++)
            {
                ref var track = ref tracks[i];
                if (track.IsFriendly || !track.TrackId.IsValid)
                {
                    continue;
                }

                var entry = state.GetOrCreate(track.TrackId);
                entry.LastSeenTime = now;
                if (now - track.LastEvidenceTime <=
                        math.max(0.0, config.MaximumEvidenceAgeSeconds) &&
                    track.KinematicUpdateTime >
                        entry.LastKinematicUpdateTime + 1e-6)
                {
                    UpdateEvidence(
                        in config,
                        entry,
                        in track,
                        tracks,
                        count,
                        friendlyMissileCandidates,
                        friendlyMissileCandidateCount,
                        ownshipPosition,
                        ownshipVelocity,
                        now);
                }

                ApplyAssessment(ref track, entry);
            }
        }

        private static void UpdateEvidence(
            in MissileIntentEstimatorConfig config,
            MissileIntentEstimatorState.Entry entry,
            in MissileTrackSnapshot track,
            MissileTrackSnapshot[] tracks,
            int trackCount,
            FriendlyMissileIntentCandidate[] friendlyMissileCandidates,
            int friendlyMissileCandidateCount,
            double3 ownshipPosition,
            double3 ownshipVelocity,
            double now)
        {
            var ownshipScore = ScoreCandidate(
                in config,
                in track,
                ownshipPosition,
                ownshipVelocity);
            var friendlyMissileScore = 0f;
            var friendlyMissileTarget = default(TrackId);
            for (var i = 0; i < trackCount; i++)
            {
                var candidate = tracks[i];
                if (!candidate.IsFriendly ||
                    candidate.TrackId.Equals(track.TrackId) ||
                    now - candidate.LastEvidenceTime >
                        config.MaximumEvidenceAgeSeconds)
                {
                    continue;
                }

                var score = ScoreCandidate(
                    in config,
                    in track,
                    candidate.PredictPosition(now),
                    candidate.PredictVelocity(now));
                if (score > friendlyMissileScore)
                {
                    friendlyMissileScore = score;
                    friendlyMissileTarget = candidate.TrackId;
                }
            }

            var candidateCount = friendlyMissileCandidates == null
                ? 0
                : math.min(
                    friendlyMissileCandidateCount,
                    friendlyMissileCandidates.Length);
            for (var i = 0; i < candidateCount; i++)
            {
                var candidate = friendlyMissileCandidates[i];
                if ((candidate.TrackId.IsValid &&
                     candidate.TrackId.Equals(track.TrackId)) ||
                    now - candidate.LastEvidenceTime >
                        config.MaximumEvidenceAgeSeconds)
                {
                    continue;
                }

                var candidatePosition = candidate.PredictPosition(now);
                var score = ScoreCandidate(
                    in config,
                    in track,
                    candidatePosition,
                    candidate.Velocity);
                if (score > friendlyMissileScore)
                {
                    friendlyMissileScore = score;
                    friendlyMissileTarget = candidate.TrackId;
                }
            }

            var previousKinematicTime = entry.LastKinematicUpdateTime;
            entry.LastKinematicUpdateTime = track.KinematicUpdateTime;
            if (entry.EvidenceSamples == 0)
            {
                entry.FirstEvidenceTime = now;
                entry.OwnshipScore = ownshipScore;
                entry.FriendlyMissileScore = friendlyMissileScore;
            }
            else
            {
                var elapsed = math.max(
                    0.0,
                    track.KinematicUpdateTime - previousKinematicTime);
                var filterTime = math.max(
                    0.0,
                    config.EvidenceFilterTimeSeconds);
                var blend = filterTime <= 1e-4
                    ? 1f
                    : (float)(1.0 - math.exp(-elapsed / filterTime));
                entry.OwnshipScore = math.lerp(
                    entry.OwnshipScore,
                    ownshipScore,
                    blend);
                entry.FriendlyMissileScore = math.lerp(
                    entry.FriendlyMissileScore,
                    friendlyMissileScore,
                    blend);
            }

            entry.EvidenceSamples++;
            ResolveIntent(
                in config,
                entry,
                friendlyMissileTarget,
                now);
        }

        private static void ResolveIntent(
            in MissileIntentEstimatorConfig config,
            MissileIntentEstimatorState.Entry entry,
            TrackId friendlyMissileTarget,
            double now)
        {
            var ownshipLeads =
                entry.OwnshipScore >= entry.FriendlyMissileScore;
            var candidateIntent = ownshipLeads
                ? MissileTargetIntent.Ownship
                : MissileTargetIntent.FriendlyMissile;
            var bestScore = ownshipLeads
                ? entry.OwnshipScore
                : entry.FriendlyMissileScore;
            var competingScore = ownshipLeads
                ? entry.FriendlyMissileScore
                : entry.OwnshipScore;
            var scoreStrength = math.saturate(
                (bestScore - config.MinimumCandidateScore) /
                math.max(0.01f, 1f - config.MinimumCandidateScore));
            var scoreSeparation = math.saturate(
                (bestScore - competingScore) /
                math.max(
                    0.01f,
                    config.ScoreSeparationForFullConfidence));
            var requiredSamples = math.max(
                2,
                config.MinimumEvidenceSamples);
            var sampleMaturity = math.saturate(
                (entry.EvidenceSamples - 1f) /
                (requiredSamples - 1f));
            var timeMaturity = math.saturate(
                (float)((now - entry.FirstEvidenceTime) /
                        math.max(
                            0.01,
                            config.MinimumEvidenceDurationSeconds)));
            var candidateConfidence =
                scoreStrength *
                scoreSeparation *
                math.min(sampleMaturity, timeMaturity);

            if (entry.Intent == MissileTargetIntent.Unknown)
            {
                if (candidateConfidence >= config.EnterConfidence)
                {
                    SetIntent(
                        entry,
                        candidateIntent,
                        candidateConfidence,
                        friendlyMissileTarget);
                }
                else
                {
                    entry.Confidence = candidateConfidence;
                }
                return;
            }

            if (entry.Intent == candidateIntent)
            {
                if (candidateIntent == MissileTargetIntent.FriendlyMissile)
                {
                    entry.SuspectedTargetTrackId = friendlyMissileTarget;
                }
                if (candidateConfidence < config.ExitConfidence)
                {
                    HoldOrClearIntent(
                        in config,
                        entry,
                        candidateConfidence);
                }
                else
                {
                    entry.ExitEvidenceCount = 0;
                    entry.Confidence = candidateConfidence;
                }
                return;
            }

            var retainedScore = entry.Intent == MissileTargetIntent.Ownship
                ? entry.OwnshipScore
                : entry.FriendlyMissileScore;
            if (candidateConfidence >= config.SwitchConfidence &&
                bestScore >= retainedScore + config.SwitchScoreMargin)
            {
                SetIntent(
                    entry,
                    candidateIntent,
                    candidateConfidence,
                    friendlyMissileTarget);
            }
            else
            {
                HoldOrClearIntent(
                    in config,
                    entry,
                    candidateConfidence);
            }
        }

        private static float ScoreCandidate(
            in MissileIntentEstimatorConfig config,
            in MissileTrackSnapshot missile,
            double3 targetPosition,
            double3 targetVelocity)
        {
            var offset = targetPosition - missile.Position;
            var range = math.length(offset);
            var missileSpeed = math.length(missile.Velocity);
            if (range <= 1.0 ||
                range > config.MaximumCandidateRangeMeters ||
                missileSpeed <= 1.0)
            {
                return 0f;
            }

            var leadPoint = (double3)MissileEngagementMath.PredictLeadPoint(
                ToFloat3(missile.Position),
                ToFloat3(targetPosition),
                ToFloat3(targetVelocity),
                (float)missileSpeed,
                config.MaximumLeadTimeSeconds);
            var aimDirection = math.normalizesafe(
                leadPoint - missile.Position);
            var velocityDirection = missile.Velocity / missileSpeed;
            var headingAngle = math.degrees(
                math.acos(
                    math.clamp(
                        math.dot(velocityDirection, aimDirection),
                        -1.0,
                        1.0)));
            var headingScore = InverseRange(
                (float)headingAngle,
                config.FullHeadingEvidenceDegrees,
                config.ZeroHeadingEvidenceDegrees);

            var relativeVelocity = targetVelocity - missile.Velocity;
            var losDirection = offset / range;
            var closingSpeed = -math.dot(relativeVelocity, losDirection);
            var closingScore = math.saturate(
                (float)(closingSpeed -
                        config.MinimumClosingSpeedMetersPerSecond) /
                math.max(50f, (float)missileSpeed * 0.65f));

            var observedLateralAcceleration =
                missile.Acceleration -
                velocityDirection *
                math.dot(missile.Acceleration, velocityDirection);
            var observedAccelerationMagnitude =
                math.length(observedLateralAcceleration);
            var losRate = math.cross(offset, relativeVelocity) /
                          math.max(1.0, range * range);
            var expectedAcceleration = PnGuidance.Command(
                (float3)losRate,
                (float3)losDirection,
                (float)math.max(0.0, closingSpeed),
                3.5f,
                600f);
            var expectedAccelerationMagnitude =
                math.length(expectedAcceleration);
            if (observedAccelerationMagnitude >=
                    config.MinimumGuidanceAccelerationMetersPerSecondSquared &&
                expectedAccelerationMagnitude >=
                    config.MinimumGuidanceAccelerationMetersPerSecondSquared)
            {
                var guidanceDot = math.clamp(
                    math.dot(
                        observedLateralAcceleration /
                            observedAccelerationMagnitude,
                        (double3)(expectedAcceleration /
                            expectedAccelerationMagnitude)),
                    -1.0,
                    1.0);
                var guidanceAngle = (float)math.degrees(
                    math.acos(guidanceDot));
                var guidanceScore = InverseRange(
                    guidanceAngle,
                    0f,
                    config.GuidanceDirectionToleranceDegrees);
                return math.saturate(
                    headingScore * 0.55f +
                    closingScore * 0.15f +
                    guidanceScore * 0.3f);
            }

            return math.saturate(
                headingScore * 0.72f + closingScore * 0.28f);
        }

        private static float InverseRange(
            float value,
            float fullEvidence,
            float zeroEvidence)
        {
            if (zeroEvidence <= fullEvidence + 1e-4f)
            {
                return value <= fullEvidence ? 1f : 0f;
            }

            return 1f - math.saturate(
                (value - fullEvidence) /
                (zeroEvidence - fullEvidence));
        }

        private static float3 ToFloat3(double3 value) =>
            new float3(
                (float)value.x,
                (float)value.y,
                (float)value.z);

        private static void SetIntent(
            MissileIntentEstimatorState.Entry entry,
            MissileTargetIntent intent,
            float confidence,
            TrackId friendlyMissileTarget)
        {
            entry.Intent = intent;
            entry.Confidence = confidence;
            entry.ExitEvidenceCount = 0;
            entry.SuspectedTargetTrackId =
                intent == MissileTargetIntent.FriendlyMissile
                    ? friendlyMissileTarget
                    : default;
        }

        private static void ClearIntent(
            MissileIntentEstimatorState.Entry entry)
        {
            entry.Intent = MissileTargetIntent.Unknown;
            entry.Confidence = 0f;
            entry.SuspectedTargetTrackId = default;
            entry.ExitEvidenceCount = 0;
        }

        private static void HoldOrClearIntent(
            in MissileIntentEstimatorConfig config,
            MissileIntentEstimatorState.Entry entry,
            float contraryConfidence)
        {
            entry.ExitEvidenceCount++;
            entry.Confidence = math.max(
                contraryConfidence,
                entry.Confidence * 0.85f);
            if (entry.ExitEvidenceCount >=
                math.max(1, config.ExitEvidenceSamples))
            {
                ClearIntent(entry);
            }
        }

        private static void ApplyAssessment(
            ref MissileTrackSnapshot track,
            MissileIntentEstimatorState.Entry entry)
        {
            var sensorMissionRole = track.MissionRole;
            var sensorMissionRoleConfidence =
                track.MissionRoleConfidence;
            var sensorMissionRoleEvidence =
                track.MissionRoleEvidence;
            track.TargetIntent = entry.Intent;
            track.TargetIntentConfidence = entry.Confidence;
            track.IntentOwnshipScore = entry.OwnshipScore;
            track.IntentFriendlyMissileScore = entry.FriendlyMissileScore;
            track.SuspectedTargetTrackId = entry.SuspectedTargetTrackId;
            track.MissionRole = entry.Intent switch
            {
                MissileTargetIntent.Ownship =>
                    MissileMissionRole.AirToAir,
                MissileTargetIntent.FriendlyMissile =>
                    MissileMissionRole.Interceptor,
                _ => MissileMissionRole.Unknown,
            };
            track.MissionRoleConfidence = entry.Intent ==
                MissileTargetIntent.Unknown
                    ? 0f
                    : entry.Confidence;
            track.MissionRoleEvidence = entry.Intent ==
                MissileTargetIntent.Unknown
                    ? MissileMissionRoleEvidence.None
                    : MissileMissionRoleEvidence.KinematicIntent;
            if (sensorMissionRole !=
                    MissileMissionRole.Unknown &&
                sensorMissionRoleEvidence ==
                    MissileMissionRoleEvidence.EsmWaveform)
            {
                track.MissionRole = sensorMissionRole;
                track.MissionRoleConfidence =
                    sensorMissionRoleConfidence;
                track.MissionRoleEvidence =
                    sensorMissionRoleEvidence;
            }
        }
    }
}
