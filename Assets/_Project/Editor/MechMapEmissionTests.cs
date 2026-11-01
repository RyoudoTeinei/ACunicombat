using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

public sealed class MechMapEmissionTests
{
    [Test]
    public void RadarUsesBlueForFriendsAndRedForEnemiesWhileJammingIsYellow()
    {
        var friendly = MechBattlefieldMap.EmissionColor(EmissionType.RadarDwell, false);
        var hostile = MechBattlefieldMap.EmissionColor(EmissionType.RadarDwell, true);
        var jamming = MechBattlefieldMap.EmissionColor(EmissionType.Jamming, true);
        Assert.Greater(friendly.b, friendly.r);
        Assert.Greater(hostile.r, hostile.b);
        Assert.Greater(jamming.r, jamming.b);
        Assert.Greater(jamming.g, jamming.b);
    }

    [Test]
    public void FlashIsBoundedAndChangesOverTime()
    {
        float a = MechBattlefieldMap.EmissionFlashAlpha(0);
        float b = MechBattlefieldMap.EmissionFlashAlpha(.25);
        Assert.That(a, Is.InRange(.35f, .95f));
        Assert.That(b, Is.InRange(.35f, .95f));
        Assert.AreNotEqual(a, b);
    }

    [Test]
    public void InvalidGeometryNeverProducesASector()
    {
        Assert.IsTrue(MechBattlefieldMap.HasDisplayableEmissionGeometry(new float3(0, 0, 1), .2f, 50000));
        Assert.IsFalse(MechBattlefieldMap.HasDisplayableEmissionGeometry(float3.zero, .2f, 50000));
        Assert.IsFalse(MechBattlefieldMap.HasDisplayableEmissionGeometry(new float3(0, 0, 1), 0, 50000));
        Assert.IsFalse(MechBattlefieldMap.HasDisplayableEmissionGeometry(new float3(0, 0, 1), .2f, 0));
    }

    private static EmissionReport Report() => new EmissionReport
    {
        ObservedAt = 10,
        ExpiresAt = 10.75,
        Emission = new EmissionEvent
        {
            EmissionType = EmissionType.RadarDwell,
            Origin = new double3(100, 0, 0),
            Direction = new float3(0, 0, 1),
            BeamWidth = .2f,
            MaxRangeMeters = 50000,
        },
    };

    [Test]
    public void CooperativeReportsRequireFreshValidGeometry()
    {
        var report = Report();
        Assert.IsTrue(MechCooperativeTelemetry.IsValidEmissionReport(report, 10.5));
        Assert.IsFalse(MechCooperativeTelemetry.IsValidEmissionReport(report, 11));
        report.Emission.Direction = float3.zero;
        Assert.IsFalse(MechCooperativeTelemetry.IsValidEmissionReport(report, 10.5));
    }

    [Test]
    public void CooperativeRadarSelectionPrefersFocusedDwell()
    {
        var events = new[]
        {
            new EmissionEvent { EmissionType = EmissionType.RadarDwell, WaveformId = 3, StartTime = 9.8, EndTime = 9.81, Direction = new float3(0, 0, 1), BeamWidth = .2f, MaxRangeMeters = 50000 },
            new EmissionEvent { EmissionType = EmissionType.RadarDwell, WaveformId = 1, StartTime = 9.7, EndTime = 9.71, Direction = new float3(1, 0, 0), BeamWidth = .1f, MaxRangeMeters = 50000 },
        };
        Assert.IsTrue(MechCooperativeTelemetry.TryLatestEmission(events, events.Length, EmissionType.RadarDwell, 10, out var selected));
        Assert.AreEqual(1, selected.WaveformId);
    }

    [Test]
    public void EsmThreatRetainsObservedBeamGeometryWithoutAddingRange()
    {
        using var world = new SimWorld();
        using var receiver = new EsmReceiverSystem(world) { SessionSeed = 5 };
        var owner = world.Register(new KinematicState { PositionWorld = double3.zero, Rotation = quaternion.identity, Mass = 1 });
        var id = new SensorId(new Handle(0, 1));
        receiver.RegisterReceiver(new EsmReceiverProfile { ReceiverId = id, OwnerEntityId = owner, SensitivityFloorDb = -60, BearingSigmaRadians = .01f });
        world.SwapSnapshots(1);
        using var emissions = new NativeList<EmissionEvent>(1, Allocator.Temp);
        emissions.Add(new EmissionEvent
        {
            EmitterId = new EmitterId(new Handle(8, 1)),
            Origin = new double3(0, 0, 1000),
            Direction = new float3(0, 0, -1),
            BeamWidth = .2f,
            MaxRangeMeters = 80000,
            MainlobePowerDb = 100,
            SidelobeLevelDb = -25,
            FrequencyBand = 1,
            WaveformId = 3,
            EmissionType = EmissionType.RadarDwell,
        });
        receiver.BeginTick();
        receiver.Consume(emissions, 1);
        var correlator = new EsmThreatCorrelator();
        correlator.RegisterWaveformClass(1, 3, EsmWaveformClass.SearchRadar);
        correlator.Process(receiver.Contacts, id, 1);
        var threats = new ThreatSnapshot[4];
        Assert.AreEqual(1, correlator.CopyThreats(threats));
        Assert.That(math.distance(threats[0].EmissionDirection, new float3(0, 0, -1)), Is.LessThan(.001));
        Assert.AreEqual(.2f, threats[0].EmissionBeamWidth, .0001);
        Assert.AreEqual(80000, threats[0].EmissionMaxRangeMeters, .001);
        Assert.AreNotEqual(threats[0].DirectionWorld, threats[0].EmissionDirection);
    }
}
