using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using UnityEngine;
using UnityEngine.UI;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private sealed class CommandButton{public Rect Bounds;public string Caption;public Action Click;}
  private readonly List<CommandButton> commandButtons=new();
  private readonly List<MechEquipmentWeaponSnapshot> commandEquipment=new();
  private sealed class WeaponReadout{public Guid Instance;public MechWeaponRole Role;public MechPartSlot Mount;public int Ammo;public string Name,SlotId;public float MinimumRange,EffectiveRange,MaximumRange,ProjectileSpeed;}
  private readonly List<WeaponReadout> commandWeapons=new();
  private MechFleetController commandFleet;
  private Guid commandExecutor,lastMapOperation,commandWeapon;
  private string executorLabel="未选择",commandMessage="";
  private IffReplyPolicy previousReplyPolicy=IffReplyPolicy.TrustedOnly;
  private int openPolicyMenu;
  private string policyMenuSelection;
  private Rect policyMenuBounds;
  private string hoveredHelp;
  private Rect hoveredHelpAnchor;
  private void BoundedLabel(Rect bounds,string value,Color color,int size=15)
  {Label(bounds.x,bounds.y,value,color,size);var text=texts[textCount-1];text.rectTransform.sizeDelta=bounds.size;text.horizontalOverflow=HorizontalWrapMode.Wrap;text.verticalOverflow=VerticalWrapMode.Truncate;}
  private void MapButton(Rect bounds,string label,Action click,bool enabled=true,bool active=false,string help=null)
  {
   Fill(bounds,active?PanelActive:enabled&&bounds.Contains(mapPointer)?PanelHover:PanelRaised);
   Box(bounds.x,bounds.y,bounds.width,bounds.height,enabled?PanelBorder:MutedColor,PanelStroke);
   if(active)Fill(new Rect(bounds.x,bounds.y,3,bounds.height),FriendlyColor);
   var labelBounds=bounds;if(help!=null)labelBounds.width-=25;
   MenuLabel(labelBounds,label,enabled?PrimaryText:DisabledText);
   if(enabled)commandButtons.Add(new CommandButton{Bounds=bounds,Caption=label,Click=click});
   if(help!=null)HelpMarker(bounds,help);
  }
  private void MenuLabel(Rect bounds,string label,Color color,int size=14)
  {
   BoundedLabel(new Rect(bounds.x+12,bounds.y,bounds.width-24,bounds.height),label,color,size);
   texts[textCount-1].alignment=TextAnchor.MiddleLeft;
   texts[textCount-1].horizontalOverflow=HorizontalWrapMode.Overflow;
  }
  private void PanelFill(Rect bounds,Color color)
  {
   var image=Stroke();image.color=color;var rect=image.rectTransform;
   rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);
   rect.anchoredPosition=new Vector2(bounds.x,-bounds.y);rect.sizeDelta=bounds.size;rect.localRotation=Quaternion.identity;
  }
  private void MenuPanel(Rect bounds,Color? background=null)
  {
   PanelFill(new Rect(bounds.x+3,bounds.y+5,bounds.width,bounds.height),new Color(0,0,0,.42f));
   PanelFill(bounds,background??PanelBackground);
   Box(bounds.x,bounds.y,bounds.width,bounds.height,PanelBorder,PanelStroke);
   Line(new Vector2(bounds.x+1,bounds.y+1),new Vector2(bounds.xMax-1,bounds.y+1),new Color(.32f,.48f,.49f,.55f));
  }
  private void MenuItem(Rect bounds,string label,Action click,bool enabled=true,bool active=false)
  {
   if(enabled&&(active||bounds.Contains(mapPointer)))Fill(bounds,active?PanelActive:PanelHover);
   if(active)Fill(new Rect(bounds.x+2,bounds.y+8,3,bounds.height-16),FriendlyColor);
   var labelBounds=bounds;labelBounds.width-=25;
   MenuLabel(labelBounds,label,enabled?PrimaryText:DisabledText);
   // Disabled rows consume the click instead of passing it to the map beneath.
   commandButtons.Add(new CommandButton{Bounds=bounds,Caption=label,Click=enabled?click:()=>{}});
   HelpMarker(bounds,MenuHelp(label)+(enabled?"":"\n当前不可用。"));
  }
  private static string WeaponHelpRole(MechWeaponRole role)=>role switch
  {
   MechWeaponRole.HandCannon=>"手持火炮",
   MechWeaponRole.AutomaticCannon=>"自动机炮",
   MechWeaponRole.InterceptorMissile=>"拦截导弹，用于拦截来袭弹药",
   MechWeaponRole.AirToAirMissile=>"空对空导弹，用于攻击空中目标",
   MechWeaponRole.PointDefenseCannon=>"近防炮，用于近距离拦截",
   _=>"用途未报告"
  };
  private static string MenuHelp(string option)=>option switch
  {
   "加入编队"=>"让所选机体加入目标的编队。",
   "退出编队"=>"让所选机体脱离编队，独立行动。",
   "支援"=>"支援目标友机，应对其附近威胁。",
   "护航"=>"选择友方机体或编队，在适当防御位置伴飞并协助拦截。",
   "制空"=>"选择网格区域，攻击区内已确认敌方并拦截威胁；无区域则在航迹点附近巡逻。",
   "防空压制"=>"选择威胁目标，使用可用反辐射弹药压制并协同拦截；辐射关联丢失时停止新发射。",
   "电子战"=>"对指定区域实施电子压制；目前没有电子战套件。",
   "预警与指挥"=>"特型机体承担预警与指挥，在航迹点附近巡逻；目前没有对应套件。",
   "加油与支援"=>"指定目标专供或区域补给；目前没有补给套件。",
   "协助指令"=>"协助目标友机完成它当前的指令。",
   "侦查"=>"接近并观察目标，补充目标情报。",
   "侦察"=>"选择观察区域，或依次指定侦察位置和探测方向。",
   "打击"=>"命令所选机体自行选择武器攻击该目标。",
   "扫描"=>"对目标进行扫描，补充识别信息。",
   "IFF 问询"=>"向目标发送独立身份问询；下方显示应答和认证状态。无应答不等于敌方。",
   "中立"=>"手动标记为中立，不改变真实阵营或 IFF 认证结果。",
   "友军"=>"手动标记为友军，不授予编队指挥权限。",
   "敌军"=>"手动标记为敌军，更新统一识别和攻击许可。",
   "锁定"=>"使用本机雷达锁定目标，仍需满足视场与航迹精度条件。",
   "辐射管制"=>"关闭主动雷达，停止主动雷达辐射。",
   "主动雷达"=>"允许开启主动雷达进行搜索和跟踪。",
   "智能模式"=>"由 AI 按战术情况管理雷达开关。",
   "确认开火"=>"进攻导弹需明确打击指令授权，自动防御仍可执行。",
   "自由开火"=>"允许 AI 自主使用进攻武器，仍遵守敌我识别和开火条件。",
   _=>option
  };
  private void HelpMarker(Rect row,string help)
  {
   var hit=new Rect(row.xMax-25,row.center.y-12,24,24);
   var center=hit.center;bool hover=hit.Contains(mapPointer);
   var color=hover?PrimaryText:new Color(.53f,.64f,.58f,1);
   Circle(center,7,color,24,1);
   Line(center+new Vector2(0,-3.5f),center+new Vector2(0,.5f),color,1.4f);
   Fill(new Rect(center.x-.7f,center.y+3,1.4f,1.4f),color);
   commandButtons.Add(new CommandButton{Bounds=hit,Caption="说明",Click=()=>{}});
   if(hover){hoveredHelp=help;hoveredHelpAnchor=hit;}
  }
  private void DrawHelpTooltip()
  {
   if(string.IsNullOrEmpty(hoveredHelp))return;
   float width=Mathf.Min(340,layoutWidth-16);
   BoundedLabel(new Rect(0,0,width-24,240),hoveredHelp,PrimaryText,14);
   var label=texts[textCount-1];
   float height=Mathf.Clamp(Mathf.Ceil(label.preferredHeight)+20,46,240);
   float x=Mathf.Clamp(hoveredHelpAnchor.xMax-width,8,layoutWidth-width-8);
   float y=hoveredHelpAnchor.y-height-8;
   if(y<8)y=hoveredHelpAnchor.yMax+8;
   y=Mathf.Clamp(y,8,H-height-8);
   MenuPanel(new Rect(x,y,width,height));
   label.rectTransform.anchoredPosition=new Vector2(x+12,-y-10);
   label.rectTransform.sizeDelta=new Vector2(width-24,height-20);
   label.transform.SetAsLastSibling();
  }
  private bool HandleCommandClick(Vector2 point)
  {
   for(int i=commandButtons.Count-1;i>=0;i--)if(commandButtons[i].Bounds.Contains(point)){commandButtons[i].Click();return true;}
   if(formationMenu!=FormationMenu.None){formationMenu=FormationMenu.None;return true;}
   if(speedMenuOpen){speedMenuOpen=false;return true;}
   if(contextOpen){if(!contextBounds.Contains(point))contextOpen=false;return true;}
   if(FormationPanelRect.Contains(point))return true;
   if(openPolicyMenu!=0){if(!policyMenuBounds.Contains(point))openPolicyMenu=0;return true;}
   return false;
  }
  private static string PhaseText(FleetTaskPhase phase)=>phase switch{FleetTaskPhase.Requested=>"待回执",FleetTaskPhase.Acquiring=>"获取目标",FleetTaskPhase.Executing=>"执行中",FleetTaskPhase.Returning=>"归队中",FleetTaskPhase.Completed=>"完成",FleetTaskPhase.Rejected=>"拒绝",FleetTaskPhase.Cancelled=>"取消",FleetTaskPhase.Expired=>"超时",_=>"待命"};
  private static string OrderText(FleetOrderKind order)=>order switch{FleetOrderKind.Attack=>"攻击",FleetOrderKind.AttackWithWeapon=>"指定武器攻击",FleetOrderKind.Return=>"归队",FleetOrderKind.SetAttackMode=>"武器管制",FleetOrderKind.SetRadarControl=>"雷达管制",_=>"移动"};
  private static string ModeText(FleetAttackMode mode)=>mode==FleetAttackMode.Free?"自由开火":"确认开火";
  private static string TaskReadout(FleetPacket p)
  {
   string task=p.Order==FleetOrderKind.AssignMission?TaskGroupName(p.TaskGroup):p.Order==FleetOrderKind.Attack||p.Order==FleetOrderKind.AttackWithWeapon?"打击目标":OrderText(p.Order);
   string action=PhaseText(p.Phase);
   if(p.HasActionReport&&p.Phase!=FleetTaskPhase.Completed&&p.Phase!=FleetTaskPhase.Returning)
    action=(MechAiTargetController.CombatState)p.CombatAction switch{MechAiTargetController.CombatState.Defensive=>"规避中",MechAiTargetController.CombatState.Intercept=>"移动到发射点",MechAiTargetController.CombatState.Extend=>"拉开距离",MechAiTargetController.CombatState.Engage=>"交战中",MechAiTargetController.CombatState.Disabled=>"无法行动",_=>p.Order==FleetOrderKind.Move?"沿路径移动":"搜索目标"};
   return task+"（"+action+"）";
  }
  private static string DamageText(FleetPacket p)=>$"耐久 {p.HealthFraction*100:0}%"+(p.HasDamageReport?$"\n效能下降 {p.ImpairedRegions}\n失效 {p.DisabledRegions}":"");
  private static string IffText(IffOutcome value)=>value switch{IffOutcome.Waiting=>"等待应答",IffOutcome.Authenticated=>"认证成功",IffOutcome.AuthenticatedUnassociated=>"认证成功／待关联",IffOutcome.NoResponse=>"无应答",IffOutcome.Untrusted=>"未获信任",IffOutcome.InvalidProof=>"认证失败",IffOutcome.Replay=>"重复应答",IffOutcome.Expired=>"已过期",IffOutcome.Corrupt=>"报文损坏",IffOutcome.Cancelled=>"已取消",IffOutcome.CapacityLimited=>"问询通道已满",_=>"未问询"};
  private static string ReasonText(FleetTaskReason value)=>value switch{FleetTaskReason.Unauthorized=>"无指挥权限",FleetTaskReason.PlayerIsNotSubordinate=>"玩家不接受下级命令",FleetTaskReason.Expired=>"请求过期",FleetTaskReason.StaleRevision=>"旧版本请求",FleetTaskReason.MissingWeapon=>"装备不存在",FleetTaskReason.MissingObservation=>"缺少目标观测",FleetTaskReason.IdentityDenied=>"身份规则限制",FleetTaskReason.UnsupportedMovement=>"移动任务未接入",FleetTaskReason.LeaderUnavailable=>"长机失联",FleetTaskReason.Cancelled=>"已取消",FleetTaskReason.LeaseExpired=>"预约过期",FleetTaskReason.LinkTimeout=>"通信超时",FleetTaskReason.WeaponUnavailable=>"装备不可用",FleetTaskReason.TargetLost=>"目标丢失",FleetTaskReason.EngagementCompleted=>"攻击批次完成",FleetTaskReason.UnsupportedRadarMode=>"智能模式待接入",_=>""};
  private void TrackMapOperation(Guid operation)
  {lastMapOperation=operation;commandMessage=operation==Guid.Empty?"请求未创建：无权限或条件不足":"请求已提交，等待回执";}
  private bool ResolveSelectedMember(Contact c,out Guid member,out FleetPacket state)
  {
   member=Guid.Empty;state=default;if(commandFleet==null||!commandFleet.Configured)return false;
   if(c.Own){member=commandFleet.MemberId;state=commandFleet.GetOwnStatus();return true;}
   if(cooperative==null||!cooperative.TryGetCooperativeMember(radar.Reference(c.Observation.TrackId),out member))return false;
   if(!commandFleet.State.Members.TryGetValue(member,out state)||!commandFleet.State.Fresh(state,radar.SimTimeSeconds))state=default;
   return true;
  }
  private void DrawMapCommandUi(double now)
  {
   commandButtons.Clear();hoveredHelp=null;commandFleet??=GetComponent<MechFleetController>();
   int index=contacts.FindIndex(c=>c.Key==selected);bool found=index>=0;var c=found?contacts[index]:default;
   bool memberKnown=found&&ResolveSelectedMember(c,out _,out _);
   Guid member=Guid.Empty;FleetPacket state=default;if(memberKnown)ResolveSelectedMember(c,out member,out state);
   bool canControl=found&&commandFleet!=null&&commandFleet.Configured&&(c.Own||memberKnown&&commandFleet.CanCommand(member));
   if(policyMenuSelection!=selected){weaponDirectMode=false;commandWeapon=Guid.Empty;}
   if(policyMenuSelection!=selected||!canControl){openPolicyMenu=0;policyMenuSelection=selected;}
   bool knownStatus=c.Own||memberKnown&&state.HasPlatformStatus;
   string health="未知",heading="未知",order="未报告",radarMode="未知",weaponMode="未知";
   FleetRadarControl control=FleetRadarControl.Active;FleetAttackMode attackMode=FleetAttackMode.Confirmed;
   commandWeapons.Clear();
   if(c.Own)
   {
    var body=GetComponent<CombatTestTarget>();health=body!=null?$"耐久 {body.HealthFraction*100:0}%":"未知";if(commandFleet!=null&&commandFleet.Configured)health=DamageText(commandFleet.GetOwnStatus());
    var flight=GetComponent<MechFlightMotionController>();if(flight!=null){var f=flight.LogicalRotation*Vector3.forward;heading=$"{Mathf.Repeat(Mathf.Atan2(f.x,f.z)*Mathf.Rad2Deg,360):000.0}°";}
    order=commandFleet!=null&&commandFleet.PlayerAutopilotActive?TaskReadout(commandFleet.GetOwnStatus()):"手动操纵（玩家控制）";
    if(commandFleet!=null&&commandFleet.Configured){control=commandFleet.State.RadarControl;attackMode=commandFleet.State.Mode;}
    var weapons=GetComponent<MechTestWeaponSystem>();if(weapons!=null){weapons.CopyEquipmentWeaponSnapshots(commandEquipment);foreach(var e in commandEquipment){weapons.TryGetWeaponSlotStatus(new MechEquipmentSlot(e.SlotId),out var weaponStatus);commandWeapons.Add(new WeaponReadout{Role=e.Role,SlotId=e.SlotId,MinimumRange=weaponStatus.MinimumRangeMeters,MaximumRange=weaponStatus.MaximumRangeMeters,ProjectileSpeed=weapons.EstimatedProjectileSpeed(e.SlotId),EffectiveRange=weaponStatus.EffectiveRangeMeters,Instance=e.InstanceId,Mount=commandFleet!=null?commandFleet.GetEquipmentMount(e.SlotId):MechPartSlot.None,Ammo=e.Rounds,Name=commandFleet!=null?commandFleet.GetEquipmentDisplayName(e.SlotId):"unknown"});}}
   }
   else if(memberKnown)
   {
    if(state.HasPlatformStatus){health=DamageText(state);heading=$"{state.HeadingDegrees:000.0}°";order=state.Phase==FleetTaskPhase.None?(state.IsLeader?"无任务（自主行动）":"编队保持（跟随长机）"):TaskReadout(state);control=state.RadarControl;attackMode=state.Mode;}
    foreach(var cap in commandFleet.State.Capabilities.Values.Where(p=>p.Sender==member&&commandFleet.State.Fresh(p,radar.SimTimeSeconds)).OrderBy(p=>p.Weapon))commandWeapons.Add(new WeaponReadout{Role=(MechWeaponRole)cap.Role,Instance=cap.Weapon,MinimumRange=cap.MinimumRange,MaximumRange=cap.MaximumRange,ProjectileSpeed=cap.ProjectileSpeed,EffectiveRange=cap.EffectiveRange,Mount=(MechPartSlot)cap.WeaponMount,Ammo=cap.Ammunition,Name=cap.WeaponName.IsEmpty?"unknown":cap.WeaponName.ToString()});
   }
   if(knownStatus){radarMode=control==FleetRadarControl.RadiationSilence?"辐射管制":control==FleetRadarControl.Intelligent?"智能模式":"主动雷达";weaponMode=ModeText(attackMode);}
   var footer=FooterRect;MenuPanel(footer);
   float unitWidth=footer.width*.33f,weaponWidth=footer.width*.42f;
   var unit=new Rect(footer.x,footer.y,unitWidth,footer.height);
   var weaponGroup=new Rect(unit.xMax,footer.y,weaponWidth,footer.height);
   var policyGroup=new Rect(weaponGroup.xMax,footer.y,footer.xMax-weaponGroup.xMax,footer.height);
   Line(new Vector2(unit.xMax,footer.y+12),new Vector2(unit.xMax,footer.yMax-12),PanelBorder);
   Line(new Vector2(weaponGroup.xMax,footer.y+12),new Vector2(weaponGroup.xMax,footer.yMax-12),PanelBorder);
   BoundedLabel(new Rect(unit.x+16,unit.y+18,unit.width*.48f,28),found?c.Name:"未选择联系人",PrimaryText,19);
   BoundedLabel(new Rect(unit.x+16,unit.y+47,unit.width*.48f,22),found?c.Type:"—",SecondaryText,12);
   BoundedLabel(new Rect(unit.x+unit.width*.52f,unit.y+36,unit.width*.44f,58),health,PrimaryText,13);
   var speedBox=new Rect(unit.x+16,unit.y+94,142,ControlHeight);var speedRecipient=member;
   MapButton(speedBox,found?$"{c.Velocity.magnitude:0.0} m/s":"—",()=>speedMenuOpen=!speedMenuOpen,canControl);
   speedMenuAnchor=speedBox;speedMenuMember=speedRecipient;
   BoundedLabel(new Rect(speedBox.xMax+12,unit.y+94,unit.width*.42f-24,ControlHeight),"航向  "+heading,PrimaryText,13);
   BoundedLabel(new Rect(unit.x+16,unit.y+133,unit.width-32,25),order,SecondaryText,12);
   Label(weaponGroup.x+16,weaponGroup.y+11,c.Own?"搭载武器":"搭载武器 / 最近报告",SecondaryText,12);
   MechPartSlot[] mounts={MechPartSlot.LeftBackWeapon,MechPartSlot.RightBackWeapon,MechPartSlot.LeftHandWeapon,MechPartSlot.RightHandWeapon};
   string[] mountNames={"左肩","右肩","左手","右手"};
   float cell=(weaponGroup.width-44)/2;
   for(int i=0;i<mounts.Length;i++)
   {
    var weapon=commandWeapons.FirstOrDefault(item=>item.Mount==mounts[i]);
    var weaponBounds=new Rect(weaponGroup.x+16+(i%2)*(cell+12),weaponGroup.y+39+(i/2)*55,cell,48);
    bool active=weapon!=null&&commandWeapon==weapon.Instance;
    Fill(weaponBounds,active?PanelActive:PanelRaised);Box(weaponBounds.x,weaponBounds.y,weaponBounds.width,weaponBounds.height,active?FriendlyColor:PanelBorder);
    if(weapon!=null)
    {
     var chosen=weapon;
     commandButtons.Add(new CommandButton{Bounds=weaponBounds,Caption=mountNames[i]+"武器",Click=()=>{commandWeapon=commandWeapon==chosen.Instance?Guid.Empty:chosen.Instance;weaponDirectMode=commandWeapon!=Guid.Empty;contextOpen=false;}});
    }
    BoundedLabel(new Rect(weaponBounds.x+9,weaponBounds.y+4,weaponBounds.width-36,18),mountNames[i]+"  /  "+(weapon!=null?weapon.Ammo.ToString():"—"),active?FriendlyColor:SecondaryText,11);
    BoundedLabel(new Rect(weaponBounds.x+9,weaponBounds.y+23,weaponBounds.width-36,21),weapon!=null?weapon.Name:c.Own?"未搭载":"未报告",weapon!=null?PrimaryText:DisabledText,12);
    string weaponHelp=weapon==null?mountNames[i]+"挂点："+(c.Own?"未搭载武器。":"尚未收到装备报告。"):
     $"{mountNames[i]}：{weapon.Name}\n{WeaponHelpRole(weapon.Role)} · 剩余弹药 {weapon.Ammo}"+
     (weapon.EffectiveRange>0?$"，有效射程 {weapon.EffectiveRange/1000:0.0} km":"")+
     (weapon.MinimumRange>0?$"，最小距离 {weapon.MinimumRange/1000:0.0} km":"")+
     "\n点击武器进入准星指派，右键目标提交打击；再次点击取消选择。";
    HelpMarker(weaponBounds,weaponHelp);
   }
   Label(policyGroup.x+14,policyGroup.y+11,"交战管制",SecondaryText,12);
   Rect radarButton=new Rect(policyGroup.x+14,policyGroup.y+39,policyGroup.width-28,ControlHeight);
   MapButton(radarButton,radarMode,()=>openPolicyMenu=openPolicyMenu==1?0:1,canControl,openPolicyMenu==1,"雷达管制：管理所选机体的主动雷达辐射许可。点击按钮选择模式。");
   Rect weaponButton=new Rect(policyGroup.x+14,policyGroup.y+82,policyGroup.width-28,ControlHeight);
   MapButton(weaponButton,weaponMode,()=>openPolicyMenu=openPolicyMenu==2?0:2,canControl,openPolicyMenu==2,"武器管制：管理所选机体的自主开火权限。点击按钮选择模式。");
   BoundedLabel(new Rect(policyGroup.x+14,policyGroup.y+125,policyGroup.width-28,32),commandMessage,SecondaryText,11);
   foreach(var observed in contacts)if(ResolveSelectedMember(observed,out var executorMember,out _)&&executorMember==commandExecutor)
   {executorLabel=observed.Name+(MechFleetController.Roster.TryGetValue(commandExecutor,out var roster)&&roster.IsLeader?"（编队）":"");break;}

   DrawFormationPanel();
   DrawMapInteraction();
   DrawCruiseMenu();
   if(openPolicyMenu!=0&&canControl)
   {
    bool radarMenu=openPolicyMenu==1;Rect anchor=radarMenu?radarButton:weaponButton;
    int count=radarMenu?3:2;
    policyMenuBounds=new Rect(anchor.x,Mathf.Max(8,anchor.y-count*36-14),anchor.width,count*36+10);
    if(policyMenuBounds.Contains(mapPointer))hoveredHelp=null;
    MenuPanel(policyMenuBounds,Background);
    Guid recipient=member;bool own=c.Own;
    Rect row=new Rect(anchor.x+1,policyMenuBounds.y+5,anchor.width-2,36);
    if(radarMenu)
    {
     MenuItem(row,"辐射管制",()=>{openPolicyMenu=0;SetRadarPolicy(own,recipient,FleetRadarControl.RadiationSilence);},true,knownStatus&&control==FleetRadarControl.RadiationSilence);
     row.y+=36;MenuItem(row,"主动雷达",()=>{openPolicyMenu=0;SetRadarPolicy(own,recipient,FleetRadarControl.Active);},true,knownStatus&&control==FleetRadarControl.Active);
     row.y+=36;MenuItem(row,"智能模式",null,false);
    }
    else
    {
     MenuItem(row,"确认开火",()=>{openPolicyMenu=0;SetWeaponPolicy(own,recipient,FleetAttackMode.Confirmed);},true,knownStatus&&attackMode==FleetAttackMode.Confirmed);
     row.y+=36;MenuItem(row,"自由开火",()=>{openPolicyMenu=0;SetWeaponPolicy(own,recipient,FleetAttackMode.Free);},true,knownStatus&&attackMode==FleetAttackMode.Free);
    }
   }
   MapButton(TimeControlRect,mapTimePaused?"继续时间":Time.timeScale<=0?"时间已暂停":"暂停时间",
    ToggleMapTime,mapTimePaused||Time.timeScale>0,mapTimePaused);
   BoundedLabel(new Rect(TimeControlRect.xMax+18,20,250,32),$"视野 {HorizontalSpanKm:0.0} km   /   {ContentZoom:0.00}×",SecondaryText,12);
   DrawFormationEditorOverlay();
   DrawMissionAssignmentOverlay();
   DrawHelpTooltip();
  }
  private void SetRadarPolicy(bool own,Guid member,FleetRadarControl mode)
  {if(commandFleet==null)return;if(own){lastMapOperation=Guid.Empty;commandMessage=commandFleet.SetOwnRadarControl(mode)?"本机雷达管制已更新":"雷达管制未执行";}else TrackMapOperation(commandFleet.RequestRadarControl(member,mode));}
  private void SetWeaponPolicy(bool own,Guid member,FleetAttackMode mode)
  {if(commandFleet==null||!commandFleet.Configured)return;if(own){if(!commandFleet.IsLeader&&!commandFleet.IsPlayer){commandMessage="无修改武器管制权限";return;}if(commandFleet.IsLeader)commandFleet.SetFormationMode(mode);else commandFleet.State.SetLocalMode(mode);lastMapOperation=Guid.Empty;commandMessage="本机武器管制已更新";}else TrackMapOperation(commandFleet.Issue(member,FleetOrderKind.SetAttackMode,mode:mode));}
 }
}
