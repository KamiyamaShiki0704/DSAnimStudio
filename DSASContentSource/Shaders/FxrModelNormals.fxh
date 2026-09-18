float3 FxrFrameUnit(float3 v,float3 fallback) {return dot(v,v)>1e-12?normalize(v):fallback;}
// GXFlver GBuffer normal construction: R along B, G along T.
// Back faces reverse the surface normal axis, not the two tangent axes.
float3 FxrAuthoredNormal(float3 normal,float4 tangent,float3 sample,bool frontFace) {
    float3 n=FxrFrameUnit(normal,float3(0,0,1));
    float3 t=FxrFrameUnit(tangent.xyz,float3(1,0,0));
    float3 b=FxrFrameUnit(cross(normal,tangent.xyz)*tangent.w,float3(0,1,0));
    return sample.x*b+sample.y*t+sample.z*(frontFace?n:-n);
}
float3 FxrAuthoredNormalBlend(float3 normal,float4 tangent,float4 tangent2,float3 first,float3 second,float weight,bool frontFace) {
    return FxrFrameUnit(lerp(FxrAuthoredNormal(normal,tangent,first,frontFace),FxrAuthoredNormal(normal,tangent2,second,frontFace),weight),FxrFrameUnit(normal,float3(0,0,1)));
}
