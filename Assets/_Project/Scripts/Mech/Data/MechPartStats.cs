using System;
using UnityEngine;

namespace ACunicombat.Mechs
{
    [Serializable]
    public sealed class MechPartStats
    {
        [Header("Structure")]
        [Min(0f)] public float massKg;
        [Min(0f)] public float armorPoints;
        [Min(0f)] public float structurePoints;
        [Min(0f)] public float stability;

        [Header("Power and Heat")]
        public float powerOutputMw;
        public float idlePowerDrawMw;
        public float peakPowerDrawMw;
        public float heatGenerationMw;
        public float heatDissipationMw;
        public float heatCapacityMj;

        [Header("Flight")]
        public float mainThrustKn;
        public float lateralThrustKn;
        public float reverseThrustKn;
        public float attitudeTorqueKnM;
        public float supportedMassKg;

        [Header("Sensors and Fire Control")]
        public float radarRangeKm;
        public float radarAperture;
        public float radarRefreshHz;
        public float sensorPrecision;
        public int trackCapacity;
        public float fireControlQuality;
        public int guidanceChannels;

        [Header("Signatures")]
        public float radarCrossSection;
        public float thermalSignature;
        public float electromagneticSignature;

        [Header("Weapons")]
        public float weaponRecoilControl;
        public float meleeControl;
        public float hardpointCapacityKg;

        public void Add(MechPartStats other)
        {
            if (other == null)
            {
                return;
            }

            massKg += other.massKg;
            armorPoints += other.armorPoints;
            structurePoints += other.structurePoints;
            stability += other.stability;
            powerOutputMw += other.powerOutputMw;
            idlePowerDrawMw += other.idlePowerDrawMw;
            peakPowerDrawMw += other.peakPowerDrawMw;
            heatGenerationMw += other.heatGenerationMw;
            heatDissipationMw += other.heatDissipationMw;
            heatCapacityMj += other.heatCapacityMj;
            mainThrustKn += other.mainThrustKn;
            lateralThrustKn += other.lateralThrustKn;
            reverseThrustKn += other.reverseThrustKn;
            attitudeTorqueKnM += other.attitudeTorqueKnM;
            supportedMassKg += other.supportedMassKg;
            radarRangeKm += other.radarRangeKm;
            radarAperture += other.radarAperture;
            radarRefreshHz += other.radarRefreshHz;
            sensorPrecision += other.sensorPrecision;
            trackCapacity += other.trackCapacity;
            fireControlQuality += other.fireControlQuality;
            guidanceChannels += other.guidanceChannels;
            radarCrossSection += other.radarCrossSection;
            thermalSignature += other.thermalSignature;
            electromagneticSignature += other.electromagneticSignature;
            weaponRecoilControl += other.weaponRecoilControl;
            meleeControl += other.meleeControl;
            hardpointCapacityKg += other.hardpointCapacityKg;
        }

        public void CopyFrom(MechPartStats other)
        {
            if (other == null)
            {
                return;
            }

            massKg = other.massKg;
            armorPoints = other.armorPoints;
            structurePoints = other.structurePoints;
            stability = other.stability;
            powerOutputMw = other.powerOutputMw;
            idlePowerDrawMw = other.idlePowerDrawMw;
            peakPowerDrawMw = other.peakPowerDrawMw;
            heatGenerationMw = other.heatGenerationMw;
            heatDissipationMw = other.heatDissipationMw;
            heatCapacityMj = other.heatCapacityMj;
            mainThrustKn = other.mainThrustKn;
            lateralThrustKn = other.lateralThrustKn;
            reverseThrustKn = other.reverseThrustKn;
            attitudeTorqueKnM = other.attitudeTorqueKnM;
            supportedMassKg = other.supportedMassKg;
            radarRangeKm = other.radarRangeKm;
            radarAperture = other.radarAperture;
            radarRefreshHz = other.radarRefreshHz;
            sensorPrecision = other.sensorPrecision;
            trackCapacity = other.trackCapacity;
            fireControlQuality = other.fireControlQuality;
            guidanceChannels = other.guidanceChannels;
            radarCrossSection = other.radarCrossSection;
            thermalSignature = other.thermalSignature;
            electromagneticSignature = other.electromagneticSignature;
            weaponRecoilControl = other.weaponRecoilControl;
            meleeControl = other.meleeControl;
            hardpointCapacityKg = other.hardpointCapacityKg;
        }
    }

    [Serializable]
    public sealed class MechAggregatedStats
    {
        [Header("Raw Totals")]
        public float totalMassKg;
        public float armorPoints;
        public float structurePoints;
        public float stability;
        public float powerOutputMw;
        public float idlePowerDrawMw;
        public float peakPowerDrawMw;
        public float heatGenerationMw;
        public float heatDissipationMw;
        public float heatCapacityMj;
        public float mainThrustKn;
        public float lateralThrustKn;
        public float reverseThrustKn;
        public float attitudeTorqueKnM;
        public float supportedMassKg;
        public float radarRangeKm;
        public float radarAperture;
        public float radarRefreshHz;
        public float sensorPrecision;
        public int trackCapacity;
        public float fireControlQuality;
        public int guidanceChannels;
        public float radarCrossSection;
        public float thermalSignature;
        public float electromagneticSignature;
        public float weaponRecoilControl;
        public float meleeControl;
        public float hardpointCapacityKg;

