// Layer algebra from original DS3/SDT DXBC and ER/NR/AC6 DXIL shaders.
// Color/lighting, fog, soft intersection and final exposure are separate stages.
float4 FxrTextureLayerSample(float4 texel,float4 tint) {
    texel.a=saturate(texel.a);
    return texel*tint;
}
float4 FxrBlendTextureLayer(float4 baseColor, float4 layer, bool alphaMask, int operation, bool modern, bool normalizeMask) {
    if (alphaMask) {
        float magnitude = length(layer.rgb);
        float3 maskColor=normalizeMask ? (magnitude <= .00001 ? 0 : layer.rgb / magnitude) : layer.rgb;
        float brightness = saturate(dot(maskColor,float3(1.0/3,1.0/3,1.0/3)));
        float mask = brightness * (modern ? 1 : layer.a);
        if (operation == 0) baseColor.a *= lerp(1, mask, layer.a);
        else if (operation == 1) baseColor.a += mask * layer.a;
        else if (operation == 2) {
            float overlay = baseColor.a < .5 ? 2 * baseColor.a * mask : 1 - 2 * (1 - baseColor.a) * (1 - mask);
            baseColor.a = lerp(baseColor.a, overlay, layer.a);
        }
    } else {
        if (operation == 0) baseColor.rgb *= lerp(1, layer.rgb, layer.a);
        else if (operation == 1) baseColor.rgb += layer.rgb * layer.a;
        else if (operation == 2) {
            float3 a = baseColor.rgb * (modern ? 1 : baseColor.a), b = layer.rgb * (modern ? 1 : layer.a);
            float3 overlay = float3(a.x < .5 ? 2*a.x*b.x : 1-2*(1-a.x)*(1-b.x),
                a.y < .5 ? 2*a.y*b.y : 1-2*(1-a.y)*(1-b.y),
                a.z < .5 ? 2*a.z*b.z : 1-2*(1-a.z)*(1-b.z));
            baseColor.rgb = lerp(baseColor.rgb, overlay, layer.a);
        }
    }
    return baseColor;
}
float4 FxrBlendTextures(float4 first, float4 second, float4 third, int mode, int operation2, int operation3) {
    first = FxrBlendTextureLayer(first, second, mode == 1 || mode == 2, operation2, false, true);
    return FxrBlendTextureLayer(first, third, mode == 1, operation3, false, true);
}
float4 FxrBlendTexturesModern(float4 first, float4 second, float4 third, int mode, int operation2, int operation3, bool premultiply, bool normalizeMask, bool clampAlpha) {
    // Nightreign clamps tinted layer alpha; ER/AC6/SDT retain it for blending.
    if(clampAlpha) {first.a=saturate(first.a);second.a=saturate(second.a);third.a=saturate(third.a);}
    if(premultiply) {first.rgb*=first.a;second.rgb*=second.a;third.rgb*=third.a;}
    first = FxrBlendTextureLayer(first, second, mode == 1 || mode == 2, operation2, true, normalizeMask);
    return FxrBlendTextureLayer(first, third, mode == 1, operation3, true, normalizeMask);
}
