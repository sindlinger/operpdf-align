using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using Newtonsoft.Json;
using Obj.Models;

namespace Obj.Utils
{
    /// <summary>
    /// Utilitário para carregar análises gravadas no Postgres (processes.json) e mapear índices.
    /// </summary>
    public static class PgAnalysisLoader
    {
        public class RawProcessRow
        {
            public string ProcessNumber { get; set; } = "";
            public string Source { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string Json { get; set; } = "";
        }

        public class RawStructureRow
        {
            public string ProcessNumber { get; set; } = "";
            public string Source { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string Json { get; set; } = "";
        }

        public class FooterInfo
        {
            public List<string> Signers { get; set; } = new List<string>();
            public string SignatureRaw { get; set; } = "";
        }
        public class ProcessRow
        {
            public long Id { get; set; }
            public string ProcessNumber { get; set; } = "";
            public string Source { get; set; } = "";
            public DateTime CreatedAt { get; set; }
            public string Json { get; set; } = "";
            public int TotalPages { get; set; }
            public int TotalWords { get; set; }
            public int TotalImages { get; set; }
            public int TotalFonts { get; set; }
            public decimal ScanRatio { get; set; }
            public bool IsScanned { get; set; }
            public string MetaTitle { get; set; } = "";
            public string MetaAuthor { get; set; } = "";
            public string MetaSubject { get; set; } = "";
            public string MetaKeywords { get; set; } = "";
            public bool HasJs { get; set; }
            public bool HasEmbedded { get; set; }
            public bool HasAttachments { get; set; }
            public bool HasMultimedia { get; set; }
            public bool HasForms { get; set; }
            public bool IsEncrypted { get; set; }
        }

        public class ProcessSummary
        {
            public string ProcessNumber { get; set; } = "";
            public int TotalPages { get; set; }
            public int TotalWords { get; set; }
            public int TotalImages { get; set; }
            public int TotalFonts { get; set; }
            public decimal ScanRatio { get; set; }
            public bool IsScanned { get; set; }
            public bool IsEncrypted { get; set; }
            public bool PermCopy { get; set; }
            public bool PermPrint { get; set; }
            public bool PermAnnotate { get; set; }
            public bool PermFillForms { get; set; }
            public bool PermExtract { get; set; }
            public bool PermAssemble { get; set; }
            public bool PermPrintHq { get; set; }
            public bool HasJs { get; set; }
            public bool HasEmbedded { get; set; }
            public bool HasAttachments { get; set; }
            public bool HasMultimedia { get; set; }
            public bool HasForms { get; set; }
            public string MetaTitle { get; set; } = "";
            public string MetaAuthor { get; set; } = "";
            public string MetaSubject { get; set; } = "";
            public string MetaKeywords { get; set; } = "";
        }

        public static string GetPgUri(string? overrideUri = null)
        {
            if (!string.IsNullOrWhiteSpace(overrideUri)) return PgDocStore.NormalizePgUri(overrideUri!);
            var env = Environment.GetEnvironmentVariable("FPDF_PG_URI");
            var uri = string.IsNullOrWhiteSpace(env) ? PgDocStore.DefaultPgUri : env!;
            return PgDocStore.NormalizePgUri(uri);
        }

