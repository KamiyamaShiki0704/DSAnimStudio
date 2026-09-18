using DSAnimStudio.TaeEditor;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Color = Microsoft.Xna.Framework.Color;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace DSAnimStudio.ImguiOSD
{
    public abstract partial class Window
    {
        /// <summary>
        /// [Preview] Live root motion XYZ displacement readout for the currently playing animation.
        /// Data comes from <see cref="RootMotionReadout"/>.
        /// </summary>
        public partial class RootMotionInfo : Window
        {
            public override SaveOpenStateTypes GetSaveOpenStateType() => SaveOpenStateTypes.SaveAlways;

            public override string NewImguiWindowTitle => "Root Motion";

            // [Preview] This is a menu-opened tool window; do not let the base class auto-close it
            // the instant it is floating and unfocused (otherwise it just flashes on open). The user
            // can still close it via the menu toggle or the window X button, and dock it if desired.
            protected override bool AutoCloseWhenFloatingAndUnfocused => false;

            private const int PlotSampleCount = 128;

            private static readonly NVector4 ColHeader = new NVector4(1.0f, 0.85f, 0.35f, 1f);
            private static readonly NVector4 ColX = new NVector4(1.0f, 0.45f, 0.45f, 1f);
            private static readonly NVector4 ColY = new NVector4(0.50f, 1.00f, 0.50f, 1f);
            private static readonly NVector4 ColZ = new NVector4(0.50f, 0.70f, 1.00f, 1f);
            private static readonly NVector4 ColDim = new NVector4(0.70f, 0.70f, 0.70f, 1f);
            private static readonly NVector4 ColWarn = new NVector4(1.0f, 0.55f, 0.25f, 1f);
            private static readonly NVector4 ColAccent = new NVector4(0.45f, 1.00f, 0.90f, 1f);

            private bool showCurves = true;
            private bool showWorldAccum = true;

            protected override void Init()
            {
                CustomBackgroundColor = new NVector4(0.08f, 0.085f, 0.095f, 1f);
            }

            private static void SectionHeader(string text)
            {
                ImGui.Separator();
                ImGui.TextColored(ColHeader, text);
            }

            private static void Row(string label, string value, NVector4 color)
            {
                ImGui.TextColored(ColDim, label);
                ImGui.SameLine();
                ImGui.TextColored(color, value);
            }

            private static void Row(string label, string value)
            {
                Row(label, value, new NVector4(1, 1, 1, 1));
            }

            private static void XyzRows(string prefix, NVector4 v)
            {
                Row($"{prefix}X", RootMotionReadout.FmtWithUnit(v.X), ColX);
                Row($"{prefix}Y", RootMotionReadout.FmtWithUnit(v.Y), ColY);
                Row($"{prefix}Z", RootMotionReadout.FmtWithUnit(v.Z), ColZ);
            }

            private void DoPlot(string label, float[] values, NVector4 color, float progress01)
            {
                if (values == null || values.Length < 2)
                    return;

                float min = values[0];
                float max = values[0];
                for (int i = 1; i < values.Length; i++)
                {
                    if (values[i] < min) min = values[i];
                    if (values[i] > max) max = values[i];
                }

                // Always include 0 in the range so a flat channel reads as flat.
                if (min > 0) min = 0;
                if (max < 0) max = 0;
                if (Math.Abs(max - min) < 0.000001f)
                {
                    min -= 0.5f;
                    max += 0.5f;
                }

                float pad = (max - min) * 0.08f;

                var plotSize = new NVector2(ImGui.GetContentRegionAvail().X - (8 * Main.DPI), 54 * Main.DPI);
                if (plotSize.X < 60 * Main.DPI)
                    plotSize.X = 60 * Main.DPI;

                ImGui.PushStyleColor(ImGuiCol.PlotLines, color);
                ImGui.PlotLines($"##RootMotionPlot_{label}", ref values[0], values.Length, 0,
                    $"{label}   min {min:0.###}  max {max:0.###}", min - pad, max + pad, plotSize);
                ImGui.PopStyleColor();

                // Playhead marker over the plot we just drew.
                var rectMin = ImGui.GetItemRectMin();
                var rectMax = ImGui.GetItemRectMax();
                float px = rectMin.X + ((rectMax.X - rectMin.X) * Math.Clamp(progress01, 0f, 1f));
                ImGui.GetWindowDrawList().AddLine(
                    new NVector2(px, rectMin.Y),
                    new NVector2(px, rectMax.Y),
                    ImGui.GetColorU32(ColAccent),
                    Math.Max(1f, 1f * Main.DPI));
            }

            protected override void BuildContents(ref bool anyFieldFocused)
            {
                ImGui.Checkbox("Enable Root Motion simulation", ref Main.Config.RootMotionPreview_Enabled);
                ImGui.TextWrapped(Main.Config.RootMotionPreview_Enabled
                    ? "Master: scaling, target turning, character collision and viewport helpers."
                    : "Simulation OFF: no preview scaling, target turning, collision or Root Motion helpers. Shared Bullet target is also disabled. Raw animation motion keeps its existing setting.");
                var mdl = RootMotionReadout.GetMainModel();
                var r = RootMotionReadout.Get(mdl);

                // ---- Unit selector + options ----
                bool isMeters = RootMotionReadout.Unit == RootMotionReadout.UnitModes.Meters;
                if (ImGui.RadioButton("m (raw HKX)", isMeters))
                    RootMotionReadout.Unit = RootMotionReadout.UnitModes.Meters;
                ImGui.SameLine();
                if (ImGui.RadioButton("cm (x100)", !isMeters))
                    RootMotionReadout.Unit = RootMotionReadout.UnitModes.Centimeters;

                bool overlay = Main.HelperDraw.EnableRootMotionDistanceText;
                if (ImGui.Checkbox("Show in viewport", ref overlay))
                    Main.HelperDraw.EnableRootMotionDistanceText = overlay;
                ImGui.SameLine();
                ImGui.Checkbox("Curves", ref showCurves);
                ImGui.SameLine();
                ImGui.Checkbox("World", ref showWorldAccum);

                if (ImGui.Button("Copy summary"))
                    ImGui.SetClipboardText(RootMotionReadout.BuildSummaryText(in r));

                ImGui.SameLine();
                if (ImGui.Button("Reset root motion"))
                {
                    try
                    {
                        mdl?.AnimContainer?.ResetRootMotion();
                    }
                    catch
                    {
                        // Non-fatal; the model may be mid-reload.
                    }
                }

                // ---- Header / status ----
                ImGui.Separator();

                if (!r.HasAnim)
                {
                    ImGui.TextColored(ColWarn, r.StatusText ?? "No animation.");
                    return;
                }

                ImGui.TextColored(ColAccent, r.AnimName);
                Row("Duration", $"{r.Duration:0.####} s");
                Row("Frames", $"{r.FrameCount}  ({(r.FrameDuration > 0 ? (1f / r.FrameDuration) : 0f):0.##} fps)");
                Row("Time", $"{r.CurrentTime:0.####} s   frame {r.CurrentFrame:0.##}");
                if (r.LoopCount != 0)
                    Row("Loop", $"{r.LoopCount}");

                DrawEvent760(mdl);

                if (!r.HasRootMotionData)
                {
                    ImGui.Separator();
                    ImGui.PushTextWrapPos(0);
                    ImGui.TextColored(ColWarn, r.StatusText);
                    ImGui.PopTextWrapPos();
                    return;
                }

                Row("RM samples", $"{r.SampleCount}");

                // ---- Current displacement ----
                SectionHeader($"Raw HKX displacement (from start of anim)");
                XyzRows("", r.Current);
                Row("Horizontal XZ", RootMotionReadout.FmtWithUnit(r.CurDistXZ), ColAccent);
                Row("3D distance", RootMotionReadout.FmtWithUnit(r.CurDistXYZ));
                Row("Yaw", $"{r.CurrentYawDeg:0.###} deg");
                ImGui.ProgressBar(Math.Clamp(r.ProgressXZ, 0f, 1f),
                    new NVector2(ImGui.GetContentRegionAvail().X - (8 * Main.DPI), 0),
                    $"{r.ProgressXZ * 100f:0.#}% of total XZ");

                // ---- Per-frame ----
                SectionHeader("Per animation frame");
                XyzRows("d", r.DeltaPerAnimFrame);
                Row("Speed XZ", RootMotionReadout.FmtSpeed(r.SpeedXZ), ColAccent);
                Row("Speed 3D", RootMotionReadout.FmtSpeed(r.SpeedXYZ));
                Row("dYaw", $"{r.DeltaYawDegPerAnimFrame:0.###} deg");
                Row("Render dXZ", RootMotionReadout.FmtWithUnit(
                    (float)Math.Sqrt((r.DeltaPerRenderFrame.X * r.DeltaPerRenderFrame.X) +
                                     (r.DeltaPerRenderFrame.Z * r.DeltaPerRenderFrame.Z))), ColDim);

                // ---- Whole animation ----
                SectionHeader("Raw HKX whole animation total");
                XyzRows("", r.Total);
                Row("Horizontal XZ", RootMotionReadout.FmtWithUnit(r.TotalDistXZ), ColAccent);
                Row("3D distance", RootMotionReadout.FmtWithUnit(r.TotalDistXYZ));
                Row("Path length", RootMotionReadout.FmtWithUnit(r.PathLength));
                Row("Y peak", RootMotionReadout.FmtWithUnit(r.PeakY), ColY);
                Row("Y lowest", RootMotionReadout.FmtWithUnit(r.LowY), ColY);
                Row("Yaw total", $"{r.TotalYawDeg:0.###} deg");
                Row("Avg XZ speed", RootMotionReadout.FmtSpeed(r.AverageSpeedXZ));
                Row("Avg path speed", RootMotionReadout.FmtSpeed(r.AveragePathSpeed));

                // ---- Actual scaled world transform ----
                if (showWorldAccum)
                {
                    SectionHeader("Actual scaled world transform");
                    XyzRows("", r.World);
                    Row("Horizontal XZ", RootMotionReadout.FmtWithUnit(r.WorldDistXZ), ColAccent);
                    Row("Yaw", $"{r.WorldYawDeg:0.###} deg");

                    var cfg = Main.Config;
                    bool multsModified = cfg != null &&
                        (Math.Abs(cfg.RootMotionTranslationMultiplierXZ - 1f) > 0.0001f ||
                         Math.Abs(cfg.RootMotionTranslationMultiplierY - 1f) > 0.0001f ||
                         Math.Abs(cfg.RootMotionRotationMultiplier - 1f) > 0.0001f ||
                         Math.Abs(cfg.RootMotionTranslationPowerXZ - 1f) > 0.0001f ||
                         Math.Abs(cfg.RootMotionTranslationPowerY - 1f) > 0.0001f ||
                         Math.Abs(cfg.RootMotionRotationPower - 1f) > 0.0001f);

                    if (multsModified)
                    {
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(ColWarn,
                            "Root motion multiplier/power is not 1.0 (Animation menu), so the accumulated " +
                            "world values above are scaled. The sections above it are raw HKX data and are unaffected.");
                        ImGui.PopTextWrapPos();
                    }

                    if (cfg != null && !cfg.EnableAnimRootMotion)
                    {
                        ImGui.PushTextWrapPos(0);
                        ImGui.TextColored(ColWarn,
                            "\"Enable Root Motion\" is off, so the character does not actually move. " +
                            "The raw displacement numbers above are still read straight from the hkx.");
                        ImGui.PopTextWrapPos();
                    }
                }

                // ---- Curves ----
                if (showCurves)
                {
                    SectionHeader($"Curves over the animation ({RootMotionReadout.UnitSuffix})");

                    if (RootMotionReadout.TryGetCurves(mdl, PlotSampleCount,
                            out var cx, out var cy, out var cz, out var cyaw))
                    {
                        float progress = r.Duration > 0 ? (r.CurrentTime / r.Duration) : 0f;
                        DoPlot("X", cx, ColX, progress);
                        DoPlot("Y", cy, ColY, progress);
                        DoPlot("Z", cz, ColZ, progress);
                        DoPlot("Yaw (deg)", cyaw, ColHeader, progress);
                    }
                    else
                    {
                        ImGui.TextColored(ColDim, "No curve data.");
                    }
                }
            }
        }
    }
}
