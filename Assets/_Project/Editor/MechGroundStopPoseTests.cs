using System;
using System.Linq;
using System.Collections.Generic;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
public sealed class MechGroundStopPoseTests
{
    const string Root="Assets/_Project/LocalSamples/AC6SkinnedModular/";
    [TestCase("LG",MechPartSlot.Legs)] [TestCase("BD",MechPartSlot.Core)]
    [TestCase("AM",MechPartSlot.Arms)] [TestCase("HD",MechPartSlot.Head)]
    public void SlideThenStopRestoresAuthoredIdleAfterActualImportedPreview(string token,MechPartSlot slot)
    {
        string modelPath=Root+"Raw/Gltf/"+token+"_M_1010.glb";
        var model=AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);Assert.That(model,Is.Not.Null);
        var idle=Clip("a000_000000",token);var slide=Clip("a000_020200",token);
        var preview=AssetDatabase.LoadAllAssetsAtPath(modelPath).OfType<AnimationClip>().FirstOrDefault();Assert.That(preview,Is.Not.Null);
        var expected=UnityEngine.Object.Instantiate(model);var actual=UnityEngine.Object.Instantiate(model);var registry=ScriptableObject.CreateInstance<MechActionRegistry>();
        try
        {
            idle.SampleAnimation(expected,1.2f);
            var expectedBones=expected.GetComponentsInChildren<Transform>(true).ToDictionary(t=>AnimationUtility.CalculateTransformPath(t,expected.transform),t=>t.localRotation);
            // This is the actual assembler startup sequence: sample the imported preview first.
            preview.SampleAnimation(actual,0);
            registry.EditorActions.Add(Action("slide",slide,slot,.4f));registry.EditorActions.Add(Action("idle",idle,slot,1));
            var player=actual.AddComponent<MechBodyActionPlayer>();player.Configure(registry,new[]{new MechBodyActionPart(slot,actual,MechPartSlot.None,Array.Empty<string>())},Array.Empty<string>(),"slide");
            player.EvaluateAtTime(.8);Assert.That(player.PlayFullBody("idle"),Is.True);player.EvaluateAtTime(2.0);
            var errors=new List<string>();double worst=0;
            foreach(var bone in actual.GetComponentsInChildren<Transform>(true))
            {
                string path=AnimationUtility.CalculateTransformPath(bone,actual.transform);if(!expectedBones.TryGetValue(path,out var rotation))continue;
                float error=Quaternion.Angle(bone.localRotation,rotation);worst=Math.Max(worst,error);if(error>.2f)errors.Add(path+" error="+error);
            }
            TestContext.WriteLine($"{token}: preview={preview.name}, idle={idle.name}, maximum angular error={worst:F3} deg");
            Assert.That(errors,Is.Empty,string.Join("\n",errors.Take(20)));
        }
        finally{UnityEngine.Object.DestroyImmediate(actual);UnityEngine.Object.DestroyImmediate(expected);UnityEngine.Object.DestroyImmediate(registry);}
    }
    static AnimationClip Clip(string action,string token)=>AssetDatabase.LoadAssetAtPath<AnimationClip>(Root+"Generated/Actions/"+action+"/AC6_"+action+"_"+token+".anim");
    static MechBodyActionDefinition Action(string name,AnimationClip clip,MechPartSlot slot,float fade)=>new MechBodyActionDefinition{actionId=name,loop=true,blendInSeconds=fade,slotClips=new List<MechBodyActionDefinition.SlotClip>{new MechBodyActionDefinition.SlotClip{slot=slot,clip=clip}}};
    [UnityEngine.TestTools.UnityTest]
    public System.Collections.IEnumerator RuntimeSlideStopUsesIdlePose()
    {
        UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,UnityEditor.SceneManagement.NewSceneMode.Single);
        yield return new UnityEngine.TestTools.EnterPlayMode();
        foreach(var token in new[]{"LG","BD","AM","HD"})
        {
            var slot=token=="LG"?MechPartSlot.Legs:token=="BD"?MechPartSlot.Core:token=="AM"?MechPartSlot.Arms:MechPartSlot.Head;
            string path=Root+"Raw/Gltf/"+token+"_M_1010.glb";var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var actual=UnityEngine.Object.Instantiate(prefab);var expected=UnityEngine.Object.Instantiate(prefab);var registry=ScriptableObject.CreateInstance<MechActionRegistry>();
            try
            {
                var preview=AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First();
                var legacy=actual.AddComponent<MechSynchronizedAnimationPlayer>();legacy.Configure(preview,new[]{actual},true,true,1);
                yield return null;
                legacy.StopPlayback();legacy.enabled=false;
                var idle=Clip("a000_000000",token);var slide=Clip("a000_020200",token);
                registry.EditorActions.Add(Action("a000_020200",slide,slot,.4f));registry.EditorActions.Add(Action("a000_000000",idle,slot,1));
                var player=actual.AddComponent<MechBodyActionPlayer>();player.Configure(registry,new[]{new MechBodyActionPart(slot,actual,MechPartSlot.None,Array.Empty<string>())},Array.Empty<string>(),"a000_020200");
                var graph=AssetDatabase.LoadAssetAtPath<MechBehaviorGraphAsset>(Root+"Generated/AC6BehaviorGraph.asset");Assert.That(graph,Is.Not.Null);
                var runner=actual.AddComponent<MechBehaviorGraphRunner>();runner.Configure(graph,player);
                int slideIndex=Array.FindIndex(graph.states,x=>x.officialName=="地上ブースト");
                for(int cycle=0;cycle<3;cycle++)
                {
                    player.EvaluateAtTime(cycle*4);runner.TryEnterState(slideIndex);yield return null;player.EvaluateAtTime(cycle*4+.8);
                    var input=actual.GetComponent<MechPlayerInputAdapter>()??actual.AddComponent<MechPlayerInputAdapter>();
                    var stopped=default(MechPlayerInputFrame);var context=new MechGraphContext{PadBits=input.BuildPadBits(stopped,false),Grounded=true,CharControlFlags=31};
                    runner.Tick(in context,.04f);Assert.That(player.ActiveFullBodyActionId,Is.EqualTo("a000_000000"));
                    yield return null;player.EvaluateAtTime(cycle*4+2);
                idle.SampleAnimation(expected,1.2f);
                var expectedBones=expected.GetComponentsInChildren<Transform>(true).ToDictionary(t=>AnimationUtility.CalculateTransformPath(t,expected.transform),t=>t.localRotation);
                var errors=new List<string>();
                foreach(var bone in actual.GetComponentsInChildren<Transform>(true)){string key=AnimationUtility.CalculateTransformPath(bone,actual.transform);float error=Quaternion.Angle(bone.localRotation,expectedBones[key]);if(error>.2f)errors.Add(key+" "+error);}
                Assert.That(errors,Is.Empty,token+" runtime stop: "+string.Join("\n",errors.Take(20)));
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(actual);UnityEngine.Object.DestroyImmediate(expected);UnityEngine.Object.DestroyImmediate(registry);}
        }
        yield return new UnityEngine.TestTools.ExitPlayMode();
    }
    public static void RebuildIdle()
    {
        string result=AC6ActionLibraryBuilder.BuildReviewAction("a000_000000","FullBody",true);Debug.Log(result);
        EditorApplication.Exit(result.Contains("4/4")?0:1);
    }
}
