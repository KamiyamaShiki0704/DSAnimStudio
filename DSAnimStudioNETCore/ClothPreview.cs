using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    sealed class ClothCollisionPoseHistory
    {
        readonly ClothCollisionSolver solver;
        readonly string[] names;
        readonly Dictionary<string,int> indices=new(StringComparer.Ordinal);
        readonly Matrix?[] start,end,sampled,lastStep;
        readonly Func<string,Matrix?> sampleDelta,lastDelta;
        Matrix startWorld,endWorld,lastWorld;
        Func<string,Matrix?> reference;
        bool initialized,hasStep;
        public ClothCollisionPoseHistory(ClothCollisionDefinition data,ClothCollisionSolver solver)
        {
            this.solver=solver;names=data.Colliders.Select(c=>c.Bone).Where(n=>!string.IsNullOrEmpty(n)).Distinct().ToArray();
            start=new Matrix?[names.Length];end=new Matrix?[names.Length];sampled=new Matrix?[names.Length];lastStep=new Matrix?[names.Length];
            for(int i=0;i<names.Length;i++)indices[names[i]]=i;
            sampleDelta=name=>indices.TryGetValue(name,out int i)?sampled[i]:null;
            lastDelta=name=>indices.TryGetValue(name,out int i)?lastStep[i]:null;
        }
        public void Reset(){initialized=hasStep=false;solver.Reset();}
        public void Begin(Matrix world,Func<string,Matrix?> delta,Func<string,Matrix?> referenceBone)
        {
            reference=referenceBone;startWorld=initialized?endWorld:world;endWorld=world;
            for(int i=0;i<names.Length;i++){start[i]=end[i];end[i]=delta?.Invoke(names[i]);if(!initialized)start[i]=end[i];}
            initialized=true;
            if(!hasStep)Sample(1);
        }
        static Matrix Interpolate(Matrix a,Matrix b,float t)
        {
            if(a==b||t<=0)return a;if(t>=1)return b;
            if(!a.Decompose(out var sa,out var ra,out var ta)||!b.Decompose(out var sb,out var rb,out var tb))return Matrix.Lerp(a,b,t);
            return Matrix.CreateScale(Vector3.Lerp(sa,sb,t))*Matrix.CreateFromQuaternion(Quaternion.Slerp(ra,rb,t))*Matrix.CreateTranslation(Vector3.Lerp(ta,tb,t));
        }
        public void Sample(float fraction)
        {
            for(int i=0;i<names.Length;i++)sampled[i]=start[i].HasValue&&end[i].HasValue?Interpolate(start[i].Value,end[i].Value,fraction):end[i];
            lastWorld=Interpolate(startWorld,endWorld,fraction);solver.Prepare(sampleDelta,lastWorld,reference);
            Array.Copy(sampled,lastStep,sampled.Length);hasStep=true;
        }
        public void ShiftWorld(Vector3 shift)
        {
            if(!initialized)return;
            startWorld.Translation+=shift;endWorld.Translation+=shift;lastWorld.Translation+=shift;
            if(hasStep){solver.Reset();solver.Prepare(lastDelta,lastWorld,reference);}
        }
    }

    public sealed class ClothPreviewSolver
    {
        public readonly ClothPreviewDefinition Definition;
        public readonly Vector3[] Positions;
        public readonly ClothCollisionSolver BodyCollisions;
        public bool WillReset(float nextTime,string animation)=>float.IsNaN(time)||clip!=animation||nextTime<time-1e-6f||nextTime-time>.25f;
        readonly ClothCollisionPoseHistory collisionMotion;
        readonly Vector3[] previous,anchors,frameAnchors,anchorNormals,frameNormals;
        float time=float.NaN,accumulator;string clip;
        public ClothPreviewSolver(ClothPreviewDefinition definition)
        {
            Definition=definition;Positions=new Vector3[definition.RestPositions.Length];previous=new Vector3[Positions.Length];
            anchors=new Vector3[Positions.Length];frameAnchors=new Vector3[Positions.Length];
            if(definition.Initialization!=null){anchorNormals=new Vector3[Positions.Length];frameNormals=new Vector3[Positions.Length];}
            if(definition.Collisions is {Colliders.Length:>0})
            {BodyCollisions=new(definition.Collisions);collisionMotion=new(definition.Collisions,BodyCollisions);}
        }
        public void Reset(){time=float.NaN;accumulator=0;clip=null;collisionMotion?.Reset();}
        public void ShiftWorld(Vector3 shift)
        {
            if(float.IsNaN(time))return;
            for(int i=0;i<Positions.Length;i++){Positions[i]+=shift;previous[i]+=shift;anchors[i]+=shift;frameAnchors[i]+=shift;}
            collisionMotion?.ShiftWorld(shift);
        }
        public void Advance(float nextTime,string animation,Matrix modelWorld,Func<string,Matrix?> boneDelta,Func<string,Matrix?> referenceBone=null)
        {
            if(!float.IsFinite(nextTime))return;
            var d=Definition;
            if(d.Initialization!=null)
            {
                float s=modelWorld.Right.Length();
                if(!float.IsFinite(s)||s<1e-7f||Math.Abs(modelWorld.Up.Length()-s)>s*.001f||Math.Abs(modelWorld.Backward.Length()-s)>s*.001f
                    ||Math.Abs(Vector3.Dot(modelWorld.Right,modelWorld.Up))>s*s*.001f||Math.Abs(Vector3.Dot(modelWorld.Right,modelWorld.Backward))>s*s*.001f||Math.Abs(Vector3.Dot(modelWorld.Up,modelWorld.Backward))>s*s*.001f)
                    throw new InvalidOperationException("Cloth range constraints require a finite uniform model scale without shear.");
            }
            var worldNormal=d.Initialization!=null?Matrix.Transpose(Matrix.Invert(modelWorld)):Matrix.Identity;
            for(int i=0;i<Positions.Length;i++)
            {
                var rest=d.RestPositions[i];var weights=d.Initialization?.Bindings[i]??d.Bindings[i];Vector3 sum=Vector3.Zero,normal=Vector3.Zero;float total=0;
                if(weights!=null)for(int w=0;w<weights.Length;w++)
                {
                    var delta=boneDelta?.Invoke(weights[w].Bone);
                    if(!delta.HasValue){if(d.Initialization!=null)throw new InvalidOperationException("Cloth initialization bone is unavailable: "+weights[w].Bone);continue;}
                    if(d.Initialization!=null&&(!float.IsFinite(delta.Value.Determinant())||Math.Abs(delta.Value.Determinant())<1e-12f))
                        throw new InvalidOperationException("Cloth initialization bone is not invertible: "+weights[w].Bone);
                    sum+=Vector3.Transform(rest,delta.Value)*weights[w].Weight;total+=weights[w].Weight;
                    if(anchorNormals!=null&&d.Initialization.ReferenceNormals[i] is Vector3[] ns&&w<ns.Length)
                        normal+=Vector3.TransformNormal(ns[w],Matrix.Transpose(Matrix.Invert(delta.Value)));
                }
                anchors[i]=Vector3.Transform(total>1e-6f?sum/total:rest,modelWorld);
                if(anchorNormals!=null)anchorNormals[i]=BulletMath.Unit(Vector3.TransformNormal(normal,worldNormal),Vector3.Up);
            }
            if(WillReset(nextTime,animation))
            {
                collisionMotion?.Reset();collisionMotion?.Begin(modelWorld,boneDelta,referenceBone);
                System.Array.Copy(anchors,Positions,Positions.Length);System.Array.Copy(anchors,previous,Positions.Length);
                System.Array.Copy(anchors,frameAnchors,Positions.Length);if(anchorNormals!=null)System.Array.Copy(anchorNormals,frameNormals,Positions.Length);
                time=nextTime;clip=animation;accumulator=0;return;
            }
            collisionMotion?.Begin(modelWorld,boneDelta,referenceBone);
            float elapsed=Math.Max(0,nextTime-time);accumulator+=elapsed;time=nextTime;const float step=1f/120;
            float damping=MathF.Pow(d.Damping,step),scale=(modelWorld.Right.Length()+modelWorld.Up.Length()+modelWorld.Backward.Length())/3;
            while(elapsed>0&&accumulator+1e-7f>=step)
            {
                accumulator-=step;
                float fraction=Math.Clamp(1-accumulator/elapsed,0,1);
                for(int i=0;i<Positions.Length;i++)
                {
                    if(d.Fixed[i]||d.InverseMass[i]<=0){Positions[i]=previous[i]=Vector3.Lerp(frameAnchors[i],anchors[i],fraction);continue;}
                    var p=Positions[i];Positions[i]+=(p-previous[i])*damping+d.Gravity*(step*step);previous[i]=p;
                }
                for(int iteration=0;iteration<8;iteration++)foreach(var link in d.Links)
                {
                    float wa=d.Fixed[link.A]?0:d.InverseMass[link.A],wb=d.Fixed[link.B]?0:d.InverseMass[link.B];if(wa+wb<=0)continue;
                    var delta=Positions[link.B]-Positions[link.A];float length=delta.Length(),rest=link.RestLength*scale;
                    if(length<1e-7f||link.StretchOnly&&length<=rest)continue;
                    var correction=delta*((length-rest)/length*link.Stiffness/(wa+wb));
                    Positions[link.A]+=correction*wa;Positions[link.B]-=correction*wb;
                }
                if(d.Initialization!=null)foreach(var range in d.Initialization.Ranges)
                {
                    int particle=range.Particle;if(d.Fixed[particle]||d.InverseMass[particle]<=0)continue;
                    var center=Vector3.Lerp(frameAnchors[particle],anchors[particle],fraction);
                    var normal=BulletMath.Unit(Vector3.Lerp(frameNormals[particle],anchorNormals[particle],fraction),anchorNormals[particle]);
                    Positions[particle]=ClothRangeProjection.Project(Positions[particle],center,normal,range,scale);
                }
                if(BodyCollisions!=null)
                {
                    collisionMotion.Sample(fraction);
                    for(int i=0;i<Positions.Length;i++)
                        if(!d.Fixed[i]&&d.InverseMass[i]>0)
                            for(int contact=0;contact<4;contact++)
                                if(!BodyCollisions.Project(i,ref previous[i],ref Positions[i]))break;
                }
            }
            for(int i=0;i<Positions.Length;i++)
                if(d.Fixed[i]||d.InverseMass[i]<=0)Positions[i]=previous[i]=anchors[i];
            System.Array.Copy(anchors,frameAnchors,Positions.Length);
            if(anchorNormals!=null)System.Array.Copy(anchorNormals,frameNormals,Positions.Length);
        }
    }

    public sealed class ClothPreview : IDisposable
    {
        // Animation updates and viewport drawing both prepare this model.
        // Serialize ownership changes/submission, never wait for native IPC here.
        readonly object previewGate=new();
        byte[] source,seamSource;
        string sourceWarning;
        ClothMeshSource[] meshSources;
        Task<NativeClothAsset> nativeAsset;
        NativeClothPreview nativePreview;
        // The old managed solver remains available to regression tools only.
        public bool UseNativeRuntime {get;set;}=true;
        Task<ClothPreviewDefinition[]> loading;
        ClothPreviewSolver[] solvers;
        readonly Dictionary<string,NewBone> bones=new(StringComparer.Ordinal);
        readonly Dictionary<string,Matrix> inverseRest=new(StringComparer.Ordinal);
        sealed class MeshFrame
        {
            public int Solver;
            public ClothPreviewMeshBinding Mapping;
            public FlverSubmeshRenderer Renderer;
            public Vector3[] Positions,Normals,Tangents;
            public Matrix[] FrameDeltas;
        }
        List<MeshFrame> meshFrames;
        readonly List<(int Solver,int Bone,ClothPreviewBoneBinding Binding)> boneFrames=new();
        readonly Dictionary<int,Matrix> boneOutputs=new();
        Vector3[][] beforeSimulation;
        Vector3[][] modelParticles;
        Model owner;
        Matrix[] lastPose;
        float[] lastWeights;
        Matrix lastWorld;
        float lastTime=float.NaN;
        string lastClip;
        VertexPositionColor[] vertices;
        int[][] edges;
        BasicEffect shader;
        bool disposed,enabled;
        public bool ShowSimulationMesh {get;set;}
        public bool Enabled
        {
            get=>enabled;
            set
            {
                lock(previewGate)
                {
                    if(enabled==value)return;
                    enabled=value;Reset();
                    if(!value){ClearMeshFrames();nativePreview?.Dispose();nativePreview=null;}
                }
            }
        }
        public bool IsLoading=>UseNativeRuntime?nativePreview?.IsLoading==true:loading is {IsCompleted:false};
        public bool HasSource=>source!=null;
        public int DeformedMeshCount=>UseNativeRuntime?nativePreview?.MeshCount??0:meshFrames?.Select(f=>f.Mapping.MeshIndex).Distinct().Count()??0;
        public int DeformedBoneCount=>Enabled?owner?.SkeletonFlver?.ClothDeformedBoneCount??0:0;
        public int ActiveBodyColliders=>Enabled?(solvers?.Sum(s=>s.BodyCollisions?.ActiveColliderCount??0)??0):0;
        public int AuthoredRangeCount=>solvers?.Sum(s=>s.Definition.Initialization?.Ranges.Length??0)??0;
        int collisionNoticeCount=-1;
        public string Status{get;private set;}="No cloth HKX in this model binder.";
        public string Limitations{get;private set;}="Uses the installed Havok Content Tools 2018.1 native Cloth runtime. Game-specific wind, external contacts and gameplay state changes are not supplied by DSA.";
        public void SetSource(byte[] bytes){source=bytes;Status="Cloth data found. Enable to preview its physical deformation.";}
        public void SetSeamSource(byte[] bytes){seamSource=bytes;}
        void RefreshCollisionNotices()
        {
            int count=0;foreach(var solver in solvers)count+=solver.BodyCollisions?.Limitations.Count??0;
            if(count==collisionNoticeCount)return;
            collisionNoticeCount=count;
            Limitations=string.Join("\n",solvers.SelectMany(s=>s.Definition.Limitations)
                .Concat(solvers.Where(s=>s.BodyCollisions!=null).SelectMany(s=>s.BodyCollisions.Limitations)).Distinct());
            if(sourceWarning!=null)Limitations+="\n"+sourceWarning;
        }
        public void SetMeshSources(SoulsFormats.FLVER2 flver)
        {
            if(HasSource)
            {
                var bytes=source;
                SoulsFormats.CLM2 seams=null;
                if(seamSource!=null)
                    try{seams=SoulsFormats.CLM2.Read(seamSource);}
                    catch(Exception){sourceWarning="Cloth seam metadata could not be read; only explicitly mapped vertices deform.";}
                if(!UseNativeRuntime)meshSources=ClothMeshSource.FromFlver(flver,seams);
                if(UseNativeRuntime)
                {
                    nativeAsset=Task.Run(()=>NativeClothAsset.Create(bytes,flver,seams));
                    // Models may close or never enable physics before this
                    // background load completes. Observe faults immediately;
                    // keep the original task for the physics panel's status.
                    _=nativeAsset.ContinueWith(static task=>{_ = task.Exception;},
                        System.Threading.CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            seamSource=null;
        }
        public void Reset()
        {
            lock(previewGate)
            {
                lastTime=float.NaN;
                if(nativePreview?.HasFailed==true){nativePreview.Dispose();nativePreview=null;}
                else nativePreview?.Reset();
                owner?.SkeletonFlver?.ClearClothBoneTransforms();if(solvers!=null)foreach(var s in solvers)s.Reset();
            }
        }
        public void ShiftWorld(Vector3 shift)
        {
            lock(previewGate)
            {
                nativePreview?.ShiftWorld(shift);
                if(solvers==null)return;
                foreach(var solver in solvers)solver.ShiftWorld(shift);
                lastTime=float.NaN;
            }
        }
        void ClearMeshFrames()
        {
            if(meshFrames!=null)foreach(var frame in meshFrames)frame.Renderer.ClearClothFrame();
            owner?.SkeletonFlver?.ClearClothBoneTransforms();
            meshFrames=null;lastTime=float.NaN;
        }
        bool SamePose(Model model,float time,string clip)
        {
            if(time!=lastTime||clip!=lastClip||model.CurrentTransform.WorldMatrix!=lastWorld||lastPose==null)return false;
            int i=0;foreach(var bone in bones.Values){if(bone.FKMatrix!=lastPose[i]||bone.Weight!=lastWeights[i])return false;i++;}
            return true;
        }
        public void Prepare(Model model,float animationTime,string animationKey)
        {
            lock(previewGate)PrepareCore(model,animationTime,animationKey);
        }
        void PrepareCore(Model model,float animationTime,string animationKey)
        {
            if(!Enabled||disposed||source==null||model==null)return;
            if(UseNativeRuntime)
            {
                owner=model;nativePreview??=new NativeClothPreview(model,nativeAsset);
                nativePreview.Prepare(animationTime,animationKey);Status=nativePreview.Status;return;
            }
            try
            {
                if(solvers==null)
                {
                    if(loading==null)
                    {
                        var bytes=source;var sources=meshSources;
                        loading=Task.Run(()=>ClothPreviewData.Read(bytes,sources));Status="Loading authored cloth data...";return;
                    }
                    if(!loading.IsCompleted)return;
                    if(loading.IsFaulted){Status=loading.Exception.GetBaseException().Message;return;}
                    solvers=loading.Result.Select(d=>new ClothPreviewSolver(d)).ToArray();
                    loading=null;meshSources=null;
                    edges=solvers.Select(s=>
                    {
                        var e=new HashSet<(int,int)>();var t=s.Definition.Triangles;
                        void Add(int a,int b){if(a!=b)e.Add(a<b?(a,b):(b,a));}
                        for(int i=0;i+2<t.Length;i+=3){Add(t[i],t[i+1]);Add(t[i+1],t[i+2]);Add(t[i+2],t[i]);}
                        return e.SelectMany(p=>new[]{p.Item1,p.Item2}).ToArray();
                    }).ToArray();
                    vertices=new VertexPositionColor[edges.Sum(e=>e.Length)];
                    modelParticles=solvers.Select(s=>new Vector3[s.Positions.Length]).ToArray();
                    beforeSimulation=solvers.Select(s=>s.Definition.BoneBindings?.Any(b=>b.BeforeSimulation)==true?new Vector3[s.Positions.Length]:null).ToArray();
                    var bindingBones=solvers.SelectMany(s=>s.Definition.Bindings.Where(b=>b!=null).SelectMany(b=>b)).Select(w=>w.Bone);
                    var collisionBones=solvers.SelectMany(s=>s.Definition.Collisions?.Colliders??Array.Empty<ClothCollisionCollider>()).Select(c=>c.Bone);
                    var initialBones=solvers.Where(s=>s.Definition.Initialization!=null).SelectMany(s=>s.Definition.Initialization.Bindings).Where(b=>b!=null).SelectMany(b=>b).Select(w=>w.Bone);
                    var outputPoseBones=solvers.Any(s=>s.Definition.BoneBindings?.Length>0)?model.SkeletonFlver.Bones.Select(b=>b.Name):Enumerable.Empty<string>();
                    foreach(var name in bindingBones.Concat(collisionBones).Concat(initialBones).Concat(outputPoseBones).Where(n=>!string.IsNullOrEmpty(n)).Distinct())
                    {
                        var bone=model.SkeletonFlver?.Bones.FirstOrDefault(b=>b.Name==name);if(bone==null)continue;
                        if(!float.IsFinite(bone.ReferenceFKMatrix.Determinant())||Math.Abs(bone.ReferenceFKMatrix.Determinant())<1e-12f)continue;
                        bones[name]=bone;inverseRest[name]=Matrix.Invert(bone.ReferenceFKMatrix);
                    }
                    lastPose=new Matrix[bones.Count];lastWeights=new float[bones.Count];owner=model;
                    var outputWriters=solvers.SelectMany((s,i)=>(s.Definition.BoneBindings??Array.Empty<ClothPreviewBoneBinding>()).Select(b=>(Solver:i,Binding:b))).ToArray();
                    foreach(var writer in outputWriters)
                    {
                        var indices=model.SkeletonFlver?.Bones.Select((b,i)=>(b,i)).Where(p=>p.b.Name==writer.Binding.Bone).ToArray();
                        if(indices?.Length!=1||outputWriters.Count(w=>w.Binding.Bone==writer.Binding.Bone)!=1
                            ||ClothPreviewData.MatrixError(indices[0].b.ReferenceFKMatrix,writer.Binding.ReferenceTransform)>.002f)
                        {sourceWarning="Some cloth output bones have ambiguous or mismatched model references; those outputs retain animation.";continue;}
                        boneFrames.Add((writer.Solver,indices[0].i,writer.Binding));
                    }
                    Limitations=string.Join("\n",solvers.SelectMany(s=>s.Definition.Limitations).Distinct());
                    if(sourceWarning!=null)Limitations+="\n"+sourceWarning;
                }
                if(!ReferenceEquals(owner,model))return;
                if(meshFrames==null)
                {
                    meshFrames=new();
                    for(int i=0;i<solvers.Length;i++)foreach(var mapping in solvers[i].Definition.MeshBindings??Array.Empty<ClothPreviewMeshBinding>())
                    {
                        var meshes=model.MainMesh?.Submeshes;
                        if(meshes==null||(uint)mapping.MeshIndex>=meshes.Count)continue;
                        if(meshes[mapping.MeshIndex].VertexCount!=mapping.VertexCount)continue;
                        meshFrames.Add(new MeshFrame{Solver=i,Mapping=mapping,Renderer=meshes[mapping.MeshIndex],Positions=new Vector3[mapping.VertexCount],
                            Normals=mapping.HasNormals?new Vector3[mapping.VertexCount]:null,Tangents=mapping.HasTangents?new Vector3[mapping.VertexCount]:null,
                            FrameDeltas=mapping.NeedsDirectionTransport?new Matrix[mapping.VertexCount]:null});
                    }
                    Status=$"Cloth: {solvers.Length} pieces / {solvers.Sum(s=>s.Positions.Length)} particles / {DeformedMeshCount} model meshes. Approximate physical preview.";
                    if(meshFrames.Count==0&&boneFrames.Count==0)Status+=" No verified model mapping; simulation mesh overlay is available.";
                    if(boneFrames.Count>0)Status+=$" {boneFrames.Count} authored bone outputs.";
                }
                if(SamePose(model,animationTime,animationKey))return;
                var world=model.CurrentTransform.WorldMatrix;
                if(!float.IsFinite(world.Determinant())||Math.Abs(world.Determinant())<1e-12f){ClearMeshFrames();Status="Cloth paused: model transform is not invertible.";return;}
                var inverseWorld=Matrix.Invert(world);
                Matrix? Delta(string name)=>bones.TryGetValue(name,out var bone)?NewAnimSkeleton_FLVER.GetWeightedBoneDelta(bone,inverseRest[name]):null;
                Matrix? Reference(string name)=>bones.TryGetValue(name,out var bone)?bone.ReferenceFKMatrix:null;
                int at=0;
                for(int n=0;n<solvers.Length;n++)
                {
                    var solver=solvers[n];bool resetting=solver.WillReset(animationTime,animationKey);
                    if(beforeSimulation[n]!=null&&!resetting)
                        for(int i=0;i<solver.Positions.Length;i++)beforeSimulation[n][i]=Vector3.Transform(solver.Positions[i],inverseWorld);
                    solver.Advance(animationTime,animationKey,world,Delta,Reference);
                    foreach(int i in edges[n])vertices[at++]=new(solver.Positions[i],solver.Definition.Fixed[i]?Color.Orange:Color.Cyan);
                    for(int i=0;i<solver.Positions.Length;i++)modelParticles[n][i]=Vector3.Transform(solver.Positions[i],inverseWorld);
                    if(beforeSimulation[n]!=null&&resetting)Array.Copy(modelParticles[n],beforeSimulation[n],modelParticles[n].Length);
                }
                RefreshCollisionNotices();
                foreach(var frame in meshFrames)
                {
                    frame.Mapping.Evaluate(modelParticles[frame.Solver],frame.Positions,frame.Normals,frame.Tangents,frame.FrameDeltas);
                    frame.Renderer.SetClothFrame(frame.Mapping.VertexIndices,frame.Positions,frame.Normals,frame.Tangents,frame.FrameDeltas);
                }
                if(boneFrames.Count>0)
                {
                    boneOutputs.Clear();
                    foreach(var frame in boneFrames)
                        if(frame.Binding.TryEvaluate(frame.Binding.BeforeSimulation?beforeSimulation[frame.Solver]:modelParticles[frame.Solver],out var transform))boneOutputs.Add(frame.Bone,transform);
                    model.SkeletonFlver.SetClothBoneTransforms(boneOutputs);
                }
                lastTime=animationTime;lastClip=animationKey;lastWorld=world;
                int pi=0;foreach(var bone in bones.Values){lastPose[pi]=bone.FKMatrix;lastWeights[pi++]=bone.Weight;}
            }
            catch(Exception ex)
            {
                ClearMeshFrames();Status="Cloth preview: "+ex.Message+" Original model retained.";
            }
        }
        public void Draw(Model model,float animationTime,string animationKey,WorldView view)
        {
            if(!Enabled||disposed||view==null)return;
            Prepare(model,animationTime,animationKey);
            if(!ShowSimulationMesh||solvers==null||vertices==null||vertices.Length==0)return;
            var gd=GFX.Device;if(gd==null)return;
            shader??=new BasicEffect(gd,Main.BasicEffectBytecode){VertexColorEnabled=true,TextureEnabled=false,LightingEnabled=false};
            var blend=gd.BlendState;var depth=gd.DepthStencilState;var raster=gd.RasterizerState;
            var indices=gd.Indices;var texture=gd.Textures[0];var sampler=gd.SamplerStates[0];
            try
            {
                shader.World=view.Matrix_World;shader.View=view.Matrix_View;shader.Projection=view.Matrix_Projection;
                gd.BlendState=BlendState.Opaque;gd.DepthStencilState=DepthStencilState.DepthRead;gd.RasterizerState=RasterizerState.CullNone;
                foreach(var pass in shader.CurrentTechnique.Passes){pass.Apply();gd.DrawUserPrimitives(PrimitiveType.LineList,vertices,0,vertices.Length/2);}
            }
            finally
            {
                gd.BlendState=blend;gd.DepthStencilState=depth;gd.RasterizerState=raster;
                gd.SetVertexBuffer(null);gd.Indices=indices;gd.Textures[0]=texture;gd.SamplerStates[0]=sampler;
            }
        }
        public void Dispose()
        {
            lock(previewGate)
            {
                nativePreview?.Dispose();nativePreview=null;nativeAsset=null;
                ClearMeshFrames();disposed=true;source=null;seamSource=null;meshSources=null;solvers=null;vertices=null;modelParticles=null;owner=null;
                bones.Clear();inverseRest.Clear();boneFrames.Clear();boneOutputs.Clear();lastPose=null;lastWeights=null;shader?.Dispose();shader=null;
            }
        }
    }
}
