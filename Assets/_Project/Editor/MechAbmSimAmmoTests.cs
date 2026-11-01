using System;
using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.FireControl;
using ACunicombat.Sensing.Contracts;
using ACunicombat.Simulation.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
public class MechAbmSimAmmoTests
{
    const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
    [TestCase("ABM-12",12000)] [TestCase("ABM-18",18000)]
    [TestCase("SIM-29",29000)] [TestCase("SIM-29M",35000)]
    [TestCase("SIM-57",57000)] [TestCase("SIM-57R",77000)]
    public void AmmunitionHasRequestedMaximumWithoutOwningLauncherCapacity(string id,int maximum)
    {
        var p=MechMissileAmmunitionCatalog.Create(id);Assert.IsNotNull(p);Assert.AreEqual(id,p.missileDisplayName);
        Assert.AreEqual(maximum,p.maximumRangeMeters);Assert.AreEqual(maximum,p.missileFlightProfile.MaximumRangeMeters);
        Assert.AreEqual(0,p.magazineCapacity);Assert.AreEqual(0,p.magazineCount);
    }
    [TestCase("ABM-12",30)] [TestCase("ABM-12",60)] [TestCase("ABM-18",30)] [TestCase("ABM-18",60)]
    [TestCase("SIM-29",30)] [TestCase("SIM-29",60)] [TestCase("SIM-29M",30)] [TestCase("SIM-29M",60)]
    [TestCase("SIM-57",30)] [TestCase("SIM-57",60)] [TestCase("SIM-57R",30)] [TestCase("SIM-57R",60)]
    public void ProductionPropulsionReachesNominalEnvelope(string id,int hz)=>MechLongRangeMissileTests.AssertFlightEnvelope(MechMissileAmmunitionCatalog.Create(id),id,hz);

    [Test] public void Sim29MHasStrongerActualTurnResponse()
    {
        float ordinary=Turn("SIM-29"),maneuver=Turn("SIM-29M");
        Assert.Greater(maneuver,ordinary*1.2f,"M variant must physically change velocity direction faster under the same target geometry.");
        TestContext.WriteLine("Velocity turn after 1s: SIM-29="+ordinary+"deg SIM-29M="+maneuver+"deg");
    }
    static float Turn(string id)
    {
        var owner=new GameObject("Turn comparison owner");var body=new GameObject(id);
        try
        {
            var audio=body.AddComponent<MechMissileAudioController>();typeof(MechMissileAudioController).GetMethod("Awake",Private).Invoke(audio,null);
            var missile=body.AddComponent<MechTestProjectile>();var p=MechMissileAmmunitionCatalog.Create(id);
            missile.Configure(owner.transform,null,default,TestWeaponKind.Missile,Vector3.forward*1000,1,.1f,180,missileProfile:p.missileFlightProfile);
            var apply=typeof(MechTestProjectile).GetMethod("ApplyGuidance",Private);
            var target=new Vector3(5000,0,10000);
            for(int i=0;i<60;i++)apply.Invoke(missile,new object[]{target,Vector3.zero,target.normalized,true,1f/60});
            Assert.Greater(missile.RcsPropellantMassRemainingKg,0);
            Assert.Less(missile.RcsPropellantMassRemainingKg,p.missileFlightProfile.RcsPropellantMassKg);
            return Vector3.Angle(Vector3.forward,missile.Velocity);
        }
        finally
        {
            var missile=body.GetComponent<MechTestProjectile>();if(missile!=null)(typeof(MechTestProjectile).GetField("seekerRuntime",Private).GetValue(missile) as IDisposable)?.Dispose();
            UnityEngine.Object.DestroyImmediate(body);UnityEngine.Object.DestroyImmediate(owner);
        }
    }
    [Test] public void ReleaseEnvelopeDependsOnRelativeMotionNotAStaticEffectiveRange()
    {
        var p=MechMissileAmmunitionCatalog.Create("SIM-29");
        var k=(MissileKinematicProfile)typeof(MechTestWeaponSystem).GetMethod("BuildMissileKinematics",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{p.missileFlightProfile});
        var track=new TrackSnapshot{TrackId=new TrackId(new Handle(1,1)),Position=new double3(0,0,40000),Velocity=new double3(0,0,-1000),LastPredictTime=10,LastUpdateTime=10,Confidence=1,Classification=1,Lifecycle=TrackLifecycle.Confirmed,ObjectClass=TrackObjectClass.AirVehicle};
        for(int i=0;i<6;i++)track.SetCovariance(i,i,1);
        var approaching=FireSolutionSolver.SolveMissile(track,double3.zero,double3.zero,k,10);
        track.Velocity=new double3(0,0,1000);
        var receding=FireSolutionSolver.SolveMissile(track,double3.zero,double3.zero,k,10);
        Assert.IsTrue(approaching.WeaponReleaseAuthorized);
        Assert.IsTrue(MechMissileLaunchSource.SafeGeometry(40000,1,20,math.length(approaching.InterceptPoint),29000));
        Assert.IsFalse(receding.WeaponReleaseAuthorized&&MechMissileLaunchSource.SafeGeometry(40000,1,20,math.length(receding.InterceptPoint),29000));
    }
}
