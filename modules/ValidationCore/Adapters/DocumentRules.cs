namespace Obj.ValidationCore
{
    public static class DocumentRules
    {
        public static string ResolveDocKeyForDetection(string? docHint)
        {
            return DocumentValidationRules.ResolveDocKeyForDetection(docHint);
        }

        public static bool IsDocMatch(string? docHint, string? expectedDoc)
        {
            return DocumentValidationRules.IsDocMatch(docHint, expectedDoc);
        }

        public static string MapDocKeyToOutputType(string? docHint)
        {
            return DocumentValidationRules.MapDocKeyToOutputType(docHint);
        }
    }
}
