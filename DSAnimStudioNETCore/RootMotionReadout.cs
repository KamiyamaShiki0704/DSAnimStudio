using System;
using SoulsAssetPipeline.Animation;
using NVector4 = System.Numerics.Vector4;

namespace DSAnimStudio
{
    /// <summary>
    /// [Preview] Live root motion displacement readout.
    ///
    /// Samples the currently playing animation's hkaDefaultAnimatedReferenceFrame data
    /// (<see cref="RootMotionData"/>) and exposes XYZ displacement numbers so they can be
    /// shown live in an ImGui window and/or as 3D text in the viewport.
    ///
    /// All raw values are in native HKX units, which is meters for every FromSoft game
    /// (matches the existing "0.000 m" attack-distance readout in Model.cs).
    /// <see cref="Unit"/> lets the user flip to centimeters (x100) which is what the
    /// UE side of the Nightreign pipeline uses.
    ///
    /// NOTE: NewHavokAnimation.RootMotionTransformDelta is a dead field (only ever zeroed,
    /// never written), so per-frame deltas are computed here instead of read from it.
    /// </summary>
    public static class RootMotionReadout
    {
        public enum UnitModes
        {
            Meters = 0,
            Centimeters = 1,
        }

        /// <summary>
        /// Not persisted in the config on purpose - it's a display preference that is
        /// cheap to re-pick and the default (meters) matches the raw HKX data.
        /// </summary>
        public static UnitModes Unit = UnitModes.Meters;

        public static float UnitScale => Unit == UnitModes.Centimeters ? 100f : 1f;
        public static string UnitSuffix => Unit == UnitModes.Centimeters ? "cm" : "m";
        private static string ValFmt => Unit == UnitModes.Centimeters ? "0.00" : "0.0000";

        public static string Fmt(float rawValue)
        {
            return (rawValue * UnitScale).ToString(ValFmt);
        }

        public static string FmtWithUnit(float rawValue)
        {
            return $"{Fmt(rawValue)} {UnitSuffix}";
        }

        public static string FmtSpeed(float rawValuePerSecond)
        {
            return $"{(rawValuePerSecond * UnitScale).ToString(ValFmt)} {UnitSuffix}/s";
        }

        public struct Readout
        {
            public bool HasModel;
            public bool HasAnim;
            public bool HasRootMotionData;

            public string AnimName;
            public string StatusText;

            public float Duration;
            public int FrameCount;
            public float FrameDuration;
            public float CurrentTime;
            public float CurrentFrame;
            public int LoopCount;

            /// <summary>Number of root motion samples stored in the hkx (usually FrameCount).</summary>
            public int SampleCount;

            /// <summary>
            /// Displacement from the animation's own start, in animation-local space.
            /// W = yaw in radians. This is "how far this animation has moved the character so far".
            /// </summary>
            public NVector4 Current;

            /// <summary>Current minus the sample one animation frame earlier (local space).</summary>
            public NVector4 DeltaPerAnimFrame;

            /// <summary>Actual delta applied on the last rendered frame (world space, from the player).</summary>
            public NVector4 DeltaPerRenderFrame;

            /// <summary>Whole-animation displacement: last sample minus first sample (local space).</summary>
            public NVector4 Total;

            /// <summary>Accumulated world-space root motion of the model (includes loops + the config multipliers).</summary>
            public NVector4 World;

            /// <summary>Arc length of the whole root motion polyline (>= |Total| when the path curves).</summary>
            public float PathLength;

            /// <summary>Highest / lowest Y relative to the first sample, over the whole animation.</summary>
            public float PeakY;
            public float LowY;

            public float CurDistXZ => Len2(Current.X, Current.Z);
            public float CurDistXYZ => Len3(Current.X, Current.Y, Current.Z);
            public float TotalDistXZ => Len2(Total.X, Total.Z);
            public float TotalDistXYZ => Len3(Total.X, Total.Y, Total.Z);
            public float WorldDistXZ => Len2(World.X, World.Z);

            public float SpeedXZ => FrameDuration > 0
                ? Len2(DeltaPerAnimFrame.X, DeltaPerAnimFrame.Z) / FrameDuration
                : 0f;

            public float SpeedXYZ => FrameDuration > 0
                ? Len3(DeltaPerAnimFrame.X, DeltaPerAnimFrame.Y, DeltaPerAnimFrame.Z) / FrameDuration
                : 0f;

            public float AverageSpeedXZ => Duration > 0 ? TotalDistXZ / Duration : 0f;
            public float AveragePathSpeed => Duration > 0 ? PathLength / Duration : 0f;

