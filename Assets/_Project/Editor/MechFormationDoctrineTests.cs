using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEngine;

public sealed class MechFormationDoctrineTests
{
    [TestCase(FleetTaskGroup.Strike, MechFormationStyle.Assault)]
    [TestCase(FleetTaskGroup.AirSuperiority, MechFormationStyle.Assault)]
    [TestCase(FleetTaskGroup.AirDefenseSuppression, MechFormationStyle.Assault)]
    [TestCase(FleetTaskGroup.Escort, MechFormationStyle.Airliner)]
    [TestCase(FleetTaskGroup.Support, MechFormationStyle.Airliner)]
    [TestCase(FleetTaskGroup.EarlyWarningCommand, MechFormationStyle.Airliner)]
    public void TaskGroup_SelectsKeepingStyle(FleetTaskGroup group, MechFormationStyle expected)
        => Assert.That(MechFormationDoctrine.DefaultStyle(group), Is.EqualTo(expected));

    [Test]
    public void AirlinerStyle_IsWiderAndSmootherThanAssault()
    {
        var air = MechFormationDoctrine.Parameters(MechFormationStyle.Airliner);
        var assault = MechFormationDoctrine.Parameters(MechFormationStyle.Assault);
        Assert.That(air.SpacingScale, Is.GreaterThan(assault.SpacingScale));
        Assert.That(air.GainSeconds, Is.GreaterThan(assault.GainSeconds));
        Assert.That(air.WeaveAmplitude, Is.Zero);
        Assert.That(assault.WeaveAmplitude, Is.GreaterThan(0f));
        Assert.That(air.BoostToRejoin, Is.False);
        Assert.That(assault.BoostToRejoin, Is.True);
    }

