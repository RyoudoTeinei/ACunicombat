using UnityEngine;

namespace ACunicombat.Mechs
{
    public static class MechPartSignatureDefaults
    {
        public static bool HasConfiguredSignature(MechPartStats stats) =>
            stats != null &&
            (stats.radarCrossSection > 0f ||
             stats.thermalSignature > 0f ||
             stats.electromagneticSignature > 0f);

        public static void ApplyIfMissing(MechPartStats stats, MechPartSlot slot)
        {
            if (stats == null)
            {
                return;
            }

            Resolve(slot, out var radar, out var thermal, out var electromagnetic);
            if (stats.radarCrossSection <= 0f)
            {
                stats.radarCrossSection = radar;
            }
            if (stats.thermalSignature <= 0f)
            {
                stats.thermalSignature = thermal;
            }
            if (stats.electromagneticSignature <= 0f)
            {
                stats.electromagneticSignature = electromagnetic;
            }
        }

        public static void AddFallback(
            MechAggregatedStats target,
            MechPartStats stats,
            MechPartSlot slot)
        {
            if (target == null || stats == null)
            {
                return;
            }

            Resolve(slot, out var radar, out var thermal, out var electromagnetic);
            if (stats.radarCrossSection <= 0f)
            {
                target.radarCrossSection += radar;
            }
            if (stats.thermalSignature <= 0f)
            {
                target.thermalSignature += thermal;
            }
            if (stats.electromagneticSignature <= 0f)
            {
                target.electromagneticSignature += electromagnetic;
            }
        }

        private static void Resolve(
            MechPartSlot slot,
            out float radar,
            out float thermal,
            out float electromagnetic)
        {
            radar = 0.2f;
            thermal = 0.1f;
            electromagnetic = 0.1f;
            switch (slot)
            {
                case MechPartSlot.Head:
                    radar = 0.7f; thermal = 0.6f; electromagnetic = 0.4f; break;
                case MechPartSlot.Core:
                    radar = 4.5f; thermal = 2f; electromagnetic = 1f; break;
                case MechPartSlot.Arms:
                case MechPartSlot.LeftArm:
                case MechPartSlot.RightArm:
                    radar = 2.6f; thermal = 1f; electromagnetic = 0.5f; break;
                case MechPartSlot.Legs:
                    radar = 3.8f; thermal = 1.4f; electromagnetic = 0.6f; break;
                case MechPartSlot.Booster:
                    radar = 0.8f; thermal = 2f; electromagnetic = 0.5f; break;
                case MechPartSlot.Generator:
                    radar = 0.3f; thermal = 1f; electromagnetic = 1.5f; break;
                case MechPartSlot.Radar:
                    radar = 0.4f; thermal = 0.3f; electromagnetic = 2.5f; break;
                case MechPartSlot.FireControlSystem:
                    radar = 0.2f; thermal = 0.2f; electromagnetic = 0.8f; break;
                case MechPartSlot.LeftHandWeapon:
                case MechPartSlot.RightHandWeapon:
                case MechPartSlot.LeftBackWeapon:
                case MechPartSlot.RightBackWeapon:
                    radar = 0.9f; thermal = 0.4f; electromagnetic = 0.3f; break;
            }
        }
    }
}
