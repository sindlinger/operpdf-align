using System;
using System.Collections.Generic;

namespace Obj.Models
{
    /// <summary>
    /// Resultado completo da análise do PDF
    /// </summary>
    public class PDFAnalysisResult
    {
        public string FilePath { get; set; } = "";
        public long FileSize { get; set; }
        public DateTime AnalysisDate { get; set; }
        public Metadata Metadata { get; set; } = new Metadata();
        public XMPMetadata XMPMetadata { get; set; } = new XMPMetadata();
        public DocumentInfo DocumentInfo { get; set; } = new DocumentInfo();
        public List<PageAnalysis> Pages { get; set; } = new List<PageAnalysis>();
        public SecurityInfo Security { get; set; } = new SecurityInfo();
        public ResourcesSummary Resources { get; set; } = new ResourcesSummary();
        public Statistics Statistics { get; set; } = new Statistics();
        public AccessibilityInfo Accessibility { get; set; } = new AccessibilityInfo();
        public List<OptionalContentGroup> Layers { get; set; } = new List<OptionalContentGroup>();
        public List<DigitalSignature> Signatures { get; set; } = new List<DigitalSignature>();
        public List<ColorProfile> ColorProfiles { get; set; } = new List<ColorProfile>();
        public BookmarkStructure Bookmarks { get; set; } = new BookmarkStructure();
        public List<BookmarkFlatItem> BookmarksFlat { get; set; } = new List<BookmarkFlatItem>();
        public PDFAInfo PDFACompliance { get; set; } = new PDFAInfo();
        public List<MultimediaInfo> Multimedia { get; set; } = new List<MultimediaInfo>();
        // public List<RichMediaAnalysis> RichMediaAnalysis { get; set; }
        // public SpatialAnalysis SpatialData { get; set; }
        public PDFAValidationResult PDFAValidation { get; set; } = new PDFAValidationResult();
        // public PDFACharacteristics PDFACharacteristics { get; set; }
        public SecurityInfo SecurityInfo { get; set; } = new SecurityInfo();
        public AccessibilityInfo AccessibilityInfo { get; set; } = new AccessibilityInfo();
        public ModificationDates ModificationDates { get; set; } = new ModificationDates();
    }
    
    /// <summary>
    /// Metadados do PDF
    /// </summary>
    public class Metadata
    {
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Keywords { get; set; } = "";
        public string Creator { get; set; } = "";
        public string Producer { get; set; } = "";
        public DateTime? CreationDate { get; set; }
        public DateTime? ModificationDate { get; set; }
        public string PDFVersion { get; set; } = "";
        public bool IsTagged { get; set; }
    }
    
    /// <summary>
    /// Informações gerais do documento
    /// </summary>
    public class DocumentInfo
    {
        public int TotalPages { get; set; }
        public bool IsEncrypted { get; set; }
        public bool IsLinearized { get; set; }
        public bool HasAcroForm { get; set; }
        public bool HasXFA { get; set; }
        public string FileStructure { get; set; } = "";
    }
    
    /// <summary>
    /// Análise detalhada de uma página
    /// </summary>
    public class PageAnalysis
    {
        public int PageNumber { get; set; }
        public PageSize Size { get; set; } = new PageSize();
        public int Rotation { get; set; }
        public TextInfo TextInfo { get; set; } = new TextInfo();
        public PageResources Resources { get; set; } = new PageResources();
        public List<Annotation> Annotations { get; set; } = new List<Annotation>();
        public List<string> Headers { get; set; } = new List<string>();
        public List<string> Footers { get; set; } = new List<string>();
        public List<string> DocumentReferences { get; set; } = new List<string>();
        public List<FontInfo> FontInfo { get; set; } = new List<FontInfo>();
        
        // File size information
        public long FileSizeBytes { get; set; }
        public double FileSizeMB => FileSizeBytes / (1024.0 * 1024.0);
        public double FileSizeKB => FileSizeBytes / 1024.0;
    }
    
