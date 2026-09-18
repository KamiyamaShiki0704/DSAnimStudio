using System;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // Shader constants verified against DS3 1402723D4 / 1402730xx and original
    // GXFfxLightShaft PS. Placement/visibility still lacks the game's camera state.
    public sealed record FxrLightShaft(Vector4 Inner, Vector4 Outer, Vector4 Parameters,
        Vector2 SourceScale, int Samples, Matrix Frame, float DirectionLength)
    {
        public static FxrLightShaft Read(JObject app, float age, int seed, Matrix frame)
        {
            float V(string key,float fallback)=>FxrPreviewValue.Get(app[key],age,seed,fallback);
            var color=FxrParticleSurface.Vector(app["color1"],age,seed,true);
            var multiplier=new Vector4(V("unk_ds3_f1_9",1),V("unk_ds3_f1_9",1),V("unk_ds3_f1_9",1),V("unk_ds3_f1_10",1));
            return new(color*multiplier*FxrParticleSurface.Vector(app["color2"],age,seed,true),
                color*multiplier*FxrParticleSurface.Vector(app["color3"],age,seed,true),
                new(V("unk_ds3_f1_2",.75f),V("unk_ds3_f1_3",.75f),V("unk_ds3_f1_4",2),V("unk_ds3_f1_5",.1f)),
                new(V("unk_ds3_f1_6",1),V("unk_ds3_f1_7",1)),Math.Max(0,(int)V("layers",30)),frame,V("unk_ds3_f1_15",1));
        }

        // 140272A9A..140272D74: native clip-space endpoint sign, normalization,
        // direction scale in hundredths, and signed axial component.
        public static Vector4 ProjectDirection(Vector3 position,Vector3 offset,Matrix viewProjection,float length,float axial)
        {
            var start=Vector4.Transform(new Vector4(position,1),viewProjection);
            var end=Vector4.Transform(new Vector4(position+offset,1),viewProjection);
            if(Math.Abs(start.W)<1e-8f||Math.Abs(end.W)<1e-8f)return Vector4.Zero;
            var a=new Vector2(start.X/start.W*.5f+.5f,.5f-start.Y/start.W*.5f);
            float sign=end.W>=0?-1:1;
            var b=new Vector2(sign*end.X/end.W*.5f+.5f,.5f-sign*end.Y/end.W*.5f);
            var delta=b-a;
            float depthDelta=end.Z/end.W-start.Z/start.W;
            float norm=MathF.Sqrt(delta.LengthSquared()+depthDelta*depthDelta);
            if(norm>1e-8f)delta*=(-length/100)/norm;else delta=Vector2.Zero;
            return new(delta.X,delta.Y,axial,end.W);
        }
    }
}
