using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using DSAnimStudio.NativePhysics;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    // The same pinned, isolated HCT host as Cloth, with a separate typed stream.
    // Called by one worker; disposal may cancel a stuck native read.
    public sealed class NativeRigidBodySession : IDisposable
    {
        readonly Process process;
        readonly NamedPipeServerStream pipe;
        readonly string temporary;
        Timer watchdog;
        BinaryReader reader;
        BinaryWriter writer;
        string error="",stage="Starting runtime";
        int sequence,disposed;
        public NativeRigidBodyDescription Description {get;private set;}
        public NativeRigidBodySession(byte[] originalHkx,string runtimePath=null,string helperPath=null)
        {
            runtimePath??=NativeClothSession.DefaultRuntimePath;
            helperPath??=Path.Combine(AppContext.BaseDirectory,"NativePhysics","DSA.NativePhysicsHost.exe");
            if(originalHkx==null||originalHkx.Length<32)throw new InvalidDataException("Original rigid-body HKX missing");
            if(!File.Exists(Path.Combine(runtimePath,"tools","hctPreviewPlugin.dll")))throw new FileNotFoundException("Havok Content Tools installation not found.");
            if(!File.Exists(helperPath))throw new FileNotFoundException("DSA native physics helper is missing.");
            temporary=Path.Combine(Path.GetTempPath(),"DSA.NativePhysics",Guid.NewGuid().ToString("N"));
            string name="DSA.NativePhysics."+Guid.NewGuid().ToString("N");
            pipe=new(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            process=new(){StartInfo=new(helperPath){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true,RedirectStandardOutput=true}};
            try
            {
                Directory.CreateDirectory(temporary);File.WriteAllBytes(Path.Combine(temporary,"rigid.hkx"),originalHkx);
                process.StartInfo.ArgumentList.Add(runtimePath);process.StartInfo.ArgumentList.Add(temporary);process.StartInfo.ArgumentList.Add(name);
                process.OutputDataReceived+=(_,e)=>{if(e.Data?.StartsWith("LOAD_")==true||e.Data=="NATIVE_PREVIEW_INITIALIZED")stage=e.Data;};
                process.ErrorDataReceived+=(_,e)=>{if(e.Data!=null)lock(process){error+=" "+e.Data;if(error.Length>2000)error=error[^2000..];}};
                process.Start();process.BeginOutputReadLine();process.BeginErrorReadLine();
                watchdog=new(_=>{error="Native rigid-body operation timed out at "+stage;try{if(!process.HasExited)process.Kill();}catch(InvalidOperationException){}},null,40000,Timeout.Infinite);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var connecting=pipe.WaitForConnectionAsync(timeout.Token);
                while(!connecting.Wait(100))if(process.HasExited)throw new InvalidOperationException("Native rigid-body initialization failed: "+error);
                connecting.GetAwaiter().GetResult();reader=new(new BufferedStream(pipe,65536));writer=new(new BufferedStream(pipe,65536));
                Description=NativeRigidBodyDescription.Read(reader);watchdog.Change(Timeout.Infinite,Timeout.Infinite);stage="Native rigid-body frame";
            }
            catch(Exception e){Dispose();throw new InvalidOperationException("Native rigid-body initialization failed: "+e.GetBaseException().Message+" "+error,e);}
        }
        public static Matrix MatrixAt(float[] values,int bone)
        {
            int i=bone*12;
            return Matrix.CreateScale(values[i+8],values[i+9],values[i+10])
                *Matrix.CreateFromQuaternion(Quaternion.Normalize(new(values[i+4],values[i+5],values[i+6],values[i+7])))
                *Matrix.CreateTranslation(values[i],values[i+1],values[i+2]);
        }
        public static void Store(Matrix matrix,float[] values,int bone)
        {
            if(!matrix.Decompose(out var scale,out var rotation,out var translation)||!float.IsFinite(matrix.Determinant()))throw new InvalidDataException("Rigid-body input pose cannot be decomposed");
            rotation=Quaternion.Normalize(rotation);int i=bone*12;
            values[i]=translation.X;values[i+1]=translation.Y;values[i+2]=translation.Z;values[i+3]=0;
            values[i+4]=rotation.X;values[i+5]=rotation.Y;values[i+6]=rotation.Z;values[i+7]=rotation.W;
            values[i+8]=scale.X;values[i+9]=scale.Y;values[i+10]=scale.Z;values[i+11]=0;
        }
        public float[] Step(float[] modelPose,float[] world,float dt,bool reset,bool simulate)
        {
            ObjectDisposedException.ThrowIf(disposed!=0,this);
            if(modelPose.Length!=Description.Bones.Length*12||world.Length!=12||!float.IsFinite(dt)||dt<=0||dt>.1f)throw new ArgumentException("Native rigid-body input bounds");
            foreach(float value in modelPose)if(!float.IsFinite(value))throw new ArgumentException("Nonfinite rigid-body input");
            foreach(float value in world)if(!float.IsFinite(value))throw new ArgumentException("Nonfinite rigid-body world pose");
            watchdog.Change(15000,Timeout.Infinite);
            try
            {
                writer.Write(NativePhysicsProtocol.Frame);writer.Write(++sequence);writer.Write(reset);writer.Write(simulate);writer.Write(dt);
                NativePhysicsProtocol.WriteFloats(writer,world);NativePhysicsProtocol.WriteFloats(writer,modelPose);writer.Flush();
                if(reader.ReadInt32()!=NativePhysicsProtocol.FrameResult||reader.ReadInt32()!=sequence)throw new InvalidDataException("Native rigid-body frame order mismatch");
                return NativePhysicsProtocol.ReadFloats(reader,modelPose.Length);
            }
            catch(IOException e){throw new InvalidOperationException("Native rigid-body frame failed: "+error,e);}
            finally{try{watchdog.Change(Timeout.Infinite,Timeout.Infinite);}catch(ObjectDisposedException){}}
        }
        public void Dispose()
        {
            if(Interlocked.Exchange(ref disposed,1)!=0)return;
            watchdog?.Dispose();try{pipe?.Dispose();}catch{}
            try{if(process!=null&&!process.HasExited&&!process.WaitForExit(300)){process.Kill();process.WaitForExit(1000);}}catch(InvalidOperationException){}
            process?.Dispose();
            string root=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"DSA.NativePhysics"))+Path.DirectorySeparatorChar;
            if(temporary!=null&&Path.GetFullPath(temporary).StartsWith(root,StringComparison.OrdinalIgnoreCase))try{Directory.Delete(temporary,true);}catch(IOException){}
        }
    }
}
