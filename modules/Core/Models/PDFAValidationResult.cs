using System.Collections.Generic;

namespace Obj.Models
{
    public class PDFAValidationResult
    {
        public bool IsValidPDFA { get; set; }
        public string PDFAPart { get; set; } = "1";
        public string ConformanceLevel { get; set; } = "B";
        public bool HasXMPMetadata { get; set; }
        public bool HasPDFAIdentification { get; set; }
        public bool HasOutputIntent { get; set; }
        public bool AllFontsEmbedded { get; set; }
        public bool IsEncrypted { get; set; }
        public bool HasJavaScript { get; set; }
        public bool HasTransparency { get; set; }
        public bool HasProhibitedAnnotations { get; set; }
        public bool HasXFAForms { get; set; }
        public bool HasProhibitedActions { get; set; }
        public bool HasEmbeddedFiles { get; set; }
        public bool HasMultimedia { get; set; }
        public List<string> ValidationMessages { get; set; } = new List<string>();
        public bool NoTransparency { get; set; }
        public bool NoJavaScript { get; set; }
        public bool NoEncryption { get; set; }
    }
}
