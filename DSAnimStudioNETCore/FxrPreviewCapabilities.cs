using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public sealed record FxrActionCapability(int Id,string Name,bool Executed,string Limitation);
    public static class FxrPreviewCapabilities
    {
        static readonly string[] Names = {
            "0:None", "1:NodeAcceleration", "11:Unk11", "15:NodeTranslation", "34:NodeSpin", "35:StaticNodeTransform", "36:RandomNodeTransform", "46:NodeAttachToCamera",
            "55:ParticleAcceleration", "60:ParticleSpeed", "64:ParticleSpeedRandomTurns", "65:ParticleSpeedPartialFollow", "75:NodeSound", "81:EmissionSound",
            "83:NodeAccelerationRandomTurns", "84:ParticleAccelerationRandomTurns", "105:ParticleAccelerationPartialFollow", "106:NodeAccelerationPartialFollow", "113:NodeAccelerationSpin",
            "120:NodeSpeed", "121:NodeSpeedRandomTurns", "122:NodeSpeedPartialFollow", "123:NodeSpeedSpin", "128:NodeAttributes", "129:ParticleAttributes", "130:Unk130",
            "131:ParticleModifier", "132:SFXReference", "133:LevelsOfDetailThresholds", "199:StateConfigMap", "200:SelectAllNodes", "201:SelectRandomNode",
            "300:PeriodicEmitter", "301:EqualDistanceEmitter", "399:OneTimeEmitter", "400:PointEmitterShape", "401:DiskEmitterShape", "402:RectangleEmitterShape",
            "403:SphereEmitterShape", "404:BoxEmitterShape", "405:CylinderEmitterShape", "500:NoSpread", "501:CircularSpread", "502:EllipticalSpread", "503:RectangularSpread",
            "600:PointSprite", "601:Line", "602:QuadLine", "603:BillboardEx", "604:MultiTextureBillboardEx", "605:Model", "606:LegacyTracer", "607:Distortion", "608:RadialBlur", "609:PointLight",
            "700:SimulateTermination", "701:FadeTermination", "702:InstantTermination", "731:NodeForceSpeed", "732:ParticleForceSpeed", "733:NodeForceAcceleration", "734:ParticleForceAcceleration", "800:ParticleForceCollision",
            "10000:GPUStandardParticle", "10001:GPUStandardCorrectParticle", "10002:Unk10002_Fluid", "10003:LightShaft", "10008:GPUSparkParticle", "10009:GPUSparkCorrectParticle", "10010:Unk10010_Tracer",
            "10012:Tracer", "10013:WaterInteraction", "10014:LensFlare", "10015:RichModel", "10100:Unk10100", "10200:CancelForce", "10300:WindForce", "10301:GravityForce", "10302:ForceCollision", "10303:TurbulenceForce", "10400:Unk10400", "10500:Unk10500", "11000:SpotLight", "20000:Unk20000_GPUBillboard"
        };
        public static readonly IReadOnlyDictionary<int,FxrActionCapability> Actions = Names.Select(n=>n.Split(':')).ToDictionary(n=>int.Parse(n[0]),n=>Describe(int.Parse(n[0]),n[1]));
        static FxrActionCapability Describe(int id,string name)
        {
            string missing=id switch {
                11 or 130 or 10002 or 10010 or 20000=>"Native action semantics are unknown.",
                75 or 81=>"Audio event/resource playback is not connected.",

                10013=>"Water-world interaction is outside the selected preview scope.",

                _=>null
            };
            string partial=id switch {
                10003=>"Original light-source/accumulation shader with scene-depth source occlusion; temporal visibility, camera-state blending and dust scheduling remain incomplete.",
                10100=>"Original force-group selection is supported; target-category flags, priority and external game receivers remain incomplete.",
                10400=>"Original 64-channel receiver mask is applied to preview force fields; external game receivers remain incomplete.",
                731 or 732 or 733 or 734 or 10200 or 10300 or 10301=>"Shared finite-volume force preview; native modulation and force-driven field feedback remain incomplete.",
                10302=>"Force-receiver volume overlap, not a solid collision surface; local volume conventions are approximate.",
                10303=>"Deterministic curl-noise force preview; native noise and random scheduling are unverified.",
                800=>"Swept particle collisions against explicit preview geometry; game terrain and native GPU depth collision are not loaded.",
                607 or 608=>"Masked scene-color sampling with one GPU snapshot per frame; native displacement scale, overlap composition and soft depth are approximate.",
                609 or 11000=>"Resource-driven lights on FXR models; main character/terrain lighting, shadows and volumetrics are not reconstructed.",
                10014=>"Four-layer screen-space lens flare with source depth test; native occlusion queries and temporal visibility remain incomplete.",
                133=>"Distance-threshold LOD preview; native boundary/switch scheduling is unverified.",
                10000 or 10001=>"CPU internal-emitter preview with original atlas and versioned trace geometry; native GPU collision, frame/camera history, parameter upload and scheduling remain incomplete.",
                10008 or 10009=>"CPU internal-emitter preview; native GPU collision, curve clocks, axes and scheduling remain incomplete.",
                1 or 15 or 34 or 83 or 106 or 113 or 120 or 121 or 122 or 123=>"Preview integration; native alignment flags and coupled motion remain unverified.",
                55 or 60 or 64 or 65 or 84 or 105=>"Deterministic preview motion; native random sequence and coupled gravity remain unverified.",
                300 or 301=>"Bounded preview scheduling; native emission lifecycle remains unverified.",
                >=400 and <=503 when id is not (400 or 500)=>"Deterministic shape sampling; native random distribution remains unverified.",
                605 or 10015=>"Original model HKX skinning, packed normal/reflectance and common layered materials; special shaders, additive base layers and soft depth remain approximate.",
                606 or 10012=>"Original center/width subdivision and projected-fold opacity; opacity re-evaluates per preview frame, attachment timing remains approximate.",
                604=>"Original per-game RGB/alpha layer operations and shared scene-depth fading; final lighting/color blend/exposure and native soft-sprite extent remain incomplete.",
                603=>"Preview material/geometry, atlas animation and scene-depth fading; native shader variants and soft-sprite extent remain incomplete.",
                600 or 601 or 602=>"Preview material/geometry and atlas animation; native shaders and soft depth remain incomplete.",
                128=>"Depth bias is not applied.",199=>"Only time/literal/termination state conditions are available.",10500=>"Only clock rate and prewarm are available.",
                _=>null
            };
            return new(id,name,missing==null,missing??partial);
        }
        public static IEnumerable<FxrActionCapability> Used(JObject root)=>root.DescendantsAndSelf().OfType<JObject>()
            .Where(o=>o["type"]?.Type==JTokenType.Integer).Select(o=>(int)o["type"]).Distinct().Where(Actions.ContainsKey).Select(i=>Actions[i]).OrderBy(a=>a.Id);
        public static bool Drawable(int id)=>id is 600 or 601 or 602 or 603 or 604 or 605 or 606 or 607 or 608 or 609 or 11000 or 10014 or 10003 or 10012 or 10015 or 10000 or 10001 or 10008 or 10009;
    }
}
