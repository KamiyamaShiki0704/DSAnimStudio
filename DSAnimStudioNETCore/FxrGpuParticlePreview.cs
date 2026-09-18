using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // CPU reconstruction of the independent emitters embedded in GPU appearances.
    // Named JSON already contains seconds/degrees; never reapply binary scaling.
    public static class FxrGpuParticlePreview
    {
        public static bool Supports(int type)=>type is 10000 or 10001 or 10008 or 10009;
        public static bool Build(JObject app,Func<float,Matrix> anchor,float begin,float end,float now,int seed,Matrix camera,List<FxrPreviewItem> items,int budget,HashSet<string> notices,FxrForceSet fields=null,JObject receiverAction=null,float clockScale=1,FxrCollisionWorld collisions=null,ulong receiverGroups=ulong.MaxValue)
        {
            int type=(int)app["type"];bool spark=type is 10008 or 10009,limited=false;
            float V(string k,float t=0,float fallback=0)=>FxrPreviewValue.Get(app[k],t,seed,fallback);
            notices.Add($"{type}: CPU particle preview; native GPU scheduling, depth-buffer collisions and unknown fields remain incomplete.");
            bool forceEnabled=fields?.Count>0&&(int?)receiverAction?["type"] is 732 or 734;
            if(forceEnabled)notices.Add("GPU particles: explicit particle force response uses shared fields; native GPU coupling is unverified.");
            bool collisionRequested=(bool?)app["particleCollision"]==true||(int?)receiverAction?["type"]==800;
            JObject collisionAction=null;
            if(collisionRequested)
            {
                if(collisions?.Count>0)
                {
                    collisionAction=(int?)receiverAction?["type"]==800?receiverAction:new JObject{["radius"]=0,["friction"]=0,["bounciness"]=app["particleBounciness"]?.DeepClone()??new JValue(0)};
                    notices.Add("GPU collisions use explicit preview geometry; original camera-depth collision visibility is not reconstructed.");
                }
                else notices.Add("GPU particle collision requested, but no preview collision geometry is supplied.");
            }
            if(type==10009)notices.Add("10009: uses Spark preview geometry; native axis and scale differences remain unverified.");
            bool nativeSpark=type==10008&&(bool?)app["_previewNativeSpark"]==true;
            Span<Vector3> sparkPoints=stackalloc Vector3[13];
            var sparkSegments=nativeSpark?new FxrRibbonGeometry[FxrGpuSparkGeometry.SegmentCount]:null;
            if(nativeSpark)notices.Add("Spark: original ten-segment geometry, distance UV and cubic tail opacity; history uses 60 Hz preview samples, native history timing and width/length upload remain approximate.");
            bool randomTurns=(bool?)app["particleRandomTurns"]==true;
            if(randomTurns)notices.Add("GPU random turns use a deterministic maximum-interval preview clock and turn initial velocity; native random timing / acceleration coupling remain unverified.");
            bool trace=!spark&&(bool?)app["traceParticles"]==true;
            if(trace)notices.Add("GPU trace: original standard-particle geometry/UV/head strip; previous pose uses a deterministic 60 Hz preview interval. Native simulation clock and parameter upload remain unverified.");
            foreach(var p in app.Properties())if(p.Name!="color"&&p.Value is JObject o&&o["function"]!=null)
                notices.Add($"{type}: {p.Name} curve argument is not natively verified; named motion fields use preview age, unknown fields are omitted.");
            float life=V("particleDuration",fallback:1)*V("particleDurationMultiplier",fallback:1);
            if(life<=0||now<begin)return false;
            using var customAtlas=!spark&&(bool?)app["randomTextureFrame"]==true?new FxrGpuTextureAnimation.Curve(app,life,seed):null;
            int limit=!spark&&(bool?)app["limitEmissionCount"]==true?Math.Max(0,(int)V("emissionCountLimit")):int.MaxValue;
            if(spark&&(bool?)app["limitConcurrentEmissions"]==true)notices.Add("Spark concurrent-emission limit semantics are unknown; preview uses its global budget.");
            var rotation=FxrTransformMotion.Rotation(FxrPreviewValue.Vec(app["emitterRotation"],0,seed));
            var anchors=new FxrFrameMemo<Matrix>(anchor,4096);
            float birth=begin;
            for(int emission=0;birth<=now+1e-6f&&birth<end&&emission<limit;emission++)
            {
                if(emission>=4096){limited=true;break;}
                int es=unchecked(seed+emission*7919);
                float Range(string key,int salt,float fallback=0,int component=0)
                {
                    float a=FxrPreviewValue.Get(app[key+"Min"],0,es,fallback,component),b=FxrPreviewValue.Get(app[key+"Max"],0,es,fallback,component);
                    return MathHelper.Lerp(Math.Min(a,b),Math.Max(a,b),FxrPreviewValue.Random(unchecked(es+salt)));
                }
                float interval=(spark?V("emissionInterval"):0)+Range("emissionInterval",11,spark?0:1);
                interval=interval<=0?1f/60:Math.Max(1f/240,interval);
                float age=now-birth;
                if(age<life&&age>=0)
                {
                    int authoredCount=Math.Max(0,(int)(V("emissionParticleCount",fallback:10)+Range("emissionParticleCount",13)));
                    int count=Math.Min(authoredCount,512);limited|=authoredCount>512;
                    for(int n=0;n<count;n++)
                    {
                        if(items.Count>=budget)return true;
                        int ps=unchecked(es+n*397);
                        float RandomRange(string key,int salt,float fallback=0,int c=0)=>MathHelper.Lerp(
                            FxrPreviewValue.Get(app[key+"Min"],0,ps,fallback,c),FxrPreviewValue.Get(app[key+"Max"],0,ps,fallback,c),FxrPreviewValue.Random(unchecked(ps+salt+c*101)));
                        Vector3 RandomVector(string key,int salt,float fallback=0)=>new(RandomRange(key,salt,fallback,0),RandomRange(key,salt,fallback,1),RandomRange(key,salt,fallback,2));
                        float P(string key,float t,float fallback=0)=>FxrPreviewValue.Get(app[key],t,ps,fallback);
                        var size=FxrPreviewValue.Vec(app["emitterSize"],0,ps,1);
                        int shape=(int)V("emitterShape",fallback:1);float distribution=Math.Clamp(V("emitterDistribution"),0,1);
                        var u=new Vector3(FxrPreviewValue.Random(ps+21),FxrPreviewValue.Random(ps+22),FxrPreviewValue.Random(ps+23));
                        Vector3 offset;
                        if(shape is 0 or 3){offset=Vector3.Backward*size.Z*MathHelper.Lerp(distribution,1,u.Z);notices.Add("GPU line emitter uses local +Z/sizeZ preview convention.");}
                        else if(shape==4)
                        {
                            float angle=u.X*MathHelper.TwoPi,r=MathF.Sqrt(MathHelper.Lerp(distribution*distribution,1,u.Y));
                            offset=new(MathF.Cos(angle)*size.X*r*.5f,(u.Z-.5f)*size.Y,MathF.Sin(angle)*size.Z*r*.5f);
                            notices.Add("GPU cylinder emitter uses a centered Y-axis preview convention.");
                        }
                        else if(shape is 1 or 2)
                        {
                            offset=(u-new Vector3(.5f))*size;
                            if(shape==1&&distribution>0)
                            {
                                var normalized=u*2-Vector3.One;float m=Math.Max(Math.Abs(normalized.X),Math.Max(Math.Abs(normalized.Y),Math.Abs(normalized.Z)));
                                if(m>1e-7f)offset*=MathF.Cbrt(distribution*distribution*distribution+(1-distribution*distribution*distribution)*m*m*m)/m;
                            }
                            if(shape==2){offset*=MathHelper.Lerp(FxrPreviewValue.Random(ps+24),1,distribution);notices.Add("GPU Box2 uses an approximate center-weighted distribution.");}
                        }
                        else{notices.Add($"GPU emitter shape {shape} is unknown; internal particles omitted.");break;}
                        offset=Vector3.Transform(offset,rotation)+FxrPreviewValue.Vec(app["particleOffset"],0,ps)+RandomVector("particleOffset",31);
                        var speed=(spark?Vector3.Zero:FxrPreviewValue.Vec(app["particleSpeed"],0,ps))+RandomVector("particleSpeed",41);
                        var acceleration=spark?Vector3.Zero:RandomVector("particleAcceleration",51);
                        var born=anchors.At(birth);
                        Vector3 TurnDisplacement(float ageValue)
                        {
                            var correction=Vector3.Zero;
                            if(!randomTurns||speed.LengthSquared()<1e-12f)return correction;
                            float turnInterval=P("particleRandomTurnIntervalMax",0);
                            if(!float.IsFinite(turnInterval)||turnInterval<=0)
                            {notices.Add("GPU random turn interval is invalid; original straight motion retained.");return correction;}
                            if(turnInterval<1f/240){turnInterval=1f/240;limited=true;notices.Add("GPU random turn interval clamped to the 240 Hz preview budget.");}
                            var velocity=speed;float magnitude=speed.Length();
                            int count=(int)Math.Min(1024,Math.Floor(ageValue/turnInterval));
                            if(ageValue/turnInterval>1024){limited=true;notices.Add("GPU random turn budget reached; continuing the last velocity without new turns.");}
                            for(int k=1;k<=count;k++)
                            {
                                float turnTime=k*turnInterval,angle=P("particleRandomTurnAngle",turnTime);
                                if(!float.IsFinite(angle)||angle<=0)continue;
                                var next=FxrEmissionGeometry.Turn(velocity,angle,unchecked(ps+15791+k*7919))*magnitude;
                                correction+=(next-velocity)*Math.Max(0,ageValue-turnTime);velocity=next;
                            }
                            return correction;
                        }
                        Vector3 BasePosition(float a)
                        {
                            var displacement=speed*a+TurnDisplacement(a)+acceleration*(.5f*a*a)+new Vector3(
                                FxrParticleSurface.Integrate(app["particleAccelerationX"],null,a,ps,displacement:true),
                                FxrParticleSurface.Integrate(app["particleAccelerationY"],null,a,ps,displacement:true),
                                FxrParticleSurface.Integrate(app["particleAccelerationZ"],null,a,ps,displacement:true));
                            var p=Vector3.Transform(offset+displacement,born);
                            p.Y-=FxrParticleSurface.Integrate(app["particleGravity"],null,a,ps,spark?1:0,displacement:true);
                            if(app["particleFollowFactor"] is JObject||P("particleFollowFactor",0)!=0)
                            {
                                int ticks=Math.Clamp((int)Math.Ceiling(a*60),1,256);float dt=a/ticks;var previous=born.Translation;
                                for(int k=1;k<=ticks;k++){var next=anchors.At(birth+k*dt).Translation;p+=(next-previous)*P("particleFollowFactor",(k-.5f)*dt);previous=next;}
                            }
                            return p;
                        }
                        Matrix BasePose(float t){var pose=born;pose.Translation=BasePosition(Math.Max(0,t-birth));return pose;}
                        FxrForceMotion forceMotion=forceEnabled?new(BasePose,fields,receiverAction,birth,begin,now,clockScale,ps,receiverGroups:receiverGroups):null;
                        Matrix ForcePose(float t)=>forceMotion?.At(t)??BasePose(t);
                        FxrCollisionMotion collisionMotion=collisionAction!=null?new(ForcePose,collisions,collisionAction,birth,ps):null;
                        Vector3 Position(float a)
                        {
                            var pose=collisionMotion?.At(birth+a)??ForcePose(birth+a);
                            limited|=forceMotion?.Limited==true||collisionMotion?.Limited==true;
                            return pose.Translation;
                        }
                        bool nativeColor=type==10000&&(bool?)app["_previewNativeGpuColor"]==true;
                        var color=nativeColor?FxrGpuTextureAnimation.SampleColor(app["color"],V("particleDuration",fallback:1),life,age,ps):FxrParticleSurface.Vector(app["color"],age,ps);
                        color+=new Vector4(RandomRange("color",61,0,0),RandomRange("color",61,0,1),RandomRange("color",61,0,2),RandomRange("color",61,0,3))*Math.Clamp(1-age/life,0,1);
                        if(nativeColor)color=Vector4.Max(color,Vector4.Zero);
                        color*=new Vector4(V("rgbMultiplier",fallback:1),V("rgbMultiplier",fallback:1),V("rgbMultiplier",fallback:1),V("alphaMultiplier",fallback:1));
                        int texture=(int)V("texture",fallback:1),blend=(int)V("blendMode",fallback:spark?4:2),scope=(int?)app["_previewScope"]??0;
                        if(spark)
                        {
                            float length=P("particleLength",age,1)*RandomRange("particleLength",71,1);
                            float duration=Math.Min(age,Math.Max(0,length));
                            float width=P("particleWidth",age,.1f)*RandomRange("particleWidth",73,1);
                            if(duration<=0||Math.Abs(width)<1e-7f)continue;
                            if(nativeSpark)
                            {
                                if(!float.IsFinite(length)||!float.IsFinite(width)){notices.Add("Spark contains a non-finite width/length; invalid geometry omitted.");continue;}
                                for(int k=0;k<12;k++)sparkPoints[k+1]=Position(Math.Max(0,age-k/60f));
                                sparkPoints[0]=sparkPoints[1]*2-sparkPoints[2];
                                FxrGpuSparkGeometry.Build(sparkPoints,camera.Translation,width*.5f,length,sparkSegments);
                                foreach(var segment in sparkSegments)
                                {
                                    if(items.Count>=budget)return true;
                                    if(Vector3.DistanceSquared(segment.Left,segment.NextLeft)<1e-12f&&Vector3.DistanceSquared(segment.Right,segment.NextRight)<1e-12f)continue;
                                    items.Add(new(texture,-1,Matrix.Identity,color,blend,Ribbon:segment,ResourceScope:scope));
                                }
                                continue;
                            }
                            var previousPosition=Position(age-duration);
                            for(int k=0;k<4;k++)
                            {
                                if(items.Count>=budget)return true;
                                float b=age-duration+duration*(k+1)/4;var p=previousPosition;var q=Position(b);previousPosition=q;
                                var side=BulletMath.Unit(Vector3.Cross(q-p,camera.Translation-p),camera.Right)*width*.5f;
                                if(Vector3.DistanceSquared(p,q)<1e-12f)continue;
                                items.Add(new(texture,-1,Matrix.Identity,color,blend,Ribbon:new(p-side,p+side,q+side,q-side,k/4f,(k+1)/4f,0,1,1),ResourceScope:scope));
                            }
                        }
                        else
                        {
                            float Size(string axis,int salt)=>P("particleSizeMultiplier",0,1)*(P("particleSize"+axis,0,1)+RandomRange("particleSize"+axis,salt)+
                                (P("particleGrowthRate"+axis+"Static",0)+RandomRange("particleGrowthRate"+axis,salt+1))*age+
                                FxrParticleSurface.Integrate(app["particleGrowthRate"+axis],null,age,ps)+.5f*RandomRange("particleGrowthAcceleration"+axis,salt+2)*age*age);
                            float x=Size("X",81),y=(bool?)app["particleUniformScale"]==true?x:Size("Y",91);
                            var angles=FxrPreviewValue.Vec(app["particleRotationVariance"],0,ps)*new Vector3(FxrPreviewValue.Random(ps+111)*2-1,FxrPreviewValue.Random(ps+112)*2-1,FxrPreviewValue.Random(ps+113)*2-1);
                            angles+=FxrPreviewValue.Vec(app["particleAngularSpeedVariance"],0,ps)*new Vector3(FxrPreviewValue.Random(ps+114)*2-1,FxrPreviewValue.Random(ps+115)*2-1,FxrPreviewValue.Random(ps+116)*2-1)*age+RandomVector("particleAngularAcceleration",117)*(.5f*age*age);
                            angles.Z+=FxrParticleSurface.Integrate(app["particleAngularAccelerationZ"],null,age,ps,displacement:true);
                            var basis=camera;basis.Translation=Position(age);var matrix=Matrix.CreateScale(x,y,1)*FxrTransformMotion.Rotation(angles)*basis;
                            int columns=Math.Clamp((int)V("columns",fallback:1),1,4096),frames=Math.Clamp((int)V("totalFrames",fallback:1),1,65536),frame=0;
                            FxrAtlasFrame atlas;
                            if(customAtlas!=null)
                            {
                                int range=Math.Clamp((int)V("maxFrameIndex"),0,65535);
                                frame=unchecked((int)V("unk_ds3_f1_109")+(int)(FxrPreviewValue.Random(ps+121)*(range+1)));
                                atlas=customAtlas.At(age,frame);
                            }
                            else atlas=FxrGpuTextureAnimation.Lifetime(columns,frames,age,life);
                            var geometry=trace?FxrGpuTraceGeometry.Create(basis.Translation,Position(Math.Max(0,age-1f/60)),camera.Translation,x,y,
                                P("particleTraceLength",age,1),P("traceParticlesThreshold",age),P("unk_ds3_f1_149",age,1),
                                (bool?)app["traceParticleHead"]==true,matrix,(bool?)app["_previewModernTrace"]==true):null;
                            items.Add(new(texture,-1,matrix,color,blend,Atlas:atlas,ResourceScope:scope,GpuTrace:geometry));
                        }
                    }
                }
                birth+=interval;
            }
            return limited;
        }
    }
}
