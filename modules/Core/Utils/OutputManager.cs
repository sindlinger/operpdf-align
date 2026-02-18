using System;
using System.IO;
using System.Text;

namespace Obj.Utils
{
    public sealed class OutputConfig
    {
        public bool Pager { get; set; }
        public int PageSize { get; set; } = 30;
        public string? TeePath { get; set; }
    }

    public static class OutputManager
    {
        public static OutputConfig Config { get; } = new OutputConfig();
        private static StreamWriter? _teeWriter;

        public static void Init(OutputConfig config)
        {
            if (config == null) return;
            Config.Pager = config.Pager;
            Config.PageSize = config.PageSize > 0 ? config.PageSize : 30;
            Config.TeePath = string.IsNullOrWhiteSpace(config.TeePath) ? null : config.TeePath;

            var consoleWriter = Console.Out;
            if (Config.Pager && !Console.IsOutputRedirected && !Console.IsInputRedirected)
            {
                consoleWriter = new PagedTextWriter(consoleWriter, Config.PageSize);
            }

            TextWriter finalWriter = consoleWriter;
            if (!string.IsNullOrWhiteSpace(Config.TeePath))
            {
                var dir = Path.GetDirectoryName(Config.TeePath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);
                _teeWriter = new StreamWriter(Config.TeePath!, false, new UTF8Encoding(true))
                {
                    AutoFlush = true
                };
                finalWriter = new TeeTextWriter(consoleWriter, _teeWriter);
                AppDomain.CurrentDomain.ProcessExit += (_, __) => ReportOutputIndex(Config.TeePath!);
            }

            Console.SetOut(finalWriter);
        }

        private static void ReportOutputIndex(string path)
        {
            try
            {
                if (PathUtils.TryGetIndexInAliasDir("O", path, out var index, out var total))
                {
                    Console.Error.WriteLine($"[INDEX O] saved {index}/{total} -> {path}");
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    internal sealed class TeeTextWriter : TextWriter
    {
        private readonly TextWriter _primary;
        private readonly TextWriter _secondary;

        public TeeTextWriter(TextWriter primary, TextWriter secondary)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _secondary = secondary ?? throw new ArgumentNullException(nameof(secondary));
        }

        public override Encoding Encoding => _primary.Encoding;

        public override void Write(char value)
        {
            _primary.Write(value);
            _secondary.Write(value);
        }

        public override void Write(string? value)
        {
            if (value == null) return;
            _primary.Write(value);
            _secondary.Write(value);
        }

        public override void WriteLine(string? value)
        {
            _primary.WriteLine(value);
            _secondary.WriteLine(value);
        }

        public override void Flush()
        {
            _primary.Flush();
            _secondary.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _primary.Flush();
                _secondary.Flush();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class PagedTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly int _pageSize;
        private int _lines;
        private bool _suppress;

        public PagedTextWriter(TextWriter inner, int pageSize)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _pageSize = pageSize > 0 ? pageSize : 30;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            if (_suppress) return;
            _inner.Write(value);
            if (value == '\n')
                HandleLineBreak();
        }

        public override void Write(string? value)
        {
            if (_suppress || string.IsNullOrEmpty(value))
                return;

            _inner.Write(value);
            CountLines(value);
        }

        public override void WriteLine(string? value)
        {
            if (_suppress) return;
            _inner.WriteLine(value);
            HandleLineBreak();
        }

        private void CountLines(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\n')
                    HandleLineBreak();
            }
        }

        private void HandleLineBreak()
        {
            _lines++;
            if (_lines < _pageSize) return;
            PromptMore();
            _lines = 0;
        }

        private void PromptMore()
        {
            if (_suppress) return;
            const string prompt = "--mais-- (Enter=continuar, q=sair)";
            _inner.Write(prompt);
            _inner.Flush();
            var key = Console.ReadKey(true);
            _inner.Write("\r" + new string(' ', prompt.Length) + "\r");
            if (key.Key == ConsoleKey.Q)
            {
                _suppress = true;
            }
        }
    }
}
