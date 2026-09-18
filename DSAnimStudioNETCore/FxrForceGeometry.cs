using System;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // Convex field volumes in FXR local coordinates. Cylinder / prism +Z is a
    // preview convention: the library documents their sizes but not a native basis.
    public static class FxrForceGeometry
    {
        public static bool Supports(JObject shape) => ((int?)shape?["shape"] ?? 1) is >= 0 and <= 4 && !((int?)shape?["type"]==10302&&(int?)shape?["shape"]==4);
        static float V(JObject a,string key,int seed,float fallback=0)=>Math.Max(0,FxrPreviewValue.Get(a?[key],0,seed,fallback));
        static Vector3 Abs(Vector3 v)=>new(Math.Abs(v.X),Math.Abs(v.Y),Math.Abs(v.Z));
        public static bool Contains(JObject shape,Vector3 local,int seed)
        {
            if(!Supports(shape))return false;
            switch((int?)shape?["shape"]??1)
            {
                case 0:return true;
                case 1:float r=V(shape,"sphereRadius",seed,10);return local.LengthSquared()<=r*r;
                case 2:var s=Abs(FxrPreviewValue.Vec(shape?["boxSize"],0,seed))*.5f;return Math.Abs(local.X)<=s.X&&Math.Abs(local.Y)<=s.Y&&Math.Abs(local.Z)<=s.Z;
                case 3:float cr=V(shape,"cylinderRadius",seed),h=V(shape,"cylinderHeight",seed);return local.X*local.X+local.Y*local.Y<=cr*cr&&Math.Abs(local.Z)<=h*.5f;
                case 4:float p=V(shape,"squarePrismApothem",seed),ph=V(shape,"squarePrismHeight",seed);return Math.Abs(local.X)<=p&&Math.Abs(local.Y)<=p&&local.Z>=0&&local.Z<=ph;
                default:return false;
            }
        }
        static Vector3 Support(JObject shape,Matrix pose,Vector3 direction,int seed)
        {
            var d=Vector3.TransformNormal(direction,Matrix.Transpose(pose));Vector3 p;
            switch((int?)shape["shape"]??1)
            {
                case 1:p=BulletMath.Unit(d,Vector3.Right)*V(shape,"sphereRadius",seed,10);break;
                case 2:var s=Abs(FxrPreviewValue.Vec(shape["boxSize"],0,seed))*.5f;p=new(d.X>=0?s.X:-s.X,d.Y>=0?s.Y:-s.Y,d.Z>=0?s.Z:-s.Z);break;
                case 3:float r=V(shape,"cylinderRadius",seed),h=V(shape,"cylinderHeight",seed)*.5f;float q=MathF.Sqrt(d.X*d.X+d.Y*d.Y);p=new(q>1e-9f?d.X*r/q:r,q>1e-9f?d.Y*r/q:0,d.Z>=0?h:-h);break;
                case 4:float a=V(shape,"squarePrismApothem",seed);p=new(d.X>=0?a:-a,d.Y>=0?a:-a,d.Z>=0?V(shape,"squarePrismHeight",seed):0);break;
                default:p=Vector3.Zero;break;
            }
            return Vector3.Transform(p,pose);
        }
        // GJK gives receiver/field overlap without replacing an elongated authored
        // receiver by a giant bounding sphere. All scratch state is on the stack.
        public static bool Intersects(JObject a,Matrix pa,int sa,JObject b,Matrix pb,int sb,out bool limited)
        {
            limited=false;
            if(!Supports(a)||!Supports(b))return false;
            if((int?)a["shape"]==0||(int?)b["shape"]==0)return true;
            if(Math.Abs(pa.Determinant())<1e-10f||Math.Abs(pb.Determinant())<1e-10f)return false;
            Vector3 SupportPair(Vector3 d)=>Support(a,pa,d,sa)-Support(b,pb,-d,sb);
            Span<Vector3> points=stackalloc Vector3[4];int count=1;
            var direction=pb.Translation-pa.Translation;if(direction.LengthSquared()<1e-12f)direction=Vector3.Right;
            points[0]=SupportPair(direction);direction=-points[0];
            for(int i=0;i<40;i++)
            {
                if(direction.LengthSquared()<1e-14f)return true;
                var next=SupportPair(direction);
                if(Vector3.Dot(next,direction)<-1e-6f)return false;
                for(int n=count;n>0;n--)points[n]=points[n-1];points[0]=next;count++;
                if(Simplex(points,ref count,ref direction))return true;
            }
            limited=true;return false;
        }
        static Vector3 CrossTowards(Vector3 edge,Vector3 point)=>Vector3.Cross(Vector3.Cross(edge,point),edge);
        static bool Simplex(Span<Vector3> p,ref int count,ref Vector3 d)
        {
            var a=p[0];var ao=-a;var ab=p[1]-a;
            if(count==2)
            {
                if(Vector3.Dot(ab,ao)>0)d=CrossTowards(ab,ao);else{count=1;d=ao;}
                return false;
            }
            var ac=p[2]-a;var normal=Vector3.Cross(ab,ac);
            if(count==3)
            {
                if(Vector3.Dot(Vector3.Cross(normal,ac),ao)>0)
                {
                    if(Vector3.Dot(ac,ao)>0){p[1]=p[2];count=2;d=CrossTowards(ac,ao);}
                    else{count=2;d=Vector3.Dot(ab,ao)>0?CrossTowards(ab,ao):ao;if(Vector3.Dot(ab,ao)<=0)count=1;}
                }
                else if(Vector3.Dot(Vector3.Cross(ab,normal),ao)>0){count=2;d=Vector3.Dot(ab,ao)>0?CrossTowards(ab,ao):ao;if(Vector3.Dot(ab,ao)<=0)count=1;}
                else if(Vector3.Dot(normal,ao)>=0)d=normal;
                else{(p[1],p[2])=(p[2],p[1]);d=-normal;}
                return false;
            }
            var ad=p[3]-a;
            // Orient each face away from the opposite tetrahedron point.
            var abc=normal;if(Vector3.Dot(abc,ad)>0)abc=-abc;
            var acd=Vector3.Cross(ac,ad);if(Vector3.Dot(acd,ab)>0)acd=-acd;
            var adb=Vector3.Cross(ad,ab);if(Vector3.Dot(adb,ac)>0)adb=-adb;
            if(Vector3.Dot(abc,ao)>0){count=3;d=abc;return false;}
            if(Vector3.Dot(acd,ao)>0){p[1]=p[2];p[2]=p[3];count=3;d=acd;return false;}
            if(Vector3.Dot(adb,ao)>0){p[2]=p[1];p[1]=p[3];count=3;d=adb;return false;}
            return true;
        }
    }
    public static class FxrTurbulenceNoise
    {
        // Seeded, continuous curl of trilinear smooth value noise. Native noise
        // and random modulation scheduling remain unknown; this is deterministic.
        static float Hash(int x,int y,int z,int seed)
        {
            uint h=unchecked((uint)(x*73856093^y*19349663^z*83492791^seed));
            h^=h>>16;h*=0x7feb352d;h^=h>>15;h*=0x846ca68b;h^=h>>16;
            return (h&0x00ffffff)/8388607.5f-1;
        }
        static float Noise(Vector3 p,int seed)
        {
            if(!BulletMath.Finite(p)||Math.Abs(p.X)>1e7f||Math.Abs(p.Y)>1e7f||Math.Abs(p.Z)>1e7f)return 0;
            int x=(int)MathF.Floor(p.X),y=(int)MathF.Floor(p.Y),z=(int)MathF.Floor(p.Z);
            var f=p-new Vector3(x,y,z);f=f*f*(new Vector3(3)-2*f);
            float a=MathHelper.Lerp(Hash(x,y,z,seed),Hash(x+1,y,z,seed),f.X),b=MathHelper.Lerp(Hash(x,y+1,z,seed),Hash(x+1,y+1,z,seed),f.X);
            float c=MathHelper.Lerp(Hash(x,y,z+1,seed),Hash(x+1,y,z+1,seed),f.X),d=MathHelper.Lerp(Hash(x,y+1,z+1,seed),Hash(x+1,y+1,z+1,seed),f.X);
            return MathHelper.Lerp(MathHelper.Lerp(a,b,f.Y),MathHelper.Lerp(c,d,f.Y),f.Z);
        }
        public static Vector3 Curl(Vector3 p,int seed)
        {
            const float e=.01f;var x=new Vector3(e,0,0);var y=new Vector3(0,e,0);var z=new Vector3(0,0,e);
            float Derivative(Vector3 delta,int s)=>(Noise(p+delta,s)-Noise(p-delta,s))/(2*e);
            return new(Derivative(y,seed+2)-Derivative(z,seed+1),Derivative(z,seed)-Derivative(x,seed+2),Derivative(x,seed+1)-Derivative(y,seed));
        }
    }
}
