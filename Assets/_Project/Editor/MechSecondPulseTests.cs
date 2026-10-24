using NUnit.Framework;
using ACunicombat.Mechs;
public class MechSecondPulseTests
{
    [Test] public void FarClosingTargetKeepsReserve() => Assert.IsNull(MissileSecondPulsePolicy.Evaluate(220000,1600,1600,0,11,500));
    [Test] public void NearTargetStartsBeforeTerminalRun() => Assert.AreEqual("terminal_energy",MissileSecondPulsePolicy.Evaluate(35000,1600,1600,0,11,500));
    [Test] public void FleeingTargetNeedsEarlyEnergy() => Assert.AreEqual("insufficient_closure",MissileSecondPulsePolicy.Evaluate(120000,200,1600,0,11,500));
    [Test] public void EvasiveTurnCanAdvanceIgnition() => Assert.AreEqual("maneuver_recovery",MissileSecondPulsePolicy.Evaluate(65000,1600,1600,12,11,500));
    [Test] public void SmallBearingNoiseDoesNotTrigger() => Assert.IsNull(MissileSecondPulsePolicy.Evaluate(65000,1600,1600,2,11,500));
    [Test] public void UnresolvedContactCannotSpendPulse() => Assert.IsNull(MissileSecondPulsePolicy.Evaluate(float.PositiveInfinity,0,1600,0,11,500));
    [Test] public void OtherMissileStagingIsUnchanged()
    {
        Assert.IsTrue(AC6TestWeaponProfileFactory.Create("WP_L_4310").missileFlightProfile.AdaptiveSecondPulse);
        foreach(string n in new[]{"4302","4230","4340"})Assert.IsFalse(AC6TestWeaponProfileFactory.Create("WP_L_"+n).missileFlightProfile.AdaptiveSecondPulse);
    }
}
