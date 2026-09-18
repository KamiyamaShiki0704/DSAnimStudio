using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    // Integrate parent deltas, not absolute positions: a changing follow factor
    // must not pull an already detached trail back to its birth point. Rebuilt
    // deterministically with the scene, with memoized samples for ribbon queries.
    public sealed class FxrPartialFollow
    {
        readonly Func<float,Matrix> parentAt;
        readonly Func<float,float> factorAt;
        readonly bool followRotation;
        readonly float begin;
        const float Step=1f/60;
        readonly List<(Matrix Parent,Matrix Followed)> samples=new();
        public bool Limited {get;private set;}
        public FxrPartialFollow(Func<float,Matrix> parentAt,float begin,Func<float,float> factorAt,bool followRotation)
        {
            this.parentAt=parentAt;this.begin=begin;this.factorAt=factorAt;this.followRotation=followRotation;
            var initial=parentAt(begin);samples.Add((initial,initial));
        }
        Matrix Advance(Matrix previous,Matrix result,Matrix next,float factor)
        {
            var position=result.Translation+(next.Translation-previous.Translation)*factor;
            if(followRotation && previous.Decompose(out _,out var a,out _) && next.Decompose(out _,out var b,out _)
                && result.Decompose(out var scale,out var rotation,out _))
            {
                var delta=Quaternion.Inverse(a)*b;
                rotation=Quaternion.Normalize(rotation*Quaternion.Slerp(Quaternion.Identity,delta,factor));
                result=Matrix.CreateScale(scale)*Matrix.CreateFromQuaternion(rotation);
            }
            result.Translation=position;return result;
        }
        public Matrix At(float time)
        {
            float age=Math.Max(0,time-begin);
            if(age>4096*Step){Limited=true;age=4096*Step;}
            int index=(int)Math.Floor(age/Step);
            while(samples.Count<=index)
            {
                int n=samples.Count;var last=samples[^1];var parent=parentAt(begin+n*Step);
                samples.Add((parent,Advance(last.Parent,last.Followed,parent,factorAt((n-.5f)*Step))));
            }
            var sample=samples[index];
            if(age-index*Step<1e-6f)return sample.Followed;
            return Advance(sample.Parent,sample.Followed,parentAt(begin+age),factorAt((index*Step+age)*.5f));
        }
    }
}
