using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using SoulsAssetPipeline;
using SoulsFormats;

namespace DSAnimStudio
{
    public static class BulletGameSupport
    {
        public static bool Supports(SoulsGames game) => game is SoulsGames.ER or SoulsGames.ERNR or SoulsGames.SDT
            or SoulsGames.DS3 or SoulsGames.DS1R or SoulsGames.BB or SoulsGames.AC6;
        public static string Name(SoulsGames game) => game switch
        {
            SoulsGames.ER => "Elden Ring", SoulsGames.ERNR => "Nightreign", SoulsGames.SDT => "Sekiro",
            SoulsGames.DS3 => "Dark Souls III", SoulsGames.DS1R => "Dark Souls Remastered",
            SoulsGames.BB => "Bloodborne", SoulsGames.AC6 => "Armored Core VI", _ => game.ToString()
        };
        public static bool IsEmission(SoulsGames game, int eventId) => Supports(game) && (eventId is 2 or 64
            || (game == SoulsGames.SDT && eventId == 4) || (game == SoulsGames.AC6 && eventId == 8)
            || (game == SoulsGames.BB && eventId == 318) || (game == SoulsGames.DS1R && eventId == 307));
        public static string Notes(SoulsGames game) => game switch
        {
            SoulsGames.DS1R or SoulsGames.BB => "Legacy layout: terminal children supported; interval-child fields are absent. Events without a muzzle field use the configured fallback dummy.",
            SoulsGames.SDT => "Events 2 / 4; separate 16-bit spawn-position field. Howitzer aiming is approximate.",
            SoulsGames.AC6 => "Events 2 / 8; Behavior muzzle range or Bullet muzzle dummy. Basic flight/children preview; missile phases, shotgun tables, capsule/beam shapes, distance lifetime, ricochet and penetration tiers are not reproduced.",
            SoulsGames.DS3 => "Event 2; legacy penetration flag and separate child fields. LaunchType is not treated as a verified child posture.",
            _ => "Event 2; parameter-based flight, target/ground hits and child chains. Special policies remain approximations."
        };
    }

    internal sealed class BulletParamLayout
    {
        internal sealed class Field
        {
            public int Offset, Bit, Width;
            public string Type;
        }
        public int RowSize;
        public Dictionary<string, Field> Fields;
        private static readonly Dictionary<string, BulletParamLayout> layouts = Load();
        private static Dictionary<string, BulletParamLayout> Load()
        {
            using var stream = typeof(BulletParamLayout).Assembly.GetManifestResourceStream("DSAnimStudio.EmbRes.BulletLayouts.json");
            using var reader = new StreamReader(stream ?? throw new InvalidOperationException("Bullet layouts resource missing."));
            return JsonConvert.DeserializeObject<Dictionary<string, BulletParamLayout>>(reader.ReadToEnd());
        }
        internal static BulletParamLayout For(SoulsGames game) => layouts.TryGetValue(game.ToString(), out var result)
            ? result : throw new NotSupportedException($"No Bullet layout for {BulletGameSupport.Name(game)}.");
        internal double Read(BinaryReaderEx reader, long start, long rowLength, string name, double fallback = 0)
        {
            if (!Fields.TryGetValue(name.ToLowerInvariant(), out var f)) return fallback;
            int size = f.Type is "f32" or "s32" or "u32" ? 4 : f.Type is "s16" or "u16" ? 2 : 1;
            if (f.Offset + size > rowLength) throw new InvalidDataException($"Bullet row too short for {name}: need {f.Offset + size}, got {rowLength} bytes.");
            long p = start + f.Offset;
            if (f.Width > 0) return (reader.GetByte(p) >> f.Bit) & ((1 << f.Width) - 1);
            return f.Type switch
            {
                // Force a double common type: otherwise the switch promotes integer
                // arms to float first, rounding IDs above 2^24 before returning double.
                "f32" => (double)reader.GetSingle(p), "s32" => reader.GetInt32(p), "u32" => reader.GetUInt32(p),
                "s16" => reader.GetInt16(p), "u16" => reader.GetUInt16(p), "s8" => reader.GetSByte(p), "u8" => reader.GetByte(p),
                _ => throw new InvalidDataException($"Unsupported Bullet field type {f.Type}.")
            };
        }
    }
}
