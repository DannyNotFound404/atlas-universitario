using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Miguel.Server.Governance;

public sealed class GovernancePreviewException(string code, string message)
    : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record GovernancePreviewDocument(
    string Kind,
    string OriginalFileName,
    bool Truncated,
    string Notice,
    IReadOnlyList<GovernancePreviewBlock> Blocks,
    IReadOnlyList<GovernancePreviewSheet> Sheets);

public sealed record GovernancePreviewBlock(
    string Type,
    int? Level,
    string? Text,
    IReadOnlyList<IReadOnlyList<string>>? Rows);

public sealed record GovernancePreviewSheet(
    string Name,
    IReadOnlyList<GovernancePreviewRow> Rows,
    bool Truncated);

public sealed record GovernancePreviewRow(
    int Number,
    IReadOnlyList<string> Cells);

public sealed class GovernancePreviewService(
    GovernanceDocumentService documents)
{
    public async Task<GovernancePreviewDocument?> BuildAsync(
        Guid versionId,
        CancellationToken cancellationToken)
    {
        GovernanceStoredFile? file =
            await documents.ResolveVersionFileAsync(versionId, cancellationToken);
        if (file is null)
        {
            return null;
        }

        string extension = Path.GetExtension(file.OriginalFileName)
            .ToLowerInvariant();
        if (extension == ".pdf")
        {
            return new GovernancePreviewDocument(
                "pdf",
                file.OriginalFileName,
                false,
                "El visor del navegador presenta el PDF original. ATLAS no permite editarlo desde esta vista.",
                [],
                []);
        }
        if (extension is not ".docx" and not ".xlsx")
        {
            throw new GovernancePreviewException(
                "unsupported_governance_preview",
                "La vista integrada está disponible para PDF, Word y Excel.");
        }

        byte[] content = await File.ReadAllBytesAsync(
            file.PhysicalPath, cancellationToken);
        return GovernancePreviewParser.Parse(content, file.OriginalFileName);
    }
}

public static partial class GovernancePreviewParser
{
    private const int MaximumArchiveEntries = 2_000;
    private const long MaximumExpandedBytes = 80_000_000;
    private const long MaximumXmlCharacters = 16_000_000;
    private const int MaximumWordBlocks = 1_500;
    private const int MaximumWordTableRows = 500;
    private const int MaximumWordTableColumns = 30;
    private const int MaximumSheets = 20;
    private const int MaximumSheetRows = 1_000;
    private const int MaximumSheetColumns = 80;
    private const int MaximumCellCharacters = 2_000;

    public static GovernancePreviewDocument Parse(
        byte[] content,
        string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        string safeName = Path.GetFileName(fileName ?? string.Empty);
        if (safeName.Length == 0 || safeName != fileName ||
            safeName.Any(char.IsControl))
        {
            throw new GovernancePreviewException(
                "invalid_governance_preview_file",
                "El nombre del documento no es válido.");
        }

        try
        {
            using MemoryStream stream = new(content, writable: false);
            using ZipArchive archive = new(
                stream, ZipArchiveMode.Read, leaveOpen: false);
            ValidateArchive(archive);
            return Path.GetExtension(safeName).ToLowerInvariant() switch
            {
                ".docx" => ReadWordDocument(archive, safeName),
                ".xlsx" => ReadWorkbook(archive, safeName),
                _ => throw new GovernancePreviewException(
                    "unsupported_governance_preview",
                    "El formato no admite una vista estructurada segura.")
            };
        }
        catch (GovernancePreviewException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or XmlException or
                FormatException or OverflowException or ArgumentException)
        {
            throw new GovernancePreviewException(
                "invalid_governance_preview_file",
                "ATLAS no pudo interpretar el documento de forma segura.");
        }
    }

