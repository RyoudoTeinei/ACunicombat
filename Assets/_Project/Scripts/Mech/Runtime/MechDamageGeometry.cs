using System;
using System.Collections.Generic;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public static class MechDamageGeometry
    {
        public static bool SplitLimbs(MechAssemblyRecord record) => record.slot==MechPartSlot.Arms || record.slot==MechPartSlot.Legs;
        public static string RegionId(string slot,MechDamageSide side) => side==MechDamageSide.Center?slot:slot+(side==MechDamageSide.Left?"/left":"/right");
        public static float LeftSign(MechRuntimeState state)
        {
            foreach(var record in state.AssembledParts)
                if(record.slot==MechPartSlot.LeftHandWeapon && record.parentMount!=null)
                {float x=state.transform.InverseTransformPoint(record.parentMount.position).x;if(Mathf.Abs(x)>.01f)return Mathf.Sign(x);}
            foreach(var record in state.AssembledParts)
                if(record.slot==MechPartSlot.RightHandWeapon && record.parentMount!=null)
                {float x=state.transform.InverseTransformPoint(record.parentMount.position).x;if(Mathf.Abs(x)>.01f)return -Mathf.Sign(x);}
            return -1;
        }
        public static MechAssemblyRecord Owner(Renderer renderer,MechRuntimeState state)
        {
            for(var t=renderer.transform;t!=null&&t!=state.transform;t=t.parent)
            {
                var part=t.GetComponent<MechAssembledPart>();
                if(part!=null)
                    foreach(var record in state.AssembledParts)if(record.instance==part.gameObject)return record;
                foreach(var record in state.AssembledParts)
                    if(record.instance!=null&&t.name.StartsWith(record.instance.name+"_Visual_",StringComparison.Ordinal))return record;
            }
            return null;
        }
        public sealed class Proxy
        {
            public MechAssemblyRecord Part;public MechDamageSide Side;public Transform Anchor;public Bounds Bounds;public bool HasBounds;
        }
        // Coarse bone-local boxes follow animation. They are collision proxies, not triangle-level penetration geometry.
        public static List<Proxy> BuildProxies(MechRuntimeState state)
        {
            var proxies=new Dictionary<(string,Transform),Proxy>();float leftSign=LeftSign(state);
            var worldToRoot=state.transform.worldToLocalMatrix;
            var inverses=new Dictionary<Transform,Matrix4x4>();
            var ids=new Dictionary<(MechAssemblyRecord,MechDamageSide),string>();
            foreach(var part in state.AssembledParts)if(part!=null)
                foreach(MechDamageSide side in Enum.GetValues(typeof(MechDamageSide)))ids[(part,side)]=RegionId(part.SlotId,side);
            foreach(var renderer in state.GetComponentsInChildren<Renderer>(true))
            {
                var part=Owner(renderer,state);if(part?.instance==null)continue;
                Mesh mesh=null;bool ownsMesh=false;BoneWeight[] weights=null;Transform[] bones=null;
                if(renderer is SkinnedMeshRenderer skinned)
                {
                    mesh=new Mesh();skinned.BakeMesh(mesh);ownsMesh=true;bones=skinned.bones;
                    if(skinned.sharedMesh!=null&&skinned.sharedMesh.isReadable)weights=skinned.sharedMesh.boneWeights;
                }
                else if(renderer is MeshRenderer)mesh=renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if(mesh==null)continue;
                try
                {
                    if(!mesh.isReadable)
                    {
                        var b=renderer.bounds;AddPoint(part,part.instance.transform,b.min);AddPoint(part,part.instance.transform,b.max);continue;
                    }
                    var vertices=mesh.vertices;var matrix=renderer.localToWorldMatrix;
                    var bonePositions=bones!=null?new Vector3[bones.Length]:null;
                    if(bones!=null)for(int b=0;b<bones.Length;b++)if(bones[b]!=null)bonePositions[b]=bones[b].position;
                    int stride=Mathf.Max(1,vertices.Length/12000);
                    for(int i=0;i<vertices.Length;i+=stride)
                    {
                        var world=matrix.MultiplyPoint3x4(vertices[i]);Transform anchor=part.instance.transform;
                        if(weights!=null&&weights.Length==vertices.Length&&bones!=null)
                        {
                            var w=weights[i];int index=w.boneIndex0;float best=w.weight0;
                            if(w.weight1>best){index=w.boneIndex1;best=w.weight1;}if(w.weight2>best){index=w.boneIndex2;best=w.weight2;}if(w.weight3>best)index=w.boneIndex3;
                            if(index>=0&&index<bones.Length&&bones[index]!=null)anchor=bones[index];
                        }
                        else if(bones!=null&&bones.Length>0)
                        {
                            // Some imported meshes discard CPU bone weights. Use a coarse nearest-joint binding instead of a frozen root box.
                            float nearest=float.PositiveInfinity;
                            for(int b=0;b<bones.Length;b++)if(bones[b]!=null)
                            {float distance=(bonePositions[b]-world).sqrMagnitude;if(distance<nearest){nearest=distance;anchor=bones[b];}}
                        }
                        AddPoint(part,anchor,world);
                    }
                }
                finally{if(ownsMesh)UnityEngine.Object.DestroyImmediate(mesh);}
            }
            return new List<Proxy>(proxies.Values);
            void AddPoint(MechAssemblyRecord part,Transform anchor,Vector3 world)
            {
                var side=SplitLimbs(part)?(worldToRoot.MultiplyPoint3x4(world).x*leftSign>=0?MechDamageSide.Left:MechDamageSide.Right):MechDamageSide.Center;
                var key=(ids[(part,side)],anchor);
                if(!proxies.TryGetValue(key,out var proxy)){proxy=new Proxy{Part=part,Side=side,Anchor=anchor};proxies.Add(key,proxy);}
                if(!inverses.TryGetValue(anchor,out var inverse)){inverse=anchor.worldToLocalMatrix;inverses[anchor]=inverse;}
                var point=inverse.MultiplyPoint3x4(world);
                if(!proxy.HasBounds){proxy.Bounds=new Bounds(point,Vector3.zero);proxy.HasBounds=true;}else proxy.Bounds.Encapsulate(point);
            }
        }
    }
}
