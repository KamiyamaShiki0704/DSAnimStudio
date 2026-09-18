using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using SoulsAssetPipeline;
using DSAnimStudio.TaeEditor;

namespace DSAnimStudio
{
    public sealed record TaeFxrSpec(int EffectId,int DummyId,int Source,bool Follow,bool IgnoreAngle,
        bool Restrict,bool EventDuration,int BladeTip=-1);

    // Resolve names from the game's actual TAE template. In particular, floor
    // events use a short dummy ID, and modern 114/115 do not contain an SFX ID.
    public static class TaeFxrResolver
    {
        public static object Field(DSAProj.Action action,string name)
        {
            var p=action.Parameters;
            if(p?.Template!=null)
                for(int i=0;i<p.Template.Count;i++)if(p.Template[i].Name==name)return p[i];
            return action.HasInternalSimField(name)?action.ReadInternalSimField(name):null;
        }
        public static int Int(DSAProj.Action a,string name,int fallback=0)
            => Field(a,name) is object value?Convert.ToInt32(value):fallback;
        public static bool TryResolve(DSAProj.Action a,zzz_ParamManagerIns parameters,out TaeFxrSpec spec,out string notice)
        {
            spec=null;notice=null;
            string name=a.Parameters?.Template?.Name??"";
            bool recognized=(a.GetInternalSimTypeName()?.StartsWith("FFX__")==true || name.Contains("FFX"));
            if(!recognized)return false;
            if(a.Type is 117 or 123) {notice=$"TAE {a.Type}: indirect effect lookup is not connected.";return false;}
            int id=Int(a,"FFXID",-1);
            if(a.Type==112 && !FloorFxrResolver.TryResolve(parameters,Main.Config.FxrPreview_FloorMaterial,id,out id,out notice))return false;
            if(a.Type is 114 or 115 && Field(a,"ParamType")!=null)
            {
                if(Int(a,"ParamType")!=0) {notice=$"TAE {a.Type}: Goods casting requires an equipped Goods selection.";return false;}
                if(!MagicBulletResolver.TryResolveSfx(parameters,Main.Config.BulletPreview_MagicID,Int(a,"SFXIndexID"),out id,out notice))return false;
            }
            if(a.Type==120) {id=Int(a,"FFXID_ForAll",-1);notice="TAE 120: common role effect only; host / multiplayer role effects are not inferred.";}
            if(a.Type==122 && Int(a,"NeedsSpEffectID",Int(a,"SpEffectID",-1))>=0)
            {notice="TAE 122: conditional SpEffect trigger is not connected.";return false;}
            if(id<0)return false;
            // Restrict signals source termination when the event ends. Until the
            // native external-value state transitions are implemented, stop new
            // emissions there and let authored particle lifetimes finish.
            bool duration=a.Type is 118 or 119 || Int(a,"IsRestrictToDummyPoly")!=0;
            spec=new(id,Int(a,a.Type==118?"DummyPolyBladeBaseID":"DummyPolyID",-1),Int(a,"DummyPolySource"),
                Int(a,"IsFollowDummyPoly",duration?1:0)!=0,Int(a,"IsIgnoreDummyPolyAngle")!=0,
                Int(a,"IsRestrictToDummyPoly")!=0,duration,Int(a,"DummyPolyBladeTipID",-1));
            if(a.Type==118)notice="Blade FXR follows its base and tip orientation; native blade contact and ribbon topology remain partial.";
            if(Int(a,"ExtraSpawnCondition")!=0)notice="TAE extra spawn conditions use preview state; native equipment / world conditions are not evaluated.";
            return true;
        }
        public static List<Matrix> Anchors(Model model,TaeFxrSpec spec)
        {
            var manager=model.DummyPolyMan;
            if(spec.Source is 1 or 2)
            {
                if(model.ChrAsm==null)return new();
                var style=model.ChrAsm.WeaponStyle;
                var source=spec.Source==1
                    ? style is NewChrAsm.WeaponStyleType.OneHandTransformedL or NewChrAsm.WeaponStyleType.LeftBoth?ParamData.AtkParam.DummyPolySource.LeftWeapon2:ParamData.AtkParam.DummyPolySource.LeftWeapon0
                    : style is NewChrAsm.WeaponStyleType.OneHandTransformedR or NewChrAsm.WeaponStyleType.RightBoth?ParamData.AtkParam.DummyPolySource.RightWeapon2:ParamData.AtkParam.DummyPolySource.RightWeapon0;
                manager=model.ChrAsm.GetDummyManager(source);
            }
            else if(spec.Source!=0)return new();
            else if(spec.DummyId>=0)manager=model.ChrAsm?.GetDummyPolySpawnPlace(ParamData.AtkParam.DummyPolySource.BaseModel,spec.DummyId,manager)??manager;
            if(manager==null)return new();
            if(spec.DummyId<0)return spec.Restrict?new():new(){Matrix.CreateScale(-1,1,-1)*model.CurrentTransform.WorldMatrix};
            int local=manager==model.DummyPolyMan?spec.DummyId:spec.DummyId%1000;
            if(!manager.NewCheckDummyPolyExists(local))return new();
            var result=manager.GetDummyMatricesByID(local,true).Take(16).ToList();
            if(spec.BladeTip>=0 && manager.NewCheckDummyPolyExists(spec.BladeTip))
            {
                var tips=manager.GetDummyMatricesByID(spec.BladeTip,true);
                for(int i=0;i<result.Count && i<tips.Count;i++)
                {
                    var direction=BulletMath.Unit(tips[i].Translation-result[i].Translation,result[i].Forward);
                    result[i]=Matrix.CreateWorld(result[i].Translation,direction,Math.Abs(Vector3.Dot(direction,Vector3.Up))>.99f?Vector3.Right:Vector3.Up);
                }
            }
            // DSA's CreateWorld dummy frame maps -Z to FLVER.Dummy.Forward.
            // FXR uses +Z as local forward. Convert at this boundary, once;
            // keep the translation/up axis and the shared dummy/bullet data intact.
            for(int i=0;i<result.Count;i++)result[i]=Matrix.CreateScale(-1,1,-1)*result[i];
            return result;
        }
    }

