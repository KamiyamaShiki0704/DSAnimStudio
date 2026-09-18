using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SoulsAssetPipeline;
using SoulsFormats;

namespace DSAnimStudio
{
    // FOOT_SFX_PARAM_ST rows select the floor material; sfxId_N selects the
    // foot-effect identifier from TAE 112. Neither index is a direct FXR ID.
    public static class FloorFxrResolver
    {
        public sealed record Material(int Id,string Name);
        sealed class TableData
        {
            public readonly Dictionary<int,uint[]> Rows=new();
            public readonly Material[] Materials;
            public TableData(PARAM_Hack table)
            {
                lock(table)
                {
                    int count=(int)table.DetectedSize/4;
                    foreach(var row in table.Rows)
                    {
                        var reader=table.GetRowReader(row);
                        Rows[row.ID]=reader.ReadUInt32s(count);
                    }
                    Materials=table.Rows.OrderBy(r=>r.ID).Select(r=>new Material(r.ID,r.Name??"")).ToArray();
                }
            }
        }
        static readonly ConditionalWeakTable<PARAM_Hack,TableData> cache=new();
        static TableData Get(zzz_ParamManagerIns manager,out string notice)
        {
            notice=null;
            var game=manager?.ParentDocument?.GameRoot?.GameType;
            int fields=game switch {SoulsGames.ER or SoulsGames.ERNR or SoulsGames.SDT or SoulsGames.DS3=>200, SoulsGames.AC6=>211,_=>0};
            if(fields==0){notice="TAE 112: no verified FootSfxParam layout for this game.";return null;}
            if(!manager.AreParamsLoaded()){notice="TAE 112: FootSfxParam is not loaded.";return null;}
            var table=manager.GetParam("FootSfxParam");
            if(table==null||table.DetectedSize!=fields*4)
            {notice="TAE 112: FootSfxParam is missing or its row layout is unsupported.";return null;}
            return cache.GetValue(table,t=>new TableData(t));
        }
        public static IReadOnlyList<Material> Materials(zzz_ParamManagerIns manager)
            =>Get(manager,out _)?.Materials??Array.Empty<Material>();
        public static bool TryResolve(zzz_ParamManagerIns manager,int material,int slot,out int effect,out string notice)
        {
            effect=-1;
            var data=Get(manager,out notice);if(data==null)return false;
            if(!data.Rows.TryGetValue(material,out var ids))
            {notice=$"TAE 112: floor material {material} is missing from FootSfxParam.";return false;}
            if(slot<0||slot>=ids.Length)
            {notice=$"TAE 112: foot-effect index {slot} is outside FootSfxParam (0-{ids.Length-1}).";return false;}
            uint id=ids[slot];
            if(id==0||id>int.MaxValue)
            {notice=$"TAE 112: FootSfxParam {material} / sfxId_{slot:D2} has no effect.";return false;}
            effect=(int)id;
            notice=$"TAE 112: FootSfxParam {material} / sfxId_{slot:D2} -> SFX {effect}.";
            return true;
        }
    }
}
