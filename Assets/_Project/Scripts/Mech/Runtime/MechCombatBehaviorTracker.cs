using System;
using System.Collections.Generic;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
namespace ACunicombat.Mechs
{
    public enum ProtectedContactKind { Ownship, FriendlyPlatform, MissionProtected, FriendlyEquipment, UnprotectedPlatform, UnprotectedEquipment }
    public struct ProtectedCombatContact
    {
        public Vector3 Position,Velocity;public float Radius;public ProtectedContactKind Kind;
        // Receiver-local observed track identity, never a projectile/world truth ID.
        public TrackId SensorTrack,AssociatedSensorTrack;
        public Guid LaunchIdentity;
    }
    // Observer-local history. Inputs are already sensor measurements; no projectile owner or selected target is accepted.
    public sealed class MechCombatBehaviorTracker
    {
        private sealed class Weapon
        {
            public TrackSnapshot Source;public Guid Evidence;public bool Missile,Confirmed,Acquired;public Vector3 FirstPosition;public int SeparationSamples;
            public Vector3 Position,Velocity;public double Time,Launched;public float SourceQuality;public int ThreatSamples;public TrackId MissileTrack,MissileSensorTrack;public Guid MissileLaunchIdentity;public double IntentEvidenceTime=-1,LastDiagnostic,LastSensorTime;public int IntentSamples,DirectedSamples;public double DirectedSince;public ProtectedContactKind DirectedKind;
        }
        private readonly Dictionary<Guid,Weapon> weapons=new Dictionary<Guid,Weapon>();
        private readonly List<Guid> expired=new List<Guid>();
        public int Count=>weapons.Count;
        public event Action<CombatBehaviorReport> Report;
        public event Action<Guid,string> Diagnostic;
        public static int Associate(TrackSnapshot[] candidates,int count,Vector3 point,Vector3 velocity,double trackTime,float positionError,bool useVelocity,out float quality)
        {
            quality=0;int best=-1;double first=double.PositiveInfinity,second=double.PositiveInfinity;
            for(int i=0;i<count;i++)
            {
                var t=candidates[i];if(!t.TrackId.IsValid||t.Lifecycle==TrackLifecycle.Tentative||t.Lifecycle==TrackLifecycle.Dropped||trackTime-t.LastUpdateTime>3||t.Confidence<.5f||t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy)continue;
                var estimate=t.PredictPosition(trackTime);double score=0;
                for(int axis=0;axis<3;axis++){double variance=t.GetCovariance(axis,axis);if(!double.IsFinite(variance)||variance<0||variance>250000){score=double.PositiveInfinity;break;}double d=estimate[axis]-point[axis];score+=d*d/(variance+positionError*positionError+400);
                    if(useVelocity){double dv=t.Velocity[axis]-velocity[axis];score+=dv*dv/Math.Max(25,t.GetCovariance(axis+3,axis+3));}}
                if(score<first){second=first;first=score;best=i;}else if(score<second)second=score;
            }
            if(first>16||second-first<4)return -1;
            quality=(float)(.99-.09*first/16);return best;
        }
        public bool Launch(Guid observation,bool missile,Vector3 point,double now,float error,TrackSnapshot[] candidates,int count,double trackTime)
        {
            Prune(now);if(observation==Guid.Empty||!double.IsFinite(now)||weapons.Count>=256||weapons.ContainsKey(observation))return false;
            int source=Associate(candidates,count,point,Vector3.zero,trackTime,error,false,out float quality);
            if(source<0)return false;
            var track=candidates[source];track.Position=track.PredictPosition(trackTime);track.LastUpdateTime=track.LastPredictTime=now;
            var w=new Weapon{Source=track,Evidence=observation,Missile=missile,Position=point,Time=now,Launched=now,SourceQuality=quality};weapons.Add(observation,w);
            Publish(w,CombatBehaviorKind.WeaponLaunch,now,.75f);return true;
        }
        public bool Acquire(Guid observation,bool missile,Vector3 point,double now,float error,TrackSnapshot[] candidates,int count,double trackTime)
        {
            Prune(now);if(observation==Guid.Empty||weapons.ContainsKey(observation)||weapons.Count>=256)return false;
            int source=Associate(candidates,count,point,Vector3.zero,trackTime,error,false,out float quality);
            if(source<0)return false;
            // Only a directly observed separation near a unique platform can recover a missed launch.
            // Never extrapolate a distant inbound missile to the nearest alleged attacker.
            var track=candidates[source];var sourcePosition=(Vector3)(float3)track.PredictPosition(trackTime);
            if(Vector3.Distance(point,sourcePosition)>Mathf.Min(250,50+3*error))return false;
            track.Position=track.PredictPosition(trackTime);track.LastUpdateTime=track.LastPredictTime=now;
            weapons.Add(observation,new Weapon{Source=track,Evidence=observation,Missile=missile,Position=point,FirstPosition=point,Time=now,Launched=now,SourceQuality=quality,Acquired=true});return true;
        }
        public void Sample(Guid observation,Vector3 position,double now,float uncertainty,IReadOnlyList<ProtectedCombatContact> protectedContacts)
        {
            if(!weapons.TryGetValue(observation,out var w)||w.Confirmed)return;
            double dt=now-w.Time;if(dt<=.02)return;
            if(dt>.75||now<w.Time)
            {
                if(now>=w.Time&&w.MissileTrack.IsValid&&now-w.LastSensorTime<=.75){w.Position=position;w.Time=now;return;}
                weapons.Remove(observation);return;
            }
            var velocity=(position-w.Position)/(float)dt;w.Position=position;w.Velocity=velocity;w.Time=now;
            if(w.Acquired)
            {
                var relative=position-(Vector3)(float3)w.Source.PredictPosition(now);
                var relativeVelocity=velocity-(Vector3)(float3)w.Source.Velocity;
                bool separating=Vector3.Dot(relative,relativeVelocity)>0&&Vector3.Distance(position,w.FirstPosition)>Mathf.Max(10,2*uncertainty);
                w.SeparationSamples=separating?w.SeparationSamples+1:0;
                if(w.SeparationSamples<2)return;
                w.Acquired=false;Publish(w,CombatBehaviorKind.WeaponLaunch,now,.75f);
            }
            bool threatening=false,ambiguous=false;ProtectedContactKind kind=default;ProtectedCombatContact selectedVictim=default;float earliest=float.PositiveInfinity;
            foreach(var contact in protectedContacts)
            {
                if(IsSameWeapon(position,velocity,uncertainty,contact,w.MissileTrack,w.MissileSensorTrack,w.MissileLaunchIdentity)||!ApproachesProtected(position,velocity,uncertainty,contact,w.Missile?120:8))continue;
                var relative=position-contact.Position;var speed=velocity-contact.Velocity;float time=-Vector3.Dot(relative,speed)/speed.sqrMagnitude;
                bool isProtected=contact.Kind!=ProtectedContactKind.UnprotectedPlatform&&contact.Kind!=ProtectedContactKind.UnprotectedEquipment;
                if(Mathf.Abs(time-earliest)<.05f&&isProtected!=threatening)ambiguous=true;
                if(time<earliest-.05f){earliest=time;threatening=isProtected;kind=contact.Kind;selectedVictim=contact;ambiguous=false;}
            }
            threatening&=!ambiguous;
            if(!threatening&&w.Missile)
            {
                int victim=DirectedVictim(position,velocity,uncertainty,protectedContacts,w.MissileTrack,w.MissileSensorTrack,w.MissileLaunchIdentity);
                if(victim>=0)
                {
                    var directedKind=protectedContacts[victim].Kind;
                    if(w.DirectedSamples==0||w.DirectedKind!=directedKind){w.DirectedSince=now;w.DirectedSamples=0;w.DirectedKind=directedKind;}
                    w.DirectedSamples++;
                    if(w.DirectedSamples>=3&&now-w.DirectedSince>=.5){threatening=true;kind=directedKind;selectedVictim=protectedContacts[victim];}
                }
                else w.DirectedSamples=0;
            }
            if(!threatening&&now-w.LastDiagnostic>=1&&protectedContacts.Count>0)
            {
                w.LastDiagnostic=now;var c=protectedContacts[0];var r=position-c.Position;var v=velocity-c.Velocity;float t=v.sqrMagnitude>0?-Vector3.Dot(r,v)/v.sqrMagnitude:0;
                Diagnostic?.Invoke(observation,$"source={w.Source.TrackId}; cpa={(r+v*t).magnitude:F1}; seconds={t:F2}; error={uncertainty:F1}; radius={c.Radius}; bound={w.MissileTrack}");
            }
            w.ThreatSamples=threatening?w.ThreatSamples+1:0;
            if(w.ThreatSamples<2||now-w.Launched<.1)return;
            w.Confirmed=true;
            Diagnostic?.Invoke(observation,$"confirmed_attack: source={w.Source.TrackId}; missile={w.Missile}; bound={w.MissileTrack}; sensorAlias={w.MissileSensorTrack}; victimKind={kind}; victimTrack={selectedVictim.SensorTrack}; victimAlias={selectedVictim.AssociatedSensorTrack}; weaponPosition={position.ToString("R")}; weaponVelocity={velocity.ToString("R")}; victimPosition={selectedVictim.Position.ToString("R")}; victimVelocity={selectedVictim.Velocity.ToString("R")}; error={uncertainty:R}; radius={selectedVictim.Radius:R}");
            Publish(w,kind==ProtectedContactKind.FriendlyEquipment?CombatBehaviorKind.EquipmentAttack:kind==ProtectedContactKind.MissionProtected?CombatBehaviorKind.ProtectedAssetAttack:w.Missile?CombatBehaviorKind.MissileAttack:CombatBehaviorKind.GunfireAttack,now,.98f);
        }
        public static int DirectedVictim(Vector3 position,Vector3 velocity,float uncertainty,IReadOnlyList<ProtectedCombatContact> contacts,TrackId observedWeapon=default,TrackId observedSensor=default,Guid observedLaunch=default)
        {
            // Behavior inference, not a lethal-radius or APS solution: sustained directed missile flight.
            // Three degrees matches the full-heading-evidence region of MissileIntentEstimator.
            int best=-1;float first=180,second=180;
            for(int i=0;i<contacts.Count;i++)
            {
                var c=contacts[i];if(IsSameWeapon(position,velocity,uncertainty,c,observedWeapon,observedSensor,observedLaunch))continue;
                var offset=c.Position-position;float range=offset.magnitude;var relativeVelocity=velocity-c.Velocity;
                if(AlreadyInsideEquipmentVolume(range*range,uncertainty,c))continue;
                if(range<1||relativeVelocity.magnitude<15||Vector3.Dot(offset,relativeVelocity)<=0||!float.IsFinite(uncertainty)||uncertainty<0||uncertainty/range>Mathf.Deg2Rad)continue;
                var lead=offset+c.Velocity*Mathf.Min(12,range/Mathf.Max(1,velocity.magnitude));
                float angle=Vector3.Angle(velocity,lead);
                if(angle<first){second=first;first=angle;best=i;}else if(angle<second)second=angle;
            }
            if(best<0||first>3||second-first<1)return -1;
            var kind=contacts[best].Kind;
            return kind==ProtectedContactKind.UnprotectedPlatform||kind==ProtectedContactKind.UnprotectedEquipment?-1:best;
        }
        public void FollowSensorTracks(MissileTrackSnapshot[] tracks,int count,double now,IReadOnlyList<ProtectedCombatContact> contacts)
        {
            // Correlate anonymous optical motion to a unique sensor track; no projectile owner/target access.
            foreach(var pair in weapons)
            {
                var w=pair.Value;if(!w.Missile||w.Confirmed||w.Acquired||now-Math.Max(w.Time,w.LastSensorTime)>.75)continue;
                int match=-1,boundRank=0;bool boundAmbiguous=false;float best=float.PositiveInfinity,second=float.PositiveInfinity;
                for(int i=0;i<count;i++)
                {
                    var t=tracks[i];if(!t.TrackId.IsValid||!t.IsWarning||now-t.LastEvidenceTime>.6||now<t.LastEvidenceTime-.05||t.PositionUncertaintyMeters>500)continue;
                    if(w.MissileTrack.IsValid)
                    {
                        if(w.MissileLaunchIdentity!=Guid.Empty&&t.OwnLaunchIdentity!=Guid.Empty&&w.MissileLaunchIdentity!=t.OwnLaunchIdentity)continue;
                        int rank=w.MissileLaunchIdentity!=Guid.Empty&&t.OwnLaunchIdentity==w.MissileLaunchIdentity?3:
                            t.AssociatedSensorTrackId.IsValid&&(t.AssociatedSensorTrackId.Equals(w.MissileTrack)||t.AssociatedSensorTrackId.Equals(w.MissileSensorTrack))?2:
                            t.TrackId.Equals(w.MissileTrack)||w.MissileSensorTrack.IsValid&&t.TrackId.Equals(w.MissileSensorTrack)?1:0;
                        if(rank>boundRank){match=i;boundRank=rank;boundAmbiguous=false;}
                        else if(rank>0&&rank==boundRank&&match>=0&&!t.TrackId.Equals(tracks[match].TrackId))boundAmbiguous=true;
                        continue;
                    }
                    if(w.Velocity.sqrMagnitude<1||Vector3.Dot(w.Velocity.normalized,((Vector3)(float3)t.Velocity).normalized)<.9f)continue;
                    float gate=Mathf.Max(30,3*t.PositionUncertaintyMeters);
                    float score=Vector3.Distance((Vector3)(float3)t.PredictPosition(now),w.Position+w.Velocity*(float)(now-w.Time))/gate;
                    if(score<best){second=best;best=score;match=i;}else if(score<second)second=score;
                }
                if(match<0||boundAmbiguous||(!w.MissileTrack.IsValid&&(best>1||second-best<.5f)))continue;
                var track=tracks[match];w.MissileTrack=track.TrackId;
                if(track.AssociatedSensorTrackId.IsValid)w.MissileSensorTrack=track.AssociatedSensorTrackId;
                else if(!w.MissileSensorTrack.IsValid)w.MissileSensorTrack=track.TrackId;
                if(track.OwnLaunchIdentity!=Guid.Empty)w.MissileLaunchIdentity=track.OwnLaunchIdentity;
                if(track.LastEvidenceTime<=w.IntentEvidenceTime)continue;
                w.IntentEvidenceTime=track.LastEvidenceTime;
                w.IntentSamples=track.IsLikelyTargetingOwnship&&track.Confidence>=.5f?w.IntentSamples+1:0;
                if(w.IntentSamples>=2)
                {
                    w.Confirmed=true;Diagnostic?.Invoke(pair.Key,$"confirmed_attack: source={w.Source.TrackId}; missile=true; bound={track.TrackId}; mechanism=sensor_intent; intent={track.TargetIntent}; confidence={track.TargetIntentConfidence:R}");Publish(w,CombatBehaviorKind.MissileAttack,now,.98f);continue;
                }
                // Keep optical differentiation independent of radar position errors.
                w.LastSensorTime=now;
                if(now-w.LastDiagnostic>=1){w.LastDiagnostic=now;Diagnostic?.Invoke(pair.Key,$"sensor={track.TrackId}; intent={track.TargetIntent}; confidence={track.TargetIntentConfidence:F2}; rangeError={track.PositionUncertaintyMeters:F1}; source={w.Source.TrackId}");}
            }
        }
        private static bool IsSameWeapon(Vector3 position,Vector3 velocity,float uncertainty,ProtectedCombatContact contact,TrackId observedWeapon=default,TrackId observedSensor=default,Guid observedLaunch=default)=>
            (observedWeapon.IsValid&&(contact.SensorTrack.Equals(observedWeapon)||contact.AssociatedSensorTrack.Equals(observedWeapon)))||
            (observedSensor.IsValid&&(contact.SensorTrack.Equals(observedSensor)||contact.AssociatedSensorTrack.Equals(observedSensor)))||
            (observedLaunch!=Guid.Empty&&contact.LaunchIdentity==observedLaunch)||
            (contact.Kind==ProtectedContactKind.FriendlyEquipment||contact.Kind==ProtectedContactKind.UnprotectedEquipment)&&Vector3.Distance(position,contact.Position)<contact.Radius+3*Mathf.Max(1,uncertainty)&&Vector3.Distance(velocity,contact.Velocity)<25;
        public static bool ApproachesProtected(Vector3 position,Vector3 velocity,float uncertainty,ProtectedCombatContact contact,float horizon)
        {
            var relative=position-contact.Position;var speed=velocity-contact.Velocity;
            float squared=speed.sqrMagnitude;if(squared<1||!float.IsFinite(squared)||!float.IsFinite(uncertainty)||uncertainty<0)return false;
            // Co-located/co-launched rounds have not demonstrated an approach from
            // outside the protected volume. Tiny relative velocity is not proof of
            // an attack. Actual impact remains independent confirmation below.
            if(AlreadyInsideEquipmentVolume(relative.sqrMagnitude,uncertainty,contact))return false;
            float time=-Vector3.Dot(relative,speed)/squared;if(time<=0||time>horizon)return false;
            // An uncertain near-pass is not confirmation of an attack on the protected volume.
            return (relative+speed*time).magnitude+uncertainty<=contact.Radius;
        }
        private static bool AlreadyInsideEquipmentVolume(float squaredDistance,float uncertainty,ProtectedCombatContact contact)
        {
            if(contact.Kind!=ProtectedContactKind.FriendlyEquipment&&contact.Kind!=ProtectedContactKind.UnprotectedEquipment)return false;
            float radius=contact.Radius+Mathf.Max(0,uncertainty);
            return squaredDistance<=radius*radius;
        }
        public void Impact(Guid observation,Vector3 point,double now,IReadOnlyList<ProtectedCombatContact> protectedContacts)
        {
            if(!weapons.TryGetValue(observation,out var w)||w.Acquired||now-Math.Max(w.Time,w.LastSensorTime)>.75)return;
            int best=-1;float distance=float.PositiveInfinity,second=float.PositiveInfinity;
            for(int i=0;i<protectedContacts.Count;i++){if(IsSameWeapon(w.Position,w.Velocity,1,protectedContacts[i],w.MissileTrack,w.MissileSensorTrack,w.MissileLaunchIdentity))continue;float d=Vector3.Distance(point,protectedContacts[i].Position);if(d<distance){second=distance;distance=d;best=i;}else if(d<second)second=d;}
            if(best<0||second-distance<5)return;var contact=protectedContacts[best];
            if(contact.Kind==ProtectedContactKind.UnprotectedPlatform||contact.Kind==ProtectedContactKind.UnprotectedEquipment||distance>contact.Radius)return;
            if(!w.Confirmed){w.Confirmed=true;Publish(w,contact.Kind==ProtectedContactKind.FriendlyEquipment?CombatBehaviorKind.EquipmentAttack:contact.Kind==ProtectedContactKind.MissionProtected?CombatBehaviorKind.ProtectedAssetAttack:CombatBehaviorKind.WeaponImpact,now,1);}
        }
        private void Publish(Weapon w,CombatBehaviorKind kind,double now,float quality)
        {
            var source=w.Source;source.Position=source.PredictPosition(now);source.LastPredictTime=now;
            Report?.Invoke(new CombatBehaviorReport{OriginalId=w.Evidence,Kind=kind,SourceTrack=source,Quality=quality,Association=w.SourceQuality,ObservedAt=now,ExpiresAt=now+15});
        }
        public void Prune(double now){expired.Clear();foreach(var pair in weapons)if(now-Math.Max(pair.Value.Time,pair.Value.LastSensorTime)>2||now-pair.Value.Launched>120)expired.Add(pair.Key);foreach(var id in expired)weapons.Remove(id);}
    }
}
