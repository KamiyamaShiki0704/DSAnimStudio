using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SoulsFormats;

namespace DSAnimStudio
{
    public sealed record ClothMeshAlias(int Source,int Target,Vector3 Normal,Vector3 Tangent,bool HasTangent);
    public sealed record ClothMeshSource(int MeshIndex,string Name,Vector3[] Positions,ClothMeshAlias[] PositionAliases=null)
    {
        public static ClothMeshSource[] FromFlver(FLVER2 flver,CLM2 clm2=null)=>flver.Meshes.Select((m,i)=>new ClothMeshSource(i,
            m.NodeIndex>=0&&m.NodeIndex<flver.Nodes.Count?flver.Nodes[m.NodeIndex].Name:"",
            m.Vertices.Select(v=>new Vector3(v.Position.X,v.Position.Y,v.Position.Z)).ToArray(),
            clm2!=null&&i<clm2.Meshes.Count?clm2.Meshes[i].Where(a=>a.Unk00>=0&&a.Unk00<m.Vertices.Count&&a.Unk02>=0&&a.Unk02<m.Vertices.Count).Select(a=>
            {
                var target=m.Vertices[a.Unk02];var tangent=target.Tangents.FirstOrDefault();
                return new ClothMeshAlias(a.Unk00,a.Unk02,new(target.Normal.X,target.Normal.Y,target.Normal.Z),new(tangent.X,tangent.Y,tangent.Z),target.Tangents.Count>0);
            }).ToArray():null)).ToArray();
    }
    public sealed record ClothMeshInfluence(int A,int B,int C,Vector4 Position,Vector3 Normal,Vector3 Tangent);
    public sealed record ClothMeshVertex(int Index,ClothMeshInfluence[] Influences,float RestTolerance=.00025f);

