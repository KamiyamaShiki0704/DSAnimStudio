using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Havoc.IO.Tagfile.Binary;
using Havoc.Objects;
using Havoc.Reflection;

namespace DSAnimStudio
{
    public sealed record ClothPreviewLink(int A,int B,float RestLength,float Stiffness,bool StretchOnly);
    public sealed record ClothPreviewWeight(string Bone,float Weight);
    public sealed record ClothPreviewDefinition(string Name,Vector3[] RestPositions,float[] InverseMass,bool[] Fixed,
        int[] Triangles,ClothPreviewLink[] Links,ClothPreviewWeight[][] Bindings,Vector3 Gravity,float Damping,string[] Limitations,
        ClothPreviewMeshBinding[] MeshBindings=null,ClothCollisionDefinition Collisions=null,ClothPreviewInitialization Initialization=null,
        ClothPreviewBoneBinding[] BoneBindings=null);

    public enum ClothCollisionFilter { Unverified, ParticleBitsIncludeColliders }
    public sealed record ClothCollisionCollider(int Index,string Name,string Shape,string Bone,Matrix RestTransform,
        Matrix Offset,Matrix ReferenceBoneTransform,Vector3 Start,Vector3 End,float Radius,bool Enabled,bool MappingVerified,
        bool RequiresReferenceMatch=true,float EndRadius=0,Matrix? AuthoredTransform=null)
    {
        public bool Supported=>Shape is "hclSphereShape" or "hclCapsuleShape" or "hclTaperedCapsuleShape";
    }
    public sealed record ClothCollisionDefinition(ClothCollisionCollider[] Colliders,float[] ParticleRadius,float[] ParticleFriction,
        uint[] StaticCollisionMasks,int TransformSetIndex,uint[] TransformIndices,Matrix[] Offsets,
        ClothCollisionFilter Filter=ClothCollisionFilter.Unverified);

