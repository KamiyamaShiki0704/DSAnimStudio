using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SoulsFormats;

namespace DSAnimStudio
{
    // Used only by the background loader. Retain one decompressed binder, not an
    // entire game's SFX bundles. Header indexes survive eviction of that binder.
    public sealed class FxrPreviewAssets : IDisposable
    {
        public sealed record Asset(byte[] Bytes, string Owner, string Name,FxrAssetReuseCache.Revision? Revision=null);
        readonly string[] roots;
        readonly string character;
        readonly CancellationToken cancellation;
        readonly Dictionary<string, HashSet<string>> indexes = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string[]> directories = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string[]> packedFiles = new(StringComparer.OrdinalIgnoreCase);
        BND4Reader reader;
        string readerPath;
        public FxrPreviewAssets(string[] roots, string character, CancellationToken cancellation)
        { this.roots=roots; this.character=character??""; this.cancellation=cancellation; }
        int Priority(string path, string owner)
        {
            if(string.Equals(path,owner,StringComparison.OrdinalIgnoreCase))return 0;
            var name=Path.GetFileName(path);
            if(character.Length>0 && name.Contains("_"+character, StringComparison.OrdinalIgnoreCase))return 1;
            return name.Contains("commoneffects",StringComparison.OrdinalIgnoreCase)?2:3;
        }
        public Asset Read(string name, string owner=null)=>ReadAny(new[]{name},owner);
        public Asset ReadAny(string[] names, string owner=null)
        {
            // Never interpret asset names from the FXR as filesystem paths.
            names=names.Select(n=>Path.GetFileName(n.Replace('\\','/'))).ToArray();
            foreach(var root in roots)
            {
                cancellation.ThrowIfCancellationRequested();
                var dir=Path.Combine(root,"sfx"); if(!Directory.Exists(dir))continue;
                if(!directories.TryGetValue(dir,out var loose))
                    directories[dir]=loose=Directory.GetFiles(dir,"*",SearchOption.AllDirectories)
                        .Where(p=>!p.EndsWith(".dcx",StringComparison.OrdinalIgnoreCase)).ToArray();
                var file=loose.Where(p=>names.Contains(Path.GetFileName(p),StringComparer.OrdinalIgnoreCase))
                    .OrderBy(p=>Priority(Owner(p,dir),owner)).ThenBy(p=>p,StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if(!packedFiles.TryGetValue(dir,out var packed))
                    packedFiles[dir]=packed=Directory.GetFiles(dir,"*",SearchOption.TopDirectoryOnly)
                        .Where(p=>p.EndsWith(".ffxbnd.dcx",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".ffxbnd",StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach(var path in packed.Concat(file==null?Array.Empty<string>():new[]{file}).OrderBy(p=>Priority(p==file?Owner(p,dir):p,owner)).ThenBy(p=>p,StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var revision=FxrAssetReuseCache.Stamp(path);
                    if(path==file)
                    {
                        string name=Path.GetFileName(file);
                        var bytes=FxrAssetReuseCache.Find(revision,name)??FxrAssetReuseCache.Share(revision,name,File.ReadAllBytes(file));
                        return new(bytes,Owner(file,dir),name,revision);
                    }
                    var sharedIndex=FxrAssetReuseCache.Index(revision);
                    if(sharedIndex!=null)
                    {
                        string match=sharedIndex.FirstOrDefault(n=>names.Contains(n,StringComparer.OrdinalIgnoreCase));
                        if(match==null)continue;
                        var cached=FxrAssetReuseCache.Find(revision,match);
                        if(cached!=null)return new(cached,path,match,revision);
                    }
                    if(indexes.TryGetValue(path,out var index)&&!names.Any(index.Contains))continue;
                    try
                    {
                        if(readerPath!=path)
                        {
                            reader?.Dispose();reader=null;readerPath=null;
                            reader=new BND4Reader(path);readerPath=path;
                        }
                        indexes[path]=new(reader.Files.Select(f=>Path.GetFileName(f.Name)),StringComparer.OrdinalIgnoreCase);
                        FxrAssetReuseCache.Index(revision,indexes[path]);
                        var header=reader.Files.FirstOrDefault(f=>names.Contains(Path.GetFileName(f.Name),StringComparer.OrdinalIgnoreCase));
                        if(header!=null)
                        {
                            string name=Path.GetFileName(header.Name);
                            return new(FxrAssetReuseCache.Share(revision,name,reader.ReadFile(header)),path,name,revision);
                        }
                    }
                    catch(InvalidDataException) { indexes[path]=new(StringComparer.OrdinalIgnoreCase); }
                }
            }
            return null;
        }
        static string Owner(string path,string dir)
        {
            string relative=Path.GetRelativePath(dir,path);
            var first=relative.Split(Path.DirectorySeparatorChar)[0];
            return Path.Combine(dir,first);
        }
        readonly Dictionary<string,byte[]> materialDefinitions=new(StringComparer.OrdinalIgnoreCase);
        public byte[] MaterialDefinition(string material,bool modern)
        {
            string name=Path.GetFileNameWithoutExtension(material?.Replace('\\','/')??"")+(modern?".matbin":".mtd");
            if(materialDefinitions.TryGetValue(name,out var result))return result;
            if(materialDefinitions.Count>=128)return null;
            foreach(string root in roots)
            {
                string dir=Path.Combine(root,modern?"material":"mtd");if(!Directory.Exists(dir))continue;
                cancellation.ThrowIfCancellationRequested();
                var loose=Directory.EnumerateFiles(dir,"*",SearchOption.AllDirectories).FirstOrDefault(p=>string.Equals(Path.GetFileName(p),name,StringComparison.OrdinalIgnoreCase));
                if(loose!=null)return materialDefinitions[name]=File.ReadAllBytes(loose);
                foreach(string path in Directory.EnumerateFiles(dir,"*",SearchOption.TopDirectoryOnly).Where(p=>p.EndsWith("bnd",StringComparison.OrdinalIgnoreCase)||p.EndsWith("bnd.dcx",StringComparison.OrdinalIgnoreCase)).OrderByDescending(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase))
                {
                    cancellation.ThrowIfCancellationRequested();
                    try
                    {
                        using var binder=new BND4Reader(path);
                        var entry=binder.Files.FirstOrDefault(f=>string.Equals(Path.GetFileName(f.Name.Replace('\\','/')),name,StringComparison.OrdinalIgnoreCase));
                        if(entry!=null)return materialDefinitions[name]=binder.ReadFile(entry);
                    }
                    catch(InvalidDataException){ }
                }
            }
            return materialDefinitions[name]=null;
        }
        public void Dispose() { reader?.Dispose();reader=null;readerPath=null;materialDefinitions.Clear(); }
    }
}
