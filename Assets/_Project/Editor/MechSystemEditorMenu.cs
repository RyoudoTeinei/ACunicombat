using System.Collections.Generic;
using ACunicombat.Mechs;
using UnityEditor;
using UnityEngine;

public static class MechSystemEditorMenu
{
    private const string SampleRoot = "Assets/_Project/Data/Mechs/Samples";
    private const string SampleLoadoutPath = SampleRoot + "/Sample_Space_AC_Loadout.asset";

    [MenuItem("Tools/ACunicombat/Mech/Create Starter Sample Assets")]
    public static void CreateStarterSampleAssets()
    {
        EnsureFolder(SampleRoot);

        var head = CreatePart(
            "Sample_Head",
            "Sample HD-1010 Head",
            MechPartSlot.Head,
            MechPartCategory.Frame,
            new MechPartStats
            {
                massKg = 1800f,
                armorPoints = 250f,
                structurePoints = 180f,
                idlePowerDrawMw = 0.8f,
                peakPowerDrawMw = 1.2f,
                radarCrossSection = 0.4f,
                thermalSignature = 0.2f
            },
            null,
            "frame", "sensor-host");

        var core = CreatePart(
            "Sample_Core",
            "Sample BD-1010 Core",
            MechPartSlot.Core,
            MechPartCategory.Frame,
            new MechPartStats
            {
                massKg = 12500f,
                armorPoints = 1800f,
                structurePoints = 1500f,
                idlePowerDrawMw = 2f,
                peakPowerDrawMw = 4f,
                heatDissipationMw = 1.5f,
                heatCapacityMj = 600f,
                radarCrossSection = 2.2f,
                thermalSignature = 1.2f,
                hardpointCapacityKg = 16000f,
                rcsThrustKn = 360f
            },
            new[]
            {
                Mount("HeadMount", MechPartSlot.Head, MechHardpointType.Frame, new Vector3(0f, 0.95f, 0.05f), 5000f),
                Mount("ArmsMount", MechPartSlot.Arms, MechHardpointType.Frame, new Vector3(0f, 0.15f, 0f), 14000f),
                Mount("LegsMount", MechPartSlot.Legs, MechHardpointType.Frame, new Vector3(0f, -0.85f, 0f), 26000f),
                Mount("BoosterMount", MechPartSlot.Booster, MechHardpointType.Internal, new Vector3(0f, 0.05f, -0.75f), 9000f),
                Mount("GeneratorBay", MechPartSlot.Generator, MechHardpointType.Internal, new Vector3(0f, -0.1f, -0.25f), 8000f),
                Mount("FcsBay", MechPartSlot.FireControlSystem, MechHardpointType.Internal, new Vector3(0f, 0.25f, 0.25f), 2000f),
                Mount("RadarMast", MechPartSlot.Radar, MechHardpointType.Sensor, new Vector3(0f, 0.75f, 0.15f), 2500f),
                Mount("LeftBackMount", MechPartSlot.LeftBackWeapon, MechHardpointType.BackWeapon, new Vector3(-0.55f, 0.45f, -0.45f), 7000f),
                Mount("RightBackMount", MechPartSlot.RightBackWeapon, MechHardpointType.BackWeapon, new Vector3(0.55f, 0.45f, -0.45f), 7000f)
            },
            "frame", "core", "space-rated");

        var arms = CreatePart(
            "Sample_Arms",
            "Sample AM-1010 Arms",
            MechPartSlot.Arms,
            MechPartCategory.Frame,
            new MechPartStats
            {
                massKg = 7200f,
                armorPoints = 900f,
                structurePoints = 700f,
                idlePowerDrawMw = 0.55f,
                peakPowerDrawMw = 2.6f,
                idleHeatMw = 0.14f,
                activeHeatMw = 0.95f,
                heatGenerationMw = 0.95f,
                heatCapacityMj = 80f,
                weaponRecoilControl = 60f,
                radarCrossSection = 1.1f,
                thermalSignature = 0.5f
            },
            new[]
            {
                Mount("LeftHandMount", MechPartSlot.LeftHandWeapon, MechHardpointType.HandWeapon, new Vector3(-0.85f, -0.25f, 0.2f), 4500f),
                Mount("RightHandMount", MechPartSlot.RightHandWeapon, MechHardpointType.HandWeapon, new Vector3(0.85f, -0.25f, 0.2f), 4500f)
            },
            "frame", "arms");

        var legs = CreatePart(
            "Sample_Legs",
            "Sample LG-1010 Space Legs",
            MechPartSlot.Legs,
            MechPartCategory.Frame,
            new MechPartStats
            {
                massKg = 10500f,
                armorPoints = 1300f,
                structurePoints = 1100f,
                idlePowerDrawMw = 0.8f,
                peakPowerDrawMw = 1.6f,
                supportedMassKg = 70000f,
                rcsThrustKn = 240f,
                radarCrossSection = 1.8f,
                thermalSignature = 0.6f
            },
            null,
            "frame", "legs", "space-rated");

        var booster = CreatePart(
            "Sample_Booster",
            "Sample BST-1010 Vector Booster",
            MechPartSlot.Booster,
            MechPartCategory.Internal,
            new MechPartStats
            {
                massKg = 4200f,
                idlePowerDrawMw = 2.5f,
                peakPowerDrawMw = 24f,
                heatGenerationMw = 7f,
                mainThrustKn = 520f,
                lateralThrustKn = 180f,
                reverseThrustKn = 300f,
                attitudeTorqueKnM = 180f,
                thermalSignature = 2.8f,
                electromagneticSignature = 0.8f
            },
            null,
            "internal", "booster", "space-rated");

        var generator = CreatePart(
            "Sample_Generator",
            "Sample GEN-1010 Compact Reactor",
            MechPartSlot.Generator,
            MechPartCategory.Internal,
            new MechPartStats
            {
                massKg = 3800f,
                powerOutputMw = 62f,
                heatGenerationMw = 4f,
                heatCapacityMj = 300f,
                thermalSignature = 1.4f,
                electromagneticSignature = 2.5f
            },
            null,
            "internal", "generator");

        var radiator = CreatePart(
            "Sample_Radiator",
            "Sample RAD-1010 Vacuum Radiator",
            MechPartSlot.Radiator,
            MechPartCategory.Internal,
            new MechPartStats
            {
                massKg = 2800f,
                idlePowerDrawMw = 0.25f,
                peakPowerDrawMw = 1.2f,
                idleHeatMw = 0.06f,
                activeHeatMw = 0.22f,
                heatGenerationMw = 0.22f,
                heatDissipationMw = 18.4f,
                heatCapacityMj = 120f,
                thermalSignature = 3f
            },
            null,
            "internal", "radiator");

        var fcs = CreatePart(
            "Sample_FCS",
            "Sample FCS-1010 Track Computer",
            MechPartSlot.FireControlSystem,
            MechPartCategory.Internal,
            new MechPartStats
            {
                massKg = 900f,
                idlePowerDrawMw = 1.4f,
                peakPowerDrawMw = 3f,
                fireControlQuality = 45f,
                trackCapacity = 6,
                guidanceChannels = 2,
                electromagneticSignature = 0.4f
            },
            null,
            "internal", "fcs");

        var radar = CreatePart(
            "Sample_Radar",
            "Sample RDR-1010 Search Radar",
            MechPartSlot.Radar,
            MechPartCategory.Internal,
            new MechPartStats
            {
                massKg = 1200f,
                idlePowerDrawMw = 2.5f,
                peakPowerDrawMw = 6f,
                heatGenerationMw = 1.2f,
                radarRangeKm = 35f,
                radarAperture = 1.2f,
                radarRefreshHz = 2f,
                sensorPrecision = 35f,
                electromagneticSignature = 3.5f
            },
            null,
            "internal", "radar", "active-sensor");

        var leftRifle = CreatePart(
            "Sample_Left_Rifle",
            "Sample Left Kinetic Rifle",
            MechPartSlot.LeftHandWeapon,
            MechPartCategory.Weapon,
            new MechPartStats
            {
                massKg = 1800f,
                idlePowerDrawMw = 0.1f,
                peakPowerDrawMw = 1.5f,
                heatGenerationMw = 0.6f,
                radarCrossSection = 0.35f,
                thermalSignature = 0.3f
            },
            null,
            "weapon", "hand-weapon", "kinetic");

        var rightRifle = CreatePart(
            "Sample_Right_Rifle",
            "Sample Right Kinetic Rifle",
            MechPartSlot.RightHandWeapon,
            MechPartCategory.Weapon,
            new MechPartStats
            {
                massKg = 1800f,
                idlePowerDrawMw = 0.1f,
                peakPowerDrawMw = 1.5f,
                heatGenerationMw = 0.6f,
                radarCrossSection = 0.35f,
                thermalSignature = 0.3f
            },
            null,
            "weapon", "hand-weapon", "kinetic");

        var leftMissiles = CreatePart(
            "Sample_Left_Missiles",
            "Sample Left Missile Pod",
            MechPartSlot.LeftBackWeapon,
            MechPartCategory.Weapon,
            new MechPartStats
            {
                massKg = 3600f,
                idlePowerDrawMw = 0.2f,
                peakPowerDrawMw = 2.5f,
                heatGenerationMw = 0.8f,
                radarCrossSection = 0.6f,
                thermalSignature = 0.4f
            },
            null,
            "weapon", "back-weapon", "missile");

        var rightMissiles = CreatePart(
            "Sample_Right_Missiles",
            "Sample Right Missile Pod",
            MechPartSlot.RightBackWeapon,
            MechPartCategory.Weapon,
            new MechPartStats
            {
                massKg = 3600f,
                idlePowerDrawMw = 0.2f,
                peakPowerDrawMw = 2.5f,
                heatGenerationMw = 0.8f,
                radarCrossSection = 0.6f,
                thermalSignature = 0.4f
            },
            null,
            "weapon", "back-weapon", "missile");

        var loadout = CreateOrLoadAsset<MechLoadoutDefinition>(SampleLoadoutPath);
        loadout.EditorConfigure(
            "sample_space_ac",
            "Sample Space AC",
            new[]
            {
                Assignment(MechPartSlot.Head, head),
                Assignment(MechPartSlot.Core, core),
                Assignment(MechPartSlot.Arms, arms),
                Assignment(MechPartSlot.Legs, legs),
                Assignment(MechPartSlot.Booster, booster),
                Assignment(MechPartSlot.Generator, generator),
                Assignment(MechPartSlot.Radiator, radiator),
                Assignment(MechPartSlot.FireControlSystem, fcs),
                Assignment(MechPartSlot.Radar, radar),
                Assignment(MechPartSlot.LeftHandWeapon, leftRifle),
                Assignment(MechPartSlot.RightHandWeapon, rightRifle),
                Assignment(MechPartSlot.LeftBackWeapon, leftMissiles),
                Assignment(MechPartSlot.RightBackWeapon, rightMissiles)
            });

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Selection.activeObject = loadout;
        EditorGUIUtility.PingObject(loadout);
    }