    public sealed class ClothCollisionSolver
    {
        struct Pose { public Vector3 A,B,PreviousA,PreviousB; public float Radius,RadiusB,PreviousRadius,PreviousRadiusB; public bool Active; }
        readonly ClothCollisionDefinition data;
        readonly Pose[] poses;
        readonly HashSet<string> notices=new();
        bool prepared;
        float particleScale=1;
        public IReadOnlyCollection<string> Limitations=>notices;
        public int ActiveColliderCount { get; private set; }
        public int LastSweepIterations { get; private set; }
        public const int MaximumSweepIterations=40;
        public ClothCollisionSolver(ClothCollisionDefinition definition)
        {
            data=definition??throw new ArgumentNullException(nameof(definition));poses=new Pose[data.Colliders.Length];
            if(data.Filter==ClothCollisionFilter.Unverified)notices.Add("Native collision filter is unverified; no collision pairs are enabled");
            if(data.Colliders.Length>32)notices.Add("Collision preview supports at most 32 explicitly filtered colliders; this layout is not projected");
            foreach(var collider in data.Colliders)
            {
                if(!collider.Supported)notices.Add("Unsupported authored collision shape: "+collider.Shape);
                if(!collider.MappingVerified)notices.Add("Unverified authored collider mapping: "+collider.Name);
            }
        }
        public void Reset(){prepared=false;ActiveColliderCount=0;System.Array.Clear(poses,0,poses.Length);}
        public void Prepare(Func<string,Matrix?> boneDelta,Matrix world,Func<string,Matrix?> referenceBone=null)
        {
            ActiveColliderCount=0;
            if(!ClothPreviewData.Affine(world)||!UniformScale(world,out particleScale))
            {notices.Add("Cloth collision world transform is nonuniform or invalid");Reset();return;}
            for(int i=0;i<poses.Length;i++)
            {
                var collider=data.Colliders[i];var prior=poses[i];poses[i].Active=false;
                if(!collider.Enabled||!collider.Supported||!collider.MappingVerified)continue;
                var delta=string.IsNullOrEmpty(collider.Bone)?Matrix.Identity:boneDelta?.Invoke(collider.Bone);
                if(!delta.HasValue){notices.Add("Collision bone is unavailable: "+collider.Bone);continue;}
                if(collider.RequiresReferenceMatch)
                {
                    var reference=referenceBone?.Invoke(collider.Bone);
                    if(!reference.HasValue||!ClothPreviewData.Affine(reference.Value)||ClothPreviewData.MatrixError(reference.Value,collider.ReferenceBoneTransform)>.002f)
                    {notices.Add("Collider model reference FK does not match the authored skeleton: "+collider.Name);continue;}
                }
                var transform=collider.RestTransform*delta.Value*world;
                if(!ClothPreviewData.Affine(transform)||!UniformScale(transform,out float scale))
                {notices.Add("Collider pose contains a non-finite, sheared or nonuniform transform: "+collider.Name);continue;}
                var a=Vector3.Transform(collider.Start,transform);var b=Vector3.Transform(collider.End,transform);float radius=collider.Radius*scale;
                float radiusB=(collider.Shape=="hclTaperedCapsuleShape"?collider.EndRadius:collider.Radius)*scale;
                if(!ClothPreviewData.Finite(a)||!ClothPreviewData.Finite(b)||!float.IsFinite(radius)||radius<0||!float.IsFinite(radiusB)||radiusB<0){notices.Add("Invalid collider geometry: "+collider.Name);continue;}
                poses[i]=new Pose{A=a,B=b,Radius=radius,RadiusB=radiusB,PreviousA=prepared&&prior.Active?prior.A:a,PreviousB=prepared&&prior.Active?prior.B:b,PreviousRadius=prepared&&prior.Active?prior.Radius:radius,PreviousRadiusB=prepared&&prior.Active?prior.RadiusB:radiusB,Active=true};
                ActiveColliderCount++;
            }
            prepared=true;
        }
        static bool UniformScale(Matrix m,out float scale)
        {
            float x=m.Right.Length(),y=m.Up.Length(),z=m.Backward.Length();scale=x;
            return x>1e-7f&&Math.Abs(x-y)<=x*.001f&&Math.Abs(x-z)<=x*.001f&&Math.Abs(Vector3.Dot(m.Right,m.Up))<=x*x*.001f&&Math.Abs(Vector3.Dot(m.Right,m.Backward))<=x*x*.001f&&Math.Abs(Vector3.Dot(m.Up,m.Backward))<=x*x*.001f;
        }
        static float SurfaceDistance(Vector3 point,Vector3 a,Vector3 b,float radiusA,float radiusB,out Vector3 normal,out float amount)
        {
            var axis=b-a;float length=axis.Length(),difference=radiusB-radiusA;
            if(length<=Math.Abs(difference)+1e-7f)
            {
                amount=radiusB>radiusA?1:0;var delta=point-Vector3.Lerp(a,b,amount);normal=Normal(delta,Vector3.UnitY,Vector3.Zero);
                return delta.Length()-Math.Max(radiusA,radiusB);
            }
            var direction=axis/length;var relative=point-a;float axial=Vector3.Dot(relative,direction);
            var radial=relative-direction*axial;float radialLength=radial.Length();float sine=difference/length,cosine=MathF.Sqrt(Math.Max(0,1-sine*sine));
            float sideDistance=radialLength*cosine-axial*sine-radiusA;
            float surfaceAxial=axial+sideDistance*sine;
            amount=(surfaceAxial+sine*radiusA)/(length-sine*difference);
            if(amount<=0||amount>=1)
            {
                amount=Math.Clamp(amount,0,1);var delta=point-Vector3.Lerp(a,b,amount);normal=Normal(delta,Vector3.UnitY,axis);
                return delta.Length()-MathHelper.Lerp(radiusA,radiusB,amount);
            }
            var radialNormal=Normal(radial,Vector3.UnitX,axis);normal=radialNormal*cosine-direction*sine;
            return sideDistance;
        }
        static Vector3 Normal(Vector3 separation,Vector3 fallback,Vector3 axis)
        {
            if(separation.LengthSquared()>1e-14f)return Vector3.Normalize(separation);
            if(axis.LengthSquared()>1e-12f){axis.Normalize();fallback-=axis*Vector3.Dot(fallback,axis);}
            if(fallback.LengthSquared()>1e-14f)return Vector3.Normalize(fallback);
            var normal=Vector3.Cross(axis,Vector3.UnitX);return normal.LengthSquared()>1e-12f?Vector3.Normalize(normal):Vector3.UnitY;
        }
        bool Sweep(Vector3 previous,Vector3 current,Pose pose,float particleRadius,out Vector3 normal,out float axisAmount)
        {
            normal=Vector3.Zero;axisAmount=0;float time=0;
            float speed=Vector3.Distance(previous,current)+Math.Max(Vector3.Distance(pose.PreviousA,pose.A),Vector3.Distance(pose.PreviousB,pose.B))+Math.Max(Math.Abs(pose.Radius-pose.PreviousRadius),Math.Abs(pose.RadiusB-pose.PreviousRadiusB));
            if(!float.IsFinite(speed)||speed<1e-8f)return false;
            for(int iteration=0;iteration<MaximumSweepIterations;iteration++)
            {
                LastSweepIterations++;
                var a=Vector3.Lerp(pose.PreviousA,pose.A,time);var b=Vector3.Lerp(pose.PreviousB,pose.B,time);var point=Vector3.Lerp(previous,current,time);
                float radius=MathHelper.Lerp(pose.PreviousRadius,pose.Radius,time)+particleRadius,radiusB=MathHelper.Lerp(pose.PreviousRadiusB,pose.RadiusB,time)+particleRadius;
                float gap=SurfaceDistance(point,a,b,radius,radiusB,out normal,out axisAmount);
                if(gap<=.00001f)
                {
                    if(time==0&&gap<-.00001f)return false;
                    var motion=Vector3.Lerp(pose.A-pose.PreviousA,pose.B-pose.PreviousB,axisAmount);
                    if(time==0&&gap>=-.00001f&&Vector3.Dot((current-previous)-motion,normal)>=0)return false;
                    return true;
                }
                float advance=gap/speed*.95f;if(time+advance>1)return false;time+=Math.Max(advance,1e-8f);
            }
            notices.Add("Cloth swept-contact iteration budget reached; endpoint penetration projection remains active");return false;
        }
        public bool Project(int particle,ref Vector3 previous,ref Vector3 current)
        {
            LastSweepIterations=0;
            if(!prepared||data.Filter!=ClothCollisionFilter.ParticleBitsIncludeColliders||poses.Length>32)return false;
            if((uint)particle>=data.ParticleRadius.Length||(uint)particle>=data.ParticleFriction.Length||data.StaticCollisionMasks.Length!=data.ParticleRadius.Length)
            {notices.Add("Collision particle mask/radius/friction arrays do not share the verified layout");return false;}
            if(!ClothPreviewData.Finite(previous)||!ClothPreviewData.Finite(current))return false;
            float particleRadius=data.ParticleRadius[particle]*particleScale,friction=data.ParticleFriction[particle];
            if(!float.IsFinite(particleRadius)||particleRadius<0||!float.IsFinite(friction)||friction<0){notices.Add("Collision particle material is invalid");return false;}
            bool changed=false;uint mask=data.StaticCollisionMasks[particle];
            for(int i=0;i<poses.Length;i++)
            {
                var descriptor=data.Colliders[i];var pose=poses[i];if(!pose.Active||descriptor.Index<0||descriptor.Index>=32||(mask&(1u<<descriptor.Index))==0)continue;
                var before=current;var oldPrevious=previous;
                float gap=SurfaceDistance(current,pose.A,pose.B,pose.Radius+particleRadius,pose.RadiusB+particleRadius,out var endpointNormal,out float amount);
                bool swept=Sweep(previous,current,pose,particleRadius,out var normal,out float hitAmount);
                if(!swept&&gap>=0)continue;
                if(swept)
                {
                    amount=hitAmount;float radius=MathHelper.Lerp(pose.Radius,pose.RadiusB,amount)+particleRadius;
                    var contact=Vector3.Lerp(pose.A,pose.B,amount)+normal*(radius+.00001f);
                    float signed=Vector3.Dot(current-contact,normal);if(signed<0)current-=normal*signed;
                }
                else {normal=endpointNormal;current-=normal*(gap-.00001f);}
                gap=SurfaceDistance(current,pose.A,pose.B,pose.Radius+particleRadius,pose.RadiusB+particleRadius,out var correctionNormal,out _);
                if(gap<0)current-=correctionNormal*(gap-.00001f);
                float radiusMotion=MathHelper.Lerp(pose.Radius-pose.PreviousRadius,pose.RadiusB-pose.PreviousRadiusB,amount);
                var surfaceMotion=Vector3.Lerp(pose.A-pose.PreviousA,pose.B-pose.PreviousB,amount)+normal*radiusMotion;
                var velocity=before-oldPrevious-surfaceMotion;float inward=Vector3.Dot(velocity,normal);var tangent=velocity-normal*inward;
                float tangentLength=tangent.Length();float normalImpulse=Math.Max(0,-inward)+Vector3.Distance(current,before);
                if(tangentLength>1e-8f)tangent*=Math.Max(0,1-friction*normalImpulse/tangentLength);
                previous=current-(surfaceMotion+tangent+normal*Math.Max(0,inward));changed=true;
            }
            return changed;
        }
    }