        public static List<ProcessRow> ListProcesses(string? pgUri = null)
        {
            var uri = GetPgUri(pgUri);
            var rows = new List<ProcessRow>();
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            using var cmd = new NpgsqlCommand(@"SELECT id, process_number, source, created_at, COALESCE(json::text, ''),
                                                      COALESCE(total_pages,0), COALESCE(total_words,0), COALESCE(total_images,0), COALESCE(total_fonts,0),
                                                      COALESCE(scan_ratio,0), COALESCE(is_scanned,false),
                                                      COALESCE(meta_title,''), COALESCE(meta_author,''), COALESCE(meta_subject,''), COALESCE(meta_keywords,''),
                                                      COALESCE(has_js,false), COALESCE(has_embedded_files,false), COALESCE(has_attachments,false),
                                                      COALESCE(has_multimedia,false), COALESCE(has_forms,false), COALESCE(is_encrypted,false)
                                               FROM processes ORDER BY created_at DESC", conn);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new ProcessRow
                {
                    Id = r.GetInt64(0),
                    ProcessNumber = r.IsDBNull(1) ? "" : r.GetString(1),
                    Source = r.IsDBNull(2) ? "" : r.GetString(2),
                    CreatedAt = r.IsDBNull(3) ? DateTime.MinValue : r.GetDateTime(3),
                    Json = r.IsDBNull(4) ? "" : r.GetString(4),
                    TotalPages = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    TotalWords = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    TotalImages = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    TotalFonts = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                    ScanRatio = r.IsDBNull(9) ? 0 : r.GetDecimal(9),
                    IsScanned = !r.IsDBNull(10) && r.GetBoolean(10),
                    MetaTitle = r.IsDBNull(11) ? "" : r.GetString(11),
                    MetaAuthor = r.IsDBNull(12) ? "" : r.GetString(12),
                    MetaSubject = r.IsDBNull(13) ? "" : r.GetString(13),
                    MetaKeywords = r.IsDBNull(14) ? "" : r.GetString(14),
                    HasJs = !r.IsDBNull(15) && r.GetBoolean(15),
                    HasEmbedded = !r.IsDBNull(16) && r.GetBoolean(16),
                    HasAttachments = !r.IsDBNull(17) && r.GetBoolean(17),
                    HasMultimedia = !r.IsDBNull(18) && r.GetBoolean(18),
                    HasForms = !r.IsDBNull(19) && r.GetBoolean(19),
                    IsEncrypted = !r.IsDBNull(20) && r.GetBoolean(20)
                });
            }
            return rows;
        }

        public static List<RawProcessRow> ListRawProcesses(string? pgUri = null, string? sourceContains = null, int? limit = null, int? offset = null)
        {
            var uri = GetPgUri(pgUri);
            var rows = new List<RawProcessRow>();
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            var sql = @"SELECT process_number, source, created_at, COALESCE(raw_json::text,'')
                        FROM raw_processes";
            if (!string.IsNullOrWhiteSpace(sourceContains))
                sql += " WHERE source ILIKE @s";
            sql += " ORDER BY created_at DESC";
            if (limit.HasValue && limit.Value > 0)
                sql += " LIMIT " + limit.Value;
            if (offset.HasValue && offset.Value > 0)
                sql += " OFFSET " + offset.Value;

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 120;
            if (!string.IsNullOrWhiteSpace(sourceContains))
                cmd.Parameters.AddWithValue("@s", "%" + sourceContains + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new RawProcessRow
                {
                    ProcessNumber = r.IsDBNull(0) ? "" : r.GetString(0),
                    Source = r.IsDBNull(1) ? "" : r.GetString(1),
                    CreatedAt = r.IsDBNull(2) ? DateTime.MinValue : r.GetDateTime(2),
                    Json = r.IsDBNull(3) ? "" : r.GetString(3)
                });
            }
            return rows;
        }

        public static List<RawStructureRow> ListRawStructures(string? pgUri = null, string? sourceContains = null, int? limit = null, int? offset = null)
        {
            var uri = GetPgUri(pgUri);
            var rows = new List<RawStructureRow>();
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            var sql = @"SELECT process_number, source, created_at, COALESCE(structure_json::text,'')
                        FROM raw_structures";
            if (!string.IsNullOrWhiteSpace(sourceContains))
                sql += " WHERE source ILIKE @s";
            sql += " ORDER BY created_at DESC";
            if (limit.HasValue && limit.Value > 0)
                sql += " LIMIT " + limit.Value;
            if (offset.HasValue && offset.Value > 0)
                sql += " OFFSET " + offset.Value;

            using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = 120;
            if (!string.IsNullOrWhiteSpace(sourceContains))
                cmd.Parameters.AddWithValue("@s", "%" + sourceContains + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new RawStructureRow
                {
                    ProcessNumber = r.IsDBNull(0) ? "" : r.GetString(0),
                    Source = r.IsDBNull(1) ? "" : r.GetString(1),
                    CreatedAt = r.IsDBNull(2) ? DateTime.MinValue : r.GetDateTime(2),
                    Json = r.IsDBNull(3) ? "" : r.GetString(3)
                });
            }
            return rows;
        }

