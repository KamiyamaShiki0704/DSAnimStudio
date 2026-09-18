using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    // Capture after opaque geometry, before debug X-ray clears depth. Shared by
    // every FXR instance in that viewport; no CPU pixel readbacks or depth writes.
    public static class FxrOpaqueDepth
    {
        static GraphicsDevice device;
        static DepthSnapshot raw;
        static RenderTarget2D resolved,owner;
        static Effect effect;
        static readonly Texture[] savedTextures=new Texture[8];
        static readonly SamplerState[] savedSamplers=new SamplerState[8];
        static readonly VertexPositionColorTexture[] quad={
            new(new(-1,-1,0),Color.White,new(0,1)),new(new(-1,1,0),Color.White,new(0,0)),new(new(1,1,0),Color.White,new(1,0)),
            new(new(-1,-1,0),Color.White,new(0,1)),new(new(1,1,0),Color.White,new(1,0)),new(new(1,-1,0),Color.White,new(1,1))};
        public static string Warning {get;private set;}
        public static int Allocations {get;private set;}
        public static long GpuBytes=>raw is {IsDisposed:false}?(long)raw.Width*raw.Height*(raw.SampleCount*(raw.DepthFormat==DepthFormat.Depth16?2:4)+4):0;
        public static Texture2D ForTarget(RenderTarget2D target)=>target==owner&&resolved is {IsDisposed:false}?resolved:null;
        static void Reset(object sender,EventArgs e)=>Dispose();
        public static void Dispose()
        {
            raw?.Dispose();raw=null;resolved?.Dispose();resolved=null;effect?.Dispose();effect=null;owner=null;
            if(device!=null){device.DeviceResetting-=Reset;device.Disposing-=Reset;device=null;}
        }
        public static void Capture(RenderTarget2D target)
        {
            owner=null;Warning=null;
            if(target==null||target.IsDisposed||target.DepthStencilFormat==DepthFormat.None)return;
            var gd=target.GraphicsDevice;
            if(device!=gd){Dispose();device=gd;device.DeviceResetting+=Reset;device.Disposing+=Reset;}
            var bindings=gd.GetRenderTargets();var viewport=gd.Viewport;
            if(bindings.Length!=1||bindings[0].RenderTarget!=target||target.RenderTargetUsage!=RenderTargetUsage.PreserveContents)
            {Warning="FXR depth needs a preserved scene target.";return;}
            var blend=gd.BlendState;var depth=gd.DepthStencilState;var raster=gd.RasterizerState;
            for(int i=0;i<8;i++){savedTextures[i]=gd.Textures[i];savedSamplers[i]=gd.SamplerStates[i];}
            try
            {
                if(raw==null||!raw.Matches(target))
                {
                    raw?.Dispose();resolved?.Dispose();raw=new(target);
                    resolved=new(gd,target.Width,target.Height,false,SurfaceFormat.Single,DepthFormat.None,0,RenderTargetUsage.PreserveContents);Allocations++;
                }
                // Unbind last frame's SRVs before reusing the copy destinations.
                for(int i=0;i<8;i++)if(gd.Textures[i]==raw||gd.Textures[i]==resolved)gd.Textures[i]=null;
                raw.Capture(target);
                if(effect==null){using var stream=typeof(FxrOpaqueDepth).Assembly.GetManifestResourceStream("DSAnimStudio.EmbRes.FxrPreview.mgfxo");using var bytes=new MemoryStream();stream.CopyTo(bytes);effect=new Effect(gd,bytes.ToArray());}
                gd.SetRenderTarget(resolved);gd.BlendState=BlendState.Opaque;gd.DepthStencilState=DepthStencilState.None;gd.RasterizerState=RasterizerState.CullNone;
                effect.Parameters["WorldViewProjection"].SetValue(Matrix.Identity);effect.Parameters["HasSceneDepth"].SetValue(false);
                effect.Parameters[raw.SampleCount>1?"RawSceneDepthMs":"RawSceneDepth"].SetValue(raw);effect.Parameters["DepthSamples"].SetValue(raw.SampleCount);
                effect.CurrentTechnique=effect.Techniques[raw.SampleCount>1?"FxrDepthResolve":"FxrDepthCopy"];
                foreach(var pass in effect.CurrentTechnique.Passes){pass.Apply();gd.DrawUserPrimitives(PrimitiveType.TriangleList,quad,0,2);}
                owner=target;
            }
            catch(Exception ex){Warning="FXR depth capture failed: "+ex.GetType().Name+": "+ex.Message;}
            finally
            {
                if(effect!=null){effect.Parameters["RawSceneDepth"].SetValue((Texture2D)null);effect.Parameters["RawSceneDepthMs"].SetValue((Texture2D)null);}
                gd.SetRenderTargets(bindings);gd.Viewport=viewport;gd.BlendState=blend;gd.DepthStencilState=depth;gd.RasterizerState=raster;
                for(int i=0;i<8;i++){gd.Textures[i]=savedTextures[i]?.IsDisposed==true?null:savedTextures[i];gd.SamplerStates[i]=savedSamplers[i];savedTextures[i]=null;savedSamplers[i]=null;}
            }
        }
    }
}