    /// <summary>
    /// Tamanho da página em diferentes unidades
    /// </summary>
    public class PageSize
    {
        public float Width { get; set; }
        public float Height { get; set; }
        public float WidthPoints { get; set; }
        public float HeightPoints { get; set; }
        public float WidthInches { get; set; }
        public float HeightInches { get; set; }
        public float WidthMM { get; set; }
        public float HeightMM { get; set; }
        
        public string GetPaperSize()
        {
            // Detectar tamanhos padrão (em pontos)
            if (IsNear(WidthPoints, 612) && IsNear(HeightPoints, 792)) return "Letter";
            if (IsNear(WidthPoints, 595) && IsNear(HeightPoints, 842)) return "A4";
            if (IsNear(WidthPoints, 612) && IsNear(HeightPoints, 1008)) return "Legal";
            if (IsNear(WidthPoints, 420) && IsNear(HeightPoints, 595)) return "A5";
            if (IsNear(WidthPoints, 842) && IsNear(HeightPoints, 1191)) return "A3";
            return "Custom";
        }
        
        private bool IsNear(float val1, float val2, float tolerance = 2)
        {
            return Math.Abs(val1 - val2) <= tolerance;
        }
    }
    
    /// <summary>
    /// Informações sobre o texto da página
    /// </summary>
    public class TextInfo
    {
        public int CharacterCount { get; set; }
        public int WordCount { get; set; }
        public int LineCount { get; set; }
        public List<FontInfo> Fonts { get; set; } = new List<FontInfo>();
        public List<LineInfo> Lines { get; set; } = new List<LineInfo>();
        public List<WordInfo> Words { get; set; } = new List<WordInfo>();
        public List<CharacterInfo> Characters { get; set; } = new List<CharacterInfo>();
        public List<string> Languages { get; set; } = new List<string>();
        public bool HasTables { get; set; }
        public bool HasColumns { get; set; }
        public double AverageLineLength { get; set; }
        public List<string> Headers { get; set; } = new List<string>();
        public List<string> Footers { get; set; } = new List<string>();
        public List<DocumentReference> DocumentReferences { get; set; } = new List<DocumentReference>();
        public string PageText { get; set; } = ""; // Texto completo da página para busca em cache
        public string PageTextRaw { get; set; } = ""; // Texto tabulado (métrico) via words
        public bool PageTextRawReady { get; set; } = false; // true quando PageTextRaw foi gerado por métrica
    }
    
    /// <summary>
    /// Informações sobre fonte
    /// </summary>
    public class FontInfo
    {
        public string Name { get; set; } = "";
        public float Size { get; set; }
        public string Style { get; set; } = "";
        public bool IsEmbedded { get; set; }
        public string BaseFont { get; set; } = "";
        public string FontType { get; set; } = "";
        public List<float> FontSizes { get; set; } = new List<float>();
        
        // Estilos de fonte
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public bool IsUnderline { get; set; }
        public bool IsStrikeout { get; set; }
        public bool IsMonospace { get; set; }
        public bool IsSerif { get; set; }
        public bool IsSansSerif { get; set; }
    }
    
    /// <summary>
    /// Referência de documento detectada
    /// </summary>
    public class DocumentReference
    {
        public string Type { get; set; } = ""; // SEI, Processo, Ofício, Anexo, etc
        public string Value { get; set; } = ""; // O valor extraído
        public int PageNumber { get; set; }
        public string Context { get; set; } = ""; // Texto ao redor da referência
    }
    
    /// <summary>
    /// Recursos da página
    /// </summary>
    public class PageResources
    {
        public List<ImageInfo> Images { get; set; } = new List<ImageInfo>();
        public int FontCount { get; set; }
        public bool HasForms { get; set; }
        public bool HasMultimedia { get; set; }
        public List<FormField> FormFields { get; set; } = new List<FormField>();
    }
    
