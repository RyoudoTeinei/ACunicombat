using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
namespace ACunicombat.Mechs
{
// Geometry is baked only on loadout changes; damage updates recolour a compact coverage cache.
public sealed class MechDamageDiagramRenderer : IDisposable
{
    private struct Pixel { public int Index;public byte Fill,Line; }
    private readonly Dictionary<string,Pixel[]> coverage;
    private readonly Color32[] pixels;
    public Texture2D Texture {get;private set;}
    public IReadOnlyCollection<string> RegionIds=>coverage.Keys;
    private MechDamageDiagramRenderer(int size,Dictionary<string,Pixel[]> coverage)
    {this.coverage=coverage;pixels=new Color32[size*size];Texture=new Texture2D(size,size,TextureFormat.RGBA32,false,true);Recolor(null);}
    public void Recolor(Func<string,MechPartCondition> resolve)
    {
        Array.Clear(pixels,0,pixels.Length);
        foreach(var pair in coverage)
        {
            var state=resolve!=null?resolve(pair.Key):MechPartCondition.NormalOrLight;
            Color color=state==MechPartCondition.NormalOrLight?new Color(.15f,1,.42f):state==MechPartCondition.FunctionImpaired?new Color(1,.73f,.12f):new Color(1,.25f,.18f);
            float opacity=state==MechPartCondition.Destroyed?0:state==MechPartCondition.SevereButUsable?.22f:state==MechPartCondition.FunctionImpaired?.20f:.18f;
            foreach(var pixel in pair.Value)
            {
                byte alpha=(byte)Mathf.Max(pixel.Line,Mathf.RoundToInt(pixel.Fill*opacity));
                if(alpha<=pixels[pixel.Index].a)continue;
                var c=(Color32)color;c.a=alpha;pixels[pixel.Index]=c;
            }
        }
        Texture.SetPixels32(pixels);Texture.Apply(false,false);
    }
    public void Dispose(){if(Texture!=null)UnityEngine.Object.DestroyImmediate(Texture);Texture=null;}
    private sealed class DrawPart { public Mesh Mesh; public Matrix4x4 Matrix; public int Id; public Matrix4x4 RegionMatrix; }
    private sealed class PreviewGroup
    {
        public int Id, RightId; public bool Weapon, Back; public Bounds Bounds; public bool HasBounds;
        public readonly List<DrawPart> Draws=new List<DrawPart>();
    }
    private static Bounds ProjectedBounds(Mesh mesh,Matrix4x4 matrix)
    {
        var b=mesh.bounds;var result=new Bounds(matrix.MultiplyPoint3x4(b.center),Vector3.zero);
        for(int corner=0;corner<8;corner++)
            result.Encapsulate(matrix.MultiplyPoint3x4(b.center+Vector3.Scale(b.extents,
                new Vector3((corner&1)==0?-1:1,(corner&2)==0?-1:1,(corner&4)==0?-1:1))));
        return result;
    }
    private static void PlaceGroup(PreviewGroup group,Vector2 center,Vector2 area)
    {
        float scale=Mathf.Min(area.x/Mathf.Max(.001f,group.Bounds.size.x),area.y/Mathf.Max(.001f,group.Bounds.size.y));
        var placement=Matrix4x4.TRS(new Vector3(center.x,center.y,0),Quaternion.identity,Vector3.one*scale)*Matrix4x4.Translate(-group.Bounds.center);
        foreach(var draw in group.Draws)draw.Matrix=placement*draw.Matrix;
    }
    private static void SmoothMask(float[] mask,float[] temporary,int size,int iterations)
    {
        int[] weights={1,4,6,4,1};
        for(int iteration=0;iteration<iterations;iteration++)
        {
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                float sum=0;for(int k=-2;k<=2;k++)sum+=mask[y*size+Mathf.Clamp(x+k,0,size-1)]*weights[k+2];
                temporary[y*size+x]=sum/16;
            }
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                float sum=0;for(int k=-2;k<=2;k++)sum+=temporary[Mathf.Clamp(y+k,0,size-1)*size+x]*weights[k+2];
                mask[y*size+x]=sum/16;
            }
        }
    }
    public static MechDamageDiagramRenderer Bake(MechLoadoutDefinition loadout,int resolution=256,float yaw=0)
    {
        const int supersample=4;int size=resolution*supersample;
        var root=new GameObject("DamageOutlinePreviewModel"){hideFlags=HideFlags.HideAndDontSave};root.SetActive(false);
        var cameraObject=new GameObject("DamageOutlinePreviewCamera"){hideFlags=HideFlags.HideAndDontSave};
        var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;
        var idShader=Resources.Load<Shader>("Shaders/MechDamageDiagramId") ?? Shader.Find("Hidden/ACunicombat/DamageDiagramId");
        if(idShader==null)throw new InvalidOperationException("Part-ID preview shader was not imported.");
        var material=new Material(idShader){hideFlags=HideFlags.HideAndDontSave};
        var baked=new List<Mesh>();var draws=new List<DrawPart>();var slots=new Dictionary<int,string>();
        RenderTexture target=null;Texture2D readback=null;var previous=RenderTexture.active;
        try
        {
            var assembler=root.AddComponent<MechAssembler>();
            assembler.ConfigureRuntimePreview();
            assembler.Loadout=loadout;assembler.Assemble();
            var records=assembler.RuntimeState.AssembledParts;
            float leftSign=MechDamageGeometry.LeftSign(assembler.RuntimeState);
            var recordIds=new Dictionary<string,int>(StringComparer.Ordinal);
            var groups=new Dictionary<int,PreviewGroup>();
            var body=new PreviewGroup();
            int id=0;
            foreach(var record in records)
            {
                if(record.instance==null)continue;
                int primary=++id;recordIds[record.SlotId]=primary;
                bool split=MechDamageGeometry.SplitLimbs(record);
                slots[primary]=MechDamageGeometry.RegionId(record.SlotId,split?MechDamageSide.Left:MechDamageSide.Center);
                var group=new PreviewGroup{Id=primary,Weapon=record.part.Category==MechPartCategory.Weapon,Back=record.part.MountClass==MechHardpointType.BackWeapon};
                groups[primary]=group;
                if(split){group.RightId=++id;slots[id]=MechDamageGeometry.RegionId(record.SlotId,MechDamageSide.Right);groups[id]=group;}
            }
            var view=Matrix4x4.Rotate(Quaternion.Inverse(Quaternion.Euler(0,yaw,0)));
            // Visit each renderer once. Nested mount hierarchies must not duplicate the equipped mesh into the torso mask.
            foreach(var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                int partId=0;
                for(var ancestor=renderer.transform;ancestor!=null&&ancestor!=root.transform;ancestor=ancestor.parent)
                {
                    var owner=ancestor.GetComponent<MechAssembledPart>();
                    if(owner!=null&&recordIds.TryGetValue(owner.SlotId,out partId))break;
                    // Existing assembler visual replicas share one logical equipment record.
                    foreach(var record in records)
                        if(record.instance!=null&&ancestor.name.StartsWith(record.instance.name+"_Visual_",StringComparison.Ordinal))
                        {partId=recordIds[record.SlotId];break;}
                    if(partId!=0)break;
                }
                if(partId==0)continue;
                Mesh mesh=null;
                if(renderer is SkinnedMeshRenderer skinned){mesh=new Mesh();skinned.BakeMesh(mesh);baked.Add(mesh);}
                else if(renderer is MeshRenderer)mesh=renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if(mesh==null||mesh.vertexCount==0)continue;
                var draw=new DrawPart{Mesh=mesh,Matrix=view*renderer.localToWorldMatrix,RegionMatrix=root.transform.worldToLocalMatrix*renderer.localToWorldMatrix,Id=partId};draws.Add(draw);
                var group=groups[partId].Weapon?groups[partId]:body;
                group.Draws.Add(draw);var bounds=ProjectedBounds(mesh,draw.Matrix);
                if(!group.HasBounds){group.Bounds=bounds;group.HasBounds=true;}else group.Bounds.Encapsulate(bounds);
            }
            if(!body.HasBounds||draws.Count==0)throw new InvalidOperationException("No frame meshes in this loadout.");
            var bodyCenter=body.Bounds.center;
            var buckets=new List<PreviewGroup>[4];for(int i=0;i<4;i++)buckets[i]=new List<PreviewGroup>();
            foreach(var group in groups.Values)
                if(group.Weapon&&group.HasBounds)
                {
                    int side=group.Bounds.center.x<bodyCenter.x?0:1;
                    buckets[(group.Back?0:2)+side].Add(group);
                }
            PlaceGroup(body,new Vector2(0,-.015f),new Vector2(.94f,1.78f));
            for(int bucket=0;bucket<4;bucket++)
            {
                var list=buckets[bucket];list.Sort((a,b)=>a.Id.CompareTo(b.Id));
                bool upper=bucket<2;float bandHeight=upper?.65f:.95f;
                for(int index=0;index<list.Count;index++)
                {
                    float rowHeight=bandHeight/list.Count;
                    float y=(upper?.56f:-.36f)+bandHeight*.5f-rowHeight*(index+.5f);
                    PlaceGroup(list[index],new Vector2(bucket%2==0?-.77f:.77f,y),new Vector2(.34f,rowHeight*.88f));
                }
            }
            camera.orthographic=true;camera.orthographicSize=1.05f;camera.aspect=1;
            camera.nearClipPlane=.01f;camera.farClipPlane=20;
            camera.transform.SetPositionAndRotation(new Vector3(0,0,-10),Quaternion.identity);
            target=new RenderTexture(size,size,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear){antiAliasing=1,filterMode=FilterMode.Point};target.Create();
            using(var commands=new CommandBuffer())
            {
                commands.SetRenderTarget(target);commands.ClearRenderTarget(true,true,Color.clear);
                commands.SetViewProjectionMatrices(camera.worldToCameraMatrix,GL.GetGPUProjectionMatrix(camera.projectionMatrix,true));
                foreach(var draw in draws)
                {
                    var properties=new MaterialPropertyBlock();properties.SetFloat("_PartId",draw.Id);
                    properties.SetFloat("_RightPartId",groups[draw.Id].RightId);properties.SetFloat("_LeftSign",leftSign);properties.SetMatrix("_RegionMatrix",draw.RegionMatrix);
                    for(int sub=0;sub<draw.Mesh.subMeshCount;sub++)commands.DrawMesh(draw.Mesh,draw.Matrix,material,sub,0,properties);
                }
                Graphics.ExecuteCommandBuffer(commands);
            }
            RenderTexture.active=target;readback=new Texture2D(size,size,TextureFormat.RGBA32,false,true);
            readback.ReadPixels(new Rect(0,0,size,size),0,0);readback.Apply();var ids=readback.GetPixels32();
            if(ids.Count(c=>c.a>0)<100)throw new InvalidOperationException("Model ID pass rendered empty.");
            var masks=new Dictionary<int,float[]>();
            foreach(var partId in slots.Keys)masks[partId]=new float[resolution*resolution];
            for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            {
                var pixel=ids[y*size+x];int partId=pixel.r+256*pixel.g;if(pixel.a==0||!masks.TryGetValue(partId,out var mask))continue;
                int py=SystemInfo.graphicsUVStartsAtTop?size-1-y:y;
                mask[(py/supersample)*resolution+x/supersample]+=1f/(supersample*supersample);
            }
            var coverage=new Dictionary<string,Pixel[]>();var temporary=new float[resolution*resolution];
            foreach(var entry in masks)
            {
                var mask=entry.Value;SmoothMask(mask,temporary,resolution,groups[entry.Key].Weapon?1:3);
                var pixels=new List<Pixel>();
                for(int y=1;y<resolution-1;y++)for(int x=1;x<resolution-1;x++)
                {
                    int index=y*resolution+x;float value=mask[index];
                    float fill=Mathf.Clamp01((value-.5f)*4f+.5f);float line=0;
                    if(value>=.02f&&value<=.98f)
                    {
                        float gx=(mask[index+1]-mask[index-1])*.5f,gy=(mask[index+resolution]-mask[index-resolution])*.5f;
                        float gradient=Mathf.Sqrt(gx*gx+gy*gy);
                        if(gradient>=.0001f)line=Mathf.Clamp01(1.25f-Mathf.Abs(value-.5f)/gradient);
                    }
                    if(fill>0||line>0)pixels.Add(new Pixel{Index=index,Fill=(byte)Mathf.RoundToInt(fill*255),Line=(byte)Mathf.RoundToInt(line*255)});
                }
                coverage[slots[entry.Key]]=pixels.ToArray();
            }
            return new MechDamageDiagramRenderer(resolution,coverage);
        }
        finally
        {
            RenderTexture.active=previous;if(readback!=null)UnityEngine.Object.DestroyImmediate(readback);
            if(target!=null){target.Release();UnityEngine.Object.DestroyImmediate(target);}foreach(var mesh in baked)UnityEngine.Object.DestroyImmediate(mesh);
            UnityEngine.Object.DestroyImmediate(material);UnityEngine.Object.DestroyImmediate(cameraObject);UnityEngine.Object.DestroyImmediate(root);
        }
    }
}
}
