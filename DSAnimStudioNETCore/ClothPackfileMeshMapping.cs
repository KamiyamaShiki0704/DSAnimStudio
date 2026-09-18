using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HKX2;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    internal static class ClothPackfileMeshMapping
    {
        static Matrix Matrix(System.Numerics.Matrix4x4 m)=>new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
        static object[] Fields(object obj,string prefix)=>obj.GetType().GetFields().Where(f=>f.Name.StartsWith(prefix,StringComparison.Ordinal))
            .OrderBy(f=>int.Parse(f.Name.Substring(prefix.Length))).Select(f=>f.GetValue(obj)).ToArray();
        static int[] Ints(object obj,string prefix)=>Fields(obj,prefix).Select(Convert.ToInt32).ToArray();

        internal static ClothPreviewMeshBinding[] Read(hclClothData cloth,int simIndex,ClothPreviewDefinition definition,ClothMeshSource[] sources,ICollection<string> notices)
        {
            if(sources==null)return Array.Empty<ClothPreviewMeshBinding>();
            var results=new List<ClothPreviewMeshBinding>();
            foreach(var op in cloth.m_operators.OfType<hclObjectSpaceMeshMeshDeformPOperator>())
            {
                if(op.m_inputBufferIdx>=cloth.m_bufferDefinitions.Count||op.m_outputBufferIdx>=cloth.m_bufferDefinitions.Count)continue;
                var input=cloth.m_bufferDefinitions[(int)op.m_inputBufferIdx];var output=cloth.m_bufferDefinitions[(int)op.m_outputBufferIdx];
                if(input.m_type!=1||output.m_type!=4||input.m_numVertices!=definition.RestPositions.Length||input.m_numTriangles!=definition.Triangles.Length/3)continue;
                var subset=op.m_inputTrianglesSubset.Select(i=>(int)i).ToArray();var transforms=op.m_triangleFromMeshTransforms.Select(Matrix).ToArray();
                // Older operators do not carry the tagfile's usedBuffers table.
                // Verify every authored inverse triangle matrix against exactly
                // one simulation pose instead of assuming array order/subType.
                var matches=cloth.m_simClothDatas.Select((s,i)=>(s,i)).Where(p=>p.s.m_simClothPoses.Count>0)
                    .Where(p=>ClothObjectSpaceMapping.RestFramesMatch(subset,transforms,p.s.m_triangleIndices.Select(i=>(int)i).ToArray(),
                        p.s.m_simClothPoses[0].m_positions.Select(v=>new Vector3(v.X,v.Y,v.Z)).ToArray(),out _)).Select(p=>p.i).ToArray();
                if(matches.Length!=1||matches[0]!=simIndex)
                {notices.Add($"Cloth buffer {output.m_name}: no unique simulation pose matches all authored inverse triangle frames");continue;}
                try
                {
                    var vertices=ClothObjectSpaceMapping.Convert(Decode(op),subset,transforms,definition.Triangles);
                    var binding=ClothMeshMapping.Bind(output.m_name,checked((int)output.m_numVertices),(int)op.m_scaleNormalBehaviour,false,false,
                        vertices,definition.RestPositions,sources,notices);
                    if(binding!=null)results.Add(binding);
                }
                catch(Exception ex) when(ex is InvalidDataException or IndexOutOfRangeException or OverflowException)
                {notices.Add($"Cloth buffer {output.m_name}: unsupported authored ObjectSpace data ({ex.Message}); original mesh retained");}
            }
            return results.ToArray();
        }

        static ClothObjectSpaceVertex[] Decode(hclObjectSpaceMeshMeshDeformPOperator op)
        {
            var deform=op.m_objectSpaceDeformer;bool packed=op.m_localPs.Count>0;
            var locals=packed?op.m_localPs.Cast<object>().ToArray():op.m_localUnpackedPs.Cast<object>().ToArray();
            var kinds=new[]{"four","three","two","one"};
            var blocks=kinds.Select(k=>((IEnumerable)deform.GetType().GetField("m_"+k+"BlendEntries").GetValue(deform)).Cast<object>().ToArray()).ToArray();
            var offsets=new int[4];int local=0;var vertices=new List<ClothObjectSpaceVertex>();
            if(deform.m_controlBytes.Count>8192)throw new InvalidDataException("ObjectSpace block count exceeds the mapping budget.");
            foreach(int control in deform.m_controlBytes)
            {
                if(control==4)continue;
                if(control<0||control>3||offsets[control]>=blocks[control].Length||local>=locals.Length)
                    throw new InvalidDataException("ObjectSpace block controls and arrays disagree.");
                var block=blocks[control][offsets[control]++];var positionFields=Fields(locals[local++],"m_localPosition_");
                var indices=Ints(block,"m_vertexIndices_");var frames=Ints(block,"m_boneIndices_");var weights=Ints(block,"m_boneWeights_");int blends=4-control;
                for(int i=0;i<indices.Length;i++)
                {
                    if(indices[i]<deform.m_startVertexIndex||indices[i]>deform.m_endVertexIndex)continue;
                    Vector3 position;
                    if(packed)
                    {
                        int at=i*4;if(at+3>=positionFields.Length)throw new InvalidDataException("ObjectSpace packed position block is truncated.");
                        position=ClothObjectSpaceMapping.UnpackPosition((short)positionFields[at],(short)positionFields[at+1],(short)positionFields[at+2],unchecked((ushort)(short)positionFields[at+3]));
                    }
                    else
                    {
                        if(i>=positionFields.Length)throw new InvalidDataException("ObjectSpace unpacked position block is truncated.");
                        var p=(System.Numerics.Vector4)positionFields[i];position=new(p.X,p.Y,p.Z);
                    }
                    var influence=new List<ClothObjectSpaceWeight>();
                    for(int j=0;j<blends;j++)
                    {
                        int at=i*blends+j;if(at>=frames.Length||blends>1&&at>=weights.Length)throw new InvalidDataException("ObjectSpace influence block is truncated.");
                        float weight=blends==1?1:weights[at]/255f;if(weight>0)influence.Add(new(frames[at],weight));
                    }
                    if(influence.Count>0)vertices.Add(new(indices[i],position,influence.ToArray()));
                }
            }
            return vertices.ToArray();
        }
    }
}
