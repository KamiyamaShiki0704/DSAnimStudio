// Positive view-space distance from a D3D projection, including orthographic.
float FxrViewDepth(float z, float4 projection) {
    return -(projection.y-z*projection.w)/(z*projection.z-projection.x);
}
float FxrClipDepth(float distance, float4 projection) {
    return (-distance*projection.x+projection.y)/(-distance*projection.z+projection.w);
}
// Original soft-sprite PS: min(scene-front, range) / range, saturated.
float FxrSoftIntersection(float sceneDepth, float frontDepth, float range) {
    return saturate((sceneDepth-frontDepth)/max(range,1e-6));
}
Texture2D<float> RawSceneDepth;
Texture2DMS<float> RawSceneDepthMs;
Texture2D<float> SceneDepth;
int DepthSamples;
float4 DepthProjection;
float2 SceneDepthSize;
bool HasSceneDepth;
float SoftDepthRadius;
float DepthOffset;
float4 FlareOcclusion;
float ReadSceneDepth(float2 pixel) {
    return SceneDepth.Load(int3(clamp(pixel,0,SceneDepthSize-1),0));
}
float FxrSourceVisibility(float2 pixel,float sourceZ) {
    if(any(pixel<0)||any(pixel>=SceneDepthSize)||sourceZ<0||sourceZ>1)return 0;
    return sourceZ<=ReadSceneDepth(pixel)+1e-6?1:0;
}
