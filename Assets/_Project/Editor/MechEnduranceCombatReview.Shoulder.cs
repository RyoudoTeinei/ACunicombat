using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using UnityEngine;
using ACunicombat.Mechs;
public static partial class MechEnduranceCombatReview
{
 static readonly HashSet<string> shoulderSeen=new();
 [Serializable] class ShoulderRow{public double time;public string shot;public string slot,owner;public float flightAge,birthToMuzzle,displayError;public Vector3 muzzle,physics,display,velocity;}
 static void AuditShoulderMuzzle(string dir)
 {
  if(shoulderSeen.Count>=128)return;
  foreach(var projectile in MechTestProjectile.Registry){
   if(projectile==null||projectile.IsMissile||shoulderSeen.Contains(projectile.GetEntityId().ToString()))continue;
   var owner=ownerField.GetValue(projectile) as Transform;if(owner==null)continue;
   var runtime=owner.GetComponent<MechRuntimeState>();if(runtime==null)continue;
   var record=runtime.AssembledParts.FirstOrDefault(r=>r.SlotId==projectile.SourceSlotId);if(record==null||record.part.WeaponPerformance.role!=MechWeaponRole.PointDefenseCannon)continue;
   var muzzle=record.instance.GetComponentsInChildren<Transform>().FirstOrDefault(t=>t.name.EndsWith("_dmy_300"));if(muzzle==null)continue;
   var presentation=projectile.transform.Find("TracerPresentation");if(presentation==null)continue;
   shoulderSeen.Add(projectile.GetEntityId().ToString());float age=(float)typeof(MechTestProjectile).GetField("projectileFlightTime",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(projectile);
   var row=new ShoulderRow{time=Time.timeAsDouble,shot=projectile.GetEntityId().ToString(),slot=record.SlotId,owner=owner.name,flightAge=age,muzzle=muzzle.position,physics=projectile.transform.position,display=presentation.position,velocity=projectile.Velocity,displayError=Vector3.Distance(presentation.position,projectile.PositionAtPhysicsTime(Time.timeAsDouble)),birthToMuzzle=age<.0001f?Vector3.Distance(muzzle.position,projectile.transform.position):-1};
   File.AppendAllText(dir+"/shoulder-muzzle.jsonl",JsonUtility.ToJson(row)+"\n");
   if(shoulderSeen.Count<=4&&!UnityEditor.SessionState.GetBool("EnduranceCombat.headless",false)){
    var view=new GameObject("Shoulder shot review").AddComponent<Camera>();view.enabled=false;view.nearClipPlane=.1f;view.farClipPlane=1000000;view.fieldOfView=55;view.transform.position=muzzle.position+owner.right*12+owner.up*4-owner.forward*12;view.transform.LookAt(muzzle.position+projectile.Velocity.normalized*2);
    typeof(MechColdLaunchPlacementV2Tests).GetMethod("Capture",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{view,dir+"/shoulder-"+shoulderSeen.Count+".png"});UnityEngine.Object.Destroy(view.gameObject);
   }
  }
 }
}
