using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace DSAnimStudio
{
    public static class MagicBulletResolver
    {
        public sealed record Candidate(int MagicId, string Name, int MotionType, string References)
        {
            public int TaeCategory => 400 + MotionType;
        }

        // Event 64 stores a slot, while the equipped spell is runtime state.
        public static int[] GetReferenceSlots(SoulsAssetPipeline.SoulsGames game, IEnumerable<DSAProj.Action> actions)
        {
            return actions.Where(a => a.Type == 64 && a.ParameterBytes != null &&
                    a.ParameterBytes.Length >= (game == SoulsAssetPipeline.SoulsGames.DS1R ? 8 : 9))
                .Select(a => game == SoulsAssetPipeline.SoulsGames.DS1R ? 0 : (int)a.ParameterBytes[8])
                .Distinct().OrderBy(i => i).ToArray();
        }

        public static IReadOnlyList<Candidate> ListCandidates(zzz_ParamManagerIns manager, IEnumerable<int> referenceSlots, out string message, int? taeCategory = null)
        {
            var result = new List<Candidate>();
            var requested = referenceSlots.Distinct().OrderBy(i => i).ToArray();
            bool noCastEvents = requested.Length == 0;
            if (noCastEvents && !taeCategory.HasValue) { message = "No Event 64 reference slots in this selection."; return result; }
            string game = manager?.ParentDocument?.GameRoot?.GameType.ToString();
            if (game == null || !layouts.TryGetValue(game, out var layout))
            { message = "No supported Magic parameter layout."; return result; }
            if (noCastEvents) requested = Enumerable.Range(0, layout.Slots.Length).ToArray();
            var slots = requested.Where(i => i >= 0 && i < layout.Slots.Length).ToArray();
            var table = manager.GetParam("Magic") ?? manager.GetParam("MagicParam");
            if (table == null) { message = "No Magic parameter table loaded."; return result; }
            lock (table)
            {
                foreach (var row in table.Rows.OrderBy(r => r.ID))
                {
                    var reader = table.GetRowReader(row);
                    long start = reader.Position;
                    if (layout.MotionTypeOffset < 0 || table.DetectedSize <= layout.MotionTypeOffset) continue;
                    int motionType = reader.GetByte(start + layout.MotionTypeOffset);
                    if (taeCategory.HasValue && 400 + motionType != taeCategory.Value) continue;
                    var references = new List<string>();
                    foreach (int index in slots)
                    {
                        var slot = layout.Slots[index];
                        if (table.DetectedSize < Math.Max(slot.IdOffset + slot.IdSize, slot.CategoryOffset + 1)) continue;
                        if (reader.GetByte(start + slot.CategoryOffset) != 1) continue;
                        int id = slot.IdSize == 2 ? reader.GetInt16(start + slot.IdOffset) : reader.GetInt32(start + slot.IdOffset);
                        if (id >= 0) references.Add($"refId{index + 1} -> Bullet {id}");
                    }
                    if (references.Count > 0)
                        result.Add(new Candidate(row.ID, row.Name ?? "", motionType, string.Join("; ", references)));
                }
            }
            message = $"{result.Count} compatible spells | " + string.Join(", ", requested.Select(i => $"refId{i + 1}"));
            if (taeCategory.HasValue) message = $"TAE {taeCategory}: 400 + refType {taeCategory - 400} | " + message;
            if (noCastEvents) message += " | No Event 64 in this selection; showing all Bullet slots for the matching motion.";
            if (slots.Length != requested.Length) message += " | Some event slots are unavailable in this game's layout.";
            return result;
        }

        private sealed class Slot { public int IdOffset, IdSize, CategoryOffset; }
        private sealed class Layout { public int RowSize; public int MotionTypeOffset = -1; public Slot[] Slots; public int[] SfxOffsets; }
        private static readonly Dictionary<string,Layout> layouts=Load();
        private static Dictionary<string,Layout> Load()
        {
            using var stream=typeof(MagicBulletResolver).Assembly.GetManifestResourceStream("DSAnimStudio.EmbRes.MagicLayouts.json");
            using var reader=new StreamReader(stream);
            return JsonConvert.DeserializeObject<Dictionary<string,Layout>>(reader.ReadToEnd());
        }

        public static bool TryResolve(zzz_ParamManagerIns manager,int magicId,int slotIndex,out int bulletId,out string message)
        {
            bulletId=-1;
            if(magicId<0) { message="Event 64: select Highlighted Magic ID in the Bullet panel; the animation does not store the equipped spell.";return false; }
            string game=manager?.ParentDocument?.GameRoot?.GameType.ToString();
            if(game==null || !layouts.TryGetValue(game,out var layout)) { message="Event 64: unsupported Magic parameter layout.";return false; }
            if(slotIndex<0 || slotIndex>=layout.Slots.Length)
            { message=$"Event 64: Magic reference slot {slotIndex} is unavailable for this game's layout ({layout.Slots.Length} slot(s)).";return false; }
            var table=manager.GetParam("Magic")??manager.GetParam("MagicParam");
            if(table==null) { message="Event 64: no Magic parameter table loaded.";return false; }
            lock(table)
            {
                var row=table.Rows.FirstOrDefault(r=>r.ID==magicId);
                if(row==null) { message=$"Event 64: Magic {magicId} is missing from the loaded table.";return false; }
                var slot=layout.Slots[slotIndex];
                if(table.DetectedSize<Math.Max(slot.IdOffset+slot.IdSize,slot.CategoryOffset+1))
                    throw new InvalidDataException("Magic row is too short for the selected reference slot.");
                var reader=table.GetRowReader(row);long start=reader.Position;
                int category=reader.GetByte(start+slot.CategoryOffset);
                if(category!=1) { message=$"Magic {magicId}, refId{slotIndex+1}: category {category} is not Bullet (Attack / SpEffect references are not emitted).";return false; }
                // Read integer IDs directly; never round them through float.
                bulletId=slot.IdSize==2?reader.GetInt16(start+slot.IdOffset):reader.GetInt32(start+slot.IdOffset);
                if(bulletId<0) { message=$"Magic {magicId}, refId{slotIndex+1}: empty Bullet reference.";return false; }
                message=$"Magic {magicId}{(string.IsNullOrWhiteSpace(row.Name)?"":" "+row.Name)} / refId{slotIndex+1} -> Bullet {bulletId}";
                return true;
            }
        }
        public static bool TryResolveSfx(zzz_ParamManagerIns manager,int magicId,int slot,out int sfx,out string message)
        {
            sfx=-1;message=null;
            string game=manager?.ParentDocument?.GameRoot?.GameType.ToString();
            if(magicId<0){message="Casting FXR: select Highlighted Magic ID in Player magic.";return false;}
            if(game==null || !layouts.TryGetValue(game,out var layout) || slot<0 || slot>=(layout.SfxOffsets?.Length??0) || layout.SfxOffsets[slot]<0)
            {message=$"Casting FXR slot {slot}: no verified Magic layout for this game.";return false;}
            var table=manager.GetParam("Magic")??manager.GetParam("MagicParam");
            if(table==null){message="Casting FXR: Magic parameters are not loaded.";return false;}
            lock(table)
            {
                var row=table.Rows.FirstOrDefault(r=>r.ID==magicId);int offset=layout.SfxOffsets[slot];
                if(row==null || table.DetectedSize<offset+4){message=$"Casting FXR: Magic {magicId} / field is missing.";return false;}
                // GetRowReader returns the table's shared reader, not an owned stream.
                var reader=table.GetRowReader(row);sfx=reader.GetInt32(reader.Position+offset);
                return sfx>=0;
            }
        }
    }
}
