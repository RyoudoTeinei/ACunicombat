using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEngine;

public sealed class MechAiTacticalManeuverTests
{
    [TestCase(MechPartSlot.LeftBackWeapon, 1f)]
    [TestCase(MechPartSlot.RightBackWeapon, -1f)]
    public void ShoulderMissile_BreaksAwayFromFiringShoulder(
        MechPartSlot slot,
        float expectedSide)
    {
        var planner = new MechAiManeuverPlanner();
        var accepted = planner.NotifyWeaponCommitted(
            MechWeaponRole.AirToAirMissile,
            slot,
            Quaternion.identity,
            Vector3.forward,
            10f);

        Assert.That(accepted, Is.True);
        Assert.That(planner.PostLaunchDirection.x * expectedSide, Is.GreaterThan(0.99f));
        Assert.That(Mathf.Abs(planner.PostLaunchDirection.z), Is.LessThan(0.001f));
        var command = Resolve(planner, 10f);
        Assert.That(command.State, Is.EqualTo(MechAiManeuverState.PostLaunchBreak));
        Assert.That(command.AccelerationWorld.x * expectedSide, Is.GreaterThan(0.1f));
        Assert.That(command.AttitudeIntent, Is.EqualTo(MechAiAttitudeIntent.TrackTarget));
    }

    [Test]
    public void ShoulderBreak_UsesLogicalBodyAxesNotWorldAxes()
    {
        var planner = new MechAiManeuverPlanner();
        var rotation = Quaternion.Euler(35f, 70f, 20f);
        planner.NotifyWeaponCommitted(
            MechWeaponRole.AirToAirMissile,
            MechPartSlot.LeftBackWeapon,
            rotation,
            rotation * Vector3.forward,
            0f);

        Assert.That(Vector3.Distance(
            planner.PostLaunchDirection,
            rotation * Vector3.right), Is.LessThan(0.001f));
    }

    [TestCase(MechWeaponRole.AutomaticCannon, MechPartSlot.LeftHandWeapon)]
    [TestCase(MechWeaponRole.HandCannon, MechPartSlot.RightHandWeapon)]
    [TestCase(MechWeaponRole.InterceptorMissile, MechPartSlot.LeftBackWeapon)]
    public void NonOffensiveShoulderMissile_DoesNotTriggerPostLaunchBreak(
        MechWeaponRole role,
        MechPartSlot slot)
    {
        var planner = new MechAiManeuverPlanner();
        Assert.That(planner.NotifyWeaponCommitted(
            role, slot, Quaternion.identity, Vector3.forward, 0f), Is.False);
        Assert.That(Resolve(planner, 0f).State,
            Is.Not.EqualTo(MechAiManeuverState.PostLaunchBreak));
    }

    [Test]
    public void SameSalvo_DoesNotRefreshBreakAndLaterShotCanRetrigger()
    {
        var planner = new MechAiManeuverPlanner();
        Assert.That(NotifyLeft(planner, 5f), Is.True);
        Assert.That(NotifyLeft(planner, 5.4f), Is.False);
        Assert.That(planner.PostLaunchRemainingSeconds,
            Is.EqualTo(MechAiManeuverPlanner.PostLaunchDurationSeconds - 0.4f).Within(0.001f));
        Assert.That(NotifyLeft(planner, 5.4f + MechAiManeuverPlanner.PostLaunchRetriggerSeconds),
            Is.True);
    }

    [Test]
    public void ExpiredBreak_ReturnsToWeaponEngagement()
    {
        var planner = new MechAiManeuverPlanner();
        NotifyLeft(planner, 0f);
        var command = Resolve(
            planner,
            MechAiManeuverPlanner.PostLaunchDurationSeconds + 0.05f);
        Assert.That(command.State, Is.EqualTo(MechAiManeuverState.FiringRun));
    }

    [Test]
    public void IncomingThreat_ImmediatelyPreemptsPostLaunchBreak()
    {
        var planner = new MechAiManeuverPlanner();
        NotifyLeft(planner, 0f);
        var command = Resolve(
            planner,
            0.1f,
            MechAiTargetController.CombatState.Defensive);
        Assert.That(command.State, Is.EqualTo(MechAiManeuverState.Defensive));
        Assert.That(command.AttitudeIntent,
            Is.EqualTo(MechAiAttitudeIntent.EmergencyManeuver));
    }

    [Test]
    public void FiringRun_HasCommitmentBeforeReturningToObliqueEngagement()
    {
        var planner = new MechAiManeuverPlanner();
        Assert.That(Resolve(planner, 0f, weaponReady: true).State,
            Is.EqualTo(MechAiManeuverState.FiringRun));
        Assert.That(Resolve(planner, 0.5f, weaponReady: false).State,
            Is.EqualTo(MechAiManeuverState.FiringRun));
        Assert.That(Resolve(planner, 1.5f, weaponReady: false).State,
            Is.EqualTo(MechAiManeuverState.ObliqueEngage));
    }

