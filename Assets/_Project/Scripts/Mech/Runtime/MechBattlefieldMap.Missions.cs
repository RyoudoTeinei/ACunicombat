using System;
using System.Collections.Generic;
using System.Linq;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
namespace ACunicombat.Mechs
{
 public sealed partial class MechBattlefieldMap
 {
  private static readonly FleetTaskGroup[] MissionChoices={FleetTaskGroup.Strike,FleetTaskGroup.Escort,FleetTaskGroup.AirSuperiority,FleetTaskGroup.AirDefenseSuppression,FleetTaskGroup.ElectronicWarfare,FleetTaskGroup.EarlyWarningCommand,FleetTaskGroup.Reconnaissance,FleetTaskGroup.Logistics};
  private enum MissionEditStage{None,Target,Area,Point,Direction}
  private MissionEditStage missionEditStage;
  private Guid missionEditRoot,focusedMissionGroup;
  private FleetPacket missionDraft;
  private readonly HashSet<Vector2Int> missionCells=new();
  private readonly Dictionary<Guid,FleetPacket> displayedMissions=new();
  private bool missionBoxDragging,missionBoxRemove;
  private Vector2 missionBoxStart,missionBoxEnd;
  private Rect MissionToolbarRect=>new Rect((layoutWidth-560)/2,66,560,78);
  private void BeginMissionAssignment(FleetTaskGroup mode)
  {
   if(FormationBusy||!commandFleet.CanEditFormation(editedFormation)||!MechFleetController.MissionAvailable(mode))return;
   if(!TryFormationContact(editedFormation,out var leader)){formationEditMessage="长机位置未报告，无法指派";return;}
   missionEditRoot=editedFormation;focusedMissionGroup=editedFormation;formationMenu=FormationMenu.None;contextOpen=false;openPolicyMenu=0;weaponDirectMode=false;
   missionDraft=new FleetPacket{TaskGroup=mode,MissionGridOrigin=(double3)(float3)missionGridOrigin,MissionGridRotation=new quaternion(missionGridRotation.x,missionGridRotation.y,missionGridRotation.z,missionGridRotation.w),MissionCellSize=WorldGridSpacingMeters,MissionPoint=(double3)(float3)leader.Position};
   if(ResolveSelectedMember(leader,out _,out var report)&&report.HasNavigationGoal)missionDraft.MissionPoint=report.NavigationGoal;
   if(manualMapRoutes.TryGetValue(editedFormation,out var route)&&route.Points.Length>0)missionDraft.MissionPoint=route.Points[route.Points.Length-1];
   missionCells.Clear();missionBoxDragging=false;
   missionEditStage=mode==FleetTaskGroup.AirSuperiority||mode==FleetTaskGroup.Reconnaissance?MissionEditStage.Area:MissionEditStage.Target;
   if(missionEditStage==MissionEditStage.Area&&displayedMissions.TryGetValue(editedFormation,out var prior)&&prior.TaskGroup==mode&&prior.MissionGeometry==FleetMissionGeometry.Area)
   {missionDraft=prior;foreach(var sector in prior.MissionSectors)for(int x=sector.x;x<=sector.z;x++)for(int z=sector.y;z<=sector.w;z++)missionCells.Add(new Vector2Int(x,z));}
   formationEditMessage=mode==FleetTaskGroup.AirDefenseSuppression?"选择辐射目标；AI 使用已装填的反辐射弹药并协同拦截":"指派中";
  }
  private void CancelMissionAssignment(){missionEditStage=MissionEditStage.None;missionBoxDragging=false;missionCells.Clear();Cursor.visible=true;}
  private bool HandleMissionPointer(Vector2 point,Mouse mouse)
  {
   if(missionEditStage==MissionEditStage.None)return false;
   mapPointer=point;
   Cursor.visible=missionEditStage==MissionEditStage.Area||!InPlot(point);
   if(MissionToolbarRect.Contains(point)||TimeControlRect.Contains(point))return false;
   if(!missionBoxDragging&&InPlot(point)&&mouse.scroll.ReadValue().y!=0)ZoomAt(mouse.scroll.ReadValue().y,point);
   if(missionEditStage==MissionEditStage.Target&&mouse.leftButton.wasPressedThisFrame)
   {
    var groupHit=formationHits.LastOrDefault(h=>h.Bounds.Contains(point)&&h.Header&&h.Root!=Guid.Empty);
    if(groupHit!=null&&missionDraft.TaskGroup==FleetTaskGroup.Escort&&TryFormationContact(groupHit.Root,out var groupLeader)){AssignMissionTarget(groupLeader,groupHit.Root,true);return true;}
    if(InPlot(point)&&ContactAt(point,out var target))AssignMissionTarget(target,Guid.Empty,false);
   }
   if(missionEditStage==MissionEditStage.Area)
   {
    if(InPlot(point)&&(mouse.leftButton.wasPressedThisFrame||mouse.rightButton.wasPressedThisFrame))
    {missionBoxDragging=true;missionBoxRemove=mouse.rightButton.wasPressedThisFrame;missionBoxStart=missionBoxEnd=point;}
    if(missionBoxDragging)missionBoxEnd=point;
    if(missionBoxDragging&&(mouse.leftButton.wasReleasedThisFrame||mouse.rightButton.wasReleasedThisFrame))
    {ApplyMissionBox();missionBoxDragging=false;}
   }
   else if(InPlot(point)&&mouse.leftButton.wasPressedThisFrame)
   {
    if(missionEditStage==MissionEditStage.Point)
    {var world=MapToGridPlane(point);float height=WorldToGrid((Vector3)(float3)missionDraft.MissionPoint).y;world+=missionGridRotation*Quaternion.Euler(0,yaw,0)*new Vector3(0,height,-height*Mathf.Tan(tilt*Mathf.Deg2Rad));missionDraft.MissionPoint=(double3)(float3)world;missionEditStage=MissionEditStage.Direction;}
    else if(missionEditStage==MissionEditStage.Direction)
    {
     var origin=(Vector3)(float3)missionDraft.MissionPoint;var destination=MapToGridPlane(point);float height=WorldToGrid(origin).y;destination+=missionGridRotation*Quaternion.Euler(0,yaw,0)*new Vector3(0,height,-height*Mathf.Tan(tilt*Mathf.Deg2Rad));
     var direction=destination-origin;if(direction.sqrMagnitude>1){missionDraft.MissionDirection=(float3)direction.normalized;missionDraft.MissionGeometry=FleetMissionGeometry.PointDirection;SubmitMissionAssignment();}
    }
   }
   return true;
  }
  private void AssignMissionTarget(Contact target,Guid member,bool formation)
  {
   bool friendly=target.Own||target.Affiliation==TrackAffiliation.Friendly;
   if(missionDraft.TaskGroup==FleetTaskGroup.Escort)
   {
    if(!friendly||(!formation&&!ResolveSelectedMember(target,out member,out _))){formationEditMessage="护航需要已知友方目标或编队";return;}
    if(member==missionEditRoot||MechFleetController.FormationMembers(missionEditRoot).Any(m=>m.Member==member)){formationEditMessage="请选择其他单位或编队作为护航对象";return;}
   }
   else if(friendly||target.Missile||!target.Observation.TrackId.IsValid){formationEditMessage="请选择有效的非友方机体目标";return;}
   if(missionDraft.TaskGroup==FleetTaskGroup.Strike&&target.Identity.Available&&!target.Identity.CanAttackPlatform){formationEditMessage="目标当前身份不允许打击";return;}
   missionDraft.Target=target.Observation;missionDraft.MissionProtectedMember=member;missionDraft.MissionProtectFormation=formation;missionDraft.MissionPoint=(double3)(float3)target.Position;missionDraft.MissionGeometry=FleetMissionGeometry.Target;SubmitMissionAssignment();
  }
  private void SubmitMissionAssignment()
  {
   formationEditsAreMission=true;formationEditOperations.Clear();formationEditOperations.AddRange(commandFleet.RequestFormationMission(missionEditRoot,missionDraft));
   if(formationEditOperations.Count==0){formationEditMessage="任务未提交：权限或任务资料不足";return;}
   displayedMissions[missionEditRoot]=missionDraft;expandedMapFormations.Add(missionEditRoot);CancelMissionAssignment();
  }
  private void SubmitMissionArea()
  {
   missionDraft.MissionSectors.Clear();
   if(missionCells.Count==0){if(missionDraft.TaskGroup==FleetTaskGroup.Reconnaissance){formationEditMessage="请选择区域，或切换点位＋方向";return;}missionDraft.MissionGeometry=FleetMissionGeometry.Point;SubmitMissionAssignment();return;}
   var rectangles=new List<int4>();
   foreach(var row in missionCells.GroupBy(c=>c.y).OrderBy(g=>g.Key))
   {
    var xs=row.Select(c=>c.x).OrderBy(x=>x).ToArray();int start=xs[0],end=start;
    for(int i=1;i<=xs.Length;i++)
    {
     if(i<xs.Length&&xs[i]==end+1){end=xs[i];continue;}
     int previous=rectangles.FindIndex(r=>r.x==start&&r.z==end&&r.w==row.Key-1);
     if(previous>=0){var merged=rectangles[previous];merged.w=row.Key;rectangles[previous]=merged;}else rectangles.Add(new int4(start,row.Key,end,row.Key));
     if(i<xs.Length)start=end=xs[i];
    }
   }
   if(rectangles.Count>missionDraft.MissionSectors.Capacity){formationEditMessage="区域过于零碎，请减少孤立网格后再提交";return;}
   foreach(var rect in rectangles)missionDraft.MissionSectors.Add(rect);
   missionDraft.MissionGeometry=FleetMissionGeometry.Area;SubmitMissionAssignment();
  }
  private void ApplyMissionBox()
  {
   float step=DisplayGridSpacingMeters;int factor=Mathf.RoundToInt(step/WorldGridSpacingMeters);
   var rect=Rect.MinMaxRect(Mathf.Min(missionBoxStart.x,missionBoxEnd.x),Mathf.Min(missionBoxStart.y,missionBoxEnd.y),Mathf.Max(missionBoxStart.x,missionBoxEnd.x),Mathf.Max(missionBoxStart.y,missionBoxEnd.y));
   var corners=new[]{new Vector2(rect.xMin,rect.yMin),new Vector2(rect.xMax,rect.yMin),new Vector2(rect.xMax,rect.yMax),new Vector2(rect.xMin,rect.yMax)};
   var local=corners.Select(c=>WorldToGrid(MapToGridPlane(c))).ToArray();int minX=Mathf.FloorToInt(local.Min(c=>c.x)/step),maxX=Mathf.FloorToInt(local.Max(c=>c.x)/step),minZ=Mathf.FloorToInt(local.Min(c=>c.z)/step),maxZ=Mathf.FloorToInt(local.Max(c=>c.z)/step);
   if((long)(maxX-minX+1)*(maxZ-minZ+1)>4096){formationEditMessage="框选过大，请分段选择";return;}
   for(int x=minX;x<=maxX;x++)for(int z=minZ;z<=maxZ;z++)
   {
    var quad=MissionCellQuad(x,z,step);if(rect.width>3||rect.height>3){if(!QuadTouchesRect(quad,rect))continue;}
    for(int dx=0;dx<factor;dx++)for(int dz=0;dz<factor;dz++){var cell=new Vector2Int(x*factor+dx,z*factor+dz);if(missionBoxRemove)missionCells.Remove(cell);else missionCells.Add(cell);}
   }
  }
  private Vector2[] MissionCellQuad(int x,int z,float step)=>new[]{Project(GridToWorld(new Vector3(x*step,0,z*step))),Project(GridToWorld(new Vector3((x+1)*step,0,z*step))),Project(GridToWorld(new Vector3((x+1)*step,0,(z+1)*step))),Project(GridToWorld(new Vector3(x*step,0,(z+1)*step)))};
  private static float Cross2(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
  private static bool QuadTouchesRect(Vector2[] quad,Rect rect)
  {
   if(quad.Any(p=>rect.Contains(p)))return true;
   var corners=new[]{new Vector2(rect.xMin,rect.yMin),new Vector2(rect.xMax,rect.yMin),new Vector2(rect.xMax,rect.yMax),new Vector2(rect.xMin,rect.yMax)};
   foreach(var p in corners){bool positive=false,negative=false;for(int i=0;i<4;i++){float cross=Cross2(quad[(i+1)%4]-quad[i],p-quad[i]);positive|=cross>0;negative|=cross<0;}if(!(positive&&negative))return true;}
   for(int i=0;i<4;i++)for(int j=0;j<4;j++){var a=quad[i];var b=quad[(i+1)%4];var c=corners[j];var d=corners[(j+1)%4];float denominator=Cross2(b-a,d-c);if(Mathf.Abs(denominator)<.001f)continue;float t=Cross2(c-a,d-c)/denominator,u=Cross2(c-a,b-a)/denominator;if(t>=0&&t<=1&&u>=0&&u<=1)return true;}
   return false;
  }
  private Guid SelectedMissionFormation()
  {
   if(missionEditStage!=MissionEditStage.None)return missionEditRoot;
   if(focusedMissionGroup!=Guid.Empty)return focusedMissionGroup;
   int i=contacts.FindIndex(c=>c.Key==selected);if(i>=0&&ResolveSelectedMember(contacts[i],out var member,out _)&&MechFleetController.Roster.TryGetValue(member,out var entry))return MechFleetController.GroupRoot(entry);
   return Guid.Empty;
  }
  private void DrawMissionRegionsWorld()
  {
   var root=SelectedMissionFormation();
   if(missionEditStage==MissionEditStage.Area)
   {
    int factor=Mathf.RoundToInt(DisplayGridSpacingMeters/WorldGridSpacingMeters);
    var visible=missionCells.GroupBy(c=>new Vector2Int(Mathf.FloorToInt(c.x/(float)factor),Mathf.FloorToInt(c.y/(float)factor)));
    foreach(var cell in visible){if(cell.Count()==factor*factor)DrawMissionQuad(MissionCellQuad(cell.Key.x,cell.Key.y,DisplayGridSpacingMeters));else foreach(var partial in cell)DrawMissionQuad(MissionCellQuad(partial.x,partial.y,WorldGridSpacingMeters));}
   }
   else if(root!=Guid.Empty&&displayedMissions.TryGetValue(root,out var p)&&p.MissionGeometry==FleetMissionGeometry.Area)
   {
    foreach(var sector in p.MissionSectors)
    {
     var local=new[]{new float3(sector.x*p.MissionCellSize,0,sector.y*p.MissionCellSize),new float3((sector.z+1)*p.MissionCellSize,0,sector.y*p.MissionCellSize),new float3((sector.z+1)*p.MissionCellSize,0,(sector.w+1)*p.MissionCellSize),new float3(sector.x*p.MissionCellSize,0,(sector.w+1)*p.MissionCellSize)};
     DrawMissionQuad(local.Select(v=>Project((Vector3)(float3)(p.MissionGridOrigin+(double3)math.mul(p.MissionGridRotation,v)))).ToArray());
    }
   }
  }
  private void DrawMissionQuad(Vector2[] points){for(int i=0;i<4;i++)Line(ScreenToContent(points[i]),ScreenToContent(points[(i+1)%4]),new Color(.25f,.75f,.93f,.9f),2/ContentZoom);}
  private void DrawMissionAssignmentOverlay()
  {
   if(missionEditStage==MissionEditStage.None)return;
   var bar=MissionToolbarRect;MenuPanel(bar);
   string instruction=missionEditStage switch{MissionEditStage.Target=>"左键选择目标／编队",MissionEditStage.Point=>"左键选择侦察位置",MissionEditStage.Direction=>"左键指定探测方向",_=>"左键点选或框选增加网格，右键减少"};
   MenuLabel(new Rect(bar.x,bar.y,bar.width,31),TaskGroupName(missionDraft.TaskGroup)+" · "+instruction,PrimaryText,13);
   MapButton(new Rect(bar.xMax-83,bar.y+38,72,30),"取消",CancelMissionAssignment);
   if(missionEditStage==MissionEditStage.Area)
   {
    MapButton(new Rect(bar.x+10,bar.y+38,106,30),"完成区域",SubmitMissionArea);
    MapButton(new Rect(bar.x+124,bar.y+38,80,30),"清空",()=>missionCells.Clear());
    if(missionDraft.TaskGroup==FleetTaskGroup.Reconnaissance)MapButton(new Rect(bar.x+212,bar.y+38,125,30),"点位＋方向",()=>{missionEditStage=MissionEditStage.Point;missionCells.Clear();});
    else MapButton(new Rect(bar.x+212,bar.y+38,155,30),"航迹点附近制空",()=>{missionCells.Clear();SubmitMissionArea();});
   }
   if(missionEditStage!=MissionEditStage.Area&&InPlot(mapPointer))
   {
    Vector2 start=missionEditStage==MissionEditStage.Direction?Project((Vector3)(float3)missionDraft.MissionPoint):TryFormationContact(missionEditRoot,out var leader)?Project(leader.Position):mapPointer;
    DrawRouteSegment(start,mapPointer);Line(mapPointer+Vector2.left*9,mapPointer+Vector2.right*9,PrimaryText);Line(mapPointer+Vector2.up*9,mapPointer+Vector2.down*9,PrimaryText);
   }
   if(missionBoxDragging)Box(Mathf.Min(missionBoxStart.x,missionBoxEnd.x),Mathf.Min(missionBoxStart.y,missionBoxEnd.y),Mathf.Abs(missionBoxEnd.x-missionBoxStart.x),Mathf.Abs(missionBoxEnd.y-missionBoxStart.y),missionBoxRemove?RouteRed:RouteBlue,1.5f);
  }
 }
}
