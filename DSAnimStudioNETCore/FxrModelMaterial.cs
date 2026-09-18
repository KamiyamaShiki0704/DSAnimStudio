using System;
using System.IO;
using System.Linq;
using SoulsFormats;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    public sealed record FxrMaterialSampler(string Name,string Path,string Role,int UvIndex,Vector2 Scale,int AddressU,int AddressV);
    public sealed record FxrMaterialParameter
    {
        public string Name { get; }
        public string Type { get; }
        public object Value { get; }
        public uint? Key { get; }
        public string ConsumedAs { get; init; }
        public bool Consumed=>ConsumedAs!=null;
        public FxrMaterialParameter(string name,string type,object value,uint? key=null)
        {
            Name=name;Type=type;Key=key;
            Value=value switch
            {
                float[] floats=>Array.AsReadOnly((float[])floats.Clone()),
                int[] ints=>Array.AsReadOnly((int[])ints.Clone()),
                _=>value
            };
        }
    }
    public sealed record FxrMaterialProfile(string Name,string Shader,FxrMaterialSampler[] Samplers,bool AuxiliaryColor,bool DefinitionLoaded)
    {
        public Vector3 DiffuseColorMultiplier { get; init; }=Vector3.One;
        public Vector3 SpecularColorMultiplier { get; init; }=Vector3.One;
        public Vector3 EmissiveColorMultiplier { get; init; }=Vector3.One;
        public bool GlowGraph { get; init; }
        public bool FalloffGraph { get; init; }
        public bool FalloffUseNormal { get; init; }
        public Vector3 FalloffInside { get; init; }=Vector3.One;
        public Vector3 FalloffOutside { get; init; }=Vector3.One;
        public Vector2 FalloffParameters { get; init; }=new(1,0);
        public bool GhostGraph { get; init; }
        public Vector3 GhostEdgeColor { get; init; }
        public Vector2 GhostEdgeParameters { get; init; }=new(1,1);
        public Vector3 SurfaceColorMultiplier { get; init; }=Vector3.One;
        public float MetallicOffset { get; init; }
        public bool PackedNormal { get; init; }
        public bool DualLayer { get; init; }
        public bool MaskedBlend { get; init; }
        public bool AuthoredNormalFrame { get; init; }
        public IReadOnlyList<FxrMaterialParameter> Params { get; init; }=Array.AsReadOnly(Array.Empty<FxrMaterialParameter>());
        public IReadOnlyList<string> ConsumedParams { get; init; }=Array.AsReadOnly(Array.Empty<string>());
        public FxrMaterialSampler First(string role)
        {foreach(var sampler in Samplers)if(sampler.Role==role&&!string.IsNullOrWhiteSpace(sampler.Path))return sampler;return null;}
        public FxrMaterialSampler Base=>DualLayer?Named("g_DiffuseTexture"):First("Albedo")??First("Emissive");
        public FxrMaterialSampler Normal=>DualLayer?Named("g_BumpmapTexture"):First("Normal");
        public FxrMaterialSampler Reflectance=>DualLayer?Named("g_SpecularTexture"):First("Specular")??First("Metallic");
        public FxrMaterialSampler Named(string name)
        {foreach(var sampler in Samplers)if(sampler.Name==name&&!string.IsNullOrWhiteSpace(sampler.Path))return sampler;return null;}
        public FxrMaterialSampler Albedo2=>DualLayer?Named("g_DiffuseTexture2"):null;
        public FxrMaterialSampler Normal2=>DualLayer?Named("g_BumpmapTexture2"):null;
        public FxrMaterialSampler Reflectance2=>DualLayer?Named("g_SpecularTexture2"):null;
        public FxrMaterialSampler BlendMask=>MaskedBlend?Named("g_BlendMaskTexture"):null;
        public FxrMaterialSampler Emissive=>GhostGraph?Named("g_EmissiveTexture"):First("Emissive");
        public FxrMaterialSampler Emissive2=>GhostGraph?Named("g_EmissiveTexture2"):null;
        public FxrMaterialSampler AuxiliarySampler=>GhostGraph?Emissive2:FalloffGraph?Named("S_AMSN__Falloff_di_CameraFade_snp_Texture2D_3_Mask1Map"):BlendMask;
    }
    public static class FxrModelMaterial
    {
        public static string Role(string name)
        {
            string n=(name??"").ToUpperInvariant();
            if(n.Contains("DIFFUSE")||n.Contains("ALBEDO"))return "Albedo";
            if(n.Contains("EMISSIVE"))return "Emissive";
            if(n.Contains("NORMAL")||n.Contains("BUMPMAP"))return "Normal";
            if(n.Contains("METALLIC"))return "Metallic";
            if(n.Contains("SPECULAR")||n.Contains("REFLECTANCE"))return "Specular";
            if(n.Contains("SHININESS"))return "Shininess";
            if(n.Contains("BLENDMASK"))return "BlendMask";
            return "Unknown";
        }
        public static FxrMaterialProfile Resolve(FLVER2.Material material,byte[] definition=null)
        {
            var samplers=new Dictionary<string,FxrMaterialSampler>(StringComparer.OrdinalIgnoreCase);
            foreach(var t in material.Textures)
                samplers[t.ParamName??""]=new(t.ParamName??"",t.Path,Role(t.ParamName),0,new(t.TilingScale.X,t.TilingScale.Y),(int)t.TilingTypeU,(int)t.TilingTypeV);
            string shader="";
            var parameters=new List<FxrMaterialParameter>();
            if(definition!=null)
            {
                if(MATBIN.Is(definition))
                {
                    var mat=MATBIN.Read(definition);shader=mat.ShaderPath;
                    parameters.AddRange(mat.Params.Select(p=>new FxrMaterialParameter(p.Name,p.Type.ToString(),p.Value,p.Key)));
                    foreach(var t in mat.Samplers)Merge(t.Type,t.Path,0,new(t.Unk14.X==0?1:t.Unk14.X,t.Unk14.Y==0?1:t.Unk14.Y));
                }
                else
                {
                    var mat=MTD.Read(definition);shader=mat.ShaderPath;
                    parameters.AddRange(mat.Params.Select(p=>new FxrMaterialParameter(p.Name,p.Type.ToString(),p.Value)));
                    var uvNumbers=mat.Textures.Select(t=>t.UVNumber).Where(n=>n>0).Distinct().OrderBy(n=>n).ToArray();
                    foreach(var t in mat.Textures)Merge(t.Type,t.Path,Math.Max(0,Array.IndexOf(uvNumbers,t.UVNumber)),t.UnkFloats.Count==2?new(t.UnkFloats[0]==0?1:t.UnkFloats[0],t.UnkFloats[1]==0?1:t.UnkFloats[1]):Vector2.One);
                }
            }
            void Merge(string name,string path,int uv,Vector2 scale)
            {
                var existing=samplers.GetValueOrDefault(name);
                samplers[name]=new(name,string.IsNullOrWhiteSpace(path)?existing?.Path:path,Role(name),uv,scale*(existing?.Scale??Vector2.One),existing?.AddressU??1,existing?.AddressV??1);
            }
            if(!samplers.Values.Any(s=>s.Role is "Albedo" or "Emissive"&&!string.IsNullOrWhiteSpace(s.Path)))
            {
                var fallback=ColorTexture(material);
                if(fallback!=null&&samplers.TryGetValue(fallback.ParamName,out var selected))samplers[fallback.ParamName]=selected with{Role="Albedo"};
            }
            Vector3 diffuse=ColorMultiplier(parameters,"g_DiffuseMapColor","g_DiffuseMapColorPower",nameof(FxrMaterialProfile.DiffuseColorMultiplier));
            Vector3 specular=ColorMultiplier(parameters,"g_SpecularMapColor","g_SpecularMapColorPower",nameof(FxrMaterialProfile.SpecularColorMultiplier));
            Vector3 emissive=ColorMultiplier(parameters,"g_EmissiveMapColor","g_EmissiveMapColorPower",nameof(FxrMaterialProfile.EmissiveColorMultiplier));
            // Shader-graph common UV is separate from the FLVER sampler scale.
            int commonUv=parameters.FindLastIndex(p=>p.Name=="group_1_CommonUV-UVParam");
            if(commonUv>=0&&parameters[commonUv].Type=="Float2"&&parameters[commonUv].Value is IReadOnlyList<float> uv&&uv.Count==2&&uv.All(float.IsFinite))
            {
                var scale=new Vector2(uv[0],uv[1]);
                if(samplers.Values.All(s=>float.IsFinite(s.Scale.X*scale.X)&&float.IsFinite(s.Scale.Y*scale.Y)))
                {
                    foreach(var key in samplers.Keys.ToArray())samplers[key]=samplers[key] with{Scale=samplers[key].Scale*scale};
                    parameters[commonUv]=parameters[commonUv] with{ConsumedAs="Sampler UV scale"};
                }
            }
            string shaderName=Path.GetFileNameWithoutExtension(shader.Replace('\\','/'));
            // Exact graph identity: similarly named graphs have different slot meanings.
            // Original S[AMSN]_Glow GBuffer PS: color_0 * albedo, metal + float_0,
            // color_1 * float_1 * emission. Dynamic weather/decal inputs are excluded.
            bool glow=shaderName=="S[AMSN]_Glow";
            Vector3 surface=Vector3.One;float metallicOffset=0;
            if(glow)
            {
                const string prefix="S_AMSN__Glow_snp_0_";
                surface=GraphColor(parameters,prefix+"color_0",nameof(FxrMaterialProfile.SurfaceColorMultiplier));
                var glowColor=GraphColor(parameters,prefix+"color_1",nameof(FxrMaterialProfile.EmissiveColorMultiplier));
                float strength=GraphFloat(parameters,prefix+"float_1",1,"Glow emission intensity");
                var result=glowColor*strength;
                if(float.IsFinite(result.X)&&float.IsFinite(result.Y)&&float.IsFinite(result.Z))emissive=result;
                metallicOffset=GraphFloat(parameters,prefix+"float_0",0,nameof(FxrMaterialProfile.MetallicOffset));
            }
            bool falloff=shaderName=="S[AMSN]_Falloff_di_CameraFade";bool falloffNormal=false;
            var inside=Vector3.One;var outside=Vector3.One;var falloffParameters=new Vector2(1,0);
            if(falloff)
            {
                const string prefix="S_AMSN__Falloff_di_CameraFade_snp_0_";
                surface=GraphColor(parameters,prefix+"color_0",nameof(FxrMaterialProfile.SurfaceColorMultiplier));
                inside=GraphColor(parameters,prefix+"color_2",nameof(FxrMaterialProfile.FalloffInside));
                outside=GraphColor(parameters,prefix+"color_3",nameof(FxrMaterialProfile.FalloffOutside));
                metallicOffset=GraphFloat(parameters,prefix+"float_0",0,nameof(FxrMaterialProfile.MetallicOffset));
                falloffParameters=new(GraphFloat(parameters,prefix+"float_3",1,"Falloff exponent"),GraphFloat(parameters,prefix+"float_4",0,"Falloff view-normal bias"));
                int i=parameters.FindLastIndex(p=>p.Name==prefix+"bool_0");
                if(i>=0&&parameters[i].Type=="Bool"&&parameters[i].Value is bool useNormal)
                {falloffNormal=useNormal;parameters[i]=parameters[i] with{ConsumedAs="Falloff normal-map selector"};}
            }
            bool ghost=shaderName=="GXFlver_ColDifSpcBumpEmiMulIblGhost";
            var edge=ghost?ParameterVector4(parameters,"g_GhostEdgeColor",Vector4.Zero,"Ghost edge RGB (W unused)"):Vector4.Zero;
            var edgeParameters=ghost?ParameterVector4(parameters,"g_GhostEdgeParam",new(1,1,0,0),"Ghost edge offset/scale (ZW unused)"):Vector4.Zero;
            bool dual=ghost||(shaderName.StartsWith("GXFlver_ColDifSpcBumpMul",StringComparison.Ordinal)&&samplers.TryGetValue("g_DiffuseTexture2",out var secondary)&&!string.IsNullOrWhiteSpace(secondary.Path));
            return new(material.MTD,shader,samplers.Values.ToArray(),glow||falloff||HasAuxiliaryVertexColor(material,shader),definition!=null)
            {
                FalloffGraph=falloff,FalloffUseNormal=falloffNormal,FalloffInside=inside,FalloffOutside=outside,FalloffParameters=falloffParameters,
                GhostGraph=ghost,GhostEdgeColor=new(edge.X,edge.Y,edge.Z),GhostEdgeParameters=new(edgeParameters.X,edgeParameters.Y),
                GlowGraph=glow,SurfaceColorMultiplier=surface,MetallicOffset=metallicOffset,
                AuthoredNormalFrame=ghost||falloff||shaderName is "GXFlver_ColDifSpcBumpIbl" or "GXFlver_ColDifSpcBumpEmiIbl" or "GXFlver_ColDifSpcBumpMulMaskIbl",
                DiffuseColorMultiplier=diffuse,
                SpecularColorMultiplier=specular,
                EmissiveColorMultiplier=emissive,
                PackedNormal=samplers.Values.Any(s=>s.Name.StartsWith("g_BumpmapTexture",StringComparison.OrdinalIgnoreCase)||s.Name.Contains("NormalMap",StringComparison.OrdinalIgnoreCase)),
                DualLayer=dual,MaskedBlend=dual&&shaderName.Contains("MulMask",StringComparison.Ordinal),
                Params=Array.AsReadOnly(parameters.ToArray()),
                ConsumedParams=Array.AsReadOnly(parameters.Where(p=>p.Consumed).Select(p=>p.Name).ToArray())
            };
        }
        static Vector4 ParameterVector4(List<FxrMaterialParameter> parameters,string name,Vector4 fallback,string target)
        {
            int i=parameters.FindLastIndex(p=>p.Name==name);
            if(i<0||parameters[i].Type!="Float4"||parameters[i].Value is not IReadOnlyList<float> c||c.Count!=4||!c.All(float.IsFinite))return fallback;
            parameters[i]=parameters[i] with{ConsumedAs=target};return new(c[0],c[1],c[2],c[3]);
        }
        static Vector3 GraphColor(List<FxrMaterialParameter> parameters,string name,string target)
        {
            int i=parameters.FindLastIndex(p=>p.Name==name);
            if(i<0||parameters[i].Type!="Float5"||parameters[i].Value is not IReadOnlyList<float> c||c.Count!=5||!c.All(float.IsFinite))return Vector3.One;
            // MATBIN color tuple is RGBA plus power. Alpha is not surface coverage:
            // the graph's pixel program only consumes this constant's RGB lanes.
            Vector3 value=new(c[0]*c[4],c[1]*c[4],c[2]*c[4]);
            if(!float.IsFinite(value.X)||!float.IsFinite(value.Y)||!float.IsFinite(value.Z))return Vector3.One;
            parameters[i]=parameters[i] with{ConsumedAs=target+" (RGB and power; alpha unused)"};
            return value;
        }
        static float GraphFloat(List<FxrMaterialParameter> parameters,string name,float fallback,string target)
        {
            int i=parameters.FindLastIndex(p=>p.Name==name);
            if(i<0||parameters[i].Type!="Float"||parameters[i].Value is not float value||!float.IsFinite(value))return fallback;
            parameters[i]=parameters[i] with{ConsumedAs=target};return value;
        }
        static Vector3 ColorMultiplier(List<FxrMaterialParameter> parameters,string colorName,string powerName,string target)
        {
            int colorIndex=parameters.FindLastIndex(p=>p.Name==colorName);
            int powerIndex=parameters.FindLastIndex(p=>p.Name==powerName);
            Vector3 color=Vector3.One;
            float power=1;
            bool hasColor=false,hasPower=false;
            if(colorIndex>=0&&parameters[colorIndex].Type=="Float3"&&
                parameters[colorIndex].Value is IReadOnlyList<float> rgb&&rgb.Count==3&&rgb.All(float.IsFinite))
            {
                color=new(rgb[0],rgb[1],rgb[2]);hasColor=true;
            }
            if(powerIndex>=0&&parameters[powerIndex].Type=="Float"&&
                parameters[powerIndex].Value is float strength&&float.IsFinite(strength))
            {
                power=strength;hasPower=true;
            }
            Vector3 result=color*power;
            if(!float.IsFinite(result.X)||!float.IsFinite(result.Y)||!float.IsFinite(result.Z))return Vector3.One;
            if(hasColor)parameters[colorIndex]=parameters[colorIndex] with{ConsumedAs=target};
            if(hasPower)parameters[powerIndex]=parameters[powerIndex] with{ConsumedAs=target};
            return result;
        }
        public static FLVER2.Texture ColorTexture(FLVER2.Material material)
        {
            // Some MATBIN-backed FLVERs declare samplers with empty paths.
            // They must keep the FXR-only fallback instead of becoming a
            // missing-texture layer and disappearing from the renderer.
            var textures=material.Textures.Where(t=>!string.IsNullOrWhiteSpace(t.Path)).ToArray();
            return textures.FirstOrDefault(t=>t.ParamName?.Equals("g_DiffuseTexture",StringComparison.OrdinalIgnoreCase)==true)
                ??textures.FirstOrDefault(t=>t.ParamName?.Contains("Diffuse",StringComparison.OrdinalIgnoreCase)==true)
                ??textures.FirstOrDefault(t=>t.ParamName?.Contains("Emissive",StringComparison.OrdinalIgnoreCase)==true)
                ??textures.FirstOrDefault(t=>t.Path.Contains("_em",StringComparison.OrdinalIgnoreCase))
                ??textures.FirstOrDefault(t=>t.Path.Contains("_a.",StringComparison.OrdinalIgnoreCase));
        }
        public static bool HasAuxiliaryVertexColor(FLVER2.Material material,string shader=null)
        {
            string name=Path.GetFileNameWithoutExtension(material.MTD?.Replace('\\','/')??"");
            // Original GXFlver_Col forward PS in DS3/SDT/ER/NR/AC6 reads
            // COLOR1.w only. Its RGB is not a diffuse tint. Resolve by shader,
            // including material variants such as S[A]_add_GXFlver; keep alpha
            // for authored edge fade. Lit ColDifSpcBump families instead use
            // RGB/alpha as surface and layer controls (original GBuffer PS).
            return string.Equals(Path.GetFileName(shader?.Replace('\\','/')??""),"GXFlver_Col.spx",StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(shader?.Replace('\\','/')??"").StartsWith("GXFlver_ColDifSpcBump",StringComparison.OrdinalIgnoreCase)
                || name.Contains("ShiningStone",StringComparison.OrdinalIgnoreCase) || name is "S[A]" or "S[A]_al";
        }
    }
}