        public static FooterInfo? GetFooterInfo(string processNumber, string? pgUri = null)
        {
            if (string.IsNullOrWhiteSpace(processNumber)) return null;
            try
            {
                var uri = GetPgUri(pgUri);
                using var conn = new NpgsqlConnection(uri);
                conn.Open();
                using var cmd = new NpgsqlCommand("SELECT footer_signers, footer_signature_raw FROM processes WHERE process_number=@p LIMIT 1", conn);
                cmd.Parameters.AddWithValue("@p", processNumber);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;

                var info = new FooterInfo();
                if (!r.IsDBNull(0))
                {
                    try
                    {
                        if (r.GetValue(0) is string[] arr)
                            info.Signers = arr.ToList();
                        else
                            info.Signers = (r.GetFieldValue<string[]>(0) ?? Array.Empty<string>()).ToList();
                    }
                    catch
                    {
                        info.Signers = new List<string>();
                    }
                }
                if (!r.IsDBNull(1))
                    info.SignatureRaw = r.GetString(1) ?? "";
                return info;
            }
            catch
            {
                return null;
            }
        }

        public static ProcessSummary? GetProcessSummaryById(long id, string? pgUri = null)
        {
            var uri = GetPgUri(pgUri);
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            using var cmd = new NpgsqlCommand(@"SELECT process_number, total_pages, total_words, total_images, total_fonts, scan_ratio, is_scanned,
                                                      is_encrypted, perm_copy, perm_print, perm_annotate, perm_fill_forms, perm_extract, perm_assemble, perm_print_hq,
                                                      has_js, has_embedded_files, has_attachments, has_multimedia, has_forms,
                                                      meta_title, meta_author, meta_subject, meta_keywords
                                               FROM processes WHERE id=@id", conn);
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new ProcessSummary
            {
                ProcessNumber = r.IsDBNull(0) ? "" : r.GetString(0),
                TotalPages = r.IsDBNull(1) ? 0 : r.GetInt32(1),
                TotalWords = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                TotalImages = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                TotalFonts = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                ScanRatio = r.IsDBNull(5) ? 0 : r.GetDecimal(5),
                IsScanned = !r.IsDBNull(6) && r.GetBoolean(6),
                IsEncrypted = !r.IsDBNull(7) && r.GetBoolean(7),
                PermCopy = !r.IsDBNull(8) && r.GetBoolean(8),
                PermPrint = !r.IsDBNull(9) && r.GetBoolean(9),
                PermAnnotate = !r.IsDBNull(10) && r.GetBoolean(10),
                PermFillForms = !r.IsDBNull(11) && r.GetBoolean(11),
                PermExtract = !r.IsDBNull(12) && r.GetBoolean(12),
                PermAssemble = !r.IsDBNull(13) && r.GetBoolean(13),
                PermPrintHq = !r.IsDBNull(14) && r.GetBoolean(14),
                HasJs = !r.IsDBNull(15) && r.GetBoolean(15),
                HasEmbedded = !r.IsDBNull(16) && r.GetBoolean(16),
                HasAttachments = !r.IsDBNull(17) && r.GetBoolean(17),
                HasMultimedia = !r.IsDBNull(18) && r.GetBoolean(18),
                HasForms = !r.IsDBNull(19) && r.GetBoolean(19),
                MetaTitle = r.IsDBNull(20) ? "" : r.GetString(20),
                MetaAuthor = r.IsDBNull(21) ? "" : r.GetString(21),
                MetaSubject = r.IsDBNull(22) ? "" : r.GetString(22),
                MetaKeywords = r.IsDBNull(23) ? "" : r.GetString(23)
            };
        }

        public static (PDFAnalysisResult analysis, ProcessRow row)? GetByIndex(string cacheIndex, string? pgUri = null)
        {
            if (!int.TryParse(cacheIndex, out var idx) || idx <= 0) return null;
            var list = ListProcesses(pgUri);
            if (idx > list.Count) return null;
            var row = list[idx - 1];
            var analysis = Deserialize(row.Json) ?? BuildFromDb(row.Id, GetPgUri(pgUri));
            return analysis == null ? null : (analysis, row);
        }

        public class BookmarkRow
        {
            public string Title { get; set; } = "";
            public int PageNumber { get; set; }
            public int Level { get; set; }
        }

        public class PageRow
        {
            public int PageNumber { get; set; }
            public string Text { get; set; } = "";
            public string Header { get; set; } = "";
            public string Footer { get; set; } = "";
            public int Words { get; set; }
            public int Chars { get; set; }
            public bool IsScanned { get; set; }
            public int ImageCount { get; set; }
            public int AnnotationCount { get; set; }
            public bool HasForm { get; set; }
            public bool HasJs { get; set; }
            public int FontCount { get; set; }
        }

        public class DocumentRow
        {
            public string DocKey { get; set; } = "";
            public string DocLabel { get; set; } = "";
            public string DocType { get; set; } = "";
            public string Subtype { get; set; } = "";
            public int StartPage { get; set; }
            public int EndPage { get; set; }
            public int TotalPages { get; set; }
            public int TotalWords { get; set; }
            public int TotalImages { get; set; }
        }

        public static List<BookmarkRow> ListBookmarks(long processId, string? pgUri = null)
        {
            var uri = GetPgUri(pgUri);
            var list = new List<BookmarkRow>();
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT title, page_number, level FROM bookmarks WHERE process_id=@p ORDER BY page_number", conn);
            cmd.Parameters.AddWithValue("@p", processId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new BookmarkRow
                {
                    Title = r.IsDBNull(0) ? "" : r.GetString(0),
                    PageNumber = r.IsDBNull(1) ? 0 : r.GetInt32(1),
                    Level = r.IsDBNull(2) ? 0 : r.GetInt32(2)
                });
            }
            return list;
        }