    [Test]
    public void PrecisionFiringRun_ReducesUnnecessaryOrbitAcceleration()
    {
        var precisionPlanner = new MechAiManeuverPlanner();
        var generalPlanner = new MechAiManeuverPlanner();
        var precision = Resolve(
            precisionPlanner, 0f,
            role: MechAiTacticalWeaponRole.PrecisionGun);
        var general = Resolve(
            generalPlanner, 0f,
            role: MechAiTacticalWeaponRole.GeneralAutomatic);

        Assert.That(Mathf.Abs(precision.AccelerationWorld.x),
            Is.LessThan(Mathf.Abs(general.AccelerationWorld.x) * 0.55f));
    }

    [Test]
    public void NoOffensiveWeapon_OrLowEnergy_UsesRecoverInsteadOfPressing()
    {
        var unarmed = new MechAiManeuverPlanner();
        Assert.That(Resolve(unarmed, 0f, hasWeapon: false).State,
            Is.EqualTo(MechAiManeuverState.Recover));
        var depleted = new MechAiManeuverPlanner();
        Assert.That(Resolve(depleted, 0f, energy: 0.1f).State,
            Is.EqualTo(MechAiManeuverState.Recover));
    }

    [Test]
    public void ReconExplicitAttack_OverridesPassiveObservationPosture()
    {
        var planner = new MechAiManeuverPlanner();
        Assert.That(Resolve(
            planner, 0f,
            taskGroup: FleetTaskGroup.Reconnaissance,
            hasTask: true).State,
            Is.EqualTo(MechAiManeuverState.FiringRun));
    }

    [Test]
    public void CloseFastPass_CombinesBrakingWithLateralEscape()
    {
        var planner = new MechAiManeuverPlanner();
        var doctrine = Doctrine();
        var command = planner.Resolve(
            Vector3.forward * 900f,
            Vector3.back * 240f,
            Vector3.right,
            MechAiTargetController.CombatState.Engage,
            in doctrine,
            FleetTaskGroup.AirSuperiority,
            false,
            false,
            true,
            true,
            1f,
            20f,
            20f,
            30f,
            2f,
            0f);

        Assert.That(command.UrgentBraking, Is.True);
        Assert.That(command.AccelerationWorld.z, Is.LessThan(0f));
        Assert.That(Mathf.Abs(command.AccelerationWorld.x), Is.GreaterThan(0.1f));
    }

    [Test]
    public void HighAngularRateEngagement_LeadsAcrossTheObservedTrack()
    {
        var planner = new MechAiManeuverPlanner();
        var doctrine = Doctrine();
        var command = planner.Resolve(
            Vector3.forward * 1400f,
            Vector3.right * 240f,
            Vector3.right,
            MechAiTargetController.CombatState.Engage,
            in doctrine,
            FleetTaskGroup.AirSuperiority,
            false,
            false,
            true,
            true,
            1f,
            20f,
            20f,
            30f,
            2f,
            0f);

        Assert.That(command.State, Is.EqualTo(MechAiManeuverState.FiringRun));
        Assert.That(command.AccelerationWorld.x, Is.GreaterThan(1f));
        Assert.That(command.AttitudeIntent, Is.EqualTo(MechAiAttitudeIntent.LeadWeapon));
    }

    private static bool NotifyLeft(MechAiManeuverPlanner planner, float now) =>
        planner.NotifyWeaponCommitted(
            MechWeaponRole.AirToAirMissile,
            MechPartSlot.LeftBackWeapon,
            Quaternion.identity,
            Vector3.forward,
            now);

    private static MechAiManeuverCommand Resolve(
        MechAiManeuverPlanner planner,
        float now,
        MechAiTargetController.CombatState state = MechAiTargetController.CombatState.Engage,
        MechAiTacticalWeaponRole role = MechAiTacticalWeaponRole.GeneralAutomatic,
        bool hasWeapon = true,
        bool weaponReady = true,
        float energy = 1f,
        FleetTaskGroup taskGroup = FleetTaskGroup.AirSuperiority,
        bool hasTask = false)
    {
        var doctrine = Doctrine(role);
        return planner.Resolve(
            Vector3.forward * 1500f,
            Vector3.zero,
            Vector3.right,
            state,
            in doctrine,
            taskGroup,
            hasTask,
            false,
            hasWeapon,
            weaponReady,
            energy,
            50f,
            50f,
            100f,
            2f,
            now);
    }

    private static MechAiCombatDoctrine Doctrine(
        MechAiTacticalWeaponRole role = MechAiTacticalWeaponRole.GeneralAutomatic) =>
        new MechAiCombatDoctrine(
            role,
            new MechAiEngagementBand(1000f, 1500f, 2000f),
            150f);
}
