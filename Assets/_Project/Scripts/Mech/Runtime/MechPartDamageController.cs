using System;
using System.Collections.Generic;
using UnityEngine;
namespace ACunicombat.Mechs
{
    [DisallowMultipleComponent]
    public sealed class MechPartDamageController : MonoBehaviour
    {
        private sealed class Region
        {
            public string Id;public MechAssemblyRecord Record;public MechDamageSide Side;
            public float Stress,Tolerance;public MechPartDamageProfile Profile;
            public float Efficiency=>Profile.Efficiency(Stress,Tolerance);
            public MechPartCondition Condition=>Profile.Condition(Stress,Tolerance);
        }
        private readonly Dictionary<string,Region> regions=new Dictionary<string,Region>(StringComparer.Ordinal);
        private readonly List<MechDamageRegion> proxies=new List<MechDamageRegion>();
        private readonly List<(Collider collider,bool enabled)> replacedColliders=new List<(Collider,bool)>();
        private readonly Dictionary<string,MechAssemblyRecord> records=new Dictionary<string,MechAssemblyRecord>(StringComparer.Ordinal);
        private readonly HashSet<Guid> receivedHits=new HashSet<Guid>();
        private readonly Queue<Guid> hitHistory=new Queue<Guid>();
        private readonly Dictionary<string,string> parents=new Dictionary<string,string>(StringComparer.Ordinal);
        private int bindingHash;private MechRuntimeState runtime;private CombatTestTarget target;
        public int Version {get;private set;}
        public int LayoutVersion {get;private set;}
        public bool IsBound=>regions.Count>0;
        public bool HasHitRegions=>proxies.Count>0;
        public IReadOnlyList<MechDamageRegion> HitRegions=>proxies;
        public event Action Changed;
        public void Bind(MechRuntimeState state,float totalAP)
        {
            if(state==null)return;int hash=17+Mathf.RoundToInt(totalAP);
            foreach(var part in state.AssembledParts)if(part!=null)hash=unchecked(hash*31+part.InstanceId.GetHashCode());
            if(IsBound&&runtime==state&&hash==bindingHash)return;
            ClearProxies();regions.Clear();records.Clear();parents.Clear();runtime=state;target=state.GetComponent<CombatTestTarget>();bindingHash=hash;
            foreach(var part in state.AssembledParts)
            {
                if(part?.part==null)continue;records[part.SlotId]=part;
                if(MechDamageGeometry.SplitLimbs(part)){AddRegion(part,MechDamageSide.Left);AddRegion(part,MechDamageSide.Right);}
                else AddRegion(part,MechDamageSide.Center);
            }
            var slots=state.CurrentLoadout?.GetSlots();
            foreach(var record in records.Values)
            {
                var parent=slots?.Find(s=>s.id==record.SlotId)?.parentSlotId;
                if(string.IsNullOrEmpty(parent))parent=record.parentMount?.GetComponentInParent<MechAssembledPart>()?.SlotId;
                if(!string.IsNullOrEmpty(parent)&&parent!=record.SlotId)parents[record.SlotId]=parent;
            }
            foreach(var collider in state.GetComponentsInChildren<Collider>(true))
                if(collider.GetComponent<MechDamageRegion>()==null&&collider.GetComponentInParent<MechAssembledPart>()!=null)
                {replacedColliders.Add((collider,collider.enabled));collider.enabled=false;}
            foreach(var geometry in MechDamageGeometry.BuildProxies(state))
            {
                if(!geometry.HasBounds||geometry.Anchor==null)continue;
                string id=MechDamageGeometry.RegionId(geometry.Part.SlotId,geometry.Side);
                var go=new GameObject("DamageRegion_"+id);go.layer=gameObject.layer;go.transform.SetParent(geometry.Anchor,false);
                var proxy=go.AddComponent<MechDamageRegion>();proxy.Configure(this,id,geometry.Part.SlotId,geometry.Part.InstanceId);
                var box=go.AddComponent<BoxCollider>();box.isTrigger=true;box.center=geometry.Bounds.center;
                var size=geometry.Bounds.size;
                box.size=new Vector3(Mathf.Max(.04f,size.x),Mathf.Max(.04f,size.y),Mathf.Max(.04f,size.z));
                proxies.Add(proxy);
            }
            var visibleSlots=new HashSet<string>();foreach(var proxy in proxies)visibleSlots.Add(proxy.SlotId);
            foreach(var record in records.Values)
            {
                if((record.part.Category!=MechPartCategory.Internal&&!(record.part.Category==MechPartCategory.Utility&&record.part.DefenseExtension.kind!=MechDefenseExtensionKind.None))||visibleSlots.Contains(record.SlotId)||parents.ContainsKey(record.SlotId))continue;
                var support=record.part.DamageProfile.supportRegion;
                if(support==MechPartSlot.None)support=record.slot==MechPartSlot.Radar?MechPartSlot.Head:MechPartSlot.Core;
                foreach(var parent in records.Values)
                    if(parent.slot==support&&parent.SlotId!=record.SlotId){parents[record.SlotId]=parent.SlotId;break;}
            }
            LayoutVersion++;Notify();
            void AddRegion(MechAssemblyRecord record,MechDamageSide side)
            {
                string id=MechDamageGeometry.RegionId(record.SlotId,side);var profile=record.part.DamageProfile;
                regions.Add(id,new Region{Id=id,Record=record,Side=side,Profile=profile,Tolerance=profile.ResolveTolerance(record.part,totalAP)});
            }
        }
        private MechDefenseExtensions Extensions => GetComponent<MechDefenseExtensions>() ?? gameObject.AddComponent<MechDefenseExtensions>();
        public int GetReactiveArmorCells(string regionId) => regions.ContainsKey(regionId)?Extensions.ArmorCells(regionId):0;
        public float ResolveReactiveArmor(float damage,Vector3 point,Vector3 direction,TestWeaponKind kind,Collider collider,out bool triggered)
        {
            float result=Extensions.Absorb(ResolveRegion(point,collider),damage,direction,kind,out triggered);
            if(triggered)Notify();return result;
        }
        public bool IsCurrentHitRegion(Collider collider)
        {
            if(!ReferenceEquals(collider,null)&&collider==null)return false;
            var region=collider!=null?collider.GetComponent<MechDamageRegion>():null;
            return region==null||(region.Owner==this&&regions.TryGetValue(region.RegionId,out var current)&&
                current.Record.InstanceId==region.InstanceId&&region.gameObject.activeInHierarchy);
        }
        public bool TryAcceptHit(Guid eventId)
        {
            if(eventId==Guid.Empty)return true;
            if(!receivedHits.Add(eventId))return false;
            hitHistory.Enqueue(eventId);while(hitHistory.Count>4096)receivedHits.Remove(hitHistory.Dequeue());return true;
        }
        public string ResolveRegion(Vector3 hitPoint,Collider hitCollider=null)
        {
            var direct=hitCollider!=null?hitCollider.GetComponent<MechDamageRegion>():null;
            if(direct!=null&&direct.Owner==this&&regions.TryGetValue(direct.RegionId,out var known)&&known.Record.InstanceId==direct.InstanceId)return direct.RegionId;
            string best=null;float distance=float.PositiveInfinity;
            foreach(var proxy in proxies)
            {
                if(proxy==null)continue;var collider=proxy.GetComponent<Collider>();if(collider==null)continue;
                float d=(collider.ClosestPoint(hitPoint)-hitPoint).sqrMagnitude;
                if(d<distance){distance=d;best=proxy.RegionId;}
            }
            if(best!=null)return best;
            foreach(var region in regions.Values)if(region.Record.slot==MechPartSlot.Core)return region.Id;
            return null;
        }
        public bool HasBlastExposure(Vector3 origin,float radius)
        {
            foreach(var proxy in proxies)
            {
                if(proxy==null||!proxy.gameObject.activeInHierarchy)continue;
                var collider=proxy.GetComponent<Collider>();if(collider==null||!collider.enabled)continue;
                var point=collider.ClosestPoint(origin);var delta=point-origin;if(delta.sqrMagnitude>radius*radius)continue;
                if(delta.sqrMagnitude<.0001f)return true;
                if(!Physics.Raycast(origin,delta.normalized,out var blocker,delta.magnitude+.002f,-5,QueryTriggerInteraction.Collide))return true;
                var hit=blocker.collider.GetComponent<MechDamageRegion>();if(hit!=null&&hit.Owner==this&&hit.RegionId==proxy.RegionId)return true;
            }
            return !HasHitRegions;
        }
        public void RecordHit(float effectiveDamage,Vector3 point,Collider hitCollider=null,Vector3? blastOrigin=null,float blastRadius=0)
        {
            if(!float.IsFinite(effectiveDamage)||effectiveDamage<=0)return;
            if(blastOrigin.HasValue&&blastRadius>0)
            {
                var contacts=new Dictionary<string,(float distance,Vector3 point)>();
                foreach(var proxy in proxies)
                {
                    if(proxy==null||!proxy.gameObject.activeInHierarchy)continue;
                    var collider=proxy.GetComponent<Collider>();if(collider==null||!collider.enabled)continue;
                    var contact=collider.ClosestPoint(blastOrigin.Value);float distance=Vector3.Distance(contact,blastOrigin.Value);
                    if(distance>blastRadius)continue;
                    if(!contacts.TryGetValue(proxy.RegionId,out var previous)||distance<previous.distance)contacts[proxy.RegionId]=(distance,contact);
                }
                var weights=new Dictionary<string,float>();float sum=0;
                foreach(var contact in contacts)
                {
                    bool exposed=true;var delta=contact.Value.point-blastOrigin.Value;
                    if(delta.sqrMagnitude>.0001f&&Physics.Raycast(blastOrigin.Value,delta.normalized,out var blocker,delta.magnitude+.002f,-5,QueryTriggerInteraction.Collide))
                    {var blockingRegion=blocker.collider.GetComponent<MechDamageRegion>();exposed=blockingRegion!=null&&blockingRegion.Owner==this&&blockingRegion.RegionId==contact.Key;}
                    if(!exposed)continue;
                    float weight=Mathf.Max(.01f,1-contact.Value.distance/blastRadius);weights[contact.Key]=weight;sum+=weight;
                }
                // Distribute one functional-damage budget, never one full blast per collider.
                if(sum>0)foreach(var weight in weights)AddStress(regions[weight.Key],effectiveDamage*weight.Value/sum);
            }
            else
            {var id=ResolveRegion(point,hitCollider);if(id==null||!regions.TryGetValue(id,out var region))return;AddStress(region,effectiveDamage);}
            Notify();
            GetComponent<MechRadarController>()?.RefreshDamageEffects();
            if(RoleEfficiency(MechPartSlot.FireControlSystem)<=0||RoleEfficiency(MechPartSlot.DataLinkTerminal)<=0)
                GetComponent<MechTestWeaponSystem>()?.CancelAllMissileSupport();
            // Stop pending fire, reloads and guidance on newly unusable equipment. The physical models remain visible.
            var weapons=GetComponent<MechTestWeaponSystem>();
            if(weapons!=null)foreach(var record in records.Values)
                if(GetEquipmentEfficiency(record.SlotId)<=0){weapons.CancelEquipmentAction(record.SlotId);weapons.CancelMissileSupport(record.SlotId);}
        }
        private static void AddStress(Region region,float amount)
        {region.Stress=Mathf.Min(region.Tolerance,region.Stress+amount*Mathf.Max(0,region.Profile.sensitivity));}
        public float GetEquipmentEfficiency(string slotId)=>target!=null&&!target.IsAlive?0:Evaluate(slotId,MechDamageSide.Center,0);
        private float Evaluate(string slotId,MechDamageSide side,int depth)
        {
            if(!enabled||!IsBound)return 1;
            if(!records.TryGetValue(slotId,out var record))return 1;
            if(depth>MechSlotGraph.MaxDepth)return 0;
            float efficiency=SlotEfficiency(slotId,side);
            if(record.slot==MechPartSlot.LeftHandWeapon)efficiency=Mathf.Min(efficiency,RoleEfficiency(MechPartSlot.Arms,MechDamageSide.Left));
            if(record.slot==MechPartSlot.RightHandWeapon)efficiency=Mathf.Min(efficiency,RoleEfficiency(MechPartSlot.Arms,MechDamageSide.Right));
            parents.TryGetValue(slotId,out var parent);
            if(!string.IsNullOrEmpty(parent)&&parent!=slotId)
            {
                var parentSide=record.slot==MechPartSlot.LeftHandWeapon?MechDamageSide.Left:record.slot==MechPartSlot.RightHandWeapon?MechDamageSide.Right:side;
                efficiency=Mathf.Min(efficiency,Evaluate(parent,parentSide,depth+1));
            }
            return efficiency;
        }
        private float SlotEfficiency(string slotId,MechDamageSide side)
        {
            float value=1;bool found=false;
            foreach(var region in regions.Values)
                if(region.Record.SlotId==slotId&&(side==MechDamageSide.Center||region.Side==side||region.Side==MechDamageSide.Center))
                {value=Mathf.Min(value,region.Efficiency);found=true;}
            return found?value:1;
        }
        public float RoleEfficiency(MechPartSlot role,MechDamageSide side=MechDamageSide.Center)
        {
            if(!enabled)return 1;if(target!=null&&!target.IsAlive)return 0;float value=1;
            foreach(var record in records.Values)
            {
                bool matches=record.slot==role || (role==MechPartSlot.Arms&&
                    ((side==MechDamageSide.Left&&record.slot==MechPartSlot.LeftArm)||(side==MechDamageSide.Right&&record.slot==MechPartSlot.RightArm)));
                if(matches)value=Mathf.Min(value,record.part.Category==MechPartCategory.Internal?Evaluate(record.SlotId,side,0):SlotEfficiency(record.SlotId,side));
            }
            return value;
        }
        public float PropulsionEfficiency=>RoleEfficiency(MechPartSlot.Booster)*
            (RoleEfficiency(MechPartSlot.Legs,MechDamageSide.Left)+RoleEfficiency(MechPartSlot.Legs,MechDamageSide.Right))*.5f;
        public MechPartCondition GetCondition(string regionId)
        {
            if(!regions.TryGetValue(regionId,out var region))return MechPartCondition.NormalOrLight;
            if(target!=null&&!target.IsAlive)return MechPartCondition.Destroyed;
            return region.Condition;
        }
        public bool IsSupportLimited(string regionId)
        {
            if(!regions.TryGetValue(regionId,out var region)||MechDamageGeometry.SplitLimbs(region.Record))return false;
            return GetEquipmentEfficiency(region.Record.SlotId)+.0001f<region.Efficiency;
        }
        public void CopySnapshots(List<MechPartDamageSnapshot> destination)
        {
            destination.Clear();foreach(var region in regions.Values)
                destination.Add(new MechPartDamageSnapshot(region.Id,region.Record.SlotId,region.Record.InstanceId,region.Side,GetCondition(region.Id),target!=null&&!target.IsAlive?0:MechDamageGeometry.SplitLimbs(region.Record)?region.Efficiency:GetEquipmentEfficiency(region.Record.SlotId)));
        }
        public void ResetFunctionalDamage(bool newLifetime=false)
        {
            foreach(var region in regions.Values)region.Stress=0;
            Extensions.ResetEquipment();
            if(newLifetime){receivedHits.Clear();hitHistory.Clear();}
            GetComponent<MechTestWeaponSystem>()?.Actions.CancelAll(Time.timeAsDouble);
            GetComponent<MechRadarController>()?.RefreshDamageEffects();Notify();
        }
        private void Notify(){Version++;Changed?.Invoke();}
        public void EnforceRegionColliders()
        {if(!enabled)return;foreach(var original in replacedColliders)if(original.collider!=null)original.collider.enabled=false;}
        private void ClearProxies()
        {
            foreach(var original in replacedColliders)if(original.collider!=null)original.collider.enabled=original.enabled;
            replacedColliders.Clear();
            // Recover after an editor reload too: generated proxies may exist before the runtime list is rebuilt.
            foreach(var proxy in GetComponentsInChildren<MechDamageRegion>(true))
                if(proxy!=null&&(proxy.Owner==this||proxy.Owner==null)&&proxy.GetComponentInParent<CombatTestTarget>()==GetComponent<CombatTestTarget>())
                {proxy.gameObject.SetActive(false);if(Application.isPlaying)Destroy(proxy.gameObject);else DestroyImmediate(proxy.gameObject);}
            proxies.Clear();
        }
        private void OnDestroy(){ClearProxies();}
    }
}
