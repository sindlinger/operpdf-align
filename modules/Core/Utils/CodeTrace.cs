using System;
using System.Collections.Generic;

namespace Obj.Utils
{
    public static class CodeTrace
    {
        private static readonly Dictionary<string, string[]> CommandFiles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = new[]
            {
                "src/Commands/Inspect/ObjectsOperators.cs",
                "modules/PatternModules/registry/extract_fields/ObjectsTextExtraction.cs",
                "modules/Core/Utils/PathUtils.cs"
            },
            ["diff"] = new[]
            {
                "modules/TextOpsRanges/ObjectsTextOpsDiff.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.SelfBlocks.cs"
            },
            ["align"] = new[]
            {
                "modules/TextOpsRanges/ObjectsTextOpsDiff.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.SelfBlocks.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs"
            },
            ["anchors"] = new[]
            {
                "modules/Align/ObjectsTextOpsAlign.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs"
            },
            ["weirdspace"] = new[]
            {
                "src/Commands/Inspect/PdfTextExtraction.cs",
                "modules/Core/Utils/ReportUtils.cs"
            },
            ["textopsvar"] = new[]
            {
                "src/Commands/Inspect/ObjectsTextOpsAlign.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.SelfBlocks.cs",
                "modules/HonorariosModule/HonorariosFacade.cs",
                "modules/ValidationCore/Rules/FieldValidationRules.cs",
                "modules/ValidationCore/Engine/ValidationEngine.cs",
                "modules/ValidationCore/Docs/DocumentRuleSet.cs"
            },
            ["textopsfixed"] = new[]
            {
                "src/Commands/Inspect/ObjectsTextOpsAlign.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.SelfBlocks.cs",
                "modules/HonorariosModule/HonorariosFacade.cs",
                "modules/ValidationCore/Rules/FieldValidationRules.cs",
                "modules/ValidationCore/Engine/ValidationEngine.cs",
                "modules/ValidationCore/Docs/DocumentRuleSet.cs"
            },
            ["textopsdiff"] = new[]
            {
                "modules/TextOpsRanges/ObjectsTextOpsDiff.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.Helpers.cs",
                "modules/TextOpsRanges/ObjectsTextOpsDiff.SelfBlocks.cs"
            },
            ["textopsalign"] = new[]
            {
                "src/Commands/Inspect/ObjectsTextOpsAlign.cs",
                "modules/Align/ObjectsTextOpsAlign.cs",
                "modules/HonorariosModule/HonorariosFacade.cs",
                "modules/ValidationCore/Rules/FieldValidationRules.cs",
                "modules/ValidationCore/Engine/ValidationEngine.cs",
                "modules/ValidationCore/Docs/DocumentRuleSet.cs"
            },
            ["objdiff"] = new[]
            {
                "src/Commands/Inspect/ObjectsObjDiff.cs",
                "modules/Core/Utils/ReportUtils.cs"
            },
            ["pattern"] = new[]
            {
                "src/Commands/Inspect/ObjectsPattern.cs",
                "modules/Core/Utils/ReportUtils.cs"
            },
            ["honorarios"] = new[]
            {
                "modules/HonorariosModule/HonorariosApi.cs",
                "modules/HonorariosModule/HonorariosEnricher.cs"
            }
        };

        public static void Print(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return;

            if (!CommandFiles.TryGetValue(command, out var files) || files.Length == 0)
            {
                Console.WriteLine($"[TRACE-CODE] sem mapa para comando: {command}");
                return;
            }

            Console.WriteLine("[TRACE-CODE] arquivos de codigo usados");
            foreach (var file in files)
                Console.WriteLine($"  - {file}");
        }
    }
}