        public static List<PageRow> ListPages(long processId, string? pgUri = null)
        {
            var uri = GetPgUri(pgUri);
            var list = new List<PageRow>();
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            using var cmd = new NpgsqlCommand(@"SELECT p.page_number, p.text, p.header_virtual, p.footer_virtual,
                                                      COALESCE(p.words,0), COALESCE(p.chars,0), COALESCE(p.is_scanned,false),
                                                      COALESCE(p.image_count,0), COALESCE(p.annotation_count,0), COALESCE(p.has_form,false), COALESCE(p.has_js,false), COALESCE(p.font_count,0)
                                               FROM pages p
                                               JOIN documents d ON d.id = p.document_id
                                               WHERE d.process_id=@pid
                                               ORDER BY p.page_number", conn);
            cmd.Parameters.AddWithValue("@pid", processId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new PageRow
                {
                    PageNumber = r.GetInt32(0),
                    Text = r.IsDBNull(1) ? "" : r.GetString(1),
                    Header = r.IsDBNull(2) ? "" : r.GetString(2),
                    Footer = r.IsDBNull(3) ? "" : r.GetString(3),
                    Words = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    Chars = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    IsScanned = !r.IsDBNull(6) && r.GetBoolean(6),
                    ImageCount = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    AnnotationCount = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                    HasForm = !r.IsDBNull(9) && r.GetBoolean(9),
                    HasJs = !r.IsDBNull(10) && r.GetBoolean(10),
                    FontCount = r.IsDBNull(11) ? 0 : r.GetInt32(11)
                });
            }
            return list;
        }

