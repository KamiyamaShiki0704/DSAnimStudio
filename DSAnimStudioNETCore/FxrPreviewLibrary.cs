using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SoulsFormats;
using SoulsAssetPipeline;

namespace DSAnimStudio
{
    public sealed record FxrPreviewReference(int Parent,int Child,string Status);
    public sealed record FxrPreviewResource(JObject Root, Dictionary<string,byte[]> Assets, string[] Notices,FxrPreviewReference[] References=null,
        Dictionary<string,FxrMaterialProfile[]> Materials=null,
        Dictionary<string,List<(FxrModelVertex[] Vertices,FxrMaterialProfile Material)>> Models=null,
        Dictionary<(string Model,int Binder),FxrModelAnimation> Animations=null);
    public static class FxrPreviewLibrary
    {
        // Resource preparation, decompression and parsing never run on the GPU thread.
        static readonly SemaphoreSlim loaders=new(2);
        public static async Task<FxrPreviewResource> Load(int id,string[] roots,string character, SoulsGames game,CancellationToken cancellation)
        {
            await loaders.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                return await Task.Run(async ()=>
                {
                    if(FxrPreviewDecoder.GameName(game)==null)throw new NotSupportedException("This game's FXR format is not implemented (supported: ER, NR, DS3, Sekiro, AC6).");
                    using var source=new FxrPreviewAssets(roots,character,cancellation);
                    var fxr=source.Read($"f{id:D9}.fxr")??throw new FileNotFoundException("FXR not found in unpacked sfx files or ffxbnd bundles. Extract the game's sfx archives if necessary.");
                    var profile=await FxrPreviewDecoder.Decode(fxr.Bytes,game,cancellation);
                    if((int?)profile["Id"]!=id)throw new InvalidDataException("FXR internal ID does not match its reference.");
                    var root=(JObject)profile["Root"].DeepClone();root["_previewStates"]=profile["States"]?.DeepClone();
                    var data=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
                    var materials=new Dictionary<string,FxrMaterialProfile[]>(StringComparer.OrdinalIgnoreCase);
                    var models=new Dictionary<string,List<(FxrModelVertex[] Vertices,FxrMaterialProfile Material)>>(StringComparer.OrdinalIgnoreCase);
                    var parsedModels=new Dictionary<string,FLVER2>(StringComparer.OrdinalIgnoreCase);
                    var animations=new Dictionary<(string,int),FxrModelAnimation>();
                    var notices=new HashSet<string>();long bytes=0;
                    var scopes=new List<string>{fxr.Owner};var references=new List<FxrPreviewReference>();int referenced=0;
                    int expandedNodes=0;
                    var decodedReferences=new Dictionary<(int,string),(JObject Profile,string Owner)>();
                    int Scope(string owner){int scope=scopes.FindIndex(p=>string.Equals(p,owner,StringComparison.OrdinalIgnoreCase));if(scope<0){scope=scopes.Count;scopes.Add(owner);}return scope;}
                    string AssetKey(string owner,string name){int scope=Scope(owner);return scope==0?name:$"@{scope}/{name}";}
                    async Task Expand(JObject tree,string owner,int parent,HashSet<int> chain)
                    {
                        foreach(var app in tree.Descendants().OfType<JProperty>().Where(p=>p.Name=="appearance").Select(p=>p.Value).OfType<JObject>())app["_previewScope"]=Scope(owner);
                        foreach(var proxy in tree.Descendants().OfType<JObject>().Where(p=>p["type"]?.Type==JTokenType.Integer && (int)p["type"]==2001).ToArray())
                        {
                            int child=ReferenceId(proxy);
                            void Result(string status){references.Add(new(parent,child,status));proxy["_previewReferenceStatus"]=status;}
                            if(child<0){Result("unrecognized reference");notices.Add($"SFX {parent}: unrecognized reference 132.");continue;}
                            if(chain.Contains(child)){Result("cycle omitted");notices.Add($"Cyclic sub-effect reference {parent} -> {child} omitted.");continue;}
                            if(chain.Count>=12 || referenced++>=256){Result("budget omitted");notices.Add("Sub-effect expansion budget reached (12 levels / 256 occurrences).");continue;}
                            try
                            {
                                cancellation.ThrowIfCancellationRequested();
                                if(!decodedReferences.TryGetValue((child,owner),out var entry))
                                {
                                    if(decodedReferences.Count>=64){Result("budget omitted");notices.Add("Sub-effect decoded-resource budget reached (64).");continue;}
                                    var nested=source.Read($"f{child:D9}.fxr",owner);
                                    if(nested==null){Result("missing");notices.Add($"Missing referenced sub-effect {parent} -> {child}.");continue;}
                                    var decoded=await FxrPreviewDecoder.Decode(nested.Bytes,game,cancellation);
                                    if((int?)decoded["Id"]!=child)throw new InvalidDataException("reference ID mismatch");
                                    decodedReferences[(child,owner)]=entry=(decoded,nested.Owner);
                                }
                                int nodeCount=((JObject)entry.Profile["Root"]).Descendants().OfType<JObject>().Count(p=>p["type"]?.Type==JTokenType.Integer && (int)p["type"] is >=2000 and <=2300);
                                if(expandedNodes+nodeCount>8192){Result("budget omitted");notices.Add("Sub-effect node expansion budget reached (8192 nodes).");continue;}
                                expandedNodes+=nodeCount;
                                var nestedRoot=(JObject)entry.Profile["Root"].DeepClone();
                                nestedRoot["_previewStates"]=entry.Profile["States"]?.DeepClone();
                                Result("loaded");
                                await Expand(nestedRoot,entry.Owner,child,new HashSet<int>(chain){child});
                                // Retain the child root, clock and state graph. Flattening
                                // only its nodes discards the referenced effect's playback.
                                proxy["_previewReference"]=nestedRoot;
                            }
                            catch(OperationCanceledException){throw;}
                            catch(Exception ex){Result("decode failed");notices.Add($"Sub-effect {parent} -> {child} could not be decoded ({ex.GetType().Name}).");}
                        }
                    }
                    await Expand(root,fxr.Owner,id,new HashSet<int>{id});
                    byte[] Read(string name,string owner)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string key=AssetKey(owner,name);if(data.TryGetValue(key,out var found))return found;
                        if(data.Count>=256||bytes>=128*1024*1024) {notices.Add("Resource budget reached (256 assets / 128 MiB per FXR).");return null;}
                        var asset=source.Read(name,owner);found=asset?.Bytes;
                        if(found!=null && bytes+found.Length>128*1024*1024) {notices.Add("Resource byte budget reached.");found=null;}
                        data[key]=found;bytes+=found?.Length??0;return found;
                    }
                    void Texture(string name,string owner)
                    {
                        if(string.IsNullOrWhiteSpace(name))return;
                        name=Path.GetFileNameWithoutExtension(name.Replace('\\','/'));
                        string key=AssetKey(owner,name+".dds");if(data.ContainsKey(key))return;
                        if(data.Count>=256||bytes>=128*1024*1024) {notices.Add("Resource budget reached.");return;}
                        string legacy=name.EndsWith("_a",StringComparison.OrdinalIgnoreCase)?name[..^2]:name;
                        var asset=source.ReadAny(new[]{name+".dds",name+".tpf",legacy+".dds",legacy+".tpf"},owner);
                        byte[] dds=null;
                        if(asset!=null)
                        {
                            try { dds=FxrAssetReuseCache.Texture(asset,name,legacy); }
                            catch(Exception) {notices.Add("Invalid texture container: "+name);}
                        }
                        if(dds!=null && bytes+dds.Length>128*1024*1024) {notices.Add("Resource byte budget reached.");dds=null;}
                        data[key]=dds;bytes+=dds?.Length??0;
                        if(dds==null)notices.Add("Missing texture: "+name);
                    }
                    foreach(var property in root.Descendants().OfType<JProperty>())
                    {
                        var obj=property.Value as JObject;
                        if(property.Name=="appearance" && obj!=null)
                        {
                            int type=(int?)obj["type"]??0;
                            if(type is 10200 or 10300 or 10301 or 10302 or 10303){notices.Add($"Non-rendering {(type==10300?"wind force":"force")} action {type}: explicit CPU volume/receiver preview; native modulation remains approximate.");continue;}
                            if(type!=0&&!FxrPreviewCapabilities.Drawable(type)) { notices.Add("Omitted appearance type "+type);continue; }
                            // Do not report missing dependencies for a material
                            // that the GPU path explicitly cannot draw anyway.
                            // Keep the unsupported blend itself visible to users.
                            if(type==604 && obj["blendMode"]?.Type==JTokenType.Integer && (int)obj["blendMode"] is not (1 or 2 or 3 or 4 or 5 or 6 or 7))
                            {notices.Add($"Appearance 604 blend {(int)obj["blendMode"]} is not implemented; material and dependencies omitted.");continue;}
                            string owner=scopes[(int?)obj["_previewScope"]??0];
                            foreach(int texture in ResourceIds(obj["texture"]))if(type is not (607 or 10003)||texture>0)Texture($"s{texture:D5}_a",owner);
                            if(type is 607 or 608)
                            {
                                foreach(int texture in ResourceIds(obj["mask"]))if(texture>0)Texture($"s{texture:D5}_a",owner);
                                if(type==607)foreach(int texture in ResourceIds(obj["normalMap"]))if(texture>0)Texture($"s{texture:D5}_n",owner);
                            }
                            if(type==10014)
                                for(int layer=1;layer<=4;layer++)foreach(int texture in ResourceIds(obj["layer"+layer]))if(texture>0)Texture($"s{texture:D5}_a",owner);
                            if(type is 10000 or 10001)obj["_previewModernTrace"]=game!=SoulsGames.DS3;
                            if(type==10008)obj["_previewNativeSpark"]=game is SoulsGames.DS3 or SoulsGames.SDT;
                            if(type==10000)obj["_previewNativeGpuColor"]=game is SoulsGames.DS3 or SoulsGames.SDT;
                            if(type==604)
                            {
                                bool softLegacy=game is SoulsGames.DS3 or SoulsGames.SDT&&(bool?)obj["depthBlend"]==true;
                                obj["_previewModernLayers"]=game!=SoulsGames.DS3||softLegacy;
                                obj["_previewNormalizeLayerMask"]=!softLegacy&&game is SoulsGames.DS3 or SoulsGames.SDT;
                                obj["_previewLayerFirstAlpha"]=(bool?)obj["depthBlend"]==true;
                                obj["_previewClampLayerAlpha"]=game==SoulsGames.ERNR;
                                for(int layer=1;layer<=3;layer++)foreach(int texture in ResourceIds(obj["layer"+layer]))Texture($"s{texture:D5}_a",owner);
                                notices.Add("604: original texture-layer RGB/alpha operations and scene-depth fading; final lighting/color blend/exposure and native soft-sprite extent remain incomplete.");
                            }
                            foreach(int model in type is 605 or 10015?ResourceIds(obj["model"]):Array.Empty<int>())
                            {
                                var mesh=Read($"s{model:D5}.flver",owner);
                                if(mesh==null) {notices.Add("Missing model: "+model);continue;}
                                try
                                {
                                    string materialKey=AssetKey(owner,$"s{model:D5}.flver");
                                    if(!materials.ContainsKey(materialKey))
                                    {
                                        var parsedModel=FLVER2.Read(mesh);
                                        parsedModels[materialKey]=parsedModel;
                                        materials[materialKey]=parsedModel.Materials.Select(mat=>
                                        {
                                            FxrMaterialProfile resolved;
                                            try{resolved=FxrModelMaterial.Resolve(mat,source.MaterialDefinition(mat.MTD,game is SoulsGames.ER or SoulsGames.ERNR or SoulsGames.AC6));}
                                            catch(OperationCanceledException){throw;}
                                            catch(Exception ex){notices.Add($"Material {mat.MTD}: definition unavailable ({ex.GetType().Name}); FLVER samplers retained.");resolved=FxrModelMaterial.Resolve(mat);}
                                            foreach(var sampler in resolved.Samplers)Texture(sampler.Path,owner);
                                            if(!resolved.DefinitionLoaded)notices.Add($"Material {mat.MTD}: definition not found; FLVER sampler preview.");
                                            notices.Add($"Material {mat.MTD}: albedo/emissive, packed normal and reflectance preview{(resolved.DualLayer?", layered blend":"")}; special shader lighting remains approximate.");
                                            return resolved;
                                        }).ToArray();
                                        models[materialKey]=FxrModelGeometry.Build(parsedModel,materials[materialKey],n=>notices.Add(n),cancellation);
                                    }
                                }
                                catch(OperationCanceledException){throw;}
                                catch(Exception) {notices.Add("Invalid or unsupported model: "+model);data[AssetKey(owner,$"s{model:D5}.flver")]=null;}
                            }
                        }
                        if(property.Name=="function" && (string)property.Value is string function && function is not ("Linear" or "Stepped" or "Bezier" or "Hermite" or "ComponentHermite"))
                            notices.Add("Unsupported curve: "+function);
                        if(property.Name=="nodeMovement" && obj!=null && (int?)obj["type"] is int move && move is not (0 or 1 or 15 or 34))
                            notices.Add(move is 83 or 106 or 120 or 121 or 122
                                ? $"Movement {move}: axial speed / acceleration and partial follow supported; random turns, Y acceleration and alignment flags remain incomplete."
                                : "Approximated node movement: "+move);
                        if(property.Name=="appearance" && obj!=null && (int?)obj["type"] is 606 or 10012)
                        {
                            if((bool?)obj["dynamicOpacity"]==true)notices.Add("Tracer fold opacity uses projected crossings and distance falloff; refreshed each preview frame.");
                        }
                        if(property.Name=="emitter" && obj!=null && (int?)obj["type"] is int emit && emit is not (0 or 300 or 301 or 399))
                            notices.Add("Omitted emitter: "+emit);
                        if(property.Name=="emitterShape" && obj!=null && (int?)obj["type"] is int shape && shape is not (0 or 400 or 401 or 402 or 403 or 404 or 405))
                            notices.Add("Approximated emitter shape: "+shape);
                        if(property.Name=="modifiers" && property.Value is JArray mods)
                            foreach(var mod in mods.OfType<JObject>())
                                if((string)mod["type"] is string mt && mt is not ("RandomRange" or "RandomFraction" or "RandomDelta"))notices.Add("Omitted value modifier: "+mt);
                        if(property.Name=="stateConfigMap" && property.Value is JArray map && map.Distinct(JToken.EqualityComparer).Count()>1)
                            notices.Add("State switching uses time / termination conditions; other external controls remain incomplete.");
                    }
                    // Collect all clips first: a model/anibnd pair can be shared
                    // by several nodes or referenced FXRs with different clips.
                    var animated=root.Descendants().OfType<JProperty>().Where(p=>p.Name=="appearance").Select(p=>p.Value).OfType<JObject>()
                        .Where(a=>(int?)a["type"] is 605 or 10015 && (int?)a["anibnd"]>0).ToArray();
                    foreach(var group in animated.SelectMany(a=>ResourceIds(a["model"]).Select(m=>(Appearance:a,Model:m)))
                        .GroupBy(x=>(Scope:(int?)x.Appearance["_previewScope"]??0,x.Model,Binder:(int)x.Appearance["anibnd"])))
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string owner=scopes[group.Key.Scope],key=AssetKey(owner,$"s{group.Key.Model:D5}.flver");
                        if(!parsedModels.TryGetValue(key,out var model))continue;
                        try
                        {
                            var animationBytes=Read($"s{group.Key.Binder:D5}.anibnd",owner)??throw new FileNotFoundException("Animation binder is missing.");
                            animations[(key,group.Key.Binder)]=new FxrModelAnimation(animationBytes,model,models[key],group.Select(x=>(int?)x.Appearance["animation"]??0));
                        }
                        catch(OperationCanceledException){throw;}
                        catch(Exception ex){notices.Add($"Model {group.Key.Model} / animation binder {group.Key.Binder}: {ex.GetBaseException().Message}");}
                    }
                    foreach(var capability in FxrPreviewCapabilities.Used(root).Where(c=>!c.Executed))
                        notices.Add($"Action {capability.Id} {capability.Name}: {capability.Limitation}");
                    return new FxrPreviewResource(root,data,notices.OrderBy(n=>n).ToArray(),references.Distinct().ToArray(),materials,models,animations);
                },cancellation).ConfigureAwait(false);
            }
            finally { loaders.Release(); }
        }
        public static int ReferenceId(JObject node)
        {
            if(node["type"]?.Type!=JTokenType.Integer || (int)node["type"]!=2001)return -1;
            if(node["sfx"]?.Type==JTokenType.Integer)return (int)node["sfx"];
            foreach(var action in (node["actions"] as JArray??new JArray()).OfType<JObject>())
            {
                if((int?)action["type"]!=132)continue;
                if(action["sfx"]?.Type==JTokenType.Integer)return (int)action["sfx"];
                var value=action["fields1"]?.First;
                if(value is JObject field)value=field["value"];
                if(value?.Type==JTokenType.Integer)return (int)value;
            }
            return -1;
        }
        // Resource selectors can be serialized as properties in newer formats.
        // Keyframe values are IDs; time positions and random seeds are not IDs.
        public static int[] ResourceIds(JToken token)
        {
            var ids=new HashSet<int>();
            void Visit(JToken value)
            {
                if(value?.Type is JTokenType.Integer or JTokenType.Float)
                {double n=(double)value;if(double.IsFinite(n)&&n>=0&&n<=int.MaxValue)ids.Add((int)n);}
                else if(value is JObject obj)
                {
                    Visit(obj["value"]);
                    foreach(var key in obj["keyframes"]??new JArray())Visit(key["value"]);
                }
            }
            Visit(token);return ids.Take(256).ToArray();
        }
    }
}
