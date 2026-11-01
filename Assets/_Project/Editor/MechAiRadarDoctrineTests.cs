using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;

namespace ACunicombat.Editor.Tests
{
    public sealed class MechAiRadarDoctrineTests
    {
        [Test]
        public void ManualModesOverrideTacticalAutomation()
        {
            var silent = Input();
            silent.Control = FleetRadarControl.RadiationSilence;
            silent.Defensive = true;
            var active = Input();
            active.Control = FleetRadarControl.Active;
            active.HasReliableTrack = true;

            var silentOutput = MechAiRadarDoctrine.Evaluate(in silent);
            var activeOutput = MechAiRadarDoctrine.Evaluate(in active);
            Assert.That(silentOutput.PowerEnabled, Is.False);
            Assert.That(silentOutput.TrackOperationMode, Is.False);
            Assert.That(activeOutput.PowerEnabled, Is.True);
            Assert.That(activeOutput.TrackOperationMode, Is.True);
            Assert.That(activeOutput.State, Is.EqualTo(MechAiRadarState.TrackWhileScan));
        }

        [Test]
        public void CoverageGapUsesSearchWithoutForcingStt()
        {
            var input = Input();
            var output = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(output.PowerEnabled, Is.True);
            Assert.That(output.TrackOperationMode, Is.False);
            Assert.That(output.State, Is.EqualTo(MechAiRadarState.Search));
        }

        [Test]
        public void PassiveOrDataLinkSolutionDoesNotExposeStt()
        {
            var input = Input();
            input.HasReliableTrack = true;
            input.HasPassiveOrDataLinkSolution = true;
            input.NeedsActiveFireControl = false;
            var output = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(output.PowerEnabled, Is.False);
            Assert.That(output.TrackOperationMode, Is.False);
            Assert.That(output.Reason, Is.EqualTo("PassiveOrDataLinkEngagement"));
        }

        [Test]
        public void WingUsesSharedCoverageWhileLeaderMaintainsTws()
        {
            var wing = Input();
            wing.SearchResponsible = false;
            wing.HasReliableTrack = true;
            wing.HasSharedTrack = true;
            var leader = wing;
            leader.SearchResponsible = true;

            var wingOutput = MechAiRadarDoctrine.Evaluate(in wing);
            var leaderOutput = MechAiRadarDoctrine.Evaluate(in leader);
            Assert.That(wingOutput.PowerEnabled, Is.False);
            Assert.That(leaderOutput.PowerEnabled, Is.True);
            Assert.That(leaderOutput.State, Is.EqualTo(MechAiRadarState.TrackWhileScan));
            Assert.That(leaderOutput.TrackOperationMode, Is.False);
        }

        [Test]
        public void ActiveFireControlPreemptsEmissionDiscipline()
        {
            var input = Input();
            input.HasReliableTrack = true;
            input.HasPassiveOrDataLinkSolution = true;
            input.NeedsActiveFireControl = true;
            var output = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(output.PowerEnabled, Is.True);
            Assert.That(output.TrackOperationMode, Is.True);
            Assert.That(output.State, Is.EqualTo(MechAiRadarState.SingleTargetTrack));
        }

        [Test]
        public void SuppressionSearchesWithoutSttAndAttacksPassively()
        {
            var input = Input();
            input.HasSpatialMission = true;
            input.TaskGroup = FleetTaskGroup.AirDefenseSuppression;
            input.NeedsActiveFireControl = true;
            var search = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(search.PowerEnabled, Is.True);
            Assert.That(search.TrackOperationMode, Is.False);

            input.HasReliableTrack = true;
            input.HasPassiveOrDataLinkSolution = true;
            var attack = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(attack.PowerEnabled, Is.False);
            Assert.That(attack.TrackOperationMode, Is.False);
            Assert.That(attack.Reason, Is.EqualTo("PassiveSuppression"));
        }

        [Test]
        public void ReconnaissanceStopsEmittingAfterCoverageIsAvailable()
        {
            var input = Input();
            input.TaskGroup = FleetTaskGroup.Reconnaissance;
            var search = MechAiRadarDoctrine.Evaluate(in input);
            input.HasMissionCoverage = true;
            var covered = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(search.PowerEnabled, Is.True);
            Assert.That(covered.PowerEnabled, Is.False);
            Assert.That(covered.TrackOperationMode, Is.False);
        }

