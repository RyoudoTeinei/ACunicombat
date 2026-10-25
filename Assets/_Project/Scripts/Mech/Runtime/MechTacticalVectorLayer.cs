using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ACunicombat.Mechs
{
    /// <summary>Top-left layout-space lines and fills, batched into one canvas mesh.</summary>
    public sealed class MechTacticalVectorLayer : MaskableGraphic
    {
        private readonly List<UIVertex> vertices = new List<UIVertex>(4096);
        private readonly List<int> indices = new List<int>(6144);
        private readonly VertexHelper vertexHelper = new VertexHelper();
        private Mesh mesh;
        private const int VerticesPerMesh=60000;
        private readonly List<MechTacticalVectorLayer> overflow=new List<MechTacticalVectorLayer>();
        private int writePage;
        private static bool Finite(Vector2 p)=>float.IsFinite(p.x)&&float.IsFinite(p.y);

        public int VertexCount { get { int total=vertices.Count;foreach(var page in overflow)total+=page.VertexCount;return total; } }
        public int IndexCount { get { int total=indices.Count;foreach(var page in overflow)total+=page.IndexCount;return total; } }

        public void Begin()
        {
            vertices.Clear();
            indices.Clear();writePage=0;
            foreach(var page in overflow){page.Begin();page.gameObject.SetActive(false);}
        }

        public void AddLine(Vector2 a, Vector2 b, Color color, float width)
        {
            Vector2 delta = b - a;
            if (delta.sqrMagnitude < .000001f || width <= 0 || color.a <= 0) return;
            Vector2 normal = new Vector2(-delta.y, delta.x).normalized * (Mathf.Max(width, 1.25f) * .5f);
            AddQuad(a - normal, b - normal, b + normal, a + normal, color);
        }

        public void AddRect(Rect bounds, Color color)
        {
            if (bounds.width <= 0 || bounds.height <= 0 || color.a <= 0) return;
            Vector2 a = bounds.position;
            AddQuad(a, a + Vector2.right * bounds.width,
                a + new Vector2(bounds.width, bounds.height), a + Vector2.up * bounds.height, color);
        }

        private void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
        {
            if(!Finite(a)||!Finite(b)||!Finite(c)||!Finite(d))return;
            if(vertices.Count>=VerticesPerMesh)
            {
                while(writePage<overflow.Count&&overflow[writePage].vertices.Count>=VerticesPerMesh)writePage++;
                if(writePage==overflow.Count)
                {
                    var go=new GameObject("Vector batch "+writePage,typeof(RectTransform),typeof(CanvasRenderer),typeof(MechTacticalVectorLayer));
                    go.layer=gameObject.layer;go.transform.SetParent(transform,false);
                    var rect=go.GetComponent<RectTransform>();rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;rect.pivot=rectTransform.pivot;
                    var page=go.GetComponent<MechTacticalVectorLayer>();page.raycastTarget=false;overflow.Add(page);
                }
                var target=overflow[writePage];target.gameObject.SetActive(true);target.AddQuad(a,b,c,d,color);return;
            }
            int start = vertices.Count;
            Color32 tint = color.linear;
            AddVertex(a, tint);
            AddVertex(b, tint);
            AddVertex(c, tint);
            AddVertex(d, tint);
            indices.Add(start); indices.Add(start + 1); indices.Add(start + 2);
            indices.Add(start); indices.Add(start + 2); indices.Add(start + 3);
        }

        private void AddVertex(Vector2 point, Color32 color)
        {
            UIVertex vertex = UIVertex.simpleVert;
            Vector2 origin = rectTransform.rect.min;
            vertex.position = new Vector3(origin.x + point.x, origin.y - point.y, 0);
            vertex.color = color;
            vertices.Add(vertex);
        }

        public void Complete()
        {
            if (mesh == null)
            {
                mesh = new Mesh { name = name + " geometry", indexFormat = UnityEngine.Rendering.IndexFormat.UInt16 };
                mesh.MarkDynamic();
            }
            mesh.Clear();
            OnPopulateMesh(vertexHelper);
            vertexHelper.FillMesh(mesh);
            mesh.RecalculateBounds();
            canvasRenderer.SetMesh(mesh);
            foreach(var page in overflow)if(page.vertices.Count>0){page.material=material;page.Complete();}
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (int i = 0; i < vertices.Count; i++) vh.AddVert(vertices[i]);
            for (int i = 0; i < indices.Count; i += 3)
                vh.AddTriangle(indices[i], indices[i + 1], indices[i + 2]);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (mesh != null){if(Application.isPlaying)Destroy(mesh);else DestroyImmediate(mesh);}
            vertexHelper.Dispose();
        }
    }
}
