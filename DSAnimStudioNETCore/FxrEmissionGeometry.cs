using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public static class FxrEmissionGeometry
    {
        static float V(JToken o,string k,float t,int seed,float fallback=0)=>FxrPreviewValue.Get(o?[k],t,seed,fallback);
        static float Radius(float random,float distribution)=>distribution>=0?random*(1-distribution):1-(1-random)*(1+distribution);
        public static Vector3 Planar(JToken shape,float time,int seed,bool rectangle)
        {
            float distribution=Math.Clamp(V(shape,"distribution",time,seed),-1,1);
            float r=Radius(MathF.Sqrt(FxrPreviewValue.Random(seed+32)),distribution),angle=FxrPreviewValue.Random(seed+31)*MathHelper.TwoPi;
            if(!rectangle)return new Vector3(MathF.Cos(angle),MathF.Sin(angle),0)*r*V(shape,"radius",time,seed,1);
            float x=FxrPreviewValue.Random(seed+31)*2-1,y=FxrPreviewValue.Random(seed+32)*2-1;
            float outer=Math.Max(Math.Abs(x),Math.Abs(y));
            float scale=outer>1e-7f?Radius(outer,distribution)/outer:0;
            return new Vector3(x*V(shape,"sizeX",time,seed,1),y*V(shape,"sizeY",time,seed,1),0)*(.5f*scale);
        }
        public static Vector3 Spread(Vector3 axis,JToken spread,float time,int seed)
        {
            axis=BulletMath.Unit(axis,Vector3.Backward);
            int type=(int?)spread?["type"]??500;
            float distribution=Math.Clamp(V(spread,"distribution",time,seed),-1,1);
            var right=Vector3.Normalize(Vector3.Cross(Math.Abs(axis.Y)>.99f?Vector3.Right:Vector3.Up,axis));var up=Vector3.Cross(axis,right);
            if(type is 502 or 503)
            {
                float x,y;
                if(type==502){float a=FxrPreviewValue.Random(seed+23)*MathHelper.TwoPi,r=Radius(MathF.Sqrt(FxrPreviewValue.Random(seed+22)),distribution);x=MathF.Cos(a)*r;y=MathF.Sin(a)*r;}
                else {x=FxrPreviewValue.Random(seed+22)*2-1;y=FxrPreviewValue.Random(seed+23)*2-1;float m=Math.Max(Math.Abs(x),Math.Abs(y)),s=m>1e-7f?Radius(m,distribution)/m:0;x*=s;y*=s;}
                float yaw=MathHelper.ToRadians(Math.Clamp(V(spread,"angleX",time,seed,30),0,180))*x;
                float pitch=MathHelper.ToRadians(Math.Clamp(V(spread,"angleY",time,seed,30),0,180))*y;
                return BulletMath.Unit(axis*(MathF.Cos(yaw)*MathF.Cos(pitch))+right*(MathF.Sin(yaw)*MathF.Cos(pitch))+up*MathF.Sin(pitch),axis);
            }
            float angle=MathHelper.ToRadians(Math.Clamp(V(spread,"angle",time,seed),0,180));
            if(angle<=0)return axis;
            float fraction=Radius(FxrPreviewValue.Random(seed+22),distribution),cos=MathHelper.Lerp(1,MathF.Cos(angle),fraction);
            float sin=MathF.Sqrt(Math.Max(0,1-cos*cos)),azimuth=FxrPreviewValue.Random(seed+23)*MathHelper.TwoPi;
            return axis*cos+(right*MathF.Cos(azimuth)+up*MathF.Sin(azimuth))*sin;
        }
        public static Vector3 Turn(Vector3 direction,float degrees,int seed)
        {
            var axis=BulletMath.Unit(direction,Vector3.Backward);
            float angle=MathHelper.ToRadians(Math.Clamp(Math.Abs(degrees),0,180))*FxrPreviewValue.Random(seed+1);
            float azimuth=FxrPreviewValue.Random(seed+2)*MathHelper.TwoPi;
            var right=BulletMath.Unit(Vector3.Cross(Math.Abs(axis.Y)>.99f?Vector3.Right:Vector3.Up,axis),Vector3.Right);
            var up=Vector3.Cross(axis,right);
            return BulletMath.Unit(axis*MathF.Cos(angle)+(right*MathF.Cos(azimuth)+up*MathF.Sin(azimuth))*MathF.Sin(angle),axis);
        }
    }
    public sealed class FxrParticleMotion
    {
        readonly JObject motion;
        readonly int seed;
        readonly float initialSpeed;
        readonly Vector3 initialDirection;
        public bool Limited {get;private set;}
        public FxrParticleMotion(JObject motion,int seed,float initialSpeed,Vector3 direction)
        {this.motion=motion;this.seed=seed;this.initialSpeed=initialSpeed;initialDirection=direction;}
        float cachedAge=float.NaN;
        (Vector3 Displacement,Vector3 Direction,float Speed) cachedState;
        List<(Vector3 Position,Vector3 Direction,float Distance)> turnHistory;
        internal int TurnEvaluations;
        public (Vector3 Displacement,Vector3 Direction,float Speed) At(float age)
        {
            if(age==cachedAge)return cachedState;
            cachedState=Evaluate(age);cachedAge=age;return cachedState;
        }
        (Vector3 Displacement,Vector3 Direction,float Speed) Evaluate(float age)
        {
            age=Math.Max(0,age);int type=(int?)motion?["type"]??0;
            bool controlled=type is 60 or 64 or 65;
            float Speed(float t)=>controlled?FxrPreviewValue.Get(motion?["speed"],t,seed)*FxrPreviewValue.Get(motion?["speedMultiplier"],t,seed,1)
                :initialSpeed+FxrParticleSurface.Integrate(motion?["acceleration"],motion?["accelerationMultiplier"],t,seed);
            float Distance(float t)=>controlled?FxrParticleSurface.Integrate(motion?["speed"],motion?["speedMultiplier"],t,seed)
                :initialSpeed*t+FxrParticleSurface.Integrate(motion?["acceleration"],motion?["accelerationMultiplier"],t,seed,displacement:true);
            if(type is not (64 or 65 or 84 or 105)||motion?["maxTurnAngle"]==null||motion["maxTurnAngle"] is JValue value && value.Value<float>()==0)
                return (initialDirection*Distance(age),initialDirection,Speed(age));
            float interval=FxrPreviewValue.Get(motion["turnInterval"],0,seed);
            if(interval<=0)return (initialDirection*Distance(age),initialDirection,Speed(age));
            interval=Math.Max(1f/240,interval);
            int turns=Math.Max(0,(int)Math.Min(1024,Math.Floor(age/interval)));Limited=age/interval>1024;
            // Force and trail sampling revisit the same completed turns hundreds
            // of times. Retain their exact prefix within this particle's Build;
            // rewind reads an earlier prefix without changing RNG or integration.
            turnHistory??=new(){(Vector3.Zero,initialDirection,0)};
            while(turnHistory.Count<=turns)
            {
                int n=turnHistory.Count;float t=n*interval,d=Distance(t);var prior=turnHistory[^1];
                var p=prior.Position+prior.Direction*(d-prior.Distance);
                var direction=FxrEmissionGeometry.Turn(prior.Direction,FxrPreviewValue.Get(motion["maxTurnAngle"],t,seed),unchecked(seed+n*7919));
                turnHistory.Add((p,direction,d));TurnEvaluations++;
            }
            var state=turnHistory[turns];var position=state.Position;
            if(!Limited)position+=state.Direction*(Distance(age)-state.Distance);
            return (position,state.Direction,Speed(Limited?turns*interval:age));
        }
    }
}
