using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using SoulsFormats;

namespace DSAnimStudio
{
    public static class FxrModelGeometry
    {
        public static List<(FxrModelVertex[] Vertices,FxrMaterialProfile Material)> Build(FLVER2 model,FxrMaterialProfile[] materials,Action<string> notice,CancellationToken cancellation=default)
        {
            var parts=new List<(FxrModelVertex[],FxrMaterialProfile)>();
            System.Numerics.Matrix4x4 NodeTransform(int node)
            {
                var matrix=System.Numerics.Matrix4x4.Identity;
                for(int i=0;i<model.Nodes.Count&&node>=0&&node<model.Nodes.Count;i++)
                {matrix*=model.Nodes[node].ComputeLocalTransform();node=model.Nodes[node].ParentIndex;}
                return matrix;
            }
            foreach(var mesh in model.Meshes)
            {
                cancellation.ThrowIfCancellationRequested();
                var faces=mesh.FaceSets.FirstOrDefault();if(faces==null)continue;
                var profile=materials?.ElementAtOrDefault(mesh.MaterialIndex)??FxrModelMaterial.Resolve(model.Materials[mesh.MaterialIndex]);
                var vertices=new List<FxrModelVertex>();
                foreach(int index in faces.Triangulate(mesh.Vertices.Count<ushort.MaxValue))
                {
                    if(index<0||index>=mesh.Vertices.Count)continue;
                    if((vertices.Count&4095)==0)cancellation.ThrowIfCancellationRequested();
                    var v=mesh.Vertices[index];var pos=v.Position;var normal=v.Normal;var tangent=v.Tangents.FirstOrDefault();
                    var tangent2=v.Tangents.Count>1?v.Tangents[1]:tangent;
                    if(!mesh.UseBoneWeights)
                    {
                        var transform=NodeTransform(v.NormalW>=0?v.NormalW:mesh.NodeIndex);
                        pos=System.Numerics.Vector3.Transform(pos,transform);
                        if(System.Numerics.Matrix4x4.Invert(transform,out var inverse))normal=System.Numerics.Vector3.TransformNormal(normal,System.Numerics.Matrix4x4.Transpose(inverse));
                        var t=System.Numerics.Vector3.TransformNormal(new(tangent.X,tangent.Y,tangent.Z),transform);tangent=new(t,tangent.W*Math.Sign(transform.GetDeterminant()));
                        var t2=System.Numerics.Vector3.TransformNormal(new(tangent2.X,tangent2.Y,tangent2.Z),transform);tangent2=new(t2,tangent2.W*Math.Sign(transform.GetDeterminant()));
                    }
                    Vector2 Uv(FxrMaterialSampler sampler)
                    {
                        int channel=sampler?.UvIndex??0;
                        if(channel>=v.UVs.Count){notice?.Invoke($"Material {profile.Name}: UV{channel} absent; UV0 fallback.");channel=0;}
                        var uv=v.UVs.ElementAtOrDefault(channel);return new Vector2(uv.X,uv.Y)*(sampler?.Scale??Vector2.One);
                    }
                    var color=v.Colors.FirstOrDefault();
                    vertices.Add(new(){Position=pos,Normal=normal,Tangent=tangent,Tangent2=tangent2,Color=v.Colors.Count==0?Color.White:new Color(color.R,color.G,color.B,color.A),
                        Uv=Uv(profile.Base),EmissiveUv=Uv(profile.Emissive),NormalUv=Uv(profile.Normal),SpecularUv=Uv(profile.Reflectance),
                        Albedo2Uv=Uv(profile.Albedo2),Normal2Uv=Uv(profile.Normal2),Specular2Uv=Uv(profile.Reflectance2),MaskUv=Uv(profile.AuxiliarySampler)});
                }
                if(profile.Base==null)notice?.Invoke($"Material {profile.Name}: FXR color fallback; no resolved albedo/emissive sampler.");
                parts.Add((vertices.ToArray(),profile));
            }
            return parts;
        }
    }
}
