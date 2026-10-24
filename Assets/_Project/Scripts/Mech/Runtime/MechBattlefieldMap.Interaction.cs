using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private Vector2 mapPointer,contextPoint;
  private bool contextOpen,routeDraft,identitySubmenu;
  private readonly System.Collections.Generic.Dictionary<string,string> iffMenuStatus=new();
  private readonly System.Collections.Generic.HashSet<string> iffMenuFollowing=new();
  private Contact contextTarget;
  private Guid routeRecipient;
  private Unity.Collections.FixedList512Bytes<double3> draftWaypoints;
  private float executorAltitude;
  private Rect contextBounds;
  private sealed class ManualMapRoute
  {
   public Guid Operation;
   public Vector3 Start;
   public Unity.Collections.FixedList512Bytes<double3> Points;
  }
  private readonly System.Collections.Generic.Dictionary<Guid,ManualMapRoute> manualMapRoutes=new();
  private static readonly Color RouteBlue=new Color(.18f,.52f,.88f,.95f);
  private static readonly Color RouteRed=new Color(.82f,.19f,.16f,1);
  private bool weaponDirectMode;
  private bool WeaponCursorActive=>weaponDirectMode&&commandWeapon!=Guid.Empty;
  private bool CommandExecutorReady=>commandFleet!=null&&commandFleet.CanCommand(commandExecutor);
  private void ArmSelectedUnit()
  {
   focusedMissionGroup=Guid.Empty;speedMenuOpen=false;contextOpen=false;openPolicyMenu=0;weaponDirectMode=false;commandWeapon=Guid.Empty;
   int index=contacts.FindIndex(c=>c.Key==selected);
   if(index<0)return;
   var c=contacts[index];
   if(ResolveSelectedMember(c,out var member,out _)&&commandFleet.CanCommand(member))
   {
    commandExecutor=member;executorAltitude=WorldToGrid(c.Position).y;
    executorLabel=c.Name+(MechFleetController.Roster.TryGetValue(member,out var entry)&&entry.IsLeader?"（编队）":"");
    commandWeapon=Guid.Empty;
   }
   else commandExecutor=Guid.Empty;
  }
  private bool ContactAt(Vector2 point,out Contact result)
  {
   result=default;float best=Mathf.Max(16,24*ContentZoom*TargetVisualScale(ContentZoom));bool found=false;
   foreach(var c in contacts){float d=Vector2.Distance(point,Project(c.Position));if(d<best){best=d;result=c;found=true;}}
   return found;
  }
  private void RightMapCommand(Vector2 point,bool append)
  {
   if(!InPlot(point))return;
   if(WeaponCursorActive)
   {
    contextOpen=false;
    if(!ContactAt(point,out var directTarget)){commandMessage="请选择打击目标";return;}
    if(directTarget.Own||directTarget.Affiliation==ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly||!directTarget.Observation.TrackId.IsValid||directTarget.Identity.Available&&!directTarget.Identity.CanAttackPlatform){commandMessage="目标不满足打击条件";return;}
    int selectedIndex=contacts.FindIndex(c=>c.Key==selected);
    if(selectedIndex>=0&&contacts[selectedIndex].Own)
    {
     TrackMapOperation(commandFleet.Issue(commandFleet.MemberId,ACunicombat.Sensing.Contracts.FleetOrderKind.AttackWithWeapon,directTarget.Observation,commandWeapon));return;
    }

    else if(CommandExecutorReady)TrackMapOperation(commandFleet.Issue(commandExecutor,ACunicombat.Sensing.Contracts.FleetOrderKind.AttackWithWeapon,directTarget.Observation,commandWeapon));
    else commandMessage="无该机体的指挥权限";
    return;
   }
   openPolicyMenu=0;
   if(ContactAt(point,out var target))
   {contextTarget=target;contextPoint=point;contextOpen=true;identitySubmenu=false;return;}
   contextOpen=false;
   if(!CommandExecutorReady)return;
   if(!routeDraft||routeRecipient!=commandExecutor){draftWaypoints.Clear();routeRecipient=commandExecutor;}
   if(!append)draftWaypoints.Clear();
   if(draftWaypoints.Length>=draftWaypoints.Capacity){commandMessage="路径点已满，请先提交";return;}
   var world=MapToGridPlane(point);
   // Intersect the cursor ray with the commanded unit's altitude, not the grid's Y=0 plane.
   world+=missionGridRotation*Quaternion.Euler(0,yaw,0)*new Vector3(0,executorAltitude,-executorAltitude*Mathf.Tan(tilt*Mathf.Deg2Rad));
   draftWaypoints.Add((double3)(float3)world);routeDraft=true;
   if(!append)SubmitMapRoute();
   else commandMessage=$"已标记 {draftWaypoints.Length} 个路径点，松开 Shift 提交";
  }
  private void SubmitMapRoute()
  {
   if(!routeDraft)return;
   var operation=commandFleet.RequestRoute(routeRecipient,draftWaypoints);
   TrackMapOperation(operation);
   if(operation!=Guid.Empty)
   {
    Vector3 start=transform.position;
    if(commandFleet.State.Members.TryGetValue(routeRecipient,out var report))start=(Vector3)(float3)report.Position;
    manualMapRoutes[routeRecipient]=new ManualMapRoute{Operation=operation,Start=start,Points=draftWaypoints};
   }
   routeDraft=false;draftWaypoints.Clear();
  }
  private static string GridAxis(int index,string positive,string negative)=>index>=0?positive+(index+1).ToString("000"):negative+(-index).ToString("000");
  private static string GridAxisRange(int start,int count,string positive,string negative)
   =>count==1?GridAxis(start,positive,negative):GridAxis(start,positive,negative)+"–"+GridAxis(start+count-1,positive,negative);
  private string MissionCoordinateText(Vector3 world)
  {
   var p=WorldToGrid(world)/1000;
   return $"{(p.x>=0?"E":"W")} {Mathf.Abs(p.x):0.0} / {(p.z>=0?"N":"S")} {Mathf.Abs(p.z):0.0} / {(p.y>=0?"U":"D")} {Mathf.Abs(p.y):0.0} km";
  }
  private void DrawMapInteraction()
  {
   var origin=Project(GridToWorld(Vector3.zero));
   if(InPlot(origin)){Circle(origin,5,new Color(.55f,.64f,.59f,1));Line(origin-Vector2.right*9,origin+Vector2.right*9,PrimaryText);Line(origin-Vector2.up*9,origin+Vector2.up*9,PrimaryText);if(Vector2.Distance(mapPointer,origin)<14)Label(origin.x+12,origin.y-22,"任务原点",PrimaryText,14);}
   int selectedAt=contacts.FindIndex(c=>c.Key==selected);if(selectedAt>=0&&InPlot(mapPointer)&&!contextOpen)Line(Project(contacts[selectedAt].Position),mapPointer,PrimaryText,1);
   DrawSelectedWeaponRanges();
   DrawPlannedRoutes();
   DrawAttackLinks();
   if(InPlot(mapPointer)&&!contextOpen)
   {
    var world=WorldToGrid(MapToGridPlane(mapPointer));float step=DisplayGridSpacingMeters;
    int col=Mathf.FloorToInt(world.x/step),row=Mathf.FloorToInt(world.z/step),count=Mathf.RoundToInt(step/WorldGridSpacingMeters);
    string address=GridAxisRange(col*count,count,"E","W")+" / "+GridAxisRange(row*count,count,"N","S")+$" · {step/1000:0} km";
    Vector2 corner=new Vector2(float.PositiveInfinity,float.PositiveInfinity);
    for(int dx=0;dx<=1;dx++)for(int dz=0;dz<=1;dz++)
    {var p=Project(GridToWorld(new Vector3((col+dx)*step,0,(row+dz)*step)));corner=Vector2.Min(corner,p);}
    var bounds=new Rect(corner.x+7,corner.y+5,350,25);
    Fill(bounds,new Color(.02f,.027f,.027f,.97f));BoundedLabel(bounds,address,PrimaryText,15);
    if(WeaponCursorActive)
    {
     var tint=PrimaryText;
     Line(mapPointer+Vector2.left*10,mapPointer+Vector2.left*4,tint);
     Line(mapPointer+Vector2.right*4,mapPointer+Vector2.right*10,tint);
     Line(mapPointer+Vector2.up*4,mapPointer+Vector2.up*10,tint);
     Line(mapPointer+Vector2.down*4,mapPointer+Vector2.down*10,tint);
    }
   }
   for(int i=0;i<draftWaypoints.Length;i++)
   {
    var p=Project((Vector3)(float3)draftWaypoints[i]);
    if(i>0)DrawRouteSegment(Project((Vector3)(float3)draftWaypoints[i-1]),p);
    else if(commandFleet!=null&&commandFleet.State.Members.TryGetValue(routeRecipient,out var draftOwner))DrawRouteSegment(Project((Vector3)(float3)draftOwner.Position),p);
    DrawRouteEndpoint(p);Label(p.x+8,p.y,(i+1).ToString(),RouteBlue,14);
   }
   if(!contextOpen)return;
   var current=contacts.FindIndex(c=>c.Key==contextTarget.Key);if(current<0){contextOpen=false;return;}contextTarget=contacts[current];
   bool friendly=contextTarget.Own||contextTarget.Affiliation==ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly;
   string[] captions=friendly?new[]{"加入编队","退出编队","支援","协助指令"}:new[]{"侦查","打击","扫描","锁定"};
   int extra=contextTarget.Own?68:96+(identitySubmenu?102:0);
   contextBounds=new Rect(Mathf.Clamp(contextPoint.x,8,layoutWidth-208),Mathf.Clamp(contextPoint.y,8,FooterRect.y-captions.Length*34-extra-44),200,captions.Length*34+extra+40);
   if(contextBounds.Contains(mapPointer))hoveredHelp=null;
   MenuPanel(contextBounds);
   MenuLabel(new Rect(contextBounds.x+1,contextBounds.y+1,198,31),executorLabel+" → "+contextTarget.Name,new Color(.63f,.69f,.65f,1),13);
   Line(new Vector2(contextBounds.x+12,contextBounds.y+32),new Vector2(contextBounds.xMax-12,contextBounds.y+32),new Color(.20f,.28f,.24f,1));
   for(int i=0;i<captions.Length;i++)
   {
    if(!friendly&&i==3)
    {
     MenuItem(new Rect(contextBounds.x+1,contextBounds.y+36+i*34,198,34),"锁定",()=>{
      contextOpen=false;lastMapOperation=Guid.Empty;
      if(!radar.RadarPowerEnabled){commandMessage="无法锁定：本机雷达已关闭";return;}
      radar.SetTrackOperationMode(true);
      bool designated=radar.TryAssignFireControlTrack(contextTarget.Observation.TrackId);
      commandMessage=!designated?"无法指定：航迹状态或雷达视场不满足条件":GetComponent<MechTestWeaponSystem>()?.CanReleaseMissileAt(contextTarget.Observation.TrackId,out _)==true?"已指定目标，导弹可发射":"已指定目标，尚未满足导弹发射条件";
     },radar!=null&&contextTarget.Observation.TrackId.IsValid);
     continue;
    }
    bool attack=!friendly&&i==1&&CommandExecutorReady&&!contextTarget.Missile&&contextTarget.Observation.TrackId.IsValid&&(!contextTarget.Identity.Available||contextTarget.Identity.CanAttackPlatform);
    MenuItem(new Rect(contextBounds.x+1,contextBounds.y+36+i*34,198,34),captions[i],()=>{
     contextOpen=false;
     TrackMapOperation(commandFleet.Issue(commandExecutor,ACunicombat.Sensing.Contracts.FleetOrderKind.Attack,contextTarget.Observation));
    },attack);
   }
   var identity=GetComponent<MechIdentityController>();
   float iy=contextBounds.y+36+captions.Length*34;
   if(contextTarget.Own&&identity!=null)
   {
    MenuItem(new Rect(contextBounds.x+1,iy,198,34),identity.InterrogatorEnabled?"IFF 问询器：开启":"IFF 问询器：关闭",()=>identity.InterrogatorEnabled=!identity.InterrogatorEnabled);
    MenuItem(new Rect(contextBounds.x+1,iy+34,198,34),identity.ResponderEnabled&&identity.ReplyPolicy!=IffReplyPolicy.Silent&&identity.ReplyPolicy!=IffReplyPolicy.Off?"IFF 应答：自动":"IFF 应答：静默",()=>{bool silent=identity.ReplyPolicy==IffReplyPolicy.Silent||identity.ReplyPolicy==IffReplyPolicy.Off||!identity.ResponderEnabled;if(silent){identity.ResponderEnabled=true;identity.ReplyPolicy=previousReplyPolicy;}else{previousReplyPolicy=identity.ReplyPolicy;identity.ReplyPolicy=IffReplyPolicy.Silent;}});
   }
   else
   {
    bool available=identity!=null&&radar!=null&&!contextTarget.Missile&&contextTarget.Observation.TrackId.IsValid;
    string key=contextTarget.Key;
    MenuItem(new Rect(contextBounds.x+1,iy,198,34),"IFF 问询",()=>{bool accepted=identity.RequestInterrogation(radar.Reference(contextTarget.Observation.TrackId),out var feedback);iffMenuStatus[key]=feedback;if(accepted)iffMenuFollowing.Add(key);else iffMenuFollowing.Remove(key);},available);
    string status=iffMenuStatus.TryGetValue(key,out var saved)?saved:"";
    if((!iffMenuStatus.ContainsKey(key)||iffMenuFollowing.Contains(key))&&identity!=null&&identity.TryGetIdentity(contextTarget.Observation.TrackId,out var state)&&state.Iff!=ACunicombat.Sensing.Contracts.IffOutcome.None)status=IffText(state.Iff);
    BoundedLabel(new Rect(contextBounds.x+12,iy+34,176,26),status,UnknownColor,11);
    MenuItem(new Rect(contextBounds.x+1,iy+62,198,34),identitySubmenu?"标记 ▾":"标记 ▸",()=>identitySubmenu=!identitySubmenu,available);
    if(identitySubmenu)
    {
     var relations=new[]{ACunicombat.Sensing.Contracts.IdentityRelation.Neutral,ACunicombat.Sensing.Contracts.IdentityRelation.Friendly,ACunicombat.Sensing.Contracts.IdentityRelation.Hostile};
     string[] names={"中立","友军","敌军"};
     for(int i=0;i<3;i++){var relation=relations[i];MenuItem(new Rect(contextBounds.x+13,iy+96+i*34,186,34),names[i],()=>{identity.MarkContact(radar.Reference(contextTarget.Observation.TrackId),relation);identitySubmenu=false;},available,contextTarget.Identity.Available&&contextTarget.Identity.Relation==relation);}
    }
   }
  }
  private void DrawPlannedRoutes()
  {
   foreach(var entry in manualMapRoutes)
   {
    var route=entry.Value;
    if(commandFleet!=null&&commandFleet.Results.TryGetValue(route.Operation,out var result)&&
       (result.Phase==ACunicombat.Sensing.Contracts.FleetTaskPhase.Rejected||result.Phase==ACunicombat.Sensing.Contracts.FleetTaskPhase.Cancelled||result.Reason==ACunicombat.Sensing.Contracts.FleetTaskReason.Cancelled))continue;
    // Use the same current observed position as the map symbol, not the
    // position captured when the route was issued.
    bool located=false;
    foreach(var observed in contacts)
    {
     if(!ResolveSelectedMember(observed,out var observedMember,out _)||observedMember!=entry.Key)continue;
     route.Start=observed.Position;located=true;break;
    }
    if(!located&&commandFleet!=null&&commandFleet.State.Members.TryGetValue(entry.Key,out var report)&&commandFleet.State.Fresh(report,radar.SimTimeSeconds))
     route.Start=(Vector3)(float3)(report.Position+report.Velocity*Math.Max(0,radar.SimTimeSeconds-report.ObservedAt));
    var previous=Project(route.Start);
    foreach(var point in route.Points){var next=Project((Vector3)(float3)point);DrawRouteSegment(previous,next);DrawRouteEndpoint(next);previous=next;}
   }
   int at=contacts.FindIndex(c=>c.Key==selected);if(at<0)return;
   var contact=contacts[at];
   if(!ResolveSelectedMember(contact,out var member,out var state)||!state.HasNavigationGoal||!commandFleet.State.Fresh(state,radar.SimTimeSeconds))return;
   // The persistent manual polyline already represents this route.
   if(state.Order==ACunicombat.Sensing.Contracts.FleetOrderKind.Move&&manualMapRoutes.ContainsKey(member)&&
      state.Phase!=ACunicombat.Sensing.Contracts.FleetTaskPhase.Returning)return;
   var goal=(Vector3)(float3)state.NavigationGoal;
   if(!float.IsFinite(goal.x)||!float.IsFinite(goal.y)||!float.IsFinite(goal.z))return;
   DrawRouteSegment(Project(contact.Position),Project(goal));DrawRouteEndpoint(Project(goal));
  }
  private void DrawRouteEndpoint(Vector2 point)
  {if(InPlot(point))Fill(new Rect(point.x-5,point.y-5,10,10),RouteRed);}
  private void DrawRouteSegment(Vector2 from,Vector2 to)
  {
   // Split at viewport and floating-panel boundaries to avoid drawing over controls.
   float t0=0,t1=1;var delta=to-from;
   if(!ClipRoute(-delta.x,from.x,ref t0,ref t1)||!ClipRoute(delta.x,layoutWidth-from.x,ref t0,ref t1)||
      !ClipRoute(-delta.y,from.y,ref t0,ref t1)||!ClipRoute(delta.y,H-from.y,ref t0,ref t1))return;
   var a=from+delta*t0;var b=from+delta*t1;
   var cuts=new System.Collections.Generic.List<float>{0,1};
   foreach(var panel in new[]{FooterRect,FormationPanelRect})
   {
    if(Mathf.Abs(b.x-a.x)>.001f){cuts.Add(Mathf.Clamp01((panel.xMin-a.x)/(b.x-a.x)));cuts.Add(Mathf.Clamp01((panel.xMax-a.x)/(b.x-a.x)));}
    if(Mathf.Abs(b.y-a.y)>.001f){cuts.Add(Mathf.Clamp01((panel.yMin-a.y)/(b.y-a.y)));cuts.Add(Mathf.Clamp01((panel.yMax-a.y)/(b.y-a.y)));}
   }
   cuts.Sort();
   for(int i=1;i<cuts.Count;i++)if(cuts[i]-cuts[i-1]>.00001f&&InPlot(Vector2.Lerp(a,b,(cuts[i]+cuts[i-1])*.5f)))
    Line(Vector2.Lerp(a,b,cuts[i-1]),Vector2.Lerp(a,b,cuts[i]),RouteBlue,1.5f);
  }
  private static bool ClipRoute(float p,float q,ref float start,ref float end)
  {
   if(Mathf.Abs(p)<.00001f)return q>=0;
   float t=q/p;if(p<0){if(t>end)return false;start=Mathf.Max(start,t);}else{if(t<start)return false;end=Mathf.Min(end,t);}return true;
  }
  private void DrawSelectedWeaponRanges()
  {
   int at=contacts.FindIndex(c=>c.Key==selected);if(at<0)return;
   var owner=contacts[at];if(!owner.Own&&owner.Affiliation!=ACunicombat.Sensing.Contracts.TrackAffiliation.Friendly)return;
   float outer=0;foreach(var w in commandWeapons)if(commandWeapon==Guid.Empty||w.Instance==commandWeapon)outer=Mathf.Max(outer,w.MaximumRange);
   if(outer>0)DrawWorldRange(owner.Position,outer,new Color(.655f,.216f,.196f,.9f));

   DrawAttackPreview(owner);
  }

  private void DrawWorldRange(Vector3 center,float radius,Color color)
  {
   Vector2 previous=Project(center+Vector3.right*radius);
   for(int i=1;i<=128;i++)
   {
    float angle=i*Mathf.PI*2/128;
    Vector2 point=Project(center+new Vector3(Mathf.Cos(angle)*radius,0,Mathf.Sin(angle)*radius));
    // Keep range lines out of the floating controls.
    if(InPlot(previous)&&InPlot(point))Line(previous,point,color,1.2f);
    previous=point;
   }
  }
 }
}
