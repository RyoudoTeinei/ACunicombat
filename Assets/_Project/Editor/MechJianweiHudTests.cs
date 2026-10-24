using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
public class MechJianweiHudTests
{
    [UnityTest] public IEnumerator SavedSceneShowsEightLoadedPlusFourReserve()
    {
        EditorSceneManager.OpenScene("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");
        typeof(MechPlayableCombatContinuityReview).GetMethod("ConfigureView",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);
        yield return new EnterPlayMode();
        string output=Path.GetFullPath("Artifacts/Jianwei7-20261022/hud");Directory.CreateDirectory(output);
        float scale=Time.timeScale,previous=Time.captureDeltaTime;Time.timeScale=1;Time.captureDeltaTime=1f/30;
        MechLoadoutDefinition copy=null;
        try
        {
            var rig=Object.FindObjectsByType<MechPlayerRig>(FindObjectsSortMode.None).First(r=>r.name.StartsWith("Player_"));
            var assembler=rig.GetComponent<MechAssembler>();copy=Object.Instantiate(assembler.Loadout);
            var left=copy.GetSlots().First(s=>s.legacyRole==MechPartSlot.LeftBackWeapon);
            Assert.IsTrue(copy.TrySetPart(left.id,Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_L_4252"),out string reason),reason);
            assembler.Loadout=copy;assembler.Assemble();
            var menu=rig.GetComponent<MechAssemblyMenu>();typeof(MechAssemblyMenu).GetMethod("SetVisible",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(menu,new object[]{false});
            Time.timeScale=1;double start=Time.timeAsDouble;float deadline=Time.realtimeSinceStartup+20;
            while(Time.timeAsDouble-start<8&&Time.realtimeSinceStartup<deadline)yield return null;
            var weapons=rig.GetComponent<MechTestWeaponSystem>();Assert.IsTrue(weapons.TryGetWeaponHudState(MechPartSlot.LeftBackWeapon,out var state));
            Assert.AreEqual(8,state.RoundsRemaining);Assert.AreEqual(8,state.MagazineCapacity);
            string status=weapons.GetMissileBankStatus(MechPartSlot.LeftBackWeapon);Assert.That(status,Does.Contain("备4/总12"));
            File.WriteAllText(output+"/state.txt",status);
            ScreenCapture.CaptureScreenshot(output+"/4252-hud.png");yield return null;yield return null;
            Assert.IsTrue(File.Exists(output+"/4252-hud.png"));
        }
        finally{Time.captureDeltaTime=previous;Time.timeScale=scale;if(copy!=null)Object.Destroy(copy);}
        yield return new ExitPlayMode();
    }
}
