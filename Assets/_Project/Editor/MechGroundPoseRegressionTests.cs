using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
public sealed class MechGroundPoseRegressionTests
{
    [Test] public void ReassembledModelReceivesCurrentLocomotionInsteadOfKeepingImportPose()
    {
        var ground=AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/a000_020200/AC6_a000_020200_LG.anim");
        Assert.That(ground,Is.Not.Null);
        var owner=new GameObject("Reassembled rig");owner.SetActive(false);
        var first=new GameObject("Old frame");var second=new GameObject("New frame");
        var part=ScriptableObject.CreateInstance<MechPartDefinition>();var loadout=ScriptableObject.CreateInstance<MechLoadoutDefinition>();var registry=ScriptableObject.CreateInstance<MechActionRegistry>();
        try
        {
            foreach(var frame in new[]{first,second})
            {
                frame.AddComponent<SkinnedMeshRenderer>();
                foreach(var path in AnimationUtility.GetCurveBindings(ground).Select(x=>x.path).Distinct())
                {
                    var parent=frame.transform;foreach(var piece in path.Split('/')){if(string.IsNullOrEmpty(piece))continue;var child=parent.Find(piece);if(child==null){child=new GameObject(piece).transform;child.SetParent(parent,false);}parent=child;}
                }
            }
            var so=new SerializedObject(part);so.FindProperty("visualRigMode").intValue=10;so.ApplyModifiedPropertiesWithoutUndo();
            registry.EditorActions.Add(new MechBodyActionDefinition{actionId="slide",loop=true,slotClips=new List<MechBodyActionDefinition.SlotClip>{new MechBodyActionDefinition.SlotClip{slot=MechPartSlot.Legs,clip=ground}}});
            var runtime=owner.AddComponent<MechRuntimeState>();
            runtime.Apply(loadout,new MechAggregatedStats(),null,new List<MechAssemblyRecord>{new MechAssemblyRecord{slot=MechPartSlot.Legs,part=part,instance=first}},null);
            var player=PlatformFactory.ConfigureBodyPlayback(owner,registry,runtime,Array.Empty<string>(),"slide");
            var rig=owner.AddComponent<MechPlayerRig>();rig.ConfigureActionPlayback(registry,Array.Empty<string>());
            var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            typeof(MechPlayerRig).GetField("runtimeState",flags).SetValue(rig,runtime);typeof(MechPlayerRig).GetField("actionPlayer",flags).SetValue(rig,player);
            runtime.Apply(loadout,new MechAggregatedStats(),null,new List<MechAssemblyRecord>{new MechAssemblyRecord{slot=MechPartSlot.Legs,part=part,instance=second}},null);
            Assert.That((bool)typeof(MechPlayerRig).GetMethod("EnsureActionPlayer",flags).Invoke(rig,null),Is.True);
            Assert.That(second.GetComponent<Animator>(),Is.Not.Null,"New assembled frame must have the authoritative animation output.");
            Assert.That(player.IsBoundTo(runtime),Is.True);Assert.That(player.ActiveFullBodyActionId,Is.EqualTo("slide"));
        }
        finally{UnityEngine.Object.DestroyImmediate(owner);UnityEngine.Object.DestroyImmediate(first);UnityEngine.Object.DestroyImmediate(second);UnityEngine.Object.DestroyImmediate(part);UnityEngine.Object.DestroyImmediate(loadout);UnityEngine.Object.DestroyImmediate(registry);}
    }

