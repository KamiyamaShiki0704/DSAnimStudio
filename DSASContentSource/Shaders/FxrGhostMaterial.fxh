float3 FxrGhostEmission(float3 first,float3 second,float weight,float3 multiplier) {
    return lerp(first,second,weight)*multiplier;
}
// Original Ghost forward PS, after lighting and emission, before scene exposure.
float3 FxrGhostEdge(float3 color,float3 normal,float3 view,float3 edge,float2 parameters) {
    float amount=saturate(max(0,1-max(0,dot(normal,view))-parameters.x)*parameters.y);
    return lerp(max(color,0),edge,amount);
}
