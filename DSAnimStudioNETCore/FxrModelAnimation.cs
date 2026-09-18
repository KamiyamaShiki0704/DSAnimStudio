using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SoulsFormats;
using SoulsAssetPipeline.Animation;

namespace DSAnimStudio
{
    // A second immutable vertex stream exists only for animated models. Static
    // models keep their original vertex format, memory footprint and GPU cache.
    [StructLayout(LayoutKind.Sequential,Pack=1)]
    public struct FxrModelSkinVertex : IVertexType
    {
        public Vector4 Indices,Weights;
        public static readonly VertexDeclaration Declaration=new(
            new VertexElement(0,VertexElementFormat.Vector4,VertexElementUsage.BlendIndices,0),
            new VertexElement(16,VertexElementFormat.Vector4,VertexElementUsage.BlendWeight,0));
        VertexDeclaration IVertexType.VertexDeclaration=>Declaration;
    }

    public sealed class FxrModelAnimation
    {
        public const int MaxBones=256;
        public sealed class Pose
        {
            public readonly Matrix[] Bones,Normals;
            internal readonly Matrix[] World;
            internal int Clip=-1;internal float Time=float.NaN;internal long Revision;
            internal Pose(int count,int palette){World=new Matrix[count];Bones=new Matrix[palette];Normals=new Matrix[palette];}
        }
        public readonly Dictionary<FxrModelVertex[],FxrModelSkinVertex[]> Skins=new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<int,HavokAnimationData> clips=new();
        readonly HKX.HKASkeleton skeleton;
        readonly int[] parents,order,paletteToBone;
        readonly Matrix[] inverseBind,reference;
        readonly Pose[] poses;
        int nextPose;
        public int BoneCount=>paletteToBone.Length;
        public IEnumerable<int> ClipIds=>clips.Keys;
        public float Duration(int clip)=>clips.TryGetValue(clip,out var c)?c.Duration:0;
        public long RetainedBytes {get;}
        public long DecodedAnimationBytes {get;}