    /// <summary>
    /// Informações sobre imagem
    /// </summary>
    public class ImageInfo
    {
        public string Name { get; set; } = "";
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitsPerComponent { get; set; }
        public string ColorSpace { get; set; } = "";
        public string CompressionType { get; set; } = "";
        public string Base64Data { get; set; } = "";
        public long EstimatedSize => (Width * Height * BitsPerComponent) / 8;
    }
    
    /// <summary>
    /// Anotações e comentários
    /// </summary>
    public class Annotation
    {
        public string Type { get; set; } = "";
        public string Contents { get; set; } = "";
        public string Author { get; set; } = "";
        public string Subject { get; set; } = "";
        public DateTime? ModificationDate { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public string FileName { get; set; } = "";
        public long? FileSize { get; set; }
    }
    
    /// <summary>
    /// Informações de segurança
    /// </summary>
    public class SecurityInfo
    {
        public bool IsEncrypted { get; set; }
        public int PermissionFlags { get; set; }
        public int EncryptionType { get; set; }
        public bool CanPrint { get; set; }
        public bool CanModify { get; set; }
        public bool CanCopy { get; set; }
        public bool CanAnnotate { get; set; }
        public bool CanFillForms { get; set; }
        public bool CanExtractContent { get; set; }
        public bool CanAssemble { get; set; }
        public bool CanPrintHighQuality { get; set; }
        public string EncryptionLevel { get; set; } = "";
        
        public string GetEncryptionLevel()
        {
            switch (EncryptionType)
            {
                case 0: return "40-bit RC4";
                case 1: return "128-bit RC4";
                case 2: return "128-bit AES";
                case 3: return "256-bit AES";
                default: return "Unknown";
            }
        }
    }
    
    /// <summary>
    /// Resumo de recursos do documento
    /// </summary>
    public class ResourcesSummary
    {
        public int TotalImages { get; set; }
        public int TotalFonts { get; set; }
        public int Forms { get; set; }
        public bool HasJavaScript { get; set; }
        public int JavaScriptCount { get; set; }
        public bool HasAttachments { get; set; }
        public int AttachmentCount { get; set; }
        public bool HasMultimedia { get; set; }
        public List<string> EmbeddedFiles { get; set; } = new List<string>();
        public List<EmbeddedFileInfo> EmbeddedFileInfos { get; set; } = new List<EmbeddedFileInfo>();
    }

    public class EmbeddedFileInfo
    {
        public string Name { get; set; } = "";
        public long? Size { get; set; }
        public string Source { get; set; } = "EmbeddedFiles"; // EmbeddedFiles or FileAttachment
        public int? Page { get; set; } // se vier de anotação, página
    }
    
    /// <summary>
    /// Estatísticas gerais do documento
    /// </summary>
    public class Statistics
    {
        public int TotalCharacters { get; set; }
        public int TotalWords { get; set; }
        public int TotalLines { get; set; }
        public double AverageWordsPerPage { get; set; }
        public int TotalImages { get; set; }
        public int TotalAnnotations { get; set; }
        public int UniqueFonts { get; set; }
        public int PagesWithImages { get; set; }
        public int PagesWithTables { get; set; }
        public int PagesWithColumns { get; set; }
        public Dictionary<string, int> FontUsage { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> LanguageDistribution { get; set; } = new Dictionary<string, int>();
    }
    
