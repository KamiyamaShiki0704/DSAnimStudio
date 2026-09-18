using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using SoulsAssetPipeline;

namespace DSAnimStudio
{
    public sealed class NativeRigidBodyPreview : IDisposable
    {
        // Both the animation loop and viewport prepare this instance. Keep one
        // owner of its pending native frame, as with the Cloth submission path.
        readonly object previewGate=new();
        byte[] source;
        bool supported,enabled,simulate,disposed,failed;
        Model owner;
        NativeRigidBodySession session;
        Task<NativeRigidBodySession> starting;
        Task<float[]> pending;
        int generation,pendingGeneration;
        int[] modelIndices;
        Matrix[] reference,inverseReference,inverseModelReference,lastInput;
        readonly Dictionary<int,Matrix> outputs=new();
        float submittedTime=float.NaN;
        string submittedClip;
        Matrix submittedWorld;
        Vector3 originShift;
        public string Status {get;private set;}="No original rigid-body HKX in this model binder.";
        public bool HasSource=>source!=null;
        public bool IsBusy=>enabled&&!failed&&(session==null||pending!=null);
        public int DeformedBoneCount=>owner?.SkeletonFlver?.RigidDeformedBoneCount??0;
        public bool Enabled
        {
            get=>enabled;
            set
            {
                lock(previewGate)
                {
                    if(enabled==value)return;enabled=value;failed=false;Reset();
                    if(!value){Release();Status="Native rigid-body preview disabled.";}
                }
            }
        }
        public bool Simulate
        {
            get=>simulate;
            set{lock(previewGate){if(simulate==value)return;simulate=value;Reset();}}
        }
        public void SetSource(byte[] bytes, SoulsGames game)
        {
            source=bytes;supported=game is SoulsGames.ER or SoulsGames.ERNR or SoulsGames.SDT or SoulsGames.DS3;
            Status=supported?"Original rigid-body data found. Enable to load the native world.":"This game's rigid-body format is not yet adapted to the installed native runtime.";
        }
        void Bind()
        {
            var d=session.Description;var bones=owner.SkeletonFlver.Bones;
            int count=d.Bones.Length;modelIndices=new int[count];reference=new Matrix[count];inverseReference=new Matrix[count];
            inverseModelReference=new Matrix[count];lastInput=new Matrix[count];
            for(int i=0;i<count;i++)
            {
                modelIndices[i]=bones.FindIndex(b=>b.Name==d.Bones[i]);
                reference[i]=NativeRigidBodySession.MatrixAt(d.ReferenceModel,i);inverseReference[i]=Matrix.Invert(reference[i]);
                if(modelIndices[i]>=0)inverseModelReference[i]=Matrix.Invert(bones[modelIndices[i]].ReferenceFKMatrix);
            }
        }
        bool SameInput()
        {
            for(int i=0;i<modelIndices.Length;i++)if(modelIndices[i]>=0&&lastInput[i]!=owner.SkeletonFlver.Bones[modelIndices[i]].FKMatrix)return false;
            return true;
        }
        float[] Capture()
        {
            var result=new float[reference.Length*12];var d=session.Description;
            for(int i=0;i<reference.Length;i++)
            {
                int index=modelIndices[i];Matrix value;
                if(index>=0)
                {
                    lastInput[i]=owner.SkeletonFlver.Bones[index].FKMatrix;
                    value=reference[i]*inverseModelReference[i]*lastInput[i];
                }
                else
                {
                    int parent=d.Parents[i];value=parent<0?reference[i]:reference[i]*inverseReference[parent]*NativeRigidBodySession.MatrixAt(result,parent);
                }
                NativeRigidBodySession.Store(value,result,i);
            }
            return result;
        }
        void Apply(float[] result)
        {
            outputs.Clear();
            if(simulate)
            {
                for(int i=0;i<modelIndices.Length;i++)if(modelIndices[i]>=0)
                {
                    int index=modelIndices[i];outputs[index]=owner.SkeletonFlver.Bones[index].ReferenceFKMatrix*inverseReference[i]*NativeRigidBodySession.MatrixAt(result,i);
                }
            }
            owner.SkeletonFlver.SetRigidBoneTransforms(outputs);
            Status=$"Native Havok Physics: {session.Description.BodyCount} bodies / {session.Description.ConstraintCount} constraints. "+(simulate?"Ragdoll dynamics drive the model.":"Bodies follow the animation.");
        }
        public void Prepare(Model model,float time,string clip)
        {
            lock(previewGate)PrepareCore(model,time,clip);
        }
        void PrepareCore(Model model,float time,string clip)
        {
            if(!enabled||disposed||failed||!float.IsFinite(time))return;
            owner=model;
            try
            {
                if(source==null||!supported)throw new InvalidOperationException("Original hknp character data for this game is unavailable. Cloth has a separate runtime path.");
                if(session==null)
                {
                    if(starting==null){var bytes=source;starting=Task.Run(()=>new NativeRigidBodySession(bytes));Status="Starting native rigid-body world...";return;}
                    if(!starting.IsCompleted)return;session=starting.GetAwaiter().GetResult();starting=null;Bind();
                }
                if(pending!=null)
                {
                    if(!pending.IsCompleted)return;
                    var result=pending.GetAwaiter().GetResult();pending=null;
                    if(pendingGeneration==generation&&clip==submittedClip&&time>=submittedTime&&time-submittedTime<=.1f)Apply(result);
                }
                var world=model.CurrentTransform.WorldMatrix*Matrix.CreateTranslation(-originShift);
                if(time==submittedTime&&clip==submittedClip&&world==submittedWorld&&SameInput())return;
                bool reset=!float.IsFinite(submittedTime)||time<submittedTime||time-submittedTime>.1f||clip!=submittedClip
                    ||time==submittedTime&&world!=submittedWorld;
                float dt=reset?1f/60:Math.Clamp(time-submittedTime,1f/240,.1f);
                var input=Capture();var worldPose=new float[12];NativeRigidBodySession.Store(world,worldPose,0);
                var active=session;bool dynamics=simulate;pendingGeneration=generation;
                submittedTime=time;submittedClip=clip;submittedWorld=world;
                pending=RunSteps(active,input,worldPose,dt,reset,dynamics);
            }
            catch(Exception e){failed=true;Status="Native rigid-body preview unavailable: "+e.GetBaseException().Message;Clear();Release();}
        }
        static Task<float[]> RunSteps(NativeRigidBodySession active,float[] input,float[] worldPose,float dt,bool reset,bool dynamics)
        {
            // Allocate the worker closure only when a new frame is submitted.
            return Task.Run(()=>
            {
                int steps=reset?1:Math.Max(1,(int)Math.Ceiling(dt*60));float[] result=null;
                for(int i=0;i<steps;i++)result=active.Step(input,worldPose,dt/steps,reset&&i==0,dynamics);
                return result;
            });
        }
        public void ShiftWorld(Vector3 shift){lock(previewGate)originShift+=shift;}
        void Clear(){owner?.SkeletonFlver?.ClearRigidBoneTransforms();owner?.Cloth?.Reset();}
        public void Reset()
        {
            lock(previewGate)
            {
                if(failed){Release();failed=false;}
                generation++;submittedTime=float.NaN;Clear();
            }
        }
        void Release()
        {
            if(pending!=null){pending.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);pending=null;}
            if(session!=null){var active=session;session=null;Task.Run(active.Dispose);}
            if(starting!=null)
            {
                starting.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();else _=t.Exception;},TaskScheduler.Default);starting=null;
            }
            modelIndices=null;reference=inverseReference=inverseModelReference=lastInput=null;outputs.Clear();
        }
        public void Dispose(){lock(previewGate){if(disposed)return;disposed=true;enabled=false;Clear();Release();source=null;}}
    }
}
