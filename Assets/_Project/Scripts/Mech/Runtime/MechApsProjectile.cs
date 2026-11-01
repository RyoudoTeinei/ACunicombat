using System.Collections.Generic;
using UnityEngine;
using ACunicombat.Simulation.Driver;
namespace ACunicombat.Mechs
{
    // A real ballistic countermunition. Truth is read only for physical blast/damage, never target selection.
    [DefaultExecutionOrder(50)]
    public sealed class MechApsProjectile : MonoBehaviour
    {
        private string telemetrySource;private ApsTelemetryData telemetryData;private bool telemetryActive;private string burstTrigger="fuse";
        public void BeginTelemetry(string source,ApsTelemetryData data)
        {
            telemetrySource=source;telemetryData=data;telemetryData.ProjectileId=System.Guid.NewGuid().ToString("N");telemetryActive=true;
            CombatTelemetrySession.RecordApsEvent(Time.timeAsDouble,source,"countermunition_launched",in telemetryData);
        }
        private void RecordOutcome(string eventName,string reason,int hits,int kills)
        {
            if(owner!=null&&eventName=="airburst")owner.GetComponent<MechOwnshipEventLog>()?.AddEvent("aps/burst/"+reason,kills>0?"APS：拦截成功":hits>0?"APS：命中来弹，尚未摧毁":"APS：空爆，未命中",kills==0);
            if(!telemetryActive)return;
            telemetryData.Reason=reason;telemetryData.Phase=burstTrigger;telemetryData.BurstPosition=transform.position;telemetryData.Hits=hits;telemetryData.Kills=kills;telemetryData.Detail=BlastDiagnostic;
            telemetryData.OwnerDistance=owner!=null?Vector3.Distance(transform.position,owner.position):-1;
            if(owner!=null){telemetryData.OwnPosition=owner.position;var motion=owner.GetComponent<MechFlightMotionController>();if(motion!=null){telemetryData.OwnVelocity=motion.Velocity;telemetryData.OwnAcceleration=motion.CurrentControlAcceleration;telemetryData.TranslationInput=motion.TranslationInputActive;}}
            CombatTelemetrySession.RecordApsEvent(detonated?BurstTimeSeconds:Time.timeAsDouble,telemetrySource,eventName,in telemetryData);
        }
        private void OnDestroy(){if(telemetryActive&&!detonated)RecordOutcome("countermunition_removed","RemovedBeforeBurst",0,0);}
        private const float CountermunitionCollisionRadius=.225f;
        private double integratedThroughTime;
        private Transform owner;private int? team;private Vector3 velocity;private float remaining,radius,damage;private bool detonated;private float minimumOwnerDistance,smokeRingScale;private Vector3 fragmentAxis;private float fragmentHalfAngle;
        public static event System.Action<MechApsProjectile,int,int> BurstResolved;
        public double BurstTimeSeconds {get;private set;}
        public Vector3 FragmentDirection=>fragmentAxis;
        public string BlastDiagnostic {get;private set;}="";
        public bool IsFlying=>!detonated;
        public int DamagedMissiles {get;private set;}
        public void Configure(Transform source,Vector3 initialVelocity,float flightSeconds,float blastRadius,float blastDamage,Vector3 forward=default,float halfAngle=60,float minimumOwnerDistance=0,float smokeRingScale=20)
        {
            ACunicombat.Simulation.Driver.BattlefieldEntityIdentity.GetOrAdd(gameObject);
            this.minimumOwnerDistance=Mathf.Max(0,minimumOwnerDistance);this.smokeRingScale=Mathf.Max(1,smokeRingScale);
            owner=source;var target=source!=null?source.GetComponent<CombatTestTarget>():null;team=target!=null?(int?)target.TeamId:null;
            fragmentAxis=forward.sqrMagnitude>.001f?forward.normalized:(initialVelocity.sqrMagnitude>.001f?initialVelocity.normalized:Vector3.forward);fragmentHalfAngle=Mathf.Clamp(halfAngle,1,89);
            integratedThroughTime=Time.timeAsDouble;
            velocity=initialVelocity;remaining=Mathf.Max(.001f,flightSeconds);radius=Mathf.Max(0,blastRadius);damage=Mathf.Max(0,blastDamage);
        }
        private void FixedUpdate()
        {
            // Update can launch between physics ticks. Do not give the charge a full
            // physics step that began before it existed (especially with carrier velocity).
            float elapsed=(float)System.Math.Max(0,Time.fixedTimeAsDouble-integratedThroughTime);
            integratedThroughTime=System.Math.Max(integratedThroughTime,Time.fixedTimeAsDouble);
            Step(Mathf.Min(Time.fixedDeltaTime,elapsed));
        }
        public void Step(float dt)
        {
            if(detonated||!float.IsFinite(dt)||dt<=0)return;
            float travelTime=Mathf.Min(remaining,dt);
            double startTime=Time.fixedTimeAsDouble-dt;
            Vector3 start=transform.position,travel=velocity*travelTime;
            float fraction=1;bool contact=false;
            // Raycasts see colliders at the end of the physics tick. They are valid for
            // stationary geometry, but not for missiles moving tens of metres per tick.
            foreach(var hit in Physics.RaycastAll(start,travel.normalized,travel.magnitude,-5,QueryTriggerInteraction.Collide))
            {
                var projectile=hit.transform.GetComponentInParent<MechTestProjectile>();
                if(projectile!=null&&projectile.IsMissile)continue;
                if((owner!=null&&hit.transform.IsChildOf(owner))||hit.transform.IsChildOf(transform))continue;
                float f=travel.magnitude>.0001f?hit.distance/travel.magnitude:1;
                if(f<=fraction){fraction=f;contact=true;burstTrigger="collision:"+hit.transform.name;}
            }
            // This registry access is physical collision detection only; target selection
            // and the planned fuse still use observer tracks, never projectile truth.
            foreach(var candidate in MechTestProjectile.Registry)
            {
                if(candidate==null||!candidate.IsInterceptableMissile||candidate.IsOwnedBy(owner))continue;
                var targetStart=candidate.PositionAtPhysicsTime(startTime);
                var targetEnd=candidate.PositionAtPhysicsTime(startTime+travelTime);
                if(TrySweptContact(targetStart-start,(targetEnd-targetStart)-travel,
                    candidate.PhysicalCollisionRadius+CountermunitionCollisionRadius,out float f)&&f<=fraction)
                {fraction=f;contact=true;burstTrigger="collision:"+candidate.name;}
            }
            transform.position=start+travel*fraction;
            remaining-=travelTime*fraction;
            if(contact||remaining<=.000001f)Detonate(startTime+travelTime*fraction);
        }
        private static bool TrySweptContact(Vector3 relativeStart,Vector3 relativeTravel,float contactRadius,out float fraction)
        {
            fraction=0;
            double c=Vector3.Dot(relativeStart,relativeStart)-(double)contactRadius*contactRadius;
            if(c<=0)return true;
            double a=Vector3.Dot(relativeTravel,relativeTravel);
            if(a<1e-12)return false;
            double b=2*Vector3.Dot(relativeStart,relativeTravel),d=b*b-4*a*c;
            if(b>=0||d<0)return false;
            double denominator=-b+System.Math.Sqrt(d);
            if(denominator<=0)return false;
            double entry=2*c/denominator;
            if(entry<0||entry>1)return false;
            fraction=(float)entry;return true;
        }
        public static bool InsideFragmentCone(Vector3 forward,Vector3 offset,float halfAngle)
            =>offset.sqrMagnitude<.0001f||Vector3.Dot(forward.normalized,offset.normalized)>=Mathf.Cos(Mathf.Clamp(halfAngle,1,89)*Mathf.Deg2Rad);
        private void Detonate(double burstTime)
        {
            if(detonated)return;detonated=true;BurstTimeSeconds=burstTime;
            if(owner!=null&&Vector3.Distance(transform.position,owner.position)<minimumOwnerDistance)
            {BlastDiagnostic="SafetyInhibited: detonation too close to owner";RecordOutcome("burst_inhibited","SafetyInhibited",0,0);BurstResolved?.Invoke(this,0,0);Destroy(gameObject);return;}
            int destroyed=0;
            var candidates=new List<MechTestProjectile>(MechTestProjectile.Registry);
            BlastDiagnostic=$"trigger={burstTrigger} time={burstTime:F6} registry={candidates.Count} radius={radius} damage={damage} axis={fragmentAxis}; ";
            foreach(var candidate in candidates)
            {
                if(candidate==null)continue;
                if(!candidate.IsInterceptableMissile||candidate.IsOwnedBy(owner)||(team.HasValue&&candidate.LaunchTeam==team))
                {BlastDiagnostic+=$"skip {candidate.name} active={candidate.IsInterceptableMissile} owned={candidate.IsOwnedBy(owner)} team={candidate.LaunchTeam}/{team}; ";continue;}
                var sampledPosition=candidate.PositionAtPhysicsTime(burstTime);var delta=sampledPosition-transform.position;float distance=delta.magnitude;if(distance>radius||!InsideFragmentCone(fragmentAxis,delta,fragmentHalfAngle)){BlastDiagnostic+=$"outside distance={distance:F2} dot={Vector3.Dot(fragmentAxis,delta.normalized):F2}; ";continue;}
                bool blocked=false;
                foreach(var hit in Physics.RaycastAll(transform.position,delta.normalized,distance,-5,QueryTriggerInteraction.Collide))
                    if(!hit.transform.IsChildOf(candidate.transform)&&!hit.transform.IsChildOf(transform)&&!(hit.transform.GetComponentInParent<MechTestProjectile>()?.IsMissile??false)) {blocked=true;BlastDiagnostic+="blocked="+hit.transform.name+"; ";break;}
                if(!blocked&&candidate.ApplyPointDefenseDamage(damage*Mathf.Clamp01(1-distance/Mathf.Max(.001f,radius)),sampledPosition,delta,TestWeaponKind.PointDefenseCannon)){DamagedMissiles++;if(!candidate.IsInterceptableMissile)destroyed++;}
            }
            RecordOutcome("airburst",destroyed>0?"Intercepted":DamagedMissiles>0?"DamagedOnly":"Missed",DamagedMissiles,destroyed);
            BurstResolved?.Invoke(this,DamagedMissiles,destroyed);
            MechApsEffects.Burst(transform.position,fragmentAxis,fragmentHalfAngle,true,smokeRingScale);
            Destroy(gameObject);
        }
    }
}
