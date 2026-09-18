using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DSAnimStudio.NativePhysics;

static partial class NativePhysicsHost
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr BufferMapCall(IntPtr buffer,IntPtr result,IntPtr usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr BufferUnmapCall(IntPtr buffer,IntPtr result);
    static readonly BufferMapCall bufferMap=MapAuthoredBuffer;
    static readonly BufferUnmapCall bufferUnmap=UnmapAuthoredBuffer;
    static readonly Dictionary<IntPtr,UserBuffer> userBuffers=new Dictionary<IntPtr,UserBuffer>();
    static IntPtr MapAuthoredBuffer(IntPtr buffer,IntPtr result,IntPtr usage)
    {
        try{userBuffers[buffer].Map();Marshal.WriteInt32(result,0);}catch{Marshal.WriteInt32(result,-1);}return result;
    }
    static IntPtr UnmapAuthoredBuffer(IntPtr buffer,IntPtr result){Marshal.WriteInt32(result,0);return result;}
    sealed class UserBuffer : IDisposable
    {
        readonly NativeMeshDescription description;
        readonly IntPtr buffer,vtable;
        readonly IntPtr[] allocations=new IntPtr[4],slots=new IntPtr[4];
        readonly byte[] original=new byte[136];
        public UserBuffer(IntPtr buffer,NativeMeshDescription d)
        {
            this.buffer=buffer;description=d;Marshal.Copy(buffer,original,0,original.Length);
            vtable=Marshal.AllocHGlobal(256);var table=new byte[256];Marshal.Copy(Marshal.ReadIntPtr(buffer),table,0,256);Marshal.Copy(table,0,vtable,256);
            try
            {
                for(int c=0;c<4;c++)
                {
                    int conversion=d.Conversions[c],slot=d.Slots[c],start=d.Starts[c];if(conversion==250)continue;
                    int size=conversion==0?16:conversion==1?12:conversion==2?4:conversion==3?6:0;
                    if(size==0||slot<0||slot>=4||start<0||d.Strides[slot]<start+size||d.Strides[slot]>255)throw new InvalidDataException("Unverified native user-buffer layout");
                    if(slots[slot]==IntPtr.Zero){allocations[slot]=Marshal.AllocHGlobal(checked(d.Strides[slot]*d.VertexCount+16));slots[slot]=new IntPtr((allocations[slot].ToInt64()+15)&~15L);}
                    for(int i=0;i<d.VertexCount;i++)
                    {
                        var p=IntPtr.Add(slots[slot],i*d.Strides[slot]+start);var source=d.InitialChannels[c];
                        if(conversion==0||conversion==1)Marshal.Copy(source,i*4,p,conversion==0?4:3);
                        else for(int k=0;k<(conversion==2?4:3);k++)
                        {
                            float value=Math.Max(-1,Math.Min(1,source[i*4+k]));
                            if(conversion==2)Marshal.WriteByte(p,k,(byte)Math.Round(value*127+127));
                            else Marshal.WriteInt16(p,k*2,(short)Math.Round(value*32767));
                        }
                    }
                }
                Marshal.WriteIntPtr(vtable,40,Marshal.GetFunctionPointerForDelegate(bufferMap));Marshal.WriteIntPtr(vtable,48,Marshal.GetFunctionPointerForDelegate(bufferUnmap));
                userBuffers.Add(buffer,this);Marshal.WriteIntPtr(buffer,vtable);Map();
            }
            catch{Dispose();throw;}
        }
        public void Map()
        {
            int[] offsets={0x18,0x40,0x58,0x70};var d=description;
            for(int c=0;c<4;c++)
            {
                int offset=offsets[c],slot=d.Slots[c];bool present=d.Conversions[c]!=250;
                Marshal.WriteIntPtr(buffer,offset,present?IntPtr.Add(slots[slot],d.Starts[c]):IntPtr.Zero);
                Marshal.WriteInt32(buffer,offset+8,present?d.VertexCount:0);Marshal.WriteByte(buffer,offset+12,present?(byte)d.Strides[slot]:(byte)0);
                Marshal.WriteInt32(buffer,offset+16,present&&d.Conversions[c]==0&&d.Starts[c]%16==0&&d.Strides[slot]%16==0?1:0);
            }
        }
        public float[] Read(int channel)
        {
            var d=description;int conversion=d.Conversions[channel];if(conversion==250)return new float[0];
            int slot=d.Slots[channel];var result=new float[d.VertexCount*3];
            for(int i=0;i<d.VertexCount;i++)
            {
                var p=IntPtr.Add(slots[slot],i*d.Strides[slot]+d.Starts[channel]);
                if(conversion<=1)Marshal.Copy(p,result,i*3,3);
                else for(int k=0;k<3;k++)result[i*3+k]=conversion==2?(Marshal.ReadByte(p,k)-127)/127f:Marshal.ReadInt16(p,k*2)/32767f;
            }
            return result;
        }
        public void Dispose()
        {
            userBuffers.Remove(buffer);Marshal.WriteIntPtr(buffer,new IntPtr(BitConverter.ToInt64(original,0)));Marshal.Copy(original,24,IntPtr.Add(buffer,24),original.Length-24);
            for(int i=0;i<4;i++)if(allocations[i]!=IntPtr.Zero){Marshal.FreeHGlobal(allocations[i]);allocations[i]=IntPtr.Zero;}
            Marshal.FreeHGlobal(vtable);
        }
    }
    static void AttachUserBuffers(IntPtr[] instances,NativePhysicsDescription d)
    {
        for(int i=0;i<instances.Length;i++)foreach(var m in d.Cloths[i].Meshes)
        {
            var buffer=Marshal.ReadIntPtr(Marshal.ReadIntPtr(instances[i],0x20),m.BufferIndex*8);
            if(!userBuffers.ContainsKey(buffer))new UserBuffer(buffer,m);
        }
    }
    static void ReleaseUserBuffers(){foreach(var b in new List<UserBuffer>(userBuffers.Values))b.Dispose();}
}
