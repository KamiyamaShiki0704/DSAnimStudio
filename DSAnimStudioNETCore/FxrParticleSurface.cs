using System;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public sealed record FxrTextureLayer(int Texture,Vector4 Color,Vector4 Uv);
    public sealed record FxrLayerMaterial(FxrTextureLayer First,FxrTextureLayer Second,FxrTextureLayer Third,int Mode,int SecondOperation=0,int ThirdOperation=0,bool Modern=false,bool NormalizeMask=true,bool ClampAlpha=false,bool FirstAlpha=false);
    public sealed record FxrLineGeometry(Vector3 HeadLeft,Vector3 HeadRight,Vector3 TailRight,Vector3 TailLeft,Vector4 HeadColor,Vector4 TailColor);

    // Shared billboard rules; model and tracer orientation enums are different.
    public static class FxrParticleSurface
    {
        public static Matrix Orient(int mode,Matrix particle,Vector3 cameraPosition,Vector3 cameraRight,Vector3 cameraUp,Vector3? flightDirection=null)
        {
            var p=particle.Translation;
            Matrix Plane(Vector3 right,Vector3 up,Vector3 back)
            {var m=Matrix.Identity;m.Right=right;m.Up=up;m.Backward=back;m.Translation=p;return m;}
            Matrix Toward(Vector3 up,bool yaw)
            {
                up=BulletMath.Unit(up,Vector3.Up);
                var delta=cameraPosition-p;
                if(yaw)delta-=up*Vector3.Dot(delta,up);
                var fallback=Vector3.Cross(cameraRight,up);
                if(fallback.LengthSquared()<1e-8f)fallback=Vector3.Cross(Math.Abs(up.Y)<.9f?Vector3.Up:Vector3.Right,up);
                var back=BulletMath.Unit(delta,BulletMath.Unit(fallback,Vector3.Backward));
                var right=BulletMath.Unit(Vector3.Cross(up,back),cameraRight);
                return Plane(right,BulletMath.Unit(Vector3.Cross(back,right),up),back);
            }
            Matrix AlongFlight()
            {
                // ER's mode-7 branch (141D0C169) reads particle +18/+1C/+20,
                // and locks the card's width to that direction. It does not
                // lock its height to the emitting node's Y axis.
                var right=BulletMath.Unit(flightDirection??particle.Backward,Vector3.Backward);
                var up=Vector3.Cross(right,cameraPosition-p);
                if(up.LengthSquared()<1e-8f)up=cameraUp-right*Vector3.Dot(cameraUp,right);
                if(up.LengthSquared()<1e-8f)up=Vector3.Cross(right,Math.Abs(right.Y)<.9f?Vector3.Up:Vector3.Right);
                up=Vector3.Normalize(up);
                return Plane(right,up,Vector3.Cross(right,up));
            }
            return mode switch
            {
                0 or 3=>Plane(Vector3.Right,Vector3.Up,Vector3.Backward),
                1=>Plane(cameraRight,cameraUp,Vector3.Cross(cameraRight,cameraUp)),
                2=>particle,
                4=>Toward(Vector3.Up,true),
                5=>Plane(Vector3.Forward,Vector3.Up,Vector3.Right),
                6=>Toward(cameraUp,false),
                7=>AlongFlight(),
                _=>particle
            };
        }
        public static Matrix OrientModel(int mode,bool rich,Matrix particle,Vector3 cameraPosition,Vector3 cameraRight,Vector3 cameraUp,Vector3 velocity)
        {
            var p=particle.Translation;
            if(mode==1){var camera=Matrix.Identity;camera.Right=cameraRight;camera.Up=cameraUp;camera.Backward=Vector3.Cross(cameraRight,cameraUp);camera.Translation=p;return camera;}
            if(mode==0||rich&&mode==3)return Matrix.CreateTranslation(p);
            var forward=mode==2?BulletMath.Unit(velocity,particle.Backward):cameraPosition-p;
            if(mode==4)forward.Y=0;
            if(!rich&&mode is 3 or 5)forward.X=0;
            forward=BulletMath.Unit(forward,Vector3.Backward);
            var up=mode==2?particle.Up:Vector3.Up;
            if(Math.Abs(Vector3.Dot(BulletMath.Unit(up,Vector3.Up),forward))>.999f)up=Vector3.Right;
            return Matrix.CreateWorld(p,-forward,up);
        }
        public static Vector4 Vector(JToken value,float time,int seed,bool clamp=false)
        {
            var v=new Vector4(FxrPreviewValue.Get(value,time,seed,1,0),FxrPreviewValue.Get(value,time,seed,1,1),
                FxrPreviewValue.Get(value,time,seed,1,2),FxrPreviewValue.Get(value,time,seed,1,3));
            return clamp?Vector4.Clamp(v,Vector4.Zero,Vector4.One):v;
        }
        // Bounded Simpson integration for authored speed/scroll curves. Numeric
        // preview integration, not a reproduction of the game's tick scheduler.
        public static float Integrate(JToken value,JToken multiplier,float age,int seed,float fallback=0,bool displacement=false,int component=0)
        {
            if(age<=0)return 0;
            bool Animated(JToken v)=>v is JObject o && (o["function"]!=null||Animated(o["value"]));
            bool valueAnimated=Animated(value),multiplierAnimated=Animated(multiplier);
            var cache=FxrCurveCache.Current;
            if((valueAnimated||multiplierAnimated)&&cache?.TryIntegral(value,multiplier,age,seed,fallback,displacement,component,out float cached)==true)return cached;
            // Random modifiers are fixed by the particle seed. Constant operands
            // therefore need one JSON evaluation, not one at every Simpson node.
            float constantValue=valueAnimated?0:FxrPreviewValue.Get(value,0,seed,fallback,component);
            float constantMultiplier=multiplierAnimated?0:FxrPreviewValue.Get(multiplier,0,seed,1,component);
            float At(float t)=>(displacement?age-t:1)*(valueAnimated?FxrPreviewValue.Get(value,t,seed,fallback,component):constantValue)
                *(multiplierAnimated?FxrPreviewValue.Get(multiplier,t,seed,1,component):constantMultiplier);
            if(!valueAnimated&&!multiplierAnimated)return age*At(0)*(displacement?.5f:1);
            int n=Math.Clamp((int)Math.Ceiling(Math.Min(age,4)*30),8,128);n+=(n&1);
            float step=age/n,sum=At(0)+At(age);
            for(int i=1;i<n;i++)sum+=At(i*step)*(i%2==0?2:4);
            float result=sum*step/3;
            cache?.AddIntegral(value,multiplier,age,seed,fallback,displacement,component,result);
            return result;
        }
        public static FxrLayerMaterial Layers(JObject app,float age,int seed)
        {
            float V(string key,float time=0,float fallback=0)=>FxrPreviewValue.Get(app[key],time,seed,fallback);
            FxrTextureLayer Layer(int n)
            {
                string k="layer"+n;
                return new((int)V(k,0,1),Vector(app[k+"Color"],age,seed),new(
                    V(k+"OffsetU")+Integrate(app[k+"SpeedU"],null,age,seed),
                    V(k+"OffsetV")+Integrate(app[k+"SpeedV"],null,age,seed),V(k+"ScaleU",age,1),V(k+"ScaleV",age,1)));
            }
            // DS3 14026439D..1402643C3 copies fields2[10..13] to
            // g_ps_TexBlendType / Type2 / Type3 / ColorBlendType.
            return new(Layer(1),Layer(2),Layer(3),(int?)app["unk_ds3_f2_10"]??0,
                (int?)app["unk_ds3_f2_11"]??0,(int?)app["unk_ds3_f2_12"]??0,(bool?)app["_previewModernLayers"]??false,(bool?)app["_previewNormalizeLayerMask"]??true,(bool?)app["_previewClampLayerAlpha"]??false,(bool?)app["_previewLayerFirstAlpha"]??false);
        }
    }
}