    /// <summary>
    /// Metadados XMP avançados
    /// </summary>
    public class XMPMetadata
    {
        public string DublinCoreTitle { get; set; } = "";
        public string DublinCoreCreator { get; set; } = "";
        public string DublinCoreSubject { get; set; } = "";
        public string DublinCoreDescription { get; set; } = "";
        public List<string> DublinCoreKeywords { get; set; } = new List<string>();
        public string CopyrightNotice { get; set; } = "";
        public string CopyrightOwner { get; set; } = "";
        public DateTime? CopyrightDate { get; set; }
        public List<EditHistoryEntry> EditHistory { get; set; } = new List<EditHistoryEntry>();
        public string CreatorTool { get; set; } = "";
        public DateTime? MetadataDate { get; set; }
        public DateTime? CreateDate { get; set; }
        public DateTime? ModifyDate { get; set; }
        public string DocumentID { get; set; } = "";
        public string InstanceID { get; set; } = "";
        public string PDFAConformance { get; set; } = "";
        public string PDFAVersion { get; set; } = "";
        public Dictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();
    }
    
    /// <summary>
    /// Entrada do histórico de edição
    /// </summary>
    public class EditHistoryEntry
    {
        public string Action { get; set; } = "";
        public DateTime When { get; set; }
        public string SoftwareAgent { get; set; } = "";
        public string Parameters { get; set; } = "";
    }
    
    /// <summary>
    /// Informações de acessibilidade
    /// </summary>
    public class AccessibilityInfo
    {
        public bool HasStructureTags { get; set; }
        public bool HasReadingOrder { get; set; }
        public bool HasAlternativeText { get; set; }
        public List<StructureElement> StructureTree { get; set; } = new List<StructureElement>();
        public int HeadingLevels { get; set; }
        public int ListElements { get; set; }
        public int TableElements { get; set; }
        public int FigureElements { get; set; }
        public bool IsTaggedPDF { get; set; }
        public string Language { get; set; } = "";
        public Dictionary<string, string> CustomRoles { get; set; } = new Dictionary<string, string>();
        public bool HasParentTree { get; set; }
        public bool HasIDTree { get; set; }
        public bool DisplayDocTitle { get; set; }
        public int ImagesWithAltText { get; set; }
        public int TotalImages { get; set; }
    }
    
    /// <summary>
    /// Elemento da estrutura de tags
    /// </summary>
    public class StructureElement
    {
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string AlternativeText { get; set; } = "";
        public string ActualText { get; set; } = "";
        public int Level { get; set; }
        public List<StructureElement> Children { get; set; } = new List<StructureElement>();
    }
    
    /// <summary>
    /// Grupo de conteúdo opcional (camada)
    /// </summary>
    public class OptionalContentGroup
    {
        public string Name { get; set; } = "";
        public string Intent { get; set; } = "";
        public bool IsVisible { get; set; }
        public bool CanToggle { get; set; }
        public string Usage { get; set; } = "";
        public int Order { get; set; }
        public List<OptionalContentGroup> SubGroups { get; set; } = new List<OptionalContentGroup>();
    }

    public class ModificationDates
    {
        public DateTime? InfoCreationDate { get; set; }
        public DateTime? InfoModDate { get; set; }
        public DateTime? XmpCreateDate { get; set; }
        public DateTime? XmpModifyDate { get; set; }
        public List<DateTime?> SignatureDates { get; set; } = new List<DateTime?>();
        public List<DateTime?> AnnotationDates { get; set; } = new List<DateTime?>();
    }
    
    /// <summary>
    /// Assinatura digital
    /// </summary>
    public class DigitalSignature
    {
        public string Name { get; set; } = "";
        public string ContactInfo { get; set; } = "";
        public string Location { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime? SignDate { get; set; }
        public CertificateInfo Certificate { get; set; } = new CertificateInfo();
        public bool IsValid { get; set; }
        public string ValidationMessage { get; set; } = "";
        public bool HasTimestamp { get; set; }
        public DateTime? TimestampDate { get; set; }
        public string SignatureType { get; set; } = "";
        public string FieldName { get; set; } = "";
        public string Filter { get; set; } = "";
        public string SubFilter { get; set; } = "";
        public DateTime? SigningTime { get; set; }
        public string SignerName { get; set; } = "";
        public int Page1 { get; set; }
        public float? BBoxX0 { get; set; }
        public float? BBoxY0 { get; set; }
        public float? BBoxX1 { get; set; }
        public float? BBoxY1 { get; set; }
    }
    
