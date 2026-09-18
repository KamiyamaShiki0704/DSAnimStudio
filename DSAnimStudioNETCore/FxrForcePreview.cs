using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // Time is relative to the currently rendered frame, shared across FXR instances.
    public sealed record FxrForceVolume(JObject Action,Func<float,Matrix> Pose,float Begin,float End,float Now,float ClockScale,int Seed,ulong Groups=ulong.MaxValue)
    {
        FxrFrameMemo<Matrix> poses,inverses;
        Matrix PoseAt(float time)=>(poses??=new(Pose,4096)).At(time);
        Matrix InverseAt(float time)=>(inverses??=new(t=>Matrix.Invert(PoseAt(t)),4096)).At(time);
        public int Type=>(int?)Action["type"]??0;
        internal bool Accepts(bool windAllowed)=>Type is 10301 or 10303||Type==10300&&windAllowed&&(int?)Action["unk_ds3_f1_31"]!=1;
        public bool Limited {get;private set;}
        bool Active(float offset)
        {
            float time=Now+offset*ClockScale;
            float tail=Type==10200?0:FxrPreviewValue.Get(Action["fadeOutTime"],0,Seed);
            return time>=Begin&&time<End+Math.Max(0,tail);
        }
        public bool Contains(Vector3 position,float offset)
        {
            float time=Now+offset*ClockScale;
            if(!Active(offset))return false;
            int shape=(int?)Action["shape"]??1;if(shape==0)return true;
            var pose=PoseAt(time);float determinant=pose.Determinant();if(!float.IsFinite(determinant)||Math.Abs(determinant)<1e-9f)return false;
            return FxrForceGeometry.Contains(Action,Vector3.Transform(position,InverseAt(time)),Seed);
        }
        public bool Overlaps(JObject receiver,Matrix pose,int seed,float offset)
        {
            if(!Active(offset))return false;
            bool result=FxrForceGeometry.Intersects(Action,PoseAt(Now+offset*ClockScale),Seed,receiver,pose,seed,out bool limited);
            Limited|=limited;return result;
        }
        public Vector3 Force(Vector3 position,float offset,bool windAllowed,bool volumeOverlap=false)
        {
            if(!Accepts(windAllowed)||(!volumeOverlap&&!Contains(position,offset)))return Vector3.Zero;
            float time=Now+offset*ClockScale,age=Math.Max(0,time-Begin);
            float min=FxrPreviewValue.Get(Action["forceRandomMultiplierMin"],0,Seed,1),max=FxrPreviewValue.Get(Action["forceRandomMultiplierMax"],0,Seed,1);
            float multiplier=(min+max)*.5f;
            float strength=FxrPreviewValue.Get(Action["force"],age,Seed,1)*FxrPreviewValue.Get(Action["forceMultiplier"],age,Seed,1)*multiplier;
            if(time>=End)strength*=Math.Clamp(1-(time-End)/Math.Max(1e-6f,FxrPreviewValue.Get(Action["fadeOutTime"],0,Seed)),0,1);
            var pose=PoseAt(time);
            bool softRadius=(bool?)Action["enableSoftRadius"]==true;
            var local=Type==10303||softRadius?Vector3.Transform(position,InverseAt(time)):Vector3.Zero;
            if(softRadius)
            {
                float radius=Math.Max(1e-6f,FxrPreviewValue.Get(Action["softRadius"],0,Seed,1));
                float fade=Math.Clamp(1-local.Length()/radius,0,1);strength*=fade*fade*(3-2*fade);
            }
            if(Type==10303)
            {
                var shift=new Vector3(FxrPreviewValue.Get(Action["noiseOffsetX"],age,Seed),FxrPreviewValue.Get(Action["noiseOffsetY"],age,Seed),FxrPreviewValue.Get(Action["noiseOffsetZ"],age,Seed));
                float scale=Math.Max(1e-5f,Math.Abs(FxrPreviewValue.Get(Action["noiseScale"],0,Seed,1)));
                var curl=FxrTurbulenceNoise.Curl((local+shift)/scale,Seed);
                var world=Vector3.TransformNormal(curl,pose);
                // Preserve local noise magnitude under node scale; rotation alone
                // changes the direction. Native modulation is not inferred here.
                return world.LengthSquared()>1e-12f?Vector3.Normalize(world)*curl.Length()*strength:Vector3.Zero;
            }
            var direction=Type==10301?pose.Translation-position:pose.Backward;
            return direction.LengthSquared()<1e-12f?Vector3.Zero:Vector3.Normalize(direction)*strength;
        }
    }
    public sealed class FxrForceSet
    {
        readonly List<FxrForceVolume> volumes=new();
        public bool Limited {get;private set;}
        public int Count=>volumes.Count;
        public void Add(FxrForceVolume volume){if(volumes.Count<256)volumes.Add(volume);else Limited=true;}
        public void AddRange(FxrForceSet other){foreach(var v in other.volumes)Add(v);Limited|=other.Limited;}
        public Vector3 Sample(Vector3 position,float offset,bool windAllowed=true,JObject receiver=null,Matrix? receiverPose=null,int receiverSeed=0,ulong receiverGroups=ulong.MaxValue)
        {
            bool Hit(FxrForceVolume v)=>FxrForceGroups.Matches(v.Groups,receiverGroups)&&(receiver!=null?v.Overlaps(receiver,receiverPose??Matrix.CreateTranslation(position),receiverSeed,offset):v.Contains(position,offset));
            foreach(var v in volumes)if(v.Type==10200&&Hit(v)){Limited|=v.Limited;return Vector3.Zero;}
            var sum=Vector3.Zero;
            foreach(var v in volumes)
            {
                if(!v.Accepts(windAllowed)||!FxrForceGroups.Matches(v.Groups,receiverGroups))continue;
                bool overlaps=receiver!=null&&Hit(v);
                if(receiver!=null&&!overlaps){Limited|=v.Limited;continue;}
                var next=sum+v.Force(position,offset,windAllowed,overlaps);Limited|=v.Limited;
                if(BulletMath.Finite(next))sum=next;else Limited=true;
            }
            return sum;
        }
    }
    public sealed class FxrForceMotion
    {
        readonly Func<float,Matrix> baseAt;
        readonly FxrForceSet fields;
        readonly JObject action;
        readonly JObject receiverVolume;
        readonly float birth,activeBegin,now,clockScale;
        readonly int seed;
        readonly ulong receiverGroups;
        readonly List<(Vector3 Offset,Vector3 Velocity)> samples=new(){(Vector3.Zero,Vector3.Zero)};
        const float Step=1f/120;
        public bool Limited {get;private set;}
        public FxrForceMotion(Func<float,Matrix> baseAt,FxrForceSet fields,JObject action,float birth,float activeBegin,float now,float clockScale,int seed,JObject receiverVolume=null,ulong receiverGroups=ulong.MaxValue)
        {this.baseAt=baseAt;this.fields=fields;this.action=action;this.birth=birth;this.activeBegin=activeBegin;this.now=now;this.clockScale=Math.Max(1e-6f,clockScale);this.seed=seed;this.receiverVolume=receiverVolume;this.receiverGroups=receiverGroups;}
        (Vector3 Offset,Vector3 Velocity) Advance((Vector3 Offset,Vector3 Velocity) state,float a,float b)
        {
            float mid=(a+b)*.5f,dt=b-a;
            var receiverPose=baseAt(mid);var position=receiverPose.Translation+state.Offset+state.Velocity*(dt*.5f);receiverPose.Translation=position;
            bool windAllowed=(int?)action["unk_sdt_f1_1"] is null or 0 or 1;
            var force=fields.Sample(position,(mid-now)/clockScale,windAllowed,receiverVolume,receiverPose,seed,receiverGroups);
            bool acceleration=(int?)action["type"] is 733 or 734;
            string field=acceleration?"acceleration":"speed";
            force*=FxrPreviewValue.Get(action[field],mid-activeBegin,seed)*FxrPreviewValue.Get(action[field+"Multiplier"],mid-activeBegin,seed,1);
            return acceleration?(state.Offset+state.Velocity*dt+force*(.5f*dt*dt),state.Velocity+force*dt):(state.Offset+force*dt,Vector3.Zero);
        }
        public Matrix At(float time)
        {
            float age=Math.Max(0,time-birth);if(age>4096*Step){Limited=true;age=4096*Step;}
            int index=(int)Math.Floor(age/Step);
            while(samples.Count<=index){int n=samples.Count;samples.Add(Advance(samples[^1],birth+(n-1)*Step,birth+n*Step));}
            var result=samples[index];if(age-index*Step>1e-7f)result=Advance(result,birth+index*Step,birth+age);
            var pose=baseAt(time);pose.Translation+=result.Offset;return pose;
        }
    }
}
