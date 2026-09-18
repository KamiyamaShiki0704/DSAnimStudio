using System;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    public static class FxrTransformMotion
    {
        public static Matrix Rotation(Vector3 degrees)=>Matrix.CreateRotationZ(MathHelper.ToRadians(degrees.Z))*
            Matrix.CreateRotationX(MathHelper.ToRadians(degrees.X))*Matrix.CreateRotationY(MathHelper.ToRadians(degrees.Y));

        // Static node angles have already been normalized by the decoder.
        // Runtime spin is a separate operation: ER 14213B770 negates X and
        // composes a delta matrix on the side selected by its first flag.
        public static Matrix Spin(JToken movement,float age,int seed,Matrix initial)
        {
            float Angle(string axis)=>FxrParticleSurface.Integrate(movement?["angularSpeed"+axis],movement?["angularSpeedMultiplier"+axis],age,seed);
            var delta=Rotation(new(-Angle("X"),Angle("Y"),Angle("Z")));
            return (int?)movement?["unk_ds3_f1_0"]==1?initial*delta:delta*initial;
        }
        public static Vector3 Translation(JToken movement,float age,int seed)
        {
            int type=(int?)movement?["type"]??0;
            if(type is not (1 or 83 or 106 or 113 or 120 or 121 or 122 or 123))return Vector3.Zero;
            bool controlledSpeed=type is 120 or 121 or 122 or 123;
            float travel=controlledSpeed?FxrParticleSurface.Integrate(movement["speedZ"],movement["speedMultiplierZ"],age,seed)
                :FxrPreviewValue.Get(movement["speedZ"],0,seed)*Math.Max(0,age)
                    +FxrParticleSurface.Integrate(movement["accelerationZ"],movement["accelerationMultiplierZ"],age,seed,displacement:true);
            return new(0,type is 1 or 113 or 120 or 123?-FxrParticleSurface.Integrate(movement["accelerationY"],null,age,seed,displacement:true):0,travel);
        }
    }
}
