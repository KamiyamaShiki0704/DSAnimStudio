using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using DSAnimStudio.NativePhysics;

namespace DSAnimStudio
{
    internal sealed class NativeClothPreview : IDisposable
    {
        readonly Model model;
        readonly Task<NativeClothAsset> assetTask;
        Task<NativeClothSession> starting;
        NativeClothSession session;
        NativeClothAsset asset;
        Task<NativeClothResult> pending;
        int generation,pendingGeneration;
        bool disposed,failed;
        float submittedTime=float.NaN;
        string submittedClip;
        Matrix submittedWorld;
        Vector3 originShift;
        Matrix StableWorld=>model.CurrentTransform.WorldMatrix*Matrix.CreateTranslation(-originShift);
        float[][] previousPose;
        NewBone[][] bones;
        int[][] boneIndices;
        Matrix[][] inverseRest;
        Matrix[][] submittedLocal;
        float[][] submittedWeights;
        readonly Dictionary<int,Matrix> outputs=new();
        readonly Dictionary<int,(int[] Indices,Vector3[] Positions,Vector3[] Normals,Vector3[] Tangents)> meshFrames=new();
        public string Status{get;private set;}="Loading native physics scene...";
        public bool HasFailed=>failed;
        public bool IsLoading=>!failed&&(asset==null||session==null);
        public int MeshCount=>meshFrames.Count;
        public NativeClothPreview(Model model,Task<NativeClothAsset> assetTask){this.model=model;this.assetTask=assetTask;}
        void Bind()
        {
            var all=model.SkeletonFlver.Bones;var d=asset.Description;
            bones=new NewBone[d.Skeletons.Length][];boneIndices=new int[bones.Length][];inverseRest=new Matrix[bones.Length][];
            submittedLocal=new Matrix[bones.Length][];submittedWeights=new float[bones.Length][];
            for(int s=0;s<bones.Length;s++)
            {
                bones[s]=new NewBone[d.Skeletons[s].Bones.Length];boneIndices[s]=new int[bones[s].Length];inverseRest[s]=new Matrix[bones[s].Length];
                submittedLocal[s]=new Matrix[bones[s].Length];submittedWeights[s]=new float[bones[s].Length];
                for(int b=0;b<bones[s].Length;b++)
                {
                    string name=d.Skeletons[s].Bones[b];int index=all.FindIndex(x=>x.Name==name);boneIndices[s][b]=index;
                    if(index<0)continue;bones[s][b]=all[index];inverseRest[s][b]=Matrix.Invert(all[index].ReferenceFKMatrix);
                }
            }
        }
        float[][] CapturePose(Matrix world)
        {
            var result=new float[bones.Length][];
            for(int s=0;s<bones.Length;s++)
            {
                result[s]=new float[bones[s].Length*16];
                for(int b=0;b<bones[s].Length;b++)
                {
                    var reference=NativeClothSession.MatrixAt(asset.Description.Skeletons[s].ReferenceMatrices,b*16);
                    var fk=bones[s][b]==null?Matrix.Identity:model.SkeletonFlver.GetPhysicsInputBoneFK(boneIndices[s][b]);
                    var delta=bones[s][b]==null||!asset.Description.Skeletons[s].ReadByCloth[b]?Matrix.Identity:NewAnimSkeleton_FLVER.GetWeightedBoneDelta(bones[s][b],inverseRest[s][b],fk);
                    if(bones[s][b]!=null){submittedLocal[s][b]=fk;submittedWeights[s][b]=bones[s][b].Weight;}
                    NativeClothSession.Store(reference*delta*world,result[s],b*16);
                }
            }
            return result;
        }
        bool SameInput()
        {
            for(int s=0;s<bones.Length;s++)for(int b=0;b<bones[s].Length;b++)if(bones[s][b]!=null&&(submittedLocal[s][b]!=model.SkeletonFlver.GetPhysicsInputBoneFK(boneIndices[s][b])||submittedWeights[s][b]!=bones[s][b].Weight))return false;
            return true;
        }
        void Apply(NativeClothResult result)
        {
            var inverseWorld=Matrix.Invert(StableWorld);var d=asset.Description;outputs.Clear();
            for(int c=0;c<d.Cloths.Length;c++)
            {
                for(int t=0;t<d.Cloths[c].TransformSkeletons.Length;t++)
                {
                    int s=d.Cloths[c].TransformSkeletons[t];var matrices=result.Transforms[c][t];var input=result.Input[s];
                    for(int b=0;b<bones[s].Length;b++)
                    {
                        if(boneIndices[s][b]<0)continue;bool changed=false;for(int j=b*16;j<b*16+16;j++)if(Math.Abs(matrices[j]-input[j])>1e-6f){changed=true;break;}
                        if(changed)outputs[boneIndices[s][b]]=NativeClothSession.MatrixAt(matrices,b*16)*inverseWorld;
                    }
                }
                for(int m=0;m<d.Cloths[c].Meshes.Length;m++)
                {
                    var mapping=d.Cloths[c].Meshes[m];var mesh=model.MainMesh.Submeshes[mapping.MeshIndex];
                    if(mapping.WrittenVertices.Length==0)continue;
                    if(mesh.VertexCount!=mapping.VertexCount)throw new InvalidOperationException("Native render mesh topology changed");
                    if(!meshFrames.TryGetValue(mapping.MeshIndex,out var frame))
                    {frame=(mapping.WrittenVertices,new Vector3[mapping.VertexCount],new Vector3[mapping.VertexCount],new Vector3[mapping.VertexCount]);meshFrames.Add(mapping.MeshIndex,frame);}
                    var channels=result.Meshes[c][m];
                    for(int i=0;i<mapping.VertexCount;i++)
                    {
                        Vector3 Read(int channel)=>new(channels[channel][i*3],channels[channel][i*3+1],channels[channel][i*3+2]);
                        frame.Positions[i]=Vector3.Transform(Read(0),inverseWorld);
                        if(channels[1].Length>0)frame.Normals[i]=BulletMath.Unit(Vector3.TransformNormal(Read(1),inverseWorld),new(mapping.ReferenceNormals[i*3],mapping.ReferenceNormals[i*3+1],mapping.ReferenceNormals[i*3+2]));
                        if(channels[2].Length>0)frame.Tangents[i]=BulletMath.Unit(Vector3.TransformNormal(Read(2),inverseWorld),new(mapping.ReferenceTangents[i*3],mapping.ReferenceTangents[i*3+1],mapping.ReferenceTangents[i*3+2]));
                    }
                    for(int a=0;a<mapping.AliasSources.Length;a++)
                    {
                        int source=mapping.AliasSources[a],target=mapping.AliasTargets[a];frame.Positions[target]=frame.Positions[source];
                        Vector3 Ref(float[] data,int i)=>new(data[i*3],data[i*3+1],data[i*3+2]);
                        Matrix Basis(Vector3 n,Vector3 t)
                        {n=BulletMath.Unit(n,Vector3.Up);t=BulletMath.Unit(t-n*Vector3.Dot(n,t),Math.Abs(n.Y)<.9f?Vector3.Cross(n,Vector3.Up):Vector3.Cross(n,Vector3.Right));var b=Vector3.Cross(t,n);return new(t.X,t.Y,t.Z,0,n.X,n.Y,n.Z,0,b.X,b.Y,b.Z,0,0,0,0,1);}
                        if(channels[1].Length>0&&channels[2].Length>0)
                        {
                            var reference=Basis(Ref(mapping.ReferenceNormals,source),Ref(mapping.ReferenceTangents,source));var current=Basis(frame.Normals[source],frame.Tangents[source]);
                            var delta=Matrix.Transpose(reference)*current;
                            frame.Normals[target]=BulletMath.Unit(Vector3.TransformNormal(Ref(mapping.ReferenceNormals,target),delta),frame.Normals[source]);frame.Tangents[target]=BulletMath.Unit(Vector3.TransformNormal(Ref(mapping.ReferenceTangents,target),delta),frame.Tangents[source]);
                        }
                    }
                    mesh.SetClothFrame(frame.Indices,frame.Positions,channels[1].Length>0?frame.Normals:null,channels[2].Length>0?frame.Tangents:null);
                }
            }
            model.SkeletonFlver.SetClothBoneTransforms(outputs);
            Status=$"Native Havok Cloth: {d.Cloths.Length} instances / {MeshCount} model meshes / {outputs.Count} bone outputs.";
        }
        public void Prepare(float time,string clip)
        {
            if(disposed||failed)return;
            try
            {
                if(asset==null)
                {
                    if(assetTask==null)throw new InvalidOperationException("Native scene source missing");
                    if(!assetTask.IsCompleted)return;asset=assetTask.GetAwaiter().GetResult();Bind();
                    starting=Task.Run(()=>new NativeClothSession(asset));Status="Starting native Havok Cloth runtime...";
                }
                if(session==null){if(!starting.IsCompleted)return;session=starting.GetAwaiter().GetResult();}
                if(pending!=null)
                {
                    if(!pending.IsCompleted)return;
                    var result=pending.GetAwaiter().GetResult();pending=null;if(pendingGeneration==generation)Apply(result);
                }
                var world=StableWorld;
                if(time==submittedTime&&clip==submittedClip&&world==submittedWorld&&SameInput())return;
                bool reset=!float.IsFinite(submittedTime)||time<submittedTime||time-submittedTime>.1f||clip!=submittedClip;
                float dt=reset?1f/60:Math.Clamp(time-submittedTime,1f/240,.1f);
                var pose=CapturePose(world);var prior=previousPose;var active=session;
                submittedTime=time;submittedClip=clip;submittedWorld=world;previousPose=pose;pendingGeneration=generation;
                pending=RunSteps(active,pose,prior,dt,reset);
            }
            catch(Exception e){failed=true;Status="Native physics unavailable: "+(e is InvalidOperationException?e.Message:e.GetBaseException().Message);Clear();Release();}
        }
        static Task<NativeClothResult> RunSteps(NativeClothSession active,float[][] pose,float[][] prior,float dt,bool reset)
        {
            return Task.Run(()=>
                {
                    int steps=reset?1:Math.Max(1,(int)Math.Ceiling(dt*60));NativeClothResult result=null;
                    for(int step=1;step<=steps;step++)
                    {
                        float[][] sample=pose;
                        if(step<steps&&prior!=null)
                        {
                            sample=pose.Select(a=>new float[a.Length]).ToArray();
                            for(int s=0;s<pose.Length;s++)for(int i=0;i<pose[s].Length;i+=16)
                            {
                                var a=NativeClothSession.MatrixAt(prior[s],i);var b=NativeClothSession.MatrixAt(pose[s],i);float amount=step/(float)steps;
                                if(!a.Decompose(out var sa,out var qa,out var pa)||!b.Decompose(out var sb,out var qb,out var pb))throw new InvalidOperationException("Physics pose cannot be interpolated");
                                NativeClothSession.Store(Matrix.CreateScale(Vector3.Lerp(sa,sb,amount))*Matrix.CreateFromQuaternion(Quaternion.Slerp(qa,qb,amount))*Matrix.CreateTranslation(Vector3.Lerp(pa,pb,amount)),sample[s],i);
                            }
                        }
                        result=active.Step(sample,dt/steps,reset&&step==1);
                    }
                    return result;
                });
        }
        public void Reset(){generation++;submittedTime=float.NaN;previousPose=null;Clear();}
        public void ShiftWorld(Vector3 shift){originShift+=shift;}
        void Clear(){foreach(int index in meshFrames.Keys)model.MainMesh.Submeshes[index].ClearClothFrame();meshFrames.Clear();model.SkeletonFlver.ClearClothBoneTransforms();}
        void Release()
        {
            if(pending!=null)
            {
                // A frame can fail after Disable/Dispose closes its owned pipe.
                // Observe that retired work even though it will never be applied.
                pending.ContinueWith(t=>{_ = t.Exception;},TaskContinuationOptions.OnlyOnFaulted);pending=null;
            }
            if(session!=null){var owned=session;session=null;Task.Run(owned.Dispose);}
            else if(starting!=null)starting.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();else _=t.Exception;},TaskScheduler.Default);
        }
        public void Dispose(){if(disposed)return;disposed=true;Clear();Release();}
    }
}
