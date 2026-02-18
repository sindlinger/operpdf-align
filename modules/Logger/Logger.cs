using System;
using System.Collections.Generic;

namespace Obj.Logging
{
    /// <summary>
    /// Módulo de logging centralizado (CLI).
    /// Integração com cores e níveis deve ser feita nos comandos.
    /// </summary>
    public static class Logger
    {
        public enum Level
        {
            Trace,
            Debug,
            Info,
            Warn,
            Error
        }

        private static bool _enabled = true;
        private static Level _minLevel = Level.Info;

        public static void Enable(bool enabled)
        {
            _enabled = enabled;
        }

        public static bool Enabled => _enabled;

        public static void SetMinLevel(Level level)
        {
            _minLevel = level;
        }

        public static void Log(Level level, string message)
        {
            if (!_enabled || level < _minLevel)
                return;

            Console.Error.WriteLine(message ?? "");
        }

        public static void Trace(string message) => Log(Level.Trace, message);
        public static void Debug(string message) => Log(Level.Debug, message);
        public static void Info(string message) => Log(Level.Info, message);
        public static void Warn(string message) => Log(Level.Warn, message);
        public static void Error(string message) => Log(Level.Error, message);

        public static void Log(Level level, string tag, string message)
        {
            Log(level, $"[{tag}] {message}");
        }

        public static void Section(string title)
        {
            if (!_enabled || _minLevel > Level.Info)
                return;
            Console.Error.WriteLine($"\n== {title} ==");
        }

        public static void Items(string tag, IEnumerable<string> items)
        {
            if (!_enabled || _minLevel > Level.Info)
                return;
            foreach (var item in items)
                Console.Error.WriteLine($"[{tag}] {item}");
        }
    }
}