            /// <summary>How much of the animation's total XZ displacement has happened so far, 0..1.</summary>
            public float ProgressXZ => TotalDistXZ > 0.00001f ? (CurDistXZ / TotalDistXZ) : 0f;

            public float CurrentYawDeg => ToDeg(Current.W);
            public float TotalYawDeg => ToDeg(Total.W);
            public float WorldYawDeg => ToDeg(World.W);
            public float DeltaYawDegPerAnimFrame => ToDeg(DeltaPerAnimFrame.W);
        }

        private static float Len2(float a, float b) => (float)Math.Sqrt((a * a) + (b * b));
        private static float Len3(float a, float b, float c) => (float)Math.Sqrt((a * a) + (b * b) + (c * c));
        private static float ToDeg(float radians) => radians * (180f / (float)Math.PI);

        public static Model GetMainModel()
        {
            try
            {
                return zzz_DocumentManager.CurrentDocument?.Scene?.MainModel;
            }
            catch
            {
                return null;
            }
        }

        public static Readout Get()
        {
            return Get(GetMainModel());
        }

        public static Readout Get(Model mdl)
        {
            var result = new Readout();
            result.StatusText = "No model loaded.";

            if (mdl == null)
                return result;

            var container = mdl.AnimContainer;
            if (container == null)
                return result;

            result.HasModel = true;
            result.World = container.RootMotionTransform.GetRootMotionVector4();

            NewHavokAnimation anim = null;
            try
            {
                anim = container.CurrentAnimation;
            }
            catch
            {
                anim = null;
            }

            if (anim == null)
            {
                result.StatusText = "No animation playing.";
                return result;
            }

            result.HasAnim = true;
            result.AnimName = anim.Name ?? "?";
            result.Duration = anim.Duration;
            result.FrameCount = anim.FrameCount;
            result.FrameDuration = anim.FrameDuration;
            result.CurrentTime = anim.CurrentTime;
            result.CurrentFrame = anim.CurrentFrame;
            result.LoopCount = anim.LoopCount;

            var player = anim.RootMotion;
            if (player != null)
                result.DeltaPerRenderFrame = container.LastAppliedRootMotionDelta;

            var data = player?.Data;
            if (data?.Frames == null || data.Frames.Length == 0)
            {
                result.StatusText = "This animation has no root motion data (no hkaDefaultAnimatedReferenceFrame).";
                return result;
            }

            result.HasRootMotionData = true;
            result.StatusText = null;
            result.SampleCount = data.Frames.Length;

            var first = data.FirstFrame;

            // Current displacement relative to the start of this animation.
            result.Current = data.GetSampleClamped(anim.CurrentTime) - first;

            // Delta vs one animation frame ago (deterministic, works while scrubbing too).
            float prevTime = anim.CurrentTime - anim.FrameDuration;
            if (prevTime < 0)
                prevTime = 0;
            result.DeltaPerAnimFrame = result.Current - (data.GetSampleClamped(prevTime) - first);

            // Whole-animation totals.
            result.Total = data.LastFrame - first;

            float pathLen = 0;
            float peakY = 0;
            float lowY = 0;
            for (int i = 0; i < data.Frames.Length; i++)
            {
                float relY = data.Frames[i].Y - first.Y;
                if (relY > peakY)
                    peakY = relY;
                if (relY < lowY)
                    lowY = relY;

                if (i > 0)
                {
                    var a = data.Frames[i - 1];
                    var b = data.Frames[i];
                    pathLen += Len3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
                }
            }

            result.PathLength = pathLen;
            result.PeakY = peakY;
            result.LowY = lowY;

            return result;
        }

        /// <summary>
        /// Per-axis curve of the animation's root motion, resampled to <paramref name="sampleCount"/>
        /// points across the whole duration. Used for the plots in the Root Motion window.
        /// Values are already scaled to the currently selected display unit.
        /// </summary>
        public static bool TryGetCurves(Model mdl, int sampleCount,
            out float[] x, out float[] y, out float[] z, out float[] yawDeg)
        {
            x = y = z = yawDeg = null;

            if (sampleCount < 2)
                return false;

            var data = mdl?.AnimContainer?.CurrentAnimation?.RootMotion?.Data;
            if (data?.Frames == null || data.Frames.Length == 0)
                return false;

            float duration = data.Duration;
            var first = data.FirstFrame;

            x = new float[sampleCount];
            y = new float[sampleCount];
            z = new float[sampleCount];
            yawDeg = new float[sampleCount];

            float scale = UnitScale;

            for (int i = 0; i < sampleCount; i++)
            {
                float t = duration * (i / (float)(sampleCount - 1));
                var s = data.GetSampleClamped(t) - first;
                x[i] = s.X * scale;
                y[i] = s.Y * scale;
                z[i] = s.Z * scale;
                yawDeg[i] = ToDeg(s.W);
            }

            return true;
        }