        public FxrModelAnimation(byte[] binderBytes,FLVER2 model,
            List<(FxrModelVertex[] Vertices,FxrMaterialProfile Material)> parts,IEnumerable<int> requested)
        {
            var binder=BND4.Read(binderBytes);
            var compendium=binder.Files.FirstOrDefault(f=>f.Name.EndsWith(".compendium",StringComparison.OrdinalIgnoreCase))?.Bytes;
            HKX Read(byte[] bytes)=>bytes.Length>=8&&bytes[4]=='T'&&bytes[5]=='A'&&bytes[6]=='G'
                ?HKX.GenFakeFromTagFile(bytes,compendium):HKX.Read(bytes,HKX.HKXVariation.HKXDS3,false);
            var skeletonFile=binder.Files.FirstOrDefault(f=>f.Name.EndsWith("_skeleton.hkx",StringComparison.OrdinalIgnoreCase))
                ??throw new InvalidDataException("FXR animation skeleton is missing.");
            skeleton=FxrAnimationMemory.Detach(Read(skeletonFile.Bytes).DataSection.Objects.OfType<HKX.HKASkeleton>().Single());
            int count=checked((int)skeleton.Bones.Size);
            if(count<=0||count>4096)throw new InvalidDataException("FXR animation skeleton size is invalid.");
            parents=new int[count];reference=new Matrix[count];
            var names=new Dictionary<string,int>(StringComparer.Ordinal);
            for(int i=0;i<count;i++)
            {
                if(!names.TryAdd(skeleton.Bones[i].Name.GetString(),i))throw new InvalidDataException("FXR animation has ambiguous bone names.");
                parents[i]=skeleton.ParentIndices[i].data;
                if(parents[i]>=count||parents[i]<-1)throw new InvalidDataException("FXR animation parent is out of range.");
                var t=skeleton.Transforms[i];
                reference[i]=Matrix.CreateScale(t.Scale.Vector.X,t.Scale.Vector.Y,t.Scale.Vector.Z)*
                    Matrix.CreateFromQuaternion(new Quaternion(t.Rotation.Vector.X,t.Rotation.Vector.Y,t.Rotation.Vector.Z,t.Rotation.Vector.W))*
                    Matrix.CreateTranslation(t.Position.Vector.X,t.Position.Vector.Y,t.Position.Vector.Z);
            }
            var sorted=new List<int>();var visited=new byte[count];
            void Visit(int i){if(visited[i]==2)return;if(visited[i]==1)throw new InvalidDataException("FXR skeleton parent cycle.");visited[i]=1;if(parents[i]>=0)Visit(parents[i]);visited[i]=2;sorted.Add(i);}
            for(int i=0;i<count;i++)Visit(i);order=sorted.ToArray();
            var palette=new Dictionary<int,int>();var boneMap=new List<int>();var inverses=new List<Matrix>();
            int Bone(int node)
            {
                if(palette.TryGetValue(node,out int index))return index;
                if(node<0||node>=model.Nodes.Count)throw new InvalidDataException("FXR model weight bone is out of range.");
                int animated=node,bone=-1;
                for(int n=0;animated>=0&&!names.TryGetValue(model.Nodes[animated].Name,out bone);n++)
                {
                    if(n>=model.Nodes.Count)throw new InvalidDataException("FXR model parent cycle.");
                    animated=model.Nodes[animated].ParentIndex;
                    if(animated>=model.Nodes.Count)throw new InvalidDataException("FXR model parent is out of range.");
                    bone=-1;
                }
                if(palette.Count>=MaxBones)throw new InvalidDataException("FXR model uses more than 256 weighted bones.");
                // Bones not authored in the animation retain their FLVER pose
                // relative to the nearest animated ancestor. A separate static
                // root (e.g. AC6 s88200's casing) remains in model space.
                var bind=System.Numerics.Matrix4x4.Identity;int parent=animated;
                for(int n=0;parent>=0;n++)
                {
                    if(n>=model.Nodes.Count||parent>=model.Nodes.Count)throw new InvalidDataException("FXR model parent cycle or index.");
                    bind*=model.Nodes[parent].ComputeLocalTransform();parent=model.Nodes[parent].ParentIndex;
                }
                if(!System.Numerics.Matrix4x4.Invert(bind,out var inv))throw new InvalidDataException("FXR model bind pose is singular.");
                index=palette.Count;palette[node]=index;boneMap.Add(bone);inverses.Add(inv);return index;
            }
            int partIndex=0;
            foreach(var mesh in model.Meshes)
            {
                var faces=mesh.FaceSets.FirstOrDefault();if(faces==null)continue;
                var vertices=parts[partIndex++].Vertices;var skin=new FxrModelSkinVertex[vertices.Length];int output=0;
                foreach(int vi in faces.Triangulate(mesh.Vertices.Count<ushort.MaxValue))
                {
                    if(vi<0||vi>=mesh.Vertices.Count)continue;
                    var v=mesh.Vertices[vi];var indices=System.Numerics.Vector4.Zero;var weights=System.Numerics.Vector4.Zero;float total=0;
                    if(!mesh.UseBoneWeights){indices.X=Bone(v.NormalW>=0?v.NormalW:mesh.NodeIndex);weights.X=1;total=1;}
                    else for(int j=0;j<4;j++)
                    {
                        float weight=v.BoneWeights[j];if(weight<=0)continue;
                        if(!float.IsFinite(weight))throw new InvalidDataException("FXR model weight is invalid.");
                        int node=v.BoneIndices[j];
                        if(model.Header.Version<=0x2000D){if(node<0||node>=mesh.BoneIndices.Count)throw new InvalidDataException("FXR mesh weight index is invalid.");node=mesh.BoneIndices[node];}
                        indices[j]=Bone(node);weights[j]=weight;total+=weight;
                    }
                    if(total<=0)throw new InvalidDataException("FXR model vertex has no weight.");
                    skin[output++]=new(){Indices=indices,Weights=weights/total};
                }
                if(output!=vertices.Length)throw new InvalidDataException("FXR skin vertex stream does not match geometry.");
                Skins[vertices]=skin;
            }
            paletteToBone=boneMap.ToArray();inverseBind=inverses.ToArray();
            var wanted=requested.Distinct().ToHashSet();
            foreach(var file in binder.Files)
            {
                var match=Regex.Match(file.Name,@"_a(\d+)_(\d+)\.hkx$",RegexOptions.IgnoreCase);
                if(!match.Success)continue;
                int id=checked(int.Parse(match.Groups[1].Value)*1000000+int.Parse(match.Groups[2].Value));
                if(!wanted.Contains(id))continue;
                var objects=Read(file.Bytes).DataSection.Objects;
                var binding=objects.OfType<HKX.HKAAnimationBinding>().SingleOrDefault()??throw new InvalidDataException("FXR animation binding is missing.");
                var frame=objects.OfType<HKX.HKADefaultAnimatedReferenceFrame>().FirstOrDefault();
                HavokAnimationData clip=objects.OfType<HKX.HKASplineCompressedAnimation>().FirstOrDefault() is { } spline
                    ?new HavokAnimationData_SplineCompressed(id,file.Name,skeleton,frame,binding,spline)
                    :objects.OfType<HKX.HKAInterleavedUncompressedAnimation>().FirstOrDefault() is { } interleaved
                    ?new HavokAnimationData_InterleavedUncompressed(id,file.Name,skeleton,frame,binding,interleaved)
                    :throw new NotSupportedException("FXR model animation compression is not supported.");
                if(clip.IsAdditiveBlend)throw new NotSupportedException("FXR additive animation needs its authored base clip.");
                if(!float.IsFinite(clip.Duration)||clip.Duration<=0||clip.FrameCount<1||clip.FrameDuration<=0)throw new InvalidDataException("FXR animation timing is invalid.");
                clips.Add(id,clip);
            }
            if(wanted.Any(id=>!clips.ContainsKey(id)))throw new InvalidDataException("An authored FXR model animation clip is missing.");
            poses=Enumerable.Range(0,8).Select(_=>new Pose(count,BoneCount)).ToArray();
            DecodedAnimationBytes=FxrAnimationMemory.Skeleton(skeleton)+clips.Values.Sum(FxrAnimationMemory.Clip);
            RetainedBytes=DecodedAnimationBytes+Skins.Values.Sum(v=>24+v.LongLength*32)+poses.Length*(128+BoneCount*128L+parents.Length*64L)
                +256+parents.LongLength*4+order.LongLength*4+paletteToBone.LongLength*4+inverseBind.LongLength*64+reference.LongLength*64;
        }
        public Pose Sample(int id,float seconds,bool loop)
        {
            if(!clips.TryGetValue(id,out var clip)||!float.IsFinite(seconds))return null;
            float time=loop?((seconds%clip.Duration)+clip.Duration)%clip.Duration:Math.Clamp(seconds,0,clip.Duration);
            foreach(var cached in poses)if(cached.Clip==id&&cached.Time==time)return cached;
            var pose=poses[nextPose];nextPose=(nextPose+1)%poses.Length;float frame=Math.Clamp(time/clip.FrameDuration,0,clip.FrameCount-1);
            foreach(int i in order)
            {
                int track=clip.HkxBoneIndexToTransformTrackMap[i];
                Matrix local=track<0?reference[i]:clip is HavokAnimationData_SplineCompressed sc
                    ?sc.GetTransformOnClampedFrame(track,frame).GetMatrixFull():clip.GetTransformOnFrame(track,frame,false).GetMatrixFull();
                pose.World[i]=parents[i]>=0?local*pose.World[parents[i]]:local;
            }
            for(int i=0;i<BoneCount;i++)
            {
                pose.Bones[i]=paletteToBone[i]<0?Matrix.Identity:inverseBind[i]*pose.World[paletteToBone[i]];
                // Animated scale can intentionally hide an effect part.
                pose.Normals[i]=Math.Abs(pose.Bones[i].Determinant())>1e-12f?Matrix.Transpose(Matrix.Invert(pose.Bones[i])):Matrix.Identity;
            }
            pose.Clip=id;pose.Time=time;pose.Revision++;return pose;
        }
    }
}
