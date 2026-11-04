using System.Collections.Generic;
using System.Diagnostics;

namespace RealisticPolicies.Engine
{
    /// <summary>Running time of the mod's systems (docs/PLAN.md §10.8 stage 8 "큰 도시 성능"); read by the self-test.</summary>
    public static class Perf
    {
        public sealed class Entry
        {
            public int Count;
            public double TotalMs;
            public double MaxMs;
        }

        public static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

        public static long Start() => Stopwatch.GetTimestamp();

        public static void Stop(string name, long start)
        {
            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            if (!Entries.TryGetValue(name, out Entry entry))
            {
                Entries[name] = entry = new Entry();
            }
            entry.Count++;
            entry.TotalMs += ms;
            if (ms > entry.MaxMs)
            {
                entry.MaxMs = ms;
            }
        }

        public static void Clear() => Entries.Clear();
    }
}
