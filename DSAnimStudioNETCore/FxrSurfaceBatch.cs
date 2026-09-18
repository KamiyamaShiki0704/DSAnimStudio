using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    // Only adjacent items with identical shader uniforms are merged. No sorting,
    // tint quantization, coordinate conversion or changes to transparent order.
    internal sealed class FxrSurfaceBatch
    {
        public readonly VertexPositionColorTexture[] Vertices=new VertexPositionColorTexture[12288];
        public int Count;
        public FxrPreviewItem Item;
        public Texture2D Texture,Second,Third;
        public Matrix Wvp;
        public PrimitiveType Primitive;
        public bool Matches(FxrPreviewItem item,Texture2D texture,Texture2D second,Texture2D third,Matrix wvp,PrimitiveType primitive)=>Count>0&&
            Texture==texture&&Second==second&&Third==third&&Wvp==wvp&&Primitive==primitive&&Item.Blend==item.Blend&&
            Item.Color==item.Color&&Item.Atlas==item.Atlas&&Item.UvTransform==item.UvTransform&&Item.Layers==item.Layers&&
            Item.AlphaFade==item.AlphaFade&&Item.AlphaCutoff==item.AlphaCutoff&&Item.Premultiply==item.Premultiply&&Item.Octagonal==item.Octagonal&&Item.SoftDepthRadius==item.SoftDepthRadius&&Item.DepthOffset==item.DepthOffset;
        public void Begin(FxrPreviewItem item,Texture2D texture,Texture2D second,Texture2D third,Matrix wvp,PrimitiveType primitive)
        {Item=item;Texture=texture;Second=second;Third=third;Wvp=wvp;Primitive=primitive;}
        public void Clear(){Count=0;Item=null;Texture=null;Second=null;Third=null;}
    }
}
