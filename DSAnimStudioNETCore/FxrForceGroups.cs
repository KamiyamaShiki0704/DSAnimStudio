using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    // ER 141D22D10 / 141D22BE0: root actions 10100 / 10400. These are
    // signed integer shifts, not normalized booleans. Keep all 64 channels.
    public readonly record struct FxrForceSourceFilter(ulong Groups,ulong Targets,int Priority);
    public static class FxrForceGroups
    {
        static int Field(JObject action,int index,int fallback=0)=>(int?)action?["unk_ds3_f1_"+index]??fallback;
        public static FxrForceSourceFilter Source(JObject action)
        {
            int channel=Field(action,23,-1);
            ulong groups=channel<0?ulong.MaxValue:1UL<<(channel&63),targets=0;
            for(int i=0;i<3;i++)if(Field(action,i+1,1)!=0)targets|=1UL<<i;
            for(int i=0;i<10;i++)targets|=unchecked((ulong)(long)Field(action,13+i))<<(32+i);
            return new(groups,targets,Field(action,0));
        }
        public static ulong Receiver(JObject action)
        {
            if(Field(action,0,1)!=0)return ulong.MaxValue;
            ulong groups=0;
            for(int i=0;i<64;i++)groups|=unchecked((ulong)(long)Field(action,i+1,1))<<i;
            return groups;
        }
        public static bool Matches(ulong source,ulong receiver)=>(source&receiver)!=0;
    }
}