    // An authored triangle-frame attachment, not a proximity-derived skin.
    // Output positions and directions have the same space as the input particles.
    public sealed class ClothPreviewMeshBinding
    {
        readonly record struct Triangle(int A,int B,int C);
        readonly record struct Influence(int Frame,Vector4 Position,Vector3 Normal,Vector3 Tangent);
        readonly Triangle[] triangles;
        readonly Influence[][] influences;
        readonly Matrix[] frames;
        readonly Matrix[] restInverseFrames, directionFrames;
        readonly bool[] validRestFrames;
        readonly float[] restNormalLengths;
        readonly int normalScale;
        public int MeshIndex {get;}
        public string Name {get;}
        public int VertexCount {get;}
        public int[] VertexIndices {get;}
        public bool HasNormals {get;}
        public bool HasTangents {get;}
        public bool NeedsDirectionTransport => !HasNormals || !HasTangents;
        public int[] PositionAliasIndices {get;}
        public float RestError {get;internal set;}
        public float RestTolerance {get;}
        internal float[] VertexRestTolerances {get;}
        internal int[] DirectParticles {get;set;}
        public bool IsDirectCopy=>DirectParticles!=null;
        internal ClothPreviewMeshBinding(ClothMeshSource source,ClothMeshVertex[] vertices,Vector3[] rest,int scale,bool normals,bool tangents)
        {
            MeshIndex=source.MeshIndex;Name=source.Name;VertexCount=source.Positions.Length;normalScale=scale;HasNormals=normals;HasTangents=tangents;
            var expanded=vertices.ToDictionary(v=>v.Index);var aliasIndices=new List<int>();
            foreach(var alias in source.PositionAliases??System.Array.Empty<ClothMeshAlias>())
            {
                if((uint)alias.Source>=source.Positions.Length||(uint)alias.Target>=source.Positions.Length||expanded.ContainsKey(alias.Target)||!expanded.TryGetValue(alias.Source,out var original)||Vector3.Distance(source.Positions[alias.Source],source.Positions[alias.Target])>1e-6f)continue;
                if(original.Influences.Any(i=>Math.Abs(TriangleFrame(rest[i.A],rest[i.B],rest[i.C]).Determinant())<1e-18f))continue;
                // CLM links duplicated positions; preserve the target's own
                // hard-edge normal, tangent and (in the renderer) UV data.
                var copies=original.Influences.Select(i=>
                {
                    var frame=TriangleFrame(rest[i.A],rest[i.B],rest[i.C]);var inverse=Matrix.Invert(frame);float weight=i.Position.W;
                    return i with{Normal=Vector3.TransformNormal(alias.Normal,inverse)*weight,Tangent=alias.HasTangent?Vector3.TransformNormal(alias.Tangent,inverse)*weight:Vector3.Zero};
                }).ToArray();
                expanded[alias.Target]=new(alias.Target,copies,original.RestTolerance);aliasIndices.Add(alias.Target);
            }
            vertices=expanded.Values.OrderBy(v=>v.Index).ToArray();PositionAliasIndices=aliasIndices.ToArray();
            VertexRestTolerances=vertices.Select(v=>v.RestTolerance).ToArray();RestTolerance=VertexRestTolerances.Max();
            var lookup=new Dictionary<Triangle,int>();var ts=new List<Triangle>();VertexIndices=vertices.Select(v=>v.Index).ToArray();
            influences=vertices.Select(v=>v.Influences.Select(i=>
            {
                var triangle=new Triangle(i.A,i.B,i.C);if(!lookup.TryGetValue(triangle,out int frame)){lookup[triangle]=frame=ts.Count;ts.Add(triangle);}
                return new Influence(frame,i.Position,i.Normal,i.Tangent);
            }).ToArray()).ToArray();
            triangles=ts.ToArray();frames=new Matrix[triangles.Length];restNormalLengths=new float[triangles.Length];
            if(NeedsDirectionTransport){restInverseFrames=new Matrix[triangles.Length];directionFrames=new Matrix[triangles.Length];validRestFrames=new bool[triangles.Length];}
            for(int i=0;i<triangles.Length;i++)
            {
                var t=triangles[i];var frame=TriangleFrame(rest[t.A],rest[t.B],rest[t.C]);restNormalLengths[i]=frame.Backward.Length();
                if(NeedsDirectionTransport)
                {
                    float determinant=frame.Determinant();validRestFrames[i]=float.IsFinite(determinant)&&Math.Abs(determinant)>1e-18f;
                    restInverseFrames[i]=validRestFrames[i]?Matrix.Invert(frame):Matrix.Identity;
                }
            }
        }
        public static Matrix TriangleFrame(Vector3 a,Vector3 b,Vector3 c)
        {
            var center=(a+b+c)/3;var x=a-center;var y=b-center;var z=Vector3.Cross(x,y);
            return new Matrix(x.X,x.Y,x.Z,0,y.X,y.Y,y.Z,0,z.X,z.Y,z.Z,0,center.X,center.Y,center.Z,1);
        }
        public void Evaluate(ReadOnlySpan<Vector3> particles,Span<Vector3> positions,Span<Vector3> normals=default,Span<Vector3> tangents=default,Span<Matrix> frameDeltas=default)
        {
            if(positions.Length<VertexCount||!normals.IsEmpty&&normals.Length<VertexCount||!tangents.IsEmpty&&tangents.Length<VertexCount||!frameDeltas.IsEmpty&&frameDeltas.Length<VertexCount)
                throw new ArgumentException("Cloth output arrays must cover the complete target mesh.");
            for(int i=0;i<triangles.Length;i++)
            {
                var t=triangles[i];var frame=TriangleFrame(particles[t.A],particles[t.B],particles[t.C]);
                // The source normalScale=1 uses the current cross product. For
                // 0/2 the normal-axis scale is kept/inverted relative to rest.
                float length=frame.Backward.Length(),rest=restNormalLengths[i];
                if(normalScale!=1&&length>1e-12f)frame.Backward*=normalScale==0?rest/length:rest*rest/(length*length);
                frames[i]=frame;
                if(NeedsDirectionTransport&&!frameDeltas.IsEmpty)
                    directionFrames[i]=validRestFrames[i]?restInverseFrames[i]*frame:Matrix.Identity;
            }
            for(int v=0;v<VertexIndices.Length;v++)
            {
                Vector3 p=Vector3.Zero,n=Vector3.Zero,t=Vector3.Zero;
                var delta=new Matrix();
                foreach(var i in influences[v])
                {
                    var frame=frames[i.Frame];var pp=Vector4.Transform(i.Position,frame);p+=new Vector3(pp.X,pp.Y,pp.Z);
                    if(HasNormals&&!normals.IsEmpty)n+=Vector3.TransformNormal(i.Normal,frame);
                    if(HasTangents&&!tangents.IsEmpty)t+=Vector3.TransformNormal(i.Tangent,frame);
                    // Missing output channels use the same authored triangles
                    // and weights as P. This transports the original vertex
                    // basis for preview; it does not execute UpdateVertexFrames.
                    if(NeedsDirectionTransport&&!frameDeltas.IsEmpty)delta+=directionFrames[i.Frame]*i.Position.W;
                }
                int index=VertexIndices[v];positions[index]=DirectParticles==null?p:particles[DirectParticles[v]];
                if(HasNormals&&!normals.IsEmpty)normals[index]=n.LengthSquared()>1e-20f?Vector3.Normalize(n):Vector3.Up;
                if(HasTangents&&!tangents.IsEmpty)tangents[index]=t.LengthSquared()>1e-20f?Vector3.Normalize(t):Vector3.Right;
                if(!frameDeltas.IsEmpty)frameDeltas[index]=NeedsDirectionTransport?delta:Matrix.Identity;
            }
        }
    }

