using UnityEngine;
namespace ACunicombat.Mechs
{
 // Estimated constant-velocity engagement envelope; final release still uses the fire-control solver.
 public static class MechEngagementEstimate
 {
  public static float Reach(Vector3 shooter,Vector3 velocity,Vector3 target,Vector3 targetVelocity,float effective,float maximum,float projectileSpeed)
  {
   if(!float.IsFinite(effective)||!float.IsFinite(maximum)||!float.IsFinite(projectileSpeed)||effective<=0||maximum<=0||projectileSpeed<=0)return 0;
   var direction=(target-shooter).normalized;var displacement=(targetVelocity-velocity)*(effective/projectileSpeed);
   float along=Vector3.Dot(displacement,direction);float cross=Mathf.Max(0,displacement.sqrMagnitude-along*along);
   if(cross>effective*effective)return 0;
   return Mathf.Clamp(Mathf.Sqrt(Mathf.Max(0,effective*effective-cross))-along,0,maximum);
  }
  public static Vector3 FiringPoint(Vector3 shooter,Vector3 target,float reach,float minimum=0)
   =>Vector3.Distance(shooter,target)<minimum?target+(shooter-target).normalized*Mathf.Max(minimum*1.1f,reach*.95f):Vector3.Distance(shooter,target)<=reach?shooter:target+(shooter-target).normalized*(reach*.95f);
  public static float HorizontalRadius(float slant,float height)=>Mathf.Sqrt(Mathf.Max(0,slant*slant-height*height));
 }
}
