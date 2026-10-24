using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;

namespace ACunicombat.Mechs
{
    public enum MechActionSource { Player, AI, AutomaticDefense }
    public enum MechActionKind { Fire, Reload }
    public enum MechActionPhase { Proposed, Reserved, Running, Cancelling, Succeeded, Failed, Cancelled, Expired }
    [Flags] public enum MechEmissionRequirement { None = 0, Radar = 1, DataLink = 2 }
    public enum MechEmissionMode { Unrestricted, Silent, AuthorizedSelfDefense }

    public readonly struct MechActionId : IEquatable<MechActionId>
    {
        public readonly MechActionSource Source;
        public readonly long Sequence;
        public MechActionId(MechActionSource source, long sequence) { Source = source; Sequence = sequence; }
        public bool Equals(MechActionId other) => Source == other.Source && Sequence == other.Sequence;
        public override bool Equals(object obj) => obj is MechActionId other && Equals(other);
        public override int GetHashCode() => unchecked((int)Source * 397 ^ Sequence.GetHashCode());
    }
    public sealed class MechActionProposal
    {
        public readonly MechActionId Id;
        public readonly Guid InstanceId;
        public readonly MechEquipmentSlot Slot;
        public readonly MechActionKind Kind;
        public readonly ObserverTrackRef Target;
        public readonly double Deadline;
        public readonly float EnergyMj;
        public readonly int GuidanceUnits;
        public readonly MechEmissionRequirement Emissions;
        public MechActionProposal(MechActionId id, Guid instanceId, MechEquipmentSlot slot, MechActionKind kind,
            ObserverTrackRef target, double deadline, float energyMj = 0, int guidanceUnits = 0,
            MechEmissionRequirement emissions = MechEmissionRequirement.None)
        { Id = id; InstanceId = instanceId; Slot = slot; Kind = kind; Target = target; Deadline = deadline;
          EnergyMj = energyMj; GuidanceUnits = guidanceUnits; Emissions = emissions; }
    }
    public readonly struct MechActionBudget
    {
        public readonly float EnergyMj;
        public readonly int GuidanceUnits;
        public MechActionBudget(float energyMj, int guidanceUnits) { EnergyMj = energyMj; GuidanceUnits = guidanceUnits; }
    }
    public readonly struct MechActionFeedback
    {
        public readonly MechActionId Id;
        public readonly Guid InstanceId;
        public readonly string SlotId, Reason;
        public readonly MechActionPhase Phase;
        public readonly MechActionKind Kind;
        public readonly double Time;
        public MechActionFeedback(MechActionProposal p, MechActionPhase phase, double time, string reason)
        { Id = p.Id; Kind = p.Kind; InstanceId = p.InstanceId; SlotId = p.Slot.Id; Phase = phase; Time = time; Reason = reason; }
    }
    public interface IMechActionExecutor
    {
        MechActionBudget AvailableBudget { get; }
        bool Check(MechActionProposal proposal, out string reason);
        bool Begin(MechActionProposal proposal, out string reason);
        bool Ready(MechActionProposal proposal);
        bool Commit(MechActionProposal proposal, out string reason);
        void Cancel(MechActionProposal proposal);
    }
    public sealed class MechEmissionArbiter
    {
        public MechEmissionMode Mode { get; set; }
        public bool AllowAutomaticDefense { get; set; }
        public bool Allows(MechActionProposal proposal) => proposal.Emissions == MechEmissionRequirement.None ||
            Mode == MechEmissionMode.Unrestricted || (Mode == MechEmissionMode.AuthorizedSelfDefense &&
            AllowAutomaticDefense && proposal.Id.Source == MechActionSource.AutomaticDefense);
    }
    // Single-threaded command phase: snapshot the queue, sort deterministically, reserve complete bundles.
    // Sequence high-water marks survive receipt eviction, so a repeated old command cannot execute twice.
    public sealed class MechActionArbiter
    {
        private sealed class Execution
        {
            public MechActionProposal Proposal;
            public MechActionPhase Phase;
            public bool Reserved;
        }
        private readonly IMechActionExecutor executor;
        private readonly List<Execution> queued = new List<Execution>();
        private readonly List<Execution> running = new List<Execution>();
        private readonly Dictionary<MechActionId, MechActionFeedback> receipts = new Dictionary<MechActionId, MechActionFeedback>();
        private readonly Queue<MechActionId> terminalIds = new Queue<MechActionId>();
        private readonly Dictionary<MechActionSource, long> highWater = new Dictionary<MechActionSource, long>();
        private readonly HashSet<Guid> leasedEquipment = new HashSet<Guid>();
        private readonly List<MechActionFeedback> events = new List<MechActionFeedback>();
        private float reservedEnergy;
        private int reservedGuidance;
        private bool ticking;
        public readonly MechEmissionArbiter Emissions = new MechEmissionArbiter();
        public int ActiveCount => queued.Count + running.Count;
        public float ReservedEnergyMj => reservedEnergy;
        public int ReservedGuidanceUnits => reservedGuidance;
        public MechActionArbiter(IMechActionExecutor executor) { this.executor = executor ?? throw new ArgumentNullException(nameof(executor)); }
        public bool Submit(MechActionProposal proposal, double now)
        {
            if (proposal == null || double.IsNaN(now) || double.IsInfinity(now) || !Enum.IsDefined(typeof(MechActionSource), proposal.Id.Source)) return false;
            if (receipts.TryGetValue(proposal.Id, out var existing)) return existing.Phase <= MechActionPhase.Running || existing.Phase == MechActionPhase.Succeeded;
            highWater.TryGetValue(proposal.Id.Source, out var previous);
            if (proposal.Id.Sequence <= previous || proposal.Id.Sequence <= 0) return false;
            highWater[proposal.Id.Source] = proposal.Id.Sequence;
            var execution = new Execution { Proposal = proposal };
            if (ActiveCount >= 256 || proposal.InstanceId == Guid.Empty || string.IsNullOrEmpty(proposal.Slot.Id) ||
                float.IsNaN(proposal.EnergyMj) || float.IsInfinity(proposal.EnergyMj) || proposal.EnergyMj < 0 ||
                proposal.GuidanceUnits < 0 || double.IsNaN(proposal.Deadline) || double.IsInfinity(proposal.Deadline) || proposal.Deadline <= now)
            { Finish(execution, MechActionPhase.Failed, now, "InvalidOrExpiredRequest"); return false; }
            queued.Add(execution); Report(execution, MechActionPhase.Proposed, now, "Queued"); return true;
        }
        public bool TryGetFeedback(MechActionId id, out MechActionFeedback feedback) => receipts.TryGetValue(id, out feedback);
        public void CopyEvents(List<MechActionFeedback> output) { output.Clear(); output.AddRange(events); }
        public bool TryGetPending(Guid instanceId, out MechActionId id)
        {
            foreach (var e in running) if (e.Proposal.InstanceId == instanceId) { id = e.Proposal.Id; return true; }
            foreach (var e in queued) if (e.Proposal.InstanceId == instanceId) { id = e.Proposal.Id; return true; }
            id = default; return false;
        }
        public bool HasPending(Guid instanceId)
        {
            foreach (var e in queued) if (e.Proposal.InstanceId == instanceId) return true;
            foreach (var e in running) if (e.Proposal.InstanceId == instanceId) return true;
            return false;
        }
        private bool CanContinue(Execution e, double now, out string reason)
        {
            if (e.Proposal.Deadline <= now) { reason = "DeadlineExpired"; return false; }
            if (!Emissions.Allows(e.Proposal)) { reason = "EmissionDenied"; return false; }
            try { return executor.Check(e.Proposal, out reason); }
            catch (Exception error) { reason = "CheckException:" + error.GetType().Name; return false; }
        }
        public void Tick(double now,bool automaticDefenseOnly=false)
        {
            if (ticking || double.IsNaN(now) || double.IsInfinity(now)) return;
            ticking = true;
            try
            {
                foreach (var e in running.ToArray())
                    if(!automaticDefenseOnly||e.Proposal.Id.Source==MechActionSource.AutomaticDefense)Advance(e, now);
                var batch = automaticDefenseOnly
                    ? queued.FindAll(e=>e.Proposal.Id.Source==MechActionSource.AutomaticDefense).ToArray()
                    : queued.ToArray();
                if(automaticDefenseOnly)queued.RemoveAll(e=>e.Proposal.Id.Source==MechActionSource.AutomaticDefense);else queued.Clear();
                Array.Sort(batch, (a,b) =>
                {
                    int ap = a.Proposal.Id.Source == MechActionSource.AutomaticDefense ? 0 : 1;
                    int bp = b.Proposal.Id.Source == MechActionSource.AutomaticDefense ? 0 : 1;
                    int c = ap.CompareTo(bp); if (c != 0) return c;
                    c = a.Proposal.Deadline.CompareTo(b.Proposal.Deadline); if (c != 0) return c;
                    c = string.CompareOrdinal(a.Proposal.Slot.Id,b.Proposal.Slot.Id); if (c != 0) return c;
                    c = a.Proposal.Id.Source.CompareTo(b.Proposal.Id.Source); return c != 0 ? c : a.Proposal.Id.Sequence.CompareTo(b.Proposal.Id.Sequence);
                });
                foreach (var e in batch)
                {
                    if (!CanContinue(e, now, out var reason)) { Finish(e, e.Proposal.Deadline <= now ? MechActionPhase.Expired : MechActionPhase.Failed, now, reason); continue; }
                    var budget = executor.AvailableBudget;
                    if (leasedEquipment.Contains(e.Proposal.InstanceId) || budget.EnergyMj - reservedEnergy + 0.00001f < e.Proposal.EnergyMj ||
                        budget.GuidanceUnits - reservedGuidance < e.Proposal.GuidanceUnits)
                    { Finish(e, MechActionPhase.Failed, now, "ResourceConflict"); continue; }
                    leasedEquipment.Add(e.Proposal.InstanceId); reservedEnergy += e.Proposal.EnergyMj; reservedGuidance += e.Proposal.GuidanceUnits;
                    e.Reserved = true; Report(e, MechActionPhase.Reserved, now, "Reserved");
                    bool begun;
                    try { begun = executor.Begin(e.Proposal, out reason); }
                    catch (Exception error) { begun = false; reason = "BeginException:" + error.GetType().Name; }
                    if (!begun) { Finish(e, MechActionPhase.Failed, now, reason); continue; }
                    running.Add(e); Report(e, MechActionPhase.Running, now, "Running"); Advance(e, now);
                }
            }
            finally { ticking = false; }
        }
        private void Advance(Execution e, double now)
        {
            if (!CanContinue(e, now, out var reason))
            { Finish(e, e.Proposal.Deadline <= now ? MechActionPhase.Expired : MechActionPhase.Failed, now, reason); return; }
            try { if (!executor.Ready(e.Proposal)) return; }
            catch (Exception error) { Finish(e, MechActionPhase.Failed, now, "ReadyException:" + error.GetType().Name); return; }
            var budget = executor.AvailableBudget;
            if (budget.EnergyMj + 0.00001f < e.Proposal.EnergyMj || budget.GuidanceUnits < e.Proposal.GuidanceUnits)
            { Finish(e, MechActionPhase.Failed, now, "ResourcesChanged"); return; }
            // Commit is the only irreversible boundary. Executor rechecks physical/fire-control conditions.
            bool committed;
            try { committed = executor.Commit(e.Proposal, out reason); }
            catch (Exception error) { committed = false; reason = "CommitException:" + error.GetType().Name; }
            Finish(e, committed ? MechActionPhase.Succeeded : MechActionPhase.Failed, now, reason);
        }
        public bool Cancel(MechActionId id, double now)
        {
            var e = queued.Find(x => x.Proposal.Id.Equals(id)) ?? running.Find(x => x.Proposal.Id.Equals(id));
            if (e == null) return false;
            Report(e, MechActionPhase.Cancelling, now, "CancellationRequested"); Finish(e, MechActionPhase.Cancelled, now, "Cancelled"); return true;
        }
        public void CancelAll(double now)
        {
            foreach (var e in queued.ToArray()) Cancel(e.Proposal.Id, now);
            foreach (var e in running.ToArray()) Cancel(e.Proposal.Id, now);
        }
        private void Finish(Execution e, MechActionPhase phase, double now, string reason)
        {
            if (e.Reserved)
            {
                if (phase != MechActionPhase.Succeeded)
                    try { executor.Cancel(e.Proposal); } catch (Exception error) { reason += ";CancelException:" + error.GetType().Name; }
                leasedEquipment.Remove(e.Proposal.InstanceId);
                reservedEnergy = Math.Max(0, reservedEnergy - e.Proposal.EnergyMj);
                reservedGuidance = Math.Max(0, reservedGuidance - e.Proposal.GuidanceUnits); e.Reserved = false;
            }
            else if (phase == MechActionPhase.Cancelled)
                try { executor.Cancel(e.Proposal); } catch (Exception error) { reason += ";CancelException:" + error.GetType().Name; }
            queued.Remove(e); running.Remove(e); Report(e, phase, now, reason);
            terminalIds.Enqueue(e.Proposal.Id);
            while (terminalIds.Count > 1024) receipts.Remove(terminalIds.Dequeue());
        }
        private void Report(Execution e, MechActionPhase phase, double now, string reason)
        {
            e.Phase = phase; var feedback = new MechActionFeedback(e.Proposal, phase, now, reason);
            receipts[e.Proposal.Id] = feedback; if (events.Count >= 2048) events.RemoveAt(0); events.Add(feedback);
        }
    }
}
