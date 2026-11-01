using System;
using System.Collections.Generic;
using UnityEngine;
namespace ACunicombat.Mechs
{
 [DisallowMultipleComponent]
 public sealed class MechOwnshipEventLog:MonoBehaviour
 {
  public sealed class Entry {public string Key,Text,Display;public double Time;public int Count=1;public bool Warning;}
  private readonly List<Entry> entries=new List<Entry>(64);
  public IReadOnlyList<Entry> Entries=>entries;
  private GUIStyle style,title;private readonly GUIContent lineContent=new GUIContent();
  public static MechOwnshipEventLog Ensure(GameObject owner)=>owner.GetComponent<MechOwnshipEventLog>()??owner.AddComponent<MechOwnshipEventLog>();
  public void AddEvent(string key,string text,bool warning=false,bool replace=false)
  {AddAt(key,text,Time.timeAsDouble,warning,replace);}
  public void AddAt(string key,string text,double now,bool warning=false,bool replace=false)
  {
   for(int i=entries.Count-1;i>=0;i--)if(entries[i].Key==key&&(replace||now-entries[i].Time<=2))
   {var e=entries[i];entries.RemoveAt(i);bool changed=e.Text!=text;if(changed)Persist(key,text,now,warning);if(e.Text==text)e.Count++;else e.Count=1;e.Text=text;e.Time=now;e.Warning=warning;Format(e);entries.Add(e);return;}
   if(entries.Count==64)entries.RemoveAt(0);var added=new Entry{Key=key,Text=text,Time=now,Warning=warning};Format(added);entries.Add(added);Persist(key,text,now,warning);
  }
  private void Persist(string key,string text,double now,bool warning)=>ACunicombat.Simulation.Driver.CombatTelemetrySession.RecordOwnshipEvent(now,name+"#"+GetEntityId(),key,text,warning);
  private static void Format(Entry e){int seconds=(int)Math.Max(0,e.Time);e.Display=$"{seconds/60:00}:{seconds%60:00}  {e.Text}"+(e.Count>1?$" ×{e.Count}":"");}
  public void Record(MechActionFeedback f,string slot,bool missile)
  {
   if(f.Phase==MechActionPhase.Reserved)return;
   if(f.Id.Source==MechActionSource.AutomaticDefense&&(f.Phase==MechActionPhase.Proposed||f.Phase==MechActionPhase.Running))return;
   if(!missile&&f.Kind==MechActionKind.Fire&&(f.Phase==MechActionPhase.Proposed||f.Phase==MechActionPhase.Running))return;
   bool bad=f.Phase==MechActionPhase.Failed||f.Phase==MechActionPhase.Expired||f.Phase==MechActionPhase.Cancelled;
   string action=f.Kind==MechActionKind.Reload?"换弹":"发射";
   string text=f.Phase switch{MechActionPhase.Proposed=>$"{slot}：{action}请求已接收",MechActionPhase.Running=>$"{slot}：{action}准备中",MechActionPhase.Succeeded=>$"{slot}：{action}成功",MechActionPhase.Cancelled=>$"{slot}：{action}取消 · {Reason(f.Reason)}",_=>$"{slot}：未{action} · {Reason(f.Reason)}"};
   bool shot=(!missile||f.Id.Source==MechActionSource.AutomaticDefense)&&f.Kind==MechActionKind.Fire;
   string key=shot?"gun/"+slot+"/"+(bad?f.Reason:"fired"):"action/"+f.Id.Source+"/"+f.Id.Sequence;
   AddAt(key,text,f.Time,bad,!shot);
  }
  public static string Reason(string reason)=>reason switch
  {
   "MissingTrack"=>"没有可用目标航迹", "SearchOnly"=>"只有搜索信息，尚未具备发射条件", "SourceEvidenceExpired"=>"制导信息已过期",
   "StaleOrUnresolvedPosition"=>"目标定位精度不足或航迹过期", "StaleObserverTrack"=>"目标航迹已丢失", "TrackChanged"=>"目标已变化，请重新指定",
   "OutsideWeaponEnvelope"=>"目标超出当前导弹可达范围", "OutsideMissileSafeFlightEnvelope"=>"安全距离或预测拦截距离不满足发射条件",
   "GuidanceCapacity" or "GuidanceChannelsBusy"=>"制导通道已占满", "DataLinkUnavailable" or "InterceptorGuidanceUnavailable"=>"数据链关闭或上行通道已占满",
   "FireControlUnavailable"=>"火控组件不可用", "IdentityDenied" or "IdentityRulesOfEngagement"=>"身份规则禁止攻击该目标",
   "MissileMagazineReloading" or "Reloading"=>"正在换弹", "NoCompatibleLoadedMissileBank"=>"当前弹仓没有适合该目标的可用导弹",
   "NoAmmo"=>"弹药耗尽", "CoolingDown"=>"武器冷却中", "Overheated"=>"武器过热", "InsufficientEnergy"=>"可用能源不足",
   "PendingAction" or "EquipmentBusy"=>"该武器已有请求，正在准备或执行", "DeployingOrRecovering"=>"武器正在展开或收回",
   "ShoulderBlocked"=>"扩展组件占用了该肩部武器", "EquipmentUnavailable"=>"武器未装备、已关闭或不可用", "ComponentFailed"=>"组件完全损坏",
   "EngineStartup"=>"开机自检尚未完成", "WeaponsUnavailable"=>"本机武器系统不可用", "NotMissileWeapon"=>"该槽位不是导弹武器",
   "EmissionDenied"=>"当前电磁管制禁止此次发射", "DeadlineExpired" or "InvalidOrExpiredRequest"=>"请求超时，未执行",
   "ResourceConflict" or "ResourcesChanged"=>"能源或制导资源发生冲突", "NoMuzzle"=>"未找到有效发射口",
   "NoInterceptorTrack"=>"没有确认且可拦截的来袭导弹", "NoInterceptSolution"=>"当前运动状态下无法形成拦截解",
   "ReleaseAuthorizationChanged"=>"提交时发射授权已失效", "ExecutionRejected"=>"执行阶段拒绝，未生成弹药", "ReloadUnavailable"=>"当前不能换弹",
   "SelfDefenseNotAuthorized" or "DefensePolicyRejected"=>"当前目标不满足自动防御条件", "Cancelled"=>"请求已撤销",
   null or ""=>"条件未满足", _=>MechDefenseExtensions.DisplayReason(reason)
  };
  private void OnEnable(){CombatTestTarget.DamageApplied+=OnDamage;}
  private void OnDisable(){CombatTestTarget.DamageApplied-=OnDamage;}
  private void OnDamage(CombatTestTarget target,Transform source,TestWeaponKind kind,float damage){if(target!=null&&target.gameObject==gameObject&&damage>0)AddEvent("damage/"+kind,kind==TestWeaponKind.Missile?"本机受到导弹命中":"本机受到武器攻击",true);}
  private void OnDestroy(){CombatTestTarget.DamageApplied-=OnDamage;}
  private void OnGUI()
  {
   if(Event.current.type!=EventType.Repaint||GetComponent<MechPlayerRig>()==null||GetComponent<MechPlayerRig>().AiControlled||MechAssemblyMenu.IsAnyMenuVisible||MechSolarSystemMap.IsAnyMapVisible)return;
   style??=new GUIStyle(GUI.skin.label){fontSize=14,wordWrap=true,clipping=TextClipping.Clip};title??=new GUIStyle(GUI.skin.label){fontSize=14,fontStyle=FontStyle.Bold};
   var matrix=MechHudLayout.BeginScaledGui();var color=GUI.color;var rect=MechHudLayout.OwnshipDefenseStatusRect;GUI.color=new Color(.4f,1,.65f);GUI.Label(rect,"本机事件",title);float y=rect.y+24;
   if(entries.Count==0)GUI.Label(new Rect(rect.x,y,rect.width,22),"暂无事件",style);
   for(int i=entries.Count-1,shown=0;i>=0&&shown<6;i--,shown++){var e=entries[i];GUI.color=e.Warning?new Color(1,.63f,.3f):new Color(.4f,1,.65f);lineContent.text=e.Display;float height=Mathf.Min(54,Mathf.Max(20,style.CalcHeight(lineContent,rect.width)));if(y+height>rect.y+200)break;GUI.Label(new Rect(rect.x,y,rect.width,height),lineContent,style);y+=height+3;}
   GUI.color=color;GUI.matrix=matrix;
  }
 }
}
