float FxrFalloffWeight(float viewNormal,float2 parameters) {
    return pow(1-saturate(viewNormal-parameters.y),max(parameters.x,.001));
}
float3 FxrFalloffEmission(float viewNormal,float mask,float3 inside,float3 outside,float2 parameters) {
    return mask*lerp(inside,outside,FxrFalloffWeight(viewNormal,parameters));
}
