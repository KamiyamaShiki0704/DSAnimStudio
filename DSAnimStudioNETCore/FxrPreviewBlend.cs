using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    public static class FxrPreviewBlend
    {
        public static readonly BlendState Multiply=new(){ColorSourceBlend=Blend.DestinationColor,ColorDestinationBlend=Blend.Zero,
            AlphaSourceBlend=Blend.Zero,AlphaDestinationBlend=Blend.One};
        public static readonly BlendState Subtract=new(){ColorSourceBlend=Blend.SourceAlpha,ColorDestinationBlend=Blend.One,ColorBlendFunction=BlendFunction.ReverseSubtract,
            AlphaSourceBlend=Blend.Zero,AlphaDestinationBlend=Blend.One};
        // The field library documents mode 0 ("Unk0") as seemingly identical to
        // Add and mode 7 ("Unk7") the same way, while mode 6 ("Unk6") resembles
        // Normal. Mode 0 was previously treated as unsupported, which silently
        // removed whole authored layers (many c7510 lightning tracers use it).
        // Draw it additively instead and keep the approximation reported.
        public static BlendState State(int mode,bool premultiplied)=>mode switch {
            1=>BlendState.Opaque,2 or 6=>premultiplied?BlendState.AlphaBlend:BlendState.NonPremultiplied,
            3=>Multiply,0 or 4 or 7=>BlendState.Additive,5=>Subtract,_=>null
        };
        public static bool IgnoresAlpha(int mode)=>mode is 1 or 3;
    }
}