    public static class ClothPreviewData
    {
        internal static IHkObject Unwrap(IHkObject o)=>o is HkPtr p?p.Value:o;
        internal static IReadOnlyList<IHkObject> Array(IHkObject o)=>Unwrap(o)?.Value as IReadOnlyList<IHkObject>??System.Array.Empty<IHkObject>();
        internal static IHkObject Field(IHkObject o,string key)=>Unwrap(o)?.Value is IReadOnlyDictionary<HkField,IHkObject> fields
            ?fields.FirstOrDefault(p=>p.Key.Name==key).Value:null;
        internal static float Number(IHkObject o,float fallback=0)=>Unwrap(o)?.Value is IConvertible v?Convert.ToSingle(v):fallback;
        internal static string Text(IHkObject o)=>Unwrap(o)?.Value as string??"";
        internal static Vector3 Vec(IHkObject o){var a=Array(o);return a.Count>=3?new(Number(a[0]),Number(a[1]),Number(a[2])):Vector3.Zero;}
        static IEnumerable<IHkObject> Objects(IHkObject root)
        {
            var visited=new HashSet<IHkObject>(ReferenceEqualityComparer.Instance);var todo=new Stack<IHkObject>();todo.Push(root);
            while(todo.Count>0)
            {
                var o=Unwrap(todo.Pop());if(o==null||!visited.Add(o))continue;yield return o;
                if(o.Value is IReadOnlyDictionary<HkField,IHkObject> fields)foreach(var v in fields.Values)todo.Push(v);
                else foreach(var v in Array(o))todo.Push(v);
            }
        }
        internal static bool Finite(Vector3 v)=>float.IsFinite(v.X)&&float.IsFinite(v.Y)&&float.IsFinite(v.Z);
        internal static bool Finite(Matrix m)=>Finite(new Vector3(m.M11,m.M12,m.M13))&&Finite(new Vector3(m.M21,m.M22,m.M23))&&Finite(new Vector3(m.M31,m.M32,m.M33))&&Finite(m.Translation)&&float.IsFinite(m.M14)&&float.IsFinite(m.M24)&&float.IsFinite(m.M34)&&float.IsFinite(m.M44);
        internal static bool Affine(Matrix m)=>Finite(m)&&Math.Abs(m.M14)<.0001f&&Math.Abs(m.M24)<.0001f&&Math.Abs(m.M34)<.0001f&&Math.Abs(m.M44-1)<.0001f;
        internal static float MatrixError(Matrix a,Matrix b)=>Math.Max(Vector3.Distance(a.Translation,b.Translation),Math.Max(Vector3.Distance(a.Right,b.Right),Math.Max(Vector3.Distance(a.Up,b.Up),Vector3.Distance(a.Backward,b.Backward))));
        internal static Matrix MatrixValue(IHkObject value)
        {
            var a=Array(value);if(a.Count!=16)return default;
            return new(Number(a[0]),Number(a[1]),Number(a[2]),Number(a[3]),Number(a[4]),Number(a[5]),Number(a[6]),Number(a[7]),Number(a[8]),Number(a[9]),Number(a[10]),Number(a[11]),Number(a[12]),Number(a[13]),Number(a[14]),Number(a[15]));
        }
        internal static Matrix[] ReferencePose(Matrix[] locals,int[] parents)
        {
            if(locals.Length!=parents.Length)return System.Array.Empty<Matrix>();
            var result=new Matrix[locals.Length];var state=new byte[locals.Length];
            Matrix Visit(int i)
            {
                if(state[i]==2)return result[i];if(state[i]==1)throw new InvalidOperationException("Cloth collision skeleton has cyclic parents.");
                state[i]=1;int parent=parents[i];if(parent>=locals.Length||parent < -1)throw new InvalidOperationException("Cloth collision skeleton parent is invalid.");
                result[i]=parent<0?locals[i]:locals[i]*Visit(parent);state[i]=2;return result[i];
            }
            for(int i=0;i<result.Length;i++)Visit(i);return result;
        }
        internal static bool VerifyCollisionTransform(Matrix authored,Matrix offset,Matrix reference,out Matrix rest,out Matrix verifiedOffset,bool transformPadding=false)
        {
            rest=authored;verifiedOffset=offset;
            if(!Affine(reference))return false;
            foreach(var raw in new[]{authored,Matrix.Transpose(authored)})foreach(var o in new[]{offset,Matrix.Transpose(offset)})
            {
                var a=raw;
                if(transformPadding){a.M14=a.M24=a.M34=0;a.M44=1;}
                if(Affine(a)&&Affine(o)&&MatrixError(o*reference,a)<.002f){rest=a;verifiedOffset=o;return true;}
            }
            return false;
        }
        internal static ClothCollisionFilter PreviewCollisionFilter(int particles,int colliders,uint[] masks)
        {
            if(colliders<0||colliders>32||masks.Length!=particles)return ClothCollisionFilter.Unverified;
            uint valid=colliders==32?uint.MaxValue:(1u<<colliders)-1;
            return masks.All(mask=>(mask&~valid)==0)?ClothCollisionFilter.ParticleBitsIncludeColliders:ClothCollisionFilter.Unverified;
        }
        internal static void CollisionNotices(ClothCollisionDefinition data,ICollection<string> notices)
        {
            if(data.Colliders.Length==0){notices.Add("No authored per-instance cloth colliders; body geometry is not inferred from bone names");return;}
            foreach(var c in data.Colliders)
            {
                if(!c.Supported)notices.Add("Authored collision shape "+c.Shape+" is retained but not solved");
                if(!c.MappingVerified)notices.Add("Authored collider "+c.Name+" has no verified offset/reference transform mapping; not projected");
            }
            if(data.Filter==ClothCollisionFilter.Unverified)notices.Add("Authored staticCollisionMasks retained; count or bit range does not match the supported preview layout, so body collision projection is unavailable (not treated as all-enabled)");
            else notices.Add("Approximate collision preview interprets each particle uint mask as collider inclusion bits (0=none, 3=colliders 0 and 1); layout checked, native Havok polarity/semantics not guaranteed");
            notices.Add("Cloth collision preview solves authored sphere/capsule/tapered-capsule hulls; native self-collision, virtual collision points, pinching and general convex shapes are not reconstructed");
        }
        static ClothCollisionDefinition ReadCollisions(IHkObject sim,IReadOnlyList<IHkObject> transforms,IHkObject[] skeletons,ICollection<string> notices)
        {
            var particles=Array(Field(sim,"particleDatas"));var radii=particles.Select(p=>Number(Field(p,"radius"),float.NaN)).ToArray();var friction=particles.Select(p=>Number(Field(p,"friction"),float.NaN)).ToArray();
            if(radii.Any(v=>!float.IsFinite(v)||v<0)||friction.Any(v=>!float.IsFinite(v)||v<0))throw new InvalidOperationException("Cloth collision particle radii/friction are invalid.");
            var map=Field(sim,"collidableTransformMap");int set=(int)Number(Field(map,"transformSetIndex"),-1);
            var indices=Array(Field(map,"transformIndices")).Select(v=>Convert.ToUInt32(Unwrap(v).Value)).ToArray();var offsets=Array(Field(map,"offsets")).Select(MatrixValue).ToArray();
            var collidables=Array(Field(sim,"perInstanceCollidables")).Select(Unwrap).ToArray();
            var matched=(uint)set<transforms.Count?skeletons.Where(s=>Text(Field(s,"name"))==Text(Field(transforms[set],"name"))).ToArray():System.Array.Empty<IHkObject>();
            var skeleton=matched.Length==1?matched[0]:null;var bones=Array(Field(skeleton,"bones")).Select(b=>Text(Field(b,"name"))).ToArray();
            var locals=Array(Field(skeleton,"referencePose")).Select(p=>
            {
                var r=Array(Field(p,"rotation"));return r.Count==4?Matrix.CreateScale(Vec(Field(p,"scale")))*Matrix.CreateFromQuaternion(new Quaternion(Number(r[0]),Number(r[1]),Number(r[2]),Number(r[3])))*Matrix.CreateTranslation(Vec(Field(p,"translation"))):default;
            }).ToArray();
            var reference=ReferencePose(locals,Array(Field(skeleton,"parentIndices")).Select(p=>(int)Number(p)).ToArray());var result=new List<ClothCollisionCollider>();
            for(int i=0;i<collidables.Length;i++)
            {
                var c=collidables[i];var shape=Unwrap(Field(c,"shape"));string kind=shape?.Type.Name??"missing";
                var start=Vec(Field(shape,"start"));var end=Vec(Field(shape,"end"));float radius=Number(Field(shape,"radius"),float.NaN);
                if(kind=="hclSphereShape"){var sphere=Array(Field(Field(shape,"sphere"),"pos"));if(sphere.Count==4){start=end=new(Number(sphere[0]),Number(sphere[1]),Number(sphere[2]));radius=Number(sphere[3]);}}
                float endRadius=radius;
                if(kind=="hclTaperedCapsuleShape"){start=Vec(Field(shape,"small"));end=Vec(Field(shape,"big"));radius=Number(Field(shape,"smallRadius"),float.NaN);endRadius=Number(Field(shape,"bigRadius"),float.NaN);}
                int bone=i<indices.Length&&indices[i]<bones.Length?(int)indices[i]:-1;var offset=i<offsets.Length?offsets[i]:default;
                var authored=MatrixValue(Field(c,"transform"));var rest=authored;var refBone=(uint)bone<reference.Length?reference[bone]:default;
                bool transformPadding=Unwrap(Field(c,"transform"))?.Type.Name.StartsWith("hkTransform",StringComparison.Ordinal)==true;
                bool verified=collidables.Length==indices.Length&&indices.Length==offsets.Length&&bone>=0&&VerifyCollisionTransform(rest,offset,refBone,out rest,out offset,transformPadding);
                if((kind is "hclSphereShape" or "hclCapsuleShape")&&(!Finite(start)||!Finite(end)||!float.IsFinite(radius)||radius<0))verified=false;
                result.Add(new(i,Text(Field(c,"name")),kind,bone>=0?bones[bone]:null,rest,offset,refBone,start,end,radius,Number(Field(c,"enabled"),1)!=0,verified,true,endRadius,authored));
            }
            var masks=Array(Field(sim,"staticCollisionMasks")).Select(v=>Convert.ToUInt32(Unwrap(v).Value)).ToArray();
            var data=new ClothCollisionDefinition(result.ToArray(),radii,friction,masks,set,indices,offsets,PreviewCollisionFilter(radii.Length,result.Count,masks));
            CollisionNotices(data,notices);return data;
        }
        static void ReadBoneSpaceAnchors(IHkObject skin,IHkObject move,IHkObject skeleton,Vector3[] rest,ClothPreviewWeight[][] bindings,ICollection<string> notices)
        {
            var targets=new Dictionary<int,int>();
            foreach(var pair in Array(Field(move,"vertexParticlePairs")))
            {
                int particle=(int)Number(Field(pair,"particleIndex")),vertex=(int)Number(Field(pair,"vertexIndex"));
                if((uint)particle>=rest.Length||targets.TryGetValue(vertex,out int prior)&&prior!=particle)
                {notices.Add("BoneSpaceSkin fixed particle input not bound: MoveParticles correspondence is invalid or ambiguous");return;}
                targets[vertex]=particle;
            }
            ReadBoneSpaceBindings(skin,skeleton,rest,targets,bindings,notices);
        }
        internal static void ReadBoneSpaceBindings(IHkObject skin,IHkObject skeleton,Vector3[] rest,IReadOnlyDictionary<int,int> targets,
            ClothPreviewWeight[][] bindings,ICollection<string> notices,Vector3[][] referenceNormals=null)
        {
            try
            {
                if(skeleton==null)throw new InvalidOperationException("input transform set has no unique named skeleton");
                var names=Array(Field(skeleton,"bones")).Select(b=>Text(Field(b,"name"))).ToArray();
                var locals=Array(Field(skeleton,"referencePose")).Select(p=>
                {
                    var q=Array(Field(p,"rotation"));return q.Count==4?Matrix.CreateScale(Vec(Field(p,"scale")))*Matrix.CreateFromQuaternion(new Quaternion(Number(q[0]),Number(q[1]),Number(q[2]),Number(q[3])))*Matrix.CreateTranslation(Vec(Field(p,"translation"))):default;
                }).ToArray();
                var reference=ReferencePose(locals,Array(Field(skeleton,"parentIndices")).Select(v=>(int)Number(v)).ToArray());
                var subset=Array(Field(skin,"transformSubset")).Select(v=>(int)Number(v)).ToArray();
                var type=skin.Type.Name;string suffix=type.Contains("PNTB")?"PNTB":type.Contains("PNT")?"PNT":type.Contains("PN")?"PN":"P";
                var blocksLocal=Array(Field(skin,"local"+suffix+"s"));if(blocksLocal.Count==0)blocksLocal=Array(Field(skin,"localUnpacked"+suffix+"s"));
                var deformer=Field(skin,"boneSpaceDeformer");var controls=Array(Field(deformer,"controlBytes"));
                if(controls.Count>8192)throw new InvalidOperationException("block count exceeds preview budget");
                var kinds=new[]{"four","three","two","one"};var blocks=kinds.Select(k=>Array(Field(deformer,k+"BlendEntries"))).ToArray();var cursor=new int[4];int localIndex=0;
                var pending=new Dictionary<int,ClothPreviewWeight[]>();var normalsPending=new Dictionary<int,Vector3[]>();float maximumError=0;
                foreach(var control in controls)
                {
                    int kind=(int)Number(control);if(kind==4)continue;
                    if(kind<0||kind>3||cursor[kind]>=blocks[kind].Count||localIndex>=blocksLocal.Count)throw new InvalidOperationException("BoneSpaceSkin control/block arrays are inconsistent");
                    var block=blocks[kind][cursor[kind]++];var local=blocksLocal[localIndex++];var positions=Array(Field(local,"localPosition"));
                    var localNormals=Array(Field(local,"localNormal"));var vertices=Array(Field(block,"vertexIndices"));var bones=Array(Field(block,"boneIndices"));int blends=4-kind;
                    for(int lane=0;lane<vertices.Count;lane++)
                    {
                        int vertex=(int)Number(vertices[lane]);if(!targets.TryGetValue(vertex,out int particle))continue;
                        var weights=new List<ClothPreviewWeight>();var normals=new List<Vector3>();float total=0;
                        for(int blend=0;blend<blends;blend++)
                        {
                            int slot=lane*blends+blend;if(slot>=positions.Count||slot>=bones.Count)throw new InvalidOperationException("BoneSpaceSkin influence arrays are truncated");
                            var p=Array(positions[slot]);if(p.Count!=4)throw new InvalidOperationException("weighted bone-space position is not a vector4");
                            float weight=Number(p[3]);if(!float.IsFinite(weight)||weight<0)throw new InvalidOperationException("BoneSpaceSkin has invalid homogeneous weights");if(weight==0)continue;
                            int index=(int)Number(bones[slot]);if((uint)index>=subset.Length||(uint)subset[index]>=names.Length||(uint)subset[index]>=reference.Length)throw new InvalidOperationException("BoneSpaceSkin subset index is invalid");
                            int bone=subset[index];var point=new Vector3(Number(p[0]),Number(p[1]),Number(p[2]))/weight;
                            float error=Vector3.Distance(Vector3.Transform(point,reference[bone]),rest[particle]);
                            if(!Finite(point)||!float.IsFinite(error)||error>.00025f)throw new InvalidOperationException("weighted local position does not reconstruct the MoveParticles rest target (error "+error+")");
                            maximumError=Math.Max(maximumError,error);weights.Add(new(names[bone],weight));total+=weight;
                            if(referenceNormals!=null)
                            {
                                if(slot>=localNormals.Count)throw new InvalidOperationException("range normal is absent from authored PN data");
                                var packed=Array(Field(localNormals[slot],"values"));var localNormal=Vec(localNormals[slot]);
                                if(packed.Count==4)
                                {
                                    float quantization=BitConverter.Int32BitsToSingle(unchecked((ushort)(int)Number(packed[3])<<16))*65536f;
                                    localNormal=new Vector3(Number(packed[0]),Number(packed[1]),Number(packed[2]))*quantization;
                                }
                                var normal=Vector3.TransformNormal(localNormal,Matrix.Transpose(Matrix.Invert(reference[bone])));
                                if(!Finite(normal)||normal.LengthSquared()<1e-15f)throw new InvalidOperationException("range reference normal is invalid");
                                normals.Add(normal);
                            }
                        }
                        if(weights.Count==0||Math.Abs(total-1)>.005f)throw new InvalidOperationException("BoneSpaceSkin weights do not sum to one");
                        if(pending.TryGetValue(particle,out var prior)&&!prior.SequenceEqual(weights))throw new InvalidOperationException("repeated BoneSpaceSkin vertex has conflicting influences");
                        pending[particle]=weights.ToArray();if(referenceNormals!=null)normalsPending[particle]=normals.ToArray();
                    }
                }
                if(targets.Values.Distinct().Any(p=>!pending.ContainsKey(p)))throw new InvalidOperationException("not every MoveParticles target has a decoded skin vertex");
                foreach(var entry in pending){bindings[entry.Key]=entry.Value;if(referenceNormals!=null)referenceNormals[entry.Key]=normalsPending[entry.Key];}
                notices.Add("Verified BoneSpaceSkin MoveParticles bindings: "+pending.Count+"; per-influence reference reconstruction error "+maximumError);
            }
            catch(InvalidOperationException e){notices.Add("BoneSpaceSkin fixed particle input not bound: "+e.Message);}
        }
        public static ClothPreviewDefinition[] Read(byte[] bytes,ClothMeshSource[] meshes=null)
        {
            if(bytes==null||bytes.Length<16)throw new InvalidOperationException("No cloth HKX data in this model binder.");
            if(bytes[0]==0x57)return ClothPackfileReader.Read(bytes,meshes);
            if(bytes.Length>64*1024*1024)throw new NotSupportedException("Cloth HKX exceeds the 64 MiB inspection budget.");
            var all=Objects(HkBinaryTagfileReader.Read(bytes,null)).ToArray();
            var skeletons=all.Where(o=>o.Type.Name=="hkaSkeleton").ToArray();var result=new List<ClothPreviewDefinition>();
            foreach(var cloth in all.Where(o=>o.Type.Name=="hclClothData"))
            {
                string clothName=Text(Field(cloth,"name"));var ops=Array(Field(cloth,"operators")).Select(Unwrap).Where(o=>o!=null).ToArray();
                var transforms=Array(Field(cloth,"transformSetDefinitions"));
                var sims=Array(Field(cloth,"simClothDatas"));
                for(int simIndex=0;simIndex<sims.Count;simIndex++)
                {
                    var sim=sims[simIndex];var poses=Array(Field(sim,"simClothPoses"));
                    if(poses.Count==0)continue;
                    var positions=Array(Field(poses[0],"positions")).Select(Vec).ToArray();if(positions.Length==0||positions.Length>32768)continue;
                    var inverse=Array(Field(sim,"particleDatas")).Select(p=>Math.Max(0,Number(Field(p,"invMass")))).ToArray();
                    if(inverse.Length!=positions.Length)throw new InvalidOperationException("Cloth particle data and rest pose sizes differ.");
                    var fixedParticles=new bool[positions.Length];foreach(var p in Array(Field(sim,"fixedParticles")))if((uint)Number(p)<fixedParticles.Length)fixedParticles[(int)Number(p)]=true;
                    var links=new List<ClothPreviewLink>();var limitations=new HashSet<string>();
                    foreach(var constraint in Array(Field(sim,"staticConstraintSets")).Select(Unwrap).Where(o=>o!=null))
                    {
                        if(constraint.Type.Name is "hclStandardLinkConstraintSet" or "hclStretchLinkConstraintSet")
                        foreach(var l in Array(Field(constraint,"links")))
                        {
                            int a=(int)Number(Field(l,"particleA")),b=(int)Number(Field(l,"particleB"));float rest=Number(Field(l,"restLength"));
                            if((uint)a<positions.Length&&(uint)b<positions.Length&&float.IsFinite(rest)&&rest>=0)
                                links.Add(new(a,b,rest,Math.Clamp(Number(Field(l,"stiffness"),1),0,1),constraint.Type.Name=="hclStretchLinkConstraintSet"));
                        }
                        else limitations.Add(constraint.Type.Name+" not solved");
                    }
                    var bindings=new ClothPreviewWeight[positions.Length][];
                    foreach(var move in ops.Where(o=>o.Type.Name=="hclMoveParticlesOperator"&&(int)Number(Field(o,"simClothIndex"))==simIndex))
                    {
                        int buffer=(int)Number(Field(move,"refBufferIdx"));
                        var skin=ops.FirstOrDefault(o=>o.Type.Name.StartsWith("hclObjectSpaceSkin")&&(int)Number(Field(o,"outputBufferIndex"),-1)==buffer);
                        if(skin==null)
                        {
                            var boneSkins=ops.Where(o=>o.Type.Name.StartsWith("hclBoneSpaceSkin",StringComparison.Ordinal)&&(int)Number(Field(o,"outputBufferIndex"),-1)==buffer).ToArray();
                            if(boneSkins.Length>0)
                            {
                                var alternatives=new List<ClothPreviewWeight[][]>();var proof=new List<string>();
                                var targetParticles=Array(Field(move,"vertexParticlePairs")).Select(p=>(int)Number(Field(p,"particleIndex"))).Distinct().ToArray();bool valid=true;
                                foreach(var candidateSkin in boneSkins)
                                {
                                    int set=(int)Number(Field(candidateSkin,"transformSetIndex"),-1);
                                    var candidates=(uint)set<transforms.Count?skeletons.Where(s=>Text(Field(s,"name"))==Text(Field(transforms[set],"name"))).ToArray():System.Array.Empty<IHkObject>();
                                    var candidateBindings=new ClothPreviewWeight[positions.Length][];var candidateProof=new List<string>();
                                    ReadBoneSpaceAnchors(candidateSkin,move,candidates.Length==1?candidates[0]:null,positions,candidateBindings,candidateProof);
                                    foreach(var item in candidateProof)proof.Add(candidateSkin.Type.Name+": "+item);
                                    if(targetParticles.Any(p=>(uint)p>=candidateBindings.Length||candidateBindings[p]==null))valid=false;
                                    alternatives.Add(candidateBindings);
                                }
                                if(valid)
                                {
                                    foreach(var alternative in alternatives.Skip(1))foreach(int particle in targetParticles)
                                        if(!alternatives[0][particle].SequenceEqual(alternative[particle]))valid=false;
                                }
                                if(valid)
                                {
                                    foreach(int particle in targetParticles)bindings[particle]=alternatives[0][particle];
                                    limitations.Add("Verified BoneSpaceSkin producer consensus: "+boneSkins.Length+" complete independently reconstructed inputs agree on all "+targetParticles.Length+" MoveParticles targets");
                                    foreach(var item in proof)limitations.Add(item);
                                }
                                else limitations.Add("BoneSpaceSkin fixed particle input not bound: MoveParticles refBuffer="+buffer+" producers are incomplete or disagree; "+string.Join("; ",proof));
                            }
                            else
                            {
                                string producers=string.Join(", ",ops.Select(o=>o.Type.Name+"[input="+Number(Field(o,"inputBufferIndex"),Number(Field(o,"inputBufferIdx"),-1))+",output="+Number(Field(o,"outputBufferIndex"),Number(Field(o,"outputBufferIdx"),-1))+"]"));
                                limitations.Add("BoneSpaceSkin fixed particle input not bound: MoveParticles refBuffer="+buffer+" has "+boneSkins.Length+" direct BoneSpaceSkin producers and no direct ObjectSpaceSkin producer; intermediate buffer remapping is not inferred. Operators: "+producers);
                            }
                            continue;
                        }
                        int transformSet=(int)Number(Field(skin,"transformSetIndex"));
                        var skeleton=transformSet>=0&&transformSet<transforms.Count?skeletons.FirstOrDefault(s=>Text(Field(s,"name"))==Text(Field(transforms[transformSet],"name"))):null;
                        var boneNames=Array(Field(skeleton,"bones")).Select(b=>Text(Field(b,"name"))).ToArray();
                        var subset=Array(Field(skin,"transformSubset")).Select(n=>(int)Number(n)).ToArray();
                        var weights=new Dictionary<int,ClothPreviewWeight[]>();var deform=Field(skin,"objectSpaceDeformer");
                        string[] names={"one","two","three","four","five","six","seven","eight"};
                        for(int n=1;n<=8;n++)foreach(var block in Array(Field(deform,names[n-1]+"BlendEntries")))
                        {
                            var vertices=Array(Field(block,"vertexIndices"));var bones=Array(Field(block,"boneIndices"));var values=Array(Field(block,"boneWeights"));
                            for(int i=0;i<vertices.Count;i++)
                            {
                                var w=new List<ClothPreviewWeight>();
                                for(int k=0;k<n;k++)
                                {
                                    int at=i*n+k;if(at>=bones.Count)continue;int b=(int)Number(bones[at]);
                                    if((uint)b>=subset.Length||(uint)subset[b]>=boneNames.Length)continue;
                                    float weight=n==1?1:at<values.Count?Number(values[at])/255f:0;
                                    if(weight>0)w.Add(new(boneNames[subset[b]],weight));
                                }
                                if(w.Count>0)weights[(int)Number(vertices[i])]=w.ToArray();
                            }
                        }
                        foreach(var pair in Array(Field(move,"vertexParticlePairs")))
                        {
                            int particle=(int)Number(Field(pair,"particleIndex")),vertex=(int)Number(Field(pair,"vertexIndex"));
                            if((uint)particle<bindings.Length&&weights.TryGetValue(vertex,out var w))bindings[particle]=w;
                        }
                    }
                    if(fixedParticles.Where((f,i)=>f&&bindings[i]==null).Any())limitations.Add("Some fixed particles have no supported animation binding; held in model reference space");
                    var triangles=Array(Field(sim,"triangleIndices")).Select(n=>(int)Number(n)).ToArray();
                    if(triangles.Any(i=>(uint)i>=positions.Length))throw new InvalidOperationException("Cloth triangle index outside particle range.");
                    var info=Field(sim,"simulationInfo");limitations.Add("Experimental PBD links and authored body collision preview; native operator order, bend/range constraints and self/complex collisions remain partial");
                    var definition=new ClothPreviewDefinition(clothName+" / "+Text(Field(sim,"name")),positions,inverse,fixedParticles,triangles,links.ToArray(),bindings,Vec(Field(info,"gravity")),Math.Clamp(Number(Field(info,"globalDampingPerSecond"),.99f),0,1),limitations.ToArray());
                    var mappings=ClothTagfileMeshMapping.Read(cloth,simIndex,definition,meshes,limitations);
                    definition=ClothCopyPipeline.Read(cloth,simIndex,definition with{MeshBindings=mappings},skeletons,meshes,limitations);
                    definition=definition with{BoneBindings=ClothBoneMapping.Read(cloth,simIndex,definition,skeletons,limitations)};
                    if(definition.MeshBindings.Length==0&&definition.BoneBindings.Length==0)limitations.Add("No verified FLVER deformation mapping; original model retained");
                    var collisions=ReadCollisions(sim,transforms,skeletons,limitations);
                    result.Add(definition with{Collisions=collisions,Limitations=limitations.ToArray()});
                }
            }
            if(result.Count==0)throw new NotSupportedException("No supported hclSimClothData rest poses were found in this HKX.");
            return result.ToArray();
        }
    }
}
