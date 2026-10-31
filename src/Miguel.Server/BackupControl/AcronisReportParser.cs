using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Miguel.Server.BackupControl;

public static class AcronisReportParser
{
    private static readonly XNamespace Main="http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly string[][] Headers=[
        ["nombre de dispositivo","device name"], ["nombre del plan","plan name"], ["último resultado","last result"],
        ["estado del dispositivo","device status","device state"],
        ["fecha de la última copia de seguridad correcta","last successful backup date","last successful backup"],
        ["ejecuciones totales","total runs"], ["número de ejecuciones correctas","successful runs","number of successful runs"] ];
    private sealed record Cell(string Text,double? DateSerial=null);

    public static AcronisReportDocument Parse(byte[] content,int offsetMinutes)
    {
        if(offsetMinutes is < -720 or > 840 || offsetMinutes%15!=0)
            throw new BackupWorkspaceException("Selecciona una zona horaria válida para las fechas del informe.");
        try
        {
            using var stream=new MemoryStream(content,false);
            using var zip=new ZipArchive(stream,ZipArchiveMode.Read);
            if(zip.Entries.Count>512 || zip.Entries.Sum(e=>e.Length)>80_000_000 ||
                zip.Entries.Any(e=>e.Length>16_000_000 || e.FullName.Contains("vbaProject",StringComparison.OrdinalIgnoreCase)) ||
                zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=zip.Entries.Count)
                throw new BackupWorkspaceException("El Excel excede los límites seguros o contiene un formato no permitido.");
            foreach(var entry in zip.Entries.Where(e=>e.FullName.EndsWith(".rels",StringComparison.OrdinalIgnoreCase)))
                if(Xml(entry).Descendants().Any(e=>e.Name.LocalName=="Relationship" && e.Attribute("TargetMode")?.Value=="External"))
                    throw new BackupWorkspaceException("El informe contiene relaciones externas. Exporta un Excel independiente desde Acronis.");
            var workbook=Xml(Required(zip,"xl/workbook.xml"));
            bool date1904=workbook.Descendants(Main+"workbookPr").FirstOrDefault()?.Attribute("date1904")?.Value is "1" or "true";
            var relationships=Xml(Required(zip,"xl/_rels/workbook.xml.rels"));
            XNamespace rel="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            var sharedEntry=zip.GetEntry("xl/sharedStrings.xml");
            string[] shared=sharedEntry is null?[]:Xml(sharedEntry).Descendants(Main+"si")
                .Select(e=>string.Concat(e.Descendants(Main+"t").Select(t=>t.Value))).ToArray();
            if(shared.Length>100_000 || shared.Any(s=>s.Length>4096))throw new BackupWorkspaceException("El libro contiene textos demasiado extensos.");
            var dateStyles=DateStyles(zip);
            List<AcronisReportDocument> reports=[];
            foreach(var sheet in workbook.Descendants(Main+"sheet"))
            {
                string? id=sheet.Attribute(rel+"id")?.Value;
                string? target=relationships.Descendants().FirstOrDefault(e=>e.Name.LocalName=="Relationship" && e.Attribute("Id")?.Value==id)?.Attribute("Target")?.Value;
                if(target is null)throw new BackupWorkspaceException("No se puede localizar una hoja del Excel.");
                if(target.Contains('\\') || target.Contains(':') || target.Split('/').Contains(".."))
                    throw new BackupWorkspaceException("El libro contiene una ruta de hoja no permitida.");
                string path=target.StartsWith('/')?target.TrimStart('/'):"xl/"+target;
                var xml=Xml(Required(zip,path));
                if(xml.Descendants(Main+"f").Any())
                    throw new BackupWorkspaceException("El informe contiene fórmulas. Usa la exportación de valores original de Acronis.");
                var sourceRows=xml.Descendants(Main+"row").ToArray();
                int[]? columns=null;int headerIndex=-1;
                for(int i=0;i<Math.Min(sourceRows.Length,20);i++)
                {
                    var cells=Cells(sourceRows[i],shared,dateStyles);
                    int[] found=Headers.Select(aliases=>cells.Where(c=>aliases.Any(a=>Header(a)==Header(c.Value.Text))).Select(c=>c.Key).DefaultIfEmpty(-1).Single()).ToArray();
                    if(found.All(n=>n>=0)) { columns=found;headerIndex=i;break; }
                }
                if(columns is null)continue;
                List<AcronisReportRow> rows=[];
                HashSet<int> rowNumbers=[];
                for(int i=headerIndex+1;i<sourceRows.Length;i++)
                {
                    var cells=Cells(sourceRows[i],shared,dateStyles);
                    var values=columns.Select(c=>cells.GetValueOrDefault(c,new(""))).ToArray();
                    if(values.All(c=>c.Text.Length==0))continue;
                    if(rows.Count>=2000)throw new BackupWorkspaceException("El informe supera 2000 filas de copia. Exporta un alcance más pequeño.");
                    int rowNumber=int.TryParse(sourceRows[i].Attribute("r")?.Value,out int n)?n:i+1;
                    if(rowNumber<1 || !rowNumbers.Add(rowNumber))
                        throw new BackupWorkspaceException("La tabla contiene números de fila inválidos o repetidos.");
                    List<string> issues=[];
                    if(values[0].Text.Length is < 1 or > 240 || values[1].Text.Length is < 1 or > 240)
                        issues.Add("Falta el equipo o el plan, o su nombre es demasiado largo.");
                    DateTimeOffset? date=null;
                    string raw=values[4].Text;
                    if(values[4].DateSerial is double serial)
                    {
                        DateTime dt=DateTime.SpecifyKind(DateTime.FromOADate(serial+(date1904?1462:0)),DateTimeKind.Unspecified);
                        date=new DateTimeOffset(dt,TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
                        raw=dt.ToString("dd/MM/yyyy HH:mm:ss",CultureInfo.InvariantCulture);
                    }
                    else if(raw.Length>0 && raw is not ("-" or "Nunca" or "Never"))
                    {
                        if(DateTime.TryParseExact(raw,["d/M/yyyy H:mm","d/M/yyyy H:mm:ss","yyyy-MM-dd HH:mm","yyyy-MM-dd HH:mm:ss"],
                            CultureInfo.InvariantCulture,DateTimeStyles.AllowWhiteSpaces,out var dt))
                            date=new DateTimeOffset(DateTime.SpecifyKind(dt,DateTimeKind.Unspecified),TimeSpan.FromMinutes(offsetMinutes)).ToUniversalTime();
                        else issues.Add("La fecha de última copia correcta no tiene un formato reconocido.");
                    }
                    int? total=Count(values[5].Text),success=Count(values[6].Text);
                    if(total is null || success is null || success>total)issues.Add("Los contadores de ejecuciones no son válidos.");
                    rows.Add(new(rowNumber,values[0].Text,values[1].Text,values[2].Text,values[3].Text,raw,date,total,success,issues));
                }
                if(rows.Count==0)throw new BackupWorkspaceException("La tabla de copias no contiene dispositivos.");
                reports.Add(new(sheet.Attribute("name")?.Value??"Estado de copias",rows));
            }
            if(reports.Count!=1)throw new BackupWorkspaceException("Se requiere una sola hoja con equipo, plan, resultado, fecha de última copia y contadores. Usa el informe Resumen de Acronis.");
            return reports[0];
        }
        catch(BackupWorkspaceException){throw;}
        catch(Exception ex) when(ex is InvalidDataException or IOException or XmlException or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {throw new BackupWorkspaceException("El Excel no tiene un formato de informe de Acronis válido.");}
    }

    private static int? Count(string value)=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out int count) && count is >=0 and <=1_000_000?count:null;
    private static string Header(string value)=>string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c=>CharUnicodeInfo.GetUnicodeCategory(c)!=UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c))).ToUpperInvariant();
    private static ZipArchiveEntry Required(ZipArchive zip,string name)=>zip.GetEntry(name)??throw new BackupWorkspaceException("El archivo no es un Excel completo.");
    private static XDocument Xml(ZipArchiveEntry entry)
    {
        using var stream=entry.Open();
        using var reader=XmlReader.Create(stream,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16_000_000 });
        return XDocument.Load(reader);
    }
    private static Dictionary<int,Cell> Cells(XElement row,string[] shared,HashSet<int> dateStyles)
    {
        Dictionary<int,Cell> result=[];int next=0;
        foreach(var c in row.Elements(Main+"c"))
        {
            string reference=c.Attribute("r")?.Value??"";int column=0;
            foreach(char letter in reference.TakeWhile(char.IsLetter))column=checked(column*26+(char.ToUpperInvariant(letter)-'A'+1));
            column=column>0?column-1:next;next=column+1;
            if(column is <0 or >63 || result.ContainsKey(column))throw new BackupWorkspaceException("La tabla excede el límite de columnas o contiene celdas duplicadas.");
            string type=c.Attribute("t")?.Value??"",raw=c.Element(Main+"v")?.Value??"";
            string value=type=="inlineStr"?string.Concat(c.Descendants(Main+"t").Select(t=>t.Value)):
                type=="s"?int.TryParse(raw,out int index) && index>=0 && index<shared.Length?shared[index]:throw new BackupWorkspaceException("El libro contiene una referencia de texto inválida."):raw;
            if(value.Length>4096)throw new BackupWorkspaceException("Una celda contiene demasiado texto.");
            bool isDate=type is "" or "n" && int.TryParse(c.Attribute("s")?.Value,out int style) && dateStyles.Contains(style);
            result[column]=new(value.Trim(),isDate && double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out double d)?d:null);
        }
        return result;
    }
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var entry=zip.GetEntry("xl/styles.xml");if(entry is null)return [];
        var xml=Xml(entry);
        var custom=xml.Descendants(Main+"numFmt").ToDictionary(e=>(string?)e.Attribute("numFmtId")??"",e=>(string?)e.Attribute("formatCode")??"");
        var formats=xml.Descendants(Main+"cellXfs").FirstOrDefault()?.Elements(Main+"xf").ToArray()??[];
        HashSet<int> result=[];
        for(int i=0;i<formats.Length;i++)
        {
            string id=formats[i].Attribute("numFmtId")?.Value??"0";
            if(int.TryParse(id,out int number) && (number is >=14 and <=22 or >=27 and <=36 or >=50 and <=58 ||
                custom.TryGetValue(id,out string? code) && code.ToLowerInvariant().Contains('y') && code.ToLowerInvariant().Contains('d')))result.Add(i);
        }
        return result;
    }
}
