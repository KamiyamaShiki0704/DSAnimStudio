using System;
using System.Collections.Generic;
using System.Linq;
using SoulsAssetPipeline.Animation;

namespace DSAnimStudio
{
    internal static class FxrAnimationMemory
    {
        // The general HKX reader keeps section/fixup references in every array
        // and string. Runtime FXR skinning needs only this compact independent rig.
        public static HKX.HKASkeleton Detach(HKX.HKASkeleton source)
        {
            HKX.HKArray<T> Array<T>(IEnumerable<T> values) where T:HKX.IHKXSerializable,new()
                =>new(new HKX.HKArrayData<T>{Elements=values.ToList()});
            return new HKX.HKASkeleton
            {
                Name=new HKX.HKCString(source.Name?.GetString()??string.Empty),
                Bones=Array(source.Bones.GetArrayData().Elements.Select(b=>new HKX.Bone{Name=new HKX.HKCString(b.Name.GetString()),LockTranslation=b.LockTranslation})),
                ParentIndices=Array(source.ParentIndices.GetArrayData().Elements.Select(i=>new HKX.HKShort(i.data))),
                Transforms=Array(source.Transforms.GetArrayData().Elements.Select(t=>new HKX.Transform{Position=new(t.Position.Vector),Rotation=new(t.Rotation.Vector),Scale=new(t.Scale.Vector)})),
                ReferenceFloats=Array(source.ReferenceFloats?.GetArrayData()?.Elements?.Select(f=>new HKX.HKFloat{data=f.data})??Enumerable.Empty<HKX.HKFloat>())
            };
        }
        static long Text(string value)=>value==null?0:24+((value.Length*2L+7)&~7L);
        static long List<T>(List<T> values,int stride)=>values==null?0:56+values.Capacity*(long)stride;
        static long ArrayLength(Array values,int stride)=>values==null?0:24+values.LongLength*stride;
        static long Channel<T>(SplineCompressedAnimation.SplineChannel<T> channel,int stride)=>channel==null?0:32+List(channel.Values,stride);
        static long VectorTrack(SplineCompressedAnimation.SplineTrackVector3 track)=>track==null?0:56+List(track.Knots,1)+Channel(track.ChannelX,4)+Channel(track.ChannelY,4)+Channel(track.ChannelZ,4);
        public static long Skeleton(HKX.HKASkeleton skeleton)
        {
            long bytes=64+Text(skeleton.Name.GetString())+40+4*144;
            bytes+=skeleton.Bones.GetArrayData().Elements.Capacity*8L;
            foreach(var bone in skeleton.Bones.GetArrayData().Elements)bytes+=72+Text(bone.Name.GetString());
            bytes+=skeleton.ParentIndices.GetArrayData().Elements.Capacity*8L+skeleton.ParentIndices.Size*24L;
            bytes+=skeleton.Transforms.GetArrayData().Elements.Capacity*8L+skeleton.Transforms.Size*136L;
            bytes+=skeleton.ReferenceFloats.GetArrayData().Elements.Capacity*8L+skeleton.ReferenceFloats.Size*24L;
            return bytes;
        }
        // Managed-cache estimate includes allocated collection capacity and the
        // decoded knot/control-point payload, which can dwarf the compressed HKX.
        public static long Clip(HavokAnimationData clip)
        {
            long bytes=128+Text(clip.Name)+ArrayLength(clip.HkxBoneIndexToTransformTrackMap,4)+ArrayLength(clip.TransformTrackIndexToHkxBoneMap,4);
            if(clip.RootMotion!=null)bytes+=64+ArrayLength(clip.RootMotion.Frames,16);
            if(clip is HavokAnimationData_InterleavedUncompressed interleaved)return bytes+List(interleaved.Transforms,40);
            if(clip is not HavokAnimationData_SplineCompressed spline)return bytes;
            bytes+=List(spline.Tracks,8);
            foreach(var block in spline.Tracks)
            {
                bytes+=ArrayLength(block,8);
                foreach(var track in block)
                {
                    bytes+=96+VectorTrack(track.SplinePosition)+VectorTrack(track.SplineScale);
                    if(track.Mask!=null)bytes+=56+List(track.Mask.PositionTypes,4)+List(track.Mask.RotationTypes,4)+List(track.Mask.ScaleTypes,4);
                    if(track.SplineRotation!=null)bytes+=40+List(track.SplineRotation.Knots,1)+Channel(track.SplineRotation.Channel,16);
                }
            }
            return bytes;
        }
    }
}