    [MenuItem("Tools/ACunicombat/Mech/Create Sample Mech Rig")]
    public static void CreateSampleMechRig()
    {
        CreateStarterSampleAssets();

        var loadout = AssetDatabase.LoadAssetAtPath<MechLoadoutDefinition>(SampleLoadoutPath);
        var rig = new GameObject("Sample_PlayerMech_Rig");
        Undo.RegisterCreatedObjectUndo(rig, "Create sample mech rig");

        var body = rig.AddComponent<Rigidbody>();
        body.useGravity = false;
        body.linearDamping = 0f;
        body.angularDamping = 0f;

        var assembler = rig.AddComponent<MechAssembler>();
        assembler.Loadout = loadout;
        rig.AddComponent<MechRuntimeState>();
        EditorUtility.SetDirty(assembler);

        assembler.Assemble();
        Selection.activeGameObject = rig;
        EditorGUIUtility.PingObject(rig);
    }

    [MenuItem("Tools/ACunicombat/Mech/Assemble Selected Mech")]
    public static void AssembleSelectedMech()
    {
        if (Selection.activeGameObject == null)
        {
            EditorUtility.DisplayDialog("ACunicombat", "Select a GameObject with a MechAssembler first.", "OK");
            return;
        }

        var assembler = Selection.activeGameObject.GetComponent<MechAssembler>();
        if (assembler == null)
        {
            EditorUtility.DisplayDialog("ACunicombat", "The selected GameObject does not have a MechAssembler.", "OK");
            return;
        }

        assembler.Assemble();
        EditorUtility.SetDirty(assembler);
    }

