using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace DSAnimStudio
{
    // A separate stream keeps the original skinned mesh untouched. Only meshes
    // matched by authored cloth buffer indices may supply deformed vertices.
    public sealed class ClothMeshBuffer : IDisposable
    {
        readonly FlverShaderVertInput[] original, deformed;
        DynamicVertexBuffer buffer;
        bool uploaded;
        public VertexBuffer Buffer => uploaded ? buffer : null;
        public int VertexCount => original.Length;
        public ClothMeshBuffer(VertexBuffer source)
        {
            original = new FlverShaderVertInput[source.VertexCount];
            source.GetData(original); // Once, after an authored mapping is validated.
            deformed = (FlverShaderVertInput[])original.Clone();
        }
        public static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
        static bool Finite(Matrix m) => Finite(m.Right) && Finite(m.Up) && Finite(m.Backward) && Finite(m.Translation)
            && float.IsFinite(m.M14) && float.IsFinite(m.M24) && float.IsFinite(m.M34) && float.IsFinite(m.M44);
        public static FlverShaderVertInput DeformTransported(FlverShaderVertInput source, Vector3 position, Vector3? normal=null, Vector3? tangent=null, Matrix? frameDelta=null)
        {
            var n=source.Normal;var t=source.Bitangent.XYZ();
            if(frameDelta.HasValue)
            {
                if(!Finite(frameDelta.Value))throw new ArgumentException("Cloth direction transport contains non-finite geometry.");
                n=Vector3.TransformNormal(n,frameDelta.Value);t=Vector3.TransformNormal(t,frameDelta.Value);
            }
            return Deform(source,position,normal??n,tangent??t);
        }
        public static FlverShaderVertInput Deform(FlverShaderVertInput source, Vector3 position, Vector3 normal, Vector3 tangent)
        {
            if (!Finite(position) || !Finite(normal) || !Finite(tangent))
                throw new ArgumentException("Cloth vertex contains non-finite geometry.");
            var oldNormal = BulletMath.Unit(source.Normal, Vector3.Up);
            var oldTangent = BulletMath.Unit(source.Bitangent.XYZ() - oldNormal * Vector3.Dot(source.Bitangent.XYZ(), oldNormal), Vector3.Right);
            var oldBinormal = Vector3.Cross(oldNormal, oldTangent);
            var n = BulletMath.Unit(normal, oldNormal);
            var t = BulletMath.Unit(tangent - n * Vector3.Dot(tangent, n),
                BulletMath.Unit(oldTangent - n * Vector3.Dot(oldTangent, n), Math.Abs(n.Y) < .9f ? Vector3.Cross(n, Vector3.Up) : Vector3.Cross(n, Vector3.Right)));
            var b = Vector3.Cross(n, t);
            var t2old = source.Bitangent2.XYZ();
            var t2 = BulletMath.Unit(t * Vector3.Dot(t2old, oldTangent) + b * Vector3.Dot(t2old, oldBinormal) + n * Vector3.Dot(t2old, oldNormal), t);
            source.Position = position;
            source.Normal = n;
            source.Bitangent = new Vector4(t, source.Bitangent.W);
            source.Binormal = b * source.Bitangent.W;
            source.Bitangent2 = new Vector4(t2, source.Bitangent2.W);
            source.Binormal2 = Vector3.Cross(n, t2) * source.Bitangent2.W;
            // FlverShader.SkinVert explicitly bypasses skinning for zero total
            // weight: these vertices are already in animated model space.
            source.BoneWeights = Vector4.Zero;
            return source;
        }
        public void SetFrame(int[] indices, Vector3[] positions, Vector3[] normals, Vector3[] tangents, Matrix[] frameDeltas=null)
        {
            if (positions.Length != VertexCount || normals != null && normals.Length != VertexCount || tangents != null && tangents.Length != VertexCount || frameDeltas != null && frameDeltas.Length != VertexCount)
                throw new ArgumentException("Cloth mesh output lengths differ.");
            foreach(int vertex in indices)
                if ((uint)vertex >= original.Length || !Finite(positions[vertex]) || normals != null && !Finite(normals[vertex]) || tangents != null && !Finite(tangents[vertex]) || frameDeltas != null && !Finite(frameDeltas[vertex]))
                    throw new ArgumentException("Cloth mesh output is invalid.");
            bool changed = !uploaded;
            for (int i = 0; i < indices.Length; i++)
            {
                int vertex = indices[i];
                if ((uint)vertex >= original.Length) throw new ArgumentOutOfRangeException(nameof(indices));
                var next = DeformTransported(original[vertex], positions[vertex], normals?[vertex], tangents?[vertex], frameDeltas?[vertex]);
                if (next != deformed[vertex]) changed = true;
                deformed[vertex] = next;
            }
            if (!changed) return;
            buffer ??= new DynamicVertexBuffer(GFX.Device, typeof(FlverShaderVertInput), original.Length, BufferUsage.WriteOnly);
            buffer.SetData(deformed, 0, deformed.Length, SetDataOptions.Discard);
            uploaded = true;
        }
        public void Dispose() { buffer?.Dispose(); buffer = null; uploaded = false; }
    }
}
