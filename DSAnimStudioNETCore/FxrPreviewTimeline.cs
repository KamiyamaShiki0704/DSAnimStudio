using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // A bounded, deterministic schedule. Only time, literal and the preview's
    // termination signal are available; missing external state is never guessed.
    public sealed class FxrPreviewTimeline
    {
        public sealed record Phase(int State,float Start,float End);
        public readonly List<Phase> Phases=new();
        public readonly HashSet<string> Notices=new();
        public float Termination=float.PositiveInfinity;
        public bool Limited;
        public static FxrPreviewTimeline Build(JArray states,float now,float stop)
        {
            var result=new FxrPreviewTimeline();
            if(states==null || states.Count==0){result.Phases.Add(new(0,0,float.PositiveInfinity));return result;}
            int state=0;float start=0;
            for(int step=0;step<128;step++)
            {
                if(state<0 || state>=states.Count){result.Termination=start;return result;}
                float next=float.PositiveInfinity;int destination=-1;
                foreach(var c in (states[state] as JArray??new JArray()).OfType<JObject>())
                {
                    if((int?)c["unk1"]!=2){result.Notices.Add("FXR condition flag is not supported; branch condition skipped.");continue;}
                    int lt=(int?)c["leftOperandType"]??0,rt=(int?)c["rightOperandType"]??0;
                    float lv=(float?)c["leftOperandValue"]??0,rv=(float?)c["rightOperandValue"]??0;
                    bool Known(int type,float value)=>type is -4 or -1 || type==-3 && value==0;
                    if(!Known(lt,lv)||!Known(rt,rv)){result.Notices.Add("FXR reference/state needs an unavailable external value; condition skipped.");continue;}
                    float Value(int type,float value,float t)=>type==-1?t-start:type==-3?(t>=stop?1:0):value;
                    bool True(float t)
                    {
                        float a=Value(lt,lv,t),b=Value(rt,rv,t);
                        return (int?)c["operator"] switch {0=>a==b,1=>a!=b,2=>a<b,3=>a<=b,4=>a>b,5=>a>=b,_=>false};
                    }
                    var candidates=new SortedSet<float>{start,MathF.BitIncrement(start)};
                    void Add(float t){if(float.IsFinite(t)&&t>=start){candidates.Add(t);candidates.Add(MathF.BitIncrement(t));}}
                    Add(stop);
                    if(lt==-1&&rt==-4)Add(start+rv);
                    if(rt==-1&&lt==-4)Add(start+lv);
                    if(lt==-1&&rt==-3 || rt==-1&&lt==-3){Add(start);Add(start+1);}
                    foreach(float t in candidates)
                        if(t<=now&&True(t)){if(t<next){next=t;destination=(int?)c["state"]??-1;}break;}
                }
                result.Phases.Add(new(state,start,next));
                if(!float.IsFinite(next))return result;
                start=next;state=destination;
            }
            result.Limited=true;result.Termination=start;
            result.Notices.Add("FXR state transition budget reached (128); possible zero-time state cycle.");
            return result;
        }
        public IEnumerable<(int Config,float Start,float End)> Activations(JArray map,float birth,float end)
        {
            int previous=-2;float from=0,to=0;
            foreach(var phase in Phases)
            {
                int config=(int?)(phase.State< (map?.Count??0)?map[phase.State]:map?.First)??0;
                float begin=Math.Max(birth,phase.Start),finish=Math.Min(end,phase.End);
                if(finish<=begin)continue;
                if(config==previous && begin==to){to=finish;continue;}
                if(previous>=0)yield return(previous,from,to);
                previous=config;from=begin;to=finish;
            }
            if(previous>=0)yield return(previous,from,to);
        }
    }
}
