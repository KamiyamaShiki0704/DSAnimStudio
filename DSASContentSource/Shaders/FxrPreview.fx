// FXR surface pass. Interpolate atlas samples BEFORE scene alpha blending.
#include "FxrLightShaft.fxh"
#include "FxrMultiTexture.fxh"
#include "FxrDepth.fxh"
float4x4 WorldViewProjection;
float4 Tint;
float4 AtlasCurrent;
float4 AtlasNext;
float FrameBlend;
float4 UvTransform;
float2 TextureSize;
float2 AlphaThresholds;
float PremultiplyAlpha;
float LayerMode;
int Layer2Operation, Layer3Operation;
bool ModernLayers;
bool LayerFirstAlpha;
bool NormalizeLayerMask;
bool ClampLayerAlpha;
float Octagonal;
float4 Layer1Color, Layer2Color, Layer3Color;
float4 Layer1Uv, Layer2Uv, Layer3Uv;
texture SurfaceTexture;
texture Layer2Texture;
texture Layer3Texture;
sampler Layer2Sampler = sampler_state
{
    Texture = <Layer2Texture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = WRAP; AddressV = WRAP;
};
sampler Layer3Sampler = sampler_state
{
    Texture = <Layer3Texture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = WRAP; AddressV = WRAP;
};
sampler SurfaceSampler = sampler_state {
    Texture = <SurfaceTexture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = WRAP; AddressV = WRAP;
};
struct Input { float4 Position : POSITION0; float4 Color : COLOR0; float2 Uv : TEXCOORD0; };
struct Output { float4 Position : SV_POSITION; float4 Color : COLOR0; float2 Uv : TEXCOORD0; float2 BaseUv : TEXCOORD1; float2 SoftDepth : TEXCOORD2; };
Output VertexMain(Input v) {
    Output o;
    o.Position = mul(v.Position, WorldViewProjection);
    o.SoftDepth=0;
    o.Color = v.Color * Tint;
    o.Uv = v.Uv * UvTransform.zw + UvTransform.xy;
    o.BaseUv = v.Uv;
    return o;
}
Output VertexSurface(Input v) {
    Output o=VertexMain(v);
    if(HasSceneDepth && (SoftDepthRadius>0 || DepthOffset!=0)) {
        float distance=FxrViewDepth(o.Position.z/o.Position.w,DepthProjection);
        float front=max(1e-5,distance-SoftDepthRadius-DepthOffset);
        o.SoftDepth=float2(front,SoftDepthRadius);
        o.Position.z=FxrClipDepth(front,DepthProjection)*o.Position.w;
    }
    return o;
}
float2 AtlasUv(float2 uv, float4 frame) {
    if (frame.z < 1 || frame.w < 1) {
        // Keep bilinear taps inside the current cell; never sample a neighbour.
        float2 inset = min(0.5 / TextureSize, frame.zw * 0.49);
        return clamp(frame.xy + frac(uv) * frame.zw, frame.xy + inset, frame.xy + frame.zw - inset);
    }
    return uv;
}
float4 PixelMain(Output v) : SV_TARGET {
    if (Octagonal > 0.5) clip(0.70710678 - abs(v.BaseUv.x - 0.5) - abs(v.BaseUv.y - 0.5));
    float2 uv = LayerMode < 0 ? v.Uv : v.BaseUv * Layer1Uv.zw + Layer1Uv.xy;
    float4 a = tex2D(SurfaceSampler, AtlasUv(uv, AtlasCurrent));
    float4 b = tex2D(SurfaceSampler, AtlasUv(uv, AtlasNext));
    float4 color = lerp(a, b, FrameBlend);
    if (LayerMode >= 0) {
        color = FxrTextureLayerSample(color, float4(Layer1Color.rgb,ModernLayers && !LayerFirstAlpha ? 1 : Layer1Color.a));
        float4 layer2 = FxrTextureLayerSample(tex2D(Layer2Sampler, v.BaseUv * Layer2Uv.zw + Layer2Uv.xy), Layer2Color);
        float4 layer3 = FxrTextureLayerSample(tex2D(Layer3Sampler, v.BaseUv * Layer3Uv.zw + Layer3Uv.xy), Layer3Color);
        color = ModernLayers
            ? FxrBlendTexturesModern(color,layer2,layer3,(int)LayerMode,Layer2Operation,Layer3Operation,PremultiplyAlpha>.5,NormalizeLayerMask,ClampLayerAlpha)
            : FxrBlendTextures(color, layer2, layer3, (int)LayerMode, Layer2Operation, Layer3Operation);
    }
    color.a = saturate((color.a - AlphaThresholds.x) / max(1e-6, 1 - AlphaThresholds.x));
    color *= v.Color;
    clip(color.a - AlphaThresholds.y);
    // Modern multi-texture shaders premultiply the texture layers before their
    // blend. Keep the preview's separate particle tint/fade premultiplied too.
    if (LayerMode>=0 && ModernLayers && PremultiplyAlpha>.5) color.rgb*=v.Color.a;
    color.rgb *= lerp(1, color.a, LayerMode>=0 && ModernLayers ? 0 : PremultiplyAlpha);
    if(HasSceneDepth && SoftDepthRadius>0) {
        float fade=FxrSoftIntersection(FxrViewDepth(ReadSceneDepth(v.Position.xy),DepthProjection),v.SoftDepth.x,v.SoftDepth.y);
        color.a*=fade;
        if(PremultiplyAlpha>.5) color.rgb*=fade;
    }
    if(HasSceneDepth && FlareOcclusion.w>0) color*=FxrSourceVisibility(FlareOcclusion.xy,FlareOcclusion.z);
    // The viewport is floating-point HDR. Texture alpha was bounded before
    // tinting, but authored alpha multipliers can raise the final product above
    // one. Never let inverse-source-alpha extrapolate the background negative.
    // Keep HDR RGB (including any premultiplied radiance) unchanged.
    return float4(color.rgb, saturate(color.a));
}
float4 DepthCopyPixel(Output v):SV_TARGET {return RawSceneDepth.Load(int3(v.Position.xy,0));}
float4 DepthResolvePixel(Output v):SV_TARGET {
    float value=1;
    for(int i=0;i<DepthSamples;i++) value=min(value,RawSceneDepthMs.Load(int2(v.Position.xy),i));
    return value;
}
technique FxrDepthCopy {pass P0 {VertexShader=compile vs_4_0 VertexMain();PixelShader=compile ps_4_0 DepthCopyPixel();}}
technique FxrDepthResolve {pass P0 {VertexShader=compile vs_4_0 VertexMain();PixelShader=compile ps_4_1 DepthResolvePixel();}}
float4x4 ModelWorld, ModelNormalMatrix;
float3 ModelCamera;
float3 ModelDiffuseColorMultiplier = float3(1,1,1);
float3 ModelSpecularColorMultiplier = float3(1,1,1);
float3 ModelEmissiveColorMultiplier = float3(1,1,1);
float3 ModelSurfaceColorMultiplier = float3(1,1,1);
float4 ModelGraph;
float3 ModelFalloffInside,ModelFalloffOutside;
float2 ModelFalloffParameters;
#include "FxrFalloffMaterial.fxh"
float ModelAuthoredNormalFrame;
#include "FxrModelNormals.fxh"
#include "FxrGhostMaterial.fxh"
float3 ModelGhostEdgeColor;
float2 ModelGhostEdgeParameters;
float ModelSecondaryEmission;
float4 ModelMaterialModes, ModelLayerFlags;
float2 ModelPrimaryMaps;
float4 ModelAddress2, ModelAddress3, ModelTexel2, ModelTexel3;
float4 ModelFlags;
float ModelDither;
float4 ModelAddress0, ModelAddress1;
float4 ModelTexel0, ModelTexel1;
int ModelLightCount;
float4x4 ModelLightInverse[16];
float4 ModelLightPosition[16], ModelLightDiffuse[16], ModelLightSpecular[16], ModelLightVolume[16];
texture ModelSpecularTexture;
sampler ModelSpecularSampler = sampler_state {
    Texture = <ModelSpecularTexture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = WRAP; AddressV = WRAP;
};
texture ModelAlbedo2Texture, ModelNormal2Texture, ModelSpecular2Texture, ModelMaskTexture;
sampler ModelAlbedo2Sampler=sampler_state {Texture=<ModelAlbedo2Texture>;MinFilter=LINEAR;MagFilter=LINEAR;MipFilter=LINEAR;AddressU=WRAP;AddressV=WRAP;};
sampler ModelNormal2Sampler=sampler_state {Texture=<ModelNormal2Texture>;MinFilter=LINEAR;MagFilter=LINEAR;MipFilter=LINEAR;AddressU=WRAP;AddressV=WRAP;};
sampler ModelSpecular2Sampler=sampler_state {Texture=<ModelSpecular2Texture>;MinFilter=LINEAR;MagFilter=LINEAR;MipFilter=LINEAR;AddressU=WRAP;AddressV=WRAP;};
sampler ModelMaskSampler=sampler_state {Texture=<ModelMaskTexture>;MinFilter=LINEAR;MagFilter=LINEAR;MipFilter=LINEAR;AddressU=WRAP;AddressV=WRAP;};
struct ModelInput {
    float4 Position : POSITION0; float4 Color : COLOR0;
    float3 Normal : NORMAL0; float4 Tangent : TANGENT0; float4 Tangent2 : TANGENT1;
    float2 Uv : TEXCOORD0; float2 EmissiveUv : TEXCOORD1; float2 NormalUv : TEXCOORD2; float2 SpecularUv : TEXCOORD3;
    float2 Albedo2Uv:TEXCOORD4;float2 Normal2Uv:TEXCOORD5;float2 Specular2Uv:TEXCOORD6;float2 MaskUv:TEXCOORD7;
};
struct ModelOutput {
    float4 Position : SV_POSITION; float4 Color : COLOR0;
    float3 Normal : TEXCOORD0; float4 Tangent : TEXCOORD1; float3 WorldPosition : TEXCOORD2;
    float2 Uv : TEXCOORD3; float2 EmissiveUv : TEXCOORD4; float2 NormalUv : TEXCOORD5; float2 SpecularUv : TEXCOORD6;
    float4 LayerUv:TEXCOORD7;float4 LayerUv2:TEXCOORD8;float LayerWeight:TEXCOORD9;
    float4 Tangent2:TEXCOORD10;
};
ModelOutput ModelVertex(ModelInput v) {
    ModelOutput o;
    o.Position = mul(v.Position, WorldViewProjection);
    o.WorldPosition = mul(v.Position, ModelWorld).xyz;
    o.Normal = mul(float4(v.Normal,0),ModelNormalMatrix).xyz;
    o.Tangent = float4(mul(float4(v.Tangent.xyz,0),ModelWorld).xyz,v.Tangent.w * sign(determinant((float3x3)ModelWorld)));
    float4 t2=dot(v.Tangent2.xyz,v.Tangent2.xyz)>1e-12?v.Tangent2:v.Tangent;
    o.Tangent2=float4(mul(float4(t2.xyz,0),ModelWorld).xyz,t2.w*sign(determinant((float3x3)ModelWorld)));
    o.Color = float4(lerp(v.Color.rgb,1,ModelFlags.w),ModelMaterialModes.z>.5?1:v.Color.a) * float4(Tint.rgb,ModelDither>0.5?1:Tint.a);
    o.LayerWeight=v.Color.a;
    o.LayerUv=float4(v.Albedo2Uv,v.Normal2Uv);o.LayerUv2=float4(v.Specular2Uv,v.MaskUv);
    o.Uv = v.Uv * UvTransform.zw + UvTransform.xy;
    o.EmissiveUv = v.EmissiveUv; o.NormalUv = v.NormalUv; o.SpecularUv = v.SpecularUv;
    return o;
}
float4 ShaftInner, ShaftOuter, ShaftParameters;
float2 ShaftScale, ShaftCenter, ShaftDepthOrigin;
float3 ShaftDirection;
int ShaftSamples;
texture ShaftSourceTexture;
sampler ShaftSampler = sampler_state {
    Texture = <ShaftSourceTexture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR;
    AddressU = CLAMP; AddressV = CLAMP;
};
float4 ShaftSourcePixel(Output v) : SV_TARGET {
    float visibility=HasSceneDepth?FxrSourceVisibility(v.Position.xy+ShaftDepthOrigin,v.Position.z):1;
    return LightShaftSource(v.BaseUv, ShaftInner, ShaftOuter, ShaftScale, tex2D(SurfaceSampler, v.BaseUv).a)*visibility;
}
float4 ShaftAccumulatePixel(Output v) : SV_TARGET {
    return LightShaftIntegrate(ShaftSampler, SurfaceSampler, v.BaseUv, ShaftCenter,
        ShaftDirection, ShaftParameters, ShaftSamples, false, 0, 0);
}
technique FxrLightShaftSource {
    pass P0 { VertexShader = compile vs_4_0 VertexMain(); PixelShader = compile ps_4_0 ShaftSourcePixel(); }
}
technique FxrLightShaftAccumulate {
    pass P0 { VertexShader = compile vs_4_0 VertexMain(); PixelShader = compile ps_4_0 ShaftAccumulatePixel(); }
}
float4x4 ModelBones[256], ModelBoneNormals[256];
struct SkinnedModelInput {
    float4 Position : POSITION0; float4 Color : COLOR0;
    float3 Normal : NORMAL0; float4 Tangent : TANGENT0; float4 Tangent2 : TANGENT1;
    float2 Uv : TEXCOORD0; float2 EmissiveUv : TEXCOORD1; float2 NormalUv : TEXCOORD2; float2 SpecularUv : TEXCOORD3;
    float2 Albedo2Uv:TEXCOORD4;float2 Normal2Uv:TEXCOORD5;float2 Specular2Uv:TEXCOORD6;float2 MaskUv:TEXCOORD7;
    float4 Indices : BLENDINDICES0; float4 Weights : BLENDWEIGHT0;
};
ModelOutput SkinnedModelVertex(SkinnedModelInput input) {
    ModelInput v;
    v.Position=0;v.Normal=0;v.Tangent=0;v.Tangent2=0;
    float3 bitangent=0,bitangent2=0;
    float4 sourceTangent2=dot(input.Tangent2.xyz,input.Tangent2.xyz)>1e-12?input.Tangent2:input.Tangent;
    [unroll] for(int i=0;i<4;i++) {
        int bone=(int)input.Indices[i];float weight=input.Weights[i];
        v.Position+=mul(input.Position,ModelBones[bone])*weight;
        v.Normal+=mul(float4(input.Normal,0),ModelBoneNormals[bone]).xyz*weight;
        v.Tangent.xyz+=mul(float4(input.Tangent.xyz,0),ModelBones[bone]).xyz*weight;
        bitangent+=mul(float4(cross(input.Normal,input.Tangent.xyz)*input.Tangent.w,0),ModelBones[bone]).xyz*weight;
        v.Tangent2.xyz+=mul(float4(sourceTangent2.xyz,0),ModelBones[bone]).xyz*weight;
        bitangent2+=mul(float4(cross(input.Normal,sourceTangent2.xyz)*sourceTangent2.w,0),ModelBones[bone]).xyz*weight;
    }
    v.Tangent.w=dot(cross(v.Normal,v.Tangent.xyz),bitangent)<0?-1:1;
    v.Tangent2.w=dot(cross(v.Normal,v.Tangent2.xyz),bitangent2)<0?-1:1;
    v.Color=input.Color;v.Uv=input.Uv;v.EmissiveUv=input.EmissiveUv;v.NormalUv=input.NormalUv;v.SpecularUv=input.SpecularUv;
    v.Albedo2Uv=input.Albedo2Uv;v.Normal2Uv=input.Normal2Uv;v.Specular2Uv=input.Specular2Uv;v.MaskUv=input.MaskUv;
    return ModelVertex(v);
}
float Address(float uv,float mode,float inset) {
    if(mode==2)return clamp(1-abs(frac(uv*.5)*2-1),inset,1-inset);
    if(mode==3||mode==4)return clamp(uv,inset,1-inset);
    if(mode==5)return clamp(abs(uv),inset,1-inset);
    return uv;
}
float2 MaterialUv(float2 uv,float2 mode,float2 inset) {
    return float2(Address(uv.x,mode.x,inset.x),Address(uv.y,mode.y,inset.y));
}
float3 SafeNormal(float3 v,float3 fallback) {return dot(v,v)>1e-10?normalize(v):fallback;}
float3 ModelNormal(float3 sample) {
    float2 xy=sample.xy*2-1;
    // Original GXFlver GBuf shaders store gloss in B, not normal Z.
    return ModelMaterialModes.x>.5?float3(xy,sqrt(saturate(1-dot(xy,xy)))):sample*2-1;
}
float4 ModelPixel(ModelOutput v,bool frontFace:SV_IsFrontFace) : SV_TARGET {
    if(ModelDither>0.5) {
        // Fixed ordered coverage is a preview pattern, not the native shader.
        uint2 pixel=(uint2)floor(v.Position.xy)&3;
        uint low=(((pixel.x^pixel.y)&1)*2)+(pixel.y&1);
        uint high=((((pixel.x>>1)^(pixel.y>>1))&1)*2)+((pixel.y>>1)&1);
        clip(saturate(Tint.a)-(low*4+high+0.5)/16.0);
    }
    float2 uv=MaterialUv(v.Uv,ModelAddress0.xy,ModelTexel0.xy);
    float4 color=lerp(tex2D(SurfaceSampler,AtlasUv(uv,AtlasCurrent)),tex2D(SurfaceSampler,AtlasUv(uv,AtlasNext)),FrameBlend);
    float layer=saturate(v.LayerWeight);
    if(ModelLayerFlags.w>.5) {
        // Original MulMask operation, with preview scale/bias (2,-1):
        // middle gray leaves the vertex blend unchanged before smoothstep.
        float mask=tex2D(ModelMaskSampler,MaterialUv(v.LayerUv2.zw,ModelAddress3.zw,ModelTexel3.zw)).r*2-1;
        layer=saturate(mask>0?layer*(1+mask):layer+mask*(1-layer));layer=layer*layer*(3-2*layer);
    }
    if(ModelLayerFlags.x>.5)color=lerp(color,tex2D(ModelAlbedo2Sampler,MaterialUv(v.LayerUv.xy,ModelAddress2.xy,ModelTexel2.xy)),layer);
    if(ModelGraph.x>.5)color.rgb=saturate(color.rgb*ModelSurfaceColorMultiplier);
    color.rgb *= ModelDiffuseColorMultiplier;
    float3 normal=SafeNormal(v.Normal,float3(0,1,0));
    float smoothness=.5;
    if(ModelFlags.y>0.5) {
        float3 tangent=SafeNormal(v.Tangent.xyz-normal*dot(v.Tangent.xyz,normal),float3(1,0,0));
        float3 bitangent=cross(normal,tangent)*(v.Tangent.w<0?-1:1);
        float3 packed=tex2D(Layer3Sampler,MaterialUv(v.NormalUv,ModelAddress1.xy,ModelTexel1.xy)).xyz;
        float3 sample=ModelPrimaryMaps.x>.5?ModelNormal(packed):float3(0,0,1);smoothness=ModelPrimaryMaps.x>.5?saturate(packed.z):.5;
        float3 secondSample=sample;
        if(ModelLayerFlags.y>.5) {
            float3 other=tex2D(ModelNormal2Sampler,MaterialUv(v.LayerUv.zw,ModelAddress2.zw,ModelTexel2.zw)).xyz;
            secondSample=ModelNormal(other);smoothness=lerp(smoothness,saturate(other.z),layer);
        }
        if(ModelAuthoredNormalFrame>.5) {
            normal=FxrAuthoredNormalBlend(v.Normal,v.Tangent,v.Tangent2,sample,secondSample,ModelLayerFlags.y>.5?layer:0,frontFace);
        } else {
            sample=lerp(sample,secondSample,ModelLayerFlags.y>.5?layer:0);
            if(ModelGraph.x>.5)sample.xy=sample.yx;
            normal=SafeNormal(sample.x*tangent+sample.y*bitangent+sample.z*normal,normal);
        }
    }
    float3 f0=0;
    float specPower=ModelMaterialModes.x>.5?exp2(1+10*smoothness):32;
    if(ModelFlags.z>.5) {
        float3 spec=ModelPrimaryMaps.y>.5?tex2D(ModelSpecularSampler,MaterialUv(v.SpecularUv,ModelAddress1.zw,ModelTexel1.zw)).rgb:0;
        if(ModelLayerFlags.z>.5)spec=lerp(spec,tex2D(ModelSpecular2Sampler,MaterialUv(v.LayerUv2.xy,ModelAddress3.xy,ModelTexel3.xy)).rgb,layer);
        if(ModelMaterialModes.y>.5) {float metal=saturate(spec.r+ModelGraph.y);f0=lerp(.04,color.rgb,metal);color.rgb*=1-metal;}
        else {f0=spec*ModelSpecularColorMultiplier;color.rgb*=1-saturate(f0);}
    }
    // Generic inspection lighting, not a recreation of a native material shader.
    if(ModelFlags.y>0.5||ModelFlags.z>0.5) {
        float3 light=normalize(float3(.4,.8,.3));
        color.rgb*=.4+.6*saturate(dot(normal,light));
        if(ModelFlags.z>0.5) {
            float3 view=SafeNormal(ModelCamera-v.WorldPosition,float3(0,0,1));
            float spec=pow(saturate(dot(normal,SafeNormal(light+view,light))),specPower);
            color.rgb+=f0*(.08+spec);
        }
    }
    float3 emission=ModelFlags.x>.5?tex2D(Layer2Sampler,MaterialUv(v.EmissiveUv,ModelAddress0.zw,ModelTexel0.zw)).rgb:0;
    if(ModelMaterialModes.w>.5) {
        float3 second=ModelSecondaryEmission>.5?tex2D(ModelMaskSampler,MaterialUv(v.LayerUv2.zw,ModelAddress3.zw,ModelTexel3.zw)).rgb:0;
        emission=FxrGhostEmission(emission,second,layer,ModelEmissiveColorMultiplier);
    } else emission*=ModelEmissiveColorMultiplier;
    if(ModelGraph.z>.5) {
        float3 selectedNormal=ModelGraph.w>.5?normal:SafeNormal(v.Normal,float3(0,0,1))*(frontFace?1:-1);
        float3 view=SafeNormal(ModelCamera-v.WorldPosition,float3(0,0,1));
        float mask=tex2D(ModelMaskSampler,MaterialUv(v.LayerUv2.zw,ModelAddress3.zw,ModelTexel3.zw)).r;
        emission=FxrFalloffEmission(dot(view,selectedNormal),mask,ModelFalloffInside,ModelFalloffOutside,ModelFalloffParameters);
    }
    // Resource-driven FXR lights. They currently affect this FXR geometry pass
    // only, without native shadow/deferred character/terrain lighting.
    float3 authoredLighting=0;
    [loop] for(int i=0;i<ModelLightCount;i++) {
        float3 local=mul(float4(v.WorldPosition,1),ModelLightInverse[i]).xyz;
        float4 volume=ModelLightVolume[i];
        float attenuation=pow(saturate(1-length(local)/max(volume.y,.0001)),2);
        if(ModelLightPosition[i].w>0.5) {
            float2 cone=local.xy*volume.y/(max(local.z,.0001)*max(volume.zw,.0001));
            attenuation=saturate(1-dot(cone,cone))*pow(saturate(1-local.z/volume.y),2);
            attenuation*=local.z>=volume.x&&local.z<volume.y?1:0;
        }
        float3 direction=SafeNormal(ModelLightPosition[i].xyz-v.WorldPosition,float3(0,1,0));
        float3 diffuse=color.rgb*ModelLightDiffuse[i].rgb*saturate(dot(normal,direction));
        float3 specular=0;
        if(ModelFlags.z>0.5) {
            float3 view=SafeNormal(ModelCamera-v.WorldPosition,float3(0,0,1));
            specular=f0*ModelLightSpecular[i].rgb*pow(saturate(dot(normal,SafeNormal(view+direction,direction))),specPower);
        }
        authoredLighting+=(diffuse+specular)*attenuation;
    }
    color.rgb+=authoredLighting+emission;
    if(ModelMaterialModes.w>.5)color.rgb=FxrGhostEdge(color.rgb,normal,SafeNormal(ModelCamera-v.WorldPosition,float3(0,0,1)),ModelGhostEdgeColor,ModelGhostEdgeParameters);
    color.a=saturate((color.a-AlphaThresholds.x)/max(1e-6,1-AlphaThresholds.x));
    color*=v.Color;clip(color.a-AlphaThresholds.y);
    color.rgb*=lerp(1,color.a,PremultiplyAlpha);
    // Model and skinned-model particles share the same HDR coverage contract.
    return float4(color.rgb, saturate(color.a));
}
technique FxrSurface { pass P0 {
    VertexShader = compile vs_4_0 VertexSurface();
    PixelShader = compile ps_4_0 PixelMain();
} }
technique FxrModel { pass P0 {
    VertexShader = compile vs_4_0 ModelVertex();
    PixelShader = compile ps_4_0 ModelPixel();
} }
technique FxrSkinnedModel { pass P0 {
    VertexShader = compile vs_4_0 SkinnedModelVertex();
    PixelShader = compile ps_4_0 ModelPixel();
} }

