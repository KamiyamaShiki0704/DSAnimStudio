using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using DSAnimStudio.NativePhysics;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    public sealed class NativeClothResult
    {
        public float[][][] Transforms;
        public float[][][][] Meshes;
        public float[][] Input;
        public int Sequence;
    }
    // Access only from the physics worker. Dispose also closes a stalled pipe
    // from the UI thread, then terminates only this owned helper if necessary.
    public sealed class NativeClothSession : IDisposable
    {
        readonly NativePhysicsDescription description;
        readonly NamedPipeServerStream pipe;
        readonly Process process;
        readonly string temporary;
        Timer watchdog;
        BinaryReader reader;BinaryWriter writer;
        int sequence,disposed;
        Vector3 simulationOrigin;
        readonly float[][] localPose,inversePose;
        string error="",progress="",stage="",trace="";
        public string DiagnosticError=>stage+" "+error+" "+trace;
        public int ProcessId=>process.Id;
        // Resolution order: the DSA_HAVOK_CONTENT_TOOLS environment variable, then
        // the HavokContentToolsPath setting in the configuration file. No install
        // path is hardcoded, so nothing machine-specific ships in the source.
        public static string DefaultRuntimePath
        {
            get
            {
                string environment=Environment.GetEnvironmentVariable("DSA_HAVOK_CONTENT_TOOLS");
                if(!string.IsNullOrWhiteSpace(environment))return environment;
                string configured=Main.Config?.HavokContentToolsPath;
                return string.IsNullOrWhiteSpace(configured)?null:configured;
            }
        }
        public NativeClothSession(NativeClothAsset asset,string runtimePath=null,string helperPath=null)
        {
            description=asset.Description;
            localPose=description.Skeletons.Select(s=>new float[s.Bones.Length*16]).ToArray();
            inversePose=description.Skeletons.Select(s=>new float[s.Bones.Length*16]).ToArray();
            runtimePath??=DefaultRuntimePath;helperPath??=Path.Combine(AppContext.BaseDirectory,"NativePhysics","DSA.NativePhysicsHost.exe");
            if(string.IsNullOrWhiteSpace(runtimePath)||!File.Exists(Path.Combine(runtimePath,"tools","hctPreviewPlugin.dll")))throw new FileNotFoundException("Havok Content Tools installation not found. Set HavokContentToolsPath in the configuration or the DSA_HAVOK_CONTENT_TOOLS environment variable to its folder.");
            if(!File.Exists(helperPath))throw new FileNotFoundException("DSA native physics helper is missing.");
            temporary=Path.Combine(Path.GetTempPath(),"DSA.NativePhysics",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
            string pipeName="DSA.NativePhysics."+Guid.NewGuid().ToString("N");
            pipe=new(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            process=new Process{StartInfo=new ProcessStartInfo(helperPath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true,RedirectStandardOutput=true}};
            try
            {
                File.WriteAllBytes(Path.Combine(temporary,"cloth.hkx"),asset.Cloth);File.WriteAllBytes(Path.Combine(temporary,"scene.hkx"),asset.Scene);
                using(var w=new BinaryWriter(File.Create(Path.Combine(temporary,"description.bin"))))description.Write(w);
                process.StartInfo.ArgumentList.Add(runtimePath);process.StartInfo.ArgumentList.Add(temporary);process.StartInfo.ArgumentList.Add(pipeName);
                process.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)lock(process){error=(error+" "+e.Data);if(error.Length>2000)error=error[^2000..];}};
                process.OutputDataReceived+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.Data))lock(process){progress=e.Data.Length>240?e.Data[..240]:e.Data;trace+=progress+"\n";if(trace.Length>2000)trace=trace[^2000..];if(progress.StartsWith("LOAD_")||progress.StartsWith("BIND_")||progress=="NATIVE_PREVIEW_INITIALIZED")stage=progress;}};process.Start();process.BeginErrorReadLine();process.BeginOutputReadLine();
                // Covers the handshake as well as pipe connection: a native
                // loader failure must not leave an immortal hidden worker.
                watchdog=new Timer(_=>{error="Native physics operation timed out. Last stage: "+progress;try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}},null,40000,Timeout.Infinite);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var connect=pipe.WaitForConnectionAsync(timeout.Token);
                while(!connect.Wait(100))if(process.HasExited)throw new InvalidOperationException("Native physics initialization failed: "+error);
                connect.GetAwaiter().GetResult();reader=new(new BufferedStream(pipe,65536));writer=new(new BufferedStream(pipe,65536));
                if(reader.ReadInt32()!=NativePhysicsProtocol.Hello||reader.ReadInt32()!=description.Cloths.Length)throw new InvalidDataException("Native physics handshake mismatch");
                watchdog.Change(Timeout.Infinite,Timeout.Infinite);
            }
            catch(Exception e){Dispose();throw new InvalidOperationException("Native physics initialization failed: "+e.GetBaseException().Message+" "+DiagnosticError,e);}
        }
        public static Matrix MatrixAt(float[] v,int i)=>new(v[i],v[i+1],v[i+2],v[i+3],v[i+4],v[i+5],v[i+6],v[i+7],v[i+8],v[i+9],v[i+10],v[i+11],v[i+12],v[i+13],v[i+14],v[i+15]);
        public static void Store(Matrix m,float[] v,int i)
        {v[i]=m.M11;v[i+1]=m.M12;v[i+2]=m.M13;v[i+3]=m.M14;v[i+4]=m.M21;v[i+5]=m.M22;v[i+6]=m.M23;v[i+7]=m.M24;v[i+8]=m.M31;v[i+9]=m.M32;v[i+10]=m.M33;v[i+11]=m.M34;v[i+12]=m.M41;v[i+13]=m.M42;v[i+14]=m.M43;v[i+15]=m.M44;}
        public NativeClothResult Step(float[][] pose,float dt,bool reset)
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);watchdog.Change(15000,Timeout.Infinite);
            try{return StepCore(pose,dt,reset);}
            catch(IOException e)
            {
                try{if(process.WaitForExit(1000))error+=" Native host exit: "+process.ExitCode;}catch(InvalidOperationException){}
                throw new InvalidOperationException("Native physics frame failed: "+DiagnosticError,e);
            }
            finally{try{watchdog.Change(Timeout.Infinite,Timeout.Infinite);}catch(ObjectDisposedException){}}
        }
        NativeClothResult StepCore(float[][] pose,float dt,bool reset)
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);
            if(pose.Length!=description.Skeletons.Length)throw new ArgumentException("Skeleton count");
            if(reset||sequence==0)
            {
                // A fixed origin for each uninterrupted simulation preserves
                // root-motion inertia while avoiding large world coordinates in
                // native compressed mesh deformers. Rebase only on reset.
                int s=Array.FindIndex(description.Skeletons,x=>x.Bones.Length>0);
                simulationOrigin=s<0?Vector3.Zero:MatrixAt(pose[s],0).Translation-MatrixAt(description.Skeletons[s].ReferenceMatrices,0).Translation;
            }
            writer.Write(NativePhysicsProtocol.Frame);writer.Write(++sequence);writer.Write(reset);writer.Write(dt);
            for(int s=0;s<pose.Length;s++)
            {
                if(pose[s].Length!=description.Skeletons[s].Bones.Length*16)throw new ArgumentException("Pose count");
                for(int i=0;i<pose[s].Length;i+=16)
                {
                    var m=MatrixAt(pose[s],i);m.Translation-=simulationOrigin;
                    if(!float.IsFinite(m.Determinant())||Math.Abs(m.Determinant())<1e-12)throw new InvalidDataException("Singular physics input matrix");
                    Store(m,localPose[s],i);Store(Matrix.Transpose(Matrix.Invert(m)),inversePose[s],i);
                }
                NativePhysicsProtocol.WriteFloats(writer,localPose[s]);NativePhysicsProtocol.WriteFloats(writer,inversePose[s]);
            }
            writer.Flush();
            if(reader.ReadInt32()!=NativePhysicsProtocol.FrameResult||reader.ReadInt32()!=sequence)throw new InvalidDataException("Native physics frame order mismatch");
            var result=new NativeClothResult{Sequence=sequence,Input=pose,Transforms=new float[description.Cloths.Length][][],Meshes=new float[description.Cloths.Length][][][]};
            for(int c=0;c<description.Cloths.Length;c++)
            {
                var desc=description.Cloths[c];result.Transforms[c]=desc.TransformSkeletons.Select(s=>NativePhysicsProtocol.ReadFloats(reader,description.Skeletons[s].Bones.Length*16)).ToArray();
                foreach(var transforms in result.Transforms[c])for(int i=0;i<transforms.Length;i+=16)
                {transforms[i+12]+=simulationOrigin.X;transforms[i+13]+=simulationOrigin.Y;transforms[i+14]+=simulationOrigin.Z;}
                result.Meshes[c]=new float[desc.Meshes.Length][][];
                for(int m=0;m<desc.Meshes.Length;m++)
                {
                    result.Meshes[c][m]=new float[3][];
                    for(int channel=0;channel<3;channel++)
                    {
                        int count=NativePhysicsProtocol.Count(reader,desc.Meshes[m].VertexCount*3);if(count!=0&&count!=desc.Meshes[m].VertexCount*3)throw new InvalidDataException("Native vertex count mismatch");
                        try{result.Meshes[c][m][channel]=NativePhysicsProtocol.ReadFloats(reader,count);}
                        catch(InvalidDataException e){throw new InvalidDataException("Native mesh "+desc.Meshes[m].MeshIndex+" channel "+channel+": "+e.Message,e);}
                    }
                    if(result.Meshes[c][m][0].Length==0)throw new InvalidDataException("Missing native mesh positions");
                    var positions=result.Meshes[c][m][0];
                    foreach(int vertex in desc.Meshes[m].WrittenVertices)
                    {int i=vertex*3;positions[i]+=simulationOrigin.X;positions[i+1]+=simulationOrigin.Y;positions[i+2]+=simulationOrigin.Z;}
                }
            }
            return result;
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            watchdog?.Dispose();
            // Closing the owned pipe also cancels a blocked worker read.
            try{pipe?.Dispose();}catch{}
            try{if(process!=null&&!process.HasExited&&!process.WaitForExit(300)){process.Kill();process.WaitForExit(1000);}}catch(InvalidOperationException){}
            process?.Dispose();
            if(temporary!=null)
            {
                string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DSA.NativePhysics"))+Path.DirectorySeparatorChar;
                if(Path.GetFullPath(temporary).StartsWith(root,StringComparison.OrdinalIgnoreCase))try{Directory.Delete(temporary,true);}catch(IOException){}
            }
        }
    }
}
