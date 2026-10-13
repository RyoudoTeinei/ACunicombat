using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class MechLocalAttitudeTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private GameObject body, cameraObject;
    private MechFlightMotionController motion;
    private MechThirdPersonCamera cameraRig;
    private Camera camera;

    [SetUp]
    public void SetUp()
    {
        body = new GameObject("Local attitude fixture");
        body.SetActive(false);
        body.transform.position = Vector3.up * 10000f;
        motion = body.AddComponent<MechFlightMotionController>();
        motion.ConfigureModelForwardYawOffset(0f);
        motion.ConfigureGravity(0f);
        Set(motion, "airCoastingDragAcceleration", 0f);
    }

    [TearDown]
    public void TearDown()
    {
        if (cameraObject != null) Object.DestroyImmediate(cameraObject);
        Object.DestroyImmediate(body);
    }

    [TestCase(0f)]
    [TestCase(60f)]
    [TestCase(89f)]
    [TestCase(90f)]
    [TestCase(91f)]
    [TestCase(120f)]
    [TestCase(179f)]
    [TestCase(-90f)]
    [TestCase(-120f)]
    public void HorizontalInput_RotatesAboutOwnUpAtEveryPitch(float pitch)
    {
        motion.EnableLocalAttitudeControl(pitch);
        var before = motion.LogicalRotation;
        var reference = motion.ReferenceRotation;
        motion.SetLocalAttitudeInput(0.3f, 0f);
        motion.Tick(default, 0.02f);
        var after = motion.LogicalRotation;
        var localDelta = Quaternion.Inverse(before) * after;
        localDelta.ToAngleAxis(out var angle, out var axis);
        Assert.That(Vector3.Dot(axis, Vector3.up), Is.GreaterThan(0.999f));
        Assert.That(angle, Is.GreaterThan(0.1f));
        Assert.That(Vector3.Dot(after * Vector3.forward, before * Vector3.right), Is.GreaterThan(0f));
        Assert.That(Quaternion.Angle(reference, motion.ReferenceRotation), Is.LessThan(0.01f));
    }

    [Test]
    public void ContinuousPitch_CompletesTwoLoopsWithoutWrappingTheControlState()
    {
        motion.EnableLocalAttitudeControl(0f);
        motion.SetLocalAttitudeInput(0f, -60f);
        var before = motion.LogicalRotation;
        for (var i = 0; i < 1200; i++)
        {
            motion.Tick(default, 0.01f);
            Assert.That(Quaternion.Angle(before, motion.LogicalRotation), Is.LessThan(0.7f));
            before = motion.LogicalRotation;
        }
        Assert.That(Quaternion.Angle(Quaternion.identity, motion.LogicalRotation), Is.LessThan(0.1f));
    }

    [Test]
    public void CombinedInput_HasNoCommandedLocalRoll()
    {
        motion.EnableLocalAttitudeControl(-110f);
        var before = motion.LogicalRotation;
        motion.SetLocalAttitudeInput(0.25f, -30f);
        motion.Tick(default, 0.02f);
        var delta = Quaternion.Inverse(before) * motion.LogicalRotation;
        delta.ToAngleAxis(out _, out var axis);
        Assert.That(Mathf.Abs(axis.z), Is.LessThan(0.001f));
        Assert.That(axis.x, Is.LessThan(0f));
        Assert.That(axis.y, Is.GreaterThan(0f));
    }

    [Test]
    public void ReferenceReset_KeepsAttitudeCameraPoseAndVelocity()
    {
        motion.EnableLocalAttitudeControl(-140f);
        MakeCamera();
        Set(motion, "velocity", new Vector3(14f, -8f, 30f));
        cameraRig.Follow(0f);
        var before = motion.LogicalRotation;
        var beforeCamera = camera.transform.position;
        var beforeVelocity = motion.Velocity;
        motion.ResetReferencePlaneToRotation(before);
        cameraRig.ResetToReferenceHorizon();
        cameraRig.Follow(0f);
        Assert.That(Quaternion.Angle(before, motion.LogicalRotation), Is.LessThan(0.01f));
        Assert.That(Vector3.Distance(beforeCamera, camera.transform.position), Is.LessThan(0.01f));
        Assert.That(motion.Velocity, Is.EqualTo(beforeVelocity));
        Assert.That(motion.LocalAttitudeControl, Is.True);
    }

    [Test]
    public void CameraAndCombatRay_ShareContinuousBodyAttitude()
    {
        // Unity's native ViewportPointToRay loses angular precision when its
        // near-plane points are reconstructed far from the origin.
        body.transform.position = Vector3.zero;
        motion.EnableLocalAttitudeControl(120f);
        MakeCamera();
        motion.SetLocalAttitudeInput(0.4f, -25f);
        for (var i = 0; i < 120; i++)
        {
            motion.Tick(default, 1f / 120f);
            cameraRig.Follow(1f / 120f);
            Assert.That(Quaternion.Angle(camera.transform.rotation, motion.LogicalRotation), Is.LessThan(0.08f));
            var renderedRay = camera.ViewportPointToRay(cameraRig.AimViewportPoint);
            Assert.That(Vector3.Angle(renderedRay.direction, cameraRig.GetCombatAimRay().direction), Is.LessThan(0.05f));
        }
    }

    [Test]
    public void FreeLook_UsesLocalUpWithoutTurningBodyOrCombatAim()
    {
        motion.EnableLocalAttitudeControl(-90f);
        MakeCamera();
        var before = motion.LogicalRotation;
        var aim = cameraRig.GetCombatAimRay().direction;
        cameraRig.UpdateLook(new MechPlayerInputFrame { FreeLookHeld = true, MouseDelta = new Vector2(100f, 0f) });
        motion.Tick(default, 1f / 60f);
        cameraRig.Follow(1f / 60f);
        Assert.That(Quaternion.Angle(before, motion.LogicalRotation), Is.LessThan(0.01f));
        Assert.That(Vector3.Angle(aim, cameraRig.GetCombatAimRay().direction), Is.LessThan(0.01f));
        var delta = Quaternion.Inverse(before) * camera.transform.rotation;
        delta.ToAngleAxis(out _, out var axis);
        Assert.That(Vector3.Dot(axis, Vector3.up), Is.GreaterThan(0.999f));
        cameraRig.UpdateLook(default);
        for (var i = 0; i < 180; i++) cameraRig.Follow(1f / 60f);
        Assert.That(Quaternion.Angle(camera.transform.rotation, before), Is.LessThan(0.1f));
    }

    [Test]
    public void ZoomSteering_ConvergesFromInvertedAttitudeInLocalAxes()
    {
        motion.EnableLocalAttitudeControl(-130f);
        MakeCamera();
        var targetDirection = (motion.LogicalRotation * new Vector3(0.3f, 0.4f, 1f)).normalized;
        for (var i = 0; i < 360; i++)
        {
            Invoke(cameraRig, "SteerLocallyToward", targetDirection);
            motion.Tick(default, 1f / 120f);
        }
        Assert.That(Vector3.Angle(motion.LogicalRotation * Vector3.forward, targetDirection), Is.LessThan(0.2f));
        Assert.That(motion.LocalAttitudeControl, Is.True);
    }

    [Test]
    public void ExplicitRoll_PreservesForward_AndNextYawStillUsesRolledUp()
    {
        motion.EnableLocalAttitudeControl(-120f);
        var before = motion.LogicalRotation;
        motion.RollReferencePlane(35f);
        var rolled = motion.LogicalRotation;
        Assert.That(Vector3.Angle(before * Vector3.forward, rolled * Vector3.forward), Is.LessThan(0.01f));
        Assert.That(Vector3.Angle(before * Vector3.up, rolled * Vector3.up), Is.EqualTo(35f).Within(0.05f));
        motion.SetLocalAttitudeInput(0.3f, 0f);
        motion.Tick(default, 0.02f);
        var delta = Quaternion.Inverse(rolled) * motion.LogicalRotation;
        delta.ToAngleAxis(out _, out var axis);
        Assert.That(Vector3.Dot(axis, Vector3.up), Is.GreaterThan(0.999f));
    }

    [Test]
    public void GroundedLocalControl_RetainsPitchEnvelope()
    {
        motion.EnableLocalAttitudeControl(0f);
        motion.SetLocalAttitudeInput(0f, 60f);
        Set(motion, "isGrounded", true);
        motion.Tick(default, 0.1f);
        Assert.That(motion.LogicalPitchDegrees, Is.Zero.Within(0.01f));
        motion.SetLocalAttitudeInput(0f, -180f);
        for (var i = 0; i < 20; i++)
        {
            Set(motion, "isGrounded", true);
            motion.Tick(default, 0.1f);
        }
        Assert.That(motion.LogicalPitchDegrees, Is.EqualTo(-85f).Within(0.05f));
    }

    [Test]
    public void HorizonLadder_FadesAtPoleButRecoversOnBothSides()
    {
        var method = typeof(MechRadarHudPresenter).GetMethod("ResolveHorizonVisibility", BindingFlags.Static | BindingFlags.NonPublic);
        float Visibility(float pitch) => (float)method.Invoke(null,
            new object[] { Quaternion.AngleAxis(pitch, Vector3.right) * Vector3.forward, Vector3.up });
        Assert.That(Visibility(90f), Is.Zero.Within(0.001f));
        Assert.That(Visibility(85f), Is.InRange(0.1f, 0.9f));
        Assert.That(Visibility(95f), Is.EqualTo(Visibility(85f)).Within(0.001f));
        Assert.That(Visibility(0f), Is.EqualTo(1f));
    }

    [Test]
    public void InvertedTargetReferenceReset_PreservesCurrentUpAndView()
    {
        motion.EnableLocalAttitudeControl(-140f);
        MakeCamera();
        var before = motion.LogicalRotation;
        cameraRig.Follow(0f);
        var beforeCamera = camera.transform.position;
        motion.ResetReferencePlaneToward(body.transform.position + (before * Vector3.forward) * 1000f);
        cameraRig.ResetToReferenceHorizon();
        cameraRig.Follow(0f);
        Assert.That(Quaternion.Angle(before, motion.LogicalRotation), Is.LessThan(0.05f));
        Assert.That(Vector3.Distance(beforeCamera, camera.transform.position), Is.LessThan(0.02f));
    }

    [Test]
    public void MouseLook_UsesLocalYawWhenInverted()
    {
        motion.EnableLocalAttitudeControl(120f);
        MakeCamera();
        var before = motion.LogicalRotation;
        cameraRig.UpdateLook(new MechPlayerInputFrame { MouseDelta = new Vector2(Screen.width, 0f) });
        motion.Tick(default, 1f / 60f);
        var delta = Quaternion.Inverse(before) * motion.LogicalRotation;
        delta.ToAngleAxis(out var angle, out var axis);
        Assert.That(angle, Is.GreaterThan(0.1f));
        Assert.That(Vector3.Dot(axis, Vector3.up), Is.GreaterThan(0.999f));
        Assert.That(motion.LocalAttitudeControl, Is.True);
    }

    private void MakeCamera()
    {
        cameraObject = new GameObject("Local attitude camera");
        camera = cameraObject.AddComponent<Camera>();
        camera.enabled = false;
        cameraRig = cameraObject.AddComponent<MechThirdPersonCamera>();
        cameraRig.Configure(body.transform, motion);
    }
    private static void Set(object target, string name, object value) =>
        target.GetType().GetField(name, Private).SetValue(target, value);
    private static object Invoke(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, Private).Invoke(target, args);
}