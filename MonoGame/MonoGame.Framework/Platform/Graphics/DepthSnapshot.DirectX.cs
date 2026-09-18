// GPU-only copy of a render target's depth. The original DSV remains bound and
// untouched; consumers sample the separate typeless resource, including MSAA.
using System;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using DxFormat=SharpDX.DXGI.Format;

namespace Microsoft.Xna.Framework.Graphics
{
    public sealed class DepthSnapshot : Texture2D
    {
        readonly DepthFormat depthFormat;
        readonly SampleDescription samples;
        public int SampleCount { get { return samples.Count; } }
        public DepthFormat DepthFormat { get { return depthFormat; } }

        public DepthSnapshot(RenderTarget2D source)
            : base(source.GraphicsDevice, source.Width, source.Height, false, SurfaceFormat.Single)
        {
            if(source.DepthStencilFormat == DepthFormat.None || source.ArraySize != 1)
                throw new ArgumentException("Depth snapshot requires a non-array depth target.", "source");
            depthFormat = source.DepthStencilFormat;
            using(var native = ((IRenderTarget)source).GetDepthStencilView().ResourceAs<SharpDX.Direct3D11.Texture2D>())
                samples = native.Description.SampleDescription;
        }

        public bool Matches(RenderTarget2D source)
        {
            return !IsDisposed && source != null && !source.IsDisposed && source.GraphicsDevice == GraphicsDevice &&
                source.Width == Width && source.Height == Height && source.DepthStencilFormat == depthFormat &&
                Math.Max(1, source.MultiSampleCount) == samples.Count && source.ArraySize == 1;
        }

        public void Capture(RenderTarget2D source)
        {
            if(!Matches(source)) throw new ArgumentException("Depth target dimensions, format or samples changed.", "source");
            using(var native = ((IRenderTarget)source).GetDepthStencilView().ResourceAs<SharpDX.Direct3D11.Texture2D>())
                lock(GraphicsDevice._d3dContext) GraphicsDevice._d3dContext.CopyResource(native, GetTexture());
        }

        protected internal override Texture2DDescription GetTexture2DDescription()
        {
            var desc=base.GetTexture2DDescription();
            desc.Format=depthFormat==DepthFormat.Depth16?DxFormat.R16_Typeless:DxFormat.R24G8_Typeless;
            desc.SampleDescription=samples;
            return desc;
        }

        internal override ShaderResourceView CreateShaderResourceView()
        {
            var desc=new ShaderResourceViewDescription {Format=depthFormat==DepthFormat.Depth16?DxFormat.R16_UNorm:DxFormat.R24_UNorm_X8_Typeless,
                Dimension=SampleCount>1?SharpDX.Direct3D.ShaderResourceViewDimension.Texture2DMultisampled:SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D};
            if(SampleCount<=1) desc.Texture2D=new ShaderResourceViewDescription.Texture2DResource {MipLevels=1};
            return new ShaderResourceView(GraphicsDevice._d3dDevice,GetTexture(),desc);
        }
    }
}
