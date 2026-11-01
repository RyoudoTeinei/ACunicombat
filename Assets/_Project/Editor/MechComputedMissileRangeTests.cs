using System;
using NUnit.Framework;
using ACunicombat.Mechs;
using UnityEngine;
using System.Reflection;

public class MechComputedMissileRangeTests
{
    [Test] public void Sim57NamesArePreservedAndOnlyExtendedRangeIsRetuned()
    {
        var ordinary=MechMissileAmmunitionCatalog.Create("SIM-57");
        var extended=MechMissileAmmunitionCatalog.Create("SIM-57R");
        Assert.AreEqual("SIM-57",ordinary.missileDisplayName);
        Assert.AreEqual(57000,ordinary.maximumRangeMeters);
        Assert.AreEqual(27.34f,ordinary.missileFlightProfile.MaximumFlightTimeSeconds);
        Assert.AreEqual("SIM-57R",extended.missileDisplayName);
        Assert.AreEqual(77000,extended.maximumRangeMeters);
        Assert.AreEqual(30.57f,extended.missileFlightProfile.MaximumFlightTimeSeconds);
        Assert.IsNull(MechMissileAmmunitionCatalog.Create("SIM-77"));
        Assert.IsNull(MechMissileAmmunitionCatalog.Create("SIM-77R"));
    }
    [TestCase(70000,true,false)]
    [TestCase(200000,true,true)]
    [TestCase(200000,false,false)]
    public void DuctedThrottleRespectsObservedTargetAndRemainingServiceLife(float range,bool observed,bool needsMoreThrust)
    {
        var owner=new GameObject("Throttle test launcher");
        var body=new GameObject("Throttle test missile");
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        MechTestProjectile missile=null;
        try
        {
            var audio=body.AddComponent<MechMissileAudioController>();
            typeof(MechMissileAudioController).GetMethod("Awake",flags).Invoke(audio,null);
            missile=body.AddComponent<MechTestProjectile>();
            var profile=MechMissileAmmunitionCatalog.Create("MTR-4340").missileFlightProfile;
            missile.Configure(owner.transform,null,default,TestWeaponKind.Missile,Vector3.forward*500,1,.1f,80,missileProfile:profile);
            void Set(string name,object value)=>typeof(MechTestProjectile).GetField(name,flags).SetValue(missile,value);
            Set("mainPropellantMassRemainingKg",0f);Set("lifeRemaining",80f);
            Set("lastGuidanceRangeMeters",range);Set("pulseObservedPosition",Vector3.forward*range);
            Set("pulseObservedVelocity",Vector3.zero);
            if(observed)Set("pulseObservationTime",typeof(MechTestProjectile).GetProperty("GuidanceTime",flags).GetValue(missile));
            float fuel=missile.CurrentMassKg;
            float throttle=(float)typeof(MechTestProjectile).GetMethod("ResolveSecondStageThrottle",flags).Invoke(missile,null);
            if(needsMoreThrust)Assert.Greater(throttle,profile.SecondStageMinimumThrottle);
            else Assert.AreEqual(profile.SecondStageMinimumThrottle,throttle);
            Assert.AreEqual(fuel,missile.CurrentMassKg,"Planning must not fabricate or consume propellant.");
        }
        finally
        {
            if(missile!=null)(typeof(MechTestProjectile).GetField("seekerRuntime",flags).GetValue(missile) as IDisposable)?.Dispose();
            UnityEngine.Object.DestroyImmediate(body);UnityEngine.Object.DestroyImmediate(owner);
        }
    }
    [Test] public void Flint15MatchesApprovedNinetyKilometerProfile()
    {
        var f=MechMissileAmmunitionCatalog.Create("DPL-15").missileFlightProfile;
        Assert.AreEqual(4,f.MainBurnDurationSeconds);
        Assert.AreEqual(6,f.SecondStageIgnitionDelaySeconds);
        Assert.AreEqual(6,f.SecondStageBurnDurationSeconds);
        Assert.AreEqual(57.7f,f.MaximumFlightTimeSeconds);
        Assert.That(f.Kinematics.BurnoutSpeed,Is.EqualTo(1818.962).Within(.01));
        Assert.AreEqual(90000,f.MaximumRangeMeters);
        Assert.AreEqual(95.09f,MechMissileAmmunitionCatalog.Create("DPL-17").missileFlightProfile.MaximumFlightTimeSeconds);
    }
    [TestCase("AAM-4301C",24000)] [TestCase("ABM-12",12000)] [TestCase("ABM-18",18000)]
    [TestCase("DPL-15",90000)] [TestCase("DPL-17",320000)]
    [TestCase("JW7",47000)] [TestCase("JW7-ER",64000)] [TestCase("LRA-4230",240000)]
    [TestCase("MTR-4340",300000)] [TestCase("MTR-4340-ER",340000)]
    [TestCase("SIM-29",29000)] [TestCase("SIM-29M",35000)] [TestCase("SIM-57",57000)] [TestCase("SIM-57R",77000)]
    public void ApprovedReferenceRangesResultFromPropulsionAndServiceLifetime(string id,float expected)
    {
        var f=MechMissileAmmunitionCatalog.Create(id).missileFlightProfile;
        Assert.AreEqual(expected,f.MaximumRangeMeters);
        double burn=f.MainBurnDurationSeconds+(f.HasSecondStage?f.SecondStageIgnitionDelaySeconds+f.SecondStageBurnDurationSeconds:0);
        Assert.Greater(f.MaximumFlightTimeSeconds,burn,"Full-thrust reference must finish both motor stages before expiry.");
    }
    [TestCase("SIM-29")] [TestCase("SIM-29M")] [TestCase("SIM-57")] [TestCase("SIM-57R")]
    [TestCase("JW7")] [TestCase("JW7-ER")] [TestCase("ABM-12")] [TestCase("ABM-18")]
    [TestCase("DPL-15")] [TestCase("DPL-17")] [TestCase("LRA-4230")]
    [TestCase("MTR-4340")] [TestCase("MTR-4340-ER")] [TestCase("AAM-4301C")]
    public void ReferenceAgreesWithIndependentMassIntegrationWithinOneHundredMeters(string id)
    {
        var p=MechMissileAmmunitionCatalog.Create(id);
        Assert.IsNotNull(p,id);
        var f=p.missileFlightProfile;
        double speed=f.EjectionSpeedMps,distance=0,main=f.MainPropellantMassKg,second=f.SecondStagePropellantMassKg;
        double secondStart=f.MainBurnDurationSeconds+f.SecondStageIgnitionDelaySeconds;
        const double step=.002;
        for(double time=0;time<f.MaximumFlightTimeSeconds;time+=step)
        {
            double dt=Math.Min(step,f.MaximumFlightTimeSeconds-time);
            bool first=main>0;
            bool next=!first&&f.HasSecondStage&&time>=secondStart&&second>0;
            double flow=first?f.MainMassFlowKgPerSecond:next?f.SecondStageMassFlowKgPerSecond:0;
            double thrust=first?f.MainEngineThrustNewtons:next?f.SecondStageThrustNewtons:0;
            double used=Math.Min(first?main:second,flow*dt);
            double mass=f.DryMassKg+f.RcsPropellantMassKg+main+second-used*.5;
            double acceleration=used>0?thrust/mass*(used/(flow*dt)):0;
            distance+=speed*dt+.5*acceleration*dt*dt;
            speed+=acceleration*dt;
            if(first)main-=used;else second-=used;
        }
        Assert.Less(Math.Abs(distance-f.MaximumRangeMeters),100,id);
        Assert.AreEqual(0,p.effectiveRangeMeters);
        Assert.AreEqual(0,f.MaximumRangeMeters%100);
    }

