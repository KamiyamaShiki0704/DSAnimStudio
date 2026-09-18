using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SoulsFormats;

namespace DSAnimStudio
{
    // Share immutable source bytes between live FXR resources. Weak values do
    // not keep a previous character's textures alive after its renderer closes.
    // Physical path + file revision prevent cross-game/mod or stale-file reuse.
    public static class FxrAssetReuseCache
    {
        public readonly record struct Revision(string Path,long Length,long WriteTicks);
        sealed class Entry
        {
            public long Used;
            public string[] Names;
            public readonly Dictionary<string,WeakReference<byte[]>> Bytes=new(StringComparer.OrdinalIgnoreCase);
        }
        static readonly object sync=new();
        static readonly Dictionary<Revision,Entry> entries=new();
        static readonly ConditionalWeakTable<byte[],Dictionary<string,byte[]>> textures=new();
        static long clock;
        const int MaxFiles=96,MaxAssetsPerFile=1024;
        public static Revision Stamp(string path)
        {
            var file=new FileInfo(path);
            return new(file.FullName.ToUpperInvariant(),file.Length,file.LastWriteTimeUtc.Ticks);
        }
        static Entry Touch(Revision revision)
        {
            if(!entries.TryGetValue(revision,out var entry))
            {
                if(entries.Count>=MaxFiles)entries.Remove(entries.MinBy(e=>e.Value.Used).Key);
                entries[revision]=entry=new();
            }
            entry.Used=++clock;return entry;
        }
        public static string[] Index(Revision revision)
        {lock(sync)return entries.TryGetValue(revision,out var entry)?entry.Names:null;}
        public static void Index(Revision revision,IEnumerable<string> names)
        {lock(sync){var entry=Touch(revision);entry.Names??=names.Select(n=>Path.GetFileName(n.Replace('\\','/'))).ToArray();}}
        public static byte[] Find(Revision revision,string name)
        {
            lock(sync)
            {
                var entry=Touch(revision);
                return entry.Bytes.TryGetValue(name,out var weak)&&weak.TryGetTarget(out var data)?data:null;
            }
        }
        public static byte[] Share(Revision revision,string name,byte[] data)
        {
            if(data==null)return null;
            lock(sync)
            {
                var entry=Touch(revision);
                if(entry.Bytes.TryGetValue(name,out var weak)&&weak.TryGetTarget(out var existing))return existing;
                if(entry.Bytes.Count>=MaxAssetsPerFile)
                {
                    foreach(var dead in entry.Bytes.Where(e=>!e.Value.TryGetTarget(out _)).Select(e=>e.Key).ToArray())entry.Bytes.Remove(dead);
                    if(entry.Bytes.Count>=MaxAssetsPerFile)entry.Bytes.Remove(entry.Bytes.Keys.First());
                }
                entry.Bytes[name]=new(data);return data;
            }
        }
        public static byte[] Texture(FxrPreviewAssets.Asset asset,string name,string legacy)
        {
            if(asset==null)return null;
            if(!asset.Name.EndsWith(".tpf",StringComparison.OrdinalIgnoreCase))return asset.Bytes;
            string key=asset.Name+"#dds:"+name;
            if(asset.Revision is Revision before&&Find(before,key) is byte[] existing)return existing;
            var decoded=textures.GetValue(asset.Bytes,bytes=>TPF.Read(bytes).Textures
                .GroupBy(t=>t.Name,StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g=>g.Key,g=>g.First().Bytes,StringComparer.OrdinalIgnoreCase));
            var result=decoded.GetValueOrDefault(name)??decoded.GetValueOrDefault(legacy);
            return asset.Revision is Revision revision?Share(revision,key,result):result;
        }
    }
}
