using System.Collections.Generic;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
public class MechMapHangRegressionTests
{
    [Test] public void TailClearanceRoundingCannotTrapDrawLoop()
    {
        float walked=4.904900074005127f,clearance=14.572131156921387f;
        float offset=clearance-walked;
        Assert.Less(walked+offset,clearance,"Reproduce the old non-progressing branch");
        float next=clearance-walked;Assert.AreEqual(offset,next);
        var spans=new List<Vector2>();
        MechBattlefieldMap.BuildTrailDashIntervals(walked,40,clearance,spans);
        Assert.Greater(spans.Count,0);Assert.LessOrEqual(spans.Count,11);
        foreach(var span in spans){Assert.Greater(span.y,span.x);Assert.GreaterOrEqual(span.x,0);Assert.LessOrEqual(span.y,40);}
        for(int i=0;i<10000;i++)MechBattlefieldMap.BuildTrailDashIntervals(i*.0079f,80,clearance,spans);
        MechBattlefieldMap.BuildTrailDashIntervals(0,float.NaN,clearance,spans);Assert.AreEqual(0,spans.Count);
    }
    [Test] public void CanvasBatchesUse16BitIndicesWithoutDroppingGeometry()
    {
        var root=new GameObject("Canvas test",typeof(Canvas));
        try
        {
            var go=new GameObject("Vectors",typeof(RectTransform),typeof(CanvasRenderer),typeof(MechTacticalVectorLayer));go.transform.SetParent(root.transform,false);
            var layer=go.GetComponent<MechTacticalVectorLayer>();layer.Begin();
            for(int i=0;i<20000;i++)layer.AddRect(new Rect(i%100,i/100,1,1),Color.white);
            layer.Complete();Canvas.ForceUpdateCanvases();Assert.AreEqual(80000,layer.VertexCount);Assert.AreEqual(120000,layer.IndexCount);
            var pages=go.GetComponentsInChildren<MechTacticalVectorLayer>();Assert.AreEqual(2,pages.Length);
            foreach(var page in pages)
            {
                var mesh=(Mesh)typeof(MechTacticalVectorLayer).GetField("mesh",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(page);
                Assert.AreEqual(UnityEngine.Rendering.IndexFormat.UInt16,mesh.indexFormat);Assert.LessOrEqual(mesh.vertexCount,60000);
            }
            layer.Begin();layer.AddRect(new Rect(0,0,1,1),Color.white);layer.Complete();Assert.AreEqual(4,layer.VertexCount);Assert.AreEqual(1,go.GetComponentsInChildren<MechTacticalVectorLayer>().Length);
        }
        finally{Object.DestroyImmediate(root);}
    }
}
