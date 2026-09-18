using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // Dedicated render contracts. These cannot be submitted as ordinary textured cards:
    // 607/608 sample the opaque scene, lights shade geometry, flares reflect in screen space.
    public sealed record FxrScenePass(int Type,int Mode=0,int Shape=0,int NormalMap=0,int Mask=0,
        float Strength=0,float Radius=1,float Angle=0,int Samples=1,Vector4 NormalUv=default,
        Vector4 Specular=default,float Near=0,float Far=0,float RadiusX=0,float RadiusY=0,
        int Reflection=0,float Offset=0,float AttenuationRadius=-1,FxrLightShaft LightShaft=null);

    public static class FxrSpecialAppearance
    {
        public static bool IsSupported(int type)=>type is 607 or 608 or 609 or 11000 or 10014 or 10003;
        public static bool Emit(JObject app,float age,int seed,Matrix particleWorld,Matrix camera,
            int resourceScope,List<FxrPreviewItem> items,Action<string> notice)
        {
            int type=(int?)app?["type"]??0;if(!IsSupported(type))return false;
            float V(string key,float fallback=0,float? clock=null)=>FxrPreviewValue.Get(app[key],clock??age,seed,fallback);
            Vector4 C(string key)=>FxrParticleSurface.Vector(app[key],age,seed,true);
            if(type==10003)
            {
                var shaft=FxrLightShaft.Read(app,age,seed,particleWorld);
                if(shaft.Samples<=0)return true;
                var shaftTransform=Matrix.CreateScale(V("width",1),V("height",1),1)*
                    FxrParticleSurface.Orient(1,particleWorld,camera.Translation,camera.Right,camera.Up);
                items.Add(new((int)V("texture",0),-1,shaftTransform,Vector4.One,(int)V("blendMode",4),
                    ResourceScope:resourceScope,ScenePass:new(type,LightShaft:shaft)));
                notice?.Invoke("Light shaft: original source/accumulation shader and depth-tested source; native temporal visibility, dust scheduling and camera-state blending remain incomplete.");
                return true;
            }
            if(type is 609 or 11000)
            {
                var diffuse=C("diffuseColor")*V("diffuseMultiplier",1);
                var specular=C("specularColor")*V("specularMultiplier",1);
                float radius=V(type==609?"radius":"far",type==609?10:50);
                if(radius<=0)return true;
                var pass=new FxrScenePass(type,Specular:specular,Near:type==609?0:Math.Max(.0001f,V("near",.01f)),
                    Far:radius,RadiusX:Math.Max(.0001f,V("radiusX",50)),RadiusY:Math.Max(.0001f,V("radiusY",50)));
                items.Add(new(-1,-1,particleWorld,diffuse,2,ResourceScope:resourceScope,ScenePass:pass));
                notice?.Invoke("FXR light: shades FXR model geometry; character/terrain lighting, shadows, volumetrics and jitter/flicker are not reconstructed.");
                return true;
            }
            if(type==10014)
            {
                for(int layer=1;layer<=4;layer++)
                {
                    string key="layer"+layer;int texture=(int)V(key,layer==1?1:0,0);if(texture<=0)continue;
                    int authoredCount=Math.Max(0,(int)V(key+"Count",1,0)),count=Math.Min(256,authoredCount);
                    if(count!=authoredCount)notice?.Invoke("Lens flare layer count exceeds 256; remaining reflections are omitted.");
                    for(int i=0;i<count;i++)
                    {
                        float Variation(string axis,int salt)=>MathHelper.Lerp(V(key+"ScaleVariation"+axis,1,0),1,FxrPreviewValue.Random(seed+layer*317+i*31+salt));
                        float sx=Variation("X",1),sy=(bool?)app[key+"UniformScale"]==true?sx:Variation("Y",2);
                        var matrix=Matrix.CreateScale(V(key+"Width",1)*sx,V(key+"Height",1)*sy,1)*
                            FxrParticleSurface.Orient(1,particleWorld,camera.Translation,camera.Right,camera.Up);
                        float offset=V(key+"Offset",0,0)*MathHelper.Lerp(V(key+"OffsetVariation",1,0),1,FxrPreviewValue.Random(seed+layer*317+i*31+3));
                        var pass=new FxrScenePass(type,Reflection:(int)V(key+"Reflection",0,0),Offset:offset,
                            AttenuationRadius:V(key+"AttenuationRadius",-1,0));
                        items.Add(new(texture,-1,matrix,C(key+"Color")*FxrParticleSurface.Vector(app[key+"ColorMultiplier"],0,seed),
                            (int)V("blendMode",4,0),ResourceScope:resourceScope,ScenePass:pass));
                    }
                }
                notice?.Invoke("Lens flare: four texture layers, screen-space reflection and source depth test; native occlusion queries, temporal visibility and bloom remain incomplete.");
                return true;
            }
            float width=V(type==607?"sizeX":"width",1),height=(bool?)app["uniformScale"]==true?width:V(type==607?"sizeY":"height",1);
            if(type==607)
            {
                float x=MathHelper.Lerp(V("scaleVariationX",1,0),1,FxrPreviewValue.Random(seed+61));
                width*=x;height*=(bool?)app["uniformScale"]==true?x:MathHelper.Lerp(V("scaleVariationY",1,0),1,FxrPreviewValue.Random(seed+62));
            }
            var basis=FxrParticleSurface.Orient((int)V("orientation",1,0),particleWorld,camera.Translation,camera.Right,camera.Up);
            var transform=Matrix.CreateScale(width,height,1)*Matrix.CreateTranslation(V("offsetX"),V("offsetY"),V("offsetZ"))*basis;
            var color=C("color")*new Vector4(V("rgbMultiplier",1),V("rgbMultiplier",1),V("rgbMultiplier",1),V("alphaMultiplier",1));
            var descriptor=new FxrScenePass(type,Mode:(int)V("mode",1,0),Shape:(int)V("shape",0,0),NormalMap:(int)V("normalMap",0,0),Mask:(int)V("mask",type==608?1:0,0),
                Strength:V(type==607?"intensity":"blurRadius",type==607?1:.5f),Radius:V("radius",1),
                Angle:MathHelper.ToRadians(FxrParticleSurface.Integrate(app["stirSpeed"],null,age,seed,60)),Samples:Math.Clamp((int)V("iterations",1,0)*4,2,64),
                NormalUv:new Vector4(V("normalMapOffsetU")+FxrParticleSurface.Integrate(app["normalMapSpeedU"],null,age,seed),
                    V("normalMapOffsetV")+FxrParticleSurface.Integrate(app["normalMapSpeedV"],null,age,seed),V("normalMapScaleU",1),V("normalMapScaleV",1)));
            if(type==607&&(descriptor.Mode is <0 or >2))
            {notice?.Invoke($"Distortion mode {descriptor.Mode} has unknown semantics; layer omitted.");return true;}
            if(type==607&&descriptor.Shape!=0)notice?.Invoke("Distortion ellipsoid: screen ellipse coverage; native curved volume and depth intersection remain approximate.");
            notice?.Invoke(type==607?"Distortion: masked scene-color sampling; native displacement scale and soft depth are approximate.":
                "Radial blur: masked multi-tap scene-color sampling; native tap schedule and soft depth are approximate.");
            items.Add(new(type==607?(int)V("texture",0,0):-1,-1,transform,color,4,
                AlphaCutoff:Math.Clamp(V("alphaThreshold")/255,0,1),ResourceScope:resourceScope,ScenePass:descriptor));
            return true;
        }

        // Independent CPU reference for the light volume; shader uses the same elliptical cone.
        public static float LightAttenuation(FxrScenePass light,Matrix frame,Vector3 point)
        {
            var local=Vector3.Transform(point,Matrix.Invert(frame));
            if(light.Type==609)return MathF.Pow(Math.Max(0,1-local.Length()/Math.Max(light.Far,.0001f)),2);
            if(local.Z<light.Near||local.Z>=light.Far)return 0;
            float x=local.X*light.Far/(Math.Max(local.Z,.0001f)*light.RadiusX),y=local.Y*light.Far/(Math.Max(local.Z,.0001f)*light.RadiusY);
            return Math.Max(0,1-x*x-y*y)*MathF.Pow(1-local.Z/light.Far,2);
        }
        public static Vector2 Reflect(Vector2 source,int reflection,float offset)=>reflection switch
        {1=>source-source*(2*offset),2=>new(source.X*(1-2*offset),source.Y),_=>source};
    }
}
