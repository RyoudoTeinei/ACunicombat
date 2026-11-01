using System.Linq;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
public class MechShoulderBoreAxisTests
{
 const BindingFlags P=BindingFlags.Instance|BindingFlags.NonPublic;
 [TestCase("L")] [TestCase("R")] public void Native5110RenderedBoreMatchesCommittedDirection(string side)
 {
  var fixture=new MechEquipmentExecutionTests();try{
   var part=Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_"+side+"_5110")??Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_"+side+"_5110");Assert.IsNotNull(part);
   var a=(MechAssembler)typeof(MechEquipmentExecutionTests).GetMethod("Assemble",P).Invoke(fixture,new object[]{1,part});var w=a.gameObject.AddComponent<MechTestWeaponSystem>();w.Configure(null,a.RuntimeState,null,null);
   var record=a.RuntimeState.AssembledParts.First(r=>r.SlotId=="gun.0");var animation=record.instance.GetComponentInChildren<MechEquipmentAnimationPlayer>();animation.SampleActionAtEnd("a000_800110");animation.SampleActionAtTime("a000_800201",.1f);
   var slot=new MechEquipmentSlot("gun.0");var state=typeof(MechTestWeaponSystem).GetMethod("GetShaoWeiState",P).Invoke(w,new object[]{slot});var type=state.GetType();
   // As in the production missing-muzzle fallback, bind after the instance state exists.
   typeof(MechTestWeaponSystem).GetMethod("ResolveMuzzles",P).Invoke(w,null);
   type.GetField("Spinning").SetValue(state,true);type.GetField("HasSlewedDirection").SetValue(state,true);type.GetField("SlewedDirection").SetValue(state,new Vector3(.1f,.1f,1).normalized);
   var muzzle=record.instance.GetComponentsInChildren<Transform>().First(t=>t.name.EndsWith("_dmy_300"));var pivot=(Transform)type.GetField("AimPivot").GetValue(state);Assert.IsNotNull(pivot);Assert.AreSame(muzzle,typeof(MechTestWeaponSystem).GetMethod("PeekShaoWeiMuzzle",P).Invoke(w,new object[]{slot}),"Test must exercise the actually bound firing muzzle");
   TestContext.WriteLine(side+" native pivot-to-muzzle vs bore bias="+Vector3.Angle(muzzle.position-pivot.position,muzzle.forward));
   var apply=typeof(MechTestWeaponSystem).GetMethod("ApplyShaoWeiVisual",P);
   apply.Invoke(w,new object[]{slot,state});var direction=(Vector3)type.GetField("SlewedDirection").GetValue(state);
   Assert.Less(Vector3.Angle(muzzle.forward,direction),.05f,"Render and ballistic directions must use the native muzzle axis, not the offset mount-to-muzzle line");
   var rotation=pivot.rotation;for(int i=0;i<5;i++)apply.Invoke(w,new object[]{slot,state});Assert.Less(Quaternion.Angle(rotation,pivot.rotation),.05f);
  }finally{fixture.Cleanup();}
 }
}
