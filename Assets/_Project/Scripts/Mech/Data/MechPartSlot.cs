using System;
using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public enum MechPartSlot
    {
        None = 0,
        Head = 10,
        Core = 20,
        Arms = 30,
        LeftArm = 31,
        RightArm = 32,
        Legs = 40,
        Booster = 50,
        Generator = 60,
        FireControlSystem = 70,
        Radar = 80,
        DataLinkTerminal = 90,
        LeftHandWeapon = 100,
        RightHandWeapon = 101,
        LeftBackWeapon = 110,
        RightBackWeapon = 111,
        Extension = 200
    }

    public enum MechPartCategory
    {
        Frame = 0,
        Internal = 10,
        Weapon = 20,
        Utility = 30
    }

    public enum MechHardpointType
    {
        None = 0,
        Frame = 10,
        Internal = 20,
        HandWeapon = 30,
        BackWeapon = 40,
        Sensor = 50,
        Utility = 60
    }

    public enum MechValidationSeverity
    {
        Info = 0,
        Warning = 10,
        Error = 20
    }

    public static class MechSlotRules
    {
        public static readonly MechPartSlot[] AssemblyOrder =
        {
            MechPartSlot.Core,
            MechPartSlot.Legs,
            MechPartSlot.Head,
            MechPartSlot.Arms,
            MechPartSlot.LeftArm,
            MechPartSlot.RightArm,
            MechPartSlot.Booster,
            MechPartSlot.Generator,
            MechPartSlot.FireControlSystem,
            MechPartSlot.Radar,
            MechPartSlot.DataLinkTerminal,
            MechPartSlot.LeftHandWeapon,
            MechPartSlot.RightHandWeapon,
            MechPartSlot.LeftBackWeapon,
            MechPartSlot.RightBackWeapon,
            MechPartSlot.Extension
        };

        public static readonly MechPartSlot[] DefaultRequiredSlots =
        {
            MechPartSlot.Head,
            MechPartSlot.Core,
            MechPartSlot.Legs,
            MechPartSlot.Booster,
            MechPartSlot.Generator,
            MechPartSlot.FireControlSystem
        };

        public static bool IsBaseAssemblySlot(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.Head:
                case MechPartSlot.Core:
                case MechPartSlot.Arms:
                case MechPartSlot.LeftArm:
                case MechPartSlot.RightArm:
                case MechPartSlot.Legs:
                case MechPartSlot.Booster:
                case MechPartSlot.Generator:
                case MechPartSlot.FireControlSystem:
                case MechPartSlot.Radar:
                case MechPartSlot.DataLinkTerminal:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsWeaponSlot(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.LeftHandWeapon:
                case MechPartSlot.RightHandWeapon:
                case MechPartSlot.LeftBackWeapon:
                case MechPartSlot.RightBackWeapon:
                    return true;
                default:
                    return false;
            }
        }

        public static bool IsMirroredArmSlot(MechPartSlot slot)
        {
            return slot == MechPartSlot.LeftArm || slot == MechPartSlot.RightArm;
        }

        public static MechHardpointType RequiredHardpointType(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.LeftHandWeapon:
                case MechPartSlot.RightHandWeapon:
                    return MechHardpointType.HandWeapon;
                case MechPartSlot.LeftBackWeapon:
                case MechPartSlot.RightBackWeapon:
                    return MechHardpointType.BackWeapon;
                case MechPartSlot.Radar:
                    return MechHardpointType.Sensor;
                default:
                    return MechHardpointType.None;
            }
        }

        public static string DisplayName(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.FireControlSystem:
                    return "FCS";
                case MechPartSlot.DataLinkTerminal:
                    return "Data Link Terminal";
                case MechPartSlot.LeftHandWeapon:
                    return "Left Hand Weapon";
                case MechPartSlot.RightHandWeapon:
                    return "Right Hand Weapon";
                case MechPartSlot.LeftBackWeapon:
                    return "Left Back Weapon";
                case MechPartSlot.RightBackWeapon:
                    return "Right Back Weapon";
                default:
                    return slot.ToString();
            }
        }

        public static Vector3 DefaultLocalPosition(MechPartSlot slot)
        {
            switch (slot)
            {
                case MechPartSlot.Head:
                    return new Vector3(0f, 2.15f, 0f);
                case MechPartSlot.Core:
                    return new Vector3(0f, 1.2f, 0f);
                case MechPartSlot.Arms:
                    return new Vector3(0f, 1.25f, 0f);
                case MechPartSlot.LeftArm:
                    return new Vector3(-0.9f, 1.25f, 0f);
                case MechPartSlot.RightArm:
                    return new Vector3(0.9f, 1.25f, 0f);
                case MechPartSlot.Legs:
                    return new Vector3(0f, 0.25f, 0f);
                case MechPartSlot.Booster:
                    return new Vector3(0f, 1.15f, -0.85f);
                case MechPartSlot.Generator:
                    return new Vector3(0f, 1.1f, -0.25f);
                case MechPartSlot.FireControlSystem:
                    return new Vector3(0f, 1.45f, 0.25f);
                case MechPartSlot.Radar:
                    return new Vector3(0f, 2.35f, 0.2f);
                case MechPartSlot.DataLinkTerminal:
                    return new Vector3(0f, 1.45f, -0.15f);
                case MechPartSlot.LeftHandWeapon:
                    return new Vector3(-1.35f, 0.95f, 0.2f);
                case MechPartSlot.RightHandWeapon:
                    return new Vector3(1.35f, 0.95f, 0.2f);
                case MechPartSlot.LeftBackWeapon:
                    return new Vector3(-0.65f, 1.65f, -0.75f);
                case MechPartSlot.RightBackWeapon:
                    return new Vector3(0.65f, 1.65f, -0.75f);
                default:
                    return Vector3.zero;
            }
        }

        public static int GetAssemblySortIndex(MechPartSlot slot)
        {
            for (var i = 0; i < AssemblyOrder.Length; i++)
            {
                if (AssemblyOrder[i] == slot)
                {
                    return i;
                }
            }

            return int.MaxValue;
        }

        public static IReadOnlyList<MechPartSlot> GetDefaultRequiredSlots()
        {
            return DefaultRequiredSlots;
        }
    }
}
