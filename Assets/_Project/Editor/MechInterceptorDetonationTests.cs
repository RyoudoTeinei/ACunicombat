using System.Collections;
using System.Reflection;
using ACunicombat.Mechs;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
public sealed class MechInterceptorDetonationTests
{
    [UnityTest] public IEnumerator WarheadDamagesTargetAtActualDetonationPoint()=>WarheadUsesActualDetonationPoint(95,true);
    [UnityTest] public IEnumerator WarheadDoesNotDamageDistantTarget()=>WarheadUsesActualDetonationPoint(0,false);
    private IEnumerator WarheadUsesActualDetonationPoint(float detonationX,bool expectDamage)
    {
        var interceptor=new GameObject("Interceptor");var target=new GameObject("Incoming missile");
        yield return new EnterPlayMode();
        try
        {
            var round=interceptor.AddComponent<MechTestProjectile>();Prepare(round,true);
            var incoming=target.AddComponent<MechTestProjectile>();Prepare(incoming,false);
            target.transform.position=new Vector3(100,0,0);
            try
            {
                typeof(MechTestProjectile).GetMethod("DetonateInterceptorWarhead",BindingFlags.Instance|BindingFlags.NonPublic)
                    .Invoke(round,new object[]{new Vector3(detonationX,0,0),0f});
            }
            catch(System.Exception e){Debug.LogException(e);throw;}
            float health=(float)typeof(MechTestProjectile).GetField("interceptionHealth",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(incoming);
            Assert.AreEqual(expectDamage,health<80,$"Detonation at {detonationX} must be evaluated from that point, not the interceptor transform.");
        }
        finally{Object.DestroyImmediate(interceptor);Object.DestroyImmediate(target);}
        yield return new ExitPlayMode();
    }
    private static void Prepare(MechTestProjectile projectile,bool interceptor)
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(MechTestProjectile);
        Set(type,flags,projectile,"weaponKind",TestWeaponKind.Missile);
        Set(type,flags,projectile,"interceptorMissile",interceptor);
        Set(type,flags,projectile,"engagementState",MissileEngagementState.Active);
        Set(type,flags,projectile,"interceptionHealth",80f);
        Set(type,flags,projectile,"warheadLethalRadius",20f);
        Set(type,flags,projectile,"damage",100f);
        Set(type,flags,projectile,"collisionRadius",.2f);
        Set(type,flags,projectile,"lifeRemaining",20f);
        Set(type,flags,projectile,"configured",true);
        Set(type,flags,projectile,"impactEventId",System.Guid.NewGuid());
    }
    private static void Set(System.Type type,BindingFlags flags,MechTestProjectile target,string name,object value)
    {
        var field=type.GetField(name,flags);if(field==null)throw new System.Exception("Missing field: "+name);field.SetValue(target,value);
    }
}
