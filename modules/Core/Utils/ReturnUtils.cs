using System;
using System.IO;
using System.Text;

namespace Obj.Utils
{
    public static class ReturnUtils
    {
        public static bool Enabled { get; private set; }
        public static string OutputFileName { get; private set; } = "";

        public static void Enable()
        {
            Enabled = true;
        }

        public static void Enable(string? outputFileName)
        {
            Enabled = true;
            if (!string.IsNullOrWhiteSpace(outputFileName))
                OutputFileName = outputFileName.Trim();
        }

        public static bool IsEnabled()
        {
            return Enabled;
        }

        public static string ResolveOutputPath(string defaultFileName = "output_pipe.json")
        {
            var file = string.IsNullOrWhiteSpace(OutputFileName) ? defaultFileName : OutputFileName;
            file = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(file))
                file = defaultFileName;
            if (!file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                file += ".json";

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "io");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, file);
        }

        public static string PersistJson(string json, string defaultFileName = "output_pipe.json")
        {
            var path = ResolveOutputPath(defaultFileName);
            File.WriteAllText(path, json ?? "", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            return path;
        }
    }
}