    /// <summary>
    /// Informações do certificado
    /// </summary>
    public class CertificateInfo
    {
        public string Subject { get; set; } = "";
        public string Issuer { get; set; } = "";
        public string SerialNumber { get; set; } = "";
        public DateTime ValidFrom { get; set; }
        public DateTime ValidTo { get; set; }
        public string Algorithm { get; set; } = "";
        public int KeySize { get; set; }
        public string Fingerprint { get; set; } = "";
    }
    
    /// <summary>
    /// Perfil de cor
    /// </summary>
    public class ColorProfile
    {
        public string Name { get; set; } = "";
        public string ColorSpace { get; set; } = "";
        public string Description { get; set; } = "";
        public string Copyright { get; set; } = "";
        public string DeviceModel { get; set; } = "";
        public string DeviceManufacturer { get; set; } = "";
        public string RenderingIntent { get; set; } = "";
        public int ProfileSize { get; set; }
        public string ProfileVersion { get; set; } = "";
        public string Type { get; set; } = "";
        public int Components { get; set; }
        public int NumberOfComponents { get; set; }
        public string AlternateColorSpace { get; set; } = "";
        public string Info { get; set; } = "";
        public string RegistryName { get; set; } = "";
        public string Condition { get; set; } = "";
        public bool HasEmbeddedProfile { get; set; }
    }
    
    /// <summary>
    /// Estrutura de bookmarks
    /// </summary>
    public class BookmarkStructure
    {
        public List<BookmarkItem> RootItems { get; set; } = new List<BookmarkItem>();
        public int TotalCount { get; set; }
        public int MaxDepth { get; set; }
    }

    /// <summary>
    /// Bookmark flat (título + página), para JSON hall
    /// </summary>
    public class BookmarkFlatItem
    {
        public string Title { get; set; } = "";
        public int PageNumber { get; set; }
        public int Level { get; set; }
        public bool HasChildren { get; set; }
        public int ChildrenCount { get; set; }
    }
    
    /// <summary>
    /// Item de bookmark
    /// </summary>
    public class BookmarkItem
    {
        public string Title { get; set; } = "";
        public bool IsOpen { get; set; }
        public BookmarkDestination Destination { get; set; } = new BookmarkDestination();
        public BookmarkAction Action { get; set; } = new BookmarkAction();
        public List<BookmarkItem> Children { get; set; } = new List<BookmarkItem>();
        public int Level { get; set; }
    }
    
    /// <summary>
    /// Destino de bookmark
    /// </summary>
    public class BookmarkDestination
    {
        public int PageNumber { get; set; }
        public string Type { get; set; } = "";
        public float Left { get; set; }
        public float Top { get; set; }
        public float Zoom { get; set; }
    }
    
    /// <summary>
    /// Ação de bookmark
    /// </summary>
    public class BookmarkAction
    {
        public string Type { get; set; } = "";
        public string URI { get; set; } = "";
        public string FileSpec { get; set; } = "";
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }
    
    /// <summary>
    /// Campo de formulário
    /// </summary>
    public class FormField
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public string Value { get; set; } = "";
        public string DefaultValue { get; set; } = "";
        public bool IsRequired { get; set; }
        public bool IsReadOnly { get; set; }
        public List<string> Options { get; set; } = new List<string>();
        public FormValidation Validation { get; set; } = new FormValidation();
        public FormCalculation Calculation { get; set; } = new FormCalculation();
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
    
    /// <summary>
    /// Validação de formulário
    /// </summary>
    public class FormValidation
    {
        public string Type { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string ErrorMessage { get; set; } = "";
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public int? MinLength { get; set; }
        public int? MaxLength { get; set; }
    }
    
