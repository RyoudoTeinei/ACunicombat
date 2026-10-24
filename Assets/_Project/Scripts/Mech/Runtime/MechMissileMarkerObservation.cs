using System;
using UnityEngine;
namespace ACunicombat.Mechs
{
    [Serializable]
    public struct MechMissileMarkerObservation
    {
        public int frame; public string localProjectileId;
        public double time;
        public uint trackId;
        public string source,style;
        public Vector3 worldPosition;
        public Vector2 hudPosition;
    }
}
