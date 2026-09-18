// DS3 GXFfxLightShaftProcedual[Mask] / Texture / Dust pixel programs.
// Kept separate so the production functions can be compared against original DXBC.
float4 LightShaftSource(float2 uv, float4 innerColor, float4 outerColor, float2 scale, float mask) {
    float radius = min(length((uv * 2 - 1) * scale), 1);
    float4 color = lerp(innerColor, outerColor, radius);
    color.a *= mask;
    return float4(color.rgb * color.a, color.a);
}
float4 LightShaftIntegrate(sampler2D source, sampler2D dust, float2 uv, float2 center,
    float3 direction, float4 parameters, int samples, bool useDust, float4 dustUv, float dustThreshold) {
    if (samples <= 0) return 0;
    float2 ray = direction.z < 0
        ? direction.xy + abs(direction.z) * (uv - center - direction.xy)
        : direction.xy + direction.z * (center - uv - direction.xy);
    float2 stepUv = ray * (parameters.z / samples);
    // These zero-axis bounds are intentional; the original shader masks its
    // integer conversion with abs(step)>0. Do not replace with an infinite bound.
    int nx = abs(stepUv.x) > 0 ? (int)(stepUv.x > 0 ? uv.x / stepUv.x : (1 - uv.x) / abs(stepUv.x)) : 0;
    int ny = abs(stepUv.y) > 0 ? (int)(stepUv.y > 0 ? uv.y / stepUv.y : (1 - uv.y) / abs(stepUv.y)) : 0;
    int count = min(samples, min(nx, ny));
    float4 sum = 0;
    float illumination = 1;
    [loop] for (int i = 0; i < count; i++) {
        uv -= stepUv;
        float4 color = tex2Dlod(source, float4(uv, 0, 0));
        color.rgb *= color.rgb;
        if (useDust) {
            float mask = tex2Dlod(dust, float4((uv - .5) * dustUv.zw + dustUv.xy, 0, 0)).a;
            color.rgb *= saturate(1 - mask * max(0, dustThreshold) * saturate(-direction.z));
        }
        sum += color * (illumination * parameters.y);
        illumination *= parameters.x;
    }
    return float4(max(0, sum.rgb) * parameters.w, saturate(sum.a));
}
