using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

public class MechReleaseHudSceneTests
{
    [UnityTest] public IEnumerator PlayableSceneUsesChineseModesAndReadOnlyReleasePreview()
    {
        EditorSceneManager.OpenScene("Assets/_Project/LocalSamples/AC6SkinnedModular/Generated/Scenes/AC6PlayerControl.unity");
        typeof(MechPlayableCombatContinuityReview).GetMethod("ConfigureView",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,null);
        yield return new EnterPlayMode();
        // Coroutine locals initialized before domain reload are not preserved by
        // the EditMode runner. Resolve the artifact path after entering play mode.
        string output=System.Environment.GetEnvironmentVariable("ACUNICOMBAT_RELEASE_HUD_OUTPUT")??"Artifacts/PassiveRelease-20261022/scene";
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);
        float previous=Time.captureDeltaTime;Time.captureDeltaTime=1f/30;
        try
        {
            var rig=Object.FindObjectsByType<MechPlayerRig>(FindObjectsSortMode.None).First(r=>r.name.StartsWith("Player_"));
            var radar=rig.GetComponent<MechRadarController>();var hud=rig.GetComponent<MechRadarHudPresenter>();
            var menu=rig.GetComponent<MechAssemblyMenu>();if(menu!=null)typeof(MechAssemblyMenu).GetMethod("SetVisible",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(menu,new object[]{false});
            var label=typeof(MechRadarHudPresenter).GetMethod("TargetModeLabel",BindingFlags.Instance|BindingFlags.NonPublic);
            var tracks=new TrackSnapshot[96];double start=radar.SimTimeSeconds;int checkedLabels=0;bool off=false,onImage=false,offImage=false;
            using(var log=new StreamWriter(output+"/labels.jsonl"))
            while(radar.SimTimeSeconds-start<12)
            {
                yield return null;double t=radar.SimTimeSeconds-start;
                if(t>6&&!off){radar.SetRadarPowerEnabled(false);off=true;}
                int count=radar.CopyTracks(tracks);
                for(int i=0;i<count;i++)
                {
                    if(tracks[i].ObjectClass!=TrackObjectClass.AirVehicle)continue;
                    int channels=radar.GetStatusSnapshot().GuidanceChannelsUsed;
                    string text=(string)label.Invoke(hud,new object[]{tracks[i]});
                    Assert.That(text,Does.Not.Contain("TWS").And.Not.Contain("STT"));
                    Assert.That(text.StartsWith("搜索")||text.StartsWith("跟踪")||text.StartsWith("数据链")||text.StartsWith("被动"),Is.True);
                    if(off)Assert.That(text,Does.Not.StartWith("搜索").And.Not.StartWith("跟踪"));
                    Assert.AreEqual(channels,radar.GetStatusSnapshot().GuidanceChannelsUsed,"Drawing HUD must not reserve a launch channel.");
                    log.WriteLine("{\"time\":"+radar.SimTimeSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"label\":\""+text+"\",\"radar\":"+(radar.RadarPowerEnabled?"true":"false")+"}");checkedLabels++;
                }
                if(t>4&&!onImage){ScreenCapture.CaptureScreenshot(output+"/radar-on.png");onImage=true;}
                if(t>10&&!offImage){ScreenCapture.CaptureScreenshot(output+"/radar-off.png");offImage=true;}
            }
            yield return null;yield return null;
            Assert.Greater(checkedLabels,20);Assert.IsFalse(radar.RadarPowerEnabled);
            Assert.IsTrue(File.Exists(output+"/radar-on.png"));Assert.IsTrue(File.Exists(output+"/radar-off.png"));
            // Exercise selection against the actual fused contacts from this scene,
            // independently of their weapon readiness and the old STT selection.
            int finalCount=radar.CopyTracks(tracks);
            var selectable=tracks.Take(finalCount).Where(t=>t.ObjectClass==TrackObjectClass.AirVehicle &&
                t.Lifecycle==TrackLifecycle.Confirmed && t.Affiliation!=TrackAffiliation.Friendly).Take(2).ToArray();
            Assert.AreEqual(2,selectable.Length,"Scene must supply two real remote aircraft for reticle handoff.");
            var cameraObject=new GameObject("Designation test camera");
            try
            {
                var camera=cameraObject.AddComponent<Camera>();camera.enabled=false;
                camera.transform.position=rig.transform.position;
                var selectedField=typeof(MechRadarController).GetField("candidateTrackId",BindingFlags.Instance|BindingFlags.NonPublic);
                var sttField=typeof(MechRadarController).GetField("sttTrackId",BindingFlags.Instance|BindingFlags.NonPublic);
                var red=typeof(MechRadarHudPresenter).GetMethod("IsPlayerDesignated",BindingFlags.Instance|BindingFlags.NonPublic);
                for(int index=0;index<2;index++)
                {
                    var p=radar.PredictTrackPosition(selectable[index]);
                    camera.transform.LookAt(p);
                    // Old tracking must not freeze reticle selection on the previous contact.
                    if(index==1)sttField.SetValue(radar,selectable[0].TrackId);
                    radar.UpdateDesignation(camera,new Vector2(.5f,.5f),0);
                    Assert.AreEqual(selectable[index].TrackId,radar.CandidateTrackId);
                    Assert.IsTrue((bool)red.Invoke(hud,new object[]{selectable[index]}));
                    Assert.IsFalse((bool)red.Invoke(hud,new object[]{selectable[1-index]}));
                    string before=(string)label.Invoke(hud,new object[]{selectable[index]});
                    selectedField.SetValue(radar,default(TrackId));
                    Assert.IsFalse((bool)red.Invoke(hud,new object[]{selectable[index]}));
                    Assert.AreEqual(before,(string)label.Invoke(hud,new object[]{selectable[index]}),"Designation must not add/remove the release-ready suffix.");
                    selectedField.SetValue(radar,selectable[index].TrackId);
                }
                Assert.IsFalse(radar.HasStt,"Radar-off reticle handoff must not retain stale STT.");
            }
            finally{Object.Destroy(cameraObject);}
            File.WriteAllText(output+"/scope.txt","12 simulated seconds in saved playable scene; normal AI/sensor/radio paths; own radar switched off after 6s. HUD labels and no-reservation contract checked. Not a full 120s combat continuity acceptance.");
        }
        finally{Time.captureDeltaTime=previous;}
        yield return new ExitPlayMode();
    }
}
