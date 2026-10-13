using System;
using System.Reflection;
using ACunicombat.DataLink;
using ACunicombat.FireControl;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using ACunicombat.Tracking;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

public sealed class MechMissileTrackLifecycleTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private GameObject fixture;
    private SimulationDriver driver;
    private MechRadarController radar;
    private MechRadarHudPresenter hud;
    private SimWorld world;
    private DataLinkSystem dataLink;
    private RadarTrackManager trackManager;

    [SetUp]
    public void SetUp()
    {
        // Keep scene discovery, loadout initialization, audio and telemetry out of
        // these lifecycle tests. Only the real tracking and link stores are needed.
        fixture = new GameObject("Missile track lifecycle fixture");
        fixture.SetActive(false);
        driver = fixture.AddComponent<SimulationDriver>();
        radar = fixture.AddComponent<MechRadarController>();
        hud = fixture.AddComponent<MechRadarHudPresenter>();
        world = new SimWorld();
        dataLink = new DataLinkSystem(world);
        trackManager = new RadarTrackManager();
        SetField(driver, "world", world);
        SetField(driver, "dataLink", dataLink);
        SetField(driver, "trackManager", trackManager);
        SetField(driver, "motionClock", new FixedTickClock(0.01, 1000));
        SetField(radar, "driver", driver);
        SetField(radar, "initialized", true);
        SetField(radar, "dataLinkPerformance", MechDataLinkPerformance.CreateStandardFallback());
        SetField(hud, "radar", radar);
    }

    [TearDown]
    public void TearDown()
    {
        // Own the stores explicitly because the inactive fixture never runs Awake.
        // Detach them before destroying components to prevent double disposal.
        if (radar != null) SetField(radar, "initialized", false);
        if (driver != null)
        {
            SetField(driver, "world", null);
            SetField(driver, "dataLink", null);
        }
        if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
        dataLink?.Dispose();
        world?.Dispose();
    }

    [Test]
    public void TerminalEnd_HidesCachedFriendlyMarkerImmediatelyButPreservesSensorTrack()
    {
        var track = CreateSensorTrack(1000f);
        var terminal = RegisterFriendlyMissile(in track);
        var cached = CopyMissileTrack(track.TrackId);
        Assert.That(cached.IsFriendly, Is.True);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayFriendlyMissile", cached), Is.True);
        GetField<MissileTrackSnapshot[]>(hud, "missileTracks")[0] = cached;
        Assert.That(Invoke<bool>(hud, "HasFriendlyMissileTrackNear", Vector3.forward * 1000f, 1), Is.True);

        radar.UnregisterMissileLinkTerminal(terminal);

        Assert.That(radar.IsRetiredFriendlyMissileTrack(track.TrackId), Is.True);
        Assert.That(driver.TryGetTrack(track.TrackId, out var raw), Is.True,
            "Ending a local weapon must not delete the anonymous sensor track.");
        Assert.That(raw.TrackId, Is.EqualTo(track.TrackId));
        Assert.That(GetField<double>(radar, "missileTrackCacheTime"), Is.NaN);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayFriendlyMissile", cached), Is.False);
        Assert.That(Invoke<bool>(hud, "HasFriendlyMissileTrackNear", Vector3.forward * 1000f, 1), Is.False,
            "An already copied HUD snapshot must stop suppressing a live projectile marker in the same frame.");
        Assert.That(CopyMissileTrack(track.TrackId).IsFriendly, Is.True,
            "The retained sensor track must not change to hostile while it coasts.");
    }

    [Test]
    public void ReusedTerminal_RejectsOldGenerationAndDoesNotRetireNewMissile()
    {
        var first = CreateSensorTrack(1000f);
        var oldTerminal = RegisterFriendlyMissile(in first);
        radar.UnregisterMissileLinkTerminal(oldTerminal);

        var second = CreateSensorTrack(3000f);
        var newTerminal = RegisterFriendlyMissile(in second);
        Assert.That(newTerminal.Value.Index, Is.EqualTo(oldTerminal.Value.Index));
        Assert.That(newTerminal.Value.Generation, Is.Not.EqualTo(oldTerminal.Value.Generation));

        radar.UnregisterMissileLinkTerminal(oldTerminal);

        // A delivery already in flight can arrive after this terminal slot has
        // been reallocated. Its higher sequence must not overwrite the new IFF.
        var inbox = GetField<NativeList<DataLinkSystem.DeliveredLinkMessage>>(dataLink, "inbox");
        inbox.Add(new DataLinkSystem.DeliveredLinkMessage
        {
            Receiver = GetField<LinkTerminalId>(radar, "ownLinkTerminalId"),
            Message = new LinkMessage
            {
                Kind = LinkMessageKind.TrackShare,
                SourceTerminal = oldTerminal,
                Sequence = 999UL,
                TrackPayload = new TrackSnapshot
                {
                    Affiliation = TrackAffiliation.Friendly,
                    ObjectClass = TrackObjectClass.Missile,
                    IdentityConfidence = 1f,
                    Position = first.Position,
                },
            },
        });
        Invoke(radar, "ConsumeFriendlyMissileReports");

        Assert.That(GetField<bool[]>(radar, "missileLinkAllocated")[newTerminal.Value.Index], Is.True);
        Assert.That(GetField<byte[]>(radar, "missileLinkGenerations")[newTerminal.Value.Index],
            Is.EqualTo(newTerminal.Value.Generation));
        Assert.That(radar.IsRetiredFriendlyMissileTrack(first.TrackId), Is.True);
        Assert.That(radar.IsRetiredFriendlyMissileTrack(second.TrackId), Is.False);
        Assert.That(CopyMissileTrack(second.TrackId).IsFriendly, Is.True);
        var currentReport = GetField<Array>(radar, "friendlyMissileReports").GetValue(newTerminal.Value.Index);
        Assert.That(GetField<LinkTerminalId>(currentReport, "SourceTerminal"), Is.EqualTo(newTerminal));

        var reusedTrackId = new TrackId(new Handle(first.TrackId.Value.Index,
            (byte)(first.TrackId.Value.Generation + 1)));
        Assert.That(radar.IsRetiredFriendlyMissileTrack(reusedTrackId), Is.False,
            "Track retirement must include the track generation, not just its array slot.");
    }

    [Test]
    public void LinkReportSilence_LeavesActiveMissileOnNormalIdentityCoast()
    {
        var track = CreateSensorTrack(1000f);
        var terminal = RegisterFriendlyMissile(in track);
        Assert.That(CopyMissileTrack(track.TrackId).IsFriendly, Is.True);
        SetTime(radar.SimTimeSeconds + 1.2);

        var coast = CopyMissileTrack(track.TrackId);

        Assert.That(GetField<bool[]>(radar, "missileLinkAllocated")[terminal.Value.Index], Is.True);
        Assert.That(radar.IsRetiredFriendlyMissileTrack(track.TrackId), Is.False);
        Assert.That(coast.IsFriendly, Is.True,
            "Report freshness expiry alone must retain the existing authenticated identity coast.");
    }

    [Test]
    public void RetiredTrack_CannotReturnAsGenericOrIncomingHudMarker()
    {
        var track = CreateSensorTrack(1000f);
        var terminal = RegisterFriendlyMissile(in track);
        var missile = CopyMissileTrack(track.TrackId);
        radar.UnregisterMissileLinkTerminal(terminal);

        // Simulate a copied presentation snapshot losing its missile/IFF label.
        track.ObjectClass = TrackObjectClass.Unknown;
        GetField<TrackSnapshot[]>(hud, "tracks")[0] = track;
        missile.Affiliation = TrackAffiliation.Unknown;
        missile.IdentityConfidence = 0f;
        missile.Quality = MissileTrackQuality.Warning;
        Assert.That(Invoke<bool>(hud, "IsTrackPresentationEligible", 0), Is.False);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayIncomingMissile", missile), Is.False);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayWorldMissileMarker", missile), Is.False);
    }

    [Test]
    public void UnownedMissile_StillUsesSensorEvidenceAgeForHudVisibility()
    {
        var track = CreateSensorTrack(1000f);
        var missile = CopyMissileTrack(track.TrackId);
        Assert.That(missile.IsFriendly, Is.False);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayIncomingMissile", missile), Is.True);

        radar.UnregisterMissileLinkTerminal(new LinkTerminalId(new Handle(63, 1)));

        Assert.That(radar.IsRetiredFriendlyMissileTrack(track.TrackId), Is.False);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayIncomingMissile", missile), Is.True);
        SetTime(radar.SimTimeSeconds + 0.7);
        Assert.That(Invoke<bool>(hud, "ShouldDisplayIncomingMissile", missile), Is.False,
            "An unowned missile continues to disappear by evidence age, not local weapon lifecycle.");
        Assert.That(driver.TryGetTrack(track.TrackId, out _), Is.True);
    }

    [Test]
    public void Retirement_OutlivesMissingMissileFusionUntilRawTrackIsDeleted()
    {
        var track = CreateSensorTrack(1000f);
        var terminal = RegisterFriendlyMissile(in track);
        radar.UnregisterMissileLinkTerminal(terminal);
        SetField(radar, "missileTrackCacheCount", 0);
        var rawCount = driver.CopyTracks(GetField<TrackSnapshot[]>(radar, "trackScratch"));

        Invoke(radar, "PruneRetiredFriendlyMissileTracks", rawCount);

        Assert.That(radar.IsRetiredFriendlyMissileTrack(track.TrackId), Is.True,
            "A temporary absence from missile fusion must not restore a generic radar marker.");
        using var noMeasurements = new NativeList<Measurement>(1, Allocator.Temp);
        trackManager.Process(noMeasurements, double3.zero, 10.0);
        Assert.That(driver.TryGetTrack(track.TrackId, out _), Is.False);
        SetTime(10.0);
        radar.CopyMissileTracks(new MissileTrackSnapshot[32]);
        Assert.That(radar.IsRetiredFriendlyMissileTrack(track.TrackId), Is.False);
    }

    [Test]
    public void CrossingMissile_CannotClaimRetiredTrackAtItsReportedPosition()
    {
        var first = CreateSensorTrack(1000f);
        var firstTerminal = RegisterFriendlyMissile(in first);
        var retired = CopyMissileTrack(first.TrackId);
        radar.UnregisterMissileLinkTerminal(firstTerminal);

        var crossing = first;
        crossing.TrackId = new TrackId(new Handle(17, 1));
        crossing.Position = new double3(0, 0, 1010);
        var activeTerminal = RegisterFriendlyMissile(in crossing, primeCache: false);
        GetField<FriendlyMissileIdentityAssociation[]>(radar, "friendlyMissileIdentityAssociations")
            [activeTerminal.Value.Index] = default;
        var live = retired;
        live.TrackId = crossing.TrackId;
        live.Position = crossing.Position;
        live.Affiliation = TrackAffiliation.Unknown;
        live.IdentityConfidence = 0f;
        var cache = GetField<MissileTrackSnapshot[]>(radar, "missileTrackCache");
        cache[0] = retired;
        cache[1] = live;
        SetField(radar, "missileTrackCacheCount", 2);

        // Place the new report exactly on the retired track: nearest-neighbour
        // selection would otherwise attach the live weapon to the spent one.
        Assert.That(MissileTrackFusion.TryScoreFriendlyDataLinkAssociation(
            in live, first.Position, first.Velocity, radar.SimTimeSeconds,
            radar.SimTimeSeconds, 1.0, 120f, 180f, out _), Is.True,
            "The crossing live track must remain inside the real IFF association gate.");
        SetReport(activeTerminal, first.Position, first.Velocity);
        Invoke(radar, "ApplyFriendlyMissileIdentification", radar.SimTimeSeconds);

        var resolved = GetField<FriendlyMissileIdentityAssociation[]>(radar,
            "friendlyMissileIdentityAssociations")[activeTerminal.Value.Index];
        Assert.That(resolved.TrackId, Is.EqualTo(crossing.TrackId));
        Assert.That(cache[1].IsFriendly, Is.True);
        Assert.That(radar.IsRetiredFriendlyMissileTrack(crossing.TrackId), Is.False);
    }

    private TrackSnapshot CreateSensorTrack(float range)
    {
        using var measurements = new NativeList<Measurement>(1, Allocator.Temp);
        var startTime = radar.SimTimeSeconds;
        for (var i = 0; i < 3; i++)
        {
            var timestamp = startTime + (i + 1) * 0.04;
            measurements.Clear();
            measurements.Add(new Measurement
            {
                SensorId = new SensorId(new Handle(0, 1)),
                Timestamp = timestamp,
                Range = range,
                Direction = new float3(0, 0, 1),
                RangeVar = 4f,
                AngleVar = 1e-6f,
                VelocityVar = 1f,
                LosRateVar = 1e-6f,
                SnrDb = 20f,
                ObjectClass = TrackObjectClass.Missile,
                ClassificationConfidence = 1f,
            });
            trackManager.Process(measurements, double3.zero, timestamp);
        }
        SetTime(startTime + 0.12);
        SetField(radar, "missileTrackCacheTime", double.NaN);
        var tracks = new TrackSnapshot[32];
        var count = driver.CopyTracks(tracks);
        for (var i = 0; i < count; i++)
            if (math.abs(tracks[i].Position.z - range) < 2.0) return tracks[i];
        Assert.Fail("The fixture did not create its sensor track.");
        return default;
    }

    private LinkTerminalId RegisterFriendlyMissile(in TrackSnapshot track, bool primeCache = true)
    {
        Assert.That(radar.TryRegisterMissileLinkTerminal(
            new Vector3((float)track.Position.x, (float)track.Position.y, (float)track.Position.z),
            Vector3.zero, out var terminal), Is.True);
        SetReport(terminal, track.Position, track.Velocity);
        GetField<FriendlyMissileIdentityAssociation[]>(radar, "friendlyMissileIdentityAssociations")
            [terminal.Value.Index] = new FriendlyMissileIdentityAssociation
            {
                TrackId = track.TrackId,
                LastAuthenticatedTime = radar.SimTimeSeconds,
                LastSupportedTime = radar.SimTimeSeconds,
                LastAssociationScore = 0.0,
            };
        SetField(radar, "missileTrackCacheTime", double.NaN);
        if (primeCache) Assert.That(CopyMissileTrack(track.TrackId).IsFriendly, Is.True);
        return terminal;
    }

    private void SetReport(LinkTerminalId terminal, double3 position, double3 velocity)
    {
        var reports = GetField<Array>(radar, "friendlyMissileReports");
        var report = Activator.CreateInstance(reports.GetType().GetElementType());
        SetField(report, "Active", true);
        SetField(report, "SourceTerminal", terminal);
        SetField(report, "Position", position);
        SetField(report, "Velocity", velocity);
        SetField(report, "StateTimestamp", radar.SimTimeSeconds);
        SetField(report, "MissionRole", MissileMissionRole.AirToAir);
        SetField(report, "MissionRoleConfidence", 1f);
        SetField(report, "Sequence", 1UL);
        reports.SetValue(report, terminal.Value.Index);
    }

    private MissileTrackSnapshot CopyMissileTrack(TrackId id)
    {
        var tracks = new MissileTrackSnapshot[32];
        var count = radar.CopyMissileTracks(tracks);
        for (var i = 0; i < count; i++)
            if (tracks[i].TrackId.Equals(id)) return tracks[i];
        Assert.Fail("Expected missile track was absent from the fusion output: " + id);
        return default;
    }

    private void SetTime(double time)
    {
        var clock = new FixedTickClock(0.01, 2000);
        clock.Advance(time + 1e-8);
        SetField(driver, "motionClock", clock);
    }

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance | BindingFlags.Public).GetValue(target);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, PrivateInstance | BindingFlags.Public).SetValue(target, value);

    private static object Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, PrivateInstance).Invoke(target, arguments);

    private static T Invoke<T>(object target, string name, params object[] arguments) =>
        (T)Invoke(target, name, arguments);
}
