using System;
using ACunicombat.ElectronicWarfare;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Sensors;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Kinematics;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

public sealed class MechElectronicWarfareTests
{
    private static JamContribution Noise(double range, float3 emissionDirection)
    {
        var hardware = new MechRadarPerformance().Physical;
        bool accepted = EwInjectionSystem.TryCreateNoiseContribution(
            new SensorId(new Handle(0, 1)), new EmitterId(new Handle(9, 1)),
            new double3(0, 0, range), double3.zero, emissionDirection,
            math.radians(45), 150000, 50000, hardware.GainLinear, 1,
            hardware.FrequencyHz, 100e6f, out var contribution);
        Assert.IsTrue(accepted);
        return contribution;
    }

    [Test]
    public void NoisePowerUsesOneWayPropagationAndReceiverGain()
    {
        var near = Noise(10000, new float3(0, 0, -1));
        var far = Noise(20000, new float3(0, 0, -1));
        Assert.That(near.PowerDb - far.PowerDb, Is.EqualTo(20 * Math.Log10(2)).Within(.001));
        Assert.IsTrue(near.PowerIsDbw);
        Assert.That(math.distance(near.ArrivalDirection, new float3(0, 0, 1)), Is.LessThan(.001));
    }

    [Test]
    public void NoiseRejectsOutsideBeamAndInvalidReceiver()
    {
        var hardware = new MechRadarPerformance().Physical;
        Assert.IsFalse(EwInjectionSystem.TryCreateNoiseContribution(
            new SensorId(new Handle(0, 1)), default, new double3(0, 0, 10000), double3.zero,
            new float3(0, 0, 1), math.radians(45), 150000, 50000, hardware.GainLinear,
            1, hardware.FrequencyHz, 100e6f, out _));
        Assert.IsFalse(EwInjectionSystem.TryCreateNoiseContribution(
            default, default, new double3(0, 0, 10000), double3.zero,
            new float3(0, 0, -1), math.radians(45), 150000, 50000, hardware.GainLinear,
            1, hardware.FrequencyHz, 100e6f, out _));
    }

    private static ThreatSnapshot Threat(EsmWaveformClass kind, float power, double seen = 10)
        => new ThreatSnapshot
        {
            WaveformClass = kind,
            DirectionWorld = new float3(0, 0, 1),
            ContactCount = 3,
            IdentificationConfidence = 1,
            LastSeenTime = seen,
            ReceivedPowerDb = power,
            FrequencyBand = 1,
            WaveformId = 3,
            Level = EsmThreatLevel.Tracked,
        };

    [Test]
    public void SelfDefensePrefersFireControlRadarAndIgnoresJamming()
    {
        var threats = new[]
        {
            Threat(EsmWaveformClass.NoiseJammer, 1000),
            Threat(EsmWaveformClass.SearchRadar, 40),
            Threat(EsmWaveformClass.FireControlRadar, 20),
        };
        Assert.IsTrue(MechElectronicWarfareTargeting.TrySelectSelfDefenseThreat(threats, threats.Length, 10, out var selected));
        Assert.AreEqual(EsmWaveformClass.FireControlRadar, selected.WaveformClass);
    }

    [Test]
    public void SelfDefenseRequiresFreshDirectionEvidence()
    {
        var threats = new[] { Threat(EsmWaveformClass.SearchRadar, 40, 7) };
        Assert.IsFalse(MechElectronicWarfareTargeting.TrySelectSelfDefenseThreat(threats, threats.Length, 10, out _));
    }

    [Test]
    public void PersistentNoiseCanBePassivelyDetectedAtCombatRange()
    {
        using var world = new SimWorld();
        using var receiver = new EsmReceiverSystem(world) { SessionSeed = 7 };
        var owner = world.Register(new KinematicState { PositionWorld = double3.zero, Rotation = quaternion.identity, Mass = 1 });
        var id = new SensorId(new Handle(0, 1));
        receiver.RegisterReceiver(new EsmReceiverProfile
        {
            ReceiverId = id,
            OwnerEntityId = owner,
            SensitivityFloorDb = -60,
            BearingSigmaRadians = math.radians(8),
        });
        world.SwapSnapshots(1);
        using var emissions = new NativeList<EmissionEvent>(1, Allocator.Temp);
        emissions.Add(new EmissionEvent
        {
            EmitterId = new EmitterId(new Handle(9, 1)),
            Origin = new double3(0, 0, 100000),
            Direction = new float3(0, 0, -1),
            BeamWidth = math.radians(45),
            MainlobePowerDb = 10f * math.log10(50000),
            SidelobeLevelDb = -20,
            FrequencyBand = 1,
            WaveformId = MechElectronicWarfareController.NoiseWaveformId,
            EmissionType = EmissionType.Jamming,
        });
        receiver.BeginTick();
        receiver.Consume(emissions, 1);
        Assert.AreEqual(1, receiver.Contacts.Length);
        Assert.AreEqual(EmissionType.Jamming, receiver.Contacts[0].EmissionType);
    }