    [Test]
    public void StrikeRole_AutomaticFromLoadout_ManualOverrideWins()
    {
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.Strike, 1, false, true, false, false), Is.EqualTo(MechStationRole.Attacker));
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.Strike, 1, false, false, false, false), Is.EqualTo(MechStationRole.Escort));
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.Strike, 1, false, true, false, false, MechStationRole.Escort),
            Is.EqualTo(MechStationRole.Escort), "Forced role must override automatic assignment");
    }

    [Test]
    public void MissionRoles_FollowCapabilities()
    {
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.AirDefenseSuppression, 0, true, true, true, false), Is.EqualTo(MechStationRole.Shooter));
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.AirDefenseSuppression, 1, false, true, false, false), Is.EqualTo(MechStationRole.Bait));
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.ElectronicWarfare, 1, false, false, false, true), Is.EqualTo(MechStationRole.Jammer));
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.Reconnaissance, 0, true, false, false, false), Is.EqualTo(MechStationRole.Scout));
        Assert.That(MechFormationDoctrine.ResolveRole(FleetTaskGroup.Reconnaissance, 2, false, false, false, false), Is.EqualTo(MechStationRole.Cover));
    }

    [Test]
    public void StationBand_UsesHysteresisSoOnStationDoesNotChatter()
    {
        var p = MechFormationDoctrine.Parameters(MechFormationStyle.Assault);
        var band = MechFormationDoctrine.ResolveBand(p.OnStationRadius * 0.5f, 5f, MechStationBand.Joining, in p);
        Assert.That(band, Is.EqualTo(MechStationBand.OnStation));
        // Drifting past the entry radius but within release keeps the lock.
        band = MechFormationDoctrine.ResolveBand((p.OnStationRadius + p.ReleaseRadius) * 0.5f, 5f, band, in p);
        Assert.That(band, Is.EqualTo(MechStationBand.OnStation));
        band = MechFormationDoctrine.ResolveBand(p.ReleaseRadius * 1.2f, 5f, band, in p);
        Assert.That(band, Is.EqualTo(MechStationBand.Joining));
        band = MechFormationDoctrine.ResolveBand(p.JoinRadius * 2f, 5f, band, in p);
        Assert.That(band, Is.EqualTo(MechStationBand.Far));
    }

    [Test]
    public void OnStation_FeedsForwardLeaderVelocity()
    {
        var p = MechFormationDoctrine.Parameters(MechFormationStyle.Airliner);
        var slotVelocity = new Vector3(0, 0, 200);
        // Exactly on the slot, already matched: no correction needed.
        var a = MechFormationDoctrine.StationAcceleration(Vector3.zero, slotVelocity, slotVelocity, MechStationBand.OnStation, in p, 30f, 30f);
        Assert.That(a.magnitude, Is.LessThan(0.01f));
        // Matched position but lagging speed: accelerate along the leader's course, not toward a point.
        a = MechFormationDoctrine.StationAcceleration(Vector3.zero, slotVelocity, new Vector3(0, 0, 150), MechStationBand.OnStation, in p, 30f, 30f);
        Assert.That(a.z, Is.GreaterThan(0f));
        Assert.That(Mathf.Abs(a.x), Is.LessThan(0.01f));
    }

    [Test]
    public void FarFromSlot_ClosesFasterThanWhenJoining_ButRespectsBraking()
    {
        var p = MechFormationDoctrine.Parameters(MechFormationStyle.Assault);
        var far = MechFormationDoctrine.StationAcceleration(new Vector3(0, 0, 5000), Vector3.zero, Vector3.zero, MechStationBand.Far, in p, 30f, 1000f);
        var join = MechFormationDoctrine.StationAcceleration(new Vector3(0, 0, 400), Vector3.zero, Vector3.zero, MechStationBand.Joining, in p, 30f, 1000f);
        Assert.That(far.z, Is.GreaterThan(join.z));
        // 2 m from the slot with tiny braking capacity must not demand a large closure speed.
        var near = MechFormationDoctrine.StationAcceleration(new Vector3(0, 0, 2), Vector3.zero, Vector3.zero, MechStationBand.Far, in p, 0.5f, 1000f);
        Assert.That(near.z, Is.LessThan(2f));
    }

    [Test]
    public void ShooterDeconfliction_YieldsToClearlyCloserFriend_AndBreaksTiesByOrder()
    {
        Assert.That(MechFormationDoctrine.ShouldYieldTarget(ownRange: 5000, friendRange: 3000, ownOrder: 0, friendOrder: 1), Is.True);
        Assert.That(MechFormationDoctrine.ShouldYieldTarget(ownRange: 3000, friendRange: 5000, ownOrder: 1, friendOrder: 0), Is.False);
        // Near-equal ranges: exactly one of the two keeps the target.
        bool a = MechFormationDoctrine.ShouldYieldTarget(4000, 4050, 1, 2);
        bool b = MechFormationDoctrine.ShouldYieldTarget(4050, 4000, 2, 1);
        Assert.That(a ^ b, Is.True);
    }

    [Test]
    public void AbandonChase_OnlyWhenOutOfReachAndNotClosing()
    {
        Assert.That(MechFormationDoctrine.ShouldAbandonChase(20000, 8000, closingSpeed: -10), Is.True);
        Assert.That(MechFormationDoctrine.ShouldAbandonChase(20000, 8000, closingSpeed: 50), Is.False, "Still closing: keep chasing");
        Assert.That(MechFormationDoctrine.ShouldAbandonChase(9000, 8000, closingSpeed: -10), Is.False, "Within margin");
        Assert.That(MechFormationDoctrine.ShouldAbandonChase(20000, 0, closingSpeed: -10), Is.False, "Unknown reach never abandons");
    }

    [Test]
    public void AssaultWeave_DesynchronisesMembers()
    {
        var p = MechFormationDoctrine.Parameters(MechFormationStyle.Assault);
        float a = MechFormationDoctrine.WeaveOffset(in p, 3f, 1), b = MechFormationDoctrine.WeaveOffset(in p, 3f, 2);
        Assert.That(Mathf.Abs(a - b), Is.GreaterThan(1f));
        Assert.That(Mathf.Abs(a), Is.LessThanOrEqualTo(p.WeaveAmplitude + 0.001f));
        Assert.That(MechFormationDoctrine.WeaveOffset(MechFormationDoctrine.Parameters(MechFormationStyle.Airliner), 3f, 1), Is.Zero);
    }
}
