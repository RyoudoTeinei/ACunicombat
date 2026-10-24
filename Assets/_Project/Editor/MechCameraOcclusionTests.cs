using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using UnityEngine;

public sealed class MechCameraOcclusionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Vector3 FollowPosition = new Vector3(0f, 0f, -30f);
    private GameObject ownship;
    private GameObject body;
    private GameObject cameraObject;
    private Camera renderCamera;
    private MechThirdPersonCamera controller;

    [SetUp]
    public void SetUp()
    {
        ownship = new GameObject("Camera occlusion ownship");
        body = GameObject.CreatePrimitive(PrimitiveType.Cube);
        body.name = "Occluding body";
        body.transform.SetParent(ownship.transform, false);
        body.transform.localScale = new Vector3(10f, 14f, 6f);
        cameraObject = new GameObject("Camera occlusion fixture");
        renderCamera = cameraObject.AddComponent<Camera>();
        renderCamera.enabled = false;
        renderCamera.fieldOfView = 60f;
        renderCamera.aspect = 16f / 9f;
        renderCamera.nearClipPlane = 0.1f;
        renderCamera.farClipPlane = 10000f;
        cameraObject.transform.SetPositionAndRotation(FollowPosition, Quaternion.identity);
        controller = cameraObject.AddComponent<MechThirdPersonCamera>();
        SetField("followTarget", ownship.transform);
        SetField("controlledCamera", renderCamera);
        SetField("followRenderers", new[] { body.GetComponent<Renderer>() });
        SetField("distance", 30f);
        SetField("focusHeight", 0f);
        SetField("pitchDegrees", 0f);
        SetField("baseCameraHeightOffset", 0f);
        SetField("baseDistanceOffset", 0f);
        SetField("baseFieldOfView", 60f);
        SetField("fieldOfViewInitialized", true);
    }

    [TearDown]
    public void TearDown()
    {
        Object.DestroyImmediate(cameraObject);
        Object.DestroyImmediate(ownship);
    }

    [Test]
    public void StationaryOcclusion_StaysEscapedAfterTargetBecomesVisible()
    {
        var target = TargetThroughBodyPlane(Vector2.zero);
        Assert.That(IsInsideBodyProjection(target), Is.True,
            "The initial target must actually be hidden by the test body.");
        var expected = Solve(target, 0f);
        Assert.That(expected.x, Is.GreaterThan(5f));
        Assert.That(Mathf.Abs(expected.y), Is.LessThan(0.001f));

        // Real camera motion is deliberately fed back into the next solve. The
        // unchanged reference composition must keep requesting the full escape.
        var actualOffset = Vector3.zero;
        for (var frame = 1; frame <= 600; frame++)
        {
            var solved = Solve(target, frame / 120f);
            AssertVector(solved, expected, 0.001f);
            actualOffset = Vector3.Lerp(actualOffset, solved, 0.12f);
            cameraObject.transform.position = FollowPosition + actualOffset;
        }

        Assert.That(IsInsideBodyProjection(target), Is.False);
        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));
    }

    [Test]
    public void ClearReference_HoldsEscapeUntilContinuouslySafeForReleaseDelay()
    {
        var blocked = TargetThroughBodyPlane(Vector2.zero);
        var escaped = Solve(blocked, 0f);
        cameraObject.transform.position = FollowPosition + escaped;
        var safe = TargetThroughBodyPlane(new Vector2(20f, 0f));
        var hold = GetField<float>("occlusionHoldSeconds");

        AssertVector(Solve(safe, 1f), escaped, 0.001f);
        AssertVector(Solve(safe, 1f + hold * 0.5f), escaped, 0.001f);
        AssertVector(Solve(safe, 1f + hold + 0.01f), Vector3.zero, 0.001f);
        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(-1));
    }

    [Test]
    public void ReleaseHysteresis_DoesNotClearAtTheEntryRectangleEdge()
    {
        var blocked = TargetThroughBodyPlane(Vector2.zero);
        var escaped = Solve(blocked, 0f);
        // At the reference pose, place the target outside the entry rectangle
        // but still within the larger exit rectangle. The unpadded right edge
        // of this cube is projected from (5, 0, -3).
        cameraObject.transform.position = FollowPosition;
        var bodyRight = renderCamera.WorldToViewportPoint(new Vector3(5f, 0f, -3f)).x;
        var x = bodyRight + GetField<float>("occlusionViewportPadding") +
                GetField<float>("occlusionEscapeMargin") * 0.5f;
        var edgeTarget = renderCamera.ViewportToWorldPoint(new Vector3(x, 0.5f, 1030f));
        cameraObject.transform.position = FollowPosition + escaped;

        for (var frame = 1; frame <= 360; frame++)
            Assert.That(Solve(edgeTarget, frame / 120f).sqrMagnitude, Is.GreaterThan(1f));
        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));
    }

    [Test]
    public void RenderedCameraOffsetAndRotation_DoNotAlterTheAbsoluteReferenceSolution()
    {
        var target = TargetThroughBodyPlane(Vector2.zero);
        var expected = Solve(target, 0f);
        var offsets = new[]
        {
            new Vector3(3f, 0f, 0f),
            new Vector3(-7f, 4f, -5f),
            new Vector3(10f, -3f, 2f),
        };
        for (var i = 0; i < offsets.Length; i++)
        {
            cameraObject.transform.SetPositionAndRotation(
                FollowPosition + offsets[i], Quaternion.Euler(7f, 20f, 3f));
            SetField("compositionLocalOffset", offsets[i]);
            AssertVector(Solve(target, i + 1f), expected, 0.001f);
        }
    }

    [TestCase(4f, 0f, 0, 1f)]
    [TestCase(-4f, 0f, 0, -1f)]
    [TestCase(0f, 6f, 1, 1f)]
    [TestCase(0f, -6f, 1, -1f)]
    public void FourEscapeDirections_ClearTheBodyWithoutChangingPreferredShoulder(
        float targetX, float targetY, int axis, float sign)
    {
        var target = TargetThroughBodyPlane(new Vector2(targetX, targetY));
        Assert.That(IsInsideBodyProjection(target), Is.True);
        SetField("shoulderSign", -sign);

        var solved = Solve(target, 0f);

        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(axis));
        Assert.That(GetField<float>("occlusionEscapeSign"), Is.EqualTo(sign));
        Assert.That(GetField<float>("shoulderSign"), Is.EqualTo(-sign));
        Assert.That(axis == 0 ? solved.x * sign : solved.y * sign, Is.GreaterThan(0f));
        cameraObject.transform.position = FollowPosition + solved;
        Assert.That(IsInsideBodyProjection(target), Is.False,
            "The selected direction must actually expose the target, not just return a plausible sign.");
    }

    [TestCase(43.9f)]
    [TestCase(200f)]
    [TestCase(500f)]
    public void SharedHighSpeedTranslation_KeepsTheSolveStableAndBoundsAtOwnship(float speed)
    {
        var target = TargetThroughBodyPlane(Vector2.zero);
        var expected = Solve(target, 0f);
        var velocity = new Vector3(0.8f, 0f, 0.6f) * speed;
        // Freeze the shape-refresh deadline so every iteration exercises current
        // pose reconstruction from cached local bounds, not a renderer refresh.
        SetField("nextBoundsRefreshTime", float.MaxValue);

        for (var frame = 1; frame <= 240; frame++)
        {
            var time = frame / 120f;
            var translation = velocity * time;
            ownship.transform.position = translation;
            var solved = Solve(target + translation, time,
                FollowPosition + translation);
            AssertVector(solved, expected, 0.005f);
            AssertVector(GetField<Bounds>("ownshipWorldBounds").center,
                body.GetComponent<Renderer>().bounds.center, 0.005f);
            cameraObject.transform.position = FollowPosition + translation + solved;
        }
    }

    [Test]
    public void ShoulderHeightAndVelocityLag_AreIncludedWithoutExceedingTotalLateralLimit()
    {
        var target = TargetThroughBodyPlane(Vector2.zero);
        var baseOffset = new Vector3(4.161227f, 1f, -4.161227f);
        var followWithLag = FollowPosition + new Vector3(-1.5f, 0.5f, -2f);
        cameraObject.transform.position = followWithLag + baseOffset;
        Assert.That(IsInsideBodyProjection(target), Is.True);

        var solved = Solve(target, 0f, followWithLag, baseOffset);

        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));
        Assert.That(Mathf.Abs(solved.x), Is.LessThanOrEqualTo(
            GetField<float>("occlusionLateralLimit") + 0.001f));
        Assert.That(solved.x, Is.GreaterThan(baseOffset.x));
        cameraObject.transform.position = followWithLag + solved;
        Assert.That(IsInsideBodyProjection(target), Is.False);
        for (var frame = 1; frame <= 360; frame++)
            AssertVector(Solve(target, frame / 120f, followWithLag, baseOffset), solved, 0.001f);
    }

    [Test]
    public void SameTargetGainsShoulderDuringSafeHold_TotalLateralOffsetRemainsBounded()
    {
        var trackId = new TrackId(new Handle(2, 1));
        Invoke("SetCompositionTarget", trackId);
        var escaped = Solve(TargetThroughBodyPlane(Vector2.zero), 0f);
        var newShoulder = new Vector3(4.161227f, 0f, 0f);
        var lateralLimit = GetField<float>("occlusionLateralLimit");
        Assert.That(escaped.x + newShoulder.x, Is.GreaterThan(lateralLimit),
            "The old relative escape plus the new shoulder must exercise the over-limit regression.");

        // Promoting a candidate to STT preserves its TrackId. If the target also
        // moves clear, the hold path must account for the changed base shoulder.
        Invoke("SetCompositionTarget", trackId);
        var held = Solve(TargetThroughBodyPlane(new Vector2(20f, 0f)),
            0.1f, FollowPosition, newShoulder);

        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));
        Assert.That(Mathf.Abs(held.x), Is.LessThanOrEqualTo(lateralLimit + 0.001f));
        AssertVector(held, escaped, 0.001f);
    }

    [Test]
    public void SharedRotation_UsesCurrentPoseOfCachedLocalBounds()
    {
        var target = TargetThroughBodyPlane(Vector2.zero);
        var expected = Solve(target, 0f);
        SetField("nextBoundsRefreshTime", float.MaxValue);
        var translation = new Vector3(120f, -80f, 240f);
        for (var frame = 1; frame <= 24; frame++)
        {
            var rotation = Quaternion.Euler(frame * 2f, frame * 3f, frame * 1.5f);
            ownship.transform.SetPositionAndRotation(translation, rotation);
            var reference = translation + rotation * FollowPosition;
            var worldTarget = translation + rotation * target;

            var solved = Solve(worldTarget, frame / 120f, reference,
                Vector3.zero, rotation);

            AssertVector(solved, expected, 0.005f);
            var worldBounds = GetField<Bounds>("ownshipWorldBounds");
            var rendererBounds = body.GetComponent<Renderer>().bounds;
            AssertVector(worldBounds.center, rendererBounds.center, 0.005f);
            AssertVector(worldBounds.extents, rendererBounds.extents, 0.005f);
            cameraObject.transform.SetPositionAndRotation(reference + rotation * solved, rotation);
            Assert.That(IsInsideBodyProjection(worldTarget), Is.False);
        }
    }

    [Test]
    public void FallbackBounds_KeepTheirLocalShapeAcrossRotationAndShapeRefresh()
    {
        body.GetComponent<Renderer>().enabled = false;
        ownship.transform.localScale = new Vector3(1.2f, 0.8f, 1.5f);
        Invoke("RefreshOwnshipBounds");
        var local = GetField<Bounds>("ownshipLocalBounds");
        SetField("nextBoundsRefreshTime", float.MaxValue);
        ownship.transform.SetPositionAndRotation(new Vector3(11f, -7f, 22f),
            Quaternion.Euler(20f, 70f, 35f));

        Invoke("RefreshOwnshipBounds");

        var cachedWorld = GetField<Bounds>("ownshipWorldBounds");
        AssertVector(cachedWorld.center, ownship.transform.TransformPoint(local.center), 0.001f);
        SetField("nextBoundsRefreshTime", float.NegativeInfinity);
        Invoke("RefreshOwnshipBounds");
        var refreshedLocal = GetField<Bounds>("ownshipLocalBounds");
        var refreshedWorld = GetField<Bounds>("ownshipWorldBounds");
        AssertVector(refreshedLocal.center, local.center, 0.001f);
        AssertVector(refreshedLocal.extents, local.extents, 0.001f);
        AssertVector(refreshedWorld.center, cachedWorld.center, 0.001f);
        AssertVector(refreshedWorld.extents, cachedWorld.extents, 0.001f);
    }

    [Test]
    public void ChangingTargetRetainsEscapeButClearingDropsIt()
    {
        var first = new TrackId(new Handle(2, 1));
        var second = new TrackId(new Handle(2, 2));
        Invoke("SetCompositionTarget", first);
        Solve(TargetThroughBodyPlane(new Vector2(-4f, 0f)), 0f);
        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));

        Invoke("SetCompositionTarget", second);

        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));
        Assert.That(GetField<TrackId>("compositionTrackId"), Is.EqualTo(second));
        Solve(TargetThroughBodyPlane(new Vector2(4f, 0f)), 0.1f);
        Invoke("ClearCompositionTarget");
        AssertEscapeCleared();
        Assert.That(GetField<TrackId>("compositionTrackId").IsValid, Is.False);
    }

    [TestCase(30)] [TestCase(60)] [TestCase(144)]
    public void MultiTargetRankingChangesDoNotReverseTheActualSmoothedCamera(int fps)
    {
        var scheduler=new MechCompositionTargetScheduler();
        var a=new TrackSnapshot{TrackId=new TrackId(new Handle(4,1))};
        var b=new TrackSnapshot{TrackId=new TrackId(new Handle(5,1))};
        scheduler.Select(0,a,default,default,default,out _);
        Vector3 last=Vector3.zero;
        for(int frame=1;frame<=fps*12;frame++)
        {
            float now=(float)frame/fps;var candidate=(frame/(fps/5))%2==0?a:b;
            Assert.IsTrue(scheduler.Select(now,default,candidate,b,a,out var selected));
            Invoke("SetCompositionTarget",selected.TrackId);
            var offset=Solve(TargetThroughBodyPlane(Vector2.zero),now);
            Invoke("SmoothCompositionOffset",offset,1f/fps);
            var actual=GetField<Vector3>("compositionLocalOffset");
            Assert.GreaterOrEqual(actual.x,last.x-0.0001f,"The held primary must not repeatedly send the camera back to centre or the opposite shoulder.");
            Assert.That(GetField<int>("occlusionEscapeAxis"),Is.EqualTo(0));
            last=actual;cameraObject.transform.position=FollowPosition+actual;
        }
        Assert.IsFalse(IsInsideBodyProjection(TargetThroughBodyPlane(Vector2.zero)));
    }

    [Test]
    public void MissingTarget_ClearsEscapeThroughCompositionUpdate()
    {
        Invoke("SetCompositionTarget", new TrackId(new Handle(2, 1)));
        Solve(TargetThroughBodyPlane(Vector2.zero), 0f);
        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(0));

        var result = (Vector3)Invoke("ComputeTargetCompositionOffset",
            FollowPosition, Quaternion.identity);

        AssertVector(result, Vector3.zero, 0.001f);
        AssertEscapeCleared();
        Assert.That(GetField<TrackId>("compositionTrackId").IsValid, Is.False);
    }

    [Test]
    public void EnteringFreeLook_ClearsTargetAndEscapeThroughFollow()
    {
        var motion = ownship.AddComponent<MechFlightMotionController>();
        SetField("motion", motion);
        SetField("observedReferenceFrameVersion", motion.ReferenceFrameVersion);
        Invoke("SetCompositionTarget", new TrackId(new Handle(2, 1)));
        Solve(TargetThroughBodyPlane(Vector2.zero), 0f);
        SetField("freeLookActive", true);
        SetField("freeLookYawDegrees", 15f);

        controller.Follow(1f / 120f);

        AssertEscapeCleared();
        Assert.That(GetField<TrackId>("compositionTrackId").IsValid, Is.False);
    }

    private Vector3 Solve(Vector3 target, float now, Vector3? followPosition = null,
        Vector3? baseOffset = null, Quaternion? rotation = null)
    {
        var arguments = new object[]
        {
            target, now, followPosition ?? FollowPosition,
            rotation ?? Quaternion.identity, baseOffset ?? Vector3.zero,
        };
        typeof(MechThirdPersonCamera).GetMethod("ApplyOcclusionEscape", PrivateInstance)
            .Invoke(controller, arguments);
        return (Vector3)arguments[4];
    }

    private static Vector3 TargetThroughBodyPlane(Vector2 point)
    {
        // A point at z=1000 whose ray from z=-30 crosses the selected (x,y)
        // on the ownship's z=0 plane. This makes the obstruction explicit.
        return FollowPosition + new Vector3(point.x, point.y, 30f) * (1030f / 30f);
    }

    private bool IsInsideBodyProjection(Vector3 target)
    {
        var renderer = body.GetComponent<Renderer>();
        var bounds = renderer.localBounds;
        var min = Vector2.one * float.PositiveInfinity;
        var max = Vector2.one * float.NegativeInfinity;
        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = renderer.localToWorldMatrix.MultiplyPoint3x4(
                bounds.center + Vector3.Scale(bounds.extents, new Vector3(x, y, z)));
            var projected = (Vector2)renderCamera.WorldToViewportPoint(corner);
            min = Vector2.Min(min, projected);
            max = Vector2.Max(max, projected);
        }
        var viewport = renderCamera.WorldToViewportPoint(target);
        return viewport.z > 0f && Rect.MinMaxRect(min.x, min.y, max.x, max.y)
            .Contains((Vector2)viewport);
    }

    private void AssertEscapeCleared()
    {
        Assert.That(GetField<int>("occlusionEscapeAxis"), Is.EqualTo(-1));
        Assert.That(GetField<float>("occlusionEscapeMeters"), Is.Zero);
        Assert.That(GetField<bool>("occlusionEscapeClamped"), Is.False);
    }

    private static void AssertVector(Vector3 actual, Vector3 expected, float tolerance)
    {
        Assert.That(Vector3.Distance(actual, expected), Is.LessThanOrEqualTo(tolerance),
            "Expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
    }

    private T GetField<T>(string name) =>
        (T)typeof(MechThirdPersonCamera).GetField(name, PrivateInstance).GetValue(controller);

    private void SetField(string name, object value) =>
        typeof(MechThirdPersonCamera).GetField(name, PrivateInstance).SetValue(controller, value);

    private object Invoke(string name, params object[] arguments) =>
        typeof(MechThirdPersonCamera).GetMethod(name, PrivateInstance).Invoke(controller, arguments);
}
