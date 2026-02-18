using System;
using System.Collections.Generic;
using System.Text;
using Obj.Commands;
using Obj.Utils;
using Obj.Logging;

namespace Obj.OperCli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            EnvLoader.LoadDefaults();

            if (args.Length == 0 || IsHelp(args[0]))
            {
                ShowHelp();
                return 0;
            }

            InitLogger(args);
            args = ApplyGlobalOutputConfig(PathUtils.NormalizeArgs(args));
            if (args.Length == 0)
            {
                ShowHelp();
                return 1;
            }

            InputPreview.PrintPlannedInputs(args);
            Preflight.Run(args);

            var mode = (args[0] ?? "").Trim().ToLowerInvariant();
            var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
            if (string.Equals(mode, "textopsalign", StringComparison.OrdinalIgnoreCase))
            {
                CodePreview.PrintPlannedCode("textopsalign", args);
                ObjectsTextOpsAlign.Execute(rest);
                return 0;
            }

            if (string.Equals(mode, "textopsvar", StringComparison.OrdinalIgnoreCase))
            {
                CodePreview.PrintPlannedCode("textopsvar", args);
                ObjectsTextOpsAlign.ExecuteWithMode(rest, ObjectsTextOpsAlign.OutputMode.VariablesOnly);
                return 0;
            }

            if (string.Equals(mode, "textopsfixed", StringComparison.OrdinalIgnoreCase))
            {
                CodePreview.PrintPlannedCode("textopsfixed", args);
                ObjectsTextOpsAlign.ExecuteWithMode(rest, ObjectsTextOpsAlign.OutputMode.FixedOnly);
                return 0;
            }

            Console.Error.WriteLine($"Comando não suportado neste projeto: {mode}");
            ShowHelp();
            return 1;
        }

        private static bool IsHelp(string arg)
        {
            return arg.Equals("help", StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                   arg.Equals("-h", StringComparison.OrdinalIgnoreCase);
        }

        private static string[] ApplyGlobalOutputConfig(string[] args)
        {
            var config = new OutputConfig();
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "--return", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "return", StringComparison.OrdinalIgnoreCase))
                {
                    ReturnUtils.Enable();
                    continue;
                }
                if (string.Equals(arg, "--pager", StringComparison.OrdinalIgnoreCase))
                {
                    config.Pager = true;
                    continue;
                }
                if (string.Equals(arg, "--page-size", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var size) && size > 0)
                        config.PageSize = size;
                    i++;
                    continue;
                }
                if (string.Equals(arg, "--tee", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    config.TeePath = args[i + 1];
                    i++;
                    continue;
                }
                rest.Add(arg);
            }

            OutputManager.Init(config);
            return rest.ToArray();
        }

        private static void InitLogger(string[] args)
        {
            if (args == null || args.Length == 0)
                return;

            var hasLog = false;
            foreach (var a in args)
            {
                if (string.Equals(a, "--log", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "log", StringComparison.OrdinalIgnoreCase))
                {
                    hasLog = true;
                    break;
                }
            }

            if (!hasLog)
            {
                var env = Environment.GetEnvironmentVariable("OPERPDF_LOG");
                if (!string.IsNullOrWhiteSpace(env) && env.Trim() == "1")
                    hasLog = true;
            }

            Logger.Enable(hasLog);
        }

        private static void ShowHelp()
        {
            Console.WriteLine("operpdf-textopsalign");
            Console.WriteLine("Uso: operpdf <textopsalign|textopsvar|textopsfixed> [opcoes]");
            Console.WriteLine();
            Console.WriteLine("Comando disponível");
            Console.WriteLine("  textopsalign  alinha blocos e retorna range por operador");
            Console.WriteLine("  textopsvar    mostra blocos variáveis (diff textual)");
            Console.WriteLine("  textopsfixed  mostra blocos fixos (comuns)");
            Console.WriteLine();
            Console.WriteLine("Global");
            Console.WriteLine("  return/--return  JSON puro");
            Console.WriteLine("  --pager          pagina saída");
            Console.WriteLine("  --page-size N    linhas por página");
            Console.WriteLine("  --tee arquivo    salva saída");
            Console.WriteLine();
            Console.WriteLine("Exemplo");
            Console.WriteLine("  operpdf textopsalign --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf textopsalign --inputs :D20 --inputs :Q200 --back --align --align-top 0");
            Console.WriteLine("  operpdf textopsvar --inputs :D20 --inputs :Q200");
            Console.WriteLine("  operpdf textopsfixed --inputs :D20 --inputs :Q200");
        }
    }
}
