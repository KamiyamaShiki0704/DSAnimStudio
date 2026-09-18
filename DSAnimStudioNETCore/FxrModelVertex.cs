using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System.Runtime.InteropServices;

namespace DSAnimStudio
{
    [StructLayout(LayoutKind.Sequential,Pack=1)]
    public struct FxrModelVertex : IVertexType
    {
        public Vector3 Position;
        public Color Color;
        public Vector3 Normal;
        public Vector4 Tangent;
        public Vector2 Uv,EmissiveUv,NormalUv,SpecularUv;
        public Vector2 Albedo2Uv,Normal2Uv,Specular2Uv,MaskUv;
        public Vector4 Tangent2;
        public static readonly VertexDeclaration Declaration=new(
            new VertexElement(0,VertexElementFormat.Vector3,VertexElementUsage.Position,0),
            new VertexElement(12,VertexElementFormat.Color,VertexElementUsage.Color,0),
            new VertexElement(16,VertexElementFormat.Vector3,VertexElementUsage.Normal,0),
            new VertexElement(28,VertexElementFormat.Vector4,VertexElementUsage.Tangent,0),
            new VertexElement(44,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,0),
            new VertexElement(52,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,1),
            new VertexElement(60,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,2),
            new VertexElement(68,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,3),
            new VertexElement(76,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,4),
            new VertexElement(84,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,5),
            new VertexElement(92,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,6),
            new VertexElement(100,VertexElementFormat.Vector2,VertexElementUsage.TextureCoordinate,7),
            new VertexElement(108,VertexElementFormat.Vector4,VertexElementUsage.Tangent,1));
        VertexDeclaration IVertexType.VertexDeclaration=>Declaration;
    }
}
