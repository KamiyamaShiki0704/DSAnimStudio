using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    // GS_VFX_StandardParticle: stretch between two simulation poses, not a
    // historical ribbon. Keep its narrow tail and connected head strip.
    public sealed record FxrGpuTraceGeometry(Vector3 HeadA,Vector3 HeadB,Vector3 TailA,Vector3 TailB,
        float TailAlpha,bool Head,Matrix HeadTransform,bool SeparateHead=false)
    {
        public int VertexCount=>Head?(SeparateHead?12:18):6;
        public static FxrGpuTraceGeometry Create(Vector3 current,Vector3 previous,Vector3 camera,
            float width,float height,float length,float threshold,float opacity,bool head,Matrix billboard,bool modern=false)
        {
            var delta=current-previous;float distance=delta.Length();
            if(!float.IsFinite(distance)||!float.IsFinite(threshold)||distance<=Math.Max(1e-7f,threshold+(modern?.001f:0))||!float.IsFinite(width+height+length+opacity))return null;
            var direction=delta/distance;float radius=(width+height)*.25f;
            var front=head?current:current+direction*radius;
            var tail=modern?current-direction*(radius+Math.Max(distance,.01f)*length):previous-direction*(radius*length);
            Vector3 Side(Vector3 p){var side=Vector3.Cross(front-tail,camera-p);float norm=side.Length();return norm>1e-7f?side/norm:Vector3.Zero;}
            var a=Side(front)*radius;var b=Side(tail)*(radius*.5f);
            return new(front+a,front-a,tail+b,tail-b,head?radius/Math.Max(distance,.01f)*opacity:opacity,head,billboard,modern);
        }
        public void StripVertex(int index,out Vector3 position,out Vector2 uv,out float alpha)
        {
            alpha=index is 2 or 3?TailAlpha:1;
            if(index<4)
            {
                position=index switch{0=>HeadA,1=>HeadB,2=>TailA,_=>TailB};
                uv=new(index<2?(Head?.5f:1):0,index%2==0?1:0);
            }
            else
            {
                int i=index-4;position=Vector3.Transform(new Vector3(i%2==0?-.5f:.5f,i<2?-.5f:.5f,0),HeadTransform);
                uv=new(i%2,i<2?1:0);
            }
        }
        public void WriteVertices(Span<VertexPositionColorTexture> vertices)
        {
            if(vertices.Length<VertexCount)throw new ArgumentException("GPU trace output span is too short.",nameof(vertices));
            int output=0;
            for(int triangle=0;triangle<VertexCount/3;triangle++)for(int corner=0;corner<3;corner++)
            {
                int t=SeparateHead&&triangle>=2?triangle+2:triangle;
                int i=corner==2?t+2:t+(t%2==0?corner:1-corner);
                StripVertex(i,out var position,out var uv,out var alpha);
                vertices[output++]=new(position,new Color(1f,1f,1f,alpha),uv);
            }
        }
    }
}