        /// <summary>
        /// Compact 2-line string for the viewport overlay.
        /// </summary>
        public static string BuildViewportOverlayText(in Readout r)
        {
            if (!r.HasRootMotionData)
                return null;

            string u = UnitSuffix;
            return
                $"Raw HKX  X {Fmt(r.Current.X)}  Y {Fmt(r.Current.Y)}  Z {Fmt(r.Current.Z)} {u}\n" +
                $"XZ {Fmt(r.CurDistXZ)} / {Fmt(r.TotalDistXZ)} {u}  ({r.ProgressXZ * 100f:0.#}%)   Yaw {r.CurrentYawDeg:0.##}\u00B0\n" +
                $"Scaled world X {Fmt(r.World.X)}  Y {Fmt(r.World.Y)}  Z {Fmt(r.World.Z)} {u}";
        }

        /// <summary>
        /// Full multi-line summary, for the "Copy" button.
        /// </summary>
        public static string BuildSummaryText(in Readout r)
        {
            if (!r.HasAnim)
                return r.StatusText ?? "";

            string u = UnitSuffix;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Animation: {r.AnimName}");
            sb.AppendLine($"Duration: {r.Duration:0.####} s | Frames: {r.FrameCount} | RootMotion samples: {r.SampleCount}");
            sb.AppendLine($"Time: {r.CurrentTime:0.####} s (frame {r.CurrentFrame:0.##}) | Loop: {r.LoopCount}");

            if (!r.HasRootMotionData)
            {
                sb.AppendLine(r.StatusText);
                return sb.ToString();
            }

            sb.AppendLine();
            sb.AppendLine($"-- Current displacement (from start of anim, {u}) --");
            sb.AppendLine($"X = {Fmt(r.Current.X)}");
            sb.AppendLine($"Y = {Fmt(r.Current.Y)}");
            sb.AppendLine($"Z = {Fmt(r.Current.Z)}");
            sb.AppendLine($"Horizontal (XZ) = {Fmt(r.CurDistXZ)} | 3D = {Fmt(r.CurDistXYZ)}");
            sb.AppendLine($"Yaw = {r.CurrentYawDeg:0.###} deg");

            sb.AppendLine();
            sb.AppendLine($"-- Whole animation total ({u}) --");
            sb.AppendLine($"X = {Fmt(r.Total.X)}");
            sb.AppendLine($"Y = {Fmt(r.Total.Y)}");
            sb.AppendLine($"Z = {Fmt(r.Total.Z)}");
            sb.AppendLine($"Horizontal (XZ) = {Fmt(r.TotalDistXZ)} | 3D = {Fmt(r.TotalDistXYZ)}");
            sb.AppendLine($"Path length = {Fmt(r.PathLength)}");
            sb.AppendLine($"Y peak = {Fmt(r.PeakY)} | Y lowest = {Fmt(r.LowY)}");
            sb.AppendLine($"Yaw total = {r.TotalYawDeg:0.###} deg");
            sb.AppendLine($"Avg XZ speed = {FmtSpeed(r.AverageSpeedXZ)} | Avg path speed = {FmtSpeed(r.AveragePathSpeed)}");

            sb.AppendLine();
            sb.AppendLine($"-- Per animation frame ({u}) --");
            sb.AppendLine($"dX = {Fmt(r.DeltaPerAnimFrame.X)} | dY = {Fmt(r.DeltaPerAnimFrame.Y)} | dZ = {Fmt(r.DeltaPerAnimFrame.Z)}");
            sb.AppendLine($"Speed XZ = {FmtSpeed(r.SpeedXZ)} | Speed 3D = {FmtSpeed(r.SpeedXYZ)}");

            sb.AppendLine();
            sb.AppendLine($"-- Accumulated world transform (includes looping + multipliers, {u}) --");
            sb.AppendLine($"X = {Fmt(r.World.X)} | Y = {Fmt(r.World.Y)} | Z = {Fmt(r.World.Z)}");
            sb.AppendLine($"Horizontal (XZ) = {Fmt(r.WorldDistXZ)} | Yaw = {r.WorldYawDeg:0.###} deg");

            return sb.ToString();
        }
    }
}
