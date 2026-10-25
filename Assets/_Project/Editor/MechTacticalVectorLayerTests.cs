using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

public sealed class MechTacticalVectorLayerTests
{
    private GameObject root;

    [SetUp]
    public void Setup()
    {
        root = new GameObject("Vector layer", typeof(RectTransform), typeof(CanvasRenderer), typeof(MechTacticalVectorLayer));
    }

    [TearDown]
    public void Cleanup()
    {
        Object.DestroyImmediate(root);
    }

    [Test]
    public void LinesAndFillsShareOneBatchedMesh()
    {
        var layer = root.GetComponent<MechTacticalVectorLayer>();
        layer.Begin();
        layer.AddLine(Vector2.zero, new Vector2(20, 10), Color.white, 2);
        layer.AddRect(new Rect(4, 5, 30, 20), Color.gray);
        Assert.That(layer.VertexCount, Is.EqualTo(8));
        Assert.That(layer.IndexCount, Is.EqualTo(12));
    }

    [Test]
    public void DegenerateGeometryAndPreviousFrameAreDiscarded()
    {
        var layer = root.GetComponent<MechTacticalVectorLayer>();
        layer.Begin();
        layer.AddLine(Vector2.one, Vector2.one, Color.white, 1);
        layer.AddRect(Rect.zero, Color.white);
        Assert.That(layer.VertexCount, Is.Zero);
        layer.AddLine(Vector2.zero, Vector2.right, Color.white, 1);
        layer.Begin();
        Assert.That(layer.VertexCount, Is.Zero);
        Assert.That(layer.IndexCount, Is.Zero);
    }

    [Test]
    public void LinesAndRectsUseTheSameFrontFaceWinding()
    {
        var layer = root.GetComponent<MechTacticalVectorLayer>();
        var field = typeof(MechTacticalVectorLayer).GetField("vertices", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        layer.Begin();
        layer.AddLine(Vector2.zero, new Vector2(30, 0), Color.white, 2);
        float lineArea = SignedArea((System.Collections.IList)field.GetValue(layer));
        layer.Begin();
        layer.AddRect(new Rect(0, 0, 30, 20), Color.white);
        float rectArea = SignedArea((System.Collections.IList)field.GetValue(layer));
        Assert.That(lineArea, Is.LessThan(0));
        Assert.That(rectArea, Is.LessThan(0));
    }

    private static float SignedArea(System.Collections.IList vertices)
    {
        Vector3 a = ((UIVertex)vertices[0]).position;
        Vector3 b = ((UIVertex)vertices[1]).position;
        Vector3 c = ((UIVertex)vertices[2]).position;
        return Vector3.Cross(b - a, c - a).z;
    }
}
