using System;

namespace ACunicombat.Mechs
{
    public enum MechInternalPartPerformanceKind
    {
        None = 0,
        FireControlSystem = 10,
        Generator = 20
    }

    [Serializable]
    public sealed class MechInternalPartPerformance
    {
        public MechInternalPartPerformanceKind kind;
        public string sourceParamId;
        public int sourceIconId;

        public float sourceEnergyLoad;
        public float missileLockCorrection;
        public float multiLockCorrection;
        public float closeRangeAssist;
        public float mediumRangeAssist;
        public float longRangeAssist;

        public float energyCapacity;
        public float energyOutput;
        public float energyRecharge;
        public float supplyRecovery;
        public float postRecoveryEnergySupply;
        public float energyWeaponSpecialization;
        public int boosterEmitterColorId;

        public bool HasData => kind != MechInternalPartPerformanceKind.None;

        public MechInternalPartPerformance Clone()
        {
            return new MechInternalPartPerformance
            {
                kind = kind,
                sourceParamId = sourceParamId,
                sourceIconId = sourceIconId,
                sourceEnergyLoad = sourceEnergyLoad,
                missileLockCorrection = missileLockCorrection,
                multiLockCorrection = multiLockCorrection,
                closeRangeAssist = closeRangeAssist,
                mediumRangeAssist = mediumRangeAssist,
                longRangeAssist = longRangeAssist,
                energyCapacity = energyCapacity,
                energyOutput = energyOutput,
                energyRecharge = energyRecharge,
                supplyRecovery = supplyRecovery,
                postRecoveryEnergySupply = postRecoveryEnergySupply,
                energyWeaponSpecialization = energyWeaponSpecialization,
                boosterEmitterColorId = boosterEmitterColorId
            };
        }
    }
}
