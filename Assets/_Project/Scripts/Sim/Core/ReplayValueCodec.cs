using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;

namespace ACunicombat.Simulation.Core
{
    /// <summary>Local replay DTO codec. Numeric bit patterns survive JSON transport.
    /// Types are supplied by the caller, never instantiated from a file type name.</summary>
    public static class ReplayValueCodec
    {
        private static readonly Dictionary<Type,FieldInfo[]> fields=new Dictionary<Type,FieldInfo[]>();
        private static FieldInfo[] Fields(Type type)
        {
            lock(fields)
            {
                if(!fields.TryGetValue(type,out var value))fields[type]=value=type.GetFields(BindingFlags.Instance|BindingFlags.Public).OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
                return value;
            }
        }
        public static string Encode<T>(T value)
        {
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream))
            {writer.Write(1);Write(writer,typeof(T),value);return Convert.ToBase64String(stream.ToArray());}
        }
        public static T Decode<T>(string payload)
        {
            using(var stream=new MemoryStream(Convert.FromBase64String(payload)))using(var reader=new BinaryReader(stream))
            {
                if(reader.ReadInt32()!=1)throw new InvalidDataException("Unsupported replay value schema");
                var value=(T)Read(reader,typeof(T));
                if(stream.Position!=stream.Length)throw new InvalidDataException("Trailing replay value data");
                return value;
            }
        }
        private static void Write(BinaryWriter w,Type t,object value)
        {
            // Keep Core independent of Unity.Collections; the caller supplies the DTO type.
            if(t.FullName=="Unity.Collections.FixedString128Bytes"){w.Write(value.ToString());return;}
            if(!t.IsValueType){w.Write(value!=null);if(value==null)return;}
            if(t.IsEnum){Write(w,Enum.GetUnderlyingType(t),Convert.ChangeType(value,Enum.GetUnderlyingType(t)));return;}
            if(t==typeof(double)){w.Write((double)value);return;}if(t==typeof(float)){w.Write((float)value);return;}
            if(t==typeof(int)){w.Write((int)value);return;}if(t==typeof(uint)){w.Write((uint)value);return;}
            if(t==typeof(long)){w.Write((long)value);return;}if(t==typeof(ulong)){w.Write((ulong)value);return;}
            if(t==typeof(short)){w.Write((short)value);return;}if(t==typeof(ushort)){w.Write((ushort)value);return;}
            if(t==typeof(byte)){w.Write((byte)value);return;}if(t==typeof(sbyte)){w.Write((sbyte)value);return;}
            if(t==typeof(bool)){w.Write((bool)value);return;}if(t==typeof(string)){w.Write((string)value);return;}
            if(t==typeof(Guid)){w.Write(((Guid)value).ToByteArray());return;}
            if(t.IsArray){var a=(Array)value;w.Write(a.Length);foreach(var item in a)Write(w,t.GetElementType(),item);return;}
            var members=Fields(t);if(members.Length==0)throw new InvalidDataException("Unsupported replay DTO: "+t);
            w.Write(members.Length);foreach(var f in members){w.Write(f.Name);Write(w,f.FieldType,f.GetValue(value));}
        }
        private static object Read(BinaryReader r,Type t)
        {
            if(t.FullName=="Unity.Collections.FixedString128Bytes")return Activator.CreateInstance(t,new object[]{r.ReadString()});
            if(!t.IsValueType&&!r.ReadBoolean())return null;
            if(t.IsEnum)return Enum.ToObject(t,Read(r,Enum.GetUnderlyingType(t)));
            if(t==typeof(double))return r.ReadDouble();if(t==typeof(float))return r.ReadSingle();
            if(t==typeof(int))return r.ReadInt32();if(t==typeof(uint))return r.ReadUInt32();
            if(t==typeof(long))return r.ReadInt64();if(t==typeof(ulong))return r.ReadUInt64();
            if(t==typeof(short))return r.ReadInt16();if(t==typeof(ushort))return r.ReadUInt16();
            if(t==typeof(byte))return r.ReadByte();if(t==typeof(sbyte))return r.ReadSByte();
            if(t==typeof(bool))return r.ReadBoolean();if(t==typeof(string))return r.ReadString();
            if(t==typeof(Guid))return new Guid(r.ReadBytes(16));
            if(t.IsArray)
            {
                int count=r.ReadInt32();if(count<0||count>1000000)throw new InvalidDataException("Replay array bounds");
                var a=Array.CreateInstance(t.GetElementType(),count);for(int i=0;i<count;i++)a.SetValue(Read(r,t.GetElementType()),i);return a;
            }
            var members=Fields(t);if(r.ReadInt32()!=members.Length)throw new InvalidDataException("Replay DTO fields changed: "+t);
            object value=Activator.CreateInstance(t);
            foreach(var f in members){if(r.ReadString()!=f.Name)throw new InvalidDataException("Replay DTO schema mismatch");f.SetValue(value,Read(r,f.FieldType));}
            return value;
        }
    }
}
