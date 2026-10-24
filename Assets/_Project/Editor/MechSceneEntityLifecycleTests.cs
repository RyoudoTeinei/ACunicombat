using System.Reflection;
using ACunicombat.Mechs;
using ACunicombat.Simulation.Driver;
using ACunicombat.Simulation.World;
using NUnit.Framework;
using UnityEngine;

public sealed class MechSceneEntityLifecycleTests
{
    const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    GameObject observer, missile;
    SimulationDriver driver;
    MechRadarController radar;
    BattlefieldEntityIdentity identity;
    static T Get<T>(object obj, string name) => (T)obj.GetType().GetField(name, Flags).GetValue(obj);
    static void Set(object obj, string name, object value) => obj.GetType().GetField(name, Flags).SetValue(obj,value);
    static void Call(object obj, string method) => obj.GetType().GetMethod(method, Flags).Invoke(obj,null);
    [SetUp] public void SetUp()
    {
        observer = new GameObject("Lifecycle observer"); observer.SetActive(false);
        driver = observer.AddComponent<SimulationDriver>(); Set(driver,"enableCombatTelemetry",false); Call(driver,"Awake");
        radar = observer.AddComponent<MechRadarController>(); Set(radar,"driver",driver); Call(radar,"BindDeviceIdentities"); Set(radar,"initialized",true);
        Set(radar,"dataLinkPerformance",MechDataLinkPerformance.CreateStandardFallback());
        missile = new GameObject("Independent missile body"); identity = BattlefieldEntityIdentity.GetOrAdd(missile);
    }
    [TearDown] public void TearDown()
    {
        if (radar != null) { Call(radar,"OnDisable"); Set(radar,"initialized",false); }
        if (driver != null)
        {
            Call(driver,"OnDestroy");
            foreach(var name in new[]{"sceneEntities","dataLink","laserRangefinder","electroOpticalSensor","esmReceiver","radar","trackContext","dwellScheduler","grid","world"})
                Set(driver,name,null);
        }
        if(observer != null) Object.DestroyImmediate(observer);
        if(missile != null) Object.DestroyImmediate(missile);
    }
    [Test] public void MissileLinkReleasePreservesSeekerBodyAndGlobalIdentity()
    {
        var before = identity.Identity;
        Assert.That(radar.TryRegisterMissileSeeker(Vector3.forward*100,Vector3.forward,10000,12,false,out var seeker,identity),Is.True);
        Assert.That(radar.TryRegisterMissileLinkTerminal(Vector3.forward*100,Vector3.forward,out var link,identity),Is.True);
        var bindings = Get<ObserverEntityBindings>(driver,"sceneEntities");
        Assert.That(bindings.EntityCount,Is.EqualTo(1)); Assert.That(bindings.LeaseCount,Is.EqualTo(2));
        radar.UnregisterMissileLinkTerminal(link);
        Assert.That(bindings.EntityCount,Is.EqualTo(1)); Assert.That(bindings.LeaseCount,Is.EqualTo(1));
        radar.UnregisterMissileSeeker(in seeker);
        Assert.That(bindings.EntityCount,Is.Zero); Assert.That(identity.Identity,Is.EqualTo(before));
    }
    [Test] public void StaleSeekerReleaseCannotUnregisterReusedSensor()
    {
        radar.TryRegisterMissileSeeker(Vector3.forward*100,Vector3.forward,10000,12,false,out var old,identity);
        radar.UnregisterMissileSeeker(in old);
        radar.TryRegisterMissileSeeker(Vector3.forward*100,Vector3.forward,10000,12,false,out var current,identity);
        Assert.That(current.SensorId.Value.Index,Is.EqualTo(old.SensorId.Value.Index));
        Assert.That(current.SensorId,Is.Not.EqualTo(old.SensorId));
        radar.UnregisterMissileSeeker(in old);
        Assert.That(Get<ObserverEntityBindings>(driver,"sceneEntities").LeaseCount,Is.EqualTo(1));
        Assert.That(Get<bool[]>(radar,"auxiliarySensorAllocated")[current.SensorId.Value.Index],Is.True);
        radar.UnregisterMissileSeeker(in current);
    }
    [Test] public void DisablingTargetMotionBehaviourDoesNotHideItsBody()
    {
        Set(radar,"autoEquipTestEmitters",false);
        var target = missile.AddComponent<CombatTestTarget>(); target.enabled=false;
        radar.GetType().GetMethod("AddSceneTarget",Flags).Invoke(radar,new object[]{target});
        Assert.That(Get<int>(radar,"sceneTargetCount"),Is.EqualTo(1));
        Assert.That(Get<ObserverEntityBindings>(driver,"sceneEntities").EntityCount,Is.EqualTo(1));
    }
    [Test] public void ProjectileDisableReleasesAllDeviceLeasesAndDestroyIsIdempotent()
    {
        radar.TryRegisterMissileSeeker(Vector3.forward*100,Vector3.forward,10000,12,false,out var seeker,identity);
        radar.TryRegisterMissileLinkTerminal(Vector3.forward*100,Vector3.forward,out var terminal,identity);
        var projectile=missile.AddComponent<MechTestProjectile>();
        Set(projectile,"radarController",radar); Set(projectile,"dataLinkController",radar);
        Set(projectile,"linkTerminal",terminal); Set(projectile,"configured",true);
        var registrationType=typeof(MechTestProjectile).GetNestedType("RadarObserverRegistration",BindingFlags.NonPublic);
        var registration=System.Activator.CreateInstance(registrationType);
        registrationType.GetField("Controller").SetValue(registration,radar);
        registrationType.GetField("Handle").SetValue(registration,seeker);
        Get<System.Collections.IList>(projectile,"radarObservers").Add(registration);
        Call(projectile,"OnDisable");
        var bindings=Get<ObserverEntityBindings>(driver,"sceneEntities");
        Assert.That(bindings.LeaseCount,Is.Zero);
        Assert.That(Get<bool[]>(radar,"auxiliarySensorAllocated")[seeker.SensorId.Value.Index],Is.False);
        Assert.That(Get<bool>(projectile,"configured"),Is.False);
        radar.TryRegisterMissileSeeker(Vector3.forward*100,Vector3.forward,10000,12,false,out var fresh,identity);
        Call(projectile,"OnDestroy");
        Assert.That(bindings.LeaseCount,Is.EqualTo(1));
        radar.UnregisterMissileSeeker(in fresh);
    }
    [Test] public void LauncherLossCoastsExistingSeekerMemoryThenExpires()
    {
        var projectile=missile.AddComponent<MechTestProjectile>();
        Set(projectile,"seekerLocked",true); Set(projectile,"seekerLockMemoryRemaining",.35f);
        Set(projectile,"seekerEstimatedPosition",Vector3.forward*100);
        Set(projectile,"seekerEstimatedVelocity",Vector3.right*20);
        var update=typeof(MechTestProjectile).GetMethod("UpdateSeeker",Flags);
        update.Invoke(projectile,new object[]{.1f});
        Assert.That(Get<bool>(projectile,"seekerLocked"),Is.True);
        Assert.That(Get<Vector3>(projectile,"seekerEstimatedPosition").x,Is.EqualTo(2f).Within(.001));
        update.Invoke(projectile,new object[]{.3f});
        Assert.That(Get<bool>(projectile,"seekerLocked"),Is.False);
    }
    [Test] public void LauncherIndependentClockUsesProjectileElapsedTime()
    {
        var projectile=missile.AddComponent<MechTestProjectile>();
        Set(projectile,"guidanceTimeOffset",12.0); Set(projectile,"projectileFlightTime",3f);
        var clock=typeof(MechTestProjectile).GetProperty("GuidanceTime",Flags);
        Assert.That((double)clock.GetValue(projectile),Is.EqualTo(15.0));
        var track=new ACunicombat.Sensing.Contracts.TrackSnapshot {
            Position=new Unity.Mathematics.double3(0,0,100), Velocity=new Unity.Mathematics.double3(10,0,0), LastPredictTime=14.0 };
        var prediction=typeof(MechTestProjectile).GetMethod("PredictRememberedTrack",Flags).Invoke(projectile,new object[]{track});
        Assert.That(((Vector3)prediction).x,Is.EqualTo(10f));
    }
    [Test] public void IndependentSeekerAcquiresFreshMeasurementAfterLauncherDestruction()
    {
        var targetObject=new GameObject("Independent seeker target");
        System.IDisposable runtime=null;
        try
        {
            targetObject.transform.position=Vector3.forward*1000;
            var target=targetObject.AddComponent<CombatTestTarget>();Set(target,"structure",1000f);
            var targetIdentity=BattlefieldEntityIdentity.GetOrAdd(targetObject);var targetId=targetIdentity.Identity;
            var projectile=missile.AddComponent<MechTestProjectile>();Set(projectile,"velocity",Vector3.forward*100);
            var type=typeof(MechTestProjectile).Assembly.GetType("ACunicombat.Mechs.MechMissileSeekerRuntime",true);
            runtime=(System.IDisposable)System.Activator.CreateInstance(type,new object[]{projectile,false,10000f,30f});
            Set(projectile,"seekerRuntime",runtime);Set(projectile,"radarController",radar);
            Set(projectile,"seekerGimbalRadians",1f);Set(projectile,"seekerHalfAngleRadians",.3f);
            Set(radar,"initialized",false);Call(driver,"OnDestroy");
            foreach(var name in new[]{"sceneEntities","dataLink","laserRangefinder","electroOpticalSensor","esmReceiver","radar","trackContext","dwellScheduler","grid","world"}) Set(driver,name,null);
            Object.DestroyImmediate(observer);observer=null;driver=null;radar=null;
            Set(projectile,"projectileFlightTime",1f);
            typeof(MechTestProjectile).GetMethod("UpdateSeeker",Flags).Invoke(projectile,new object[]{.1f});
            Assert.That(Get<bool>(projectile,"seekerLocked"),Is.True);
            Assert.That(Get<double>(projectile,"seekerLastMeasurementTime"),Is.EqualTo(1.0));
            targetObject.transform.position=Vector3.forward*1020;
            Set(projectile,"projectileFlightTime",1.1f);
            typeof(MechTestProjectile).GetMethod("UpdateSeeker",Flags).Invoke(projectile,new object[]{.1f});
            Assert.That(Get<double>(projectile,"seekerLastMeasurementTime"),Is.GreaterThan(1.05));
            Assert.That(Get<bool>(projectile,"seekerLocked"),Is.True);
        }
        finally{runtime?.Dispose();Object.DestroyImmediate(targetObject);}
    }
    [Test] public void SharedFactoryCreatesPlaybackWithoutShowcaseAndReusesComponent()
    {
        var root=new GameObject("Platform without demo");root.SetActive(false);
        var actions=ScriptableObject.CreateInstance<MechActionRegistry>();
        try
        {
            var state=root.AddComponent<MechRuntimeState>();
            var first=PlatformFactory.ConfigureBodyPlayback(root,actions,state,new string[0]);
            var second=PlatformFactory.ConfigureBodyPlayback(root,actions,state,new string[0]);
            Assert.That(first,Is.Not.Null);Assert.That(second,Is.SameAs(first));
            Assert.That(root.GetComponents<MechBodyActionPlayer>().Length,Is.EqualTo(1));
            Assert.That(root.GetComponent<AC6CombatShowcaseController>(),Is.Null);
        }
        finally{Object.DestroyImmediate(root);Object.DestroyImmediate(actions);}
    }
    [Test] public void DynamicRosterReleasesInactiveBodyAndFindsNewlyRegisteredBody()
    {
        Set(radar,"autoEquipTestEmitters",false);
        var target=missile.AddComponent<CombatTestTarget>();Set(target,"structure",1000f);
        var firstId=identity.Identity;
        Call(radar,"RefreshSceneTargetRoster");
        Assert.That(Get<int>(radar,"sceneTargetCount"),Is.EqualTo(1));
        missile.SetActive(false);
        Call(radar,"RefreshSceneTargetRoster");
        Assert.That(Get<int>(radar,"sceneTargetCount"),Is.Zero);
        Assert.That(Get<ObserverEntityBindings>(driver,"sceneEntities").EntityCount,Is.Zero);
        missile.SetActive(true);
        Call(radar,"RefreshSceneTargetRoster");
        Assert.That(Get<int>(radar,"sceneTargetCount"),Is.EqualTo(1));
    }
    [Test] public void IndependentSeekerReceivesChaffThroughSharedSignalRegistry()
    {
        var source=new GameObject("Chaff signal test");System.IDisposable runtime=null;
        try
        {
            var signal=source.AddComponent<MechRadarDecoySignal>();signal.Configure(Vector3.forward*1000,Vector3.zero,18f,2);
            var sourceId=BattlefieldEntityIdentity.GetOrAdd(source).Identity;
            var projectile=missile.AddComponent<MechTestProjectile>();
            var type=typeof(MechTestProjectile).Assembly.GetType("ACunicombat.Mechs.MechMissileSeekerRuntime",true);
            runtime=(System.IDisposable)System.Activator.CreateInstance(type,new object[]{projectile,false,10000f,30f});
            var measurements=new ACunicombat.Sensing.Contracts.Measurement[8];
            var count=(int)type.GetMethod("Scan").Invoke(runtime,new object[]{1.0,Vector3.forward,30f,1UL,measurements});
            Assert.That(count,Is.EqualTo(1));
            Assert.That(measurements[0].ObjectClass,Is.EqualTo(ACunicombat.Sensing.Contracts.TrackObjectClass.Decoy));
        }
        finally{runtime?.Dispose();Object.DestroyImmediate(source);}
    }
    [Test] public void InFlightMissileRegistersNewObserverOnceAndReleasesInactiveObserver()
    {
        var projectile=missile.AddComponent<MechTestProjectile>();
        Assert.That(Get<System.Collections.IList>(projectile,"radarObservers").Count,Is.Zero);
        observer.SetActive(true);Call(radar,"OnEnable");
        Call(projectile,"RefreshRadarObservers");Call(projectile,"RefreshRadarObservers");
        Assert.That(Get<System.Collections.IList>(projectile,"radarObservers").Count,Is.EqualTo(1));
        Assert.That(Get<ObserverEntityBindings>(driver,"sceneEntities").LeaseCount,Is.EqualTo(1));
        observer.SetActive(false);Call(radar,"OnDisable");
        Call(projectile,"RefreshRadarObservers");
        Assert.That(Get<System.Collections.IList>(projectile,"radarObservers").Count,Is.Zero);
        Assert.That(Get<ObserverEntityBindings>(driver,"sceneEntities").LeaseCount,Is.Zero);
    }
    [Test] public void SensorAndLinkEmitterAddressesCannotAlias()
    {
        ACunicombat.Sensing.Contracts.SensorId sensor=default;
        for(var i=0;i<30;i++)Assert.That(driver.TryAllocateRadarSensor(out sensor),Is.True);
        Assert.That(driver.TryAllocateLinkTerminal(out var terminal),Is.True);
        var radarEmitter=driver.GetSensorEmitterId(sensor);var linkEmitter=driver.GetLinkEmitterId(terminal);
        Assert.That(radarEmitter,Is.Not.EqualTo(linkEmitter));
        driver.UnregisterLinkTerminal(terminal);
        Assert.That(driver.GetSensorEmitterId(sensor),Is.EqualTo(radarEmitter));
    }
    [Test] public void AiDiagnosticCopiesDoNotCreateSensorsOrExposeMutableBuffers()
    {
        var ai=missile.AddComponent<MechAiTargetController>();
        var snapshot=new ACunicombat.Sensing.Contracts.TrackSnapshot {Confidence=.75f};
        Get<ACunicombat.Sensing.Contracts.TrackSnapshot[]>(ai,"airTrackScratch")[0]=snapshot;
        Set(ai,"consumedAirCount",1);
        var destination=new ACunicombat.Sensing.Contracts.TrackSnapshot[1];
        Assert.That(ai.CopyConsumedAirInputs(destination),Is.EqualTo(1));destination[0].Confidence=0;
        ai.CopyConsumedAirInputs(destination);
        Assert.That(destination[0].Confidence,Is.EqualTo(.75f));
        Assert.That(ai.GetComponent<SimulationDriver>(),Is.Null);
    }
    [Test] public void MidcourseMemorySurvivesMissingLinkButExpiresByMissileClock()
    {
        var projectile=missile.AddComponent<MechTestProjectile>();
        Set(projectile,"hasCachedGuidanceTrack",true);Set(projectile,"guidanceTimeOffset",10.0);
        Set(projectile,"guidanceMemoryExpiresAt",10.2);Set(projectile,"projectileFlightTime",.1f);
        Set(projectile,"cachedGuidanceTrack",new ACunicombat.Sensing.Contracts.TrackSnapshot {Confidence=.8f});
        var method=typeof(MechTestProjectile).GetMethod("TryUpdateDatalinkTrack",Flags);
        var args=new object[]{.02f,default(ACunicombat.Sensing.Contracts.TrackSnapshot)};
        Assert.That((bool)method.Invoke(projectile,args),Is.True);
        Set(projectile,"projectileFlightTime",.3f);
        Assert.That((bool)method.Invoke(projectile,args),Is.False);
        Assert.That(Get<float>(projectile,"guidanceMemoryRemaining"),Is.Zero);
    }
    [Test] public void TerminalLockCannotFreezeOldMidcourseMemory()
    {
        var projectile=missile.AddComponent<MechTestProjectile>();
        Set(projectile,"hasCachedGuidanceTrack",true);Set(projectile,"guidanceTimeOffset",10.0);
        Set(projectile,"guidanceMemoryExpiresAt",11.0);Set(projectile,"projectileFlightTime",3f);
        Set(projectile,"seekerLocked",true);Set(projectile,"guidanceMemoryRemaining",4f);
        var args=new object[]{.02f,default(ACunicombat.Sensing.Contracts.TrackSnapshot)};
        Assert.That((bool)typeof(MechTestProjectile).GetMethod("TryUpdateDatalinkTrack",Flags).Invoke(projectile,args),Is.False);
    }
    [Test] public void ExplicitLifetimeHooksInvalidateOldBinding()
    {
        var first = identity.Identity;
        var lease = driver.RegisterSceneEntity(identity,default);
        // EditMode does not dispatch normal MonoBehaviour lifecycle callbacks.
        Call(identity,"OnDisable");
        Get<ObserverEntityBindings>(driver,"sceneEntities").PruneRetired();
        Assert.That(driver.IsSceneEntityCurrent(in lease),Is.False);
        Call(identity,"OnEnable");
        Assert.That(identity.Identity,Is.Not.EqualTo(first));
        var current = driver.RegisterSceneEntity(identity,default);
        Assert.That(driver.ReleaseSceneEntity(in lease),Is.False);
        Assert.That(driver.IsSceneEntityCurrent(in current),Is.True);
    }
    [Test] public void ExplicitSupportCancellation_PreservesMissileAndLetsMemoryExpire()
    {
        var projectile=missile.AddComponent<MechTestProjectile>();
        Set(projectile,"hasCachedGuidanceTrack",true);Set(projectile,"guidanceTimeOffset",10.0);
        Set(projectile,"guidanceMemoryExpiresAt",10.2);Set(projectile,"projectileFlightTime",.1f);
        Set(projectile,"cachedGuidanceTrack",new ACunicombat.Sensing.Contracts.TrackSnapshot {Confidence=.8f});
        Assert.That(projectile.StopExternalGuidanceSupport(),Is.True);
        Assert.That(projectile.StopExternalGuidanceSupport(),Is.False);
        Assert.That(projectile.gameObject.activeSelf,Is.True);
        var args=new object[]{.02f,default(ACunicombat.Sensing.Contracts.TrackSnapshot)};
        var method=typeof(MechTestProjectile).GetMethod("TryUpdateDatalinkTrack",Flags);
        Assert.That((bool)method.Invoke(projectile,args),Is.True);
        Set(projectile,"projectileFlightTime",.3f);Assert.That((bool)method.Invoke(projectile,args),Is.False);
    }
}