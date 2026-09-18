using System;
using System.Collections.Generic;
using System.Linq;
using Havoc.Objects;
using Microsoft.Xna.Framework;
using static DSAnimStudio.ClothPreviewData;

namespace DSAnimStudio
{
    public sealed record ClothPreviewRange(int Particle,float MaximumDistance,float MinNormalDistance,float MaxNormalDistance,float Stiffness,bool ApplyNormal);
    public sealed record ClothPreviewInitialization(ClothPreviewWeight[][] Bindings,Vector3[][] ReferenceNormals,ClothPreviewRange[] Ranges);

    public static class ClothRangeProjection
    {
        public static Vector3 Project(Vector3 position,Vector3 center,Vector3 normal,ClothPreviewRange range,float scale)
        {
            float radius=range.MaximumDistance*scale;
            var offset=position-center;var projected=offset;
            if(range.ApplyNormal)
            {
                normal=BulletMath.Unit(normal,Vector3.Up);
                float along=Vector3.Dot(offset,normal);
                float min=Math.Clamp(range.MinNormalDistance,-range.MaximumDistance,range.MaximumDistance)*scale;
                float max=Math.Clamp(range.MaxNormalDistance,-range.MaximumDistance,range.MaximumDistance)*scale;
                float height=Math.Clamp(along,min,max);var tangent=offset-normal*along;
                float allowed=MathF.Sqrt(Math.Max(0,radius*radius-height*height)),length=tangent.Length();
                if(length>allowed&&length>1e-10f)tangent*=allowed/length;
                projected=normal*height+tangent;
            }
            else if(offset.LengthSquared()>radius*radius&&offset.LengthSquared()>1e-20f)projected=Vector3.Normalize(offset)*radius;
            return center+Vector3.Lerp(offset,projected,range.Stiffness);
        }
    }

