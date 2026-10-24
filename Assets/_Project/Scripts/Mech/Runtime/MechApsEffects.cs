using UnityEngine;
namespace ACunicombat.Mechs
{
    public sealed class MechApsEffects : MonoBehaviour
    {
        private static Material casing,jet,smoke,sparks;
        private Light flash;private float elapsed;
        public const float SmokeRingLifetime=1.5f;
        public static void AttachCylinder(GameObject root)
        {
            if(casing==null)casing=MechEffectResources.CreateLitDebrisMaterial("APS casing",new Color(.24f,.28f,.3f));
            var body=GameObject.CreatePrimitive(PrimitiveType.Cylinder);body.name="Cylindrical countermunition";
            body.transform.SetParent(root.transform,false);body.transform.localRotation=Quaternion.Euler(90,0,0);body.transform.localScale=new Vector3(.45f,.35f,.45f);
            var collider=body.GetComponent<Collider>();collider.enabled=false;Destroy(collider);body.GetComponent<Renderer>().sharedMaterial=casing;
        }
        private static AudioClip Clip(string prefix,int count)=>MechAudioVariantSet.LoadNumbered("Audio/APS/",prefix,1,count).Next();
        private static void Play(AudioClip clip,Vector3 position,float volume,bool ownship=false,bool airburst=false)
        {
            if(clip==null)return;var root=new GameObject("APS audio");root.transform.position=position;
            var source=root.AddComponent<AudioSource>();source.playOnAwake=false;source.clip=clip;source.volume=volume;source.pitch=Random.Range(.97f,1.03f);
            source.spatialBlend=ownship?0:1;source.dopplerLevel=0;source.rolloffMode=AudioRolloffMode.Logarithmic;source.minDistance=airburst?150:12;source.maxDistance=airburst?1500:450;
            source.Play();Destroy(root,clip.length/.97f+.1f);
        }
        public static void Launch(Vector3 position,Vector3 direction,bool ownship)
        {
            var listener=Object.FindAnyObjectByType<AudioListener>();bool far=listener!=null&&Vector3.Distance(listener.transform.position,position)>100;
            Play(Clip(ownship?"smoke_grenade_launcher_interior-":far?"smoke_grenade_launcher_far-":"smoke_grenade_launcher-",4),position,ownship?.5f:.65f,ownship);
            var root=new GameObject("APS ejection puff");root.transform.SetPositionAndRotation(position,Quaternion.LookRotation(direction));
            EnsureMaterials();var ps=System(root.transform,"Ejection",smoke,.22f,1.1f);
            for(int i=0;i<12;i++)Emit(ps,position+Random.insideUnitSphere*.15f,direction*Random.Range(1f,4f),Random.Range(.4f,1f),.22f,new Color(.9f,.92f,.95f,.25f));
            Destroy(root,.4f);
        }
        public static GameObject Burst(Vector3 position,Vector3 direction,float coneDegrees,bool audio=true,float smokeScale=20)
        {
            smokeScale=Mathf.Max(1,smokeScale);
            EnsureMaterials();var root=new GameObject("APS directional burst");root.transform.SetPositionAndRotation(position,Quaternion.LookRotation(direction));
            var component=root.AddComponent<MechApsEffects>();
            var flame=System(root.transform,"Forward flash jet",jet,.22f,2);
            flame.GetComponent<ParticleSystemRenderer>().sortingOrder=1;
            var fragments=System(root.transform,"Forward fragments",sparks,.5f,.08f);
            var renderer=fragments.GetComponent<ParticleSystemRenderer>();renderer.renderMode=ParticleSystemRenderMode.Stretch;renderer.velocityScale=.025f;renderer.lengthScale=1.5f;
            var ring=System(root.transform,"White transverse smoke ring",smoke,SmokeRingLifetime,1);
            var basis=root.transform;
            // Overlapping lobes form a thick, irregular wall and a forward plume.
            var core=System(root.transform,"Forward dense smoke",smoke,1.4f,1);
            for(int i=0;i<128;i++)
            {
                float angle=Random.value*Mathf.PI*2;
                var radial=basis.right*Mathf.Cos(angle)+basis.up*Mathf.Sin(angle);
                float lobes=1+.22f*Mathf.Sin(angle*5+.8f)+.13f*Mathf.Sin(angle*9);
                var offset=radial*Random.Range(.12f,.72f)*lobes+direction*Random.Range(-.22f,.28f);
                float shade=Random.Range(.62f,.98f);
                Emit(ring,position+offset*smokeScale,(radial*Random.Range(1.5f,4.5f)*lobes+direction*Random.Range(.1f,1.2f))*smokeScale,Random.Range(.8f,1.7f)*smokeScale,Random.Range(1.05f,SmokeRingLifetime),new Color(shade,shade,shade*.98f,.65f));
            }
            for(int i=0;i<64;i++)
            {
                var axis=Cone(basis,38);float shade=Random.Range(.15f,.4f);
                Emit(core,position+axis*Random.Range(.1f,1.1f)*smokeScale,axis*Random.Range(2,5)*smokeScale,Random.Range(.65f,1.25f)*smokeScale,Random.Range(.85f,1.4f),new Color(shade,shade*.94f,shade*.88f,.8f));
            }
            for(int i=0;i<36;i++)
            {
                var axis=Cone(basis,32);
                Emit(flame,position+axis*Random.Range(.15f,.95f)*smokeScale,axis*Random.Range(2,5)*smokeScale,Random.Range(.6f,1.1f)*smokeScale,Random.Range(.2f,.4f),new Color(9f,Random.Range(2.5f,4f),.35f,.85f));
            }
            for(int i=0;i<110;i++)
            {var axis=Cone(basis,coneDegrees);Emit(fragments,position,axis*Random.Range(55f,130f),Random.Range(.035f,.09f),Random.Range(.2f,.5f),new Color(1,.85f,.5f,1));}
            component.flash=root.AddComponent<Light>();component.flash.color=new Color(1,.7f,.36f);component.flash.intensity=9;component.flash.range=18;component.flash.shadows=LightShadows.None;
            if(audio)
            {
                var main=MechAudioVariantSet.LoadNumbered("Audio/Impacts/Missile/","explosion_air_close_",1,10).Next();Play(main,position,1f,airburst:true);
                Play(Clip("smoke_grenade_explosion-",4),position,.3f,airburst:true);
            }
            Destroy(root,1.8f);return root;
        }
        private static Vector3 Cone(Transform basis,float degrees)
        {
            float cos=Mathf.Lerp(1,Mathf.Cos(degrees*Mathf.Deg2Rad),Random.value),sin=Mathf.Sqrt(Mathf.Max(0,1-cos*cos)),phi=Random.value*Mathf.PI*2;
            return basis.forward*cos+(basis.right*Mathf.Cos(phi)+basis.up*Mathf.Sin(phi))*sin;
        }
        private static void EnsureMaterials()
        {
            if(jet==null){jet=MechEffectResources.CreateAdditiveParticleMaterial("APS textured flash",Color.white);jet.mainTexture=MechEffectResources.FireballParticleTexture;jet.SetTexture("_BaseMap",jet.mainTexture);}
            if(sparks==null)sparks=MechEffectResources.CreateAdditiveParticleMaterial("APS fragments",Color.white);
            if(smoke==null){smoke=MechEffectResources.CreateSmokeParticleMaterial("APS shaded cloud",Color.white);smoke.mainTexture=BuildCloudTexture();smoke.SetTexture("_BaseMap",smoke.mainTexture);}
        }
        // Baked turbulent density and lighting keep lobes readable without per-particle lights.
        private static Texture2D BuildCloudTexture()
        {
            const int resolution=128;
            var texture=new Texture2D(resolution,resolution,TextureFormat.RGBA32,true){name="APS turbulent cloud",hideFlags=HideFlags.DontSave,wrapMode=TextureWrapMode.Clamp};
            var pixels=new Color[resolution*resolution];
            for(int y=0;y<resolution;y++)for(int x=0;x<resolution;x++)
            {
                float u=(x+.5f)/resolution,v=(y+.5f)/resolution;
                float px=u*2-1,py=v*2-1;
                float broad=Mathf.PerlinNoise(u*5.8f+3.1f,v*5.8f+9.7f),fine=Mathf.PerlinNoise(u*19+17,v*19+5);
                float density=Mathf.Clamp01((.83f+(broad-.5f)*.35f-Mathf.Sqrt(px*px+py*py))*9);
                float light=Mathf.Clamp01(.42f+py*.22f+(broad-.5f)*.75f+(fine-.5f)*.2f);
                pixels[y*resolution+x]=new Color(light,light,light,density*(.65f+.35f*broad));
            }
            texture.SetPixels(pixels);texture.Apply(true,true);return texture;
        }
        private static ParticleSystem System(Transform parent,string name,Material material,float life,float size)
        {
            var go=new GameObject(name);go.transform.SetParent(parent,false);var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=ps.main;main.playOnAwake=false;main.loop=false;main.duration=life;main.startLifetime=life;main.startSpeed=0;main.startSize=size;main.maxParticles=256;main.simulationSpace=ParticleSystemSimulationSpace.World;
            var emission=ps.emission;emission.enabled=false;var shape=ps.shape;shape.enabled=false;
            var colors=ps.colorOverLifetime;colors.enabled=true;var gradient=new Gradient();gradient.SetKeys(new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},new[]{new GradientAlphaKey(1,0),new GradientAlphaKey(.7f,.35f),new GradientAlphaKey(0,1)});colors.color=gradient;
            var sizes=ps.sizeOverLifetime;sizes.enabled=true;sizes.size=new ParticleSystem.MinMaxCurve(1,AnimationCurve.Linear(0,.65f,1,1.6f));
            var renderer=go.GetComponent<ParticleSystemRenderer>();renderer.sharedMaterial=material;renderer.shadowCastingMode=UnityEngine.Rendering.ShadowCastingMode.Off;renderer.receiveShadows=false;renderer.sortMode=ParticleSystemSortMode.Distance;
            ps.Play();return ps;
        }
        private static void Emit(ParticleSystem ps,Vector3 position,Vector3 velocity,float size,float lifetime,Color color)
            =>ps.Emit(new ParticleSystem.EmitParams{position=position,velocity=velocity,startSize=size,startLifetime=lifetime,startColor=color,rotation=Random.Range(0,360)},1);
        private void Update(){elapsed+=Time.deltaTime;if(flash!=null)flash.intensity=9*Mathf.Clamp01(1-elapsed/.09f);}
    }
}
