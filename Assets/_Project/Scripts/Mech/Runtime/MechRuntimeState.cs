using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public sealed class MechRuntimeState : MonoBehaviour
    {
        [SerializeField] private MechLoadoutDefinition currentLoadout;
        [SerializeField] private bool loadoutValid;
        [SerializeField] private MechAggregatedStats aggregatedStats = new MechAggregatedStats();
        [SerializeField] private List<MechValidationIssue> validationIssues = new List<MechValidationIssue>();
        [SerializeField] private List<MechAssemblyRecord> assembledParts = new List<MechAssemblyRecord>();
        [SerializeField] private List<MechMountPoint> mountPoints = new List<MechMountPoint>();

        public MechLoadoutDefinition CurrentLoadout => currentLoadout;
        public bool LoadoutValid => loadoutValid;
        public MechAggregatedStats AggregatedStats => aggregatedStats;
        public IReadOnlyList<MechValidationIssue> ValidationIssues => validationIssues;
        public IReadOnlyList<MechAssemblyRecord> AssembledParts => assembledParts;
        public IReadOnlyList<MechMountPoint> MountPoints => mountPoints;

        public void Apply(
            MechLoadoutDefinition loadout,
            MechAggregatedStats stats,
            MechValidationResult validation,
            List<MechAssemblyRecord> records,
            List<MechMountPoint> mounts)
        {
            currentLoadout = loadout;
            loadoutValid = validation != null && validation.IsValid;

            if (aggregatedStats == null)
            {
                aggregatedStats = new MechAggregatedStats();
            }
            CopyStats(stats, aggregatedStats);

            validationIssues.Clear();
            if (validation != null)
            {
                for (var i = 0; i < validation.Issues.Count; i++)
                {
                    validationIssues.Add(validation.Issues[i]);
                }
            }

            assembledParts.Clear();
            if (records != null)
            {
                assembledParts.AddRange(records);
            }

            mountPoints.Clear();
            if (mounts != null)
            {
                mountPoints.AddRange(mounts);
            }
        }

        private static void CopyStats(MechAggregatedStats source, MechAggregatedStats target)
        {
            target.Clear();
            if (source == null)
            {
                return;
            }

            target.totalMassKg = source.totalMassKg;
            target.armorPoints = source.armorPoints;
            target.structurePoints = source.structurePoints;
            target.stability = source.stability;
            target.powerOutputMw = source.powerOutputMw;
            target.idlePowerDrawMw = source.idlePowerDrawMw;
            target.peakPowerDrawMw = source.peakPowerDrawMw;
            target.heatGenerationMw = source.heatGenerationMw;
            target.heatDissipationMw = source.heatDissipationMw;
            target.heatCapacityMj = source.heatCapacityMj;
            target.mainThrustKn = source.mainThrustKn;
            target.lateralThrustKn = source.lateralThrustKn;
            target.reverseThrustKn = source.reverseThrustKn;
            target.upwardThrustKn = source.upwardThrustKn;
            target.downwardThrustKn = source.downwardThrustKn;
            target.attitudeTorqueKnM = source.attitudeTorqueKnM;
            target.rcsThrustKn = source.rcsThrustKn;
            target.supportedMassKg = source.supportedMassKg;
            target.radarRangeKm = source.radarRangeKm;
            target.radarAperture = source.radarAperture;
            target.radarRefreshHz = source.radarRefreshHz;
            target.sensorPrecision = source.sensorPrecision;
            target.trackCapacity = source.trackCapacity;
            target.fireControlQuality = source.fireControlQuality;
            target.guidanceChannels = source.guidanceChannels;
            target.radarCrossSection = source.radarCrossSection;
            target.thermalSignature = source.thermalSignature;
            target.electromagneticSignature = source.electromagneticSignature;
            target.weaponRecoilControl = source.weaponRecoilControl;
            target.hardpointCapacityKg = source.hardpointCapacityKg;
            target.energyStorageCapacityMj = source.energyStorageCapacityMj;
            target.energyRechargePowerKw = source.energyRechargePowerKw;
            target.energyRechargeDelayS = source.energyRechargeDelayS;
            target.depletedEnergyRestartFraction = source.depletedEnergyRestartFraction;
            target.radarPerformance.CopyFrom(source.radarPerformance);
            target.fireControlPerformance.CopyFrom(source.fireControlPerformance);
            target.idlePowerSurplusMw = source.idlePowerSurplusMw;
            target.peakPowerSurplusMw = source.peakPowerSurplusMw;
            target.heatSurplusMw = source.heatSurplusMw;
            target.mainAccelerationMps2 = source.mainAccelerationMps2;
            target.lateralAccelerationMps2 = source.lateralAccelerationMps2;
            target.reverseAccelerationMps2 = source.reverseAccelerationMps2;
            target.upwardAccelerationMps2 = source.upwardAccelerationMps2;
            target.downwardAccelerationMps2 = source.downwardAccelerationMps2;
            target.attitudeTorquePerTon = source.attitudeTorquePerTon;
            target.rcsAccelerationMps2 = source.rcsAccelerationMps2;
            target.rcsTurnRateDegreesPerSecond = source.rcsTurnRateDegreesPerSecond;
            target.supportedMassRatio = source.supportedMassRatio;
        }
    }
}
