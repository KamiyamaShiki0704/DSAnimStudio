using System;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    public static class FxrGpuSparkGeometry
    {
        public const int SegmentCount=10;
        // Points: leading direction point, head, ten historical nodes, final direction point.
        public static void Build(ReadOnlySpan<Vector3> points,Vector3 camera,float halfWidth,float length,Span<FxrRibbonGeometry> segments)
        {
            if(points.Length!=13||segments.Length<SegmentCount)throw new ArgumentException("Spark geometry requires 13 history points and 10 segment slots.");
            if(!float.IsFinite(halfWidth)||!float.IsFinite(length))throw new ArgumentException("Spark geometry sizes must be finite.");
            for(int i=0;i<points.Length;i++)if(!float.IsFinite(points[i].X)||!float.IsFinite(points[i].Y)||!float.IsFinite(points[i].Z))throw new ArgumentException("Spark history must be finite.");
            Vector3 previousSide=Vector3.Zero;float cumulative=0,previousU=0,previousAlpha=1;
            for(int i=1;i<=11;i++)
            {
                var direction=BulletMath.Unit(points[i+1]-points[i-1],Vector3.Backward);
                var view=BulletMath.Unit(camera-points[i],Vector3.Up);
                var side=BulletMath.Unit(Vector3.Cross(direction,view),i>1?BulletMath.Unit(previousSide,Vector3.Right):Vector3.Right)*halfWidth;
                if(i>1)
                {
                    cumulative+=Vector3.Distance(points[i-1],points[i]);
                    float u=length>1e-8f?Math.Clamp(cumulative/length,0,1):1;
                    float alpha=1-u*u*u;
                    segments[i-2]=new(points[i-1]+previousSide,points[i-1]-previousSide,points[i]-side,points[i]+side,previousU,u,0,previousAlpha,alpha);
                    previousU=u;previousAlpha=alpha;
                }
                previousSide=side;
            }
        }
    }
}
