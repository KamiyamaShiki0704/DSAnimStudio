using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json.Linq;
using SoulsFormats;
using SoulsAssetPipeline;
using System.Threading;
using System.Threading.Tasks;

namespace DSAnimStudio
{
    public sealed partial class FxrPreviewRenderer : IDisposable
    {
        readonly Dictionary<int,JObject> profiles = new();
        // Keyed by the "effect:scope:name" string. A tuple key was measured and
        // rejected: it saved under 1% of frame allocation while breaking the eight
        // GPU regressions that inject stubbed textures through this exact field.
        readonly Dictionary<string,TextureFetchRequest> textures = new();
        readonly Dictionary<(int Effect,int Scope,int Texture,char Channel),TextureFetchRequest> textureIds=new();
        readonly Dictionary<byte[],TextureFetchRequest> sharedTextures = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<(int Effect,int Scope,int Model),List<(FxrModelVertex[] Vertices,FxrMaterialProfile Material)>> meshes = new();
        readonly Dictionary<FxrModelVertex[],VertexBuffer> modelVertexBuffers=new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<FxrModelSkinVertex[],VertexBuffer> modelSkinBuffers=new(ReferenceEqualityComparer.Instance);
        readonly VertexBufferBinding[] skinnedBindings=new VertexBufferBinding[2];
        long skinBufferBytes;
        FxrModelAnimation.Pose submittedPose;
        long submittedPoseRevision;
        public const long ModelVertexBufferBudgetBytes=64L*1024*1024;
        public const int ModelVertexBufferBudgetCount=1024;
        GraphicsDevice graphicsDevice;
        bool modelVertexBuffersEnabled=true,modelBufferUploadsDisabled,skinBufferUploadsDisabled;
        long modelSubmitTicks;
        public bool ModelVertexBuffersEnabled
        {
            get=>modelVertexBuffersEnabled;
            set
            {
                if(modelVertexBuffersEnabled==value)return;
                modelVertexBuffersEnabled=value;
                ClearModelVertexBuffers();
            }
        }
        public bool ModelDiagnosticsEnabled {get;set;}
        public int ModelVertexBufferCount=>modelVertexBuffers.Count;
        public long ModelVertexBufferBytes {get;private set;}
        public long ModelVertexBufferUploadCount {get;private set;}
        public long ModelVertexBufferUploadBytes {get;private set;}
        public long ModelVertexBufferUploadFailures {get;private set;}
        public int FrameModelVertexBufferUploadCount {get;private set;}
        public long FrameModelVertexBufferUploadBytes {get;private set;}
        public int ModelBufferDrawCalls {get;private set;}
        public int ModelImmediateDrawCalls {get;private set;}
        public long ModelImmediateUploadBytes {get;private set;}
        public double ModelSubmitCpuMilliseconds=>modelSubmitTicks*1000.0/System.Diagnostics.Stopwatch.Frequency;
        readonly FxrPreviewScene scene = new();
        readonly FxrSceneReplayCache sceneReplay=new();
        public bool SceneReplayEnabled {get;set;}=true;
        public int SceneReplayHits=>sceneReplay.FrameHits;
        readonly HashSet<string> notices = new();
        // The deduplicating notice set already keeps a single copy, but the text
        // used to be interpolated for every primitive before the lookup rejected
        // it. Track which per-effect blend approximations were reported this
        // frame so the string is only built once.
        readonly HashSet<(int Effect,int Blend)> reportedBlendApproximations=new();
        readonly Dictionary<int,Task<FxrPreviewResource>> pending = new();
        readonly Dictionary<int,FxrPreviewResource> resources = new();
        readonly HashSet<byte[]> retainedAssetBytes=new(ReferenceEqualityComparer.Instance);
        readonly HashSet<FxrModelVertex[]> retainedModelVertices=new(ReferenceEqualityComparer.Instance);
        long retainedAssetSize;
        CancellationTokenSource cancellation=new();
        FxrPreviewResource currentResource;
        int currentEffect;
        int currentScope;
        string character;
        SoulsGames game;
        BasicEffect shader;
        Effect surfaceShader;
        readonly FxrSurfaceBatch surfaceBatch=new();
        readonly Dictionary<int,VertexPositionColorTexture[]> ribbonBuffers=new();
        VertexPositionColorTexture[] RibbonBuffer(int count)
        {if(!ribbonBuffers.TryGetValue(count,out var vertices))ribbonBuffers[count]=vertices=new VertexPositionColorTexture[count];return vertices;}
        bool queuingSurfaces;
        public bool BatchingEnabled {get;set;}=true;
        public int DrawCalls {get;private set;}
        public double CpuFrameMilliseconds {get;private set;}
        public long FrameAllocatedBytes {get;private set;}
        static readonly VertexPositionColorTexture[] SpriteQuad=Quad(new(-.5f,-.5f,0),new(-.5f,.5f,0),new(.5f,.5f,0),new(.5f,-.5f,0),0);
        string rootKey;
        string[] roots = Array.Empty<string>();
        public string Status { get; private set; } = "FXR preview: play an animation to load referenced effects.";
        public string Warning => string.Join("\n",notices.Take(32));
        public bool IsLoading => pending.Values.Any(t=>!t.IsCompleted);
        public string ReferenceSummary=>string.Join("\n",resources.OrderBy(p=>p.Key).SelectMany(p=>
        {
            var links=p.Value.References??Array.Empty<FxrPreviewReference>();
            return links.Length>0?links.Select(r=>$"SFX {p.Key}: {r.Parent} -> {r.Child} [{r.Status}]")
                :new[]{$"SFX {p.Key}: no FXR references"+(p.Value.Notices.Any(n=>n.Contains("wind force"))?"; contains a non-rendering wind force field":"")};
        }).Take(128));

