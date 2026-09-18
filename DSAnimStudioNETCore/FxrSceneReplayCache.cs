using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public sealed record FxrSceneReplaySnapshot(FxrPreviewItem[] Items,string[] Notices,int[] OmittedAppearances,bool Limited);

    // Reuses only the immediately preceding frame's evaluation of loader-owned,
    // immutable graphs. GPU drawing always runs. A sample of the current anchor
    // is insufficient for trails: every historical time actually read by Build
    // is checked again with the new frame's AnchorAt delegate before reuse.
    public sealed class FxrSceneReplayCache : IDisposable
    {
        public const int MaxItems=4096,MaxPoseSamples=8192,MaxEntries=256,MaxProfiles=64,MaxNoticeCharacters=262144;
        readonly record struct Key(JObject Root,int EffectId,int Seed,int Age,int End,string Source,Matrix Camera,Matrix Projection);
        readonly record struct PoseSample(int Time,Matrix Pose);
        sealed class Entry
        {
            public Key Key;
            public long Frame;
            public List<PoseSample> Poses;
            public FxrSceneReplaySnapshot Snapshot;
            public int NoticeCharacters;
        }
        sealed class Recorder
        {
            readonly Func<float,Matrix> anchor;
            public readonly List<PoseSample> Poses=new();
            public bool Stable=true;
            public Recorder(Func<float,Matrix> anchor){this.anchor=anchor;}
            public Matrix At(float time)
            {
                var pose=anchor(time);int key=BitConverter.SingleToInt32Bits(time);
                if(!Stable)return pose;
                if(!Finite(pose)||!float.IsFinite(time)){Stable=false;return pose;}
                if(Poses.Count<MaxPoseSamples)Poses.Add(new(key,pose));
                else Stable=false;
                return pose;
            }
        }
        readonly Dictionary<JObject,bool> immutableProfiles=new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<int,Entry> entries=new();
        readonly Dictionary<int,(Key Key,long Frame)> observations=new();
        readonly List<int> unusedSlots=new();
        long frame;
        bool active;
        public int ItemCount {get;private set;}
        public int PoseCount {get;private set;}
        public int NoticeCharacters {get;private set;}
        public int EntryCount=>entries.Count;
        public int ObservationCount=>observations.Count;
        public int ProfileCount=>immutableProfiles.Count;
        public int FrameHits {get;private set;}
        public int FrameBuilds {get;private set;}
        public bool LastReused {get;private set;}

        // Register only after the background loader has completed every graph
        // mutation. Graphs inserted by editors/tests must remain unregistered.
        public bool RegisterImmutableProfile(JObject root)
        {
            if(root==null)return false;
            if(immutableProfiles.ContainsKey(root))return true;
            if(immutableProfiles.Count>=MaxProfiles)return false;
            immutableProfiles.Add(root,FxrPreviewScene.HasForceVolumes(root));return true;
        }
        public bool HasForceVolumes(JObject root)=>immutableProfiles.TryGetValue(root,out bool has)?has:FxrPreviewScene.HasForceVolumes(root);
        public void BeginFrame()
        {
            if(active)EndFrame();
            if(frame==long.MaxValue){Clear();frame=0;}
            frame++;active=true;FrameHits=0;FrameBuilds=0;LastReused=false;
        }
        static bool Finite(Matrix m)=>
            float.IsFinite(m.M11)&&float.IsFinite(m.M12)&&float.IsFinite(m.M13)&&float.IsFinite(m.M14)&&
            float.IsFinite(m.M21)&&float.IsFinite(m.M22)&&float.IsFinite(m.M23)&&float.IsFinite(m.M24)&&
            float.IsFinite(m.M31)&&float.IsFinite(m.M32)&&float.IsFinite(m.M33)&&float.IsFinite(m.M34)&&
            float.IsFinite(m.M41)&&float.IsFinite(m.M42)&&float.IsFinite(m.M43)&&float.IsFinite(m.M44);
        void Remove(int slot)
        {
            if(!entries.Remove(slot,out var entry))return;
            ItemCount-=entry.Snapshot.Items.Length;PoseCount-=entry.Poses.Count;NoticeCharacters-=entry.NoticeCharacters;
        }
        static bool AnchorsMatch(List<PoseSample> poses,Func<float,Matrix> anchor)
        {
            // Preserve repeated reads and their order, not just unique times.
            foreach(var sample in poses)
                if(anchor(BitConverter.Int32BitsToSingle(sample.Time))!=sample.Pose)return false;
            return true;
        }
        public FxrSceneReplaySnapshot Evaluate(int slot,JObject root,FxrPlaybackInstance input,Matrix camera,FxrPreviewScene scene,
            FxrForceSet fields=null,FxrCollisionWorld collisions=null,bool enabled=true,Matrix? projection=null)
        {
            if(!active)throw new InvalidOperationException("BeginFrame must precede FXR replay evaluation.");
            LastReused=false;
            bool eligible=enabled&&slot>=0&&immutableProfiles.TryGetValue(root,out bool ownsForces)&&!ownsForces&&
                (fields==null||fields.Count==0&&!fields.Limited)&&collisions==null&&Finite(camera)&&Finite(projection??Matrix.Identity)&&float.IsFinite(input.Age)&&
                !float.IsNaN(input.EmissionEnd)&&input.AnchorAt!=null;
            var key=new Key(root,input.EffectId,input.Seed,BitConverter.SingleToInt32Bits(input.Age),BitConverter.SingleToInt32Bits(input.EmissionEnd),input.Source,camera,projection??Matrix.Identity);
            bool stableInput=eligible&&observations.TryGetValue(slot,out var observed)&&observed.Frame==frame-1&&ReferenceEquals(observed.Key.Root,root)&&observed.Key==key;
            if(eligible&&(observations.ContainsKey(slot)||observations.Count<MaxEntries))observations[slot]=(key,frame);
            else{observations.Remove(slot);eligible=false;}
            if(eligible&&entries.TryGetValue(slot,out var previous)&&previous.Frame==frame-1&&ReferenceEquals(previous.Key.Root,root)&&previous.Key==key&&AnchorsMatch(previous.Poses,input.AnchorAt))
            {
                previous.Frame=frame;FrameHits++;LastReused=true;return previous.Snapshot;
            }
            Remove(slot);FrameBuilds++;
            // During playback a changing age/key cannot hit next frame. Retain
            // only its lightweight key; record poses after two equal keys so
            // ordinary playing frames do not allocate useless pose histories.
            Recorder recorder=eligible&&stableInput?new(input.AnchorAt):null;
            scene.Build(root,recorder==null?input:input with{AnchorAt=recorder.At},camera,fields,collisions,projection);
            var snapshot=new FxrSceneReplaySnapshot(scene.Items.ToArray(),scene.Notices.ToArray(),scene.OmittedAppearances.ToArray(),scene.Limited);
            if(recorder?.Stable==true&&entries.Count<MaxEntries&&ItemCount+snapshot.Items.Length<=MaxItems&&PoseCount+recorder.Poses.Count<=MaxPoseSamples)
            {
                long chars=0;foreach(var notice in snapshot.Notices)chars+=notice.Length;
                if(chars+NoticeCharacters<=MaxNoticeCharacters)
                {
                    entries.Add(slot,new Entry{Key=key,Frame=frame,Poses=recorder.Poses,Snapshot=snapshot,NoticeCharacters=(int)chars});
                    ItemCount+=snapshot.Items.Length;PoseCount+=recorder.Poses.Count;NoticeCharacters+=(int)chars;
                }
            }
            return snapshot;
        }
        public void EndFrame()
        {
            if(!active)return;
            unusedSlots.Clear();
            foreach(var pair in entries)if(pair.Value.Frame!=frame)unusedSlots.Add(pair.Key);
            foreach(int slot in unusedSlots)Remove(slot);
            unusedSlots.Clear();
            foreach(var pair in observations)if(pair.Value.Frame!=frame)unusedSlots.Add(pair.Key);
            foreach(int slot in unusedSlots)observations.Remove(slot);
            unusedSlots.Clear();active=false;
        }
        public void Clear()
        {
            entries.Clear();observations.Clear();immutableProfiles.Clear();unusedSlots.Clear();ItemCount=0;PoseCount=0;NoticeCharacters=0;
            FrameHits=0;FrameBuilds=0;LastReused=false;active=false;
        }
        public void Dispose()=>Clear();
    }
}
