using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using Unity.Mathematics;
namespace ACunicombat.Mechs
{
    // Transport/world boundary: only this layer reads endpoint geometry. No target entity is returned to observers.
    public static class MechIffRadio
    {
        private struct Delivery {public double Due;public Action Deliver;}
        private static readonly List<MechIdentityController> endpoints=new List<MechIdentityController>();
        private static readonly List<Delivery> deliveries=new List<Delivery>();
        private static int lastFrame=-1;
        public static IffAuthority Authority {get;private set;}=new IffAuthority();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] private static void Reset(){endpoints.Clear();deliveries.Clear();Authority=new IffAuthority();lastFrame=-1;}
        public static void Register(MechIdentityController endpoint){if(!endpoints.Contains(endpoint))endpoints.Add(endpoint);}
        public static void Unregister(MechIdentityController endpoint){endpoints.Remove(endpoint);if(endpoint.Credential!=null){// Certificates outlive their transmitter so an in-flight response can still be verified.
            foreach(var observer in endpoints)if(observer!=null&&observer.Radar?.TelemetryDriver!=null)observer.Radar.TelemetryDriver.ReleaseExternalIffEmitter(endpoint.Credential.Subject);}}
        public static double TravelSeconds(double metres)=>Math.Max(0,metres)/299792458.0;
        public static double LinkMargin(double distance,double frequencyHz,double powerDbm,double gainDb,double sensitivityDbm,double interferenceDb)
        {double loss=20*Math.Log10(Math.Max(1,distance))+20*Math.Log10(Math.Max(1,frequencyHz))-147.55;return powerDbm+gainDb-loss-sensitivityDbm-interferenceDb;}
        private static bool Link(MechIdentityController tx,MechIdentityController rx,double distance)=>tx!=null&&rx!=null&&tx.TransmitterAvailable&&rx.ReceiverAvailable&&distance<=Math.Min(tx.RadioRangeMetres,rx.RadioRangeMetres)&&LinkMargin(distance,tx.FrequencyHz,tx.TransmitPowerDbm,tx.AntennaGainDb+rx.AntennaGainDb,rx.ReceiveSensitivityDbm,rx.InterferenceDb)>=0;
        private static void Schedule(double due,Action callback){if(deliveries.Count<2048)deliveries.Add(new Delivery{Due=due,Deliver=callback});}
        public static void Tick(double now)
        {
            if(lastFrame==Time.frameCount)return;lastFrame=Time.frameCount;
            for(int i=deliveries.Count-1;i>=0;i--){if(deliveries[i].Due>now)continue;var item=deliveries[i];deliveries.RemoveAt(i);item.Deliver();}
        }
        private static void Emit(MechIdentityController tx,Vector3 direction,float width,double now)
        {
            var origin=tx.transform.position;var subject=tx.Credential.Subject;float power=tx.TransmitPowerDbm+tx.AntennaGainDb;
            foreach(var rx in endpoints)
            {
                if(rx==null||rx==tx||rx.Radar?.TelemetryDriver==null)continue;
                var observer=rx;double delay=TravelSeconds(Vector3.Distance(origin,rx.transform.position));
                Schedule(now+delay,()=>{if(observer==null||observer.Radar?.TelemetryDriver==null)return;
                    observer.Radar.TelemetryDriver.QueueExternalIffEmission(subject,(double3)(float3)origin,(float3)direction,width,power);});
            }
            tx.Log("radio_emit",default,IffOutcome.None,$"width={width:F3}");
        }
        public static void Interrogate(MechIdentityController sender,IffChallenge challenge,Vector3 direction,double now)
        {
            if(!sender.InterrogatorEnabled||!sender.TransmitterAvailable)return;
            direction.Normalize();Emit(sender,direction,sender.BeamWidthDegrees*Mathf.Deg2Rad,now);
            var origin=sender.transform.position;
            foreach(var receiver in endpoints)
            {
                if(receiver==null||receiver==sender)continue;
                var delta=receiver.transform.position-origin;double distance=delta.magnitude;
                if(Vector3.Angle(direction,delta)>sender.BeamWidthDegrees*.5f)continue;
                if(!Link(sender,receiver,distance)){sender.Log("transport_drop",default,IffOutcome.None,"query: receiver_off/range/link_margin");continue;}
                var target=receiver;double arrival=now+TravelSeconds(distance)+sender.PacketSeconds;
                Schedule(arrival,()=>{if(target!=null&&sender!=null)target.ReceiveQuery(sender,challenge,Time.timeAsDouble);});
            }
        }
        public static void Respond(MechIdentityController responder,MechIdentityController recipient,IffChallenge query,double now)
        {
            double sent=now+responder.ProcessingSeconds;
            Schedule(sent,()=>{
                if(responder==null||recipient==null||!responder.ResponderEnabled||responder.ReplyPolicy==IffReplyPolicy.Silent||responder.ReplyPolicy==IffReplyPolicy.Off||!responder.TransmitterAvailable)return;
                var origin=responder.transform.position;var delta=recipient.transform.position-origin;double distance=delta.magnitude;
                Emit(responder,delta.normalized,responder.BeamWidthDegrees*Mathf.Deg2Rad,Time.timeAsDouble);
                if(!Link(responder,recipient,distance)){responder.Log("transport_drop",default,IffOutcome.None,"reply: receiver_off/range/link_margin");return;}
                var motion=responder.GetComponent<MechFlightMotionController>();
                var response=Authority.Reply(responder.Credential,query,Time.timeAsDouble,(double3)(float3)origin,(double3)(float3)(motion!=null?motion.Velocity:Vector3.zero));
                // Receiver obtains a noisy bearing, not a scene object identity.
                uint hash=(uint)response.Subject.GetHashCode();float error=((hash%1001)/1000f-.5f)*.4f;
                var bearing=Quaternion.AngleAxis(error,Vector3.up)*(-delta).normalized;
                Schedule(Time.timeAsDouble+TravelSeconds(distance)+responder.PacketSeconds,()=>{if(recipient!=null&&recipient.ReceiverAvailable)recipient.ReceiveReply(response,bearing,Time.timeAsDouble);});
            });
        }
    }
}
