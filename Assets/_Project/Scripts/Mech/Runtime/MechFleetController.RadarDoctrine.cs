using ACunicombat.Sensing.Contracts;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed partial class MechFleetController
    {
        private double nextRadarDoctrineEvaluation;
        private double radarPowerStateSince;
        private bool radarPowerStateInitialized;
        public MechAiRadarState CurrentRadarDoctrineState { get; private set; } = MechAiRadarState.Search;
        public string LastRadarDoctrineReason { get; private set; } = "NotEvaluated";

        private void ApplyRadarControlMode()
        {
            if (!Configured || radar == null) return;
            nextRadarDoctrineEvaluation = double.NegativeInfinity;
            if (State.RadarControl == FleetRadarControl.RadiationSilence)
            {
                radar.SetRadarPowerEnabled(false);
                CurrentRadarDoctrineState = MechAiRadarState.Silent;
                LastRadarDoctrineReason = "ManualRadiationSilence";
                return;
            }
            if (State.RadarControl == FleetRadarControl.Active)
            {
                radar.SetRadarPowerEnabled(true);
                radar.SetTrackOperationMode(true);
                CurrentRadarDoctrineState = MechAiRadarState.Search;
                LastRadarDoctrineReason = "ManualActive";
                return;
            }
            UpdateRadarDoctrine(true);
        }

        private void UpdateRadarDoctrine(bool force = false)
        {
            if (!Configured || radar == null || State.RadarControl != FleetRadarControl.Intelligent) return;
            var now = radar.SimTimeSeconds;
            var ai = GetComponent<MechAiTargetController>();
            var defensive = ai != null && ai.CurrentCombatState == MechAiTargetController.CombatState.Defensive;
            if (!force && !defensive && now < nextRadarDoctrineEvaluation) return;
            nextRadarDoctrineEvaluation = now + MechAiRadarDoctrine.EvaluationIntervalSeconds;

            if (!radarPowerStateInitialized || radarPowerStateInitialized && radar.RadarPowerEnabled != lastRadarDoctrinePower)
            {
                radarPowerStateSince = now;
                radarPowerStateInitialized = true;
            }
            lastRadarDoctrinePower = radar.RadarPowerEnabled;

            var trackCount = radar.CopyTracks(air);
            var hasReliableTrack = false;
            var hasSharedTrack = false;
            var hasMissionCoverage = false;
            var selected = default(TrackSnapshot);
            var selectedDistance = float.PositiveInfinity;
            var selectionPinned = false;
            for (var i = 0; i < trackCount; i++)
            {
                var candidate = air[i];
                if (!MechAiRadarDoctrine.IsReliableTrack(in candidate, now)) continue;
                hasReliableTrack = true;
                var shared = MechMissileLaunchSource.Fresh(
                    candidate.SourceEvidence.DataLink, now,
                    MechAiRadarDoctrine.ReliableTrackMaximumAgeSeconds);
                hasSharedTrack |= shared;
                if (HasSpatialMission && TaskGroup == FleetTaskGroup.Reconnaissance &&
                    MissionContains(State.Task.Order, radar.PredictTrackPosition(in candidate)))
                {
                    hasMissionCoverage = true;
                }
                var distance = Vector3.Distance(transform.position, radar.PredictTrackPosition(in candidate));
                var preferred = ai != null && ai.HasSelectedAirTrack &&
                                ai.SelectedAirTrackId.Equals(candidate.TrackId) ||
                                radar.SttTrackId.Equals(candidate.TrackId);
                if (preferred || !selectionPinned && (!selected.TrackId.IsValid || distance < selectedDistance))
                {
                    selected = candidate;
                    selectedDistance = distance;
                    selectionPinned |= preferred;
                }
            }

            var hasPassiveOrDataLinkSolution = false;
            var needsActiveFireControl = false;
            if (selected.TrackId.IsValid && weapons != null)
            {
                weapons.CopyEquipmentWeaponSnapshots(equipment);
                foreach (var item in equipment)
                {
                    if (!item.Enabled || !MechAiRulePlanner.IsOffensiveRole(item.Role) ||
                        !State.AllowsWeapon(item.InstanceId) ||
                        item.Role == MechWeaponRole.AirToAirMissile && !State.AllowsOffensiveMissile(item.InstanceId) ||
                        !AllowsMissionEquipment(item.SlotId, item.Role) ||
                        !weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(item.SlotId), out var status) ||
                        !status.CanAttemptFire || selectedDistance < status.MinimumRangeMeters ||
                        selectedDistance > status.MaximumRangeMeters)
                    {
                        continue;
                    }
                    if (item.Role != MechWeaponRole.AirToAirMissile)
                    {
                        needsActiveFireControl = true;
                        continue;
                    }
                    if (weapons.CanReleaseMissileAt(item.SlotId, selected.TrackId, out var mode, out _) &&
                        (mode == MechTargetTrackingMode.Passive || mode == MechTargetTrackingMode.DataLink))
                    {
                        hasPassiveOrDataLinkSolution = true;
                    }
                    else
                    {
                        needsActiveFireControl = true;
                    }
                }
            }

            var output = MechAiRadarDoctrine.Evaluate(new MechAiRadarDoctrineInput
            {
                Control = State.RadarControl,
                TaskGroup = TaskGroup,
                HasSpatialMission = HasSpatialMission,
                Defensive = defensive,
                HasReliableTrack = hasReliableTrack,
                HasSharedTrack = hasSharedTrack,
                HasMissionCoverage = hasMissionCoverage,
                HasPassiveOrDataLinkSolution = hasPassiveOrDataLinkSolution,
                NeedsActiveFireControl = needsActiveFireControl,
                SearchResponsible = IsLeader,
                CurrentPower = radar.RadarPowerEnabled,
                PowerStateElapsedSeconds = now - radarPowerStateSince,
            });
            if (radar.RadarPowerEnabled != output.PowerEnabled) radarPowerStateSince = now;
            radar.SetRadarPowerEnabled(output.PowerEnabled);
            radar.SetTrackOperationMode(output.TrackOperationMode);
            CurrentRadarDoctrineState = output.State;
            LastRadarDoctrineReason = output.Reason;
            lastRadarDoctrinePower = output.PowerEnabled;
            radarPowerStateInitialized = true;
        }

        private bool lastRadarDoctrinePower;
    }
}
