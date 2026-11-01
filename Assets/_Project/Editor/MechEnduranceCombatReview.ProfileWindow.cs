using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEditorInternal;
using UnityEditor;
public static partial class MechEnduranceCombatReview
{
 static bool profileWindowStarted,profileWindowFinished;
 static string profileWindowPath;
 static double profileWindowWallStart,nextProfileGuard;
 static void SampleCpuWindow(double now,string directory)
 {
  if(Arg("-cpuWindow","0")!="1"||profileWindowFinished)return;
  if(!profileWindowStarted&&now>=8)
  {
   profileWindowPath=Path.Combine(directory,"cpu-window.raw");Profiler.logFile=profileWindowPath;Profiler.enableBinaryLog=true;Profiler.enabled=true;profileWindowStarted=true;
   profileWindowWallStart=EditorApplication.timeSinceStartup;nextProfileGuard=0;
   EditorApplication.update+=GuardCpuWindow;EditorApplication.playModeStateChanged+=ProfilePlayStateChanged;
   AssemblyReloadEvents.beforeAssemblyReload+=StopCpuWindow;EditorApplication.quitting+=StopCpuWindow;
   File.WriteAllLines(Path.Combine(directory,"profiler-api.txt"),typeof(ProfilerDriver).GetMethods(BindingFlags.Public|BindingFlags.Static).Where(m=>m.Name.Contains("FrameData")||m.Name.Contains("LoadProfile")).Select(m=>m.ToString()));
  }
  if(profileWindowStarted&&(now>=28||(File.Exists(profileWindowPath)&&new FileInfo(profileWindowPath).Length>=128L*1024*1024)))StopCpuWindow();
 }
 static void ProfilePlayStateChanged(PlayModeStateChange state){if(state==PlayModeStateChange.ExitingPlayMode)StopCpuWindow();}
 static void GuardCpuWindow()
 {
  double wall=EditorApplication.timeSinceStartup;if(wall<nextProfileGuard)return;nextProfileGuard=wall+.25;
  try{if(wall-profileWindowWallStart>=20||(File.Exists(profileWindowPath)&&new FileInfo(profileWindowPath).Length>=128L*1024*1024))StopCpuWindow();}
  catch(IOException){StopCpuWindow();}
 }
 static void StopCpuWindow()
 {
  if(!profileWindowStarted||profileWindowFinished)return;
  Profiler.enabled=false;Profiler.enableBinaryLog=false;profileWindowFinished=true;
  EditorApplication.update-=GuardCpuWindow;EditorApplication.playModeStateChanged-=ProfilePlayStateChanged;
  AssemblyReloadEvents.beforeAssemblyReload-=StopCpuWindow;EditorApplication.quitting-=StopCpuWindow;
 }
}