    /// <summary>
    /// Cálculo de formulário
    /// </summary>
    public class FormCalculation
    {
        public string Formula { get; set; } = "";
        public List<string> Dependencies { get; set; } = new List<string>();
        public string CalculationType { get; set; } = "";
    }
    
    /// <summary>
    /// Informações de multimídia do PDF
    /// </summary>
    public class MultimediaInfo
    {
        public string Type { get; set; } = "";
        public int PageNumber { get; set; }
        public List<string> Assets { get; set; } = new List<string>();
        public int ConfigurationCount { get; set; }
        public string ActionType { get; set; } = "";
        public bool HasRendition { get; set; }
    }
    
    /// <summary>
    /// Informações de conformidade PDF/A
    /// </summary>
    public class PDFAInfo
    {
        public bool IsPDFA { get; set; }
        public string ConformanceLevel { get; set; } = "";
        public bool HasOutputIntent { get; set; }
        public string OutputIntentInfo { get; set; } = "";
        public bool HasICCProfile { get; set; }
        public bool HasEmbeddedFonts { get; set; }
        public bool HasTransparency { get; set; }
        public bool HasJavaScript { get; set; }
        public bool HasEncryption { get; set; }
    }
    
    /// <summary>
    /// Classes para os novos subcomandos de filter
    /// </summary>
    public class FontDetails
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsEmbedded { get; set; }
        public List<float> SizesUsed { get; set; } = new List<float>();
        public int UsageCount { get; set; }
        public List<int> PagesUsed { get; set; } = new List<int>();
        public bool HasBold { get; set; }
        public bool HasItalic { get; set; }
    }
    
    public class FontMatch
    {
        public FontDetails FontDetails { get; set; } = new FontDetails();
        public List<string> MatchReasons { get; set; } = new List<string>();
    }
    
    public class DublinCoreData
    {
        public string Title { get; set; } = "";
        public string Creator { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Description { get; set; } = "";
        public List<string> Keywords { get; set; } = new List<string>();
    }
    
    public class CopyrightData
    {
        public string Notice { get; set; } = "";
        public string Owner { get; set; } = "";
        public DateTime? Date { get; set; }
    }
    
    public class MetadataMatch
    {
        public XMPMetadata XMPMetadata { get; set; } = new XMPMetadata();
        public DublinCoreData DublinCore { get; set; } = new DublinCoreData();
        public List<EditHistoryEntry> EditHistory { get; set; } = new List<EditHistoryEntry>();
        public CopyrightData CopyrightInfo { get; set; } = new CopyrightData();
        public Metadata Metadata { get; set; } = new Metadata();
        public Dictionary<string, string> CustomProperties { get; set; } = new Dictionary<string, string>();
    }
    
    public class StructureMatch
    {
        public PDFAInfo PDFACompliance { get; set; } = new PDFAInfo();
        public PDFAValidationResult PDFAValidation { get; set; } = new PDFAValidationResult();
        // public PDFACharacteristics PDFACharacteristics { get; set; }
        public AccessibilityInfo AccessibilityInfo { get; set; } = new AccessibilityInfo();
        public List<OptionalContentGroup> Layers { get; set; } = new List<OptionalContentGroup>();
        public List<ColorProfile> ColorProfiles { get; set; } = new List<ColorProfile>();
        public SecurityInfo SecurityInfo { get; set; } = new SecurityInfo();
    }
    
    public class ModificationMatch
    {
        public ModificationArea Modification { get; set; } = new ModificationArea();
        public PageAnalysis PageInfo { get; set; } = new PageAnalysis();
    }
    
    /// <summary>
    /// Classes Match para resultados de filtro
    /// </summary>
    public class PageMatch
    {
        public int PageNumber { get; set; }
        public PageAnalysis PageInfo { get; set; } = new PageAnalysis();
        public List<string> MatchReasons { get; set; } = new List<string>();
        public List<ObjectMatch> RelatedObjects { get; set; } = new List<ObjectMatch>();
    }
    
