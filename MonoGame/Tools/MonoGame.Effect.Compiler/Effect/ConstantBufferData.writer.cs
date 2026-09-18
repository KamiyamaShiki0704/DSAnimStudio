using System.IO;

using MonoGame.Framework.Utilities;

namespace MonoGame.Effect
{
    internal partial class ConstantBufferData
    {
        public void Write(BinaryWriter writer, Options options)
        {
            writer.Write(Name);

            // The local runtime uses 32-bit sizes and parameter offsets.
            writer.Write(Size);

            writer.Write(ParameterIndex.Count);
            for (var i=0; i < ParameterIndex.Count; i++)
            {
                writer.Write(ParameterIndex[i]);
                writer.Write(ParameterOffset[i]);
            }
        }
    }
}
