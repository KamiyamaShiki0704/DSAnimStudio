using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using HKX2;
using Havoc.Objects;
using Havoc.IO.Tagfile.Binary;
using SoulsFormats;
using DSAnimStudio.NativePhysics;

namespace DSAnimStudio
{
    // Only scene geometry is translated. The original Cloth operators,
    // constraints and collision data are passed unchanged to the native loader.
    public sealed class NativeClothAsset
    {
        public byte[] Cloth, Scene;
        public NativePhysicsDescription Description;
        static object Unwrap(object o)=>o is IHkObject hk?ClothPreviewData.Unwrap(hk):o;
        static object Field(object o,string name)=>Unwrap(o) is IHkObject hk?ClothPreviewData.Field(hk,name):o?.GetType().GetField("m_"+name)?.GetValue(o);
        static object Value(object o)=>Unwrap(o) is IHkObject hk?hk.Value:o;
        static object[] Items(object o)=>Value(o) is IEnumerable list?list.Cast<object>().ToArray():System.Array.Empty<object>();
        static string Text(object o)=>Value(o) as string??"";
        static int Int(object o)=>Convert.ToInt32(Value(o));
        static float Num(object o)=>Convert.ToSingle(Value(o));
        static string TypeName(object o)=>Unwrap(o) is IHkObject hk?hk.Type.Name:o?.GetType().Name;
        static IEnumerable<object> Objects(object root)
        {
            var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);var pending=new Stack<object>();pending.Push(root);
            while(pending.Count>0)
            {
                var o=Unwrap(pending.Pop());if(o==null||!seen.Add(o))continue;yield return o;
                if(o is HkClass c){foreach(var v in c.Value.Values)pending.Push(v);}
                else if(o is HkArray a){if(a.Value!=null)foreach(var v in a.Value)pending.Push(v);}
                else if(o is IHavokObject){foreach(var f in o.GetType().GetFields())if(f.Name.StartsWith("m_"))pending.Push(f.GetValue(o));}
                else if(o is IEnumerable list&&o is not string){foreach(var v in list)pending.Push(v);}
            }
        }
        static Matrix4x4 Pose(object p)
        {
            if(p is Matrix4x4 m)return m;
            Vector3 V(string name){var v=Items(Field(p,name));return new(Num(v[0]),Num(v[1]),Num(v[2]));}
            var q=Items(Field(p,"rotation"));return Matrix4x4.CreateScale(V("scale"))*Matrix4x4.CreateFromQuaternion(new(Num(q[0]),Num(q[1]),Num(q[2]),Num(q[3])))*Matrix4x4.CreateTranslation(V("translation"));
        }
        static object[] ActiveOperators(object cloth)
        {
            var operators=Items(Field(cloth,"operators"));var states=Items(Field(cloth,"clothStateDatas"));
            if(states.Length==0)throw new InvalidDataException("Native Cloth has no initial state");
            return Items(Field(states[0],"operators")).Select(i=>operators[Int(i)]).ToArray();
        }
        static int InitializationState(object cloth,string prefix,int bufferType)
        {
            var states=Items(Field(cloth,"clothStateDatas"));var operators=Items(Field(cloth,"operators"));var buffers=Items(Field(cloth,"bufferDefinitions"));
            var matches=states.Select((s,i)=>(s,i)).Where(x=>Text(Field(x.s,"name"))==prefix+Text(Field(states[0],"name"))).ToArray();
            if(matches.Length!=1)throw new InvalidDataException("Missing authored native initialization state: "+prefix+Text(Field(states[0],"name")));
            var ops=Items(Field(matches[0].s,"operators")).Select(i=>operators[Int(i)]).ToArray();
            if(ops.Any(o=>TypeName(o)=="hclSimulateOperator")||ops.Length>0&&!ops.Any(o=>(TypeName(o)=="hclCopyVerticesOperator"||TypeName(o)=="hclGatherAllVerticesOperator")&&Int(Field(buffers[Int(Field(o,"outputBufferIdx"))],"type"))==bufferType))
                throw new InvalidDataException("Native initialization state lacks the required animation-to-simulation copy");
            return matches[0].i;
        }
        public static float[] Flatten(IEnumerable<Matrix4x4> matrices)=>matrices.SelectMany(m=>new[]{m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44}).ToArray();
        static NativeParticleInitialization[] InitializationCopies(object cloth,int previous,int current)
        {
            var states=Items(Field(cloth,"clothStateDatas"));
            if(Items(Field(states[previous],"operators")).Length>0&&Items(Field(states[current],"operators")).Length>0)return Array.Empty<NativeParticleInitialization>();
            if(Items(Field(states[previous],"operators")).Length!=0||Items(Field(states[current],"operators")).Length!=0)throw new InvalidDataException("Partially authored native initialization states");
            var operators=Items(Field(cloth,"operators"));var sequence=Items(Field(states[0],"operators")).Select(Int).ToArray();var buffers=Items(Field(cloth,"bufferDefinitions"));var sims=Items(Field(cloth,"simClothDatas"));
            var copies=new List<NativeParticleInitialization>();
            foreach(int id in sequence.Where(i=>TypeName(operators[i])=="hclSimulateOperator"))
            {
                int sim=Int(Field(operators[id],"simClothIndex")),count=Items(Field(sims[sim],"particleDatas")).Length;
                var preceding=sequence.TakeWhile(i=>i!=id).Select(i=>operators[i]).ToArray();
                var move=preceding.LastOrDefault(o=>TypeName(o)=="hclMoveParticlesOperator"&&Int(Field(o,"simClothIndex"))==sim);
                if(move==null)throw new InvalidDataException("Native initialization has no preceding particle mapping");
                int input=Int(Field(move,"refBufferIdx"));var pairs=Items(Field(move,"vertexParticlePairs"));
                if(pairs.Length==0||pairs.Any(p=>Int(Field(p,"vertexIndex"))!=Int(Field(p,"particleIndex")))||Int(Field(buffers[input],"numVertices"))!=count)
                    throw new InvalidDataException("Native initialization requires an explicit non-linear particle mapping");
                var output=Enumerable.Range(0,buffers.Length).Where(i=>Int(Field(buffers[i],"type"))==1&&Int(Field(buffers[i],"subType"))==sim&&Int(Field(buffers[i],"numVertices"))==count).ToArray();
                if(output.Length!=1||!preceding.Any(o=>(TypeName(o).Contains("SpaceSkin")||TypeName(o)=="hclCopyVerticesOperator")&&Int(Field(o,"outputBufferIndex")??Field(o,"outputBufferIdx"))==input))
                    throw new InvalidDataException("Native initialization has no unique skinned particle buffer");
                copies.Add(new(){Operator=id,Simulation=sim,Input=input,Output=output[0],Count=count});
            }
            if(copies.Count!=sims.Length||copies.Select(c=>c.Simulation).Distinct().Count()!=sims.Length)throw new InvalidDataException("Native initialization does not cover each simulation exactly once");
            return copies.ToArray();
        }
        static int[] WrittenVertices(object cloth,int buffer,int count)
        {
            var indices=new HashSet<int>();
            foreach(var op in ActiveOperators(cloth).Where(op=>(Field(op,"outputBufferIdx")??Field(op,"outputBufferIndex")) is object output&&Int(output)==buffer))
            {
                if(TypeName(op)=="hclCopyVerticesOperator")
                {foreach(int i in Enumerable.Range(Int(Field(op,"startVertexOut")),Int(Field(op,"numberOfVertices"))))indices.Add(i);}
                else if(TypeName(op).Contains("MeshMeshDeform")||TypeName(op).Contains("SpaceSkin"))
                {
                    var deformer=Field(op,"boneSpaceDeformer")??Field(op,"objectSpaceDeformer");
                    foreach(var block in Objects(deformer))
                    {
                        foreach(var i in Items(Field(block,"vertexIndices")))indices.Add(Int(i));
                        for(int lane=0;lane<32;lane++){var value=Field(block,"vertexIndices_"+lane);if(value==null)break;indices.Add(Int(value));}
                    }
                }
            }
            // Block padding may carry the 0xffff invalid-vertex sentinel.
            return indices.Where(i=>i>=0&&i<count).OrderBy(i=>i).ToArray();
        }
        static NativeParticleRoute[] InitializationRoutes(object cloth,int current)
        {
            var states=Items(Field(cloth,"clothStateDatas"));var ops=Items(Field(cloth,"operators"));var buffers=Items(Field(cloth,"bufferDefinitions"));var sims=Items(Field(cloth,"simClothDatas"));
            var init=Items(Field(states[current],"operators")).Select(i=>ops[Int(i)]).Where(o=>TypeName(o) is "hclCopyVerticesOperator" or "hclGatherAllVerticesOperator").ToArray();
            if(init.Length==0)return Array.Empty<NativeParticleRoute>();
            var routes=new List<NativeParticleRoute>();var active=ActiveOperators(cloth);
            Vector3 Position(object o){if(o is Vector4 v)return new(v.X,v.Y,v.Z);var a=Items(o);return new(Num(a[0]),Num(a[1]),Num(a[2]));}
            Vector3[] Reference(int s)=>Items(Field(Items(Field(sims[s],"simClothPoses")).FirstOrDefault(),"positions")).Select(Position).ToArray();
            foreach(int target in active.Where(o=>TypeName(o)=="hclSimulateOperator").Select(o=>Int(Field(o,"simClothIndex"))).Distinct())
            {
                // Some assets retain a simulation with no buffer binding or
                // mesh/bone output. It has no animation-to-simulation copy to
                // route; keep the authored execution without fabricating one.
                if(!buffers.Any(b=>Int(Field(b,"type"))==1&&Int(Field(b,"subType"))==target))continue;
                var outputs=init.Select(o=>(Op:o,Buffer:buffers[Int(Field(o,"outputBufferIdx"))])).Where(x=>Int(Field(x.Buffer,"type"))==1).ToArray();
                if(outputs.Any(x=>Int(Field(x.Buffer,"subType"))==target))continue;
                var moves=active.Where(o=>TypeName(o)=="hclMoveParticlesOperator"&&Int(Field(o,"simClothIndex"))==target).ToArray();var reference=Reference(target);
                // Several exporters initialize an animation-only simulation even
                // for another state. Only route its positions when the original
                // particle order/pose and reference skin buffer agree exactly.
                var sources=outputs.Where(x=>moves.Any(m=>Int(Field(m,"refBufferIdx"))==Int(Field(x.Op,"inputBufferIdx"))))
                    .Select(x=>Int(Field(x.Buffer,"subType"))).Distinct().Where(s=>s>=0&&s<sims.Length&&reference.Length>0&&Reference(s).SequenceEqual(reference)).ToArray();
                if(sources.Length!=1)throw new InvalidDataException("No unique native initialization particle route: "+Text(Field(cloth,"name"))+" / "+target);
                routes.Add(new(){SourceSimulation=sources[0],TargetSimulation=target,Count=reference.Length});
            }
            return routes.ToArray();
        }
        public static NativeClothAsset Create(byte[] cloth,FLVER2 flver,CLM2 seams=null)
        {
            var objects=Objects(cloth[0]==0x57?ClothPackfileReader.ReadRoot(cloth):HkBinaryTagfileReader.Read(cloth,null)).ToArray();
            var skeletons=objects.Where(o=>TypeName(o)=="hkaSkeleton").Select(o=>
            {
                var names=Items(Field(o,"bones")).Select(b=>Text(Field(b,"name"))).ToArray();
                var parents=Items(Field(o,"parentIndices")).Select(Int).ToArray();var matrices=Items(Field(o,"referencePose")).Select(Pose).ToArray();
                if(names.Length!=matrices.Length||names.Length!=parents.Length)throw new InvalidDataException("Native skeleton array mismatch");
                for(int i=0;i<matrices.Length;i++){if(parents[i]>=i)throw new InvalidDataException("Native skeleton parent order");if(parents[i]>=0)matrices[i]*=matrices[parents[i]];}
                return new NativeSkeletonDescription{Name=Text(Field(o,"name")),Bones=names,ReferenceMatrices=Flatten(matrices)};
            }).ToArray();
            foreach(var skeleton in skeletons)skeleton.ReadByCloth=new bool[skeleton.Bones.Length];
            var namesByMesh=flver.Meshes.Select(m=>(uint)m.NodeIndex<flver.Nodes.Count?flver.Nodes[m.NodeIndex].Name:"").ToArray();
            var cloths=objects.Where(o=>TypeName(o)=="hclClothData").Select(o=>new NativeClothDescription
            {
                Name=Text(Field(o,"name")),
                PreviousInitializationState=InitializationState(o,"_preAtoS_",2),
                CurrentInitializationState=InitializationState(o,"_curAtoS_",1),
                TransformSkeletons=Items(Field(o,"transformSetDefinitions")).Select(t=>
                {
                    string name=Text(Field(t,"name"));int count=Int(Field(t,"numTransforms"));
                    var indices=skeletons.Select((s,i)=>(s,i)).Where(x=>x.s.Name==name&&x.s.Bones.Length==count).ToArray();
                    if(indices.Length!=1)throw new InvalidDataException("No unique native skeleton for transform set "+name+" ("+count+")");return indices[0].i;
                }).ToArray(),
                Meshes=Items(Field(o,"bufferDefinitions")).Select((b,i)=>(b,i)).Where(x=>Int(Field(x.b,"type"))==4).Select(x=>
                {
                    string name=Text(Field(x.b,"name"));int section=Int(Field(x.b,"subType")),count=Int(Field(x.b,"numVertices")),triangles=Int(Field(x.b,"numTriangles"));
                    // Havok validates both vertex and triangle counts. Different
                    // FLVER sections can have identical names/vertex counts but
                    // different topology (c1350 hair: 476 vs authored 596 tris).
                    var matching=Enumerable.Range(0,namesByMesh.Length).Where(i=>namesByMesh[i]==name&&flver.Meshes[i].Vertices.Count==count&&
                        (triangles==0||flver.Meshes[i].FaceSets.FirstOrDefault()?.Triangulate(true).Count/3==triangles)).ToArray();
                    if(matching.Length>1)
                    {
                        // Multiple sections may share a name and vertex count.
                        // In that case require the explicit authored section;
                        // do not merge different geometry or pick by proximity.
                        var sections=Enumerable.Range(0,namesByMesh.Length).Where(i=>namesByMesh[i]==name).ToArray();
                        if((uint)section<sections.Length&&matching.Contains(sections[section]))matching=new[]{sections[section]};
                    }
                    if(matching.Length!=1)throw new InvalidDataException("No unique native mesh topology: "+name+" / "+section+" / "+count);
                    var converters=ActiveOperators(o).Where(op=>TypeName(op)=="hclOutputConvertOperator"&&Int(Field(op,"userBufferIndex"))==x.i).GroupBy(op=>Int(Field(op,"shadowBufferIndex"))).Select(g=>g.First()).ToArray();
                    if(converters.Length>1)throw new InvalidDataException("Ambiguous native output conversion");
                    int read=converters.Length==1?Int(Field(converters[0],"shadowBufferIndex")):x.i;
                    var written=WrittenVertices(o,x.i,count).Concat(WrittenVertices(o,read,count)).Distinct().ToHashSet();
                    // Some display buffers are animation inputs or used only in
                    // other authored states. Supply them to Havok without
                    // replacing their normal animated rendering in state zero.
                    int meshIndex=matching[0];var mesh=flver.Meshes[meshIndex];var aliases=seams!=null&&meshIndex<seams.Meshes.Count?seams.Meshes[meshIndex].Where(a=>written.Contains(a.Unk00)&&!written.Contains(a.Unk02)&&a.Unk02>=0&&a.Unk02<count&&Vector3.Distance(mesh.Vertices[a.Unk00].Position,mesh.Vertices[a.Unk02].Position)<1e-6f).ToArray():null;
                    if(aliases!=null)foreach(var a in aliases)written.Add(a.Unk02);
                    var layout=Field(x.b,"bufferLayout");object Element(int i)=>Items(Field(layout,"elementsLayout")).ElementAtOrDefault(i)??Field(layout,"elementsLayout_"+i);object Slot(int i)=>Items(Field(layout,"slots")).ElementAtOrDefault(i)??Field(layout,"slots_"+i);
                    return new NativeMeshDescription{BufferIndex=x.i,MeshIndex=meshIndex,VertexCount=count,SceneSection=section,ReadBufferIndex=read,WrittenVertices=written.OrderBy(i=>i).ToArray(),
                        AliasSources=aliases?.Select(a=>(int)a.Unk00).ToArray()??System.Array.Empty<int>(),AliasTargets=aliases?.Select(a=>(int)a.Unk02).ToArray()??System.Array.Empty<int>(),
                        ReferenceNormals=mesh.Vertices.SelectMany(v=>new[]{v.Normal.X,v.Normal.Y,v.Normal.Z}).ToArray(),ReferenceTangents=mesh.Vertices.SelectMany(v=>{var t=v.Tangents.FirstOrDefault();return new[]{t.X,t.Y,t.Z};}).ToArray(),
                        Conversions=Enumerable.Range(0,4).Select(i=>Int(Field(Element(i),"vectorConversion"))).ToArray(),Slots=Enumerable.Range(0,4).Select(i=>Int(Field(Element(i),"slotId"))).ToArray(),Starts=Enumerable.Range(0,4).Select(i=>Int(Field(Element(i),"slotStart"))).ToArray(),Strides=Enumerable.Range(0,4).Select(i=>Int(Field(Slot(i),"stride"))).ToArray(),
                        InitialChannels=Enumerable.Range(0,4).Select(channel=>mesh.Vertices.SelectMany(v=>{var tangent=v.Tangents.FirstOrDefault();Vector4 value=channel==0?new(v.Position,1):channel==1?new(v.Normal,0):channel==2?tangent:new(Vector3.Cross(v.Normal,new(tangent.X,tangent.Y,tangent.Z))*tangent.W,0);return new[]{value.X,value.Y,value.Z,value.W};}).ToArray()).ToArray()};
                }).ToArray()
            }).ToArray();
            foreach(var clothObject in objects.Where(o=>TypeName(o)=="hclClothData"))
            {
                var description=cloths.Single(c=>c.Name==Text(Field(clothObject,"name")));
                description.Initializations=InitializationCopies(clothObject,description.PreviousInitializationState,description.CurrentInitializationState);
                description.InitializationRoutes=InitializationRoutes(clothObject,description.CurrentInitializationState);
                foreach(var state in Items(Field(clothObject,"clothStateDatas")))foreach(var access in Items(Field(state,"usedTransformSets")))
                {
                    int set=Int(Field(access,"transformSetIndex"));var skeleton=skeletons[description.TransformSkeletons[set]];
                    foreach(var tracker in Items(Field(Field(access,"transformSetUsage"),"perComponentTransformTrackers")))foreach(string field in new[]{"read","readBeforeWrite"})
                    {
                        var storage=Field(Field(tracker,field),"storage");var words=Items(Field(storage,"words"));int bits=Int(Field(storage,"numBits"));
                        if(bits>0&&bits!=skeleton.Bones.Length)throw new InvalidDataException("Native transform read-mask size mismatch");
                        for(int i=0;i<bits;i++)if((Convert.ToUInt32(Value(words[i/32]))&(1u<<(i%32)))!=0)skeleton.ReadByCloth[i]=true;
                    }
                }
            }
            if(cloths.Length==0||skeletons.Length==0)throw new InvalidDataException("No native Cloth and skeleton data");
            hkxNode Node(string name,Matrix4x4 matrix)=>new(){m_name=name,m_keyFrames=new(){matrix},m_children=new(),m_bone=true};
            var nodes=flver.Nodes.Select(n=>Node(n.Name,Matrix4x4.CreateScale(n.Scale)*Matrix4x4.CreateRotationX(n.Rotation.X)*Matrix4x4.CreateRotationZ(n.Rotation.Z)*Matrix4x4.CreateRotationY(n.Rotation.Y)*Matrix4x4.CreateTranslation(n.Translation))).ToArray();
            var root=Node("DSA",Matrix4x4.Identity);root.m_bone=false;
            for(int i=0;i<nodes.Length;i++){int parent=flver.Nodes[i].ParentIndex;if(parent>=0)nodes[parent].m_children.Add(nodes[i]);else root.m_children.Add(nodes[i]);}
            var scene=new hkxScene{m_rootNode=root,m_modeller="DSA native physics",m_asset="Character",m_numFrames=1,m_sceneLength=1f/60,m_appliedTransform=Matrix4x4.Identity,m_meshes=new(),m_materials=new()};
            var requiredMeshes=cloths.SelectMany(c=>c.Meshes).GroupBy(m=>m.MeshIndex).Select(g=>g.First()).ToArray();
            foreach(var group in requiredMeshes.GroupBy(m=>flver.Meshes[m.MeshIndex].NodeIndex))
            {
                var mesh=new hkxMesh{m_sections=new()};
                // hclBufferDefinition.subType is a section index, not an ordinal
                // among cloth outputs. Retain intervening non-cloth sections.
                var nodeMeshes=Enumerable.Range(0,flver.Meshes.Count).Where(i=>flver.Meshes[i].NodeIndex==group.Key).ToArray();
                for(int section=0;section<=group.Max(m=>m.SceneSection);section++)
                {
                    var explicitMeshes=group.Where(m=>m.SceneSection==section).Select(m=>m.MeshIndex).Distinct().ToArray();
                    if(explicitMeshes.Length>1)throw new InvalidDataException("Conflicting native scene section mappings");
                    int meshIndex=explicitMeshes.Length==1?explicitMeshes[0]:section<nodeMeshes.Length?nodeMeshes[section]:-1;
                    if(meshIndex<0)throw new InvalidDataException("Missing intermediate native scene section");
                    var fm=flver.Meshes[meshIndex];
                    var vd=new hkxVertexBufferVertexData{m_vectorData=new(),m_floatData=new(),m_numVerts=(uint)fm.Vertices.Count,m_vectorStride=64,m_floatStride=8};
                    void Vec(Vector4 v){vd.m_vectorData.Add(BitConverter.SingleToUInt32Bits(v.X));vd.m_vectorData.Add(BitConverter.SingleToUInt32Bits(v.Y));vd.m_vectorData.Add(BitConverter.SingleToUInt32Bits(v.Z));vd.m_vectorData.Add(BitConverter.SingleToUInt32Bits(v.W));}
                    foreach(var v in fm.Vertices)
                    {
                        Vec(new(v.Position,1));Vec(new(v.Normal,0));var t=v.Tangents.Count>0?v.Tangents[0]:new Vector4(1,0,0,1);
                        Vec(new(t.X,t.Y,t.Z,0));Vec(new(Vector3.Cross(v.Normal,new(t.X,t.Y,t.Z))*t.W,0));
                        var uv=v.UVs.Count>0?v.UVs[0]:Vector3.Zero;vd.m_floatData.Add(BitConverter.SingleToUInt32Bits(uv.X));vd.m_floatData.Add(BitConverter.SingleToUInt32Bits(uv.Y));
                    }
                    var desc=new hkxVertexDescription{m_decls=new()};
                    foreach(var p in new[]{(DataUsage.HKX_DU_POSITION,0u),(DataUsage.HKX_DU_NORMAL,16u),(DataUsage.HKX_DU_TANGENT,32u),(DataUsage.HKX_DU_BINORMAL,48u)})desc.m_decls.Add(new(){m_type=DataType.HKX_DT_FLOAT,m_usage=p.Item1,m_byteOffset=p.Item2,m_byteStride=64,m_numElements=3});
                    desc.m_decls.Add(new(){m_type=DataType.HKX_DT_FLOAT,m_usage=DataUsage.HKX_DU_TEXCOORD,m_byteOffset=0,m_byteStride=8,m_numElements=2});
                    var indices=fm.FaceSets[0].Triangulate(true).Select(i=>(uint)i).ToList();var material=new hkxMaterial{m_name="Cloth",m_diffuseColor=Vector4.One};scene.m_materials.Add(material);
                    mesh.m_sections.Add(new(){m_vertexBuffer=new(){m_data=vd,m_desc=desc},m_indexBuffers=new(){new(){m_indexType=IndexType.INDEX_TYPE_TRI_LIST,m_indices32=indices,m_length=(uint)indices.Count}},m_material=material});
                }
                nodes[group.Key].m_object=mesh;nodes[group.Key].m_bone=false;scene.m_meshes.Add(mesh);
            }
            using var stream=new MemoryStream();using(var writer=new BinaryWriterEx(false,stream,true))new PackFileSerializer().Serialize(new hkRootLevelContainer{m_namedVariants=new(){new(){m_name="Scene Data",m_className="hkxScene",m_variant=scene}}},writer);
            return new(){Cloth=cloth,Scene=stream.ToArray(),Description=new(){Skeletons=skeletons,Cloths=cloths}};
        }
    }
}
