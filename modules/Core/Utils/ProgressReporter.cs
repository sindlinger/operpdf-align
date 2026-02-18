using System;
using System.Threading;

namespace Obj.Utils
{
    public sealed class ProgressReporter
    {
        private readonly int _total;
        private readonly int _every;
        private readonly DateTime _start;
        private int _count;
        private int _lastPrinted;
        private readonly string _label;
        private readonly object _lock = new object();

        private ProgressReporter(string label, int total, int every)
        {
            _label = label;
            _total = Math.Max(1, total);
            _every = Math.Max(1, every);
            _start = DateTime.UtcNow;
        }

        public static ProgressReporter? FromConfig(string label, int total)
        {
            var defaults = ExecutionConfig.GetProgressDefaults();
            if (!defaults.Enabled || total <= 0)
                return null;
            return new ProgressReporter(label, total, defaults.Every);
        }

        public void Tick(string? item = null)
        {
            var count = Interlocked.Increment(ref _count);
            var shouldPrint = count == _total ||
                              count - Volatile.Read(ref _lastPrinted) >= _every;
            if (!shouldPrint)
                return;

            lock (_lock)
            {
                if (count != _total && count - _lastPrinted < _every)
                    return;
                _lastPrinted = count;
                var elapsed = DateTime.UtcNow - _start;
                var pct = Math.Min(100.0, count * 100.0 / _total);
                var eta = count > 0
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (_total - count) / count)
                    : TimeSpan.Zero;
                var msg = $"[PROGRESS] {_label} {count}/{_total} ({pct:0.0}%) elapsed={FormatDuration(elapsed)} eta={FormatDuration(eta)}";
                if (!string.IsNullOrWhiteSpace(item))
                    msg += $" item={item}";
                Console.Error.WriteLine(msg);
            }
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
            return $"{ts.Minutes:00}:{ts.Seconds:00}";
        }

        // Config now centralized in ExecutionConfig.
    }
}
