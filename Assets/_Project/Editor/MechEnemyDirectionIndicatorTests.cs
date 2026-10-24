using System;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

public sealed class MechEnemyDirectionIndicatorTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Rect Viewport = new Rect(0, 0, 1920, 1080);
    private static readonly Rect CenterFrame = new Rect(900, 500, 66, 66);
    private static readonly TrackId First = new TrackId(new Handle(1, 1));
    private object state;
    private GameObject ownship, cameraObject;
    private MechRadarController radar;
    private MechRadarHudPresenter hud;
    private Camera camera;

    [SetUp]
    public void SetUp()
    {
        state = Activator.CreateInstance(typeof(MechRadarHudPresenter)
            .GetNestedType("EnemyDirectionState", BindingFlags.NonPublic), true);
    }

    [TearDown]
    public void TearDown()
    {
        if (cameraObject != null) Object.DestroyImmediate(cameraObject);
        if (ownship != null) Object.DestroyImmediate(ownship);
    }

    [Test]
    public void VisibleCenter_ShowsNormalFrameEvenWhenFrameIsPartlyClipped()
    {
        Step(Vector3.forward, Vector2.left, new Rect(-20, 500, 66, 66), true);
        Assert.That(Get<bool>(state, "BoxVisible"), Is.True);
        Step(Vector3.forward, Vector2.left, new Rect(-35, 500, 66, 66), true);
        Assert.That(Get<bool>(state, "BoxVisible"), Is.False);
        Step(Vector3.forward, Vector2.left, new Rect(-30, 500, 66, 66), true);
        Assert.That(Get<bool>(state, "BoxVisible"), Is.False);
        Step(Vector3.forward, Vector2.left, new Rect(-25, 500, 66, 66), true);
        Assert.That(Get<bool>(state, "BoxVisible"), Is.True);
    }

    [TestCase(1f, 1f, 1f, -1f)]
    [TestCase(-1f, 1f, -1f, -1f)]
    [TestCase(1f, -1f, 1f, 1f)]
    [TestCase(-1f, -1f, -1f, 1f)]
    public void RearDirections_AreNotMirroredByPerspective(float x, float y, float expectedX, float expectedY)
    {
        Step(new Vector3(x, y, -1), new Vector2(-x, y), CenterFrame, true);
        var direction = Get<Vector2>(state, "Direction");
        Assert.That(Mathf.Sign(direction.x), Is.EqualTo(expectedX));
        Assert.That(Mathf.Sign(direction.y), Is.EqualTo(expectedY));
        Assert.That(Get<bool>(state, "IsRear"), Is.True);
        Assert.That(Get<bool>(state, "BoxVisible"), Is.False);
    }

    [Test]
    public void NearRearPole_KeepsLastEdgeSide()
    {
        Step(new Vector3(-0.6f, 0, -1), Vector2.right, CenterFrame, false);
        for (var i = 0; i < 60; i++)
            Step(new Vector3(i % 2 == 0 ? 0.02f : -0.02f, 0, -1), Vector2.right, CenterFrame, false);
        Assert.That(Get<Vector2>(state, "Direction").x, Is.LessThan(-0.99f));
        for (var i = 0; i < 60; i++) Step(new Vector3(0.5f, 0, -1), Vector2.left, CenterFrame, false);
        Assert.That(Get<Vector2>(state, "Direction").x, Is.GreaterThan(0.99f));
    }

    [Test]
    public void HemisphereBoundary_DoesNotMirrorTheEdgeBearing()
    {
        Step(new Vector3(1, 0, 0.2f), Vector2.right, CenterFrame, false);
        for (var i = 0; i < 60; i++) Step(new Vector3(1, 0, -0.02f), Vector2.left, CenterFrame, false);
        Assert.That(Get<Vector2>(state, "Direction").x, Is.GreaterThan(0.99f));
    }

    [Test]
    public void TargetGenerationChange_ResetsOldSide()
    {
        Step(new Vector3(-1, 0, -1), Vector2.left, CenterFrame, false);
        Step(new Vector3(1, 0, -1), Vector2.right, CenterFrame, false, new TrackId(new Handle(1, 2)));
        Assert.That(Get<Vector2>(state, "Direction").x, Is.GreaterThan(0.99f));
    }

    [Test]
    public void AllKnownOffscreenTracks_GetIndependentEdgeMarkersWithoutLocking()
    {
        MakeHud();
        var tracks = Get<TrackSnapshot[]>(hud, "tracks");
        var second = new TrackId(new Handle(2, 1));
        tracks[0] = Track(First, new Vector3(10000, 0, 1000));
        tracks[1] = Track(second, new Vector3(-10000, 0, 1000));
        Set(hud, "trackCount", 2);
        Prepare();
        var right = State(First); var left = State(second);
        Assert.That(right, Is.Not.Null); Assert.That(left, Is.Not.Null);
        Assert.That(Get<bool>(right, "BoxVisible"), Is.False);
        Assert.That(Get<bool>(left, "BoxVisible"), Is.False);
        Assert.That(Get<int>(right, "Edge"), Is.EqualTo(1));
        Assert.That(Get<int>(left, "Edge"), Is.EqualTo(0));
        Assert.That(Get<TrackId>(radar, "sttTrackId").IsValid, Is.False);
        Assert.That(Get<TrackId>(radar, "candidateTrackId").IsValid, Is.False);
    }

    [Test]
    public void ScreenCenterHasFrame_LeavingScreenReplacesFrameAndLabels()
    {
        MakeHud();
        var tracks = Get<TrackSnapshot[]>(hud, "tracks");
        tracks[0] = Track(First, camera.transform.position + Vector3.forward * 1000);
        Set(hud, "trackCount", 1);
        Prepare();
        Assert.That(Invoke<bool>(hud, "ShouldDrawDirectionTargetFrame", 0), Is.True);
        tracks[0].Position = new double3(10000, 0, 1000);
        Prepare();
        Assert.That(Invoke<bool>(hud, "ShouldDrawDirectionTargetFrame", 0), Is.False);
        Assert.That(Get<bool>(State(First), "BoxVisible"), Is.False);
        Set(hud, "trackCount", 0);
        Prepare();
        Assert.That(State(First), Is.Null);
    }

    [Test]
    public void FriendlyMissileDecoyAndDroppedTracks_DoNotGetEnemyEdgeCues()
    {
        MakeHud();
        Set(hud, "trackCount", 1);
        var tracks = Get<TrackSnapshot[]>(hud, "tracks");
        var track = Track(First, Vector3.back * 1000);
        track.Affiliation = TrackAffiliation.Friendly;
        tracks[0] = track; Prepare();
        Assert.That(State(First), Is.Null);
        track.Affiliation = TrackAffiliation.Hostile;
        foreach (var type in new[] { TrackObjectClass.Missile, TrackObjectClass.Decoy })
        {
            track.ObjectClass = type; tracks[0] = track; Prepare();
            Assert.That(State(First), Is.Null);
        }
        track.ObjectClass = TrackObjectClass.AirVehicle;
        track.Lifecycle = TrackLifecycle.Coasting; tracks[0] = track; Prepare();
        Assert.That(State(First), Is.Not.Null);
        track.Lifecycle = TrackLifecycle.Dropped; tracks[0] = track; Prepare();
        Assert.That(State(First), Is.Null);
    }

    [Test]
    public void NearbyTargetsOnSameEdge_AreSeparatedWithInfoInsideViewport()
    {
        MakeHud();
        var tracks = Get<TrackSnapshot[]>(hud, "tracks");
        for (var i = 0; i < 3; i++)
            tracks[i] = Track(new TrackId(new Handle(i + 1, 1)), new Vector3(10000 + i * 500, 0, 1000));
        Set(hud, "trackCount", 3); Prepare();
        var viewport = Invoke<Rect>(hud, "GetDirectionViewport");
        for (var i = 0; i < 3; i++)
        {
            var current = State(tracks[i].TrackId);
            var anchor = Get<Vector2>(current, "Anchor");
            var info = Get<Rect>(current, "InfoRect");
            Assert.That(viewport.Contains(anchor), Is.True);
            Assert.That(info.xMin, Is.GreaterThanOrEqualTo(viewport.xMin));
            Assert.That(info.xMax, Is.LessThanOrEqualTo(viewport.xMax));
            Assert.That(info.yMin, Is.GreaterThanOrEqualTo(viewport.yMin));
            Assert.That(info.yMax, Is.LessThanOrEqualTo(viewport.yMax));
            Assert.That(info.xMax, Is.LessThan(anchor.x), "Right-edge text belongs inside the icon.");
            for (var j = 0; j < i; j++)
                Assert.That(info.Overlaps(Get<Rect>(State(tracks[j].TrackId), "InfoRect")), Is.False);
        }
    }

    [TestCase(1f, 0f, 1)]
    [TestCase(-1f, 0f, 0)]
    [TestCase(0f, -1f, 2)]
    [TestCase(0f, 1f, 3)]
    [TestCase(1f, -1f, 2)]
    public void RayPlacement_UsesViewportEdgeIncludingOffsetViewport(float x, float y, int expectedEdge)
    {
        var viewport = new Rect(100, 50, 1600, 900);
        var args = new object[] { new Vector2(x, y), viewport, 40f, 0 };
        var method = typeof(MechRadarHudPresenter).GetMethod("ResolveEdgeAnchor", BindingFlags.Static | BindingFlags.NonPublic);
        var anchor = (Vector2)method.Invoke(null, args);
        Assert.That((int)args[3], Is.EqualTo(expectedEdge));
        Assert.That(viewport.Contains(anchor), Is.True);
        Assert.That(Vector2.Distance(anchor, viewport.center), Is.GreaterThan(350f));
    }

    [Test]
    public void ThreeDimensionalOrientation_TracksRealDepthContinuously()
    {
        MakeHud();
        foreach (var direction in new[] { new Vector3(1, .4f, 2), new Vector3(1, .4f, .2f),
                    new Vector3(1, .4f, -2), Vector3.back, Vector3.up })
        {
            for (var i = 0; i < 80; i++) Step(direction, Vector2.right, CenterFrame, false);
            var orientation = Get<Quaternion>(state, "Orientation");
            Invoke<object>(hud, "BuildDirectionArrow", orientation);
            var vertices = Get<Vector3[]>(hud, "directionArrowVertices");
            var baseCenter = (vertices[0] + vertices[1] + vertices[2] + vertices[3]) * 0.25f;
            Assert.That(Vector3.Angle(vertices[4] - baseCenter, direction), Is.LessThan(0.1f),
                "The pyramid must point along actual camera-space depth, not a preset tilt.");
            Assert.That(Get<int>(hud, "directionArrowEdgeCount"), Is.EqualTo(8));
            Assert.That(Vector3.Distance(vertices[0], vertices[1]),
                Is.EqualTo(Vector3.Distance(vertices[1], vertices[2])).Within(0.001f));
        }
    }

    [Test]
    public void CameraRotation_ReprojectsEdgeAndSpatialOrientation()
    {
        MakeHud();
        var tracks = Get<TrackSnapshot[]>(hud, "tracks");
        tracks[0] = Track(First, new Vector3(1000, 10, 0));
        Set(hud, "trackCount", 1); Prepare();
        Assert.That(Get<int>(State(First), "Edge"), Is.EqualTo(1));
        camera.transform.rotation = Quaternion.Euler(0, 90, 0);
        for (var i = 0; i < 60; i++) Prepare();
        Assert.That(Get<bool>(State(First), "BoxVisible"), Is.True);
        Assert.That(Vector3.Angle(Get<Quaternion>(State(First), "Orientation") * Vector3.forward,
            camera.transform.InverseTransformDirection(new Vector3(1000, 10, 0) - camera.transform.position)),
            Is.LessThan(0.1f));
    }

    [Test]
    public void Graphics_ActualDepthChangesGreenWirePyramidProjection()
    {
        Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));
        MakeHud();
        Assert.That(Resources.Load<Shader>("Mech/EnemyDirectionArrow"), Is.Not.Null);
        var target = new RenderTexture(768, 256, 0, RenderTextureFormat.ARGB32);
        var image = new Texture2D(768, 256, TextureFormat.RGBA32, false);
        target.Create();
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            GL.Clear(true, true, new Color(.018f, .025f, .032f, 1));
            var rays = new[] { new Vector3(1, .4f, 1.5f), new Vector3(1, .4f, 0), new Vector3(1, .4f, -1.5f) };
            for (var i = 0; i < rays.Length; i++)
            {
                Invoke<object>(hud, "BuildDirectionArrow", Quaternion.LookRotation(rays[i].normalized, Vector3.up));
                Invoke<object>(hud, "RenderDirectionArrow", new Vector2(128 + i * 256, 128),
                    90f, new Color(.3f, 1f, .5f, 1), Matrix4x4.identity, 768f, 256f);
            }
            image.ReadPixels(new Rect(0, 0, 768, 256), 0, 0); image.Apply();
            var pixels = image.GetPixels32();
            for (var i = 0; i < 3; i++)
            {
                var green = 0;
                for (var y = 0; y < 256; y++)
                for (var x = i * 256; x < (i + 1) * 256; x++)
                {
                    var p = pixels[y * 768 + x];
                    if (p.g > 80 && p.g > p.r * 1.3f && p.g > p.b * 1.1f) green++;
                }
                Assert.That(green, Is.InRange(300, 3000), "Each view must show hollow green edges.");
            }
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Environment.GetEnvironmentVariable("ACUNICOMBAT_SENSOR_BENCH_OUTPUT") ?? System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath,"../Temp")),"EdgePyramidDepthViews.png"), image.EncodeToPNG());
        }
        finally
        {
            RenderTexture.active = previous; target.Release();
            Object.DestroyImmediate(target); Object.DestroyImmediate(image);
        }
    }

    [Test]
    public void CrowdedEdge_KeepsEveryConeAndCompactsLabels()
    {
        MakeHud();
        var tracks = Get<TrackSnapshot[]>(hud, "tracks");
        for (var i = 0; i < 32; i++)
            tracks[i] = Track(new TrackId(new Handle(i, 1)), new Vector3(10000 + i * 1000, 0, 1000));
        Set(hud, "trackCount", 32); Prepare();
        var viewport = Invoke<Rect>(hud, "GetDirectionViewport");
        var previousY = float.NegativeInfinity;
        var ordered = new System.Collections.Generic.List<object>();
        for (var i = 0; i < 32; i++)
        {
            var item = State(tracks[i].TrackId);
            Assert.That(item, Is.Not.Null);
            Assert.That(Get<bool>(item, "FullDetails"), Is.False);
            Assert.That(viewport.Contains(Get<Vector2>(item, "Anchor")), Is.True);
            ordered.Add(item);
        }
        ordered.Sort((a, b) => Get<Vector2>(a, "Anchor").y.CompareTo(Get<Vector2>(b, "Anchor").y));
        foreach (var item in ordered)
        {
            var y = Get<Vector2>(item, "Anchor").y;
            Assert.That(y - previousY, Is.GreaterThan(20f));
            previousY = y;
        }
    }

    private void MakeHud()
    {
        ownship = new GameObject("Edge indicator ownship"); ownship.SetActive(false);
        radar = ownship.AddComponent<MechRadarController>();
        hud = ownship.AddComponent<MechRadarHudPresenter>();
        cameraObject = new GameObject("Edge indicator camera");
        camera = cameraObject.AddComponent<Camera>(); camera.enabled = false;
        camera.fieldOfView = 60f; camera.aspect = 16f / 9f;
        camera.transform.position = new Vector3(0, 10, -30);
        Set(hud, "radar", radar); Set(hud, "viewCamera", camera);
    }
    private object State(TrackId id) => Invoke<object>(hud, "GetDirectionState", id);
    private void Prepare() { Set(hud, "directionPreparedFrame", -1); Invoke<object>(hud, "PrepareEnemyDirectionIndicator", 1f / 60f); }
    private static TrackSnapshot Track(TrackId id, Vector3 p) => new TrackSnapshot
    {
        TrackId = id, Position = new double3(p.x, p.y, p.z), Affiliation = TrackAffiliation.Hostile,
        ObjectClass = TrackObjectClass.AirVehicle, Lifecycle = TrackLifecycle.Confirmed, Confidence = .9f
    };
    private void Step(Vector3 local, Vector2 screenDirection, Rect rect, bool eligible, TrackId? id = null) =>
        Invoke<object>(state, "Update", id ?? First, local, screenDirection, rect, eligible, Viewport, 1f / 60f);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Fields).GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Fields).SetValue(target, value);
    private static T Invoke<T>(object target, string name, params object[] args) =>
        (T)target.GetType().GetMethod(name, Fields).Invoke(target, args);
}