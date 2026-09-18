using System;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    public static class FxrTracerOpacity
    {
        // ER 141CF69F6..6AB4 tests proper intersections of projected adjacent
        // width spans. 141CF6EC0..70C2 fades nearby knots by world distance.
        // Re-evaluate on camera changes instead of emulating the native timer.
        public static void Evaluate(ReadOnlySpan<Vector3> left,ReadOnlySpan<Vector3> right,Matrix viewProjection,float radius,Span<float> alpha)
        {
            int count=left.Length;
            if(count!=right.Length||alpha.Length<count||count>513)throw new ArgumentException("Invalid tracer knot count.");
            alpha=alpha[..count];alpha.Fill(1);
            Span<Vector2> a=stackalloc Vector2[count],b=stackalloc Vector2[count];
            Span<bool> valid=stackalloc bool[count],fold=stackalloc bool[count];fold.Clear();
            for(int i=0;i<count;i++)valid[i]=Project(left[i],viewProjection,out a[i])&&Project(right[i],viewProjection,out b[i]);
            for(int i=0;i<count-2;i++)
                if(valid[i]&&valid[i+1]&&Crosses(a[i],b[i],a[i+1],b[i+1])){fold[i]=true;alpha[i]=0;}
            if(!float.IsFinite(radius)||radius<=0)return;
            for(int i=0;i<count;i++)if(fold[i])
            {
                var center=(left[i]+right[i])*.5f;
                for(int direction=-1;direction<=1;direction+=2)
                    for(int j=i+direction;j>=0&&j<count;j+=direction)
                    {
                        if(alpha[j]<=0)break;
                        float distance=Vector3.Distance(center,(left[j]+right[j])*.5f)/radius;
                        if(!float.IsFinite(distance)||distance>=1)break;
                        alpha[j]=Math.Min(alpha[j],distance);
                    }
            }
        }
        static bool Project(Vector3 p,Matrix matrix,out Vector2 result)
        {
            var clip=Vector4.Transform(new Vector4(p,1),matrix);
            result=new(clip.X/clip.W,clip.Y/clip.W);
            // Do not invent screen crossings across the eye plane or a
            // singular projection. The normal renderer handles clipping.
            return clip.W>1e-6f&&float.IsFinite(result.X)&&float.IsFinite(result.Y);
        }
        public static bool Crosses(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
        {
            static double Side(Vector2 v,Vector2 w)=>(double)v.X*w.Y-(double)v.Y*w.X;
            return Side(b-a,c-a)*Side(b-a,d-a)<0&&Side(d-c,a-c)*Side(d-c,b-c)<0;
        }
    }
}