    [Test]
    public void ElectronicWarfareMissionIsAvailableAndAccepted()
    {
        Assert.IsTrue(MechFleetController.MissionAvailable(FleetTaskGroup.ElectronicWarfare));
        Assert.IsFalse(MechFleetController.MissionAvailable(FleetTaskGroup.EarlyWarningCommand));
        var leader = Guid.NewGuid();
        var wing = Guid.NewGuid();
        var state = new MechFleetState(wing, Guid.NewGuid(), leader, false, false, FleetAttackMode.Confirmed);
        var track = new TrackSnapshot
        {
            TrackId = new TrackId(new Handle(4, 1)),
            Position = new double3(50000, 0, 0),
            LastUpdateTime = 1,
            LastPredictTime = 1,
            Lifecycle = TrackLifecycle.Confirmed,
            ObjectClass = TrackObjectClass.AirVehicle,
        };
        var order = new FleetPacket
        {
            Kind = FleetPacketKind.Order,
            Order = FleetOrderKind.AssignMission,
            TaskGroup = FleetTaskGroup.ElectronicWarfare,
            MissionGeometry = FleetMissionGeometry.Target,
            Target = track,
            Sender = leader,
            Recipient = wing,
            Fleet = state.Fleet,
            Operation = Guid.NewGuid(),
            Revision = 1,
            ObservedAt = 1,
            ExpiresAt = 4,
            TaskDeadline = 30,
        };
        Assert.IsTrue(state.Receive(order, 1));
        Assert.AreEqual(FleetTaskGroup.ElectronicWarfare, state.TaskGroup);
    }

    private sealed class RadarFixture : IDisposable
    {
        public readonly SimWorld World = new SimWorld();
        public readonly SpatialHashGrid Grid = new SpatialHashGrid(SimWorld.MaxEntities * 2);
        public readonly DwellScheduler Scheduler = new DwellScheduler(64);
        public readonly RadarSensorSystem Radar;
        public readonly RadarPhysicalParameters Hardware = new MechRadarPerformance().Physical;
        public readonly SensorId Id = new SensorId(new Handle(0, 1));
        private ulong sequence;

        public RadarFixture()
        {
            Hardware.AntennaDutyCycle = 1;
            Radar = new RadarSensorSystem(World, Grid, Scheduler) { SessionSeed = 19 };
            var owner = World.Register(State(double3.zero));
            Radar.RegisterSensor(new RadarSensorProfile
            {
                SensorId = Id,
                OwnerEntityId = owner,
                EmitterId = new EmitterId(new Handle(0, 1)),
                Physical = Hardware,
                DwellConfig = Hardware.BuildDwell(.008f, Hardware.BeamWidthRadians),
                ClutterPowerDb = -300,
                SidelobeLevelDb = -25,
                FrequencyBand = 1,
                Capability = SensorCapability.Range | SensorCapability.Doppler,
            });
            var target = World.Register(State(new double3(0, 0, 100000)));
            Radar.RegisterTarget(new RadarTargetProfile { EntityId = target, RadarCrossSectionSquareMeters = 1 });
        }

        private static KinematicState State(double3 position)
            => new KinematicState { PositionWorld = position, Rotation = quaternion.identity, Mass = 1 };

        public float Sample(double now, JamContribution? jam = null)
        {
            Radar.ScheduleDwell(new Dwell
            {
                SensorId = Id,
                DueTime = now,
                DwellTimeSeconds = .008f,
                BeamDirection = new float3(0, 0, 1),
                BeamWidthRadians = Hardware.BeamWidthRadians,
                Sequence = ++sequence,
            });
            if (jam.HasValue) Radar.JamInboxForEw.Add(jam.Value);
            Radar.Tick(now);
            var diagnostics = Radar.SignalDiagnostics;
            return diagnostics[diagnostics.Count - 1].SnrDb;
        }

        public void Dispose()
        {
            Radar.Dispose();
            Scheduler.Dispose();
            Grid.Dispose();
            World.Dispose();
        }
    }

    [Test]
    public void PhysicalNoiseSuppressesSnrAndStoppingRestoresReceiver()
    {
        using var fixture = new RadarFixture();
        float baseline = fixture.Sample(.04);
        var jam = Noise(100000, new float3(0, 0, -1));
        float jammed = fixture.Sample(.08, jam);
        float recovered = fixture.Sample(.12);
        Assert.That(jammed, Is.LessThan(baseline - 20));
        Assert.That(recovered, Is.EqualTo(baseline).Within(.001));
    }

    [Test]
    public void NoiseOnlyAffectsAddressedRadar()
    {
        using var fixture = new RadarFixture();
        float baseline = fixture.Sample(.04);
        var jam = Noise(100000, new float3(0, 0, -1));
        jam.VictimSensorId = new SensorId(new Handle(7, 1));
        Assert.That(fixture.Sample(.08, jam), Is.EqualTo(baseline).Within(.001));
    }
}
