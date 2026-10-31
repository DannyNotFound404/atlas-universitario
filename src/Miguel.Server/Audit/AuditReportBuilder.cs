using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Miguel.Server.Audit;

public static class AuditReportBuilder
{
    public static string Outcome(string value) => value switch {
        "SUCCEEDED" => "Acción completada", "FAILED" => "Fallido o rechazado", "REQUESTED" => "Solicitado",
        "WARNING" => "Con observaciones", _ => "Sin resultado determinado" };
    public static string Attribution(string value) => value switch {
        "CONFIRMED" => "Emisor autenticado", "SYSTEM" => "Emisión de sistema", _ => "Histórico sin atribución" };
    private static string Period(string value) => value switch {
        "ALL" => "Todo el histórico", "24H" => "Últimas 24 horas", "7D" => "Últimos 7 días", "30D" => "Últimos 30 días",
        "90D" => "Últimos 90 días", "365D" => "Últimos 365 días", _ => value };
    private static string Bounds(string period, DateTimeOffset? from, DateTimeOffset? to) =>
        $"Desde: {(from.HasValue ? from.Value.ToString("O") : Period(period))}; hasta (exclusivo): {to?.ToString("O") ?? "sin límite"}";
    public static string Filters(AuditEventQuery q) => $"{Bounds(q.Period,q.FromUtc,q.ToUtc)}; usuario: {OrAll(q.Actor)}; módulo: {(q.Module.Length==0 ? "Todos" : AuditTaxonomy.GetModuleLabel(q.Module))}; resultado: {(q.Outcome.Length==0 ? "Todos" : Outcome(q.Outcome))}; búsqueda: {OrAll(q.Search)}; objeto: {OrAll(q.Target)}";
    public static string Filters(AuditEnrollmentQuery q) => $"{Bounds(q.Period,q.FromUtc,q.ToUtc)}; agencia: {OrAll(q.Agency)}; atribución: {(q.Attribution.Length==0 ? "Todas" : Attribution(q.Attribution))}; búsqueda: {OrAll(q.Search)}";
    private static string OrAll(string value) => value.Length==0 ? "Sin filtro" : value;

