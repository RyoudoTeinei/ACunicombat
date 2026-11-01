using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechAiRadarState : byte
    {
        Silent = 0,
        Listening = 1,
        Search = 2,
        TrackWhileScan = 3,
        SingleTargetTrack = 4,
        Defensive = 5,
    }

    public struct MechAiRadarDoctrineInput
    {
        public FleetRadarControl Control;
        public FleetTaskGroup TaskGroup;
        public bool HasSpatialMission;
        public bool Defensive;
        public bool HasReliableTrack;
        public bool HasSharedTrack;
        public bool HasMissionCoverage;
        public bool HasPassiveOrDataLinkSolution;
        public bool NeedsActiveFireControl;
        public bool SearchResponsible;
        public bool CurrentPower;
        public double PowerStateElapsedSeconds;
    }

    public readonly struct MechAiRadarDoctrineOutput
    {
        public readonly bool PowerEnabled;
        public readonly bool TrackOperationMode;
        public readonly MechAiRadarState State;
        public readonly string Reason;

        public MechAiRadarDoctrineOutput(
            bool powerEnabled,
            bool trackOperationMode,
            MechAiRadarState state,
            string reason)
        {
            PowerEnabled = powerEnabled;
            TrackOperationMode = trackOperationMode && powerEnabled;
            State = state;
            Reason = reason;
        }
    }

    public static class MechAiRadarDoctrine
    {
        public const double EvaluationIntervalSeconds = 0.5;
        public const double MinimumPowerDwellSeconds = 2.0;
        public const double ReliableTrackMaximumAgeSeconds = 2.0;
        public const float ReliableTrackMinimumConfidence = 0.55f;
        public const float ReliableTrackMaximumSigmaMeters = 500f;

        public static bool IsReliableTrack(in TrackSnapshot track, double now)
        {
            if (!track.TrackId.IsValid ||
                track.ObjectClass != TrackObjectClass.AirVehicle ||
                track.Affiliation == TrackAffiliation.Friendly ||
                track.Identity.Available && !track.Identity.CanAttackPlatform ||
                track.Lifecycle != TrackLifecycle.Confirmed &&
                track.Lifecycle != TrackLifecycle.Coasting ||
                track.Confidence < ReliableTrackMinimumConfidence ||
                !double.IsFinite(track.LastUpdateTime) ||
                now < track.LastUpdateTime ||
                now - track.LastUpdateTime > ReliableTrackMaximumAgeSeconds)
            {
                return false;
            }

            var sigma = track.PredictPositionUncertainty(now);
            return double.IsFinite(sigma) && sigma >= 0 && sigma <= ReliableTrackMaximumSigmaMeters;
        }

        public static MechAiRadarDoctrineOutput Evaluate(in MechAiRadarDoctrineInput input)
        {
            if (input.Control == FleetRadarControl.RadiationSilence)
            {
                return new MechAiRadarDoctrineOutput(
                    false, false, MechAiRadarState.Silent, "ManualRadiationSilence");
            }
            if (input.Control == FleetRadarControl.Active)
            {
                return new MechAiRadarDoctrineOutput(
                    true, true,
                    input.Defensive
                        ? MechAiRadarState.Defensive
                        : input.NeedsActiveFireControl
                            ? MechAiRadarState.SingleTargetTrack
                            : input.HasReliableTrack
                                ? MechAiRadarState.TrackWhileScan
                                : MechAiRadarState.Search,
                    "ManualActive");
            }

            var desired = EvaluateIntelligent(in input);
            if (!input.Defensive &&
                input.CurrentPower != desired.PowerEnabled &&
                input.PowerStateElapsedSeconds < MinimumPowerDwellSeconds)
            {
                return new MechAiRadarDoctrineOutput(
                    input.CurrentPower,
                    desired.TrackOperationMode,
                    input.CurrentPower
                        ? desired.HasTrackForDisplay()
                            ? MechAiRadarState.TrackWhileScan
                            : MechAiRadarState.Search
                        : MechAiRadarState.Listening,
                    "PowerDwellHold");
            }
            return desired;
        }

        private static bool HasTrackForDisplay(this MechAiRadarDoctrineOutput output)
        {
            return output.State == MechAiRadarState.TrackWhileScan ||
                   output.State == MechAiRadarState.SingleTargetTrack ||
                   output.State == MechAiRadarState.Listening;
        }

        private static MechAiRadarDoctrineOutput EvaluateIntelligent(
            in MechAiRadarDoctrineInput input)
        {
            if (input.Defensive)
            {
                return new MechAiRadarDoctrineOutput(
                    true, true, MechAiRadarState.Defensive, "IncomingThreat");
            }

            var suppression = input.HasSpatialMission &&
                              input.TaskGroup == FleetTaskGroup.AirDefenseSuppression;
            if (suppression)
            {
                return input.HasReliableTrack && input.HasPassiveOrDataLinkSolution
                    ? new MechAiRadarDoctrineOutput(
                        false, false, MechAiRadarState.Listening, "PassiveSuppression")
                    : new MechAiRadarDoctrineOutput(
                        true, false, MechAiRadarState.Search, "SuppressionSearchWithoutStt");
            }

            if (input.TaskGroup == FleetTaskGroup.Reconnaissance)
            {
                return input.HasMissionCoverage || input.HasReliableTrack
                    ? new MechAiRadarDoctrineOutput(
                        false, false, MechAiRadarState.Listening, "ReconnaissanceCoverageAvailable")
                    : new MechAiRadarDoctrineOutput(
                        true, false, MechAiRadarState.Search, "ReconnaissanceCoverageGap");
            }

            if (input.HasReliableTrack && input.NeedsActiveFireControl)
            {
                return new MechAiRadarDoctrineOutput(
                    true, true, MechAiRadarState.SingleTargetTrack, "ActiveFireControlRequired");
            }
            if (!input.HasReliableTrack)
            {
                return new MechAiRadarDoctrineOutput(
                    true, false, MechAiRadarState.Search, "TrackCoverageGap");
            }
            if (input.HasPassiveOrDataLinkSolution)
            {
                return new MechAiRadarDoctrineOutput(
                    false, false, MechAiRadarState.Listening, "PassiveOrDataLinkEngagement");
            }
            if (input.HasSharedTrack && !input.SearchResponsible)
            {
                return new MechAiRadarDoctrineOutput(
                    false, false, MechAiRadarState.Listening, "SharedTrackCoverage");
            }
            if (input.SearchResponsible)
            {
                return new MechAiRadarDoctrineOutput(
                    true, false, MechAiRadarState.TrackWhileScan, "SearchResponsibleTws");
            }
            return new MechAiRadarDoctrineOutput(
                false, false, MechAiRadarState.Listening, "PreserveEmissionDiscipline");
        }
    }

    public static class MechAiTargetSelection
    {
        public const float SwitchScoreMargin = 0.75f;
        public const float MinimumSwitchIntervalSeconds = 2f;

        public static float Score(
            in TrackSnapshot track,
            float rangeMeters,
            bool canEngage,
            double now)
        {
            var classPenalty = track.ObjectClass == TrackObjectClass.AirVehicle ? 0f : 3f;
            var sigma = track.PredictPositionUncertainty(now);
            var uncertaintyPenalty = double.IsFinite(sigma)
                ? Mathf.Clamp((float)sigma / 2000f, 0f, 3f)
                : 3f;
            return classPenalty +
                   Mathf.Max(0f, rangeMeters) / 10000f +
                   (1f - Mathf.Clamp01(track.Confidence)) * 2f +
                   Mathf.Clamp01(track.InterferenceScore) +
                   uncertaintyPenalty +
                   (canEngage ? 0f : 4f);
        }

        public static bool ShouldSwitch(
            bool hasCurrent,
            bool currentCanEngage,
            float currentScore,
            bool hasAlternative,
            bool alternativeCanEngage,
            float alternativeScore,
            float now,
            float nextAllowedSwitchTime)
        {
            if (!hasAlternative) return false;
            if (!hasCurrent) return true;
            if (!currentCanEngage && alternativeCanEngage) return true;
            if (currentCanEngage && !alternativeCanEngage) return false;
            if (now < nextAllowedSwitchTime) return false;
            return alternativeScore + SwitchScoreMargin < currentScore;
        }
    }
}