    [Test] public void LegacyAuthoredRangeDoesNotChangeEnvelope()
    {
        var p=MissileFlightProfile.AirToAirDefault;
        p.MaximumRangeMeters=100;
        float small=p.Sanitized().MaximumRangeMeters;
        p.MaximumRangeMeters=90000000;
        Assert.AreEqual(small,p.Sanitized().MaximumRangeMeters);
        p.MainPropellantMassKg*=2;
        Assert.Greater(p.Sanitized().MaximumRangeMeters,small);
    }

    [Test] public void MoreMassReducesRangeAndLateSecondPulseReducesReferenceDistance()
    {
        var p=MechMissileAmmunitionCatalog.Create("DPL-17").missileFlightProfile;
        float initial=p.ReferenceRangeMeters;
        p.DryMassKg*=2;
        Assert.Less(p.ReferenceRangeMeters,initial);
        p.DryMassKg*=.5f;p.SecondStageIgnitionDelaySeconds+=20;
        Assert.Less(p.ReferenceRangeMeters,initial);
    }
    [Test] public void OldWeaponFieldsCannotOverrideCachedEnvelopeAndFuelEditsInvalidateIt()
    {
        var weapon=MechMissileAmmunitionCatalog.Create("SIM-29");
        float original=weapon.maximumRangeMeters;
        weapon.maximumRangeMeters=1;weapon.effectiveRangeMeters=90000000;
        weapon.RefreshMissileEnvelope();
        Assert.AreEqual(original,weapon.maximumRangeMeters);
        Assert.AreEqual(0,weapon.effectiveRangeMeters);
        weapon.missileFlightProfile.MainPropellantMassKg*=2;
        weapon.RefreshMissileEnvelope();
        Assert.Greater(weapon.maximumRangeMeters,original);
    }
}
