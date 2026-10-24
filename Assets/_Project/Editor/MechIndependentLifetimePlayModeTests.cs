using System.Collections;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Simulation.Driver;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
public sealed class MechIndependentLifetimePlayModeTests
{
    [UnityTest] public IEnumerator LateJoinersShareTimeAndReleasedChaffOutlivesItsDispenser()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);Time.timeScale=1;
        yield return new EnterPlayMode();
        var a=new GameObject("Early observer");var first=a.AddComponent<SimulationDriver>();
        var waitUntil=Time.fixedTime+.08f;var deadline=Time.realtimeSinceStartup+5;
        while(Time.fixedTime<waitUntil && Time.realtimeSinceStartup<deadline)yield return null;
        var b=new GameObject("Late observer");var second=b.AddComponent<SimulationDriver>();
        Assert.That(second.SimTimeSeconds,Is.EqualTo(first.SimTimeSeconds).Within(1e-9));
        var dispenserObject=new GameObject("Disposable dispenser");var dispenser=dispenserObject.AddComponent<MechCountermeasureController>();
        typeof(MechCountermeasureController).GetField("chaffLifetimeSeconds",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(dispenser,.6f);
        typeof(MechCountermeasureController).GetMethod("RegisterChaffCloud",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(dispenser,new object[]{Vector3.forward*100,Vector3.right});
        var signal=Object.FindAnyObjectByType<MechRadarDecoySignal>();Assert.That(signal,Is.Not.Null);
        Object.Destroy(dispenserObject);yield return null;
        Assert.That(signal!=null && signal.SignalActive,Is.True,"Released cloud must not be owned by the destroyed dispenser.");
        deadline=Time.realtimeSinceStartup+5;
        while(signal!=null && Time.realtimeSinceStartup<deadline)yield return null;
        Assert.That(signal==null,Is.True,"Independent cloud must expire on its own lifetime.");
        Object.Destroy(a);Object.Destroy(b);yield return null;
        yield return new ExitPlayMode();EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
    }
}