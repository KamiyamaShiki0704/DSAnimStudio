using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HKX2;
using Microsoft.Xna.Framework;
using SoulsFormats;

namespace DSAnimStudio
{
    /// <summary>Reads authored 64-bit Havok 2014 cloth without inventing mesh or bone correspondences.</summary>
    public static class ClothPackfileReader
    {
        public static hkRootLevelContainer ReadRoot(byte[] bytes)
        {
            if(bytes==null||bytes.Length<64)throw new InvalidDataException("Cloth packfile header is truncated.");
            if(bytes.Length>64*1024*1024)throw new NotSupportedException("Cloth HKX exceeds the 64 MiB inspection budget.");
            if(BitConverter.ToUInt32(bytes,0)!=0x57E0E057||BitConverter.ToUInt32(bytes,4)!=0x10C0C010)
                throw new InvalidDataException("Cloth HKX is not a Havok packfile.");
            string version=Encoding.ASCII.GetString(bytes,0x28,16).Split('\0')[0];
            if(bytes[0x10]!=8||bytes[0x11]!=1||BitConverter.ToInt32(bytes,0x0C)!=11||version!="hk_2014.1.0-r1")
                throw new NotSupportedException($"Cloth packfile {version} requires a separately verified layout; connected layout is little-endian 64-bit Havok 2014.1.");
            var root=new PackFileDeserializer{ValidateClassSignatures=true}.Deserialize(new BinaryReaderEx(false,bytes));
            return root as hkRootLevelContainer??throw new InvalidDataException("Cloth packfile root is not hkRootLevelContainer.");
        }

        public static ClothPreviewDefinition[] Read(byte[] bytes,ClothMeshSource[] sources=null)
        {
            var root=ReadRoot(bytes);
            var variants=root.m_namedVariants.Select(v=>v.m_variant).ToArray();
            var skeletons=variants.OfType<hkaAnimationContainer>().SelectMany(a=>a.m_skeletons??new()).ToArray();
            var result=new List<ClothPreviewDefinition>();
            foreach(var cloth in variants.OfType<hclClothContainer>().SelectMany(c=>c.m_clothDatas??new()))
            for(int simIndex=0;simIndex<cloth.m_simClothDatas.Count;simIndex++)
            {
                var sim=cloth.m_simClothDatas[simIndex];
                if(sim.m_simClothPoses==null||sim.m_simClothPoses.Count==0)continue;
                var positions=sim.m_simClothPoses[0].m_positions.Select(v=>new Vector3(v.X,v.Y,v.Z)).ToArray();
                if(positions.Length==0||positions.Length>32768)throw new InvalidDataException("Cloth particle count is outside the 1..32768 preview range.");
                if(positions.Any(p=>!Finite(p)))throw new InvalidDataException("Cloth rest positions contain non-finite values.");
                if(sim.m_particleDatas.Count!=positions.Length)throw new InvalidDataException("Cloth particle data and rest pose sizes differ.");
                var inverse=sim.m_particleDatas.Select(p=>p.m_invMass).ToArray();
                if(inverse.Any(m=>!float.IsFinite(m)||m<0))throw new InvalidDataException("Cloth inverse masses are invalid.");
                var fixedParticles=new bool[positions.Length];
                foreach(int index in sim.m_fixedParticles)
                {
                    if((uint)index>=positions.Length)throw new InvalidDataException("Fixed cloth particle is outside the authored particle array.");
                    fixedParticles[index]=true;
                }
                var triangles=sim.m_triangleIndices.Select(n=>(int)n).ToArray();
                if(triangles.Length%3!=0||triangles.Any(n=>(uint)n>=positions.Length))throw new InvalidDataException("Cloth triangle topology is invalid.");
                var links=new List<ClothPreviewLink>();var limitations=new HashSet<string>();
                void Link(int a,int b,float rest,float stiffness,bool stretch)
                {
                    if((uint)a>=positions.Length||(uint)b>=positions.Length||!float.IsFinite(rest)||rest<0||!float.IsFinite(stiffness))
                        throw new InvalidDataException("Cloth link constraint is outside the authored particle array or has invalid parameters.");
                    links.Add(new(a,b,rest,Math.Clamp(stiffness,0,1),stretch));
                }
                foreach(var constraint in sim.m_staticConstraintSets)
                {
                    if(constraint is hclStandardLinkConstraintSet standard)foreach(var link in standard.m_links)Link(link.m_particleA,link.m_particleB,link.m_restLength,link.m_stiffness,false);
                    else if(constraint is hclStretchLinkConstraintSet stretch)foreach(var link in stretch.m_links)Link(link.m_particleA,link.m_particleB,link.m_restLength,link.m_stiffness,true);
                    else limitations.Add(constraint.GetType().Name+" not solved");
                }
                var bindings=new ClothPreviewWeight[positions.Length][];
                foreach(var move in cloth.m_operators.OfType<hclMoveParticlesOperator>().Where(o=>o.m_simClothIndex==simIndex))
                {
                    var candidates=cloth.m_operators.OfType<hclObjectSpaceSkinOperator>().Where(o=>o.m_outputBufferIndex==move.m_refBufferIdx).ToArray();
                    if(candidates.Length!=1){limitations.Add("Fixed particle input has no unique supported skin operator");continue;}
                    var skin=candidates[0];
                    var transform=skin.m_transformSetIndex<cloth.m_transformSetDefinitions.Count?cloth.m_transformSetDefinitions[(int)skin.m_transformSetIndex]:null;
                    var skeleton=transform==null?null:skeletons.SingleOrDefault(s=>s.m_name==transform.m_name);
                    if(skeleton==null){limitations.Add("Fixed particle input transform set has no named skeleton");continue;}
                    var weights=SkinWeights(skin,skeleton,limitations);
                    foreach(var pair in move.m_vertexParticlePairs)
                    {
                        if(pair.m_particleIndex>=positions.Length)throw new InvalidDataException("MoveParticles index is outside the authored particle array.");
                        if(weights.TryGetValue(pair.m_vertexIndex,out var mapped))bindings[pair.m_particleIndex]=mapped;
                    }
                }
                if(fixedParticles.Where((fixedParticle,i)=>fixedParticle&&bindings[i]==null).Any())
                    limitations.Add("Some fixed particles have no verified bone binding; held in model reference space");
                var gravity=sim.m_simulationInfo.m_gravity;
                if(!Finite(new(gravity.X,gravity.Y,gravity.Z))||!float.IsFinite(sim.m_simulationInfo.m_globalDampingPerSecond))
                    throw new InvalidDataException("Cloth simulation gravity or damping is non-finite.");
                limitations.Add("Experimental PBD links and authored body collision preview; native operator order, unsupported constraints and self/complex collisions remain partial; no verified FLVER deformation yet");
                var definition=new ClothPreviewDefinition(cloth.m_name+" / "+sim.m_name,positions,inverse,fixedParticles,triangles,links.ToArray(),bindings,
                    new(gravity.X,gravity.Y,gravity.Z),Math.Clamp(sim.m_simulationInfo.m_globalDampingPerSecond,0,1),limitations.ToArray());
                var meshBindings=ClothPackfileMeshMapping.Read(cloth,simIndex,definition,sources,limitations);
                if(meshBindings.Length>0)
                {
                    limitations.Remove("Experimental PBD links and authored body collision preview; native operator order, unsupported constraints and self/complex collisions remain partial; no verified FLVER deformation yet");
                    limitations.Add("Experimental PBD links, authored body collisions and ObjectSpace P mesh deformation; native operator order, unsupported constraints and self/complex collisions remain partial");
                }
                var collisions=ReadCollisions(sim,cloth,skeletons,limitations);
                result.Add(definition with{MeshBindings=meshBindings,Collisions=collisions,Limitations=limitations.ToArray()});
            }
            if(result.Count==0)throw new NotSupportedException("No hclSimClothData rest poses were found in this packfile.");
            return result.ToArray();
        }

