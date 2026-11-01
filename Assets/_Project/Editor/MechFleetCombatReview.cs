using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using ACunicombat.Mechs;
using ACunicombat.Sensing.Contracts;
// Additive acceptance checks over the real playable scene. Commands are issued through the player API.
public static class MechFleetCombatReview
{
    static double stageEnteredAt;
    static bool initialized;static MechFleetController player;static MechFleetController[] actors,wingmen;
    static StreamWriter trace;static string output;static int stage;static double nextSample;
    static Guid attack;static bool linked,acknowledged,detached,returning,freeMode,radarOffLinked;
    static readonly List<string> failures=new();static readonly List<MechActionFeedback> actions=new();
    static readonly Dictionary<Guid,HashSet<string>> observed=new();static readonly HashSet<string> actionEvents=new();static int offensiveShots,defensiveShots,confirmedViolations,explicitShots;static Guid specifiedWeapon;static MechDataLinkPolicy priorLinkPolicy;
    [Serializable]class Row{public double time;public string actor,mode,phase,reason;public int members,capabilities,claims,shots;public bool detached,leader,radar,link;}
    public static void PrepareDefenseSpawn()
    {
        var rigs=UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude);var own=rigs.First(r=>r.GetComponent<MechAiTargetController>()==null);
        int friendly=0,enemy=0;foreach(var rig in rigs){if(rig==own)continue;bool same=rig.GetComponent<CombatTestTarget>().TeamId==own.GetComponent<CombatTestTarget>().TeamId;
            rig.transform.position=own.transform.position+(same?new Vector3((++friendly%2==0?1:-1)*350,150,-200):new Vector3((++enemy%2==0?1:-1)*2500,1500,15000+enemy*4000));}
    }
    public static void ConfigureDefenseEquipment(MechPlayerRig own)
    {
        var interceptor=own.GetComponent<MechRuntimeState>().CurrentLoadout.EnumerateEnabledAssignments().Select(x=>x.part).FirstOrDefault(p=>p.WeaponPerformance.role==MechWeaponRole.InterceptorMissile);
        if(interceptor==null)interceptor=Resources.Load<MechPartDefinition>("AC6Parts/AC6_FULL_WP_R_4300");
        if(interceptor==null||interceptor.WeaponPerformance.role!=MechWeaponRole.InterceptorMissile)throw new Exception("Installed interceptor asset unavailable");
        foreach(var rig in UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude))
        {
            if(rig==own||rig.GetComponent<CombatTestTarget>().TeamId!=own.GetComponent<CombatTestTarget>().TeamId)continue;
            var assembler=rig.GetComponent<MechAssembler>();var loadout=UnityEngine.Object.Instantiate(assembler.Loadout);var slot=loadout.GetSlots().First(s=>s.legacyRole==MechPartSlot.RightBackWeapon);
            var attackSlot=loadout.GetSlots().First(s=>s.legacyRole==MechPartSlot.LeftBackWeapon);
            if(!loadout.TrySetPart(attackSlot.id,Resources.Load<MechPartDefinition>("AC6Parts/AC6_NATIVE_WP_L_4290"),out var attackReason))throw new Exception(attackReason);
            if(!loadout.TrySetPart(slot.id,interceptor,out var reason))throw new Exception(reason);assembler.Loadout=loadout;assembler.Assemble();
        }
    }
    public static void Step(MechPlayerRig own,double elapsed,string directory)
    {
        if(!initialized)
        {
            initialized=true;output=directory;MechFleetController.LogDirectoryOverride=directory+"/fleet-logs";
            actors=UnityEngine.Object.FindObjectsByType<MechFleetController>(FindObjectsInactive.Exclude);foreach(var actor in actors)actor.ReopenLog();player=own.GetComponent<MechFleetController>();
            if(player==null||!player.Configured)throw new Exception("Playable scene has no configured fleet");
            wingmen=actors.Where(a=>!a.IsPlayer&&a.State.Leader==player.MemberId).ToArray();
            if(wingmen.Length<2||!actors.Any(a=>a.IsLeader&&!a.IsPlayer))throw new Exception("Missing real AI leader/wingmen");
            trace=new StreamWriter(directory+"/fleet-scene.jsonl");
        }
        double now=own.GetComponent<MechRadarController>().SimTimeSeconds;
        linked|=player.State.Members.Count>=2 && wingmen.All(w=>w.State.Members.ContainsKey(player.MemberId));
        if(stage==0&&elapsed>20)
        {
            var tracks=new TrackSnapshot[96];var radar=own.GetComponent<MechRadarController>();int n=radar.CopyTracks(tracks);
            var target=tracks.Take(n).FirstOrDefault(t=>t.ObjectClass==TrackObjectClass.AirVehicle&&t.Affiliation!=TrackAffiliation.Friendly&&(t.Identity.Available?t.Identity.CanAttackPlatform:t.Affiliation==TrackAffiliation.Hostile));
            var capability=player.State.Capabilities.Values.FirstOrDefault(p=>p.Sender==wingmen[0].MemberId&&p.Role==(int)MechWeaponRole.AirToAirMissile&&p.Available&&player.State.Fresh(p,now));
            if(target.TrackId.IsValid&&capability.Weapon!=Guid.Empty){specifiedWeapon=capability.Weapon;attack=player.Issue(wingmen[0].MemberId,FleetOrderKind.AttackWithWeapon,target,capability.Weapon);stage++;stageEnteredAt=elapsed;}
        }
        if(attack!=Guid.Empty&&player.Results.TryGetValue(attack,out var reply))acknowledged|=reply.Phase==FleetTaskPhase.Acquiring||reply.Phase==FleetTaskPhase.Executing||reply.Phase==FleetTaskPhase.Returning||reply.Phase==FleetTaskPhase.Completed;
        detached|=wingmen.Any(w=>w.Detached);returning|=wingmen.Any(w=>w.State.Task?.Phase==FleetTaskPhase.Returning);
        if(stage==1&&elapsed-stageEnteredAt>=30){player.SetFormationMode(FleetAttackMode.Free);stage++;stageEnteredAt=elapsed;}
        freeMode|=wingmen.All(w=>w.State.Mode==FleetAttackMode.Free);
        if(stage==2&&elapsed-stageEnteredAt>=15){player.Issue(wingmen[0].MemberId,FleetOrderKind.Return);stage++;stageEnteredAt=elapsed;}
        if(stage==3&&elapsed-stageEnteredAt>=10){own.GetComponent<MechRadarController>().SetRadarPowerEnabled(false);stage++;stageEnteredAt=elapsed;}
        if(stage==4&&elapsed-stageEnteredAt>=5){radarOffLinked=player.State.Members.Values.Any(p=>player.State.Fresh(p,now)&&now-p.ObservedAt<1);own.GetComponent<MechRadarController>().SetRadarPowerEnabled(true);priorLinkPolicy=own.GetComponent<MechRadarController>().DataLinkPolicy;while(own.GetComponent<MechRadarController>().DataLinkPolicy!=MechDataLinkPolicy.Silent)own.GetComponent<MechRadarController>().CycleDataLinkPolicy();player.LinkEnabled=false;stage++;stageEnteredAt=elapsed;}
        if(stage==5&&elapsed-stageEnteredAt>=7){if(wingmen.Any(w=>w.State.Members.TryGetValue(player.MemberId,out var report)&&w.State.Fresh(report,w.GetComponent<MechRadarController>().SimTimeSeconds)))failures.Add("Leader report incorrectly fresh during link outage");player.LinkEnabled=true;while(own.GetComponent<MechRadarController>().DataLinkPolicy!=priorLinkPolicy)own.GetComponent<MechRadarController>().CycleDataLinkPolicy();stage++;stageEnteredAt=elapsed;}
        foreach(var actor in actors)
        {
            if(!observed.TryGetValue(actor.MemberId,out var seen))observed[actor.MemberId]=seen=new HashSet<string>();
            var weapons=actor.GetComponent<MechTestWeaponSystem>();weapons.Actions.CopyEvents(actions);var inventory=new List<MechEquipmentWeaponSnapshot>();weapons.CopyEquipmentWeaponSnapshots(inventory);
            foreach(var action in actions)
            {
                string key=actor.MemberId+":"+action.Id.Source+":"+action.Id.Sequence+":"+action.Phase;
                if(actionEvents.Add(key))trace.WriteLine(JsonUtility.ToJson(new Row{time=now,actor=actor.name,mode=actor.State.Mode.ToString(),phase="Action:"+action.Phase,reason=action.Id.Source+":"+action.SlotId+":"+action.Reason}));
                if(action.Phase!=MechActionPhase.Succeeded||action.Kind!=MechActionKind.Fire||!seen.Add(action.Id.Source+":"+action.Id.Sequence))continue;
                var equipment=inventory.FirstOrDefault(e=>e.InstanceId==action.InstanceId);
                if(MechFleetController.IsDefensiveRole(equipment.Role))defensiveShots++;
                if(actor==wingmen[0]&&action.InstanceId==specifiedWeapon&&actor.State.Mode==FleetAttackMode.Confirmed&&actor.State.Task?.Order.Order==FleetOrderKind.AttackWithWeapon)explicitShots++;
                if(equipment.Role!=MechWeaponRole.AirToAirMissile||actor.IsPlayer)continue;offensiveShots++;
                bool explicitGrant=actor.State.Task?.Order.Order==FleetOrderKind.AttackWithWeapon&&actor.State.Task.Order.Weapon==action.InstanceId;
                if(actor.State.Mode==FleetAttackMode.Confirmed&&!explicitGrant)confirmedViolations++;
            }
            if(elapsed>=nextSample)
            {
                foreach(var e in inventory)trace.WriteLine(JsonUtility.ToJson(new Row{time=now,actor=actor.name,phase="Inventory",reason=e.SlotId+":"+e.Role+":enabled="+e.Enabled+":ammo="+e.Rounds+":magazines="+e.Magazines+":pending="+e.Pending+":id="+e.InstanceId}));
                var state=actor.GetComponent<MechRuntimeState>();foreach(var part in state.AssembledParts.Where(p=>p.part.Category==MechPartCategory.Weapon))trace.WriteLine(JsonUtility.ToJson(new Row{time=now,actor=actor.name,phase="Mount",reason=part.SlotId+":"+part.part.PartId+":active="+(part.instance!=null&&part.instance.activeInHierarchy)+":alive="+actor.GetComponent<CombatTestTarget>().IsAlive+":startup="+actor.GetComponent<MechEngineController>().StartupActive}));
            }
            if(elapsed>=nextSample)trace.WriteLine(JsonUtility.ToJson(new Row{time=now,actor=actor.name,mode=actor.State.Mode.ToString(),phase=actor.State.Task?.Phase.ToString()??"None",reason=actor.LastDecision,members=actor.State.Members.Count,capabilities=actor.State.Capabilities.Count,claims=actor.State.Claims.Count,detached=actor.Detached,leader=actor.IsLeader,radar=actor.GetComponent<MechRadarController>().RadarPowerEnabled,link=actor.LinkEnabled,shots=seen.Count}));
        }
        if(elapsed>=nextSample){nextSample=elapsed+.25;trace.Flush();}
    }
    public static bool Finish()
    {
        if(!initialized)return false;trace?.Dispose();trace=null;
        if(explicitShots==0)failures.Add("No actual specified-weapon commit");
        if(!linked)failures.Add("No physical fleet heartbeat exchange");if(!acknowledged)failures.Add("Explicit weapon command never acknowledged");if(!detached)failures.Add("No individual task detach");if(!returning)failures.Add("No task return transition");if(!freeMode)failures.Add("Free attack policy not delivered");if(!radarOffLinked)failures.Add("No fresh data link with own radar off");if(confirmedViolations>0)failures.Add("Unauthorized offensive missile launches: "+confirmedViolations);if(offensiveShots==0)failures.Add("No actual AI offensive missile commits");
        File.WriteAllText(output+"/fleet-acceptance.txt",$"linked={linked}; commandAck={acknowledged}; detached={detached}; returning={returning}; freeMode={freeMode}; radarOffLink={radarOffLinked}; specifiedShots={explicitShots}; AIoffensiveShots={offensiveShots}; defensiveShots={defensiveShots}; unauthorizedMissiles={confirmedViolations}\n"+string.Join("\n",failures)+"\nPASS="+(failures.Count==0));
        MechFleetController.LogDirectoryOverride=null;return failures.Count==0;
    }
}
