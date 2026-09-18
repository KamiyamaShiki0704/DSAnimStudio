using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    public sealed partial class FxrPreviewRenderer
    {
        RenderTarget2D lightShaftSource;
        BlendState lightShaftAdd;

        void DrawLightShaft(FxrPreviewItem item,WorldView view)
        {
            var shaft=item.ScenePass.LightShaft;
            var wvp=item.Transform*view.Matrix_World*view.Matrix_View*view.Matrix_Projection;
            if(wvp.M44<=0||!float.IsFinite(wvp.M44)||shaft.Samples<=0)return;
            if(shaft.Samples>4096||!float.IsFinite(shaft.Parameters.LengthSquared())||!float.IsFinite(shaft.Inner.LengthSquared())||!float.IsFinite(shaft.Outer.LengthSquared())||!float.IsFinite(shaft.SourceScale.LengthSquared())||!float.IsFinite(shaft.DirectionLength))
            {Notice("Light shaft has invalid parameters or more than 4096 samples; layer omitted.");return;}
            var texture=item.Texture<=0?WhiteTexture():Texture(item.Texture);if(texture==null)return;
            var gd=GFX.Device;var bindings=gd.GetRenderTargets();var viewport=gd.Viewport;
            if(bindings.Length!=1||bindings[0].RenderTarget is not RenderTarget2D destination||destination.RenderTargetUsage!=RenderTargetUsage.PreserveContents)
            {Notice("Light-shaft compositing needs a preserved scene target; layer omitted.");return;}
            // Reused, full viewport resolution and HDR. No sample-count reduction,
            // per-frame pixel buffer or readback; source alpha stays separate from RGB.
            if(lightShaftSource==null||lightShaftSource.IsDisposed||lightShaftSource.Width!=viewport.Width||lightShaftSource.Height!=viewport.Height)
            {
                lightShaftSource?.Dispose();
                lightShaftSource=new RenderTarget2D(gd,viewport.Width,viewport.Height,false,SurfaceFormat.HalfVector4,DepthFormat.None,0,RenderTargetUsage.DiscardContents);
            }
            var depth=gd.DepthStencilState;var blend=gd.BlendState;var raster=gd.RasterizerState;
            try
            {
                EnsureSurfaceShader();var p=surfaceShader.Parameters;
                p["SoftDepthRadius"].SetValue(0f);p["DepthOffset"].SetValue(0f);
                // Remove prior SRV bindings before turning the shared mask into an RTV.
                for(int i=0;i<5;i++)if(gd.Textures[i]==lightShaftSource)gd.Textures[i]=null;
                gd.SetRenderTarget(lightShaftSource);gd.Clear(Color.Transparent);
                gd.DepthStencilState=DepthStencilState.None;gd.BlendState=BlendState.Opaque;gd.RasterizerState=RasterizerState.CullNone;
                p["WorldViewProjection"].SetValue(wvp);p["Tint"].SetValue(Vector4.One);p["UvTransform"].SetValue(new Vector4(0,0,1,1));
                p["ShaftInner"].SetValue(shaft.Inner);p["ShaftOuter"].SetValue(shaft.Outer);p["ShaftScale"].SetValue(shaft.SourceScale);
                p["ShaftDepthOrigin"].SetValue(new Vector2(viewport.X,viewport.Y));
                p["SurfaceTexture"].SetValue(texture);
                surfaceShader.CurrentTechnique=surfaceShader.Techniques["FxrLightShaftSource"];
                foreach(var pass in surfaceShader.CurrentTechnique.Passes)
                {pass.Apply();gd.DrawUserPrimitives(PrimitiveType.TriangleList,SpriteQuad,0,2);DrawCalls++;}
                gd.SetRenderTargets(bindings);gd.Viewport=viewport;
                lightShaftAdd??=new(){ColorSourceBlend=Blend.One,ColorDestinationBlend=Blend.One,
                    AlphaSourceBlend=Blend.Zero,AlphaDestinationBlend=Blend.One};
                gd.BlendState=item.Blend is 4 or 7?lightShaftAdd:FxrPreviewBlend.State(item.Blend,true)??BlendState.AlphaBlend;
                p["WorldViewProjection"].SetValue(Matrix.CreateScale(2,2,1));p["ShaftSourceTexture"].SetValue(lightShaftSource);
                p["ShaftCenter"].SetValue(new Vector2(.5f+.5f*wvp.M41/wvp.M44,.5f-.5f*wvp.M42/wvp.M44));
                var frame=shaft.Frame*view.Matrix_World;var offset=frame.Backward*shaft.DirectionLength;
                var horizontal=new Vector2(offset.X,offset.Z);var facing=new Vector2(view.Matrix_View.M31,view.Matrix_View.M33);
                float axial=horizontal.LengthSquared()>1e-12f&&facing.LengthSquared()>1e-12f?Vector2.Dot(Vector2.Normalize(horizontal),Vector2.Normalize(facing)):0;
                axial*=Math.Abs(axial);
                var direction=FxrLightShaft.ProjectDirection(frame.Translation,offset,view.Matrix_View*view.Matrix_Projection,shaft.DirectionLength,axial);
                p["ShaftDirection"].SetValue(new Vector3(direction.X,direction.Y,direction.Z));
                p["ShaftParameters"].SetValue(shaft.Parameters);p["ShaftSamples"].SetValue(shaft.Samples);
                surfaceShader.CurrentTechnique=surfaceShader.Techniques["FxrLightShaftAccumulate"];
                foreach(var pass in surfaceShader.CurrentTechnique.Passes)
                {pass.Apply();gd.DrawUserPrimitives(PrimitiveType.TriangleList,SpriteQuad,0,2);DrawCalls++;}
            }
            finally
            {
                gd.SetRenderTargets(bindings);gd.Viewport=viewport;gd.DepthStencilState=depth;gd.BlendState=blend;gd.RasterizerState=raster;
                for(int i=0;i<5;i++)if(gd.Textures[i]==lightShaftSource)gd.Textures[i]=null;
                surfaceShader.Parameters["ShaftSourceTexture"].SetValue((Texture2D)null);
            }
        }
    }
}
