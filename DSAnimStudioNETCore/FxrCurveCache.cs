using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;

namespace DSAnimStudio
{
    internal sealed class FxrCurveCache : IDisposable
    {
        [ThreadStatic] internal static FxrCurveCache Current;
        readonly FxrCurveCache previous;
        struct Entry {internal JObject Curve;internal float Time,Fallback,Value;internal int Component;}
        const int Size=4096;
        readonly Entry[] values=ArrayPool<Entry>.Shared.Rent(Size);
        // Force integration asks the same authored speed integrals for many
        // particles and historical poses. Keep exact Simpson results for this
        // Build only; neither integration spacing nor summation order changes.
        const int IntegralSlots=8192;
        struct IntegralEntry
        {
            internal JToken Curve,Multiplier;
            internal float Age,Fallback,Result;
            internal int Seed,Component;
            internal bool Displacement,Valid;
        }
        IntegralEntry[] integrals;
        readonly Dictionary<JToken,bool> seeded=new(ReferenceEqualityComparer.Instance);
        internal int IntegralCacheHits;
        bool Seeded(JToken token)
        {
            if(token is not JObject obj)return false;
            if(seeded.TryGetValue(token,out bool result))return result;
            result=obj["modifiers"] is JArray mods&&mods.Count>0||obj["function"]==null&&Seeded(obj["value"]);
            if(seeded.Count<4096)seeded.Add(token,result);
            return result;
        }
        int IntegralSeed(JToken value,JToken multiplier,int seed)=>Seeded(value)||Seeded(multiplier)?seed:0;
        static int IntegralIndex(JToken a,JToken b,float age,int seed,float fallback,bool displacement,int component)=>
            HashCode.Combine(a==null?0:RuntimeHelpers.GetHashCode(a),b==null?0:RuntimeHelpers.GetHashCode(b),age,seed,fallback,displacement,component)&(IntegralSlots-1);
        internal bool TryIntegral(JToken value,JToken multiplier,float age,int seed,float fallback,bool displacement,int component,out float result)
        {
            result=0;if(integrals==null)return false;
            seed=IntegralSeed(value,multiplier,seed);
            ref var e=ref integrals[IntegralIndex(value,multiplier,age,seed,fallback,displacement,component)];
            if(!e.Valid||!ReferenceEquals(e.Curve,value)||!ReferenceEquals(e.Multiplier,multiplier)||e.Age!=age||e.Seed!=seed||e.Fallback!=fallback||e.Displacement!=displacement||e.Component!=component)return false;
            IntegralCacheHits++;result=e.Result;return true;
        }
        internal void AddIntegral(JToken value,JToken multiplier,float age,int seed,float fallback,bool displacement,int component,float result)
        {
            if(integrals==null){integrals=ArrayPool<IntegralEntry>.Shared.Rent(IntegralSlots);Array.Clear(integrals,0,IntegralSlots);}
            seed=IntegralSeed(value,multiplier,seed);
            integrals[IntegralIndex(value,multiplier,age,seed,fallback,displacement,component)]=new()
                {Curve=value,Multiplier=multiplier,Age=age,Seed=seed,Fallback=fallback,Displacement=displacement,Component=component,Result=result,Valid=true};
        }
        // Parsed keys are local to the same Build as the value cache. JSON can be
        // edited between builds without a global invalidation protocol. Pools
        // retain value-only key arrays; no resource graph survives Dispose.
        const int ParsedSlots=256, MaxKeysPerCurve=4096, MaxPooledKeys=32768;
        enum Function { Linear, Stepped, Bezier, Hermite }
        struct Key { internal float Position,Value,P1,P2,T1,T2; }
        // A shared key-count ceiling retains the common small arrays without
        // reserving that same array count for every size bucket. Idle key data
        // is capped at 32768 * 24 bytes (768 KiB), across all threads.
        static class KeyPool
        {
            static readonly Stack<Key[]>[] buckets=new Stack<Key[]>[9];
            static int retained;
            internal static Key[] Rent(int count)
            {
                int size=16,index=0;while(size<count){size*=2;index++;}
                lock(buckets)
                {
                    var bucket=buckets[index];
                    if(bucket?.Count>0){retained-=size;return bucket.Pop();}
                }
                return new Key[size];
            }
            internal static void Return(Key[] keys)
            {
                lock(buckets)
                {
                    if(retained+keys.Length>MaxPooledKeys)return;
                    int size=16,index=0;while(size<keys.Length){size*=2;index++;}
                    (buckets[index]??=new Stack<Key[]>()).Push(keys);retained+=keys.Length;
                }
            }
        }
        struct Parsed
        {
            internal JObject Curve;
            internal int Component,Count;
            internal Key[] Keys;
            internal Function Function;
            internal bool Loop,Sorted;
        }
        readonly Parsed[] parsed=ArrayPool<Parsed>.Shared.Rent(ParsedSlots);
        int pooledKeys;
        bool disposed;
        static int Index(JObject curve,float time,int component,float fallback)=>HashCode.Combine(RuntimeHelpers.GetHashCode(curve),time,component,fallback)&(Size-1);
        internal FxrCurveCache()
        {
            Array.Clear(values,0,Size);Array.Clear(parsed,0,ParsedSlots);
            previous=Current;Current=this;
        }
        internal bool TryGet(JObject curve,float time,int component,float fallback,out float value)
        {
            ref var e=ref values[Index(curve,time,component,fallback)];value=e.Value;
            return ReferenceEquals(e.Curve,curve)&&e.Time==time&&e.Component==component&&e.Fallback==fallback;
        }
        internal void Add(JObject curve,float time,int component,float fallback,float value)
        {values[Index(curve,time,component,fallback)]=new(){Curve=curve,Time=time,Component=component,Fallback=fallback,Value=value};}
        internal bool TryEvaluate(JObject curve,string function,float time,int component,out float result)
        {
            result=0;
            int slot=HashCode.Combine(RuntimeHelpers.GetHashCode(curve),component)&(ParsedSlots-1);
            ref var entry=ref parsed[slot];
            if(!ReferenceEquals(entry.Curve,curve)||entry.Component!=component)
            {
                if(!TryParse(curve,function,component,ref entry))return false;
            }
            var keys=entry.Keys;int count=entry.Count;
            float end=keys[count-1].Position;
            float t=entry.Loop&&end>0?Math.Max(0,time)%end:time;
            int next=0;
            if(entry.Sorted&&count>16)
            {
                int high=count;
                while(next<high){int middle=next+(high-next)/2;if(keys[middle].Position<t)next=middle+1;else high=middle;}
            }
            else while(next<count&&keys[next].Position<t)next++;
            if(next==0){result=keys[0].Value;return true;}
            if(next==count){result=keys[count-1].Value;return true;}
            ref var a=ref keys[next-1];ref var b=ref keys[next];
            float span=b.Position-a.Position,x=span>0?(t-a.Position)/span:0,av=a.Value,bv=b.Value;
            if(entry.Function==Function.Stepped)result=x>=1?bv:av;
            else if(entry.Function==Function.Bezier)
            {
                float c1=av+a.P1/3,c2=bv-b.P2/3,k=1-x;
                result=k*k*k*av+3*k*k*x*c1+3*k*x*x*c2+x*x*x*bv;
            }
            else if(entry.Function==Function.Hermite)
                result=av==bv?av:MathHelper.Lerp(av,bv,FxrPreviewValue.HermiteAmount(a.T1,a.T2,x));
            else result=MathHelper.Lerp(av,bv,x);
            return true;
        }
        bool TryParse(JObject curve,string function,int component,ref Parsed entry)
        {
            var components=curve["components"] as JArray;
            var source=function=="ComponentHermite"?
                components!=null&&component>=0&&component<components.Count?components[component] as JArray:null:
                curve["keyframes"] as JArray;
            if(source==null||source.Count==0||source.Count>MaxKeysPerCurve)return false;
            int capacity=16;while(capacity<source.Count)capacity*=2;
            if(pooledKeys-(entry.Keys?.Length??0)+capacity>MaxPooledKeys)return false;
            if(entry.Keys!=null){pooledKeys-=entry.Keys.Length;KeyPool.Return(entry.Keys);entry=default;}
            var keys=KeyPool.Rent(source.Count);
            var kind=function switch {"Stepped"=>Function.Stepped,"Bezier"=>Function.Bezier,"Hermite" or "ComponentHermite"=>Function.Hermite,_=>Function.Linear};
            int channel=function=="ComponentHermite"?0:component;
            bool sorted=true;
            try
            {
                for(int n=0;n<source.Count;n++)
                {
                    var key=source[n];
                    keys[n]=new Key{Position=(float)key["position"],Value=FxrPreviewValue.Component(key["value"],channel)};
                    if(kind==Function.Bezier){keys[n].P1=FxrPreviewValue.Component(key["p1"],channel);keys[n].P2=FxrPreviewValue.Component(key["p2"],channel);}
                    if(kind==Function.Hermite){keys[n].T1=FxrPreviewValue.Component(key["t1"],channel);keys[n].T2=FxrPreviewValue.Component(key["t2"],channel);}
                    if(!float.IsFinite(keys[n].Position)||(n>0&&keys[n].Position<keys[n-1].Position))sorted=false;
                }
            }
            catch(Exception ex) when(ex is ArgumentException or InvalidCastException or FormatException or NullReferenceException)
            {
                // Preserve lazy JSON evaluation for malformed unused keys.
                KeyPool.Return(keys);return false;
            }
            entry=new Parsed{Curve=curve,Component=component,Count=source.Count,Keys=keys,Function=kind,Loop=(bool?)curve["loop"]==true,Sorted=sorted};
            pooledKeys+=keys.Length;
            return true;
        }
        public void Dispose()
        {
            if(disposed)return;disposed=true;Current=previous;
            for(int n=0;n<ParsedSlots;n++)if(parsed[n].Keys!=null)KeyPool.Return(parsed[n].Keys);
            ArrayPool<Parsed>.Shared.Return(parsed,true);
            ArrayPool<Entry>.Shared.Return(values,true);
            if(integrals!=null){ArrayPool<IntegralEntry>.Shared.Return(integrals,true);integrals=null;}
            seeded.Clear();
        }
    }
}
