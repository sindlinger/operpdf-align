using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Obj.Models;
using Npgsql;
using NpgsqlTypes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace Obj.Utils
{
    /// <summary>
    /// Persiste processos/documentos/páginas diretamente no Postgres
    /// seguindo o schema de tools/pg_ddl_new.sql.
    /// </summary>
    public static class PgDocStore
    {
        public static string DefaultPgUri = "postgres://fpdf:fpdf@localhost:5432/fpdf";

        private static string Clean(string? s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\0", " ");
        }

        private static object? SanitizeJson(object? value)
        {
            if (value == null) return null;
            if (value is string s) return Clean(s);
            if (value is Dictionary<string, object> dict)
            {
                var cleanDict = new Dictionary<string, object>(dict.Count, dict.Comparer);
                foreach (var kv in dict)
                    cleanDict[kv.Key] = SanitizeJson(kv.Value) ?? "";
                return cleanDict;
            }
            if (value is IList<object> list)
            {
                var cleanList = new List<object>(list.Count);
                foreach (var item in list)
                    cleanList.Add(SanitizeJson(item) ?? "");
                return cleanList;
            }
            if (value is IEnumerable<string> strEnum)
                return strEnum.Select(Clean).ToList();
            return value;
        }

        public static string SerializeForPostgres(object payload, Formatting formatting = Formatting.None)
        {
            var settings = new JsonSerializerSettings
            {
                FloatFormatHandling = FloatFormatHandling.String,
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
                DateFormatHandling = DateFormatHandling.IsoDateFormat,
                Culture = CultureInfo.InvariantCulture,
                Formatting = formatting
            };
            return JsonConvert.SerializeObject(payload, settings);
        }

        public static void UpsertRawProcess(string pgUri, string processNumber, string sourcePath, string rawJson)
        {
            pgUri = NormalizePgUri(pgUri);
            processNumber = Clean(processNumber);
            rawJson = rawJson ?? "";
            rawJson = Regex.Replace(rawJson, @"\p{C}+", " ");
            // Remove unpaired surrogate escape sequences that Postgres JSON rejects.
            rawJson = Regex.Replace(rawJson, @"\\uD[89AB][0-9A-F]{2}", " ", RegexOptions.IgnoreCase);
            rawJson = Regex.Replace(rawJson, @"\\uD[CDEF][0-9A-F]{2}", " ", RegexOptions.IgnoreCase);
            // Remove control Unicode escapes (U+0000..U+001F) that break Postgres JSON parsing.
            rawJson = Regex.Replace(rawJson, @"\\u00(0[0-9A-F]|1[0-9A-F])", " ", RegexOptions.IgnoreCase);
            // Remove malformed \\u escapes that aren't followed by 4 hex digits.
            rawJson = Regex.Replace(rawJson, @"\\u(?![0-9A-Fa-f]{4})", " ", RegexOptions.IgnoreCase);

            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();

            // ensure table
            using (var ensure = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS raw_processes(
                    process_number text PRIMARY KEY,
                    source text,
                    created_at timestamptz default now(),
                    raw_json jsonb
                );
            ", conn))
            {
                ensure.ExecuteNonQuery();
            }

            using (var cmd = new NpgsqlCommand(@"
                INSERT INTO raw_processes(process_number, source, raw_json)
                VALUES (@p,@s,@j)
                ON CONFLICT (process_number) DO NOTHING;
            ", conn))
            {
                cmd.Parameters.AddWithValue("@p", processNumber);
                cmd.Parameters.AddWithValue("@s", sourcePath ?? "");
                cmd.Parameters.Add("@j", NpgsqlDbType.Jsonb).Value = rawJson;
                cmd.ExecuteNonQuery();
            }

            // Se já existir, deixamos intocado (read-only); opcional: avisar
            using (var check = new NpgsqlCommand("SELECT 1 FROM raw_processes WHERE process_number=@p", conn))
            {
                check.Parameters.AddWithValue("@p", processNumber);
                using var r = check.ExecuteReader();
                if (!r.Read())
                {
                    Console.WriteLine($"[PgDocStore] raw salvo (novo) {processNumber}");
                }
            }
        }

        public static void UpsertRawFile(string pgUri, string processNumber, string sourcePath, byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length == 0) return;
            pgUri = NormalizePgUri(pgUri);
            processNumber = Clean(processNumber);
            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();

            using (var ensure = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS raw_files(
                    process_number text PRIMARY KEY,
                    source text,
                    created_at timestamptz default now(),
                    file_bytes bytea,
                    sha256 text,
                    file_size bigint
                );
            ", conn))
            {
                ensure.ExecuteNonQuery();
            }

            // Evita sobrescrever: raw_files é somente inserção
            using var cmd = new NpgsqlCommand(@"
                INSERT INTO raw_files(process_number, source, file_bytes, sha256, file_size)
                VALUES (@p,@s,@b,@h,@sz)
                ON CONFLICT (process_number) DO NOTHING;
            ", conn);
            cmd.Parameters.AddWithValue("@p", processNumber);
            cmd.Parameters.AddWithValue("@s", sourcePath ?? "");
            cmd.Parameters.AddWithValue("@b", fileBytes);
            cmd.Parameters.AddWithValue("@h", ComputeSha256Hex(fileBytes));
            cmd.Parameters.AddWithValue("@sz", (long)fileBytes.Length);
            cmd.ExecuteNonQuery();
        }

        public static void UpsertRawStructure(string pgUri, string processNumber, string sourcePath, string structureJson)
        {
            if (string.IsNullOrWhiteSpace(structureJson)) return;
            pgUri = NormalizePgUri(pgUri);
            processNumber = Clean(processNumber);
            structureJson = structureJson ?? "";
            structureJson = Regex.Replace(structureJson, @"\p{C}+", " ");
            structureJson = Regex.Replace(structureJson, @"\\uD[89AB][0-9A-F]{2}", " ", RegexOptions.IgnoreCase);
            structureJson = Regex.Replace(structureJson, @"\\uD[CDEF][0-9A-F]{2}", " ", RegexOptions.IgnoreCase);
            structureJson = Regex.Replace(structureJson, @"\\u00(0[0-9A-F]|1[0-9A-F])", " ", RegexOptions.IgnoreCase);
            structureJson = Regex.Replace(structureJson, @"\\u(?![0-9A-Fa-f]{4})", " ", RegexOptions.IgnoreCase);

            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();

            using (var ensure = new NpgsqlCommand(@"
                CREATE TABLE IF NOT EXISTS raw_structures(
                    process_number text PRIMARY KEY,
                    source text,
                    created_at timestamptz default now(),
                    structure_json jsonb
                );
            ", conn))
            {
                ensure.ExecuteNonQuery();
            }

            using var cmd = new NpgsqlCommand(@"
                INSERT INTO raw_structures(process_number, source, structure_json)
                VALUES (@p,@s,@j)
                ON CONFLICT (process_number) DO NOTHING;
            ", conn);
            cmd.Parameters.AddWithValue("@p", processNumber);
            cmd.Parameters.AddWithValue("@s", sourcePath ?? "");
            cmd.Parameters.Add("@j", NpgsqlDbType.Jsonb).Value = structureJson;
            cmd.ExecuteNonQuery();
        }

        public static byte[]? FetchRawFile(string pgUri, string processNumber)
        {
            if (string.IsNullOrWhiteSpace(processNumber)) return null;
            pgUri = NormalizePgUri(pgUri);
            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT file_bytes FROM raw_files WHERE process_number=@p", conn);
            cmd.Parameters.AddWithValue("@p", processNumber);
            var obj = cmd.ExecuteScalar();
            return obj == null || obj == DBNull.Value ? null : (byte[])obj;
        }

        private static string ComputeSha256Hex(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = sha.ComputeHash(data);
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static string? FetchRawProcess(string pgUri, string processNumber)
        {
            pgUri = NormalizePgUri(pgUri);
            processNumber = Clean(processNumber);
            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT raw_json FROM raw_processes WHERE process_number=@p", conn);
            cmd.Parameters.AddWithValue("@p", processNumber);
            var obj = cmd.ExecuteScalar();
            return obj == null || obj == DBNull.Value ? null : obj.ToString();
        }

        public static string? FetchRawStructure(string pgUri, string processNumber)
        {
            pgUri = NormalizePgUri(pgUri);
            processNumber = Clean(processNumber);
            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT structure_json FROM raw_structures WHERE process_number=@p", conn);
            cmd.Parameters.AddWithValue("@p", processNumber);
            var obj = cmd.ExecuteScalar();
            return obj == null || obj == DBNull.Value ? null : obj.ToString();
        }

        public static string NormalizePgUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) throw new ArgumentNullException(nameof(uri));
            if (uri.StartsWith("Host=", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("User Id=", StringComparison.OrdinalIgnoreCase))
                return uri; // already connection string format

            if (uri.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            {
                var u = new Uri(uri);
                var userInfo = u.UserInfo.Split(':');
                var user = userInfo.Length > 0 ? userInfo[0] : "";
                var pass = userInfo.Length > 1 ? userInfo[1] : "";
                var builder = new NpgsqlConnectionStringBuilder
                {
                    Host = u.Host,
                    Port = u.IsDefaultPort ? 5432 : u.Port,
                    Username = user,
                    Password = pass,
                    Database = u.AbsolutePath.TrimStart('/')
                };
                return builder.ToString();
            }

            throw new ArgumentException("Invalid Postgres URI format", nameof(uri));
        }

        public static long UpsertProcess(string pgUri, string sourcePath, PDFAnalysisResult analysis, BookmarkClassifier classifier, bool storeJson = false, bool storeDocuments = false, string? jsonPayload = null)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (classifier == null) throw new ArgumentNullException(nameof(classifier));

            pgUri = NormalizePgUri(pgUri);

            var processNumber = Clean(InferProcessFromName(sourcePath) ?? InferProcessFromMetadata(analysis) ?? "sem_processo");
            var headerFooter = ExtractHeaderFooter(analysis?.Pages);
            var totalPages = analysis?.DocumentInfo?.TotalPages ?? analysis?.Pages?.Count ?? 0;

            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();
            using var tx = conn.BeginTransaction();

            long processId;
            string? analysisJson = null;
            if (storeJson)
            {
                if (!string.IsNullOrEmpty(jsonPayload))
                {
                    analysisJson = jsonPayload;
                }
                else
                {
                    analysisJson = JsonConvert.SerializeObject(analysis);
                }
            }

            using (var cmd = new NpgsqlCommand(@"
                INSERT INTO processes(process_number, source, json,
                    total_pages, total_words, total_chars, total_images, total_fonts, scan_ratio, is_scanned,
                    is_encrypted, perm_copy, perm_print, perm_annotate, perm_fill_forms, perm_extract, perm_assemble, perm_print_hq,
                    has_js, has_embedded_files, has_attachments, has_multimedia, has_forms,
                    meta_title, meta_author, meta_subject, meta_keywords, meta_creator, meta_producer, created_pdf, modified_pdf, doc_types,
                    header_origin, header_title, header_subtitle, footer_signers, footer_signed_at, footer_signature_raw)
                VALUES (@p,@s,@j,
                    @tp,@tw,@tc,@ti,@tf,@sr,@isc,
                    @enc,@pcopy,@pprint,@pannot,@pfill,@pextract,@passemble,@pprinthq,
                    @hjs,@hemb,@hatt,@hmult,@hforms,
                    @mt,@ma,@ms,@mk,@mc,@mpr,@cpdf,@mpdf,@dtypes,
                    @ho,@ht,@hs,@fs,@fsa,@fsr)
                ON CONFLICT (process_number) DO UPDATE SET
                    source = EXCLUDED.source,
                    json   = EXCLUDED.json,
                    total_pages = EXCLUDED.total_pages,
                    total_words = EXCLUDED.total_words,
                    total_chars = EXCLUDED.total_chars,
                    total_images = EXCLUDED.total_images,
                    total_fonts = EXCLUDED.total_fonts,
                    scan_ratio = EXCLUDED.scan_ratio,
                    is_scanned = EXCLUDED.is_scanned,
                    is_encrypted = EXCLUDED.is_encrypted,
                    perm_copy = EXCLUDED.perm_copy,
                    perm_print = EXCLUDED.perm_print,
                    perm_annotate = EXCLUDED.perm_annotate,
                    perm_fill_forms = EXCLUDED.perm_fill_forms,
                    perm_extract = EXCLUDED.perm_extract,
                    perm_assemble = EXCLUDED.perm_assemble,
                    perm_print_hq = EXCLUDED.perm_print_hq,
                    has_js = EXCLUDED.has_js,
                    has_embedded_files = EXCLUDED.has_embedded_files,
                    has_attachments = EXCLUDED.has_attachments,
                    has_multimedia = EXCLUDED.has_multimedia,
                    has_forms = EXCLUDED.has_forms,
                    meta_title = EXCLUDED.meta_title,
                    meta_author = EXCLUDED.meta_author,
                    meta_subject = EXCLUDED.meta_subject,
                    meta_keywords = EXCLUDED.meta_keywords,
                    meta_creator = EXCLUDED.meta_creator,
                    meta_producer = EXCLUDED.meta_producer,
                    created_pdf = EXCLUDED.created_pdf,
                    modified_pdf = EXCLUDED.modified_pdf,
                    doc_types = EXCLUDED.doc_types,
                    header_origin = EXCLUDED.header_origin,
                    header_title = EXCLUDED.header_title,
                    header_subtitle = EXCLUDED.header_subtitle,
                    footer_signers = EXCLUDED.footer_signers,
                    footer_signed_at = EXCLUDED.footer_signed_at,
                    footer_signature_raw = EXCLUDED.footer_signature_raw
                RETURNING id;
            ", conn, tx))
            {
                cmd.Parameters.AddWithValue("@p", processNumber);
                cmd.Parameters.AddWithValue("@s", sourcePath ?? "");
                if (analysisJson != null)
                    cmd.Parameters.Add("@j", NpgsqlDbType.Jsonb).Value = analysisJson;
                else
                    cmd.Parameters.AddWithValue("@j", DBNull.Value);
            var agg = AggregateProcess(analysis);
                cmd.Parameters.AddWithValue("@tp", agg.TotalPages);
                cmd.Parameters.Add("@tw", NpgsqlDbType.Bigint).Value = (long)agg.TotalWords;
                cmd.Parameters.Add("@tc", NpgsqlDbType.Bigint).Value = (long)agg.TotalChars;
                cmd.Parameters.AddWithValue("@ti", agg.TotalImages);
                cmd.Parameters.AddWithValue("@tf", agg.TotalFonts);
                cmd.Parameters.AddWithValue("@sr", agg.ScanRatio);
                cmd.Parameters.AddWithValue("@isc", agg.IsScanned);
                cmd.Parameters.AddWithValue("@enc", agg.IsEncrypted);
                cmd.Parameters.AddWithValue("@pcopy", agg.PermCopy);
                cmd.Parameters.AddWithValue("@pprint", agg.PermPrint);
                cmd.Parameters.AddWithValue("@pannot", agg.PermAnnotate);
                cmd.Parameters.AddWithValue("@pfill", agg.PermFillForms);
                cmd.Parameters.AddWithValue("@pextract", agg.PermExtract);
                cmd.Parameters.AddWithValue("@passemble", agg.PermAssemble);
                cmd.Parameters.AddWithValue("@pprinthq", agg.PermPrintHq);
                cmd.Parameters.AddWithValue("@hjs", agg.HasJs);
                cmd.Parameters.AddWithValue("@hemb", agg.HasEmbedded);
                cmd.Parameters.AddWithValue("@hatt", agg.HasAttachments);
                cmd.Parameters.AddWithValue("@hmult", agg.HasMultimedia);
                cmd.Parameters.AddWithValue("@hforms", agg.HasForms);
                cmd.Parameters.AddWithValue("@mt", (object?)Clean(agg.MetaTitle) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ma", (object?)Clean(agg.MetaAuthor) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ms", (object?)Clean(agg.MetaSubject) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@mk", (object?)Clean(agg.MetaKeywords) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@mc", (object?)Clean(agg.MetaCreator) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@mpr", (object?)Clean(agg.MetaProducer) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cpdf", agg.CreatedPdf.HasValue ? agg.CreatedPdf.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@mpdf", agg.ModifiedPdf.HasValue ? agg.ModifiedPdf.Value : (object)DBNull.Value);
                cmd.Parameters.Add("@dtypes", NpgsqlDbType.Jsonb).Value = (agg.DocTypes ?? new List<string>()).Select(Clean).ToList();
                cmd.Parameters.AddWithValue("@ho", (object?)Clean(headerFooter.HeaderOrigin) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ht", (object?)Clean(headerFooter.HeaderTitle) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hs", (object?)Clean(headerFooter.HeaderSubtitle) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fs", headerFooter.FooterSigners?.Select(Clean).ToArray() ?? Array.Empty<string>());
                cmd.Parameters.AddWithValue("@fsa", headerFooter.FooterSignedAt.HasValue ? headerFooter.FooterSignedAt.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@fsr", (object?)Clean(headerFooter.FooterSignatureRaw) ?? DBNull.Value);
                processId = Convert.ToInt64(cmd.ExecuteScalar());
            }

            if (storeDocuments)
            {
                // Apaga documentos/páginas anteriores desse processo (idempotente)
                using (var del = new NpgsqlCommand("DELETE FROM documents WHERE process_id=@pid", conn, tx))
                {
                    del.Parameters.AddWithValue("@pid", processId);
                    del.ExecuteNonQuery();
                }

                var allDocs = BuildDocuments(analysis ?? new PDFAnalysisResult(), classifier, out var flatBookmarks);

                foreach (var doc in allDocs)
                {
                    long docId;
                    using (var cmd = new NpgsqlCommand(@"
                        INSERT INTO documents(process_id, doc_key, doc_label_raw, doc_type, subtype, start_page, end_page, meta,
                                              header_origin, header_title, header_subtitle, footer_signers, footer_signed_at, footer_signature_raw,
                                              total_pages, total_words, total_chars, total_images, total_fonts, scan_ratio, has_forms, has_annotations)
                        VALUES (@pid,@key,@label,@type,@sub,@sp,@ep,@meta,@ho,@ht,@hs,@fs,@fsa,@fsr,
                                @tp,@tw,@tc,@ti,@tf,@sr,@hf,@ha)
                        RETURNING id;
                    ", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@pid", processId);
                        cmd.Parameters.AddWithValue("@key", Clean(doc.DocKey));
                        cmd.Parameters.AddWithValue("@label", Clean(doc.RawLabel) ?? "");
                        cmd.Parameters.AddWithValue("@type", Clean(doc.Macro) ?? "outros");
                        cmd.Parameters.AddWithValue("@sub", Clean(doc.Subcat) ?? "outros");
                        cmd.Parameters.AddWithValue("@sp", doc.StartPage);
                        cmd.Parameters.AddWithValue("@ep", doc.EndPage);
                        var metaSan = SanitizeJson(doc.Meta ?? new Dictionary<string, object>());
                        cmd.Parameters.Add("@meta", NpgsqlDbType.Jsonb).Value = metaSan ?? new Dictionary<string, object>();
                        cmd.Parameters.AddWithValue("@ho", (object?)Clean(doc.HeaderOrigin) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ht", (object?)Clean(doc.HeaderTitle) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@hs", (object?)Clean(doc.HeaderSubtitle) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@fs", doc.FooterSigners?.Select(Clean).ToArray() ?? Array.Empty<string>());
                        cmd.Parameters.AddWithValue("@fsa", doc.FooterSignedAt.HasValue ? doc.FooterSignedAt.Value : (object)DBNull.Value);
                        cmd.Parameters.AddWithValue("@fsr", (object?)Clean(doc.FooterSignatureRaw) ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@tp", doc.TotalPages);
                        cmd.Parameters.Add("@tw", NpgsqlDbType.Bigint).Value = (long)doc.TotalWords;
                        cmd.Parameters.Add("@tc", NpgsqlDbType.Bigint).Value = (long)doc.TotalChars;
                        cmd.Parameters.AddWithValue("@ti", doc.TotalImages);
                        cmd.Parameters.AddWithValue("@tf", doc.TotalFonts);
                        cmd.Parameters.AddWithValue("@sr", doc.ScanRatio);
                        cmd.Parameters.AddWithValue("@hf", doc.HasForms);
                        cmd.Parameters.AddWithValue("@ha", doc.HasAnnotations);
                    docId = Convert.ToInt64(cmd.ExecuteScalar());
                }

                    // Páginas e bookmarks não são mais gravados (dados completos ficam no JSON bruto)
                }

                // Removido: persistência de bookmarks. Informação completa continua acessível no JSON bruto.
            }

            tx.Commit();
            return processId;
        }

        /// <summary>
        /// Insere documentos já segmentados (dicts vindos do run) vinculados a um processo.
        /// Remove documentos anteriores para manter idempotência.
        /// </summary>
        public static void UpsertDocuments(string pgUri, long processId, IEnumerable<Dictionary<string, object>> docs)
        {
            pgUri = NormalizePgUri(pgUri);
            using var conn = new NpgsqlConnection(pgUri);
            conn.Open();
            using var tx = conn.BeginTransaction();

            using (var del = new NpgsqlCommand("DELETE FROM documents WHERE process_id=@pid", conn, tx))
            {
                del.Parameters.AddWithValue("@pid", processId);
                del.ExecuteNonQuery();
            }

            int seq = 0;
            foreach (var doc in docs)
            {
                seq++;
                string label = Clean(GetStr(doc, "doc_label"));
                string docType = Clean(GetStr(doc, "doc_type"));
                string docKey = BuildDocKeySafe(label, seq, GetInt(doc, "start_page", 1));

                string headerOrigin = Clean(GetStr(doc, "origin_main"));
                string headerTitle = Clean(GetStr(doc, "origin_sub"));
                string headerSubtitle = Clean(GetStr(doc, "origin_extra"));
                var signer = GetStr(doc, "signer");
                DateTime? signedAt = ParseDate(GetStr(doc, "signed_at"));
                string footerRaw = GetStr(doc, "footer");

                int startPage = GetInt(doc, "start_page", 1);
                int endPage = GetInt(doc, "end_page", startPage);
                int docPages = GetInt(doc, "doc_pages", endPage - startPage + 1);
                long wordCount = GetLong(doc, "word_count");
                long charCount = GetLong(doc, "char_count");
                int images = GetInt(doc, "images");
                int fonts = (doc.TryGetValue("fonts", out var f) && f is IEnumerable<object> farr) ? farr.Count() : 0;
                decimal scanRatio = 0;

                var meta = SanitizeJson(doc) ?? new Dictionary<string, object>();

                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO documents(process_id, doc_key, doc_label_raw, doc_type, subtype, start_page, end_page, meta,
                                          header_origin, header_title, header_subtitle, footer_signers, footer_signed_at, footer_signature_raw,
                                          total_pages, total_words, total_chars, total_images, total_fonts, scan_ratio, has_forms, has_annotations)
                    VALUES (@pid,@key,@label,@type,@sub,@sp,@ep,@meta,
                            @ho,@ht,@hs,@fs,@fsa,@fsr,
                            @tp,@tw,@tc,@ti,@tf,@sr,@hf,@ha);
                ", conn, tx);

                cmd.Parameters.AddWithValue("@pid", processId);
                cmd.Parameters.AddWithValue("@key", docKey);
                cmd.Parameters.AddWithValue("@label", label ?? "");
                cmd.Parameters.AddWithValue("@type", string.IsNullOrWhiteSpace(docType) ? "outros" : docType);
                cmd.Parameters.AddWithValue("@sub", GetStr(doc, "template") ?? "outros");
                cmd.Parameters.AddWithValue("@sp", startPage);
                cmd.Parameters.AddWithValue("@ep", endPage);
                cmd.Parameters.Add("@meta", NpgsqlDbType.Jsonb).Value = meta;
                cmd.Parameters.AddWithValue("@ho", (object?)headerOrigin ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ht", (object?)headerTitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@hs", (object?)headerSubtitle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fs", string.IsNullOrWhiteSpace(signer) ? Array.Empty<string>() : new[] { signer });
                cmd.Parameters.AddWithValue("@fsa", signedAt.HasValue ? signedAt.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@fsr", (object?)footerRaw ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@tp", docPages);
                cmd.Parameters.Add("@tw", NpgsqlDbType.Bigint).Value = wordCount;
                cmd.Parameters.Add("@tc", NpgsqlDbType.Bigint).Value = charCount;
                cmd.Parameters.AddWithValue("@ti", images);
                cmd.Parameters.AddWithValue("@tf", fonts);
                cmd.Parameters.AddWithValue("@sr", scanRatio);
                cmd.Parameters.AddWithValue("@hf", false);
                cmd.Parameters.AddWithValue("@ha", false);

                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
        private static List<DocumentRecord> BuildDocuments(PDFAnalysisResult analysis, BookmarkClassifier classifier, out List<BookmarkFlat> flatOut)
        {
            var totalPages = analysis.DocumentInfo?.TotalPages ?? analysis.Pages.Count;
            var bookmarks = FlattenBookmarks(analysis.Bookmarks?.RootItems ?? new List<BookmarkItem>());
            flatOut = bookmarks.ToList();

            // Ordenar por página inicial
            bookmarks = bookmarks.OrderBy(b => b.StartPage).ToList();

            // Fallback quando não há bookmarks
            if (!bookmarks.Any())
            {
                bookmarks.Add(new BookmarkFlat
                {
                    Title = "sem_bookmark",
                    StartPage = 1,
                    EndPage = totalPages
                });
            }

            // Cobertura de páginas; criar fallback para páginas não cobertas
            var covered = new HashSet<int>(bookmarks.SelectMany(b => Enumerable.Range(b.StartPage, b.EndPage - b.StartPage + 1)));
            var missing = Enumerable.Range(1, totalPages).Where(p => !covered.Contains(p)).ToList();
            if (missing.Any())
            {
                var ranges = ToRanges(missing);
                foreach (var (start, end) in ranges)
                {
                    bookmarks.Add(new BookmarkFlat
                    {
                        Title = "sem_bookmark",
                        StartPage = start,
                        EndPage = end
                    });
                }
            }

            var documents = new List<DocumentRecord>();
            int seq = 0;
            foreach (var b in bookmarks.OrderBy(b => b.StartPage))
            {
                var label = classifier.Classify(b.Title ?? "");
                var pages = analysis.Pages
                    .Where(p => p.PageNumber >= b.StartPage && p.PageNumber <= b.EndPage)
                    .OrderBy(p => p.PageNumber)
                    .Select(p => PageDto.From(p))
                    .ToList();

                var headerFooter = ExtractHeaderFooterPages(pages);

                var doc = new DocumentRecord
                {
                    RawLabel = b.Title ?? "",
                    Macro = label.Macro ?? "",
                    Subcat = label.Subcat ?? "",
                    StartPage = b.StartPage,
                    EndPage = b.EndPage,
                    Pages = pages,
                    DocKey = BuildDocKey(label.Canon, ++seq, b.StartPage),
                    HeaderOrigin = headerFooter.HeaderOrigin ?? "",
                    HeaderTitle = headerFooter.HeaderTitle ?? "",
                    HeaderSubtitle = headerFooter.HeaderSubtitle ?? "",
                    FooterSigners = headerFooter.FooterSigners ?? new List<string>(),
                    FooterSignedAt = headerFooter.FooterSignedAt,
                    FooterSignatureRaw = headerFooter.FooterSignatureRaw ?? "",
                    Meta = new Dictionary<string, object>
                    {
                        ["canonical"] = label.Canon ?? "",
                        ["macro"] = label.Macro ?? "",
                        ["subcat"] = label.Subcat ?? "",
                        ["page_count"] = pages.Count,
                        ["total_chars"] = pages.Sum(p => p.Text?.Length ?? 0),
                        ["has_images"] = (analysis.Resources?.TotalImages ?? analysis.Statistics?.TotalImages ?? 0) > 0
                    }
                };
                doc.TotalPages = pages.Count;
                doc.TotalWords = pages.Sum(p => p.WordCount);
                doc.TotalChars = pages.Sum(p => p.CharCount);
                doc.TotalImages = pages.Sum(p => p.ImageCount);
                doc.TotalFonts = pages.Sum(p => p.FontCount);
                doc.ScanRatio = doc.TotalPages == 0 ? 0 : Math.Round(100m * pages.Count(p => p.IsScanned) / doc.TotalPages, 2);
                doc.HasForms = pages.Any(p => p.HasForm);
                doc.HasAnnotations = pages.Any(p => p.AnnotationCount > 0);
                documents.Add(doc);
            }

            return documents;
        }

        private static string BuildDocKey(string canon, int seq, int startPage)
        {
            var baseKey = string.IsNullOrWhiteSpace(canon) ? "doc" : canon.Replace(' ', '_').ToLower();
            return $"{baseKey}_{seq:000}_{startPage:0000}";
        }

        private static string BuildDocKeySafe(string label, int seq, int startPage)
        {
            var baseKey = string.IsNullOrWhiteSpace(label) ? "doc" : Regex.Replace(label.ToLower(), @"\s+", "_");
            baseKey = Regex.Replace(baseKey, @"[^a-z0-9_]+", "");
            if (string.IsNullOrWhiteSpace(baseKey)) baseKey = "doc";
            return $"{baseKey}_{seq:000}_{startPage:0000}";
        }

        private static List<BookmarkFlat> FlattenBookmarks(List<BookmarkItem> root)
        {
            var list = new List<BookmarkFlat>();
            void Walk(IEnumerable<BookmarkItem> items)
            {
                foreach (var it in items)
                {
                    var flat = new BookmarkFlat
                    {
                        Title = it.Title ?? "",
                        StartPage = Math.Max(1, it.Destination?.PageNumber ?? 1),
                        Level = it.Level
                    };
                    list.Add(flat);
                    if (it.Children != null && it.Children.Any()) Walk(it.Children);
                }
            }
            Walk(root);

            // Calcular endPage por proximidade
            list = list.OrderBy(b => b.StartPage).ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var current = list[i];
                var nextStart = i + 1 < list.Count ? list[i + 1].StartPage : (int?)null;
                current.EndPage = (nextStart.HasValue ? nextStart.Value - 1 : current.StartPage);
                list[i] = current;
            }
            return list;
        }

        private class HeaderFooterInfo
        {
            public string HeaderOrigin { get; set; } = "";
            public string HeaderTitle { get; set; } = "";
            public string HeaderSubtitle { get; set; } = "";
            public List<string> FooterSigners { get; set; } = new();
            public DateTime? FooterSignedAt { get; set; }
            public string FooterSignatureRaw { get; set; } = "";
        }

        private static HeaderFooterInfo ExtractHeaderFooter(IEnumerable<PageAnalysis>? pages)
        {
            var dtoPages = pages?.Select(PageDto.From).ToList() ?? new List<PageDto>();
            return ExtractHeaderFooterPages(dtoPages);
        }

        private static HeaderFooterInfo ExtractHeaderFooterPages(IEnumerable<PageDto> pages)
        {
            var list = pages?.ToList() ?? new List<PageDto>();
            var info = new HeaderFooterInfo();
            if (!list.Any()) return info;

            var first = list.First();
            var last = list.Last();

            // Header: primeiras linhas da primeira página
            var headerLines = (first.Text ?? "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Take(6).Select(l => l.Trim()).ToList();
            if (headerLines.Count > 0)
            {
                info.HeaderOrigin = headerLines[0];
                if (headerLines.Count > 1) info.HeaderTitle = headerLines[1];
                if (headerLines.Count > 2) info.HeaderSubtitle = headerLines[2];
            }

            // Footer: últimas linhas da última página
            var footerLines = (last.Text ?? "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Reverse().Take(10).Reverse().Select(l => l.Trim()).ToList();
            if (footerLines.Count > 0)
            {
                info.FooterSignatureRaw = string.Join("\n", footerLines);
                info.FooterSigners = DetectSigners(footerLines);
                info.FooterSignedAt = DetectDate(footerLines);
            }

            return info;
        }

        private class ProcessAggregate
        {
            public int TotalPages; public int TotalWords; public int TotalChars; public int TotalImages; public int TotalFonts;
            public decimal ScanRatio; public bool IsScanned;
            public bool IsEncrypted; public bool PermCopy; public bool PermPrint; public bool PermAnnotate; public bool PermFillForms; public bool PermExtract; public bool PermAssemble; public bool PermPrintHq;
            public bool HasJs; public bool HasEmbedded; public bool HasAttachments; public bool HasMultimedia; public bool HasForms;
            public string MetaTitle = ""; public string MetaAuthor = ""; public string MetaSubject = ""; public string MetaKeywords = ""; public string MetaCreator = ""; public string MetaProducer = "";
            public DateTime? CreatedPdf; public DateTime? ModifiedPdf;
            public List<string> DocTypes = new();
        }

        private static ProcessAggregate AggregateProcess(PDFAnalysisResult? analysis)
        {
            var agg = new ProcessAggregate();
            var pages = analysis?.Pages ?? new List<PageAnalysis>();
            agg.TotalPages = analysis?.DocumentInfo?.TotalPages ?? pages.Count;
            agg.TotalWords = pages.Sum(p => p?.TextInfo?.WordCount ?? 0);
            agg.TotalChars = pages.Sum(p => p?.TextInfo?.CharacterCount ?? (p?.TextInfo?.PageText?.Length ?? 0));
            agg.TotalImages = pages.Sum(p => p?.Resources?.Images?.Count ?? 0);
            agg.TotalFonts = pages.Sum(p => p?.TextInfo?.Fonts?.Count ?? 0);
            agg.ScanRatio = agg.TotalPages == 0 ? 0 : Math.Round(100m * pages.Count(p => (p?.TextInfo?.WordCount ?? 0) == 0 && (p?.Resources?.Images?.Count ?? 0) > 0) / agg.TotalPages, 2);
            agg.IsScanned = agg.ScanRatio >= 90;
            var sec = analysis?.Security;
            agg.IsEncrypted = sec?.IsEncrypted ?? false;
            agg.PermCopy = sec?.CanCopy ?? true;
            agg.PermPrint = sec?.CanPrint ?? true;
            agg.PermAnnotate = sec?.CanAnnotate ?? true;
            agg.PermFillForms = sec?.CanFillForms ?? true;
            agg.PermExtract = sec?.CanExtractContent ?? true;
            agg.PermAssemble = sec?.CanAssemble ?? true;
            agg.PermPrintHq = sec?.CanPrintHighQuality ?? true;
            var res = analysis?.Resources;
            agg.HasJs = res?.HasJavaScript ?? false;
            agg.HasEmbedded = (res?.EmbeddedFiles?.Count ?? 0) > 0;
            agg.HasAttachments = res?.HasAttachments ?? false;
            agg.HasMultimedia = res?.HasMultimedia ?? false;
            agg.HasForms = (res?.Forms ?? 0) > 0;
            var meta = analysis?.Metadata;
            agg.MetaTitle = meta?.Title ?? "";
            agg.MetaAuthor = meta?.Author ?? "";
            agg.MetaSubject = meta?.Subject ?? "";
            agg.MetaKeywords = meta?.Keywords ?? "";
            agg.MetaCreator = meta?.Creator ?? "";
            agg.MetaProducer = meta?.Producer ?? "";
            agg.CreatedPdf = meta?.CreationDate;
            agg.ModifiedPdf = meta?.ModificationDate;
            agg.DocTypes = new List<string>();
            return agg;
        }

        private static List<string> DetectSigners(IEnumerable<string> lines)
        {
            var signers = new List<string>();
            var nameRegex = new Regex(@"\b[A-ZÁÂÃÉÊÍÓÔÕÚÇ][A-Za-zÁÂÃÉÊÍÓÔÕÚÇàáâãéêíóôõúç'\-]{2,}(?:\s+[A-ZÁÂÃÉÊÍÓÔÕÚÇ][A-Za-zÁÂÃÉÊÍÓÔÕÚÇàáâãéêíóôõúç'\-]{2,})+", RegexOptions.Compiled);
            foreach (var line in lines)
            {
                var m = nameRegex.Match(line);
                if (m.Success)
                {
                    var name = m.Value.Trim();
                    if (!signers.Contains(name)) signers.Add(name);
                }
            }
            return signers;
        }

        private static DateTime? DetectDate(IEnumerable<string> lines)
        {
            var dateRegex = new Regex(@"\b(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})\b", RegexOptions.Compiled);
            foreach (var line in lines)
            {
                var m = dateRegex.Match(line);
                if (m.Success)
                {
                    var d = int.Parse(m.Groups[1].Value);
                    var mo = int.Parse(m.Groups[2].Value);
                    var y = int.Parse(m.Groups[3].Value);
                    if (y < 100) y += 2000;
                    try { return new DateTime(y, mo, d); } catch { }
                }
            }
            return null;
        }

        private static List<(int start, int end)> ToRanges(IEnumerable<int> nums)
        {
            var ranges = new List<(int, int)>();
            var ordered = nums.OrderBy(x => x).ToList();
            if (!ordered.Any()) return ranges;
            int start = ordered[0];
            int prev = ordered[0];
            foreach (var n in ordered.Skip(1))
            {
                if (n == prev + 1)
                {
                    prev = n; continue;
                }
                ranges.Add((start, prev));
                start = n; prev = n;
            }
            ranges.Add((start, prev));
            return ranges;
        }

        private static string GetStr(Dictionary<string, object>? dict, string key)
        {
            if (dict == null || !dict.TryGetValue(key, out var v) || v == null) return "";
            return v.ToString() ?? "";
        }

        private static Dictionary<string, object>? GetDict(Dictionary<string, object> dict, string key)
        {
            if (dict.TryGetValue(key, out var v) && v is Dictionary<string, object> d) return d;
            if (dict.TryGetValue(key, out var j) && j is JObject jo) return jo.ToObject<Dictionary<string, object>>();
            return null;
        }

        private static int GetInt(Dictionary<string, object> dict, string key, int def = 0)
        {
            if (!dict.TryGetValue(key, out var v) || v == null) return def;
            if (v is int i) return i;
            if (int.TryParse(v.ToString(), out var parsed)) return parsed;
            return def;
        }

        private static long GetLong(Dictionary<string, object> dict, string key, long def = 0)
        {
            if (!dict.TryGetValue(key, out var v) || v == null) return def;
            if (v is long l) return l;
            if (long.TryParse(v.ToString(), out var parsed)) return parsed;
            return def;
        }

        private static double GetDouble(Dictionary<string, object> dict, string key, double def = 0)
        {
            if (!dict.TryGetValue(key, out var v) || v == null) return def;
            if (v is double d) return d;
            if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            return def;
        }

        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.AssumeLocal, out var dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dtu)) return dtu;
            return null;
        }

        private static string? InferProcessFromName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var digits = new string(name.Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(digits) ? null : digits;
        }

        private static string? InferProcessFromMetadata(PDFAnalysisResult analysis)
        {
            if (!string.IsNullOrWhiteSpace(analysis?.Metadata?.Title)) return analysis.Metadata.Title;
            return null;
        }

        private static void InsertPageImages(NpgsqlConnection conn, NpgsqlTransaction tx, long pageId, PageDto page)
        {
            if (page.Images == null || page.Images.Count == 0) return;
            int idx = 0;
            foreach (var img in page.Images)
            {
                idx++;
                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO page_images(page_id, img_index, name, width, height, color_space, compression, bits_per_component, estimated_size)
                    VALUES (@p,@i,@n,@w,@h,@cs,@cp,@bpc,@est);
                ", conn, tx);
                cmd.Parameters.AddWithValue("@p", pageId);
                cmd.Parameters.AddWithValue("@i", idx);
                cmd.Parameters.AddWithValue("@n", img.Name ?? $"img{idx}");
                cmd.Parameters.AddWithValue("@w", img.Width);
                cmd.Parameters.AddWithValue("@h", img.Height);
                cmd.Parameters.AddWithValue("@cs", img.ColorSpace ?? "");
                cmd.Parameters.AddWithValue("@cp", img.CompressionType ?? "");
                cmd.Parameters.AddWithValue("@bpc", img.BitsPerComponent);
                cmd.Parameters.AddWithValue("@est", img.EstimatedSize);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertPageAnnotations(NpgsqlConnection conn, NpgsqlTransaction tx, long pageId, PageDto page)
        {
            if (page.Annotations == null || page.Annotations.Count == 0) return;
            foreach (var ann in page.Annotations)
            {
                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO page_annotations(page_id, type, author, subject, contents, modification_date, x, y)
                    VALUES (@p,@t,@a,@s,@c,@m,@x,@y);
                ", conn, tx);
                cmd.Parameters.AddWithValue("@p", pageId);
                cmd.Parameters.AddWithValue("@t", ann.Type ?? "");
                cmd.Parameters.AddWithValue("@a", ann.Author ?? "");
                cmd.Parameters.AddWithValue("@s", ann.Subject ?? "");
                cmd.Parameters.AddWithValue("@c", ann.Contents ?? "");
                cmd.Parameters.AddWithValue("@m", ann.ModificationDate.HasValue ? ann.ModificationDate.Value : (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@x", ann.X);
                cmd.Parameters.AddWithValue("@y", ann.Y);
                cmd.ExecuteNonQuery();
            }
        }

        private static void InsertPageFonts(NpgsqlConnection conn, NpgsqlTransaction tx, long pageId, PageDto page)
        {
            var fonts = page.FontsDetailed ?? new List<FontInfo>();
            if (fonts.Count == 0) return;
            foreach (var f in fonts)
            {
                using var cmd = new NpgsqlCommand(@"
                    INSERT INTO page_fonts(page_id, name, base_font, font_type, size, is_embedded, is_bold, is_italic, is_underline, is_strikeout, is_monospace, is_serif, is_sans)
                    VALUES (@p,@n,@b,@t,@s,@e,@bld,@it,@ul,@st,@mono,@ser,@sans);
                ", conn, tx);
                cmd.Parameters.AddWithValue("@p", pageId);
                cmd.Parameters.AddWithValue("@n", f.Name ?? "");
                cmd.Parameters.AddWithValue("@b", f.BaseFont ?? "");
                cmd.Parameters.AddWithValue("@t", f.FontType ?? "");
                cmd.Parameters.AddWithValue("@s", (double)f.Size);
                cmd.Parameters.AddWithValue("@e", f.IsEmbedded);
                cmd.Parameters.AddWithValue("@bld", f.IsBold);
                cmd.Parameters.AddWithValue("@it", f.IsItalic);
                cmd.Parameters.AddWithValue("@ul", f.IsUnderline);
                cmd.Parameters.AddWithValue("@st", f.IsStrikeout);
                cmd.Parameters.AddWithValue("@mono", f.IsMonospace);
                cmd.Parameters.AddWithValue("@ser", f.IsSerif);
                cmd.Parameters.AddWithValue("@sans", f.IsSansSerif);
                cmd.ExecuteNonQuery();
            }
        }

        private class BookmarkFlat
        {
            public string Title { get; set; } = "";
            public int StartPage { get; set; }
            public int EndPage { get; set; }
            public int Level { get; set; }
        }

        private class DocumentRecord
        {
            public string DocKey { get; set; } = "";
            public string RawLabel { get; set; } = "";
            public string Macro { get; set; } = "";
            public string Subcat { get; set; } = "";
            public int StartPage { get; set; }
            public int EndPage { get; set; }
            public List<PageDto> Pages { get; set; } = new();
            public Dictionary<string, object> Meta { get; set; } = new();
            public string HeaderOrigin { get; set; } = "";
            public string HeaderTitle { get; set; } = "";
            public string HeaderSubtitle { get; set; } = "";
            public List<string> FooterSigners { get; set; } = new();
            public DateTime? FooterSignedAt { get; set; }
            public string FooterSignatureRaw { get; set; } = "";
            public int TotalPages { get; set; }
            public int TotalWords { get; set; }
            public int TotalChars { get; set; }
            public int TotalImages { get; set; }
            public int TotalFonts { get; set; }
            public decimal ScanRatio { get; set; }
            public bool HasForms { get; set; }
            public bool HasAnnotations { get; set; }
        }

        private class PageDto
        {
            public int PageNumber { get; set; }
            public string Text { get; set; } = "";
            public string Header { get; set; } = "";
            public string Footer { get; set; } = "";
            public bool HasMoney { get; set; }
            public bool HasCpf { get; set; }
            public string Fonts { get; set; } = "";
            public int WordCount { get; set; }
            public int CharCount { get; set; }
            public bool IsScanned { get; set; }
            public int ImageCount { get; set; }
            public int AnnotationCount { get; set; }
            public bool HasForm { get; set; }
            public bool HasJs { get; set; }
            public int FontCount { get; set; }
            public List<string> FontList { get; set; } = new();
            public List<ImageInfo> Images { get; set; } = new();
            public List<Annotation> Annotations { get; set; } = new();
            public List<FontInfo> FontsDetailed { get; set; } = new();

            public static PageDto From(PageAnalysis p)
            {
                var text = p?.TextInfo?.PageText ?? "";
                var header = (p?.TextInfo?.Headers != null && p.TextInfo.Headers.Any())
                    ? string.Join("\n", p.TextInfo.Headers)
                    : TopLines(text, 5);
                var footer = (p?.TextInfo?.Footers != null && p.TextInfo.Footers.Any())
                    ? string.Join("\n", p.TextInfo.Footers)
                    : BottomLines(text, 5);
                var fonts = p?.TextInfo?.Fonts != null ? string.Join("|", p.TextInfo.Fonts.Select(f => f.Name ?? "")) : "";
                var fontList = p?.TextInfo?.Fonts != null ? p.TextInfo.Fonts.Select(f => f.Name ?? "").ToList() : new List<string>();
                var wordCount = p?.TextInfo?.WordCount ?? (text.Length == 0 ? 0 : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
                var charCount = p?.TextInfo?.CharacterCount ?? text.Length;
                var imgCount = p?.Resources?.Images?.Count ?? 0;
                var annCount = p?.Annotations?.Count ?? 0;
                var hasForm = p?.Resources?.HasForms ?? false;
                var isScanned = wordCount == 0 && imgCount > 0;
                var images = p?.Resources?.Images ?? new List<ImageInfo>();
                var ann = p?.Annotations ?? new List<Annotation>();
                var fontsDetailed = p?.TextInfo?.Fonts ?? new List<FontInfo>();
                return new PageDto
                {
                    PageNumber = p?.PageNumber ?? 0,
                    Text = text,
                    Header = header,
                    Footer = footer,
                    HasMoney = HasMoneyText(text),
                    HasCpf = HasCpfText(text),
                    Fonts = fonts,
                    FontList = fontList,
                    WordCount = wordCount,
                    CharCount = charCount,
                    ImageCount = imgCount,
                    AnnotationCount = annCount,
                    HasForm = hasForm,
                    HasJs = false,
                    IsScanned = isScanned,
                    FontCount = fontList.Count,
                    Images = images,
                    Annotations = ann,
                    FontsDetailed = fontsDetailed
                };
            }

            private static string TopLines(string text, int n)
            {
                return string.Join("\n", (text ?? "").Split('\n').Take(n));
            }

            private static string BottomLines(string text, int n)
            {
                var arr = (text ?? "").Split('\n');
                return string.Join("\n", arr.Skip(Math.Max(0, arr.Length - n)));
            }

            private static bool HasMoneyText(string text)
            {
                if (string.IsNullOrEmpty(text)) return false;
                return text.Contains("R$", StringComparison.OrdinalIgnoreCase) ||
                       text.IndexOf("honor", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private static bool HasCpfText(string text)
            {
                if (string.IsNullOrEmpty(text)) return false;
                return System.Text.RegularExpressions.Regex.IsMatch(text, @"\b\d{3}[\.-]?\d{3}[\.-]?\d{3}[\.-]?\d{2}\b");
            }
        }
    }
}
