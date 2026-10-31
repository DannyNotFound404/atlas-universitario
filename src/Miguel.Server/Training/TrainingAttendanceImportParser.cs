using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Miguel.Server.Training;

public sealed class TrainingAttendanceImportException(
    string code,
    string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static partial class TrainingAttendanceImportParser
{
    private const int MaximumRows = 5000;
    private const int MaximumArchiveEntries = 200;
    private const long MaximumExpandedBytes = 40_000_000;
    private static readonly CultureInfo GuatemalaCulture =
        CultureInfo.GetCultureInfo("es-GT");
    private static readonly CultureInfo SpanishCulture =
        CultureInfo.GetCultureInfo("es-ES");

    private static readonly IReadOnlyDictionary<string, string[]> HeaderAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["timestamp"] =
            [
                "marcatemporal", "fechayhora", "fechahora", "timestamp",
                "horaderegistro", "fechaderegistro"
            ],
            ["name"] =
            [
                "nombrecompleto", "nombresyapellidos", "nombreyapellido",
                "fullname", "nombre"
            ],
            ["agency"] =
            [
                "agencia", "sucursal", "oficina", "ubicacion",
                "sededetrabajo"
            ],
            ["department"] =
            [
                "departamento", "area", "unidad", "departamentoarea",
                "areadetrabajo"
            ],
            ["email"] =
            [
                "direcciondecorreoelectronico", "correoelectronico", "email",
                "correo"
            ]
        };

    public static async Task<TrainingAttendanceImportDocument> ParseAsync(
        Stream source,
        string fileName,
        string? mediaType,
        long maximumBytes,
        TimeZoneInfo operationalTimeZone,
        CancellationToken cancellationToken)
    {
        string safeName = Path.GetFileName(fileName ?? string.Empty);
        if (safeName.Length == 0 || safeName != fileName || safeName.Length > 255 ||
            safeName.IndexOfAny(['/', '\\']) >= 0 ||
            safeName.Any(char.IsControl))
        {
            throw new TrainingAttendanceImportException(
                "invalid_attendance_file_name",
                "El nombre del archivo de asistencia no es válido.");
        }

        string extension = Path.GetExtension(safeName).ToLowerInvariant();
        if (extension is not ".xlsx" and not ".csv")
        {
            throw new TrainingAttendanceImportException(
                "unsupported_attendance_file",
                "Utilice un archivo .xlsx o .csv exportado desde Google Forms.");
        }

        long effectiveLimit = maximumBytes is >= 100_000 and <= 10_000_000
            ? maximumBytes
            : 10_000_000;
        byte[] content = await ReadLimitedAsync(
            source, effectiveLimit, cancellationToken);
        if (content.Length == 0)
        {
            throw new TrainingAttendanceImportException(
                "empty_attendance_file",
                "El archivo de asistencia está vacío.");
        }

        IReadOnlyList<IReadOnlyList<ImportCell>> table = extension == ".xlsx"
            ? ReadXlsx(content)
            : ReadCsv(content);
        ParsedRows parsed = ParseRows(table, operationalTimeZone);
        string sha256 = Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();
        string normalizedMediaType = extension == ".xlsx"
            ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : "text/csv";

        return new TrainingAttendanceImportDocument(
            safeName,
            normalizedMediaType,
            extension,
            content,
            sha256,
            parsed.SourceRowCount,
            parsed.DuplicateRowCount,
            parsed.RejectedRowCount,
            parsed.HeaderMapping,
            parsed.Warnings,
            parsed.Rows);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream destination = new();
        byte[] buffer = new byte[81920];
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (destination.Length + read > maximumBytes)
            {
                throw new TrainingAttendanceImportException(
                    "attendance_file_too_large",
                    $"El archivo supera el límite de {maximumBytes / 1_000_000} MB.");
            }
            await destination.WriteAsync(
                buffer.AsMemory(0, read), cancellationToken);
        }
        return destination.ToArray();
    }

    private static IReadOnlyList<IReadOnlyList<ImportCell>> ReadCsv(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content)
                .TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException exception)
        {
            throw new TrainingAttendanceImportException(
                "invalid_csv_encoding",
                "El CSV debe estar codificado como UTF-8.")
            {
                Source = exception.Source
            };
        }

        char delimiter = ChooseDelimiter(text);
        List<List<string>> rows = ParseDelimited(text, delimiter);
        return rows
            .Take(MaximumRows + 2)
            .Select(row => (IReadOnlyList<ImportCell>)row
                .Select(value => new ImportCell(value, null))
                .ToArray())
            .ToArray();
    }

    private static char ChooseDelimiter(string text)
    {
        string firstLine = text.Split(['\r', '\n'], 2)[0];
        int commas = firstLine.Count(character => character == ',');
        int semicolons = firstLine.Count(character => character == ';');
        int tabs = firstLine.Count(character => character == '\t');
        return tabs > commas && tabs > semicolons
            ? '\t'
            : semicolons > commas ? ';' : ',';
    }

    private static List<List<string>> ParseDelimited(string text, char delimiter)
    {
        List<List<string>> rows = [];
        List<string> row = [];
        StringBuilder value = new();
        bool quoted = false;

        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        value.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    value.Append(character);
                }
                continue;
            }

            if (character == '"' && value.Length == 0)
            {
                quoted = true;
            }
            else if (character == delimiter)
            {
                row.Add(value.ToString());
                value.Clear();
            }
            else if (character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length &&
                    text[index + 1] == '\n')
                {
                    index++;
                }
                row.Add(value.ToString());
                value.Clear();
                rows.Add(row);
                if (rows.Count > MaximumRows + 1) break;
                row = [];
            }
            else
            {
                value.Append(character);
            }
        }

        if (quoted)
        {
            throw new TrainingAttendanceImportException(
                "invalid_csv_format",
                "El CSV contiene un campo entre comillas sin cerrar.");
        }
        if (value.Length > 0 || row.Count > 0)
        {
            row.Add(value.ToString());
            rows.Add(row);
        }
        return rows;
    }

    private static IReadOnlyList<IReadOnlyList<ImportCell>> ReadXlsx(byte[] content)
    {
        try
        {
            using MemoryStream stream = new(content, writable: false);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumArchiveEntries ||
                archive.Entries.Sum(entry => Math.Max(0, entry.Length)) > MaximumExpandedBytes)
            {
                throw new TrainingAttendanceImportException(
                    "unsafe_xlsx_archive",
                    "El libro excede los límites seguros de procesamiento.");
            }

            ZipArchiveEntry workbookEntry = GetRequiredEntry(archive, "xl/workbook.xml");
            XDocument workbook = LoadXml(workbookEntry);
            XNamespace spreadsheet =
                "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace relationships =
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XElement? firstSheet = workbook
                .Descendants(spreadsheet + "sheet")
                .FirstOrDefault();
            string relationshipId = firstSheet?.Attribute(relationships + "id")?.Value
                ?? throw new TrainingAttendanceImportException(
                    "xlsx_without_worksheet",
                    "El libro no contiene una hoja de respuestas.");

            XDocument workbookRelationships = LoadXml(GetRequiredEntry(
                archive, "xl/_rels/workbook.xml.rels"));
            XNamespace packageRelationships =
                "http://schemas.openxmlformats.org/package/2006/relationships";
            string target = workbookRelationships
                .Descendants(packageRelationships + "Relationship")
                .FirstOrDefault(item =>
                    item.Attribute("Id")?.Value == relationshipId)
                ?.Attribute("Target")?.Value
                ?? throw new TrainingAttendanceImportException(
                    "xlsx_without_worksheet",
                    "No se pudo localizar la hoja de respuestas.");
            string worksheetPath = NormalizeArchivePath("xl", target);
            XDocument worksheet = LoadXml(GetRequiredEntry(archive, worksheetPath));

            IReadOnlyList<string> sharedStrings = ReadSharedStrings(archive);
            IReadOnlySet<int> dateStyles = ReadDateStyles(archive);
            bool date1904 = string.Equals(
                workbook.Descendants(spreadsheet + "workbookPr")
                    .FirstOrDefault()?.Attribute("date1904")?.Value,
                "1",
                StringComparison.Ordinal);

            List<IReadOnlyList<ImportCell>> rows = [];
            foreach (XElement rowElement in worksheet
                         .Descendants(spreadsheet + "row")
                         .Take(MaximumRows + 2))
            {
                SortedDictionary<int, ImportCell> cells = [];
                int nextColumn = 0;
                foreach (XElement cell in rowElement.Elements(spreadsheet + "c"))
                {
                    string reference = cell.Attribute("r")?.Value ?? string.Empty;
                    int column = reference.Length > 0
                        ? ColumnIndex(reference)
                        : nextColumn;
                    nextColumn = column + 1;
                    cells[column] = ReadXlsxCell(
                        cell, spreadsheet, sharedStrings, dateStyles, date1904);
                }
                if (cells.Count == 0)
                {
                    rows.Add([]);
                    continue;
                }
                ImportCell[] values = Enumerable
                    .Range(0, cells.Keys.Max() + 1)
                    .Select(index => cells.TryGetValue(index, out ImportCell? value)
                        ? value
                        : new ImportCell(string.Empty, null))
                    .ToArray();
                rows.Add(values);
            }
            return rows;
        }
        catch (TrainingAttendanceImportException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or IOException or
                System.Xml.XmlException or FormatException or OverflowException)
        {
            throw new TrainingAttendanceImportException(
                "invalid_xlsx_file",
                "El archivo .xlsx no es un libro de respuestas válido.");
        }
    }

    private static ZipArchiveEntry GetRequiredEntry(
        ZipArchive archive,
        string path) =>
        archive.GetEntry(path) ?? throw new TrainingAttendanceImportException(
            "invalid_xlsx_file",
            $"El libro no contiene {path}.");

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using Stream stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static string NormalizeArchivePath(string basePath, string target)
    {
        Uri root = new($"http://atlas.local/{basePath.TrimEnd('/')}/");
        string path = Uri.UnescapeDataString(new Uri(root, target).AbsolutePath)
            .TrimStart('/');
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new TrainingAttendanceImportException(
                "invalid_xlsx_file",
                "El libro contiene una relación de archivo insegura.");
        }
        return path;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        XDocument document = LoadXml(entry);
        XNamespace spreadsheet =
            "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        return document.Descendants(spreadsheet + "si")
            .Select(item => string.Concat(
                item.Descendants(spreadsheet + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static IReadOnlySet<int> ReadDateStyles(ZipArchive archive)
    {
        ZipArchiveEntry? entry = archive.GetEntry("xl/styles.xml");
        if (entry is null) return new HashSet<int>();
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
        if (cellFormats is null) return result;
        int index = 0;
        foreach (XElement format in cellFormats.Elements(spreadsheet + "xf"))
        {
            uint id = uint.TryParse(
                format.Attribute("numFmtId")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out uint parsed) ? parsed : 0;
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

    private static ImportCell ReadXlsxCell(
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
            return new ImportCell(
                string.Concat(cell.Descendants(spreadsheet + "t")
                    .Select(item => item.Value)),
                null);
        }
        if (type == "s" && int.TryParse(raw, out int sharedIndex) &&
            sharedIndex >= 0 && sharedIndex < sharedStrings.Count)
        {
            return new ImportCell(sharedStrings[sharedIndex], null);
        }
        if (type is "str" or "d")
        {
            return new ImportCell(raw, null);
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
            DateTime date = DateTime.SpecifyKind(
                DateTime.FromOADate(serial).AddDays(date1904 ? 1462 : 0),
                DateTimeKind.Unspecified);
            return new ImportCell(
                date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                date);
        }
        return new ImportCell(raw, null);
    }

    private static int ColumnIndex(string reference)
    {
        int result = 0;
        foreach (char character in reference)
        {
            if (!char.IsAsciiLetter(character)) break;
            result = checked(result * 26 + char.ToUpperInvariant(character) - 'A' + 1);
        }
        return Math.Max(0, result - 1);
    }

    private static ParsedRows ParseRows(
        IReadOnlyList<IReadOnlyList<ImportCell>> table,
        TimeZoneInfo operationalTimeZone)
    {
        if (table.Count > MaximumRows + 1)
        {
            throw new TrainingAttendanceImportException(
                "too_many_attendance_rows",
                $"La asistencia admite hasta {MaximumRows} respuestas por archivo.");
        }

        int headerIndex = -1;
        for (int index = 0; index < table.Count; index++)
        {
            if (table[index].Any(cell => !string.IsNullOrWhiteSpace(cell.Text)))
            {
                headerIndex = index;
                break;
            }
        }
        if (headerIndex < 0)
        {
            throw new TrainingAttendanceImportException(
                "attendance_file_without_headers",
                "No se encontró la fila de encabezados.");
        }

        IReadOnlyList<ImportCell> headers = table[headerIndex];
        Dictionary<string, int> columns = [];
        Dictionary<string, string> mapping = [];
        HashSet<int> assignedColumns = [];
        foreach ((string canonical, string[] aliases) in HeaderAliases)
        {
            for (int index = 0; index < headers.Count; index++)
            {
                if (assignedColumns.Contains(index)) continue;
                string normalized = NormalizeText(headers[index].Text);
                if (aliases.Any(alias => HeaderMatches(
                        canonical, normalized, alias)))
                {
                    columns[canonical] = index;
                    mapping[canonical] = headers[index].Text.Trim();
                    assignedColumns.Add(index);
                    break;
                }
            }
        }
        string[] required = ["timestamp", "name", "agency", "department"];
        string[] missing = required.Where(item => !columns.ContainsKey(item)).ToArray();
        if (missing.Length > 0)
        {
            throw new TrainingAttendanceImportException(
                "missing_attendance_columns",
                "Faltan las columnas requeridas: fecha/hora, nombre completo, agencia y departamento.");
        }

        List<TrainingAttendanceImportRow> accepted = [];
        List<string> warnings = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        int sourceRows = 0;
        int duplicates = 0;
        int rejected = 0;
        for (int index = headerIndex + 1; index < table.Count; index++)
        {
            IReadOnlyList<ImportCell> row = table[index];
            if (row.All(cell => string.IsNullOrWhiteSpace(cell.Text))) continue;
            sourceRows++;
            int sourceRow = index + 1;
            string name = CellText(row, columns["name"]);
            string agency = CellText(row, columns["agency"]);
            string department = CellText(row, columns["department"]);
            string? email = columns.TryGetValue("email", out int emailColumn)
                ? NullIfEmpty(CellText(row, emailColumn))
                : null;
            ImportCell timestampCell = Cell(row, columns["timestamp"]);

            if (name.Length is 0 or > 180 || agency.Length is 0 or > 180 ||
                department.Length is 0 or > 160 ||
                (email is not null &&
                 (email.Length > 254 || !email.Contains('@') || email.Contains(' '))) ||
                !TryReadTimestamp(
                    timestampCell,
                    operationalTimeZone,
                    out DateTimeOffset respondedAtUtc))
            {
                rejected++;
                AddWarning(warnings, $"Fila {sourceRow}: datos incompletos o fecha inválida.");
                continue;
            }

            string identitySource = email is not null
                ? $"EMAIL|{NormalizeText(email)}"
                : $"PERSON|{NormalizeText(name)}|{NormalizeText(agency)}|{NormalizeText(department)}";
            string identityKey = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(identitySource)))
                .ToLowerInvariant();
            if (!identities.Add(identityKey))
            {
                duplicates++;
                AddWarning(warnings, $"Fila {sourceRow}: respuesta duplicada omitida.");
                continue;
            }

            accepted.Add(new TrainingAttendanceImportRow(
                sourceRow,
                name,
                email,
                agency,
                department,
                respondedAtUtc,
                identityKey));
        }

        if (accepted.Count == 0)
        {
            throw new TrainingAttendanceImportException(
                "attendance_without_valid_rows",
                "El archivo no contiene respuestas completas que ATLAS pueda importar.");
        }
        if (warnings.Count == 20 && duplicates + rejected > 20)
        {
            warnings.Add("Existen más incidencias; consulte los totales de la importación.");
        }
        return new ParsedRows(
            sourceRows, duplicates, rejected, mapping, warnings, accepted);
    }

    private static ImportCell Cell(IReadOnlyList<ImportCell> row, int index) =>
        index >= 0 && index < row.Count
            ? row[index]
            : new ImportCell(string.Empty, null);

    private static string CellText(IReadOnlyList<ImportCell> row, int index) =>
        Cell(row, index).Text.Trim();

    private static string? NullIfEmpty(string value) =>
        value.Length == 0 ? null : value;

    private static bool TryReadTimestamp(
        ImportCell cell,
        TimeZoneInfo operationalTimeZone,
        out DateTimeOffset utc)
    {
        if (cell.DateValue is DateTime dateValue)
        {
            return TryConvertLocalToUtc(dateValue, operationalTimeZone, out utc);
        }
        string value = cell.Text.Trim();
        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTimeOffset offsetValue) &&
            HasExplicitOffset(value))
        {
            utc = offsetValue.ToUniversalTime();
            return true;
        }
        foreach (CultureInfo culture in new[]
                 {
                     GuatemalaCulture, SpanishCulture, CultureInfo.InvariantCulture
                 })
        {
            if (DateTime.TryParse(
                    value,
                    culture,
                    DateTimeStyles.AllowWhiteSpaces,
                    out DateTime local) &&
                TryConvertLocalToUtc(local, operationalTimeZone, out utc))
            {
                return true;
            }
        }
        utc = default;
        return false;
    }

    private static bool TryConvertLocalToUtc(
        DateTime value,
        TimeZoneInfo timeZone,
        out DateTimeOffset utc)
    {
        DateTime local = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }
        TimeSpan offset = timeZone.GetUtcOffset(local);
        utc = new DateTimeOffset(local, offset).ToUniversalTime();
        return true;
    }

    private static bool HasExplicitOffset(string value) =>
        OffsetSuffixRegex().IsMatch(value);

    private static bool HeaderMatches(
        string canonical,
        string normalized,
        string alias)
    {
        if (normalized == alias) return true;
        if (normalized.Length == 0) return false;

        if (canonical == "email")
        {
            // "correo" puede aparecer en preguntas como "señales de alerta de
            // un correo sospechoso". Para correo electrónico exigimos que el
            // encabezado esté anclado al inicio o que termine con un alias
            // inequívoco.
            if (alias == "correo")
            {
                return normalized.StartsWith(alias, StringComparison.Ordinal);
            }
            return normalized.StartsWith(alias, StringComparison.Ordinal) ||
                   normalized.EndsWith(alias, StringComparison.Ordinal);
        }

        return normalized.StartsWith(alias, StringComparison.Ordinal) ||
               (alias.Length >= 6 &&
                normalized.Contains(alias, StringComparison.Ordinal));
    }

    public static string NormalizeText(string? value)
    {
        string decomposed = (value ?? string.Empty).Trim().Normalize(
            NormalizationForm.FormD);
        StringBuilder result = new();
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                result.Append(char.ToLowerInvariant(character));
            }
        }
        return result.ToString();
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 20) warnings.Add(warning);
    }

    private sealed record ImportCell(string Text, DateTime? DateValue);

    private sealed record ParsedRows(
        int SourceRowCount,
        int DuplicateRowCount,
        int RejectedRowCount,
        IReadOnlyDictionary<string, string> HeaderMapping,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<TrainingAttendanceImportRow> Rows);

    [GeneratedRegex("\"[^\"]*\"|\\[[^\\]]*\\]")]
    private static partial Regex QuotedFormatPartRegex();

    [GeneratedRegex("(?:Z|[+-]\\d{2}:?\\d{2})$")]
    private static partial Regex OffsetSuffixRegex();
}
