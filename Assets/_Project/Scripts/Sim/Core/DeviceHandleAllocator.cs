using System;
namespace ACunicombat.Simulation.Core
{
    /// <summary>Bounded device handle allocation. Wrapped generations are retired, not reused.</summary>
    public sealed class DeviceHandleAllocator
    {
        private readonly bool[] allocated;
        private readonly byte[] generations;
        private readonly bool[] reserved;
        public DeviceHandleAllocator(int capacity,params int[] reservedIndices)
        {
            if(capacity<=0)throw new ArgumentOutOfRangeException(nameof(capacity));
            allocated=new bool[capacity];generations=new byte[capacity];reserved=new bool[capacity];
            for(var i=0;i<capacity;i++)generations[i]=1;
            foreach(var index in reservedIndices){if((uint)index>=capacity)throw new ArgumentOutOfRangeException(nameof(reservedIndices));allocated[index]=reserved[index]=true;}
        }
        public bool Owns(Handle handle)=>handle.IsValid && (uint)handle.Index<allocated.Length && allocated[handle.Index] && generations[handle.Index]==handle.Generation;
        public bool TryReserve(Handle handle)
        {
            if(Owns(handle))return true;
            if(!handle.IsValid || (uint)handle.Index>=allocated.Length || allocated[handle.Index] || generations[handle.Index]!=handle.Generation)return false;
            allocated[handle.Index]=true;return true;
        }
        public bool TryAllocate(out Handle handle)
        {
            for(var i=0;i<allocated.Length;i++)if(!allocated[i] && generations[i]!=0)
            {allocated[i]=true;handle=new Handle(i,generations[i]);return true;}
            handle=default;return false;
        }
        public bool Release(Handle handle)
        {
            if(!Owns(handle)||reserved[handle.Index])return false;
            var i=handle.Index;allocated[i]=false;generations[i]=generations[i]==byte.MaxValue?(byte)0:(byte)(generations[i]+1);return true;
        }
    }
}