using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace ACunicombat.Mechs
{
    // Sample mission membership only. Tactical decisions never query this scene inventory.
    public static class MechFleetMissionBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register(){SceneManager.sceneLoaded-=Loaded;SceneManager.sceneLoaded+=Loaded;}
        private static void Loaded(Scene scene,LoadSceneMode mode)
        {
            if(scene.name!="AC6PlayerControl")return;
            ConfigureMission(UnityEngine.Object.FindObjectsByType<MechPlayerRig>(FindObjectsInactive.Exclude));
        }
        public static void ConfigureMission(IEnumerable<MechPlayerRig> rigs)
        {
            foreach(var group in rigs.Where(r=>r.GetComponent<CombatTestTarget>()!=null).GroupBy(r=>r.GetComponent<CombatTestTarget>().TeamId))
            {
                var members=group.OrderBy(r=>r.AiControlled?1:0).ThenBy(r=>r.name,StringComparer.Ordinal).ToArray();
                if(members.Length==0||members.Any(r=>r.GetComponent<MechFleetController>()?.Configured==true))continue;
                var leader=members[0].GetComponent<MechFleetController>()??members[0].gameObject.AddComponent<MechFleetController>();
                Guid fleet=Guid.NewGuid();bool player=!members[0].AiControlled;
                var controllers=members.Select(r=>r.GetComponent<MechFleetController>()??r.gameObject.AddComponent<MechFleetController>()).ToArray();
                for(int i=0;i<members.Length;i++)
                {
                    var c=members[i].GetComponent<MechFleetController>()??members[i].gameObject.AddComponent<MechFleetController>();
                    Guid immediate=i==0?leader.MemberId:i%2==1?controllers[i-1].MemberId:leader.MemberId;
                    bool elementLead=i==0||i%2==0&&i+1<members.Length;
                    c.Configure(fleet,immediate,!members[i].AiControlled,elementLead,i,player?ACunicombat.Sensing.Contracts.FleetAttackMode.Confirmed:ACunicombat.Sensing.Contracts.FleetAttackMode.Free,leader.MemberId,members.Length);
                    if(player)c.GrantAuthority(leader.MemberId);
                }
                if(player&&leader.GetComponent<MechFleetCommandPanel>()==null)leader.gameObject.AddComponent<MechFleetCommandPanel>();
            }
        }
    }
}
