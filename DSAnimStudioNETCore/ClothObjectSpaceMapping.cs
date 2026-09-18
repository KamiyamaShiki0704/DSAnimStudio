using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace DSAnimStudio
{
    public sealed record ClothObjectSpaceWeight(int Frame,float Weight);
    public sealed record ClothObjectSpaceVertex(int Index,Vector3 Position,ClothObjectSpaceWeight[] Weights,float RestTolerance=.00025f);

    /// <summary>Shared packfile/tagfile math for authored object-space P deformation.</summary>
    public static class ClothObjectSpaceMapping
    {
        public static Vector3 UnpackPosition(short x,short y,short z,ushort exponent)
        {
            float scale=BitConverter.Int32BitsToSingle(unchecked(exponent<<16))*65536f;
            return new Vector3(x,y,z)*scale;
        }

        public static bool RestFramesMatch(int[] subset,Matrix[] triangleFromMesh,int[] triangles,Vector3[] rest,out float error)
        {
            error=0;if(subset.Length==0||subset.Length!=triangleFromMesh.Length)return false;
            for(int n=0;n<subset.Length;n++)
            {
                long at=(long)subset[n]*3;if(at<0||at+2>=triangles.Length)return false;
                int a=triangles[at],b=triangles[at+1],c=triangles[at+2];
                if((uint)a>=rest.Length||(uint)b>=rest.Length||(uint)c>=rest.Length)return false;
                var m=triangleFromMesh[n]*ClothPreviewMeshBinding.TriangleFrame(rest[a],rest[b],rest[c]);
                float e=Math.Max(Math.Max(Math.Max(Math.Abs(m.M11-1),Math.Abs(m.M12)),Math.Max(Math.Abs(m.M13),Math.Abs(m.M14))),
                    Math.Max(Math.Max(Math.Abs(m.M21),Math.Abs(m.M22-1)),Math.Max(Math.Abs(m.M23),Math.Abs(m.M24))));
                e=Math.Max(e,Math.Max(Math.Max(Math.Abs(m.M31),Math.Abs(m.M32)),Math.Max(Math.Abs(m.M33-1),Math.Abs(m.M34))));
                e=Math.Max(e,Math.Max(Math.Max(Math.Abs(m.M41),Math.Abs(m.M42)),Math.Max(Math.Abs(m.M43),Math.Abs(m.M44-1))));
                if(!float.IsFinite(e))return false;error=Math.Max(error,e);
            }
            // This is a reconstruction check on authored inverse bind frames,
            // not a spatial search for particles or target vertices.
            return error<=.001f;
        }

        public static ClothMeshVertex[] Convert(ClothObjectSpaceVertex[] vertices,int[] subset,Matrix[] triangleFromMesh,int[] triangles)
        {
            if(subset.Length!=triangleFromMesh.Length)throw new InvalidDataException("ObjectSpace triangle subset and inverse frames differ in length.");
            var output=new ClothMeshVertex[vertices.Length];
            for(int n=0;n<vertices.Length;n++)
            {
                var vertex=vertices[n];var influences=new ClothMeshInfluence[vertex.Weights.Length];
                for(int j=0;j<influences.Length;j++)
                {
                    var w=vertex.Weights[j];
                    if((uint)w.Frame>=subset.Length||!float.IsFinite(w.Weight)||w.Weight<0)
                        throw new InvalidDataException("ObjectSpace authored influence is outside its triangle subset.");
                    long at=(long)subset[w.Frame]*3;if(at<0||at+2>=triangles.Length)throw new InvalidDataException("ObjectSpace triangle index is outside its simulation mesh.");
                    var local=Vector4.Transform(new Vector4(vertex.Position,1),triangleFromMesh[w.Frame])*w.Weight;
                    influences[j]=new(triangles[at],triangles[at+1],triangles[at+2],local,Vector3.Zero,Vector3.Zero);
                }
                output[n]=new(vertex.Index,influences,vertex.RestTolerance);
            }
            return output;
        }
    }
}