    public static byte[] Events(IReadOnlyList<AuditEventView> items, DateTimeOffset generated, string actor, string filters, string format)
    {
        string[] headers=["Evento","Fecha UTC","Módulo","Acción","Resultado","Usuario","Nombre conservado","Objeto","Referencia del objeto","Datos al registrar","Información registrada"];
        var rows=items.Select(e=>new[]{e.Id.ToString(),e.OccurredAtUtc.ToString("O"),e.ModuleLabel,e.ActionLabel,Outcome(e.Outcome),e.ActorId,
            e.ActorDisplayName,e.TargetDisplayName ?? TargetType(e.TargetType),e.TargetId ?? "",e.ContextCaptured ? "Sí" : "Histórico sin captura de nombres",Detail(e.Detail)}).ToList();
        return Build("Actividad de ATLAS",headers,rows,generated,actor,filters,format,
            "El resultado describe la acción registrada. Una solicitud no confirma su ejecución posterior. Los valores anteriores sólo se muestran cuando fueron conservados en el evento.");
    }
    public static byte[] Enrollments(IReadOnlyList<AuditEnrollmentView> items, DateTimeOffset generated, string actor, string filters, string format)
    {
        string[] headers=["Vinculación","Activo","Agencia","Emisor","Nombre del emisor","Fecha de emisión UTC","Origen de emisión","Fecha de vinculación UTC","Origen de vinculación","Instalador","Agente","Atribución","Reinscripción","Referencia de emisión","Nombre al emitir"];
        var rows=items.Select(e=>new[]{e.Id.ToString(),e.MachineName,e.AgencyName,e.IssuedBy ?? "No registrado",e.IssuedByDisplayName,
            e.IssuedAtUtc?.ToString("O") ?? "No conservada",e.IssuedFromIp ?? "No conservado",e.EnrolledAtUtc.ToString("O"),e.EnrolledFromIp ?? "No conservado",
            e.InstallerVersion ?? "No conservada",e.AgentVersion,Attribution(e.AttributionStatus),e.IsReenrollment ? "Sí" : "No",
            e.IssuanceId?.ToString() ?? "No disponible",e.IssuerNameCaptured ? "Conservado" : "Sólo cuenta o referencia histórica"}).ToList();
        return Build("Vinculación de activos",headers,rows,generated,actor,filters,format,
            "Relaciona al emisor autenticado del instalador con el activo que utilizó la autorización de un solo uso. No acredita quién ejecutó físicamente el archivo. El usuario interactivo actual no prueba la identidad del ejecutor.");
    }
    private static byte[] Build(string title,string[] headers,List<string[]> rows,DateTimeOffset generated,string actor,string filters,string format,string scope)
    {
        string metadata=$"Generado: {generated:O} · Usuario: {actor} · Registros: {rows.Count:N0}";
        StringBuilder output=new();
        if(format=="csv")
        {
            Csv(output,"ATLAS",title); Csv(output,"Generación",metadata); Csv(output,"Filtros",filters); Csv(output,"Alcance",scope);
            output.AppendLine(); Csv(output,headers); foreach(var row in rows) Csv(output,row);
            return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(output.ToString())).ToArray();
        }
        output.Append("<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\"><title>ATLAS · ").Append(H(title));
        output.Append("</title><style>body{font:14px 'Segoe UI',Arial,sans-serif;color:#173b4e;max-width:1300px;margin:40px auto;padding:0 24px}h1{font-size:28px}p{line-height:1.6}.brand{color:#008c99;letter-spacing:.12em;font-weight:700}.meta{padding:18px;background:#f1f7f8;border-radius:12px}article{margin:22px 0;padding:20px;border:1px solid #d9e5e8;border-radius:12px;break-inside:avoid}h2{font-size:18px;margin-top:0}dl{display:grid;grid-template-columns:190px 1fr;gap:8px 16px}dt{font-weight:600}dd{margin:0;white-space:pre-wrap;overflow-wrap:anywhere}.hint{color:#536c79}@media print{body{margin:0;padding:0;font-size:10pt}article{border-color:#b9cbd0}.hint{display:none}@page{margin:16mm}}@media(max-width:600px){dl{grid-template-columns:1fr}dd{margin-bottom:10px}}</style></head><body><p class=\"brand\">ATLAS · AUDITORÍA</p><h1>");
        output.Append(H(title)).Append("</h1><div class=\"meta\"><p>").Append(H(metadata)).Append("</p><p>").Append(H(filters)).Append("</p></div><p>").Append(H(scope)).Append("</p><p class=\"hint\">Puedes imprimir este informe o guardarlo como PDF desde el navegador. Todas las fechas del informe se expresan en UTC.</p>");
        if(rows.Count==0) output.Append("<p>No hay registros para los filtros seleccionados.</p>");
        foreach(var row in rows)
        {
            output.Append("<article><h2>").Append(H(title=="Actividad de ATLAS" ? row[3] : row[1])).Append(" · #").Append(H(row[0])).Append("</h2><dl>");
            for(int i=1;i<headers.Length;i++) output.Append("<dt>").Append(H(headers[i])).Append("</dt><dd>").Append(H(row[i])).Append("</dd>");
            output.Append("</dl></article>");
        }
        output.Append("<footer>Fuente: registros conservados por ATLAS. Esta exportación queda registrada en la auditoría junto con la huella SHA-256 del archivo.</footer></body></html>");
        return Encoding.UTF8.GetBytes(output.ToString());
    }
    public static string TargetType(string? value) => value switch {
        "Device"=>"Activo", "AdminUser"=>"Cuenta administrativa", "GovernanceDocument"=>"Documento", "GovernanceDocumentVersion"=>"Versión documental",
        "BackupPlan"=>"Plan de respaldo", "BackupControlProfile"=>"Control de respaldo", "NasAppliance"=>"NAS", "Agency"=>"Agencia", "Audit"=>"Auditoría",
        "OperationalCase"=>"Expediente", "TrainingSession"=>"Sesión", "TrainingCycle"=>"Ciclo de capacitación", _=>"Registro de ATLAS" };
    public static string Detail(JsonElement? detail)
    {
        if(detail is null) return "No se conservó información adicional.";
        var safe=AuditRedactor.RedactJson(detail.Value.GetRawText());
        List<string> fields=[];
        void Visit(JsonElement value,string path)
        {
            if(value.ValueKind==JsonValueKind.Object)
                foreach(var property in value.EnumerateObject()) Visit(property.Value,(path.Length==0?"":path+" · ")+Label(property.Name));
            else if(value.ValueKind==JsonValueKind.Array) {int i=0; foreach(var item in value.EnumerateArray()) Visit(item,$"{path} [{++i}]"); if(i==0)fields.Add(path+": Sin elementos");}
            else fields.Add(path+": "+(value.ValueKind switch { JsonValueKind.True=>"Sí",JsonValueKind.False=>"No",JsonValueKind.Null=>"No registrado",_=>value.ToString()}));
        }
        Visit(safe!.Value,""); return string.Join("\n",fields);
    }
    private static string Label(string key) => key.ToLowerInvariant() switch {
        "before"=>"Anterior", "after"=>"Nuevo", "backupprovider"=>"Modalidad de respaldo", "nasapplianceid"=>"Referencia del NAS", "folders"=>"Carpetas",
        "name"=>"Nombre", "title"=>"Título", "revision"=>"Versión", "originalfilename"=>"Archivo", "sha256"=>"Huella SHA-256", "remoteaddress"=>"Dirección de origen",
        "status"=>"Estado", "description"=>"Descripción", "notes"=>"Observaciones", "reason"=>"Motivo", "exportedrows"=>"Registros exportados", "filters"=>"Filtros",
        _=>Regex.Replace(key.Replace('_',' '),"([a-z])([A-Z])","$1 $2") };
    private static string H(string? value)=>WebUtility.HtmlEncode(AuditRedactor.RedactText(value??""));
    private static void Csv(StringBuilder output,params string[] values)=>output.AppendLine(string.Join(",",values.Select(value=>
    {
        string safe=AuditRedactor.RedactText(value); string trimmed=safe.TrimStart();
        if(trimmed.Length>0 && "=+-@".Contains(trimmed[0]) || safe.Length>0 && "\t\r\n".Contains(safe[0])) safe="'"+safe;
        return "\""+safe.Replace("\"","\"\"")+"\"";
    })));
}
