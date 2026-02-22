using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Obj.Utils
{
    public static class ReportUtils
    {
        private const string Reset = "\u001b[0m";
        private const string Blue = "\u001b[38;5;39m";
        private const string Orange = "\u001b[38;5;208m";
        private const string Gray = "\u001b[38;5;250m";
        private const string GraySoft = "\u001b[38;5;246m";
        private static readonly Regex AnsiRegex = new(@"\u001b\[[0-9;]*m", RegexOptions.Compiled);

        public static void WriteSummary(string title, IEnumerable<(string Key, string Value)> items)
        {
            Console.WriteLine(StyleIfPlain(title, Gray));
            foreach (var (key, value) in items)
            {
                Console.WriteLine($"  {StyleIfPlain(key + ":", GraySoft)} {StyleIfPlain(value ?? "", Gray)}");
            }
        }

        public static string BlueLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return $"{Blue}{text}{Reset}";
        }

        public static string OrangeLabel(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return $"{Orange}{text}{Reset}";
        }

        public static void WriteTable(string title, string[] headers, IEnumerable<string[]> rows, int maxRows = 0)
        {
            var data = rows.ToList();
            if (maxRows > 0)
                data = data.Take(maxRows).ToList();
            if (data.Count == 0)
                return;

            var widths = new int[headers.Length];
            for (int i = 0; i < headers.Length; i++)
                widths[i] = VisibleLength(headers[i]);
            foreach (var row in data)
            {
                for (int i = 0; i < headers.Length && i < row.Length; i++)
                {
                    widths[i] = Math.Max(widths[i], VisibleLength(row[i] ?? ""));
                }
            }

            Console.WriteLine(StyleIfPlain(title, Gray));
            Console.WriteLine(StyleIfPlain(FormatRow(headers, widths), GraySoft));
            foreach (var row in data)
                Console.WriteLine(StyleIfPlain(FormatRow(row, widths), Gray));
        }

        public static (double Low, double High, double Cut, int LowCount, int HighCount) KMeans1D(IReadOnlyList<double> values, int maxIter = 50)
        {
            if (values.Count == 0) return (0, 0, 0, 0, 0);
            var min = values.Min();
            var max = values.Max();
            double c1 = min;
            double c2 = max;

            for (int iter = 0; iter < maxIter; iter++)
            {
                var g1 = new List<double>();
                var g2 = new List<double>();
                foreach (var v in values)
                {
                    if (Math.Abs(v - c1) <= Math.Abs(v - c2))
                        g1.Add(v);
                    else
                        g2.Add(v);
                }
                var n1 = g1.Count > 0 ? g1.Average() : c1;
                var n2 = g2.Count > 0 ? g2.Average() : c2;
                if (Math.Abs(n1 - c1) < 1e-6 && Math.Abs(n2 - c2) < 1e-6)
                    break;
                c1 = n1;
                c2 = n2;
            }

            var low = Math.Min(c1, c2);
            var high = Math.Max(c1, c2);
            var cut = (low + high) / 2.0;
            var lowCount = values.Count(v => v <= cut);
            var highCount = values.Count - lowCount;
            return (low, high, cut, lowCount, highCount);
        }

        public static string F(double value, int digits = 4)
        {
            return value.ToString($"F{digits}", CultureInfo.InvariantCulture);
        }

        private static string FormatRow(string[] row, int[] widths)
        {
            var parts = new string[widths.Length];
            for (int i = 0; i < widths.Length; i++)
            {
                var cell = i < row.Length ? row[i] ?? "" : "";
                var pad = Math.Max(0, widths[i] - VisibleLength(cell));
                parts[i] = cell + new string(' ', pad + 2);
            }
            return string.Join("", parts).TrimEnd();
        }

        private static int VisibleLength(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;
            return AnsiRegex.Replace(value, "").Length;
        }

        private static string StyleIfPlain(string value, string color)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Contains("\u001b[", StringComparison.Ordinal)
                ? value
                : $"{color}{value}{Reset}";
        }
    }
}