        [Header("Derived")]
        public float idlePowerSurplusMw;
        public float peakPowerSurplusMw;
        public float heatSurplusMw;
        public float mainAccelerationMps2;
        public float lateralAccelerationMps2;
        public float reverseAccelerationMps2;
        public float attitudeTorquePerTon;
        public float supportedMassRatio;

        public void Clear()
        {
            totalMassKg = 0f;
            armorPoints = 0f;
            structurePoints = 0f;
            stability = 0f;
            powerOutputMw = 0f;
            idlePowerDrawMw = 0f;
            peakPowerDrawMw = 0f;
            heatGenerationMw = 0f;
            heatDissipationMw = 0f;
            heatCapacityMj = 0f;
            mainThrustKn = 0f;
            lateralThrustKn = 0f;
            reverseThrustKn = 0f;
            attitudeTorqueKnM = 0f;
            supportedMassKg = 0f;
            radarRangeKm = 0f;
            radarAperture = 0f;
            radarRefreshHz = 0f;
            sensorPrecision = 0f;
            trackCapacity = 0;
            fireControlQuality = 0f;
            guidanceChannels = 0;
            radarCrossSection = 0f;
            thermalSignature = 0f;
            electromagneticSignature = 0f;
            weaponRecoilControl = 0f;
            meleeControl = 0f;
            hardpointCapacityKg = 0f;
            idlePowerSurplusMw = 0f;
            peakPowerSurplusMw = 0f;
            heatSurplusMw = 0f;
            mainAccelerationMps2 = 0f;
            lateralAccelerationMps2 = 0f;
            reverseAccelerationMps2 = 0f;
            attitudeTorquePerTon = 0f;
            supportedMassRatio = 0f;
        }

        public void Add(MechPartStats stats)
        {
            if (stats == null)
            {
                return;
            }

            totalMassKg += stats.massKg;
            armorPoints += stats.armorPoints;
            structurePoints += stats.structurePoints;
            stability += stats.stability;
            powerOutputMw += stats.powerOutputMw;
            idlePowerDrawMw += stats.idlePowerDrawMw;
            peakPowerDrawMw += stats.peakPowerDrawMw;
            heatGenerationMw += stats.heatGenerationMw;
            heatDissipationMw += stats.heatDissipationMw;
            heatCapacityMj += stats.heatCapacityMj;
            mainThrustKn += stats.mainThrustKn;
            lateralThrustKn += stats.lateralThrustKn;
            reverseThrustKn += stats.reverseThrustKn;
            attitudeTorqueKnM += stats.attitudeTorqueKnM;
            supportedMassKg += stats.supportedMassKg;
            radarRangeKm += stats.radarRangeKm;
            radarAperture += stats.radarAperture;
            radarRefreshHz += stats.radarRefreshHz;
            sensorPrecision += stats.sensorPrecision;
            trackCapacity += stats.trackCapacity;
            fireControlQuality += stats.fireControlQuality;
            guidanceChannels += stats.guidanceChannels;
            radarCrossSection += stats.radarCrossSection;
            thermalSignature += stats.thermalSignature;
            electromagneticSignature += stats.electromagneticSignature;
            weaponRecoilControl += stats.weaponRecoilControl;
            meleeControl += stats.meleeControl;
            hardpointCapacityKg += stats.hardpointCapacityKg;
        }

        public void RecalculateDerived()
        {
            idlePowerSurplusMw = powerOutputMw - idlePowerDrawMw;
            peakPowerSurplusMw = powerOutputMw - peakPowerDrawMw;
            heatSurplusMw = heatDissipationMw - heatGenerationMw;

            if (totalMassKg > 0f)
            {
                mainAccelerationMps2 = mainThrustKn * 1000f / totalMassKg;
                lateralAccelerationMps2 = lateralThrustKn * 1000f / totalMassKg;
                reverseAccelerationMps2 = reverseThrustKn * 1000f / totalMassKg;
                attitudeTorquePerTon = attitudeTorqueKnM / Mathf.Max(0.001f, totalMassKg / 1000f);
                supportedMassRatio = supportedMassKg > 0f ? totalMassKg / supportedMassKg : 0f;
            }
            else
            {
                mainAccelerationMps2 = 0f;
                lateralAccelerationMps2 = 0f;
                reverseAccelerationMps2 = 0f;
                attitudeTorquePerTon = 0f;
                supportedMassRatio = 0f;
            }
        }
    }
}
