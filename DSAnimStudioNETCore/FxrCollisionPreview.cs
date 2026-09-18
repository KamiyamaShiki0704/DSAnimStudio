using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public readonly record struct FxrCollisionContact(float Fraction,Vector3 Center,Vector3 Normal);

    // Explicit preview geometry only. An empty world never creates an invisible
    // floor. GPU depth-buffer collisions are a separate native mechanism.
    public sealed class FxrCollisionWorld
    {
        readonly List<(Vector3 Normal,float D)> planes=new();
        readonly List<(Vector3 A,Vector3 B,float Radius)> capsules=new();
        readonly List<(Vector3 A,Vector3 B,Vector3 C,Vector3 Min,Vector3 Max)> triangles=new();
        readonly record struct TreeNode(Vector3 Min,Vector3 Max,int Start,int Count,int Left,int Right);
        readonly List<TreeNode> tree=new();
        int[] triangleOrder;
        public int Count=>planes.Count+capsules.Count+triangles.Count;
        public bool Limited {get;private set;}
        public void AddPlane(Vector3 normal,float d)
        {
            float length=normal.Length();
            if(planes.Count>=32||!BulletMath.Finite(normal)||!float.IsFinite(d)||length<1e-8f){Limited=true;return;}
            planes.Add((normal/length,d/length));
        }
        public void AddCapsule(Vector3 a,Vector3 b,float radius)
        {
            if(capsules.Count>=128||!BulletMath.Finite(a)||!BulletMath.Finite(b)||!float.IsFinite(radius)||radius<0){Limited=true;return;}
            capsules.Add((a,b,radius));
        }
        public void AddTriangle(Vector3 a,Vector3 b,Vector3 c)
        {
            if(triangles.Count>=32768||!BulletMath.Finite(a)||!BulletMath.Finite(b)||!BulletMath.Finite(c)){Limited=true;return;}
            if(Vector3.Cross(b-a,c-a).LengthSquared()<1e-16f)return;
            triangles.Add((a,b,c,Vector3.Min(a,Vector3.Min(b,c)),Vector3.Max(a,Vector3.Max(b,c))));
            triangleOrder=null;tree.Clear();
        }
        void PrepareTree()
        {
            if(triangleOrder!=null)return;
            triangleOrder=new int[triangles.Count];for(int i=0;i<triangleOrder.Length;i++)triangleOrder[i]=i;
            if(triangles.Count>0)BuildNode(0,triangles.Count);
        }
        int BuildNode(int start,int count)
        {
            var min=new Vector3(float.MaxValue);var max=new Vector3(float.MinValue);
            for(int i=start;i<start+count;i++){var t=triangles[triangleOrder[i]];min=Vector3.Min(min,t.Min);max=Vector3.Max(max,t.Max);}
            int node=tree.Count;tree.Add(default);
            if(count<=8){tree[node]=new(min,max,start,count,-1,-1);return node;}
            var size=max-min;int axis=size.X>size.Y?(size.X>size.Z?0:2):(size.Y>size.Z?1:2);
            float Component(Vector3 v)=>axis==0?v.X:axis==1?v.Y:v.Z;
            Array.Sort(triangleOrder,start,count,Comparer<int>.Create((a,b)=>
            {
                var ta=triangles[a];var tb=triangles[b];int order=Component(ta.Min+ta.Max).CompareTo(Component(tb.Min+tb.Max));
                return order!=0?order:a.CompareTo(b);
            }));
            int half=count/2;int left=BuildNode(start,half),right=BuildNode(start+half,count-half);
            tree[node]=new(min,max,start,0,left,right);return node;
        }
        static Vector3 ClosestSegment(Vector3 p,Vector3 a,Vector3 b)
        {var d=b-a;float q=d.LengthSquared();return q<1e-12f?a:a+d*Math.Clamp(Vector3.Dot(p-a,d)/q,0,1);}
        static Vector3 ClosestTriangle(Vector3 p,Vector3 a,Vector3 b,Vector3 c)
        {
            var ab=b-a;var ac=c-a;var ap=p-a;float d1=Vector3.Dot(ab,ap),d2=Vector3.Dot(ac,ap);
            if(d1<=0&&d2<=0)return a;
            var bp=p-b;float d3=Vector3.Dot(ab,bp),d4=Vector3.Dot(ac,bp);if(d3>=0&&d4<=d3)return b;
            float vc=d1*d4-d3*d2;if(vc<=0&&d1>=0&&d3<=0)return a+ab*(d1/(d1-d3));
            var cp=p-c;float d5=Vector3.Dot(ab,cp),d6=Vector3.Dot(ac,cp);if(d6>=0&&d5<=d6)return c;
            float vb=d5*d2-d1*d6;if(vb<=0&&d2>=0&&d6<=0)return a+ac*(d2/(d2-d6));
            float va=d3*d6-d5*d4;if(va<=0&&d4>=d3&&d5>=d6)return b+(c-b)*((d4-d3)/(d4-d3+d5-d6));
            float inv=1/(va+vb+vc);return a+ab*(vb*inv)+ac*(vc*inv);
        }
        bool SweepShape(Vector3 from,Vector3 to,float radius,Vector3 a,Vector3 b,Vector3 c,float colliderRadius,bool triangle,out FxrCollisionContact hit)
        {
            hit=default;var delta=to-from;float distance=delta.Length(),fraction=0,totalRadius=radius+colliderRadius,tolerance=Math.Max(1e-5f,distance*2e-7f);
            for(int iteration=0;iteration<40;iteration++)
            {
                var center=from+delta*fraction;var closest=triangle?ClosestTriangle(center,a,b,c):ClosestSegment(center,a,b);
                var offset=center-closest;float separation=offset.Length();
                var normal=BulletMath.Unit(offset,triangle?BulletMath.Unit(Vector3.Cross(b-a,c-a),Vector3.Up):BulletMath.Unit(-delta,Vector3.Up));
                float gap=separation-totalRadius;
                if(gap<=tolerance)
                {
                    // A touching particle leaving a surface must not be bounced
                    // again. Initial penetration is resolved even if stationary.
                    if(fraction==0&&gap>=-1e-5f&&Vector3.Dot(delta,normal)>=0)return false;
                    if(triangle&&separation<1e-7f&&Vector3.Dot(delta,normal)>0)normal=-normal;
                    hit=new(fraction,closest+normal*(totalRadius+1e-5f),normal);return true;
                }
                if(distance<1e-9f)return false;
                fraction+=gap/distance;
                if(fraction>1)return false;
            }
            Limited=true;return false;
        }
        public bool Sweep(Vector3 from,Vector3 to,float radius,out FxrCollisionContact hit)
        {
            hit=new(2,Vector3.Zero,Vector3.Zero);
            if(!BulletMath.Finite(from)||!BulletMath.Finite(to)||!float.IsFinite(radius)){Limited=true;return false;}
            radius=Math.Max(0,radius);var delta=to-from;
            foreach(var p in planes)
            {
                float a=Vector3.Dot(from,p.Normal)+p.D-radius,b=Vector3.Dot(to,p.Normal)+p.D-radius;
                if(a>=-1e-6f&&b>=0)continue;
                float t=a<=0?0:a/(a-b);if(t<0||t>1||t>=hit.Fraction)continue;
                var center=from+delta*t;float depth=Vector3.Dot(center,p.Normal)+p.D-radius;
                center+=p.Normal*(1e-5f-depth);hit=new(t,center,p.Normal);
            }
            var min=Vector3.Min(from,to)-new Vector3(radius);var max=Vector3.Max(from,to)+new Vector3(radius);
            foreach(var c in capsules)
            {
                var extent=new Vector3(c.Radius);
                if(!Overlap(min,max,Vector3.Min(c.A,c.B)-extent,Vector3.Max(c.A,c.B)+extent))continue;
                if(SweepShape(from,to,radius,c.A,c.B,Vector3.Zero,c.Radius,false,out var next)&&next.Fraction<hit.Fraction)hit=next;
            }
            if(triangles.Count>0)
            {
                PrepareTree();Span<int> pending=stackalloc int[64];int count=1;pending[0]=0;
                while(count>0)
                {
                    var node=tree[pending[--count]];if(!Overlap(min,max,node.Min,node.Max))continue;
                    if(node.Count==0){pending[count++]=node.Left;pending[count++]=node.Right;continue;}
                    for(int i=node.Start;i<node.Start+node.Count;i++)
                    {
                        var t=triangles[triangleOrder[i]];if(!Overlap(min,max,t.Min,t.Max))continue;
                        if(SweepShape(from,to,radius,t.A,t.B,t.C,0,true,out var next)&&next.Fraction<hit.Fraction)hit=next;
                    }
                }
            }
            return hit.Fraction<=1;
        }
        static bool Overlap(Vector3 min,Vector3 max,Vector3 otherMin,Vector3 otherMax)=>min.X<=otherMax.X&&max.X>=otherMin.X&&min.Y<=otherMax.Y&&max.Y>=otherMin.Y&&min.Z<=otherMax.Z&&max.Z>=otherMin.Z;
    }

    public sealed class FxrCollisionMotion
    {
        readonly Func<float,Matrix> baseAt;
        readonly FxrCollisionWorld world;
        readonly JObject action;
        readonly float birth;
        readonly int seed;
        readonly List<(Vector3 Offset,Vector3 Velocity)> samples=new(){(Vector3.Zero,Vector3.Zero)};
        const float Step=1f/120;
        public bool Limited {get;private set;}
        public FxrCollisionMotion(Func<float,Matrix> baseAt,FxrCollisionWorld world,JObject action,float birth,int seed)
        {this.baseAt=baseAt;this.world=world;this.action=action;this.birth=birth;this.seed=seed;}
        (Vector3 Offset,Vector3 Velocity) Advance((Vector3 Offset,Vector3 Velocity) state,float a,float b)
        {
            float dt=b-a;var baseStart=baseAt(a).Translation;var baseEnd=baseAt(b).Translation;
            var baseVelocity=(baseEnd-baseStart)/dt;var velocity=baseVelocity+state.Velocity;
            var start=baseStart+state.Offset;float remaining=dt;
            float radius=Math.Max(0,FxrPreviewValue.Get(action["radius"],a-birth,seed,1));
            float friction=FxrPreviewValue.Get(action["friction"],a-birth,seed,.5f),bounce=Math.Max(0,FxrPreviewValue.Get(action["bounciness"],a-birth,seed,.5f));
            for(int collision=0;collision<8;collision++)
            {
                var target=start+velocity*remaining;
                if(!world.Sweep(start,target,radius,out var hit))return(target-baseEnd,velocity-baseVelocity);
                start=hit.Center;remaining*=1-hit.Fraction;
                float normalSpeed=Vector3.Dot(velocity,hit.Normal);
                if(normalSpeed<0)
                {
                    var tangent=velocity-hit.Normal*normalSpeed;
                    velocity=tangent*Math.Max(0,1-friction)-hit.Normal*normalSpeed*bounce;
                }
                if(!BulletMath.Finite(velocity)||!BulletMath.Finite(start)){Limited=true;return state;}
                if(remaining<1e-7f)return(start-baseEnd,velocity-baseVelocity);
            }
            Limited=true;return(start-baseEnd,velocity-baseVelocity);
        }
        public Matrix At(float time)
        {
            if(world==null||world.Count==0)return baseAt(time);
            float age=Math.Max(0,time-birth);if(age>4096*Step){Limited=true;age=4096*Step;}
            int index=(int)Math.Floor(age/Step);
            while(samples.Count<=index){int n=samples.Count;samples.Add(Advance(samples[^1],birth+(n-1)*Step,birth+n*Step));}
            var result=samples[index];if(age-index*Step>1e-7f)result=Advance(result,birth+index*Step,birth+age);
            var pose=baseAt(time);pose.Translation+=result.Offset;Limited|=world.Limited;return pose;
        }
    }
}
