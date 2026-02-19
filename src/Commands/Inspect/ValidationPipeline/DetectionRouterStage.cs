using System;
using System.Collections.Generic;
using Obj.DocDetector;
using Obj.ValidatorModule;

namespace Obj.Commands
{
    /// <summary>
    /// Single routing point for weighted document detection dispatch.
    /// Keeps command flows thin and avoids duplicated detector selection logic.
    /// </summary>
    internal static class ObjectsDetectionRouter
    {
        internal static readonly IReadOnlyList<string> DetectableDocKeys = new[]
        {
            DocumentValidationRules.DocKeyDespacho,
            DocumentValidationRules.DocKeyCertidaoConselho,
            DocumentValidationRules.DocKeyRequerimentoHonorarios
        };

        internal static WeightedDetectionResult DetectWeighted(string pdfPath, string? docHint)
        {
            var detectDocKey = DocumentValidationRules.ResolveDocKeyForDetection(docHint);

            if (DocumentValidationRules.IsDocMatch(detectDocKey, DocumentValidationRules.DocKeyDespacho))
                return WeightedDespachoDetector.Detect(pdfPath);

            if (DocumentValidationRules.IsDocMatch(detectDocKey, DocumentValidationRules.DocKeyCertidaoConselho))
                return WeightedCertidaoDetector.Detect(pdfPath);

            if (DocumentValidationRules.IsDocMatch(detectDocKey, DocumentValidationRules.DocKeyRequerimentoHonorarios))
                return WeightedRequerimentoDetector.Detect(pdfPath);

            return WeightedDocDetector.Detect(pdfPath, detectDocKey);
        }

        internal static string GetStepNameForDoc(string docHint)
        {
            var key = DocumentValidationRules.ResolveDocKeyForDetection(docHint);
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyDespacho))
                return "WeightedDespachoDetector.Detect";
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyCertidaoConselho))
                return "WeightedCertidaoDetector.Detect";
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyRequerimentoHonorarios))
                return "WeightedRequerimentoDetector.Detect";
            return "WeightedDocDetector.Detect";
        }

        internal static string GetLabelForDoc(string docHint)
        {
            var key = DocumentValidationRules.ResolveDocKeyForDetection(docHint);
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyDespacho))
                return "Despacho";
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyCertidaoConselho))
                return "Certidao";
            if (DocumentValidationRules.IsDocMatch(key, DocumentValidationRules.DocKeyRequerimentoHonorarios))
                return "Requerimento";
            return key;
        }
    }
}
