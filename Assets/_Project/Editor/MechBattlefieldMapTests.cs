using System;using System.Reflection;using ACunicombat.Mechs;using ACunicombat.Simulation.Driver;using NUnit.Framework;using UnityEngine;
public sealed class MechBattlefieldMapTests
{
 const BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
 GameObject root,other;
 [SetUp] public void Setup(){root=new GameObject("Map contract");root.SetActive(false);}
 [TearDown] public void Cleanup(){SetVisible(false);UnityEngine.Object.DestroyImmediate(root);if(other!=null)UnityEngine.Object.DestroyImmediate(other);}
 static void SetVisible(bool value)=>typeof(MechSolarSystemMap).GetMethod("SetBattlefieldVisible",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{value});
 [Test] public void AiInputContinuesWhileHumanMapIsOpen()
 {
  var input=root.AddComponent<MechPlayerInputAdapter>();var frame=new MechPlayerInputFrame{MoveLocal=new Vector2(.4f,.8f),RiseHeld=true};
  input.SubmitExternalFrame(in frame);SetVisible(true);var sampled=input.Sample();
  Assert.That(sampled.MoveLocal,Is.EqualTo(frame.MoveLocal));Assert.That(sampled.RiseHeld,Is.True);
 }
 [Test] public void HumanFlightInputIsBlockedByMap()
 {
  var input=root.AddComponent<MechPlayerInputAdapter>();input.SetExternalControl(false);SetVisible(true);
  Assert.That(input.Sample().MoveLocal,Is.EqualTo(Vector2.zero));
 }
 [Test] public void InitialFormationIsWithin150KmAndNonCoplanar()
 {
  var a=MechBattlefieldScenario.FriendlyA;var b=MechBattlefieldScenario.FriendlyB;var c=MechBattlefieldScenario.EnemyA;var d=MechBattlefieldScenario.EnemyB;
  foreach(var p in new[]{a,b,c,d}){Assert.That(p.magnitude,Is.LessThanOrEqualTo(150000));Assert.That(Mathf.Abs(p.y),Is.GreaterThan(1000));}
  Assert.That(Mathf.Abs(Vector3.Dot(Vector3.Cross(b-a,c-a),d-a)),Is.GreaterThan(1e9f));
 }
 [Test] public void ProjectionExposesAltitudeInsteadOfFlatteningContacts()
 {
  var map=root.AddComponent<MechBattlefieldMap>();typeof(MechBattlefieldMap).GetField("tilt",Private).SetValue(map,25f);var p=new Vector3(10000,20000,30000);
  Assert.That(Vector2.Distance(map.Project(p),map.Project(p,true)),Is.GreaterThan(10));
 }
 [Test] public void InactiveIdentityIsDeferredWithoutPartiallyRegisteringTarget()
 {
  var radar=root.AddComponent<MechRadarController>();other=new GameObject("Not enabled yet");
  var target=other.AddComponent<CombatTestTarget>();BattlefieldEntityIdentity.GetOrAdd(other).enabled=false;
  Assert.DoesNotThrow(()=>typeof(MechRadarController).GetMethod("AddSceneTarget",Private).Invoke(radar,new object[]{target}));
  Assert.That((int)typeof(MechRadarController).GetField("sceneTargetCount",Private).GetValue(radar),Is.Zero);
 }
 [Test] public void AiRigCannotAttachHumanMap()
 {
  var rig=root.AddComponent<MechPlayerRig>();rig.ConfigureAiControl();
  var attached=typeof(MechSolarSystemMap).GetMethod("EnsureAttached",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{rig});
  Assert.That(attached,Is.EqualTo(false));Assert.That(root.GetComponent<MechBattlefieldMap>(),Is.Null);
 }
 [Test] public void ExistingSceneEnemyDoesNotAppearWithoutAnObservation()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var link=root.AddComponent<MechCooperativeTelemetry>();
  typeof(MechBattlefieldMap).GetField("cooperative",Private).SetValue(map,link);
  other=new GameObject("Unobserved real enemy");other.transform.position=Vector3.forward*50000;other.AddComponent<CombatTestTarget>().ConfigureIdentity("ENEMY",2);
  typeof(MechBattlefieldMap).GetMethod("Gather",Private).Invoke(map,null);
  Assert.That(map.Contacts.Count,Is.EqualTo(1));Assert.That(map.Contacts[0].Own,Is.True);
 }

 [TestCase(1920,1080)] [TestCase(2560,1440)] [TestCase(1760,1000)] [TestCase(1200,1000)]
 public void NativeViewportFillsWindowWithoutLetterboxing(int w,int h)
 {
  var v=MechBattlefieldMap.FitViewport(w,h);
  Assert.That(v.width,Is.EqualTo(w));Assert.That(v.height,Is.EqualTo(h));Assert.That(v.xMin,Is.GreaterThanOrEqualTo(0));Assert.That(v.xMax,Is.LessThanOrEqualTo(w));
  Assert.That(v.yMin,Is.GreaterThanOrEqualTo(0));Assert.That(v.yMax,Is.LessThanOrEqualTo(h));
  Assert.That(v.width,Is.EqualTo(Mathf.Round(v.width)));Assert.That(v.height,Is.EqualTo(Mathf.Round(v.height)));
  var centre=MechBattlefieldMap.ScreenToLayout(v.center,v);Assert.That(centre.x,Is.EqualTo(500f*w/h).Within(.01));Assert.That(centre.y,Is.EqualTo(500).Within(.01));
 }
 [Test] public void NormalizedAndLegacyWheelNotchesHaveSameVisibleEffect()
 {
  float a=MechBattlefieldMap.ZoomRange(150,1),b=MechBattlefieldMap.ZoomRange(150,120);
  Assert.That(a,Is.EqualTo(b).Within(.001));Assert.That(a,Is.LessThan(135));
  Assert.That(MechBattlefieldMap.ZoomRange(a,-1),Is.EqualTo(150).Within(.001));
 }
 [Test] public void ZoomKeepsPointUnderCursorAndResetRestoresView()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var world=new Vector3(30000,10000,40000);var anchor=map.Project(world);
  map.ZoomAt(2,anchor);Assert.That(map.RangeKm,Is.LessThan(map.BaseRangeKm));Assert.That(Vector2.Distance(anchor,map.Project(world)),Is.LessThan(.001));
  var other=map.Project(world+Vector3.right*10000);Assert.That(Vector2.Distance(anchor,other),Is.GreaterThan(30));
  map.ResetView();Assert.That(map.RangeKm,Is.EqualTo(map.BaseRangeKm));Assert.That(Vector2.Distance(anchor,map.Project(world)),Is.LessThan(.001));
 }
 [Test] public void InfoPanelWheelDoesNotZoomBattlefield()
 {
  var map=root.AddComponent<MechBattlefieldMap>();map.ZoomAt(1,map.FooterRect.center);Assert.That(map.RangeKm,Is.EqualTo(map.BaseRangeKm));
 }
 [Test] public void ExtremeWheelInputRemainsBounded()
 {
  Assert.That(MechBattlefieldMap.ZoomRange(5,10000),Is.EqualTo(5));Assert.That(MechBattlefieldMap.ZoomRange(2000,-10000),Is.EqualTo(2000));
  Assert.That(MechBattlefieldMap.ZoomRange(150,float.NaN),Is.EqualTo(150));
 }

 [Test] public void OldShowcaseOverlayDoesNotDrawOverMap()
 {
  var showcase=root.AddComponent<AC6CombatShowcaseController>();SetVisible(true);
  Assert.DoesNotThrow(()=>typeof(AC6CombatShowcaseController).GetMethod("OnGUI",Private).Invoke(showcase,null));
 }

 [Test] public void LeftDragPansWithoutBecomingClick()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var world=new Vector3(10000,10000,20000);var original=map.Project(world);
  map.BeginPan(new Vector2(500,400));map.MovePan(new Vector2(620,465));
  Assert.That(Vector2.Distance(map.Project(world),original+new Vector2(120,65)),Is.LessThan(.001));
  Assert.That(map.EndPan(new Vector2(620,465)),Is.False);
  map.ResetView();Assert.That(Vector2.Distance(map.Project(world),original),Is.LessThan(.001));
 }
 [Test] public void ClickToleranceAndPanelIsolationArePreserved()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var before=map.Project(Vector3.zero);
  map.BeginPan(new Vector2(500,400));Assert.That(map.EndPan(new Vector2(502,401)),Is.True);
  Assert.That(map.Project(Vector3.zero),Is.EqualTo(before));
  map.BeginPan(map.FooterRect.center);map.MovePan(new Vector2(600,450));Assert.That(map.EndPan(new Vector2(600,450)),Is.False);
  Assert.That(map.Project(Vector3.zero),Is.EqualTo(before));
 }

 [Test] public void WheelDuringDragRebasesPanWithoutJumpingBack()
 {
  var map=root.AddComponent<MechBattlefieldMap>();map.BeginPan(new Vector2(500,400));map.MovePan(new Vector2(520,420));
  map.ZoomAt(1,new Vector2(520,420));var before=map.Project(Vector3.zero);
  map.MovePan(new Vector2(530,425));Assert.That(Vector2.Distance(map.Project(Vector3.zero),before+new Vector2(10,5)),Is.LessThan(.001));
  Assert.That(map.EndPan(new Vector2(530,425)),Is.False);
 }
 [Test] public void LosingFocusCancelsCapturedDrag()
 {
  var map=root.AddComponent<MechBattlefieldMap>();map.BeginPan(new Vector2(500,400));map.MovePan(new Vector2(520,420));var before=map.Project(Vector3.zero);
  typeof(MechBattlefieldMap).GetMethod("OnApplicationFocus",Private).Invoke(map,new object[]{false});map.MovePan(new Vector2(800,600));
  Assert.That(map.Project(Vector3.zero),Is.EqualTo(before));Assert.That(map.EndPan(new Vector2(800,600)),Is.False);
 }

 [Test] public void FullMapEdgesAcceptInputButFloatingPanelsBlockIt()
 {
  var map=root.AddComponent<MechBattlefieldMap>();
  Assert.That(map.InPlot(new Vector2(10,10)),Is.True);
  Assert.That(map.InPlot(new Vector2(1500,750)),Is.True);
  Assert.That(map.InPlot(map.FooterRect.center),Is.False);
  Assert.That(map.InPlot(new Vector2(30,30)),Is.False);
  float before=map.RangeKm;map.ZoomAt(1,map.FooterRect.center);Assert.That(map.RangeKm,Is.EqualTo(before));
 }

 [Test] public void LargerScreenPixelsReduceLogicalResolutionWithoutZoomingWorld()
 {
  var original=MechBattlefieldMap.LogicalScreenSize(1600,1);var enlarged=MechBattlefieldMap.LogicalScreenSize(1600,1.4f);
  Assert.That(original,Is.EqualTo(new Vector2Int(1280,800)));
  Assert.That(original.x/(float)enlarged.x,Is.EqualTo(1.4f).Within(.003));Assert.That(original.y/(float)enlarged.y,Is.EqualTo(1.4f).Within(.003));
  var map=root.AddComponent<MechBattlefieldMap>();var before=map.Project(new Vector3(20000,5000,30000));
  typeof(MechBattlefieldMap).GetField("screenPixelScale",Private).SetValue(map,1.8f);
  Assert.That(map.Project(new Vector3(20000,5000,30000)),Is.EqualTo(before));Assert.That(map.RangeKm,Is.EqualTo(map.BaseRangeKm));
 }
 [Test] public void ContentMagnificationKeepsPanelDimensionsAndScalesMapGeometry()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var card=map.DetailsRect;var footer=map.FooterRect;
  var origin=map.Project(Vector3.zero);var distant=map.Project(Vector3.right*10000);
  map.ZoomAt(3,map.MapCenter);
  Assert.That(map.ContentZoom,Is.EqualTo(Mathf.Exp(.48f)).Within(.001));
  Assert.That(Vector2.Distance(map.Project(Vector3.zero),map.Project(Vector3.right*10000))/Vector2.Distance(origin,distant),Is.EqualTo(map.ContentZoom).Within(.001));
  Assert.That(map.DetailsRect,Is.EqualTo(card));Assert.That(map.FooterRect,Is.EqualTo(footer));
  var transform=typeof(MechBattlefieldMap).GetMethod("ContentToScreen",Private);
  var a=(Vector2)transform.Invoke(map,new object[]{new Vector2(500,400)});
  var b=(Vector2)transform.Invoke(map,new object[]{new Vector2(520,400)});
  Assert.That(Vector2.Distance(a,b),Is.EqualTo(20*map.ContentZoom).Within(.001));
 }
 [Test] public void ContentZoomIsBoundedAndHomeRestoresOverview()
 {
  var map=root.AddComponent<MechBattlefieldMap>();map.ZoomAt(100000,map.MapCenter);
  Assert.That(map.ContentZoom,Is.EqualTo(8));map.ZoomAt(-100000,map.MapCenter);map.ZoomAt(-100000,map.MapCenter);
  Assert.That(map.ContentZoom,Is.EqualTo(.25f));map.ResetView();Assert.That(map.ContentZoom,Is.EqualTo(1));
 }
 [Test] public void DisplayTreatmentAdaptsWithoutChangingTextureContract()
 {
  var overview=MechBattlefieldMap.RenderProfile(1);var detail=MechBattlefieldMap.RenderProfile(5);
  Assert.That(overview.x,Is.EqualTo(1));Assert.That(overview.y,Is.EqualTo(.08f).Within(.0001));
  Assert.That(detail.x,Is.GreaterThan(2));Assert.That(detail.y,Is.EqualTo(1));
  Assert.That(detail.z,Is.GreaterThan(overview.z));Assert.That(detail.w,Is.GreaterThan(overview.w));
  Assert.That(MechBattlefieldMap.RenderProfile(.25f),Is.EqualTo(overview));
  Assert.That(MechBattlefieldMap.RenderProfile(8),Is.EqualTo(detail));
 }
 [Test] public void VisibleZoomSmoothsAndConvergesToTheRequestedView()
 {
  var map=root.AddComponent<MechBattlefieldMap>();typeof(MechBattlefieldMap).GetField("visible",Private).SetValue(map,true);
  float initial=map.RangeKm,expected=MechBattlefieldMap.ZoomRange(initial,1);
  map.ZoomAt(1,map.MapCenter);Assert.That(map.RangeKm,Is.EqualTo(initial));
  map.AdvanceView(.05f);Assert.That(map.RangeKm,Is.LessThan(initial));Assert.That(map.RangeKm,Is.GreaterThan(expected));
  for(int i=0;i<5;i++)map.AdvanceView(.25f);
  Assert.That(map.RangeKm,Is.EqualTo(expected).Within(.001));map.ResetView();
 }
 [Test] public void CollapsedFormationPanelReleasesItsUnusedMapArea()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var property=typeof(MechBattlefieldMap).GetProperty("FormationPanelRect",Private);
  Assert.That(map.InPlot(new Vector2(50,100)),Is.False);
  typeof(MechBattlefieldMap).GetField("formationPanelCollapsed",Private).SetValue(map,true);
  var collapsed=(Rect)property.GetValue(map);Assert.That(collapsed.height,Is.EqualTo(40));
  Assert.That(map.InPlot(new Vector2(50,100)),Is.True);
 }
 [Test] public void ExpandedPolicyMenuBlocksMapInput()
 {
  var map=root.AddComponent<MechBattlefieldMap>();
  typeof(MechBattlefieldMap).GetField("openPolicyMenu",Private).SetValue(map,1);
  typeof(MechBattlefieldMap).GetField("policyMenuBounds",Private).SetValue(map,new Rect(500,400,220,118));
  Assert.That(map.InPlot(new Vector2(550,450)),Is.False);
  float before=map.RangeKm;map.ZoomAt(1,new Vector2(550,450));Assert.That(map.RangeKm,Is.EqualTo(before));
 }
 [Test] public void TargetSourcesSortByFreshEvidenceAndExpireIndependently()
 {
  var e=new ACunicombat.Sensing.Contracts.TrackSourceEvidenceSet();
  e.Record(ACunicombat.Sensing.Contracts.TrackSourceMask.Radar,.6f,10);
  e.Record(ACunicombat.Sensing.Contracts.TrackSourceMask.ElectroOptical,.9f,10);
  e.Record(ACunicombat.Sensing.Contracts.TrackSourceMask.DataLink,1,7);
  Assert.That(MechBattlefieldMap.FormatSources(e,10),Is.EqualTo("光电/雷达"));
  e.Record(ACunicombat.Sensing.Contracts.TrackSourceMask.Radar,.95f,11);
  Assert.That(MechBattlefieldMap.FormatSources(e,11),Is.EqualTo("雷达/光电"));
  Assert.That(MechBattlefieldMap.FormatSources(e,14),Is.EqualTo("无当前信源"));
 }
 [Test] public void UnknownIdentityCannotLeakConfiguredRole()
 {
  Assert.That(MechBattlefieldMap.FormatType(false,ACunicombat.Sensing.Contracts.ObservedWeightClass.Heavy,ACunicombat.Sensing.Contracts.ObservedCombatRole.AirToAir),Is.EqualTo("未知类型"));
  Assert.That(MechBattlefieldMap.FormatType(true,ACunicombat.Sensing.Contracts.ObservedWeightClass.Medium,ACunicombat.Sensing.Contracts.ObservedCombatRole.LongRangeAirToAir),Is.EqualTo("中型远程空对空 MC"));
  Assert.That(MechBattlefieldMap.AltitudeSign(70,1),Is.EqualTo(1));Assert.That(MechBattlefieldMap.AltitudeSign(70,0),Is.Zero);Assert.That(MechBattlefieldMap.AltitudeSign(-120,1),Is.EqualTo(-1));
 }
 [Test] public void TrailKeepsObservedTurnsWithoutInventingCoastingPositions()
 {
  var trail=new MechBattlefieldMap.ObservedTrail();
  trail.Observe(Vector3.zero,0,0);trail.Observe(Vector3.right*100,1,1);trail.Observe(new Vector3(100,0,100),2,2);
  trail.Observe(Vector3.one*999,2,2.8);
  Assert.That(trail.Samples.Count,Is.EqualTo(3));Assert.That(trail.Samples[1].Position,Is.EqualTo(Vector3.right*100));
  Assert.That(trail.Samples[2].Position,Is.EqualTo(new Vector3(100,0,100)));
  trail.Observe(Vector3.one*1000,7,7);Assert.That(trail.Samples.Count,Is.EqualTo(1));
 }
 [Test] public void TrailBoundsHistoryAndResetsOnSimulationRewind()
 {
  var trail=new MechBattlefieldMap.ObservedTrail();for(int i=0;i<300;i++)trail.Observe(Vector3.right*i,i,i);
  Assert.That(trail.Samples.Count,Is.LessThanOrEqualTo(128));Assert.That(trail.Samples[0].Time,Is.GreaterThanOrEqualTo(269));
  trail.Observe(Vector3.zero,0,0);Assert.That(trail.Samples.Count,Is.EqualTo(1));
 }
 [Test] public void RadarScanOnlyShowsRecentExecutedRadarPulses()
 {
  var e=new ACunicombat.Sensing.Contracts.EmissionEvent{EmissionType=ACunicombat.Sensing.Contracts.EmissionType.RadarDwell,StartTime=10,EndTime=10.01,Direction=new Unity.Mathematics.float3(0,0,1),BeamWidth=.04f};
  Assert.That(MechBattlefieldMap.IsVisibleRadarEmission(e,10.1),Is.True);
  Assert.That(MechBattlefieldMap.IsVisibleRadarEmission(e,9.9),Is.False);
  Assert.That(MechBattlefieldMap.IsVisibleRadarEmission(e,10.3),Is.False);
  e.EmissionType=ACunicombat.Sensing.Contracts.EmissionType.DataLink;Assert.That(MechBattlefieldMap.IsVisibleRadarEmission(e,10.1),Is.False);
 }
 [Test] public void TargetSizeMatchesRequestedEndpointsAndTransitionsSmoothly()
 {
  Assert.That(MechBattlefieldMap.TargetVisualScale(1),Is.EqualTo(.56f).Within(.0001));
  Assert.That(MechBattlefieldMap.TargetVisualScale(5)*5,Is.EqualTo(1.68f).Within(.0001));
  Assert.That(MechBattlefieldMap.TargetVisualScale(3)*3,Is.EqualTo(1.12f).Within(.0001));
 }
 [TestCase(0,0)] [TestCase(35,25)] [TestCase(170,75)]
 public void WorldGridProjectionRoundTripsAfterOwnshipMovementAndPan(float yaw,float tilt)
 {
  var map=root.AddComponent<MechBattlefieldMap>();root.transform.position=new Vector3(45000,12000,-70000);
  typeof(MechBattlefieldMap).GetField("yaw",Private).SetValue(map,yaw);typeof(MechBattlefieldMap).GetField("tilt",Private).SetValue(map,tilt);
  map.ZoomAt(3,map.MapCenter);map.BeginPan(new Vector2(400,400));map.MovePan(new Vector2(500,460));map.EndPan(new Vector2(500,460));
  var world=map.GridToWorld(new Vector3(30000,0,20000));
  Assert.That(Vector3.Distance(world,map.MapToGridPlane(map.Project(world))),Is.LessThan(.1));
  Assert.That(Vector2.Distance(map.Project(world+Vector3.up*9000,true),map.Project(world)),Is.LessThan(.01));
 }
 [Test] public void WorldGridCellsHaveEqual25KmEdgesBeforeProjection()
 {
  var map=root.AddComponent<MechBattlefieldMap>();float step=MechBattlefieldMap.WorldGridSpacingMeters;
  var a=map.MapToGridPlane(map.Project(Vector3.zero));var b=map.MapToGridPlane(map.Project(Vector3.right*step));var c=map.MapToGridPlane(map.Project(Vector3.forward*step));
  Assert.That(Vector3.Distance(a,b),Is.EqualTo(25000).Within(.1));Assert.That(Vector3.Distance(a,c),Is.EqualTo(25000).Within(.1));
 }
 [TestCase(16f/9)] [TestCase(4f/3)] [TestCase(21f/9)] public void OneXSpans250KmAndTenWorldGridCells(float aspect)
 {
  var map=root.AddComponent<MechBattlefieldMap>();typeof(MechBattlefieldMap).GetField("layoutWidth",Private).SetValue(map,1000f*aspect);map.ResetView();
  Assert.That(map.HorizontalSpanKm,Is.EqualTo(250f).Within(.01));Assert.That(map.ContentZoom,Is.EqualTo(1));
  var distance=Vector2.Distance(map.Project(Vector3.zero),map.Project(Vector3.right*100000));
  Assert.That(distance,Is.EqualTo(map.LayoutWidth*100/250).Within(.01));
  map.ZoomAt(100000,map.MapCenter);Assert.That(map.HorizontalSpanKm,Is.EqualTo(31.25f).Within(.01));Assert.That(map.ContentZoom,Is.EqualTo(8));
 }
 [Test] public void DefaultAndHomeAreVerticalTopDown()
 {
  var map=root.AddComponent<MechBattlefieldMap>();var p=new Vector3(20000,30000,40000);
  Assert.That(map.Project(p),Is.EqualTo(map.Project(p,true)));
  typeof(MechBattlefieldMap).GetField("tilt",Private).SetValue(map,40f);map.ResetView();
  Assert.That(map.Project(p),Is.EqualTo(map.Project(p,true)));
  Assert.That(Vector2.Distance(map.Project(Vector3.zero),map.Project(Vector3.right*25000)),Is.EqualTo(Vector2.Distance(map.Project(Vector3.zero),map.Project(Vector3.forward*25000))).Within(.01));
 }
 [Test] public void RadarBeamWorldRangeStaysFixedWhenZoomingPastDistantTarget()
 {
  var map=root.AddComponent<MechBattlefieldMap>();
  var emission=new ACunicombat.Sensing.Contracts.EmissionEvent{Origin=Unity.Mathematics.double3.zero,MaxRangeMeters=150000};
  var end=MechBattlefieldMap.RadarBeamEndpoint(emission,Vector3.right);
  float original=Vector2.Distance(map.Project(Vector3.zero),map.Project(end));
  map.ZoomAt(Mathf.Log(3.1f)/.16f,map.MapCenter);
  Assert.That(MechBattlefieldMap.RadarBeamEndpoint(emission,Vector3.right),Is.EqualTo(end));
  Assert.That(Vector2.Distance(map.Project(Vector3.zero),map.Project(end))/original,Is.EqualTo(3.1f).Within(.001));
  Assert.That(map.Project(end).x,Is.GreaterThan(map.Project(Vector3.right*65000).x));
 }
}