    internal static class ClothCopyPipeline
    {
        static int Int(IHkObject o,int fallback=-1)=>(int)Number(o,fallback);
        static bool Is(IHkObject o,string type)=>Unwrap(o)?.Type.Name==type;
        static int[] StateOps(IHkObject state)=>Array(Field(state,"operators")).Select(v=>Int(v)).ToArray();
        internal static ClothPreviewDefinition Read(IHkObject cloth,int simIndex,ClothPreviewDefinition definition,IHkObject[] skeletons,ClothMeshSource[] sources,HashSet<string> notices)
        {
            var buffers=Array(Field(cloth,"bufferDefinitions"));var ops=Array(Field(cloth,"operators")).Select(Unwrap).ToArray();
            var states=Array(Field(cloth,"clothStateDatas"));if(states.Count==0)return definition;
            var order=StateOps(states[0]);if(order.Any(i=>(uint)i>=ops.Length||ops[i]==null))return definition;
            int simulate=System.Array.FindIndex(order,i=>Is(ops[i],"hclSimulateOperator")&&Int(Field(ops[i],"simClothIndex"))==simIndex);
            if(simulate<0)return definition;
            var simBuffers=Array(Field(ops[order[simulate]],"usedBuffers")).Select(b=>Int(Field(b,"bufferIndex")))
                .Where(i=>(uint)i<buffers.Count&&Int(Field(buffers[i],"type"))==1&&Int(Field(buffers[i],"numVertices"))==definition.RestPositions.Length).ToHashSet();
            var mappings=new List<ClothPreviewMeshBinding>(definition.MeshBindings??System.Array.Empty<ClothPreviewMeshBinding>());
            ClothPreviewInitialization initialization=null;
            foreach(int outputIndex in order.Skip(simulate+1))
            {
                var copy=ops[outputIndex];if(!Is(copy,"hclCopyVerticesOperator"))continue;
                int input=Int(Field(copy,"inputBufferIdx")),output=Int(Field(copy,"outputBufferIdx"));
                int count=Int(Field(copy,"numberOfVertices")),from=Int(Field(copy,"startVertexIn")),to=Int(Field(copy,"startVertexOut"));
                if(!simBuffers.Contains(input)||(uint)output>=buffers.Count||Int(Field(buffers[output],"type"))!=4)continue;
                if(count<=0||from<0||to<0||(long)from+count>definition.RestPositions.Length||(long)to+count>Int(Field(buffers[output],"numVertices")))
                {notices.Add("Cloth CopyVertices has invalid authored ranges; output retained as animation");continue;}
                try
                {
                    if(from!=0||count!=definition.RestPositions.Length)throw new InvalidOperationException("partial simulation initialization requires an explicit merged input chain");
                    var targets=Enumerable.Range(0,count).ToDictionary(k=>to+k,k=>from+k);
                    // Initialization must explicitly copy this same user buffer back into the simulation.
                    var candidates=new List<ClothPreviewInitialization>();
                    foreach(var state in states)
                    {
                        var sequence=StateOps(state);if(sequence.Any(i=>(uint)i>=ops.Length||ops[i]==null)||sequence.Any(i=>Is(ops[i],"hclSimulateOperator")))continue;
                        for(int at=0;at<sequence.Length;at++)
                        {
                            var reverse=ops[sequence[at]];
                            if(!Is(reverse,"hclCopyVerticesOperator")||Int(Field(reverse,"inputBufferIdx"))!=output||Int(Field(reverse,"outputBufferIdx"))!=input
                                ||Int(Field(reverse,"startVertexIn"))!=to||Int(Field(reverse,"startVertexOut"))!=from||Int(Field(reverse,"numberOfVertices"))!=count)continue;
                            var producers=sequence.Take(at).Select(i=>ops[i]).Where(o=>Int(Field(o,"outputBufferIndex"))==output&&o.Type.Name.StartsWith("hclBoneSpaceSkin",StringComparison.Ordinal)).ToArray();
                            if(producers.Length!=1)continue;
                            if(sequence.Take(at).Select(i=>ops[i]).Any(o=>!ReferenceEquals(o,producers[0])
                                &&(Int(Field(o,"outputBufferIdx"))==output||Int(Field(o,"outputBufferIndex"))==output)))continue;
                            var skin=producers[0];int set=Int(Field(skin,"transformSetIndex"));var transforms=Array(Field(cloth,"transformSetDefinitions"));
                            var skeleton=(uint)set<transforms.Count?skeletons.SingleOrDefault(s=>Text(Field(s,"name"))==Text(Field(transforms[set],"name"))):null;
                            var weights=new ClothPreviewWeight[definition.RestPositions.Length][];var normals=new Vector3[weights.Length][];
                            var proof=new List<string>();ReadBoneSpaceBindings(skin,skeleton,definition.RestPositions,targets,weights,proof,normals);
                            if(targets.Values.Any(i=>weights[i]==null||normals[i]==null))throw new InvalidOperationException(string.Join("; ",proof));
                            candidates.Add(new(weights,normals,System.Array.Empty<ClothPreviewRange>()));
                        }
                    }
                    if(candidates.Count==0)throw new InvalidOperationException("no verified reverse-copy initialization state");
                    var initial=candidates[0];
                    if(candidates.Skip(1).Any(c=>targets.Values.Any(i=>!c.Bindings[i].SequenceEqual(initial.Bindings[i])||!c.ReferenceNormals[i].SequenceEqual(initial.ReferenceNormals[i]))))
                        throw new InvalidOperationException("initialization states disagree on authored skin input");
                    if(targets.Values.Any(i=>definition.Fixed[i]&&(definition.Bindings[i]==null||!definition.Bindings[i].SequenceEqual(initial.Bindings[i]))))
                        throw new InvalidOperationException("initial skin disagrees with fixed-particle input");
                    // Any later skin writes must be fixed-particle overwrites, not hidden replacements of the simulation.
                    foreach(int later in order.Skip(System.Array.IndexOf(order,outputIndex)+1))
                    {
                        var op=ops[later];
                        if(op.Type.Name.StartsWith("hclBoneSpaceSkin",StringComparison.Ordinal)&&Int(Field(op,"outputBufferIndex"))==output)
                        {
                            var deform=Field(op,"boneSpaceDeformer");
                            var written=new[]{"one","two","three","four"}.SelectMany(k=>Array(Field(deform,k+"BlendEntries")))
                                .SelectMany(b=>Array(Field(b,"vertexIndices"))).Select(v=>Int(v)).Distinct().Where(v=>targets.ContainsKey(v));
                            if(written.Any(v=>!definition.Fixed[targets[v]]))throw new InvalidOperationException("later skin overwrites a free simulation vertex");
                        }
                        else if(Is(op,"hclCopyVerticesOperator")&&Int(Field(op,"outputBufferIdx"))==output)
                            throw new InvalidOperationException("later copy overwrites the simulation output");
                        else if(Is(op,"hclOutputConvertOperator")&&Int(Field(op,"userBufferIndex"))==output)
                        {
                            var info=Field(op,"conversionInfo");int elements=Int(Field(info,"numElementsConverted"),0);
                            if(Array(Field(info,"elementConversions")).Take(elements).Any(e=>Int(Field(e,"index"))==0&&Int(Field(e,"conversion"))!=1))
                                throw new InvalidOperationException("unverified position format conversion");
                        }
                    }
                    var ranges=new List<ClothPreviewRange>();var sim=Array(Field(cloth,"simClothDatas"))[simIndex];
                    foreach(var constraint in Array(Field(sim,"staticConstraintSets")).Where(c=>Is(c,"hclLocalRangeConstraintSet")))
                    {
                        if(Int(Field(constraint,"referenceMeshBufferIdx"))!=output||Int(Field(constraint,"shapeType"))!=0)continue;
                        var batch=new List<ClothPreviewRange>();bool valid=true;
                        foreach(var local in Array(Field(constraint,"localConstraints")))
                        {
                            int particle=Int(Field(local,"particleIndex")),vertex=Int(Field(local,"referenceVertex"));
                            float distance=Number(Field(local,"maximumDistance"),float.NaN),min=Number(Field(local,"minNormalDistance"),float.NaN),max=Number(Field(local,"maxNormalDistance"),float.NaN);
                            float stiffness=Number(Field(constraint,"stiffness"),1);
                            if(!targets.TryGetValue(vertex,out int mapped)||mapped!=particle||!float.IsFinite(distance*distance)||distance<0||!float.IsFinite(min)||!float.IsFinite(max)||min>max||min>distance||max< -distance||!float.IsFinite(stiffness)){valid=false;break;}
                            batch.Add(new(particle,distance,min,max,Math.Clamp(stiffness,0,1),Number(Field(constraint,"applyNormalComponent"))!=0));
                        }
                        if(valid){ranges.AddRange(batch);notices.Remove("hclLocalRangeConstraintSet not solved");}
                        else notices.Add("LocalRange reference mapping/parameters are unsupported; constraint not substituted");
                    }
                    if(from==0&&count==definition.RestPositions.Length)initialization=initial with{Ranges=ranges.ToArray()};
                    if(sources!=null)
                    {
                        var binding=ClothMeshMapping.BindCopy(Text(Field(buffers[output],"name")),Int(Field(buffers[output],"numVertices")),targets,definition.RestPositions,definition.Triangles,sources,notices);
                        if(binding!=null)mappings.Add(binding);
                    }
                    notices.Add($"Verified authored CopyVertices [{from},{from+count}) -> buffer {output}[{to},{to+count}); reverse-copy initialization and {ranges.Count} spherical local ranges; tangent transport is a preview, not native UpdateVertexFrames");
                }
                catch(InvalidOperationException e){notices.Add("Cloth CopyVertices not applied: "+e.Message);}
            }
            return definition with{Initialization=initialization,MeshBindings=mappings.ToArray()};
        }
    }
}