    public static class ClothMeshMapping
    {
        public static ClothPreviewMeshBinding BindCopy(string name,int count,IReadOnlyDictionary<int,int> targets,Vector3[] rest,int[] topology,ClothMeshSource[] sources,ICollection<string> notices)
        {
            if(targets.Count==0||targets.Any(p=>(uint)p.Key>=count||(uint)p.Value>=rest.Length)||topology.Length%3!=0||topology.Any(i=>(uint)i>=rest.Length))
            {notices.Add("CopyVertices target range/topology is invalid");return null;}
            var candidates=sources.Where(s=>s.Name==name&&s.Positions.Length==count&&targets.All(p=>Vector3.Distance(s.Positions[p.Key],rest[p.Value])<.00025f)).ToArray();
            if(candidates.Length!=1){notices.Add($"CopyVertices {name}: {candidates.Length} same-index rest matches; no proximity binding used");return null;}
            var frames=new Dictionary<int,(int A,int B,int C)>();
            for(int i=0;i<topology.Length;i+=3)
            {
                var t=(A:topology[i],B:topology[i+1],C:topology[i+2]);
                if(Math.Abs(ClothPreviewMeshBinding.TriangleFrame(rest[t.A],rest[t.B],rest[t.C]).Determinant())<1e-18f)continue;
                frames.TryAdd(t.A,t);frames.TryAdd(t.B,t);frames.TryAdd(t.C,t);
            }
            var ordered=targets.OrderBy(p=>p.Key).ToArray();
            var vertices=ordered.Select(p=>
            {
                var t=frames.GetValueOrDefault(p.Value,(p.Value,p.Value,p.Value));
                return new ClothMeshVertex(p.Key,new[]{new ClothMeshInfluence(t.Item1,t.Item2,t.Item3,new Vector4(0,0,0,1),Vector3.Zero,Vector3.Zero)});
            }).ToArray();
            var source=candidates[0] with{PositionAliases=null};
            var result=new ClothPreviewMeshBinding(source,vertices,rest,1,false,false){DirectParticles=ordered.Select(p=>p.Value).ToArray()};
            result.RestError=ordered.Max(p=>Vector3.Distance(source.Positions[p.Key],rest[p.Value]));
            notices.Add("CopyVertices uses exact authored particle positions; incident-triangle direction transport is a preview, not native normal/tangent operators");
            return result;
        }
        // Names/counts identify declared buffers; every authored output vertex
        // must also reconstruct its exact same-index FLVER rest position.
        public static ClothPreviewMeshBinding Bind(string name,int count,int normalScale,bool normals,bool tangents,
            ClothMeshVertex[] vertices,Vector3[] rest,ClothMeshSource[] sources,ICollection<string> notices)
        {
            if(sources==null||vertices.Length==0)return null;
            if(normalScale<0||normalScale>2){notices.Add($"Cloth buffer {name}: unsupported scaleNormalBehaviour {normalScale}");return null;}
            var ordered=vertices.GroupBy(v=>v.Index).Select(g=>g.First()).OrderBy(v=>v.Index).ToArray();
            if(ordered.Any(v=>v.Index<0||v.Index>=count||!float.IsFinite(v.RestTolerance)||v.RestTolerance<=0||v.Influences.Length==0||v.Influences.Any(i=>(uint)i.A>=rest.Length||(uint)i.B>=rest.Length||(uint)i.C>=rest.Length)))
            {notices.Add($"Cloth buffer {name}: invalid authored deformation indices");return null;}
            var accepted=new List<ClothPreviewMeshBinding>();float best=float.PositiveInfinity;
            foreach(var source in sources.Where(s=>s.Name==name&&s.Positions.Length==count))
            {
                var mapping=new ClothPreviewMeshBinding(source,ordered,rest,normalScale,normals,tangents);var output=new Vector3[count];mapping.Evaluate(rest,output);
                float error=0;bool verified=true;for(int j=0;j<mapping.VertexIndices.Length;j++){int i=mapping.VertexIndices[j];float e=Vector3.Distance(output[i],source.Positions[i]);if(!float.IsFinite(e)){error=float.PositiveInfinity;verified=false;break;}error=Math.Max(error,e);verified&=e<=mapping.VertexRestTolerances[j];}
                best=Math.Min(best,error);mapping.RestError=error;if(verified)accepted.Add(mapping);
            }
            if(accepted.Count!=1){notices.Add($"Cloth buffer {name}/{count}: exact rest verification found {accepted.Count} unambiguous FLVER matches (best error {best:G5} m); original mesh retained");return null;}
            if(normalScale!=1)notices.Add($"Cloth buffer {name}: keep/invert normal-axis scaling is an unverified preview convention");
            if(accepted[0].NeedsDirectionTransport)notices.Add($"Cloth buffer {name}: missing direction channels use authored triangle-weight basis transport for preview, not native UpdateVertexFrames");
            return accepted[0];
        }
    }
}
