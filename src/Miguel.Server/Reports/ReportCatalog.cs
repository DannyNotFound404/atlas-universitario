using System.Data;
using System.Globalization;
using Miguel.Server.Configuration;
using Npgsql;
using Microsoft.Extensions.Options;
using Miguel.Server.BackupControl;

namespace Miguel.Server.Reports;

public sealed class ReportCatalog(NpgsqlDataSource db, TimeProvider clock, IOptions<ServerOptions>? options = null)
{
    public static readonly ReportTemplate[] Templates = [
        new("executive","Resumen ejecutivo","Cobertura, resultados de copias, postura observada y prioridades por agencia.","V4 pp. 3–4, 9–10 y 12–14; base §11",true,["coverage","backupOverview","backupCompliance","securityOverview","priorities"]),
        new("inventory","Inventario y custodia","Hardware, usuario observado, responsable formal y estado del equipo.","V4 pp. 6 y 16",true,["inventory","custody"]),
        new("backups","Copias y restauraciones","Alcances específicos, fuentes, ejecuciones y demostraciones con sus muestras y repeticiones.","V4 pp. 9–10",true,["backupCompliance","backupProfiles","backupObservations","automaticBackups","restorations","planDemonstrations"]),
        new("shared","Trabajo comunal en red","Equipos que trabajan sobre una carpeta compartida y su destino registrado.","V4 pp. 9–10; base §7.1",true,["shared"]),
        new("security","Seguridad","Cobertura de postura y eventos recibidos; la ausencia de eventos no acredita seguridad.","V4 pp. 12–14 y 20–22",true,["posture","securityEvents"]),
        new("software","Software y licencias","Instalaciones observadas, autorización y derechos registrados.","V4 p. 16",true,["software","licenses","softwareActions"]),
        new("vulnerabilities","Vulnerabilidades y tratamiento","Coincidencias de software, fuentes externas, prioridades y seguimiento verificable.","V4 p. 20 y pp. 21–22; aplicación anticipada",true,["vulnerabilityFindings","vulnerabilitySources","vulnerabilityHistory"]),
        new("updates","Actualizaciones ATLAS","Versiones observadas y resultados de intentos del período.","V4 pp. 12–13; aplicación técnica",true,["updaterDevices","updaterAttempts"]),
        new("capacity","NAS y capacidad","Estado y capacidad observados, con sondeos del período.","V4 p. 10",true,["nas","nasHistory"]),
        new("governance","Capacitaciones y documentos","Sesiones, asistencia y vigencia documental institucional.","V4 pp. 7 y 16–17; base §§2 y 11",false,["training","attendance","documents"]),
        new("cases","Expedientes y auditoría","Seguimiento de expedientes y actividad administrativa institucional.","V4 pp. 20–22 y 33–34; base §11",false,["cases","audit"])
    ];
    // V4 pp. 9-10 / base §7.2: an execution on a device is not automatically
    // evidence for every control of that device. Match its authorized NAS and,
    // when available, its explicit folder identities without hiding MISSING or
    // FAILED states. Legacy/unconfigured scope remains explicitly unverifiable.
    private const string RelevantAutomaticRunSql = """
        b.device_id=p.device_id AND p.provider='ATLAS_BACKUPNAS'
        AND lower(b.payload->>'destinationHost')=lower(n.host)
        AND lower(b.payload->>'destinationShare')=lower(n.share_name)
        AND (b.payload->'evidence' IS NULL OR b.payload->'evidence'='null'::jsonb
          OR COALESCE(cardinality(p.protected_folders),0)=0
          OR NOT EXISTS (
            SELECT 1 FROM unnest(p.protected_folders) expected(folder)
            WHERE NOT EXISTS (
              SELECT 1 FROM jsonb_array_elements(COALESCE(b.payload#>'{evidence,folders}','[]'::jsonb)) actual
              WHERE atlas_backup_folder_key(expected.folder)=atlas_backup_folder_key(actual->>'name')
            )
          ))
        """;
    private sealed record Section(string Title, string Basis, string Source, string Sql);
    private static readonly Dictionary<string,Section> Sections = new()
    {
        ["coverage"] = new("Cobertura por agencia","CURRENT","devices, inventory_snapshots, security_posture_snapshots, software_devices", """
            SELECT d.agency_code AS "Agencia", COUNT(*) AS "Equipos registrados",
            COUNT(*) FILTER(WHERE d.enabled AND d.retired_at_utc IS NULL) AS "Activos",
            COUNT(*) FILTER(WHERE NOT d.enabled OR d.retired_at_utc IS NOT NULL) AS "Retirados o deshabilitados",
            COUNT(*) FILTER(WHERE d.enabled AND d.retired_at_utc IS NULL AND EXISTS(SELECT 1 FROM inventory_snapshots i WHERE i.device_id=d.id)) AS "Activos con inventario",
            COUNT(*) FILTER(WHERE d.enabled AND d.retired_at_utc IS NULL AND NOT EXISTS(SELECT 1 FROM inventory_snapshots i WHERE i.device_id=d.id)) AS "Activos sin inventario",
            COUNT(*) FILTER(WHERE d.enabled AND d.retired_at_utc IS NULL AND d.last_seen_at_utc < @cutoff - INTERVAL '1 day') AS "Activos sin comunicación en 24 h"
            FROM fd d GROUP BY d.agency_code ORDER BY d.agency_code
            """),
        ["backupOverview"] = new("Último resultado técnico por herramienta · controles activos sin pausa","CURRENT","backup_control_profiles, backup_control_observations, backup_runs, asset_custody", $$"""
            SELECT p.agency_code AS "Agencia",p.provider AS "Herramienta",COUNT(*) AS "Controles",
            COUNT(*) FILTER(WHERE o.result IN ('SUCCESS','Successful')) AS "Resultado correcto",
            COUNT(*) FILTER(WHERE o.result IN ('WARNING','Warning')) AS "Con advertencias",
            COUNT(*) FILTER(WHERE o.result IN ('FAILED','Failed')) AS "Con errores técnicos reportados",
            COUNT(*) FILTER(WHERE o.result IN ('CANCELLED','Cancelled')) AS "Cancelados",
            COUNT(*) FILTER(WHERE o.result='Skipped') AS "Omitidos sin iniciar",
            COUNT(*) FILTER(WHERE o.result='Partial') AS "Con archivos o carpetas pendientes",
            COUNT(*) FILTER(WHERE o.result='Running') AS "En curso",
            COUNT(*) FILTER(WHERE o.result IS NULL OR o.result NOT IN ('SUCCESS','Successful','WARNING','Warning','FAILED','Failed','CANCELLED','Cancelled','Skipped','Partial','Running')) AS "Sin resultado confirmado",
            COUNT(*) FILTER(WHERE o.observed_at < @cutoff - make_interval(hours=>p.frequency_hours,mins=>p.grace_minutes)) AS "Evidencias fuera de plazo"
            FROM backup_control_profiles p LEFT JOIN devices d ON d.id=p.device_id
            LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
            LEFT JOIN nas_appliances n ON n.id=p.nas_appliance_id
            LEFT JOIN LATERAL(
                SELECT x.observed_at,x.result FROM (
                    SELECT period_end_utc AS observed_at,result,created_at_utc AS received FROM backup_control_observations o WHERE profile_id=p.id
                      AND atlas_backup_evidence_context_matches(p,'OBSERVATION',o.id,o.context_key)
                    UNION ALL SELECT b.finished_at_utc,CASE WHEN b.status='Running' AND b.finished_at_utc<@cutoff-INTERVAL '10 minutes' THEN 'Unconfirmed' ELSE b.status END,COALESCE(b.received_at_utc,b.finished_at_utc)
                    FROM backup_runs b WHERE {{RelevantAutomaticRunSql}}
                )x WHERE c.tracking_since_utc IS NULL OR x.observed_at>=c.tracking_since_utc ORDER BY x.observed_at DESC,x.received DESC LIMIT 1
            )o ON true
            WHERE p.enabled AND p.approval_status IN ('ACTIVE','APPROVED') AND NOT COALESCE(c.backups_paused,false) AND (@agency='' OR p.agency_code=@agency)
              AND (p.device_id IS NULL OR d.enabled)
            GROUP BY p.agency_code,p.provider ORDER BY p.agency_code,p.provider
            """),
        ["securityOverview"] = new("Controles de seguridad observados · activos habilitados","CURRENT","devices, security_posture_snapshots", """
            SELECT d.agency_code AS "Agencia",v.control AS "Control",COUNT(*) AS "Equipos",
            COUNT(*) FILTER(WHERE v.enabled=true AND s.captured_at_utc BETWEEN @cutoff-make_interval(hours=>@freshHours) AND @cutoff+INTERVAL '5 minutes') AS "Confirmados activos recientes",
            COUNT(*) FILTER(WHERE v.enabled=false AND s.captured_at_utc BETWEEN @cutoff-make_interval(hours=>@freshHours) AND @cutoff+INTERVAL '5 minutes') AS "Reportados inactivos recientes",
            COUNT(*) FILTER(WHERE v.enabled IS NULL OR s.captured_at_utc IS NULL OR s.captured_at_utc < @cutoff-make_interval(hours=>@freshHours) OR s.captured_at_utc>@cutoff+INTERVAL '5 minutes') AS "Sin información vigente",
            COUNT(*) FILTER(WHERE s.captured_at_utc < @cutoff-make_interval(hours=>@freshHours) OR s.captured_at_utc>@cutoff+INTERVAL '5 minutes') AS "Reportes fuera de vigencia"
            FROM fd d LEFT JOIN LATERAL(SELECT * FROM security_posture_snapshots WHERE device_id=d.id ORDER BY captured_at_utc DESC,id DESC LIMIT 1)s ON true
            CROSS JOIN LATERAL(VALUES
              ('Firewall',CASE
                WHEN EXISTS(SELECT 1 FROM jsonb_array_elements(COALESCE(s.payload->'firewallProfiles','[]'))x WHERE x->>'enabled'='false') THEN false
                WHEN jsonb_array_length(COALESCE(s.payload->'firewallProfiles','[]'))>0 AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(COALESCE(s.payload->'firewallProfiles','[]'))x WHERE (x->>'enabled') IS DISTINCT FROM 'true') THEN true ELSE NULL END),
              ('BitLocker en C:',(SELECT (x->>'protectionEnabled')::boolean FROM jsonb_array_elements(COALESCE(s.payload->'bitLockerVolumes','[]'))x WHERE upper(x->>'driveLetter')='C:' LIMIT 1)),
              ('Arranque seguro',(s.payload->>'secureBootEnabled')::boolean)
            )v(control,enabled)
            WHERE d.enabled AND d.retired_at_utc IS NULL GROUP BY d.agency_code,v.control ORDER BY d.agency_code,v.control
            """),
        ["priorities"] = new("Expedientes pendientes","CURRENT","operational_cases", """
            SELECT case_number AS "Expediente",title AS "Asunto",agency_code AS "Agencia",severity AS "Severidad",status AS "Estado",
            assigned_to AS "Responsable",due_at_utc AS "Vencimiento"
            FROM operational_cases WHERE (@agency='' OR agency_code=@agency) AND status NOT IN ('CLOSED','ACCEPTED_EXCEPTION')
            ORDER BY due_at_utc NULLS LAST,case_number
            """),
        ["inventory"] = new("Inventario observado","CURRENT","devices, inventory_snapshots", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",d.enabled AS "Habilitado",d.retired_at_utc AS "Retirado",
            d.interactive_user_display_name AS "Usuario observado",d.interactive_user_account AS "Cuenta observada",d.interactive_user_checked_at_utc AS "Usuario comprobado",
            d.agent_version AS "Agente",d.last_seen_at_utc AS "Última comunicación",i.captured_at_utc AS "Inventario capturado",
            i.payload#>>'{computer,manufacturer}' AS "Fabricante",i.payload#>>'{computer,model}' AS "Modelo",
            i.payload#>>'{bios,serialNumber}' AS "Serie",i.payload#>>'{operatingSystem,description}' AS "Sistema operativo",
            i.payload#>>'{processor,name}' AS "Procesador",i.payload#>>'{processor,logicalProcessors}' AS "Procesadores lógicos",
            i.payload#>>'{processor,maxClockMegahertz}' AS "Frecuencia CPU MHz",i.payload#>>'{memory,totalBytes}' AS "RAM bytes"
            FROM fd d LEFT JOIN LATERAL (SELECT * FROM inventory_snapshots WHERE device_id=d.id ORDER BY captured_at_utc DESC,id DESC LIMIT 1) i ON true
            ORDER BY d.agency_code,d.machine_name,d.id
            """),
        ["custody"] = new("Asignación formal y custodia","CURRENT","devices, asset_custody", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",c.state AS "Estado de custodia",c.assigned_to AS "Responsable formal",
            c.custodian AS "Custodio",c.location AS "Ubicación",c.review_due_utc AS "Próxima revisión",c.backups_paused AS "Seguimiento pausado",c.pause_reason AS "Motivo",c.updated_at_utc AS "Actualizado"
            FROM fd d LEFT JOIN asset_custody c ON c.current_device_id=d.id ORDER BY d.machine_name,d.id
            """),
        ["backupProfiles"] = new("Controles de copia","CURRENT","backup_control_profiles, backup_control_observations, backup_runs, nas_appliances", $$"""
            SELECT p.code AS "Control",p.system_name AS "Activo o sistema",p.agency_code AS "Agencia",p.provider AS "Herramienta",p.task_name AS "Nombre de copia",
            p.protected_scope AS "Carpetas protegidas",p.expected_destination AS "Destino",p.frequency_hours AS "Frecuencia horas",p.retention_days AS "Retención configurada días",
            p.restore_test_interval_days AS "Prueba cada días",p.process_owner AS "Responsable del proceso",
            p.enabled AND (p.device_id IS NULL OR COALESCE(d.enabled,false)) AS "Habilitado",
            CASE WHEN p.device_id IS NOT NULL AND NOT COALESCE(d.enabled,false) THEN 'Equipo desvinculado · historial' ELSE p.approval_status END AS "Estado del seguimiento",
            o.observed_at AS "Última evidencia de copia de la asignación actual",o.result AS "Resultado de copia",o.source AS "Fuente",
            COALESCE(c.backups_paused,false) AS "Seguimiento pausado",c.pause_reason AS "Motivo de pausa",c.review_due_utc AS "Revisar custodia",
            CASE WHEN p.device_id IS NOT NULL AND NOT COALESCE(d.enabled,false) THEN 'Equipo desvinculado · historial'
            WHEN COALESCE(c.backups_paused,false) THEN 'Pausado' WHEN NOT p.enabled THEN 'Inactivo'
            WHEN o.observed_at IS NULL THEN 'Sin evidencia' WHEN o.observed_at < @cutoff - make_interval(hours=>p.frequency_hours,mins=>p.grace_minutes) THEN 'Fuera del plazo de copia'
            ELSE 'Dentro del plazo; revisar resultado y destino' END AS "Antigüedad de evidencia",
            r.finished_at_utc AS "Última restauración de la asignación",r.result AS "Resultado de restauración",r.validated_by AS "Validó restauración",
            o.reported_host AS "Servidor informado por la ejecución",o.reported_share AS "Recurso informado por la ejecución",
            o.scope_context AS "Correspondencia del alcance observado"
            FROM backup_control_profiles p LEFT JOIN devices d ON d.id=p.device_id
            LEFT JOIN asset_custody c ON c.current_device_id=p.device_id
            LEFT JOIN nas_appliances n ON n.id=p.nas_appliance_id
            LEFT JOIN LATERAL(
                SELECT x.observed_at,x.result,x.source,x.reported_host,x.reported_share,x.scope_context FROM (
                    SELECT period_end_utc AS observed_at,result,source,created_at_utc AS received,
                           NULL::text AS reported_host,NULL::text AS reported_share,'Evidencia registrada explícitamente para el control'::text AS scope_context
                    FROM backup_control_observations observed WHERE profile_id=p.id
                      AND atlas_backup_evidence_context_matches(p,'OBSERVATION',observed.id,observed.context_key)
                    UNION ALL SELECT b.finished_at_utc,CASE b.status WHEN 'Successful' THEN 'SUCCESS' WHEN 'Warning' THEN 'WARNING' WHEN 'Failed' THEN 'FAILED'
                      WHEN 'Running' THEN CASE WHEN b.finished_at_utc<@cutoff-INTERVAL '10 minutes' THEN 'Unconfirmed' ELSE b.status END ELSE b.status END,
                      'AGENT',COALESCE(b.received_at_utc,b.finished_at_utc),b.payload->>'destinationHost',b.payload->>'destinationShare',
                      CASE WHEN b.payload->'evidence' IS NULL OR b.payload->'evidence'='null'::jsonb THEN 'Reporte histórico sin desglose de carpetas; alcance no verificable'
                        WHEN COALESCE(cardinality(p.protected_folders),0)=0 THEN 'Control sin carpetas estructuradas; alcance no verificable'
                        ELSE 'Nombres de carpetas correspondientes al control; revisar resultado técnico' END
                    FROM backup_runs b WHERE {{RelevantAutomaticRunSql}}
                )x WHERE c.tracking_since_utc IS NULL OR x.observed_at>=c.tracking_since_utc ORDER BY x.observed_at DESC,x.received DESC LIMIT 1
            )o ON true
            LEFT JOIN LATERAL(SELECT * FROM backup_restoration_tests restored WHERE profile_id=p.id
                AND atlas_backup_evidence_context_matches(p,'RESTORATION',restored.id,restored.context_key)
                AND(c.tracking_since_utc IS NULL OR finished_at_utc>=c.tracking_since_utc) ORDER BY finished_at_utc DESC,created_at_utc DESC,id LIMIT 1)r ON true
            WHERE (@agency='' OR p.agency_code=@agency) ORDER BY p.code
            """),
        ["backupObservations"] = new("Evidencias de ejecución del período","EVENTS","backup_control_observations", """
            SELECT p.code AS "Control",p.system_name AS "Activo registrado en el control actual",o.source AS "Fuente",o.result AS "Resultado",o.period_start_utc AS "Inicio del período",o.period_end_utc AS "Fin del período",o.created_at_utc AS "Registrado",o.created_by AS "Registró",
              CASE WHEN atlas_backup_evidence_context_matches(p,'OBSERVATION',o.id,o.context_key) THEN 'Compatible con configuración actual' ELSE 'Histórico; no acredita configuración actual' END AS "Contexto de evidencia"
            FROM backup_control_observations o JOIN backup_control_profiles p ON p.id=o.profile_id
            WHERE (@agency='' OR p.agency_code=@agency) AND o.period_end_utc>=@from AND o.period_end_utc<@to ORDER BY o.period_end_utc,o.id
            """),
        ["automaticBackups"] = new("Ejecuciones automáticas de Atlas Backup","EVENTS","backup_runs", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",b.event_id AS "Ejecución",b.started_at_utc AS "Inicio",
            CASE WHEN b.status<>'Running' THEN b.finished_at_utc END AS "Fin confirmado",
            b.finished_at_utc AS "Última señal del cliente",b.received_at_utc AS "Recibido por ATLAS",
            CASE b.status WHEN 'Successful' THEN 'Completada' WHEN 'Warning' THEN CASE WHEN b.acceptable THEN 'Completada con advertencias · aceptable' ELSE 'Advertencias · alcance sin confirmar' END
              WHEN 'Failed' THEN 'Fallida' WHEN 'Cancelled' THEN 'Cancelada' WHEN 'Skipped' THEN 'No iniciada' WHEN 'Partial' THEN 'Con archivos o carpetas pendientes'
              WHEN 'Running' THEN CASE WHEN b.finished_at_utc<@cutoff-INTERVAL '10 minutes' THEN 'Sin confirmación de cierre' ELSE 'En curso' END ELSE 'Sin confirmación de cierre' END AS "Resultado",
            b.acceptable AS "Cierre sin fallos de archivos según criterio técnico original",
            b.payload->>'destinationHost' AS "Servidor destino",b.payload->>'destinationShare' AS "Recurso destino",
            CASE WHEN b.payload#>>'{evidence,version}'='2' THEN b.payload->>'filesCopied' END AS "Archivos copiados confirmados",
            CASE WHEN b.payload#>>'{evidence,version}'='2' THEN b.payload->>'filesSkipped' END AS "Sin cambios o excluidos confirmados",
            CASE WHEN b.payload#>>'{evidence,version}'='2' AND jsonb_array_length(COALESCE(b.payload#>'{evidence,folders}','[]'::jsonb))>0
              AND NOT EXISTS(SELECT 1 FROM jsonb_array_elements(b.payload#>'{evidence,folders}') f WHERE f->>'filesFailed' IS NULL)
              THEN (SELECT SUM((f->>'filesFailed')::bigint) FROM jsonb_array_elements(b.payload#>'{evidence,folders}') f) END AS "Archivos pendientes confirmados por error",
            CASE WHEN b.payload#>>'{evidence,version}'='2' THEN b.payload->>'bytesCopied' END AS "Bytes copiados confirmados"
            FROM backup_runs b JOIN fd d ON d.id=b.device_id WHERE b.finished_at_utc>=@from AND b.finished_at_utc<@to ORDER BY b.finished_at_utc,b.id
            """),
        ["restorations"] = new("Pruebas de restauración del período","EVENTS","backup_restoration_tests", """
            SELECT p.code AS "Control",r.tested_scope AS "Alcance probado",r.result AS "Resultado",r.integrity_verified AS "Integridad comprobada",r.functionality_verified AS "Funcionalidad comprobada",
            r.rto_achieved_minutes AS "Recuperación minutos",r.executed_by AS "Ejecutó",r.validated_by AS "Validó",r.validated_at_utc AS "Validación",r.finished_at_utc AS "Finalizó",r.next_test_due_utc AS "Próxima prueba",
              CASE WHEN atlas_backup_evidence_context_matches(p,'RESTORATION',r.id,r.context_key) THEN 'Compatible con configuración actual' ELSE 'Histórico; no acredita configuración actual' END AS "Contexto de evidencia"
            FROM backup_restoration_tests r JOIN backup_control_profiles p ON p.id=r.profile_id
            WHERE (@agency='' OR p.agency_code=@agency) AND r.finished_at_utc>=@from AND r.finished_at_utc<@to ORDER BY r.finished_at_utc,r.id
            """),
        ["planDemonstrations"] = new("Demostraciones por plan · muestras y repeticiones del período","EVENTS","backup_plan_demonstrations, backup_plan_demonstration_samples, backup_restoration_tests", $"""
            SELECT d.id AS "Demostración",d.configuration->>'name' AS "Plan al registrar",d.plan_revision AS "Versión del plan",
                d.population_count AS "Equipos del plan al registrar",d.required_samples AS "Muestras requeridas al crear la sesión",
                {Miguel.Server.BackupControl.BackupDemonstrationRules.EffectiveRequiredSql} AS "Muestras requeridas según criterio actual",
                s.snapshot->>'name' AS "Equipo o sistema probado",s.snapshot->>'agency' AS "Agencia",
                s.snapshot->>'destination' AS "Destino probado",s.snapshot->>'scope' AS "Alcance declarado al probar",
                s.repeats_sample_id AS "Repite muestra",r.backup_used_at_utc AS "Copia utilizada",r.tested_scope AS "Restaurado y comprobado",
                r.result AS "Resultado de este intento",r.integrity_verified AS "Integridad comprobada",r.functionality_verified AS "Funcionamiento comprobado",
                r.rto_achieved_minutes AS "Duración minutos",r.finished_at_utc AS "Finalizó",r.executed_by AS "Ejecutó",r.validated_by AS "Validó",r.notes AS "Observaciones"
            FROM backup_plan_demonstrations d JOIN backup_plan_demonstration_samples s ON s.demonstration_id=d.id
            JOIN backup_restoration_tests r ON r.id=s.restoration_id
            WHERE (@agency='' OR s.snapshot->>'agency'=@agency) AND r.finished_at_utc>=@from AND r.finished_at_utc<@to
            ORDER BY r.finished_at_utc,s.attempt_order
            """),
        ["shared"] = new("Equipos de trabajo comunal","CURRENT","devices, nas_appliances", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",d.interactive_user_display_name AS "Usuario observado",
            n.display_name AS "Recurso registrado",n.host AS "Servidor",n.share_name AS "Carpeta de red",n.enabled AS "Recurso habilitado",n.last_probe_status AS "Sondeo",n.last_probe_at_utc AS "Último sondeo"
            FROM fd d LEFT JOIN nas_appliances n ON n.id=d.nas_appliance_id WHERE d.backup_provider='SHARED_FOLDER' ORDER BY d.agency_code,n.display_name,d.machine_name
            """),
        ["posture"] = new("Cobertura de postura de seguridad","CURRENT","devices, security_posture_snapshots", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",s.captured_at_utc AS "Postura capturada",s.received_at_utc AS "Recibida",
            CASE WHEN s.id IS NULL THEN 'Sin reporte' WHEN s.captured_at_utc>@cutoff+INTERVAL '5 minutes' THEN 'Fecha futura; revisar reloj' WHEN s.captured_at_utc<@cutoff-make_interval(hours=>@freshHours) THEN 'Observación antigua; no acredita estado vigente' ELSE 'Observación reciente' END AS "Cobertura",
            (SELECT string_agg((f->>'name')||': '||COALESCE(f->>'enabled','Sin dato'),'; ') FROM jsonb_array_elements(s.payload->'firewallProfiles') f) AS "Perfiles firewall",
            (SELECT string_agg((v->>'driveLetter')||': '||COALESCE(v->>'protectionEnabled','Sin dato'),'; ') FROM jsonb_array_elements(s.payload->'bitLockerVolumes') v) AS "Cifrado por volumen",
            s.payload->>'secureBootEnabled' AS "Arranque seguro",s.payload#>>'{tpm,present}' AS "TPM presente",s.payload->>'pendingReboot' AS "Reinicio pendiente"
            FROM fd d LEFT JOIN LATERAL(SELECT * FROM security_posture_snapshots WHERE device_id=d.id ORDER BY captured_at_utc DESC,id DESC LIMIT 1)s ON true ORDER BY d.machine_name,d.id
            """),
        ["securityEvents"] = new("Eventos de seguridad del período","EVENTS","endpoint_security_events", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",e.occurred_at_utc AS "Ocurrió",e.received_at_utc AS "Recibido",e.event_code AS "Código",e.category AS "Categoría",e.severity AS "Severidad",e.outcome AS "Resultado"
            FROM endpoint_security_events e JOIN fd d ON d.id=e.device_id WHERE e.occurred_at_utc>=@from AND e.occurred_at_utc<@to ORDER BY e.occurred_at_utc,e.id
            """),
        ["software"] = new("Instalaciones observadas","CURRENT","software_effective_inventory", """
            SELECT machine_name AS "Activo",agency_code AS "Agencia",name AS "Producto",publisher AS "Editor",version AS "Versión",authorization_status AS "Autorización",support_ends_on AS "Fin de soporte",license_metric AS "Métrica licencia",inventory_at_utc AS "Inventario",complete_sources AS "Fuentes completas"
            FROM software_effective_inventory WHERE (@agency='' OR agency_code=@agency) ORDER BY machine_name,name,version,installation_key
            """),
        ["vulnerabilityFindings"] = new("Hallazgos y tratamiento · validar aplicabilidad","CURRENT","vulnerability_findings, vulnerability_catalog, vulnerability_kev, vulnerability_epss", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",p.name AS "Producto",f.cve AS "CVE",
            CASE f.state WHEN 'OPEN' THEN 'Por validar' WHEN 'CONFIRMED' THEN 'Afectación validada' WHEN 'IN_PROGRESS' THEN 'En tratamiento'
            WHEN 'RESOLVED' THEN 'Corrección validada' WHEN 'NOT_APPLICABLE' THEN 'No aplica documentado' ELSE 'Excepción documentada' END AS "Seguimiento",
            CASE WHEN v.rejected THEN 'CVE retirado' WHEN NOT d.enabled OR d.retired_at_utc IS NOT NULL THEN 'Activo retirado'
            WHEN f.inventory_at_utc<@cutoff-interval '72 hours' THEN 'Observación antigua'
            WHEN s.captured_at_utc>f.evaluated_at_utc OR v.modified_at_utc>f.evaluated_at_utc THEN 'Pendiente de reevaluar'
            WHEN f.observation='PRESENT' THEN 'Coincidencia observada' WHEN f.observation='NOT_OBSERVED' THEN 'Ya no coincide; requiere validación'
            WHEN f.observation='STALE' THEN 'Observación antigua o parcial' ELSE 'Revisar evidencia' END AS "Observación",
            CASE f.priority WHEN 'URGENT' THEN 'Explotación conocida; priorizar' WHEN 'HIGH' THEN 'Alta' WHEN 'MEDIUM' THEN 'Media' ELSE 'Revisar' END AS "Prioridad operativa",
            v.cvss AS "CVSS",v.cvss_version AS "Versión CVSS",k.cve IS NOT NULL AS "En catálogo KEV local",
            e.score AS "EPSS probabilidad 0 a 1",e.score_date AS "Fecha EPSS",f.inventory_at_utc AS "Inventario observado",
            f.evaluated_at_utc AS "Evaluado",f.owner AS "Responsable",f.due_on AS "Fecha objetivo",f.exception_until AS "Excepción hasta",
            f.notes AS "Análisis",f.reference AS "Referencia",f.validated_by AS "Validación declarada"
            FROM vulnerability_findings f JOIN fd d ON d.id=f.device_id JOIN software_products p ON p.id=f.product_id JOIN vulnerability_catalog v ON v.cve=f.cve
            LEFT JOIN vulnerability_kev k ON k.cve=f.cve LEFT JOIN vulnerability_epss e ON e.cve=f.cve LEFT JOIN software_devices s ON s.device_id=f.device_id
            ORDER BY d.machine_name,f.cve,f.id
            """),
        ["vulnerabilitySources"] = new("Fuentes institucionales · fecha y estado de sincronización","CURRENT","vulnerability_sources", """
            SELECT id AS "Fuente",CASE status WHEN 'OK' THEN 'Actualización correcta' WHEN 'SYNCING' THEN 'Carga parcial en curso' WHEN 'ERROR' THEN 'Error; se conserva carga previa' ELSE 'Pendiente' END AS "Sincronización",
              last_success_at_utc AS "Última carga completa",data_at_utc AS "Fecha de datos",cursor AS "Progreso parcial",total AS "Registros de la consulta",
              error AS "Error técnico",sha256 AS "SHA-256 del último archivo o página" FROM vulnerability_sources ORDER BY id
            """),
        ["vulnerabilityHistory"] = new("Actividad de vulnerabilidades del período","EVENTS","vulnerability_history", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",f.cve AS "CVE",h.occurred_at_utc AS "Fecha",h.actor AS "Cuenta",
              CASE h.action WHEN 'DETECTED' THEN 'Hallazgo registrado' WHEN 'REVIEW' THEN 'Revisión registrada' WHEN 'REOPENED' THEN 'Reabierto por nueva evidencia' ELSE 'Observación actualizada' END AS "Actividad",
              h.detail#>>'{decision,notes}' AS "Análisis registrado",h.detail#>>'{decision,reference}' AS "Referencia de evidencia"
              FROM vulnerability_history h JOIN vulnerability_findings f ON f.id=h.finding_id JOIN fd d ON d.id=f.device_id
              WHERE h.occurred_at_utc>=@from AND h.occurred_at_utc<@to ORDER BY h.occurred_at_utc,h.id
            """),
        ["licenses"] = new("Derechos de licencia institucionales","CURRENT","software_licenses, software_products, software_license_assignments", """
            SELECT p.name AS "Producto",l.reference AS "Referencia",l.metric AS "Métrica",l.quantity AS "Derechos registrados",l.starts_on AS "Desde",l.expires_on AS "Vencimiento",l.active AS "Activo",
            (SELECT COUNT(*) FROM software_license_assignments a WHERE a.license_id=l.id AND a.released_at_utc IS NULL) AS "Asignaciones vigentes"
            FROM software_licenses l JOIN software_products p ON p.id=l.product_id ORDER BY p.name,l.id
            """),
        ["softwareActions"] = new("Acciones de software","CURRENT","software_actions", """
            SELECT a.title AS "Acción",a.owner AS "Responsable",a.due_on AS "Vencimiento",a.status AS "Estado",d.machine_name AS "Activo",d.agency_code AS "Agencia"
            FROM software_actions a LEFT JOIN devices d ON d.id=a.device_id WHERE (@agency='' OR d.agency_code=@agency) ORDER BY a.due_on,a.id
            """),
        ["updaterDevices"] = new("Versiones y comunicación","CURRENT","devices, updater_devices", """
            SELECT d.machine_name AS "Activo",d.agency_code AS "Agencia",d.agent_version AS "Agente",d.backup_client_version AS "Cliente de copias",u.updater_version AS "Actualizador",u.last_check_at_utc AS "Última comprobación",d.enabled AS "Habilitado"
            FROM fd d LEFT JOIN updater_devices u ON u.device_id=d.id ORDER BY d.machine_name,d.id
            """),
        ["updaterAttempts"] = new("Intentos de actualización iniciados en el período","EVENTS","updater_attempts", """
            SELECT d.machine_name AS "Activo",a.release_id AS "Versión publicada ID",a.phase AS "Fase actual",a.code AS "Resultado actual",a.created_at_utc AS "Iniciado",a.updated_at_utc AS "Último cambio",a.confirmed_at_utc AS "Confirmado"
            FROM updater_attempts a JOIN fd d ON d.id=a.device_id WHERE a.created_at_utc>=@from AND a.created_at_utc<@to ORDER BY a.created_at_utc,a.attempt_id
            """),
        ["nas"] = new("Recursos NAS","CURRENT","nas_appliances", """
            SELECT display_name AS "Recurso",agency_code AS "Agencia",host AS "Servidor",share_name AS "Carpeta",enabled AS "Activo",last_probe_status AS "Estado",last_probe_at_utc AS "Sondeo",total_bytes AS "Capacidad bytes",free_bytes AS "Libre bytes"
            FROM nas_appliances WHERE (@agency='' OR agency_code=@agency) ORDER BY agency_code,display_name,id
            """),
        ["nasHistory"] = new("Sondeos NAS del período","EVENTS","nas_probe_history", """
            SELECT n.display_name AS "Recurso",n.agency_code AS "Agencia",h.probed_at_utc AS "Sondeo",h.status AS "Estado",h.total_bytes AS "Capacidad bytes",h.free_bytes AS "Libre bytes",h.latency_ms AS "Latencia ms"
            FROM nas_probe_history h JOIN nas_appliances n ON n.id=h.nas_appliance_id WHERE (@agency='' OR n.agency_code=@agency) AND h.probed_at_utc>=@from AND h.probed_at_utc<@to ORDER BY h.probed_at_utc,h.id
            """),
        ["training"] = new("Sesiones del período","EVENTS","training_sessions, training_cycles", """
            SELECT c.name AS "Ciclo",s.title AS "Sesión",s.scheduled_date AS "Fecha",s.status AS "Estado",s.trainer AS "Facilitador",s.duration_minutes AS "Duración minutos"
            FROM training_sessions s JOIN training_cycles c ON c.id=s.cycle_id WHERE s.scheduled_date>=@fromDate AND s.scheduled_date<=@toDate ORDER BY s.scheduled_date,s.id
            """),
        ["attendance"] = new("Asistencia registrada en sesiones del período","EVENTS","training_session_attendance, training_sessions", """
            SELECT s.title AS "Sesión",s.scheduled_date AS "Fecha",a.display_name AS "Participante",a.agency_label AS "Agencia",a.department AS "Área",a.status AS "Asistencia",a.recorded_at_utc AS "Registrada"
            FROM training_session_attendance a JOIN training_sessions s ON s.id=a.session_id WHERE s.scheduled_date>=@fromDate AND s.scheduled_date<=@toDate ORDER BY s.scheduled_date,s.id,a.participant_id
            """),
        ["documents"] = new("Documentos institucionales","CURRENT","governance_documents", """
            SELECT d.code AS "Código",d.title AS "Documento",d.document_type AS "Tipo",d.status AS "Estado",d.owner_name AS "Responsable",d.approver_name AS "Aprobador registrado",d.effective_date AS "Vigencia registrada",d.next_review_date AS "Próxima revisión",
              CASE WHEN d.status<>'APPROVED' THEN 'Sin aprobación vigente'
                WHEN upper(v.sha256)='A0E64776EAFD5D7598810B16124AF4E630C092282CA996AF052C0AF9385EB088' THEN
                  CASE WHEN @policyDate<DATE '2027-06-01' THEN 'Aplicación anticipada; vigencia formal 2027-06-01' ELSE 'Vigente; vigencia formal 2027-06-01' END
                WHEN d.effective_date IS NULL THEN 'Vigencia por registrar'
                WHEN d.effective_date>@policyDate THEN 'Aprobado; vigencia formal programada'
                ELSE 'Vigente según fecha registrada' END AS "Aplicabilidad al generar"
            FROM governance_documents d LEFT JOIN governance_document_versions v ON v.document_id=d.id AND v.is_current ORDER BY d.code
            """),
        ["cases"] = new("Expedientes institucionales","CURRENT","operational_cases", """
            SELECT case_number AS "Expediente",title AS "Asunto",case_type AS "Tipo",agency_code AS "Agencia",severity AS "Severidad",status AS "Estado",assigned_to AS "Responsable",due_at_utc AS "Vencimiento",created_at_utc AS "Creado",closed_at_utc AS "Cerrado"
            FROM operational_cases ORDER BY case_number
            """),
        ["audit"] = new("Actividad administrativa del período","EVENTS","audit_events (solo metadatos; detalles excluidos)", """
            SELECT occurred_at_utc AS "Fecha",actor_id AS "Actor",actor_type AS "Tipo de actor",action AS "Acción",target_type AS "Objeto",target_id AS "Referencia"
            FROM audit_events WHERE occurred_at_utc>=@from AND occurred_at_utc<@to ORDER BY occurred_at_utc,id
            """)
    };

    public static ReportRequest Normalize(ReportRequest request, GeneralSettings settings, DateTimeOffset now)
    {
        var template = Templates.SingleOrDefault(x=>x.Id==request.Template) ?? throw new PortalException("Plantilla desconocida.");
        string agency = request.Agency?.Trim().ToUpperInvariant() ?? "";
        if(agency.Length>64 || agency.Any(c=>!char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')) throw new PortalException("Agencia no válida.");
        if(!template.AgencyFilter && agency.Length>0) throw new PortalException("Esta plantilla es institucional y no admite filtro por agencia.");
        var today=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now,ConfigurationService.Zone(settings.ReportTimeZone)).DateTime);
        var to=request.To??today; var from=request.From??to.AddDays(1-settings.DefaultPeriodDays);
        if(from>to || to>today || to.DayNumber-from.DayNumber>365 || from.Year<2000) throw new PortalException("Seleccione un período de hasta 366 días, sin fechas futuras.");
        if(request.View != "TECHNICAL") throw new PortalException("Seleccione la plantilla Resumen ejecutivo para esa presentación.");
        return request with { Agency=agency,From=from,To=to };
    }
    public async Task<ReportSnapshot> CaptureAsync(Guid id,ReportRequest request,GeneralSettings settings,string author,CancellationToken ct)
    {
        DateTimeOffset cutoff=clock.GetUtcNow(); request=Normalize(request,settings,cutoff);
        var zone=ConfigurationService.Zone(settings.ReportTimeZone);
        var from=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(request.From!.Value.ToDateTime(TimeOnly.MinValue),zone));
        var to=new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(request.To!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),zone));
        var template=Templates.Single(x=>x.Id==request.Template);
        await using var c=await db.OpenConnectionAsync(ct);
        await using var t=await c.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);
        List<ReportTable> tables=[]; int total=0;
        foreach(string sectionId in template.Sections)
        {
            if(sectionId=="backupCompliance")
            {
                var evaluated=await new BackupControlRepository(db,clock).ReadDashboardAsync(ct);
                var selected=evaluated.Profiles.Where(p=>request.Agency==""||p.AgencyCode==request.Agency);
                List<string[]> evaluatedRows;
                string[] columns;
                if(template.Id=="executive")
                {
                    columns=["Agencia","Herramienta","Controles activos sin pausa","Copias aceptables vigentes","Pendientes de copia en plazo","Por revisar","Calendario sin configurar"];
                    evaluatedRows=selected.Where(p=>p.Enabled&&!p.TrackingPaused&&!p.DeviceRetired&&BackupApprovalStatuses.IsActive(p.ApprovalStatus))
                        .GroupBy(p=>new{p.AgencyCode,p.Provider}).OrderBy(g=>g.Key.AgencyCode).ThenBy(g=>g.Key.Provider)
                        .Select(g=>new[]{g.Key.AgencyCode,g.Key.Provider,g.Count().ToString(CultureInfo.InvariantCulture),
                            g.Count(p=>p.Compliance is BackupControlCompliance.Compliant or BackupControlCompliance.RestoreTestDue).ToString(CultureInfo.InvariantCulture),
                            g.Count(p=>p.Compliance==BackupControlCompliance.Scheduled).ToString(CultureInfo.InvariantCulture),
                            g.Count(p=>p.Compliance is not(BackupControlCompliance.Compliant or BackupControlCompliance.RestoreTestDue or BackupControlCompliance.Scheduled)).ToString(CultureInfo.InvariantCulture),
                            g.Count(p=>p.ScheduleStatus?.ConfigurationPending==true).ToString(CultureInfo.InvariantCulture)}).ToList();
                }
                else
                {
                    columns=["Control","Activo o sistema","Agencia","Estado operativo","Motivo de evaluación","Última copia aceptable","Resultado aceptado","Destino autorizado","Contexto de evidencia","Calendario","Último día exigible","Corte de evaluación"];
                    evaluatedRows=selected.OrderBy(p=>p.Code).Select(p=>new[]{p.Code,p.SystemName,p.AgencyCode,FriendlyState(p.Compliance),
                        p.ComplianceDetail,p.LastAcceptableAtUtc.HasValue?Cell(p.LastAcceptableAtUtc.Value,zone):"Sin evidencia compatible",
                        FriendlyState(p.LastAcceptableResult??"UNKNOWN"),p.ExpectedDestination??(p.NasHost is null?"Sin destino":$"\\\\{p.NasHost}\\{p.NasShareName}"),
                        p.ObservationContextDetail??p.ObservationContextStatus??"Consultar fuente",
                        p.ScheduleStatus?.Mode=="CALENDAR"?$"Días locales completos · {p.ScheduleStatus.TimeZone}":"Frecuencia y margen en horas",
                        p.ScheduleStatus?.ObligationDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)??"Sin obligación cerrada de calendario",
                        Cell(evaluated.GeneratedAtUtc,zone)}).ToList();
                }
                total+=evaluatedRows.Count;
                if(total>10000)throw new PortalException("Más de 10,000 filas. Reduzca el período o seleccione una agencia.");
                tables.Add(new("Estado operativo de copias · evaluación por control","CURRENT",
                    "BackupControlRepository · mismo motor que portal y alertas · corte "+evaluated.GeneratedAtUtc.ToString("O"),columns,evaluatedRows));
                continue;
            }
            var s=Sections[sectionId];
            // The selected columns are an allowlist. Payloads, credentials and audit detail are never exported wholesale.
            await using var cmd=new NpgsqlCommand("WITH fd AS (SELECT * FROM devices WHERE (@agency='' OR agency_code=@agency)) "+s.Sql+" LIMIT 10001",c,t) { CommandTimeout=60 };
            cmd.Parameters.AddWithValue("agency",request.Agency!); cmd.Parameters.AddWithValue("from",from); cmd.Parameters.AddWithValue("to",to);
            cmd.Parameters.AddWithValue("cutoff",cutoff); cmd.Parameters.AddWithValue("fromDate",request.From.Value); cmd.Parameters.AddWithValue("toDate",request.To.Value);
            cmd.Parameters.AddWithValue("freshHours",Math.Clamp(options?.Value.SecurityPostureFreshnessHours??24,1,8760));
            cmd.Parameters.AddWithValue("policyDate",DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(cutoff,ConfigurationService.Zone("America/Guatemala")).DateTime));
            await using var r=await cmd.ExecuteReaderAsync(ct);
            List<string[]> rows=[];
            while(await r.ReadAsync(ct))
            {
                if(++total>10000) throw new PortalException("Más de 10,000 filas. Reduzca el período o seleccione una agencia.");
                string[] cells=new string[r.FieldCount];
                for(int i=0;i<cells.Length;i++) cells[i]=r.IsDBNull(i)?"Sin dato":Cell(r.GetValue(i),zone,r.GetName(i));
                rows.Add(cells);
            }
            tables.Add(new(s.Title,s.Basis,s.Source,Enumerable.Range(0,r.FieldCount).Select(r.GetName).ToArray(),rows));
        }
        await t.CommitAsync(ct);
        return new(id,template.Title,template.Id == "executive" ? 5 : template.Id == "backups" ? 4 : 2,request,cutoff,from,to,zone.Id,settings.Institution,settings.Classification,author,template.Policy,
            ["Estado actual: fotografía al generar. No reconstruye el estado de activos, responsables o permisos en fechas pasadas.",
             "Eventos: intervalo consultado inclusivo por fecha local; datos disponibles al corte. Agencia y nombres corresponden al registro actual.",
             "Sin dato no significa cumplimiento. Una copia exitosa no demuestra restauración. Instalación no demuestra uso ni derecho de licencia.",
             "Resumen de copias: último resultado observado, no cumplimiento integral; excluye controles inactivos o pausados. Las evidencias fuera de plazo pueden pertenecer a cualquier resultado. Revise destino y restauración en el informe de copias.",
             "La tabla Estado operativo de copias usa el mismo evaluador de contexto, alcance y calendario que el portal y muestra su propio corte. La antigüedad de evidencia de las tablas técnicas se expresa en horas; no sustituye ese estado operativo. Un control pendiente dentro del día local se identifica aparte y no se suma a copias ya realizadas.",
             "La atribución automática por control requiere que el servidor y recurso reportados correspondan al NAS autorizado y, cuando existe un alcance estructurado y desglose, que sus carpetas estén identificadas. El historial de ejecuciones conserva aparte otros trabajos del mismo equipo. Los alcances históricos o sin configurar se identifican como no verificables.",
             "Los resultados técnicos originales y los archivos pendientes se conservan. Una ejecución terminada con pendientes identificados puede ser aceptable según el criterio operativo configurado en Control de respaldos; un resultado técnico con errores no equivale por sí solo a una copia vencida. La aceptación requiere comprobar alcance, destino y plazo. No garantiza que los pendientes se copiarán en el siguiente intento.",
             "Resumen de seguridad: valores observados, no certificación de protección; los reportes antiguos se identifican aparte y no cambian su resultado histórico.",
             "Vulnerabilidades: coincidencias de inventario pendientes de validar condiciones, no prueba de explotación. EPSS es una estimación global con fecha, no la probabilidad de ataque a este equipo. Sin coincidencias o sin datos no equivale a ausencia de vulnerabilidades.",
             "Los derechos de licencia son institucionales aunque las instalaciones se filtren por agencia. Los intentos muestran su último estado al corte.",
             "Retención institucional de copias: seis meses (V4 pp. 9–10). Los valores técnicos de retención no cambian esta obligación.",
             "Política V4: aplicación anticipada; vigencia formal 1 de junio de 2027. La huella SHA-256 acredita integridad, no aprobación."],tables);
    }
    private static string FriendlyState(string value) => value.ToUpperInvariant() switch
    {
        "HIGH" => "Alta", "CRITICAL" => "Crítica", "MEDIUM" => "Media", "LOW" => "Baja", "INFO" => "Informativa",
        "EVALUATING" => "En evaluación", "ASSIGNED" => "Asignado",
        "NEW" => "Nuevo", "IN_REVIEW" => "En revisión", "IN_TREATMENT" => "En tratamiento",
        "PENDING_VALIDATION" => "Pendiente de validación", "CLOSED" => "Cerrado",
        "ACCEPTED_EXCEPTION" => "Excepción aceptada", "SUCCESS" or "SUCCESSFUL" => "Correcto",
        "FAILED" => "Fallido", "WARNING" => "Con advertencias", "CANCELLED" => "Cancelado",
        "PARTIAL" => "Con archivos o carpetas pendientes", "SKIPPED" => "No iniciada", "RUNNING" => "En curso", "UNCONFIRMED" => "Sin confirmación de cierre",
        "UNKNOWN" => "Sin determinar", "OPEN" => "Pendiente", "IN_PROGRESS" => "En curso",
        "APPROVED" => "Aprobado", "DRAFT" => "Borrador", "OBSOLETE" => "Obsoleto",
        "COMPLIANT" => "Copia aceptable vigente", "RESTORE_TEST_DUE" => "Copia vigente; restauración pendiente",
        "SCHEDULED" => "Pendiente de copia dentro del plazo", "OVERDUE" => "Sin copia aceptable del plazo esperado",
        "NOT_CONFIRMED" => "Copia sin confirmar", "DESTINATION_MISMATCH" => "Destino por revisar",
        "CONFIGURATION_REQUIRED" => "Configuración por completar", "EXCEPTION_EXPIRED" => "Excepción vencida",
        "PAUSED" => "Seguimiento pausado", "PAUSE_REVIEW_DUE" => "Pausa por revisar", "INACTIVE" => "Inactivo",
        "PENDING_APPROVAL" => "Pendiente de aprobación",
        "RESOLVED" => "Resuelto", _ => value
    };
    internal static string Cell(object value,TimeZoneInfo zone,string column = "") => value switch {
        string text when column is "Severidad" or "Estado" or "Resultado" or "Resultado de copia" or "Resultado de restauración" => FriendlyState(text),
        DateTime d=>TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(d,DateTimeKind.Utc),zone).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture),
        DateTimeOffset d=>TimeZoneInfo.ConvertTime(d,zone).ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture),
        DateOnly d=>d.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture), bool b=>b?"Sí":"No",
        IFormattable f=>f.ToString(null,CultureInfo.InvariantCulture)??"", _=>value.ToString()??"" };
}
