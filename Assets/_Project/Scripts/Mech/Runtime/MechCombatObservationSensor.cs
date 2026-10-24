using System;
using System.Collections.Generic;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public interface ICombatObservationReceiver
    {
        bool TryObserveCombatPoint(Vector3 point,bool missile,bool launch,out Vector3 measured,out float error);
        void ObserveWeaponLaunch(Guid observation,bool missile,Vector3 position,double time,float error);
        void ObserveWeaponSample(Guid observation,Vector3 position,double time,float error);
        void ObserveWeaponImpact(Guid observation,Vector3 position,double time);
    }
    public interface ICombatRadiometricReceiver
    {
        bool TryObserveCombatRadiance(Vector3 point,float signatureDb,bool launch,out Vector3 measured,out float error);
        void ObserveWeaponAcquisition(Guid observation,bool missile,Vector3 position,double time,float error);
    }
    // Analytic observation boundary. No owner, faction or internal guidance target crosses it.
    public static class MechCombatObservationSensor
    {
        private sealed class Seen {public Guid Id=Guid.NewGuid();public double Last,PhysicsTime;}
        private static readonly Dictionary<ICombatObservationReceiver,Dictionary<MechTestProjectile,Seen>> receivers=new Dictionary<ICombatObservationReceiver,Dictionary<MechTestProjectile,Seen>>();
        private static readonly List<MechTestProjectile> obsolete=new List<MechTestProjectile>();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void Reset(){receivers.Clear();obsolete.Clear();}
        public static void Register(ICombatObservationReceiver receiver){if(!receivers.ContainsKey(receiver))receivers.Add(receiver,new Dictionary<MechTestProjectile,Seen>());}
        public static void Unregister(ICombatObservationReceiver receiver)=>receivers.Remove(receiver);
        private static bool Observe(ICombatObservationReceiver receiver,MechTestProjectile projectile,bool launch,out Vector3 point,out float error)
        {
            if(receiver is ICombatRadiometricReceiver radiometric)
                return radiometric.TryObserveCombatRadiance(projectile.transform.position,projectile.IsMissile?projectile.InfraredIntensityDb:28f,launch,out point,out error);
            return receiver.TryObserveCombatPoint(projectile.transform.position,projectile.IsMissile,launch,out point,out error);
        }
        public static void Launched(MechTestProjectile projectile)
        {
            foreach(var entry in receivers)
            {
                if(entry.Value.Count>=256||!Observe(entry.Key,projectile,true,out var point,out var error))continue;
                var seen=new Seen{Last=Time.timeAsDouble,PhysicsTime=projectile.PhysicsStateTime};entry.Value[projectile]=seen;
                entry.Key.ObserveWeaponLaunch(seen.Id,projectile.IsMissile,point,projectile.PhysicsStateTime,error);
            }
        }
        public static void Poll(ICombatObservationReceiver receiver,double now)
        {
            if(!receivers.TryGetValue(receiver,out var seen))return;
            // Late detections are acquisitions, never fabricated launch observations.
            if(receiver is ICombatRadiometricReceiver acquiring)
                foreach(var projectile in MechTestProjectile.Registry)
                {
                    if(projectile==null||!projectile.IsMissile||!projectile.isActiveAndEnabled||seen.ContainsKey(projectile)||seen.Count>=256)continue;
                    if(!Observe(receiver,projectile,false,out var point,out var error))continue;
                    var state=new Seen{Last=now,PhysicsTime=projectile.PhysicsStateTime};seen.Add(projectile,state);
                    acquiring.ObserveWeaponAcquisition(state.Id,true,point,state.PhysicsTime,error);
                }
            obsolete.Clear();
            foreach(var pair in seen)
            {
                var projectile=pair.Key;var state=pair.Value;
                if(projectile==null||!projectile.isActiveAndEnabled||now-state.Last>.75){obsolete.Add(projectile);continue;}
                if(!Observe(receiver,projectile,false,out var point,out var error))continue;
                state.Last=now;if(projectile.PhysicsStateTime<=state.PhysicsTime)continue;state.PhysicsTime=projectile.PhysicsStateTime;receiver.ObserveWeaponSample(state.Id,point,state.PhysicsTime,error);
            }
            foreach(var projectile in obsolete)seen.Remove(projectile);
        }
        public static void Impact(MechTestProjectile projectile,Vector3 point)
        {
            foreach(var entry in receivers)if(entry.Value.TryGetValue(projectile,out var seen)&&Time.timeAsDouble-seen.Last<=.75)
            {
                if(entry.Key.TryObserveCombatPoint(point,projectile.IsMissile,false,out var measured,out _))entry.Key.ObserveWeaponImpact(seen.Id,measured,Time.timeAsDouble);
            }
        }
    }
}