        public static List<DocumentRow> ListDocuments(long processId, string? pgUri = null)
        {
            var uri = GetPgUri(pgUri);
            var list = new List<DocumentRow>();
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            using var cmd = new NpgsqlCommand(@"SELECT doc_key, doc_label_raw, doc_type, subtype, start_page, end_page,
                                                      COALESCE(total_pages,0), COALESCE(total_words,0), COALESCE(total_images,0)
                                               FROM documents WHERE process_id=@pid ORDER BY start_page", conn);
            cmd.Parameters.AddWithValue("@pid", processId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new DocumentRow
                {
                    DocKey = r.IsDBNull(0) ? "" : r.GetString(0),
                    DocLabel = r.IsDBNull(1) ? "" : r.GetString(1),
                    DocType = r.IsDBNull(2) ? "" : r.GetString(2),
                    Subtype = r.IsDBNull(3) ? "" : r.GetString(3),
                    StartPage = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    EndPage = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                    TotalPages = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                    TotalWords = r.IsDBNull(7) ? 0 : r.GetInt32(7),
                    TotalImages = r.IsDBNull(8) ? 0 : r.GetInt32(8)
                });
            }
            return list;
        }

        public static (PDFAnalysisResult analysis, ProcessRow row)? GetByProcess(string processNumber, string? pgUri = null)
        {
            if (string.IsNullOrWhiteSpace(processNumber)) return null;
            var uri = GetPgUri(pgUri);
            using var conn = new NpgsqlConnection(uri);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT id, process_number, source, created_at, COALESCE(json,'') FROM processes WHERE process_number=@p LIMIT 1", conn);
            cmd.Parameters.AddWithValue("@p", processNumber);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var row = new ProcessRow
            {
                Id = r.GetInt64(0),
                ProcessNumber = r.IsDBNull(1) ? "" : r.GetString(1),
                Source = r.IsDBNull(2) ? "" : r.GetString(2),
                CreatedAt = r.IsDBNull(3) ? DateTime.MinValue : r.GetDateTime(3),
                Json = r.IsDBNull(4) ? "" : r.GetString(4)
            };
            var analysis = Deserialize(row.Json) ?? BuildFromDb(row.Id, uri);
            return analysis == null ? null : (analysis, row);
        }

