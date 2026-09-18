using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Microsoft.Xna.Framework;
using Havoc.Objects;
using static DSAnimStudio.ClothPreviewData;

namespace DSAnimStudio
{
    internal static class ClothTagfileMeshMapping
    {
        static IHkObject[] Pointers(IHkObject o)=>Array(o).Select(Unwrap).Where(p=>p!=null).ToArray();
        static int Int(IHkObject o,int fallback=0)=>(int)Number(o,fallback);
        static Vector4 V4(IHkObject o)
        {
            var a=Array(o);return a.Count>=4?new(Number(a[0]),Number(a[1]),Number(a[2]),Number(a[3])):Vector4.Zero;
        }
        static Vector3 Direction(IHkObject o)
        {
            var packed=Array(Field(o,"values"));
            if(packed.Count==4)
            {
                float scale=BitConverter.Int32BitsToSingle(unchecked((ushort)Int(packed[3])<<16))*65536f;
                return new Vector3(Number(packed[0]),Number(packed[1]),Number(packed[2]))*scale;
            }
            var a=Array(o);return a.Count>=3?new(Number(a[0]),Number(a[1]),Number(a[2])):Vector3.Zero;
        }
        internal static ClothPreviewMeshBinding[] Read(IHkObject cloth,int simIndex,ClothPreviewDefinition definition,ClothMeshSource[] meshes,ICollection<string> notices)
        {
            if(meshes==null)return System.Array.Empty<ClothPreviewMeshBinding>();
            var buffers=Array(Field(cloth,"bufferDefinitions")).Select(Unwrap).ToArray();var ops=Pointers(Field(cloth,"operators"));
            var simBuffers=ops.Where(o=>o.Type.Name=="hclSimulateOperator"&&Int(Field(o,"simClothIndex"),-1)==simIndex)
                .SelectMany(o=>Array(Field(o,"usedBuffers"))).Select(b=>Int(Field(b,"bufferIndex"),-1))
                .Where(i=>(uint)i<buffers.Length&&Int(Field(buffers[i],"type"))==1).ToHashSet();
            var results=new List<ClothPreviewMeshBinding>();
            foreach(var op in ops.Where(o=>o.Type.Name.StartsWith("hclBoneSpaceMeshMeshDeform",StringComparison.Ordinal)||o.Type.Name.StartsWith("hclObjectSpaceMeshMeshDeform",StringComparison.Ordinal)))
            {
                int input=Int(Field(op,"inputBufferIdx"),-1),output=Int(Field(op,"outputBufferIdx"),-1);
                if(!simBuffers.Contains(input)||(uint)output>=buffers.Length)continue;
                var buffer=buffers[output];int count=Int(Field(buffer,"numVertices"));string name=Text(Field(buffer,"name"));
                if(Int(Field(buffer,"type"))!=4||count<=0||count>65536)continue;
                if(Int(Field(buffers[input],"numVertices"))!=definition.RestPositions.Length||Int(Field(buffers[input],"numTriangles"))!=definition.Triangles.Length/3)
                {notices.Add($"Cloth buffer {name}: simulation input topology does not match its Simulate operator");continue;}
                if(!meshes.Any(s=>s.Name==name&&s.Positions.Length==count))continue;
                try
                {
                    bool objectSpace=op.Type.Name.StartsWith("hclObjectSpace",StringComparison.Ordinal);
                    bool normals=false,tangents=false;
                    var vertices=objectSpace?DecodeObjectSpace(op,definition):Decode(op,definition,out normals,out tangents);
                    var binding=ClothMeshMapping.Bind(name,count,Int(Field(op,"scaleNormalBehaviour")),normals,tangents,vertices,definition.RestPositions,meshes,notices);
                    if(binding!=null)results.Add(binding);
                }
                catch(Exception e) when(e is InvalidOperationException or IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
                {notices.Add($"Cloth buffer {name}: unsupported authored MeshMeshDeform data ({e.Message}); original mesh retained");}
            }
            return results.ToArray();
        }
        static Matrix MatrixValue(IHkObject value)
        {
            var a=Array(value);if(a.Count!=16)throw new InvalidOperationException("triangle inverse bind matrix is not a sixteen-scalar matrix");
            return new Matrix(Number(a[0]),Number(a[1]),Number(a[2]),Number(a[3]),Number(a[4]),Number(a[5]),Number(a[6]),Number(a[7]),Number(a[8]),Number(a[9]),Number(a[10]),Number(a[11]),Number(a[12]),Number(a[13]),Number(a[14]),Number(a[15]));
        }
        static ClothMeshVertex[] DecodeObjectSpace(IHkObject op,ClothPreviewDefinition definition)
        {
            if(op.Type.Name!="hclObjectSpaceMeshMeshDeformPOperator")throw new InvalidOperationException("ObjectSpace PN/PNT channels require separate verification");
            var subset=Array(Field(op,"inputTrianglesSubset")).Select(p=>Int(p)).ToArray();
            var inverse=Array(Field(op,"triangleFromMeshTransforms")).Select(MatrixValue).ToArray();
            // An empty subset addresses the entire input triangle buffer. The
            // author inverse frames below still have to prove that ordering.
            if(subset.Length==0&&inverse.Length==definition.Triangles.Length/3)subset=Enumerable.Range(0,inverse.Length).ToArray();
            if(!ClothObjectSpaceMapping.RestFramesMatch(subset,inverse,definition.Triangles,definition.RestPositions,out _))
            {
                var transposed=inverse.Select(Matrix.Transpose).ToArray();
                if(!ClothObjectSpaceMapping.RestFramesMatch(subset,transposed,definition.Triangles,definition.RestPositions,out _))
                    throw new InvalidOperationException("authored triangle inverse bind frames do not match this simulation rest pose");
                inverse=transposed;
            }
            var locals=Array(Field(op,"localPs"));if(locals.Count==0)locals=Array(Field(op,"localUnpackedPs"));
            var deform=Field(op,"objectSpaceDeformer");var controls=Array(Field(deform,"controlBytes"));
            if(controls.Count>8192)throw new InvalidOperationException("block count exceeds mapping budget");
            var kinds=new[]{"four","three","two","one"};var blocks=kinds.Select(k=>Array(Field(deform,k+"BlendEntries"))).ToArray();var offsets=new int[4];
            int localIndex=0;var result=new List<ClothObjectSpaceVertex>();
            foreach(var control in controls)
            {
                int kind=Int(control);if(kind==4)continue;if(kind<0||kind>3)throw new InvalidOperationException("unverified ObjectSpace deformation control byte");
                if(offsets[kind]>=blocks[kind].Count||localIndex>=locals.Count)throw new InvalidOperationException("ObjectSpace block arrays are inconsistent");
                var block=blocks[kind][offsets[kind]++];var p=Array(Field(locals[localIndex++],"localPosition"));int blends=4-kind;
                var indices=Array(Field(block,"vertexIndices"));var bones=Array(Field(block,"boneIndices"));var weights=Array(Field(block,"boneWeights"));
                for(int vertex=0;vertex<indices.Count;vertex++)
                {
                    if(vertex>=p.Count)throw new InvalidOperationException("ObjectSpace positions are not vertex-aligned");
                    var influences=new List<ClothObjectSpaceWeight>();
                    for(int blend=0;blend<blends;blend++)
                    {
                        int slot=vertex*blends+blend;if(slot>=bones.Count||blends!=1&&slot>=weights.Count)throw new InvalidOperationException("ObjectSpace weights are not vertex-aligned");
                        float weight=blends==1?1:Number(weights[slot])/255f;if(weight>0)influences.Add(new(Int(bones[slot]),weight));
                    }
                    var packed=Array(Field(p[vertex],"values"));float tolerance=.00025f;
                    if(packed.Count==4)
                    {
                        float step=BitConverter.Int32BitsToSingle(unchecked((ushort)Int(packed[3])<<16))*65536f;
                        // Three rounded packed coordinates contribute at most
                        // sqrt(3)/2 quantization steps, plus float matrix error.
                        tolerance=Math.Max(tolerance,Math.Abs(step)*.8660254f+.00005f);
                    }
                    if(influences.Count>0)result.Add(new(Int(indices[vertex]),Direction(p[vertex]),influences.ToArray(),tolerance));
                }
            }
            return ClothObjectSpaceMapping.Convert(result.ToArray(),subset,inverse,definition.Triangles);
        }
        static ClothMeshVertex[] Decode(IHkObject op,ClothPreviewDefinition definition,out bool normals,out bool tangents)
        {
            var type=op.Type.Name;normals=type.Contains("PN");tangents=type.Contains("PNT");string suffix=tangents?(type.Contains("PNTB")?"PNTB":"PNT"):normals?"PN":"P";
            var locals=Array(Field(op,"local"+suffix+"s"));if(locals.Count==0)locals=Array(Field(op,"localUnpacked"+suffix+"s"));
            var deform=Field(op,"boneSpaceDeformer");var controls=Array(Field(deform,"controlBytes"));
            var subset=Array(Field(op,"inputTrianglesSubset")).Select(p=>Int(p)).ToArray();
            if(controls.Count>8192)throw new InvalidOperationException("block count exceeds mapping budget");
            var kinds=new[]{"four","three","two","one"};var blocks=kinds.Select(k=>Array(Field(deform,k+"BlendEntries"))).ToArray();var offsets=new int[4];
            int localIndex=0;var result=new List<ClothMeshVertex>();
            foreach(var control in controls)
            {
                int kind=Int(control);if(kind==4)continue;if(kind<0||kind>3)throw new InvalidOperationException("unknown deformation control byte");
                if(offsets[kind]>=blocks[kind].Count||localIndex>=locals.Count)throw new InvalidOperationException("deformation block arrays are inconsistent");
                var block=blocks[kind][offsets[kind]++];var local=locals[localIndex++];int blends=4-kind;
                var indices=Array(Field(block,"vertexIndices"));var bones=Array(Field(block,"boneIndices"));var p=Array(Field(local,"localPosition"));var n=Array(Field(local,"localNormal"));var t=Array(Field(local,"localTangent"));
                for(int vertex=0;vertex<indices.Count;vertex++)
                {
                    var influences=new List<ClothMeshInfluence>();
                    for(int blend=0;blend<blends;blend++)
                    {
                        int slot=vertex*blends+blend;if(slot>=bones.Count||slot>=p.Count)throw new InvalidOperationException("influence arrays are inconsistent");
                        var position=V4(p[slot]);var normal=normals&&slot<n.Count?Direction(n[slot]):Vector3.Zero;var tangent=tangents&&slot<t.Count?Direction(t[slot]):Vector3.Zero;
                        if(position==Vector4.Zero&&normal==Vector3.Zero&&tangent==Vector3.Zero)continue;
                        int triangle=Int(bones[slot]);if((uint)triangle>=subset.Length)throw new InvalidOperationException("triangle subset index outside range");
                        int at=subset[triangle]*3;if(at<0||at+2>=definition.Triangles.Length)throw new InvalidOperationException("simulation triangle index outside range");
                        influences.Add(new(definition.Triangles[at],definition.Triangles[at+1],definition.Triangles[at+2],position,normal,tangent));
                    }
                    if(influences.Count>0)result.Add(new(Int(indices[vertex]),influences.ToArray()));
                }
            }
            return result.ToArray();
        }
    }
}
