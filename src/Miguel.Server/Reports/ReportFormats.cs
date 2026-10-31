using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace Miguel.Server.Reports;

public static class ReportFormats
{
    public static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static byte[] Csv(ReportSnapshot s)
    {
        StringBuilder b=new("\uFEFF");
        void Row(IEnumerable<string> cells)=>b.AppendLine(string.Join(",",cells.Select(CsvCell)));
        Row([s.Institution,s.Title,s.Classification]); Row(["ID",s.Id.ToString(),"Corte UTC",s.CutoffUtc.ToString("O"),"Zona",s.TimeZone]);
        Row(["Período",s.Filters.From.ToString()!,s.Filters.To.ToString()!,"Autor",s.Author]);
        foreach(var note in s.Notes)Row(["Nota",note]);
        foreach(var table in s.Tables) { Row([]);Row([table.Title,table.Basis,table.Source]);Row(table.Columns);foreach(var row in table.Rows)Row(row); }
        return Encoding.UTF8.GetBytes(b.ToString());
    }
    public static string CsvCell(string value)
    {
        string trimmed=value.TrimStart();
        if(trimmed.Length>0 && "=+-@＝＋－＠".Contains(trimmed[0]) || value.IndexOfAny(['\t','\r','\n'])>=0) value="'"+value;
        return '"'+value.Replace("\"","\"\"")+'"';
    }
    public static byte[] Xlsx(ReportSnapshot s)
    {
        List<(string Name,List<string[]> Rows)> sheets=[];
        sheets.Add(("Resumen", [ [s.Institution,s.Title], ["Clasificación",s.Classification], ["Identificador",s.Id.ToString()],
            ["Plantilla versión",s.TemplateVersion.ToString()], ["Autor",s.Author], ["Corte UTC",s.CutoffUtc.ToString("O")],
            ["Zona horaria",s.TimeZone], ["Desde",s.Filters.From.ToString()!], ["Hasta inclusive",s.Filters.To.ToString()!], ["Agencia",s.Filters.Agency??"Todas"], ["Base",s.Policy],
            ..s.Notes.Select(x=>new[]{"Limitación",x}),..s.Tables.Select(x=>new[]{x.Title,x.Rows.Count.ToString()+" filas",x.Basis}) ]));
        for(int i=0;i<s.Tables.Count;i++) {var t=s.Tables[i];sheets.Add(($"Detalle {i+1}",[t.Columns,..t.Rows]));}
        sheets.Add(("Diccionario",[["Hoja","Contenido","Base temporal","Fuente","Campos"],..s.Tables.Select((t,i)=>new[]{"Detalle "+(i+1),t.Title,t.Basis=="CURRENT"?"Estado al generar":"Eventos del período; estado registrado al generar",t.Source,string.Join("; ",t.Columns)})]));
        using MemoryStream stream=new();
        using(var zip=new ZipArchive(stream,ZipArchiveMode.Create,true))
        {
            void Xml(string name,Action<XmlWriter> action) {using var entry=zip.CreateEntry(name,CompressionLevel.Fastest).Open();using var w=XmlWriter.Create(entry,new XmlWriterSettings{Encoding=new UTF8Encoding(false),CheckCharacters=true});action(w);}
            const string main="http://schemas.openxmlformats.org/spreadsheetml/2006/main",rel="http://schemas.openxmlformats.org/package/2006/relationships",docrel="http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            Xml("[Content_Types].xml",w=>{w.WriteStartElement("Types","http://schemas.openxmlformats.org/package/2006/content-types");
                void Type(string tag,string key,string val,string type){w.WriteStartElement(tag);w.WriteAttributeString(key,val);w.WriteAttributeString("ContentType",type);w.WriteEndElement();}
                Type("Default","Extension","rels","application/vnd.openxmlformats-package.relationships+xml");Type("Default","Extension","xml","application/xml");
                Type("Override","PartName","/xl/workbook.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Type("Override","PartName","/xl/styles.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for(int i=0;i<sheets.Count;i++)Type("Override","PartName",$"/xl/worksheets/sheet{i+1}.xml","application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");w.WriteEndElement();});
            void Relationship(XmlWriter w,string id,string kind,string target){w.WriteStartElement("Relationship");w.WriteAttributeString("Id",id);w.WriteAttributeString("Type",docrel+"/"+kind);w.WriteAttributeString("Target",target);w.WriteEndElement();}
            Xml("_rels/.rels",w=>{w.WriteStartElement("Relationships",rel);Relationship(w,"rId1","officeDocument","xl/workbook.xml");w.WriteEndElement();});
            Xml("xl/workbook.xml",w=>{w.WriteStartElement("workbook",main);w.WriteStartElement("sheets");for(int i=0;i<sheets.Count;i++){w.WriteStartElement("sheet");w.WriteAttributeString("name",sheets[i].Name);w.WriteAttributeString("sheetId",(i+1).ToString());w.WriteAttributeString("r","id",docrel,"rId"+(i+1));w.WriteEndElement();}w.WriteEndElement();w.WriteEndElement();});
            Xml("xl/_rels/workbook.xml.rels",w=>{w.WriteStartElement("Relationships",rel);for(int i=0;i<sheets.Count;i++)Relationship(w,"rId"+(i+1),"worksheet",$"worksheets/sheet{i+1}.xml");Relationship(w,"styles","styles","styles.xml");w.WriteEndElement();});
            Xml("xl/styles.xml",w=>{w.WriteStartElement("styleSheet",main);w.WriteRaw("<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts><fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF123A50\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border/></borders><cellStyleXfs count=\"1\"><xf/></cellStyleXfs><cellXfs count=\"2\"><xf fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment vertical=\"top\" wrapText=\"1\"/></xf><xf fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFill=\"1\" applyFont=\"1\" applyAlignment=\"1\"><alignment wrapText=\"1\"/></xf></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");w.WriteEndElement();});
            for(int index=0;index<sheets.Count;index++)
            {
                var rows=sheets[index].Rows;
                Xml($"xl/worksheets/sheet{index+1}.xml",w=>{w.WriteStartElement("worksheet",main);w.WriteStartElement("sheetViews");w.WriteStartElement("sheetView");w.WriteAttributeString("workbookViewId","0");w.WriteStartElement("pane");w.WriteAttributeString("ySplit","1");w.WriteAttributeString("topLeftCell","A2");w.WriteAttributeString("state","frozen");w.WriteEndElement();w.WriteEndElement();w.WriteEndElement();
                    w.WriteStartElement("cols");w.WriteStartElement("col");w.WriteAttributeString("min","1");w.WriteAttributeString("max",Math.Max(1,rows.Max(x=>x.Length)).ToString());w.WriteAttributeString("width","28");w.WriteAttributeString("customWidth","1");w.WriteEndElement();w.WriteEndElement();
                    w.WriteStartElement("sheetData");for(int ri=0;ri<rows.Count;ri++){w.WriteStartElement("row");w.WriteAttributeString("r",(ri+1).ToString());foreach(string cell in rows[ri]){w.WriteStartElement("c");w.WriteAttributeString("t","inlineStr");w.WriteAttributeString("s",ri==0?"1":"0");w.WriteStartElement("is");w.WriteStartElement("t");w.WriteAttributeString("xml","space",null,"preserve");w.WriteString(XmlSafe(cell));w.WriteEndElement();w.WriteEndElement();w.WriteEndElement();}w.WriteEndElement();}w.WriteEndElement();w.WriteEndElement();});
            }
        }
        return stream.ToArray();
    }
    private static string XmlSafe(string value)
    {
        StringBuilder b=new();for(int i=0;i<value.Length;i++)
        {if(char.IsHighSurrogate(value[i])&&i+1<value.Length&&char.IsLowSurrogate(value[i+1])){b.Append(value[i]);b.Append(value[++i]);}else if(XmlConvert.IsXmlChar(value[i]))b.Append(value[i]);}
        return b.ToString();
    }

    // Text PDF, with repeated headings and record cards for wide datasets. No browser, scripts or remote resources.
    public static byte[] Pdf(ReportSnapshot s)
    {
        List<List<(string Text,int Size,bool Bold)>> pages=[[]];
        int used=0;string continuation="";
        void Line(string text,int size=10,bool bold=false)
        {
            foreach(var line in Wrap(text,size,bold)) {
                if(used+size+7>670){pages.Add([]);used=0;if(continuation.Length>0){pages[^1].Add((Wrap(continuation,10,true).First(),10,true));used=34;}}
                pages[^1].Add((line,size,bold));used+=size+7;
            }
        }
        Line(s.Title,18,true);Line(s.Institution,12,true);Line(s.Classification);
        Line("Período: "+s.Filters.From+" a "+s.Filters.To+" | Zona: "+s.TimeZone);
        Line("Corte UTC: "+s.CutoffUtc.ToString("yyyy-MM-dd HH:mm:ss")+" | Autor: "+s.Author);
        Line("Informe: "+s.Id);Line("Base: "+s.Policy);Line("");
        foreach(var table in s.Tables)Line(table.Title+": "+table.Rows.Count+" registros ("+(table.Basis=="CURRENT"?"estado al corte":"período")+")",11,true);
        Line("");foreach(string note in s.Notes)Line(note,9);
        Line("Los caracteres fuera del alfabeto latino se identifican como [U+...]. Excel y CSV conservan el texto Unicode.",9);
        foreach(var table in s.Tables)
        {
            pages.Add([]);used=0;Line(table.Title,16,true);Line(table.Basis=="CURRENT"?"Estado actual al corte de generación":"Eventos del período consultado; estado observado al corte");Line("Fuente: "+table.Source,9);Line("");
            if(table.Rows.Count==0)Line("No hay registros disponibles para este alcance.");
            for(int ri=0;ri<table.Rows.Count;ri++)
            {
                continuation=table.Title+" | Registro "+(ri+1)+" (continuación)";
                if(used>550){pages.Add([]);used=0;Line(table.Title+" (continuación)",12,true);}
                Line("Registro "+(ri+1)+" / "+table.Rows.Count,11,true);
                for(int ci=0;ci<table.Columns.Length;ci++)Line(table.Columns[ci]+": "+table.Rows[ri][ci]);
                Line("");
            }
        }
        if(pages.Count>1500)throw new PortalException("El PDF supera 1,500 páginas. Reduzca el alcance.");
        List<byte[]> objects=[];
        int Add(string content){objects.Add(Encoding.Latin1.GetBytes(content));return objects.Count;}
        Add("<< /Type /Catalog /Pages 2 0 R >>");Add("");Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        List<int> pageIds=[];
        for(int pi=0;pi<pages.Count;pi++)
        {
            StringBuilder content=new();
            void Text(string text,int size,bool bold,int x,int y)=>content.Append($"BT /{(bold?"F2":"F1")} {size} Tf 1 0 0 1 {x} {y} Tm ({PdfText(text)}) Tj ET\n");
            content.Append("0.07 0.20 0.28 rg\n");Text(Wrap("ATLAS  |  "+s.Classification,9,true).First(),9,true,42,807);int y=770;
            foreach(var line in pages[pi]){Text(line.Text,line.Size,line.Bold,42,y);y-=line.Size+7;}
            Text(s.Id.ToString()+"  |  "+(pi+1)+" / "+pages.Count,8,false,42,28);
            string body=content.ToString();int contentId=Add("<< /Length "+Encoding.Latin1.GetByteCount(body)+" >>\nstream\n"+body+"endstream");
            pageIds.Add(Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));
        }
        objects[1]=Encoding.ASCII.GetBytes("<< /Type /Pages /Count "+pageIds.Count+" /Kids ["+string.Join(" ",pageIds.Select(x=>x+" 0 R"))+"] >>");
        using MemoryStream output=new();void Write(string text)=>output.Write(Encoding.Latin1.GetBytes(text));
        Write("%PDF-1.4\n%âãÏÓ\n");List<long> offsets=[0];
        for(int i=0;i<objects.Count;i++){offsets.Add(output.Position);Write((i+1)+" 0 obj\n");output.Write(objects[i]);Write("\nendobj\n");}
        long xref=output.Position;Write("xref\n0 "+(objects.Count+1)+"\n0000000000 65535 f \n");foreach(long offset in offsets.Skip(1))Write(offset.ToString("D10",CultureInfo.InvariantCulture)+" 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count+1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");return output.ToArray();
    }
    private static IEnumerable<string> Wrap(string value,int size,bool bold)
    {
        foreach(string paragraph in PdfAlphabet(value).Replace("\r","").Replace("\t"," ").Split('\n'))
        {
            string text=paragraph;var metrics=bold?PdfFontMetrics.Bold:PdfFontMetrics.Regular;
            while(text.Length>0){int limit=0;double width=0;while(limit<text.Length){char c=text[limit];int advance=metrics[c<=255?c:'?'];if(width+advance*size/1000d>505)break;width+=advance*size/1000d;limit++;}
                if(limit>=text.Length){yield return text;break;}if(limit==0)limit=1;int cut=text.LastIndexOf(' ',Math.Min(limit,text.Length-1));if(cut<limit/2)cut=limit;yield return text[..cut];text=text[cut..].TrimStart();}
            if(paragraph.Length==0)yield return "";
        }
    }
    private static string PdfText(string value)
    {
        // WinAnsi supports Spanish; unsupported Unicode is explicit rather than silently corrupting a PDF stream.
        value=PdfAlphabet(value);
        StringBuilder b=new();foreach(char c in value){if(c is '(' or ')' or '\\')b.Append('\\');b.Append(c is >= ' ' and <= '\u00ff'?c:'?');}return b.ToString();
    }
    private static string PdfAlphabet(string value)
    {
        value=value.Replace('–','-').Replace('—','-').Replace('“','"').Replace('”','"').Replace('’','\'');
        StringBuilder b=new();foreach(var rune in value.EnumerateRunes())b.Append(rune.Value<=255?rune.ToString():"[U+"+rune.Value.ToString("X4",CultureInfo.InvariantCulture)+"]");return b.ToString();
    }
}
