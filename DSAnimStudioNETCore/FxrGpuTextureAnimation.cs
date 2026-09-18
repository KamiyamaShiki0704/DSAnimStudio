using System;
using System.Buffers;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public static class FxrGpuTextureAnimation
    {
        public sealed class Curve : IDisposable
        {
            Vector2[] points;
            readonly int count,steps,columns,frames,rows,counterWhenMissing;
            readonly float life;
            public Curve(JObject app,float life,int seed)
            {
                this.life=life;
                columns=Math.Clamp((int?)app["columns"]??1,1,4096);frames=Math.Clamp((int?)app["totalFrames"]??1,1,65536);rows=Math.Max(1,frames/columns);
                // ER 141D04B83..141D04D1C: next power of two above the 30 Hz
                // unmultiplied duration, at most 2048 rows; sample min(row,steps)/30.
                float duration=FxrPreviewValue.Get(app["particleDuration"],0,seed,1);
                steps=float.IsFinite(duration)?(int)Math.Clamp((double)duration*30,0,int.MaxValue-1):0;
                count=2;while(count<=steps&&count<2048)count*=2;
                points=ArrayPool<Vector2>.Shared.Rent(count);
                Span<float> values=stackalloc float[count];
                for(int i=0;i<count;i++)values[i]=FxrPreviewValue.Get(app["unk_ds3_p1_15"],Math.Min(i,steps)*(1f/30),seed);
                Bake(values,frames,points.AsSpan(0,count));
                int counter=0,previous=0;
                for(int i=0;i<count;i++)
                {
                    int frame=(int)points[i].X;
                    counter=(counter+(frame<previous?frames-previous+frame:frame-previous))&65535;
                    previous=frame;points[i].X=counter;
                }
                counterWhenMissing=(counter+(previous>0?frames-previous:0))&65535;
            }
            public FxrAtlasFrame At(float age,int initialFrame)
            {
                float normalized=life>0&&float.IsFinite(age)?Math.Clamp(age/life,0,1):0;
                int row=(int)Math.Clamp((double)normalized*steps,0,int.MaxValue-1);
                // Prefix counters make each particle query O(1); the table is
                // shared for this emitter build and returned after submission.
                int acc=(initialFrame+(row<count?(int)points[row].X:counterWhenMissing))&65535;
                float blend=row<count?points[row].Y:0;
                int current=acc%frames,nextCell=(current+1)%frames;
                Vector4 Cell(int f)=>new((f%columns)/(float)columns,(f/columns)/(float)rows,1f/columns,1f/rows);
                return new(Cell(current),Cell(nextCell),blend);
            }
            public void Dispose(){if(points!=null){ArrayPool<Vector2>.Shared.Return(points);points=null;}}
        }
        public static Vector4 SampleColor(JToken color,float duration,float life,float age,int seed)
        {
            if(!float.IsFinite(duration)||duration<0||!float.IsFinite(life)||life<=0||!float.IsFinite(age))return Vector4.Zero;
            int steps=(int)Math.Clamp((double)duration*30,0,int.MaxValue-1),count=2;
            while(count<=steps&&count<2048)count*=2;
            int row=(int)Math.Clamp((double)Math.Clamp(age/life,0,1)*steps,0,int.MaxValue-1);
            return row<count?FxrParticleSurface.Vector(color,Math.Min(row,steps)*(1f/30),seed):Vector4.Zero;
        }
        // Original LUT writer 141D05A7B..141D05B99; independently executed in
        // verify-fxr-gpu-atlas-bake.py. Frame changes backfill blend values.
        public static void Bake(ReadOnlySpan<float> values,int frames,Span<Vector2> output)
        {
            if(output.Length<values.Length||frames<1)throw new ArgumentException();
            output[..values.Length].Clear();int previous=0,begin=0;
            for(int i=0;i<values.Length;i++)
            {
                float value=values[i];int rounded=float.IsFinite(value)?unchecked((int)(value+(value<0?-.5f:.5f))):0;
                int frame=Math.Abs(rounded%frames);
                if(frame!=previous||i==values.Length-1)
                {
                    int denominator=i-begin-1;
                    for(int j=begin;j<i;j++)output[j].Y=denominator<=0?1:(j-begin)/(float)denominator;
                    output[i].Y=1;previous=frame;begin=i;
                }
                output[i].X=frame;
            }
        }
        // CS_VFX_StandardParticle: ordinary atlas advances over normalized lifetime,
        // blending adjacent cells and holding the final cell. No arbitrary FPS.
        // Columns/rows follow the original CPU seed upload (integer division).
        public static FxrAtlasFrame Lifetime(int columns,int frames,float age,float life)
        {
            columns=Math.Clamp(columns,1,4096);frames=Math.Clamp(frames,1,65536);
            int rows=Math.Max(1,frames/columns);
            float t=life>0&&float.IsFinite(age)?Math.Clamp(age/life,0,1):0;
            float index=t*Math.Max(1,frames-1);int current=(int)index;
            int next=Math.Min(frames-1,Math.Max(1,current+1));
            Vector4 Cell(int f)=>new((f%columns)/(float)columns,(f/columns)/(float)rows,1f/columns,1f/rows);
            return new(Cell(current),Cell(next),Math.Clamp(index-current,0,1));
        }
    }
}
