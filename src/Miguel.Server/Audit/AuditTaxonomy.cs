namespace Miguel.Server.Audit;

public static class AuditTaxonomy
{
    public static string GetModule(string action)
    {
        if (action.StartsWith("admin.login", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("logout", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("account", StringComparison.OrdinalIgnoreCase) || action.StartsWith("admin.mfa.",StringComparison.Ordinal))
        {
            return "ACCESS";
        }

        if (action.Contains("installer", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("enrollment", StringComparison.OrdinalIgnoreCase))
        {
            return "ENROLLMENT";
        }

        if(action.StartsWith("admin.report.",StringComparison.Ordinal))return "REPORTS";
        if(action.StartsWith("admin.configuration.",StringComparison.Ordinal)||action.StartsWith("admin.agent_policy.",StringComparison.Ordinal)||action.StartsWith("admin.evidence.hold.",StringComparison.Ordinal)||action.StartsWith("admin.notification.",StringComparison.Ordinal))return "CONFIGURATION";
        if (action.StartsWith("admin.audit.", StringComparison.Ordinal)) return "AUDIT";
        if (action.Contains("software", StringComparison.OrdinalIgnoreCase)) return "SOFTWARE";
        if (action.Contains("backup_control", StringComparison.OrdinalIgnoreCase)) return "BACKUPS";
        if (action.Contains("release", StringComparison.OrdinalIgnoreCase)) return "RELEASES";
        if (action.Contains("governance", StringComparison.OrdinalIgnoreCase)) return "GOVERNANCE";
        if (action.Contains("training", StringComparison.OrdinalIgnoreCase)) return "TRAINING";
        if (action.Contains("case", StringComparison.OrdinalIgnoreCase)) return "CASES";
        if (action.Contains("network", StringComparison.OrdinalIgnoreCase)) return "NETWORK";
        if (action.Contains("nas", StringComparison.OrdinalIgnoreCase)) return "NAS";
        if (action.Contains("agency", StringComparison.OrdinalIgnoreCase)) return "AGENCIES";
        if (action.Contains("device", StringComparison.OrdinalIgnoreCase) ||
            action.Contains("inventory", StringComparison.OrdinalIgnoreCase)) return "ASSETS";
        return "OTHER";
    }

    private static readonly HashSet<string> CompletedActions = ["admin.report.queued","admin.report.generated","admin.report.downloaded","admin.report.cancelled","admin.report.variant.saved","admin.report.schedule.created","admin.report.schedule.changed","admin.report.schedule.queued","admin.configuration.changed","admin.configuration.retention.changed","admin.agent_policy.published","admin.evidence.hold.created","admin.evidence.hold.released","admin.notification.muted","admin.notification.unmuted","admin.account.updated","admin.account.password.reset","admin.account.sessions.revoked","admin.account.identity.confirmed","admin.mfa.enrollment.started","admin.mfa.enabled","admin.mfa.disabled","admin.mfa.recovery.used","admin.account.created",
        "admin.software.product.created", "admin.software.product.saved", "admin.software.rule.saved",
        "admin.software.license.saved", "admin.software.license.assigned", "admin.software.license.released",
        "admin.software.action.saved", "admin.software.review.recorded", "admin.software.report.exported",
        "admin.software.vulnerability.mapping.saved", "admin.software.vulnerability.reviewed", "admin.software.vulnerability.exported",
        "admin.device.custody.link_identity",
        "admin.device.custody.register",
        "admin.device.custody.return",
        "admin.device.custody.start_preparation",
        "admin.device.custody.save_preparation",
        "admin.device.custody.make_available",
        "admin.device.custody.reopen_preparation",
        "admin.device.custody.review_custody",
        "admin.device.custody.assign",
        "admin.agency.created",
        "admin.agency.updated",
        "admin.installer.generated",
        "device.enrollment.succeeded",
        "admin.login.succeeded",
        "admin.logout",
        "admin.audit.exported",
        "admin.backup_control.activity.recorded",
        "admin.backup_control.acronis.imported",
        "admin.backup_control.acronis.reviewed",
        "admin.backup_control.acronis.supplemented",
        "admin.backup_control.demonstration.created",
        "admin.backup_control.demonstration.sample_added",
        "admin.backup_control.demonstration.retried",
        "admin.backup_control.evidence.uploaded",
        "admin.backup_control.observation.created",
        "admin.backup_control.plan.applied",
        "admin.backup_control.plan.saved",
        "admin.backup_control.profile.activated",
        "admin.backup_control.profile.created",
        "admin.backup_control.profile.updated",
        "admin.backup_control.restoration.created",
        "admin.case.created",
        "admin.case.updated",
        "admin.case.commented",
        "admin.device.backup_policy.updated",
        "admin.device.retired",
        "admin.device.update_ring.updated",
        "admin.governance.document.approved",
        "admin.governance.document.correction_requested",
        "admin.governance.document.created",
        "admin.governance.document.description_updated",
        "admin.governance.document.status_changed",
        "admin.governance.document.updated",
        "admin.governance.version.downloaded",
        "admin.governance.version.previewed",
        "admin.governance.version.uploaded",
        "admin.inventory.exported",
        "admin.nas.browsed",
        "admin.nas.created",
        "admin.nas.retired",
        "admin.nas.reactivated",
        "admin.nas.mount.provisioned",
        "admin.nas.updated",
        "admin.network.asset.classified",
        "admin.network.scope.configured", "admin.network.scan.completed",
        "admin.release.activated",
        "admin.release.policy.saved",
        "admin.release.device_retry.authorized",
        "admin.release.automatically_paused",
        "admin.release.control.registered",
        "admin.release.report.exported",
        "admin.release.deployment.paused",
        "admin.release.deployment.removed",
        "admin.release.deployment.resumed",
        "admin.release.deployment.updated",
        "admin.release.uploaded",
        "admin.training.attendance.imported",
        "admin.training.cycle.cloned",
        "admin.training.cycle.created",
        "admin.training.cycle.updated",
        "admin.training.participant.created",
        "admin.training.participant.updated",
        "admin.training.session.created",
        "admin.training.session.deleted",
        "admin.training.session.updated",
        "admin.training.topic.created",
        "admin.training.topic.updated"];
    private static readonly HashSet<string> RejectedActions = ["admin.mfa.verification.failed","admin.login.failed",
        "device.enrollment.rejected",
        "device.enrollment.invalid"];
    public static string GetOutcome(string action) => action=="admin.network.scan.failed"?"FAILED":
        action is "admin.network.scan.partial" or "admin.network.scan.interrupted" or "admin.network.scan.cancelled"?"WARNING":
        RejectedActions.Contains(action) ? "FAILED" :
        action is "admin.network.scan.queued" or "admin.network.scan.retry_queued" ? "REQUESTED" : CompletedActions.Contains(action) ? "SUCCEEDED" : "UNKNOWN";

    // The same explicit catalog drives list filters, details, exports and counts.
    internal static string OutcomeSql(string alias) {
        string List(IEnumerable<string> values) => string.Join(",", values.Select(v => "'" + v.Replace("'", "''") + "'"));
        string status = $"upper(COALESCE({alias}.detail->>'status', {alias}.detail#>>'{{detail,status}}', ''))";
        return $"CASE WHEN {alias}.action='admin.nas.probed' THEN CASE {status} WHEN 'ONLINE' THEN 'SUCCEEDED' WHEN 'OFFLINE' THEN 'FAILED' WHEN 'DEGRADED' THEN 'WARNING' ELSE 'UNKNOWN' END " +
            $"WHEN {alias}.action='admin.network.scan.failed' THEN 'FAILED' WHEN {alias}.action IN ('admin.network.scan.partial','admin.network.scan.interrupted','admin.network.scan.cancelled') THEN 'WARNING' " +
            $"WHEN {alias}.action IN ({List(RejectedActions)}) THEN 'FAILED' WHEN {alias}.action IN ('admin.network.scan.queued','admin.network.scan.retry_queued') THEN 'REQUESTED' " +
            $"WHEN {alias}.action IN ({List(CompletedActions)}) THEN 'SUCCEEDED' ELSE 'UNKNOWN' END";
    }

    public static string GetModuleLabel(string module) => module switch
    {
        "REPORTS" => "Reportes",
        "CONFIGURATION" => "Configuración",
        "ACCESS" => "Acceso",
        "AUDIT" => "Auditoría",
        "ENROLLMENT" => "Vinculación",
        "ASSETS" => "Activos",
        "AGENCIES" => "Agencias",
        "NAS" => "NAS",
        "NETWORK" => "Redes",
        "BACKUPS" => "Respaldos",
        "SOFTWARE" => "Software",
        "RELEASES" => "Actualizaciones",
        "CASES" => "Expedientes",
        "TRAINING" => "Capacitaciones",
        "GOVERNANCE" => "Gobierno documental",
        _ => "Otros"
    };

    public static string GetActionLabel(string action) => action switch
    {
        "admin.report.queued" => "Generación de informe solicitada",
        "admin.report.generated" => "Informe generado y conservado",
        "admin.report.downloaded" => "Informe descargado y verificado",
        "admin.report.cancelled" => "Generación cancelada",
        "admin.report.variant.saved" => "Filtros de informe guardados",
        "admin.report.schedule.created" => "Programación de informe creada",
        "admin.report.schedule.changed" => "Programación de informe actualizada",
        "admin.report.schedule.queued" => "Informe programado en cola",
        "admin.configuration.changed" => "Configuración institucional actualizada",
        "admin.configuration.retention.changed" => "Retención técnica actualizada",
        "admin.agent_policy.published" => "Política de agente publicada",
        "admin.evidence.hold.created" => "Evidencia protegida de eliminación",
        "admin.evidence.hold.released" => "Protección de evidencia finalizada",
        "admin.notification.muted" => "Avisos silenciados temporalmente",
        "admin.notification.unmuted" => "Avisos reactivados",
        "admin.account.updated" => "Cuenta administrativa actualizada",
        "admin.account.password.reset" => "Contraseña administrativa restablecida",
        "admin.account.sessions.revoked" => "Sesiones revocadas",
        "admin.account.identity.confirmed" => "Identidad comprobada nuevamente",
        "admin.mfa.enrollment.started" => "Preparación del segundo factor iniciada",
        "admin.mfa.enabled" => "Segundo factor habilitado",
        "admin.mfa.disabled" => "Segundo factor deshabilitado",
        "admin.mfa.recovery.used" => "Código de recuperación utilizado",
        "admin.mfa.verification.failed" => "Comprobación de segundo factor rechazada",

        "admin.software.product.created" => "Producto agregado al catálogo",
        "admin.software.product.saved" => "Clasificación de software actualizada",
        "admin.software.rule.saved" => "Regla de autorización o soporte registrada",
        "admin.software.license.saved" => "Licencia de software registrada o actualizada",
        "admin.software.license.assigned" => "Licencia de software asignada",
        "admin.software.license.released" => "Asignación de licencia liberada",
        "admin.software.action.saved" => "Acción de software registrada o actualizada",
        "admin.software.review.recorded" => "Revisión semestral de software registrada",
        "admin.software.report.exported" => "Informe o evidencia de software exportado",
        "admin.software.vulnerability.mapping.saved" => "Relación de producto con catálogo de vulnerabilidades registrada",
        "admin.software.vulnerability.reviewed" => "Seguimiento de vulnerabilidad registrado",
        "admin.software.vulnerability.exported" => "Evidencia de vulnerabilidad exportada",
        "admin.account.created" => "Cuenta administrativa creada",
        "admin.agency.created" => "Agencia registrada",
        "admin.agency.updated" => "Agencia actualizada",
        "admin.installer.generated" => "Instalador de vinculación emitido",
        "device.enrollment.succeeded" => "Activo vinculado con instalador",
        "device.enrollment.rejected" => "Vinculación rechazada",
        "device.enrollment.invalid" => "Solicitud de vinculación inválida",
        "admin.login.succeeded" => "Inicio de sesión correcto",
        "admin.login.failed" => "Inicio de sesión rechazado",
        "admin.logout" => "Cierre de sesión",
        "admin.audit.exported" => "Auditoría exportada",
        "admin.backup_control.activity.recorded" => "Actividad de respaldo registrada",
        "admin.backup_control.acronis.imported" => "Informe general de Acronis conservado y comparado",
        "admin.backup_control.acronis.reviewed" => "Evidencia de Acronis revisada y copias seleccionadas registradas",
        "admin.backup_control.acronis.supplemented" => "Copias añadidas a un informe de Acronis revisado",
        "admin.backup_control.demonstration.created" => "Demostración de restauración del plan registrada",
        "admin.backup_control.demonstration.sample_added" => "Muestra añadida a una demostración de restauración",
        "admin.backup_control.demonstration.retried" => "Muestra de restauración repetida con historial",
        "admin.backup_control.evidence.uploaded" => "Evidencia de respaldo incorporada",
        "admin.backup_control.observation.created" => "Resultado de respaldo registrado",
        "admin.backup_control.plan.applied" => "Plan de respaldo aplicado",
        "admin.backup_control.plan.saved" => "Plan de respaldo guardado",
        "admin.backup_control.profile.activated" => "Control de respaldo activado",
        "admin.backup_control.profile.created" => "Control de respaldo creado",
        "admin.backup_control.profile.updated" => "Control de respaldo actualizado",
        "admin.backup_control.restoration.created" => "Prueba de restauración registrada",
        "admin.case.created" => "Expediente creado",
        "admin.case.updated" => "Expediente actualizado",
        "admin.case.commented" => "Seguimiento añadido al expediente",
        "admin.device.backup_policy.updated" => "Política de respaldo del activo actualizada",
        "admin.device.custody.link_identity" => "Identidad reinstalada vinculada al activo",
        "admin.device.custody.register" => "Asignación actual registrada",
        "admin.device.custody.return" => "Devolución del activo registrada",
        "admin.device.custody.start_preparation" => "Preparación del activo iniciada",
        "admin.device.custody.save_preparation" => "Avance de preparación registrado",
        "admin.device.custody.make_available" => "Preparación validada; activo disponible",
        "admin.device.custody.reopen_preparation" => "Preparación del activo reabierta",
        "admin.device.custody.review_custody" => "Custodia y fecha de revisión actualizadas",
        "admin.device.custody.assign" => "Nueva entrega del activo registrada",
        "admin.device.retired" => "Activo retirado",
        "admin.device.update_ring.updated" => "Anillo de actualización modificado",
        "admin.governance.document.approved" => "Documento aprobado como oficial",
        "admin.governance.document.correction_requested" => "Corrección documental solicitada",
        "admin.governance.document.created" => "Ficha documental creada",
        "admin.governance.document.description_updated" => "Descripción documental actualizada",
        "admin.governance.document.status_changed" => "Estado documental actualizado",
        "admin.governance.document.updated" => "Ficha documental actualizada",
        "admin.governance.version.downloaded" => "Documento descargado",
        "admin.governance.version.previewed" => "Documento consultado",
        "admin.governance.version.uploaded" => "Versión documental incorporada",
        "admin.inventory.exported" => "Inventario exportado",
        "admin.nas.browsed" => "Contenido del NAS consultado",
        "admin.nas.created" => "NAS registrado",
        "admin.nas.retired" => "NAS dado de baja",
        "admin.nas.reactivated" => "NAS reactivado",
        "admin.nas.mount.provisioned" => "Montaje de NAS preparado",
        "admin.nas.probed" => "Conectividad del NAS verificada",
        "admin.nas.updated" => "NAS actualizado",
        "admin.network.asset.classified" => "Activo de red clasificado",
        "admin.network.scan.queued" => "Descubrimiento de red solicitado",
        "admin.network.scope.configured" => "Programación de red configurada",
        "admin.network.scan.completed" => "Sondeo de red completado",
        "admin.network.scan.partial" => "Sondeo de red con resultado parcial",
        "admin.network.scan.failed" => "Sondeo de red fallido",
        "admin.network.scan.retry_queued" => "Reintento de sondeo programado",
        "admin.network.scan.interrupted" => "Sondeo de red interrumpido",
        "admin.network.scan.cancelled" => "Sondeo de red cancelado",
        "admin.release.activated" => "Versión activada para distribución",
        "admin.release.policy.saved" => "Condiciones del despliegue guardadas",
        "admin.release.device_retry.authorized" => "Reintento de actualización autorizado",
        "admin.release.automatically_paused" => "Despliegue detenido automáticamente por fallos",
        "admin.release.control.registered" => "Autorización firmada registrada",
        "admin.release.report.exported" => "Seguimiento de actualizaciones exportado",
        "admin.release.deployment.paused" => "Despliegue de versión pausado",
        "admin.release.deployment.removed" => "Despliegue de versión retirado",
        "admin.release.deployment.resumed" => "Despliegue de versión reanudado",
        "admin.release.deployment.updated" => "Despliegue de versión actualizado",
        "admin.release.uploaded" => "Paquete de actualización incorporado",
        "admin.training.attendance.imported" => "Asistencia de capacitación importada",
        "admin.training.cycle.cloned" => "Ciclo de capacitación replicado",
        "admin.training.cycle.created" => "Ciclo de capacitación creado",
        "admin.training.cycle.updated" => "Ciclo de capacitación actualizado",
        "admin.training.participant.created" => "Participante registrado",
        "admin.training.participant.updated" => "Participante actualizado",
        "admin.training.session.created" => "Sesión de capacitación creada",
        "admin.training.session.deleted" => "Sesión de capacitación eliminada",
        "admin.training.session.updated" => "Sesión de capacitación actualizada",
        "admin.training.topic.created" => "Tema de capacitación creado",
        "admin.training.topic.updated" => "Tema de capacitación actualizado",
        _ => "Actividad administrativa registrada"
    };
}