    // Shared animation/dummy sampling for one effect instance. Sampling recorded
    // transforms makes pause and arrow rewind deterministic without advancing FXR.
    public sealed class FxrAnchorHistory
    {
        readonly List<(float Time,Matrix[] Poses)> samples=new();
        public int Count=>samples.Count;
        public float FirstTime=>samples.Count==0?float.PositiveInfinity:samples[0].Time;
        public void Clear()=>samples.Clear();
        public void Add(float time,IEnumerable<Matrix> poses)
        {
            var values=poses.Take(16).ToArray();if(values.Length==0)return;
            int at=samples.BinarySearch((time,values),Comparer<(float Time,Matrix[] Poses)>.Create((a,b)=>a.Time.CompareTo(b.Time)));
            if(at>=0)samples[at]=(time,values);
            else samples.Insert(~at,(time,values));
            if(samples.Count>4096)samples.RemoveAt(0);
        }
        public int PoseCount=>samples.Count==0?0:samples[^1].Poses.Length;
        public Matrix At(float time,int index,bool ignoreAngle)
        {
            if(samples.Count==0)return Matrix.Identity;
            int at=samples.BinarySearch((time,null),Comparer<(float Time,Matrix[] Poses)>.Create((a,b)=>a.Time.CompareTo(b.Time)));
            if(at<0)at=~at;
            Matrix Read(int i)=>samples[i].Poses[Math.Min(index,samples[i].Poses.Length-1)];
            var value=Read(Math.Min(at,samples.Count-1));
            if(at>0 && at<samples.Count)
            {
                float blend=Math.Clamp((time-samples[at-1].Time)/Math.Max(1e-6f,samples[at].Time-samples[at-1].Time),0,1);
                var before=Read(at-1);
                if(before.Decompose(out var sa,out var qa,out var pa) && value.Decompose(out var sb,out var qb,out var pb))
                    value=Matrix.CreateScale(Vector3.Lerp(sa,sb,blend))*Matrix.CreateFromQuaternion(Quaternion.Slerp(qa,qb,blend))*Matrix.CreateTranslation(Vector3.Lerp(pa,pb,blend));
            }
            return ignoreAngle?Matrix.CreateTranslation(value.Translation):value;
        }
    }

