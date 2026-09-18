using System;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    // Common input contract for Bullet, animation notifications and future
    // persistent character effects. The renderer never has to invent a Bullet.
    public sealed record FxrPlaybackInstance(int EffectId,int Seed,float Age,float EmissionEnd,
        Func<float,Matrix> AnchorAt,string Source);
}