        public static PDFAnalysisResult? Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonConvert.DeserializeObject<PDFAnalysisResult>(json); }
            catch { return null; }
        }

        /// <summary>
        /// Construção mínima a partir das tabelas documents/pages quando o JSON não está disponível.
        /// </summary>
        private static PDFAnalysisResult? BuildFromDb(long processId, string pgUri)
        {
            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();

            // Pré-carrega recursos ricos por página
            var imgsByPage = new Dictionary<long, List<ImageInfo>>();
            using (var cmdImgs = new NpgsqlCommand(@"
                SELECT i.page_id, i.img_index, i.name, i.width, i.height, i.color_space, i.compression, i.bits_per_component, i.estimated_size
                  FROM page_images i
                  JOIN pages p ON p.id = i.page_id
                  JOIN documents d ON d.id = p.document_id
                 WHERE d.process_id=@pid
                 ORDER BY i.page_id, i.img_index", conn))
            {
                cmdImgs.Parameters.AddWithValue("@pid", processId);
                using var r = cmdImgs.ExecuteReader();
                while (r.Read())
                {
                    var pid = r.GetInt64(0);
                    if (!imgsByPage.TryGetValue(pid, out var list))
                    {
                        list = new List<ImageInfo>();
                        imgsByPage[pid] = list;
                    }
                    list.Add(new ImageInfo
                    {
                        Name = r.IsDBNull(2) ? "" : r.GetString(2),
                        Width = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                        Height = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                        ColorSpace = r.IsDBNull(5) ? "" : r.GetString(5),
                        CompressionType = r.IsDBNull(6) ? "" : r.GetString(6),
                        BitsPerComponent = r.IsDBNull(7) ? 0 : r.GetInt32(7)
                    });
                }
            }

            var annByPage = new Dictionary<long, List<Annotation>>();
            using (var cmdAnn = new NpgsqlCommand(@"
                SELECT a.page_id, a.type, a.author, a.subject, a.contents, a.modification_date, a.x, a.y
                  FROM page_annotations a
                  JOIN pages p ON p.id = a.page_id
                  JOIN documents d ON d.id = p.document_id
                 WHERE d.process_id=@pid", conn))
            {
                cmdAnn.Parameters.AddWithValue("@pid", processId);
                using var r = cmdAnn.ExecuteReader();
                while (r.Read())
                {
                    var pid = r.GetInt64(0);
                    if (!annByPage.TryGetValue(pid, out var list))
                    {
                        list = new List<Annotation>();
                        annByPage[pid] = list;
                    }
                    list.Add(new Annotation
                    {
                        Type = r.IsDBNull(1) ? "" : r.GetString(1),
                        Author = r.IsDBNull(2) ? "" : r.GetString(2),
                        Subject = r.IsDBNull(3) ? "" : r.GetString(3),
                        Contents = r.IsDBNull(4) ? "" : r.GetString(4),
                        ModificationDate = r.IsDBNull(5) ? (DateTime?)null : r.GetDateTime(5),
                        X = r.IsDBNull(6) ? 0 : (float)r.GetDouble(6),
                        Y = r.IsDBNull(7) ? 0 : (float)r.GetDouble(7)
                    });
                }
            }

            var fontsByPage = new Dictionary<long, List<FontInfo>>();
            using (var cmdFonts = new NpgsqlCommand(@"
                SELECT f.page_id, f.name, f.base_font, f.font_type, f.size, f.is_embedded, f.is_bold, f.is_italic, f.is_underline, f.is_strikeout, f.is_monospace, f.is_serif, f.is_sans
                  FROM page_fonts f
                  JOIN pages p ON p.id = f.page_id
                  JOIN documents d ON d.id = p.document_id
                 WHERE d.process_id=@pid", conn))
            {
                cmdFonts.Parameters.AddWithValue("@pid", processId);
                using var r = cmdFonts.ExecuteReader();
                while (r.Read())
                {
                    var pid = r.GetInt64(0);
                    if (!fontsByPage.TryGetValue(pid, out var list))
                    {
                        list = new List<FontInfo>();
                        fontsByPage[pid] = list;
                    }
                    list.Add(new FontInfo
                    {
                        Name = r.IsDBNull(1) ? "" : r.GetString(1),
                        BaseFont = r.IsDBNull(2) ? "" : r.GetString(2),
                        FontType = r.IsDBNull(3) ? "" : r.GetString(3),
                        Size = r.IsDBNull(4) ? 0 : (float)r.GetDouble(4),
                        IsEmbedded = !r.IsDBNull(5) && r.GetBoolean(5),
                        IsBold = !r.IsDBNull(6) && r.GetBoolean(6),
                        IsItalic = !r.IsDBNull(7) && r.GetBoolean(7),
                        IsUnderline = !r.IsDBNull(8) && r.GetBoolean(8),
                        IsStrikeout = !r.IsDBNull(9) && r.GetBoolean(9),
                        IsMonospace = !r.IsDBNull(10) && r.GetBoolean(10),
                        IsSerif = !r.IsDBNull(11) && r.GetBoolean(11),
                        IsSansSerif = !r.IsDBNull(12) && r.GetBoolean(12)
                    });
                }
            }

            // 1) Carrega informações agregadas do processo
            using (var cmdProcess = new NpgsqlCommand(@"
                SELECT process_number, COALESCE(total_pages,0), COALESCE(total_words,0), COALESCE(total_images,0),
                       COALESCE(total_fonts,0), COALESCE(scan_ratio,0), COALESCE(is_scanned,false),
                       COALESCE(is_encrypted,false), COALESCE(perm_copy,false), COALESCE(perm_print,false),
                       COALESCE(perm_annotate,false), COALESCE(perm_fill_forms,false), COALESCE(perm_extract,false),
                       COALESCE(perm_assemble,false), COALESCE(perm_print_hq,false),
                       COALESCE(has_js,false), COALESCE(has_embedded_files,false), COALESCE(has_attachments,false),
                       COALESCE(has_multimedia,false), COALESCE(has_forms,false),
                       COALESCE(meta_title,''), COALESCE(meta_author,''), COALESCE(meta_subject,''), COALESCE(meta_keywords,''),
                       COALESCE(meta_creator,''), COALESCE(meta_producer,''), meta_creation_date, meta_modification_date,
                       COALESCE(header_origin,''), COALESCE(header_title,''), COALESCE(header_subtitle,''),
                       COALESCE(footer_signers,''), footer_signed_at, COALESCE(footer_signature_raw,'')
                FROM processes WHERE id=@pid", conn))
            {
                cmdProcess.Parameters.AddWithValue("@pid", processId);
                using var rp = cmdProcess.ExecuteReader();
                if (!rp.Read()) return null;

                var analysis = new PDFAnalysisResult
                {
                    Pages = new List<PageAnalysis>(),
                    Metadata = new Metadata
                    {
                        Title = rp.IsDBNull(20) ? "" : rp.GetString(20),
                        Author = rp.IsDBNull(21) ? "" : rp.GetString(21),
                        Subject = rp.IsDBNull(22) ? "" : rp.GetString(22),
                        Keywords = rp.IsDBNull(23) ? "" : rp.GetString(23),
                        Creator = rp.IsDBNull(24) ? "" : rp.GetString(24),
                        Producer = rp.IsDBNull(25) ? "" : rp.GetString(25),
                        CreationDate = rp.IsDBNull(26) ? (DateTime?)null : rp.GetDateTime(26),
                        ModificationDate = rp.IsDBNull(27) ? (DateTime?)null : rp.GetDateTime(27)
                    },
                    DocumentInfo = new DocumentInfo
                    {
                        TotalPages = rp.IsDBNull(1) ? 0 : rp.GetInt32(1),
                        IsEncrypted = !rp.IsDBNull(7) && rp.GetBoolean(7),
                        HasAcroForm = !rp.IsDBNull(19) && rp.GetBoolean(19)
                    },
                    Security = new SecurityInfo
                    {
                        IsEncrypted = !rp.IsDBNull(7) && rp.GetBoolean(7),
                        CanCopy = !rp.IsDBNull(8) && rp.GetBoolean(8),
                        CanPrint = !rp.IsDBNull(9) && rp.GetBoolean(9),
                        CanAnnotate = !rp.IsDBNull(10) && rp.GetBoolean(10),
                        CanFillForms = !rp.IsDBNull(11) && rp.GetBoolean(11),
                        CanExtractContent = !rp.IsDBNull(12) && rp.GetBoolean(12),
                        CanAssemble = !rp.IsDBNull(13) && rp.GetBoolean(13),
                        CanPrintHighQuality = !rp.IsDBNull(14) && rp.GetBoolean(14)
                    },
                    Resources = new ResourcesSummary
                    {
                        TotalImages = rp.IsDBNull(3) ? 0 : rp.GetInt32(3),
                        TotalFonts = rp.IsDBNull(4) ? 0 : rp.GetInt32(4),
                        HasJavaScript = !rp.IsDBNull(15) && rp.GetBoolean(15),
                        HasAttachments = !rp.IsDBNull(17) && rp.GetBoolean(17),
                        HasMultimedia = !rp.IsDBNull(18) && rp.GetBoolean(18),
                        Forms = rp.IsDBNull(19) ? 0 : (rp.GetBoolean(19) ? 1 : 0)
                    },
                    Statistics = new Statistics
                    {
                        TotalWords = rp.IsDBNull(2) ? 0 : rp.GetInt32(2),
                        TotalImages = rp.IsDBNull(3) ? 0 : rp.GetInt32(3),
                        UniqueFonts = rp.IsDBNull(4) ? 0 : rp.GetInt32(4)
                    },
                    Bookmarks = new BookmarkStructure()
                };

                analysis.FilePath = rp.IsDBNull(0) ? "" : rp.GetString(0);

                // 2) Carrega páginas
                using (var cmd = new NpgsqlCommand(@"
                    SELECT p.id, p.page_number, p.text, p.header_virtual, p.footer_virtual,
                           COALESCE(p.words,0), COALESCE(p.chars,0), COALESCE(p.is_scanned,false),
                           COALESCE(p.image_count,0), COALESCE(p.annotation_count,0),
                           COALESCE(p.has_form,false), COALESCE(p.has_js,false), COALESCE(p.font_count,0), COALESCE(p.fonts,'')
                    FROM pages p
                    JOIN documents d ON d.id = p.document_id
                    WHERE d.process_id=@pid
                    ORDER BY p.page_number", conn))
                {
                    cmd.Parameters.AddWithValue("@pid", processId);
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var pageId = r.GetInt64(0);
                        var fontsRaw = r.IsDBNull(13) ? "" : r.GetString(13);
                        var fonts = ParseFonts(fontsRaw);
                        if (fontsByPage.TryGetValue(pageId, out var richFonts) && richFonts.Any())
                            fonts = richFonts;
                        var images = imgsByPage.TryGetValue(pageId, out var limg) ? limg : new List<ImageInfo>();
                        var annotations = annByPage.TryGetValue(pageId, out var lann) ? lann : new List<Annotation>();
                        var page = new PageAnalysis
                        {
                            PageNumber = r.IsDBNull(1) ? 0 : r.GetInt32(1),
                            TextInfo = new TextInfo
                            {
                                PageText = r.IsDBNull(2) ? "" : r.GetString(2),
                                WordCount = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                                CharacterCount = r.IsDBNull(6) ? 0 : r.GetInt32(6),
                                Fonts = fonts
                            },
                            Headers = new List<string> { r.IsDBNull(3) ? "" : r.GetString(3) },
                            Footers = new List<string> { r.IsDBNull(4) ? "" : r.GetString(4) },
                            Resources = new PageResources
                            {
                                FontCount = r.IsDBNull(12) ? 0 : r.GetInt32(12),
                                HasForms = !r.IsDBNull(10) && r.GetBoolean(10),
                                HasMultimedia = !r.IsDBNull(9) && r.GetInt32(9) > 0,
                                Images = images
                            },
                            Annotations = annotations
                        };
                        analysis.Pages.Add(page);
                    }
                }

                // 3) Bookmarks
                var bkRows = ListBookmarks(processId, pgUri);
                if (bkRows.Any())
                {
                    var root = new List<BookmarkItem>();
                    foreach (var b in bkRows)
                    {
                        root.Add(new BookmarkItem
                        {
                            Title = b.Title,
                            Destination = new BookmarkDestination { PageNumber = b.PageNumber },
                            Level = b.Level
                        });
                    }
                    analysis.Bookmarks = new BookmarkStructure { RootItems = root, TotalCount = root.Count };
                }

                // 4) Ajusta estatísticas derivadas
                analysis.DocumentInfo.TotalPages = analysis.Pages.Count;
                analysis.Statistics.TotalWords = analysis.Pages.Sum(p => p.TextInfo?.WordCount ?? 0);
                analysis.Statistics.TotalImages = analysis.Pages.Sum(p => p.Resources?.Images?.Count ?? 0);
                analysis.Resources.TotalImages = analysis.Statistics.TotalImages;
                analysis.Resources.TotalFonts = Math.Max(analysis.Resources.TotalFonts, analysis.Pages.Sum(p => p.TextInfo?.Fonts?.Count ?? 0));

                return analysis;
            }
        }

        private static List<FontInfo> ParseFonts(string raw)
        {
            var list = new List<FontInfo>();
            if (string.IsNullOrWhiteSpace(raw)) return list;
            foreach (var name in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                list.Add(new FontInfo { Name = name });
            }
            return list;
        }

        private static List<ImageInfo> BuildDummyImages(int count)
        {
            var imgs = new List<ImageInfo>();
            for (int i = 0; i < count; i++)
            {
                imgs.Add(new ImageInfo { Name = $"img{i + 1}", Width = 0, Height = 0 });
            }
            return imgs;
        }

        private static List<Annotation> BuildDummyAnnotations(int count)
        {
            var list = new List<Annotation>();
            for (int i = 0; i < count; i++)
            {
                list.Add(new Annotation { Type = "Unknown" });
            }
            return list;
        }
    }
}
