namespace DMEdit.App.Controls;

// Performance-stats partial of EditorControl.  Holds the two nested
// classes (TimingStat, PerfStatsData) that the stats panel and dev-mode
// overlay consume.  The live PerfStats instance and its two Stopwatch
// helpers live in the main EditorControl.cs file alongside all other
// fields — partials share state so nothing here needs a "parent"
// reference.
public sealed partial class EditorControl {

    /// <summary>
    /// Tracks timing statistics with exponential moving average, min, and max.
    /// </summary>
    public sealed class TimingStat {
        private const double Alpha = 0.1; // EMA smoothing factor
        public double Avg { get; private set; }
        public double Min { get; private set; } = double.MaxValue;
        public double Max { get; private set; }
        public int Count { get; private set; }

        public void Record(double ms) {
            Count++;
            if (Count == 1) {
                Avg = ms;
            } else {
                Avg = Alpha * ms + (1 - Alpha) * Avg;
            }
            if (ms < Min) Min = ms;
            if (ms > Max) Max = ms;
        }

        public void Reset() {
            Avg = 0;
            Min = double.MaxValue;
            Max = 0;
            Count = 0;
        }

        public string Format() =>
            Count == 0 ? "—" : $"{Avg:F2}ms ({Min:F2}–{Max:F2})";
    }

    /// <summary>Performance statistics exposed for the stats panel.</summary>
    public sealed class PerfStatsData {
        public TimingStat Layout { get; } = new();
        public TimingStat Render { get; } = new();
        public TimingStat Edit { get; } = new();
        public int ViewportLines { get; set; }
        public int ViewportRows { get; set; }
        public double ScrollPercent { get; set; }
        /// <summary>Time from open to first renderable chunk (streaming loads only).</summary>
        public double FirstChunkTimeMs { get; set; }
        /// <summary>Total time from open to fully loaded.</summary>
        public double LoadTimeMs { get; set; }
        public double SaveTimeMs { get; set; }
        /// <summary>Time for the most recent ReplaceAll operation.</summary>
        public double ReplaceAllTimeMs { get; set; }
        /// <summary>How many times ScrollCaretIntoView needed a retry pass.</summary>
        public long ScrollRetries { get; set; }
        /// <summary>How many times ScrollCaretIntoView ran (past all early exits).</summary>
        public long ScrollCaretCalls { get; set; }
        /// <summary>How many times ScrollExact ran (should be 0 or 1 per user action).</summary>
        public long ScrollExactCalls { get; set; }
        /// <summary>Current GC memory in MB.</summary>
        public double MemoryMb { get; set; }
        /// <summary>Peak GC memory seen this session in MB.</summary>
        public double PeakMemoryMb { get; set; }
        /// <summary>Cumulative Gen 0 GC count (snapshot).</summary>
        public int Gen0 { get; set; }
        /// <summary>Cumulative Gen 1 GC count (snapshot).</summary>
        public int Gen1 { get; set; }
        /// <summary>Cumulative Gen 2 GC count (snapshot).</summary>
        public int Gen2 { get; set; }
        /// <summary>Cumulative count of InvalidateLayout calls.</summary>
        public long LayoutInvalidations { get; set; }
        /// <summary>Cumulative count of row index rebuilds.</summary>
        public long RowIndexBuilds { get; set; }
        /// <summary>Time of the most recent row index build in ms.</summary>
        public double RowIndexBuildMs { get; set; }
        /// <summary>Cumulative count of Render calls.</summary>
        public long RenderCalls { get; set; }

        /// <summary>Samples current GC memory and updates peak.</summary>
        public void SampleMemory() {
            MemoryMb = System.GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            if (MemoryMb > PeakMemoryMb) {
                PeakMemoryMb = MemoryMb;
            }
            Gen0 = System.GC.CollectionCount(0);
            Gen1 = System.GC.CollectionCount(1);
            Gen2 = System.GC.CollectionCount(2);
        }

        public void Reset() {
            Layout.Reset();
            Render.Reset();
            Edit.Reset();
        }
    }
}
