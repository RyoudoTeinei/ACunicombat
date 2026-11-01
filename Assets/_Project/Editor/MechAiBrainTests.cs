using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public sealed class MechAiBrainTests
{
    private sealed class Probe : MechAiState
    {
        public int Entered, Exited;
        public Probe(int priority, float dwell = 0f) : base(priority, dwell) { }
        public override void Enter(float now) { base.Enter(now); Entered++; }
        public override void Exit(float now) => Exited++;
        public void Finish() => IsFinished = true;
    }

    [Test]
    public void AnyTransition_PreemptsOnlyLowerPriorityStates()
    {
        var fsm = new MechAiStateMachine();
        var search = new Probe(9); var engage = new Probe(3); var evade = new Probe(1);
        bool threat = false, target = false;
        fsm.AddAny(evade, () => threat, interrupt: true);
        fsm.AddAny(engage, () => target);
        fsm.Start(search, 0f);

        target = true; fsm.Tick(0.1f);
        Assert.That(fsm.Current, Is.SameAs(engage));
        threat = true; fsm.Tick(0.2f);
        Assert.That(fsm.Current, Is.SameAs(evade));
        // Engage (3) must not preempt Evade (1) even though its condition holds.
        fsm.Tick(0.3f);
        Assert.That(fsm.Current, Is.SameAs(evade));
        Assert.That(fsm.Interrupted, Is.SameAs(engage), "Evade should remember the displaced base state");
    }

    [Test]
    public void OrdinaryTransition_WaitsForMinimumDwell_UnlessFinished()
    {
        var fsm = new MechAiStateMachine();
        var engage = new Probe(3, 1f); var search = new Probe(9);
        bool lost = false;
        fsm.Add(engage, search, () => lost);
        fsm.Start(engage, 0f);
        lost = true;
        fsm.Tick(0.5f);
        Assert.That(fsm.Current, Is.SameAs(engage), "Left before minimum dwell");
        fsm.Tick(1.01f);
        Assert.That(fsm.Current, Is.SameAs(search));

        fsm.Start(engage, 2f); engage.Finish();
        fsm.Tick(2.1f);
        Assert.That(fsm.Current, Is.SameAs(search), "A finished state may leave immediately");
    }

    [Test]
    public void BasePreemption_RespectsDwell_InterruptsDoNot()
    {
        // Real-scene regression: Engage->Navigate->Engage within 0.02 s.
        var fsm = new MechAiStateMachine();
        var engage = new Probe(3, 0.6f); var navigate = new Probe(6, 0.4f); var evade = new Probe(1);
        bool target = true, navReq = false, threat = false;
        fsm.AddAny(engage, () => target && !navReq);
        fsm.Add(engage, navigate, () => navReq);
        fsm.AddAny(evade, () => threat, interrupt: true);
        fsm.Start(engage, 0f);
        navReq = true; fsm.Tick(1f);
        Assert.That(fsm.Current, Is.SameAs(navigate));
        navReq = false; fsm.Tick(1.02f);
        Assert.That(fsm.Current, Is.SameAs(navigate), "Engage must not re-preempt inside Navigate's dwell");
        fsm.Tick(1.41f);
        Assert.That(fsm.Current, Is.SameAs(engage));
        threat = true; fsm.Tick(1.42f);
        Assert.That(fsm.Current, Is.SameAs(evade), "Interrupts ignore dwell");
    }

    [Test]
    public void Switch_CallsExitThenEnter_AndCountsTransitions()
    {
        var fsm = new MechAiStateMachine();
        var a = new Probe(9); var b = new Probe(3);
        fsm.AddAny(b, () => true);
        fsm.Start(a, 0f);
        fsm.Tick(0f);
        Assert.That(a.Exited, Is.EqualTo(1));
        Assert.That(b.Entered, Is.EqualTo(1));
        Assert.That(fsm.TransitionCount, Is.EqualTo(1));
        Assert.That(fsm.Previous, Is.SameAs(a));
    }

    private static MechAiEvasionPlan Evaluate(float tti, float missileSpeed, Vector3 ownVelocity,
        float escapeSpeed = 200f, float lateralAcceleration = 40f, bool evading = false)
    {
        // Missile 3 km north of us, flying south toward us.
        var missile = new MechAiMissileThreat(new Vector3(0, 0, 3000), new Vector3(0, 0, -missileSpeed), tti);
        return MechAiEvasionDoctrine.Evaluate(in missile, Vector3.zero, ownVelocity, Vector3.up,
            escapeSpeed, lateralAcceleration, evading);
    }

    [Test]
    public void Evasion_WaitsUntilTimeToImpactNeedsTheManeuver()
    {
        var early = Evaluate(tti: 12f, missileSpeed: 900f, ownVelocity: Vector3.zero);
        Assert.That(early.Active, Is.False, "Fast missile far away must not trigger premature evasion");
        var due = Evaluate(tti: 3f, missileSpeed: 900f, ownVelocity: Vector3.zero);
        Assert.That(due.Mode, Is.EqualTo(MechAiEvasionMode.Beam));
        Assert.That(Mathf.Abs(Vector3.Dot(due.Direction, Vector3.forward)), Is.LessThan(0.01f),
            "Beam direction must be perpendicular to the missile velocity");
    }

    [Test]
    public void Evasion_DragsSlowMissileCold_AwayFromIt()
    {
        var plan = Evaluate(tti: 8f, missileSpeed: 260f, ownVelocity: Vector3.zero, escapeSpeed: 200f, lateralAcceleration: 30f);
        Assert.That(plan.Mode, Is.EqualTo(MechAiEvasionMode.Cold));
        Assert.That(Vector3.Dot(plan.Direction, Vector3.back), Is.GreaterThan(0.99f));
    }

    [Test]
    public void Evasion_BeamsOnTheSideAlreadyMovedToward()
    {
        // Already sliding left at 60 m/s: only 0.5 s more is needed, so it beams late but on that side.
        var waiting = Evaluate(tti: 2.5f, missileSpeed: 900f, ownVelocity: new Vector3(-60f, 0, 0));
        Assert.That(waiting.Active, Is.False, "Existing lateral speed should delay the beam");
        var plan = Evaluate(tti: 1.8f, missileSpeed: 900f, ownVelocity: new Vector3(-60f, 0, 0));
        Assert.That(plan.Mode, Is.EqualTo(MechAiEvasionMode.Beam));
        Assert.That(plan.Direction.x, Is.LessThan(-0.99f));
    }

    [Test]
    public void Evasion_TerminalPhaseBoostsAndReleasesCountermeasures()
    {
        var plan = Evaluate(tti: 1f, missileSpeed: 900f, ownVelocity: Vector3.zero);
        Assert.That(plan.Mode, Is.EqualTo(MechAiEvasionMode.Terminal));
        Assert.That(plan.QuickBoost, Is.True);
        Assert.That(plan.ReleaseCountermeasures, Is.True);
    }

    [Test]
    public void Evasion_OnceStarted_DoesNotFlickerOffBeforeImpact()
    {
        var plan = Evaluate(tti: 6f, missileSpeed: 900f, ownVelocity: new Vector3(120f, 0, 0), evading: true);
        Assert.That(plan.Active, Is.True);
    }
}
