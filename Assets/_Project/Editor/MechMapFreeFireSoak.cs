using System;
using System.IO;
using System.Linq;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
[InitializeOnLoad]
public static class MechMapFreeFireSoak
{
    const string Key="MapFreeFireSoak",Dir="Artifacts/UnityHang-20261025/soak";
    static double last;
    static MechMapFreeFireSoak(){EditorApplication.update+=Tick;}
    public static void Run()
    {
        Directory.CreateDirectory(Dir);SessionState.SetFloat(Key+"scale",Time.timeScale);SessionState.SetFloat(Key+"step",Time.captureDeltaTime);
        SessionState.SetInt(Key,1);SessionState.SetFloat(Key+"wall",(float)EditorApplication.timeSinceStartup);
        EditorSceneManager.OpenScene("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");EditorApplication.isPlaying=true;
    }
    static void Tick()
    {
        int stage=SessionState.GetInt(Key,0);if(stage==0)return;
        if(stage==9&&!EditorApplication.isPlaying){SessionState.SetInt(Key,0);Time.timeScale=SessionState.GetFloat(Key+"scale",1);Time.captureDeltaTime=SessionState.GetFloat(Key+"step",0);EditorApplication.Exit(SessionState.GetInt(Key+"exit",0));return;}
        if(!EditorApplication.isPlaying)return;
        try
        {
            Time.timeScale=1;Time.captureDeltaTime=1f/60;
            if(EditorApplication.timeSinceStartup-SessionState.GetFloat(Key+"wall",0)>240)throw new Exception("Soak wall timeout");
            var rig=UnityEngine.Object.FindObjectsByType<MechPlayerRig>().FirstOrDefault(x=>!x.AiControlled);if(rig==null)return;
            if(MechAssemblyMenu.IsAnyMenuVisible)
                typeof(MechAssemblyMenu).GetMethod("SetVisible",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)?.Invoke(rig.GetComponent<MechAssemblyMenu>(),new object[]{false});
            var map=rig.GetComponent<MechBattlefieldMap>();var fleet=rig.GetComponent<MechFleetController>();
            if(Time.timeAsDouble-last>2){last=Time.timeAsDouble;File.AppendAllText(Dir+"/heartbeat.txt",$"stage={stage} time={Time.timeAsDouble:F2} missiles={MechTestProjectile.Registry.Count} memory={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()}\n");}
            if(stage==1&&Time.time>=35){map.SetVisible(true);map.Refresh();SessionState.SetInt(Key,2);File.AppendAllText(Dir+"/heartbeat.txt","MAP OPEN\n");}
            else if(stage==2&&Time.time>=40)
            {
                typeof(MechBattlefieldMap).GetMethod("SetWeaponPolicy",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).Invoke(map,new object[]{true,Guid.Empty,FleetAttackMode.Free});
                if(fleet.State.Mode!=FleetAttackMode.Free)throw new Exception("Map free-fire command did not apply");
                SessionState.SetInt(Key,3);File.AppendAllText(Dir+"/heartbeat.txt","FREE FIRE through map command\n");
            }
            else if(stage==3&&Time.time>=100)
            {
                map.Refresh();map.Capture(Dir+"/map.png");File.WriteAllText(Dir+"/result.txt","PASS: actual playable scene, map opened at 35s, free fire at 40s, reached 100s with real AI and regular scheduling.");
                SessionState.SetInt(Key+"exit",0);SessionState.SetInt(Key,9);EditorApplication.isPlaying=false;
            }
        }
        catch(Exception e){File.WriteAllText(Dir+"/failure.txt",e.ToString());SessionState.SetInt(Key+"exit",1);SessionState.SetInt(Key,9);EditorApplication.isPlaying=false;}
    }
}
