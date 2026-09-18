using System;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // Atlas selection is independent of emission and draw submission. Frame index
    // is an authored property (possibly a curve), not an invented playback FPS.
    public readonly record struct FxrAtlasFrame(Vector4 Current,Vector4 Next,float Blend)
    {
        public static FxrAtlasFrame Full=>new(new(0,0,1,1),new(0,0,1,1),0);
        public Vector4 CurrentOrFull=>Current.Z==0||Current.W==0?new(0,0,1,1):Current;
    }
    public static class FxrTextureAnimation
    {
        public static FxrAtlasFrame Evaluate(JObject appearance,float age,int seed)
        {
            int columns=Math.Clamp((int?)appearance?["columns"]??1,1,4096);
            int frames=Math.Clamp((int?)appearance?["totalFrames"]??1,1,65536);
            int rows=(frames+columns-1)/columns;
            float index=FxrPreviewValue.Get(appearance?["frameIndex"],age,seed)
                +FxrPreviewValue.Get(appearance?["frameIndexOffset"],age,seed);
            if(!float.IsFinite(index))index=0;
            float wrapped=((index%frames)+frames)%frames;
            int current=(int)Math.Floor(wrapped),next=(current+1)%frames;
            Vector4 Cell(int frame)=>new((frame%columns)/(float)columns,(frame/columns)/(float)rows,1f/columns,1f/rows);
            return new(Cell(current),Cell(next),(bool?)appearance?["interpolateFrames"]!=false?wrapped-current:0);
        }
    }
}