    [Test] public void WeaponArmOverlayMustNotOverrideGroundSlideRootOrPelvis()
    {
        string rootPath="Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Actions/";
        var ground=AssetDatabase.LoadAssetAtPath<AnimationClip>(rootPath+"a000_020200/AC6_a000_020200_LG.anim");
        var weapon=AssetDatabase.LoadAssetAtPath<AnimationClip>(rootPath+"a200_030020/AC6_a200_030020_LG.anim");
        Assert.That(ground,Is.Not.Null);Assert.That(weapon,Is.Not.Null);
        var rig=new GameObject("Ground pose skeleton");var registry=ScriptableObject.CreateInstance<MechActionRegistry>();
        var nodes=new Dictionary<string,Transform>{{"",rig.transform}};
        try
        {
            foreach(var path in AnimationUtility.GetCurveBindings(ground).Concat(AnimationUtility.GetCurveBindings(weapon)).Select(b=>b.path).Distinct())
            {
                string full="";var parent=rig.transform;foreach(var piece in path.Split('/'))
                {if(string.IsNullOrEmpty(piece))continue;full=full.Length==0?piece:full+"/"+piece;if(!nodes.TryGetValue(full,out var node)){node=new GameObject(piece).transform;node.SetParent(parent,false);nodes.Add(full,node);}parent=node;}
            }
            rig.AddComponent<SkinnedMeshRenderer>();
            registry.EditorActions.Add(new MechBodyActionDefinition{actionId="slide",loop=true,blendInSeconds=.01f,slotClips=new List<MechBodyActionDefinition.SlotClip>{new MechBodyActionDefinition.SlotClip{slot=MechPartSlot.Legs,clip=ground}}});
            registry.EditorActions.Add(new MechBodyActionDefinition{actionId="arm",layer=MechBodyActionLayer.UpperBody,loop=true,blendInSeconds=.01f,slotClips=new List<MechBodyActionDefinition.SlotClip>{new MechBodyActionDefinition.SlotClip{slot=MechPartSlot.Legs,clip=weapon}}});
            var upper=nodes.Values.Select(t=>t.name).Where(n=>n.Contains("R_")||n.Contains("L_")||n=="Spine1").Where(n=>!n.Contains("Thigh")&&!n.Contains("Coxa")&&!n.Contains("Calf")&&!n.Contains("Foot")).ToArray();
            var player=rig.AddComponent<MechBodyActionPlayer>();var parts=new[]{new MechBodyActionPart(MechPartSlot.Legs,rig,MechPartSlot.None,Array.Empty<string>())};
            ground.SampleAnimation(rig,.8f);
            var expected=nodes.ToDictionary(x=>x.Key,x=>x.Value.localRotation);
            Assert.That(expected.Values.Any(q=>Quaternion.Angle(q,Quaternion.identity)>10),Is.True,"Clip must contain a nontrivial pose.");
            foreach(var node in nodes.Values)node.localRotation=Quaternion.identity;
            player.Configure(registry,parts,upper,"slide");player.EvaluateAtTime(.8);
            foreach(var pair in expected)Assert.That(Quaternion.Angle(nodes[pair.Key].localRotation,pair.Value),Is.LessThan(.15),"Base clip not applied: "+pair.Key);
            var lower=nodes.Where(x=>x.Key=="Master/Root"||x.Value.name=="Pelvis"||x.Value.name=="Pelvis_Rot_XYZ"||x.Value.name.StartsWith("Spine")).ToDictionary(x=>x.Key,x=>(x.Value.localPosition,x.Value.localRotation));
            Assert.That(lower.Count,Is.GreaterThanOrEqualTo(2));
            player.Configure(registry,parts,upper,"slide");player.EvaluateAtTime(.1);Assert.That(player.PlayRightArm("arm"),Is.True);player.EvaluateAtTime(.8);
            foreach(var pair in lower)
            {
                Assert.That(Vector3.Distance(nodes[pair.Key].localPosition,pair.Value.localPosition),Is.LessThan(.001),pair.Key+" translated by weapon overlay");
                Assert.That(Quaternion.Angle(nodes[pair.Key].localRotation,pair.Value.localRotation),Is.LessThan(.1),pair.Key+" rotated by weapon overlay");
            }
        }
        finally{UnityEngine.Object.DestroyImmediate(rig);UnityEngine.Object.DestroyImmediate(registry);}
    }
}