        static Matrix Xna(System.Numerics.Matrix4x4 m)=>new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
        static Vector3 Xna(System.Numerics.Vector4 v)=>new(v.X,v.Y,v.Z);
        static ClothCollisionDefinition ReadCollisions(hclSimClothData sim,hclClothData cloth,hkaSkeleton[] skeletons,ICollection<string> notices)
        {
            var map=sim.m_collidableTransformMap;int set=map?.m_transformSetIndex??-1;
            var indices=map?.m_transformIndices?.ToArray()??Array.Empty<uint>();var offsets=map?.m_offsets?.Select(Xna).ToArray()??Array.Empty<Matrix>();
            var collidables=sim.m_perInstanceCollidables?.ToArray()??Array.Empty<hclCollidable>();
            var candidates=(uint)set<cloth.m_transformSetDefinitions.Count?skeletons.Where(s=>s.m_name==cloth.m_transformSetDefinitions[set].m_name).ToArray():Array.Empty<hkaSkeleton>();
            var skeleton=candidates.Length==1?candidates[0]:null;
            var reference=skeleton==null?Array.Empty<Matrix>():ClothPreviewData.ReferencePose(skeleton.m_referencePose.Select(Xna).ToArray(),skeleton.m_parentIndices.Select(p=>(int)p).ToArray());
            var descriptors=new List<ClothCollisionCollider>();
            for(int i=0;i<collidables.Length;i++)
            {
                var c=collidables[i];string shape=c?.m_shape?.GetType().Name??"missing";Vector3 start=Vector3.Zero,end=Vector3.Zero;float radius=float.NaN;
                if(c?.m_shape is hclCapsuleShape capsule){start=Xna(capsule.m_start);end=Xna(capsule.m_end);radius=capsule.m_radius;}
                else if(c?.m_shape is hclSphereShape sphere){start=end=Xna(sphere.m_sphere.m_pos);radius=sphere.m_sphere.m_pos.W;}
                float endRadius=radius;
                if(c?.m_shape is hclTaperedCapsuleShape tapered){start=Xna(tapered.m_small);end=Xna(tapered.m_big);radius=tapered.m_smallRadius;endRadius=tapered.m_bigRadius;}
                var authored=c==null?default:Xna(c.m_transform);var rest=authored;var offset=i<offsets.Length?offsets[i]:default;
                int bone=skeleton!=null&&i<indices.Length&&indices[i]<skeleton.m_bones.Count?(int)indices[i]:-1;var refBone=(uint)bone<reference.Length?reference[bone]:default;
                bool verified=collidables.Length==indices.Length&&indices.Length==offsets.Length&&bone>=0&&ClothPreviewData.VerifyCollisionTransform(rest,offset,refBone,out rest,out offset,true);
                if((shape is "hclSphereShape" or "hclCapsuleShape")&&(!Finite(start)||!Finite(end)||!float.IsFinite(radius)||radius<0))verified=false;
                descriptors.Add(new(i,c?.m_name??"missing",shape,bone>=0?skeleton.m_bones[bone].m_name:null,rest,offset,refBone,start,end,radius,c!=null,verified,true,endRadius,authored));
            }
            var radii=sim.m_particleDatas.Select(p=>p.m_radius).ToArray();var friction=sim.m_particleDatas.Select(p=>p.m_friction).ToArray();
            if(radii.Any(v=>!float.IsFinite(v)||v<0)||friction.Any(v=>!float.IsFinite(v)||v<0))throw new InvalidDataException("Cloth particle collision radius/friction is invalid.");
            var masks=sim.m_staticCollisionMasks?.ToArray()??Array.Empty<uint>();
            var data=new ClothCollisionDefinition(descriptors.ToArray(),radii,friction,masks,set,indices,offsets,ClothPreviewData.PreviewCollisionFilter(radii.Length,descriptors.Count,masks));
            ClothPreviewData.CollisionNotices(data,notices);return data;
        }
        static bool Finite(Vector3 p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
        static readonly string[] BlendNames={"one","two","three","four"};
        static int[] NumberedFields(object instance,string prefix)=>instance.GetType().GetFields()
            .Where(f=>f.Name.StartsWith(prefix,StringComparison.Ordinal)).OrderBy(f=>int.Parse(f.Name.Substring(prefix.Length)))
            .Select(f=>Convert.ToInt32(f.GetValue(instance))).ToArray();
        static Dictionary<int,ClothPreviewWeight[]> SkinWeights(hclObjectSpaceSkinOperator skin,hkaSkeleton skeleton,HashSet<string> limitations)
        {
            var result=new Dictionary<int,ClothPreviewWeight[]>();var deform=skin.m_objectSpaceDeformer;
            for(int n=1;n<=4;n++)
            {
                var blocks=(IEnumerable)deform.GetType().GetField("m_"+BlendNames[n-1]+"BlendEntries").GetValue(deform);
                foreach(var block in blocks)
                {
                    var vertices=NumberedFields(block,"m_vertexIndices_");var indices=NumberedFields(block,"m_boneIndices_");var amounts=NumberedFields(block,"m_boneWeights_");
                    for(int i=0;i<vertices.Length;i++)
                    {
                        int vertex=vertices[i];if(vertex<deform.m_startVertexIndex||vertex>deform.m_endVertexIndex)continue;
                        var weights=new List<ClothPreviewWeight>();bool valid=true;
                        for(int k=0;k<n;k++)
                        {
                            int at=i*n+k;if(at>=indices.Length||indices[at]>=skin.m_transformSubset.Count){valid=false;break;}
                            int bone=skin.m_transformSubset[indices[at]];if(bone>=skeleton.m_bones.Count){valid=false;break;}
                            float weight=n==1?1:at<amounts.Length?amounts[at]/255f:0;
                            if(weight>0)weights.Add(new(skeleton.m_bones[bone].m_name,weight));
                        }
                        if(!valid||weights.Count==0){limitations.Add("Skin block has an invalid authored transform subset mapping");continue;}
                        // SIMD tail lanes repeat the last vertex. Keep its first
                        // verified influence set rather than inventing vertices.
                        if(result.TryGetValue(vertex,out var prior))
                        {
                            if(!prior.SequenceEqual(weights))limitations.Add("Repeated skin vertex has conflicting influence sets");
                        }
                        else result.Add(vertex,weights.ToArray());
                    }
                }
            }
            return result;
        }
    }
}