    private static GovernancePreviewDocument ReadWordDocument(
        ZipArchive archive,
        string fileName)
    {
        XDocument document = LoadXml(GetRequiredEntry(
            archive, "word/document.xml"));
        XNamespace word =
            "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XElement? body = document.Root?.Element(word + "body");
        if (body is null)
        {
            throw new GovernancePreviewException(
                "invalid_governance_preview_file",
                "El documento de Word no contiene un cuerpo legible.");
        }

        List<GovernancePreviewBlock> blocks = [];
        bool truncated = false;
        foreach (XElement element in body.Elements())
        {
            if (blocks.Count >= MaximumWordBlocks)
            {
                truncated = true;
                break;
            }

            if (element.Name == word + "p")
            {
                string text = WordText(element, word);
                if (text.Length == 0)
                {
                    continue;
                }
                int? level = HeadingLevel(element, word);
                blocks.Add(new GovernancePreviewBlock(
                    level.HasValue ? "heading" : "paragraph",
                    level,
                    LimitText(text),
                    null));
                continue;
            }

            if (element.Name == word + "tbl")
            {
                List<IReadOnlyList<string>> rows = [];
                foreach (XElement row in element.Elements(word + "tr"))
                {
                    if (rows.Count >= MaximumWordTableRows)
                    {
                        truncated = true;
                        break;
                    }
                    rows.Add(row.Elements(word + "tc")
                        .Take(MaximumWordTableColumns)
                        .Select(cell => LimitText(WordText(cell, word)))
                        .ToArray());
                    if (row.Elements(word + "tc").Skip(MaximumWordTableColumns).Any())
                    {
                        truncated = true;
                    }
                }
                if (rows.Count > 0)
                {
                    blocks.Add(new GovernancePreviewBlock(
                        "table", null, null, rows));
                }
            }
        }

        bool hasImages = archive.Entries.Any(entry =>
            entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase));
        List<string> notices =
        [
            "Vista segura de sólo lectura; el archivo original permanece intacto."
        ];
        if (hasImages)
        {
            notices.Add(
                "Las imágenes y la maquetación avanzada no se reproducen en esta vista estructurada.");
        }
        if (truncated)
        {
            notices.Add(
                "El documento supera el límite de visualización; descargue el original para consultarlo completo.");
        }
        return new GovernancePreviewDocument(
            "word", fileName, truncated, string.Join(' ', notices), blocks, []);
    }

    private static GovernancePreviewDocument ReadWorkbook(
        ZipArchive archive,
        string fileName)
    {
        XDocument workbook = LoadXml(GetRequiredEntry(
            archive, "xl/workbook.xml"));
        XDocument relationshipDocument = LoadXml(GetRequiredEntry(
            archive, "xl/_rels/workbook.xml.rels"));
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace officeRelationships =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace packageRelationships =
            "http://schemas.openxmlformats.org/package/2006/relationships";

        Dictionary<string, string> relationships = relationshipDocument
            .Descendants(packageRelationships + "Relationship")
            .Where(item => !string.Equals(
                item.Attribute("TargetMode")?.Value,
                "External",
                StringComparison.OrdinalIgnoreCase))
            .Where(item => item.Attribute("Id") is not null &&
                           item.Attribute("Target") is not null)
            .ToDictionary(
                item => item.Attribute("Id")!.Value,
                item => NormalizeArchivePath(
                    "xl", item.Attribute("Target")!.Value),
                StringComparer.Ordinal);
        IReadOnlyList<string> sharedStrings = ReadSharedStrings(archive);
        IReadOnlySet<int> dateStyles = ReadDateStyles(archive);
        bool date1904 = string.Equals(
            workbook.Descendants(spreadsheet + "workbookPr")
                .FirstOrDefault()?.Attribute("date1904")?.Value,
            "1",
            StringComparison.Ordinal);

        List<GovernancePreviewSheet> sheets = [];
        bool truncated = false;
        List<XElement> sheetDefinitions = workbook
            .Descendants(spreadsheet + "sheet")
            .ToList();
        foreach (XElement definition in sheetDefinitions.Take(MaximumSheets))
        {
            string name = LimitText(
                definition.Attribute("name")?.Value ?? $"Hoja {sheets.Count + 1}");
            string? relationshipId =
                definition.Attribute(officeRelationships + "id")?.Value;
            if (relationshipId is null ||
                !relationships.TryGetValue(relationshipId, out string? worksheetPath))
            {
                continue;
            }
            ZipArchiveEntry? worksheetEntry = archive.GetEntry(worksheetPath);
            if (worksheetEntry is null)
            {
                continue;
            }

            XDocument worksheet = LoadXml(worksheetEntry);
            List<GovernancePreviewRow> rows = [];
            bool sheetTruncated = false;
            IEnumerable<XElement> rowElements = worksheet
                .Descendants(spreadsheet + "row");
            foreach (XElement rowElement in rowElements.Take(MaximumSheetRows))
            {
                int rowNumber = int.TryParse(
                    rowElement.Attribute("r")?.Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int parsedRow)
                    ? parsedRow
                    : rows.Count + 1;
                SortedDictionary<int, string> cells = [];
                int nextColumn = 0;
                foreach (XElement cell in rowElement.Elements(spreadsheet + "c"))
                {
                    string reference = cell.Attribute("r")?.Value ?? string.Empty;
                    int column = reference.Length > 0
                        ? ColumnIndex(reference)
                        : nextColumn;
                    nextColumn = column + 1;
                    if (column >= MaximumSheetColumns)
                    {
                        sheetTruncated = true;
                        continue;
                    }
                    cells[column] = LimitText(ReadCell(
                        cell, spreadsheet, sharedStrings, dateStyles, date1904));
                }
                if (cells.Count == 0)
                {
                    continue;
                }
                string[] values = Enumerable
                    .Range(0, cells.Keys.Max() + 1)
                    .Select(index => cells.GetValueOrDefault(index, string.Empty))
                    .ToArray();
                rows.Add(new GovernancePreviewRow(rowNumber, values));
            }
            if (rowElements.Skip(MaximumSheetRows).Any())
            {
                sheetTruncated = true;
            }
            truncated |= sheetTruncated;
            sheets.Add(new GovernancePreviewSheet(name, rows, sheetTruncated));
        }
        if (sheetDefinitions.Count > MaximumSheets)
        {
            truncated = true;
        }
        if (sheets.Count == 0)
        {
            throw new GovernancePreviewException(
                "invalid_governance_preview_file",
                "El libro no contiene hojas que ATLAS pueda mostrar.");
        }

        string notice =
            "Vista segura de sólo lectura. ATLAS muestra los valores guardados; no ejecuta fórmulas, macros ni vínculos externos.";
        if (truncated)
        {
            notice +=
                " El libro supera el límite de visualización; descargue el original para consultarlo completo.";
        }
        return new GovernancePreviewDocument(
            "spreadsheet", fileName, truncated, notice, [], sheets);
    }

    private static string WordText(XElement element, XNamespace word)
    {
        List<string> parts = [];
        foreach (XElement descendant in element.Descendants())
        {
            if (descendant.Name == word + "t")
            {
                parts.Add(descendant.Value);
            }
            else if (descendant.Name == word + "tab")
            {
                parts.Add("\t");
            }
            else if (descendant.Name == word + "br" ||
                     descendant.Name == word + "cr")
            {
                parts.Add("\n");
            }
        }
        return string.Concat(parts).Trim();
    }

    private static int? HeadingLevel(XElement paragraph, XNamespace word)
    {
        string style = paragraph.Element(word + "pPr")?
            .Element(word + "pStyle")?
            .Attribute(word + "val")?.Value ?? string.Empty;
        Match match = HeadingStyleRegex().Match(style);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int level))
        {
            return Math.Clamp(level, 1, 6);
        }
        string? outline = paragraph.Element(word + "pPr")?
            .Element(word + "outlineLvl")?
            .Attribute(word + "val")?.Value;
        return int.TryParse(outline, out int outlineLevel)
            ? Math.Clamp(outlineLevel + 1, 1, 6)
            : null;
    }

    private static string ReadCell(
        XElement cell,
        XNamespace spreadsheet,
        IReadOnlyList<string> sharedStrings,
        IReadOnlySet<int> dateStyles,
        bool date1904)
    {
        string type = cell.Attribute("t")?.Value ?? string.Empty;
        string raw = cell.Element(spreadsheet + "v")?.Value ?? string.Empty;
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(spreadsheet + "t")
                .Select(item => item.Value));
        }
        if (type == "s" && int.TryParse(raw, out int sharedIndex) &&
            sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedIndex];
        }
        if (type == "b")
        {
            return raw == "1" ? "VERDADERO" : "FALSO";
        }
        if (type is "str" or "d" or "e")
        {
            return raw;
        }

        int styleIndex = int.TryParse(cell.Attribute("s")?.Value, out int style)
            ? style
            : -1;
        if (dateStyles.Contains(styleIndex) && double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double serial))
        {
            DateTime date = DateTime.FromOADate(serial)
                .AddDays(date1904 ? 1462 : 0);
            return date.TimeOfDay == TimeSpan.Zero
                ? date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)
                : date.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        }
        return raw;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }
        XDocument document = LoadXml(entry);
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(spreadsheet + "si")
            .Select(item => LimitText(string.Concat(
                item.Descendants(spreadsheet + "t").Select(text => text.Value))))
            .ToArray();
    }

    private static IReadOnlySet<int> ReadDateStyles(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/styles.xml");
        if (entry is null)
        {
            return new HashSet<int>();
        }
        XDocument document = LoadXml(entry);
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Dictionary<uint, string> customFormats = document
            .Descendants(spreadsheet + "numFmt")
            .Where(item => item.Attribute("numFmtId") is not null)
            .ToDictionary(
                item => uint.Parse(
                    item.Attribute("numFmtId")!.Value,
                    CultureInfo.InvariantCulture),
                item => item.Attribute("formatCode")?.Value ?? string.Empty);
        HashSet<uint> builtIn =
        [
            14, 15, 16, 17, 18, 19, 20, 21, 22, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 45, 46, 47, 50, 51, 52, 53, 54, 55, 56, 57, 58
        ];
        HashSet<int> result = [];
        XElement? cellFormats = document.Descendants(spreadsheet + "cellXfs")
            .FirstOrDefault();
        if (cellFormats is null)
        {
            return result;
        }
        int index = 0;
        foreach (XElement format in cellFormats.Elements(spreadsheet + "xf"))
        {
            uint id = uint.TryParse(
                format.Attribute("numFmtId")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint parsed)
                ? parsed
                : 0;
            if (builtIn.Contains(id) ||
                (customFormats.TryGetValue(id, out string? code) &&
                 LooksLikeDateFormat(code)))
            {
                result.Add(index);
            }
            index++;
        }
        return result;
    }

    private static bool LooksLikeDateFormat(string format)
    {
        string value = QuotedFormatPartRegex().Replace(format, string.Empty)
            .Replace("\\", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return value.Contains('y') || value.Contains('d') ||
               value.Contains('h') || value.Contains('s');
    }

    private static int ColumnIndex(string reference)
    {
        int result = 0;
        foreach (char character in reference)
        {
            if (!char.IsAsciiLetter(character))
            {
                break;
            }
            result = checked(
                result * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return Math.Max(0, result - 1);
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > MaximumArchiveEntries ||
            archive.Entries.Sum(entry => Math.Max(0, entry.Length)) >
                MaximumExpandedBytes)
        {
            throw new GovernancePreviewException(
                "unsafe_governance_preview_archive",
                "El documento excede los límites seguros de visualización.");
        }
        if (archive.Entries.Any(entry =>
                entry.FullName.Contains("..", StringComparison.Ordinal) ||
                entry.FullName.StartsWith('/') ||
                entry.FullName.StartsWith('\\')))
        {
            throw new GovernancePreviewException(
                "unsafe_governance_preview_archive",
                "El documento contiene rutas internas no permitidas.");
        }
    }

    private static ZipArchiveEntry GetRequiredEntry(
        ZipArchive archive,
        string path) =>
        archive.GetEntry(path) ?? throw new GovernancePreviewException(
            "invalid_governance_preview_file",
            $"El documento no contiene {path}.");

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumExpandedBytes)
        {
            throw new GovernancePreviewException(
                "unsafe_governance_preview_archive",
                "Una parte del documento excede el límite seguro.");
        }
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumXmlCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true
        };
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string NormalizeArchivePath(string basePath, string target)
    {
        Uri root = new($"http://atlas.local/{basePath.TrimEnd('/')}/");
        Uri resolved = new(root, target);
        if (!string.Equals(resolved.Host, "atlas.local", StringComparison.Ordinal))
        {
            throw new GovernancePreviewException(
                "unsafe_governance_preview_archive",
                "El documento contiene un vínculo externo no permitido.");
        }
        string path = Uri.UnescapeDataString(resolved.AbsolutePath).TrimStart('/');
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new GovernancePreviewException(
                "unsafe_governance_preview_archive",
                "El documento contiene una relación interna insegura.");
        }
        return path;
    }

    private static string LimitText(string value) =>
        value.Length <= MaximumCellCharacters
            ? value
            : value[..MaximumCellCharacters] + "…";

    [GeneratedRegex(@"(?:heading|title|t[ií]tulo)[^0-9]*([1-6])", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyleRegex();

    [GeneratedRegex("\"[^\"]*\"")]
    private static partial Regex QuotedFormatPartRegex();
}
