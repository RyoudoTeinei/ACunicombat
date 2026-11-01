using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public sealed class MechStrikeMissionTests
{
    private static MechStrikeInputs Inputs(float now, float targetRange, int fresh = 4, int onStation = 4, bool observed = true)
        => new MechStrikeInputs
        {
            Now = now,
            TargetObserved = observed,
            TargetPosition = new Vector3(0, 0, targetRange),
            PackagePosition = Vector3.zero,
            AttackRangeMeters = 8000f,
            MembersFresh = fresh,
            MembersOnStation = onStation,
        };

    [Test]
    public void Package_AssemblesBeforeIngress()
    {
        var strike = new MechStrikeMission();
        strike.Reset(0f);
        Assert.That(strike.Update(Inputs(1f, 60000f, fresh: 4, onStation: 2)), Is.EqualTo(MechStrikePhase.Assemble));
        Assert.That(strike.Update(Inputs(2f, 60000f, fresh: 4, onStation: 4)), Is.EqualTo(MechStrikePhase.Ingress));
    }

    [Test]
    public void Assembly_TimesOutSoAStragglerCannotHoldThePackage()
    {
        var strike = new MechStrikeMission();
        strike.Reset(0f);
        strike.Update(Inputs(1f, 60000f, fresh: 4, onStation: 1));
        Assert.That(strike.Update(Inputs(MechStrikeMission.AssembleTimeoutSeconds + 1f, 60000f, fresh: 4, onStation: 1)),
            Is.EqualTo(MechStrikePhase.Ingress));
    }

    [Test]
    public void Ingress_BecomesAttackAtTheAttackLine()
    {
        var strike = new MechStrikeMission();
        strike.Reset(0f);
        strike.Update(Inputs(1f, 60000f));
        Assert.That(strike.Update(Inputs(2f, 30000f)), Is.EqualTo(MechStrikePhase.Ingress));
        Assert.That(strike.Update(Inputs(3f, 8000f * MechStrikeMission.IngressLineFactor - 10f)), Is.EqualTo(MechStrikePhase.Attack));
    }

    [Test]
    public void LostTarget_EgressesThenRejoinsThenCompletes()
    {
        var strike = new MechStrikeMission();
        strike.Reset(0f);
        strike.Update(Inputs(1f, 60000f));
        strike.Update(Inputs(2f, 5000f));
        Assert.That(strike.Phase, Is.EqualTo(MechStrikePhase.Attack));
        Assert.That(strike.Update(Inputs(5f, 5000f, observed: false)), Is.EqualTo(MechStrikePhase.Attack), "Brief dropout must not abort");
        Assert.That(strike.Update(Inputs(2f + MechStrikeMission.TargetLostSeconds + 1f, 5000f, observed: false)), Is.EqualTo(MechStrikePhase.Egress));
        // Last known target 5 km away; package must open to 1.5x attack range.
        var far = Inputs(15f, 0f, observed: false); far.PackagePosition = new Vector3(0, 0, 5000f - 8000f * 1.6f);
        Assert.That(strike.Update(far), Is.EqualTo(MechStrikePhase.Rejoin));
        Assert.That(strike.Update(Inputs(16f, 0f, observed: false)), Is.EqualTo(MechStrikePhase.Complete));
    }

    [Test]
    public void IngressPoint_SitsOnTheAttackLineToward()
    {
        var point = MechStrikeMission.IngressPoint(new Vector3(0, 0, -50000), Vector3.zero, 8000f);
        Assert.That(point.z, Is.LessThan(0f));
        Assert.That(point.magnitude, Is.LessThan(8000f * MechStrikeMission.IngressLineFactor));
        Assert.That(point.magnitude, Is.GreaterThan(8000f * 0.9f));
    }

    [Test]
    public void EscortCover_LeadsThePackageOnTheThreatAxis_OnAlternatingSides()
    {
        var a = MechStrikeMission.EscortCoverPoint(Vector3.zero, Vector3.forward, 250f, 1);
        var b = MechStrikeMission.EscortCoverPoint(Vector3.zero, Vector3.forward, 250f, 2);
        Assert.That(a.z, Is.GreaterThan(0f));
        Assert.That(b.z, Is.GreaterThan(0f));
        Assert.That(Mathf.Sign(a.x), Is.Not.EqualTo(Mathf.Sign(b.x)));
    }

    [Test]
    public void EgressPoint_OpensAwayFromTarget()
    {
        var p = MechStrikeMission.EgressPoint(new Vector3(0, 0, -4000), Vector3.zero, 8000f);
        Assert.That(p.z, Is.LessThan(-8000f * MechStrikeMission.EgressDistanceFactor));
    }
}
