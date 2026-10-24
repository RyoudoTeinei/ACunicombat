using System.Collections;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ACunicombat.SensorBench
{
    public sealed class SceneEntityPlayModeTests
    {
        [UnityTest] public IEnumerator ActualGameObjectLifecycleRetiresObserverBindings()
        {
            UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Single);
            Time.timeScale=1f;
            yield return new EnterPlayMode();
            var actor = new GameObject("P1 lifetime runtime probe");
            var driverObject = new GameObject("P1 observer runtime probe");
            var driver = driverObject.AddComponent<SimulationDriver>();
            var owner = BattlefieldEntityIdentity.GetOrAdd(actor);
            var initial = owner.Identity;
            var lease = driver.RegisterSceneEntity(owner, default);
            actor.SetActive(false);
            yield return new WaitForFixedUpdate();
            var inactiveRetired = !driver.IsSceneEntityCurrent(in lease);
            actor.SetActive(true);
            var reborn = owner.Identity;
            var fresh = driver.RegisterSceneEntity(owner, default);
            var oldCannotRelease = !driver.ReleaseSceneEntity(in lease);
            var freshSurvives = driver.IsSceneEntityCurrent(in fresh);
            Object.Destroy(actor);
            yield return null;
            yield return new WaitForFixedUpdate();
            var destroyedRetired = !driver.IsSceneEntityCurrent(in fresh);
            Object.Destroy(driverObject);
            yield return null;
            Assert.That(inactiveRetired, Is.True);
            Assert.That(reborn, Is.Not.EqualTo(initial));
            Assert.That(oldCannotRelease && freshSurvives && destroyedRetired, Is.True);
            yield return new ExitPlayMode();
        }
    }
}