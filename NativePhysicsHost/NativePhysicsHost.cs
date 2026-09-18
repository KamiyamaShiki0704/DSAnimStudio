using System;
using System.IO;
using System.IO.Pipes;
using System.Collections.Generic;
using DSAnimStudio.NativePhysics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Isolated .NET Framework host: never load the mixed-mode vendor runtime into DSA.
static partial class NativePhysicsHost
{
    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AssetCall(IntPtr manager,IntPtr asset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void WorldStepCall(IntPtr world,IntPtr jobs,int threads,float dt,IntPtr scratch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void WorldCall(IntPtr world);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void StateCall(IntPtr cloth,int state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ScratchSizeCall(IntPtr world,int threads);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr AllocateCall(ulong bytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr ConstructorCall(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr StreamConstructorCall(IntPtr instance,IntPtr data,int length,int memoryType,long offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void AddAssetCall(IntPtr manager,IntPtr asset,[MarshalAs(UnmanagedType.I1)]bool allowInstances);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void MatrixProductCall(IntPtr output,IntPtr a,IntPtr b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CollidableTransformCall(IntPtr collidable,IntPtr next,IntPtr previous,float dt,[MarshalAs(UnmanagedType.I1)]bool normalize);
    static T NativeFunction<T>(int rva){return (T)(object)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(GetModuleHandle("hctPreviewPlugin.dll"),rva),typeof(T));}
    static IntPtr Manager(Type previewType,object control)
    {
        unsafe {return Marshal.ReadIntPtr((IntPtr)Pointer.Unbox(previewType.GetField("m_si",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(control)),0x20);}
    }
    static AssetCall AssetFunction(int rva){return (AssetCall)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(GetModuleHandle("hctPreviewPlugin.dll"),rva),typeof(AssetCall));}
    static void LoadClothDisabled(Type previewType,object control,byte[] bytes)
    {
        // The editor's updateContentsFromPackfile immediately enables an asset
        // before its scene exists. Load through the same native PreviewAsset
        // loader, then bind the complete scene before the first enable.
        IntPtr asset=IntPtr.Zero,stream=Marshal.AllocHGlobal(64);bool constructed=false;
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try
        {
            NativeFunction<StreamConstructorCall>(0x187B50)(stream,pinned.AddrOfPinnedObject(),bytes.Length,2,0);constructed=true;
            asset=NativeFunction<AllocateCall>(0x6DDD0)(288);
            if(asset==IntPtr.Zero)throw new OutOfMemoryException("Native Cloth asset allocation failed");
            NativeFunction<ConstructorCall>(0x5BC80)(asset);
            NativeFunction<AssetCall>(0x5C270)(asset,stream);
            if(Marshal.ReadIntPtr(asset,24)==IntPtr.Zero)throw new InvalidDataException("Native Cloth asset has no root");
            NativeFunction<AddAssetCall>(0x5D490)(Manager(previewType,control),asset,false);
        }
        finally
        {
            if(asset!=IntPtr.Zero)NativeFunction<WorldCall>(0x118E80)(asset);
            if(constructed)NativeFunction<WorldCall>(0x187C00)(stream);
            pinned.Free();Marshal.FreeHGlobal(stream);
        }
    }
    static Action BindLoadedScene(Type previewType,object control)
    {
        var manager=Manager(previewType,control);var assets=Marshal.ReadIntPtr(manager,0x18);int count=Marshal.ReadInt32(manager,0x20);
        if(count<1||count>32)throw new InvalidDataException("Asset array range");
        IntPtr clothAsset=IntPtr.Zero,clothRoot=IntPtr.Zero,sceneVariant=IntPtr.Zero;
        for(int i=0;i<count;i++)
        {
            var asset=Marshal.ReadIntPtr(assets,i*8);var rootObject=Marshal.ReadIntPtr(asset,24);
            int variants=Marshal.ReadInt32(rootObject,8);var data=Marshal.ReadIntPtr(rootObject);
            if(variants<0||variants>64)throw new InvalidDataException("Variant array range");
            for(int j=0;j<variants;j++)
            {
                var v=IntPtr.Add(data,j*24);string cn=Marshal.PtrToStringAnsi(new IntPtr(Marshal.ReadIntPtr(v,8).ToInt64()&~1L));
                Console.WriteLine("ASSET "+i+" VARIANT "+j+" "+cn);
                if(cn=="hkxScene")sceneVariant=v;
                if(cn=="hclClothContainer"){clothRoot=rootObject;clothAsset=asset;}
            }
        }
        if(clothAsset==IntPtr.Zero||sceneVariant==IntPtr.Zero)throw new InvalidDataException("Need original Cloth and a scene");
        var disable=AssetFunction(0x5D890);var enable=AssetFunction(0x5D6F0);
        var original=Marshal.ReadIntPtr(clothRoot);int oldCount=Marshal.ReadInt32(clothRoot,8),oldCapacity=Marshal.ReadInt32(clothRoot,12);
        var buffer=Marshal.AllocHGlobal((oldCount+1)*24);byte[] copy=new byte[oldCount*24];Marshal.Copy(original,copy,0,copy.Length);Marshal.Copy(copy,0,buffer,copy.Length);
        copy=new byte[24];Marshal.Copy(sceneVariant,copy,0,24);Marshal.Copy(copy,0,IntPtr.Add(buffer,oldCount*24),24);
        Marshal.WriteIntPtr(clothRoot,buffer);Marshal.WriteInt32(clothRoot,8,oldCount+1);Marshal.WriteInt32(clothRoot,12,unchecked((int)0x80000000)|(oldCount+1));
        Console.WriteLine("BIND_SCENE_TO_ORIGINAL_CLOTH");Console.Out.Flush();enable(manager,clothAsset);
        return ()=>{disable(manager,clothAsset);Marshal.WriteIntPtr(clothRoot,original);Marshal.WriteInt32(clothRoot,8,oldCount);Marshal.WriteInt32(clothRoot,12,oldCapacity);Marshal.FreeHGlobal(buffer);};
    }

    static string root;
    static object Call(Type t,object target,string method,params object[] args)
    {return t.GetMethod(method,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance).Invoke(target,args);}
    static Type TypeIn(string file,string name){return Assembly.LoadFrom(Path.Combine(root,file)).GetType(name,true);}
    static IntPtr[] Instances(Type t,object control,NativePhysicsDescription d)
    {
        var world=Marshal.ReadIntPtr(Manager(t,control),0x290);int count=Marshal.ReadInt32(world,0x20);
        if(count!=d.Cloths.Length)throw new InvalidDataException("Native cloth instance count does not match authored data: "+count+" / "+d.Cloths.Length);
        var found=new Dictionary<string,IntPtr>(StringComparer.Ordinal);
        for(int i=0;i<count;i++){var c=Marshal.ReadIntPtr(Marshal.ReadIntPtr(world,0x18),i*8);var data=Marshal.ReadIntPtr(c,0x18);string name=Marshal.PtrToStringAnsi(new IntPtr(Marshal.ReadIntPtr(data,0x18).ToInt64()&~1L));found.Add(name,c);}
        var result=new IntPtr[count];
        for(int i=0;i<count;i++)
        {
            var c=found[d.Cloths[i].Name];result[i]=c;
            if(Marshal.ReadInt32(c,0x114)!=0)throw new InvalidDataException("Native initial Cloth state differs from authored state zero");
            if(Marshal.ReadInt32(c,0x38)!=d.Cloths[i].TransformSkeletons.Length)throw new InvalidDataException("Native transform-set count mismatch");
            for(int j=0;j<d.Cloths[i].TransformSkeletons.Length;j++)
            {
                var ts=Marshal.ReadIntPtr(Marshal.ReadIntPtr(c,0x30),j*8);int bones=d.Skeletons[d.Cloths[i].TransformSkeletons[j]].Bones.Length;
                if(Marshal.ReadInt32(ts,0x20)!=bones||Marshal.ReadInt32(ts,0x30)!=bones)throw new InvalidDataException("Native skeleton topology mismatch");
            }
            foreach(var m in d.Cloths[i].Meshes)
            {
                if(m.BufferIndex>=Marshal.ReadInt32(c,0x28)||m.ReadBufferIndex>=Marshal.ReadInt32(c,0x28))throw new InvalidDataException("Native mesh buffer index");
                var b=Marshal.ReadIntPtr(Marshal.ReadIntPtr(c,0x20),m.BufferIndex*8);
                if(Marshal.ReadIntPtr(Marshal.ReadIntPtr(b),40).ToInt64()-GetModuleHandle("hctPreviewPlugin.dll").ToInt64()!=0x8763f0)throw new InvalidDataException("Native display buffer type mismatch");
            }
        }
        return result;
    }
    static float[] Channel(IntPtr buffer,int offset,int count)
    {
        // PN operators do not author a tangent channel. A reused native slot
        // may retain a non-null pointer with zero elements; it is still absent.
        var ptr=Marshal.ReadIntPtr(buffer,offset);if(ptr==IntPtr.Zero||Marshal.ReadInt32(buffer,offset+8)==0)return new float[0];
        int stride=Marshal.ReadByte(buffer,offset+12);
        if(Marshal.ReadInt32(buffer,offset+8)!=count||stride<12||stride>128)
        throw new InvalidDataException("Native vertex channel topology mismatch: offset="+offset+" stride="+stride+" count="+Marshal.ReadInt32(buffer,offset+8)+" expected="+count);
        var values=new float[count*3];for(int i=0;i<count;i++)Marshal.Copy(IntPtr.Add(ptr,i*stride),values,i*3,3);
        return values;
    }
    static bool Finite(float[] data){foreach(float v in data)if(float.IsNaN(v)||float.IsInfinity(v))return false;return true;}
    static void WritePoses(IntPtr[] instances,NativePhysicsDescription d,float[][] poses,float[][] inverse)
    {
        for(int i=0;i<instances.Length;i++)for(int j=0;j<d.Cloths[i].TransformSkeletons.Length;j++)
        {
            int index=d.Cloths[i].TransformSkeletons[j];var ts=Marshal.ReadIntPtr(Marshal.ReadIntPtr(instances[i],0x30),j*8);
            Marshal.Copy(poses[index],0,Marshal.ReadIntPtr(ts,0x18),poses[index].Length);Marshal.Copy(inverse[index],0,Marshal.ReadIntPtr(ts,0x28),inverse[index].Length);
        }
    }
    static void ResetMotionHistory(IntPtr[] instances)
    {
        // hclSimulateOperator transfers motion from simInstance+0x100 to the
        // authored transform-set bone (7FF6EE..7FF78C). Teleport/rewind must
        // begin with the new input pose as both endpoints. Particle copies
        // alone otherwise apply the preceding world displacement a second time.
        IntPtr allocation=Marshal.AllocHGlobal(80),matrix=new IntPtr((allocation.ToInt64()+15)&~15L);
        try{foreach(var cloth in instances)
        {
            int count=Marshal.ReadInt32(cloth,0x48);var sims=Marshal.ReadIntPtr(cloth,0x40);
            for(int i=0;i<count;i++)
            {
                var sim=Marshal.ReadIntPtr(sims,i*8);
                if(Marshal.ReadByte(sim,0x248)!=0)
                {
                var mapping=NativeFunction<ConstructorCall>(0x7CDC80)(sim);
                int set=Marshal.ReadInt32(mapping),bone=Marshal.ReadInt32(mapping,4);
                if(set<0||set>=Marshal.ReadInt32(cloth,0x38))throw new InvalidDataException("Native transfer-motion transform set");
                var ts=Marshal.ReadIntPtr(Marshal.ReadIntPtr(cloth,0x30),set*8);
                if(bone<0||bone>=Marshal.ReadInt32(ts,0x20))throw new InvalidDataException("Native transfer-motion bone");
                NativeFunction<AssetCall>(0x11C2A0)(IntPtr.Add(Marshal.ReadIntPtr(ts,0x18),bone*64),IntPtr.Add(sim,0x100));
                }
                // Native collision interpolation also keeps the previous body
                // pose. 7FF7FD..7FF94A maps each collidable through its authored
                // bone and local transform; synchronize both endpoints here.
                var data=Marshal.ReadIntPtr(sim,0x18);int collisionSet=Marshal.ReadInt32(data,0xA8);
                if(collisionSet<0)continue;
                if(collisionSet>=Marshal.ReadInt32(cloth,0x38))throw new InvalidDataException("Native collidable transform set");
                var transforms=Marshal.ReadIntPtr(Marshal.ReadIntPtr(cloth,0x30),collisionSet*8);
                int collidableCount=Marshal.ReadInt32(sim,0x170);
                if(collidableCount>Marshal.ReadInt32(data,0xB8)||collidableCount>Marshal.ReadInt32(data,0xC8))throw new InvalidDataException("Native collidable mapping size");
                for(int c=0;c<collidableCount;c++)
                {
                    int bone=Marshal.ReadInt32(Marshal.ReadIntPtr(data,0xB0),c*4);
                    if(bone<0||bone>=Marshal.ReadInt32(transforms,0x20))throw new InvalidDataException("Native collidable bone");
                    NativeFunction<MatrixProductCall>(0x11C8A0)(matrix,IntPtr.Add(Marshal.ReadIntPtr(transforms,0x18),bone*64),IntPtr.Add(Marshal.ReadIntPtr(data,0xC0),c*64));
                    var collidable=Marshal.ReadIntPtr(Marshal.ReadIntPtr(sim,0x168),c*8);
                    NativeFunction<CollidableTransformCall>(0x752B50)(collidable,matrix,matrix,1f/60,false);
                }
            }
        }}finally{Marshal.FreeHGlobal(allocation);}
    }
    static Action InstallInitializationCopies(IntPtr[] instances,NativePhysicsDescription description)
    {
        // Empty exporter transition states still have an authored skin/move
        // chain. Execute that chain with native CopyVertices at each simulation
        // slot for one initialization pass. The normal solver list is restored
        // immediately; no forces, constraints or game data files are changed.
        var locations=new List<IntPtr>();var originals=new List<IntPtr>();var copies=new List<IntPtr>();
        Action restore=delegate{for(int i=0;i<locations.Count;i++)Marshal.WriteIntPtr(locations[i],originals[i]);foreach(var copy in copies)Marshal.FreeHGlobal(copy);};
        try
        {
            for(int c=0;c<instances.Length;c++)foreach(var init in description.Cloths[c].Initializations)
            {
                var data=Marshal.ReadIntPtr(instances[c],0x18);
                if(init.Operator>=Marshal.ReadInt32(data,0x58)||init.Simulation>=Marshal.ReadInt32(instances[c],0x48)||init.Input>=Marshal.ReadInt32(instances[c],0x28)||init.Output>=Marshal.ReadInt32(instances[c],0x28))throw new InvalidDataException("Native initialization indices");
                var location=IntPtr.Add(Marshal.ReadIntPtr(data,0x50),init.Operator*8);var original=Marshal.ReadIntPtr(location);
                if(Marshal.ReadIntPtr(Marshal.ReadIntPtr(original),0x20).ToInt64()-GetModuleHandle("hctPreviewPlugin.dll").ToInt64()!=0x7FF620||Marshal.ReadInt32(original,0x48)!=init.Simulation)throw new InvalidDataException("Native initialization simulation layout");
                var copy=Marshal.AllocHGlobal(96);copies.Add(copy);Marshal.Copy(new byte[96],0,copy,96);
                Marshal.WriteIntPtr(copy,IntPtr.Add(GetModuleHandle("hctPreviewPlugin.dll"),0xBBBEC0));
                Marshal.WriteInt32(copy,0x48,init.Input);Marshal.WriteInt32(copy,0x4C,init.Output);Marshal.WriteInt32(copy,0x50,init.Count);
                locations.Add(location);originals.Add(original);Marshal.WriteIntPtr(location,copy);
            }
            return restore;
        }
        catch{restore();throw;}
    }
    static unsafe void InitializePreviousParticles(IntPtr[] instances,NativePhysicsDescription description)
    {
        for(int c=0;c<instances.Length;c++)foreach(var init in description.Cloths[c].Initializations)
        {
            var sim=Marshal.ReadIntPtr(Marshal.ReadIntPtr(instances[c],0x40),init.Simulation*8);
            if(Marshal.ReadInt32(sim,0x28)!=init.Count||Marshal.ReadInt32(sim,0x38)!=init.Count)throw new InvalidDataException("Native initialization particle topology");
            var current=(float*)Marshal.ReadIntPtr(sim,0x20);var previous=(float*)Marshal.ReadIntPtr(sim,0x30);
            for(int i=0;i<init.Count*4;i++)previous[i]=current[i];
        }
    }
    static void Serve(Type type,object control,NativePhysicsDescription d,Stream pipe)
    {
        var r=new BinaryReader(new BufferedStream(pipe,65536));var w=new BinaryWriter(new BufferedStream(pipe,65536));var instances=Instances(type,control,d);
        AttachUserBuffers(instances,d);
        w.Write(NativePhysicsProtocol.Hello);w.Write(instances.Length);w.Flush();
        IntPtr scratchBase=IntPtr.Zero;
        try
        {
            var world=Marshal.ReadIntPtr(Manager(type,control),0x290);
            int scratchCapacity=0;IntPtr scratch=IntPtr.Zero;bool initialized=false;
            var packedFrameNotices=new HashSet<string>();
            var pre=NativeFunction<AssetCall>(0x733710);var step=NativeFunction<WorldStepCall>(0x733A20);var post=NativeFunction<WorldCall>(0x733DD0);
            var setState=NativeFunction<StateCall>(0x736F90);
            Action<float> run=delegate(float delta){
                int bytes=NativeFunction<ScratchSizeCall>(0x733210)(world,1);
                if(bytes<0||bytes>256*1024*1024)throw new InvalidDataException("Native scratch size");
                if(scratch==IntPtr.Zero||bytes>scratchCapacity){if(scratchBase!=IntPtr.Zero)Marshal.FreeHGlobal(scratchBase);scratchBase=Marshal.AllocHGlobal(bytes+16);scratch=new IntPtr((scratchBase.ToInt64()+15)&~15L);scratchCapacity=bytes;}
                pre(world,IntPtr.Zero);step(world,IntPtr.Zero,1,delta,scratch);
            };
            while(true)
            {
                int command=r.ReadInt32();if(command==NativePhysicsProtocol.Quit)return;
                if(command!=NativePhysicsProtocol.Frame)throw new InvalidDataException("Unknown native physics command");
                int sequence=r.ReadInt32();bool reset=r.ReadBoolean();float dt=r.ReadSingle();
                if(float.IsNaN(dt)||dt<=0||dt>.1f)throw new InvalidDataException("Native step outside bounds");
                var poses=new float[d.Skeletons.Length][];var inverse=new float[poses.Length][];
                for(int i=0;i<poses.Length;i++){poses[i]=NativePhysicsProtocol.ReadFloats(r,d.Skeletons[i].Bones.Length*16);inverse[i]=NativePhysicsProtocol.ReadFloats(r,poses[i].Length);}
                // Rewind through the authored native transitions. Toggling the
                // editor asset recreates display bindings and is not a rewind.
                if(reset)initialized=false;
                if(!initialized)
                {
                    WritePoses(instances,d,poses,inverse);ResetMotionHistory(instances);
                    // Execute the asset's own skin/copy transitions. Both history
                    // and current particle positions must start at the input pose.
                    for(int phase=0;phase<2;phase++)
                    {
                        for(int i=0;i<instances.Length;i++)setState(instances[i],phase==0&&d.Cloths[i].Initializations.Length>0?0:phase==0?d.Cloths[i].PreviousInitializationState:d.Cloths[i].CurrentInitializationState);
                        WritePoses(instances,d,poses,inverse);
                        Action restore=phase==0?InstallInitializationCopies(instances,d):null;
                        try{run(dt);if(phase==0)InitializePreviousParticles(instances,d);post(world);}
                        finally{if(restore!=null)restore();}
                    }
                    RouteInitializationParticles(instances,d);
                    for(int i=0;i<instances.Length;i++)setState(instances[i],0);
                    initialized=true;
                }
                WritePoses(instances,d,poses,inverse);run(dt);
                w.Write(NativePhysicsProtocol.FrameResult);w.Write(sequence);
                for(int i=0;i<instances.Length;i++)
                {
                    for(int j=0;j<d.Cloths[i].TransformSkeletons.Length;j++)
                    {
                        int index=d.Cloths[i].TransformSkeletons[j];var ts=Marshal.ReadIntPtr(Marshal.ReadIntPtr(instances[i],0x30),j*8);
                        var matrices=new float[poses[index].Length];Marshal.Copy(Marshal.ReadIntPtr(ts,0x18),matrices,0,matrices.Length);NativePhysicsProtocol.WriteFloats(w,matrices);
                    }
                    foreach(var m in d.Cloths[i].Meshes)
                    {
                        // Game output converters pack normals/tangents for FLVER.
                        // Read their authored float shadow buffer before packing;
                        // it contains the exact native operator outputs.
                        var array=Marshal.ReadIntPtr(instances[i],0x20);var b=Marshal.ReadIntPtr(array,m.BufferIndex*8);var shadow=Marshal.ReadIntPtr(array,m.ReadBufferIndex*8);
                        userBuffers[b].Map();
                        for(int c=0;c<3;c++)
                        {
                            var channel=c==0||b==shadow?userBuffers[b].Read(c):Channel(shadow,c==1?0x40:0x58,m.VertexCount);
                            // The temporary tangent produced by some authored
                            // vertex-frame operators is nonfinite. The original
                            // output converter still writes a finite packed game
                            // vertex. Use that native result, never fabricated
                            // normals or repaired simulation positions.
                            if(c>0&&m.Conversions[c]>=2&&m.Conversions[c]<=3&&!Finite(channel))
                            {
                                channel=userBuffers[b].Read(c);string key=m.MeshIndex+":"+c;
                                if(packedFrameNotices.Add(key))Console.WriteLine("NATIVE_PACKED_VERTEX_FRAME "+key);
                            }
                            for(int n=0;n<channel.Length;n++)if(float.IsNaN(channel[n])||float.IsInfinity(channel[n]))
                                throw new InvalidDataException("Nonfinite native channel: mesh="+m.MeshIndex+" channel="+c+" vertex="+(n/3)+" conversion="+m.Conversions[c]+" buffer="+m.BufferIndex+" shadow="+m.ReadBufferIndex);
                            w.Write(channel.Length);NativePhysicsProtocol.WriteFloats(w,channel);
                        }
                    }
                }
                post(world);w.Flush();
            }
        }
        finally{ReleaseUserBuffers();if(scratchBase!=IntPtr.Zero)Marshal.FreeHGlobal(scratchBase);}
    }
    static unsafe void RouteInitializationParticles(IntPtr[] instances,NativePhysicsDescription description)
    {
        for(int c=0;c<instances.Length;c++)foreach(var route in description.Cloths[c].InitializationRoutes)
        {
            int count=Marshal.ReadInt32(instances[c],0x48);var sims=Marshal.ReadIntPtr(instances[c],0x40);
            if(route.SourceSimulation>=count||route.TargetSimulation>=count)throw new InvalidDataException("Native initialization route index");
            var source=Marshal.ReadIntPtr(sims,route.SourceSimulation*8);var target=Marshal.ReadIntPtr(sims,route.TargetSimulation*8);
            if(Marshal.ReadInt32(source,0x28)!=route.Count||Marshal.ReadInt32(target,0x28)!=route.Count||Marshal.ReadInt32(target,0x38)!=route.Count)throw new InvalidDataException("Native initialization route topology");
            var input=(float*)Marshal.ReadIntPtr(source,0x20);var current=(float*)Marshal.ReadIntPtr(target,0x20);var previous=(float*)Marshal.ReadIntPtr(target,0x30);
            for(int i=0;i<route.Count*4;i++)current[i]=previous[i]=input[i];
        }
    }
    [STAThread] static int Main(string[] args)
    {
        if(args.Length!=3)return 2;
        SetErrorMode(0x8003); root=Path.GetFullPath(args[0]);
        using(var sha=System.Security.Cryptography.SHA256.Create())
        {
            string hash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(Path.Combine(root,"tools/hctPreviewPlugin.dll")))).Replace("-","");
            if(hash!="83E33363FBD320BB89613521633E5EDD4F5169D7501012426A850F982F025F75")throw new InvalidDataException("Unsupported HCT native layout");
        }
        Environment.SetEnvironmentVariable("PATH",root+";"+Path.Combine(root,"bin")+";"+Path.Combine(root,"tools")+";"+Path.Combine(root,"tools/gac")+";"+Environment.GetEnvironmentVariable("PATH"));
        AppDomain.CurrentDomain.AssemblyResolve+=(s,e)=>{
            string name=new AssemblyName(e.Name).Name+".dll";
            foreach(string dir in new[]{"tools/gac","tools","bin",""}){string p=Path.Combine(root,dir,name);if(File.Exists(p))return Assembly.LoadFrom(p);}
            return null;
        };
        try
        {
            var graphicsControlType=TypeIn("tools/gac/Havok.Tool.UI.dll","Havok.Tool.GraphicsControl");
            Console.WriteLine("CREATE_HIDDEN_GRAPHICS_CONTEXT");Console.Out.Flush();
            var graphicsControl=Activator.CreateInstance(graphicsControlType,new object[]{"Direct3D11",IntPtr.Zero,null,null});
            var baseType=TypeIn("tools/gac/Havok.Tool.Interfaces.dll","Havok.Tool.ToolBaseSystem");
            Console.WriteLine("BASE " + Call(baseType,null,"init"));
            var graphics=TypeIn("tools/gac/Havok.Graphics.dll","Havok.Graphics.hkgBaseSystemCLR");
            var sync=Call(graphics,null,"getBaseSyncInfoAsStaticPtr");
            var system=TypeIn("tools/gac/Havok.Graphics.dll","Havok.Graphics.hkgSystemCLR");
            var graphicsInfo=Call(system,null,"getGraphicsInfoAsStaticPtr");
            var previewType=TypeIn("tools/hctPreviewPlugin.dll","PreviewPlugin.hctPreviewControl");
            var control=Activator.CreateInstance(previewType);
            Call(previewType,control,"initBaseSystem",sync,graphicsInfo);
            var toolbar=new ToolStrip();
            toolbar.Items.Add(new ToolStripButton(){Name="toolStripFlyModeButton"});
            toolbar.Items.Add(new ToolStripButton(){Name="toolStripFirstPersonButton"});
            Call(previewType,control,"setNavigationToolBar",toolbar);
            Call(previewType,control,"setParentMenu",new MenuStrip());
            Call(previewType,control,"setParentControl",new Form(){ShowInTaskbar=false},null);
            Call(previewType,control,"init");
            var window=graphicsControlType.GetField("m_windowHKG").GetValue(graphicsControl);
            var displayWorld=graphicsControlType.GetField("m_displayWorldHKG").GetValue(graphicsControl);
            var context=Call(window.GetType(),window,"getContext");
            Call(previewType,control,"setContext",displayWorld,context,window);
            Console.WriteLine("NATIVE_PREVIEW_INITIALIZED");Console.Out.Flush();

            Action restoreScene=null;
            try
            {
                if(File.Exists(Path.Combine(args[1],"rigid.hkx")))
                {
                    Console.WriteLine("LOAD_ORIGINAL_RIGID_BODY");Console.Out.Flush();
                    var originalRigid=File.ReadAllBytes(Path.Combine(args[1],"rigid.hkx"));
                    Call(previewType,control,"updateContentsFromPackfile",(object)originalRigid);
                    using(var pipe=new NamedPipeClientStream(".",args[2],PipeDirection.InOut,PipeOptions.None))
                    {pipe.Connect(30000);ServeRigid(previewType,control,pipe,originalRigid);}
                }
                else
                {
                NativePhysicsDescription description;
                using(var reader=new BinaryReader(File.OpenRead(Path.Combine(args[1],"description.bin"))))description=NativePhysicsDescription.Read(reader);
                Console.WriteLine("LOAD_ADAPTER_SCENE");Console.Out.Flush();
                Call(previewType,control,"updateContentsFromPackfile",(object)File.ReadAllBytes(Path.Combine(args[1],"scene.hkx")));
                Console.WriteLine("LOAD_ORIGINAL_CLOTH_DISABLED");Console.Out.Flush();
                LoadClothDisabled(previewType,control,File.ReadAllBytes(Path.Combine(args[1],"cloth.hkx")));
                Console.WriteLine("BIND_NATIVE_SCENE");Console.Out.Flush();
                restoreScene=BindLoadedScene(previewType,control);
                using(var pipe=new NamedPipeClientStream(".",args[2],PipeDirection.InOut,PipeOptions.None))
                {pipe.Connect(30000);Serve(previewType,control,description,pipe);}
                }
            }
            finally{ReleaseUserBuffers();if(restoreScene!=null)restoreScene();            Call(previewType,control,"quit");
            Call(previewType,control,"quitBaseSystem");
            ((IDisposable)control).Dispose();
            Call(baseType,null,"quitBaseSystem");
            Call(graphicsControlType,graphicsControl,"Cleanup");
            ((IDisposable)graphicsControl).Dispose();
}
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