    public class BookmarkMatch
    {
        public BookmarkItem Bookmark { get; set; } = new BookmarkItem();
        public List<string> MatchReasons { get; set; } = new List<string>();
        public List<ObjectMatch> RelatedObjects { get; set; } = new List<ObjectMatch>();
    }
    
    public class WordMatch
    {
        public string Word { get; set; } = "";
        public int PageNumber { get; set; }
        public string Context { get; set; } = "";
        public List<string> MatchReasons { get; set; } = new List<string>();
        public List<ObjectMatch> RelatedObjects { get; set; } = new List<ObjectMatch>();
    }
    
    public class AnnotationMatch
    {
        public int PageNumber { get; set; }
        public Annotation Annotation { get; set; } = new Annotation();
        public List<string> MatchReasons { get; set; } = new List<string>();
        public List<ObjectMatch> RelatedObjects { get; set; } = new List<ObjectMatch>();
    }
    
    public class ObjectMatch
    {
        public int ObjectNumber { get; set; }
        public int Generation { get; set; }
        public string ObjectType { get; set; } = "";
        public object? PdfObject { get; set; } = null;
        public List<string> MatchReasons { get; set; } = new List<string>();
        public long StreamLength { get; set; }
        public List<string> DictionaryKeys { get; set; } = new List<string>();
        public int IndirectReferencesCount { get; set; }
        public List<object> DetailedPages { get; set; } = new List<object>();
    }
    
    /// <summary>
    /// Extensão para WordMatch com características únicas
    /// </summary>
    public class WordDetails : WordMatch
    {
        public string FontName { get; set; } = "";
        public float FontSize { get; set; }
        public string FontStyle { get; set; } = "";
        public string TextColor { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }

    /// <summary>
    /// Palavra com fonte/estilo e bounding box (iText7).
    /// </summary>
    public class WordInfo
    {
        public string Text { get; set; } = string.Empty;
        public string Font { get; set; } = string.Empty;
        public float Size { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public int RenderMode { get; set; }
        public float CharSpacing { get; set; }
        public float WordSpacing { get; set; }
        public float HorizontalScaling { get; set; }
        public float Rise { get; set; }
        public float X0 { get; set; }
        public float Y0 { get; set; }
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float NormX0 { get; set; }
        public float NormY0 { get; set; }
        public float NormX1 { get; set; }
        public float NormY1 { get; set; }
    }

    /// <summary>
    /// Caractere (glifo) com fonte/estilo e bounding box.
    /// </summary>
    public class CharacterInfo
    {
        public int Index { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Font { get; set; } = string.Empty;
        public float Size { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public int RenderMode { get; set; }
        public float CharSpacing { get; set; }
        public float WordSpacing { get; set; }
        public float HorizontalScaling { get; set; }
        public float Rise { get; set; }
        public float X0 { get; set; }
        public float Y0 { get; set; }
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float BaseX0 { get; set; }
        public float BaseY0 { get; set; }
        public float BaseX1 { get; set; }
        public float BaseY1 { get; set; }
        public float NormX0 { get; set; }
        public float NormY0 { get; set; }
        public float NormX1 { get; set; }
        public float NormY1 { get; set; }
    }

    /// <summary>
    /// Texto por linha com fonte/estilo e coordenadas básicas.
    /// </summary>
    public class LineInfo
    {
        public string Text { get; set; } = string.Empty;
        public string Font { get; set; } = string.Empty;
        public float Size { get; set; }
        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }
        public int RenderMode { get; set; }
        public float CharSpacing { get; set; }
        public float WordSpacing { get; set; }
        public float HorizontalScaling { get; set; }
        public float Rise { get; set; }
        public string LineHash { get; set; } = string.Empty;
        public float X0 { get; set; }
        public float Y0 { get; set; }
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float NormX0 { get; set; }
        public float NormY0 { get; set; }
        public float NormX1 { get; set; }
        public float NormY1 { get; set; }
    }
}
