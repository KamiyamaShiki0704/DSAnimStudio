using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // A deterministic, explicitly partial FXR interpreter. Profiles are decoded by
    // @cccode/fxr and bound to the exact source hash by FxrPreviewRenderer.
    public static class FxrPreviewValue
    {
        public static float Random(int seed)
        {
            uint v = unchecked((uint)seed); v ^= v >> 16; v *= 0x7feb352d; v ^= v >> 15; v *= 0x846ca68b; v ^= v >> 16;
            return (v >> 8) / 16777216f;
        }
        internal static float Component(JToken token, int index, float fallback = 0)
            => token is JArray a ? index >= 0 && index < a.Count ? (float?)a[index] ?? fallback : fallback : (float?)token ?? fallback;
        public static float Get(JToken value, float time, int seed, float fallback = 0, int component = 0)
        {
            if (value == null || value.Type == JTokenType.Null) return fallback;
            if (value is not JObject obj) return Component(value, component, fallback);
            float result = fallback;
            string function = (string)obj["function"];
            var cache=function!=null&&obj["modifiers"]==null?FxrCurveCache.Current:null;
            if(cache!=null&&cache.TryGet(obj,time,component,fallback,out var cached))return cached;
            if (function != null)
            {
                if(function is not ("Linear" or "Stepped" or "Bezier" or "Hermite" or "ComponentHermite"))return fallback;
                if (FxrCurveCache.Current?.TryEvaluate(obj, function, time, component, out result) != true)
                {
                var components = obj["components"] as JArray;
                var keys = function == "ComponentHermite" ? components != null && component >= 0 && component < components.Count ? components[component] as JArray : null : obj["keyframes"] as JArray;
                if (keys == null || keys.Count == 0) return fallback;
                float end = (float)keys.Last["position"];
                float t = (bool?)obj["loop"] == true && end > 0 ? Math.Max(0, time) % end : time;
                int next = 0; while (next < keys.Count && (float)keys[next]["position"] < t) next++;
                int channel = function == "ComponentHermite" ? 0 : component;
                if (next == 0) result = Component(keys[0]["value"], channel);
                else if (next == keys.Count) result = Component(keys.Last["value"], channel);
                else
                {
                    var a = keys[next - 1]; var b = keys[next];
                    float span = (float)b["position"] - (float)a["position"], x = span > 0 ? (t - (float)a["position"]) / span : 0;
                    float av = Component(a["value"], channel), bv = Component(b["value"], channel);
                    if(function == "Stepped")result=x>=1?bv:av;
                    else if(function=="Bezier")
                    {
                        // FXR p1/p2 are value-space tangent deltas, not handle times.
                        float c1=av+Component(a["p1"],channel)/3,c2=bv-Component(b["p2"],channel)/3,k=1-x;
                        result=k*k*k*av+3*k*k*x*c1+3*k*x*x*c2+x*x*x*bv;
                    }
                    else if (function is "ComponentHermite" or "Hermite")
                    {
                        result = av==bv?av:MathHelper.Lerp(av,bv,HermiteAmount(Component(a["t1"],channel),Component(a["t2"],channel),x));
                    }
                    else result = MathHelper.Lerp(av, bv, x);
                }
                }
            }
            else result = Get(obj["value"], time, seed, fallback, component);
            if(obj["modifiers"] is JArray modifiers)foreach (var mod in modifiers)
            {
                float r = Random(unchecked(seed + (int)Component(mod["seed"], component)));
                if ((string)mod["type"] == "RandomRange") result += MathHelper.Lerp(Component(mod["min"], component), Component(mod["max"], component), r);
                else if ((string)mod["type"] == "RandomDelta") result += (r*2-1)*Component(mod["max"],component);
                else if ((string)mod["type"] == "RandomFraction") result *= 1 - r * Component(mod["max"], component);
                // External-value modifiers require the native controller and are not claimed here.
            }
            result=float.IsFinite(result)?result:fallback;
            cache?.Add(obj,time,component,fallback,result);
            return result;
        }
        // Same Curve2 approximation as the bundled @cccode/fxr 32.1.0.
        // Both angles belong to the starting key; they describe normalized
        // easing, not tan(angle) derivatives in absolute value/time units.
        static readonly Vector4[] HermiteCurves = {
            new(.3f,.1f,.7f,.9f),new(.135f,.135f,.525f,1),new(.015f,.675f,.33f,1),
            new(.475f,0,.865f,.865f),new(0,0,1,1),new(.015f,.9f,.5f,.5f),
            new(.71f,0,.985f,.37f),new(.525f,.525f,.965f,.07f),new(.065f,1.4f,.935f,-.4f)
        };
        internal static float HermiteAmount(float first,float second,float x)
        {
            if(x<=0||x>=1)return Math.Clamp(x,0,1);
            double Curve(int index)
            {
                if(index==4)return x;
                var c=HermiteCurves[index];
                double Bezier(double a,double b,double t)=>t*((t+3*(a-b)*t+(3*b-6*a))*t+3*a);
                double lo=0,hi=1,t=x;
                for(int i=0;i<24;i++)
                {
                    double error=Bezier(c.X,c.Z,t)-x;
                    if(Math.Abs(error)<1e-8)break;
                    if(error>0)hi=t;else lo=t;
                    double slope=3*(t*(t+3*(c.X-c.Z)*t+2*(c.Z-2*c.X))+c.X);
                    double next=Math.Abs(slope)>1e-8?t-error/slope:double.NaN;
                    t=next>lo&&next<hi?next:(lo+hi)*.5;
                }
                return Bezier(c.Y,c.W,t);
            }
            double a=first*4/Math.PI,b=second*4/Math.PI;
            int ix=(int)Math.Clamp(Math.Floor(a),0,1),iy=(int)Math.Clamp(Math.Floor(b),0,1),index=ix+3*iy;
            a-=ix;b-=iy;
            double top=Curve(index),bottom=Curve(index+3);
            top+=(Curve(index+1)-top)*a;bottom+=(Curve(index+4)-bottom)*a;
            return (float)(top+(bottom-top)*b);
        }
        public static Vector3 Vec(JToken value, float time, int seed, float fallback = 0)
            => new(Get(value,time,seed,fallback,0),Get(value,time,seed,fallback,1),Get(value,time,seed,fallback,2));
    }

    public sealed record FxrTracerSpline(Vector4 Start,Vector4 End,Vector4 StartTangent,Vector4 EndTangent,Vector3 Direction,Vector3 NextDirection)
    {
        public void Edges(float t,out Vector3 left,out Vector3 right)
        {
            var value=Vector4.Hermite(Start,StartTangent,End,EndTangent,t);
            var center=new Vector3(value.X,value.Y,value.Z);
            var side=Vector3.Lerp(Direction,NextDirection,t)*(value.W*.5f);
            left=center-side;right=center+side;
        }
    }
    public sealed record FxrRibbonTangents(Vector3 Left,Vector3 Right,Vector3 NextLeft,Vector3 NextRight,FxrTracerSpline Native=null);
    public sealed record FxrRibbonGeometry(Vector3 Left,Vector3 Right,Vector3 NextRight,Vector3 NextLeft,
        float U,float NextU,float V,float Alpha,float NextAlpha,FxrRibbonTangents Curve=null,int Pieces=1)
    {
        public int VertexCount=>checked(6*(Curve==null?1:Math.Max(1,Pieces)));
        void Edges(float t,out Vector3 left,out Vector3 right)
        {
            if(t<=0){left=Left;right=Right;}
            else if(t>=1){left=NextLeft;right=NextRight;}
            else if(Curve?.Native!=null)Curve.Native.Edges(t,out left,out right);
            else if(Curve!=null){left=Vector3.Hermite(Left,Curve.Left,NextLeft,Curve.NextLeft,t);right=Vector3.Hermite(Right,Curve.Right,NextRight,Curve.NextRight,t);}
            else{left=Vector3.Lerp(Left,NextLeft,t);right=Vector3.Lerp(Right,NextRight,t);}
        }
        public int WriteVertices(Span<Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture> vertices)
        {
            int count=VertexCount;
            if(vertices.Length<count)throw new ArgumentException("Ribbon output span is too short.",nameof(vertices));
            int pieces=count/6,at=0;var left=Left;var right=Right;
            for(int i=1;i<=pieces;i++)
            {
                float t0=(i-1f)/pieces,t1=i/(float)pieces;
                Edges(t1,out var nl,out var nr);
                float u=pieces==1?U:MathHelper.Lerp(U,NextU,t0),nextU=pieces==1?NextU:MathHelper.Lerp(U,NextU,t1);
                float alpha=pieces==1?Alpha:MathHelper.Lerp(Alpha,NextAlpha,t0),nextAlpha=pieces==1?NextAlpha:MathHelper.Lerp(Alpha,NextAlpha,t1);
                var a=new Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture(left,new Color(1f,1f,1f,alpha),new(u,V+1));
                var b=new Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture(right,new Color(1f,1f,1f,alpha),new(u,V));
                var c=new Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture(nr,new Color(1f,1f,1f,nextAlpha),new(nextU,V));
                var d=new Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture(nl,new Color(1f,1f,1f,nextAlpha),new(nextU,V+1));
                vertices[at++]=a;vertices[at++]=b;vertices[at++]=c;vertices[at++]=a;vertices[at++]=c;vertices[at++]=d;
                left=nl;right=nr;
            }
            return count;
        }
        // Keep one draw item per authored segment. Tessellation must not consume
        // extra effect-instance budget or issue a draw call for every small quad.
        public IEnumerable<FxrRibbonGeometry> Segments()
        {
            if(Curve==null||Pieces<=1){yield return this;yield break;}
            var left=Left;var right=Right;
            for(int i=1;i<=Pieces;i++)
            {
                float t0=(i-1f)/Pieces,t1=i/(float)Pieces;Edges(t1,out var nl,out var nr);
                yield return new(left,right,nr,nl,MathHelper.Lerp(U,NextU,t0),MathHelper.Lerp(U,NextU,t1),V,
                    MathHelper.Lerp(Alpha,NextAlpha,t0),MathHelper.Lerp(Alpha,NextAlpha,t1));
                left=nl;right=nr;
            }
        }
    }

    public sealed record FxrPreviewItem(int Texture, int Model, Matrix Transform, Vector4 Color, int Blend,
        Vector3? RibbonEnd = null, float RibbonWidth = 0, float U = 0, FxrAtlasFrame Atlas=default,Vector4 UvTransform=default,
        float AlphaFade=0,float AlphaCutoff=0,bool Premultiply=false,FxrRibbonGeometry Ribbon=null,int ResourceScope=0,
        FxrLayerMaterial Layers=null,FxrLineGeometry Line=null,bool Octagonal=false,FxrLineSegment Segment=null,bool Dither=false,FxrScenePass ScenePass=null,
        int AnimationBinder=0,int AnimationClip=0,float AnimationTime=0,bool AnimationLoop=true,float SoftDepthRadius=0,float DepthOffset=0,FxrGpuTraceGeometry GpuTrace=null);
    public sealed record FxrLineSegment(Vector3 Start,Vector3 End,Vector4 StartColor,Vector4 EndColor);

    public sealed class FxrPreviewScene
    {
        public readonly List<FxrPreviewItem> Items = new();
        public readonly HashSet<int> OmittedAppearances = new();
        public readonly HashSet<string> Notices = new();
        public bool Limited;
        const int Budget = 2048;
        int visited;
        float now;
        Vector3 cameraRight, cameraUp, cameraPosition;
        Matrix tracerProjection;
        Func<float,Matrix> effectAnchorAt;
        FxrPreviewTimeline effectTimeline;
        static int Type(JToken t) => (int?)(t as JObject)?["type"] ?? 0;
        static float V(JToken o, string key, float t, int seed, float fallback = 0) => FxrPreviewValue.Get((o as JObject)?[key],t,seed,fallback);
        static Matrix Rotation(Vector3 degrees) => FxrTransformMotion.Rotation(degrees);
        static Matrix Facing(Vector3 p, Vector3 direction) => Matrix.CreateWorld(p, -BulletMath.Unit(direction,Vector3.Forward),
            Math.Abs(Vector3.Dot(direction,Vector3.Up))>.99f ? Vector3.Right : Vector3.Up);

        static Vector3 SpreadDirection(Vector3 axis,JToken spread,float emissionTime,int seed)
        {
            float angle=MathHelper.ToRadians(Math.Clamp(V(spread,"angle",emissionTime,seed),0,180));
            if(angle<=0)return axis;
            float fraction=FxrPreviewValue.Random(seed+22),distribution=Math.Clamp(V(spread,"distribution",emissionTime,seed),-1,1);
            fraction=distribution>=0?fraction*(1-distribution):1-(1-fraction)*(1+distribution);
            float cos=MathHelper.Lerp(1,MathF.Cos(angle),fraction),sin=MathF.Sqrt(Math.Max(0,1-cos*cos));
            float azimuth=FxrPreviewValue.Random(seed+23)*MathHelper.TwoPi;
            var right=Vector3.Normalize(Vector3.Cross(Math.Abs(axis.Y)>.99f?Vector3.Right:Vector3.Up,axis));
            var up=Vector3.Cross(axis,right);
            return axis*cos+(right*MathF.Cos(azimuth)+up*MathF.Sin(azimuth))*sin;
        }

        Matrix AttachmentAt(JToken attributes,Func<float,Matrix> parentAt,float birth,float time,Func<float,Matrix> originalAnchor)
        {
            int mode=(int?)(attributes as JObject)?["attachment"]??1;
            // Modes 2/4 bypass transformed ancestors and use the effect's
            // original attachment point. Modes 3/4 discard rotation entirely.
            var matrix=mode switch {0=>parentAt(birth),2 or 4=>originalAnchor(time),_=>parentAt(time)};
            return mode is 3 or 4?Matrix.CreateTranslation(matrix.Translation):matrix;
        }

        public static Vector3 Sample(BulletParticle p, float age)
        {
            int count = Math.Min(p.Trail.Count,p.TrailAges.Count);
            if (count == 0) return p.Position;
            if (age <= p.TrailAges[0]) return p.Trail[0];
            for (int i=1;i<count;i++)
                if (age <= p.TrailAges[i]) return Vector3.Lerp(p.Trail[i-1],p.Trail[i],
                    Math.Clamp((age-p.TrailAges[i-1])/Math.Max(1e-6f,p.TrailAges[i]-p.TrailAges[i-1]),0,1));
            return Vector3.Lerp(p.Trail[count-1],p.Position,Math.Clamp((age-p.TrailAges[count-1])/Math.Max(1e-6f,p.Age-p.TrailAges[count-1]),0,1));
        }
        public void Build(JObject root, BulletParticle bullet, double worldTime, bool hit, Matrix camera)
        {
            float age = (float)(worldTime-(hit?bullet.DeathTime:bullet.BirthTime));
            if(age<0 || hit && (bullet.Alive || bullet.EndReason is not ("Unit hit" or "Ground hit"))) {Items.Clear();return;}
            float end = hit ? 3 : bullet.Alive ? float.PositiveInfinity : bullet.Age;
            Matrix Anchor(float t)
            {
                if(hit) return Facing(bullet.HitPosition,BulletMath.Unit(bullet.HitNormal,Vector3.Up));
                var pos=Sample(bullet,t);var before=Sample(bullet,Math.Max(0,t-.01f));
                return Facing(pos,BulletMath.Unit(pos-before,bullet.Direction));
            }
            Build(root,new FxrPlaybackInstance(hit?bullet.Definition.HitSfx:bullet.Definition.FlightSfx,bullet.Serial,age,end,Anchor,"Bullet"),camera);
        }
        bool collectingFields;
        float clockScale=1;
        FxrForceSet forceFields;
        FxrCollisionWorld collisionWorld;
        ulong sourceForceGroups=ulong.MaxValue,receiverForceGroups=ulong.MaxValue;
        public static bool HasForceVolumes(JObject root)=>root.Descendants().OfType<JObject>().Any(o=>(int?)(o["appearance"] as JObject)?["type"] is 10200 or 10300 or 10301 or 10303);
        public FxrForceSet CollectForces(JObject root,FxrPlaybackInstance instance,Matrix camera)
        {
            collectingFields=true;forceFields=new();
            try{Build(root,instance,camera,forceFields);return forceFields;}
            finally{collectingFields=false;}
        }
        public void Build(JObject root,FxrPlaybackInstance instance,Matrix camera,FxrForceSet fields=null,FxrCollisionWorld collisions=null,Matrix? projection=null)
        {
            Items.Clear();OmittedAppearances.Clear();Notices.Clear();Limited=false;visited=0;now=instance.Age;clockScale=1;
            if(now<0)return;
            forceFields=fields;
            collisionWorld=collisions;
            if(!collectingFields&&fields==null&&HasForceVolumes(root))forceFields=new FxrPreviewScene().CollectForces(root,instance,camera);
            cameraRight=camera.Right;cameraUp=camera.Up;cameraPosition=camera.Translation;
            tracerProjection=Matrix.Invert(camera)*(projection??Matrix.Identity);
            using var curveCache=new FxrCurveCache();
            using var poseStorage=new FxrFrameMemo<Matrix>.Scope();
            WalkEffect(root,instance.AnchorAt,0,instance.EmissionEnd,instance.Seed,0);
            Limited|=forceFields?.Limited==true;
        }
        void WalkEffect(JObject root,Func<float,Matrix> anchor,float birth,float end,int seed,int depth)
        {
            float outerNow=now,outerScale=clockScale;var outerAnchor=effectAnchorAt;var outerTimeline=effectTimeline;
            ulong outerSourceGroups=sourceForceGroups,outerReceiverGroups=receiverForceGroups;
            float rate=V(root["unk10500"],"rateOfTime",0,seed,1),prewarm=V(root["unk10500"],"initialSimulationTime",0,seed);
            if(!float.IsFinite(rate)||rate<=0){Notices.Add("FXR nonpositive time rate is not supported.");return;}
            if(rate<.001f||rate>1000||prewarm<0||prewarm>60){Limited=true;Notices.Add("FXR clock preview limit reached (rate 0.001-1000 / prewarm 0-60 s).");}
            rate=Math.Clamp(rate,.001f,1000);prewarm=Math.Clamp(prewarm,0,60);
            try
            {
                sourceForceGroups=FxrForceGroups.Source(root["unk10100"] as JObject).Groups;
                receiverForceGroups=FxrForceGroups.Receiver(root["unk10400"] as JObject);
                now=(outerNow-birth)*rate+prewarm;clockScale=outerScale*rate;
                float stop=(end-birth)*rate+prewarm;
                // Referenced effects use their own local clock but their original
                // attachment is the point at which the parent emitted the proxy.
                effectAnchorAt=t=>anchor(birth+Math.Max(0,(t-prewarm)/rate));
                effectTimeline=FxrPreviewTimeline.Build(root["_previewStates"] as JArray,now,stop);
                Limited|=effectTimeline.Limited;Notices.UnionWith(effectTimeline.Notices);
                stop=Math.Min(stop,effectTimeline.Termination);
                int termination=(int?)root["termination"]?["type"]??700;
                float fade=1;
                if(!collectingFields&&now>=stop && termination==702)return;
                if(!collectingFields&&now>=stop && termination==701)
                {
                    float duration=V(root["termination"],"duration",0,seed,1);
                    if(duration<=0||now>=stop+duration)return;
                    fade=1-(now-stop)/duration;stop=float.PositiveInfinity;
                    if(effectTimeline.Phases.Count>0)effectTimeline.Phases[^1]=effectTimeline.Phases[^1] with{End=stop};
                }
                int firstItem=Items.Count;
                foreach(var child in root["nodes"]??new JArray())Walk(child,effectAnchorAt,0,stop,seed,depth);
                if(fade<1)for(int i=firstItem;i<Items.Count;i++){var color=Items[i].Color;color.W*=fade;Items[i]=Items[i] with{Color=color};}
            }
            finally{now=outerNow;clockScale=outerScale;effectAnchorAt=outerAnchor;effectTimeline=outerTimeline;sourceForceGroups=outerSourceGroups;receiverForceGroups=outerReceiverGroups;}
        }
        void Walk(JToken node, Func<float,Matrix> parentAt, float parentBirth, float parentEnd, int seed, int depth,int configIndex=-2)
        {
            if(node["_previewReference"] is JObject referencedRoot)
            {
                if(depth>16||visited++>Budget){Limited=true;return;}
                WalkEffect(referencedRoot,parentAt,parentBirth,parentEnd,unchecked(seed*397+FxrPreviewLibrary.ReferenceId((JObject)node)),depth+1);return;
            }
            if(configIndex==-2 && node["configs"] is JArray)
            {
                foreach(var activation in effectTimeline.Activations(node["stateConfigMap"] as JArray,parentBirth,parentEnd))
                    Walk(node,parentAt,activation.Start,activation.End,seed,depth,activation.Config);
                return;
            }
            if(depth>16 || visited++>Budget || Items.Count>=Budget) { Limited=true; return; }
            if(configIndex==-2)configIndex=0;
            if(configIndex<0)return;
            var cfg=node["configs"]?.ElementAtOrDefault(configIndex);
            if(cfg==null) { foreach(var ch in node["nodes"] ?? new JArray()) Walk(ch,parentAt,parentBirth,parentEnd,seed+71,depth+1);return; }
            if((int?)node["type"]==2002)
            {
                float lodDuration=V(cfg,"duration",0,seed,-1),stop=lodDuration<0?parentEnd:Math.Min(parentEnd,parentBirth+lodDuration);
                if(now<parentBirth||now>=stop)return;
                float distance=Vector3.Distance(cameraPosition,parentAt(now).Translation);
                var thresholds=cfg["thresholds"] as JArray;var children=node["nodes"] as JArray;
                if(thresholds==null||children==null)return;
                for(int i=0;i<Math.Min(5,Math.Min(thresholds.Count,children.Count));i++)
                    if(distance<FxrPreviewValue.Get(thresholds[i],0,seed,10000))
                    {Walk(children[i],parentAt,parentBirth,stop,unchecked(seed+(i+1)*7919),depth+1);break;}
                return;
            }
            float begin=parentBirth+V(cfg["nodeAttributes"],"delay",0,seed);
            if(now<begin)return;
            float duration=V(cfg["nodeAttributes"],"duration",0,seed,-1);
            float end=Math.Min(parentEnd,duration<0?float.PositiveInfinity:begin+duration);
            if(begin>=end)return;
            var transform=cfg["nodeTransform"] as JObject; var movement=cfg["nodeMovement"] as JObject;
            var originalAnchor=effectAnchorAt;
            FxrPartialFollow partialFollow=null;
            if((int?)(cfg["nodeAttributes"] as JObject)?["attachment"]==0 && Type(movement) is 106 or 122)
                partialFollow=new(parentAt,begin,a=>V(movement,"followFactor",a,seed),(bool?)movement["followRotation"]!=false);
            var ov=FxrPreviewValue.Vec(transform?["offsetVariance"],0,seed);var rv=FxrPreviewValue.Vec(transform?["rotationVariance"],0,seed);
            var random=new Vector3(FxrPreviewValue.Random(seed+11)*2-1,FxrPreviewValue.Random(seed+12)*2-1,FxrPreviewValue.Random(seed+13)*2-1);
            var offsetVariance=ov*random;var rotationVariance=rv*random;
            Matrix NodeBaseAt(float time)
            {
                float age=Math.Max(0,time-begin);
                var offset=FxrPreviewValue.Vec(transform?["offset"],age,seed);
                var rot=FxrPreviewValue.Vec(transform?["rotation"],age,seed);
                offset+=offsetVariance;rot+=rotationVariance;
                if(Type(movement)==15) offset+=FxrPreviewValue.Vec(movement["translation"],age,seed);
                var rotation=Rotation(rot);
                if(Type(movement) is 34 or 113 or 123)rotation=FxrTransformMotion.Spin(movement,age,seed,rotation);
                offset+=Vector3.TransformNormal(FxrTransformMotion.Translation(movement,age,seed),rotation);
                var parent=AttachmentAt(cfg["nodeAttributes"],parentAt,begin,time,originalAnchor);
                if(Type(movement)==46)
                {
                    parent=Matrix.CreateTranslation(cameraPosition);
                    if((bool?)movement["followRotation"]!=false){parent.Right=cameraRight;parent.Up=cameraUp;parent.Backward=Vector3.Cross(cameraRight,cameraUp);}
                }
                if(partialFollow!=null){parent=partialFollow.At(time);Limited|=partialFollow.Limited;}
                return rotation*Matrix.CreateTranslation(offset)*parent;
            }
            FxrForceMotion nodeForce=null;
            if(!collectingFields&&forceFields?.Count>0&&Type(cfg["nodeForceMovement"]) is 731 or 733)
                nodeForce=new(NodeBaseAt,forceFields,(JObject)cfg["nodeForceMovement"],begin,begin,now,clockScale,seed,
                    Type(cfg["appearance"])==10302?(JObject)cfg["appearance"]:null,receiverForceGroups);
            var nodePoses=new FxrFrameMemo<Matrix>(time=>
            {var pose=nodeForce?.At(time)??NodeBaseAt(time);Limited|=nodeForce?.Limited==true;return pose;});
            Matrix NodeAt(float time)=>nodePoses.At(time);
            // Basic nodes own persistent child nodes. Only node emitters (2202)
            // spawn a new child tree on every emission; repeating a proxy for
            // every ordinary particle duplicates whole referenced effects.
            bool ownsChildren=(int?)node["type"]==2200;
            if(ownsChildren)
            {
                int index=0;
                foreach(var child in node["nodes"]??new JArray())Walk(child,NodeAt,begin,end,unchecked(seed+ ++index*7919),depth+1);
            }
            if(Type(cfg["appearance"])==10302)
            {
                Notices.Add("10302 is a force receiver volume, not a solid particle collision surface.");
                return;
            }
            if(cfg["appearance"] is JObject special&&Type(special) is 609 or 11000 or 10014 or 10003)
            {
                if(!collectingFields&&now<end)
                {
                    var camera=Matrix.Identity;camera.Right=cameraRight;camera.Up=cameraUp;camera.Backward=Vector3.Cross(cameraRight,cameraUp);camera.Translation=cameraPosition;
                    FxrSpecialAppearance.Emit(special,now-begin,seed,NodeAt(now),camera,(int?)special["_previewScope"]??0,Items,message=>Notices.Add(message));
                }
                return;
            }
            if(cfg["appearance"] is JObject field&&Type(field) is 10200 or 10300 or 10301 or 10303)
            {
                if(collectingFields)forceFields.Add(new(field,NodeAt,begin,end,now,clockScale,seed,sourceForceGroups));
                Notices.Add($"Force volume {Type(field)}: sphere/box/cylinder/prism preview; native axes, noise/random modulation and moving-force feedback remain approximate.");
                return;
            }
            if(cfg["appearance"] is JObject gpu&&FxrGpuParticlePreview.Supports(Type(gpu)))
            {
                if(collectingFields)return;
                var camera=Matrix.Identity;camera.Right=cameraRight;camera.Up=cameraUp;camera.Backward=Vector3.Cross(cameraRight,cameraUp);camera.Translation=cameraPosition;
                Limited|=FxrGpuParticlePreview.Build(gpu,NodeAt,begin,end,now,seed,camera,Items,Budget,Notices,
                    forceFields,cfg["particleForceMovement"] as JObject,clockScale,collisionWorld,receiverForceGroups);
                return;
            }
            var emitter=cfg["emitter"] as JObject;int et=Type(emitter);
            if(et is not (0 or 300 or 301 or 399))return;
            float interval=et==399?0:V(emitter,"interval",0,seed);
            var births=new List<float>();
            if(et==301)
            {
                births.Add(begin);Vector3 origin=NodeAt(begin).Translation,last=origin;
                float previous=begin,until=Math.Min(now,end);
                for(int tick=1;previous<until&&births.Count<512;tick++)
                {
                    if(tick>4096){Limited=true;break;}
                    float t=Math.Min(until,begin+tick/60f),threshold=Math.Max(.01f,V(emitter,"threshold",t-begin,seed));
                    var pos=NodeAt(t).Translation;float cursor=0;
                    while(Vector3.Distance(origin,pos)>=threshold&&births.Count<512)
                    {
                        var delta=pos-last;var from=last-origin;
                        float a=delta.LengthSquared(),b=2*Vector3.Dot(from,delta),c=from.LengthSquared()-threshold*threshold;
                        if(a<1e-12f)break;
                        float discriminant=b*b-4*a*c;if(discriminant<0)break;
                        float fraction=(-b+MathF.Sqrt(discriminant))/(2*a);
                        if(fraction<cursor-1e-6f||fraction>1+1e-6f)break;
                        fraction=Math.Clamp(fraction,cursor,1);
                        float birthTime=MathHelper.Lerp(previous,t,fraction);
                        if(birthTime>=end)break;
                        births.Add(birthTime);origin=Vector3.Lerp(last,pos,fraction);cursor=fraction;
                    }
                    last=pos;previous=t;
                }
                if(births.Count>=512)Limited=true;
            }
            else if(et is 0 or 399) births.Add(begin);
            else
            {
                // A zero periodic interval emits each simulation step. It is
                // not OneTimeEmitter (399); use the preview's 60 Hz cadence.
                float t=begin;
                while(t<=now+1e-6f&&t<end&&births.Count<512)
                {
                    births.Add(t);interval=V(emitter,"interval",t-begin,seed);
                    t+=interval<=0?1f/60:Math.Max(interval,1f/240);
                }
                if(t<=Math.Min(now,end))Limited=true;
            }
            int maxTotal=(int)V(emitter,"totalEmissions",0,seed,-1);if(maxTotal>=0&&births.Count>maxTotal) births.RemoveRange(maxTotal,births.Count-maxTotal);
            int concurrent=(int)V(emitter,"maxConcurrent",0,seed,-1);
            int aliveCount=0;
            // Every lookup below depends only on the node configuration, never on
            // the individual particle, so it is resolved once per emitter. It used
            // to be repeated for every emitted particle, which made the per-particle
            // cost on high-count emitters dominated by repeated JSON indexing and by
            // re-enumerating the child array for each particle.
            var attributes=cfg["particleAttributes"] as JObject;
            var app=cfg["appearance"] as JObject;int type=Type(app);
            bool tracer=type is 606 or 10012;
            var modifier=cfg["particleModifier"] as JObject;var shape=cfg["emitterShape"] as JObject;
            int shapeType=Type(shape);
            var particleMotion=cfg["particleMovement"] as JObject;
            bool hasChildren=node["nodes"]?.Any()??false;
            bool emitsNodes=(int?)node["type"]==2202;
            for(int b=births.Count-1;b>=0;b--)
            {
            float emissionTime=births[b]-begin;
            int per=et==399?1:Math.Clamp((int)V(emitter,"perEmission",emissionTime,seed,1),0,64);
            for(int j=0;j<per;j++)
            {
                int ps=unchecked(seed*397+b*67+j*101); float birth=births[b],age=now-birth;
                float life=V(attributes,"duration",0,ps,-1);
                float death=life<0?end:birth+life;
                float fadeTime=tracer?Math.Max(0,V(app,"fadeOutTime",age,ps)):0;
                bool visible=now<death || tracer && now<death+fadeTime;
                if(age<0 || !visible && !hasChildren)continue;
                if(concurrent>0 && aliveCount++>=concurrent)continue;
                // FXR LocalNorth is +Z; MonoGame.Forward is -Z.
                var direction=Vector3.Backward;
                int dir=(int)V(shape,"direction",0,ps);if(dir==1||dir==4)direction=Vector3.Up;else if(dir==2||dir==5)direction=Vector3.Down;
                bool globalDirection=dir is 1 or 2 or 3;
                Vector3 offset=Vector3.Zero;
                if(shapeType==404)
                {
                    var size=new Vector3(V(shape,"sizeX",emissionTime,ps,1),V(shape,"sizeY",emissionTime,ps,1),V(shape,"sizeZ",emissionTime,ps,1));
                    offset=new Vector3(FxrPreviewValue.Random(ps+31)-.5f,FxrPreviewValue.Random(ps+32)-.5f,FxrPreviewValue.Random(ps+33)-.5f)*size;
                    if((bool?)shape["emitInside"]==false)
                    {
                        float xy=Math.Abs(size.X*size.Y),xz=Math.Abs(size.X*size.Z),yz=Math.Abs(size.Y*size.Z);
                        float face=FxrPreviewValue.Random(ps+34)*(xy+xz+yz),sign=FxrPreviewValue.Random(ps+35)<.5f?-1:1;
                        if(face<yz){offset.X=size.X*.5f*sign;if(dir==0)direction=Vector3.Right*sign;}
                        else if(face<yz+xz){offset.Y=size.Y*.5f*sign;if(dir==0)direction=Vector3.Up*sign;}
                        else{offset.Z=size.Z*.5f*sign;if(dir==0)direction=Vector3.Backward*sign;}
                    }
                }
                if(shapeType is 401 or 402)offset=FxrEmissionGeometry.Planar(shape,emissionTime,ps,shapeType==402);
                if(shapeType==403)
                {
                    float z=FxrPreviewValue.Random(ps+31)*2-1, angle=FxrPreviewValue.Random(ps+32)*MathHelper.TwoPi;
                    float planar=MathF.Sqrt(Math.Max(0,1-z*z)), radius=V(shape,"radius",0,ps);
                    if((bool?)shape["emitInside"]!=false)radius*=MathF.Cbrt(FxrPreviewValue.Random(ps+33));
                    offset=new Vector3(planar*MathF.Cos(angle),planar*MathF.Sin(angle),z)*radius;
                }
                if(shapeType==405)
                {
                    float angle=FxrPreviewValue.Random(ps+31)*MathHelper.TwoPi;
                    float radius=Math.Max(0,V(shape,"radius",emissionTime,ps,1));
                    if((bool?)shape["emitInside"]!=false)radius*=MathF.Sqrt(FxrPreviewValue.Random(ps+32));
                    float axial=(FxrPreviewValue.Random(ps+33)-.5f)*Math.Max(0,V(shape,"height",emissionTime,ps,1));
                    bool yAxis=(bool?)shape["yAxis"]==true;
                    var radial=yAxis?new Vector3(MathF.Cos(angle),0,MathF.Sin(angle)):new Vector3(MathF.Cos(angle),MathF.Sin(angle),0);
                    offset=radial*radius+(yAxis?Vector3.Up:Vector3.Backward)*axial;
                    if(dir==0)direction=radial;
                }
                direction=FxrEmissionGeometry.Spread(direction,cfg["directionSpread"],emissionTime,ps);
                float speed=V(modifier,"speed",emissionTime,ps);
                // A node emitter emits transforms, not just particle positions.
                // Its shape/spread direction must become the child branch's +Z
                // axis even when it has no particle speed (e.g. inward FXR paths).
                var emissionRotation=Matrix.Identity;
                if(emitsNodes)
                {
                    emissionRotation=Facing(Vector3.Zero,direction);
                    if(globalDirection)
                    {
                        var birthBasis=NodeAt(birth);birthBasis.Translation=Vector3.Zero;
                        emissionRotation*=Matrix.Invert(birthBasis);
                    }
                }
                var trajectory=new FxrParticleMotion(particleMotion,ps,speed,direction);
                FxrPartialFollow particleFollow=null;
                if((int?)attributes?["attachment"]==0 && Type(particleMotion) is 65 or 105)
                    particleFollow=new(NodeAt,birth,a=>V(particleMotion,"followFactor",a,ps),(bool?)particleMotion["followRotation"]!=false);
                Matrix ParticleBaseAt(float t)
                {
                    float a=Math.Max(0,t-birth);var basis=AttachmentAt(attributes,NodeAt,birth,t,originalAnchor);
                    if(particleFollow!=null){basis=particleFollow.At(t);Limited|=particleFollow.Limited;}
                    var displacement=trajectory.At(a).Displacement;Limited|=trajectory.Limited;
                    var position=Vector3.Transform(offset,basis)+(globalDirection?displacement:Vector3.TransformNormal(displacement,basis));
                    position.Y-=FxrParticleSurface.Integrate(particleMotion?["gravity"],null,a,ps,displacement:true);
                    if(emitsNodes)basis=emissionRotation*basis;
                    basis.Translation=position;return basis;
                }
                FxrForceMotion particleForce=null;
                if(!collectingFields&&forceFields?.Count>0&&Type(cfg["particleForceMovement"]) is 732 or 734)
                    particleForce=new(ParticleBaseAt,forceFields,(JObject)cfg["particleForceMovement"],birth,begin,now,clockScale,ps,receiverGroups:receiverForceGroups);
                FxrCollisionMotion particleCollision=null;
                Matrix ForceAt(float t)=>particleForce?.At(t)??ParticleBaseAt(t);
                if(!collectingFields&&Type(cfg["particleForceMovement"])==800)
                {
                    if(collisionWorld?.Count>0)particleCollision=new(ForceAt,collisionWorld,(JObject)cfg["particleForceMovement"],birth,ps);
                    else Notices.Add("800 particle collision requested, but no preview collision geometry is supplied.");
                }
                var particlePoses=new FxrFrameMemo<Matrix>(t=>
                {var pose=particleCollision?.At(t)??ForceAt(t);Limited|=particleForce?.Limited==true||particleCollision?.Limited==true;return pose;});
                Matrix ParticleAt(float t)=>particlePoses.At(t);
                if(Type(particleMotion) is 64 or 65 or 84 or 105)Notices.Add($"Particle movement {Type(particleMotion)}: speed / acceleration and partial follow supported; random turns and gravity-direction coupling remain approximate.");
                if(collectingFields||!visible) { /* Descendants can outlive the particle that emitted them. */ }
                else if(tracer)
                {
                    AddTracer(app,modifier,ParticleAt,birth,death,age,emissionTime,ps);
                }
                else if(type is 607 or 608)
                {
                    var camera=Matrix.Identity;camera.Right=cameraRight;camera.Up=cameraUp;camera.Backward=Vector3.Cross(cameraRight,cameraUp);camera.Translation=cameraPosition;
                    FxrSpecialAppearance.Emit(app,age,ps,ParticleAt(now),camera,(int?)app["_previewScope"]??0,Items,message=>Notices.Add(message));
                }
                else if(type==601)
                {
                    var head=ParticleAt(now);var before=ParticleAt(Math.Max(birth,now-1f/120));
                    var fallback=globalDirection?direction:Vector3.TransformNormal(direction,head);
                    var travel=BulletMath.Unit(head.Translation-before.Translation,BulletMath.Unit(fallback,Vector3.Backward));
                    float sx=V(modifier,"scaleX",emissionTime,ps,1),sz=(bool?)modifier?["uniformScale"]==true?sx:V(modifier,"scaleZ",emissionTime,ps,1);
                    float length=V(app,"length",emissionTime,ps,1)*V(app,"lengthMultiplier",age,ps,1)*sz;
                    if(Math.Abs(length)>1e-7f)Items.Add(new(-1,-1,head,SurfaceColor(app,modifier,age,emissionTime,now-begin,ps,true),(int)V(app,"blendMode",0,ps,2),
                        AlphaCutoff:Math.Clamp(V(app,"alphaThreshold",now-begin,ps)/255,0,1),ResourceScope:(int?)app["_previewScope"]??0,
                        Segment:new(head.Translation,head.Translation-travel*length,FxrParticleSurface.Vector(app["startColor"],now-begin,ps,true),FxrParticleSurface.Vector(app["endColor"],now-begin,ps,true))));
                }
                else if(type==602)
                {
                    var head=ParticleAt(now);var previous=ParticleAt(Math.Max(birth,now-1f/120));
                    var fallback=globalDirection?direction:Vector3.TransformNormal(direction,head);
                    var travel=BulletMath.Unit(head.Translation-previous.Translation,BulletMath.Unit(fallback,head.Backward));
                    float sx=V(modifier,"scaleX",emissionTime,ps,1),sz=(bool?)modifier?["uniformScale"]==true?sx:V(modifier,"scaleZ",emissionTime,ps,1);
                    float width=V(app,"width",emissionTime,ps,1)*V(app,"widthMultiplier",age,ps,1)*sx;
                    float length=V(app,"length",emissionTime,ps,1)*V(app,"lengthMultiplier",age,ps,1)*sz;
                    var side=BulletMath.Unit(Vector3.Cross(travel,cameraPosition-head.Translation),cameraRight)*width*.5f;
                    var tail=head.Translation-travel*length;
                    if(Math.Abs(width)>1e-7f&&Math.Abs(length)>1e-7f)Items.Add(new(-1,-1,head,SurfaceColor(app,modifier,age,emissionTime,now-begin,ps,true),(int)V(app,"blendMode",0,ps,2),
                        Line:new(head.Translation-side,head.Translation+side,tail+side,tail-side,FxrParticleSurface.Vector(app["startColor"],now-begin,ps,true),FxrParticleSurface.Vector(app["endColor"],now-begin,ps,true)),
                        AlphaCutoff:Math.Clamp(V(app,"alphaThreshold",now-begin,ps)/255,0,1),ResourceScope:(int?)app["_previewScope"]??0));
                }
                else if(type is 600 or 603 or 604 or 605 or 10015)
                {
                    float sx=V(modifier,"scaleX",emissionTime,ps,1),sy=(bool?)modifier?["uniformScale"]==true?sx:V(modifier,"scaleY",emissionTime,ps,1);
                    float sz=(bool?)modifier?["uniformScale"]==true?sx:V(modifier,"scaleZ",emissionTime,ps,1);
                    bool modelAppearance=type is 605 or 10015;
                    if(modelAppearance)
                    {
                        float Variation(string axis,int salt)=>MathHelper.Lerp(V(app,"scaleVariation"+axis,0,ps,1),1,FxrPreviewValue.Random(ps+salt));
                        float x=Variation("X",61);sx*=x;sy*=(bool?)app["uniformScale"]==true?x:Variation("Y",62);sz*=(bool?)app["uniformScale"]==true?x:Variation("Z",63);
                    }
                    float baseWidth=V(app,type==600?"size":modelAppearance?"sizeX":"width",age,ps,1);
                    float width=baseWidth*sx;
                    float height=((bool?)app["uniformScale"]==true?baseWidth:V(app,type==600?"size":modelAppearance?"sizeY":"height",age,ps,1))*sy;
                    var basis=ParticleAt(now);var pos=basis.Translation;
                    int orientation=(int?)app["orientation"]??1;
                    if(type is 603 or 604)
                    {
                        var state=trajectory.At(age);
                        var flight=globalDirection?state.Direction:Vector3.TransformNormal(state.Direction,basis);
                        var velocity=flight*state.Speed-Vector3.Up*FxrParticleSurface.Integrate(particleMotion?["gravity"],null,age,ps);
                        if(particleForce!=null||nodeForce!=null||particleCollision!=null)velocity=ParticleAt(now).Translation-ParticleAt(Math.Max(birth,now-1f/120)).Translation;
                        basis=FxrParticleSurface.Orient(orientation,basis,cameraPosition,cameraRight,cameraUp,BulletMath.Unit(velocity,flight));
                    }
                    else if(modelAppearance)
                    {
                        var state=trajectory.At(age);var flight=globalDirection?state.Direction:Vector3.TransformNormal(state.Direction,basis);
                        var velocity=flight*state.Speed-Vector3.Up*FxrParticleSurface.Integrate(particleMotion?["gravity"],null,age,ps);
                        if(particleForce!=null||nodeForce!=null||particleCollision!=null)velocity=ParticleAt(now).Translation-ParticleAt(Math.Max(birth,now-1f/120)).Translation;
                        basis=FxrParticleSurface.OrientModel(orientation,type==10015,basis,cameraPosition,cameraRight,cameraUp,BulletMath.Unit(velocity,flight));
                    }
                    else if(type==600) {basis=Matrix.Identity;basis.Right=cameraRight;basis.Up=cameraUp;basis.Backward=Vector3.Cross(cameraRight,cameraUp);basis.Translation=pos;}
                    if(type is 603 or 604 && (orientation==3 || orientation>7))Notices.Add($"Billboard orientation {orientation}: projection variant remains approximate.");
                    var rot=new Vector3(V(app,"rotationX",0,ps),V(app,"rotationY",0,ps),V(app,"rotationZ",0,ps));
                    rot+=new Vector3(FxrParticleSurface.Integrate(app["angularSpeedX"],app["angularSpeedMultiplierX"],age,ps),
                        FxrParticleSurface.Integrate(app["angularSpeedY"],app["angularSpeedMultiplierY"],age,ps),
                        FxrParticleSurface.Integrate(app["angularSpeedZ"],app["angularSpeedMultiplierZ"],age,ps));
                    var scale=Matrix.CreateScale(width,height,modelAppearance?((bool?)app["uniformScale"]==true?baseWidth:V(app,"sizeZ",age,ps,1))*sz:1);
                    var rotation=Rotation(rot);
                    var matrix=((bool?)app["scaleBeforeRotation"]!=false?scale*rotation:rotation*scale)
                        *Matrix.CreateTranslation(V(app,"offsetX",age,ps),V(app,"offsetY",age,ps),V(app,"offsetZ",age,ps))*basis;
                    var layers=type==604?FxrParticleSurface.Layers(app,age,ps):null;
                    var uvTransform=new Vector4(V(app,"offsetU",modelAppearance?0:age,ps)+FxrParticleSurface.Integrate(app["speedU"],app["speedMultiplierU"],age,ps),V(app,"offsetV",modelAppearance?0:age,ps)+FxrParticleSurface.Integrate(app["speedV"],app["speedMultiplierV"],age,ps),V(app,"scaleU",age,ps,1),V(app,"scaleV",age,ps,1));
                    if(type==10015)uvTransform=new(FxrPreviewValue.Get(app["offsetUV"],0,ps,component:0)+FxrParticleSurface.Integrate(app["speedUV"],app["speedMultiplierUV"],age,ps,component:0),FxrPreviewValue.Get(app["offsetUV"],0,ps,component:1)+FxrParticleSurface.Integrate(app["speedUV"],app["speedMultiplierUV"],age,ps,component:1),1,1);
                    if(type==605&&V(app,"totalFrames",0,ps,1)>1)uvTransform=new(0,0,1,1);
                    Items.Add(new(layers?.First.Texture??(int)V(app,"texture",age,ps,-1),(int)V(app,"model",modelAppearance?0:age,ps,-1),matrix,type is 603 or 604 or 605 or 10015?SurfaceColor(app,modifier,age,emissionTime,now-begin,ps):Color(app,modifier,age,ps),type==10015?4:(int)V(app,"blendMode",0,ps,4),Atlas:FxrTextureAnimation.Evaluate(app,age,ps),
                        UvTransform:uvTransform,
                        AlphaFade:Math.Clamp(V(app,"alphaFadeThreshold",age,ps)/255,0,1),AlphaCutoff:Math.Clamp(V(app,"alphaThreshold",now-begin,ps)/255,0,1),Premultiply:(bool?)app["premultiplyAlpha"]==true,ResourceScope:(int?)app["_previewScope"]??0,Layers:layers,Octagonal:(bool?)app["octagonal"]==true,Dither:type==10015&&(bool?)app["dither"]==true,
                        AnimationBinder:modelAppearance?(int?)app["anibnd"]??0:0,AnimationClip:(int?)app["animation"]??0,AnimationTime:age*V(app,"animationSpeed",0,ps,1),AnimationLoop:(bool?)app["loopAnimation"]??true,
                        SoftDepthRadius:type is 603 or 604&&(bool?)app["depthBlend"]==true?.5f*Math.Min(Math.Abs(width),Math.Abs(height)):0,
                        DepthOffset:type is 603 or 604?V(app,"depthOffset",age,ps):0));
                }
                else if(type==10300)Notices.Add("Non-rendering wind force field (10300): interaction with other particles is not simulated.");
                else if(type!=0)OmittedAppearances.Add(type);
                int childIndex=0;
                var children=node["nodes"] as JArray??new JArray();
                int selected=-1;
                if(Type(cfg["nodeSelector"])==201)
                {
                    var weights=children.Select((_,i)=>Math.Max(0,FxrPreviewValue.Get(cfg["nodeSelector"]?["weights"]?.ElementAtOrDefault(i),emissionTime,ps,1))).ToArray();
                    float total=weights.Sum(),pick=FxrPreviewValue.Random(ps+991)*total;
                    for(int i=0;i<weights.Length;i++){pick-=weights[i];if(pick<0){selected=i;break;}}
                    if(total<=0)selected=children.Count;
                }
                if(!ownsChildren)foreach(var child in children)
                {int index=childIndex++;if(selected<0||selected==index)Walk(child,ParticleAt,birth,death,unchecked(ps+(index+1)*7919),depth+1);}
                if(Items.Count>=Budget){Limited=true;return;}
            }
            }
        }
        void AddTracer(JObject app,JObject modifier,Func<float,Matrix> at,float birth,float death,float age,float emission,int seed)
        {
            float last=Math.Min(now,death),life=Math.Max(0,V(app,"segmentDuration",age,seed,1));
            if(last<=birth || life<=0)return;
            float step=Math.Max(1f/240,V(app,"segmentInterval",0,seed));
            if(V(app,"segmentInterval",0,seed)<=0)step=1f/60;
            int authoredLimit=(int)V(app,"concurrentSegments",0,seed,100);
            if(authoredLimit<=0)return;
            int cap=Math.Min(512,authoredLimit);
            double finalIndex=Math.Floor((last-birth)/step+1e-5);
            double firstIndex=Math.Max(0,Math.Ceiling((last-life-birth)/step-1e-5));
            bool leading=last-(birth+finalIndex*step)>1e-5;
            double needed=finalIndex-firstIndex+(leading?1:0);
            if(needed>cap && authoredLimit>cap)Limited=true;
            firstIndex=Math.Max(firstIndex,finalIndex-cap+(leading?1:0));
            var times=new List<float>();
            for(int n=0;n<=cap && firstIndex+n<=finalIndex;n++)times.Add(Math.Min(last,birth+(float)((firstIndex+n)*step)));
            if(leading)times.Add(last);
            if(times.Count<2)return;
            float tail=times[0],span=Math.Max(1e-6f,last-tail);
            float fadeOut=now<death?1:Math.Clamp(1-(now-death)/Math.Max(1e-6f,V(app,"fadeOutTime",age,seed)),0,1);
            float fraction=V(app,"textureFraction",age,seed,.1f),scroll=FxrParticleSurface.Integrate(app["speedU"],null,age,seed);
            bool attached=(bool?)app["attachedUV"]!=false;
            int orientation=(int?)app["orientation"]??1;
            float U(float time)=>scroll+(attached?(time-tail)/span:(time-birth)/step)*fraction;
            float start=V(app,"startFadeEndpoint",age,seed)/100,end=V(app,"endFadeEndpoint",age,seed)/100;
            float Alpha(float time)
            {
                float p=(time-tail)/span;
                return fadeOut*(start>0&&p<start?Math.Clamp(p/start,0,1):end>0?Math.Clamp((1-p)/end,0,1):1);
            }
            Vector3 Side(Matrix pose,Vector3 travel,int mode)=>mode switch
            {
                1=>BulletMath.Unit(pose.Backward,Vector3.Backward),
                2=>Vector3.Up,
                3=>Vector3.Right,
                5=>Vector3.Normalize(Vector3.One),
                _=>BulletMath.Unit(Vector3.Cross(travel,cameraPosition-pose.Translation),cameraRight)
            };
            int subdivisions=(int)V(app,"segmentSubdivision",0,seed);
            if(subdivisions>31){Limited=true;Notices.Add("Tracer subdivision capped at 32 pieces per completed segment.");}
            int pieces=1+Math.Clamp(subdivisions,0,31);
            // Pooling these histories was measured and rejected: on the tracer-bound
            // 653503 benchmark it raised the median from 17.2 ms to 30.6 ms because
            // the per-tracer rent/return traffic thrashes the shared pool while the
            // item budget is already saturated. Plain allocation stays faster here.
            int count=times.Count;
            var poses=times.Select(at).ToArray();
            var widths=new float[count];float widthMultiplier=V(app,"widthMultiplier",emission,seed,1);
            for(int k=0;k<count;k++)widths[k]=V(app,"width",times[k]-birth,seed,1)*widthMultiplier;
            var tint=SurfaceColor(app,modifier,age,emission,age+emission,seed);var atlas=FxrTextureAnimation.Evaluate(app,age,seed);
            int texture=(int)V(app,"texture",0,seed,-1),blend=(int)V(app,"blendMode",0,seed,2),scope=(int?)app["_previewScope"]??0;
            float variance=FxrPreviewValue.Random(seed+83)*V(app,"varianceV",age,seed),alphaFade=Math.Clamp(V(app,"alphaFadeThreshold",age,seed)/255,0,1),alphaCutoff=Math.Clamp(V(app,"alphaThreshold",age+emission,seed)/255,0,1);
            bool premultiply=(bool?)app["premultiplyAlpha"]==true;
            bool dynamicOpacity=(bool?)app["dynamicOpacity"]==true;
            Span<float> opacity=stackalloc float[times.Count];
            foreach(int mode in orientation==4?new[]{2,3}:new[]{orientation})
            {
                var left=new Vector3[count];var right=new Vector3[count];var directions=new Vector3[count];
                for(int k=0;k<count;k++)
                {
                    var travel=poses[Math.Min(k+1,times.Count-1)].Translation-poses[Math.Max(0,k-1)].Translation;
                    directions[k]=Side(poses[k],travel,mode);var side=directions[k]*widths[k]*.5f;
                    left[k]=poses[k].Translation-side;right[k]=poses[k].Translation+side;
                }
                if(dynamicOpacity)FxrTracerOpacity.Evaluate(left.AsSpan(0,count),right.AsSpan(0,count),tracerProjection,V(app,"unk_sdt_f1_15",age,seed,1),opacity);
                else opacity.Fill(1);
                // ER 141CF0DD0: uniform Hermite interpolation of the center and
                // width, with XYZ tangent scale 0.5 and W scale 1. Direction is
                // interpolated separately. Smoothing the two edges instead
                // introduces an extra bend when the weapon rotates or widens.
                Vector4 CenterWidth(int k)
                {
                    if(k<0)return 2*new Vector4(poses[0].Translation,widths[0])-new Vector4(poses[1].Translation,widths[1]);
                    if(k>=count)return 2*new Vector4(poses[count-1].Translation,widths[count-1])-new Vector4(poses[count-2].Translation,widths[count-2]);
                    return new(poses[k].Translation,widths[k]);
                }
                for(int i=1;i<times.Count;i++)
                {
                    if(Items.Count>=Budget){Limited=true;return;}
                    float t0=times[i-1],t1=times[i];var a=poses[i-1];var b=poses[i];
                    // The leading segment is always an unsmoothed quad. Its
                    // other end follows the source until the next knot completes.
                    bool smooth=pieces>1&&(!leading||i<times.Count-1);
                    var curve=smooth?new FxrRibbonTangents(Vector3.Zero,Vector3.Zero,Vector3.Zero,Vector3.Zero,
                        new(CenterWidth(i-1),CenterWidth(i),(CenterWidth(i)-CenterWidth(i-2))*new Vector4(.5f,.5f,.5f,1),
                            (CenterWidth(i+1)-CenterWidth(i-1))*new Vector4(.5f,.5f,.5f,1),directions[i-1],directions[i])):null;
                    var geometry=new FxrRibbonGeometry(left[i-1],right[i-1],right[i],left[i],
                        U(t0),U(t1),variance,Alpha(t0)*opacity[i-1],Alpha(t1)*opacity[i],curve,smooth?pieces:1);
                    // A stationary pivot can still sweep a visible blade with its
                    // changing local-Z orientation; test area, not center speed.
                    if(Vector3.Cross(geometry.Right-geometry.Left,geometry.NextRight-geometry.Left).LengthSquared()<1e-12f &&
                        Vector3.Cross(geometry.NextRight-geometry.Left,geometry.NextLeft-geometry.Left).LengthSquared()<1e-12f)continue;
                    Items.Add(new(texture,-1,a,tint,blend,b.Translation,widths[i-1],U(t0),atlas,
                        AlphaFade:alphaFade,AlphaCutoff:alphaCutoff,Premultiply:premultiply,Ribbon:geometry,ResourceScope:scope));
                }
            }
        }
        static Vector4 SurfaceColor(JToken app,JToken modifier,float age,float emission,float active,int seed,bool line=false)
        {
            var c=FxrParticleSurface.Vector(app["color1"],age,seed,true)*FxrParticleSurface.Vector(app["color2"],line?age:emission,seed,true)*
                FxrParticleSurface.Vector(app["color3"],line?active:age,seed,line)*FxrParticleSurface.Vector(modifier?["color"],active,seed);
            float rgb=V(app,"rgbMultiplier",active,seed,1);c*=new Vector4(rgb,rgb,rgb,V(app,"alphaMultiplier",active,seed,1));return c;
        }
        static Vector4 Color(JToken app,JToken modifier,float age,int seed)
        {
            var c=Vector4.One;
            for(int i=0;i<4;i++)
            {
                float v=FxrPreviewValue.Get(app["color1"],age,seed,1,i)*FxrPreviewValue.Get(app["color2"],age,seed,1,i)*
                    FxrPreviewValue.Get(app["color3"],age,seed,1,i)*FxrPreviewValue.Get(modifier?["color"],0,seed,1,i)*V(app,i==3?"alphaMultiplier":"rgbMultiplier",age,seed,1);
                if(i==0)c.X=v;else if(i==1)c.Y=v;else if(i==2)c.Z=v;else c.W=v;
            }
            return c;
        }
    }
}