texture SceneColorTexture;
sampler SceneColorSampler = sampler_state {
    Texture = <SceneColorTexture>;
    MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = POINT;
    AddressU = CLAMP; AddressV = CLAMP;
};
float2 SceneDimensions, SceneCenter;
float4 SceneEffect, SceneNormalUv;
int SceneSamples;
float SceneAlphaCutoff;
float4 SceneColorPixel(Output v) : SV_TARGET {
    float2 screen=v.Position.xy/SceneDimensions;
    float2 local=(v.BaseUv-.5)*2;
    float coverage=tex2D(SurfaceSampler,v.BaseUv).a*tex2D(Layer3Sampler,v.BaseUv).a*v.Color.a;
    float4 source;
    if(SceneEffect.x>2.5) {
        // Multi-tap radial scene sampling, rather than a fabricated blur card.
        float2 ray=(SceneCenter-screen)*SceneEffect.y;
        source=0;
        [loop] for(int i=0;i<SceneSamples;i++)
            source+=tex2D(SceneColorSampler,screen+ray*(float(i)/max(SceneSamples-1,1)));
        source/=max(SceneSamples,1);
    } else {
        float radius=length(local)/SceneEffect.z;clip(1-radius);
        float2 displacement;
        if(SceneEffect.x<.5) {
            float angle=SceneEffect.w*(1-radius);
            float2 rotated=float2(local.x*cos(angle)-local.y*sin(angle),local.x*sin(angle)+local.y*cos(angle));
            displacement=(rotated-local)*SceneEffect.y*.02;
        } else if(SceneEffect.x<1.5) {
            float2 normal=tex2D(Layer2Sampler,v.BaseUv*SceneNormalUv.zw+SceneNormalUv.xy).xy*2-1;
            displacement=normal*SceneEffect.y*.02*(1-radius);
        } else {
            float angle=SceneEffect.y*(1-radius)*3.14159265;
            float2 rotated=float2(local.x*cos(angle)-local.y*sin(angle),local.x*sin(angle)+local.y*cos(angle));
            displacement=(rotated-local)*.02;
        }
        // Displacement is in screen UV; authored/native displacement conversion
        // is not known. Strength=0 is handled as a no-op before submission.
        source=tex2D(SceneColorSampler,screen+displacement);
    }
    coverage=saturate(coverage);clip(coverage-SceneAlphaCutoff);
    return float4(source.rgb*v.Color.rgb,coverage);
}
technique FxrSceneColor { pass P0 {
    VertexShader = compile vs_4_0 VertexMain();
    PixelShader = compile ps_4_0 SceneColorPixel();
} }
