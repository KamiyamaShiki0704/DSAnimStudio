using System;
using System.Globalization;
using SoulsAssetPipeline;

namespace DSAnimStudio
{
    /// <summary>[Preview] Nightreign native 760 horizontal arithmetic.
    /// Event scheduling and preview guards remain separate from the native geometry.</summary>
    public sealed class Event760State
    {
        // These shipped TAE templates share the same 32-byte 760 payload.
        public static bool SupportsGame(SoulsGames? game) => game is SoulsGames.DS3
            or SoulsGames.SDT or SoulsGames.ER or SoulsGames.ERNR or SoulsGames.AC6;

        public object Action;
        public string AnimationName;
        public float StartTime, EndTime, Time;
        public bool IsEnable, Allowed = true, SimulationEnabled = true;
        public float ReferenceDist, EnableRangeMin, EnableRangeMax;
        public float ArriveAngleFromTarget, ArriveDistFromTarget;
        public bool HasTarget;
        public string DistSource = "none";
        public float DistToTarget, AngleToTargetDeg;
        public bool ManualMultiplier;
        public float ManualScale = 1, MaxScale = 50;
        public float WindowRawDistance;
        public bool HasWindowMeasurement;
        public string MeasurementSource = "HKX event window";
        public float RawScale { get; private set; } = 1;
        public float AppliedScale { get; private set; } = 1;
        public bool IsActive { get; private set; }
        public string Note { get; private set; } = "";
        public bool InsideArrivalRadius => DistToTarget < ArriveDistFromTarget;
        public float DistanceToArrivalPoint
        {
            get
            {
                // Equivalent to sqrt(D²+R²-2DR*cos(theta)), without cancellation
                // at small angles / almost equal radii. The native helper rotates
                // the actor-target direction then measures this same distance.
                double d = DistToTarget, r = ArriveDistFromTarget;
                double sinHalf = Math.Sin(ArriveAngleFromTarget * Math.PI / 360);
                return (float)Math.Sqrt((d - r) * (d - r) + 4 * d * r * sinHalf * sinHalf);
            }
        }
        public float Numerator => InsideArrivalRadius ? EnableRangeMin
            : Math.Min(Math.Max(DistanceToArrivalPoint, EnableRangeMin), EnableRangeMax);
        public bool InTimeWindow => EndTime > StartTime && Time >= StartTime && Time < EndTime;
        public float MinScale => ReferenceDist > 0.0001f ? EnableRangeMin / ReferenceDist : float.NaN;
        public float MaxNativeScale => ReferenceDist > 0.0001f ? EnableRangeMax / ReferenceDist : float.NaN;
        public float ConstantScaleTravel => WindowRawDistance * AppliedScale;

        public void Evaluate()
        {
            IsActive = false;
            RawScale = AppliedScale = 1;
            Note = "";
            if (!SimulationEnabled) Note = "simulation off";
            else if (!Allowed) Note = "event muted / excluded by solo or state";
            else if (!IsEnable) Note = "IsEnable = false";
            else if (!float.IsFinite(Time) || !float.IsFinite(StartTime) || !float.IsFinite(EndTime)) Note = "invalid event time";
            else if (!InTimeWindow) Note = "outside event window [start, end)";
            else if (!ManualMultiplier && !HasTarget) Note = "no target; choose fixed target or manual distance";
            else if (!ManualMultiplier && (!float.IsFinite(DistToTarget) || DistToTarget < 0
                || !float.IsFinite(ArriveDistFromTarget) || ArriveDistFromTarget < 0
                || !float.IsFinite(ArriveAngleFromTarget))) Note = "invalid target distance / angle";
            else if (!ManualMultiplier && (!float.IsFinite(EnableRangeMin) || !float.IsFinite(EnableRangeMax)
                || EnableRangeMin < 0 || EnableRangeMax < EnableRangeMin)) Note = "invalid numerator bounds";
            else if (!ManualMultiplier && (!float.IsFinite(ReferenceDist) || ReferenceDist <= 0.0001f))
                Note = "invalid ReferenceDist; scale bypassed";
            else if (!float.IsFinite(MaxScale)) Note = "invalid multiplier limit";
            else
            {
                RawScale = ManualMultiplier ? ManualScale : Numerator / ReferenceDist;
                if (!float.IsFinite(RawScale) || RawScale < 0)
                    Note = "invalid / negative multiplier; scale bypassed";
                else
                {
                    AppliedScale = MaxScale > 0 ? Math.Min(RawScale, MaxScale) : RawScale;
                    IsActive = true;
                    Note = AppliedScale != RawScale ? "limited by max multiplier" : ManualMultiplier ? "manual multiplier" : "";
                }
            }
        }

        private static string F(float n) => n.ToString("0.####", CultureInfo.InvariantCulture);
        public string Formula => ManualMultiplier
            ? $"manual scale = {F(ManualScale)}; applied = {F(AppliedScale)}x"
            : $"D = {F(DistToTarget)} m; R = {F(ArriveDistFromTarget)} m; theta = {F(ArriveAngleFromTarget)} deg\n"
              + (InsideArrivalRadius ? $"D < R: numerator = min = {F(EnableRangeMin)}\n"
                  : $"S = sqrt(D^2 + R^2 - 2*D*R*cos(theta)) = {F(DistanceToArrivalPoint)} m\n"
                    + $"numerator = clamp(S, {F(EnableRangeMin)}, {F(EnableRangeMax)}) = {F(Numerator)}\n")
              + $"s = {F(Numerator)} / {F(ReferenceDist)} = {F(ReferenceDist > 0.0001f ? Numerator / ReferenceDist : float.NaN)}\n"
              + $"applied = {F(AppliedScale)}x (max = {(MaxScale > 0 ? F(MaxScale) : "unlimited")})";

        public string Summary => $"Event 760 | {AnimationName} | [{F(StartTime)}, {F(EndTime)}) s | t = {F(Time)} s\n"
            + Formula + $"\n{(IsActive ? "ACTIVE" : "BYPASSED")}: {Note}\n"
            + $"Raw window XZ = {(HasWindowMeasurement ? F(WindowRawDistance) : "unavailable")} m ({MeasurementSource})\n"
            + "Window distance x current scale is a constant-scale estimate, not a dynamic endpoint prediction.";
    }
}
