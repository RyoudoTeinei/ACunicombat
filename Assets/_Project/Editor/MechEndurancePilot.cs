using UnityEngine;
using ACunicombat.Mechs;
// Normal flight-control request only. No transform, velocity, target, or sensor state injection.
public static class MechEndurancePilot
{
 public static MechPlayerInputFrame Patrol(double elapsed,Vector3 anchor,Vector3 position,Vector3 velocity,Quaternion logicalRotation)
 {
  float t=(float)elapsed,phase=t/18f,rise=Mathf.Exp(-t/12f);
  var desired=anchor+new Vector3(4500*Mathf.Sin(phase),2000*(1-rise),4500*(1-Mathf.Cos(phase)));
  var desiredVelocity=new Vector3(250*Mathf.Cos(phase),2000f/12*rise,250*Mathf.Sin(phase));
  var acceleration=Vector3.ClampMagnitude((desired-position)*.012f+(desiredVelocity-velocity)*.45f,50);
  var local=Quaternion.Inverse(logicalRotation)*acceleration;
  return new MechPlayerInputFrame{HasAutonomousFlightCommand=true,AutonomousAccelerationWorld=acceleration,MoveLocal=Vector2.ClampMagnitude(new Vector2(local.x,local.z)/50,1),RiseHeld=t<8||local.y>.5f,DescendHeld=t>=8&&local.y<-.5f};
 }
}
