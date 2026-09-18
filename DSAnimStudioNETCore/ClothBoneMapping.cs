using System;
using System.Collections.Generic;
using System.Linq;
using Havoc.Objects;
using Microsoft.Xna.Framework;
using static DSAnimStudio.ClothPreviewData;

namespace DSAnimStudio
{
    public sealed record ClothPreviewBoneBinding(string Bone,int A,int B,int C,Matrix LocalTransform,Matrix ReferenceTransform,bool BeforeSimulation=false)
    {
        public bool TryEvaluate(ReadOnlySpan<Vector3> particles,out Matrix transform)
        {
            transform=ReferenceTransform;
            if((uint)A>=particles.Length||(uint)B>=particles.Length||(uint)C>=particles.Length)return false;
            var frame=ClothPreviewMeshBinding.TriangleFrame(particles[A],particles[B],particles[C]);
            if(!Affine(frame)||Math.Abs(frame.Determinant())<1e-18f)return false;
            var value=LocalTransform*frame;
            if(!Affine(value)||Math.Abs(value.Determinant())<1e-12f)return false;
            transform=value;return true;
        }
    }

    // HCT 2018.1, Operators 9.6: simulation triangles drive a transform set.
    // Consume serialized triangle/bone offsets and local transforms; never remap
    // by distance or assume that transform-set indices are FLVER bone indices.
    internal static class ClothBoneMapping
    {
        static int Int(IHkObject o,int fallback=-1)=>(int)Number(o,fallback);
        internal static ClothPreviewBoneBinding[] Read(IHkObject cloth,int simIndex,ClothPreviewDefinition definition,IHkObject[] skeletons,ICollection<string> notices)
        {
            var result=new List<ClothPreviewBoneBinding>();
            var ops=Array(Field(cloth,"operators")).Select(Unwrap).ToArray();
            var states=Array(Field(cloth,"clothStateDatas"));var buffers=Array(Field(cloth,"bufferDefinitions"));
            if(states.Count==0)return result.ToArray();
            var order=Array(Field(states[0],"operators")).Select(o=>Int(o)).ToArray();
            if(order.Any(i=>(uint)i>=ops.Length||ops[i]==null))return result.ToArray();
            int simulate=System.Array.FindIndex(order,i=>ops[i].Type.Name=="hclSimulateOperator"&&Int(Field(ops[i],"simClothIndex"))==simIndex);
            if(simulate<0)return result.ToArray();
            var inputs=Array(Field(ops[order[simulate]],"usedBuffers")).Select(b=>Int(Field(b,"bufferIndex")))
                .Where(i=>(uint)i<buffers.Count&&Int(Field(buffers[i],"type"))==1
                    &&Int(Field(buffers[i],"numVertices"))==definition.RestPositions.Length
                    &&Int(Field(buffers[i],"numTriangles"))==definition.Triangles.Length/3).ToHashSet();
            var transforms=Array(Field(cloth,"transformSetDefinitions"));
            foreach(int index in order)
            {
                var op=ops[index];if(op.Type.Name!="hclSimpleMeshBoneDeformOperator"||!inputs.Contains(Int(Field(op,"inputBufferIdx"))))continue;
                try
                {
                    int set=Int(Field(op,"outputTransformSetIdx"));
                    if((uint)set>=transforms.Count)throw new InvalidOperationException("invalid transform set");
                    string name=Text(Field(transforms[set],"name"));
                    var candidates=skeletons.Where(s=>Text(Field(s,"name"))==name).ToArray();
                    if(candidates.Length!=1)throw new InvalidOperationException("no unique named output skeleton");
                    var skeleton=candidates[0];var names=Array(Field(skeleton,"bones")).Select(b=>Text(Field(b,"name"))).ToArray();
                    var reference=ReferencePose(Array(Field(skeleton,"referencePose")).Select(p=>
                    {
                        var r=Array(Field(p,"rotation"));return r.Count==4?Matrix.CreateScale(Vec(Field(p,"scale")))
                            *Matrix.CreateFromQuaternion(new(Number(r[0]),Number(r[1]),Number(r[2]),Number(r[3])))
                            *Matrix.CreateTranslation(Vec(Field(p,"translation"))):default;
                    }).ToArray(),Array(Field(skeleton,"parentIndices")).Select(p=>Int(p)).ToArray());
                    var pairs=Array(Field(op,"triangleBonePairs"));var locals=Array(Field(op,"localBoneTransforms"));
                    if(pairs.Count!=locals.Count||pairs.Count>names.Length||reference.Length!=names.Length)throw new InvalidOperationException("inconsistent output arrays");
                    var pending=new List<ClothPreviewBoneBinding>();float error=0;
                    for(int i=0;i<pairs.Count;i++)
                    {
                        int boneOffset=Int(Field(pairs[i],"boneOffset")),triangleOffset=Int(Field(pairs[i],"triangleOffset"));
                        // hkMatrix4 = 64 bytes, each triangle = three 16-bit indices.
                        if(boneOffset<0||boneOffset%64!=0||triangleOffset<0||triangleOffset%6!=0)throw new InvalidOperationException("unaligned authored offsets");
                        int bone=boneOffset/64,at=triangleOffset/2;
                        if((uint)bone>=names.Length||at+2>=definition.Triangles.Length||string.IsNullOrEmpty(names[bone]))throw new InvalidOperationException("authored offset outside input/output");
                        var binding=new ClothPreviewBoneBinding(names[bone],definition.Triangles[at],definition.Triangles[at+1],definition.Triangles[at+2],MatrixValue(locals[i]),reference[bone],System.Array.IndexOf(order,index)<simulate);
                        if(!binding.TryEvaluate(definition.RestPositions,out var rest))throw new InvalidOperationException("singular triangle or local transform");
                        float e=MatrixError(rest,reference[bone]);error=Math.Max(error,e);
                        if(e>.002f)throw new InvalidOperationException("serialized local frame does not reconstruct output reference: "+e);
                        if(pending.Any(b=>b.Bone==binding.Bone)||result.Any(b=>b.Bone==binding.Bone))throw new InvalidOperationException("multiple output writers for the same bone");
                        pending.Add(binding);
                    }
                    result.AddRange(pending);notices.Add("Verified simulation-to-bone outputs: "+pending.Count+"; reference transform error "+error);
                }
                catch(InvalidOperationException e){notices.Add("Cloth bone output retained as animation: "+e.Message);}
            }
            return result.ToArray();
        }
    }
}
