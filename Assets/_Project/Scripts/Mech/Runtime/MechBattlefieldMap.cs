using System;
using System.Collections.Generic;
using System.IO;
using ACunicombat.Sensing.Contracts;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.UI;
namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed partial class MechBattlefieldMap : MonoBehaviour
    {
        public struct Contact
        {
            public ContactIdentitySnapshot Identity;public TrackSnapshot Observation;public string Key,Name,Source,Type;public TrackSourceEvidenceSet Evidence;public Vector3 Position,Velocity;public TrackAffiliation Affiliation;
            public bool Missile,Own;public float Confidence;public double Updated;
        }
        public sealed class ObservedTrail
        {
            public const float RetentionSeconds=30f;
            public struct Sample { public Vector3 Position;public double Time; }
            public readonly List<Sample> Samples=new List<Sample>(128);
            private double lastEvidence=double.NegativeInfinity;
            public void Observe(Vector3 position,double evidenceTime,double now)
            {
                if(double.IsNaN(evidenceTime)||double.IsNaN(now)||double.IsInfinity(now)||double.IsInfinity(evidenceTime)||!float.IsFinite(position.x)||!float.IsFinite(position.y)||!float.IsFinite(position.z))return;
                if(Samples.Count>0&&now<Samples[Samples.Count-1].Time){Samples.Clear();lastEvidence=double.NegativeInfinity;}
                Samples.RemoveAll(p=>now-p.Time>RetentionSeconds);
                if(evidenceTime<=lastEvidence||now-evidenceTime>3||evidenceTime>now+.01)return;
                if(evidenceTime-lastEvidence>3)Samples.Clear();
                lastEvidence=evidenceTime;
                if(Samples.Count>0&&now-Samples[Samples.Count-1].Time<.5)return;
                if(Samples.Count>=128)Samples.RemoveAt(0);
                Samples.Add(new Sample{Position=position,Time=now});
            }
        }
        private readonly Dictionary<string,ObservedTrail> trails=new Dictionary<string,ObservedTrail>();
        private readonly HashSet<string> currentTrailKeys=new HashSet<string>();
        private readonly List<string> retiredTrailKeys=new List<string>();
        private float nextHistoryPoll;
        private void RecordTrails()
        {
            double now=radar!=null?radar.SimTimeSeconds:Time.timeAsDouble;currentTrailKeys.Clear();
            foreach(var c in contacts)
            {
                if(c.Missile)continue;
                currentTrailKeys.Add(c.Key);
                if(!trails.TryGetValue(c.Key,out var trail)){trail=new ObservedTrail();trails.Add(c.Key,trail);}
                // Only a new received observation can add a sample; coasting predictions cannot invent history.
                trail.Observe(c.Position,c.Updated,now);
            }
            retiredTrailKeys.Clear();foreach(var key in trails.Keys)if(!currentTrailKeys.Contains(key))retiredTrailKeys.Add(key);
            foreach(var key in retiredTrailKeys){trails.Remove(key);altitudeSigns.Remove(key);}
        }
        private readonly List<Vector2> trailDashIntervals=new List<Vector2>(12);
        public static void BuildTrailDashIntervals(float walked,float segment,float clearance,List<Vector2> result)
        {
            result.Clear();
            if(!float.IsFinite(walked)||!float.IsFinite(segment)||!float.IsFinite(clearance)||walked<0||segment<=0||clearance<0)return;
            float end=Mathf.Min(80,walked+segment);
            // Fixed integer dash indices avoid float increments that can stop advancing.
            for(int dash=0;dash<11;dash++)
            {
                float dashStart=clearance+dash*8;
                float a=Mathf.Max(walked,dashStart),b=Mathf.Min(end,dashStart+5);
                if(b>a)result.Add(new Vector2(a-walked,b-walked));
            }
        }
        private void DrawTrail(Contact c,Vector2 head,Color color,double now)
        {
            if(!trails.TryGetValue(c.Key,out var trail))return;
            Vector2 start=head;float walked=0;float clearance=15*TargetVisualScale(ContentZoom);
            for(int i=trail.Samples.Count-1;i>=0&&walked<80;i--)
            {
                var sample=trail.Samples[i];var end=ScreenToContent(Project(sample.Position));
                float length=Vector2.Distance(start,end);
                if(!float.IsFinite(length))break;
                if(length<.001f){start=end;continue;}
                var direction=(end-start)/length;float segment=Mathf.Min(length,80-walked);
                BuildTrailDashIntervals(walked,segment,clearance,trailDashIntervals);
                var tint=color;tint.a*=Mathf.Lerp(.25f,.75f,Mathf.Clamp01(1-(float)(now-sample.Time)/ObservedTrail.RetentionSeconds));
                foreach(var interval in trailDashIntervals)Line(start+direction*interval.x,start+direction*interval.y,tint,1);
                walked+=segment;start=end;
            }
        }
        private readonly EmissionEvent[] scanEmissions=new EmissionEvent[64];
        public static bool IsVisibleRadarEmission(EmissionEvent emission,double now)
        {
            return emission.EmissionType==EmissionType.RadarDwell&&now>=emission.StartTime&&now-emission.EndTime<=.25
                &&math.all(math.isfinite(emission.Direction))&&math.lengthsq(emission.Direction)>.5f
                &&float.IsFinite(emission.BeamWidth)&&emission.BeamWidth>0;
        }
        private void RadarRay(Vector2 start,Vector2 end,Color tint,float width)
        {
            // Keep the 11.7-unit ownship symbol plus a 4.3-unit clear margin free of scan light.
            var center=ScreenToContent(Project(transform.position));
            var offset=start-center;var delta=end-start;float length=delta.magnitude;
            float radius=16*TargetVisualScale(ContentZoom);
            if(length<.001f)return;
            if(offset.sqrMagnitude<radius*radius)
            {
                var direction=delta/length;float b=Vector2.Dot(offset,direction);
                float exit=-b+Mathf.Sqrt(Mathf.Max(0,b*b+radius*radius-offset.sqrMagnitude));
                if(exit>=length)return;
                start+=direction*exit;
            }
            Line(start,end,tint,width);
        }
        public static Vector3 RadarBeamEndpoint(EmissionEvent emission,Vector3 direction)=>
            (Vector3)(float3)emission.Origin+direction.normalized*(float)emission.MaxRangeMeters;
        private void DrawRadarScan(double now)
        {
            if(radar==null)return;
            int count=radar.CopyOwnRadarEmissions(scanEmissions);
            foreach(var emission in new ArraySegment<EmissionEvent>(scanEmissions,0,count))
            {
                if(!IsVisibleRadarEmission(emission,now))continue;
                var direction=((Vector3)emission.Direction).normalized;
                var across=Vector3.Cross(direction,Mathf.Abs(direction.y)<.9f?Vector3.up:Vector3.right).normalized;
                var vertical=Vector3.Cross(across,direction).normalized;
                var origin=(Vector3)(float3)emission.Origin;
                // World-space extent must not shrink when the map zooms. The UI viewport clips it.
                float reach=(float)emission.MaxRangeMeters;
                if(!float.IsFinite(reach)||reach<=0)continue;
                var start=ScreenToContent(Project(origin));
                float half=Mathf.Min(emission.BeamWidth*.5f,Mathf.PI*.49f);
                float fade=Mathf.Clamp01(1-(float)Math.Max(0,now-emission.EndTime)/.25f);
                var tint=new Color(.24f,.68f,.34f,.36f*fade);
                // Project the real three-dimensional beam cone, including elevation; no artificial rotating sweep.
                for(int side=0;side<12;side++)
                {
                    float angle=side*Mathf.PI*2/12;
                    var edge=direction*Mathf.Cos(half)+(across*Mathf.Cos(angle)+vertical*Mathf.Sin(angle))*Mathf.Sin(half);
                    RadarRay(start,ScreenToContent(Project(RadarBeamEndpoint(emission,edge))),new Color(tint.r,tint.g,tint.b,tint.a*.16f),.8f);
                }
                RadarRay(start,ScreenToContent(Project(RadarBeamEndpoint(emission,direction))),tint,.8f);
            }
        }
        private bool drawingTarget;
        private Vector2 targetAnchor;
        // Counteract world zoom while allowing a restrained, sublinear marker size increase.
        public static float TargetVisualScale(float zoom)
        {float z=Mathf.Clamp(zoom,.25f,8);return .56f*(z<1?1f:1f+.5f*(z-1f))/z;}
        public static bool ShowContactLabels(float zoom)=>zoom>=.5f;
        private Vector2 ScaleTargetPoint(Vector2 p)=>targetAnchor+(p-targetAnchor)*TargetVisualScale(ContentZoom);
        private const int W=1600,H=1000,MapLayer=30;
        private Action<Vector2,Vector2,Color,float> symbolLine;
        private readonly Dictionary<string,int> altitudeSigns=new Dictionary<string,int>();
        private readonly List<Contact> contacts=new List<Contact>();
        private readonly TrackSnapshot[] tracks=new TrackSnapshot[32];
        private readonly MissileTrackSnapshot[] missiles=new MissileTrackSnapshot[64];
        private readonly List<MechCooperativeTelemetry.Report> friends=new List<MechCooperativeTelemetry.Report>();
        private readonly List<Image> lines=new List<Image>();private readonly List<Text> texts=new List<Text>();
        private int lineCount,textCount;private Camera camera;private Canvas canvas;private Font font;private Material material;
        private RenderTexture source,logical,reconstructed,display,overlayDisplay;private GameObject uiRoot;
        private CanvasScaler scaler;private RectTransform plotRoot;private bool drawingWorld;
        private MechTacticalVectorLayer overlayVectors;
        private Rect viewport;private Vector2 pan,targetPan;
        private float layoutWidth=W;
        [SerializeField,Range(.75f,2f)] private float screenPixelScale=1f;
        public float ScreenPixelScale=>screenPixelScale;
        public int DisplayTextureBuildCount {get;private set;}
        public static Vector2Int LogicalScreenSize(float width,float pixelScale)
        {
            float density=.8f/Mathf.Clamp(pixelScale,.75f,2f);
            return new Vector2Int(Mathf.Max(1,Mathf.RoundToInt(width*density)),Mathf.Max(1,Mathf.RoundToInt(H*density)));
        }
        public float LayoutWidth=>layoutWidth;
        public Vector2 MapCenter=>new Vector2(layoutWidth*.5f,H*.5f);
        public Rect DetailsRect=>new Rect(layoutWidth-Mathf.Min(300,layoutWidth-48)-24,24,Mathf.Min(300,layoutWidth-48),440);
        public Rect FooterRect=>new Rect(20,H-190,layoutWidth-40,166);
        private Rect TimeControlRect=>new Rect((layoutWidth-150)/2,20,150,32);
        [SerializeField] private bool mapTimePaused;
        [SerializeField] private float mapPreviousTimeScale=1f;
        private void ToggleMapTime()
        {
            if(mapTimePaused){RestoreMapTime();return;}
            if(Time.timeScale<=0)return;
            mapPreviousTimeScale=Time.timeScale;mapTimePaused=true;Time.timeScale=0;
        }
        private void RestoreMapTime()
        {
            if(!mapTimePaused)return;
            Time.timeScale=mapPreviousTimeScale>0?mapPreviousTimeScale:1;
            mapTimePaused=false;
        }
        private bool pointerCaptured,pointerDragged;private Vector2 pointerStart,panStart;
        public Rect Viewport=>viewport;
        public Vector2Int SourceResolution=>source==null?Vector2Int.zero:new Vector2Int(source.width,source.height);
        private MechRadarController radar;private MechCooperativeTelemetry cooperative;private MechFlightMotionController motion;
        private string selected="own";private bool visible;private float nextRefresh,rangeKm=250f*440/W,targetRangeKm=250f*440/W,yaw,targetYaw,tilt=0,targetTilt;private CursorLockMode priorLock;private bool priorCursor;
        public bool Visible=>visible;public RenderTexture Display=>display;public IReadOnlyList<Contact> Contacts=>contacts;
        public float RangeKm=>rangeKm;
        public float HorizontalSpanKm=>layoutWidth*rangeKm/440f;
        public const float BaseHorizontalSpanKm=250f;
        public float BaseRangeKm=>BaseHorizontalSpanKm*440f/layoutWidth;
        public float ContentZoom=>BaseRangeKm/rangeKm;
        public Vector2Int ContentLogicalResolution=>logical==null?Vector2Int.zero:new Vector2Int(logical.width,logical.height);
        // Magnification changes the display treatment, never the allocated texture sizes.
        public static Vector4 RenderProfile(float zoom)
        {
            float transition=Mathf.SmoothStep(0,1,(Mathf.Clamp(zoom,1,5)-1)/4);
            return new Vector4(Mathf.Lerp(1,2.25f,transition),Mathf.Lerp(.08f,1,transition),Mathf.Lerp(.04f,.7f,transition),Mathf.Lerp(0,.035f,transition));
        }
        private Vector2 ContentToScreen(Vector2 p)=>MapCenter+(p-MapCenter)*ContentZoom;
        private Vector2 ScreenToContent(Vector2 p)=>MapCenter+(p-MapCenter)/ContentZoom;
        public void SelectContact(string key){selected=key;Refresh();}
        public static Rect FitViewport(int width,int height)
        {
            return new Rect(0,0,Mathf.Max(1,width),Mathf.Max(1,height));
        }
        public static Vector2 ScreenToLayout(Vector2 topLeftScreen,Rect view)=>new Vector2((topLeftScreen.x-view.x)*H/view.height,(topLeftScreen.y-view.y)*H/view.height);
        public static float ZoomRange(float current,float scroll)
        {
            if(float.IsNaN(scroll)||float.IsInfinity(scroll))return current;
            // Input System may report normalized notches (1) or legacy Windows deltas (120).
            float notches=Mathf.Abs(scroll)>=20?scroll/120f:scroll;
            return Mathf.Clamp(current*Mathf.Exp(-Mathf.Clamp(notches,-20,20)*.16f),5,2000);
        }
        public void ZoomAt(float scroll,Vector2 point)
        {
            if(!InPlot(point))return;
            float current=visible?targetRangeKm:rangeKm;
            float next=Mathf.Clamp(ZoomRange(current,scroll),BaseRangeKm/8f,BaseRangeKm/.25f);float ratio=current/next;
            var relative=point-MapCenter;targetPan=relative-(relative-(visible?targetPan:pan))*ratio;targetRangeKm=next;
            if(!visible){rangeKm=next;pan=targetPan;}
            if(pointerCaptured){pointerStart=point;panStart=targetPan;}
        }
        public bool InPlot(Vector2 p)=>p.x>=0&&p.x<=layoutWidth&&p.y>=0&&p.y<=H&&!(speedMenuOpen&&speedMenuBounds.Contains(p))&&!(openPolicyMenu!=0&&policyMenuBounds.Contains(p))&&!FooterRect.Contains(p)&&!TimeControlRect.Contains(p)&&!FormationPanelRect.Contains(p)&&(missionEditStage==MissionEditStage.None||!MissionToolbarRect.Contains(p))&&!speedCustomEditing;
        public void BeginPan(Vector2 point)
        {
            pointerCaptured=InPlot(point);pointerDragged=false;pointerStart=point;panStart=visible?targetPan:pan;
        }
        public void MovePan(Vector2 point)
        {
            if(!pointerCaptured)return;
            var delta=point-pointerStart;if(delta.sqrMagnitude>=16)pointerDragged=true;
            if(pointerDragged){targetPan=panStart+delta;if(!visible)pan=targetPan;}
        }
        public bool EndPan(Vector2 point)
        {
            MovePan(point);bool click=pointerCaptured&&!pointerDragged&&InPlot(point);
            pointerCaptured=pointerDragged=false;return click;
        }
        public void ResetView()
        {
            pointerCaptured=pointerDragged=false;rangeKm=targetRangeKm=BaseRangeKm;pan=targetPan=Vector2.zero;
            yaw=targetYaw=0;tilt=targetTilt=0;nextRefresh=0;
        }
        private void SnapViewTarget(){targetRangeKm=rangeKm;targetPan=pan;targetYaw=yaw;targetTilt=tilt;}
        public void AdvanceView(float deltaTime)
        {
            if(!visible||deltaTime<=0)return;
            float blend=1-Mathf.Exp(-Mathf.Min(deltaTime,.25f)/.075f);
            pan=Vector2.Lerp(pan,targetPan,blend);
            rangeKm=Mathf.Exp(Mathf.Lerp(Mathf.Log(rangeKm),Mathf.Log(targetRangeKm),blend));
            yaw=Mathf.Lerp(yaw,targetYaw,blend);tilt=Mathf.Lerp(tilt,targetTilt,blend);
            if(Vector2.Distance(pan,targetPan)<.01f)pan=targetPan;
            if(Mathf.Abs(rangeKm-targetRangeKm)<.0001f)rangeKm=targetRangeKm;
            if(Mathf.Abs(yaw-targetYaw)<.001f)yaw=targetYaw;
            if(Mathf.Abs(tilt-targetTilt)<.001f)tilt=targetTilt;
        }


        private void Awake()
        {
            radar=GetComponent<MechRadarController>();motion=GetComponent<MechFlightMotionController>();
            cooperative=GetComponent<MechCooperativeTelemetry>();
            InitializeMissionGrid();
        }
        private void OnEnable(){RenderPipelineManager.endCameraRendering+=FinishCamera;RenderPipelineManager.beginCameraRendering+=MaskCamera;}
        private void MaskCamera(ScriptableRenderContext context,Camera other){if(other!=camera)other.cullingMask&=~(1<<MapLayer);}
        public void SetVisible(bool value)
        {
            if(visible==value)return;
            if(value)
            {
                MechAssemblyMenu.CloseAllForSolarMap();
                EnsureDisplay();priorLock=Cursor.lockState;priorCursor=Cursor.visible;Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
                visible=true;SnapViewTarget();MechSolarSystemMap.SetBattlefieldVisible(true);uiRoot.SetActive(true);Refresh();
            }
            else
            {
                CancelMissionAssignment();CloseFormationModal();formationMenu=FormationMenu.None;dragFormationMember=Guid.Empty;
                RestoreMapTime();
                visible=false;speedCustomEditing=false;weaponDirectMode=false;commandWeapon=Guid.Empty;contextOpen=false;routeDraft=false;draftWaypoints.Clear();openPolicyMenu=0;pointerCaptured=pointerDragged=false;MechSolarSystemMap.SetBattlefieldVisible(false);if(uiRoot!=null)uiRoot.SetActive(false);
                Cursor.lockState=priorLock;Cursor.visible=priorCursor;
            }
        }
        private void SampleMapPointer()
        {
            var mouse=Mouse.current;if(mouse==null)return;
            var screen=mouse.position.ReadValue();
            mapPointer=ScreenToLayout(new Vector2(screen.x,Screen.height-screen.y),viewport);
        }
        private void Update()
        {
            if(ScenarioEditing){UpdateScenarioEditor();return;}
            var keyboard=Keyboard.current;
            if(visible&&speedCustomEditing&&keyboard!=null&&keyboard.escapeKey.wasPressedThisFrame){speedCustomEditing=false;return;}
            if(visible&&missionEditStage!=MissionEditStage.None&&keyboard!=null&&keyboard.escapeKey.wasPressedThisFrame){CancelMissionAssignment();Refresh();return;}
            if(visible&&formationModal!=FormationModal.None)
            {
                if(keyboard!=null&&keyboard.escapeKey.wasPressedThisFrame)CloseFormationModal();
                if(Time.unscaledTime>=nextRefresh){nextRefresh=Time.unscaledTime+.1f;Refresh();}return;
            }
            if(keyboard!=null&&keyboard.mKey.wasPressedThisFrame){if(!MechAssemblyMenu.IsAnyMenuVisible)SetVisible(!visible);}
            if(!visible){if(Time.unscaledTime>=nextHistoryPoll){nextHistoryPoll=Time.unscaledTime+.5f;Gather();RecordTrails();}return;}
            EnsureDisplay();
            ResizeTargets();
            AdvanceView(Time.unscaledDeltaTime);
            if(keyboard!=null&&keyboard.homeKey.wasPressedThisFrame)ResetView();
            if(keyboard!=null&&keyboard.escapeKey.wasPressedThisFrame){SetVisible(false);return;}
            var mouse=Mouse.current;
            if(mouse!=null)
            {
                SampleMapPointer();var point=mapPointer;
                if(HandleMissionPointer(point,mouse)){pointerCaptured=false;Refresh();return;}
                if(HandleFormationPointer(point,mouse)){pointerCaptured=false;Refresh();return;}
                bool shift=keyboard!=null&&(keyboard.leftShiftKey.isPressed||keyboard.rightShiftKey.isPressed);
                bool alt=keyboard!=null&&(keyboard.leftAltKey.isPressed||keyboard.rightAltKey.isPressed);
                if(routeDraft&&!shift)SubmitMapRoute();
                Cursor.visible=!(WeaponCursorActive&&InPlot(point)&&!contextOpen);
                if(mouse.rightButton.wasPressedThisFrame&&!alt){RightMapCommand(point,shift);Refresh();}
                float scroll=mouse.scroll.ReadValue().y;if(scroll!=0){if(FormationPanelRect.Contains(point))ScrollFormationList(scroll);else ZoomAt(scroll,point);}
                if(alt&&mouse.rightButton.isPressed&&!pointerCaptured&&InPlot(point)){var delta=mouse.delta.ReadValue();targetYaw+=delta.x*.2f;targetTilt=Mathf.Clamp(targetTilt-delta.y*.2f,0,75);}
                if(mouse.leftButton.wasPressedThisFrame){if(HandleCommandClick(point)){pointerCaptured=false;Refresh();return;}BeginPan(point);}
                if(mouse.leftButton.isPressed)MovePan(point);
                if(mouse.leftButton.wasReleasedThisFrame&&EndPan(point))
                {
                    var p=point;
                    float best=24*ContentZoom*TargetVisualScale(ContentZoom);foreach(var c in contacts){float distance=Vector2.Distance(p,Project(c.Position));if(distance<best){best=distance;selected=c.Key;}}
                    ArmSelectedUnit();
                }
            }
            if(Time.unscaledTime>=nextRefresh){nextRefresh=Time.unscaledTime+.1f;Gather();RecordTrails();}
            Redraw();
        }
        private void EnsureDisplay()
        {
            if(uiRoot!=null&&scaler!=null&&plotRoot!=null&&font!=null)return;
            // Hot reload may retain the previous camera/canvas while newly added fields are null.
            ReleaseDisplayResources();
            material=new Material(Resources.Load<Shader>("Shaders/MechTacticalDisplayRgb")){hideFlags=HideFlags.HideAndDontSave};
            ResizeTargets();
            uiRoot=new GameObject("Live battlefield display");uiRoot.layer=MapLayer;
            var cameraObject=new GameObject("Battlefield UI camera");cameraObject.layer=MapLayer;cameraObject.transform.SetParent(uiRoot.transform);
            camera=cameraObject.AddComponent<Camera>();camera.orthographic=true;camera.orthographicSize=500;camera.cullingMask=1<<MapLayer;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=Background;camera.targetTexture=source;camera.nearClipPlane=.01f;camera.farClipPlane=10;camera.depth=110;
            var canvasObject=new GameObject("Observed tactical picture",typeof(RectTransform),typeof(Canvas));canvasObject.layer=MapLayer;canvasObject.transform.SetParent(uiRoot.transform,false);
            canvas=canvasObject.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceCamera;canvas.worldCamera=camera;canvas.planeDistance=1;canvas.pixelPerfect=false;
            scaler=canvasObject.AddComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ConstantPixelSize;scaler.scaleFactor=source.height/(float)H;
            var plot=new GameObject("Clipped tactical content",typeof(RectTransform),typeof(RectMask2D));plot.layer=MapLayer;plot.transform.SetParent(canvas.transform,false);
            plotRoot=plot.GetComponent<RectTransform>();plotRoot.anchorMin=Vector2.zero;plotRoot.anchorMax=Vector2.one;plotRoot.offsetMin=plotRoot.offsetMax=Vector2.zero;
            plot.GetComponent<RectMask2D>().padding=Vector4.zero;plot.GetComponent<RectMask2D>().enabled=false;
            overlayVectors=CreateVectorLayer("Batched tactical geometry",canvas.transform);
            font=Font.CreateDynamicFontFromOSFont(new[]{"Consolas","Microsoft YaHei","SimHei"},36);
        }
        private MechTacticalVectorLayer CreateVectorLayer(string name,Transform parent)
        {
            var go=new GameObject(name,typeof(RectTransform),typeof(CanvasRenderer),typeof(MechTacticalVectorLayer));
            go.layer=MapLayer;go.transform.SetParent(parent,false);
            var rect=go.GetComponent<RectTransform>();rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=rect.offsetMax=Vector2.zero;
            var layer=go.GetComponent<MechTacticalVectorLayer>();layer.raycastTarget=false;return layer;
        }
        private void ResizeTargets()
        {
            var next=FitViewport(Screen.width,Screen.height);
            float oldBase=BaseRangeKm,preservedZoom=ContentZoom,targetZoom=targetRangeKm>0?oldBase/targetRangeKm:preservedZoom;
            layoutWidth=H*next.width/next.height;
            rangeKm=BaseRangeKm/preservedZoom;targetRangeKm=BaseRangeKm/targetZoom;
            var logicalSize=LogicalScreenSize(layoutWidth,screenPixelScale);
            if(plotRoot!=null)plotRoot.GetComponent<RectMask2D>().padding=Vector4.zero;
            if(display!=null&&display.width==(int)next.width&&display.height==(int)next.height&&logical!=null&&logical.width==logicalSize.x&&logical.height==logicalSize.y){viewport=next;return;}
            viewport=next;if(camera!=null)camera.targetTexture=null;
            foreach(var t in new[]{source,logical,reconstructed,display,overlayDisplay})if(t!=null){t.Release();Destroy(t);}
            int w=(int)next.width,h=(int)next.height;
            float supersampling=Mathf.Min(2f,SystemInfo.maxTextureSize/(float)Mathf.Max(w,h));
            int sw=Mathf.Max(1,Mathf.RoundToInt(w*supersampling)),sh=Mathf.Max(1,Mathf.RoundToInt(h*supersampling));
            source=Texture(sw,sh,24);source.filterMode=FilterMode.Point;
            // Zoom never reallocates display resources; only viewport or pixel density does.
            logical=Texture(logicalSize.x,logicalSize.y,0,true);logical.filterMode=FilterMode.Trilinear;
            overlayDisplay=Texture(w,h,0);
            reconstructed=Texture(w,h,0);display=Texture(w,h,0);display.filterMode=FilterMode.Point;
            DisplayTextureBuildCount++;
            if(camera!=null)camera.targetTexture=source;
            if(scaler!=null)scaler.scaleFactor=source.height/(float)H;
            nextRefresh=0;
        }
        private static RenderTexture Texture(int w,int h,int depth,bool mip=false)
        {
            var t=new RenderTexture(w,h,depth,RenderTextureFormat.ARGBHalf,RenderTextureReadWrite.Linear){useMipMap=mip,autoGenerateMips=false,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};t.Create();return t;
        }
        private void FinishCamera(ScriptableRenderContext context,Camera cam)
        {
            if(!visible||cam!=camera||material==null)return;
            var profile=RenderProfile(ContentZoom);
            material.SetVector("_ViewRect",new Vector4(0,0,1,1));
            material.SetVector("_OutputSize",new Vector4(display.width,display.height,0,0));
            // Native clean path: average the supersampled source, without an emitter mask.
            Graphics.Blit(source,overlayDisplay,material,3);
            material.SetFloat("_GlassScatter",profile.w);
            material.SetFloat("_GlassDispersion",Mathf.Lerp(0,.035f,profile.y));
            material.SetFloat("_RgbStrength",profile.z);
            material.SetFloat("_CellScale",profile.x);
            material.SetVector("_LogicalSize",new Vector4(logical.width,logical.height,0,0));
            if(profile.y<=0)
            {
                Graphics.Blit(overlayDisplay,display);
                return;
            }
            Graphics.Blit(source,logical,material,0);logical.GenerateMips();
            Graphics.Blit(logical,reconstructed,material,1);Graphics.Blit(reconstructed,display,material,2);
            material.SetTexture("_CleanTex",overlayDisplay);material.SetFloat("_PixelBlend",profile.y);
            material.SetVector("_FormationRect",PanelUv(FormationPanelRect));
            material.SetVector("_DetailsRect",new Vector4(-2,-2,-1,-1));material.SetVector("_FooterRect",PanelUv(FooterRect));
            material.SetVector("_TimeRect",PanelUv(new Rect(TimeControlRect.x,12,TimeControlRect.width+286,48)));
            material.SetVector("_ContextRect",PanelUv(contextOpen?contextBounds:new Rect(-2,-2,1,1)));
            material.SetVector("_PolicyRect",PanelUv(openPolicyMenu!=0?policyMenuBounds:new Rect(-2,-2,1,1)));
            material.SetVector("_SpeedRect",PanelUv(speedMenuOpen?speedMenuBounds:new Rect(-2,-2,1,1)));
            Graphics.Blit(display,reconstructed,material,4);Graphics.Blit(reconstructed,display);
        }
        private Vector4 PanelUv(Rect r)=>new Vector4(r.x/layoutWidth,1-r.yMax/H,r.xMax/layoutWidth,1-r.y/H);
        private void OnGUI()
        {
            if(!visible||display==null)return;
            int old=GUI.depth;var color=GUI.color;GUI.depth=-4000;
            GUI.color=Background;GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),Texture2D.whiteTexture);
            GUI.color=Color.white;
            // Texture dimensions equal the viewport's physical pixels: no post-filter resampling.
            GUI.DrawTexture(viewport,display,ScaleMode.StretchToFill,false);DrawFormationModalGui();DrawCruiseSpeedEditor();GUI.color=color;GUI.depth=old;
        }
        private Image Stroke()
        {
            if(lineCount==lines.Count){var go=new GameObject("Stroke",typeof(RectTransform),typeof(CanvasRenderer),typeof(Image));go.layer=MapLayer;go.transform.SetParent(canvas.transform,false);var image=go.GetComponent<Image>();image.raycastTarget=false;lines.Add(image);}
            var result=lines[lineCount++];var parent=drawingWorld?(Transform)plotRoot:canvas.transform;if(result.transform.parent!=parent)result.transform.SetParent(parent,false);result.gameObject.SetActive(true);if(!drawingWorld)result.transform.SetAsLastSibling();return result;
        }
        private void Line(Vector2 a,Vector2 b,Color color,float width=1)
        {
            if(drawingTarget){a=ScaleTargetPoint(a);b=ScaleTargetPoint(b);width*=TargetVisualScale(ContentZoom);}
            if(drawingWorld)
            {
                a=ContentToScreen(a);b=ContentToScreen(b);width*=ContentZoom;
                if(!ClipWorldLine(ref a,ref b))return;
            }
            var image=Stroke();image.color=color;var r=image.rectTransform;r.anchorMin=r.anchorMax=new Vector2(0,1);r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=new Vector2((a.x+b.x)*.5f,-(a.y+b.y)*.5f);r.sizeDelta=new Vector2(Vector2.Distance(a,b),width);r.localRotation=Quaternion.Euler(0,0,-Mathf.Atan2(b.y-a.y,b.x-a.x)*Mathf.Rad2Deg);
        }
        private bool ClipWorldLine(ref Vector2 a,ref Vector2 b)
        {
            const float padding=3;float start=0,end=1;var delta=b-a;
            if(!ClipRoute(-delta.x,a.x+padding,ref start,ref end)||!ClipRoute(delta.x,layoutWidth+padding-a.x,ref start,ref end)||
               !ClipRoute(-delta.y,a.y+padding,ref start,ref end)||!ClipRoute(delta.y,H+padding-a.y,ref start,ref end))return false;
            if(end-start<=.00001f)return false;
            var clippedA=a+delta*start;var clippedB=a+delta*end;a=clippedA;b=clippedB;return true;
        }
        private void Label(float x,float y,string value,Color color,int size=17)
        {
            if(textCount==texts.Count){var go=new GameObject("Readout",typeof(RectTransform),typeof(CanvasRenderer),typeof(Text));go.layer=MapLayer;go.transform.SetParent(canvas.transform,false);var t=go.GetComponent<Text>();t.font=font;t.raycastTarget=false;t.horizontalOverflow=HorizontalWrapMode.Overflow;t.verticalOverflow=VerticalWrapMode.Overflow;texts.Add(t);}
            var label=texts[textCount++];var parent=drawingWorld?(Transform)plotRoot:canvas.transform;if(label.transform.parent!=parent)label.transform.SetParent(parent,false);label.gameObject.SetActive(true);if(!drawingWorld)label.transform.SetAsLastSibling();label.horizontalOverflow=HorizontalWrapMode.Overflow;label.verticalOverflow=VerticalWrapMode.Overflow;label.supportRichText=true;label.text=MechTrackDisplayNames.HighlightHostile(value);label.color=color;label.fontSize=size;label.alignment=TextAnchor.UpperLeft;var rect=label.rectTransform;rect.anchorMin=rect.anchorMax=new Vector2(0,1);rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(x,-y);rect.sizeDelta=new Vector2(400,30);
            float zoom=(drawingWorld?ContentZoom:1)*(drawingTarget?TargetVisualScale(ContentZoom):1);rect.localScale=Vector3.one*zoom;
            if(drawingWorld){var p=ContentToScreen(drawingTarget?ScaleTargetPoint(new Vector2(x,y)):new Vector2(x,y));rect.anchoredPosition=new Vector2(p.x,-p.y);}
        }
        private void Fill(Rect bounds,Color color)
        {
            overlayVectors.AddRect(bounds,color);
        }
        private void Box(float x,float y,float w,float h,Color c,float weight=1){Line(new Vector2(x,y),new Vector2(x+w,y),c,weight);Line(new Vector2(x+w,y),new Vector2(x+w,y+h),c,weight);Line(new Vector2(x+w,y+h),new Vector2(x,y+h),c,weight);Line(new Vector2(x,y+h),new Vector2(x,y),c,weight);}
        private void Circle(Vector2 center,float radius,Color c,int segments=64,float width=1)
        {for(int i=0;i<segments;i++){float a=i*Mathf.PI*2/segments,b=(i+1)*Mathf.PI*2/segments;Line(center+new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,center+new Vector2(Mathf.Cos(b),Mathf.Sin(b))*radius,c,width);}}
        [SerializeField] private bool missionGridReady;
        [SerializeField] private Vector3 missionGridOrigin;
        [SerializeField] private Quaternion missionGridRotation=Quaternion.identity;
        private void InitializeMissionGrid()
        {
            if(missionGridReady)return;
            missionGridOrigin=transform.position;
            missionGridRotation=motion!=null?motion.ReferenceRotation:transform.rotation;
            missionGridReady=true;
        }
        public Vector3 WorldToGrid(Vector3 world){InitializeMissionGrid();return Quaternion.Inverse(missionGridRotation)*(world-missionGridOrigin);}
        public Vector3 GridToWorld(Vector3 grid){InitializeMissionGrid();return missionGridOrigin+missionGridRotation*grid;}
        public Vector2 Project(Vector3 world,bool ground=false)
        {
            var local=WorldToGrid(world);if(ground)local.y=0;
            var r=Quaternion.Euler(0,-yaw,0)*(local-WorldToGrid(transform.position));
            float scale=440/(rangeKm*1000);return MapCenter+pan+new Vector2(r.x*scale,-(r.z*Mathf.Cos(tilt*Mathf.Deg2Rad)+r.y*Mathf.Sin(tilt*Mathf.Deg2Rad))*scale);
        }
        public const float WorldGridSpacingMeters=25000f;
        public float DisplayGridSpacingMeters=>WorldGridSpacingMeters*(ContentZoom<.5f?4:ContentZoom<1f?2:1);
        public Vector3 MapToGridPlane(Vector2 point)
        {
            var relative=(point-MapCenter-pan)*(rangeKm*1000/440f);
            var own=WorldToGrid(transform.position);float angle=tilt*Mathf.Deg2Rad;
            float localZ=(-relative.y+own.y*Mathf.Sin(angle))/Mathf.Cos(angle);
            return GridToWorld(own+Quaternion.Euler(0,yaw,0)*new Vector3(relative.x,-own.y,localZ));
        }
        private void DrawWorldGrid()
        {
            var a=WorldToGrid(MapToGridPlane(Vector2.zero));var b=WorldToGrid(MapToGridPlane(new Vector2(layoutWidth,0)));
            var c=WorldToGrid(MapToGridPlane(new Vector2(0,H)));var d=WorldToGrid(MapToGridPlane(new Vector2(layoutWidth,H)));
            float step=DisplayGridSpacingMeters;
            float minX=Mathf.Floor(Mathf.Min(a.x,b.x,c.x,d.x)/step)*step-step;
            float maxX=Mathf.Ceil(Mathf.Max(a.x,b.x,c.x,d.x)/step)*step+step;
            float minZ=Mathf.Floor(Mathf.Min(a.z,b.z,c.z,d.z)/step)*step-step;
            float maxZ=Mathf.Ceil(Mathf.Max(a.z,b.z,c.z,d.z)/step)*step+step;
            int columns=Mathf.CeilToInt((maxX-minX)/step),rows=Mathf.CeilToInt((maxZ-minZ)/step);
            float gridWidth=.8f/ContentZoom;
            for(int i=0;i<=columns;i++)
            {
                float x=minX+i*step;bool major=Mathf.RoundToInt(x/WorldGridSpacingMeters)%4==0;
                Line(ScreenToContent(Project(GridToWorld(new Vector3(x,0,minZ)))),ScreenToContent(Project(GridToWorld(new Vector3(x,0,maxZ)))),major?GridMajor:GridMinor,gridWidth*(major?1.35f:1));
            }
            for(int i=0;i<=rows;i++)
            {
                float z=minZ+i*step;bool major=Mathf.RoundToInt(z/WorldGridSpacingMeters)%4==0;
                Line(ScreenToContent(Project(GridToWorld(new Vector3(minX,0,z)))),ScreenToContent(Project(GridToWorld(new Vector3(maxX,0,z)))),major?GridMajor:GridMinor,gridWidth*(major?1.35f:1));
            }
        }
        private void Gather()
        {
            if(ScenarioEditing){contacts.Clear();contacts.AddRange(scenarioContacts());return;}
            cooperative??=GetComponent<MechCooperativeTelemetry>()??gameObject.AddComponent<MechCooperativeTelemetry>();
            motion??=GetComponent<MechFlightMotionController>();
            radar??=GetComponent<MechRadarController>();contacts.Clear();double now=radar!=null?radar.SimTimeSeconds:Time.timeAsDouble;
            var ownLoadout=GetComponent<MechRuntimeState>()?.CurrentLoadout;
            contacts.Add(new Contact{Key="own",Name="本机",Type=FormatType(true,ownLoadout!=null?ownLoadout.TacticalWeight:ObservedWeightClass.Unknown,ownLoadout!=null?ownLoadout.TacticalRole:ObservedCombatRole.Unknown),Own=true,Position=transform.position,Velocity=motion!=null?motion.Velocity:Vector3.zero,Affiliation=TrackAffiliation.Friendly,Confidence=1,Updated=now,Source="本机导航"});
            cooperative.CopyReports(friends);
            foreach(var report in friends)contacts.Add(FromTrack(report.Track,"friend/"+report.Track.TrackId.Packed,MechTrackDisplayNames.For(radar).Label(report.Track)));
            if(radar==null)return;
            int count=radar.CopyTracks(tracks);
            for(int i=0;i<count;i++)
            {
                var t=tracks[i];if(t.Lifecycle==TrackLifecycle.Dropped||t.ObjectClass==TrackObjectClass.Missile||t.ObjectClass==TrackObjectClass.Decoy)continue;
                var position=(Vector3)(float3)t.PredictPosition(now);bool friendlyDuplicate=false;
                int match=-1,matchCount=0;
                for(int f=0;f<friends.Count;f++)if(math.distance(friends[f].Track.PredictPosition(now),(double3)(float3)position)<150&&math.distance(friends[f].Track.Velocity,t.Velocity)<100){match=f;matchCount++;}
                if(matchCount==1){var friend=contacts[match+1];foreach(var kind in SourceKinds){var e=t.SourceEvidence.Get(kind);if(e.Valid)friend.Evidence.Record(kind,e.Confidence,e.Timestamp);}friend.Source=FormatSources(friend.Evidence,now);contacts[match+1]=friend;friendlyDuplicate=true;}
                if(!friendlyDuplicate)contacts.Add(FromTrack(t,"track/"+t.TrackId.Packed,MechTrackDisplayNames.For(radar).Label(t)));
            }
            count=radar.CopyMissileTracks(missiles);
            for(int i=0;i<count;i++)
            {
                var t=missiles[i];if(!MechMissileDisplayPolicy.ShouldDisplay(in t,now,radar.IsRetiredFriendlyMissileTrack(t.TrackId)))continue;
                contacts.Add(new Contact{Key="missile/"+t.TrackId.Packed,Name=t.KnownModel.IsEmpty?"unknown":t.KnownModel.ToString(),Missile=true,
                    Position=(Vector3)(float3)t.PredictPosition(now),Velocity=(Vector3)(float3)t.Velocity,
                    Affiliation=t.IsFriendly?TrackAffiliation.Friendly:t.IsLikelyTargetingOwnship?TrackAffiliation.Hostile:t.Affiliation,
                    Type="导弹",Confidence=t.Confidence,Updated=t.LastUpdateTime,Source=MissileSources(t.SourceMask)});
            }
        }
        private static readonly TrackSourceMask[] SourceKinds={TrackSourceMask.Radar,TrackSourceMask.ElectroOptical,TrackSourceMask.Esm,TrackSourceMask.DataLink,TrackSourceMask.LaserRangefinder,TrackSourceMask.CloseProtectionRadar,TrackSourceMask.Iff};
        private static string SourceName(TrackSourceMask s){switch(s){case TrackSourceMask.Iff:return "IFF";case TrackSourceMask.Radar:return "雷达";case TrackSourceMask.ElectroOptical:return "光电";case TrackSourceMask.Esm:return "被动射频";case TrackSourceMask.DataLink:return "数据链";case TrackSourceMask.LaserRangefinder:return "激光";case TrackSourceMask.CloseProtectionRadar:return "近防雷达";default:return "未知信源";}}
        public static string FormatSources(TrackSourceEvidenceSet evidence,double now)
        {
            var active=new List<TrackSourceMask>();foreach(var s in SourceKinds)if(evidence.Get(s).Score(now,s==TrackSourceMask.Iff?20:3)>=0)active.Add(s);
            active.Sort((a,b)=>{int order=evidence.Get(b).Score(now,b==TrackSourceMask.Iff?20:3).CompareTo(evidence.Get(a).Score(now,a==TrackSourceMask.Iff?20:3));return order!=0?order:((uint)a).CompareTo((uint)b);});
            return active.Count==0?"无当前信源":string.Join("/",active.ConvertAll(SourceName));
        }
        private static string MissileSources(MissileTrackSourceMask mask)
        {
            var names=new List<string>();
            if((mask&MissileTrackSourceMask.Radar)!=0)names.Add("雷达");if((mask&MissileTrackSourceMask.Rwr)!=0)names.Add("雷达告警");
            if((mask&MissileTrackSourceMask.Maw)!=0)names.Add("导弹告警");if((mask&MissileTrackSourceMask.ElectroOptical)!=0)names.Add("光电");
            if((mask&MissileTrackSourceMask.LaserRangefinder)!=0)names.Add("激光");if((mask&MissileTrackSourceMask.DataLink)!=0)names.Add("数据链");
            return names.Count==0?"无当前信源":string.Join("/",names);
        }
        private static string IdentityRelationLabel(ContactIdentitySnapshot identity)
        {
            if(identity.Conflict)return identity.Relation==IdentityRelation.Hostile?"敌对·身份冲突":identity.Relation==IdentityRelation.SuspectedHostile?"疑似·身份冲突":"身份冲突";
            string relation=identity.Relation==IdentityRelation.Friendly?"友方":identity.Relation==IdentityRelation.Neutral?"中立":identity.Relation==IdentityRelation.SuspectedHostile?"疑似敌对":identity.Relation==IdentityRelation.Hostile?"敌对":"未知";
            return identity.Party==IdentityParty.ThirdParty?"第三方·"+relation:relation;
        }
        public static string FormatType(bool identified,ObservedWeightClass weight,ObservedCombatRole role)
        {
            if(!identified)return "未知类型";
            string w=weight==ObservedWeightClass.Light?"轻型":weight==ObservedWeightClass.Medium?"中型":weight==ObservedWeightClass.Heavy?"重型":"";
            string r=role==ObservedCombatRole.AirToAir?"空对空":role==ObservedCombatRole.AirDefense?"防空":role==ObservedCombatRole.Reconnaissance?"侦查":role==ObservedCombatRole.LongRangeAirToAir?"远程空对空":"";
            return string.IsNullOrEmpty(w+r)?"MC":w+r+" MC";
        }
        private Contact FromTrack(TrackSnapshot t,string key,string name)=>new Contact{Observation=t,Identity=t.Identity,Key=key,Name=name,Type=FormatType(t.IsIdentifiedMech,t.IdentifiedWeight,t.IdentifiedRole),Evidence=t.SourceEvidence,Position=(Vector3)(float3)t.PredictPosition(radar.SimTimeSeconds),Velocity=(Vector3)(float3)t.Velocity,Affiliation=t.Affiliation,Confidence=t.Confidence,Updated=t.LastUpdateTime,Source=FormatSources(t.SourceEvidence,radar.SimTimeSeconds)};
        public static int AltitudeSign(float metres,int previous)
        {if(metres>100)return 1;if(metres< -100)return -1;if(previous>0&&metres>50)return 1;if(previous<0&&metres< -50)return -1;return 0;}
        private void SelectionCorners(Vector2 center)
        {
            symbolLine??=Line;MechTacticalSymbols.Selection(symbolLine,center);
        }
        public void Refresh()
        {
            Gather();if(!ScenarioEditing)RecordTrails();Redraw();
        }
        private void Redraw()
        {
            if(canvas==null)return;lineCount=0;textCount=0;drawingWorld=false;
            SampleMapPointer();overlayVectors.Begin();
            plotRoot.SetAsFirstSibling();overlayVectors.transform.SetAsLastSibling();
            double now=radar!=null?radar.SimTimeSeconds:Time.timeAsDouble;
            drawingWorld=true;
            DrawWorldGrid();
            if(!ScenarioEditing){DrawMissionRegionsWorld();DrawRadarScan(now);}
            foreach(var c in contacts)
            {
                Vector3 presentedPosition=PresentationPosition(c,now);
                var screenPoint=Project(presentedPosition);if(screenPoint.x< -200*ContentZoom||screenPoint.x>layoutWidth+50*ContentZoom||screenPoint.y< -80*ContentZoom||screenPoint.y>H+80*ContentZoom)continue;
                var p=ScreenToContent(screenPoint);
                Color color=c.Own?PrimaryText:c.Identity.Available&&c.Identity.Relation==IdentityRelation.Neutral?new Color(.55f,.58f,.57f):c.Affiliation==TrackAffiliation.Friendly?FriendlyColor:c.Affiliation==TrackAffiliation.Hostile?HostileColor:UnknownColor;
                DrawTrail(c,p,color,now);
                var ground=ScreenToContent(Project(presentedPosition,true));if(Vector2.Distance(p,ground)>3){Line(p,ground,MutedColor);Line(ground-new Vector2(4,0),ground+new Vector2(4,0),MutedColor);}
                targetAnchor=p;drawingTarget=true;
                symbolLine??=Line;
                MechTacticalSymbols.Draw(symbolLine,p,c.Affiliation,c.Missile,color,c.Missile?.8f:1);
                drawingTarget=false;
                var v=ScreenToContent(Project(presentedPosition+c.Velocity*20))-p;
                float clearance=15.3f*TargetVisualScale(ContentZoom)*(c.Missile?.8f:1);
                float length=Mathf.Min(v.magnitude,80)*(c.Missile?.65f:1);
                if(length>clearance)
                {
                    var start=p+v.normalized*clearance;
                    var end=p+v.normalized*(clearance+(length-clearance)*(c.Missile?1f:.9f));
                    Line(start,end,color);
                }
                drawingTarget=true;
                if(ShowContactLabels(ContentZoom))
                {
                    var rel=c.Position-transform.position;
                    altitudeSigns.TryGetValue(c.Key,out int previous);int sign=AltitudeSign(rel.y,previous);altitudeSigns[c.Key]=sign;
                    string arrow=sign>0?" ▲":sign<0?" ▼":"";
                    if(c.Own)PlatformLabel(p.x+24,p.y+3,"本机",color,14);
                    else if(c.Missile)
                    {
                        Label(p.x+24,p.y+3,c.Name,color,14);
                        Label(p.x+24,p.y+21,$"R {rel.magnitude/1000:0.0} km",SecondaryText,13);
                    }
                    else
                    {
                        PlatformLabel(p.x+24,p.y+3,$"{c.Name} / {c.Type}{arrow}",color,14);
                        PlatformLabel(p.x+24,p.y+22.8f,$"R {rel.magnitude/1000:0.0} km ({c.Source})",color,13);
                        PlatformLabel(p.x+24,p.y+41.5f,$"Z {rel.y/1000:+0.0;-0.0;+0.0} km",SecondaryText,13);
                    }
                    if(c.Identity.Available&&(c.Identity.Conflict||c.Identity.Relation==IdentityRelation.SuspectedHostile))Label(p.x-5,p.y-27,"!",HostileColor,14);
                }
                if(c.Key==selected)SelectionCorners(p);
                drawingTarget=false;
            }
            drawingWorld=false;
            if(!ScenarioEditing)DrawMapCommandUi(now);
            overlayVectors.Complete();
            for(int i=lineCount;i<lines.Count;i++)lines[i].gameObject.SetActive(false);for(int i=textCount;i<texts.Count;i++)texts[i].gameObject.SetActive(false);
        }
        private void PlatformLabel(float x,float y,string value,Color color,int size)
        {
            Label(x,y,value,color,size);
            texts[textCount-1].rectTransform.localScale*=1.1f;
        }
        public void Capture(string path)
        {
            if(display==null)throw new InvalidOperationException("Map has not rendered");SaveTexture(display,path);
        }
        public void CaptureRegion(string path,Rect area,int width=1400)
        {
            if(logical==null||material==null)throw new InvalidOperationException("Map has not rendered");
            if(area.width<=0||area.height<=0||width<1)throw new ArgumentOutOfRangeException(nameof(area));
            int height=Mathf.Max(1,Mathf.RoundToInt(width*area.height/area.width));
            var core=Texture(width,height,0);var glass=Texture(width,height,0);
            try
            {
                // Reconstruct from this live frame's logical cells; never upscale the final screenshot.
                material.SetVector("_ViewRect",new Vector4(area.x/layoutWidth,1-area.yMax/H,area.width/layoutWidth,area.height/H));
                material.SetVector("_OutputSize",new Vector4(width,height,0,0));
                Graphics.Blit(logical,core,material,1);Graphics.Blit(core,glass,material,2);SaveTexture(glass,path);
            }
            finally
            {
                material.SetVector("_ViewRect",new Vector4(0,0,1,1));material.SetVector("_OutputSize",new Vector4(display.width,display.height,0,0));
                core.Release();glass.Release();Destroy(core);Destroy(glass);
            }
        }
        private static void SaveTexture(RenderTexture image,string path)
        {
            var old=RenderTexture.active;RenderTexture.active=image;
            var t=new Texture2D(image.width,image.height,TextureFormat.RGBAFloat,false,true);t.ReadPixels(new Rect(0,0,image.width,image.height),0,0);t.Apply();RenderTexture.active=old;
            var pixels=t.GetPixels();for(int i=0;i<pixels.Length;i++)pixels[i]=pixels[i].gamma;var png=new Texture2D(image.width,image.height,TextureFormat.RGBA32,false);png.SetPixels(pixels);png.Apply();File.WriteAllBytes(path,png.EncodeToPNG());Destroy(t);Destroy(png);
        }
        private void OnApplicationFocus(bool focused){if(!focused)pointerCaptured=pointerDragged=false;}
        private void OnDisable(){SetVisible(false);RestoreMapTime();RenderPipelineManager.endCameraRendering-=FinishCamera;RenderPipelineManager.beginCameraRendering-=MaskCamera;}
        private void ReleaseDisplayResources()
        {
            if(uiRoot!=null)Destroy(uiRoot);uiRoot=null;canvas=null;camera=null;scaler=null;plotRoot=null;
            overlayVectors=null;lines.Clear();texts.Clear();
            foreach(var t in new[]{source,logical,reconstructed,display,overlayDisplay})if(t!=null){t.Release();Destroy(t);}
            source=logical=reconstructed=display=overlayDisplay=null;
            if(material!=null)Destroy(material);material=null;if(font!=null)Destroy(font);font=null;
        }
        private void OnDestroy(){ReleaseDisplayResources();}
    }
}