    public sealed class TaeFxrPreview
    {
        sealed class Track
        {
            public TaeFxrSpec Spec;
            public DSAProj.Action Action;
            public NewHavokAnimation Animation;
            public Func<List<Matrix>> Anchors;
            public FxrAnchorHistory History=new();
            public int Loop,Seed,Seen;
            public float Start,End;
        }
        readonly object sync=new();
        readonly Dictionary<(DSAProj.Action,NewHavokAnimation),Track> tracks=new();
        readonly HashSet<string> notices=new();
        int frame,serial;
        public string Status {get;private set;}="Animation FXR: enable preview, then play or scrub an animation.";
        public string Warning {get {lock(sync)return string.Join("\n",notices.Take(12));}}
        void Notice(string value){if(value!=null && notices.Count<32)notices.Add(value);}
        public void Reset(){lock(sync){tracks.Clear();notices.Clear();serial=0;Status="Animation FXR: no recorded effects.";}}
        public void BeginFrame(){lock(sync){frame++;if(!Main.Config.BulletPreview_FxrEnabled || !Main.Config.FxrPreview_AnimationEvents)Reset();}}
        public void Register(DSAProj.Action action,NewHavokAnimation animation,Model model)
        {
            if(!Main.Config.BulletPreview_FxrEnabled || !Main.Config.FxrPreview_AnimationEvents || !action.IsActive || model==null)return;
            if(FxrPreviewDecoder.GameName(model.Document.GameRoot.GameType)==null)
            {lock(sync)Notice("This game's FXR format is not connected (ER, NR, DS3, Sekiro, AC6 supported).");return;}
            lock(sync)
            {
                if(!TaeFxrResolver.TryResolve(action,model.Document.ParamManager,out var spec,out var notice)) {Notice(notice);return;}
                Notice(notice);
                Register(action,animation,spec,()=>TaeFxrResolver.Anchors(model,spec));
            }
        }
        public void Register(DSAProj.Action action,NewHavokAnimation animation,TaeFxrSpec spec,Func<List<Matrix>> anchors)
        {
            lock(sync)
            {
                var key=(action,animation);
                if(!tracks.TryGetValue(key,out var t))
                {
                    if(tracks.Count>=128){Notice("Animation FXR event budget reached (128). Solo a smaller event selection.");return;}
                    tracks[key]=t=new(){Action=action,Animation=animation,Seed=++serial};
                }
                if(t.Spec!=spec || t.Start!=action.StartTime || t.End!=action.EndTime || t.Loop!=animation.LoopCount)t.History.Clear();
                t.Spec=spec;t.Start=action.StartTime;t.End=action.EndTime;t.Anchors=anchors;t.Loop=animation.LoopCount;t.Seen=frame;
                t.History.Add(animation.CurrentTime,anchors());
            }
        }
        public void SampleAfterPose()
        {
            lock(sync)
            {
                foreach(var key in tracks.Where(k=>k.Value.Seen!=frame).Select(k=>k.Key).ToArray())tracks.Remove(key);
                foreach(var t in tracks.Values)
                {
                    if(t.Loop!=t.Animation.LoopCount){t.History.Clear();t.Loop=t.Animation.LoopCount;}
                    var poses=t.Anchors();t.History.Add(t.Animation.CurrentTime,poses);
                    if(poses.Count==0)Notice($"SFX {t.Spec.EffectId}: dummy {t.Spec.DummyId}, source {t.Spec.Source} is missing; attachment omitted.");
                    if(t.Animation.CurrentTime>=t.Start && t.History.FirstTime>t.Start+.035f)
                        Notice("Seek before recorded attachment history: using earliest sampled pose. Replay from start for accurate trails / frozen spawn positions.");
                }
                Status=$"Animation FXR: {tracks.Count} event(s), {tracks.Values.Count(t=>t.Animation.CurrentTime>=t.Start)} started. Pause / arrow rewind use recorded attachment poses.";
            }
        }
        public IEnumerable<FxrPlaybackInstance> Instances()
        {
            lock(sync)
            {
                var list=new List<FxrPlaybackInstance>();
                foreach(var t in tracks.Values)
                {
                    float age=t.Animation.CurrentTime-t.Start;if(age<0)continue;
                    float end=t.Spec.EventDuration?Math.Max(0,t.End-t.Start):float.PositiveInfinity;
                    for(int i=0;i<t.History.PoseCount;i++)
                    {
                        int index=i;
                        Matrix Anchor(float a) {lock(sync)return t.History.At(t.Start+(t.Spec.Follow?Math.Min(a,end):0),index,t.Spec.IgnoreAngle);}
                        list.Add(new(t.Spec.EffectId,unchecked(t.Seed*397+i),age,end,Anchor,$"TAE {t.Action.Type}"));
                    }
                }
                return list;
            }
        }
    }
}