    private static MechPartDefinition CreatePart(
        string id,
        string displayName,
        MechPartSlot slot,
        MechPartCategory category,
        MechPartStats stats,
        IEnumerable<MechMountPointDefinition> mounts,
        params string[] tags)
    {
        var path = SampleRoot + "/" + id + ".asset";
        var part = CreateOrLoadAsset<MechPartDefinition>(path);
        part.EditorConfigure(id, displayName, slot, category, null, stats, mounts, tags,
			newArmPerformance: slot == MechPartSlot.Arms ? CreateSampleArmPerformance() : null);
        return part;
    }

	private static MechArmPerformance CreateSampleArmPerformance()
	{
		return new MechArmPerformance
		{
			weaponLoadLimitKg = 8000f,
			recoilImpulseLimitNs = 22000f,
			recoilRecoveryRateNsPerSecond = 85000f,
			aimTraverseRateDegPerSecond = 105f,
			aimAngularAccelerationDegPerSecondSquared = 420f,
			weaponPointingErrorMrad = 0.18f,
			thrusterNozzles = new[]
			{
				new MechArmThrusterNozzle { nozzleId = "arm-lateral", role = MechArmThrusterRole.Lateral, localPositionM = new Vector3(0.82f, 0.08f, 0f), localThrustDirection = Vector3.left, maximumThrustKn = 40f, gimbalLimitDeg = 8f, maximumPowerKw = 420f, maximumHeatKw = 180f },
				new MechArmThrusterNozzle { nozzleId = "arm-rcs-forward", role = MechArmThrusterRole.Rcs, localPositionM = new Vector3(0.78f, 0.32f, 0.28f), localThrustDirection = Vector3.forward, maximumThrustKn = 75f, maximumPowerKw = 260f, maximumHeatKw = 120f },
				new MechArmThrusterNozzle { nozzleId = "arm-rcs-rear", role = MechArmThrusterRole.Rcs, localPositionM = new Vector3(0.78f, -0.18f, -0.24f), localThrustDirection = Vector3.back, maximumThrustKn = 75f, maximumPowerKw = 260f, maximumHeatKw = 120f }
			}
		};
	}

    private static MechMountPointDefinition Mount(
        string id,
        MechPartSlot targetSlot,
        MechHardpointType hardpointType,
        Vector3 localPosition,
        float maxMassKg)
    {
        return new MechMountPointDefinition
        {
            mountId = id,
            targetSlot = targetSlot,
            hardpointType = hardpointType,
            localPosition = localPosition,
            localEulerAngles = Vector3.zero,
            localScale = Vector3.one,
            maxMassKg = maxMassKg
        };
    }

    private static MechPartAssignment Assignment(MechPartSlot slot, MechPartDefinition part)
    {
        return new MechPartAssignment
        {
            slot = slot,
            part = part,
            enabled = true
        };
    }

    private static T CreateOrLoadAsset<T>(string path) where T : ScriptableObject
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null)
        {
            return asset;
        }

        asset = ScriptableObject.CreateInstance<T>();
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    private static void EnsureFolder(string folderPath)
    {
        var parts = folderPath.Split('/');
        var current = parts[0];
        for (var i = 1; i < parts.Length; i++)
        {
            var next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }
}
