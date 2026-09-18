using System;
using System.IO;

namespace DSAnimStudio.NativePhysics
{
    // Shared by the .NET Framework native helper and the .NET DSA client.
    // Matrices use four contiguous float4 columns in Havok (the same flat
    // storage as XNA row-vector matrices); positions never include UI offsets.
    public sealed class NativeSkeletonDescription
    {
        public string Name;
        public string[] Bones;
        public float[] ReferenceMatrices;
        public bool[] ReadByCloth;
    }
    public sealed class NativeMeshDescription
    {
        public int BufferIndex, MeshIndex, VertexCount, ReadBufferIndex;
        public int SceneSection;
        // DSA render metadata; the native host only needs buffer topology.
        public int[] WrittenVertices, AliasSources, AliasTargets;
        public float[] ReferenceNormals, ReferenceTangents;
        public int[] Conversions, Slots, Starts, Strides;
        public float[][] InitialChannels;
    }
    public sealed class NativeClothDescription
    {
        public string Name;
        public int PreviousInitializationState, CurrentInitializationState;
        public int[] TransformSkeletons;
        public NativeMeshDescription[] Meshes;
        public NativeParticleInitialization[] Initializations=new NativeParticleInitialization[0];
        public NativeParticleRoute[] InitializationRoutes=new NativeParticleRoute[0];
    }
    public sealed class NativeParticleRoute
    {
        public int SourceSimulation,TargetSimulation,Count;
    }
    public sealed class NativeParticleInitialization
    {
        public int Operator,Simulation,Input,Output,Count;
    }
    public sealed class NativePhysicsDescription
    {
        public NativeSkeletonDescription[] Skeletons;
        public NativeClothDescription[] Cloths;
        public void Write(BinaryWriter w)
        {
            w.Write(0x3350444E);w.Write(Skeletons.Length);
            foreach(var s in Skeletons){w.Write(s.Name);w.Write(s.Bones.Length);foreach(var b in s.Bones)w.Write(b);NativePhysicsProtocol.WriteFloats(w,s.ReferenceMatrices);}
            w.Write(Cloths.Length);
            foreach(var c in Cloths){w.Write(c.Name);w.Write(c.PreviousInitializationState);w.Write(c.CurrentInitializationState);w.Write(c.TransformSkeletons.Length);foreach(var i in c.TransformSkeletons)w.Write(i);w.Write(c.Meshes.Length);foreach(var m in c.Meshes){w.Write(m.BufferIndex);w.Write(m.MeshIndex);w.Write(m.VertexCount);w.Write(m.ReadBufferIndex);foreach(var a in new[]{m.Conversions,m.Slots,m.Starts,m.Strides})foreach(int v in a)w.Write(v);foreach(var a in m.InitialChannels)NativePhysicsProtocol.WriteFloats(w,a);}}
            foreach(var c in Cloths){w.Write(c.Initializations.Length);foreach(var i in c.Initializations){w.Write(i.Operator);w.Write(i.Simulation);w.Write(i.Input);w.Write(i.Output);w.Write(i.Count);}}
            foreach(var c in Cloths){w.Write(c.InitializationRoutes.Length);foreach(var i in c.InitializationRoutes){w.Write(i.SourceSimulation);w.Write(i.TargetSimulation);w.Write(i.Count);}}
        }
        public static NativePhysicsDescription Read(BinaryReader r)
        {
            if(r.ReadInt32()!=0x3350444E)throw new InvalidDataException("Unsupported native physics description");
            var d=new NativePhysicsDescription{Skeletons=new NativeSkeletonDescription[NativePhysicsProtocol.Count(r,64)]};
            for(int i=0;i<d.Skeletons.Length;i++)
            {
                var s=new NativeSkeletonDescription{Name=r.ReadString(),Bones=new string[NativePhysicsProtocol.Count(r,8192)]};
                for(int j=0;j<s.Bones.Length;j++)s.Bones[j]=r.ReadString();s.ReferenceMatrices=NativePhysicsProtocol.ReadFloats(r,s.Bones.Length*16);d.Skeletons[i]=s;
            }
            d.Cloths=new NativeClothDescription[NativePhysicsProtocol.Count(r,1024)];
            for(int i=0;i<d.Cloths.Length;i++)
            {
                var c=new NativeClothDescription{Name=r.ReadString(),PreviousInitializationState=NativePhysicsProtocol.Count(r,65535),CurrentInitializationState=NativePhysicsProtocol.Count(r,65535),TransformSkeletons=new int[NativePhysicsProtocol.Count(r,128)]};
                for(int j=0;j<c.TransformSkeletons.Length;j++){c.TransformSkeletons[j]=r.ReadInt32();if(c.TransformSkeletons[j]<0||c.TransformSkeletons[j]>=d.Skeletons.Length)throw new InvalidDataException("Transform skeleton mapping");}
                c.Meshes=new NativeMeshDescription[NativePhysicsProtocol.Count(r,2048)];
                for(int j=0;j<c.Meshes.Length;j++)
                {
                    var m=new NativeMeshDescription{BufferIndex=r.ReadInt32(),MeshIndex=r.ReadInt32(),VertexCount=NativePhysicsProtocol.Count(r,1000000),ReadBufferIndex=r.ReadInt32()};if(m.BufferIndex<0||m.MeshIndex<0||m.ReadBufferIndex<0)throw new InvalidDataException("Mesh mapping");
                    m.Conversions=new int[4];m.Slots=new int[4];m.Starts=new int[4];m.Strides=new int[4];foreach(var a in new[]{m.Conversions,m.Slots,m.Starts,m.Strides})for(int k=0;k<4;k++)a[k]=r.ReadInt32();
                    m.InitialChannels=new float[4][];for(int k=0;k<4;k++)m.InitialChannels[k]=NativePhysicsProtocol.ReadFloats(r,m.VertexCount*4);c.Meshes[j]=m;
                }
                d.Cloths[i]=c;
            }
            foreach(var c in d.Cloths)
            {
                c.Initializations=new NativeParticleInitialization[NativePhysicsProtocol.Count(r,1024)];
                for(int i=0;i<c.Initializations.Length;i++)c.Initializations[i]=new NativeParticleInitialization{Operator=NativePhysicsProtocol.Count(r,65535),Simulation=NativePhysicsProtocol.Count(r,1024),Input=NativePhysicsProtocol.Count(r,65535),Output=NativePhysicsProtocol.Count(r,65535),Count=NativePhysicsProtocol.Count(r,1000000)};
            }
            foreach(var c in d.Cloths)
            {
                c.InitializationRoutes=new NativeParticleRoute[NativePhysicsProtocol.Count(r,1024)];
                for(int i=0;i<c.InitializationRoutes.Length;i++)c.InitializationRoutes[i]=new NativeParticleRoute{SourceSimulation=NativePhysicsProtocol.Count(r,1024),TargetSimulation=NativePhysicsProtocol.Count(r,1024),Count=NativePhysicsProtocol.Count(r,1000000)};
            }
            return d;
        }
    }
    public static class NativePhysicsProtocol
    {
        public const int Hello=0x3148504E,Frame=1,Quit=2,FrameResult=3,Failure=4;
        public static int Count(BinaryReader r,int maximum){int n=r.ReadInt32();if(n<0||n>maximum)throw new InvalidDataException("Native physics message exceeds its bounds");return n;}
        public static float[] ReadFloats(BinaryReader r,int count){var a=new float[count];for(int i=0;i<count;i++){a[i]=r.ReadSingle();if(float.IsNaN(a[i])||float.IsInfinity(a[i]))throw new InvalidDataException("Nonfinite native physics data");}return a;}
        public static void WriteFloats(BinaryWriter w,float[] a){foreach(float v in a)w.Write(v);}
    }
    public sealed class NativeRigidBodyDescription
    {
        public const int Hello=0x3152424E;
        public string[] Bones;
        public int[] Parents;
        public float[] ReferenceModel;
        public int BodyCount,ConstraintCount;
        public void Write(BinaryWriter w)
        {
            w.Write(Hello);w.Write(BodyCount);w.Write(ConstraintCount);w.Write(Bones.Length);
            for(int i=0;i<Bones.Length;i++){w.Write(Bones[i]);w.Write(Parents[i]);}
            NativePhysicsProtocol.WriteFloats(w,ReferenceModel);
        }
        public static NativeRigidBodyDescription Read(BinaryReader r)
        {
            if(r.ReadInt32()!=Hello)throw new InvalidDataException("Native rigid-body protocol mismatch");
            var d=new NativeRigidBodyDescription{BodyCount=NativePhysicsProtocol.Count(r,8192),ConstraintCount=NativePhysicsProtocol.Count(r,65535)};
            int count=NativePhysicsProtocol.Count(r,8192);if(count<1)throw new InvalidDataException("Native rigid skeleton is empty");
            d.Bones=new string[count];d.Parents=new int[count];
            for(int i=0;i<count;i++)
            {d.Bones[i]=r.ReadString();d.Parents[i]=r.ReadInt32();if(d.Bones[i].Length>1024||d.Parents[i]<-1||d.Parents[i]>=i)throw new InvalidDataException("Native rigid skeleton hierarchy");}
            d.ReferenceModel=NativePhysicsProtocol.ReadFloats(r,count*12);return d;
        }
    }
}