        [Test]
        public void DefensiveThreatBypassesPowerDwell()
        {
            var input = Input();
            input.CurrentPower = false;
            input.PowerStateElapsedSeconds = 0.1;
            input.Defensive = true;
            var output = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(output.PowerEnabled, Is.True);
            Assert.That(output.TrackOperationMode, Is.True);
            Assert.That(output.State, Is.EqualTo(MechAiRadarState.Defensive));
        }

        [Test]
        public void RoutinePowerChangesHaveDwellButSttDoesNotStayOn()
        {
            var input = Input();
            input.CurrentPower = true;
            input.PowerStateElapsedSeconds = 0.5;
            input.HasReliableTrack = true;
            input.HasPassiveOrDataLinkSolution = true;
            var output = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(output.PowerEnabled, Is.True);
            Assert.That(output.TrackOperationMode, Is.False);
            Assert.That(output.Reason, Is.EqualTo("PowerDwellHold"));

            input.PowerStateElapsedSeconds = MechAiRadarDoctrine.MinimumPowerDwellSeconds;
            output = MechAiRadarDoctrine.Evaluate(in input);
            Assert.That(output.PowerEnabled, Is.False);
        }

        [Test]
        public void TrackQualityRejectsStaleLowConfidenceAndLargeUncertainty()
        {
            const double now = 20;
            var track = Track(now);
            Assert.That(MechAiRadarDoctrine.IsReliableTrack(in track, now), Is.True);
            track.LastUpdateTime = now - MechAiRadarDoctrine.ReliableTrackMaximumAgeSeconds - 0.1;
            Assert.That(MechAiRadarDoctrine.IsReliableTrack(in track, now), Is.False);
            track = Track(now);
            track.Confidence = 0.2f;
            Assert.That(MechAiRadarDoctrine.IsReliableTrack(in track, now), Is.False);
            track = Track(now);
            track.SetCovariance(0, 0, 1000000);
            Assert.That(MechAiRadarDoctrine.IsReliableTrack(in track, now), Is.False);
        }

        [Test]
        public void CurrentTargetIsStickyUntilAlternativeIsSignificantlyBetter()
        {
            Assert.That(MechAiTargetSelection.ShouldSwitch(
                true, true, 2f, true, true, 1.5f, 10f, 0f), Is.False);
            Assert.That(MechAiTargetSelection.ShouldSwitch(
                true, true, 2f, true, true, 1f, 10f, 0f), Is.True);
            Assert.That(MechAiTargetSelection.ShouldSwitch(
                true, true, 2f, true, true, 1f, 10f, 11f), Is.False);
        }

        [Test]
        public void EngageableAlternativeImmediatelyReplacesUnusableCurrentChoice()
        {
            Assert.That(MechAiTargetSelection.ShouldSwitch(
                true, false, 3f, true, true, 3.2f, 1f, 100f), Is.True);
            Assert.That(MechAiTargetSelection.ShouldSwitch(
                true, true, 3f, true, false, 0.1f, 10f, 0f), Is.False);
            Assert.That(MechAiTargetSelection.ShouldSwitch(
                true, true, 3f, false, false, 0f, 10f, 0f), Is.False);
        }

        [Test]
        public void TargetScoreIncludesEngagementAndObservationQuality()
        {
            const double now = 10;
            var track = Track(now);
            var ready = MechAiTargetSelection.Score(in track, 10000, true, now);
            var unavailable = MechAiTargetSelection.Score(in track, 10000, false, now);
            Assert.That(unavailable - ready, Is.EqualTo(4f).Within(0.001f));
            track.Confidence = 0.5f;
            track.InterferenceScore = 0.5f;
            Assert.That(MechAiTargetSelection.Score(in track, 10000, true, now), Is.GreaterThan(ready));
        }

        private static MechAiRadarDoctrineInput Input()
        {
            return new MechAiRadarDoctrineInput
            {
                Control = FleetRadarControl.Intelligent,
                SearchResponsible = true,
                PowerStateElapsedSeconds = 10,
            };
        }

        private static TrackSnapshot Track(double now)
        {
            return new TrackSnapshot
            {
                TrackId = new TrackId(new Handle(7, 1)),
                ObjectClass = TrackObjectClass.AirVehicle,
                Affiliation = TrackAffiliation.Hostile,
                Lifecycle = TrackLifecycle.Confirmed,
                Confidence = 0.9f,
                LastUpdateTime = now,
                LastPredictTime = now,
            };
        }
    }
}
