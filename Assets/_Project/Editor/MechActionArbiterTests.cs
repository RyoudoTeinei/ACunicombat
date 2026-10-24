using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Mechs;
using NUnit.Framework;

public sealed class MechActionArbiterTests
{
    private sealed class Executor : IMechActionExecutor
    {
        public float Energy = 10;
        public int Guidance = 2, Began, Cancelled;
        public bool IsReady, Valid = true, ThrowBegin, ThrowCommit;
        public readonly List<MechActionProposal> Committed = new List<MechActionProposal>();
        public MechActionBudget AvailableBudget => new MechActionBudget(Energy, Guidance);
        public bool Check(MechActionProposal p, out string reason) { reason = "Unavailable"; return Valid; }
        public bool Begin(MechActionProposal p, out string reason) { Began++; reason = null; if (ThrowBegin) throw new InvalidOperationException(); return true; }
        public bool Ready(MechActionProposal p) => IsReady;
        public bool Commit(MechActionProposal p, out string reason)
        { if (ThrowCommit) throw new InvalidOperationException(); Energy -= p.EnergyMj; Committed.Add(p); reason = "Committed"; return true; }
        public void Cancel(MechActionProposal p) { Cancelled++; }
    }
    private static MechActionProposal Proposal(long sequence, string slot = "gun", Guid instance = default, float energy = 1, int guidance = 0,
        double deadline = 10, MechActionSource source = MechActionSource.Player, MechEmissionRequirement emission = MechEmissionRequirement.None)
        => new MechActionProposal(new MechActionId(source, sequence), instance == Guid.Empty ? Guid.NewGuid() : instance,
            new MechEquipmentSlot(slot), MechActionKind.Fire, default, deadline, energy, guidance, emission);
    [Test] public void PhysicsDefenseTickRetainsPlayerQueueAndNeverDoubleCommits()
    {
        var executor=new Executor{IsReady=true};var arbiter=new MechActionArbiter(executor);
        var player=Proposal(1);var defense=Proposal(1,source:MechActionSource.AutomaticDefense);
        arbiter.Submit(player,0);arbiter.Submit(defense,0);arbiter.Tick(0,true);
        Assert.That(executor.Committed.Count,Is.EqualTo(1));Assert.That(executor.Committed[0].Id.Source,Is.EqualTo(MechActionSource.AutomaticDefense));
        Assert.That(arbiter.HasPending(player.InstanceId),Is.True);
        arbiter.Tick(0,true);Assert.That(executor.Committed.Count,Is.EqualTo(1));
        arbiter.Tick(.01);Assert.That(executor.Committed.Count,Is.EqualTo(2));Assert.That(arbiter.ActiveCount,Is.Zero);
    }
    [Test] public void EightSlots_RespectCompleteResourceBundle()
    {
        var e = new Executor(); var a = new MechActionArbiter(e);
        for (int i=0;i<8;i++) Assert.That(a.Submit(Proposal(i+1,"slot."+i,guidance:1),0),Is.True);
        a.Tick(0); Assert.That(e.Began,Is.EqualTo(2)); Assert.That(a.ReservedGuidanceUnits,Is.EqualTo(2));
        Assert.That(e.Energy,Is.EqualTo(10)); Assert.That(e.Committed,Is.Empty);
        e.IsReady=true; a.Tick(1); Assert.That(e.Committed.Count,Is.EqualTo(2));
        Assert.That(e.Energy,Is.EqualTo(8)); Assert.That(a.ReservedEnergyMj,Is.Zero); Assert.That(a.ReservedGuidanceUnits,Is.Zero);
    }
    [Test] public void ConflictingRequests_UseStableSlotOrder()
    {
        var e=new Executor { Guidance=1 };var a=new MechActionArbiter(e);
        a.Submit(Proposal(1,"z",guidance:1),0);a.Submit(Proposal(2,"a",guidance:1),0);a.Tick(0);
        e.IsReady=true;a.Tick(1);Assert.That(e.Committed.Single().Slot.Id,Is.EqualTo("a"));
    }
    [Test] public void SameEquipment_HasOnlyOneExecutionLease()
    {
        var e=new Executor();var a=new MechActionArbiter(e);var id=Guid.NewGuid();
        a.Submit(Proposal(1,instance:id),0);a.Submit(Proposal(2,instance:id),0);a.Tick(0);
        Assert.That(e.Began,Is.EqualTo(1));Assert.That(a.ActiveCount,Is.EqualTo(1));
    }
    [Test] public void ReservationDoesNotDeduct_AndCancelReleasesExactlyOnce()
    {
        var e=new Executor();var a=new MechActionArbiter(e);var p=Proposal(1,guidance:1);
        a.Submit(p,0);a.Tick(0);Assert.That(e.Energy,Is.EqualTo(10));
        Assert.That(a.Cancel(p.Id,1),Is.True);Assert.That(a.Cancel(p.Id,1),Is.False);
        Assert.That(e.Cancelled,Is.EqualTo(1));Assert.That(a.ReservedEnergyMj,Is.Zero);Assert.That(a.ReservedGuidanceUnits,Is.Zero);
    }
    [Test] public void QueuedCancel_NeverBeginsOrSpends()
    {
        var e=new Executor { IsReady=true };var a=new MechActionArbiter(e);var p=Proposal(1);
        a.Submit(p,0);a.Cancel(p.Id,0);a.Tick(0);Assert.That(e.Began,Is.Zero);Assert.That(e.Committed,Is.Empty);
    }
    [Test] public void DuplicateCommands_AreSingleEffectEvenAfterReceiptEviction()
    {
        var e=new Executor { IsReady=true };var a=new MechActionArbiter(e);var p=Proposal(1,energy:0);
        a.Submit(p,0);a.Tick(0);a.Submit(p,0);a.Tick(0);Assert.That(e.Committed.Count,Is.EqualTo(1));
        for(int i=2;i<1050;i++){a.Submit(Proposal(i,energy:0),0);a.Tick(0);}
        Assert.That(a.Submit(p,0),Is.False);Assert.That(e.Committed.Count,Is.EqualTo(1049));
    }
    [Test] public void EmissionChange_CancelsPendingSupport()
    {
        var e=new Executor();var a=new MechActionArbiter(e);
        a.Submit(Proposal(1,guidance:1,emission:MechEmissionRequirement.DataLink),0);a.Tick(0);
        a.Emissions.Mode=MechEmissionMode.Silent;a.Tick(1);
        Assert.That(e.Cancelled,Is.EqualTo(1));Assert.That(e.Committed,Is.Empty);Assert.That(a.ReservedGuidanceUnits,Is.Zero);
    }
    [TestCase(MechActionSource.Player,false)] [TestCase(MechActionSource.AI,false)] [TestCase(MechActionSource.AutomaticDefense,true)]
    public void SelfDefenseEmission_RequiresExplicitSourceAuthorization(MechActionSource source,bool allowed)
    {
        var e=new Executor { IsReady=true };var a=new MechActionArbiter(e);
        a.Emissions.Mode=MechEmissionMode.AuthorizedSelfDefense;a.Emissions.AllowAutomaticDefense=true;
        a.Submit(Proposal(1,source:source,emission:MechEmissionRequirement.Radar),0);a.Tick(0);
        Assert.That(e.Committed.Count,Is.EqualTo(allowed?1:0));
    }
    [Test] public void ExpiredAndInvalidatedActions_ReleaseTheirReservations()
    {
        var e=new Executor();var a=new MechActionArbiter(e);var p=Proposal(1,deadline:1);
        a.Submit(p,0);a.Tick(0);a.Tick(2);a.TryGetFeedback(p.Id,out var f);Assert.That(f.Phase,Is.EqualTo(MechActionPhase.Expired));
        a.Submit(Proposal(2),2);a.Tick(2);e.Valid=false;a.Tick(3);Assert.That(a.ActiveCount,Is.Zero);Assert.That(a.ReservedEnergyMj,Is.Zero);
    }
    [TestCase(true)] [TestCase(false)] public void ExecutorExceptions_ReleaseEverything(bool begin)
    {
        var e=new Executor { IsReady=true,ThrowBegin=begin,ThrowCommit=!begin };var a=new MechActionArbiter(e);
        a.Submit(Proposal(1,guidance:1),0);Assert.DoesNotThrow(()=>a.Tick(0));Assert.That(a.ActiveCount,Is.Zero);
        Assert.That(a.ReservedGuidanceUnits,Is.Zero);Assert.That(a.ReservedEnergyMj,Is.Zero);Assert.That(e.Committed,Is.Empty);
    }
    [Test] public void ResourceChangeDuringDeployment_RechecksBeforeCommit()
    {
        var e=new Executor();var a=new MechActionArbiter(e);a.Submit(Proposal(1,energy:5),0);a.Tick(0);
        e.Energy=1;e.IsReady=true;a.Tick(1);Assert.That(e.Committed,Is.Empty);Assert.That(a.ReservedEnergyMj,Is.Zero);
    }
    [Test] public void FailedBundle_DoesNotHoldHalfTheResources()
    {
        var e=new Executor { Guidance=0 };var a=new MechActionArbiter(e);a.Submit(Proposal(1,energy:5,guidance:1),0);a.Tick(0);
        Assert.That(e.Began,Is.Zero);Assert.That(a.ReservedEnergyMj,Is.Zero);
    }
    [TestCase(float.NaN)] [TestCase(float.PositiveInfinity)] [TestCase(-1f)]
    public void InvalidResourceAmounts_AreRejected(float value)
    {var e=new Executor();var a=new MechActionArbiter(e);Assert.That(a.Submit(Proposal(1,energy:value),0),Is.False);Assert.That(a.ActiveCount,Is.Zero);}
    [Test] public void PlayerAndAI_HaveEquivalentExecutionTraces()
    {
        var traces=new List<string[]>();
        foreach(var source in new[]{MechActionSource.Player,MechActionSource.AI})
        {
            var e=new Executor { IsReady=true };var a=new MechActionArbiter(e);a.Submit(Proposal(1,source:source),0);a.Tick(0);
            var events=new List<MechActionFeedback>();a.CopyEvents(events);traces.Add(events.Select(x=>x.Phase+":"+x.Reason).ToArray());
        }
        CollectionAssert.AreEqual(traces[0],traces[1]);
    }
}
