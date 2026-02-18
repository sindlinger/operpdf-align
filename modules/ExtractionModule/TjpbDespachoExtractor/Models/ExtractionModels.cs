using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace Obj.TjpbDespachoExtractor.Models
{
    public class ExtractionResult
    {
        [JsonProperty("pdf")]
        public PdfInfo Pdf { get; set; } = new PdfInfo();

        [JsonProperty("run")]
        public RunInfo Run { get; set; } = new RunInfo();

        [JsonProperty("bookmarks")]
        public List<BookmarkInfo> Bookmarks { get; set; } = new List<BookmarkInfo>();

        [JsonProperty("candidates")]
        public List<CandidateWindowInfo> Candidates { get; set; } = new List<CandidateWindowInfo>();

        [JsonProperty("documents")]
        public List<DespachoDocumentInfo> Documents { get; set; } = new List<DespachoDocumentInfo>();

        [JsonProperty("signatures")]
        public List<SignatureInfo> Signatures { get; set; } = new List<SignatureInfo>();

        [JsonProperty("errors")]
        public List<string> Errors { get; set; } = new List<string>();

        [JsonProperty("logs")]
        public List<LogEntry> Logs { get; set; } = new List<LogEntry>();
    }

    public class PdfInfo
    {
        [JsonProperty("fileName")]
        public string FileName { get; set; } = "";

        [JsonProperty("filePath")]
        public string FilePath { get; set; } = "";

        [JsonProperty("pages")]
        public int Pages { get; set; }

        [JsonProperty("sha256")]
        public string Sha256 { get; set; } = "";
    }

    public class RunInfo
    {
        [JsonProperty("startedAt")]
        public string StartedAt { get; set; } = "";

        [JsonProperty("finishedAt")]
        public string FinishedAt { get; set; } = "";

        [JsonProperty("configVersion")]
        public string ConfigVersion { get; set; } = "";

        [JsonProperty("toolVersions")]
        public Dictionary<string, string> ToolVersions { get; set; } = new Dictionary<string, string>();
    }

    public class BookmarkInfo
    {
        [JsonProperty("title")]
        public string Title { get; set; } = "";

        [JsonProperty("page1")]
        public int Page1 { get; set; }

        [JsonProperty("page0")]
        public int Page0 { get; set; }
    }

    public class CandidateWindowInfo
    {
        [JsonProperty("startPage1")]
        public int StartPage1 { get; set; }

        [JsonProperty("endPage1")]
        public int EndPage1 { get; set; }

        [JsonProperty("scoreDmp")]
        public double ScoreDmp { get; set; }

        [JsonProperty("scoreDiffPlex")]
        public double ScoreDiffPlex { get; set; }

        [JsonProperty("anchorsHit")]
        public List<string> AnchorsHit { get; set; } = new List<string>();

        [JsonProperty("density")]
        public Dictionary<string, double> Density { get; set; } = new Dictionary<string, double>();

        [JsonProperty("signals")]
        public Dictionary<string, object> Signals { get; set; } = new Dictionary<string, object>();
    }

    public class DespachoDocumentInfo
    {
        [JsonProperty("docType")]
        public string DocType { get; set; } = "despacho";

        [JsonProperty("startPage1")]
        public int StartPage1 { get; set; }

        [JsonProperty("endPage1")]
        public int EndPage1 { get; set; }

        [JsonProperty("matchScore")]
        public double MatchScore { get; set; }

        [JsonProperty("bands")]
        public List<BandInfo> Bands { get; set; } = new List<BandInfo>();

        [JsonProperty("paragraphs")]
        public List<ParagraphInfo> Paragraphs { get; set; } = new List<ParagraphInfo>();

        [JsonProperty("fields")]
        public Dictionary<string, FieldInfo> Fields { get; set; } = new Dictionary<string, FieldInfo>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class BandInfo
    {
        [JsonProperty("page1")]
        public int Page1 { get; set; }

        [JsonProperty("band")]
        public string Band { get; set; } = "";

        [JsonProperty("text")]
        public string Text { get; set; } = "";

        [JsonProperty("hashSha256")]
        public string HashSha256 { get; set; } = "";

        [JsonProperty("bboxN")]
        public BBoxN? BBoxN { get; set; }
    }

    public class ParagraphInfo
    {
        [JsonProperty("page1")]
        public int Page1 { get; set; }

        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; } = "";

        [JsonProperty("hashSha256")]
        public string HashSha256 { get; set; } = "";

        [JsonProperty("bboxN")]
        public BBoxN? BBoxN { get; set; }
    }

    public class FieldInfo
    {
        [JsonProperty("value")]
        public string Value { get; set; } = "-";

        [JsonProperty("confidence")]
        public double Confidence { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; } = "not_found";

        [JsonProperty("evidence")]
        public EvidenceInfo? Evidence { get; set; }
    }

    public class EvidenceInfo
    {
        [JsonProperty("page1")]
        public int Page1 { get; set; }

        [JsonProperty("bboxN")]
        public BBoxN? BBoxN { get; set; }

        [JsonProperty("snippet")]
        public string Snippet { get; set; } = "";
    }

    public class SignatureInfo
    {
        [JsonProperty("method")]
        public string Method { get; set; } = "";

        [JsonProperty("fieldName")]
        public string FieldName { get; set; } = "";

        [JsonProperty("signerName")]
        public string SignerName { get; set; } = "";

        [JsonProperty("signDate")]
        public string? SignDate { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; } = "";

        [JsonProperty("location")]
        public string Location { get; set; } = "";

        [JsonProperty("page1")]
        public int Page1 { get; set; }

        [JsonProperty("bboxN")]
        public BBoxN? BBoxN { get; set; }

        [JsonProperty("snippet")]
        public string Snippet { get; set; } = "";
    }

    public class BBoxN
    {
        [JsonProperty("x0")]
        public double X0 { get; set; }

        [JsonProperty("y0")]
        public double Y0 { get; set; }

        [JsonProperty("x1")]
        public double X1 { get; set; }

        [JsonProperty("y1")]
        public double Y1 { get; set; }
    }

    public class LogEntry
    {
        [JsonProperty("level")]
        public string Level { get; set; } = "info";

        [JsonProperty("message")]
        public string Message { get; set; } = "";

        [JsonProperty("data")]
        public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();

        [JsonProperty("at")]
        public string At { get; set; } = "";
    }
}
