using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEngine;

public sealed class MechForceAllocationTests
{
    private static MechForceMember Member(float x, float strength = 1f, float reach = 0f, bool busy = false)
        => new MechForceMember { Id = Guid.NewGuid(), Position = new Vector3(x, 0, 0), Strength = strength, MaximumRange = reach, Busy = busy };

    private static MechForceThreat Threat(float z, float strength = 1f)
        => new MechForceThreat { Position = new Vector3(0, 0, z), Strength = strength };

    [Test]
    public void OneWeakThreat_CommitsEnoughAndKeepsReserve()
    {
        var members = new List<MechForceMember> { Member(0), Member(100), Member(200), Member(300) };
        var threats = new List<MechForceThreat> { Threat(5000, 0.8f) };
        var a = MechForceAllocation.Allocate(members, threats, Vector3.zero);
        Assert.That(a.Count, Is.EqualTo(1), "One member (1.0) covers 0.8 x 1.2");
        Assert.That(members.Count - a.Count, Is.EqualTo(3), "Rest stays in reserve");
    }

    [Test]
    public void StrongThreat_GetsAPair_ButNeverMoreThanCap()
    {
        var members = new List<MechForceMember> { Member(0), Member(100), Member(200), Member(300) };
        var threats = new List<MechForceThreat> { Threat(5000, 5f) };
        var a = MechForceAllocation.Allocate(members, threats, Vector3.zero);
        Assert.That(a.Count, Is.EqualTo(MechForceAllocation.MaximumPerThreat));
    }

    [Test]
    public void ManyThreats_EngageAtLeastHalfThreatCount()
    {
        var members = new List<MechForceMember> { Member(0, 3f), Member(100, 3f), Member(200, 3f), Member(300, 3f) };
        var threats = new List<MechForceThreat> { Threat(4000, 0.1f), Threat(6000, 0.1f), Threat(8000, 0.1f), Threat(9000, 0.1f) };
        var a = MechForceAllocation.Allocate(members, threats, Vector3.zero);
        Assert.That(a.Select(x => x.ThreatIndex).Distinct().Count(), Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void NearestThreatIsServedFirst_ByNearestMember()
    {
        var near = Member(0); var far = Member(9000);
        var members = new List<MechForceMember> { far, near };
        var threats = new List<MechForceThreat> { Threat(30000, 0.5f), Threat(2000, 0.5f) };
        var a = MechForceAllocation.Allocate(members, threats, Vector3.zero);
        Assert.That(a[0].ThreatIndex, Is.EqualTo(1));
        Assert.That(a[0].Member, Is.EqualTo(near.Id));
    }

    [Test]
    public void BusyWinchesterOrOutOfReachMembers_AreNotAssigned()
    {
        var busy = Member(0, busy: true); var empty = Member(10, strength: 0f); var shortReach = Member(20, reach: 1000f);
        var members = new List<MechForceMember> { busy, empty, shortReach };
        var a = MechForceAllocation.Allocate(members, new List<MechForceThreat> { Threat(5000) }, Vector3.zero);
        Assert.That(a, Is.Empty);
    }

    [TestCase(2f, 5f, true)]
    [TestCase(4.6f, 5f, false)]
    [TestCase(6f, 5f, false)]
    [TestCase(6f, -1f, true)]
    public void CanReachBeforeImpact_IncludesReaction(float intercept, float impact, bool expected)
        => Assert.That(MechForceAllocation.CanReachBeforeImpact(intercept, impact), Is.EqualTo(expected));

    [Test]
    public void ThreatStrength_HeavyLongRangeOutranksLightRecon()
    {
        var heavy = new TrackSnapshot { IdentifiedWeight = ObservedWeightClass.Heavy, IdentifiedRole = ObservedCombatRole.LongRangeAirToAir, Confidence = 1f };
        var recon = new TrackSnapshot { IdentifiedWeight = ObservedWeightClass.Light, IdentifiedRole = ObservedCombatRole.Reconnaissance, Confidence = 1f };
        Assert.That(MechForceAllocation.ThreatStrength(in heavy), Is.GreaterThan(MechForceAllocation.ThreatStrength(in recon) * 3f));
    }

    [Test]
    public void MemberStrength_ZeroWhenWinchesterOrUnarmed_ScalesWithHealth()
    {
        Assert.That(MechForceAllocation.MemberStrength(1f, 2, true, winchester: true), Is.Zero);
        Assert.That(MechForceAllocation.MemberStrength(1f, 0, false, false), Is.Zero);
        Assert.That(MechForceAllocation.MemberStrength(1f, 2, true, false), Is.GreaterThan(MechForceAllocation.MemberStrength(0.2f, 2, true, false)));
    }
}
