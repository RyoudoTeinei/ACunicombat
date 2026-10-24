using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEngine.InputSystem;
namespace ACunicombat.Mechs
{
    // Keyboard-only panel: no map hooks, cursor ownership or replacement of flight controls.
    public sealed class MechFleetCommandPanel:MonoBehaviour
    {
        private MechFleetController fleet;private MechRadarController radar;private bool visible;private int memberIndex,weaponIndex;
        private readonly List<MechFleetRosterEntry> members=new();private readonly List<FleetPacket> weapons=new();
        private void Awake(){fleet=GetComponent<MechFleetController>();radar=GetComponent<MechRadarController>();}
        private void Update()
        {
            if(fleet==null||!fleet.IsPlayer||!fleet.IsLeader||Keyboard.current==null)return;
            var k=Keyboard.current;if(k.f6Key.wasPressedThisFrame)visible=!visible;if(!visible)return;
            members.Clear();members.AddRange(MechFleetController.Roster.Values.Where(r=>fleet.CanCommand(r.Member)).OrderBy(r=>r.Name));
            if(k.upArrowKey.wasPressedThisFrame)memberIndex--;if(k.downArrowKey.wasPressedThisFrame)memberIndex++;
            memberIndex=Math.Clamp(memberIndex,0,Math.Max(0,members.Count-1));weapons.Clear();
            if(members.Count>0)weapons.AddRange(fleet.State.Capabilities.Values.Where(p=>p.Sender==members[memberIndex].Member&&fleet.State.Fresh(p,radar.SimTimeSeconds)&&!MechFleetController.IsDefensiveRole((MechWeaponRole)p.Role)).OrderBy(p=>p.Weapon));
            if(k.rightArrowKey.wasPressedThisFrame)weaponIndex++;if(k.leftArrowKey.wasPressedThisFrame)weaponIndex--;
            weaponIndex=Math.Clamp(weaponIndex,0,weapons.Count);
            if(k.f7Key.wasPressedThisFrame)fleet.SetFormationMode(fleet.State.Mode==FleetAttackMode.Confirmed?FleetAttackMode.Free:FleetAttackMode.Confirmed);
            if(members.Count==0)return;
            if(k.enterKey.wasPressedThisFrame&&radar.TryGetTrack(radar.CandidateTrackId,out var target))
                fleet.Issue(members[memberIndex].Member,weaponIndex==0?FleetOrderKind.Attack:FleetOrderKind.AttackWithWeapon,target,weaponIndex==0?Guid.Empty:weapons[weaponIndex-1].Weapon);
            if(k.backspaceKey.wasPressedThisFrame)fleet.Issue(members[memberIndex].Member,FleetOrderKind.Return);
        }
        private void OnGUI()
        {
            if(!visible||fleet==null||!fleet.Configured)return;
            GUILayout.BeginArea(new Rect(20,140,430,310),GUI.skin.box);
            GUILayout.Label("编队指挥 [F6 关闭]   F7 切换攻击规则");
            GUILayout.Label(fleet.State.Mode==FleetAttackMode.Confirmed?"确认攻击：自动防御及近距火炮；进攻导弹需指定":"自由攻击：允许自主使用进攻导弹");
            GUILayout.Label("↑↓ 选择机体　←→ 选择武器　Enter 攻击当前雷达目标");GUILayout.Label("Backspace 取消任务并归队");
            if(members.Count>0){var r=members[memberIndex];GUILayout.Label("执行者："+r.Name+(r.IsLeader?"（整个编队）":"（单机脱队）"));}
            GUILayout.Label("武器："+(weaponIndex==0?"自行选择":((MechWeaponRole)weapons[weaponIndex-1].Role)+" / "+weapons[weaponIndex-1].Ammunition+" 发"));
            foreach(var result in fleet.Results.Values.OrderByDescending(x=>x.ObservedAt).Take(3))GUILayout.Label(result.Phase+" / "+result.Reason);
            GUILayout.Label("通信："+fleet.LastDecision);GUILayout.EndArea();
        }
    }
}
