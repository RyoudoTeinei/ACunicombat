using System.Collections.Generic;
using UnityEngine;

namespace ACunicombat.Mechs
{
    public static class MechSurfaceNormalTextures
    {
        private static readonly Dictionary<string,Texture2D> cache=new();
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()=>cache.Clear();
        public static Texture2D FindRestored(Texture original)
        {
            if(original==null)return null;
            string name=original.name;
            if(!name.EndsWith("_n",System.StringComparison.OrdinalIgnoreCase))return null;
            if(!cache.TryGetValue(name,out var texture))
            {texture=Resources.Load<Texture2D>("AC6Materials/Normals/"+name);cache[name]=texture;}
            return texture;
        }
    }
}
