using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    public sealed partial class FxrPreviewRenderer
    {
        const int MaxSceneLights=16;
        readonly List<(int Effect,FxrPreviewItem Item)> postItems=new();
        readonly Matrix[] lightInverse=new Matrix[MaxSceneLights];
        readonly Vector4[] lightPosition=new Vector4[MaxSceneLights],lightDiffuse=new Vector4[MaxSceneLights],
            lightSpecular=new Vector4[MaxSceneLights],lightVolume=new Vector4[MaxSceneLights];
        int lightCount;
        RenderTarget2D sceneColor;
        void BeginSpecialFrame(){lightCount=0;postItems.Clear();}
        void CollectLight(FxrPreviewItem item,WorldView view)
        {
            var light=item.ScenePass;if(light?.Type is not (609 or 11000))return;
            if(lightCount==MaxSceneLights){Notice("FXR lighting budget reached (16 lights); further lights omitted.");return;}
            var world=item.Transform*view.Matrix_World;
            if(Math.Abs(world.Determinant())<1e-12f)return;
            lightInverse[lightCount]=Matrix.Invert(world);
            lightPosition[lightCount]=new(world.Translation,light.Type==11000?1:0);
            lightDiffuse[lightCount]=item.Color;
            lightSpecular[lightCount]=light.Specular;
            lightVolume[lightCount]=new(light.Near,light.Far,light.RadiusX,light.RadiusY);lightCount++;
        }
        void ApplySceneLights()
        {
            EnsureSurfaceShader();var p=surfaceShader.Parameters;
            p["ModelLightCount"].SetValue(lightCount);
            if(lightCount==0)return;
            p["ModelLightInverse"].SetValue(lightInverse);p["ModelLightPosition"].SetValue(lightPosition);
            p["ModelLightDiffuse"].SetValue(lightDiffuse);p["ModelLightSpecular"].SetValue(lightSpecular);
            p["ModelLightVolume"].SetValue(lightVolume);
        }
        void DrawFlare(FxrPreviewItem item,WorldView view)
        {
            var pass=item.ScenePass;var wvp=item.Transform*view.Matrix_World*view.Matrix_View*view.Matrix_Projection;
            if(wvp.M44<=0)return;
            var ndc=new Vector2(wvp.M41,wvp.M42)/wvp.M44;
            var reflected=FxrSpecialAppearance.Reflect(ndc,pass.Reflection,pass.Offset);var delta=reflected-ndc;
            wvp.M41+=delta.X*wvp.M44;wvp.M42+=delta.Y*wvp.M44;
            float attenuation=pass.AttenuationRadius>0?Math.Max(0,1-ndc.Length()/pass.AttenuationRadius):1;
            if(attenuation<=0)return;
            var texture=Texture(item.Texture);if(texture==null)return;
            var gd=GFX.Device;var depth=gd.DepthStencilState;
            try
            {
                gd.DepthStencilState=DepthStencilState.None;
                var vp=gd.Viewport;
                surfaceShader.Parameters["FlareOcclusion"].SetValue(new Vector4(vp.X+(ndc.X*.5f+.5f)*vp.Width,vp.Y+(.5f-ndc.Y*.5f)*vp.Height,wvp.M43/wvp.M44,1));
                DrawSurface(SpriteQuad,SpriteQuad.Length,item with{Color=item.Color*attenuation},texture,WhiteTexture(),WhiteTexture(),wvp,PrimitiveType.TriangleList);
            }
            finally{surfaceShader.Parameters["FlareOcclusion"].SetValue(Vector4.Zero);gd.DepthStencilState=depth;}
        }
        bool CaptureScene()
        {
            var gd=GFX.Device;var bindings=gd.GetRenderTargets();
            if(bindings.Length!=1||bindings[0].RenderTarget is not RenderTarget2D target||target.RenderTargetUsage!=RenderTargetUsage.PreserveContents)
            {Notice("FXR distortion/blur requires a preserved scene color target; this render destination cannot be sampled safely.");return false;}
            if(sceneColor==null||sceneColor.Width!=target.Width||sceneColor.Height!=target.Height||sceneColor.Format!=target.Format)
            {sceneColor?.Dispose();sceneColor=new RenderTarget2D(gd,target.Width,target.Height,false,target.Format,DepthFormat.None,0,RenderTargetUsage.DiscardContents);}
            var viewport=gd.Viewport;var depth=gd.DepthStencilState;var blend=gd.BlendState;var raster=gd.RasterizerState;
            try
            {
                // GPU-only copy. No GetData, per-frame readback, or CPU color buffer.
                gd.SetRenderTarget(sceneColor);
                // This MonoGame fork uses custom effect bytecode. Its built-in
                // SpriteEffect is incompatible, so use DSA's loaded BasicEffect.
                gd.BlendState=BlendState.Opaque;gd.DepthStencilState=DepthStencilState.None;
                gd.RasterizerState=RasterizerState.CullNone;gd.SamplerStates[0]=SamplerState.PointClamp;
                shader.World=Matrix.CreateScale(2,2,1);shader.View=shader.Projection=Matrix.Identity;
                shader.Texture=target;shader.TextureEnabled=true;shader.VertexColorEnabled=true;
                shader.DiffuseColor=Vector3.One;shader.Alpha=1;
                foreach(var pass in shader.CurrentTechnique.Passes){pass.Apply();gd.DrawUserPrimitives(PrimitiveType.TriangleList,SpriteQuad,0,2);}
            }
            finally
            {gd.SetRenderTargets(bindings);gd.Viewport=viewport;gd.DepthStencilState=depth;gd.BlendState=blend;gd.RasterizerState=raster;}
            return true;
        }
        void DrawScenePasses(WorldView view)
        {
            if(postItems.Count==0)return;
            FlushSurface();if(!CaptureScene())return;
            // Every layer reads the same pre-distortion scene; this avoids unbounded
            // full-target copies for overlapping particles. Native inter-layer ordering differs.
            Notice("Scene-color effects share one pre-distortion snapshot; overlapping distortion/blur composition is approximate.");
            foreach(var entry in postItems)
            {
                currentEffect=entry.Effect;currentResource=resources.GetValueOrDefault(entry.Effect);currentScope=entry.Item.ResourceScope;
                var item=entry.Item;var pass=item.ScenePass;var wvp=item.Transform*view.Matrix_World*view.Matrix_View*view.Matrix_Projection;
                if(wvp.M44<=0||!float.IsFinite(pass.Strength)||Math.Abs(pass.Strength)<1e-8f)continue;
                var texture=item.Texture<=0?WhiteTexture():Texture(item.Texture);
                var normal=pass.NormalMap<=0?WhiteTexture():Texture(pass.NormalMap,'n');
                var mask=pass.Mask<=0?WhiteTexture():Texture(pass.Mask);
                if(texture==null||normal==null||mask==null)continue;
                var p=surfaceShader.Parameters;surfaceShader.CurrentTechnique=surfaceShader.Techniques["FxrSceneColor"];
                p["WorldViewProjection"].SetValue(wvp);p["Tint"].SetValue(item.Color);
                p["UvTransform"].SetValue(new Vector4(0,0,1,1));
                p["SurfaceTexture"].SetValue(texture);p["Layer2Texture"].SetValue(normal);p["Layer3Texture"].SetValue(mask);
                p["SceneColorTexture"].SetValue(sceneColor);
                p["SceneDimensions"].SetValue(new Vector2(sceneColor.Width,sceneColor.Height));
                var vp=GFX.Device.Viewport;var center=new Vector2(wvp.M41,wvp.M42)/wvp.M44;
                center=new((vp.X+(center.X*.5f+.5f)*vp.Width)/sceneColor.Width,(vp.Y+(.5f-center.Y*.5f)*vp.Height)/sceneColor.Height);
                p["SceneCenter"].SetValue(center);
                p["SceneEffect"].SetValue(new Vector4(pass.Type==608?3:pass.Mode,pass.Strength,Math.Max(.0001f,pass.Radius),pass.Angle));
                p["SceneNormalUv"].SetValue(pass.NormalUv);p["SceneSamples"].SetValue(pass.Samples);
                p["SceneAlphaCutoff"].SetValue(item.AlphaCutoff);
                GFX.Device.BlendState=BlendState.NonPremultiplied;GFX.Device.DepthStencilState=DepthStencilState.DepthRead;
                foreach(var shaderPass in surfaceShader.CurrentTechnique.Passes)
                {shaderPass.Apply();GFX.Device.DrawUserPrimitives(PrimitiveType.TriangleList,SpriteQuad,0,2);DrawCalls++;}
            }
        }
        void DisposeScenePasses(){sceneColor?.Dispose();sceneColor=null;lightShaftSource?.Dispose();lightShaftSource=null;lightShaftAdd?.Dispose();lightShaftAdd=null;postItems.Clear();}
    }
}
