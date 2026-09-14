// Ported from the Replay Analyser (EchoAnalyser.Core/Parsing/IndexingLimits.cs). Logic is unchanged; only the
// namespace, explicit usings and nullable context differ, so fixes can be diffed against
// the original.
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
namespace Spark.ReplayAnalyser.Parsing;

/// <summary>
/// How many replays it is safe to decode at once.
///
/// A large <c>.butter</c> file expands to tens of thousands of fully-populated frames, so a
/// single worker can hold well over a gigabyte. Fanning out to every core will happily eat
/// 10 GB+ and start swapping on a smaller machine, which is far slower than using fewer
/// workers — so the limit comes from free memory as much as from core count.
/// </summary>
public static class IndexingLimits
{
    /// <summary>Rough peak working set for one in-flight replay, in bytes.</summary>
    private const double BytesPerWorker = 1.5 * 1024 * 1024 * 1024;

    public static int MaxParallelism
    {
        get
        {
            int byCpu = Math.Max(1, Environment.ProcessorCount - 1);

            long available = AvailableMemoryBytes();
            if (available <= 0) return Math.Min(byCpu, 4);

            // Leave roughly a quarter of what is free for everything else on the machine.
            int byMemory = (int)(available * 0.75 / BytesPerWorker);
            return Math.Clamp(Math.Min(byCpu, byMemory), 1, 8);
        }
    }

    private static long AvailableMemoryBytes()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            // TotalAvailableMemoryBytes is the container/machine limit; subtract what the
            // process already holds to approximate the headroom left.
            long limit = info.TotalAvailableMemoryBytes;
            if (limit <= 0) return 0;
            return Math.Max(0, limit - GC.GetTotalMemory(false));
        }
        catch
        {
            return 0;
        }
    }
}
