using System;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEngine;

public sealed class MechLeaderSuccessionTests
{
    private readonly System.Collections.Generic.List<GameObject> created = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var go in created) if (go != null) UnityEngine.Object.DestroyImmediate(go);
        created.Clear();
        MechFleetController.Roster.Clear();
    }

    private MechFleetController Member(Guid fleet, Guid leader, Guid root, int index, int size, out Guid id)
    {
        var go = new GameObject("member" + index); created.Add(go);
        var f = go.AddComponent<MechFleetController>();
        id = f.MemberId;
        f.Configure(fleet, leader == Guid.Empty ? id : leader, false, index == 0, index, FleetAttackMode.Confirmed,
            root == Guid.Empty ? id : root, size);
        return f;
    }

    private static void Heartbeat(MechFleetController observer, Guid sender, Guid fleet, double now, bool alive = true)
        => observer.State.Receive(new FleetPacket
        {
            Kind = FleetPacketKind.Heartbeat, Sender = sender, Fleet = fleet,
            ObservedAt = now, ExpiresAt = now + 3, Available = alive,
        }, now);

    private static void Tick(MechFleetController f, double now)
        => typeof(MechFleetController).GetMethod("UpdateSuccession",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(f, new object[] { now });

    private static void Register(Guid member, Guid fleet, Guid leader, Guid root, int index, int size)
        => MechFleetController.Roster[member] = new MechFleetRosterEntry
        { Member = member, Fleet = fleet, Leader = leader, FormationRoot = root, FormationIndex = index, FormationSize = size };

    [Test]
    public void LiveConfiguredLeader_IsFollowed()
    {
        var fleet = Guid.NewGuid(); var lead = Guid.NewGuid();
        Register(lead, fleet, lead, lead, 0, 3);
        var wing = Member(fleet, lead, lead, 1, 3, out _);
        Heartbeat(wing, lead, fleet, 1);
        Tick(wing, 1);
        Assert.That(wing.ActiveLeader, Is.EqualTo(lead));
        Assert.That(wing.IsActingLeader, Is.False);
    }

    [Test]
    public void LostElementLead_FallsBackToFormationRoot()
    {
        var fleet = Guid.NewGuid(); var root = Guid.NewGuid(); var element = Guid.NewGuid();
        Register(root, fleet, root, root, 0, 4);
        Register(element, fleet, root, root, 2, 4);
        var wing = Member(fleet, element, root, 3, 4, out _);
        Heartbeat(wing, root, fleet, 1);
        Heartbeat(wing, element, fleet, 1, alive: false);
        Tick(wing, 1);
        Assert.That(wing.ActiveLeader, Is.EqualTo(root));
        Assert.That(wing.LeaderSucceeded, Is.True);
    }

    [Test]
    public void LostRoot_LowestLiveIndexBecomesActingLeader()
    {
        var fleet = Guid.NewGuid(); var root = Guid.NewGuid();
        Register(root, fleet, root, root, 0, 3);
        var second = Member(fleet, root, root, 1, 3, out var secondId);
        var third = Member(fleet, root, root, 2, 3, out _);
        // Root silent (never heard) past the startup grace. Each member hears the other.
        Tick(second, 0); Tick(third, 0);
        double t = MechFleetController.SuccessionStartupGraceSeconds + 1;
        Heartbeat(third, secondId, fleet, t);
        Heartbeat(second, third.MemberId, fleet, t);
        Tick(second, t); Tick(third, t);
        Assert.That(second.IsActingLeader, Is.True, "Lowest live index takes over");
        Assert.That(third.ActiveLeader, Is.EqualTo(secondId), "Others follow the same successor");
    }

    [Test]
    public void NeverHeardLeader_IsNotSucceededDuringStartupGrace()
    {
        var fleet = Guid.NewGuid(); var lead = Guid.NewGuid();
        Register(lead, fleet, lead, lead, 0, 2);
        var wing = Member(fleet, lead, lead, 1, 2, out _);
        Tick(wing, 0);
        Tick(wing, 1);
        Assert.That(wing.ActiveLeader, Is.EqualTo(lead), "No heartbeat yet is not a lost leader");
        Tick(wing, MechFleetController.SuccessionStartupGraceSeconds + 0.5);
        Assert.That(wing.IsActingLeader, Is.True, "Still silent after grace: take over");
    }

    [Test]
    public void StaleReportsExpire_AndLeaderReturnsWhenHeardAgain()
    {
        var fleet = Guid.NewGuid(); var lead = Guid.NewGuid();
        Register(lead, fleet, lead, lead, 0, 2);
        var wing = Member(fleet, lead, lead, 1, 2, out _);
        Heartbeat(wing, lead, fleet, 1);
        Tick(wing, 10); // report expired at 4
        Assert.That(wing.IsActingLeader, Is.True);
        Heartbeat(wing, lead, fleet, 11);
        Tick(wing, 11);
        Assert.That(wing.ActiveLeader, Is.EqualTo(lead));
    }
}