        void Notice(string message) { if(notices.Count<128)notices.Add(message); }
        byte[] ReadAsset(string subpath)
        {
            string name=Path.GetFileName(subpath);return currentResource?.Assets.GetValueOrDefault(currentScope==0?name:$"@{currentScope}/{name}");
        }
        public void SetRoots(string gameRoot,string modRoot=null, SoulsGames game=SoulsGames.ERNR,string character=null)
        {
            string key=$"{gameRoot}|{modRoot}|{game}|{character}";if(key==rootKey)return;
            Dispose();rootKey=key;
            this.game=game;this.character=character;
            roots=new[]{modRoot,gameRoot}.Where(r=>!string.IsNullOrWhiteSpace(r)).ToArray();
        }
        public bool HasTail(IEnumerable<BulletParticle> bullets,double time) => bullets.Any(p=>!p.Alive
            && time>=p.DeathTime && time-p.DeathTime<3
            && (p.Definition.FlightSfx>=0 || (p.EndReason is "Unit hit" or "Ground hit") && p.Definition.HitSfx>=0));
        JObject LoadProfile(int id)
        {
            if(id<0)return null;
            if(profiles.TryGetValue(id,out var found))return found;
            if(!pending.TryGetValue(id,out var task))
            {
                if(pending.Count+profiles.Count>=64) {Notice("FXR cache budget reached (64 effects); reload resources to clear.");return null;}
                pending[id]=FxrPreviewLibrary.Load(id,roots,character,game,cancellation.Token);return null;
            }
            if(!task.IsCompleted)return null;
            pending.Remove(id);
            profiles[id]=null;
            if(task.IsCanceled) {Notice($"SFX {id}: loading canceled or timed out.");return null;}
            if(task.IsFaulted) {Notice($"SFX {id}: {task.Exception.GetBaseException().Message}");return null;}
            var resource=task.Result;
            var newBytes=new HashSet<byte[]>(ReferenceEqualityComparer.Instance);long size=0;
            foreach(var bytes in resource.Assets.Values)
                if(bytes!=null&&!retainedAssetBytes.Contains(bytes)&&newBytes.Add(bytes))size+=bytes.LongLength;
            var newVertices=new HashSet<FxrModelVertex[]>(ReferenceEqualityComparer.Instance);
            if(resource.Models!=null)foreach(var parts in resource.Models.Values)foreach(var part in parts)
                if(!retainedModelVertices.Contains(part.Vertices)&&newVertices.Add(part.Vertices))size+=part.Vertices.LongLength*FxrModelVertex.Declaration.VertexStride;
            if(resource.Animations!=null)foreach(var animation in resource.Animations.Values)size+=animation.RetainedBytes;
            if(retainedAssetSize+size>256L*1024*1024)
            {Notice($"SFX {id}: resource cache budget reached (256 MiB); reload resources to clear.");return null;}
            retainedAssetBytes.UnionWith(newBytes);retainedModelVertices.UnionWith(newVertices);retainedAssetSize+=size;
            resources[id]=resource;
            foreach(var message in resource.Notices)Notice($"SFX {id}: {message}");
            sceneReplay.RegisterImmutableProfile(resource.Root);
            return profiles[id]=resource.Root;
        }
        Texture2D Texture(string name)
        {
            if(string.IsNullOrEmpty(name))return null;
            string key=currentEffect+":"+currentScope+":"+name;
            if(textures.TryGetValue(key,out var request))return request?.Fetch2D();
            textures[key]=null;var bytes=ReadAsset("tex/"+name+".dds");
            if(bytes==null)
            {
                var tpf=ReadAsset("tex/"+name+".tpf");
                if(tpf!=null)bytes=TPF.Read(tpf).Textures.FirstOrDefault(t=>string.Equals(t.Name,name,StringComparison.OrdinalIgnoreCase))?.Bytes;
            }
            if(bytes==null) { Notice($"Missing FXR texture {name}; layer omitted.");return null; }
            if(!sharedTextures.TryGetValue(bytes,out request))sharedTextures[bytes]=request=new TextureFetchRequest(bytes,name);
            textures[key]=request;return request.Fetch2D();
        }
        Texture2D Texture(int id,char channel='a')
        {
            var key=(currentEffect,currentScope,id,channel);
            if(textureIds.TryGetValue(key,out var request))return request?.Fetch2D();
            string name=$"s{id:D5}_{channel}";
            var result=Texture(name);textureIds[key]=textures.GetValueOrDefault(currentEffect+":"+currentScope+":"+name);
            return result;
        }
        List<(FxrModelVertex[] Vertices,FxrMaterialProfile Material)> Mesh(int id)
        {
            var key=(currentEffect,currentScope,id);
            if(meshes.TryGetValue(key,out var cached))return cached;
            string materialKey=(currentScope==0?"":$"@{currentScope}/")+$"s{id:D5}.flver";
            if(currentResource?.Models?.TryGetValue(materialKey,out var prepared)==true)return meshes[key]=prepared;
            var bytes=ReadAsset($"model/s{id:D5}.flver");
            if(bytes==null){Notice($"Missing FXR model s{id:D5}.flver.");return meshes[key]=new();}
            return meshes[key]=FxrModelGeometry.Build(FLVER2.Read(bytes),currentResource?.Materials?.GetValueOrDefault(materialKey),Notice);
        }
        VertexBuffer ModelVertexBuffer(FxrModelVertex[] vertices)=>PreparedModelVertexBuffer(vertices,false);
        VertexBuffer PreparedModelVertexBuffer(FxrModelVertex[] vertices,bool required)
        {
            if(!ModelVertexBuffersEnabled&&!required)return null;
            var gd=GFX.Device;
            if(modelVertexBuffers.TryGetValue(vertices,out var cached))
            {
                if(!cached.IsDisposed&&ReferenceEquals(cached.GraphicsDevice,gd))return cached;
                modelVertexBuffers.Remove(vertices);
                ModelVertexBufferBytes-=(long)cached.VertexCount*FxrModelVertex.Declaration.VertexStride;
                cached.Dispose();
            }
            if(modelBufferUploadsDisabled)return null;
            long bytes=vertices.LongLength*FxrModelVertex.Declaration.VertexStride;
            if(modelVertexBuffers.Count>=ModelVertexBufferBudgetCount||bytes>ModelVertexBufferBudgetBytes-ModelVertexBufferBytes-skinBufferBytes)
                return null;
            VertexBuffer buffer=null;
            try
            {
                buffer=new VertexBuffer(gd,FxrModelVertex.Declaration,vertices.Length,BufferUsage.WriteOnly);
                buffer.SetData(vertices);
                ModelVertexBufferUploadCount++;ModelVertexBufferUploadBytes+=bytes;
                FrameModelVertexBufferUploadCount++;FrameModelVertexBufferUploadBytes+=bytes;
                modelVertexBuffers.Add(vertices,buffer);ModelVertexBufferBytes+=bytes;
                var result=buffer;buffer=null;return result;
            }
            catch(Exception ex)
            {
                modelBufferUploadsDisabled=true;ModelVertexBufferUploadFailures++;
                Notice($"FXR model vertex-buffer upload failed ({ex.GetType().Name}); uncached models use immediate drawing until reload or device reset.");
                return null;
            }
            finally{buffer?.Dispose();}
        }
        void ClearModelVertexBuffers()
        {
            foreach(var buffer in modelVertexBuffers.Values)buffer.Dispose();
            modelVertexBuffers.Clear();ModelVertexBufferBytes=0;modelBufferUploadsDisabled=false;
            foreach(var buffer in modelSkinBuffers.Values)buffer.Dispose();
            modelSkinBuffers.Clear();skinBufferBytes=0;skinBufferUploadsDisabled=false;
            Array.Clear(skinnedBindings);
            submittedPose=null;
        }
        VertexBuffer SkinVertexBuffer(FxrModelSkinVertex[] skin)
        {
            var gd=GFX.Device;
            if(modelSkinBuffers.TryGetValue(skin,out var cached))
            {
                if(!cached.IsDisposed&&ReferenceEquals(cached.GraphicsDevice,gd))return cached;
                modelSkinBuffers.Remove(skin);
                skinBufferBytes-=(long)cached.VertexCount*FxrModelSkinVertex.Declaration.VertexStride;
                cached.Dispose();
            }
            if(skinBufferUploadsDisabled)return null;
            long bytes=skin.LongLength*FxrModelSkinVertex.Declaration.VertexStride;
            if(modelSkinBuffers.Count>=ModelVertexBufferBudgetCount||bytes>ModelVertexBufferBudgetBytes-ModelVertexBufferBytes-skinBufferBytes)
            {Notice("FXR animated-model vertex buffer budget reached.");return null;}
            VertexBuffer buffer=null;
            try
            {
                buffer=new VertexBuffer(gd,FxrModelSkinVertex.Declaration,skin.Length,BufferUsage.WriteOnly);
                buffer.SetData(skin);modelSkinBuffers.Add(skin,buffer);skinBufferBytes+=bytes;
                var result=buffer;buffer=null;return result;
            }
            catch(Exception ex)
            {
                skinBufferUploadsDisabled=true;ModelVertexBufferUploadFailures++;
                Notice($"FXR animation-stream upload failed ({ex.GetType().Name}); new animated streams are suspended until reload or device reset.");
                return null;
            }
            finally{buffer?.Dispose();}
        }
        public void Draw(IEnumerable<BulletParticle> bullets,double worldTime,WorldView view)
            => DrawInstances(BulletInstances(bullets,worldTime),view);
        public static IEnumerable<FxrPlaybackInstance> BulletInstances(IEnumerable<BulletParticle> bullets,double worldTime)
        {
            foreach(var bullet in bullets)
            {
                Matrix Anchor(float age)
                {
                    var pos=FxrPreviewScene.Sample(bullet,age);var before=FxrPreviewScene.Sample(bullet,Math.Max(0,age-.01f));
                    var direction=BulletMath.Unit(pos-before,bullet.Direction);
                    return Matrix.CreateWorld(pos,-direction,Math.Abs(Vector3.Dot(direction,Vector3.Up))>.99f?Vector3.Right:Vector3.Up);
                }
                if(bullet.Definition.FlightSfx>=0)yield return new(bullet.Definition.FlightSfx,bullet.Serial,(float)(worldTime-bullet.BirthTime),bullet.Alive?float.PositiveInfinity:bullet.Age,Anchor,"Bullet flight");
                if(!bullet.Alive && bullet.EndReason is "Unit hit" or "Ground hit" && bullet.Definition.HitSfx>=0)
                {
                    var direction=BulletMath.Unit(bullet.HitNormal,Vector3.Up);
                    var matrix=Matrix.CreateWorld(bullet.HitPosition,-direction,Math.Abs(Vector3.Dot(direction,Vector3.Up))>.99f?Vector3.Right:Vector3.Up);
                    yield return new(bullet.Definition.HitSfx,bullet.Serial,(float)(worldTime-bullet.DeathTime),3,_=>matrix,"Bullet hit");
                }
            }
        }
        public void DrawInstances(IEnumerable<FxrPlaybackInstance> instances,WorldView view,FxrCollisionWorld collisions=null)
        {
            FrameModelVertexBufferUploadCount=0;FrameModelVertexBufferUploadBytes=0;
            ModelBufferDrawCalls=0;ModelImmediateDrawCalls=0;ModelImmediateUploadBytes=0;modelSubmitTicks=0;
            var gd=GFX.Device;if(gd==null||gd.IsDisposed||view==null)return;
            EnsureGraphicsDevice(gd);
            var blend=gd.BlendState;var depth=gd.DepthStencilState;var raster=gd.RasterizerState;
            var samplers=Enumerable.Range(0,8).Select(i=>gd.SamplerStates[i]).ToArray();
            var oldTextures=Enumerable.Range(0,8).Select(i=>gd.Textures[i]).ToArray();var indices=gd.Indices;
            int total=0;DrawCalls=0;surfaceBatch.Clear();queuingSurfaces=true;reportedBlendApproximations.Clear();
            sceneReplay.BeginFrame();
            long frameStart=System.Diagnostics.Stopwatch.GetTimestamp(),allocatedStart=GC.GetAllocatedBytesForCurrentThread();
            try
            {
                foreach(int loaded in pending.Where(p=>p.Value.IsCompleted).Select(p=>p.Key).ToArray())LoadProfile(loaded);
                shader??=new BasicEffect(gd,Main.BasicEffectBytecode){LightingEnabled=false,VertexColorEnabled=true};
                EnsureSurfaceShader();
                var currentTargets=gd.GetRenderTargets();
                var sceneDepth=currentTargets.Length==1?FxrOpaqueDepth.ForTarget(currentTargets[0].RenderTarget as RenderTarget2D):null;
                var projection=view.Matrix_Projection;
                surfaceShader.Parameters["HasSceneDepth"].SetValue(sceneDepth!=null);
                surfaceShader.Parameters["SceneDepth"].SetValue(sceneDepth);
                surfaceShader.Parameters["SceneDepthSize"].SetValue(sceneDepth!=null?new Vector2(sceneDepth.Width,sceneDepth.Height):Vector2.One);
                surfaceShader.Parameters["DepthProjection"].SetValue(new Vector4(projection.M33,projection.M43,projection.M34,projection.M44));
                surfaceShader.Parameters["FlareOcclusion"].SetValue(Vector4.Zero);
                if(FxrOpaqueDepth.Warning!=null)Notice(FxrOpaqueDepth.Warning);
                shader.View=view.Matrix_View;shader.Projection=view.Matrix_Projection;
                gd.DepthStencilState=DepthStencilState.DepthRead;gd.RasterizerState=RasterizerState.CullNone;
                gd.SamplerStates[0]=SamplerState.LinearWrap;
                var camera=Matrix.Invert(view.Matrix_World*view.Matrix_View);
                var playback=instances.Where(i=>i.Age>=0).Take(4096).ToArray();
                var fields=new FxrForceSet();var collector=new FxrPreviewScene();
                foreach(var instance in playback)
                {
                    var profile=LoadProfile(instance.EffectId);
                    if(profile!=null&&sceneReplay.HasForceVolumes(profile))
                    {
                        fields.AddRange(collector.CollectForces(profile,instance,camera));
                        foreach(var message in collector.Notices)Notice($"SFX {instance.EffectId}: {message}");
                    }
                }
                BeginSpecialFrame();
                var built=new List<(int Id,FxrPreviewItem[] Items)>();int builtCount=0,playbackSlot=0;
                foreach(var instance in playback)
                {
                    int slot=playbackSlot++;
                    if(builtCount>=4096)break;
                    int id=instance.EffectId;
                    if(instance.Age<0)continue;
                    var root=LoadProfile(id);if(root==null)continue;
                    currentEffect=id;currentResource=resources.GetValueOrDefault(id);
                    try
                    {
                    var evaluated=sceneReplay.Evaluate(slot,root,instance,camera,scene,fields,collisions,SceneReplayEnabled,projection);
                    foreach(var message in evaluated.Notices)Notice($"SFX {id}: {message}");
                    if(evaluated.OmittedAppearances.Length>0)Notice("Omitted appearance types: "+string.Join(", ",evaluated.OmittedAppearances.OrderBy(x=>x))+" (multi-texture / special materials / lighting).");
                    if(evaluated.Limited)Notice("FXR preview geometry budget reached.");
                    var items=evaluated.Items;built.Add((id,items));builtCount+=items.Length;
                    foreach(var item in items)CollectLight(item,view);
                    }
                    catch(Exception ex){Notice($"SFX {id}: scene evaluation failed ({ex.GetType().Name}). Other effects remain available.");}
                }
                ApplySceneLights();
                foreach(var instance in built)
                {
                    int id=instance.Id;currentEffect=id;currentResource=resources.GetValueOrDefault(id);
                    try
                    {
                    foreach(var item in instance.Items)
                    {
                        currentScope=item.ResourceScope;
                        if(total++>=4096) {Notice("FXR draw budget reached.");break;}
                        if(item.ScenePass!=null)
                        {
                            if(item.ScenePass.Type is 607 or 608)postItems.Add((id,item));
                            else if(item.ScenePass.Type==10014){FlushSurface();DrawFlare(item,view);}
                            else if(item.ScenePass.Type==10003){FlushSurface();DrawLightShaft(item,view);}
                            continue;
                        }
                        // The field library identifies 6/7 as variants resembling
                        // normal/additive. Keep that approximation explicit; do
                        // not discard entire referenced trails using these modes.
                        int blendMode=item.Blend switch{0 or 7=>4,6=>2,_=>item.Blend};
                        if(item.Blend is 0 or 6 or 7&&reportedBlendApproximations.Add((id,item.Blend)))
                            Notice($"SFX {id}: blend {item.Blend} uses {(blendMode==2?"normal-alpha":"additive")} preview (field library only notes it resembles {(item.Blend==6?"Normal":"Add")}); native variant differences remain unverified.");
                        var state=FxrPreviewBlend.State(item.Blend,item.Premultiply);
                        if(state==null) {Notice($"Blend mode {item.Blend} is not implemented; layer omitted.");continue;}
                        if(item.Model>=0)FlushSurface();
                        gd.BlendState=state;
                        shader.DiffuseColor=new Vector3(Math.Clamp(item.Color.X,0,32),Math.Clamp(item.Color.Y,0,32),Math.Clamp(item.Color.Z,0,32));
                        shader.Alpha=Math.Clamp(item.Color.W,0,1);
                        if(shader.Alpha<=0&&!FxrPreviewBlend.IgnoresAlpha(item.Blend))continue;
                        if(item.Model>=0)
                        {
                            shader.World=item.Transform*view.Matrix_World;
                            foreach(var part in Mesh(item.Model))SubmitModel(part.Vertices,part.Material,item,view);
                        }
                        else
                        {
                            var tex=item.Line!=null||item.Segment!=null?WhiteTexture():Texture(item.Texture);if(tex==null)continue;
                            shader.Texture=tex;shader.TextureEnabled=true;
                            if(item.Segment is FxrLineSegment segment)
                            {
                                var a=new VertexPositionColorTexture(segment.Start,new Color(segment.StartColor),Vector2.Zero);
                                var b=new VertexPositionColorTexture(segment.End,new Color(segment.EndColor),Vector2.One);
                                var vertices=RibbonBuffer(2);vertices[0]=a;vertices[1]=b;
                                SubmitSurface(vertices,item,tex,view.Matrix_World*view.Matrix_View*view.Matrix_Projection,PrimitiveType.LineList);
                            }
                            else if(item.Line is FxrLineGeometry line)
                            {
                                var a=new VertexPositionColorTexture(line.HeadLeft,new Color(line.HeadColor),new(0,0));
                                var b=new VertexPositionColorTexture(line.HeadRight,new Color(line.HeadColor),new(1,0));
                                var c=new VertexPositionColorTexture(line.TailRight,new Color(line.TailColor),new(1,1));
                                var d=new VertexPositionColorTexture(line.TailLeft,new Color(line.TailColor),new(0,1));
                                var vertices=RibbonBuffer(6);vertices[0]=a;vertices[1]=b;vertices[2]=c;vertices[3]=a;vertices[4]=c;vertices[5]=d;
                                SubmitSurface(vertices,item,tex,view.Matrix_World*view.Matrix_View*view.Matrix_Projection);
                            }
                            else if(item.GpuTrace!=null)
                            {
                                var vertices=RibbonBuffer(item.GpuTrace.VertexCount);item.GpuTrace.WriteVertices(vertices);
                                SubmitSurface(vertices,item,tex,view.Matrix_World*view.Matrix_View*view.Matrix_Projection);
                            }
                            else if(item.Ribbon!=null)
                            {
                                var vertices=RibbonBuffer(item.Ribbon.VertexCount);item.Ribbon.WriteVertices(vertices);
                                SubmitSurface(vertices,item,tex,view.Matrix_World*view.Matrix_View*view.Matrix_Projection);
                            }
                            else if(item.RibbonEnd.HasValue)
                            {
                                var a=item.Transform.Translation;var b=item.RibbonEnd.Value;
                                var side=BulletMath.Unit(Vector3.Cross(b-a,camera.Translation-a),camera.Right)*item.RibbonWidth*.5f;
                                var vertices=RibbonBuffer(6);FillQuad(vertices,a-side,a+side,b+side,b-side,item.U);
                                SubmitSurface(vertices,item,tex,view.Matrix_World*view.Matrix_View*view.Matrix_Projection);
                            }
                            else
                            {
                                SubmitSurface(SpriteQuad,item,tex,
                                    item.Transform*view.Matrix_World*view.Matrix_View*view.Matrix_Projection);
                            }
                        }
                    }
                    FlushSurface();
                    }
                    catch(Exception ex) { surfaceBatch.Clear();Notice($"SFX {id}: layer rendering failed ({ex.GetType().Name}). Other effects remain available."); }
                    if(total>=4096)break;
                }
                DrawScenePasses(view);
                CpuFrameMilliseconds=System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
                FrameAllocatedBytes=GC.GetAllocatedBytesForCurrentThread()-allocatedStart;
                Status=$"FXR preview: {total} items / {DrawCalls} draws | CPU {CpuFrameMilliseconds:0.0} ms / {FrameAllocatedBytes/1024} KiB | {resources.Count} loaded / {pending.Values.Count(t=>!t.IsCompleted)} loading. Partial reconstruction.";
            }
            catch(Exception ex) { Notice("FXR preview: "+ex.GetType().Name+": "+ex.Message); }
            finally
            {
                sceneReplay.EndFrame();
                surfaceBatch.Clear();queuingSurfaces=false;
                surfaceShader?.Parameters["SceneDepth"].SetValue((Texture2D)null);
                gd.BlendState=blend;gd.DepthStencilState=depth;gd.RasterizerState=raster;
                for(int i=0;i<samplers.Length;i++){gd.SamplerStates[i]=samplers[i];gd.Textures[i]=oldTextures[i];}
                gd.Indices=indices;gd.SetVertexBuffer(null);
            }
        }
        void Submit(VertexPositionColorTexture[] vertices)
        {
            if(vertices.Length<3)return;
            foreach(var pass in shader.CurrentTechnique.Passes) {pass.Apply();GFX.Device.DrawUserPrimitives(PrimitiveType.TriangleList,vertices,0,vertices.Length/3);}
        }
        void EnsureSurfaceShader()
        {
            if(surfaceShader!=null)return;
            using var stream=typeof(FxrPreviewRenderer).Assembly.GetManifestResourceStream("DSAnimStudio.EmbRes.FxrPreview.mgfxo");
            using var bytes=new MemoryStream();stream.CopyTo(bytes);surfaceShader=new Effect(GFX.Device,bytes.ToArray());
        }
        void SubmitModel(FxrModelVertex[] vertices,FxrMaterialProfile material,FxrPreviewItem item,WorldView view)
        {
            if(!ModelDiagnosticsEnabled){SubmitModelCore(vertices,material,item,view);return;}
            long start=System.Diagnostics.Stopwatch.GetTimestamp();
            try{SubmitModelCore(vertices,material,item,view);}
            finally{modelSubmitTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-start;}
        }
        void SubmitModelCore(FxrModelVertex[] vertices,FxrMaterialProfile material,FxrPreviewItem item,WorldView view)
        {
            if(vertices.Length<3)return;
            EnsureSurfaceShader();surfaceShader.CurrentTechnique=surfaceShader.Techniques["FxrModel"];
            FxrModelSkinVertex[] skin=null;FxrModelAnimation.Pose pose=null;
            if(item.AnimationBinder>0)
            {
                string modelKey=(currentScope==0?"":$"@{currentScope}/")+$"s{item.Model:D5}.flver";
                if(currentResource?.Animations?.TryGetValue((modelKey,item.AnimationBinder),out var animation)==true&&animation.Skins.TryGetValue(vertices,out skin))
                    pose=animation.Sample(item.AnimationClip,item.AnimationTime,item.AnimationLoop);
                if(pose==null){Notice($"SFX {currentEffect}: model {item.Model} animation {item.AnimationBinder}/{item.AnimationClip} unavailable; bind pose retained.");skin=null;}
                else surfaceShader.CurrentTechnique=surfaceShader.Techniques["FxrSkinnedModel"];
            }
            Texture2D Map(FxrMaterialSampler s)=>string.IsNullOrWhiteSpace(s?.Path)?null:Texture(Path.GetFileNameWithoutExtension(s.Path.Replace('\\','/')));
            var baseMap=Map(material.Base);if(material.Base!=null&&baseMap==null)return;
            baseMap??=WhiteTexture();
            var emissive=material.Emissive;var emission=emissive==material.Base?null:Map(emissive);
            var normal=Map(material.Normal);var specular=Map(material.Reflectance);
            var albedo2=Map(material.Albedo2);var normal2=Map(material.Normal2);var specular2=Map(material.Reflectance2);var mask=Map(material.AuxiliarySampler);
            if(material.Albedo2!=null&&albedo2==null)return;
            if(material.FalloffGraph&&mask==null)Notice("Falloff mask absent: white preview fallback; native default texture not verified.");
            var p=surfaceShader.Parameters;var world=item.Transform*view.Matrix_World;
            if(pose!=null&&(!ReferenceEquals(pose,submittedPose)||pose.Revision!=submittedPoseRevision))
            {p["ModelBones"].SetValue(pose.Bones);p["ModelBoneNormals"].SetValue(pose.Normals);submittedPose=pose;submittedPoseRevision=pose.Revision;}
            p["WorldViewProjection"].SetValue(world*view.Matrix_View*view.Matrix_Projection);
            p["ModelWorld"].SetValue(world);p["ModelNormalMatrix"].SetValue(Matrix.Transpose(Matrix.Invert(world)));
            p["ModelCamera"].SetValue(Matrix.Invert(view.Matrix_View).Translation);
            p["ModelDiffuseColorMultiplier"].SetValue(material.Base?.Role=="Emissive"?material.EmissiveColorMultiplier:material.DiffuseColorMultiplier);
            p["ModelSpecularColorMultiplier"].SetValue(material.SpecularColorMultiplier);
            p["ModelEmissiveColorMultiplier"].SetValue(material.EmissiveColorMultiplier);
            p["ModelSurfaceColorMultiplier"].SetValue(material.SurfaceColorMultiplier);
            p["ModelGraph"].SetValue(new Vector4(material.GlowGraph||material.FalloffGraph?1:0,material.MetallicOffset,material.FalloffGraph?1:0,material.FalloffUseNormal?1:0));
            p["ModelFalloffInside"].SetValue(material.FalloffInside);p["ModelFalloffOutside"].SetValue(material.FalloffOutside);p["ModelFalloffParameters"].SetValue(material.FalloffParameters);
            p["ModelAuthoredNormalFrame"].SetValue(material.AuthoredNormalFrame?1f:0f);
            p["ModelMaterialModes"].SetValue(new Vector4(material.PackedNormal?1:0,material.Reflectance?.Role=="Metallic"?1:0,material.DualLayer?1:0,material.GhostGraph?1:0));
            p["ModelGhostEdgeColor"].SetValue(material.GhostEdgeColor);p["ModelGhostEdgeParameters"].SetValue(material.GhostEdgeParameters);
            p["ModelSecondaryEmission"].SetValue(material.GhostGraph&&mask!=null?1f:0f);
            p["ModelLayerFlags"].SetValue(new Vector4(albedo2!=null?1:0,normal2!=null?1:0,specular2!=null?1:0,material.MaskedBlend&&mask!=null?1:0));
            p["ModelFlags"].SetValue(new Vector4(emission!=null?1:0,normal!=null||normal2!=null?1:0,specular!=null||specular2!=null?1:0,material.AuxiliaryColor?1:0));
            p["ModelPrimaryMaps"].SetValue(new Vector2(normal!=null?1:0,specular!=null?1:0));
            // GXFlver_Col is the authored unlit color shader, including the
            // verified sword-wave meshes. Scene lights must not relight it.
            p["ModelLightCount"].SetValue(material.Shader?.EndsWith("GXFlver_Col.spx",StringComparison.OrdinalIgnoreCase)==true?0:lightCount);
            p["ModelDither"].SetValue(item.Dither?1f:0f);
            Vector2 Address(FxrMaterialSampler s)=>new(s?.AddressU??1,s?.AddressV??1);
            Vector2 Texel(Texture2D t)=>new(.5f/(t?.Width??1),.5f/(t?.Height??1));
            Vector4 Pair(Vector2 a,Vector2 b)=>new(a.X,a.Y,b.X,b.Y);
            p["ModelAddress0"].SetValue(Pair(Address(material.Base),Address(emissive)));
            p["ModelAddress1"].SetValue(Pair(Address(material.Normal),Address(material.Reflectance)));
            p["ModelTexel0"].SetValue(Pair(Texel(baseMap),Texel(emission)));p["ModelTexel1"].SetValue(Pair(Texel(normal),Texel(specular)));
            p["ModelAddress2"].SetValue(Pair(Address(material.Albedo2),Address(material.Normal2)));p["ModelAddress3"].SetValue(Pair(Address(material.Reflectance2),Address(material.AuxiliarySampler)));
            p["ModelTexel2"].SetValue(Pair(Texel(albedo2),Texel(normal2)));p["ModelTexel3"].SetValue(Pair(Texel(specular2),Texel(mask)));
            p["Tint"].SetValue(item.Color);p["AtlasCurrent"].SetValue(item.Atlas.CurrentOrFull);
            p["AtlasNext"].SetValue(item.Atlas.Next==Vector4.Zero?item.Atlas.CurrentOrFull:item.Atlas.Next);p["FrameBlend"].SetValue(item.Atlas.Blend);
            p["UvTransform"].SetValue(item.UvTransform==Vector4.Zero?new Vector4(0,0,1,1):item.UvTransform);
            p["TextureSize"].SetValue(new Vector2(baseMap.Width,baseMap.Height));p["AlphaThresholds"].SetValue(new Vector2(item.AlphaFade,item.AlphaCutoff));
            p["PremultiplyAlpha"].SetValue(item.Premultiply&&item.Blend is 2 or 6?1f:0f);
            p["SurfaceTexture"].SetValue(baseMap);p["Layer2Texture"].SetValue(emission??WhiteTexture());
            p["Layer3Texture"].SetValue(normal??WhiteTexture());p["ModelSpecularTexture"].SetValue(specular??WhiteTexture());
            p["ModelAlbedo2Texture"].SetValue(albedo2??WhiteTexture());p["ModelNormal2Texture"].SetValue(normal2??WhiteTexture());p["ModelSpecular2Texture"].SetValue(specular2??WhiteTexture());p["ModelMaskTexture"].SetValue(mask??WhiteTexture());
            var gd=GFX.Device;var buffer=queuingSurfaces||skin!=null?PreparedModelVertexBuffer(vertices,skin!=null):null;int primitives=vertices.Length/3;
            var skinBuffer=skin==null?null:SkinVertexBuffer(skin);
            if(skin!=null&&(buffer==null||skinBuffer==null))return;
            try
            {
                foreach(var pass in surfaceShader.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    if(buffer!=null)
                    {
                        if(skinBuffer!=null){skinnedBindings[0]=new(buffer);skinnedBindings[1]=new(skinBuffer);gd.SetVertexBuffers(skinnedBindings);}
                        else gd.SetVertexBuffer(buffer);
                        gd.DrawPrimitives(PrimitiveType.TriangleList,0,primitives);
                        ModelBufferDrawCalls++;
                    }
                    else
                    {
                        gd.DrawUserPrimitives(PrimitiveType.TriangleList,vertices,0,primitives);
                        ModelImmediateDrawCalls++;ModelImmediateUploadBytes+=(long)primitives*3*FxrModelVertex.Declaration.VertexStride;
                    }
                    DrawCalls++;
                }
            }
            finally{if(buffer!=null)gd.SetVertexBuffer(null);}
        }
        Texture2D whiteTexture;
        Texture2D WhiteTexture()
        {if(whiteTexture==null){whiteTexture=new Texture2D(GFX.Device,1,1);whiteTexture.SetData(new[]{Color.White});}return whiteTexture;}
        void FlushSurface()
        {
            if(surfaceBatch.Count==0)return;
            var b=surfaceBatch;
            try{DrawSurface(b.Vertices,b.Count,b.Item,b.Texture,b.Second,b.Third,b.Wvp,b.Primitive);}
            finally{b.Clear();}
        }
        void SubmitSurface(VertexPositionColorTexture[] vertices,FxrPreviewItem item,Texture2D texture,Matrix wvp,PrimitiveType primitive=PrimitiveType.TriangleList)
        {
            int stride=primitive==PrimitiveType.LineList?2:3;
            if(vertices.Length<stride)return;
            var layers=item.Layers;
            var second=layers==null?WhiteTexture():Texture(layers.Second.Texture);
            var third=layers==null?WhiteTexture():Texture(layers.Third.Texture);
            if(second==null||third==null)return;
            if(queuingSurfaces&&BatchingEnabled&&vertices.Length<=surfaceBatch.Vertices.Length)
            {
                if(!surfaceBatch.Matches(item,texture,second,third,wvp,primitive)||surfaceBatch.Count+vertices.Length>surfaceBatch.Vertices.Length)FlushSurface();
                if(surfaceBatch.Count==0)surfaceBatch.Begin(item,texture,second,third,wvp,primitive);
                Array.Copy(vertices,0,surfaceBatch.Vertices,surfaceBatch.Count,vertices.Length);surfaceBatch.Count+=vertices.Length;
                return;
            }
            FlushSurface();DrawSurface(vertices,vertices.Length,item,texture,second,third,wvp,primitive);
        }
        void DrawSurface(VertexPositionColorTexture[] vertices,int count,FxrPreviewItem item,Texture2D texture,Texture2D second,Texture2D third,Matrix wvp,PrimitiveType primitive)
        {
            int stride=primitive==PrimitiveType.LineList?2:3;var layers=item.Layers;
            // Unsupported modes are reported and omitted by the caller; a batched
            // surface must not hand a null state to the device.
            var state=FxrPreviewBlend.State(item.Blend,item.Premultiply);
            if(state==null)return;
            GFX.Device.BlendState=state;
            EnsureSurfaceShader();surfaceShader.CurrentTechnique=surfaceShader.Techniques["FxrSurface"];
            var p=surfaceShader.Parameters;
            p["WorldViewProjection"].SetValue(wvp);p["Tint"].SetValue(item.Color);
            p["AtlasCurrent"].SetValue(item.Atlas.CurrentOrFull);
            p["AtlasNext"].SetValue(item.Atlas.Next==Vector4.Zero?item.Atlas.CurrentOrFull:item.Atlas.Next);
            p["FrameBlend"].SetValue(item.Atlas.Blend);
            p["UvTransform"].SetValue(item.UvTransform==Vector4.Zero?new Vector4(0,0,1,1):item.UvTransform);
            p["TextureSize"].SetValue(new Vector2(texture.Width,texture.Height));
            p["AlphaThresholds"].SetValue(new Vector2(item.AlphaFade,item.AlphaCutoff));
            p["PremultiplyAlpha"].SetValue(item.Premultiply && item.Blend is 2 or 6?1f:0f);
            p["SurfaceTexture"].SetValue(texture);
            p["Layer2Texture"].SetValue(second);p["Layer3Texture"].SetValue(third);
            p["LayerMode"].SetValue(layers==null?-1f:layers.Mode);
            p["Layer2Operation"].SetValue(layers?.SecondOperation??0);p["Layer3Operation"].SetValue(layers?.ThirdOperation??0);
            p["ModernLayers"].SetValue(layers?.Modern??false);
            p["LayerFirstAlpha"].SetValue(layers?.FirstAlpha??false);
            p["NormalizeLayerMask"].SetValue(layers?.NormalizeMask??true);
            p["ClampLayerAlpha"].SetValue(layers?.ClampAlpha??false);
            p["Layer1Color"].SetValue(layers?.First.Color??Vector4.One);
            p["Layer2Color"].SetValue(layers?.Second.Color??Vector4.One);
            p["Layer3Color"].SetValue(layers?.Third.Color??Vector4.One);
            p["Layer1Uv"].SetValue(layers?.First.Uv??new Vector4(0,0,1,1));
            p["Layer2Uv"].SetValue(layers?.Second.Uv??new Vector4(0,0,1,1));
            p["Layer3Uv"].SetValue(layers?.Third.Uv??new Vector4(0,0,1,1));
            p["Octagonal"].SetValue(item.Octagonal?1f:0f);
            p["SoftDepthRadius"].SetValue(float.IsFinite(item.SoftDepthRadius)?Math.Max(0,item.SoftDepthRadius):0);
            p["DepthOffset"].SetValue(float.IsFinite(item.DepthOffset)?item.DepthOffset:0);
            foreach(var pass in surfaceShader.CurrentTechnique.Passes)
            {pass.Apply();GFX.Device.DrawUserPrimitives(primitive,vertices,0,count/stride);DrawCalls++;}
        }
        static VertexPositionColorTexture[] Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,float u)
        {
            var vertices=new VertexPositionColorTexture[6];FillQuad(vertices,a,b,c,d,u);return vertices;
        }
        static void FillQuad(VertexPositionColorTexture[] vertices,Vector3 a,Vector3 b,Vector3 c,Vector3 d,float u)
        {
            Vector2 Uv(float x,float y)=>new(x,y);
            var va=new VertexPositionColorTexture(a,Color.White,Uv(u,1));var vb=new VertexPositionColorTexture(b,Color.White,Uv(u,0));
            var vc=new VertexPositionColorTexture(c,Color.White,Uv(u+1,0));var vd=new VertexPositionColorTexture(d,Color.White,Uv(u+1,1));
            vertices[0]=va;vertices[1]=vb;vertices[2]=vc;vertices[3]=va;vertices[4]=vc;vertices[5]=vd;
        }
        void EnsureGraphicsDevice(GraphicsDevice device)
        {
            if(ReferenceEquals(graphicsDevice,device))return;
            if(graphicsDevice!=null){DetachGraphicsDevice();DisposeGraphicsResources();}
            graphicsDevice=device;
            device.DeviceResetting+=OnGraphicsDeviceResetting;
            device.Disposing+=OnGraphicsDeviceDisposing;
        }
        void DetachGraphicsDevice()
        {
            if(graphicsDevice==null)return;
            graphicsDevice.DeviceResetting-=OnGraphicsDeviceResetting;
            graphicsDevice.Disposing-=OnGraphicsDeviceDisposing;
            graphicsDevice=null;
        }
        void OnGraphicsDeviceResetting(object sender,EventArgs args)
        {
            if(ReferenceEquals(sender,graphicsDevice))DisposeGraphicsResources();
        }
        void OnGraphicsDeviceDisposing(object sender,EventArgs args)
        {
            if(!ReferenceEquals(sender,graphicsDevice))return;
            DetachGraphicsDevice();DisposeGraphicsResources();
        }
        void DisposeGraphicsResources()
        {
            ClearModelVertexBuffers();DisposeScenePasses();
            shader?.Dispose();shader=null;surfaceShader?.Dispose();surfaceShader=null;whiteTexture?.Dispose();whiteTexture=null;
            foreach(var texture in textures.Values.Distinct())texture?.Dispose();
            textures.Clear();textureIds.Clear();sharedTextures.Clear();surfaceBatch.Clear();queuingSurfaces=false;
        }
        public void Dispose()
        {
            DetachGraphicsDevice();DisposeGraphicsResources();
            sceneReplay.Dispose();scene.Items.Clear();scene.Notices.Clear();scene.OmittedAppearances.Clear();
            cancellation.Cancel();cancellation.Dispose();cancellation=new();
            pending.Clear();resources.Clear();retainedAssetBytes.Clear();retainedModelVertices.Clear();retainedAssetSize=0;currentResource=null;
            textures.Clear();textureIds.Clear();sharedTextures.Clear();meshes.Clear();profiles.Clear();notices.Clear();ribbonBuffers.Clear();surfaceBatch.Clear();queuingSurfaces=false;
            Status="FXR preview: play or replay to load referenced effects.";
        }
    }
}
