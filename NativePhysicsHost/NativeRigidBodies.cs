using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using DSAnimStudio.NativePhysics;

static partial class NativePhysicsHost
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RigidStepCall(IntPtr product,float dt);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void RigidPoseCall(IntPtr ragdoll,IntPtr pose,IntPtr world);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SkeletonMapCall(IntPtr mapper,IntPtr sourcePose,IntPtr targetPose,int constraintSource);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void BodyMotionTypeCall(IntPtr world,int id,int type,int cacheMode);

    // The storage belongs to this adapter. Evaluation, model/local conversion,
    // unmapped bones and chain mapping are the original hkaPose/Mapper code.
    sealed class NativePoseBuffer : IDisposable
    {
        readonly IntPtr allocation;
        public readonly IntPtr Pose,Skeleton;
        public readonly int Count;
        public NativePoseBuffer(IntPtr skeleton)
        {
            Skeleton=skeleton;Count=Marshal.ReadInt32(skeleton,0x38);
            if(Count<1||Count>8192||Marshal.ReadInt32(skeleton,0x48)!=Count||Marshal.ReadInt32(skeleton,0x28)!=Count)throw new InvalidDataException("Native rigid skeleton topology");
            int floats=Marshal.ReadInt32(skeleton,0x68);
            if(floats<0||floats>8192)throw new InvalidDataException("Native skeleton float slots");
            int n=checked(80+Count*100+floats*4+31);
            allocation=Marshal.AllocHGlobal(n);Pose=new IntPtr((allocation.ToInt64()+15)&~15L);
            Marshal.Copy(new byte[n-15],0,Pose,n-15);
            Marshal.WriteIntPtr(Pose,skeleton);
            ArrayAt(8,IntPtr.Add(Pose,80),Count);
            ArrayAt(24,IntPtr.Add(Pose,80+Count*48),Count);
            ArrayAt(40,IntPtr.Add(Pose,80+Count*96),Count);
            ArrayAt(64,IntPtr.Add(Pose,80+Count*100),floats);
            var reference=new float[Count*12];Marshal.Copy(Marshal.ReadIntPtr(skeleton,0x40),reference,0,reference.Length);SetLocal(reference);
        }
        void ArrayAt(int offset,IntPtr data,int count)
        {Marshal.WriteIntPtr(Pose,offset,data);Marshal.WriteInt32(Pose,offset+8,count);Marshal.WriteInt32(Pose,offset+12,unchecked((int)0x80000000)|count);}
        public IntPtr Model {get{return Marshal.ReadIntPtr(NativeFunction<ConstructorCall>(0x296200)(Pose));}}
        public void SetLocal(float[] values)
        {
            if(values.Length!=Count*12||!Finite(values))throw new InvalidDataException("Native local input pose");
            Marshal.Copy(values,0,Marshal.ReadIntPtr(NativeFunction<ConstructorCall>(0x296220)(Pose)),values.Length);
        }
        public void SetModel(float[] values)
        {
            if(values.Length!=Count*12||!Finite(values))throw new InvalidDataException("Native model input pose");
            Marshal.Copy(values,0,Marshal.ReadIntPtr(NativeFunction<ConstructorCall>(0x296250)(Pose)),values.Length);
        }
        public float[] ReadModel()
        {
            var result=new float[Count*12];Marshal.Copy(Model,result,0,result.Length);
            // hkVector4 padding has no translation/scale meaning and may retain
            // SIMD scratch values. Do not expose it as semantic IPC data.
            for(int i=0;i<result.Length;i+=12){result[i+3]=0;result[i+11]=0;}
            if(!Finite(result))throw new InvalidDataException("Nonfinite native rigid pose");return result;
        }
        public void Dispose(){Marshal.FreeHGlobal(allocation);}
    }

    // Uses the pinned HCT's original Ragdoll instance and both original mappers.
    // No replacement mass, constraint solver, collision shape or name heuristic.
    sealed class NativeRigidBodyBridge : IDisposable
    {
        readonly IntPtr manager,product,toRagdoll,toAnimation;
        IntPtr ragdoll;
        readonly NativePoseBuffer animation,ragPose;
        readonly IntPtr worldAllocation,worldPose;
        int[] dynamicBodies;
        readonly float[] ragdollReferenceLocal;
        int mode=-1;
        public readonly int BodyCount,ConstraintCount;
        public IntPtr Skeleton {get{return animation.Skeleton;}}
        public int BoneCount {get{return animation.Count;}}
        public int RagdollBoneCount {get{return ragPose.Count;}}
        public NativeRigidBodyBridge(IntPtr impl)
        {
            manager=Marshal.ReadIntPtr(impl,0x20);product=Marshal.ReadIntPtr(impl,0x40);
            if(Marshal.ReadIntPtr(manager,0x90)==IntPtr.Zero||Marshal.ReadInt32(manager,0x320)!=1)throw new InvalidDataException("Expected one native hknp ragdoll in this character asset");
            if(Marshal.ReadInt32(manager,0x20)!=1)throw new InvalidDataException("Rigid-body session must own its original asset");
            ragdoll=Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager,0x318));
            var ragSkeleton=Marshal.ReadIntPtr(ragdoll,0x50);var mappers=new List<IntPtr>();
            for(int i=0;i<Marshal.ReadInt32(manager,0x20);i++)
            {
                var asset=Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager,0x18),i*8);var root=Marshal.ReadIntPtr(asset,0x18);
                int count=Marshal.ReadInt32(root,8);if(count<0||count>128)throw new InvalidDataException("Native asset variant count");
                for(int j=0;j<count;j++)
                {
                    var v=IntPtr.Add(Marshal.ReadIntPtr(root),j*24);
                    var name=Marshal.PtrToStringAnsi(new IntPtr(Marshal.ReadIntPtr(v,8).ToInt64()&~1L));
                    if(name=="hkaSkeletonMapper")mappers.Add(Marshal.ReadIntPtr(v,16));
                }
            }
            foreach(var mapper in mappers)
            {
                if(Marshal.ReadIntPtr(mapper,0x28)==ragSkeleton)
                {
                    if(toRagdoll!=IntPtr.Zero)throw new InvalidDataException("Ambiguous animation to ragdoll mapper");
                    toRagdoll=mapper;
                }
                if(Marshal.ReadIntPtr(mapper,0x20)==ragSkeleton)
                {
                    if(toAnimation!=IntPtr.Zero)throw new InvalidDataException("Ambiguous ragdoll to animation mapper");
                    toAnimation=mapper;
                }
            }
            if(toRagdoll==IntPtr.Zero||toAnimation==IntPtr.Zero||Marshal.ReadIntPtr(toRagdoll,0x20)!=Marshal.ReadIntPtr(toAnimation,0x28))throw new InvalidDataException("Native bidirectional skeleton mapping missing");
            if(Marshal.ReadInt32(toRagdoll,0xC4)!=0||Marshal.ReadInt32(toAnimation,0xC4)!=0)throw new InvalidDataException("Expected original ragdoll mapping type");
            animation=new NativePoseBuffer(Marshal.ReadIntPtr(toRagdoll,0x20));ragPose=new NativePoseBuffer(ragSkeleton);
            ragdollReferenceLocal=new float[ragPose.Count*12];Marshal.Copy(Marshal.ReadIntPtr(ragSkeleton,0x40),ragdollReferenceLocal,0,ragdollReferenceLocal.Length);
            BodyCount=Marshal.ReadInt32(ragdoll,0x30);ConstraintCount=Marshal.ReadInt32(ragdoll,0x40);
            CaptureOriginalMotionTypes();
            worldAllocation=Marshal.AllocHGlobal(64);worldPose=new IntPtr((worldAllocation.ToInt64()+15)&~15L);
            Marshal.WriteByte(product,0x18,1);
        }
        void CaptureOriginalMotionTypes()
        {
            var originalDynamic=new List<int>();var nativeWorld=Marshal.ReadIntPtr(manager,0x90);
            for(int i=0;i<BodyCount;i++)
            {
                int id=Marshal.ReadInt32(Marshal.ReadIntPtr(ragdoll,0x28),i*4);
                var body=IntPtr.Add(Marshal.ReadIntPtr(nativeWorld,0x28),(id&0xFFFFFF)*0xB0);
                if((Marshal.ReadInt32(body,0x44)&7)==2)originalDynamic.Add(id);
            }
            dynamicBodies=originalDynamic.ToArray();
        }
        public float[] ReferenceModel(){return animation.ReadModel();}
        public NativeRigidBodyDescription Description()
        {
            var d=new NativeRigidBodyDescription{BodyCount=BodyCount,ConstraintCount=ConstraintCount,Bones=new string[BoneCount],Parents=new int[BoneCount],ReferenceModel=ReferenceModel()};
            for(int i=0;i<BoneCount;i++)
            {
                d.Bones[i]=Marshal.PtrToStringAnsi(new IntPtr(Marshal.ReadIntPtr(Marshal.ReadIntPtr(Skeleton,0x30),i*16).ToInt64()&~1L));
                d.Parents[i]=Marshal.ReadInt16(Marshal.ReadIntPtr(Skeleton,0x20),i*2);
            }
            return d;
        }
        public float[] Step(float[] inputModel,float[] world,float dt,bool reset,bool simulate)
        {
            if(world.Length!=12||!Finite(world)||float.IsNaN(dt)||dt<=0||dt>.1f)throw new InvalidDataException("Native rigid frame input");
            int nextMode=simulate?1:2;
            animation.SetModel(inputModel);Marshal.Copy(world,0,worldPose,12);
            if(mode!=nextMode)
            {
                mode=nextMode;reset=true;Marshal.WriteInt32(manager,0x2F0,mode);
                if(simulate)foreach(int id in dynamicBodies)NativeFunction<BodyMotionTypeCall>(0x476180)(Marshal.ReadIntPtr(manager,0x90),id,2,0);
                // Exact HCT switch: restore authored mass/constraints for
                // dynamics, or keyframe mapped bodies and disable constraints.
                NativeFunction<AssetCall>(0x45690)(manager,ragPose.Skeleton);
                if(Environment.GetEnvironmentVariable("DSA_NATIVE_RIGID_TRACE")=="1")
                {
                    var w=Marshal.ReadIntPtr(manager,0x90);int id=Marshal.ReadInt32(Marshal.ReadIntPtr(ragdoll,0x28))&0xFFFFFF;
                    var body=IntPtr.Add(Marshal.ReadIntPtr(w,0x28),id*0xB0);int motion=Marshal.ReadInt32(body,0x40);
                    Console.WriteLine("RIGID_MODE "+mode+" flags="+Marshal.ReadInt32(body,0x44)+" active="+Marshal.ReadInt32(w,0x100)+" inverse="+Marshal.ReadInt64(IntPtr.Add(Marshal.ReadIntPtr(w,0x180),motion*128),0x20).ToString("X16"));Console.Out.Flush();
                }
            }
            if(reset||!simulate)
            {
                if(reset)ragPose.SetLocal(ragdollReferenceLocal);
                NativeFunction<SkeletonMapCall>(0x29B430)(toRagdoll,animation.Pose,ragPose.Pose,2);
                // Original setPoseModelSpace also clears linear/angular velocity.
                NativeFunction<RigidPoseCall>(0x472020)(ragdoll,ragPose.Model,worldPose);
            }
            NativeFunction<RigidStepCall>(0x58800)(product,dt);
            NativeFunction<RigidPoseCall>(0x471BC0)(ragdoll,ragPose.Model,worldPose);
            NativeFunction<SkeletonMapCall>(0x29B430)(toAnimation,ragPose.Pose,animation.Pose,2);
            return animation.ReadModel();
        }
        public void Dispose(){animation.Dispose();ragPose.Dispose();Marshal.FreeHGlobal(worldAllocation);}
    }
    static unsafe void ServeRigid(Type previewType,object control,Stream pipe,byte[] originalRigid)
    {
        var impl=(IntPtr)System.Reflection.Pointer.Unbox(previewType.GetField("m_si",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance).GetValue(control));
        NativeRigidBodyBridge bridge=new NativeRigidBodyBridge(impl);
        try
        {
            var r=new BinaryReader(new BufferedStream(pipe,65536));var w=new BinaryWriter(new BufferedStream(pipe,65536));
            bridge.Description().Write(w);w.Flush();int previous=0;bool previousSimulate=false;
            while(true)
            {
                int command=r.ReadInt32();if(command==NativePhysicsProtocol.Quit)return;
                if(command!=NativePhysicsProtocol.Frame)throw new InvalidDataException("Unknown native rigid-body command");
                int sequence=r.ReadInt32();if(sequence!=previous+1)throw new InvalidDataException("Native rigid frame order");previous=sequence;
                bool reset=r.ReadBoolean(),simulate=r.ReadBoolean();float dt=r.ReadSingle();
                var world=NativePhysicsProtocol.ReadFloats(r,12);var pose=NativePhysicsProtocol.ReadFloats(r,bridge.BoneCount*12);
                if(sequence>1&&(reset||simulate!=previousSimulate))
                {
                    // Reset the native world as well as its original resource.
                    // Re-enabling an asset retains world solver caches. Dispose
                    // borrowed skeleton buffers before replacing native objects.
                    bridge.Dispose();bridge=null;
                    Call(previewType,control,"clearSceneButton_Click",null,EventArgs.Empty);
                    Marshal.WriteInt32(Manager(previewType,control),0x2F0,0);
                    Call(previewType,control,"updateContentsFromPackfile",(object)originalRigid);
                    bridge=new NativeRigidBodyBridge(impl);reset=true;
                }
                var result=bridge.Step(pose,world,dt,reset,simulate);
                previousSimulate=simulate;
                w.Write(NativePhysicsProtocol.FrameResult);w.Write(sequence);NativePhysicsProtocol.WriteFloats(w,result);w.Flush();
            }
        }
        finally{if(bridge!=null)bridge.Dispose();}
    }
}
