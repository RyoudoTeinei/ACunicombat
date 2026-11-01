using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;

public sealed class MechAiSpatialControlTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private GameObject ownship;
    private MechFlightMotionController motion;
    private MechAiTargetController ai;

    [SetUp]
    public void SetUp()
    {
        // A real six-axis nozzle fixture without a loadout, radar, model or AI
        // Awake side effects. Each axis has 1 kN available for a 1000 kg body.
        ownship = new GameObject("AI spatial control fixture");
        ownship.SetActive(false);
        ownship.transform.position = Vector3.up * 10000f;
        motion = ownship.AddComponent<MechFlightMotionController>();
        ai = ownship.AddComponent<MechAiTargetController>();
        SetField(ai, "motion", motion);
        motion.ConfigureModelForwardYawOffset(0f);
        motion.ConfigureGravity(0f);
        motion.ConfigurePropulsionForceModel(false, false, 0f);
        motion.ConfigureManeuverEnergy(100f, 4f, 15f, 18f, 0f, 0.5f, 0.2f);
        SetField(motion, "fallbackMassKg", 1000f);
        SetField(motion, "fallbackMainThrustKn", 1f);
        SetField(motion, "fallbackLateralThrustKn", 1f);
        SetField(motion, "fallbackReverseThrustKn", 1f);
        SetField(motion, "verticalThrustKn", 1f);
        SetField(motion, "descentThrustFraction", 1f);
        SetField(motion, "shoulderLiftFraction", 0f);
        SetField(motion, "auxiliaryNozzleThrustKn", 1f);
        SetField(motion, "airCoastingDragAcceleration", 0f);
        SetField(motion, "fixedControlCenterLocal", Vector3.zero);
        AddNozzle("test.forward", Vector3.forward, MechThrusterRole.Main);
        AddNozzle("test.reverse", Vector3.back, MechThrusterRole.Reverse);
        AddNozzle("test.right", Vector3.right, MechThrusterRole.StrafePositive);
        AddNozzle("test.left", Vector3.left, MechThrusterRole.StrafeNegative);
        AddNozzle("test.up", Vector3.up, MechThrusterRole.LegLift);
        AddNozzle("test.down", Vector3.down, MechThrusterRole.Descent);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(ownship);
    }

    [TestCase(30f, 0f)]
    [TestCase(-30f, 0f)]
    [TestCase(80f, 0f)]
    [TestCase(-80f, 0f)]
    [TestCase(90f, 0f)]
    [TestCase(-90f, 0f)]
    [TestCase(0f, 180f)]
    [TestCase(30f, 160f)]
    public void Attitude_ReachesTargetElevationAndRearHemisphereInAnyReferenceFrame(
        float elevation, float yaw)
    {
        var localDirection = Quaternion.AngleAxis(yaw, Vector3.up) *
                             Quaternion.AngleAxis(-elevation, Vector3.right) * Vector3.forward;
        var references = new[] { Quaternion.identity, Quaternion.Euler(37f, 62f, 23f) };
        foreach (var reference in references)
        {
            motion.ResetReferencePlaneToRotation(reference);
            var targetDirection = reference * localDirection;

            SettleAttitude(targetDirection);

            Assert.That(Vector3.Angle(motion.LogicalRotation * Vector3.forward, targetDirection),
                Is.LessThan(0.2f));
            Assert.That(Quaternion.Angle(motion.ReferenceRotation, reference), Is.LessThan(0.01f),
                "Tracking the target must not replace the AI's independent navigation frame.");
        }
    }

    [TestCase(MechWeaponRole.AutomaticCannon, 7f)]
    [TestCase(MechWeaponRole.HandCannon, 9f)]
    [TestCase(MechWeaponRole.AirToAirMissile, 55f)]
    public void HighElevationAlignment_UsesTheExistingWeaponAngleLimits(
        MechWeaponRole role, float angleLimit)
    {
        var direction = Quaternion.AngleAxis(-80f, Vector3.right) * Vector3.forward;
        SettleAttitude(direction);
        var angle = Vector3.Angle(motion.LogicalRotation * Vector3.forward, direction);
        var weapon = Weapon(role);

        Assert.That(MechAiRulePlanner.IsEligibleForAirTarget(
            in weapon, 2000f, angle, 0.95f, 5f), Is.True);
        Assert.That(MechAiRulePlanner.IsEligibleForAirTarget(
            in weapon, 2000f, angleLimit, 0.95f, 5f), Is.True);
        Assert.That(MechAiRulePlanner.IsEligibleForAirTarget(
            in weapon, 2000f, angleLimit + 0.1f, 0.95f, 5f), Is.False,
            "Fixing spatial pointing must not loosen the existing firing eligibility gate.");
    }

    [Test]
    public void SpatialApproachAndWithdrawal_FollowTheFullLineOfSightIncludingHeight()
    {
        var doctrine = Doctrine(0f);
        var directions = new[] { Vector3.up, Vector3.down, new Vector3(3f, 4f, -2f).normalized };
        foreach (var radial in directions)
        {
            var approach = SpatialAcceleration(radial * 5000f, Vector3.zero,
                Vector3.zero, MechAiTargetController.CombatState.Intercept, in doctrine, out _);
            var withdraw = SpatialAcceleration(radial * 500f, Vector3.zero,
                Vector3.zero, MechAiTargetController.CombatState.Extend, in doctrine, out _);

            Assert.That(Vector3.Dot(approach.normalized, radial), Is.GreaterThan(0.999f));
            Assert.That(Vector3.Dot(withdraw.normalized, radial), Is.LessThan(-0.999f));
            Assert.That(approach.magnitude, Is.InRange(0.1f, 30.001f));
            Assert.That(withdraw.magnitude, Is.InRange(0.1f, 30.001f));
        }
    }

    [Test]
    public void PreferredRange_MatchesAllThreeRelativeVelocityComponents()
    {
        var doctrine = Doctrine(0f);
        var relativeVelocity = new Vector3(20f, -12f, 16f);
        var acceleration = SpatialAcceleration(Vector3.forward * 1500f, relativeVelocity,
            Vector3.zero, MechAiTargetController.CombatState.Engage, in doctrine, out _);
        AssertVector(acceleration, relativeVelocity / 2f, 0.001f);

        var rotation = Quaternion.Euler(30f, 60f, 20f);
        var rotated = SpatialAcceleration(rotation * (Vector3.forward * 1500f),
            rotation * relativeVelocity, Vector3.zero,
            MechAiTargetController.CombatState.Engage, in doctrine, out _);
        AssertVector(rotated, rotation * acceleration, 0.001f);
        var matched = SpatialAcceleration(Vector3.forward * 1500f, Vector3.zero,
            Vector3.zero, MechAiTargetController.CombatState.Engage, in doctrine, out _);
        AssertVector(matched, Vector3.zero, 0.001f);
    }

    [Test]
    public void FastClosing_BrakesBeforeOvershootingThePreferredRange()
    {
        var doctrine = Doctrine(0f);
        var relativeVelocity = Vector3.back * 200f;
        var far = SpatialAcceleration(Vector3.forward * 5000f, relativeVelocity,
            Vector3.zero, MechAiTargetController.CombatState.Intercept, in doctrine, out var farBrake);
        var near = SpatialAcceleration(Vector3.forward * 1900f, relativeVelocity,
            Vector3.zero, MechAiTargetController.CombatState.Intercept, in doctrine, out var nearBrake);

        Assert.That(farBrake, Is.False);
        Assert.That(far.z, Is.GreaterThan(0f));
        Assert.That(nearBrake, Is.True);
        Assert.That(near.z, Is.LessThan(0f));
        Assert.That(near.magnitude, Is.LessThanOrEqualTo(30.001f));
    }

    [Test]
    public void OrbitControl_BuildsTangentialVelocityAndSuppliesCentripetalAcceleration()
    {
        var doctrine = Doctrine(100f);
        var buildingOrbit = SpatialAcceleration(Vector3.forward * 1500f, Vector3.zero,
            Vector3.right, MechAiTargetController.CombatState.Engage, in doctrine, out _);
        Assert.That(buildingOrbit.x, Is.GreaterThan(0f));
        Assert.That(Mathf.Abs(buildingOrbit.z), Is.LessThan(0.001f));

        // The ownship already moves right at the desired orbit speed, so only
        // inward curvature, not more lateral acceleration, should be commanded.
        var establishedOrbit = SpatialAcceleration(Vector3.forward * 1500f, Vector3.left * 100f,
            Vector3.right, MechAiTargetController.CombatState.Engage, in doctrine, out _);
        Assert.That(establishedOrbit.z, Is.GreaterThan(0f));
        Assert.That(Mathf.Abs(establishedOrbit.x), Is.LessThan(0.001f));
        Assert.That(establishedOrbit.magnitude, Is.LessThanOrEqualTo(30.001f));
    }

    [Test]
    public void OrbitTangent_CrossesTheReferencePoleWithoutFlippingSides()
    {
        var tangent = Vector3.right;
        for (var degrees = 75; degrees <= 105; degrees++)
        {
            var radial = Quaternion.AngleAxis(-degrees, Vector3.right) * Vector3.forward;
            var next = MechAiRulePlanner.ResolveOrbitTangent(radial, tangent, Vector3.up);
            Assert.That(Vector3.Dot(next, tangent), Is.GreaterThan(0.999f));
            Assert.That(Mathf.Abs(Vector3.Dot(next, radial)), Is.LessThan(0.0001f));
            Assert.That(next.magnitude, Is.EqualTo(1f).Within(0.0001f));
            tangent = next;
        }
        var atPoleWithoutHistory = MechAiRulePlanner.ResolveOrbitTangent(
            Vector3.up, Vector3.zero, Vector3.up);
        Assert.That(atPoleWithoutHistory.magnitude, Is.EqualTo(1f).Within(0.0001f));
        Assert.That(Mathf.Abs(Vector3.Dot(atPoleWithoutHistory, Vector3.up)), Is.LessThan(0.0001f));
    }

    [Test]
    public void AiFlightFrame_ConnectsVerticalApproachAndWithdrawalToAutonomousInput()
    {
        var doctrine = Doctrine(0f);
        foreach (var radial in new[] { Vector3.up, Vector3.down })
        foreach (var state in new[]
        {
            MechAiTargetController.CombatState.Intercept,
            MechAiTargetController.CombatState.Extend,
        })
        {
            var approaching = state == MechAiTargetController.CombatState.Intercept;
            var arguments = new object[]
            {
                approaching ? 5000f : 500f, radial, Vector3.zero,
                state, doctrine, Vector3.zero,
            };

            var frame = (MechPlayerInputFrame)Invoke(ai, "BuildFlightFrame", arguments);

            Assert.That(frame.HasAutonomousFlightCommand, Is.True);
            var expectedDirection = approaching ? radial : -radial;
            Assert.That(Vector3.Dot(frame.AutonomousAccelerationWorld.normalized, expectedDirection),
                Is.GreaterThan(0.999f));
            Assert.That(frame.AutonomousAccelerationWorld.magnitude, Is.GreaterThan(0.1f));
            Assert.That(Vector3.Dot(((Vector3)arguments[5]).normalized, expectedDirection),
                Is.GreaterThan(0.999f));
            Assert.That(frame.BrakeHeld, Is.False,
                "Spatial braking must not fall back to the player's planar brake mode.");
        }
    }

    [Test]
    public void ReadyWeapon_KeepsTheTargetInViewDuringApproachAndWithdrawalUnlessBrakingIsUrgent()
    {
        var targetDirection = new Vector3(0.2f, 0.8f, 0.3f).normalized;
        var maneuverDirection = -targetDirection;
        SetField(ai, "urgentBraking", false);
        foreach (var state in new[]
        {
            MechAiTargetController.CombatState.Intercept,
            MechAiTargetController.CombatState.Extend,
        })
        {
            var pointing = (Vector3)Invoke(ai, "ResolveAttitudeDirection",
                targetDirection, maneuverDirection, state, true);
            AssertVector(pointing, targetDirection, 0.0001f);
        }
        SetField(ai, "urgentBraking", true);
        var emergencyPointing = (Vector3)Invoke(ai, "ResolveAttitudeDirection",
            targetDirection, maneuverDirection, MechAiTargetController.CombatState.Extend, true);
        AssertVector(emergencyPointing, maneuverDirection, 0.0001f);
    }

    [Test]
    public void AutonomousWorldAcceleration_UsesRealNozzleLimitsAndIntegratesVelocity()
    {
        motion.ResetReferencePlaneToRotation(Quaternion.Euler(30f, 50f, 20f));
        var commandDirection = motion.LogicalRotation * Vector3.up;
        var initialVelocity = new Vector3(3f, 4f, 5f);
        SetField(motion, "velocity", initialVelocity);
        var energyBefore = motion.CurrentManeuverEnergy;
        var input = AutonomousInput(commandDirection * 1000f);
        Assert.That(motion.EstimateTranslationAcceleration(commandDirection),
            Is.EqualTo(1f).Within(0.001f));

        motion.Tick(in input, 0.1f);

        Assert.That(Vector3.Dot(motion.CurrentControlAcceleration, commandDirection),
            Is.InRange(0.1f, 1.0001f));
        Assert.That(Vector3.Cross(motion.CurrentControlAcceleration, commandDirection).magnitude,
            Is.LessThan(0.001f), "The body's local-up component must survive the world-space command bridge.");
        AssertVector(motion.Velocity,
            initialVelocity + motion.CurrentControlForceNewtons / motion.CurrentMassKg * 0.1f, 0.0001f);
        Assert.That(motion.CurrentManeuverEnergy, Is.EqualTo(energyBefore));
        foreach (var nozzle in motion.ThrusterChannels)
            Assert.That(nozzle.CurrentForceNewtons.magnitude, Is.LessThanOrEqualTo(1000.1f));
    }

    [Test]
    public void PartialEnergy_DoesNotLimitOrdinaryAutonomousNozzleOutput()
    {
        // Ordinary thrust remains available independently of the burst reserve.
        SetField(motion, "currentManeuverEnergy", 0.2f);
        var input = AutonomousInput(Vector3.up * 1000f);

        motion.Tick(in input, 0.1f);

        Assert.That(motion.CurrentControlForceNewtons.y, Is.EqualTo(1000f).Within(0.1f));
        Assert.That(motion.Velocity.y, Is.EqualTo(0.1f).Within(0.0001f));
        Assert.That(motion.CurrentManeuverEnergy, Is.EqualTo(.2f));
    }

    [Test]
    public void DepletedEnergy_StillAllowsOrdinaryAutonomousThrust()
    {
        var velocity = new Vector3(3f, -4f, 5f);
        SetField(motion, "velocity", velocity);
        SetField(motion, "currentManeuverEnergy", 0f);
        SetField(motion, "maneuverEnergyDepleted", true);
        var input = AutonomousInput(Vector3.one * 1000f);

        motion.Tick(in input, 0.1f);

        Assert.That(motion.CurrentControlForceNewtons.magnitude, Is.GreaterThan(0));
        Assert.That((motion.Velocity-velocity).magnitude, Is.GreaterThan(0));
        Assert.That(motion.CurrentManeuverEnergy, Is.Zero);
    }

    [Test]
    public void DisabledEngine_PreventsAutonomousThrust()
    {
        motion.SetEngineAvailable(false);
        Assert.That(motion.EstimateTranslationAcceleration(Vector3.up), Is.Zero);
        var input = AutonomousInput(Vector3.one * 1000f);

        motion.Tick(in input, 0.1f);

        AssertVector(motion.CurrentControlForceNewtons, Vector3.zero, 0.001f);
        AssertVector(motion.Velocity, Vector3.zero, 0.001f);
    }

    [TestCase(1,0,false,false)]
    [TestCase(-1,0,false,false)]
    [TestCase(0,1,false,false)]
    [TestCase(0,-1,false,false)]
    [TestCase(0,0,true,false)]
    [TestCase(0,0,false,true)]
    public void ContinuousTranslation_RechargesAfterQuickBoost(int x,int y,bool rise,bool descend)
    {
        motion.ConfigureManeuverEnergy(100,40,15,18,24,.5f,.2f);
        Assert.That(motion.BeginQuickBoost(Vector2.up),Is.True);
        float afterBurst=motion.CurrentManeuverEnergy;
        var input=new MechPlayerInputFrame{MoveLocal=new Vector2(x,y),RiseHeld=rise,DescendHeld=descend};
        for(int i=0;i<4;i++)motion.Tick(in input,.1f);
        Assert.That(motion.CurrentManeuverEnergy,Is.EqualTo(afterBurst).Within(.001f));
        for(int i=0;i<30;i++)motion.Tick(in input,.1f);
        Assert.That(motion.CurrentManeuverEnergy,Is.EqualTo(100).Within(.001f));
        Assert.That(motion.Velocity.magnitude,Is.GreaterThan(0));
    }
    [Test] public void AssaultBoost_StillConsumesBurstReserve()
    {
        motion.TriggerAssaultBoost();
        var input=new MechPlayerInputFrame{MoveLocal=Vector2.up};
        motion.Tick(in input,.1f);
        Assert.That(motion.CurrentManeuverEnergy,Is.LessThan(100));
    }

    [Test]
    public void ManualTranslation_StillUsesNozzlesAndRetainsVelocityAfterRelease()
    {
        var input = new MechPlayerInputFrame { MoveLocal = Vector2.right };
        motion.Tick(in input, 0.1f);
        var velocity = motion.Velocity;
        Assert.That(velocity.x, Is.GreaterThan(0f));
        Assert.That(velocity.x, Is.LessThanOrEqualTo(0.1001f));
        var noInput = default(MechPlayerInputFrame);

        motion.Tick(in noInput, 0.1f);

        AssertVector(motion.Velocity, velocity, 0.0001f);
        AssertVector(motion.CurrentControlForceNewtons, Vector3.zero, 0.001f);
    }

    [Test]
    public void CombatNavigationYield_IsLimitedToExplicitNavigationTasks()
    {
        Assert.That(MechAiRulePlanner.ShouldYieldCombatToNavigation(
            false, false, false, false), Is.False);
        Assert.That(MechAiRulePlanner.ShouldYieldCombatToNavigation(
            true, false, false, false), Is.False);
        Assert.That(MechAiRulePlanner.ShouldYieldCombatToNavigation(
            true, true, false, false), Is.True);
        Assert.That(MechAiRulePlanner.ShouldYieldCombatToNavigation(
            true, false, true, false), Is.True);
        Assert.That(MechAiRulePlanner.ShouldYieldCombatToNavigation(
            true, false, false, true), Is.True);
    }

    [Test]
    public void Intercept_LeadsCrossingTargetInsteadOfOnlyMatchingItsVelocity()
    {
        var doctrine = Doctrine(0f);
        var acceleration = MechAiRulePlanner.CalculateSpatialAcceleration(
            Vector3.forward * 10000f, Vector3.right * 400f, Vector3.zero,
            MechAiTargetController.CombatState.Intercept, in doctrine,
            20f, 20f, 30f, 2f, out _);
        Assert.That(acceleration.x, Is.GreaterThan(1f));
        Assert.That(acceleration.z, Is.GreaterThan(0f));
    }

    [Test]
    public void Intercept_UsesCombatClosingSpeedInsteadOfOld320MeterCap()
    {
        var doctrine = Doctrine(0f);
        var acceleration = MechAiRulePlanner.CalculateSpatialAcceleration(
            Vector3.forward * 100000f, Vector3.zero, Vector3.zero,
            MechAiTargetController.CombatState.Intercept, in doctrine,
            20f, 20f, 1000f, 2f, out _);
        Assert.That(acceleration.z, Is.GreaterThan(350f));
        Assert.That(acceleration.z, Is.LessThanOrEqualTo(400.001f));
    }

    [Test]
    public void Navigation_UsesAvailableAccelerationFarAwayAndBrakesOnArrival()
    {
        var far = MechAiRulePlanner.CalculateNavigationAcceleration(
            Vector3.forward * 10000f, Vector3.zero, Vector3.zero,
            300f, 20f, 20f, 100f, 2f);
        Assert.That(far.z, Is.EqualTo(100f).Within(0.001f));

        var near = MechAiRulePlanner.CalculateNavigationAcceleration(
            Vector3.forward * 10f, Vector3.zero, Vector3.forward * 100f,
            300f, 20f, 20f, 100f, 2f);
        Assert.That(near.z, Is.LessThan(0f));
    }

    [Test]
    public void Navigation_MatchesReportedLeaderVelocityAtFormationPoint()
    {
        var leaderVelocity = new Vector3(20f, 10f, -5f);
        var acceleration = MechAiRulePlanner.CalculateNavigationAcceleration(
            Vector3.zero, leaderVelocity, Vector3.zero,
            300f, 20f, 20f, 100f, 2f);
        AssertVector(acceleration, leaderVelocity / 2f, 0.001f);
    }

    [Test]
    public void DefensiveFlightFrame_UsesIncomingMissileAxisWithoutAirTarget()
    {
        SetField(ai, "incomingThreat", true);
        SetField(ai, "hasIncomingThreatObservation", true);
        SetField(ai, "incomingThreatPosition", ownship.transform.position + Vector3.up * 500f);
        SetField(ai, "incomingThreatVelocity", Vector3.down * 1000f);

        var frame = (MechPlayerInputFrame)Invoke(ai, "BuildIncomingThreatFlightFrame",
            0, Vector3.zero, Vector3.zero);

        Assert.That(frame.HasAutonomousFlightCommand, Is.True);
        Assert.That(frame.AutonomousAccelerationWorld.y, Is.LessThan(-0.1f));
    }

    private void SettleAttitude(Vector3 targetDirection)
    {
        var noTranslation = default(MechPlayerInputFrame);
        for (var i = 0; i < 480; i++)
        {
            Invoke(ai, "CommandAttitude", targetDirection);
            motion.Tick(in noTranslation, 1f / 120f);
        }
    }

    private void AddNozzle(string id, Vector3 direction, MechThrusterRole role)
    {
        var anchor = new GameObject(id).transform;
        anchor.SetParent(ownship.transform, false);
        var up = Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.99f
            ? Vector3.forward : Vector3.up;
        anchor.localRotation = Quaternion.LookRotation(-direction, up);
        GetField<List<MechThrusterChannelState>>(motion, "thrusterChannels").Add(
            new MechThrusterChannelState(id, role, MechThrusterSide.Center,
                anchor, Vector3.zero, false));
    }

    private static MechWeaponSlotStatus Weapon(MechWeaponRole role) =>
        new MechWeaponSlotStatus(MechPartSlot.LeftHandWeapon, role,
            20, 20, 1, false, 0f, 0f, 3750f, 5000f, 600f,
            0.3f, 0.6f, 4, 0.4f, 0f, muzzleVelocityMetersPerSecond: 1500f);

    private static MechAiCombatDoctrine Doctrine(float orbitSpeed) =>
        new MechAiCombatDoctrine(MechAiTacticalWeaponRole.GeneralAutomatic,
            new MechAiEngagementBand(1000f, 1500f, 2000f), orbitSpeed);

    private static Vector3 SpatialAcceleration(Vector3 relativePosition, Vector3 relativeVelocity,
        Vector3 tangent, MechAiTargetController.CombatState state,
        in MechAiCombatDoctrine doctrine, out bool urgentBraking) =>
        MechAiRulePlanner.CalculateSpatialAcceleration(relativePosition, relativeVelocity,
            tangent, state, in doctrine, 20f, 20f, 30f, 2f, out urgentBraking);

    private static MechPlayerInputFrame AutonomousInput(Vector3 acceleration) =>
        new MechPlayerInputFrame
        {
            HasAutonomousFlightCommand = true,
            AutonomousAccelerationWorld = acceleration,
        };

    private static void AssertVector(Vector3 actual, Vector3 expected, float tolerance)
    {
        Assert.That(Vector3.Distance(actual, expected), Is.LessThanOrEqualTo(tolerance),
            "Expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
    }

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, PrivateInstance).GetValue(target);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, PrivateInstance).SetValue(target, value);

    private static object Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, PrivateInstance).Invoke(target, arguments);
}
