using System;
using System.Collections.Generic;

namespace DSAnimStudio
{
    // Owned by one evaluation instance only. Exact time keys preserve authored
    // sampling, parent history, seed and local clock; nothing survives a rebuild.
    public sealed class FxrFrameMemo<T>
    {
        // Storage only is shared. The scope detaches every lease before putting
        // an empty dictionary back, including on exceptions. Captured node / force
        // delegates may outlive Build and must never see another frame's values.
        public sealed class Scope : IDisposable
        {
            [ThreadStatic] static Scope current;
            readonly Scope previous;
            FxrFrameMemo<T> first;
            bool disposed;
            public Scope(){previous=current;current=this;}
            internal static void Track(FxrFrameMemo<T> memo)
            {
                if(current==null)return;
                memo.nextScoped=current.first;current.first=memo;
            }
            internal static bool Active=>current!=null;
            public void Dispose()
            {
                if(disposed)return;disposed=true;current=previous;
                while(first!=null)
                {
                    var memo=first;first=memo.nextScoped;memo.nextScoped=null;
                    var dictionary=memo.values;memo.values=null;memo.hasLast=false;memo.lastValue=default;
                    if(dictionary!=null)Return(dictionary);
                }
            }
        }
        // At most 32768 value slots and 256 dictionaries per value type. There
        // are no delegate, model or JSON references in the empty retained tables.
        static readonly Stack<Dictionary<float,T>>[] pool=new Stack<Dictionary<float,T>>[14];
        static int pooledSlots,pooledTables;
        static Dictionary<float,T> Rent(int limit)
        {
            lock(pool)for(int i=0;i<pool.Length;i++)
                if(pool[i]?.Count>0&&(1<<i)<=Math.Max(16,limit*2))
                {
                    var table=pool[i].Pop();pooledSlots-=table.EnsureCapacity(0);pooledTables--;return table;
                }
            return new();
        }
        static void Return(Dictionary<float,T> table)
        {
            int slots=table.EnsureCapacity(0),bucket=0;
            table.Clear();
            while(bucket<pool.Length&&(1<<bucket)<slots)bucket++;
            if(bucket>=pool.Length)return;
            lock(pool)
            {
                if(pooledSlots+slots>32768||pooledTables>=256)return;
                (pool[bucket]??=new()).Push(table);pooledSlots+=slots;pooledTables++;
            }
        }
        readonly Func<float,T> evaluate;
        readonly int capacity;
        Dictionary<float,T> values;
        float lastTime;
        T lastValue;
        bool hasLast;
        FxrFrameMemo<T> nextScoped;
        public FxrFrameMemo(Func<float,T> evaluate,int capacity=512){this.evaluate=evaluate;this.capacity=capacity;}
        public T At(float time)
        {
            if(hasLast&&time==lastTime)return lastValue;
            if(values!=null&&values.TryGetValue(time,out var found)){lastTime=time;lastValue=found;return found;}
            if(hasLast&&values==null)
            {
                values=Scope.Active?Rent(capacity):new();Scope.Track(this);values[lastTime]=lastValue;
            }
            var value=evaluate(time);
            if(values!=null&&values.Count<capacity)values[time]=value;
            hasLast=true;lastTime=time;lastValue=value;return value;
        }
    }
}
