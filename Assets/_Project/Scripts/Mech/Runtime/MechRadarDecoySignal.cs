using System.Collections.Generic;
using ACunicombat.Simulation.Driver;
using UnityEngine;
namespace ACunicombat.Mechs
{
    /// <summary>Independent physical chaff lifetime; the dispenser only launches it.</summary>
    public sealed class MechRadarDecoySignal : MonoBehaviour
    {
        private struct Registration {public MechRadarController Controller;public MechRadarController.RadarDecoyHandle Handle;}
        private readonly List<Registration> registrations=new List<Registration>();
        public Vector3 Velocity {get;private set;}
        public float CrossSection {get;private set;}
        public bool SignalActive {get;private set;}
        private int? emittingTeam;
        private MechRadarController releasingRadar;
        private float age,lifetime,initialRcs,refreshRemaining;
        private bool moving;
        public void Configure(Vector3 position,Vector3 velocity,float crossSection,int? team)
        {transform.position=position;Velocity=velocity;CrossSection=crossSection;emittingTeam=team;SignalActive=true;}
        public void Launch(Vector3 position,Vector3 velocity,float crossSection,float duration,int? team,MechRadarController originator)
        {
            Configure(position,velocity,crossSection,team);initialRcs=crossSection;lifetime=Mathf.Max(.5f,duration);
            age=0;moving=true;releasingRadar=originator;BattlefieldEntityIdentity.GetOrAdd(gameObject);RefreshObservers();
        }
        public bool VisibleToLegacyReceiver(int? receiverTeam)=>!emittingTeam.HasValue || !receiverTeam.HasValue || emittingTeam.Value!=receiverTeam.Value;
        private void Update()
        {
            if(!moving)return;
            age+=Time.deltaTime;
            if(age>=lifetime){Destroy(gameObject);return;}
            transform.position+=Velocity*Time.deltaTime;
            var t=Mathf.Clamp01(age/lifetime);CrossSection=Mathf.Lerp(initialRcs,.01f,t*t);
            refreshRemaining-=Time.deltaTime;if(refreshRemaining<=0){RefreshObservers();refreshRemaining=.25f;}
            foreach(var registration in registrations)if(registration.Controller!=null)
                registration.Controller.UpdateRadarDecoy(in registration.Handle,transform.position,Velocity,CrossSection);
        }
        private void RefreshObservers()
        {
            for(var i=registrations.Count-1;i>=0;i--)
            {
                var old=registrations[i];
                if(old.Controller!=null && old.Controller.isActiveAndEnabled)continue;
                if(old.Controller!=null)old.Controller.UnregisterRadarDecoy(in old.Handle);
                registrations.RemoveAt(i);
            }
            foreach(var controller in MechRadarController.ActiveControllers)
            {
                if(controller==null || controller==releasingRadar || !controller.isActiveAndEnabled)continue;
                var target=controller.GetComponentInParent<CombatTestTarget>();
                if(!VisibleToLegacyReceiver(target!=null?(int?)target.TeamId:null))continue;
                var present=false;foreach(var item in registrations)if(item.Controller==controller){present=true;break;}
                if(!present && controller.TryRegisterRadarDecoy(transform.position,Velocity,CrossSection,out var handle,BattlefieldEntityIdentity.GetOrAdd(gameObject)))
                    registrations.Add(new Registration {Controller=controller,Handle=handle});
            }
        }
        private void OnDisable()
        {
            moving=false;SignalActive=false;
            foreach(var item in registrations)if(item.Controller!=null)item.Controller.UnregisterRadarDecoy(in item.Handle);
            registrations.Clear();
        }
        private void OnDestroy(){OnDisable();}
    }
}