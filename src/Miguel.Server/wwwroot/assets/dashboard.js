"use strict";

const state = {
  session: null,
  summary: null,
  security: null,
  notifications: {
    page: 1,
    pageSize: 20,
    total: 0,
    unreadCount: 0,
    items: []
  },
  network: null,
  networkPage: { page: 1, pageSize: 25, scope: "", search: "" },
  agencies: [],
  nas: [],
  backupCoverage: [],
  backupCoveragePage: { page: 1, pageSize: 20 },
  backupCoverageSort: { field: "machineName", direction: "asc" },
  backupControl: {
    generatedAtUtc: null,
    summary: {
      total: 0, compliant: 0, attention: 0, failed: 0, overdue: 0,
      restoreTestsDue: 0, pendingApproval: 0, coveragePercentage: 0
    },
    profiles: [],
    standards: [],
    devices: [],
    nas: []
  },
  backupControlUi: {
    selectedId: null,
    detail: null,
    editingId: null,
    fieldOrigins: {},
    applyingTemplate: false
  },
  nasBrowser: {
    nasId: "",
    path: "",
    parentPath: null,
    page: 1,
    pageSize: 50,
    total: 0
  },
  nasDirectory: {
    status: "ACTIVE",
    search: "",
    agency: "",
    expanded: new Set(),
    initialized: false
  },
  editingAgencyCode: null,
  editingNasId: null,
  mountingNasId: null,
  distribution: null,
  releaseStore: null,
  serverReleaseStore: null,
  releaseDeployments: null,
  releaseView: { tab: "production", page: 1, pageSize: 8 },
  devices: { page: 1, pageSize: 50, total: 0, items: [] },
  cases: {
    page: 1,
    pageSize: 20,
    total: 0,
    summary: { total: 0, open: 0, criticalOpen: 0, overdue: 0, unassigned: 0 },
    items: [],
    filters: { search: "", type: "", status: "", severity: "", agency: "" },
    selected: null,
    editingId: null,
    preparedSource: null,
    findingRegistry: new Map()
  },
  training: null,
  trainingUi: {
    selectedSession: null,
    sessionDetail: null
  },
  governance: null,
  governanceUi: {
    search: "",
    type: "",
    status: "",
    selectedId: null,
    detail: null,
    editingId: null,
    collapsed: new Set(),
    preview: null,
    previewRequest: 0
  },
  audit: {
    loaded: false,
    overview: null,
    enrollments: { page: 1, pageSize: 20, total: 0, items: [] },
    events: { page: 1, pageSize: 30, total: 0, items: [] },
    enrollmentFilters: { search: "", agency: "", attribution: "", period: "ALL" },
    eventFilters: { search: "", actor: "", module: "", outcome: "", period: "30D" }
  },
  securityPages: {
    devicePage: 1,
    devicePageSize: 10,
    deviceSort: "machine",
    deviceDirection: "asc",
    eventPage: 1,
    eventPageSize: 20,
    eventDeviceId: "",
    eventSort: "date",
    eventDirection: "desc"
  },
  view: "overview",
  filters: { search: "", agency: "", connectivity: "" },
  searchTimer: null,
  networkSearchTimer: null,
  caseSearchTimer: null,
  auditEnrollmentSearchTimer: null,
  auditEventSearchTimer: null
};

const viewTitles = {
  overview: ["Resumen", "Resumen ejecutivo"],
  assets: ["Activos", "Activos administrados"],
  agencies: ["Agencias", "Cobertura por agencia"],
  backups: ["Respaldos", "Control de respaldos"],
  nas: ["Almacenamiento NAS", "Almacenamiento distribuido"],
  network: ["Redes e IP", "Descubrimiento de red"],
  security: ["Postura de seguridad", "Controles y cumplimiento"],
  software: ["Software", "Control de software"],
  distribution: ["Actualizaciones", "Distribución de ATLAS"],
  governance: ["Políticas y procedimientos", "Gobierno documental"],
  training: ["Capacitaciones", "Programa de capacitaciones"],
  cases: ["Expedientes", "Centro de expedientes"],
  reports: ["Reportes", "Centro de reportes"],
  audit: ["Auditoría", "Registro de auditoría"],
  settings: ["Configuración", "Configuración de ATLAS"]
};
const roleLabels = {
  Administrator: "Administrador",
  Auditor: "Auditor",
  Operator: "Operador",
  Installer: "T\u00e9cnico de instalaci\u00f3n",
  TechnologyManager: "Gerente de Tecnolog\u00eda e Inform\u00e1tica"
};
const accountTitleLabels = {};
const governanceApprovalAttestation =
  "He le\u00eddo y apruebo este procedimiento como oficial.";
const installerViews = new Set(["assets", "distribution"]);
const connectivityLabels = {
  Online: "En línea",
  Attention: "Atención",
  Offline: "Sin reportar",
  Retired: "Retirado"
};
const backupLabels = {
  1: "Correcta",
  2: "Advertencia",
  3: "Fallida",
  4: "Cancelada",
  Successful: "Correcta",
  Warning: "Advertencia",
  Failed: "Fallida",
  Cancelled: "Cancelada",
  Skipped: "No iniciada",
  Running: "En curso",
  Partial: "Finalizada con archivos o carpetas pendientes",
  Unconfirmed: "Resultado por confirmar",
  UNCONFIRMED: "Resultado por confirmar"
};
const backupProviderLabels = {
  ATLAS: "ATLAS / BackupNAS",
  ACRONIS: "Acronis",
  EXTERNAL: "Externo",
  SHARED_FOLDER: "Puesto comunal / carpeta de red"
};
const coverageLabels = {
  COMPLIANT: "Cumple",
  WARNING: "Advertencia",
  FAILED: "Fallida",
  MISSING: "Sin copia",
  CANCELLED: "Copia cancelada",
  OVERDUE: "Copia atrasada",
  CONFIGURATION_REQUIRED: "Configurar NAS",
  DESTINATION_MISMATCH: "Destino incorrecto",
  EXTERNAL: "Gestion externa",
  NOT_APPLICABLE: "No aplica por equipo"
};
const backupControlProviderLabels = {
  ATLAS_BACKUPNAS: "Atlas Backup / NAS",
  ACRONIS: "Acronis Cloud",
  OTHER: "Otro autorizado"
};
const backupControlScopeLabels = {
  ENDPOINT: "Endpoint",
  ON_PREM_SYSTEM: "Sistema local",
  LOCAL_EXPORT: "Archivo o exportación local"
};
const backupControlApprovalLabels = {
  DRAFT: "Borrador",
  PENDING_APPROVAL: "Borrador",
  APPROVED: "Seguimiento activo",
  ACTIVE: "Seguimiento activo",
  RETURNED: "Borrador"
};
const backupControlComplianceLabels = {
  SCHEDULED: "Pendiente dentro del día",
  RUNNING: "Copia en curso", SKIPPED: "Copia no iniciada", PARTIAL: "Archivos o carpetas pendientes",
  CANCELLED: "Copia cancelada",
  PAUSED: "Pausado por devolución",
  PAUSE_REVIEW_DUE: "Revisar pausa",
  COMPLIANT: "Al día",
  WARNING: "Advertencia",
  FAILED: "Fallido",
  OVERDUE: "Vencido",
  NOT_CONFIRMED: "Sin confirmar",
  DESTINATION_MISMATCH: "Destino incorrecto",
  CONFIGURATION_REQUIRED: "Configuración pendiente",
  EXCEPTION_EXPIRED: "Excepción vencida",
  RESTORE_TEST_DUE: "Restauración vencida",
  PENDING_APPROVAL: "Por activar",
  INACTIVE: "Inactivo"
};
const backupRestorationLabels = {
  FILE: "Archivo",
  APPLICATION: "Aplicación",
  SYSTEM: "Sistema",
  HOT_RECOVERY: "Recuperación en caliente"
};
const backupControlResultLabels = {
  RUNNING: "En curso", SKIPPED: "No iniciada", PARTIAL: "Archivos o carpetas pendientes", UNCONFIRMED: "Resultado por confirmar",
  CANCELLED: "Cancelada",
  UNKNOWN: "Resultado no reconocido",
  SUCCESS: "Correcto",
  WARNING: "Con advertencias",
  FAILED: "Fallido"
};
const backupControlConfigurationLabels = {
  STANDARD: "Estándar recomendado",
  CUSTOMIZED: "Estándar personalizado",
  MANUAL: "Configuración personalizada controlada"
};
const backupControlExceptionLabels = {
  NONE: "Sin excepción",
  OPERATIONAL: "Excepción operativa",
  NORMATIVE: "Excepción normativa"
};
const backupControlOriginLabels = {
  DETECTED: "Detectado",
  INHERITED: "Heredado",
  SUGGESTED: "Sugerido",
  MANUAL: "Manual",
  OVERRIDE: "Personalizado"
};
const backupScopeCategoryLabels = {
  STANDARD_BACKUPNAS: "Configuración estándar esperada de BackupNAS",
  ACRONIS_POLICY: "Política autorizada de Acronis",
  DOCUMENTS: "Documentos",
  DESKTOP: "Escritorio",
  IMAGES: "Imágenes",
  FAVORITES: "Favoritos",
  DOWNLOADS: "Descargas",
  OTHER: "Otro alcance declarado"
};
const nasStatusLabels = {
  RETIRED: "Dado de baja",
  ONLINE: "Accesible desde ATLAS",
  DEGRADED: "Acceso parcial desde ATLAS",
  OFFLINE: "Sin respuesta al sondeo",
  UNKNOWN: "Sin sondeo"
};
const networkKindLabels = {
  ROUTER: "Router / gateway",
  NAS: "NAS",
  ENDPOINT: "Endpoint ATLAS",
  PRINTER: "Impresora",
  ACCESS_POINT: "Punto de acceso",
  SWITCH: "Switch",
  SERVER: "Servidor",
  CAMERA: "Camara",
  UNKNOWN: "Sin clasificar"
};
const networkScanStatusLabels = {
  NEVER: "Sin sondeo",
  QUEUED: "En cola",
  RUNNING: "Escaneando",
  SUCCEEDED: "Completado",
  FAILED: "Fallido",
  TIMED_OUT: "Tiempo agotado",
  SKIPPED: "Omitido",
  PARTIAL: "Parcial · revisar cobertura",
  INTERRUPTED: "Interrumpido",
  CANCELLED: "Cancelado"
};
const caseTypeLabels = {
  INCIDENT: "Incidente",
  VULNERABILITY: "Vulnerabilidad",
  EXCEPTION: "Excepción",
  RESTORATION: "Restauración",
  ACCESS: "Acceso",
  SUPPLIER: "Proveedor",
  OTHER: "Otro"
};
const caseStatusLabels = {
  NEW: "Nuevo",
  EVALUATING: "En evaluación",
  ASSIGNED: "Asignado",
  IN_TREATMENT: "En tratamiento",
  PENDING_VALIDATION: "Pendiente de validar",
  CLOSED: "Cerrado",
  ACCEPTED_EXCEPTION: "Excepción aceptada"
};
const caseSeverityLabels = {
  INFO: "Informativa",
  LOW: "Baja",
  MEDIUM: "Media",
  HIGH: "Alta",
  CRITICAL: "Crítica"
};
const casePriorityLabels = {
  LOW: "Baja",
  MEDIUM: "Media",
  HIGH: "Alta",
  URGENT: "Urgente"
};
const caseTimelineLabels = {
  CREATED: "Expediente creado",
  UPDATED: "Información actualizada",
  STATUS_CHANGED: "Estado actualizado",
  COMMENT: "Seguimiento registrado"
};
const trainingCycleStatusLabels = {
  DRAFT: "Borrador",
  ACTIVE: "Ciclo activo",
  CLOSED: "Cerrado",
  ARCHIVED: "Archivado"
};
const trainingSessionStatusLabels = {
  DRAFT: "Borrador",
  PLANNED: "Programada",
  COMPLETED: "Realizada",
  RESCHEDULED: "Reprogramada",
  CANCELLED: "Cancelada"
};
const trainingModalityLabels = {
  VIRTUAL: "Virtual",
  IN_PERSON: "Presencial",
  HYBRID: "Híbrida"
};
const governanceTypeLabels = {
  MASTER_POLICY: "Política principal",
  POLICY: "Política",
  PROCEDURE: "Procedimiento",
  STANDARD: "Estándar",
  GUIDE: "Guía",
  FORM: "Formato",
  ANNEX: "Anexo"
};
const governanceStatusLabels = {
  DRAFT: "Borrador",
  IN_REVIEW: "En revisión",
  APPROVED: "Aprobado",
  OBSOLETE: "Obsoleto"
};

const elements = {};
for (const id of [
  "sidebar", "menuButton", "breadcrumb", "pageTitle", "globalSearch",
  "assetSearch", "agencyFilter", "statusFilter", "clearFilters",
  "refreshButton", "lastRefresh", "logoutButton", "navDeviceCount",
  "notificationCenter", "notificationButton", "notificationCount",
  "notificationPanel", "notificationPanelMeta", "markAllNotifications",
  "notificationRows", "notificationPaginationLabel",
  "notificationPreviousPage", "notificationNextPage",
  "attentionToastStack",
  "sidebarAvatar", "sidebarUser", "sidebarRole", "todayLabel", "kpiTotal",
  "kpiTotalMeta", "kpiOnline", "kpiOnlineMeta", "kpiSecurity",
  "kpiSecurityMeta", "kpiAttention", "kpiAttentionMeta", "backupSuccess",
  "backupWarning", "backupFailed", "backupMissing", "backupTruth",
  "overviewConnectivityChart", "overviewConnectivityTotal",
  "overviewConnectivityLegend", "overviewBackupChart",
  "overviewSecurityChart", "overviewInfrastructureMetrics",
  "overviewSecurityTrend",
  "backupEmptyState", "overviewAgencyRows", "recentDeviceRows", "deviceRows",
  "deviceResultLabel", "deviceLoading", "paginationLabel", "previousPage",
  "nextPage", "agencyCards", "agencyRows", "drawerBackdrop", "deviceDrawer",
  "drawerTitle", "drawerSubtitle", "drawerContent", "closeDrawer", "toast",
  "distributionRows", "installerForm", "installerAgency",
  "installerLifetime", "generateInstaller", "releaseUploadForm",
  "releaseFile", "releaseFileLabel", "uploadRelease", "releaseProgress",
  "releaseProgressLabel", "releaseProgressValue", "releaseProgressBar",
  "releaseRows", "activeReleaseLabel", "deploymentScope",
  "deploymentAgencyField", "deploymentAgency", "deploymentHint",
  "releaseProductionCount", "releaseTestingCount", "releaseHistoryCount",
  "releaseTabDescription", "releasePaginationLabel",
  "releasePreviousPage", "releaseNextPage",
  "serverReleaseRows", "activeServerReleaseLabel",
  "releaseDetailOverlay", "releaseDetailClose", "releaseDetailEyebrow",
  "releaseDetailTitle", "releaseDetailSubtitle", "releaseDetailContent",
  "governancePreviewOverlay", "governancePreviewClose",
  "governancePreviewTitle", "governancePreviewMeta",
  "governancePreviewDownload", "governancePreviewNotice",
  "governancePreviewTabs", "governancePreviewBody",
  "deploymentRows", "pilotRingCount", "newAgencyButton", "agencyFormPanel",
  "agencyForm", "agencyCode", "agencyName", "agencyLocation",
  "agencyResponsible", "agencyProvider", "cancelAgency", "agencyFormTitle",
  "agencySubmit", "newNasButton",
  "nasFormPanel", "nasForm", "nasCode", "nasName", "nasAgency", "nasHost",
  "nasPort", "nasShare", "nasManufacturer", "nasModel", "nasSerial",
  "nasPrimary", "cancelNas", "nasFormTitle", "nasSubmit", "nasRows", "nasExplorerSelect",
  "nasMountPanel", "nasMountForm", "nasMountTitle", "nasMountProtocol",
  "nasMountUsername", "nasMountPassword", "nasMountDomain",
  "nasMountSecurity", "nasMountAllShares", "nasMountScopeText",
  "nasMountNfsExport", "cancelNasMount", "nasMountSubmit",
  "nasPhysicalCount", "nasResourceCount", "nasOnlineCount",
  "nasCapacityTotal", "nasSearch", "nasAgencyFilter", "expandAllNas",
  "collapseAllNas", "nasDetailOverlay", "nasDetailClose",
  "nasDetailAvatar", "nasDetailName", "nasDetailPath", "nasDetailStatus",
  "nasDetailCapacity", "nasDetailProbe", "nasDetailExplore",
  "nasDetailEdit", "nasDetailMount", "nasDetailCase",
  "probeNasButton", "nasBrowserStatus", "nasUpButton", "nasCurrentPath",
  "nasExplorerRows", "backupCoverageRows", "securityCoverage",
  "newBackupControlButton", "backupControlTotal", "backupControlCoverage",
  "backupControlCompliant", "backupControlAttention", "backupControlFailed",
  "backupControlOverdue", "backupControlPending", "backupControlRestoreDue",
  "backupStandardCount", "backupStandardCatalog",
  "backupControlEmptyState", "backupControlRows", "backupControlFormPanel",
  "backupControlForm", "backupControlFormTitle", "backupControlId",
  "backupControlConfigurationMode", "backupControlStandard",
  "backupControlAutofillSummary", "backupControlReasonField",
  "backupControlReason",
  "backupControlCode", "backupControlSystem", "backupControlAgency",
  "backupControlScopeType", "backupControlProvider", "backupControlMode",
  "backupControlDevice", "backupControlNas", "backupControlTechnology",
  "backupControlTask", "backupControlProtectedScope",
  "backupControlScopeAdditional", "backupControlScopeExclusions",
  "backupControlFrequency", "backupControlGrace", "backupControlRetention",
  "backupControlRpo", "backupControlRto", "backupControlRestoreInterval",
  "backupControlResponsible", "backupControlOwner",
  "backupControlDestination", "backupControlEnabled", "backupControlCancel",
  "backupActivationDialog", "backupActivationCode",
  "backupControlExceptionType", "backupControlExceptionReasonField",
  "backupControlExceptionReason", "backupControlExceptionExpiryField",
  "backupControlExceptionExpiry", "backupControlExceptionWarning",
  "backupControlSave", "backupObservationPanel", "backupObservationForm",
  "backupObservationTitle", "backupObservationProfileId",
  "backupObservationSource", "backupObservationResult",
  "backupObservationPeriodStart", "backupObservationPeriodEnd",
  "backupObservationStarted", "backupObservationFinished",
  "backupObservationReference", "backupObservationDetail",
  "backupObservationCancel", "backupObservationSave",
  "backupRestorationPanel", "backupRestorationForm",
  "backupRestorationTitle", "backupRestorationProfileId",
  "backupRestorationModality", "backupRestorationResult",
  "backupRestorationBackupUsed", "backupRestorationStarted",
  "backupRestorationFinished", "backupRestorationRto",
  "backupRestorationExecutedBy", "backupRestorationValidatedBy",
  "backupRestorationValidatedAt", "backupRestorationNextDue",
  "backupRestorationScope", "backupRestorationIntegrity",
  "backupRestorationFunctionality", "backupRestorationHistorical",
  "backupRestorationNotes", "backupRestorationCancel",
  "backupRestorationSave", "backupEvidencePanel", "backupEvidenceForm",
  "backupEvidenceTitle", "backupEvidenceProfileId", "backupEvidenceType",
  "backupEvidenceAt", "backupEvidenceSource", "backupEvidenceFile",
  "backupEvidenceFileLabel", "backupEvidenceDescription",
  "backupEvidenceCancel", "backupEvidenceUpload",
  "backupControlDetailPanel", "backupControlDetailTitle",
  "backupControlDetailMeta", "backupControlDetailClose",
  "backupControlDetailBody",
  "backupSortBy", "backupSortDirection",
  "backupCoveragePaginationLabel", "backupCoveragePreviousPage",
  "backupCoverageNextPage", "nasExplorerPaginationLabel",
  "nasExplorerPreviousPage", "nasExplorerNextPage",
  "securityCoverageMeta", "securityFirewall", "securityBitLocker",
  "securityFailedLogons", "securityRemoteMeta", "securityCriticalEvents",
  "securityHighMeta", "securityDeviceRows",
  "securityDeviceSortBy", "securityDeviceSortDirection",
  "securityEventRows", "securityDevicePaginationLabel",
  "securityDevicePreviousPage", "securityDeviceNextPage",
  "securityEventPaginationLabel", "securityEventPreviousPage",
  "securityEventNextPage", "securityEventHeading",
  "securityEventDescription", "securityEventDeviceFilter",
  "securityEventSortBy", "securityEventSortDirection",
  "scanAllNetworks", "networkEngineDot",
  "networkEngineState", "networkEngineMeta", "networkScopeCards",
  "networkSearch", "networkScopeFilter", "networkAssetRows",
  "networkPaginationLabel", "networkPreviousPage", "networkNextPage",
  "caseTotal", "caseOpen", "caseCritical", "caseOverdue", "caseUnassigned",
  "caseSearch", "caseTypeFilter", "caseStatusFilter", "caseSeverityFilter",
  "caseAgencyFilter", "newCaseButton", "caseRows", "casePaginationLabel",
  "casePreviousPage", "caseNextPage", "caseEditorPanel", "caseForm",
  "caseFormTitle", "caseId", "caseType", "caseTitle", "caseDescription",
  "caseSource", "caseSeverity", "casePriority", "caseStatus", "caseAgency",
  "caseAssignedTo", "caseOwner", "caseDueAt", "caseDevices",
  "caseEvidencePanel", "caseEvidenceSummary", "caseChangeNote",
  "caseSave", "caseCancel", "caseCancelSecondary",
  "caseDetailPanel", "caseDetailClose", "caseDetailTitle", "caseDetailMeta",
  "caseDetailBody", "caseEditButton", "caseCommentForm", "caseComment",
  "caseCommentSubmit",
  "trainingNewTopic", "trainingNewCycle",
  "trainingCycleSelect", "trainingEditCycle", "trainingCloneCycle",
  "trainingSessionCount", "trainingCycleRange", "trainingCompletedCount",
  "trainingCompletionRate", "trainingOverdueCount", "trainingAttendanceRate",
  "trainingAttendanceMeta", "trainingCycleStatus", "trainingCycleName",
  "trainingCycleNotes", "trainingProgressLabel", "trainingProgressBar",
  "trainingNewSession", "trainingSessionGrid", "trainingCycleFormPanel",
  "trainingCycleForm", "trainingCycleMode", "trainingCycleId",
  "trainingCycleFormTitle", "trainingCycleFormName", "trainingCycleStart",
  "trainingCycleEnd", "trainingCycleFormStatus", "trainingCycleFormNotes",
  "trainingCloneNote", "trainingCycleSave", "trainingCycleCancel",
  "trainingCycleCancelSecondary", "trainingSessionFormPanel",
  "trainingSessionForm", "trainingSessionId", "trainingSessionFormTitle",
  "trainingSessionTopic", "trainingSessionSequence", "trainingSessionDate",
  "trainingSessionTime", "trainingSessionStatus", "trainingSessionTitle",
  "trainingSessionCategory", "trainingSessionModality",
  "trainingSessionDuration", "trainingSessionTrainer",
  "trainingSessionFocus", "trainingSessionMaterial",
  "trainingSessionPresentationUrl", "trainingSessionFormUrl",
  "trainingSessionNotes",
  "trainingSessionSave", "trainingSessionCancel",
  "trainingSessionCancelSecondary", "trainingAttendancePanel",
  "trainingAttendanceTitle", "trainingAttendanceSubtitle",
  "trainingAttendanceClose", "trainingAttendanceFormLink",
  "trainingPresentationLink", "trainingResourceLinksEmpty",
  "trainingAttendanceFile", "trainingAttendanceFileLabel",
  "trainingAttendanceUpload", "trainingAttendanceImportSummary",
  "trainingAttendanceWarnings", "trainingAttendanceRows",
  "trainingAttendanceImportRows", "trainingTopicRows",
  "trainingTopicFormPanel", "trainingTopicForm",
  "trainingTopicId", "trainingTopicFormTitle", "trainingTopicCode",
  "trainingTopicTitle", "trainingTopicCategory", "trainingTopicDuration",
  "trainingTopicModality", "trainingTopicActive", "trainingTopicFocus",
  "trainingTopicCancel", "trainingTopicCancelSecondary",
  "governanceNewDocument", "governanceApprovedCount",
  "governanceReviewCount", "governanceDraftCount", "governanceVersionCount",
  "governanceSearch", "governanceTypeFilter", "governanceStatusFilter",
  "governanceClearFilters", "governanceFormPanel", "governanceForm",
  "governanceDocumentId", "governanceFormTitle", "governanceCode",
  "governanceType", "governanceTitle", "governanceOwner",
  "governanceParent", "governanceSummary", "governanceObjective",
  "governanceScope", "governanceRelated", "governanceFormClose",
  "governanceFormCancel", "governanceSave", "governanceVersionPanel",
  "governanceVersionTitle", "governanceVersionClose",
  "governanceVersionLabel", "governanceChangeSummary", "governanceFile",
  "governanceFileLabel", "governanceVersionCancel",
  "governanceVersionUpload", "governanceDetailPanel",
  "governanceDetailCode", "governanceDetailTitle", "governanceDetailMeta",
  "governanceDetailClose", "governanceDetailBody", "governanceAddVersion",
  "governanceEdit", "governanceEditDescription", "governanceReview",
  "governanceDescriptionForm", "governanceDescriptionSummary",
  "governanceDescriptionObjective", "governanceDescriptionScope",
  "governanceDescriptionCancel", "governanceDescriptionSave",
  "governanceReturnDraft",
  "governanceRequestCorrection", "governanceApprove", "governanceObsolete",
  "governanceApprovalBox",
  "governanceApproverName", "governanceEffectiveDate",
  "governanceNextReviewDate", "governanceConfirmApproval",
  "governanceCancelApproval", "governanceResultLabel",
  "governanceDocumentGrid",
  "auditRefresh", "auditTotalEvents", "auditEvents24h",
  "auditFailedAccess", "auditDistinctActors", "auditActiveTokens",
  "auditEnrollmentExport", "auditEnrollmentSearch",
  "auditEnrollmentAgency", "auditEnrollmentAttribution",
  "auditEnrollmentRows", "auditEnrollmentPagination",
  "auditEnrollmentPrevious", "auditEnrollmentNext",
  "auditEventExport", "auditEventSearch", "auditEventActor",
  "auditEventModule", "auditEventOutcome", "auditEventPeriod",
  "auditEventRows", "auditEventPagination", "auditEventPrevious",
  "auditEventNext", "auditDetailOverlay", "auditDetailClose",
  "auditDetailEyebrow", "auditDetailTitle", "auditDetailBody"
]) {
  elements[id] = document.getElementById(id);
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#039;");
}

function atlasIcon(name, className = "") {
  const safeName = String(name || "file").replace(/[^a-z0-9-]/gi, "");
  const safeClass = String(className || "")
    .split(/\s+/)
    .filter(Boolean)
    .map((value) => value.replace(/[^a-z0-9_-]/gi, ""))
    .filter(Boolean)
    .join(" ");
  return `<svg class="atlas-icon${safeClass ? ` ${safeClass}` : ""}" aria-hidden="true"><use href="/assets/vendor/tabler/tabler-icons.svg#ti-${safeName}"></use></svg>`;
}

function emptyRow(columnCount, message) {
  return `<tr class="empty-row"><td colspan="${columnCount}">${escapeHtml(message)}</td></tr>`;
}

function formatNumber(value) {
  return new Intl.NumberFormat("es-GT").format(Number(value || 0));
}

function formatBytes(value) {
  const bytes = Number(value || 0);
  if (!Number.isFinite(bytes) || bytes <= 0) {
    return "Sin datos";
  }

  const units = ["B", "KB", "MB", "GB", "TB"];
  const index = Math.min(
    Math.floor(Math.log(bytes) / Math.log(1024)),
    units.length - 1
  );
  const amount = bytes / (1024 ** index);
  return `${amount.toLocaleString("es-GT", {
    maximumFractionDigits: index > 2 ? 2 : 1
  })} ${units[index]}`;
}

function formatDate(value) {
  if (!value) {
    return "Sin datos";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "Sin datos";
  }
  return new Intl.DateTimeFormat("es-GT", {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(date);
}

function formatDateTimeLocal(value) {
  if (!value) {
    return "";
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "";
  }
  const local = new Date(date.getTime() - (date.getTimezoneOffset() * 60_000));
  return local.toISOString().slice(0, 16);
}

function formatRelative(value) {
  if (!value) {
    return "Sin datos";
  }
  const date = new Date(value);
  const deltaSeconds = Math.round((date.getTime() - Date.now()) / 1000);
  const absolute = Math.abs(deltaSeconds);
  const formatter = new Intl.RelativeTimeFormat("es", { numeric: "auto" });
  if (absolute < 60) {
    return formatter.format(deltaSeconds, "second");
  }
  if (absolute < 3600) {
    return formatter.format(Math.round(deltaSeconds / 60), "minute");
  }
  if (absolute < 86400) {
    return formatter.format(Math.round(deltaSeconds / 3600), "hour");
  }
  return formatter.format(Math.round(deltaSeconds / 86400), "day");
}

function statusBadge(connectivity) {
  const key = Object.hasOwn(connectivityLabels, connectivity)
    ? connectivity
    : "Offline";
  return `<span class="status-badge ${key.toLowerCase()}">${escapeHtml(connectivityLabels[key])}</span>`;
}

function securityLabel(products) {
  const names = [...new Map((Array.isArray(products) ? products : [])
    .map(name => String(name || "").trim()).filter(Boolean)
    .map(name => [name.toLocaleLowerCase("es-GT"), name])).values()];
  return names.length ? names.join(", ") : "Sin productos reportados";
}

function hardwareLabel(device) {
  return [device.manufacturer, device.model].map(cleanInventoryValue).filter(Boolean).join(" ") ||
    "Modelo no reportado";
}

function primaryNetwork(device) {
  const adapters = Array.isArray(device.networkInterfaces)
    ? device.networkInterfaces
    : [];
  const adapter = adapters.find((item) =>
    Array.isArray(item.ipv4Addresses) && item.ipv4Addresses.length > 0);
  if (!adapter) {
    return {
      address: "Sin datos",
      assignment: "No reportado",
      adapter: "",
      gateways: []
    };
  }
  return {
    address: adapter.ipv4Addresses[0],
    assignment: adapter.dhcpEnabled === true
      ? "DHCP"
      : adapter.dhcpEnabled === false
        ? "Estática"
        : "No determinado",
    adapter: adapter.description || "",
    gateways: Array.isArray(adapter.defaultGateways)
      ? adapter.defaultGateways
      : []
  };
}

function atlasRequestId() {
  if (typeof globalThis.crypto?.randomUUID === "function") return globalThis.crypto.randomUUID();
  if (typeof globalThis.crypto?.getRandomValues !== "function") throw Error("Este navegador no puede crear un identificador de solicitud seguro. Usa un navegador compatible.");
  const bytes = globalThis.crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64; bytes[8] = (bytes[8] & 63) | 128;
  const text = [...bytes].map(value => value.toString(16).padStart(2, "0")).join("");
  return `${text.slice(0,8)}-${text.slice(8,12)}-${text.slice(12,16)}-${text.slice(16,20)}-${text.slice(20)}`;
}

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: "same-origin",
    ...options,
    headers: {
      "Accept": "application/json",
      ...(options.headers || {})
    }
  });
  if (response.status === 401) {
    window.location.replace("/login");
    throw new Error("unauthorized");
  }
  if (!response.ok) {
    const error = new Error(`http_${response.status}`);
    error.status = response.status;
    try {
      const detail = await response.json();
      error.code = detail.code || detail.error || null;
      error.detail = detail.detail || null;
    } catch {
      // Some reverse-proxy errors do not contain JSON.
    }
    throw error;
  }
  return response.status === 204 ? null : response.json();
}

function canReadAudit() {
  return ["Administrator", "Auditor", "TechnologyManager"]
    .includes(state.session?.role);
}

function buildAuditEventQuery(includePagination = true) {
  const query = new URLSearchParams();
  const filters = state.audit.eventFilters;
  Object.entries(filters).forEach(([key, value]) => {
    if (value) query.set(key, value);
  });
  if (includePagination) {
    query.set("page", String(state.audit.events.page || 1));
    query.set("pageSize", String(state.audit.events.pageSize || 30));
  }
  return query.toString();
}

function buildAuditEnrollmentQuery(includePagination = true) {
  const query = new URLSearchParams();
  const filters = state.audit.enrollmentFilters;
  Object.entries(filters).forEach(([key, value]) => {
    if (value) query.set(key, value);
  });
  if (includePagination) {
    query.set("page", String(state.audit.enrollments.page || 1));
    query.set("pageSize", String(state.audit.enrollments.pageSize || 20));
  }
  return query.toString();
}

async function loadAuditOverview() {
  state.audit.overview = await api("/api/v1/admin/audit/overview");
  renderAuditOverview();
}

async function loadAuditEvents() { return loadAuditPage("events"); }

async function loadAuditEnrollments() { return loadAuditPage("enrollments"); }

async function loadAudit(showMessage = false) {
  if (!canReadAudit()) return;
  elements.auditRefresh?.classList.add("loading");
  if (elements.auditRefresh) elements.auditRefresh.disabled = true;
  try {
    const outcomes = await Promise.all([
      loadAuditOverview(),
      loadAuditEnrollments(),
      loadAuditEvents()
    ]);
    state.audit.loaded = true;
    if (showMessage && outcomes.slice(1).every(Boolean)) showToast("Auditoría actualizada.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      document.getElementById("auditStatus").textContent="No se pudo actualizar el resumen. Vuelve a intentar.";
      [elements.auditTotalEvents,elements.auditEvents24h,elements.auditFailedAccess,elements.auditDistinctActors,elements.auditActiveTokens,document.getElementById("auditEnrollmentTotal"),document.getElementById("auditEnrollmentUnknown")].forEach(el=>el.textContent="—");
      showToast("No fue posible consultar la auditoría.");
    }
  } finally {
    elements.auditRefresh?.classList.remove("loading");
    if (elements.auditRefresh) elements.auditRefresh.disabled = false;
  }
}

function renderAuditOverview() {
  const overview = state.audit.overview;
  if (!overview) return;
  elements.auditTotalEvents.textContent = formatNumber(overview.totalEvents);
  elements.auditEvents24h.textContent = formatNumber(overview.eventsLast24Hours);
  elements.auditFailedAccess.textContent = formatNumber(overview.failedAccessLast24Hours);
  elements.auditDistinctActors.textContent = formatNumber(overview.distinctActors30Days);
  elements.auditActiveTokens.textContent = formatNumber(overview.activeEnrollmentTokens);
  document.getElementById("auditEnrollmentTotal").textContent = formatNumber(overview.enrollmentEvents);
  document.getElementById("auditEnrollmentUnknown").textContent = formatNumber(overview.unavailableEnrollments);
  document.getElementById("auditStatus").textContent = `Actualizado ${formatDate(overview.generatedAtUtc)}`;

  const selectedActor = elements.auditEventActor.value;
  const actors = Array.isArray(overview.actors) ? overview.actors : [];
  elements.auditEventActor.innerHTML = `<option value="">Todos los usuarios</option>${actors
    .map((actor) => `<option value="${escapeHtml(actor.value)}">${escapeHtml(actor.label)} · ${escapeHtml(actor.value)}</option>`)
    .join("")}`;
  elements.auditEventActor.value = selectedActor;

  const selectedAgency = elements.auditEnrollmentAgency.value;
  elements.auditEnrollmentAgency.innerHTML = `<option value="">Todas las agencias</option>${state.agencies
    .map((agency) => `<option value="${escapeHtml(agency.agencyCode || agency.code)}">${escapeHtml(agency.displayName || agency.agencyCode || agency.code)}</option>`)
    .join("")}`;
  elements.auditEnrollmentAgency.value = selectedAgency;
}

function auditAttributionLabel(status) {
  return {
    CONFIRMED: "Emisor autenticado",
    SYSTEM: "Emisión de sistema",
    UNAVAILABLE: "Registro histórico"
  }[status] || status || "No disponible";
}

function auditOutcomeLabel(outcome) { return {SUCCEEDED:"Acción completada",FAILED:"Fallido o rechazado",REQUESTED:"Solicitado",WARNING:"Con observaciones",UNKNOWN:"Sin resultado determinado"}[outcome] || "Sin resultado determinado"; }

function auditActorTypeLabel(actorType) {
  return roleLabels[actorType] || {
    Admin: "Usuario administrativo",
    Anonymous: "No autenticado",
    System: "Sistema"
  }[actorType] || "Identidad registrada";
}

function auditTargetTypeLabel(targetType) {
  return {
    AdminUser: "Cuenta administrativa",
    Agency: "Agencia",
    Audit: "Auditoría",
    BackupControlProfile: "Control de respaldo",
    BackupPlan: "Plan de respaldo",
    Device: "Activo administrado",
    GovernanceDocument: "Documento de gobierno",
    GovernanceDocumentVersion: "Versión documental",
    NasAppliance: "Servidor NAS",
    NetworkAsset: "Activo de red",
    NetworkScope: "Alcance de red",
    OperationalCase: "Expediente",
    TrainingCycle: "Ciclo de capacitación",
    TrainingParticipant: "Participante",
    TrainingSession: "Sesión de capacitación",
    TrainingTopic: "Tema de capacitación"
  }[targetType] || "Registro de ATLAS";
}

function auditFindDetailValue(detail, names, depth = 0) {
  if (!detail || typeof detail !== "object" || depth > 4) return null;
  for (const [key, value] of Object.entries(detail)) {
    if (names.includes(key) && ["string", "number"].includes(typeof value) && String(value).trim()) {
      return String(value);
    }
  }
  for (const value of Object.values(detail)) {
    const found = auditFindDetailValue(value, names, depth + 1);
    if (found) return found;
  }
  return null;
}

function auditTargetPresentation(item) {
  if (!item.targetId) {
    return { label: "Sin objeto específico", type: "Evento general" };
  }
  const type = auditTargetTypeLabel(item.targetType);
  if (item.targetDisplayName) return { label: item.targetDisplayName, type };
  const detailLabel = auditFindDetailValue(item.detail, [
    "originalFileName", "title", "caseNumber", "machineName",
    "displayName", "releaseVersion", "code", "scopeCode", "name"
  ]);
  if (detailLabel) return { label: detailLabel, type };

  const isInternalId = /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i.test(item.targetId);
  return {
    label: isInternalId ? type : item.targetId,
    type: isInternalId ? "Referencia conservada" : type
  };
}

function auditDetailLabel(key, prefix = "") {
  const labels = {
    accepted: "Registros aceptados",
    agencyCode: "Código de agencia",
    approverName: "Aprobado por",
    backupClientVersion: "Versión de BackupNAS",
    code: "Código",
    deviceKind: "Clasificación del activo",
    displayName: "Nombre",
    effectiveDate: "Vigente desde",
    executionIdentityVerified: "Identidad de ejecución verificada",
    exportedRows: "Registros exportados",
    exportType: "Tipo de exportación",
    fileName: "Archivo",
    installerVersion: "Versión del instalador",
    machineName: "Nombre del activo",
    nextReviewDate: "Próxima revisión",
    notes: "Observaciones",
    originalFileName: "Archivo",
    protectedDetail: "Información del evento",
    reason: "Motivo",
    rejected: "Registros rechazados",
    remoteAddress: "Dirección de origen",
    releaseVersion: "Versión",
    sha256: "Huella SHA-256",
    sizeBytes: "Tamaño",
    source: "Origen",
    status: "Estado",
    title: "Título",
    updateRing: "Anillo de actualización",
    versionLabel: "Versión documental"
  };
  const extra = {before:"Anterior",after:"Nuevo",backupProvider:"Modalidad de respaldo",provider:"Herramienta",nasApplianceId:"Referencia del NAS",folders:"Carpetas",frequencyHours:"Frecuencia de copia · horas",restorationIntervalDays:"Intervalo de restauración · días",retentionDays:"Retención · días",ownerName:"Responsable",revision:"Versión",definition:"Configuración",name:"Nombre",description:"Descripción",search:"Búsqueda",actor:"Usuario",module:"Módulo",outcome:"Resultado",target:"Objeto",attribution:"Atribución",agency:"Agencia",format:"Formato",filters:"Filtros",rpoHours:"Pérdida de datos objetivo · horas",rtoMinutes:"Recuperación objetivo · minutos",exclusions:"Exclusiones",scope:"Alcance",protectedScope:"Carpetas protegidas"};
  const labelKey = Object.keys({...labels,...extra}).find(name=>name.toLowerCase()===key.toLowerCase());
  const human = extra[labelKey] || labels[labelKey] || key
    .replace(/_/g, " ")
    .replace(/([a-záéíóúñ])([A-Z])/g, "$1 $2")
    .replace(/^./, (letter) => letter.toUpperCase());
  return `${prefix}${human}`;
}

function auditDetailValue(key, value) {
  if (value === "[PROTEGIDO]") return "Protegido por seguridad";
  if (typeof value === "boolean") return value ? "Sí" : "No";
  if (/bytes$/i.test(key) && Number.isFinite(Number(value))) {
    return formatBytes(Number(value));
  }
  if (value === null || value === undefined || value === "") return "No aplica";
  const label = { ...backupProviderLabels, ...backupControlProviderLabels, ...governanceStatusLabels, ...caseStatusLabels, ...nasStatusLabels, ...backupControlResultLabels, ACTIVE: "Activo", REQUESTED: "Solicitado", UNKNOWN: "Sin determinar", KEEP: "Conservar alcance", PLAN: "Usar plan", CUSTOM: "Personalizado" }[String(value)];
  if (/utc$/i.test(key) && !Number.isNaN(Date.parse(String(value)))) return formatDate(value);
  return label || String(value);
}

function auditDetailFields(detail) {
  const fields = [];
  const visit = (value, path = "", key = "") => {
    if (Array.isArray(value)) {
      if (!value.length) fields.push({key:path,label:path,value:"Sin elementos"});
      value.forEach((child,index) => visit(child,`${path} · ${index + 1}`,key));
    } else if (value && typeof value === "object") {
      for (const [name,child] of Object.entries(value)) {
        visit(child,[path,auditDetailLabel(name)].filter(Boolean).join(" · "),name);
      }
    } else {
      fields.push({key:path,label:path,value:auditDetailValue(key,value)});
    }
  };
  visit(detail);
  return fields;
}

function renderAuditComparison(detail) {
  if (!detail || typeof detail !== "object") return "";
  const beforeKey = Object.keys(detail).find(key=>key.toLowerCase()==="before");
  const afterKey = Object.keys(detail).find(key=>key.toLowerCase()==="after");
  if (!beforeKey || !afterKey) return `<p class="audit-history-note">Este evento no conserva una comparación de valores anteriores y nuevos.</p>`;
  const before = new Map(auditDetailFields(detail[beforeKey]).map(field=>[field.key,field]));
  const after = new Map(auditDetailFields(detail[afterKey]).map(field=>[field.key,field]));
  const keys = [...new Set([...before.keys(),...after.keys()])].filter(Boolean);
  return `<section class="audit-comparison"><h4>Antes y después</h4><div class="table-wrap"><table class="data-table"><thead><tr><th>Dato</th><th>Anterior</th><th>Nuevo</th></tr></thead><tbody>${keys.map(key=>{
    const old=before.get(key)?.value ?? "No registrado";
    const current=after.get(key)?.value ?? "No registrado";
    return `<tr class="${old===current ? "" : "audit-changed"}"><th scope="row">${escapeHtml(key)}</th><td>${escapeHtml(old)}</td><td>${escapeHtml(current)}</td></tr>`;
  }).join("")}</tbody></table></div></section>`;
}


function renderAuditFriendlyDetail(detail) {
  const additional = detail && typeof detail==="object" && !Array.isArray(detail) ? Object.fromEntries(Object.entries(detail).filter(([key])=>!["before","after"].includes(key.toLowerCase()))) : detail;
  const fields = auditDetailFields(additional);
  if (!fields.length) {
    return `<section class="audit-friendly-detail audit-friendly-detail-empty">
      <h4>Información del evento</h4>
      <p>No se conservó información adicional para este evento.</p>
    </section>`;
  }
  return `<section class="audit-friendly-detail">
    <h4>Información registrada</h4>
    <div class="audit-friendly-grid">${fields.map((field) => `<div>
      <span>${escapeHtml(field.label)}</span>
      <strong>${escapeHtml(field.value)}</strong>
    </div>`).join("")}</div>
  </section>`;
}

function renderAuditTechnicalReference(item) {
  return `<details class="audit-technical-reference">
    <summary>Referencia técnica</summary>
    <div><span>Identificador del evento</span><strong>#${escapeHtml(item.id)}</strong></div>
    <div><span>Acción interna</span><strong>${escapeHtml(item.action)}</strong></div>
    ${item.targetId ? `<div><span>Identificador del objeto</span><strong>${escapeHtml(item.targetId)}</strong></div>` : ""}
  </details>`;
}

function renderAuditEnrollments() {
  const page = state.audit.enrollments || {};
  const rows = Array.isArray(page.items) ? page.items : [];
  elements.auditEnrollmentRows.innerHTML = rows.length
    ? rows.map((item) => {
        const historical = item.attributionStatus === "UNAVAILABLE";
        const issuer = item.issuedBy
          ? `<span class="cell-primary">${escapeHtml(item.issuedByDisplayName)}</span><span class="cell-secondary">${escapeHtml(item.issuedBy)} · ${escapeHtml(roleLabels[item.issuedByRole] || item.issuedByRole || "Rol no disponible")}</span>`
          : `<span class="cell-primary muted">Registro histórico</span><span class="cell-secondary">El emisor no se registraba en esa fecha</span>`;
        const sourceIp = item.issuedFromIp
          ? `Origen ${escapeHtml(item.issuedFromIp)}`
          : historical ? "Sin datos de emisión" : "Origen no conservado";
        const enrollmentIp = item.enrolledFromIp
          ? `Origen ${escapeHtml(item.enrolledFromIp)}`
          : "Origen no conservado";
        const issuanceDate = item.issuedAtUtc
          ? formatDate(item.issuedAtUtc)
          : historical ? "Anterior a la trazabilidad" : "No registrada";
        const versions = item.installerVersion
          ? `<span class="cell-primary">Instalador ${escapeHtml(item.installerVersion)}</span><span class="cell-secondary">Agente ${escapeHtml(item.agentVersion || "no reportado")}</span>`
          : `<span class="cell-primary">Agente ${escapeHtml(item.agentVersion || "no reportado")}</span><span class="cell-secondary">Versión del instalador no conservada</span>`;
        return `<tr>
          <td><span class="cell-primary">${escapeHtml(item.machineName)}</span><span class="cell-secondary">${escapeHtml(item.agencyName)} · ${escapeHtml(item.agencyCode)}</span></td>
          <td>${issuer}</td>
          <td><span class="cell-primary">${escapeHtml(issuanceDate)}</span><span class="cell-secondary">${sourceIp}</span></td>
          <td><span class="cell-primary">${escapeHtml(formatDate(item.enrolledAtUtc))}</span><span class="cell-secondary">${enrollmentIp}${item.isReenrollment ? " · Reinscripción" : ""}</span></td>
          <td>${versions}</td>
          <td><span class="audit-badge attribution-${escapeHtml(String(item.attributionStatus || "").toLowerCase())}">${escapeHtml(auditAttributionLabel(item.attributionStatus))}</span><button class="audit-row-detail" type="button" data-audit-enrollment-id="${item.id}">Ver evidencia</button></td>
        </tr>`;
      }).join("")
    : emptyRow(6, "No existen vinculaciones para los filtros seleccionados.");

  renderPagination(
    page,
    elements.auditEnrollmentPagination,
    elements.auditEnrollmentPrevious,
    elements.auditEnrollmentNext,
    "vinculaciones");
  elements.auditEnrollmentRows.querySelectorAll("[data-audit-enrollment-id]")
    .forEach((button) => button.addEventListener("click", () =>
      openAuditEnrollmentDetail(Number(button.dataset.auditEnrollmentId))));
}

function renderAuditEvents() {
  const page = state.audit.events || {};
  const rows = Array.isArray(page.items) ? page.items : [];
  elements.auditEventRows.innerHTML = rows.length
    ? rows.map((item) => {
      const target = auditTargetPresentation(item);
      return `<tr>
        <td><span class="cell-primary">${escapeHtml(formatDate(item.occurredAtUtc))}</span><span class="cell-secondary">Evento #${item.id}</span></td>
        <td><span class="audit-module module-${escapeHtml(String(item.module || "other").toLowerCase())}">${escapeHtml(item.moduleLabel)}</span></td>
        <td><span class="cell-primary">${escapeHtml(item.actionLabel)}</span></td>
        <td><span class="cell-primary">${escapeHtml(item.actorDisplayName)}</span><span class="cell-secondary">${escapeHtml(item.actorId)} · ${escapeHtml(auditActorTypeLabel(item.actorType))}</span></td>
        <td><span class="cell-primary">${escapeHtml(target.label)}</span><span class="cell-secondary">${escapeHtml(target.type)}</span></td>
        <td><span class="audit-badge outcome-${escapeHtml(String(item.outcome || "").toLowerCase())}">${escapeHtml(auditOutcomeLabel(item.outcome))}</span></td>
        <td><button class="audit-detail-button" type="button" data-audit-event-id="${item.id}" aria-label="Ver detalle del evento">${atlasIcon("chevron-right")}</button></td>
      </tr>`;
    }).join("")
    : emptyRow(7, "No existen eventos para los filtros seleccionados.");
  renderPagination(
    page,
    elements.auditEventPagination,
    elements.auditEventPrevious,
    elements.auditEventNext,
    "eventos");
  elements.auditEventRows.querySelectorAll("[data-audit-event-id]")
    .forEach((button) => button.addEventListener("click", () =>
      void openAuditEventDetail(Number(button.dataset.auditEventId))));
}

async function openAuditEventDetail(id) {
  try {
    const item = await api(`/api/v1/admin/audit/events/${id}`);
    const target = auditTargetPresentation(item);
    showAuditDetail(
      `EVENTO #${item.id} · ${item.moduleLabel}`,
      item.actionLabel,
      `<div class="audit-detail-grid">
        <div><span>Fecha</span><strong>${escapeHtml(formatDate(item.occurredAtUtc))}</strong></div>
        <div><span>Resultado</span><strong>${escapeHtml(auditOutcomeLabel(item.outcome))}</strong></div>
        <div><span>Identidad</span><strong>${escapeHtml(item.actorDisplayName)}</strong><small>${escapeHtml(item.actorId)} · ${escapeHtml(auditActorTypeLabel(item.actorType))}</small></div>
        <div><span>Objeto</span><strong>${escapeHtml(target.label)}</strong><small>${escapeHtml(target.type)}</small></div>
      </div>${renderAuditComparison(item.detail)}${renderAuditFriendlyDetail(item.detail)}<p class="audit-history-note">${item.contextCaptured ? "Los nombres disponibles se conservaron al registrar este evento." : "Registro histórico: los nombres que faltan no se reconstruyen con datos actuales."}</p>${renderAuditTechnicalReference(item)}`);
  } catch (error) {
    if (error.message !== "unauthorized") showToast("No fue posible abrir el evento.");
  }
}

function openAuditEnrollmentDetail(id) {
  const item = (state.audit.enrollments.items || [])
    .find((candidate) => Number(candidate.id) === id);
  if (!item) return;
  const observedUser = item.interactiveUserDisplayName || item.interactiveUserAccount;
  const historical = item.attributionStatus === "UNAVAILABLE";
  showAuditDetail(
    `VINCULACIÓN #${item.id}`,
    item.machineName,
    `<div class="audit-detail-grid">
      <div><span>Activo y agencia</span><strong>${escapeHtml(item.machineName)}</strong><small>${escapeHtml(item.agencyName)} · ${escapeHtml(item.agencyCode)}</small></div>
      <div><span>Instalador emitido por</span><strong>${escapeHtml(item.issuedByDisplayName || (historical ? "Registro histórico" : "No registrado"))}</strong><small>${escapeHtml(item.issuedBy || auditAttributionLabel(item.attributionStatus))}</small></div>
      <div><span>Emisión</span><strong>${escapeHtml(item.issuedAtUtc ? formatDate(item.issuedAtUtc) : (historical ? "Anterior a la trazabilidad" : "No registrada"))}</strong><small>${escapeHtml(item.issuedFromIp || (historical ? "Sin datos de emisión" : "IP no conservada"))}</small></div>
      <div><span>Vinculación</span><strong>${escapeHtml(formatDate(item.enrolledAtUtc))}</strong><small>${escapeHtml(item.enrolledFromIp || "IP no conservada")}</small></div>
      <div><span>Versiones</span><strong>${item.installerVersion ? `Instalador ${escapeHtml(item.installerVersion)}` : `Agente ${escapeHtml(item.agentVersion)}`}</strong><small>${item.installerVersion ? `Agente ${escapeHtml(item.agentVersion)}` : "Versión del instalador no conservada"}${item.backupClientVersion ? ` · Backup ${escapeHtml(item.backupClientVersion)}` : ""}</small></div>
      <div><span>Atribución</span><strong>${escapeHtml(auditAttributionLabel(item.attributionStatus))}</strong><small>${item.isReenrollment ? "Reinscripción" : "Primera vinculación observada"}</small></div>
    </div>
    <div class="audit-enrollment-chain"><span>${historical ? "Emisor no conservado" : item.attributionStatus === "SYSTEM" ? "Emisión de sistema" : "Emisor autenticado"}</span><span aria-hidden="true">→</span><span>${historical ? "Registro histórico" : "Autorización de un solo uso"}</span><span aria-hidden="true">→</span><span>Activo vinculado</span></div>
    <p class="audit-history-note">${item.issuerNameCaptured ? "Nombre del emisor conservado al emitir el instalador." : "El nombre al emitir no fue conservado; se mantiene la cuenta o referencia histórica disponible."}</p>
    <button type="button" class="secondary-button" data-audit-object="${escapeHtml(item.deviceId)}" data-object-name="${escapeHtml(item.machineName)}">Ver actividad de este activo</button>
    <details class="audit-technical-reference"><summary>Referencia de vinculación</summary><p>Emisión: ${escapeHtml(item.issuanceId || "No disponible")}</p><p>Activo: ${escapeHtml(item.deviceId)}</p></details>
    <div class="audit-proof-note"><strong>${historical ? "Registro anterior al control de atribución." : "La ejecución local no se atribuye sin evidencia."}</strong><span>${historical ? "Este activo ya estaba vinculado cuando se habilitó esta trazabilidad; ATLAS no inventa un emisor para datos que no fueron conservados." : observedUser ? `El usuario interactivo observado actualmente es ${escapeHtml(observedUser)}, pero se presenta sólo como contexto y no como prueba de ejecución.` : "ATLAS no recibió evidencia suficiente para atribuir la ejecución física del archivo."}</span></div>`);
}

function showAuditDetail(eyebrow, title, body) {
  state.audit.returnFocus = document.activeElement;
  elements.auditDetailEyebrow.textContent = eyebrow;
  elements.auditDetailTitle.textContent = title;
  elements.auditDetailBody.innerHTML = body;
  elements.auditDetailOverlay.classList.remove("hidden");
  elements.auditDetailOverlay.setAttribute("aria-hidden", "false");
  elements.auditDetailClose.focus();
  elements.auditDetailBody.querySelectorAll("[data-audit-object]").forEach(button=>button.addEventListener("click",()=>{const id=button.dataset.auditObject,name=button.dataset.objectName;closeAuditDetail();clearAuditFilters("events");state.audit.eventFilters.target=id;const chip=document.getElementById("auditTargetFilter");chip.textContent=`Actividad del activo: ${name}`;chip.classList.remove("hidden");selectAuditTab("events");void loadAuditEvents();}));
}

function closeAuditDetail() {
  elements.auditDetailOverlay.classList.add("hidden");
  elements.auditDetailOverlay.setAttribute("aria-hidden", "true");
  elements.auditDetailBody.innerHTML = "";
  if (state.audit.returnFocus?.isConnected) state.audit.returnFocus.focus();
}

async function exportAudit(kind,format="csv") {
  if (!canReadAudit() || state.audit.exporting) return;
  const query=kind==="enrollments" ? buildAuditEnrollmentQuery(false) : buildAuditEventQuery(false);
  state.audit.exporting=true;
  document.querySelectorAll("[data-audit-report],#auditEventExport,#auditEnrollmentExport").forEach(b=>b.disabled=true);
  try {
    const response=await fetch(`/api/v1/admin/audit/${kind}/${format==="csv" ? "export.csv" : "report.html"}?${query}`,{credentials:"same-origin"});
    if(response.status===401){window.location.replace("/login");return;}
    if(!response.ok){const result=await response.json().catch(()=>({}));throw new Error(result.detail || "No fue posible generar el informe. Revisa las fechas y vuelve a intentar.");}
    const blob=await response.blob();const url=URL.createObjectURL(blob);
    const link=document.createElement("a");link.href=url;link.download=`atlas-${kind==="events"?"auditoria":"vinculaciones"}-${new Date().toISOString().slice(0,10)}.${format}`;
    document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),60000);
    showToast("Archivo generado y exportación registrada en auditoría.");
  } catch(error){showToast(error.message);}
  finally {state.audit.exporting=false;document.querySelectorAll("[data-audit-report],#auditEventExport,#auditEnrollmentExport").forEach(b=>b.disabled=false);}
}

async function loadAuditPage(kind) {
  if (!canReadAudit()) return false;
  const enrollment=kind==="enrollments", key=enrollment?"enrollment":"event";
  const version=(state.audit[kind+"Request"]||0)+1;state.audit[kind+"Request"]=version;
  state.audit[kind+"Loading"]=true;updateAuditReports();
  const rows=enrollment?elements.auditEnrollmentRows:elements.auditEventRows;
  const previous=enrollment?elements.auditEnrollmentPrevious:elements.auditEventPrevious;
  const next=enrollment?elements.auditEnrollmentNext:elements.auditEventNext;
  const pagination=enrollment?elements.auditEnrollmentPagination:elements.auditEventPagination;
  rows.innerHTML=emptyRow(enrollment?6:7,"Consultando registros…"); previous.disabled=true;next.disabled=true;
  try {
    const page=await api(`/api/v1/admin/audit/${kind}?${enrollment?buildAuditEnrollmentQuery():buildAuditEventQuery()}`);
    if(state.audit[kind+"Request"]!==version)return false;
    state.audit[kind]=page;state.audit[kind+"Error"]=false;state.audit[kind+"Loading"]=false;
    if(enrollment)renderAuditEnrollments();else renderAuditEvents();
    updateAuditReports();return true;
  }catch(error){
    if(state.audit[kind+"Request"]!==version)return false;
    state.audit[kind+"Error"]=true;state.audit[kind+"Loading"]=false;state.audit[kind].items=[];
    rows.innerHTML=emptyRow(enrollment?6:7,"No se pudo consultar la auditoría. Usa Actualizar para reintentar.");
    pagination.textContent="Consulta no disponible";previous.disabled=true;next.disabled=true;
    updateAuditReports();if(error.message!=="unauthorized")showToast("No fue posible consultar los registros de auditoría.");return false;
  }
}

function selectAuditTab(tab) {
  if (!["summary","events","enrollments","reports"].includes(tab)) return;
  state.audit.tab=tab;
  document.querySelectorAll("[data-audit-panel]").forEach(panel=>panel.classList.toggle("hidden",panel.dataset.auditPanel!==tab));
  document.querySelectorAll(".audit-tabs [data-audit-tab]").forEach(button=>{
    if(button.dataset.auditTab===tab)button.setAttribute("aria-current","page");else button.removeAttribute("aria-current");
  });
  updateAuditReports();
}

function updateAuditReports() {
  for(const kind of ["events","enrollments"]){
    const filters=state.audit[kind==="events"?"eventFilters":"enrollmentFilters"];
    const label=document.querySelector(`[data-audit-report-scope="${kind}"]`);if(!label)continue;
    const from=filters.fromUtc?formatDate(filters.fromUtc):({ALL:"Todo el histórico","24H":"Últimas 24 horas","7D":"Últimos 7 días","30D":"Últimos 30 días","90D":"Últimos 90 días","365D":"Último año"}[filters.period]||"Todo el histórico");
    const extra=Object.entries(filters).filter(([k,v])=>v && !["period","fromUtc","toUtc"].includes(k)).map(([k,v])=>`${auditDetailLabel(k)}: ${k==="outcome"?auditOutcomeLabel(v):k==="attribution"?auditAttributionLabel(v):k==="module"?(elements.auditEventModule.selectedOptions[0]?.textContent||v):v}`).join(" · ");
    label.textContent=`${from}${filters.toUtc?` · Hasta ${formatDate(filters.toUtc)} (límite exclusivo)`:""}${extra?` · ${extra}`:""}. ${state.audit[kind+"Loading"]?"Consultando cantidad de registros…":state.audit[kind+"Error"]?"Consulta pendiente de reintento.":`${formatNumber(state.audit[kind].total||0)} registros en la última consulta.`}`;
  }
}

function clearAuditFilters(kind) {
  const enrollment=kind==="enrollments";
  state.audit[enrollment?"enrollmentFilters":"eventFilters"]=enrollment?{search:"",agency:"",attribution:"",period:"ALL"}:{search:"",actor:"",module:"",outcome:"",period:"ALL"};
  const ids=enrollment?["Search","Agency","Attribution"]:["Search","Actor","Module","Outcome","Period"];
  ids.forEach(id=>elements[`audit${enrollment?"Enrollment":"Event"}${id}`].value=id==="Period"?"ALL":"");
  document.querySelectorAll(`[data-audit-kind="${kind}"]`).forEach(input=>input.value="");
  if(!enrollment)document.getElementById("auditTargetFilter").classList.add("hidden");
  state.audit[kind].page=1;
}

function auditDateBounds(from,to) {
  const start=from?new Date(`${from}T00:00:00`):null,end=to?new Date(`${to}T00:00:00`):null;
  if(start && !Number.isFinite(start.getTime()) || end && !Number.isFinite(end.getTime()) || start && end && start>end)throw new Error("Revisa las fechas: el final debe ser igual o posterior al inicio.");
  if(end)end.setDate(end.getDate()+1);
  return {fromUtc:start?.toISOString()||"",toUtc:end?.toISOString()||""};
}


function buildDeviceQuery() {
  const query = new URLSearchParams({
    page: String(state.devices.page),
    pageSize: String(state.devices.pageSize)
  });
  for (const [key, value] of Object.entries(state.filters)) {
    if (value) {
      query.set(key, value);
    }
  }
  return query.toString();
}

function buildCaseQuery() {
  const query = new URLSearchParams({
    page: String(state.cases.page),
    pageSize: String(state.cases.pageSize)
  });
  for (const [key, value] of Object.entries(state.cases.filters)) {
    if (value) {
      query.set(key, value);
    }
  }
  return query.toString();
}

function buildSecurityQuery() {
  const query = new URLSearchParams({
    devicePage: String(state.securityPages.devicePage),
    devicePageSize: String(state.securityPages.devicePageSize),
    deviceSort: state.securityPages.deviceSort,
    deviceDirection: state.securityPages.deviceDirection,
    eventPage: String(state.securityPages.eventPage),
    eventPageSize: String(state.securityPages.eventPageSize),
    eventSort: state.securityPages.eventSort,
    eventDirection: state.securityPages.eventDirection
  });
  if (state.securityPages.eventDeviceId) {
    query.set("eventDeviceId", state.securityPages.eventDeviceId);
  }
  return query.toString();
}

function buildNetworkQuery() {
  const query = new URLSearchParams({
    page: String(state.networkPage.page),
    pageSize: String(state.networkPage.pageSize)
  });
  if (state.networkPage.scope) {
    query.set("scope", state.networkPage.scope);
  }
  if (state.networkPage.search) {
    query.set("search", state.networkPage.search);
  }
  if (state.networkPage.presence) query.set("presence",state.networkPage.presence);
  if (state.networkPage.sort) query.set("sort",state.networkPage.sort);
  if (state.networkPage.descending) query.set("descending","true");
  if (state.networkPage.runId) query.set("runId",state.networkPage.runId);
  return query.toString();
}

// Availability belongs to each source. A failed query never means zero records.
const atlasSourceViews = {
  summary: ["overview"], devices: ["assets"], agencies: ["agencies", "overview"],
  backupControl: ["backups", "overview"], backupCoverage: ["backups"], backupPlans: ["backups"], nas: ["nas", "overview"],
  security: ["security", "overview"], network: ["network", "overview"], cases: ["cases"],
  training: ["training"], governance: ["governance"],
  distribution: ["distribution"], releases: ["distribution"],
  serverReleases: ["distribution"], deployments: ["distribution"], notifications: ["overview"]
};
const atlasSourceLabels = {
  summary: "resumen", devices: "inventario", agencies: "agencias", backupControl: "control de respaldos",
  backupPlans: "planes de respaldo", backupCoverage: "cobertura de copias", nas: "NAS", security: "postura y eventos", network: "redes",
  cases: "expedientes", training: "capacitaciones", governance: "documentos",
  distribution: "componentes", releases: "versiones de equipos", serverReleases: "versiones del servidor",
  deployments: "despliegues", notifications: "atención y avisos"
};
function sourceRecord(key) {
  state.sources ||= {};
  return state.sources[key] ||= { status: "idle", lastSuccessAtUtc: null, request: 0 };
}
function sourceBegin(key) {
  const source = sourceRecord(key);
  source.status = "loading";
  source.request += 1;
  renderSourceAvailability();
  return source.request;
}
function sourceSuccess(key, request) {
  const source = sourceRecord(key);
  if (request != null && request !== source.request) return false;
  source.status = "ready";
  source.lastSuccessAtUtc = new Date().toISOString();
  renderSourceAvailability();
  return true;
}
function sourceFailure(key, request) {
  const source = sourceRecord(key);
  if (request != null && request !== source.request) return false;
  source.status = "error";
  renderSourceAvailability();
  return true;
}
function renderSourceAvailability() {
  if (!state.sources) return;
  Object.keys(viewTitles).forEach(view => {
    const section = document.getElementById(`view-${view}`);
    if (!section) return;
    const sources = Object.entries(state.sources).filter(([key]) => (atlasSourceViews[key] || []).includes(view));
    const unresolved = sources.filter(([, value]) => value.status === "error");
    const initialLoading = sources.filter(([, value]) => value.status === "loading" && !value.lastSuccessAtUtc);
    const primaryKey = ({ overview: "summary", assets: "devices", agencies: "agencies", backups: "backupControl", nas: "nas", security: "security", network: "network", cases: "cases", training: "training", governance: "governance" })[view];
    const primary = primaryKey && state.sources[primaryKey];
    section.toggleAttribute("data-source-unavailable", !!primary && !primary.lastSuccessAtUtc && ["loading", "error"].includes(primary.status));
    let banner = section.querySelector(":scope > .source-status");
    if (!banner && (unresolved.length || initialLoading.length)) {
      banner = document.createElement("div"); banner.className = "source-status"; banner.setAttribute("role", "status");
      const heading = section.querySelector(":scope > .section-heading");
      heading ? heading.after(banner) : section.prepend(banner);
    }
    if (!banner) return;
    banner.hidden = !unresolved.length && !initialLoading.length;
    banner.classList.toggle("source-error", unresolved.length > 0);
    if (banner.hidden) return;
    const current = unresolved.length ? unresolved : initialLoading;
    banner.replaceChildren();
    const text = document.createElement("div"), title = document.createElement("strong"), detail = document.createElement("p");
    title.textContent = unresolved.length ? `Consulta pendiente: ${current.map(([key]) => atlasSourceLabels[key]).join(", ")}` : "Consultando información…";
    detail.textContent = unresolved.length
      ? current.map(([key, value]) => value.lastSuccessAtUtc ? `${atlasSourceLabels[key]}: se conserva la última consulta correcta (${formatDate(value.lastSuccessAtUtc)}).` : `${atlasSourceLabels[key]}: no hay una consulta correcta disponible; no se interpreta como ausencia de registros.`).join(" ")
      : "Los datos se mostrarán cuando termine la consulta de esta fuente.";
    text.append(title, detail); banner.append(text);
    if (unresolved.length) {
      const retry = document.createElement("button"); retry.type = "button"; retry.className = "secondary-button compact"; retry.textContent = "Volver a consultar";
      retry.addEventListener("click", () => void refreshAll()); banner.append(retry);
    }
  });
}
function setInventoryLoading(status) {
  elements.deviceLoading.textContent = status === "ready" ? "Datos actuales" : status === "error" ? "Consulta pendiente" : "Actualizando";
  elements.deviceLoading.classList.toggle("ready", status === "ready");
  elements.deviceLoading.classList.toggle("source-error", status === "error");
  elements.deviceLoading.setAttribute("role", "status");
}
async function loadDevices() {
  const request = sourceBegin("devices"), query = buildDeviceQuery();
  setInventoryLoading("loading");
  try {
    const value = await api(`/api/v1/admin/devices?${query}`);
    if (request !== sourceRecord("devices").request || query !== buildDeviceQuery()) return;
    state.devices = value;
    sourceSuccess("devices", request);
    renderDevices(); setInventoryLoading("ready");
  } catch (error) {
    if (request !== sourceRecord("devices").request || query !== buildDeviceQuery()) return;
    sourceFailure("devices", request); setInventoryLoading("error");
    if (error.message !== "unauthorized") showToast("No fue posible consultar el inventario. Revisa el aviso de esta fuente.");
  }
}
async function loadSecurity() {
  const request = sourceBegin("security"), query = buildSecurityQuery();
  try {
    const value = await api(`/api/v1/admin/security?${query}`);
    if (request !== sourceRecord("security").request || query !== buildSecurityQuery()) return;
    state.security = value; sourceSuccess("security", request); renderSecurity(); renderExecutiveOverview();
  } catch (error) {
    if (request !== sourceRecord("security").request || query !== buildSecurityQuery()) return;
    sourceFailure("security", request);
    if (error.message !== "unauthorized") showToast("No fue posible consultar la postura. Se conserva su fecha anterior.");
  }
}
async function loadNetworkDiscovery() {
  if (!["Administrator", "Auditor", "Operator", "TechnologyManager"].includes(state.session?.role)) return;
  const current = state.networkRequestNumber = (state.networkRequestNumber || 0) + 1;
  const request = sourceBegin("network");
  try {
    const result = await api(`/api/v1/admin/network-discovery?${buildNetworkQuery()}`);
    if (current !== state.networkRequestNumber || request !== sourceRecord("network").request || state.session?.role === "Installer") return;
    state.network = result; sourceSuccess("network", request); renderNetworkDiscovery();
  } catch (error) {
    if (current !== state.networkRequestNumber || request !== sourceRecord("network").request) return;
    sourceFailure("network", request);
    if (!state.network) elements.networkEngineState.textContent = "Consulta de red pendiente";
    globalThis.AtlasNetworkWorkspace?.render();
    if (error.message !== "unauthorized") showToast("No fue posible actualizar la red. Se conserva la última observación disponible.");
  }
}
async function refreshAll(showMessage = false) {
  const refresh = state.refreshRequestNumber = (state.refreshRequestNumber || 0) + 1;
  elements.refreshButton.classList.add("loading"); elements.refreshButton.disabled = true;
  const critical = ["summary", "agencies", "devices"], tokens = Object.fromEntries(critical.map(key => [key, sourceBegin(key)]));
  const deviceQuery = buildDeviceQuery(); setInventoryLoading("loading");
  try {
    const session = await api("/api/v1/admin/session");
    if (refresh !== state.refreshRequestNumber) return;
    const installerOnly = session.role === "Installer";
    const main = await Promise.allSettled([api("/api/v1/admin/summary"), api("/api/v1/admin/agencies"), api(`/api/v1/admin/devices?${deviceQuery}`)]);
    if (refresh !== state.refreshRequestNumber) return;
    state.session = session;
    main.forEach((result, index) => {
      const key = critical[index];
      if (key === "devices" && (tokens[key] !== sourceRecord(key).request || deviceQuery !== buildDeviceQuery())) return;
      if (result.status === "fulfilled") { state[key] = result.value; sourceSuccess(key, tokens[key]); }
      else { if (result.reason?.message === "unauthorized") throw result.reason; sourceFailure(key, tokens[key]); }
    });
    if (tokens.devices === sourceRecord("devices").request) setInventoryLoading(sourceRecord("devices").status);
    const optional = installerOnly ? [] : [
      ["nas", "/api/v1/admin/nas", value => { state.nas = value; }],
      ["backupCoverage", "/api/v1/admin/backup-coverage", value => { state.backupCoverage = value; }],
      ["backupControl", "/api/v1/admin/backup-controls", value => { state.backupControl = value; }],
      ["backupPlans", "/api/v1/admin/backup-controls/plans", value => { if (typeof backupWorkspace !== "undefined") { backupWorkspace.plans = value; backupWorkspace.loaded = true; backupWorkspace.error = null; } }],
      ["security", `/api/v1/admin/security?${buildSecurityQuery()}`, value => { state.security = value; }],
      ["network", `/api/v1/admin/network-discovery?${buildNetworkQuery()}`, value => { state.network = value; }],
      ["cases", `/api/v1/admin/cases?${buildCaseQuery()}`, value => { state.cases = { ...state.cases, ...value }; }],
      ["training", "/api/v1/admin/training", value => { state.training = value; }],
      ["governance", "/api/v1/admin/governance", value => { state.governance = value; }],
      ["notifications", "/api/v1/admin/notifications?page=1&pageSize=20", value => { state.notifications = value; }]
    ];
    if (session.role === "Administrator" || installerOnly) optional.push(["distribution", "/api/v1/admin/distribution", value => { state.distribution = value; }]);
    if (session.role === "Administrator") optional.push(
      ["releases", "/api/v1/admin/releases", value => { state.releaseStore = value; }],
      ["serverReleases", "/api/v1/admin/server-releases", value => { state.serverReleaseStore = value; }],
      ["deployments", "/api/v1/admin/release-deployments", value => { state.releaseDeployments = value; }]
    );
    const requests = optional.map(([key]) => sourceBegin(key));
    const results = await Promise.allSettled(optional.map(([, url]) => api(url)));
    if (refresh !== state.refreshRequestNumber) return;
    results.forEach((result, index) => {
      const [key, , apply] = optional[index], request = requests[index];
      if (request !== sourceRecord(key).request) return;
      if (result.status === "fulfilled") { apply(result.value); sourceSuccess(key, request); }
      else { if (result.reason?.message === "unauthorized") throw result.reason; sourceFailure(key, request); }
    });
    renderAll(); renderSourceAvailability();
    if (state.view === "audit" && canReadAudit()) await loadAudit();
    const pending = Object.entries(state.sources).filter(([, value]) => value.status === "error").map(([key]) => atlasSourceLabels[key] || "fuente");
    elements.lastRefresh.textContent = pending.length
      ? `Consulta parcial · ${pending.length} ${pending.length === 1 ? "fuente pendiente" : "fuentes pendientes"}`
      : `Consultado ${formatRelative(new Date().toISOString())} · cada indicador conserva la fecha de su dato`;
    if (showMessage) showToast(pending.length ? "La consulta terminó con fuentes pendientes. Revisa sus avisos." : "Consulta completada. Cada fuente conserva la fecha de sus datos.");
  } catch (error) {
    if (refresh !== state.refreshRequestNumber) return;
    critical.forEach(key => { if (sourceRecord(key).status === "loading") sourceFailure(key, tokens[key]); });
    setInventoryLoading(sourceRecord("devices").status);
    renderSourceAvailability();
    if (error.message !== "unauthorized") showToast("No fue posible completar la consulta. Se conservan las últimas respuestas disponibles.");
  } finally {
    if (refresh === state.refreshRequestNumber) { elements.refreshButton.classList.remove("loading"); elements.refreshButton.disabled = false; }
  }
}

function renderAll() {
  renderSession();
  renderSummary();
  renderAgencies();
  renderDevices();
  renderBackupControl();
  renderBackupCoverage();
  renderNas();
  renderNetworkDiscovery();
  renderSecurity();
  renderTraining();
  renderGovernance();
  renderCases();
  renderNotifications();
  showAttentionToasts();
  renderExecutiveOverview();
  renderDistribution();
  renderReleaseStore();
  renderServerReleaseStore();
  renderReleaseDeployments();
}

function canManageCases() {
  return state.session?.role === "Administrator" ||
    state.session?.role === "Operator";
}

function canManageTraining() {
  return state.session?.role === "Administrator" ||
    state.session?.role === "Operator";
}

function canManageGovernance() {
  return state.session?.role === "Administrator" ||
    state.session?.role === "Operator";
}

function canApproveGovernance() {
  return state.session?.role === "TechnologyManager";
}

function formatTrainingDate(value) {
  if (!value) {
    return "Sin fecha";
  }
  const date = new Date(`${value}T12:00:00`);
  return Number.isNaN(date.getTime())
    ? "Sin fecha"
    : new Intl.DateTimeFormat("es-GT", {
      weekday: "short",
      day: "numeric",
      month: "short",
      year: "numeric"
    }).format(date);
}

function trainingCycleById(id) {
  return (state.training?.cycles || [])
    .find((cycle) => cycle.id === id) || null;
}

function selectedTrainingCycle() {
  return trainingCycleById(state.training?.selectedCycleId);
}

function trainingSessionById(id) {
  return (state.training?.sessions || [])
    .find((session) => session.id === id) || null;
}

function trainingTopicById(id) {
  return (state.training?.topics || [])
    .find((topic) => topic.id === id) || null;
}

function isTrainingSessionOverdue(session) {
  const today = new Date();
  const localDate = [
    today.getFullYear(),
    String(today.getMonth() + 1).padStart(2, "0"),
    String(today.getDate()).padStart(2, "0")
  ].join("-");
  return session.scheduledDate < localDate &&
    session.status !== "COMPLETED" &&
    session.status !== "CANCELLED";
}

function trainingMutationOptions(method, body) {
  return {
    method,
    headers: {
      "Content-Type": "application/json",
      "X-Atlas-Request": "dashboard"
    },
    ...(body === undefined ? {} : { body: JSON.stringify(body) })
  };
}

async function loadTraining(cycleId = null) {
  const sourceRequest = sourceBegin("training");
  const query = cycleId
    ? `?cycleId=${encodeURIComponent(cycleId)}`
    : "";
  try {
    const sourceValue = await api(`/api/v1/admin/training${query}`);
    if (sourceRequest !== sourceRecord("training").request) return;
    state.training = sourceValue;
    sourceSuccess("training", sourceRequest);
    state.trainingUi.selectedSession = null;
    state.trainingUi.sessionDetail = null;
    closeTrainingPanels();
    renderTraining();
  } catch (error) {
    if (sourceRequest !== sourceRecord("training").request) return;
    sourceFailure("training", sourceRequest);
    if (error.message !== "unauthorized") {
      showToast("No fue posible actualizar el programa de capacitaciones.");
    }
  }
}

function renderTraining() {
  if (!elements.trainingSessionGrid) {
    return;
  }
  const dashboard = state.training;
  if (!dashboard) {
    elements.trainingSessionGrid.innerHTML =
      "<div class=\"training-empty\">Consultando el programa de capacitaciones…</div>";
    return;
  }

  const cycles = dashboard.cycles || [];
  const cycle = selectedTrainingCycle();
  const summary = dashboard.summary || {};
  elements.trainingCycleSelect.innerHTML = cycles.length
    ? cycles.map((item) => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)} · ${escapeHtml(trainingCycleStatusLabels[item.status] || item.status)}</option>`).join("")
    : "<option value=\"\">Sin ciclos registrados</option>";
  elements.trainingCycleSelect.value = dashboard.selectedCycleId || "";
  elements.trainingCycleSelect.disabled = cycles.length === 0;
  elements.trainingEditCycle.disabled = !cycle;
  elements.trainingCloneCycle.disabled = !cycle;
  elements.trainingNewSession.disabled = !cycle || !(dashboard.topics || []).some((item) => item.active);

  const sessionCount = Number(summary.sessionCount || 0);
  const completed = Number(summary.completedSessions || 0);
  const overdue = Number(summary.overdueSessions || 0);
  const responses = Number(summary.attendancePresent || 0);
  const completionRate = sessionCount > 0
    ? Math.round(completed * 100 / sessionCount)
    : 0;

  elements.trainingSessionCount.textContent = formatNumber(sessionCount);
  elements.trainingCompletedCount.textContent = formatNumber(completed);
  elements.trainingOverdueCount.textContent = formatNumber(overdue);
  elements.trainingCompletionRate.textContent = `${formatNumber(completionRate)}% de avance`;
  elements.trainingAttendanceRate.textContent = formatNumber(responses);
  elements.trainingAttendanceMeta.textContent = responses === 1
    ? "respuesta importada"
    : "respuestas importadas";
  elements.trainingProgressLabel.textContent = `${formatNumber(completionRate)}%`;
  elements.trainingProgressBar.style.width = `${completionRate}%`;

  elements.trainingCycleRange.textContent = cycle
    ? `${formatTrainingDate(cycle.startDate)} — ${formatTrainingDate(cycle.endDate)}`
    : "Sin ciclo seleccionado";
  elements.trainingCycleName.textContent = cycle?.name || "Seleccione un ciclo";
  elements.trainingCycleNotes.textContent = cycle?.notes ||
    "El calendario y las asistencias se mantienen separados por ciclo.";
  elements.trainingCycleStatus.textContent = cycle
    ? trainingCycleStatusLabels[cycle.status] || cycle.status
    : "Sin ciclo";
  elements.trainingCycleStatus.className =
    `training-cycle-state ${String(cycle?.status || "").toLowerCase()}`;

  renderTrainingSessions();
  renderTrainingCatalogs();
  renderTrainingSelectors();
}

function renderTrainingSessions() {
  const sessions = state.training?.sessions || [];
  const manageable = canManageTraining();
  elements.trainingSessionGrid.innerHTML = sessions.length
    ? sessions.map((session) => {
      const overdue = isTrainingSessionOverdue(session);
      const displayStatus = overdue ? "Atrasada" :
        trainingSessionStatusLabels[session.status] || session.status;
      const statusClass = overdue
        ? "overdue"
        : String(session.status || "draft").toLowerCase();
      const material = session.materialReference || "Material sin asignar";
      const resourceLinks = [
        session.presentationUrl
          ? `<a href="${escapeHtml(session.presentationUrl)}" target="_blank" rel="noopener noreferrer">Presentación ↗</a>`
          : "",
        session.attendanceFormUrl
          ? `<a href="${escapeHtml(session.attendanceFormUrl)}" target="_blank" rel="noopener noreferrer">Formulario ↗</a>`
          : ""
      ].filter(Boolean).join("");
      const manageActions = manageable
        ? `<button class="secondary-button compact" type="button" data-training-session-edit="${escapeHtml(session.id)}">Editar</button>
           ${session.status === "COMPLETED" ? "" : `<button class="secondary-button compact training-danger-button" type="button" data-training-session-delete="${escapeHtml(session.id)}">Quitar</button>`}`
        : "";
      return `<article class="training-session-card ${escapeHtml(statusClass)}">
        <header>
          <span class="training-sequence">${formatNumber(session.sequenceNumber)}</span>
          <span class="training-status-badge ${escapeHtml(statusClass)}">${escapeHtml(displayStatus)}</span>
        </header>
        <h4>${escapeHtml(session.title)}</h4>
        <div class="training-session-date"><strong>${escapeHtml(formatTrainingDate(session.scheduledDate))}</strong>${session.startTime ? ` · ${escapeHtml(String(session.startTime).slice(0, 5))}` : ""}</div>
        <p class="training-session-focus">${escapeHtml(session.securityFocus)}</p>
        <div class="training-session-material" title="${escapeHtml(material)}">${atlasIcon("file-description")} ${escapeHtml(material)}</div>
        <div class="training-session-links">${resourceLinks || "<span>Enlaces pendientes</span>"}</div>
        <div class="training-session-stats"><span>${escapeHtml(trainingModalityLabels[session.modality] || session.modality)} · ${formatNumber(session.durationMinutes)} min</span><strong>${formatNumber(session.presentAttendance)} respuestas</strong></div>
        <div class="training-session-actions">
          <button class="primary-button compact" type="button" data-training-attendance="${escapeHtml(session.id)}">Ver asistencia</button>
          ${manageActions}
        </div>
      </article>`;
    }).join("")
    : `<div class="training-empty"><strong>Este ciclo todavía no tiene sesiones.</strong><br>Añada la primera capacitación o duplique un ciclo anterior.</div>`;

  elements.trainingSessionGrid
    .querySelectorAll("[data-training-attendance]")
    .forEach((button) => button.addEventListener("click", () =>
      void openTrainingAttendance(button.dataset.trainingAttendance)));
  elements.trainingSessionGrid
    .querySelectorAll("[data-training-session-edit]")
    .forEach((button) => button.addEventListener("click", () =>
      beginEditTrainingSession(button.dataset.trainingSessionEdit)));
  elements.trainingSessionGrid
    .querySelectorAll("[data-training-session-delete]")
    .forEach((button) => button.addEventListener("click", () =>
      void deleteTrainingSession(button.dataset.trainingSessionDelete)));
}

function renderTrainingCatalogs() {
  const manageable = canManageTraining();
  const topics = state.training?.topics || [];
  elements.trainingTopicRows.innerHTML = topics.length
    ? topics.map((topic) => `<article class="training-catalog-item ${topic.active ? "" : "inactive"}">
        <div><h4>${escapeHtml(topic.title)}</h4><p>${escapeHtml(topic.category)} · ${formatNumber(topic.defaultDurationMinutes)} min · ${escapeHtml(trainingModalityLabels[topic.defaultModality] || topic.defaultModality)}</p></div>
        ${manageable ? `<button class="secondary-button compact" type="button" data-training-topic-edit="${escapeHtml(topic.id)}">Editar</button>` : ""}
      </article>`).join("")
    : "<div class=\"training-empty\">No hay temas registrados.</div>";

  elements.trainingTopicRows
    .querySelectorAll("[data-training-topic-edit]")
    .forEach((button) => button.addEventListener("click", () =>
      beginEditTrainingTopic(button.dataset.trainingTopicEdit)));
}

function renderTrainingSelectors() {
  const currentTopic = elements.trainingSessionTopic.value;
  const topics = (state.training?.topics || []).filter((topic) =>
    topic.active || topic.id === currentTopic);
  elements.trainingSessionTopic.innerHTML = topics.length
    ? topics.map((topic) => `<option value="${escapeHtml(topic.id)}">${escapeHtml(topic.title)}</option>`).join("")
    : "<option value=\"\">Primero cree un tema</option>";
  if (topics.some((topic) => topic.id === currentTopic)) {
    elements.trainingSessionTopic.value = currentTopic;
  }

}

function closeTrainingPanels() {
  [
    elements.trainingCycleFormPanel,
    elements.trainingSessionFormPanel,
    elements.trainingTopicFormPanel,
    elements.trainingAttendancePanel
  ].forEach((panel) => panel?.classList.add("hidden"));
}

function openTrainingPanel(panel) {
  closeTrainingPanels();
  panel.classList.remove("hidden");
  panel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function addYearsToDateOnly(value, years) {
  const [year, month, day] = String(value).split("-").map(Number);
  const candidate = new Date(Date.UTC(year + years, month - 1, day));
  return candidate.toISOString().slice(0, 10);
}

function nextTrainingCycleName(name) {
  const value = String(name || "");
  return /(\d{4})(\D+)(\d{4})/.test(value)
    ? value.replace(
      /(\d{4})(\D+)(\d{4})/,
      (_, start, separator, end) =>
        `${Number(start) + 1}${separator}${Number(end) + 1}`)
    : `${value || "Ciclo"} · siguiente`;
}

function beginCreateTrainingCycle() {
  elements.trainingCycleForm.reset();
  elements.trainingCycleMode.value = "create";
  elements.trainingCycleId.value = "";
  elements.trainingCycleFormStatus.value = "DRAFT";
  elements.trainingCycleFormStatus.disabled = false;
  elements.trainingCycleFormTitle.textContent = "Nuevo ciclo";
  elements.trainingCycleSave.textContent = "Crear ciclo";
  elements.trainingCloneNote.classList.add("hidden");
  openTrainingPanel(elements.trainingCycleFormPanel);
  elements.trainingCycleFormName.focus();
}

function beginEditTrainingCycle() {
  const cycle = selectedTrainingCycle();
  if (!cycle) return;
  elements.trainingCycleForm.reset();
  elements.trainingCycleMode.value = "edit";
  elements.trainingCycleId.value = cycle.id;
  elements.trainingCycleFormName.value = cycle.name;
  elements.trainingCycleStart.value = cycle.startDate;
  elements.trainingCycleEnd.value = cycle.endDate;
  elements.trainingCycleFormStatus.value = cycle.status;
  elements.trainingCycleFormStatus.disabled = false;
  elements.trainingCycleFormNotes.value = cycle.notes || "";
  elements.trainingCycleFormTitle.textContent = `Editar ${cycle.name}`;
  elements.trainingCycleSave.textContent = "Guardar cambios";
  elements.trainingCloneNote.classList.add("hidden");
  openTrainingPanel(elements.trainingCycleFormPanel);
}

function beginCloneTrainingCycle() {
  const cycle = selectedTrainingCycle();
  if (!cycle) return;
  elements.trainingCycleForm.reset();
  elements.trainingCycleMode.value = "clone";
  elements.trainingCycleId.value = cycle.id;
  elements.trainingCycleFormName.value = nextTrainingCycleName(cycle.name);
  elements.trainingCycleStart.value = addYearsToDateOnly(cycle.startDate, 1);
  elements.trainingCycleEnd.value = addYearsToDateOnly(cycle.endDate, 1);
  elements.trainingCycleFormStatus.value = "DRAFT";
  elements.trainingCycleFormStatus.disabled = true;
  elements.trainingCycleFormNotes.value = `Creado a partir de ${cycle.name}.`;
  elements.trainingCycleFormTitle.textContent = "Crear siguiente ciclo";
  elements.trainingCycleSave.textContent = "Duplicar calendario";
  elements.trainingCloneNote.classList.remove("hidden");
  openTrainingPanel(elements.trainingCycleFormPanel);
  elements.trainingCycleFormName.focus();
}

async function saveTrainingCycle(event) {
  event.preventDefault();
  const mode = elements.trainingCycleMode.value;
  const id = elements.trainingCycleId.value;
  const clone = mode === "clone";
  const payload = {
    name: elements.trainingCycleFormName.value,
    startDate: elements.trainingCycleStart.value,
    endDate: elements.trainingCycleEnd.value,
    notes: elements.trainingCycleFormNotes.value || null,
    ...(clone ? {} : { status: elements.trainingCycleFormStatus.value })
  };
  const path = clone
    ? `/api/v1/admin/training/cycles/${encodeURIComponent(id)}/clone`
    : mode === "edit"
      ? `/api/v1/admin/training/cycles/${encodeURIComponent(id)}`
      : "/api/v1/admin/training/cycles";
  const method = mode === "edit" ? "PUT" : "POST";
  elements.trainingCycleSave.disabled = true;
  try {
    const result = await api(path, trainingMutationOptions(method, payload));
    await loadTraining(result.id);
    showToast(clone
      ? "Siguiente ciclo creado sin copiar asistencias."
      : mode === "edit" ? "Ciclo actualizado." : "Ciclo creado.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.code === "training_cycle_exists"
        ? "Ya existe un ciclo con ese nombre."
        : error.code === "training_cycle_range_conflict"
          ? "Las fechas no pueden excluir sesiones existentes."
          : "No fue posible guardar el ciclo.");
    }
  } finally {
    elements.trainingCycleSave.disabled = false;
  }
}

function applyTrainingTopicToSession() {
  const topic = trainingTopicById(elements.trainingSessionTopic.value);
  if (!topic) return;
  elements.trainingSessionTitle.value = topic.title;
  elements.trainingSessionFocus.value = topic.securityFocus;
  elements.trainingSessionCategory.value = topic.category;
  elements.trainingSessionDuration.value = topic.defaultDurationMinutes;
  elements.trainingSessionModality.value = topic.defaultModality;
}

function suggestedTrainingSessionDate() {
  const cycle = selectedTrainingCycle();
  const sessions = state.training?.sessions || [];
  if (!cycle) return "";
  if (!sessions.length) return cycle.startDate;
  const last = sessions[sessions.length - 1].scheduledDate;
  const date = new Date(`${last}T12:00:00`);
  date.setDate(date.getDate() + 28);
  const candidate = [
    date.getFullYear(),
    String(date.getMonth() + 1).padStart(2, "0"),
    String(date.getDate()).padStart(2, "0")
  ].join("-");
  return candidate > cycle.endDate ? cycle.endDate : candidate;
}

function beginCreateTrainingSession() {
  const cycle = selectedTrainingCycle();
  const topics = (state.training?.topics || []).filter((topic) => topic.active);
  if (!cycle || !topics.length) {
    showToast("Cree un tema disponible antes de añadir una sesión.");
    return;
  }
  elements.trainingSessionForm.reset();
  elements.trainingSessionId.value = "";
  renderTrainingSelectors();
  elements.trainingSessionTopic.value = topics[0].id;
  elements.trainingSessionSequence.value =
    Math.max(0, ...(state.training?.sessions || []).map((item) => Number(item.sequenceNumber))) + 1;
  elements.trainingSessionDate.value = suggestedTrainingSessionDate();
  elements.trainingSessionStatus.value = "PLANNED";
  applyTrainingTopicToSession();
  elements.trainingSessionFormTitle.textContent = "Nueva sesión";
  elements.trainingSessionSave.textContent = "Crear sesión";
  openTrainingPanel(elements.trainingSessionFormPanel);
}

function beginEditTrainingSession(id) {
  const session = trainingSessionById(id);
  if (!session) return;
  elements.trainingSessionForm.reset();
  elements.trainingSessionId.value = session.id;
  renderTrainingSelectors();
  elements.trainingSessionTopic.value = session.topicId;
  elements.trainingSessionSequence.value = session.sequenceNumber;
  elements.trainingSessionDate.value = session.scheduledDate;
  elements.trainingSessionTime.value = session.startTime
    ? String(session.startTime).slice(0, 5)
    : "";
  elements.trainingSessionStatus.value = session.status;
  elements.trainingSessionTitle.value = session.title;
  elements.trainingSessionFocus.value = session.securityFocus;
  elements.trainingSessionCategory.value = session.category;
  elements.trainingSessionModality.value = session.modality;
  elements.trainingSessionDuration.value = session.durationMinutes;
  elements.trainingSessionTrainer.value = session.trainer || "";
  elements.trainingSessionMaterial.value = session.materialReference || "";
  elements.trainingSessionPresentationUrl.value = session.presentationUrl || "";
  elements.trainingSessionFormUrl.value = session.attendanceFormUrl || "";
  elements.trainingSessionNotes.value = session.notes || "";
  elements.trainingSessionFormTitle.textContent = `Editar sesión ${session.sequenceNumber}`;
  elements.trainingSessionSave.textContent = "Guardar cambios";
  openTrainingPanel(elements.trainingSessionFormPanel);
}

async function saveTrainingSession(event) {
  event.preventDefault();
  const id = elements.trainingSessionId.value;
  const payload = {
    cycleId: state.training?.selectedCycleId,
    topicId: elements.trainingSessionTopic.value,
    sequenceNumber: Number(elements.trainingSessionSequence.value),
    scheduledDate: elements.trainingSessionDate.value,
    startTime: elements.trainingSessionTime.value
      ? `${elements.trainingSessionTime.value}:00`
      : null,
    title: elements.trainingSessionTitle.value,
    securityFocus: elements.trainingSessionFocus.value,
    category: elements.trainingSessionCategory.value,
    modality: elements.trainingSessionModality.value,
    durationMinutes: Number(elements.trainingSessionDuration.value),
    status: elements.trainingSessionStatus.value,
    trainer: elements.trainingSessionTrainer.value || null,
    materialReference: elements.trainingSessionMaterial.value || null,
    presentationUrl: elements.trainingSessionPresentationUrl.value || null,
    attendanceFormUrl: elements.trainingSessionFormUrl.value || null,
    notes: elements.trainingSessionNotes.value || null
  };
  const path = id
    ? `/api/v1/admin/training/sessions/${encodeURIComponent(id)}`
    : "/api/v1/admin/training/sessions";
  elements.trainingSessionSave.disabled = true;
  try {
    await api(path, trainingMutationOptions(id ? "PUT" : "POST", payload));
    await loadTraining(state.training?.selectedCycleId);
    showToast(id ? "Sesión actualizada." : "Sesión añadida al ciclo.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.code === "training_session_outside_cycle"
        ? "La fecha debe estar dentro del ciclo seleccionado."
        : "No fue posible guardar la sesión.");
    }
  } finally {
    elements.trainingSessionSave.disabled = false;
  }
}

async function deleteTrainingSession(id) {
  const session = trainingSessionById(id);
  if (!session || !window.confirm(
    `¿Quitar “${session.title}” de este ciclo? La asistencia pendiente también se retirará.`
  )) {
    return;
  }
  try {
    await api(
      `/api/v1/admin/training/sessions/${encodeURIComponent(id)}`,
      trainingMutationOptions("DELETE"));
    await loadTraining(state.training?.selectedCycleId);
    showToast("Sesión retirada del ciclo.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.code === "completed_training_session_is_protected"
        ? "Una sesión realizada se conserva como evidencia; puede cancelarla o corregirla."
        : "No fue posible retirar la sesión.");
    }
  }
}

function beginCreateTrainingTopic() {
  elements.trainingTopicForm.reset();
  elements.trainingTopicId.value = "";
  elements.trainingTopicDuration.value = "30";
  elements.trainingTopicModality.value = "VIRTUAL";
  elements.trainingTopicActive.checked = true;
  elements.trainingTopicFormTitle.textContent = "Nuevo tema";
  openTrainingPanel(elements.trainingTopicFormPanel);
  elements.trainingTopicTitle.focus();
}

function beginEditTrainingTopic(id) {
  const topic = trainingTopicById(id);
  if (!topic) return;
  elements.trainingTopicForm.reset();
  elements.trainingTopicId.value = topic.id;
  elements.trainingTopicCode.value = topic.code;
  elements.trainingTopicTitle.value = topic.title;
  elements.trainingTopicFocus.value = topic.securityFocus;
  elements.trainingTopicCategory.value = topic.category;
  elements.trainingTopicDuration.value = topic.defaultDurationMinutes;
  elements.trainingTopicModality.value = topic.defaultModality;
  elements.trainingTopicActive.checked = topic.active;
  elements.trainingTopicFormTitle.textContent = "Editar tema reutilizable";
  openTrainingPanel(elements.trainingTopicFormPanel);
}

async function saveTrainingTopic(event) {
  event.preventDefault();
  const id = elements.trainingTopicId.value;
  const payload = {
    code: elements.trainingTopicCode.value,
    title: elements.trainingTopicTitle.value,
    securityFocus: elements.trainingTopicFocus.value,
    category: elements.trainingTopicCategory.value,
    defaultDurationMinutes: Number(elements.trainingTopicDuration.value),
    defaultModality: elements.trainingTopicModality.value,
    active: elements.trainingTopicActive.checked
  };
  try {
    await api(
      id ? `/api/v1/admin/training/topics/${encodeURIComponent(id)}` : "/api/v1/admin/training/topics",
      trainingMutationOptions(id ? "PUT" : "POST", payload));
    await loadTraining(state.training?.selectedCycleId);
    showToast(id ? "Tema actualizado." : "Tema añadido al catálogo.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.code === "training_topic_exists"
        ? "Ese código de tema ya existe."
        : "No fue posible guardar el tema.");
    }
  }
}

async function openTrainingAttendance(id) {
  try {
    const detail = await api(
      `/api/v1/admin/training/sessions/${encodeURIComponent(id)}`);
    state.trainingUi.selectedSession = detail.session;
    state.trainingUi.sessionDetail = detail;
    elements.trainingAttendanceFile.value = "";
    elements.trainingAttendanceFileLabel.textContent = "Seleccionar archivo";
    renderTrainingAttendance();
    openTrainingPanel(elements.trainingAttendancePanel);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible abrir la asistencia de esta sesión.");
    }
  }
}

function renderTrainingAttendance() {
  const session = state.trainingUi.selectedSession;
  const detail = state.trainingUi.sessionDetail;
  if (!session || !detail) return;
  elements.trainingAttendanceTitle.textContent = session.title;
  elements.trainingAttendanceSubtitle.textContent =
    `${formatTrainingDate(session.scheduledDate)} · ${selectedTrainingCycle()?.name || "Ciclo"}. El Excel vigente pertenece únicamente a esta sesión.`;

  const formUrl = session.attendanceFormUrl || "";
  const presentationUrl = session.presentationUrl || "";
  elements.trainingAttendanceFormLink.classList.toggle("hidden", !formUrl);
  elements.trainingPresentationLink.classList.toggle("hidden", !presentationUrl);
  elements.trainingAttendanceFormLink.href = formUrl || "#";
  elements.trainingPresentationLink.href = presentationUrl || "#";
  elements.trainingResourceLinksEmpty.classList.toggle(
    "hidden", Boolean(formUrl || presentationUrl));

  const attendance = detail.attendance || [];
  elements.trainingAttendanceRows.innerHTML = attendance.length
    ? attendance.map((item) => `<tr>
        <td><span class="cell-primary">${escapeHtml(item.displayName)}</span><span class="cell-secondary">Fila ${formatNumber(item.sourceRowNumber)} del archivo</span></td>
        <td>${escapeHtml(item.agency)}</td>
        <td>${escapeHtml(item.department)}</td>
        <td title="${escapeHtml(formatDate(item.respondedAtUtc))}">${escapeHtml(formatDate(item.respondedAtUtc))}</td>
        <td>${escapeHtml(item.email || "No recopilado")}</td>
      </tr>`).join("")
    : emptyRow(5, "Todavía no se ha importado una exportación de Google Forms para esta sesión.");

  const imports = detail.imports || [];
  const current = imports.find((item) => item.isCurrent) || null;
  elements.trainingAttendanceImportSummary.innerHTML = current
    ? `<article><span>Archivo vigente</span><strong>${escapeHtml(current.originalFileName)}</strong><small>${escapeHtml(formatDate(current.importedAtUtc))}</small></article>
       <article><span>Respuestas aceptadas</span><strong>${formatNumber(current.acceptedRowCount)}</strong><small>personas únicas</small></article>
       <article><span>Duplicadas</span><strong>${formatNumber(current.duplicateRowCount)}</strong><small>omitidas automáticamente</small></article>
       <article><span>Rechazadas</span><strong>${formatNumber(current.rejectedRowCount)}</strong><small>filas incompletas</small></article>`
    : `<div class="training-empty"><strong>Sin archivo de asistencia.</strong><br>Exporte las respuestas de Google Forms y súbalas aquí.</div>`;

  const warnings = current?.warnings || [];
  elements.trainingAttendanceWarnings.classList.toggle("hidden", warnings.length === 0);
  elements.trainingAttendanceWarnings.innerHTML = warnings.length
    ? `<strong>Observaciones del procesamiento</strong><ul>${warnings.map((warning) => `<li>${escapeHtml(warning)}</li>`).join("")}</ul>`
    : "";

  elements.trainingAttendanceImportRows.innerHTML = imports.length
    ? imports.map((item) => `<article class="training-import-history-item ${item.isCurrent ? "current" : ""}">
        <div><strong>${escapeHtml(item.originalFileName)}</strong><span>${escapeHtml(formatDate(item.importedAtUtc))} · ${formatBytes(item.sizeBytes)} · ${formatNumber(item.acceptedRowCount)} aceptadas</span></div>
        <div><span class="training-import-state">${item.isCurrent ? "Vigente" : "Anterior"}</span><code title="SHA-256 completo: ${escapeHtml(item.sha256)}">SHA-256 ${escapeHtml(String(item.sha256).slice(0, 16))}…</code></div>
      </article>`).join("")
    : "<div class=\"training-empty\">No hay archivos importados.</div>";
}

function selectTrainingAttendanceFile() {
  const file = elements.trainingAttendanceFile.files?.[0];
  elements.trainingAttendanceFileLabel.textContent = file
    ? `${file.name} · ${formatBytes(file.size)}`
    : "Seleccionar archivo";
}

async function uploadTrainingAttendance() {
  const session = state.trainingUi.selectedSession;
  const file = elements.trainingAttendanceFile.files?.[0];
  if (!session || !file) {
    showToast("Seleccione el Excel o CSV exportado desde Google Forms.");
    return;
  }
  if (!/\.(xlsx|csv)$/i.test(file.name)) {
    showToast("La asistencia debe cargarse como archivo .xlsx o .csv.");
    return;
  }
  const hasCurrent = (state.trainingUi.sessionDetail?.imports || [])
    .some((item) => item.isCurrent);
  if (hasCurrent && !window.confirm(
    "Este archivo reemplazará la vista vigente de asistencia. El archivo anterior continuará conservado como evidencia."
  )) {
    return;
  }

  elements.trainingAttendanceUpload.disabled = true;
  elements.trainingAttendanceUpload.textContent = "Procesando…";
  try {
    const detail = await api(
      `/api/v1/admin/training/sessions/${encodeURIComponent(session.id)}/attendance-imports`,
      {
        method: "POST",
        headers: {
          "Content-Type": file.type ||
            (file.name.toLowerCase().endsWith(".csv") ? "text/csv" :
              "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
          "X-Atlas-Request": "dashboard",
          "X-Atlas-File-Name": encodeURIComponent(file.name)
        },
        body: file
      });
    state.trainingUi.selectedSession = detail.session;
    state.trainingUi.sessionDetail = detail;
    state.training = await api(
      `/api/v1/admin/training?cycleId=${encodeURIComponent(state.training?.selectedCycleId || "")}`);
    elements.trainingAttendanceFile.value = "";
    elements.trainingAttendanceFileLabel.textContent = "Seleccionar archivo";
    renderTraining();
    renderTrainingAttendance();
    showToast(`${formatNumber(detail.attendance?.length || 0)} respuestas importadas correctamente.`);
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        attendance_file_too_large: "El archivo supera el límite permitido.",
        unsupported_attendance_file: "Utilice un archivo .xlsx o .csv.",
        missing_attendance_columns: "Faltan columnas: fecha/hora, nombre completo, agencia o departamento.",
        attendance_without_valid_rows: "El archivo no contiene respuestas completas.",
        invalid_xlsx_file: "El Excel no es un libro válido de respuestas.",
        invalid_csv_encoding: "El CSV debe estar guardado como UTF-8."
      };
      showToast(messages[error.code] || "No fue posible procesar el archivo de asistencia.");
    }
  } finally {
    elements.trainingAttendanceUpload.disabled = false;
    elements.trainingAttendanceUpload.textContent = "Procesar archivo";
  }
}

const caseEvidenceLabels = {
  machineName: "Equipo",
  agencyCode: "Agencia",
  connectivity: "Conectividad",
  lastSeenAtUtc: "Último reporte",
  compliance: "Estado de copia",
  backupProvider: "Proveedor",
  lastBackupAtUtc: "Última copia",
  bytesCopied: "Volumen copiado",
  destination: "Destino",
  detail: "Detalle",
  findings: "Hallazgos",
  capturedAtUtc: "Postura recibida",
  eventCode: "Evento Windows",
  category: "Categoría",
  outcome: "Resultado",
  occurredAtUtc: "Fecha del evento",
  sourceAddress: "Origen de red",
  userName: "Usuario",
  nasName: "Recurso NAS",
  path: "Ruta",
  probeStatus: "Estado del sondeo",
  lastProbeAtUtc: "Último sondeo",
  notificationId: "Aviso ATLAS",
  filesCopied: "Archivos copiados",
  filesSkipped: "Archivos sin cambios o excluidos",
  lastBackupStatus: "Resultado de la copia",
  securityWarnings: "Advertencias de recolección",
  severity: "Severidad",
  provider: "Proveedor del evento",
  manufacturer: "Fabricante",
  model: "Modelo",
  serialNumber: "Serie",
  capacity: "Capacidad"
};

function normalizeCaseSeverity(value) {
  return {
    CRITICAL: "CRITICAL",
    HIGH: "HIGH",
    ERROR: "HIGH",
    MEDIUM: "MEDIUM",
    WARNING: "MEDIUM",
    LOW: "LOW",
    INFORMATION: "INFO",
    INFO: "INFO"
  }[String(value || "MEDIUM").toUpperCase()] || "MEDIUM";
}

function casePriorityForSeverity(severity) {
  return {
    CRITICAL: "URGENT",
    HIGH: "HIGH",
    MEDIUM: "MEDIUM",
    LOW: "LOW",
    INFO: "LOW"
  }[severity] || "MEDIUM";
}

function caseDueHours(severity) {
  return {
    CRITICAL: 4,
    HIGH: 24,
    MEDIUM: 72,
    LOW: 168,
    INFO: 336
  }[severity] || 72;
}

function caseEvidenceValue(key, value) {
  if (value === null || value === undefined || value === "") {
    return "Sin datos";
  }
  if (key.endsWith("AtUtc")) {
    return formatDate(value);
  }
  if (key === "bytesCopied") {
    return formatBytes(value);
  }
  if (Array.isArray(value)) {
    return value.length ? value.join(", ") : "Ninguno";
  }
  if (typeof value === "boolean") {
    return value ? "Sí" : "No";
  }
  if (typeof value === "object") {
    return JSON.stringify(value);
  }
  return String(value);
}

function caseEvidenceMarkup(snapshot) {
  const entries = Object.entries(snapshot || {})
    .filter(([, value]) => value !== null && value !== undefined && value !== "");
  if (!entries.length) {
    return "";
  }
  return `
    <section class="case-source-evidence">
      <h4>Evidencia de origen</h4>
      <div class="case-evidence-grid">
        ${entries.map(([key, value]) => `
          <div>
            <span>${escapeHtml(caseEvidenceLabels[key] || key)}</span>
            <strong>${escapeHtml(caseEvidenceValue(key, value))}</strong>
          </div>`).join("")}
      </div>
    </section>`;
}

function caseFindingToken(finding) {
  return encodeURIComponent(`${finding.sourceKind}|${finding.sourceKey}`);
}

function caseFindingButton(finding, label = "Preparar expediente") {
  if (!canManageCases() || !finding?.sourceKind || !finding?.sourceKey) {
    return "";
  }
  const token = caseFindingToken(finding);
  state.cases.findingRegistry.set(token, finding);
  return `<button class="case-source-action" type="button" data-case-finding="${escapeHtml(token)}">${escapeHtml(label)}</button>`;
}

function attachCaseFindingActions(container) {
  container?.querySelectorAll("[data-case-finding]").forEach((button) => {
    button.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation();
      const finding = state.cases.findingRegistry.get(button.dataset.caseFinding || "");
      if (finding) {
        void prepareCaseFromFinding(finding);
      }
    });
  });
}

function ensureCaseDeviceOption(finding) {
  if (!finding.deviceId) {
    return;
  }
  let option = Array.from(elements.caseDevices.options)
    .find((candidate) => candidate.value === finding.deviceId);
  if (!option) {
    option = document.createElement("option");
    option.value = finding.deviceId;
    option.textContent = `${finding.machineName || "Equipo"} · ${finding.agencyCode || "Sin agencia"}`;
    elements.caseDevices.appendChild(option);
  }
  option.selected = true;
}

async function prepareCaseFromFinding(rawFinding) {
  if (!canManageCases()) {
    showToast("Tu rol no permite crear expedientes.");
    return;
  }
  const severity = normalizeCaseSeverity(rawFinding.severity);
  const finding = {
    type: "INCIDENT",
    title: "Situación detectada por ATLAS",
    description: "ATLAS preparó este registro para revisión humana.",
    source: "ATLAS",
    priority: casePriorityForSeverity(severity),
    snapshot: {},
    ...rawFinding,
    severity
  };
  try {
    const query = new URLSearchParams({
      sourceKind: finding.sourceKind,
      sourceKey: finding.sourceKey
    });
    const existing = await api(`/api/v1/admin/cases/by-source?${query}`);
    closeNotificationPanel();
    switchView("cases");
    state.cases.selected = existing;
    renderCaseDetail(existing);
    showToast(`Ya existe ${existing.case.caseNumber} para esta situación.`);
    return;
  } catch (error) {
    if (error.message === "unauthorized") {
      return;
    }
    if (error.status !== 404) {
      showToast("No fue posible comprobar expedientes existentes.");
      return;
    }
  }

  closeNotificationPanel();
  switchView("cases");
  beginCreateCase();
  state.cases.preparedSource = {
    sourceKind: finding.sourceKind,
    sourceKey: finding.sourceKey,
    sourceEntityId: finding.sourceEntityId || null,
    sourceSnapshot: finding.snapshot || {}
  };
  elements.caseType.value = finding.type;
  elements.caseTitle.value = finding.title;
  elements.caseDescription.value = finding.description;
  elements.caseSource.value = finding.source;
  elements.caseSeverity.value = finding.severity;
  elements.casePriority.value = finding.priority || casePriorityForSeverity(finding.severity);
  elements.caseAgency.value = finding.agencyCode || "";
  elements.caseDueAt.value = formatDateTimeLocal(
    new Date(Date.now() + (caseDueHours(finding.severity) * 3_600_000)));
  ensureCaseDeviceOption(finding);
  elements.caseEvidenceSummary.textContent =
    `${finding.source}. Revise el asunto, la severidad, el responsable y la fecha objetivo antes de confirmar.`;
  elements.caseEvidencePanel.classList.remove("hidden");
  elements.caseTitle.focus();
}

function renderCaseSelectors() {
  const agencyOptions = state.agencies
    .filter((agency) => agency.enabled)
    .map((agency) =>
      `<option value="${escapeHtml(agency.agencyCode)}">${escapeHtml(agency.displayName || agency.agencyCode)}</option>`)
    .join("");
  const filterValue = state.cases.filters.agency;
  const formValue = elements.caseAgency.value;
  elements.caseAgencyFilter.innerHTML =
    "<option value=\"\">Todas las agencias</option>" + agencyOptions;
  elements.caseAgencyFilter.value = filterValue;
  elements.caseAgency.innerHTML =
    "<option value=\"\">Sin agencia</option>" + agencyOptions;
  elements.caseAgency.value = formValue;

  const selectedDevices = new Set(
    Array.from(elements.caseDevices.selectedOptions || []).map((option) => option.value));
  elements.caseDevices.innerHTML = (state.devices.items || [])
    .map((device) => `
      <option value="${escapeHtml(device.id)}">
        ${escapeHtml(device.machineName)} · ${escapeHtml(device.agencyCode)}
      </option>`)
    .join("");
  Array.from(elements.caseDevices.options).forEach((option) => {
    option.selected = selectedDevices.has(option.value);
  });
}

function renderCases() {
  if (!elements.caseRows) {
    return;
  }
  const page = state.cases;
  const summary = page.summary || {};
  elements.caseTotal.textContent = formatNumber(summary.total);
  elements.caseOpen.textContent = formatNumber(summary.open);
  elements.caseCritical.textContent = formatNumber(summary.criticalOpen);
  elements.caseOverdue.textContent = formatNumber(summary.overdue);
  elements.caseUnassigned.textContent = formatNumber(summary.unassigned);
  elements.newCaseButton.classList.toggle("hidden", !canManageCases());
  renderCaseSelectors();

  const items = Array.isArray(page.items) ? page.items : [];
  elements.caseRows.innerHTML = items.length
    ? items.map((item) => {
      const overdue = item.dueAtUtc &&
        new Date(item.dueAtUtc).getTime() < Date.now() &&
        !["CLOSED", "ACCEPTED_EXCEPTION"].includes(item.status);
      return `
        <tr class="clickable case-row" data-case-id="${escapeHtml(item.id)}">
          <td><span class="cell-primary case-number">${escapeHtml(item.caseNumber)}</span><span class="cell-secondary">${formatNumber(item.deviceCount)} activo${Number(item.deviceCount) === 1 ? "" : "s"}</span></td>
          <td><span class="cell-primary">${escapeHtml(item.title)}</span><span class="cell-secondary">${escapeHtml(caseTypeLabels[item.type] || item.type)}</span></td>
          <td><span class="case-severity-badge ${String(item.severity).toLowerCase()}">${escapeHtml(caseSeverityLabels[item.severity] || item.severity)}</span></td>
          <td><span class="case-status-badge ${String(item.status).toLowerCase()}">${escapeHtml(caseStatusLabels[item.status] || item.status)}</span></td>
          <td>${escapeHtml(item.agencyName || item.agencyCode || "General")}</td>
          <td>${escapeHtml(item.assignedTo || "Sin asignar")}</td>
          <td><span class="cell-primary">${escapeHtml(formatRelative(item.updatedAtUtc))}</span>${overdue ? "<span class=\"cell-secondary case-overdue\">Fecha objetivo vencida</span>" : ""}</td>
          <td><button class="secondary-button compact case-open-button" type="button" data-case-id="${escapeHtml(item.id)}">Abrir</button></td>
        </tr>`;
    }).join("")
    : emptyRow(8, "No hay expedientes que coincidan con los filtros.");

  elements.caseRows.querySelectorAll("[data-case-id]").forEach((element) => {
    element.addEventListener("click", (event) => {
      event.stopPropagation();
      void openCase(element.dataset.caseId);
    });
  });
  const totalPages = Math.max(1, Math.ceil(Number(page.total || 0) / Number(page.pageSize || 20)));
  elements.casePaginationLabel.textContent = page.total
    ? `Página ${page.page} de ${totalPages} · ${formatNumber(page.total)} expedientes`
    : "0 expedientes";
  elements.casePreviousPage.disabled = Number(page.page || 1) <= 1;
  elements.caseNextPage.disabled = Number(page.page || 1) >= totalPages;
}

async function loadCases() {
  const sourceRequest = sourceBegin("cases");
  try {
    const page = await api(`/api/v1/admin/cases?${buildCaseQuery()}`);
    if (sourceRequest !== sourceRecord("cases").request) return;
    sourceSuccess("cases", sourceRequest);
    state.cases = { ...state.cases, ...page };
    renderCases();
  } catch (error) {
    if (sourceRequest !== sourceRecord("cases").request) return;
    sourceFailure("cases", sourceRequest);
    if (error.message !== "unauthorized") {
      showToast("No fue posible actualizar los expedientes.");
    }
  }
}

function resetCaseForm() {
  state.cases.editingId = null;
  state.cases.preparedSource = null;
  elements.caseForm.reset();
  elements.caseId.value = "";
  elements.caseType.value = "INCIDENT";
  elements.caseSeverity.value = "MEDIUM";
  elements.casePriority.value = "MEDIUM";
  elements.caseStatus.value = "NEW";
  elements.caseStatus.disabled = true;
  elements.caseChangeNote.closest("label").classList.add("hidden");
  elements.caseEvidencePanel.classList.add("hidden");
  elements.caseEvidenceSummary.textContent =
    "Revise la información antes de crear el expediente.";
  Array.from(elements.caseDevices.options).forEach((option) => {
    option.selected = false;
  });
}

function beginCreateCase() {
  resetCaseForm();
  elements.caseFormTitle.textContent = "Nuevo expediente";
  elements.caseSave.textContent = "Crear expediente";
  elements.caseEditorPanel.classList.remove("hidden");
  elements.caseDetailPanel.classList.add("hidden");
  elements.caseTitle.focus();
  elements.caseEditorPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function beginEditCase() {
  const detail = state.cases.selected;
  if (!detail || !canManageCases()) {
    return;
  }
  const item = detail.case;
  state.cases.preparedSource = null;
  elements.caseEvidencePanel.classList.add("hidden");
  state.cases.editingId = item.id;
  elements.caseId.value = item.id;
  elements.caseType.value = item.type;
  elements.caseTitle.value = item.title;
  elements.caseDescription.value = detail.description || "";
  elements.caseSource.value = detail.source || "";
  elements.caseSeverity.value = item.severity;
  elements.casePriority.value = item.priority;
  elements.caseStatus.value = item.status;
  elements.caseStatus.disabled = false;
  elements.caseAgency.value = item.agencyCode || "";
  elements.caseAssignedTo.value = item.assignedTo || "";
  elements.caseOwner.value = item.institutionalOwner || "";
  elements.caseDueAt.value = formatDateTimeLocal(item.dueAtUtc);
  const selectedIds = new Set((detail.devices || []).map((device) => device.id));
  Array.from(elements.caseDevices.options).forEach((option) => {
    option.selected = selectedIds.has(option.value);
  });
  elements.caseChangeNote.value = "";
  elements.caseChangeNote.closest("label").classList.remove("hidden");
  elements.caseFormTitle.textContent = `Editar ${item.caseNumber}`;
  elements.caseSave.textContent = "Guardar cambios";
  elements.caseEditorPanel.classList.remove("hidden");
  elements.caseDetailPanel.classList.add("hidden");
  elements.caseEditorPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function closeCaseEditor() {
  elements.caseEditorPanel.classList.add("hidden");
  resetCaseForm();
}

function closeCaseDetail() {
  elements.caseDetailPanel.classList.add("hidden");
  state.cases.selected = null;
}

function caseTimelineMarkup(timeline) {
  if (!Array.isArray(timeline) || !timeline.length) {
    return "<div class=\"empty-state\"><p>Este expediente todavía no tiene actividad.</p></div>";
  }
  return timeline.map((entry) => {
    const transition = entry.fromStatus && entry.toStatus
      ? `<span>${escapeHtml(caseStatusLabels[entry.fromStatus] || entry.fromStatus)} → ${escapeHtml(caseStatusLabels[entry.toStatus] || entry.toStatus)}</span>`
      : "";
    return `
      <article class="case-timeline-entry">
        <span class="case-timeline-dot"></span>
        <div>
          <header><strong>${escapeHtml(caseTimelineLabels[entry.eventType] || entry.eventType)}</strong><time>${escapeHtml(formatDate(entry.occurredAtUtc))}</time></header>
          ${transition}
          ${entry.comment ? `<p>${escapeHtml(entry.comment)}</p>` : ""}
          <small>${escapeHtml(entry.actorId)} · ${escapeHtml(roleLabels[entry.actorRole] || entry.actorRole)}</small>
        </div>
      </article>`;
  }).join("");
}

function renderCaseDetail(detail) {
  const item = detail.case;
  elements.caseDetailMeta.textContent =
    `${caseTypeLabels[item.type] || item.type} · ${caseSeverityLabels[item.severity] || item.severity}`;
  elements.caseDetailTitle.textContent = `${item.caseNumber} · ${item.title}`;
  elements.caseEditButton.classList.toggle("hidden", !canManageCases());
  elements.caseCommentForm.classList.toggle("hidden", !canManageCases());
  const devices = (detail.devices || []).length
    ? detail.devices.map((device) =>
      `<span class="case-device-chip">${escapeHtml(device.machineName)}<small>${escapeHtml(device.agencyCode)}</small></span>`).join("")
    : "<span class=\"case-empty-value\">Sin activos relacionados</span>";
  elements.caseDetailBody.innerHTML = `
    <div class="case-detail-grid">
      <div><span>Estado</span><strong><span class="case-status-badge ${String(item.status).toLowerCase()}">${escapeHtml(caseStatusLabels[item.status] || item.status)}</span></strong></div>
      <div><span>Prioridad</span><strong>${escapeHtml(casePriorityLabels[item.priority] || item.priority)}</strong></div>
      <div><span>Agencia</span><strong>${escapeHtml(item.agencyName || item.agencyCode || "General")}</strong></div>
      <div><span>Asignado a</span><strong>${escapeHtml(item.assignedTo || "Sin asignar")}</strong></div>
      <div><span>Responsable institucional</span><strong>${escapeHtml(item.institutionalOwner || "Sin definir")}</strong></div>
      <div><span>Fecha objetivo</span><strong>${escapeHtml(formatDate(item.dueAtUtc))}</strong></div>
      <div><span>Origen</span><strong>${escapeHtml(detail.source || "No especificado")}</strong></div>
      <div><span>Última actualización</span><strong>${escapeHtml(formatDate(item.updatedAtUtc))}</strong></div>
    </div>
    ${detail.sourceKind ? caseEvidenceMarkup(detail.sourceSnapshot) : ""}
    <section class="case-description"><h4>Descripción</h4><p>${escapeHtml(detail.description)}</p></section>
    <section class="case-related-devices"><h4>Activos relacionados</h4><div>${devices}</div></section>
    <section class="case-timeline"><h4>Línea de tiempo</h4>${caseTimelineMarkup(detail.timeline)}</section>`;
  elements.caseEditorPanel.classList.add("hidden");
  elements.caseDetailPanel.classList.remove("hidden");
  elements.caseDetailPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function openCase(id) {
  try {
    const detail = await api(`/api/v1/admin/cases/${encodeURIComponent(id)}`);
    state.cases.selected = detail;
    renderCaseDetail(detail);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible abrir el expediente.");
    }
  }
}

function caseFormPayload(includeStatus) {
  const dueAt = elements.caseDueAt.value;
  const payload = {
    type: elements.caseType.value,
    title: elements.caseTitle.value,
    description: elements.caseDescription.value,
    source: elements.caseSource.value || null,
    severity: elements.caseSeverity.value,
    priority: elements.casePriority.value,
    agencyCode: elements.caseAgency.value || null,
    deviceIds: Array.from(elements.caseDevices.selectedOptions).map((option) => option.value),
    assignedTo: elements.caseAssignedTo.value || null,
    institutionalOwner: elements.caseOwner.value || null,
    dueAtUtc: dueAt ? new Date(dueAt).toISOString() : null
  };
  if (includeStatus) {
    payload.status = elements.caseStatus.value;
    payload.changeNote = elements.caseChangeNote.value || null;
  } else if (state.cases.preparedSource) {
    Object.assign(payload, state.cases.preparedSource);
  }
  return payload;
}

async function saveCase(event) {
  event.preventDefault();
  const id = state.cases.editingId;
  elements.caseSave.disabled = true;
  try {
    const detail = await api(id
      ? `/api/v1/admin/cases/${encodeURIComponent(id)}`
      : "/api/v1/admin/cases", {
      method: id ? "PUT" : "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify(caseFormPayload(Boolean(id)))
    });
    state.cases.selected = detail;
    closeCaseEditor();
    await loadCases();
    renderCaseDetail(detail);
    showToast(id ? "Expediente actualizado y auditado." : "Expediente creado correctamente.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      if (error.code === "case_source_exists" && error.detail) {
        closeCaseEditor();
        await openCase(error.detail);
        showToast("La situación ya tenía un expediente abierto; ATLAS abrió el existente.");
      } else {
        showToast(error.code === "invalid_case_reference"
          ? "La agencia o uno de los activos ya no está disponible."
          : error.code === "invalid_case"
            ? "Revise el asunto, la descripción y los valores seleccionados."
            : error.status >= 500
              ? "El servidor no pudo guardar el expediente. No se conservaron cambios."
              : "No fue posible guardar el expediente.");
      }
    }
  } finally {
    elements.caseSave.disabled = false;
  }
}

async function addCaseComment(event) {
  event.preventDefault();
  const detail = state.cases.selected;
  const comment = elements.caseComment.value.trim();
  if (!detail || !comment) {
    return;
  }
  elements.caseCommentSubmit.disabled = true;
  try {
    const updated = await api(
      `/api/v1/admin/cases/${encodeURIComponent(detail.case.id)}/comments`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "X-Atlas-Request": "dashboard"
        },
        body: JSON.stringify({ comment })
      });
    state.cases.selected = updated;
    elements.caseComment.value = "";
    renderCaseDetail(updated);
    await loadCases();
    showToast("Seguimiento incorporado al expediente.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible registrar el seguimiento.");
    }
  } finally {
    elements.caseCommentSubmit.disabled = false;
  }
}

function renderNetworkDiscovery() {
  if (globalThis.AtlasNetworkScanner) { globalThis.AtlasNetworkScanner.render(); return; }
  if (!state.network) {
    return;
  }

  const network = state.network;
  const scopes = Array.isArray(network.scopes) ? network.scopes : [];
  const assets = network.assets || { page: 1, pageSize: 25, total: 0, items: [] };
  const canManage = state.session?.role === "Administrator";
  const engineReady = network.enabled && network.engineAvailable;
  const engineLabel = !network.enabled
    ? "Pendiente de habilitar en el servidor"
    : !network.engineAvailable
      ? "Nmap no disponible"
      : network.scanInProgress
        ? "Escaneo autorizado en curso"
        : "Motor listo";

  elements.networkEngineState.textContent = engineLabel;
  elements.networkEngineDot.className = `pulse-dot ${
    engineReady ? network.scanInProgress ? "working" : "online" : "attention"
  }`;
  elements.networkEngineMeta.textContent = network.nextAutomaticScanAtUtc
    ? `Motor ${network.engine} · proximo ciclo ${formatRelative(network.nextAutomaticScanAtUtc)}`
    : network.enabled && !network.automaticEnabled
      ? `Motor ${network.engine} · programación pausada, disponible al solicitar Verificar`
      : `Motor ${network.engine} · solo alcances registrados por ATLAS`;
  elements.scanAllNetworks.disabled = !canManage || !engineReady;
  elements.scanAllNetworks.textContent = `Verificar las ${formatNumber(scopes.length)} redes`;
  elements.networkEngineMeta.textContent += ` · ICMP ${network.icmpAvailable ? "disponible" : "pendiente de habilitar"}`;

  const selectedScope = state.networkPage.scope;
  elements.networkScopeFilter.innerHTML = [
    '<option value="">Todas las redes</option>',
    ...scopes.map((scope) =>
      `<option value="${escapeHtml(scope.code)}">${escapeHtml(scope.displayName)} · ${escapeHtml(scope.network)}</option>`)
  ].join("");
  elements.networkScopeFilter.value = selectedScope;

  elements.networkScopeCards.innerHTML = scopes.length
    ? scopes.map((scope) => {
      const status = networkScanStatusLabels[scope.lastScanStatus] ||
        scope.lastScanStatus;
      const statusClass = scope.lastScanStatus === "SUCCEEDED"
        ? "online"
        : ["RUNNING", "QUEUED"].includes(scope.lastScanStatus)
          ? "working"
          : scope.lastScanStatus === "NEVER" ? "unknown" : "attention";
      return `
        <article class="network-scope-card">
          <div class="network-scope-title">
            <div>
              <strong>${escapeHtml(scope.displayName)}</strong>
              <small>${escapeHtml(scope.agencyCode)} · ${escapeHtml(scope.network)}</small>
            </div>
            <span class="network-scan-status ${statusClass}">${escapeHtml(status)}</span>
          </div>
          <div class="network-scope-metrics">
            <span><b>${formatNumber(scope.observedAssets)}</b> observadas</span>
            <span><b>${formatNumber(scope.notObservedAddresses)}</b> no observadas*</span>
          </div>
          <div class="network-scope-footer">
            <small>Gateway ${escapeHtml(scope.gateway)} · ${escapeHtml(formatRelative(scope.lastScanAtUtc))}<br>${network.automaticEnabled && scope.automaticEnabled ? `Automático · cada ${formatNumber(scope.intervalMinutes)} min` : "Programación pausada"}<br>Presencia completada: ${escapeHtml(formatRelative(scope.lastCompleteScanAtUtc))}</small>
            <div class="network-scope-actions">${canManage ? `<button class="secondary-button compact scan-network" type="button" data-scope="${escapeHtml(scope.code)}" ${!engineReady ? "disabled" : ""}>Verificar</button><button class="secondary-button compact" type="button" data-network-config="${escapeHtml(scope.code)}">Programación</button>` : ""}</div>
          </div>
        </article>`;
    }).join("")
    : '<article class="network-scope-card empty-state"><p>No hay redes autorizadas configuradas.</p></article>';

  const kindOptions = Object.entries(networkKindLabels)
    .map(([value, label]) => `<option value="${value}">${escapeHtml(label)}</option>`)
    .join("");
  elements.networkAssetRows.innerHTML = Array.isArray(assets.items) && assets.items.length
    ? assets.items.map((asset) => {
      const identity = asset.linkedDeviceName || asset.linkedNasName ||
        asset.displayName || asset.hostname || "Sin nombre reportado";
      const scope = scopes.find((item) => item.code === asset.scopeCode);
      const fresh = Number.isFinite(new Date(asset.lastSeenAtUtc).getTime()) && Date.now() - new Date(asset.lastSeenAtUtc).getTime() <= Math.max(10,scope?.intervalMinutes || 30)*120000;
      const observed = Number(asset.consecutiveMisses || 0) === 0 && fresh;
      const computer = [asset.computerManufacturer, asset.computerModel]
        .filter(Boolean).join(" ");
      const board = [asset.baseBoardManufacturer, asset.baseBoardProduct]
        .filter(Boolean).join(" ");
      const hardware = computer || asset.vendor ||
        asset.operatingSystemHint || "No identificado";
      const hardwareDetail = board
        ? `Placa: ${board}`
        : asset.macAddress || asset.deviceTypeHint || "Sin detalle adicional";
      const services = Array.isArray(asset.services) ? asset.services : [];
      const serviceBadges = services.length
        ? `<div class="network-service-list">${services.slice(0, 4).map((service) => {
          const product = [service.product, service.version, service.extraInfo]
            .filter(Boolean).join(" ");
          return `<span class="network-service" title="${escapeHtml(product || service.name)}">${escapeHtml(`${service.port}/${service.protocol} ${service.name}`)}</span>`;
        }).join("")}${services.length > 4
          ? `<span class="network-service more">+${formatNumber(services.length - 4)}</span>`
          : ""}</div>`
        : '<span class="cell-secondary">Sin puertos identificados</span>';
      const source = asset.classificationSource === "MANUAL"
        ? "Manual"
        : asset.linkedDeviceId
          ? "Agente ATLAS"
          : asset.linkedNasId
            ? "NAS registrado"
            : asset.ipAddress === scope?.gateway
              ? "Gateway registrado"
              : "Nmap / DNS";
      const classification = canManage
        ? `<select class="network-kind-select" data-asset-id="${escapeHtml(asset.id)}" data-display-name="${escapeHtml(asset.displayName || "")}">${kindOptions}</select>`
        : escapeHtml(networkKindLabels[asset.deviceKind] || asset.deviceKind);
      return `
        <tr>
          <td><span class="cell-primary network-ip">${escapeHtml(asset.ipAddress)}</span><span class="cell-secondary">${escapeHtml(asset.agencyName)} &middot; ${escapeHtml(asset.scopeCode)}</span></td>
          <td><span class="cell-primary">${escapeHtml(identity)}</span><span class="cell-secondary">${escapeHtml(asset.hostname || "Sin nombre DNS")}</span>${asset.hardwareHistorical && asset.historicalDeviceName ? `<span class="cell-secondary">Referencia anterior: ${escapeHtml(asset.historicalDeviceName)}</span>` : ""}</td>
          <td><span class="cell-primary">${escapeHtml(hardware)}</span><span class="cell-secondary">${escapeHtml(hardwareDetail)}</span>${asset.hardwareHistorical ? '<span class="cell-secondary">Hardware del inventario anterior · pendiente de comprobar</span>' : ""}</td>
          <td>${serviceBadges}<span class="cell-secondary">${asset.servicesCheckStatus === "COMPLETE" ? "Comprobados" : asset.servicesCheckStatus === "PARTIAL" ? "Identificación parcial" : "Información histórica o pendiente"} · ${escapeHtml(formatRelative(asset.servicesCheckedAtUtc))}</span></td>
          <td>${classification}<span class="cell-secondary">${escapeHtml(source)} &middot; ${escapeHtml(({RECENT_AGENT:"Coincidencia reciente del agente",REGISTERED_NAS:"Destino registrado",REGISTERED_GATEWAY:"Gateway registrado",MANUAL:"Clasificación registrada",INFERRED:"Identificación probable",AMBIGUOUS:"Coincidencia ambigua",LEGACY:"Información anterior",UNKNOWN:"Por identificar"})[asset.identificationBasis] || "Por identificar")}</span></td>
          <td title="${escapeHtml(formatDate(asset.lastSeenAtUtc))}">${escapeHtml(formatRelative(asset.lastSeenAtUtc))}</td>
          <td><span class="network-presence ${observed ? "observed" : "missed"}">${observed ? "Observada" : !fresh ? "Sin observación reciente" : `No observada · ${formatNumber(asset.consecutiveMisses)} sondeo(s)`}</span><span class="cell-secondary">${escapeHtml((asset.presenceSources || []).filter(s=>s!=="ARP_CACHE").join(" + ") || "Fuente anterior")}</span></td>
          <td><button class="secondary-button compact" type="button" data-network-history="${escapeHtml(asset.id)}">Historial</button>${canManage ? `<button class="secondary-button compact save-network-kind" type="button" data-asset-id="${escapeHtml(asset.id)}">Guardar</button>` : ""}</td>
        </tr>`;
    }).join("")
    : emptyRow(8, "Todavia no hay dispositivos observados con estos filtros.");

  elements.networkAssetRows.querySelectorAll(".network-kind-select")
    .forEach((select) => { select.value = select.closest("tr") ?
      assets.items.find((item) => item.id === select.dataset.assetId)?.deviceKind || "UNKNOWN" : "UNKNOWN"; });
  elements.networkAssetRows.querySelectorAll(".save-network-kind")
    .forEach((button) => button.addEventListener(
      "click", () => void updateNetworkAsset(button.dataset.assetId)));
  document.querySelectorAll(".scan-network").forEach((button) => {
    button.addEventListener(
      "click", () => void queueNetworkScan(button.dataset.scope || null));
  });
  renderPagination(
    assets,
    elements.networkPaginationLabel,
    elements.networkPreviousPage,
    elements.networkNextPage,
    "dispositivos");
}

async function queueNetworkScan(scopeCode = null) {
  try {
    const result = await api("/api/v1/admin/network-discovery/scans", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify({ scopeCode })
    });
    showToast(`${formatNumber(result.queued)} red(es) añadidas a la cola.${result.alreadyQueued ? ` ${formatNumber(result.alreadyQueued)} ya tenían un sondeo pendiente.` : ""}`);
    await loadNetworkDiscovery();
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        network_discovery_disabled: "Habilita primero el descubrimiento en el servidor.",
        nmap_unavailable: "Nmap no esta instalado o no coincide con la ruta autorizada.",
        network_probe_unavailable: "El motor de red no está disponible. Revisa las herramientas del servidor.",
        scan_already_running: "Ya existe un ciclo de descubrimiento en curso.",
        network_scope_not_found: "La red solicitada no pertenece al catalogo autorizado."
      };
      showToast(messages[error.code] || "No fue posible iniciar el sondeo.");
    }
  }
}

async function updateNetworkAsset(assetId) {
  const select = document.querySelector(
    `.network-kind-select[data-asset-id="${CSS.escape(assetId)}"]`);
  if (!select) {
    return;
  }
  try {
    await api(`/api/v1/admin/network-discovery/assets/${encodeURIComponent(assetId)}`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify({
        deviceKind: select.value,
        displayName: select.dataset.displayName || null
      })
    });
    showToast("Clasificacion de red guardada y auditada.");
    await loadNetworkDiscovery();
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible guardar la clasificacion.");
    }
  }
}

const securityEventLabels = {
  InteractiveLogon: "Inicio interactivo",
  RemoteInteractiveLogon: "Inicio remoto (RDP)",
  NetworkLogon: "Acceso de red",
  ExplicitCredentialLogon: "Credencial explícita",
  RemoteAuthentication: "Autenticación RDP",
  RemoteSessionLogon: "Sesión remota iniciada",
  RemoteSessionLogoff: "Sesión remota cerrada",
  RemoteSessionDisconnected: "Sesión remota desconectada",
  RemoteSessionReconnected: "Sesión remota reconectada",
  InteractiveSessionLogon: "Sesión local iniciada",
  InteractiveSessionLogoff: "Sesión local cerrada",
  SessionShellStarted: "Escritorio de sesión iniciado",
  SessionDisconnected: "Sesión desconectada",
  SessionReconnected: "Sesión reconectada",
  BatchLogon: "Inicio de tarea programada",
  ServiceLogon: "Inicio de servicio",
  NetworkCleartextLogon: "Acceso de red con credencial expuesta",
  NewCredentialsLogon: "Inicio con credenciales nuevas",
  SessionLogoff: "Sesión finalizada",
  UserInitiatedLogoff: "Cierre solicitado por usuario",
  AuditLogCleared: "Registro de auditoría borrado",
  SystemTimeChanged: "Hora del sistema modificada",
  PrivilegedLogon: "Sesión con privilegios especiales",
  ServiceInstalled: "Servicio instalado",
  AuditPolicyChanged: "Política de auditoría modificada",
  LocalAccountCreated: "Cuenta local creada",
  LocalAccountEnabled: "Cuenta local habilitada",
  LocalAccountDisabled: "Cuenta local deshabilitada",
  LocalAccountDeleted: "Cuenta local eliminada",
  LocalGroupMemberAdded: "Miembro agregado a grupo local",
  LocalGroupMemberRemoved: "Miembro retirado de grupo local",
  FirewallRuleAdded: "Regla de firewall agregada",
  FirewallRuleChanged: "Regla de firewall modificada",
  FirewallRuleRemoved: "Regla de firewall eliminada",
  FirewallSettingChanged: "Configuración de firewall modificada",
  SystemStartup: "Sistema iniciado",
  SystemShutdown: "Sistema apagado correctamente",
  UnexpectedShutdown: "Apagado inesperado",
  MalwareDetected: "Amenaza detectada por Defender",
  MalwareActionTaken: "Acción de Defender aplicada",
  AttackSurfaceReductionBlocked: "Ataque bloqueado por ASR",
  RealtimeProtectionDisabled: "Protección en tiempo real deshabilitada",
  AntimalwareConfigurationChanged: "Configuración antimalware modificada",
  AntimalwareEngineFailure: "Fallo del motor antimalware",
  AntispywareDisabled: "Protección antispyware deshabilitada",
  AntivirusDisabled: "Antivirus deshabilitado",
  TamperProtectionBlocked: "Protección contra alteraciones actuó"
};

const securitySeverityLabels = {
  Critical: "Crítica",
  High: "Alta",
  Medium: "Media",
  Low: "Baja",
  Information: "Informativa"
};

const securityOutcomeLabels = {
  Success: ["Efectivo", "healthy"],
  Failure: ["Fallido", "attention"],
  Information: ["Informativo", "unknown"],
  Detected: ["Detectado", "critical"],
  Changed: ["Modificado", "medium"],
  Blocked: ["Bloqueado", "healthy"]
};

function securityControl(value, inverted = false) {
  if (value === null || value === undefined) {
    return '<span class="security-chip unknown">Sin datos</span>';
  }
  const healthy = inverted ? value === false : value === true;
  return `<span class="security-chip ${healthy ? "healthy" : "attention"}">${healthy ? "Correcto" : "Atención"}</span>`;
}

function postureObservation(device) {
  const state = device.observationState || (!device.capturedAtUtc ? "MISSING" : "UNCLASSIFIED");
  return {
    state,
    label: ({ FRESH: "Reporte reciente", STALE: "Reporte vencido", MISSING: "Sin reporte", FUTURE: "Fecha del reporte por revisar", UNCLASSIFIED: "Vigencia por confirmar" })[state] || "Vigencia por confirmar",
    current: state === "FRESH",
    historical: ["STALE", "FUTURE", "UNCLASSIFIED"].includes(state)
  };
}
function postureControl(device, value, inverted = false) {
  const observation = postureObservation(device);
  if (observation.current) return securityControl(value, inverted);
  if (!observation.historical || value == null) return '<span class="security-chip unknown">Sin datos actuales</span>';
  const label = value === true ? "Sí" : "No";
  return `<span class="posture-history-control">Última observación: ${label}<br>Protección actual por confirmar</span>`;
}

function postureFinding(device) {
  const observation = postureObservation(device);
  if (["STALE", "MISSING", "FUTURE"].includes(observation.state)) return {
    sourceKind: "POSTURE", sourceKey: device.deviceId, sourceEntityId: device.deviceId,
    type: "VULNERABILITY", title: `Postura pendiente de actualizar · ${device.machineName}`,
    description: `${observation.label}. La fecha observada es ${formatDate(device.capturedAtUtc)}. Los valores históricos no confirman la protección actual; consulte el agente y registre una observación reciente.`,
    source: "Postura de seguridad", severity: "MEDIUM", agencyCode: device.agencyCode, deviceId: device.deviceId, machineName: device.machineName,
    snapshot: { capturedAtUtc: device.capturedAtUtc, observationState: observation.state }
  };
  const findings = [];
  if (!device.capturedAtUtc) findings.push("El equipo no ha enviado postura de seguridad");
  if (device.firewallEnabled === false) findings.push("Firewall deshabilitado");
  if (device.systemDriveProtected === false) findings.push("Unidad del sistema sin BitLocker confirmado");
  if (device.secureBootEnabled === false) findings.push("Secure Boot deshabilitado");
  if (device.tpmPresent === false) findings.push("TPM no detectado");
  if (device.smb1Enabled === true) findings.push("SMBv1 habilitado");
  if (device.remoteDesktopEnabled === true) findings.push("Escritorio remoto habilitado; validar autorización");
  if (device.pendingReboot === true) findings.push("Reinicio pendiente");
  const collectionWarnings = Array.isArray(device.collectionWarnings)
    ? device.collectionWarnings.filter(Boolean)
    : [];
  collectionWarnings.forEach((warning) => findings.push(`Telemetría: ${warning}`));
  if (!findings.length) {
    return null;
  }
  const high = device.firewallEnabled === false || device.smb1Enabled === true;
  return {
    sourceKind: "POSTURE",
    sourceKey: device.deviceId,
    sourceEntityId: device.deviceId,
    type: "VULNERABILITY",
    title: `Postura de seguridad · ${device.machineName}`,
    description: `ATLAS detectó los siguientes puntos para validación:\n\n- ${findings.join("\n- ")}\n\nConfirme la condición, documente las excepciones autorizadas y registre el tratamiento aplicado.`,
    source: "Postura de seguridad",
    severity: high ? "HIGH" : "MEDIUM",
    agencyCode: device.agencyCode,
    deviceId: device.deviceId,
    machineName: device.machineName,
    snapshot: {
      machineName: device.machineName,
      agencyCode: device.agencyCode,
      capturedAtUtc: device.capturedAtUtc,
      findings,
      securityWarnings: collectionWarnings
    }
  };
}

function securityEventFinding(event) {
  const severity = normalizeCaseSeverity(event.severity);
  const outcome = String(event.outcome || "").toUpperCase();
  const actionable = severity === "CRITICAL" || severity === "HIGH" ||
    ["FAILURE", "FAILED", "ERROR", "DENIED", "DETECTED"].some((value) =>
      outcome.includes(value));
  if (!actionable) {
    return null;
  }
  const userName = [event.userDomain, event.userName]
    .filter(Boolean).join("\\") || "No informado";
  const detail = [event.failureReason, event.details, event.processName]
    .filter(Boolean).join(" · ") || "Sin detalle adicional";
  return {
    sourceKind: "SECURITY",
    sourceKey: String(event.id),
    sourceEntityId: String(event.id),
    type: "INCIDENT",
    title: `${securityEventLabels[event.category] || event.category} · ${event.machineName}`,
    description: `Evento de seguridad detectado en ${event.machineName}.\n\nResultado: ${event.outcome || "No informado"}.\nUsuario: ${userName}.\nDetalle: ${detail}.\n\nRevise el contexto antes de clasificarlo como incidente, falso positivo o actividad autorizada.`,
    source: "Bitácora de seguridad",
    severity,
    agencyCode: event.agencyCode,
    deviceId: event.deviceId,
    machineName: event.machineName,
    snapshot: {
      machineName: event.machineName,
      agencyCode: event.agencyCode,
      eventCode: event.eventCode,
      category: securityEventLabels[event.category] || event.category,
      outcome: event.outcome,
      severity: event.severity,
      occurredAtUtc: event.occurredAtUtc,
      sourceAddress: event.sourceAddress,
      userName,
      provider: event.provider,
      detail
    }
  };
}

function renderPagination(
  pageData,
  labelElement,
  previousButton,
  nextButton,
  itemLabel
) {
  const page = Number(pageData?.page || 1);
  const pageSize = Number(pageData?.pageSize || 1);
  const total = Number(pageData?.total || 0);
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  const first = total ? ((page - 1) * pageSize) + 1 : 0;
  const last = total ? Math.min(page * pageSize, total) : 0;
  labelElement.textContent = total
    ? `${formatNumber(first)}–${formatNumber(last)} de ${formatNumber(total)} ${itemLabel} · Página ${formatNumber(page)} de ${formatNumber(totalPages)}`
    : `0 ${itemLabel} · Página 1 de 1`;
  previousButton.disabled = page <= 1;
  nextButton.disabled = page >= totalPages;
}

function renderSecurity() {
  if (!state.security) {
    return;
  }
  const summary = state.security.summary;
  elements.securityDeviceSortBy.value = state.securityPages.deviceSort;
  elements.securityDeviceSortDirection.textContent = securityDeviceDirectionLabel();
  elements.securityEventSortBy.value = state.securityPages.eventSort;
  elements.securityEventSortDirection.textContent = securityEventDirectionLabel();
  const options = Array.isArray(state.security.deviceOptions)
    ? state.security.deviceOptions
    : [];
  const selectedDeviceExists = options.some((item) =>
    item.deviceId === state.securityPages.eventDeviceId);
  if (state.securityPages.eventDeviceId && !selectedDeviceExists) {
    state.securityPages.eventDeviceId = "";
  }
  elements.securityEventDeviceFilter.innerHTML =
    "<option value=\"\">Todos los equipos</option>" +
    options.map((item) => `<option value="${escapeHtml(item.deviceId)}">${escapeHtml(item.machineName)} · ${escapeHtml(item.agencyCode)}</option>`).join("");
  elements.securityEventDeviceFilter.value = state.securityPages.eventDeviceId;
  const selectedDevice = options.find((item) =>
    item.deviceId === state.securityPages.eventDeviceId);
  elements.securityEventHeading.textContent = selectedDevice
    ? `Bitácora de ${selectedDevice.machineName}`
    : "Bitácoras de seguridad por equipo";
  elements.securityEventDescription.textContent = selectedDevice
    ? `Eventos registrados para ${selectedDevice.machineName} en ${selectedDevice.agencyCode}.`
    : "Seleccione un equipo o consulte la actividad consolidada de toda la flota.";
  const total = Number(summary.totalDevices || 0);
  const reporting = Number(summary.reportingDevices || 0);
  elements.securityCoverage.textContent = total
    ? `${Math.round((reporting / total) * 100)}%`
    : "0%";
  elements.securityCoverageMeta.textContent = `${reporting} de ${total} equipos · reportes de las últimas ${summary.freshnessHours ?? 24} h` +
    (Number.isInteger(summary.staleDevices) ? ` · ${summary.staleDevices} vencidos` : "") +
    (Number.isInteger(summary.missingDevices) ? ` · ${summary.missingDevices} sin reporte` : "");
  elements.securityFirewall.textContent = `${summary.firewallProtectedDevices || 0}/${total}`;
  elements.securityBitLocker.textContent = `${summary.bitLockerProtectedDevices || 0}/${total}`;
  elements.securityFailedLogons.textContent = formatNumber(summary.failedLogonsLast24Hours);
  elements.securityRemoteMeta.textContent =
    `${formatNumber(summary.successfulRemoteEventsLast24Hours)} eventos remotos efectivos en 24 h`;
  elements.securityCriticalEvents.textContent =
    formatNumber(summary.criticalEventsLast24Hours);
  elements.securityHighMeta.textContent =
    `${formatNumber(summary.highEventsLast24Hours)} eventos de severidad alta en 24 h`;

  const devicePage = state.security.devices || {};
  const devices = Array.isArray(devicePage.items)
    ? devicePage.items
    : [];
  state.securityPages.devicePage = Number(devicePage.page || 1);
  state.securityPages.devicePageSize = Number(
    devicePage.pageSize || state.securityPages.devicePageSize);
  elements.securityDeviceRows.innerHTML = devices.length
    ? devices.map((device) => `
        <tr class="security-device-row clickable" data-security-device-id="${escapeHtml(device.deviceId)}" title="Abrir bitácora de ${escapeHtml(device.machineName)}">
          <td>
            <button class="table-device-open" type="button" data-security-open="${escapeHtml(device.deviceId)}" aria-label="Ver bitácora de ${escapeHtml(device.machineName)}">${escapeHtml(device.machineName)}</button>
            <span class="cell-secondary">${escapeHtml(device.agencyCode)}</span>
            ${caseFindingButton(postureFinding(device), "Expediente")}
          </td>
          <td><span class="posture-observation ${escapeHtml(postureObservation(device).state.toLowerCase())}">${escapeHtml(postureObservation(device).label)}</span>${device.capturedAtUtc ? escapeHtml(formatRelative(device.capturedAtUtc)) : "Sin fecha"}</td>
          <td>${postureControl(device, device.firewallEnabled)}</td>
          <td>${postureControl(device, device.systemDriveProtected)}</td>
          <td>${postureControl(device, device.secureBootEnabled)}</td>
          <td>${postureControl(device, device.tpmPresent)}</td>
          <td>${postureControl(device, device.smb1Enabled, true)}</td>
          <td>${postureControl(device, device.remoteDesktopEnabled, true)}</td>
          <td>${postureControl(device, device.pendingReboot, true)}</td>
        </tr>`).join("")
    : emptyRow(9, "Aún no hay equipos activos.");
  renderPagination(
    devicePage,
    elements.securityDevicePaginationLabel,
    elements.securityDevicePreviousPage,
    elements.securityDeviceNextPage,
    "equipos");
  elements.securityDeviceRows.querySelectorAll("[data-security-device-id]")
    .forEach((row) => row.addEventListener("click", () => {
      state.securityPages.eventDeviceId = row.dataset.securityDeviceId || "";
      state.securityPages.eventPage = 1;
      void loadSecurity().then(() => {
        elements.securityEventHeading.scrollIntoView({ behavior: "smooth", block: "start" });
      });
    }));
  attachCaseFindingActions(elements.securityDeviceRows);

  const eventPage = state.security.recentEvents || {};
  const events = Array.isArray(eventPage.items)
    ? eventPage.items
    : [];
  state.securityPages.eventPage = Number(eventPage.page || 1);
  state.securityPages.eventPageSize = Number(
    eventPage.pageSize || state.securityPages.eventPageSize);
  elements.securityEventRows.innerHTML = events.length
    ? events.map((event) => {
        const subject = [event.subjectUserDomain, event.subjectUserName]
          .filter(Boolean).join("\\");
        const user = [event.userDomain, event.userName]
          .filter(Boolean).join("\\") || "No informado";
        const actor = subject || user;
        const target = event.targetName && event.targetName !== actor
          ? event.targetName
          : "";
        const origin = [event.sourceAddress, event.workstationName]
          .filter(Boolean).join(" · ") || "Local / no informado";
        const detail = [
          event.details,
          event.processName,
          event.authenticationPackage,
          event.sessionName,
          event.logonType ? `Tipo de inicio ${event.logonType}` : ""
        ].filter(Boolean).join(" · ");
        const outcome = securityOutcomeLabels[event.outcome] ||
          [event.outcome || "Informativo", "unknown"];
        const severity = event.severity || "Information";
        return `
          <tr>
            <td>${escapeHtml(formatDate(event.occurredAtUtc))}</td>
            <td><span class="security-chip severity-${escapeHtml(severity.toLowerCase())}">${escapeHtml(securitySeverityLabels[severity] || severity)}</span></td>
            <td><span class="cell-primary">${escapeHtml(event.machineName)}</span><span class="cell-secondary">${escapeHtml(event.agencyCode)}</span></td>
            <td>
              <span class="cell-primary">${escapeHtml(securityEventLabels[event.category] || event.category)}</span>
              <span class="cell-secondary">Windows ${escapeHtml(event.eventCode)}${event.provider ? ` · ${escapeHtml(event.provider)}` : ""}</span>
              ${caseFindingButton(securityEventFinding(event), "Expediente")}
            </td>
            <td><span class="security-chip ${escapeHtml(outcome[1])}">${escapeHtml(outcome[0])}</span></td>
            <td><span class="cell-primary">${escapeHtml(actor)}</span>${target ? `<span class="cell-secondary">Objetivo: ${escapeHtml(target)}</span>` : ""}</td>
            <td><span class="cell-primary">${escapeHtml(origin)}</span>${detail ? `<span class="cell-secondary security-detail" title="${escapeHtml(detail)}">${escapeHtml(detail)}</span>` : ""}</td>
          </tr>`;
      }).join("")
    : emptyRow(7, "Aún no se han recibido eventos de seguridad.");
  attachCaseFindingActions(elements.securityEventRows);
  renderPagination(
    eventPage,
    elements.securityEventPaginationLabel,
    elements.securityEventPreviousPage,
    elements.securityEventNextPage,
    "eventos");
  renderExecutiveOverview();
}

function renderSession() {
  if (!state.session) {
    return;
  }
  elements.sidebarUser.textContent = state.session.displayName;
  const normalizedUsername = String(state.session.username || "")
    .trim()
    .toUpperCase();
  elements.sidebarRole.textContent =
    accountTitleLabels[normalizedUsername] ||
    roleLabels[state.session.role] ||
    state.session.role;
  const canExport =
    state.session.role === "Administrator" ||
    state.session.role === "Auditor" ||
    state.session.role === "TechnologyManager";
  document.querySelectorAll("[data-export]").forEach((button) => {
    button.classList.toggle("hidden", !canExport);
    button.disabled = !canExport;
  });
  document.querySelectorAll("[data-admin-only]").forEach((element) => {
    element.classList.toggle(
      "hidden",
      state.session.role !== "Administrator");
  });
  const canInstall =
    state.session.role === "Administrator" ||
    state.session.role === "Installer";
  document.querySelectorAll("[data-installer-access]").forEach((element) => {
    element.classList.toggle("hidden", !canInstall);
  });
  document.querySelectorAll("[data-audit-access]").forEach((element) => {
    element.classList.toggle("hidden", !canReadAudit());
  });
  document.querySelectorAll("[data-training-manage]").forEach((element) => {
    element.classList.toggle("hidden", !canManageTraining());
  });
  document.querySelectorAll("[data-governance-manage]").forEach((element) => {
    element.classList.toggle("hidden", !canManageGovernance());
  });
  document.querySelectorAll("[data-governance-approve]").forEach((element) => {
    element.classList.toggle("hidden", !canApproveGovernance());
  });
  const installerOnly = state.session.role === "Installer";
  document.querySelectorAll(".main-nav .nav-label").forEach((element) => {
    element.classList.toggle("hidden", installerOnly);
  });
  document.querySelectorAll(".nav-item[data-view]").forEach((element) => {
    if (installerOnly && !installerViews.has(element.dataset.view)) {
      element.classList.add("hidden");
    }
  });
  if (installerOnly && !installerViews.has(state.view)) {
    switchView("assets");
  }
  if (!canReadAudit() && state.view === "audit") {
    switchView("overview");
  }
}

function renderSummary() {
  const summary = state.summary;
  if (!summary) {
    return;
  }
  const coverage = summary.totalDevices > 0
    ? Math.round(summary.securityDetectedDevices * 100 / summary.totalDevices)
    : 0;
  const attentionTotal = summary.attentionDevices + summary.offlineDevices;
  elements.todayLabel.textContent = new Intl.DateTimeFormat("es-GT", {
    weekday: "long",
    day: "numeric",
    month: "long",
    year: "numeric"
  }).format(new Date(summary.generatedAtUtc)).toUpperCase();
  elements.navDeviceCount.textContent = formatNumber(summary.totalDevices);
  elements.kpiTotal.textContent = formatNumber(summary.totalDevices);
  elements.kpiTotalMeta.textContent =
    `${formatNumber(summary.agencies)} agencias registradas · ${formatNumber(state.agencies.filter(a => a.enabled && a.totalDevices > 0).length)} con equipos`;
  elements.kpiOnline.textContent = formatNumber(summary.onlineDevices);
  elements.kpiOnlineMeta.textContent =
    `${formatNumber(summary.attentionDevices)} con demora menor a 24 horas`;
  elements.kpiSecurity.textContent =
    formatNumber(summary.securityDetectedDevices);
  elements.kpiSecurityMeta.textContent =
    `${formatNumber(coverage)}% reporta antivirus o EDR · no acredita protección completa`;
  elements.kpiAttention.textContent = formatNumber(attentionTotal);
  elements.kpiAttentionMeta.textContent =
    `${formatNumber(summary.offlineDevices)} sin reporte por más de 24 horas`;
  elements.backupSuccess.textContent = formatNumber(summary.successfulBackups);
  elements.backupWarning.textContent = formatNumber(summary.warningBackups);
  elements.backupFailed.textContent = formatNumber(summary.failedBackups);
  elements.backupMissing.textContent = formatNumber(summary.devicesWithoutBackup);

  const assessed = summary.successfulBackups + summary.warningBackups + summary.failedBackups + summary.devicesWithoutBackup;
  elements.backupTruth.textContent = `Alcance: ${formatNumber(assessed)} equipos configurados con Atlas Backup / NAS de ${formatNumber(summary.totalDevices)} activos. Acronis y carpetas comunales se consultan en Control de respaldos. Corte: ${formatDate(summary.generatedAtUtc)}.`;

}

function chartPercent(value, total) {
  return total > 0
    ? Math.max(0, Math.min(100, (Number(value || 0) / total) * 100))
    : 0;
}

function horizontalBar(label, value, total, tone) {
  const percent = chartPercent(value, total);
  return `<div class="horizontal-chart-row">
    <div class="horizontal-chart-label"><span>${escapeHtml(label)}</span><strong>${formatNumber(value)}</strong></div>
    <div class="horizontal-chart-track"><span class="tone-${escapeHtml(tone)}" data-chart-width="${percent.toFixed(2)}"></span></div>
  </div>`;
}

function applyChartDimensions(container) {
  // CSSOM assignments are compatible with style-src 'self'; style attributes
  // embedded through innerHTML are blocked by the production CSP.
  container?.querySelectorAll('[data-chart-width], [data-chart-height]').forEach(node => {
    if (node.dataset.chartWidth !== undefined) node.style.width = `${Number(node.dataset.chartWidth)}%`;
    if (node.dataset.chartHeight !== undefined) node.style.height = `${Number(node.dataset.chartHeight)}%`;
  });
}

function overviewBackupControlReading(module) {
  // Use the control module's existing decision and denominator. Do not turn
  // technical run totals, missing modules or absent dates into compliance.
  if (!module?.generatedAtUtc || Number.isNaN(Date.parse(module.generatedAtUtc)) || !Array.isArray(module.profiles)) return null;
  const total = module.summary?.total, current = module.summary?.compliant;
  if (!Number.isInteger(total) || total < 0 || !Number.isInteger(current) || current < 0 || current > total) return null;
  let pending = null;
  if (typeof backupAcceptedWithPendingFiles === "function") {
    const active = module.profiles.filter(profile => profile.enabled && !profile.trackingPaused && !profile.deviceRetired &&
      ["ACTIVE", "APPROVED"].includes(profile.approvalStatus));
    const count = active.filter(backupAcceptedWithPendingFiles).length;
    if (active.length === total && count <= current) pending = count;
  }
  return { total, current, pending, percentage: total > 0 ? chartPercent(current, total) : null, generatedAtUtc: module.generatedAtUtc };
}

function renderOverviewCopyHero() {
  const value = document.getElementById("overviewCopyValue");
  if (!value) return;
  const unit = document.getElementById("overviewCopyUnit"), scope = document.getElementById("overviewCopyScope");
  const progress = document.getElementById("overviewCopyProgress"), fill = document.getElementById("overviewCopyProgressFill");
  const breakdown = document.getElementById("overviewCopyBreakdown"), footnote = document.getElementById("overviewCopyFootnote");
  const reading = overviewBackupControlReading(state.backupControl);
  if (!reading) {
    value.textContent = "—";
    unit.textContent = "Evaluación no disponible";
    scope.textContent = "No se pudo consultar el corte de Control de respaldos. No se supone que las copias estén hechas ni pendientes.";
    breakdown.innerHTML = "";
    footnote.textContent = "Abre Control de respaldos para consultar o actualizar la evidencia.";
    progress.removeAttribute("aria-valuenow");
    progress.setAttribute("aria-valuetext", "Sin medición disponible");
    fill.style.width = "0%";
    return;
  }
  value.textContent = reading.percentage === null ? "—" : `${new Intl.NumberFormat("es-GT", { maximumFractionDigits: 1 }).format(reading.percentage)}%`;
  unit.textContent = `${formatNumber(reading.current)} de ${formatNumber(reading.total)} controles`;
  scope.textContent = reading.total ? "Controles activos y aprobados, sin pausas. La copia aceptable se evalúa con el alcance, destino y plazo configurados." : "No hay controles activos y aprobados sin pausa para evaluar. No se calcula un porcentaje sin denominador.";
  breakdown.innerHTML = reading.total ? `<span><strong>${formatNumber(reading.current)}</strong> con copia aceptable vigente</span><span><strong>${formatNumber(reading.total - reading.current)}</strong> sin copia aceptable vigente</span>${reading.pending === null ? "" : `<small>De las vigentes, ${formatNumber(reading.pending)} ${reading.pending === 1 ? "conserva" : "conservan"} archivos pendientes.</small>`}` : "";
  footnote.textContent = `Corte: ${formatDate(reading.generatedAtUtc)} · Una ejecución no acredita restauración.`;
  if (reading.percentage === null) {
    progress.removeAttribute("aria-valuenow");
    progress.setAttribute("aria-valuetext", "Sin controles para evaluar");
  } else {
    progress.setAttribute("aria-valuenow", reading.percentage.toFixed(2));
    progress.setAttribute("aria-valuetext", `${formatNumber(reading.current)} de ${formatNumber(reading.total)} controles con copia aceptable vigente`);
  }
  fill.style.width = `${reading.percentage ?? 0}%`;
}

function renderExecutiveOverview() {
  renderOverviewCopyHero();
  if (!state.summary || !elements.overviewConnectivityChart) {
    return;
  }
  const summary = state.summary;
  const total = Number(summary.totalDevices || 0);
  const online = Number(summary.onlineDevices || 0);
  const attention = Number(summary.attentionDevices || 0);
  const offline = Number(summary.offlineDevices || 0);
  const onlineStop = chartPercent(online, total);
  const attentionStop = onlineStop + chartPercent(attention, total);
  elements.overviewConnectivityChart.style.setProperty("--online-stop", `${onlineStop}%`);
  elements.overviewConnectivityChart.style.setProperty("--attention-stop", `${attentionStop}%`);
  elements.overviewConnectivityTotal.textContent = formatNumber(total);
  elements.overviewConnectivityLegend.innerHTML = [
    ["En línea", online, "online"],
    ["Atención", attention, "attention"],
    ["Sin reportar", offline, "offline"]
  ].map(([label, value, tone]) => `<div><span class="legend-dot tone-${tone}"></span><span>${label}</span><strong>${formatNumber(value)}</strong></div>`).join("");

  const backupTotal = Math.max(0,
    Number(summary.successfulBackups || 0) + Number(summary.warningBackups || 0) +
    Number(summary.failedBackups || 0) + Number(summary.devicesWithoutBackup || 0));
  elements.overviewBackupChart.innerHTML = [
    horizontalBar("Correctas", summary.successfulBackups, backupTotal, "success"),
    horizontalBar("Advertencias", summary.warningBackups, backupTotal, "warning"),
    horizontalBar("Fallos o destino incorrecto", summary.failedBackups, backupTotal, "danger"),
    horizontalBar("Sin telemetría", summary.devicesWithoutBackup, backupTotal, "neutral")
  ].join("");

  const posture = state.security?.summary;
  const postureTotal = Number(posture?.totalDevices || total);
  elements.overviewSecurityChart.innerHTML = posture
    ? [
      horizontalBar("Postura recibida", posture.reportingDevices, postureTotal, "cyan"),
      horizontalBar("Firewall", posture.firewallProtectedDevices, postureTotal, "success"),
      horizontalBar("BitLocker C:", posture.bitLockerProtectedDevices, postureTotal, "navy"),
      horizontalBar("Secure Boot", posture.secureBootDevices, postureTotal, "violet")
    ].join("")
    : "<p class=\"chart-empty\">Sin telemetría de postura.</p>";

  const nasResources = (Array.isArray(state.nas) ? state.nas : []).filter(n => n.enabled);
  const physicalNas = new Set(nasResources.map(nasGroupKey)).size;
  const onlineNas = nasResources.filter((nas) => nas.lastProbeStatus === "ONLINE").length;
  const observedAssets = Number(state.network?.assets?.total || 0);
  elements.overviewInfrastructureMetrics.innerHTML = [
    ["Agencias", state.agencies.filter(a => a.enabled).length, "Registradas y habilitadas"],
    ["NAS físicos", physicalNas, `${onlineNas} recursos accesibles desde ATLAS`],
    ["Recursos NAS", nasResources.length, "Recursos habilitados"],
    ["IP observadas", observedAssets, "Observaciones históricas; no indica presencia actual"]
  ].map(([label, value, detail]) => {
    const key = label === "Agencias" ? "agencies" : label === "IP observadas" ? "network" : "nas";
    const source = state.sources?.[key], unavailable = source?.status === "error" && !source.lastSuccessAtUtc;
    return `<div><span>${escapeHtml(label)}</span><strong>${unavailable ? "—" : formatNumber(value)}</strong><small>${unavailable ? "Fuente pendiente de consulta" : escapeHtml(detail)}</small></div>`;
  }).join("");

  const daily = Array.isArray(state.security?.dailyEvents)
    ? state.security.dailyEvents
    : [];
  const knownEventCount = item => Number.isInteger(item?.totalEvents) && item.totalEvents >= 0;
  const maximum = Math.max(1, ...daily.filter(knownEventCount).map(item => item.totalEvents));
  const eventTotal = document.getElementById("overviewEventTotal");
  if (eventTotal) eventTotal.textContent = state.security && daily.length && daily.every(knownEventCount)
    ? formatNumber(daily.reduce((sum, item) => sum + item.totalEvents, 0)) : "—";
  elements.overviewSecurityTrend.innerHTML = daily.length
    ? daily.map((item) => {
      const known = knownEventCount(item), value = known ? item.totalEvents : null;
      const date = new Date(`${item.day}T12:00:00`);
      const label = Number.isNaN(date.getTime()) ? "Sin fecha" : new Intl.DateTimeFormat("es-GT", {
        weekday: "short", day: "numeric"
      }).format(date);
      const height = known ? value > 0 ? Math.max(8, (value / maximum) * 100) : 2 : 0;
      return `<div class="trend-column" title="${escapeHtml(label)}: ${known ? `${formatNumber(value)} eventos` : "Recuento no disponible"}">
        <strong>${known ? formatNumber(value) : "—"}</strong><div class="trend-track"><span data-chart-height="${height.toFixed(2)}"></span></div><small>${escapeHtml(label)}</small>
      </div>`;
    }).join("")
    : state.security ? "<p class=\"chart-empty\">Aún no hay eventos recibidos para graficar.</p>" : "<p class=\"chart-empty\">Tendencia no disponible. No se pudo consultar la fuente de seguridad.</p>";
  applyChartDimensions(elements.overviewBackupChart);
  applyChartDimensions(elements.overviewSecurityChart);
  applyChartDimensions(elements.overviewSecurityTrend);
}

function agencyRow(agency, includeBackup) {
  const cells = [
    `<td><span class="cell-primary">${escapeHtml(agency.displayName || agency.agencyCode)}</span><span class="cell-secondary">${escapeHtml(agency.agencyCode)}</span></td>`,
    `<td>${formatNumber(agency.totalDevices)}</td>`,
    `<td>${formatNumber(agency.onlineDevices)}</td>`,
    `<td>${formatNumber(agency.attentionDevices)}</td>`,
    `<td>${formatNumber(agency.offlineDevices)}</td>`,
    `<td>${formatNumber(agency.securityDetectedDevices)}</td>`,
    `<td title="${escapeHtml(formatDate(agency.lastSeenAtUtc))}">${escapeHtml(formatRelative(agency.lastSeenAtUtc))}</td>`
  ];
  if (includeBackup) {
    cells.push(`<td>${escapeHtml(formatRelative(agency.lastBackupAtUtc))}</td>`);
  }
  return `<tr class="clickable" data-agency="${escapeHtml(agency.agencyCode)}">${cells.join("")}</tr>`;
}

function renderAgencies() {
  const canManage = state.session?.role === "Administrator";
  if (!state.agencies.length) {
    elements.overviewAgencyRows.innerHTML =
      emptyRow(7, "Todavía no hay agencias con equipos inscritos.");
    elements.agencyRows.innerHTML =
      emptyRow(8, "Todavía no hay agencias con equipos inscritos.");
    elements.agencyCards.innerHTML =
      "<div class=\"empty-state\"><h3>Sin agencias</h3><p>Los equipos aparecerán después de inscribirse.</p></div>";
  } else {
    elements.overviewAgencyRows.innerHTML =
      state.agencies.map((agency) => agencyRow(agency, false)).join("");
    elements.agencyRows.innerHTML =
      state.agencies.map((agency) => agencyRow(agency, true)).join("");
    elements.agencyCards.innerHTML = state.agencies.map((agency) => `
      <article class="agency-card">
        <header>
          <div><h3>${escapeHtml(agency.displayName || agency.agencyCode)}</h3><span class="cell-secondary">${escapeHtml(agency.agencyCode)}</span></div>
          <div class="card-actions">
            ${canManage ? `<button class="secondary-button compact edit-agency" type="button" data-agency-code="${escapeHtml(agency.agencyCode)}">Editar</button>` : ""}
            <span class="agency-total">${formatNumber(agency.totalDevices)}</span>
          </div>
        </header>
        <div class="agency-metrics">
          <div><span>EN LÍNEA</span><b>${formatNumber(agency.onlineDevices)}</b></div>
          <div><span>ATENCIÓN</span><b>${formatNumber(agency.attentionDevices)}</b></div>
          <div><span>SIN REPORTE</span><b>${formatNumber(agency.offlineDevices)}</b></div>
        </div>
        <footer class="agency-infrastructure">
          <span>${escapeHtml(backupProviderLabels[agency.defaultBackupProvider] || agency.defaultBackupProvider)}</span>
          <span>${formatNumber(agency.onlineNas)} de ${formatNumber(agency.nasCount)} NAS con respuesta de red</span>
        </footer>
      </article>
    `).join("");
  }

  const selectedAgency = elements.agencyFilter.value;
  elements.agencyFilter.innerHTML =
    "<option value=\"\">Todas las agencias</option>" +
    state.agencies.filter((agency) => agency.enabled).map((agency) =>
      `<option value="${escapeHtml(agency.agencyCode)}">${escapeHtml(agency.displayName || agency.agencyCode)}</option>`
    ).join("");
  elements.agencyFilter.value = selectedAgency;

  const selectedInstallerAgency = elements.installerAgency.value;
  elements.installerAgency.innerHTML =
    "<option value=\"\">Seleccione una agencia</option>" +
    state.agencies.filter((agency) => agency.enabled).map((agency) =>
      `<option value="${escapeHtml(agency.agencyCode)}">${escapeHtml(agency.displayName || agency.agencyCode)}</option>`
    ).join("");
  elements.installerAgency.value = selectedInstallerAgency;

  const selectedDeploymentAgency = elements.deploymentAgency.value;
  elements.deploymentAgency.innerHTML =
    "<option value=\"\">Seleccione una agencia</option>" +
    state.agencies.filter((agency) => agency.enabled).map((agency) =>
      `<option value="${escapeHtml(agency.agencyCode)}">${escapeHtml(agency.displayName || agency.agencyCode)}</option>`
    ).join("");
  elements.deploymentAgency.value = selectedDeploymentAgency;

  const selectedNasAgency = elements.nasAgency.value;
  elements.nasAgency.innerHTML =
    "<option value=\"\">Seleccione una agencia</option>" +
    state.agencies.filter((agency) => agency.enabled).map((agency) =>
      `<option value="${escapeHtml(agency.agencyCode)}">${escapeHtml(agency.displayName || agency.agencyCode)}</option>`
    ).join("");
  elements.nasAgency.value = selectedNasAgency;

  document.querySelectorAll("[data-agency]").forEach((row) => {
    row.addEventListener("click", () => {
      state.filters.agency = row.dataset.agency || "";
      elements.agencyFilter.value = state.filters.agency;
      state.devices.page = 1;
      switchView("assets");
      void loadDevices();
    });
  });
  document.querySelectorAll(".edit-agency").forEach((button) => {
    button.addEventListener("click", () => beginEditAgency(
      button.dataset.agencyCode || ""));
  });
}

const spanishCollator = new Intl.Collator("es", {
  numeric: true,
  sensitivity: "base"
});

function canManageBackupControls() {
  return state.session?.role === "Administrator";
}

function backupControlProfileById(id) {
  return (state.backupControl?.profiles || [])
    .find((item) => item.id === id) || null;
}

function nullableNumber(element) {
  const value = String(element?.value || "").trim();
  return value === "" ? null : Number(value);
}

function nullableIso(element) {
  const value = String(element?.value || "").trim();
  if (!value) {
    return null;
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

function backupControlMutationOptions(method, body) {
  return {
    method,
    headers: {
      "Content-Type": "application/json",
      "X-Atlas-Request": "dashboard"
    },
    body: JSON.stringify(body)
  };
}

async function loadBackupControl(selectedId = null, openDetail = false) {
  const sourceRequest = sourceBegin("backupControl");
  try {
    const sourceValue = await api("/api/v1/admin/backup-controls");
    if (sourceRequest !== sourceRecord("backupControl").request) return;
    state.backupControl = sourceValue;
    sourceSuccess("backupControl", sourceRequest);
    renderBackupControl();
    if (selectedId && openDetail) {
      await openBackupControlDetail(selectedId);
    }
  } catch (error) {
    if (sourceRequest !== sourceRecord("backupControl").request) return;
    sourceFailure("backupControl", sourceRequest);
    if (error.message !== "unauthorized") {
      showToast("No fue posible actualizar el control de respaldos.");
    }
  }
}

function backupControlStandardById(id) {
  return (state.backupControl?.standards || []).find((item) => item.id === id) || null;
}

function backupControlBaseStandard() {
  return (state.backupControl?.standards || []).find((item) =>
    item.code === "BKP-BASE-INSTITUCIONAL") || null;
}

function recommendedBackupStandard(device = null) {
  const provider = device?.backupProvider || elements.backupControlProvider.value;
  const code = provider === "ATLAS_BACKUPNAS"
    ? "BKP-ENDPOINT-ATLAS"
    : provider === "ACRONIS"
      ? "BKP-ENDPOINT-ACRONIS"
      : "BKP-BASE-INSTITUCIONAL";
  return (state.backupControl?.standards || []).find((item) => item.code === code) ||
    backupControlBaseStandard();
}

function renderBackupStandardCatalog() {
  const standards = state.backupControl?.standards || [];
  elements.backupStandardCount.textContent =
    `${formatNumber(standards.length)} estándares vigentes`;
  elements.backupStandardCatalog.innerHTML = standards.map((standard) => {
    const details = [
      standard.provider ? backupControlProviderLabels[standard.provider] || standard.provider : "Cualquier proveedor",
      standard.frequencyHours ? `cada ${formatNumber(standard.frequencyHours)} h` : "frecuencia definida por el control",
      standard.retentionDays === 180 ? "retención 6 meses" : null
    ].filter(Boolean).join(" · ");
    return `<article class="backup-standard-card${standard.code === "BKP-BASE-INSTITUCIONAL" ? " base" : ""}">
      <div><span>${escapeHtml(standard.code)} · v${escapeHtml(standard.version)}</span><strong>${escapeHtml(standard.name)}</strong></div>
      <p>${escapeHtml(standard.description)}</p>
      <small>${escapeHtml(details)}</small>
    </article>`;
  }).join("");

  const selected = elements.backupControlStandard.value;
  elements.backupControlStandard.innerHTML = standards.map((standard) =>
    `<option value="${escapeHtml(standard.id)}">${escapeHtml(standard.name)} · v${escapeHtml(standard.version)}</option>`
  ).join("");
  if (standards.some((item) => item.id === selected)) {
    elements.backupControlStandard.value = selected;
  }
}

function setBackupFieldOrigin(field, origin) {
  if (!field || !origin) {
    return;
  }
  state.backupControlUi.fieldOrigins[field] = origin;
  renderBackupFieldOrigins();
}

function renderBackupFieldOrigins() {
  document.querySelectorAll("[data-origin-field]").forEach((container) => {
    const field = container.dataset.originField;
    const origin = state.backupControlUi.fieldOrigins[field];
    let badge = container.querySelector(".field-origin-badge");
    if (!origin) {
      badge?.remove();
      return;
    }
    if (!badge) {
      badge = document.createElement("small");
      badge.className = "field-origin-badge";
      container.prepend(badge);
    }
    badge.className = `field-origin-badge ${origin.toLowerCase()}`;
    badge.textContent = backupControlOriginLabels[origin] || origin;
  });
  updateBackupAutofillSummary();
}

function selectedBackupScopeCategories() {
  return Array.from(document.querySelectorAll(
    "input[name=\"backupScopeCategory\"]:checked"
  )).map((item) => item.value);
}

function setBackupScopeCategories(categories) {
  const selected = new Set(categories || []);
  document.querySelectorAll("input[name=\"backupScopeCategory\"]")
    .forEach((item) => { item.checked = selected.has(item.value); });
}

function syncBackupProtectedScope(force = false) {
  const categories = selectedBackupScopeCategories();
  const additional = elements.backupControlScopeAdditional.value.trim();
  const exclusions = elements.backupControlScopeExclusions.value.trim();
  if (!force && !categories.length && !additional && !exclusions) {
    return;
  }
  const parts = [];
  if (categories.length) {
    parts.push(categories.map((item) => backupScopeCategoryLabels[item] || item).join(", "));
  }
  if (additional) {
    parts.push(`Alcance adicional: ${additional}`);
  }
  if (exclusions) {
    parts.push(`Exclusiones declaradas: ${exclusions}`);
  }
  elements.backupControlProtectedScope.value = parts.join(". ");
}

function setBackupValue(element, value, field, origin) {
  if (value !== null && value !== undefined && value !== "") {
    element.value = value;
    setBackupFieldOrigin(field, origin);
  }
}

function applyBackupStandard(standard, preserveDetected = true) {
  if (!standard) {
    return;
  }
  state.backupControlUi.applyingTemplate = true;
  elements.backupControlStandard.value = standard.id;
  const values = [
    [elements.backupControlScopeType, standard.scopeType, "scopeType"],
    [elements.backupControlProvider, standard.provider, "provider"],
    [elements.backupControlMode, standard.operationMode, "operationMode"],
    [elements.backupControlTechnology, standard.technology, "technology"],
    [elements.backupControlTask, standard.taskName, "taskName"],
    [elements.backupControlFrequency, standard.frequencyHours, "frequency"],
    [elements.backupControlGrace, standard.graceMinutes, "grace"],
    [elements.backupControlRetention, standard.retentionDays, "retention"],
    [elements.backupControlRpo, standard.rpoHours, "rpo"],
    [elements.backupControlRto, standard.rtoMinutes, "rto"],
    [elements.backupControlRestoreInterval, standard.restoreTestIntervalDays, "restoreInterval"],
    [elements.backupControlResponsible, standard.technicalResponsible, "technicalResponsible"]
  ];
  values.forEach(([element, value, field]) => {
    if (value === null || value === undefined || value === "") {
      if (state.backupControlUi.fieldOrigins[field] === "INHERITED") {
        element.value = "";
        delete state.backupControlUi.fieldOrigins[field];
      }
      return;
    }
    if (preserveDetected && state.backupControlUi.fieldOrigins[field] === "DETECTED") {
      return;
    }
    setBackupValue(element, value, field, "INHERITED");
  });
  if (standard.protectedScope) {
    elements.backupControlProtectedScope.value = standard.protectedScope;
    setBackupScopeCategories(standard.protectedScopeCategories || []);
    setBackupFieldOrigin("protectedScope", "INHERITED");
  } else if (state.backupControlUi.fieldOrigins.protectedScope === "INHERITED") {
    elements.backupControlProtectedScope.value = "";
    setBackupScopeCategories([]);
    delete state.backupControlUi.fieldOrigins.protectedScope;
  }
  state.backupControlUi.applyingTemplate = false;
  updateBackupControlConditionalFields();
  renderBackupFieldOrigins();
}

function generateBackupControlCode(machineName) {
  const stem = `BKP-${String(machineName || "CONTROL")}`
    .toUpperCase().replace(/[^A-Z0-9_-]/g, "-").slice(0, 59);
  const existing = new Set((state.backupControl?.profiles || []).map((item) => item.code));
  if (!existing.has(stem)) {
    return stem;
  }
  for (let index = 2; index < 100; index++) {
    const suffix = `-${index}`;
    const candidate = `${stem.slice(0, 64 - suffix.length)}${suffix}`;
    if (!existing.has(candidate)) {
      return candidate;
    }
  }
  return `${stem.slice(0, 55)}-${Date.now().toString().slice(-8)}`;
}

function selectedBackupDevice() {
  return (state.backupControl?.devices || []).find((item) =>
    item.id === elements.backupControlDevice.value) || null;
}

function applySelectedBackupDevice() {
  const device = selectedBackupDevice();
  if (!device) {
    updateBackupAutofillSummary();
    return;
  }
  state.backupControlUi.applyingTemplate = true;
  elements.backupControlAgency.value = device.agencyCode;
  renderBackupControlOptions();
  elements.backupControlDevice.value = device.id;
  setBackupValue(elements.backupControlSystem, device.machineName,
    "systemName", "DETECTED");
  if (!elements.backupControlId.value || !elements.backupControlCode.value) {
    setBackupValue(elements.backupControlCode,
      generateBackupControlCode(device.machineName), "code", "DETECTED");
  }
  setBackupValue(elements.backupControlAgency, device.agencyCode,
    "agency", "DETECTED");
  setBackupFieldOrigin("device", "DETECTED");
  setBackupValue(elements.backupControlProvider, device.backupProvider,
    "provider", "DETECTED");
  const matchingNas = device.nasApplianceId ||
    (state.backupControl?.nas || []).find((item) =>
      String(item.host).toLowerCase() ===
        String(device.lastReportedDestinationHost || "").toLowerCase() &&
      String(item.shareName).toLowerCase() ===
        String(device.lastReportedDestinationShare || "").toLowerCase())?.id;
  if (matchingNas) {
    elements.backupControlNas.value = matchingNas;
    setBackupFieldOrigin("nas", "DETECTED");
  }
  const host = device.nasHost || device.lastReportedDestinationHost;
  const share = device.nasShareName || device.lastReportedDestinationShare;
  if (host && share) {
    setBackupValue(elements.backupControlDestination,
      `\\\\${host}\\${share}`, "destination", "DETECTED");
  } else if (device.backupProvider === "ACRONIS") {
    setBackupValue(elements.backupControlDestination,
      "Acronis Cloud", "destination", "DETECTED");
  }
  setBackupFieldOrigin("scopeType", "DETECTED");

  const configuration = elements.backupControlConfigurationMode.value;
  if (configuration !== "MANUAL") {
    const standard = recommendedBackupStandard(device);
    if (standard) {
      applyBackupStandard(standard, true);
    }
  } else {
    const base = backupControlBaseStandard();
    if (base) {
      elements.backupControlStandard.value = base.id;
      setBackupValue(elements.backupControlRetention,
        base.retentionDays || 180, "retention", "INHERITED");
      setBackupValue(elements.backupControlResponsible,
        base.technicalResponsible, "technicalResponsible", "INHERITED");
    }
  }
  state.backupControlUi.applyingTemplate = false;
  updateBackupControlConditionalFields();
}

function updateBackupControlConditionalFields() {
  const configuration = elements.backupControlConfigurationMode.value;
  const needsReason = configuration !== "STANDARD";
  elements.backupControlReasonField.classList.toggle("hidden", !needsReason);
  elements.backupControlReason.required = needsReason;
  elements.backupControlStandard.disabled = configuration === "MANUAL";
  if (configuration === "MANUAL") {
    const base = backupControlBaseStandard();
    if (base) {
      elements.backupControlStandard.value = base.id;
    }
  }

  const exception = elements.backupControlExceptionType.value;
  const hasException = exception !== "NONE";
  const operational = exception === "OPERATIONAL";
  elements.backupControlExceptionReasonField.classList.toggle("hidden", !hasException);
  elements.backupControlExceptionReason.required = hasException;
  elements.backupControlExceptionExpiryField.classList.toggle("hidden", !operational);
  elements.backupControlExceptionExpiry.required = operational;
  elements.backupControlExceptionWarning.classList.toggle(
    "hidden", exception !== "NORMATIVE");
  const canActivate = exception !== "NORMATIVE";
  elements.backupControlEnabled.disabled = !canActivate;
  if (!canActivate) elements.backupControlEnabled.checked = false;
  elements.backupControlSave.textContent = elements.backupControlEnabled.checked
    ? "Guardar y activar seguimiento" : "Guardar borrador";
  const folders = document.getElementById("backupControlFolders");
  if (folders) folders.required = elements.backupControlProvider.value === "ATLAS_BACKUPNAS" && elements.backupControlEnabled.checked;
  updateBackupAutofillSummary();
}

function updateBackupAutofillSummary() {
  const origins = Object.values(state.backupControlUi.fieldOrigins || {});
  const detected = origins.filter((item) => item === "DETECTED").length;
  const inherited = origins.filter((item) => item === "INHERITED").length;
  const suggested = origins.filter((item) => item === "SUGGESTED").length;
  const required = [
    elements.backupControlCode, elements.backupControlSystem,
    elements.backupControlAgency, elements.backupControlTask,
    elements.backupControlProtectedScope, elements.backupControlRpo,
    elements.backupControlRto, elements.backupControlRestoreInterval,
    elements.backupControlOwner, elements.backupControlDestination
  ];
  const pending = required.filter((item) => !String(item.value || "").trim()).length;
  const device = selectedBackupDevice();
  elements.backupControlAutofillSummary.innerHTML = device
    ? `<strong>${escapeHtml(detected)} detectados · ${escapeHtml(inherited)} heredados${suggested ? ` · ${escapeHtml(suggested)} sugeridos` : ""}</strong><span>${pending ? `${escapeHtml(pending)} campos requieren confirmación.` : "Configuración completa para guardar y activar."}</span>`
    : "<strong>Seleccione un activo</strong><span>ATLAS identificará la configuración disponible.</span>";
}

function renderBackupControlOptions() {
  const selectedAgency = elements.backupControlAgency.value;
  elements.backupControlAgency.innerHTML =
    "<option value=\"\">Seleccione una agencia</option>" +
    (state.agencies || []).filter((item) => item.enabled).map((item) =>
      `<option value="${escapeHtml(item.agencyCode)}">${escapeHtml(item.displayName || item.agencyCode)}</option>`
    ).join("");
  elements.backupControlAgency.value = selectedAgency;

  const agency = elements.backupControlAgency.value;
  const selectedDevice = elements.backupControlDevice.value;
  const selectedNas = elements.backupControlNas.value;
  elements.backupControlDevice.innerHTML =
    "<option value=\"\">No aplica</option>" +
    (state.backupControl?.devices || [])
      .filter((item) => !agency || item.agencyCode === agency)
      .map((item) => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.machineName)} · ${escapeHtml(item.agencyCode)}</option>`)
      .join("");
  elements.backupControlNas.innerHTML =
    "<option value=\"\">No aplica</option>" +
    (state.backupControl?.nas || [])
      .filter((item) => !agency || item.agencyCode === agency)
      .map((item) => `<option value="${escapeHtml(item.id)}">${escapeHtml(item.displayName)} · \\${escapeHtml(item.host)}\\${escapeHtml(item.shareName)}</option>`)
      .join("");
  elements.backupControlDevice.value = selectedDevice;
  elements.backupControlNas.value = selectedNas;
}

function backupControlActionButtons(profile) {
  const button = (action, label, icon, primary = false) =>
    `<button class="backup-control-action${primary ? " primary" : ""} backup-control-${action}-action" type="button" data-id="${escapeHtml(profile.id)}" aria-label="${escapeHtml(label)} · ${escapeHtml(profile.code)}">${atlasIcon(icon)}<span>${label}</span></button>`;
  const actions = [button("detail", "Ver detalle", "file-description")];
  if (canManageBackupControls() && !profile.deviceRetired) {
    actions.push(button("edit", "Editar", "pencil"));
    if (profile.exceptionType !== "NORMATIVE" &&
        ["DRAFT", "PENDING_APPROVAL", "RETURNED"].includes(profile.approvalStatus)) {
      actions.push(button("activate", "Activar seguimiento", "check", true));
    }
    actions.push(
      button("observation", "Resultado", "list-details"),
      button("restoration", "Restauración", "refresh"),
      button("evidence", "Evidencia", "upload"));
  }
  return actions.join("");
}

function renderBackupControl() {
  const module = state.backupControl || {};
  const summary = module.summary || {};
  const profiles = Array.isArray(module.profiles) ? module.profiles : [];
  elements.backupControlTotal.textContent = formatNumber(summary.total);
  elements.backupControlCoverage.textContent =
    `${formatNumber(summary.coveragePercentage)} % con evidencia vigente`;
  elements.backupControlCompliant.textContent = formatNumber(summary.compliant);
  elements.backupControlAttention.textContent = formatNumber(summary.attention);
  elements.backupControlFailed.textContent = formatNumber(summary.failed);
  elements.backupControlOverdue.textContent =
    `${formatNumber(summary.overdue)} controles vencidos`;
  elements.backupControlPending.textContent =
    formatNumber(summary.pendingApproval);
  elements.backupControlRestoreDue.textContent =
    `${formatNumber(summary.restoreTestsDue)} pruebas de restauración vencidas`;
  elements.backupControlEmptyState.classList.toggle("hidden", profiles.length > 0);
  elements.newBackupControlButton.classList.toggle(
    "hidden", !canManageBackupControls());
  document.querySelectorAll("[data-backup-control-manage]").forEach((item) =>
    item.classList.toggle("hidden", !canManageBackupControls()));
  renderBackupStandardCatalog();

  elements.backupControlRows.innerHTML = profiles.length
    ? profiles.map((profile) => {
      const provider = backupControlProviderLabels[profile.provider] ||
        profile.provider;
      const scope = backupControlScopeLabels[profile.scopeType] ||
        profile.scopeType;
      const compliance = backupControlComplianceLabels[profile.compliance] ||
        profile.compliance;
      const approval = !profile.enabled ? "Seguimiento desactivado" :
        backupControlApprovalLabels[profile.approvalStatus] || profile.approvalStatus;
      const restore = profile.lastRestorationAtUtc
        ? `${backupControlResultLabels[profile.lastRestorationResult] || profile.lastRestorationResult} · ${formatRelative(profile.lastRestorationAtUtc)}`
        : "Sin prueba registrada";
      return `<tr>
        <td><span class="cell-primary">${escapeHtml(profile.code)} · ${escapeHtml(profile.systemName)}</span><span class="cell-secondary">${escapeHtml(profile.standardName)} v${escapeHtml(profile.standardVersion)} · ${escapeHtml(backupControlConfigurationLabels[profile.configurationMode] || profile.configurationMode)}</span></td>
        <td>${escapeHtml(profile.agencyName || profile.agencyCode)}</td>
        <td><span class="cell-primary">${escapeHtml(provider)}</span><span class="cell-secondary">${escapeHtml(profile.taskName || profile.technology)}</span></td>
        <td title="${escapeHtml(formatDate(profile.lastObservationAtUtc))}"><span class="cell-primary">${escapeHtml(formatRelative(profile.lastObservationAtUtc))}</span><span class="cell-secondary">${formatNumber(profile.evidenceCount)} archivo(s)</span></td>
        <td>${escapeHtml(restore)}</td>
        <td><span class="coverage-badge ${escapeHtml(profile.compliance.toLowerCase())}" title="${escapeHtml(profile.complianceDetail)}">${escapeHtml(compliance)}</span><span class="cell-secondary">${escapeHtml(approval)}${profile.exceptionType !== "NONE" ? ` · ${escapeHtml(backupControlExceptionLabels[profile.exceptionType] || profile.exceptionType)}` : ""}</span></td>
        <td><div class="row-actions backup-control-actions">${backupControlActionButtons(profile)}</div></td>
      </tr>`;
    }).join("")
    : emptyRow(7, "Todavía no se han definido controles de respaldo.");
  renderBackupControlOptions();

  elements.backupControlRows.querySelectorAll(".backup-control-detail-action")
    .forEach((button) => button.addEventListener("click", () =>
      void openBackupControlDetail(button.dataset.id)));
  elements.backupControlRows.querySelectorAll(".backup-control-edit-action")
    .forEach((button) => button.addEventListener("click", () =>
      beginBackupControlEdit(button.dataset.id)));
  elements.backupControlRows.querySelectorAll(".backup-control-observation-action")
    .forEach((button) => button.addEventListener("click", () =>
      beginBackupObservation(button.dataset.id)));
  elements.backupControlRows.querySelectorAll(".backup-control-restoration-action")
    .forEach((button) => button.addEventListener("click", () =>
      beginBackupRestoration(button.dataset.id)));
  elements.backupControlRows.querySelectorAll(".backup-control-evidence-action")
    .forEach((button) => button.addEventListener("click", () =>
      beginBackupEvidence(button.dataset.id)));
  elements.backupControlRows.querySelectorAll(".backup-control-activate-action")
    .forEach((button) => button.addEventListener("click", () =>
      void activateBackupControl(button.dataset.id, button)));
  if (typeof renderBackupWorkspace === "function") renderBackupWorkspace();
  if (typeof renderExecutiveOverview === "function") renderExecutiveOverview();
}

function hideBackupControlEditors() {
  [
    elements.backupControlFormPanel,
    elements.backupObservationPanel,
    elements.backupRestorationPanel,
    elements.backupEvidencePanel
  ].forEach((panel) => panel.classList.add("hidden"));
}

function beginBackupControlEdit(id = null) {
  if (!canManageBackupControls()) {
    return;
  }
  hideBackupControlEditors();
  elements.backupControlForm.reset();
  state.backupControlUi.fieldOrigins = {};
  state.backupControlUi.applyingTemplate = true;
  state.backupControlUi.editingId = id;
  const profile = id ? backupControlProfileById(id) : null;
  elements.backupControlFormTitle.textContent = profile
    ? `Editar ${profile.code}`
    : "Registrar control de respaldo";
  elements.backupControlId.value = profile?.id || "";
  renderBackupStandardCatalog();
  elements.backupControlConfigurationMode.value =
    profile?.configurationMode || "STANDARD";
  elements.backupControlStandard.value = profile?.standardId ||
    recommendedBackupStandard()?.id || "";
  elements.backupControlReason.value = profile?.configurationReason || "";
  elements.backupControlCode.value = profile?.code || "";
  elements.backupControlSystem.value = profile?.systemName || "";
  elements.backupControlAgency.value = profile?.agencyCode || "";
  renderBackupControlOptions();
  elements.backupControlScopeType.value = profile?.scopeType || "ENDPOINT";
  elements.backupControlProvider.value = profile?.provider || "ATLAS_BACKUPNAS";
  elements.backupControlDevice.required =
    elements.backupControlProvider.value === "ATLAS_BACKUPNAS";
  elements.backupControlMode.value = profile?.operationMode || "SEMI_AUTOMATIC";
  elements.backupControlDevice.value = profile?.deviceId || "";
  elements.backupControlNas.value = profile?.nasApplianceId || "";
  elements.backupControlTechnology.value = profile?.technology || "Atlas Backup";
  elements.backupControlTask.value = profile?.taskName || "";
  elements.backupControlProtectedScope.value = profile?.protectedScope || "";
  document.getElementById("backupControlFolders").value = (profile?.protectedFolders || []).join("\n");
  setBackupScopeCategories(profile?.protectedScopeCategories || []);
  elements.backupControlScopeAdditional.value =
    profile?.protectedScopeAdditional || "";
  elements.backupControlScopeExclusions.value =
    profile?.protectedScopeExclusions || "";
  elements.backupControlFrequency.value = profile?.frequencyHours || 24;
  elements.backupControlGrace.value = profile?.graceMinutes ?? 120;
  elements.backupControlRetention.value = profile?.retentionDays || 180;
  elements.backupControlRpo.value = profile?.rpoHours || "";
  elements.backupControlRto.value = profile?.rtoMinutes || "";
  elements.backupControlRestoreInterval.value =
    profile?.restoreTestIntervalDays || "";
  elements.backupControlResponsible.value = profile?.technicalResponsible ||
    "Coordinador de Ciberseguridad";
  elements.backupControlOwner.value = profile?.processOwner || "";
  elements.backupControlDestination.value = profile?.expectedDestination || "";
  elements.backupControlExceptionType.value = profile?.exceptionType || "NONE";
  elements.backupControlExceptionReason.value = profile?.exceptionReason || "";
  elements.backupControlExceptionExpiry.value =
    formatDateTimeLocal(profile?.exceptionExpiresAtUtc);
  elements.backupControlEnabled.checked = profile
    ? profile.enabled && ["ACTIVE", "APPROVED"].includes(profile.approvalStatus)
    : true;
  state.backupControlUi.fieldOrigins = profile?.fieldOrigins
    ? { ...profile.fieldOrigins }
    : {};
  if (!profile) {
    const standard = recommendedBackupStandard();
    if (standard) {
      applyBackupStandard(standard, false);
    }
    setBackupFieldOrigin("code", "SUGGESTED");
    setBackupFieldOrigin("processOwner", "MANUAL");
    setBackupFieldOrigin("rto", "MANUAL");
    setBackupFieldOrigin("destination", "MANUAL");
  }
  state.backupControlUi.applyingTemplate = false;
  updateBackupControlConditionalFields();
  renderBackupFieldOrigins();
  elements.backupControlFormPanel.classList.remove("hidden");
  elements.backupControlFormPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function beginBackupObservation(id) {
  const profile = backupControlProfileById(id);
  if (!profile || !canManageBackupControls()) {
    return;
  }
  hideBackupControlEditors();
  elements.backupObservationForm.reset();
  elements.backupObservationProfileId.value = id;
  elements.backupObservationTitle.textContent =
    `Registrar comprobación · ${profile.code}`;
  elements.backupObservationSource.value = profile.provider === "ACRONIS"
    ? "ACRONIS_EVIDENCE"
    : "MANUAL";
  const now = new Date();
  const start = new Date(now);
  start.setHours(0, 0, 0, 0);
  elements.backupObservationPeriodStart.value = formatDateTimeLocal(start);
  elements.backupObservationPeriodEnd.value = formatDateTimeLocal(now);
  elements.backupObservationFinished.value = formatDateTimeLocal(now);
  elements.backupObservationPanel.classList.remove("hidden");
  elements.backupObservationPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function beginBackupRestoration(id) {
  const profile = backupControlProfileById(id);
  if (!profile || !canManageBackupControls()) {
    return;
  }
  hideBackupControlEditors();
  elements.backupRestorationForm.reset();
  elements.backupRestorationProfileId.value = id;
  elements.backupRestorationTitle.textContent =
    `Documentar restauración · ${profile.code}`;
  const now = new Date();
  elements.backupRestorationStarted.value = formatDateTimeLocal(now);
  elements.backupRestorationFinished.value = formatDateTimeLocal(now);
  elements.backupRestorationExecutedBy.value =
    state.session?.displayName || "Coordinador de Ciberseguridad";
  elements.backupRestorationPanel.classList.remove("hidden");
  elements.backupRestorationPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function beginBackupEvidence(id) {
  const profile = backupControlProfileById(id);
  if (!profile || !canManageBackupControls()) {
    return;
  }
  hideBackupControlEditors();
  elements.backupEvidenceForm.reset();
  elements.backupEvidenceProfileId.value = id;
  elements.backupEvidenceTitle.textContent = `Incorporar evidencia · ${profile.code}`;
  elements.backupEvidenceAt.value = formatDateTimeLocal(new Date());
  elements.backupEvidenceSource.value =
    backupControlProviderLabels[profile.provider] || profile.provider;
  elements.backupEvidencePanel.classList.remove("hidden");
  elements.backupEvidencePanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function openBackupControlDetail(id) {
  try {
    const detail = await api(
      `/api/v1/admin/backup-controls/profiles/${encodeURIComponent(id)}`);
    state.backupControlUi.selectedId = id;
    state.backupControlUi.detail = detail;
    const profile = detail.profile;
    elements.backupControlDetailTitle.textContent =
      `Seguimiento de copia · ${profile.systemName}`;
    elements.backupControlDetailTitle.title = profile.code;
    elements.backupControlDetailMeta.textContent =
      `${profile.agencyName || profile.agencyCode} · ${backupControlProviderLabels[profile.provider] || profile.provider} · ${backupControlApprovalLabels[profile.approvalStatus] || profile.approvalStatus}`;
    const observations = (detail.observations || []).map((item) =>
      `<li><strong>${escapeHtml(backupControlResultLabels[item.result] || item.result)}</strong><span>${escapeHtml(item.source)} · ${escapeHtml(formatDate(item.periodEndUtc))}</span><small>${escapeHtml(item.detail || item.externalReference || "Sin observaciones")}</small></li>`
    ).join("") || "<li><span>Sin comprobaciones manuales.</span></li>";
    const restorations = (detail.restorations || []).map((item) =>
      `<li><strong>${escapeHtml(backupRestorationLabels[item.modality] || item.modality)}</strong><span>${escapeHtml(backupControlResultLabels[item.result] || item.result)} · ${escapeHtml(formatDate(item.finishedAtUtc))}</span><small>${escapeHtml(item.testedScope)}</small></li>`
    ).join("") || "<li><span>Sin pruebas registradas.</span></li>";
    const evidence = (detail.evidence || []).map((item) =>
      `<li><a href="/api/v1/admin/backup-controls/evidence/${encodeURIComponent(item.id)}/file" target="_blank" rel="noopener">${escapeHtml(item.originalFileName)}</a><span>${escapeHtml(item.evidenceType)} · ${escapeHtml(formatDate(item.evidenceAtUtc))}</span><small>SHA-256 ${escapeHtml(item.sha256)}</small></li>`
    ).join("") || "<li><span>Sin archivos de evidencia.</span></li>";
    const originCounts = Object.values(profile.fieldOrigins || {}).reduce(
      (counts, origin) => {
        counts[origin] = (counts[origin] || 0) + 1;
        return counts;
      }, {});
    const exception = profile.exceptionType === "NONE"
      ? "Sin excepción"
      : `${backupControlExceptionLabels[profile.exceptionType] || profile.exceptionType}: ${profile.exceptionReason || "Sin detalle"}`;
    elements.backupControlDetailBody.innerHTML = `
      <div class="backup-control-detail-grid">
        <div><span>Alcance protegido</span><strong>${escapeHtml(profile.protectedScope)}</strong></div>
        <div><span>Responsables</span><strong>${escapeHtml(profile.technicalResponsible)}</strong><small>${escapeHtml(profile.processOwner || "Propietario pendiente")}</small></div>
        <div><span>Frecuencia</span><strong>Cada ${formatNumber(profile.frequencyHours)} horas</strong><small>Margen ${formatNumber(profile.graceMinutes)} minutos</small></div>
        <div><span>Continuidad</span><strong>RPO ${profile.rpoHours ? `${formatNumber(profile.rpoHours)} h` : "pendiente"}</strong><small>RTO ${profile.rtoMinutes ? `${formatNumber(profile.rtoMinutes)} min` : "pendiente"}</small></div>
        <div><span>Estándar aplicado</span><strong>${escapeHtml(profile.standardName)} · v${escapeHtml(profile.standardVersion)}</strong><small>${escapeHtml(backupControlConfigurationLabels[profile.configurationMode] || profile.configurationMode)}</small></div>
        <div><span>Procedencia</span><strong>${formatNumber(originCounts.DETECTED || 0)} detectados · ${formatNumber(originCounts.INHERITED || 0)} heredados</strong><small>${formatNumber(originCounts.MANUAL || 0)} manuales · ${formatNumber(originCounts.OVERRIDE || 0)} personalizados</small></div>
        <div><span>Retención institucional</span><strong>6 meses</strong><small>Registrada como ${formatNumber(profile.retentionDays)} días operativos</small></div>
        <div><span>Tratamiento especial</span><strong>${escapeHtml(exception)}</strong><small>${profile.exceptionExpiresAtUtc ? `Revisión ${escapeHtml(formatDate(profile.exceptionExpiresAtUtc))}` : "Sin vencimiento excepcional"}</small></div>
      </div>
      <div class="backup-control-history-grid">
        <section><h4>Comprobaciones</h4><ul>${observations}</ul></section>
        <section><h4>Restauraciones</h4><ul>${restorations}</ul></section>
        <section><h4>Evidencias</h4><ul>${evidence}</ul></section>
      </div>`;
    if (typeof renderBackupActivityDetail === "function") renderBackupActivityDetail(detail);
    elements.backupControlDetailPanel.classList.remove("hidden");
    elements.backupControlDetailPanel.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible abrir el detalle del control.");
    }
  }
}

async function saveBackupControl(event) {
  event.preventDefault();
  syncBackupProtectedScope();
  const id = elements.backupControlId.value;
  const body = {
    code: elements.backupControlCode.value,
    scopeType: elements.backupControlScopeType.value,
    deviceId: elements.backupControlDevice.value || null,
    agencyCode: elements.backupControlAgency.value,
    systemName: elements.backupControlSystem.value,
    protectedScope: elements.backupControlProtectedScope.value,
    protectedFolders: document.getElementById("backupControlFolders").value.split(/\r?\n/).map(value => value.trim()).filter(Boolean),
    provider: elements.backupControlProvider.value,
    operationMode: elements.backupControlMode.value,
    technology: elements.backupControlTechnology.value,
    taskName: elements.backupControlTask.value || null,
    nasApplianceId: elements.backupControlNas.value || null,
    expectedDestination: elements.backupControlDestination.value || null,
    frequencyHours: Number(elements.backupControlFrequency.value),
    graceMinutes: Number(elements.backupControlGrace.value),
    retentionDays: nullableNumber(elements.backupControlRetention),
    rpoHours: nullableNumber(elements.backupControlRpo),
    rtoMinutes: nullableNumber(elements.backupControlRto),
    restoreTestIntervalDays: nullableNumber(elements.backupControlRestoreInterval),
    technicalResponsible: elements.backupControlResponsible.value,
    processOwner: elements.backupControlOwner.value || null,
    standardId: elements.backupControlStandard.value || null,
    configurationMode: elements.backupControlConfigurationMode.value,
    configurationReason: elements.backupControlReason.value || null,
    protectedScopeCategories: selectedBackupScopeCategories(),
    protectedScopeAdditional: elements.backupControlScopeAdditional.value || null,
    protectedScopeExclusions: elements.backupControlScopeExclusions.value || null,
    fieldOrigins: state.backupControlUi.fieldOrigins || {},
    exceptionType: elements.backupControlExceptionType.value,
    exceptionReason: elements.backupControlExceptionReason.value || null,
    exceptionExpiresAtUtc: nullableIso(elements.backupControlExceptionExpiry),
    enabled: elements.backupControlEnabled.checked,
    activate: elements.backupControlEnabled.checked
  };
  elements.backupControlSave.disabled = true;
  try {
    const result = await api(
      id
        ? `/api/v1/admin/backup-controls/profiles/${encodeURIComponent(id)}`
        : "/api/v1/admin/backup-controls/profiles",
      backupControlMutationOptions(id ? "PUT" : "POST", body));
    const savedId = result?.profile?.id || id || null;
    hideBackupControlEditors();
    await loadBackupControl(savedId, Boolean(savedId));
    showToast(result?.profile?.approvalStatus === "ACTIVE"
      ? "Control guardado. El seguimiento está activo."
      : "Borrador guardado. El seguimiento está desactivado.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        invalid_backup_control_profile: "Revise los campos obligatorios y las carpetas exactas del control.",
        backup_control_scope_configuration_required: "Indica las carpetas exactas que utiliza esta copia; la descripción general no basta para comparar el alcance.",
        invalid_backup_control_relationship: "El equipo o NAS no pertenece a la agencia seleccionada.",
        backup_control_profile_code_exists: "Ya existe un control con ese código."
      };
      showToast(messages[error.code] || "No fue posible guardar el control.");
    }
  } finally {
    elements.backupControlSave.disabled = false;
  }
}

async function saveBackupObservation(event) {
  event.preventDefault();
  const id = elements.backupObservationProfileId.value;
  const body = {
    source: elements.backupObservationSource.value,
    result: elements.backupObservationResult.value,
    periodStartUtc: nullableIso(elements.backupObservationPeriodStart),
    periodEndUtc: nullableIso(elements.backupObservationPeriodEnd),
    startedAtUtc: nullableIso(elements.backupObservationStarted),
    finishedAtUtc: nullableIso(elements.backupObservationFinished),
    externalReference: elements.backupObservationReference.value || null,
    detail: elements.backupObservationDetail.value || null
  };
  elements.backupObservationSave.disabled = true;
  try {
    await api(
      `/api/v1/admin/backup-controls/profiles/${encodeURIComponent(id)}/observations`,
      backupControlMutationOptions("POST", body));
    hideBackupControlEditors();
    await loadBackupControl(id, true);
    showToast("Comprobación registrada con trazabilidad.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.code === "backup_control_observation_exists"
        ? "Ese comprobante ya fue registrado."
        : "Revise las fechas y el resultado de la comprobación.");
    }
  } finally {
    elements.backupObservationSave.disabled = false;
  }
}

async function saveBackupRestoration(event) {
  event.preventDefault();
  const id = elements.backupRestorationProfileId.value;
  const body = {
    modality: elements.backupRestorationModality.value,
    result: elements.backupRestorationResult.value,
    operationalCaseId: null,
    backupUsedAtUtc: nullableIso(elements.backupRestorationBackupUsed),
    startedAtUtc: nullableIso(elements.backupRestorationStarted),
    finishedAtUtc: nullableIso(elements.backupRestorationFinished),
    testedScope: elements.backupRestorationScope.value,
    integrityVerified: elements.backupRestorationIntegrity.checked,
    functionalityVerified: elements.backupRestorationFunctionality.checked,
    rtoAchievedMinutes: nullableNumber(elements.backupRestorationRto),
    executedBy: elements.backupRestorationExecutedBy.value,
    validatedBy: elements.backupRestorationValidatedBy.value || null,
    validatedAtUtc: nullableIso(elements.backupRestorationValidatedAt),
    notes: elements.backupRestorationNotes.value || null,
    historical: elements.backupRestorationHistorical.checked,
    nextTestDueUtc: nullableIso(elements.backupRestorationNextDue)
  };
  elements.backupRestorationSave.disabled = true;
  try {
    await api(
      `/api/v1/admin/backup-controls/profiles/${encodeURIComponent(id)}/restorations`,
      backupControlMutationOptions("POST", body));
    hideBackupControlEditors();
    await loadBackupControl(id, true);
    showToast("Prueba de restauración documentada.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("Revise las fechas, el alcance y la validación de la prueba.");
    }
  } finally {
    elements.backupRestorationSave.disabled = false;
  }
}

async function saveBackupEvidence(event) {
  event.preventDefault();
  const id = elements.backupEvidenceProfileId.value;
  const file = elements.backupEvidenceFile.files?.[0];
  const evidenceAt = nullableIso(elements.backupEvidenceAt);
  if (!file || !evidenceAt) {
    showToast("Seleccione un archivo e indique la fecha de la evidencia.");
    return;
  }
  elements.backupEvidenceUpload.disabled = true;
  try {
    await api(
      `/api/v1/admin/backup-controls/profiles/${encodeURIComponent(id)}/evidence`,
      {
        method: "POST",
        headers: {
          "Content-Type": file.type || "application/octet-stream",
          "X-Atlas-Request": "dashboard",
          "X-Atlas-File-Name": encodeURIComponent(file.name),
          "X-Atlas-Evidence-Type": elements.backupEvidenceType.value,
          "X-Atlas-Evidence-At": evidenceAt,
          "X-Atlas-Evidence-Description": encodeURIComponent(
            elements.backupEvidenceDescription.value || ""),
          "X-Atlas-Evidence-Source": encodeURIComponent(
            elements.backupEvidenceSource.value || "")
        },
        body: file
      });
    hideBackupControlEditors();
    await loadBackupControl(id, true);
    showToast("Evidencia protegida y huella SHA-256 calculada.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        backup_evidence_too_large: "La evidencia supera el límite de 25 MB.",
        invalid_backup_evidence_content: "El tipo o contenido del archivo no está permitido.",
        empty_backup_evidence: "El archivo seleccionado está vacío."
      };
      showToast(messages[error.code] || "No fue posible guardar la evidencia.");
    }
  } finally {
    elements.backupEvidenceUpload.disabled = false;
  }
}

function confirmBackupControlActivation(profile) {
  const dialog = elements.backupActivationDialog;
  if (dialog.open) return Promise.resolve(false);
  elements.backupActivationCode.textContent = profile.code;
  dialog.returnValue = "cancel";
  return new Promise((resolve) => {
    const onClose = () => resolve(dialog.returnValue === "activate");
    dialog.addEventListener("close", onClose, { once: true });
    try {
      dialog.showModal();
    } catch {
      dialog.removeEventListener("close", onClose);
      resolve(false);
    }
  });
}

async function activateBackupControl(id, button) {
  const profile = backupControlProfileById(id);
  if (!profile || !canManageBackupControls() || button?.disabled) return;
  // Capture the version shown before opening the confirmation.
  const expectedUpdatedAtUtc = profile.updatedAtUtc;
  if (button) button.disabled = true;
  try {
    if (!await confirmBackupControlActivation(profile)) return;
    await api(
      `/api/v1/admin/backup-controls/profiles/${encodeURIComponent(id)}/activate`,
      backupControlMutationOptions("POST", { expectedUpdatedAtUtc }));
    await loadBackupControl(id, true);
    showToast("Seguimiento activado.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.code === "invalid_backup_control_activation_transition"
        ? "El control cambió o está desactivado. Actualice la vista y revise su configuración."
        : error.code === "backup_control_normative_exception_requires_formal_authority"
          ? "La excepción normativa requiere interpretación o autorización formal."
          : "No fue posible activar el seguimiento.");
    }
  } finally {
    if (button) button.disabled = false;
  }
}

function compareNullable(left, right) {
  if (left === null || left === undefined || left === "") {
    return right === null || right === undefined || right === "" ? 0 : 1;
  }
  if (right === null || right === undefined || right === "") {
    return -1;
  }
  if (typeof left === "number" || typeof right === "number") {
    return Number(left || 0) - Number(right || 0);
  }
  return spanishCollator.compare(String(left), String(right));
}

function sortedBackupCoverage() {
  const complianceRank = {
    FAILED: 0, CANCELLED: 1, WARNING: 1, OVERDUE: 2, MISSING: 3,
    CONFIGURATION_REQUIRED: 4, DESTINATION_MISMATCH: 5,
    COMPLIANT: 6, EXTERNAL: 7, NOT_APPLICABLE: 8
  };
  const field = state.backupCoverageSort.field;
  const direction = state.backupCoverageSort.direction === "desc" ? -1 : 1;
  return [...(Array.isArray(state.backupCoverage) ? state.backupCoverage : [])]
    .sort((left, right) => {
      let leftValue = left[field];
      let rightValue = right[field];
      if (field === "lastBackupAtUtc") {
        leftValue = leftValue ? new Date(leftValue).getTime() : null;
        rightValue = rightValue ? new Date(rightValue).getTime() : null;
      } else if (field === "compliance") {
        leftValue = complianceRank[leftValue] ?? 99;
        rightValue = complianceRank[rightValue] ?? 99;
      }
      const leftMissing = leftValue === null || leftValue === undefined || leftValue === "";
      const rightMissing = rightValue === null || rightValue === undefined || rightValue === "";
      if (leftMissing !== rightMissing) {
        return leftMissing ? 1 : -1;
      }
      const result = compareNullable(leftValue, rightValue);
      return result === 0
        ? spanishCollator.compare(left.machineName || "", right.machineName || "")
        : result * direction;
    });
}

function backupDirectionLabel() {
  if (state.backupCoverageSort.field === "lastBackupAtUtc") {
    return state.backupCoverageSort.direction === "desc"
      ? "Más reciente"
      : "Más antigua";
  }
  if (state.backupCoverageSort.field === "bytesCopied") {
    return state.backupCoverageSort.direction === "desc"
      ? "Mayor volumen"
      : "Menor volumen";
  }
  return state.backupCoverageSort.direction === "desc" ? "Z → A" : "A → Z";
}

function securityDeviceDirectionLabel() {
  if (state.securityPages.deviceSort === "captured") {
    return state.securityPages.deviceDirection === "desc"
      ? "Más reciente"
      : "Más antigua";
  }
  if (state.securityPages.deviceSort === "attention") {
    return state.securityPages.deviceDirection === "desc"
      ? "Mayor atención"
      : "Menor atención";
  }
  return state.securityPages.deviceDirection === "desc" ? "Z → A" : "A → Z";
}

function securityEventDirectionLabel() {
  if (state.securityPages.eventSort === "date") {
    return state.securityPages.eventDirection === "desc"
      ? "Más reciente"
      : "Más antigua";
  }
  if (state.securityPages.eventSort === "severity") {
    return state.securityPages.eventDirection === "desc"
      ? "Mayor severidad"
      : "Menor severidad";
  }
  return state.securityPages.eventDirection === "desc" ? "Z → A" : "A → Z";
}

function backupFinding(item) {
  const compliance = String(item.compliance || "UNKNOWN").toUpperCase();
  const actionable = new Set([
    "WARNING", "CANCELLED", "FAILED", "MISSING", "OVERDUE",
    "CONFIGURATION_REQUIRED", "DESTINATION_MISMATCH"
  ]);
  if (!actionable.has(compliance)) {
    return null;
  }
  const severe = new Set([
    "FAILED", "MISSING", "CONFIGURATION_REQUIRED", "DESTINATION_MISMATCH"
  ]).has(compliance);
  const destination = item.nasHost && item.nasShareName
    ? `\\\\${item.nasHost}\\${item.nasShareName}`
    : item.nasDisplayName || "Destino no confirmado";
  return {
    sourceKind: "BACKUP",
    sourceKey: item.deviceId,
    sourceEntityId: item.deviceId,
    type: "RESTORATION",
    title: `Seguimiento de copia · ${item.machineName}`,
    description: `${item.detail || "ATLAS detectó una condición de respaldo que requiere revisión."}\n\nValide el destino, el resultado más reciente y la posibilidad de recuperación antes de cerrar el expediente.`,
    source: "Cobertura de copias",
    severity: severe ? "HIGH" : "MEDIUM",
    agencyCode: item.agencyCode,
    deviceId: item.deviceId,
    machineName: item.machineName,
    snapshot: {
      machineName: item.machineName,
      agencyCode: item.agencyCode,
      compliance,
      backupProvider: item.backupProvider,
      lastBackupAtUtc: item.lastBackupAtUtc,
      lastBackupStatus: item.lastBackupStatus,
      filesCopied: item.filesCopied,
      filesSkipped: item.filesSkipped,
      bytesCopied: item.bytesCopied,
      destination,
      detail: item.detail
    }
  };
}

function renderBackupCoverage() {
  const items = sortedBackupCoverage();
  elements.backupSortBy.value = state.backupCoverageSort.field;
  elements.backupSortDirection.textContent = backupDirectionLabel();
  const pageSize = state.backupCoveragePage.pageSize;
  const totalPages = Math.max(1, Math.ceil(items.length / pageSize));
  state.backupCoveragePage.page = Math.min(
    Math.max(1, state.backupCoveragePage.page),
    totalPages);
  const page = state.backupCoveragePage.page;
  const visibleItems = items.slice(
    (page - 1) * pageSize,
    page * pageSize);
  elements.backupEmptyState.classList.toggle("hidden", items.length > 0);
  elements.backupCoverageRows.innerHTML = visibleItems.length
    ? visibleItems.map((item) => {
      const provider = backupProviderLabels[item.backupProvider] ||
        item.backupProvider;
      const destination = item.backupProvider === "ATLAS"
        ? item.nasHost && item.nasShareName
          ? `\\\\${item.nasHost}\\${item.nasShareName}`
          : "NAS sin asignar"
        : item.backupProvider === "SHARED_FOLDER"
          ? "Trabajo directo en carpeta de red"
          : "Gestionado fuera de ATLAS";
      const status = coverageLabels[item.compliance] || item.compliance;
      return `
        <tr class="clickable" data-device-id="${escapeHtml(item.deviceId)}">
          <td><span class="cell-primary">${escapeHtml(item.machineName)}</span></td>
          <td>${escapeHtml(item.agencyCode)}</td>
          <td>${escapeHtml(provider)}</td>
          <td><span class="cell-primary">${escapeHtml(item.nasDisplayName || destination)}</span><span class="cell-secondary">${escapeHtml(destination)}</span></td>
          <td title="${escapeHtml(formatDate(item.lastBackupAtUtc))}">${escapeHtml(formatRelative(item.lastBackupAtUtc))}</td>
          <td>${escapeHtml(formatBytes(item.bytesCopied))}</td>
          <td>
            <span class="coverage-badge ${escapeHtml(item.compliance.toLowerCase())}" title="${escapeHtml(item.detail)}">${escapeHtml(status)}</span>
            ${caseFindingButton(backupFinding(item), "Expediente")}
          </td>
        </tr>`;
    }).join("")
    : emptyRow(7, "Todavia no hay equipos activos para evaluar.");
  renderPagination(
    { page, pageSize, total: items.length },
    elements.backupCoveragePaginationLabel,
    elements.backupCoveragePreviousPage,
    elements.backupCoverageNextPage,
    "equipos");
  elements.backupCoverageRows.querySelectorAll("[data-device-id]")
    .forEach((row) => row.addEventListener(
      "click",
      () => void openDevice(row.dataset.deviceId)));
  attachCaseFindingActions(elements.backupCoverageRows);
}

function nasStatusBadge(status) {
  const normalized = Object.hasOwn(nasStatusLabels, status)
    ? status
    : "UNKNOWN";
  return `<span class="nas-status ${normalized.toLowerCase()}">${escapeHtml(nasStatusLabels[normalized])}</span>`;
}

function renderNasLegacy() {
  const appliances = Array.isArray(state.nas) ? state.nas : [];
  const canManage = state.session?.role === "Administrator";
  const groups = new Map();
  appliances.forEach((nas) => {
    const key = `${nas.agencyCode}|${String(nas.host).toLowerCase()}|${nas.smbPort}`;
    if (!groups.has(key)) {
      groups.set(key, []);
    }
    groups.get(key).push(nas);
  });
  elements.nasRows.innerHTML = appliances.length
    ? Array.from(groups.values()).map((resources) => {
      const representative = resources[0];
      const hardware = [representative.manufacturer, representative.model]
        .filter(Boolean).join(" ") || "NAS fisico";
      const groupRow = `
        <tr class="nas-host-row">
          <td colspan="7"><span class="cell-primary">${escapeHtml(hardware)} · ${escapeHtml(representative.host)}</span><span class="cell-secondary">${escapeHtml(representative.agencyCode)} · ${formatNumber(resources.length)} recurso${resources.length === 1 ? "" : "s"} de respaldo registrado${resources.length === 1 ? "" : "s"}</span></td>
        </tr>`;
      const resourceRows = resources.map((nas) => {
      const used = nas.totalBytes > 0 && nas.freeBytes >= 0
        ? nas.totalBytes - nas.freeBytes
        : null;
      const capacity = used === null
        ? "Sin datos"
        : `${formatBytes(used)} de ${formatBytes(nas.totalBytes)}`;
      return `
        <tr class="nas-resource-row">
          <td><span class="cell-primary">${escapeHtml(nas.shareName)}</span><span class="cell-secondary">${escapeHtml(nas.displayName)} · ${escapeHtml(nas.code)}${nas.isPrimary ? " · Principal" : ""}</span></td>
          <td>${escapeHtml(nas.agencyCode)}</td>
          <td><span class="cell-primary">\\\\${escapeHtml(nas.host)}\\${escapeHtml(nas.shareName)}</span><span class="cell-secondary">SMB · Puerto ${formatNumber(nas.smbPort)}</span></td>
          <td>${escapeHtml(capacity)}</td>
          <td title="${escapeHtml(formatDate(nas.lastProbeAtUtc))}">${escapeHtml(formatRelative(nas.lastProbeAtUtc))}</td>
          <td>${nasStatusBadge(nas.lastProbeStatus)}</td>
          <td><div class="row-actions"><button class="secondary-button compact browse-nas" type="button" data-nas-id="${escapeHtml(nas.id)}">Explorar</button>${canManage ? `<button class="secondary-button compact mount-nas" type="button" data-nas-id="${escapeHtml(nas.id)}">Montaje</button><button class="secondary-button compact add-nas-share" type="button" data-nas-id="${escapeHtml(nas.id)}">Añadir recurso</button><button class="secondary-button compact edit-nas" type="button" data-nas-id="${escapeHtml(nas.id)}">Editar</button>` : ""}</div></td>
        </tr>`;
      }).join("");
      return groupRow + resourceRows;
    }).join("")
    : emptyRow(7, "No hay recursos de respaldo registrados.");

  const selected = elements.nasExplorerSelect.value || state.nasBrowser.nasId;
  elements.nasExplorerSelect.innerHTML =
    "<option value=\"\">Seleccione un recurso de respaldo</option>" +
    appliances.filter((nas) => nas.enabled).map((nas) =>
      `<option value="${escapeHtml(nas.id)}">\\\\${escapeHtml(nas.host)}\\${escapeHtml(nas.shareName)} · ${escapeHtml(nas.agencyCode)}</option>`
    ).join("");
  if (appliances.some((nas) => nas.id === selected)) {
    elements.nasExplorerSelect.value = selected;
    state.nasBrowser.nasId = selected;
  }
  renderPagination(
    state.nasBrowser,
    elements.nasExplorerPaginationLabel,
    elements.nasExplorerPreviousPage,
    elements.nasExplorerNextPage,
    "elementos");

  document.querySelectorAll(".browse-nas").forEach((button) => {
    button.addEventListener("click", () => {
      elements.nasExplorerSelect.value = button.dataset.nasId || "";
      state.nasBrowser.nasId = button.dataset.nasId || "";
      switchView("nas");
      void loadNasBrowser("");
    });
  });
  document.querySelectorAll(".edit-nas").forEach((button) => {
    button.addEventListener("click", () => beginEditNas(
      button.dataset.nasId || ""));
  });
  document.querySelectorAll(".add-nas-share").forEach((button) => {
    button.addEventListener("click", () => beginCreateNasResource(
      button.dataset.nasId || ""));
  });
  document.querySelectorAll(".mount-nas").forEach((button) => {
    button.addEventListener("click", () => beginMountNas(
      button.dataset.nasId || ""));
  });
}

function nasGroupKey(nas) {
  return `${nas.agencyCode}|${String(nas.host).toLowerCase()}|${nas.smbPort}`;
}

function nasInitials(value) {
  const parts = String(value || "NAS").trim().split(/[\s_-]+/).filter(Boolean);
  return (parts.slice(0, 2).map((part) => part[0]).join("") || "NA").toUpperCase();
}

function nasCapacity(nas) {
  const used = nas.totalBytes > 0 && nas.freeBytes >= 0
    ? nas.totalBytes - nas.freeBytes
    : null;
  return {
    used,
    label: used === null
      ? "Sin datos"
      : `${formatBytes(used)} de ${formatBytes(nas.totalBytes)}`,
    percent: used === null || nas.totalBytes <= 0
      ? 0
      : Math.min(100, Math.max(0, (used / nas.totalBytes) * 100))
  };
}

function nasGroupStatus(resources) {
  const active = resources.filter(item => item.enabled);
  if (!active.length) return "RETIRED";
  const statuses = active.map((item) => item.lastProbeStatus || "UNKNOWN");
  if (statuses.every((status) => status === "ONLINE")) {
    return "ONLINE";
  }
  if (statuses.some((status) => status === "ONLINE" || status === "DEGRADED")) {
    return "DEGRADED";
  }
  if (statuses.length && statuses.every((status) => status === "OFFLINE")) {
    return "OFFLINE";
  }
  return "UNKNOWN";
}

function renderNas() {
  queueMicrotask(() => applyChartDimensions(elements.nasRows));
  const appliances = Array.isArray(state.nas) ? state.nas : [];
  const canManage = state.session?.role === "Administrator";
  const groups = new Map();
  const lifecycle = state.nasDirectory.status || "ACTIVE";
  document.querySelectorAll('[data-nas-status]').forEach(tab => tab.setAttribute('aria-selected',String(tab.dataset.nasStatus === lifecycle)));
  appliances.filter(nas => lifecycle === "ALL" || (lifecycle === "ACTIVE" ? nas.enabled : !nas.enabled)).forEach((nas) => {
    const key = nasGroupKey(nas);
    if (!groups.has(key)) {
      groups.set(key, []);
    }
    groups.get(key).push(nas);
  });

  const groupEntries = Array.from(groups.entries()).sort((left, right) => {
    const a = left[1][0];
    const b = right[1][0];
    return `${a.agencyCode}|${a.host}`.localeCompare(`${b.agencyCode}|${b.host}`, "es");
  });
  if (!state.nasDirectory.initialized && groupEntries.length) {
    state.nasDirectory.expanded.add(groupEntries[0][0]);
    state.nasDirectory.initialized = true;
  }

  const currentAgency = elements.nasAgencyFilter.value || state.nasDirectory.agency;
  const agencyCodes = [...new Set(appliances.map((nas) => nas.agencyCode))]
    .sort((a, b) => a.localeCompare(b, "es"));
  elements.nasAgencyFilter.innerHTML =
    "<option value=\"\">Todas las agencias</option>" +
    agencyCodes.map((code) => `<option value="${escapeHtml(code)}">${escapeHtml(code)}</option>`).join("");
  if (agencyCodes.includes(currentAgency)) {
    elements.nasAgencyFilter.value = currentAgency;
  } else {
    state.nasDirectory.agency = "";
  }

  const activeGroups = new Map();
  appliances.filter(n => n.enabled).forEach(n => {const key=nasGroupKey(n);activeGroups.set(key,[...(activeGroups.get(key)||[]),n]);});
  const totalCapacity = Array.from(activeGroups.entries()).reduce((sum, [, resources]) => {
    const capacitySource = resources.find((item) => item.enabled && Number(item.totalBytes) > 0);
    return sum + Number(capacitySource?.totalBytes || 0);
  }, 0);
  elements.nasPhysicalCount.textContent = formatNumber(activeGroups.size);
  elements.nasResourceCount.textContent = formatNumber(appliances.filter(n => n.enabled).length);
  elements.nasOnlineCount.textContent = formatNumber(
    appliances.filter((nas) => nas.enabled && nas.lastProbeStatus === "ONLINE").length);
  elements.nasCapacityTotal.textContent = totalCapacity > 0
    ? formatBytes(totalCapacity)
    : "Sin datos";

  const search = String(state.nasDirectory.search || "").trim().toLowerCase();
  const agency = state.nasDirectory.agency;
  const visibleGroups = groupEntries.filter(([, resources]) => {
    const representative = resources[0];
    if (agency && representative.agencyCode !== agency) {
      return false;
    }
    if (!search) {
      return true;
    }
    const searchable = resources.flatMap((nas) => [
      nas.displayName, nas.shareName, nas.code, nas.agencyCode, nas.host,
      nas.manufacturer, nas.model, nas.serialNumber
    ]).join(" ").toLowerCase();
    return searchable.includes(search);
  });

  elements.nasRows.innerHTML = visibleGroups.length
    ? visibleGroups.map(([key, resources]) => {
      const representative = resources[0];
      const capacitySource = resources.find((item) => Number(item.totalBytes) > 0)
        || representative;
      const capacity = nasCapacity(capacitySource);
      const status = nasGroupStatus(resources);
      const expanded = state.nasDirectory.expanded.has(key);
      const hardware = [representative.manufacturer, representative.model]
        .filter(Boolean).join(" ") || "NAS administrado";
      const latestProbe = resources
        .map((item) => item.lastProbeAtUtc)
        .filter(Boolean)
        .sort()
        .at(-1);
      const resourceCards = resources
        .slice()
        .sort((a, b) => String(a.displayName).localeCompare(String(b.displayName), "es"))
        .map((nas) => {
          const itemCapacity = nasCapacity(nas);
          return `
            <button class="atlas-nas-resource" type="button" data-nas-id="${escapeHtml(nas.id)}">
              <span class="atlas-nas-avatar">${escapeHtml(nasInitials(nas.displayName || nas.shareName))}</span>
              <span class="atlas-nas-resource-copy">
                <strong>${escapeHtml(nas.displayName)}</strong>
                <small>\\\\${escapeHtml(nas.host)}\\${escapeHtml(nas.shareName)}</small>
                <span class="atlas-nas-resource-meta">${nas.enabled ? `${escapeHtml(itemCapacity.label)} · ${escapeHtml(formatRelative(nas.lastProbeAtUtc))}` : escapeHtml(nas.retirementReason || "Baja anterior sin motivo registrado")}</span>
              </span>
              <span class="atlas-nas-resource-side">
                ${nasStatusBadge(nas.enabled ? nas.lastProbeStatus : "RETIRED")}
                ${nas.isPrimary ? "<small>Principal</small>" : ""}
                <b aria-hidden="true">${atlasIcon("chevron-right")}</b>
              </span>
            </button>`;
        }).join("");

      return `
        <article class="atlas-nas-appliance ${status === "RETIRED" ? "is-retired" : ""} ${expanded ? "is-expanded" : ""}" data-group-key="${escapeHtml(key)}">
          <button class="atlas-nas-appliance-header" type="button" data-toggle-nas="${escapeHtml(key)}" aria-expanded="${expanded}">
            <span class="atlas-nas-device-mark"><b>NAS</b><i></i><i></i><i></i></span>
            <span class="atlas-nas-appliance-copy">
              <span class="atlas-nas-title-line"><strong>${escapeHtml(hardware)}</strong>${nasStatusBadge(status)}</span>
              <small>${escapeHtml(representative.host)} · ${escapeHtml(representative.agencyCode)}</small>
              <span>${formatNumber(resources.length)} recurso${resources.length === 1 ? "" : "s"} de respaldo</span>
            </span>
            <span class="atlas-nas-chevron" aria-hidden="true">${atlasIcon("chevron-down")}</span>
          </button>
          <div class="atlas-nas-appliance-body" ${expanded ? "" : "hidden"}>
            <div class="atlas-nas-appliance-summary">
              <div><span>Último sondeo</span><strong>${escapeHtml(formatRelative(latestProbe))}</strong></div>
              <div><span>Capacidad</span><strong>${escapeHtml(capacity.label)}</strong></div>
              <div class="atlas-nas-capacity-bar" aria-label="${capacity.percent.toFixed(0)} por ciento utilizado"><i data-chart-width="${capacity.percent.toFixed(2)}"></i></div>
              ${canManage && status !== "RETIRED" ? `<button class="secondary-button compact add-nas-share" type="button" data-nas-id="${escapeHtml(representative.id)}">${atlasIcon("plus")}Añadir recurso</button>` : ""}
            </div>
            <div class="atlas-nas-resource-grid">${resourceCards}</div>
            ${canManage ? `<div class="nas-lifecycle-actions">${nasLifecycleButton(representative.id,status === "RETIRED","NAS",status === "RETIRED" ? "Reactivar NAS" : "Dar de baja NAS")}</div>` : ""}
          </div>
        </article>`;
    }).join("")
    : `<div class="atlas-nas-empty"><strong>No encontramos recursos con esos filtros.</strong><span>Pruebe otra búsqueda o registre un NAS nuevo.</span></div>`;

  const selected = elements.nasExplorerSelect.value || state.nasBrowser.nasId;
  elements.nasExplorerSelect.innerHTML =
    "<option value=\"\">Seleccione un recurso de respaldo</option>" +
    appliances.filter((nas) => nas.enabled).map((nas) =>
      `<option value="${escapeHtml(nas.id)}">\\\\${escapeHtml(nas.host)}\\${escapeHtml(nas.shareName)} · ${escapeHtml(nas.agencyCode)}</option>`
    ).join("");
  if (appliances.some((nas) => nas.id === selected && nas.enabled)) {
    elements.nasExplorerSelect.value = selected;
    state.nasBrowser.nasId = selected;
    if (!elements.nasDetailOverlay.classList.contains("hidden")) {
      updateNasDetail(selected);
    }
  }
  if (selected && !appliances.some(n => n.id === selected && n.enabled)) {
    elements.nasExplorerSelect.value="";
    elements.nasExplorerRows.innerHTML=nasBrowserEmpty("Recurso dado de baja. Su historial se conserva.");
    state.nasBrowser.total=0;
    if (!elements.nasDetailOverlay.classList.contains("hidden")) updateNasDetail(selected);
  }
  renderPagination(
    state.nasBrowser,
    elements.nasExplorerPaginationLabel,
    elements.nasExplorerPreviousPage,
    elements.nasExplorerNextPage,
    "elementos");

  elements.nasRows.querySelectorAll("[data-toggle-nas]").forEach((button) => {
    button.addEventListener("click", () => {
      const key = button.dataset.toggleNas || "";
      if (state.nasDirectory.expanded.has(key)) {
        state.nasDirectory.expanded.delete(key);
      } else {
        state.nasDirectory.expanded.add(key);
      }
      renderNas();
    });
  });
  elements.nasRows.querySelectorAll(".atlas-nas-resource").forEach((button) => {
    button.addEventListener("click", () => openNasDetail(button.dataset.nasId || ""));
  });
  elements.nasRows.querySelectorAll(".add-nas-share").forEach((button) => {
    button.addEventListener("click", () => beginCreateNasResource(button.dataset.nasId || ""));
  });
}

function updateNasDetail(nasId) {
  const nas = state.nas.find((item) => item.id === nasId);
  if (!nas) {
    return;
  }
  const capacity = nasCapacity(nas);
  elements.nasDetailAvatar.textContent = nasInitials(nas.displayName || nas.shareName);
  elements.nasDetailName.textContent = nas.displayName || nas.shareName;
  elements.nasDetailPath.textContent = `\\\\${nas.host}\\${nas.shareName} · ${nas.agencyCode}`;
  elements.nasDetailStatus.textContent = nas.enabled ? nasStatusLabels[nas.lastProbeStatus] || "Sin sondeo" : "Dado de baja";
  elements.nasDetailStatus.className = `status-${nas.enabled ? String(nas.lastProbeStatus || "UNKNOWN").toLowerCase() : "retired"}`;
  for (const key of ["nasDetailExplore", "nasDetailMount", "probeNasButton"]) elements[key].disabled = !nas.enabled;
  document.getElementById("nasLifecycleDetail")?.remove();
  const lifecycleDetail=document.createElement("div");lifecycleDetail.id="nasLifecycleDetail";
  lifecycleDetail.innerHTML = (!nas.enabled ? `<p class="nas-retirement-note"><strong>Dado de baja</strong> · ${escapeHtml(nas.retiredAtUtc ? formatDate(nas.retiredAtUtc) : "Fecha no registrada")} · ${escapeHtml(nas.retiredBy || "Responsable no registrado")}<br>${escapeHtml(nas.retirementReason || "Baja anterior sin motivo registrado")}</p>` : "") + (state.session?.role === "Administrator" ? nasLifecycleButton(nas.id,!nas.enabled,"RESOURCE",nas.enabled ? "Dar de baja recurso" : "Reactivar recurso") : "");
  elements.nasDetailProbe.closest('.atlas-nas-detail-facts').after(lifecycleDetail);
  elements.nasDetailCapacity.textContent = capacity.label;
  elements.nasDetailProbe.textContent = formatRelative(nas.lastProbeAtUtc);
  const finding = nasFinding(nas, capacity.label);
  elements.nasDetailCase.classList.toggle("hidden", !finding || !canManageCases());
  if (finding && canManageCases()) {
    const token = caseFindingToken(finding);
    state.cases.findingRegistry.set(token, finding);
    elements.nasDetailCase.dataset.caseFinding = token;
  } else {
    delete elements.nasDetailCase.dataset.caseFinding;
  }
}

function nasFinding(nas, capacityLabel) {
  if (!nas.enabled) return null;
  const status = String(nas.lastProbeStatus || "UNKNOWN").toUpperCase();
  if (status !== "DEGRADED" && status !== "OFFLINE") {
    return null;
  }
  const severity = status === "OFFLINE" ? "HIGH" : "MEDIUM";
  const path = `\\\\${nas.host}\\${nas.shareName}`;
  return {
    sourceKind: "NAS",
    sourceKey: nas.id,
    sourceEntityId: nas.id,
    type: "INCIDENT",
    title: `Disponibilidad de NAS · ${nas.displayName || nas.shareName}`,
    description: `ATLAS detectó que el recurso ${path} se encuentra ${nasStatusLabels[status] || status}.\n\nCompruebe conectividad, montaje de solo lectura, permisos y disponibilidad del almacenamiento antes de cerrar el expediente.`,
    source: "Sondeo de NAS",
    severity,
    agencyCode: nas.agencyCode,
    snapshot: {
      nasName: nas.displayName || nas.shareName,
      agencyCode: nas.agencyCode,
      path,
      probeStatus: nasStatusLabels[status] || status,
      lastProbeAtUtc: nas.lastProbeAtUtc,
      detail: nas.lastProbeError,
      capacity: capacityLabel
    }
  };
}

let nasDetailReturnFocus = null;
let nasBrowseRequest = 0;

function openNasDetail(nasId) {
  const nas = state.nas.find((item) => item.id === nasId);
  if (!nas) return;
  const dialog = elements.nasDetailOverlay;
  if (!dialog.open) nasDetailReturnFocus = document.activeElement;
  state.nasBrowser = { nasId, path: "", parentPath: null, page: 1, pageSize: 50, total: 0 };
  elements.nasExplorerSelect.value = nasId;
  elements.nasCurrentPath.textContent = "/";
  elements.nasUpButton.disabled = true;
  elements.nasExplorerPreviousPage.disabled = true;
  elements.nasExplorerNextPage.disabled = true;
  elements.nasExplorerPaginationLabel.textContent = "Sin elementos cargados";
  updateNasDetail(nasId);
  dialog.classList.remove("hidden");
  // Native modal escapes animated ancestors and keeps focus inside the detail.
  dialog.oncancel = (event) => { event.preventDefault(); closeNasDetail(); };
  dialog.onclose = () => { if (!dialog.open) closeNasDetail(); };
  if (!dialog.open) dialog.showModal();
  dialog.querySelector(".atlas-nas-detail-body").scrollTop = 0;
  elements.nasDetailClose.focus({ preventScroll: true });
  document.body.classList.add("atlas-nas-detail-open");
  if (nas.enabled) void loadNasBrowser("", 1);
  else {
    nasBrowseRequest++;
    elements.nasExplorerRows.setAttribute("aria-busy", "false");
    elements.nasExplorerRows.innerHTML = nasBrowserEmpty("Recurso dado de baja. Reactívalo para explorar.");
    elements.nasBrowserStatus.textContent = "Monitoreo suspendido";
  }
}

function closeNasDetail() {
  const dialog = elements.nasDetailOverlay;
  if (dialog.classList.contains("hidden")) return;
  if (dialog.open) dialog.close();
  dialog.classList.add("hidden");
  document.body.classList.remove("atlas-nas-detail-open");
  nasBrowseRequest++;
  // Refreshing the directory replaces the original resource button.
  const resource = [...elements.nasRows.querySelectorAll(".atlas-nas-resource")]
    .find(button => button.dataset.nasId === state.nasBrowser.nasId);
  const target = nasDetailReturnFocus?.isConnected ? nasDetailReturnFocus : resource;
  target?.focus({ preventScroll: true });
  nasDetailReturnFocus = null;
}

function nasFilePresentation(item) {
  if (item.isDirectory) {
    return { kind: "folder", icon: "folder", type: "Carpeta" };
  }
  const extension = String(item.name || "").split(".").pop().toLowerCase();
  const catalog = {
    pdf: ["pdf", "file-type-pdf", "Documento PDF"],
    xls: ["excel", "file-type-xls", "Hoja de cálculo"],
    xlsx: ["excel", "file-type-xls", "Hoja de cálculo"],
    csv: ["excel", "file-type-xls", "Datos tabulares"],
    doc: ["word", "file-type-doc", "Documento Word"],
    docx: ["word", "file-type-doc", "Documento Word"],
    ppt: ["presentation", "file-type-ppt", "Presentación"],
    pptx: ["presentation", "file-type-ppt", "Presentación"],
    pst: ["outlook", "mail", "Archivo de Outlook"],
    ost: ["outlook", "mail", "Archivo de Outlook"],
    zip: ["archive", "file-zip", "Archivo comprimido"],
    rar: ["archive", "file-zip", "Archivo comprimido"],
    "7z": ["archive", "file-zip", "Archivo comprimido"],
    jpg: ["image", "photo", "Imagen"],
    jpeg: ["image", "photo", "Imagen"],
    png: ["image", "photo", "Imagen"],
    txt: ["text", "file-text", "Archivo de texto"],
    log: ["text", "file-text", "Registro"]
  };
  const match = catalog[extension] || ["file", "file", "Archivo"];
  return { kind: match[0], icon: match[1], type: match[2] };
}

function nasBrowserEmpty(message) {
  return `<div class="atlas-nas-files-empty"><span aria-hidden="true">${atlasIcon("folder")}</span><strong>${escapeHtml(message)}</strong></div>`;
}

async function loadNasBrowser(path = "", page = 1) {
  const requestId = ++nasBrowseRequest;
  const nasId = elements.nasExplorerSelect.value;
  if (!nasId) {
    elements.nasExplorerRows.setAttribute("aria-busy", "false");
    elements.nasUpButton.disabled = true;
    state.nasBrowser = {
      nasId: "",
      path: "",
      parentPath: null,
      page: 1,
      pageSize: 50,
      total: 0
    };
    elements.nasCurrentPath.textContent = "/";
    elements.nasBrowserStatus.textContent = "Seleccione un recurso de respaldo disponible.";
    elements.nasExplorerRows.innerHTML = nasBrowserEmpty("Seleccione un recurso de respaldo.");
    renderPagination(
      state.nasBrowser,
      elements.nasExplorerPaginationLabel,
      elements.nasExplorerPreviousPage,
      elements.nasExplorerNextPage,
      "elementos");
    return;
  }
  elements.nasBrowserStatus.textContent = "Consultando carpetas y archivos…";
  elements.nasExplorerRows.setAttribute("aria-busy", "true");
  elements.nasExplorerRows.innerHTML = nasBrowserEmpty("Cargando recurso…");
  elements.nasUpButton.disabled = true;
  elements.nasExplorerPreviousPage.disabled = true;
  elements.nasExplorerNextPage.disabled = true;
  const pageSize = Number(state.nasBrowser.pageSize || 50);
  const query = new URLSearchParams({
    path,
    page: String(Math.max(1, page)),
    pageSize: String(pageSize)
  });
  try {
    const result = await api(
      `/api/v1/admin/nas/${encodeURIComponent(nasId)}/browse?${query}`);
    if (requestId !== nasBrowseRequest || elements.nasExplorerSelect.value !== nasId || !state.nas.some(n => n.id === nasId && n.enabled)) return;
    state.nasBrowser = {
      nasId,
      path: result.relativePath,
      parentPath: result.parentPath,
      page: result.page,
      pageSize: result.pageSize,
      total: result.total
    };
    elements.nasCurrentPath.textContent = result.relativePath
      ? `/${result.relativePath}`
      : "/";
    elements.nasBrowserStatus.textContent =
      `${result.nasName} · ${formatNumber(result.total)} elementos · lectura solamente`;
    elements.nasUpButton.disabled = result.parentPath === null;
    elements.nasExplorerRows.innerHTML = result.items.length
      ? result.items.map((item) => {
        const presentation = nasFilePresentation(item);
        return `
          <button class="atlas-nas-file ${item.isDirectory ? "nas-directory" : ""}" type="button" data-path="${escapeHtml(item.relativePath)}" ${item.isDirectory ? "" : "disabled"}>
            <span class="atlas-nas-file-icon ${presentation.kind}">${atlasIcon(presentation.icon)}</span>
            <span class="atlas-nas-file-copy">
              <strong title="${escapeHtml(item.name)}">${escapeHtml(item.name)}</strong>
              <small>${escapeHtml(presentation.type)}${item.isDirectory ? "" : ` · ${escapeHtml(formatBytes(item.sizeBytes))}`}</small>
              <span>${escapeHtml(formatDate(item.lastModifiedAtUtc))}</span>
            </span>
          </button>`;
      }).join("")
      : nasBrowserEmpty("La carpeta está vacía.");
    renderPagination(
      result,
      elements.nasExplorerPaginationLabel,
      elements.nasExplorerPreviousPage,
      elements.nasExplorerNextPage,
      "elementos");
    elements.nasExplorerRows.querySelectorAll(".nas-directory").forEach((row) => {
      row.addEventListener("click", () =>
        void loadNasBrowser(row.dataset.path || "", 1));
    });
  } catch (error) {
    if (requestId !== nasBrowseRequest || elements.nasExplorerSelect.value !== nasId) return;
    if (error.message !== "unauthorized") {
      elements.nasCurrentPath.textContent = path ? `/${path}` : "/";
      state.nasBrowser.path = path;
      state.nasBrowser.parentPath = path ? path.split("/").slice(0, -1).join("/") : null;
      elements.nasUpButton.disabled = state.nasBrowser.parentPath === null;
      state.nasBrowser.page = 1;
      state.nasBrowser.total = 0;
      elements.nasBrowserStatus.textContent = error.status === 409
        ? "El recurso todavía no está montado en el servidor."
        : "No fue posible leer esta ruta.";
      elements.nasExplorerRows.innerHTML = nasBrowserEmpty(
        "Verifique el montaje CIFS o NFS de solo lectura y los permisos del servicio.");
      renderPagination(
        state.nasBrowser,
        elements.nasExplorerPaginationLabel,
        elements.nasExplorerPreviousPage,
        elements.nasExplorerNextPage,
        "elementos");
    }
  } finally {
    if (requestId === nasBrowseRequest) elements.nasExplorerRows.setAttribute("aria-busy", "false");
  }
}

function resetAgencyForm() {
  state.editingAgencyCode = null;
  elements.agencyForm.reset();
  elements.agencyCode.disabled = false;
  elements.agencyFormTitle.textContent = "Nueva agencia";
  elements.agencySubmit.textContent = "Guardar agencia";
}

function beginCreateAgency() {
  resetAgencyForm();
  elements.agencyFormPanel.classList.remove("hidden");
  elements.agencyCode.focus();
}

function beginEditAgency(agencyCode) {
  const agency = state.agencies.find((item) => item.agencyCode === agencyCode);
  if (!agency) {
    return;
  }
  state.editingAgencyCode = agency.agencyCode;
  elements.agencyCode.value = agency.agencyCode;
  elements.agencyCode.disabled = true;
  elements.agencyName.value = agency.displayName || agency.agencyCode;
  elements.agencyLocation.value = agency.location || "";
  elements.agencyResponsible.value = agency.responsible || "";
  elements.agencyProvider.value = agency.defaultBackupProvider || "ATLAS";
  elements.agencyFormTitle.textContent = `Editar ${agency.agencyCode}`;
  elements.agencySubmit.textContent = "Guardar cambios";
  elements.agencyFormPanel.classList.remove("hidden");
  elements.agencyName.focus();
  elements.agencyFormPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function saveAgency(event) {
  event.preventDefault();
  const request = {
    code: state.editingAgencyCode || elements.agencyCode.value,
    displayName: elements.agencyName.value,
    location: elements.agencyLocation.value || null,
    responsible: elements.agencyResponsible.value || null,
    defaultBackupProvider: elements.agencyProvider.value,
    enabled: true
  };
  try {
    const editingCode = state.editingAgencyCode;
    const url = editingCode
      ? `/api/v1/admin/agencies/${encodeURIComponent(editingCode)}`
      : "/api/v1/admin/agencies";
    await api(url, {
      method: editingCode ? "PUT" : "POST",
      headers: { "Content-Type": "application/json", "X-Atlas-Request": "dashboard" },
      body: JSON.stringify(request)
    });
    resetAgencyForm();
    elements.agencyFormPanel.classList.add("hidden");
    await refreshAll();
    showToast(editingCode
      ? "Agencia actualizada y actividad registrada."
      : "Agencia registrada y disponible para nuevos instaladores.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.status === 409
        ? "Ya existe una agencia con ese codigo."
        : "No fue posible registrar la agencia.");
    }
  }
}

function resetNasForm() {
  state.editingNasId = null;
  elements.nasForm.reset();
  elements.nasPort.value = "445";
  elements.nasPrimary.checked = true;
  elements.nasFormTitle.textContent = "Registrar destino de respaldo";
  elements.nasSubmit.textContent = "Guardar recurso";
}

function beginCreateNas() {
  resetNasForm();
  elements.nasFormPanel.classList.remove("hidden");
  elements.nasCode.focus();
}

function beginCreateNasResource(nasId) {
  const source = state.nas.find((item) => item.id === nasId);
  if (!source) {
    return;
  }
  resetNasForm();
  elements.nasAgency.value = source.agencyCode;
  elements.nasHost.value = source.host;
  elements.nasPort.value = String(source.smbPort || 445);
  elements.nasManufacturer.value = source.manufacturer || "";
  elements.nasModel.value = source.model || "";
  elements.nasSerial.value = source.serialNumber || "";
  elements.nasPrimary.checked = false;
  elements.nasFormTitle.textContent = `Añadir recurso a ${source.host}`;
  elements.nasFormPanel.classList.remove("hidden");
  elements.nasCode.focus();
  elements.nasFormPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

function beginEditNas(nasId) {
  const nas = state.nas.find((item) => item.id === nasId);
  if (!nas) {
    return;
  }
  state.editingNasId = nas.id;
  elements.nasCode.value = nas.code;
  elements.nasName.value = nas.displayName;
  elements.nasAgency.value = nas.agencyCode;
  elements.nasHost.value = nas.host;
  elements.nasPort.value = String(nas.smbPort || 445);
  elements.nasShare.value = nas.shareName;
  elements.nasManufacturer.value = nas.manufacturer || "";
  elements.nasModel.value = nas.model || "";
  elements.nasSerial.value = nas.serialNumber || "";
  elements.nasPrimary.checked = Boolean(nas.isPrimary);
  elements.nasFormTitle.textContent = `Editar ${nas.code}`;
  elements.nasSubmit.textContent = "Guardar cambios";
  elements.nasFormPanel.classList.remove("hidden");
  elements.nasName.focus();
  elements.nasFormPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function saveNas(event) {
  event.preventDefault();
  const request = {
    code: elements.nasCode.value,
    agencyCode: elements.nasAgency.value,
    displayName: elements.nasName.value,
    host: elements.nasHost.value,
    smbPort: Number(elements.nasPort.value),
    shareName: elements.nasShare.value,
    manufacturer: elements.nasManufacturer.value || null,
    model: elements.nasModel.value || null,
    serialNumber: elements.nasSerial.value || null,
    isPrimary: elements.nasPrimary.checked,
    enabled: state.editingNasId ? Boolean(state.nas.find(n => n.id === state.editingNasId)?.enabled) : true
  };
  try {
    const editingId = state.editingNasId;
    const url = editingId
      ? `/api/v1/admin/nas/${encodeURIComponent(editingId)}`
      : "/api/v1/admin/nas";
    const saved = await api(url, {
      method: editingId ? "PUT" : "POST",
      headers: { "Content-Type": "application/json", "X-Atlas-Request": "dashboard" },
      body: JSON.stringify(request)
    });
    resetNasForm();
    elements.nasFormPanel.classList.add("hidden");
    await refreshAll();
    elements.nasExplorerSelect.value = saved.id;
    showToast(editingId
      ? `Destino actualizado. Verifique que Linux use ${saved.mountPath}.`
      : `Destino registrado. Monte ${saved.mountPath} como CIFS o NFS de solo lectura.`);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(error.status === 409
        ? "El codigo o el recurso principal ya existe."
        : "No fue posible registrar el destino de respaldo.");
    }
  }
}

function updateNasMountFields() {
  const isCifs = elements.nasMountProtocol.value === "CIFS";
  const selectedNas = state.nas.find((item) => item.id === state.mountingNasId);
  const relatedCount = selectedNas
    ? state.nas.filter((item) =>
      item.enabled &&
      item.agencyCode === selectedNas.agencyCode &&
      String(item.host).toLowerCase() === String(selectedNas.host).toLowerCase() &&
      item.smbPort === selectedNas.smbPort).length
    : 0;
  document.querySelectorAll(".nas-cifs-field").forEach((field) =>
    field.classList.toggle("hidden", !isCifs));
  document.querySelectorAll(".nas-nfs-field").forEach((field) =>
    field.classList.toggle("hidden", isCifs));
  elements.nasMountUsername.required = isCifs;
  elements.nasMountPassword.required = isCifs;
  elements.nasMountNfsExport.required = !isCifs;
  elements.nasMountAllShares.disabled = !isCifs || relatedCount < 2;
}

function resetNasMountForm() {
  state.mountingNasId = null;
  elements.nasMountForm.reset();
  elements.nasMountProtocol.value = "CIFS";
  elements.nasMountSecurity.value = "SMB311_SEAL";
  elements.nasMountAllShares.checked = false;
  elements.nasMountTitle.textContent = "Configurar montaje de solo lectura";
  elements.nasMountSubmit.disabled = false;
  elements.nasMountSubmit.textContent = "Configurar y comprobar";
  updateNasMountFields();
}

function beginMountNas(nasId) {
  const nas = state.nas.find((item) => item.id === nasId);
  if (!nas) {
    return;
  }
  resetNasMountForm();
  state.mountingNasId = nas.id;
  const relatedResources = state.nas.filter((item) =>
    item.enabled &&
    item.agencyCode === nas.agencyCode &&
    String(item.host).toLowerCase() === String(nas.host).toLowerCase() &&
    item.smbPort === nas.smbPort);
  elements.nasMountTitle.textContent =
    `Montaje seguro de ${nas.shareName} en ${nas.host}`;
  elements.nasMountAllShares.checked = relatedResources.length > 1;
  elements.nasMountAllShares.disabled = relatedResources.length < 2;
  elements.nasMountScopeText.textContent = relatedResources.length > 1
    ? `Aplicar esta cuenta a los ${relatedResources.length} recursos registrados de este NAS`
    : "Este NAS solo tiene un recurso registrado";
  elements.nasMountNfsExport.value = "";
  elements.nasMountPanel.classList.remove("hidden");
  elements.nasMountUsername.focus();
  elements.nasMountPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function provisionNasMount(event) {
  event.preventDefault();
  const nasId = state.mountingNasId;
  if (!nasId) {
    showToast("Seleccione el recurso NAS que desea montar.");
    return;
  }
  const isCifs = elements.nasMountProtocol.value === "CIFS";
  const request = {
    protocol: elements.nasMountProtocol.value,
    username: isCifs ? elements.nasMountUsername.value : null,
    password: isCifs ? elements.nasMountPassword.value : null,
    domain: isCifs ? (elements.nasMountDomain.value || null) : null,
    smbSecurityProfile: isCifs ? elements.nasMountSecurity.value : null,
    nfsExport: isCifs ? null : elements.nasMountNfsExport.value
  };
  const selectedNas = state.nas.find((item) => item.id === nasId);
  const targets = isCifs && elements.nasMountAllShares.checked && selectedNas
    ? state.nas.filter((item) =>
      item.enabled &&
      item.agencyCode === selectedNas.agencyCode &&
      String(item.host).toLowerCase() === String(selectedNas.host).toLowerCase() &&
      item.smbPort === selectedNas.smbPort)
    : state.nas.filter((item) => item.id === nasId);
  elements.nasMountSubmit.disabled = true;
  elements.nasMountSubmit.textContent = targets.length > 1
    ? `Configurando 0 de ${targets.length}...`
    : "Configurando...";
  try {
    const results = [];
    const failures = [];
    for (let index = 0; index < targets.length; index += 1) {
      const target = targets[index];
      elements.nasMountSubmit.textContent = targets.length > 1
        ? `Configurando ${index + 1} de ${targets.length}...`
        : "Configurando...";
      try {
        results.push(await api(
          `/api/v1/admin/nas/${encodeURIComponent(target.id)}/mount`,
          {
            method: "POST",
            headers: {
              "Content-Type": "application/json",
              "X-Atlas-Request": "dashboard"
            },
            body: JSON.stringify(request)
          }));
      } catch (error) {
        if (error.message === "unauthorized") {
          throw error;
        }
        failures.push({ target, error });
      }
    }
    elements.nasMountPassword.value = "";
    await refreshAll();
    if (failures.length === 0) {
      resetNasMountForm();
      elements.nasMountPanel.classList.add("hidden");
      elements.nasExplorerSelect.value = nasId;
      state.nasBrowser.nasId = nasId;
      await loadNasBrowser("", 1);
      showToast(targets.length > 1
        ? `${results.length} montajes CIFS configurados y comprobados.`
        : `Montaje ${results[0].provisioning.protocol} configurado y comprobado.`);
      return;
    }
    elements.nasMountSubmit.disabled = false;
    elements.nasMountSubmit.textContent = "Reintentar configuración";
    const failedNames = failures.slice(0, 3)
      .map((failure) => failure.target.shareName).join(", ");
    showToast(`${results.length} de ${targets.length} montajes listos. Fallaron: ${failedNames}${failures.length > 3 ? "..." : ""}.`);
  } catch (error) {
    elements.nasMountPassword.value = "";
    elements.nasMountSubmit.disabled = false;
    elements.nasMountSubmit.textContent = "Configurar y comprobar";
    if (error.message !== "unauthorized") {
      const message = error.status === 503
        ? "El servicio local de montajes no está disponible."
        : error.code === "CIFS_UTILS_MISSING"
          ? "Falta cifs-utils en el servidor."
          : error.detail || "No fue posible configurar el montaje de solo lectura.";
      showToast(message);
    }
  }
}

async function probeSelectedNas() {
  const nasId = elements.nasExplorerSelect.value;
  if (!nasId) {
    showToast("Seleccione un recurso de respaldo para sondearlo.");
    return;
  }
  elements.probeNasButton.disabled = true;
  try {
    const result = await api(`/api/v1/admin/nas/${encodeURIComponent(nasId)}/probe`, {
      method: "POST",
      headers: { "X-Atlas-Request": "dashboard" }
    });
    await refreshAll();
    elements.nasExplorerSelect.value = nasId;
    showToast(`Sondeo finalizado: ${nasStatusLabels[result.lastProbeStatus] || result.lastProbeStatus}.`);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible completar el sondeo.");
    }
  } finally {
    elements.probeNasButton.disabled = false;
  }
}

function renderDistribution() {
  if (!state.distribution || !Array.isArray(state.distribution.packages)) {
    elements.distributionRows.innerHTML =
      emptyRow(4, "El catálogo de distribución no está disponible.");
    return;
  }

  elements.distributionRows.innerHTML =
    state.distribution.packages.map((packageInfo) => `
      <tr>
        <td><span class="cell-primary">${escapeHtml(packageInfo.component)}</span></td>
        <td>${escapeHtml(packageInfo.version)}</td>
        <td>${escapeHtml(formatBytes(packageInfo.sizeBytes))}</td>
        <td><span class="cell-secondary" title="${escapeHtml(packageInfo.sha256)}">${escapeHtml(packageInfo.sha256.slice(0, 16))}…</span></td>
      </tr>
    `).join("");
}

function renderReleaseStore() {
  const store = state.releaseStore;
  const deployments = Array.isArray(state.releaseDeployments?.deployments)
    ? state.releaseDeployments.deployments
    : [];
  if (!store || !Array.isArray(store.releases)) {
    elements.activeReleaseLabel.textContent = "Sin versión firmada activa";
    elements.releaseRows.innerHTML =
      emptyRow(6, "Todavía no hay versiones firmadas almacenadas.");
    updateReleaseTabCounts({ production: [], testing: [], history: [] });
    updateReleasePagination(0, 1);
    return;
  }

  const active = store.releases.find((release) => release.isActive);
  elements.activeReleaseLabel.textContent = active
    ? `Activa: ${active.releaseVersion}`
    : "Sin versión firmada activa";
  elements.activeReleaseLabel.classList.toggle("active", Boolean(active));

  if (!store.releases.length) {
    elements.releaseRows.innerHTML =
      emptyRow(6, "Carga el primer paquete firmado de ATLAS.");
    updateReleaseTabCounts({ production: [], testing: [], history: [] });
    updateReleasePagination(0, 1);
    return;
  }

  const groups = classifyReleases(store.releases, active, deployments);
  updateReleaseTabCounts(groups);
  if (!active && state.releaseView.tab === "production") {
    state.releaseView.tab = "testing";
  }
  updateReleaseTabs();

  const selected = groups[state.releaseView.tab] || [];
  const totalPages = Math.max(
    1,
    Math.ceil(selected.length / state.releaseView.pageSize));
  state.releaseView.page = Math.min(state.releaseView.page, totalPages);
  const offset = (state.releaseView.page - 1) * state.releaseView.pageSize;
  const visibleReleases = selected.slice(
    offset,
    offset + state.releaseView.pageSize);

  if (!visibleReleases.length) {
    const emptyMessages = {
      production: "No existe una versión activa en producción.",
      testing: "No hay versiones nuevas ni despliegues controlados en curso.",
      history: "El histórico todavía está vacío."
    };
    elements.releaseRows.innerHTML = emptyRow(
      6,
      emptyMessages[state.releaseView.tab]);
    updateReleasePagination(0, totalPages);
    return;
  }

  elements.releaseRows.innerHTML = visibleReleases.map((release) => {
    const components = Array.isArray(release.packages)
      ? release.packages.map((item) =>
        `${item.component} ${item.version}`).join(" · ")
      : "Sin componentes";
    const targeted = deployments.filter((deployment) =>
      deployment.releaseId === release.releaseId && !deployment.paused);
    const stateLabel = release.isActive
      ? "Activa general"
      : targeted.length
        ? `${targeted.length} despliegue${targeted.length === 1 ? "" : "s"}`
        : "Preparada";
    const deploymentAction = release.isActive
      ? `<span class="release-current-marker">En distribución</span>`
      : `<button class="secondary-button compact deploy-release"
           type="button"
           data-release-id="${escapeHtml(release.releaseId)}"
           data-release-version="${escapeHtml(release.releaseVersion)}">
           Desplegar
         </button>`;
    const summary = release.presentation?.summary ||
      "Versión anterior al catálogo de funcionalidades firmadas.";
    return `
      <tr>
        <td>
          <span class="cell-primary" title="${escapeHtml(release.releaseId)}">${escapeHtml(release.releaseVersion)}</span>
          <span class="cell-secondary release-version-summary" title="${escapeHtml(summary)}">${escapeHtml(summary)}</span>
        </td>
        <td>${escapeHtml(formatDate(release.createdAtUtc))}</td>
        <td><span class="release-components" title="${escapeHtml(components)}">${escapeHtml(components)}</span></td>
        <td>
          <span class="status-badge ${release.signatureVerified ? "online" : "offline"}">
            ${release.signatureVerified ? "Verificada" : "No verificada"}
          </span>
        </td>
        <td><span class="release-state ${release.isActive ? "active" : ""}">${stateLabel}</span></td>
        <td>
          <div class="release-row-actions">
            <button class="secondary-button compact inspect-endpoint-release"
              type="button" data-release-id="${escapeHtml(release.releaseId)}">
              Ver detalles
            </button>
            ${deploymentAction}
          </div>
        </td>
      </tr>`;
  }).join("");

  updateReleasePagination(selected.length, totalPages);

  document.querySelectorAll(".deploy-release").forEach((button) => {
    button.addEventListener("click", () => void deployRelease(
      button.dataset.releaseId,
      button.dataset.releaseVersion
    ));
  });
  document.querySelectorAll(".inspect-endpoint-release").forEach((button) => {
    button.addEventListener("click", () =>
      openEndpointReleaseDetail(button.dataset.releaseId));
  });
}

function classifyReleases(releases, active, deployments) {
  const groups = { production: [], testing: [], history: [] };
  const activeCreatedAt = active
    ? Date.parse(active.createdAtUtc)
    : Number.NEGATIVE_INFINITY;

  releases.forEach((release) => {
    if (release.isActive) {
      groups.production.push(release);
      return;
    }
    const hasControlledDeployment = deployments.some((deployment) =>
      deployment.releaseId === release.releaseId && !deployment.paused);
    const createdAt = Date.parse(release.createdAtUtc);
    if (hasControlledDeployment || !active || createdAt > activeCreatedAt) {
      groups.testing.push(release);
      return;
    }
    groups.history.push(release);
  });
  return groups;
}

function updateReleaseTabCounts(groups) {
  elements.releaseProductionCount.textContent = groups.production.length;
  elements.releaseTestingCount.textContent = groups.testing.length;
  elements.releaseHistoryCount.textContent = groups.history.length;
}

function updateReleaseTabs() {
  const descriptions = {
    production: "Versión activa que recibe actualmente toda la flota.",
    testing: "Versiones verificadas posteriores a producción o asignadas a despliegues controlados.",
    history: "Versiones anteriores conservadas para trazabilidad y evaluación de recuperación."
  };
  document.querySelectorAll(".release-tab").forEach((button) => {
    const selected = button.dataset.releaseTab === state.releaseView.tab;
    button.classList.toggle("active", selected);
    button.setAttribute("aria-selected", String(selected));
  });
  elements.releaseTabDescription.textContent =
    descriptions[state.releaseView.tab];
}

function updateReleasePagination(total, totalPages) {
  const page = Math.min(state.releaseView.page, totalPages);
  if (total === 0) {
    elements.releasePaginationLabel.textContent = "0 versiones";
  } else {
    const start = (page - 1) * state.releaseView.pageSize + 1;
    const end = Math.min(total, page * state.releaseView.pageSize);
    elements.releasePaginationLabel.textContent =
      `${start}–${end} de ${total} versiones`;
  }
  elements.releasePreviousPage.disabled = page <= 1;
  elements.releaseNextPage.disabled = page >= totalPages;
}

function renderServerReleaseStore() {
  const store = state.serverReleaseStore;
  if (!store || !Array.isArray(store.releases)) {
    elements.activeServerReleaseLabel.textContent =
      "Catálogo del servidor no disponible";
    elements.activeServerReleaseLabel.classList.remove("active");
    elements.serverReleaseRows.innerHTML = emptyRow(
      6,
      "No fue posible consultar las versiones del servidor.");
    return;
  }

  const active = store.releases.find((release) => release.isActive);
  elements.activeServerReleaseLabel.textContent = active
    ? `Activa: ${active.releaseVersion}`
    : "Sin versión activa observada";
  elements.activeServerReleaseLabel.classList.toggle("active", Boolean(active));
  if (!store.releases.length) {
    elements.serverReleaseRows.innerHTML = emptyRow(
      6,
      "El catálogo de versiones del servidor está vacío.");
    return;
  }

  elements.serverReleaseRows.innerHTML = store.releases.map((release) => {
    const firstChange = release.changesFromPrevious?.[0] ||
      "Sin comparación documentada";
    const evidence = release.observedOnServer
      ? `${release.schemaMigrationCount ?? "—"} migraciones · SHA verificado`
      : "Catálogo documental";
    const status = release.isActive
      ? '<span class="release-state active">Activa</span>'
      : release.observedOnServer
        ? '<span class="release-state">Observada</span>'
        : '<span class="release-state">Documentada</span>';
    return `
      <tr>
        <td>
          <span class="cell-primary">${escapeHtml(release.releaseVersion)}</span>
          <span class="cell-secondary release-version-summary" title="${escapeHtml(release.summary)}">${escapeHtml(release.summary)}</span>
        </td>
        <td>${escapeHtml(formatDate(release.releasedAtUtc))}</td>
        <td><span class="release-components" title="${escapeHtml(firstChange)}">${escapeHtml(firstChange)}</span></td>
        <td><span class="cell-secondary">${escapeHtml(evidence)}</span></td>
        <td>${status}</td>
        <td>
          <button class="secondary-button compact inspect-server-release"
            type="button" data-server-release="${escapeHtml(release.releaseVersion)}">
            Ver detalles
          </button>
        </td>
      </tr>`;
  }).join("");

  document.querySelectorAll(".inspect-server-release").forEach((button) => {
    button.addEventListener("click", () =>
      openServerReleaseDetail(button.dataset.serverRelease));
  });
}

function openEndpointReleaseDetail(releaseId) {
  const releases = state.releaseStore?.releases || [];
  const selected = releases.find((release) => release.releaseId === releaseId);
  if (!selected) {
    showToast("La versión seleccionada ya no está disponible.");
    return;
  }
  const active = releases.find((release) => release.isActive) || null;
  const chronological = [...releases].sort((left, right) =>
    Date.parse(left.createdAtUtc) - Date.parse(right.createdAtUtc));
  const declaredPrevious = selected.presentation?.previousReleaseVersion;
  const previous = (declaredPrevious
    ? releases.find((release) =>
      release.releaseVersion === declaredPrevious)
    : null) || [...chronological]
      .reverse()
      .find((release) =>
        Date.parse(release.createdAtUtc) < Date.parse(selected.createdAtUtc)) ||
    null;
  const features = selected.presentation?.features || [];
  const changes = selected.presentation?.changesFromPrevious ||
    automaticComponentChanges(selected, previous);
  const comparison = renderEndpointComponentComparison(selected, previous);
  const relation = releaseRelation(selected, active);
  const laterFeatures = relation === "older"
    ? collectEndpointFeaturesAfter(selected, active, releases)
    : [];
  const restoreOutcome = selected.presentation?.restoreOutcome ||
    "El paquete conserva las versiones de componentes indicadas. Esta versión " +
    "es anterior al catálogo firmado y no incluye una declaración histórica " +
    "más detallada sobre su recuperación.";

  openReleaseDetailDialog({
    eyebrow: "RELEASE FIRMADO DE ENDPOINTS",
    title: selected.releaseVersion,
    subtitle: relation === "active"
      ? "Versión general activa"
      : relation === "older"
        ? `Versión anterior a ${active?.releaseVersion || "producción"}`
        : "Versión preparada o en validación",
    content: `
      <p class="release-detail-summary">${escapeHtml(
        selected.presentation?.summary ||
        "Versión conservada para trazabilidad; fue creada antes de incorporar notas firmadas.")}</p>
      <div class="release-detail-facts">
        ${releaseFact("Creada", formatDate(selected.createdAtUtc))}
        ${releaseFact("Versión anterior", previous?.releaseVersion || declaredPrevious || "No determinada")}
        ${releaseFact("Firma", selected.signatureVerified ? "Verificada" : "No verificada")}
        ${releaseFact("Estado", selected.isActive ? "Activa general" : "Almacenada")}
      </div>
      <div class="release-detail-grid">
        ${releaseListSection(
          "Funcionalidades de esta versión",
          features,
          "No existen notas firmadas para este release histórico.")}
        ${releaseListSection(
          "Qué ofrece sobre la anterior",
          changes,
          "No se detectaron cambios de componentes.")}
      </div>
      <section class="release-detail-section">
        <h3>Comparación de componentes</h3>
        ${comparison}
      </section>
      ${laterFeatures.length
        ? releaseListSection(
          "Funciones posteriores que esta versión no incluye",
          laterFeatures,
          "")
        : ""}
      <div class="release-restore-grid">
        <article class="release-restore-card application">
          <h3>Qué se obtendría en el endpoint</h3>
          <p>${escapeHtml(restoreOutcome)}</p>
        </article>
        <article class="release-restore-card full">
          <h3>Límite de la operación normal</h3>
          <p>Activar un release histórico cambia la oferta del servidor, pero el
          Updater protege contra bajas de versión. Un endpoint que ya tenga
          componentes más nuevos no retrocede automáticamente.</p>
        </article>
      </div>
      ${relation === "older" ? releaseWarning(
        "Para volver realmente a esta versión se necesita un procedimiento de " +
        "mantenimiento controlado, validación del paquete y comprobación posterior. " +
        "La pantalla de Actualizaciones no ejecuta ese downgrade.") : ""}
    `
  });
}

function openServerReleaseDetail(releaseVersion) {
  const releases = state.serverReleaseStore?.releases || [];
  const selected = releases.find((release) =>
    release.releaseVersion === releaseVersion);
  if (!selected) {
    showToast("La versión del servidor seleccionada ya no está disponible.");
    return;
  }
  const active = releases.find((release) => release.isActive) || null;
  const relation = selected.isActive
    ? "active"
    : Date.parse(selected.releasedAtUtc) < Date.parse(active?.releasedAtUtc || 0)
      ? "older"
      : "newer";
  const laterFeatures = relation === "older"
    ? collectServerFeaturesAfter(selected, active, releases)
    : [];
  const evidenceLabel = selected.evidenceSource ===
      "RUNTIME_AND_DOCUMENTATION"
    ? "Binario activo y documentación"
    : "Documentación controlada";
  const hash = selected.applicationSha256
    ? selected.applicationSha256
    : "No observado en este servidor";

  openReleaseDetailDialog({
    eyebrow: "VERSIÓN DE ATLAS SERVER",
    title: selected.releaseVersion,
    subtitle: selected.isActive
      ? "Servidor central activo"
      : `Comparada con ${active?.releaseVersion || "la versión activa"}`,
    content: `
      <p class="release-detail-summary">${escapeHtml(selected.summary)}</p>
      <div class="release-detail-facts">
        ${releaseFact("Publicada", formatDate(selected.releasedAtUtc))}
        ${releaseFact("Versión anterior", selected.previousReleaseVersion || "No determinada")}
        ${releaseFact("Evidencia", evidenceLabel)}
        ${releaseFact(
          "Esquema observado",
          selected.schemaMigrationCount == null
            ? "No observado"
            : `${selected.schemaMigrationCount} migraciones`,
          hash)}
      </div>
      <div class="release-detail-grid">
        ${releaseListSection(
          "Funcionalidades de esta versión",
          selected.features,
          "Sin funcionalidades documentadas.")}
        ${releaseListSection(
          "Qué ofrece sobre la anterior",
          selected.changesFromPrevious,
          "Sin diferencias documentadas.")}
      </div>
      ${laterFeatures.length
        ? releaseListSection(
          "Funciones posteriores que dejarían de estar disponibles",
          laterFeatures,
          "")
        : ""}
      <div class="release-restore-grid">
        <article class="release-restore-card application">
          <h3>Restaurar solo la aplicación</h3>
          <p>${escapeHtml(selected.applicationRestoreOutcome)}</p>
        </article>
        <article class="release-restore-card full">
          <h3>Recuperación completa</h3>
          <p>${escapeHtml(selected.fullRestoreOutcome)}</p>
        </article>
      </div>
      ${!selected.observedOnServer ? releaseWarning(
        "Esta versión está documentada, pero el servidor actual no ha registrado " +
        "su binario ni un respaldo compatible. Antes de recuperarla deben verificarse " +
        "el paquete exacto, su SHA-256 y la copia que se utilizará.") : ""}
      ${relation === "older" ? releaseWarning(
        "Una reversión de binarios no elimina migraciones nuevas de PostgreSQL. " +
        "La compatibilidad se valida antes de ejecutar; una recuperación completa " +
        "también devuelve los datos al momento del respaldo y descarta cambios posteriores.") : ""}
    `
  });
}

function openReleaseDetailDialog({ eyebrow, title, subtitle, content }) {
  elements.releaseDetailEyebrow.textContent = eyebrow;
  elements.releaseDetailTitle.textContent = title;
  elements.releaseDetailSubtitle.textContent = subtitle;
  elements.releaseDetailContent.innerHTML = content;
  globalThis.AtlasUpdateSections?.enhanceReleaseDetails(elements.releaseDetailContent);
  elements.releaseDetailOverlay.classList.remove("hidden");
  elements.releaseDetailOverlay.setAttribute("aria-hidden", "false");
  document.body.style.overflow = "hidden";
  elements.releaseDetailClose.focus();
}

function closeReleaseDetailDialog() {
  elements.releaseDetailOverlay.classList.add("hidden");
  elements.releaseDetailOverlay.setAttribute("aria-hidden", "true");
  elements.releaseDetailContent.innerHTML = "";
  document.body.style.overflow = "";
}

function releaseFact(label, value, detail = "") {
  return `
    <div class="release-detail-fact">
      <span>${escapeHtml(label)}</span>
      <strong>${escapeHtml(value)}</strong>
      ${detail ? `<small title="${escapeHtml(detail)}">${escapeHtml(detail)}</small>` : ""}
    </div>`;
}

function releaseListSection(title, values, emptyMessage) {
  const items = Array.isArray(values) ? values.filter(Boolean) : [];
  return `
    <section class="release-detail-section">
      <h3>${escapeHtml(title)}</h3>
      <ul>${items.length
        ? items.map((item) => `<li>${escapeHtml(item)}</li>`).join("")
        : `<li>${escapeHtml(emptyMessage)}</li>`}
      </ul>
    </section>`;
}

function releaseWarning(message) {
  return `
    <div class="release-impact-warning">
      <svg class="atlas-icon" aria-hidden="true"><use href="/assets/vendor/tabler/tabler-icons.svg#ti-alert-triangle"></use></svg>
      <span>${escapeHtml(message)}</span>
    </div>`;
}

function endpointComponentLabel(component) {
  return ({
    agent: "Agente",
    backupnas: "BackupNAS",
    "backup-service": "Servicio VSS",
    updater: "Updater",
    setup: "Instalador"
  })[component] || component;
}

function automaticComponentChanges(selected, previous) {
  if (!previous) {
    return ["Primera versión disponible para comparación en este servidor."];
  }
  const previousMap = new Map((previous.packages || []).map((item) =>
    [item.component, item.version]));
  return (selected.packages || [])
    .filter((item) => previousMap.get(item.component) !== item.version)
    .map((item) => previousMap.has(item.component)
      ? `${endpointComponentLabel(item.component)}: ${previousMap.get(item.component)} → ${item.version}`
      : `${endpointComponentLabel(item.component)} incorporado en ${item.version}`);
}

function renderEndpointComponentComparison(selected, previous) {
  const selectedMap = new Map((selected.packages || []).map((item) =>
    [item.component, item.version]));
  const previousMap = new Map((previous?.packages || []).map((item) =>
    [item.component, item.version]));
  const components = [...new Set([
    ...previousMap.keys(),
    ...selectedMap.keys()
  ])].sort();
  return `
    <div class="table-wrap">
      <table class="release-component-comparison">
        <thead><tr><th>Componente</th><th>Anterior</th><th>Seleccionada</th><th>Diferencia</th></tr></thead>
        <tbody>${components.map((component) => {
          const before = previousMap.get(component) || "No incluido";
          const after = selectedMap.get(component) || "No incluido";
          const difference = before === after
            ? "Sin cambio"
            : before === "No incluido"
              ? "Nuevo"
              : after === "No incluido"
                ? "Retirado"
                : "Actualizado";
          return `<tr>
            <td>${escapeHtml(endpointComponentLabel(component))}</td>
            <td>${escapeHtml(before)}</td>
            <td>${escapeHtml(after)}</td>
            <td class="${difference === "Sin cambio" ? "" : "release-comparison-change"}">${escapeHtml(difference)}</td>
          </tr>`;
        }).join("")}</tbody>
      </table>
    </div>`;
}

function releaseRelation(selected, active) {
  if (!active || selected.releaseId === active.releaseId) {
    return selected.isActive ? "active" : "newer";
  }
  return Date.parse(selected.createdAtUtc) < Date.parse(active.createdAtUtc)
    ? "older"
    : "newer";
}

function collectEndpointFeaturesAfter(selected, active, releases) {
  if (!active) {
    return [];
  }
  const start = Date.parse(selected.createdAtUtc);
  const end = Date.parse(active.createdAtUtc);
  return uniqueReleaseItems(releases
    .filter((release) => {
      const created = Date.parse(release.createdAtUtc);
      return created > start && created <= end;
    })
    .flatMap((release) => release.presentation?.features || []));
}

function collectServerFeaturesAfter(selected, active, releases) {
  if (!active) {
    return [];
  }
  const start = Date.parse(selected.releasedAtUtc);
  const end = Date.parse(active.releasedAtUtc);
  return uniqueReleaseItems(releases
    .filter((release) => {
      const released = Date.parse(release.releasedAtUtc);
      return released > start && released <= end;
    })
    .flatMap((release) => release.features || []));
}

function uniqueReleaseItems(values) {
  const seen = new Set();
  return values.filter((value) => {
    const key = String(value).trim().toLocaleLowerCase("es-GT");
    if (!key || seen.has(key)) {
      return false;
    }
    seen.add(key);
    return true;
  });
}

function deploymentTargetLabel(deployment) {
  if (deployment.scopeType === "RING") {
    return "Anillo PILOTO";
  }
  if (deployment.scopeType === "AGENCY") {
    const agency = state.agencies.find((item) =>
      item.agencyCode === deployment.scopeValue);
    return `Agencia ${agency?.displayName || deployment.scopeValue}`;
  }
  const device = state.devices.items?.find((item) =>
    item.id === deployment.scopeValue);
  return `Equipo ${device?.machineName || deployment.scopeValue}`;
}

function renderReleaseDeployments() {
  const store = state.releaseDeployments;
  const deployments = Array.isArray(store?.deployments)
    ? store.deployments
    : [];
  const pilot = Array.isArray(store?.rings)
    ? store.rings.find((ring) => ring.ringCode === "PILOT")
    : null;
  elements.pilotRingCount.textContent =
    `Ver piloto · ${formatNumber(pilot?.devices || 0)} equipos`;
  elements.pilotRingCount.classList.toggle("active", Boolean(pilot?.devices));

  if (!deployments.length) {
    elements.deploymentRows.innerHTML =
      emptyRow(5, "No hay despliegues dirigidos; todos usan la versión general.");
    return;
  }

  const releases = new Map(
    (state.releaseStore?.releases || []).map((release) =>
      [release.releaseId, release.releaseVersion]));
  elements.deploymentRows.innerHTML = deployments.map((deployment) => `
    <tr>
      <td>
        <span class="deployment-target">${escapeHtml(deploymentTargetLabel(deployment))}</span>
        <span class="cell-secondary">${escapeHtml(`${deployment.scopeType}:${deployment.scopeValue}`)}</span>
      </td>
      <td>${escapeHtml(releases.get(deployment.releaseId) || deployment.releaseId)}</td>
      <td>${formatNumber(deployment.targetDevices)}</td>
      <td><span class="release-state ${deployment.paused ? "paused" : "active"}">${deployment.paused ? "Pausado" : "En curso"}</span></td>
      <td><div class="deployment-actions">
        <button class="secondary-button compact toggle-deployment" type="button"
          data-scope-type="${escapeHtml(deployment.scopeType)}"
          data-scope-value="${escapeHtml(deployment.scopeValue)}"
          data-paused="${deployment.paused ? "false" : "true"}">
          ${deployment.paused ? "Reanudar" : "Pausar"}
        </button>
        <button class="secondary-button compact remove-deployment" type="button"
          data-scope-type="${escapeHtml(deployment.scopeType)}"
          data-scope-value="${escapeHtml(deployment.scopeValue)}">
          Retirar
        </button>
      </div></td>
    </tr>`).join("");

  document.querySelectorAll(".toggle-deployment").forEach((button) => {
    button.addEventListener("click", () => void setDeploymentPaused(
      button.dataset.scopeType,
      button.dataset.scopeValue,
      button.dataset.paused === "true"
    ));
  });
  document.querySelectorAll(".remove-deployment").forEach((button) => {
    button.addEventListener("click", () => void removeDeployment(
      button.dataset.scopeType,
      button.dataset.scopeValue
    ));
  });
}

function deviceContactAt(device) {
  return device.lastContactAtUtc || device.lastSeenAtUtc;
}

function devicePresence(device) {
  const key = String(device.connectivity || "").toLowerCase();
  const labels = { online: "En línea", attention: "Contacto por revisar", offline: "Sin reportar", retired: "Retirado" };
  const status = Object.hasOwn(labels, key) ? key : "unknown";
  const source = device.lastContactAtUtc
    ? `Último contacto recibido por ATLAS: ${formatDate(device.lastContactAtUtc)}.`
    : `Último reporte informado por el agente: ${formatDate(device.lastSeenAtUtc)}.`;
  return { status, label: labels[status] || "Sin confirmar", detail: `${source} El estado se calcula por la antigüedad del contacto; no es una comprobación instantánea de red.` };
}

function deviceIdentityCell(device, includeCustody = false, includeFinding = false) {
  const presence = devicePresence(device);
  return `<div class="motion-device-identity">
    <span class="motion-device-presence ${presence.status}" title="${escapeHtml(presence.detail)}">
      <span class="motion-device-symbol" aria-hidden="true">${atlasIcon("device-desktop")}</span>
      <span class="motion-device-state">${escapeHtml(presence.label)}</span>
      <span class="motion-presence-description">${escapeHtml(presence.detail)}</span>
    </span>
    <span class="motion-device-copy">
      <button type="button" class="cell-primary table-device-open" data-device-open="${escapeHtml(device.id)}" aria-label="Ver ficha de ${escapeHtml(device.machineName)}">${escapeHtml(device.machineName)}</button>
      <span class="cell-secondary">${escapeHtml(cleanInventoryValue(device.serialNumber) || "Serie no reportada")}</span>
      ${includeCustody ? `<span class="cell-secondary" data-custody-summary="${escapeHtml(device.id)}"></span>` : ""}
      ${includeFinding ? caseFindingButton(deviceConnectivityFinding(device), "Expediente") : ""}
    </span>
  </div>`;
}

function deviceOverviewRow(device) {
  const observedUser = observedUserPresentation(device);
  return `
    <tr class="clickable" data-device-id="${escapeHtml(device.id)}">
      <td>${deviceIdentityCell(device, true)}</td>
      <td>
        <span class="cell-primary">${escapeHtml(device.agencyCode)}</span>
        <span class="cell-secondary">${escapeHtml(observedUser.compact)}</span>
      </td>
      <td>${escapeHtml(hardwareLabel(device))}</td>
      <td>${escapeHtml(device.agentVersion)}</td>
      <td>${escapeHtml(securityLabel(device.securityProducts))}</td>
      <td title="${escapeHtml(formatDate(deviceContactAt(device)))}">${escapeHtml(formatRelative(deviceContactAt(device)))}</td>
      <td>${escapeHtml(primaryNetwork(device).address)}</td>
    </tr>`;
}

function observedUserPresentation(device) {
  const account = String(device?.interactiveUserAccount || "").trim();
  const displayName = String(device?.interactiveUserDisplayName || "").trim();
  const checkedAtUtc = device?.interactiveUserCheckedAtUtc || null;
  const primary = displayName || account || (checkedAtUtc
    ? "Sin sesión interactiva"
    : "Usuario aún no reportado");
  const secondaryParts = [];
  if (displayName && account) {
    secondaryParts.push(account);
  }
  if (checkedAtUtc) {
    secondaryParts.push(`observado ${formatRelative(checkedAtUtc)}`);
  } else if (!account) {
    secondaryParts.push("requiere Agente 0.3.2 o posterior");
  }
  return {
    primary,
    secondary: secondaryParts.join(" · "),
    compact: displayName || account || "Usuario no reportado"
  };
}

function deviceConnectivityFinding(device) {
  const connectivity = String(device.connectivity || "UNKNOWN").toUpperCase();
  if (connectivity === "ONLINE") {
    return null;
  }
  return {
    sourceKind: "DEVICE",
    sourceKey: device.id,
    sourceEntityId: device.id,
    type: "INCIDENT",
    title: `Conectividad del activo · ${device.machineName}`,
    description: `El activo ${device.machineName} figura como ${connectivity === "OFFLINE" ? "fuera de línea" : "requiere atención"}.\n\nValide conectividad, estado del servicio ATLAS Agent, fecha del último reporte y cualquier mantenimiento autorizado antes de determinar el tratamiento.`,
    source: "Inventario de activos",
    severity: connectivity === "OFFLINE" ? "HIGH" : "MEDIUM",
    agencyCode: device.agencyCode,
    deviceId: device.id,
    machineName: device.machineName,
    snapshot: {
      machineName: device.machineName,
      agencyCode: device.agencyCode,
      connectivity,
      lastSeenAtUtc: device.lastSeenAtUtc,
      lastContactAtUtc: device.lastContactAtUtc || null,
      manufacturer: device.manufacturer,
      model: device.model,
      serialNumber: device.serialNumber
    }
  };
}

function deviceInventoryRow(device) {
  const observedUser = observedUserPresentation(device);
  const network = primaryNetwork(device);
  const capacity = device.diskTotalBytes > 0
    ? `${formatBytes(device.memoryBytes)} RAM · ${formatBytes(device.diskTotalBytes)} disco`
    : `${formatBytes(device.memoryBytes)} RAM · sin disco`;
  const backupVersion = device.backupClientVersion
    ? `BackupNAS ${device.backupClientVersion}`
    : "Backup: versión no reportada";
  return `
    <tr class="clickable" data-device-id="${escapeHtml(device.id)}">
      <td>${deviceIdentityCell(device, false, true)}</td>
      <td>
        <span class="cell-primary">${escapeHtml(device.agencyCode)}</span>
        <span class="cell-secondary">${escapeHtml(observedUser.primary)}</span>
      </td>
      <td class="asset-technical-column">
        <span class="cell-primary">${escapeHtml(hardwareLabel(device))}</span>
        <span class="cell-secondary">${escapeHtml(device.processor || "Procesador no reportado")}</span>
      </td>
      <td class="asset-technical-column">
        <span class="cell-primary">${escapeHtml(capacity)}</span>
        <span class="cell-secondary">${device.diskUsedPercent === null ? "Uso no disponible" : `${formatNumber(device.diskUsedPercent)}% utilizado`}</span>
      </td>
      <td>${escapeHtml(securityLabel(device.securityProducts))}</td>
      <td>
        <span class="cell-primary">${escapeHtml(`Agente ${device.agentVersion}`)}</span>
        <span class="cell-secondary">${escapeHtml(backupVersion)}</span>
      </td>
      <td title="${escapeHtml(formatDate(deviceContactAt(device)))}">${escapeHtml(formatRelative(deviceContactAt(device)))}</td>
      <td>
        <span class="cell-primary asset-ipv4">${escapeHtml(network.address)}</span>
        <span class="cell-secondary">${escapeHtml(network.assignment)}</span>
      </td>
    </tr>`;
}

function renderDevices() {
  const table = document.getElementById("assetInventoryTable");
  if (table) table.dataset.columns = state.inventoryColumns === "technical" ? "technical" : "essential";
  const toggle = document.getElementById("assetColumnsToggle");
  if (toggle) { toggle.setAttribute("aria-pressed", String(state.inventoryColumns === "technical")); toggle.textContent = state.inventoryColumns === "technical" ? "Vista esencial" : "Vista técnica"; }
  const devices = state.devices.items || [];
  elements.recentDeviceRows.innerHTML = devices.length
    ? devices.slice(0, 8).map(deviceOverviewRow).join("")
    : emptyRow(7, "Todavía no hay agentes que reporten inventario.");
  elements.deviceRows.innerHTML = devices.length
    ? devices.map(deviceInventoryRow).join("")
    : emptyRow(8, "No se encontraron activos con estos filtros.");

  const start = state.devices.total === 0
    ? 0
    : (state.devices.page - 1) * state.devices.pageSize + 1;
  const end = Math.min(
    state.devices.page * state.devices.pageSize,
    state.devices.total
  );
  const totalPages = Math.max(
    1,
    Math.ceil(state.devices.total / state.devices.pageSize)
  );
  elements.deviceResultLabel.textContent =
    `${formatNumber(start)}–${formatNumber(end)} de ${formatNumber(state.devices.total)} activos`;
  elements.paginationLabel.textContent =
    `Página ${formatNumber(state.devices.page)} de ${formatNumber(totalPages)}`;
  elements.previousPage.disabled = state.devices.page <= 1;
  elements.nextPage.disabled = state.devices.page >= totalPages;

  document.querySelectorAll("[data-device-id]").forEach((row) => {
    row.addEventListener("click", event => {
      if (!event.target.closest("button, a, input, select, textarea")) void openDevice(row.dataset.deviceId);
    });
  });
  document.querySelectorAll("[data-device-open]").forEach(button => button.addEventListener("click", () => void openDevice(button.dataset.deviceOpen)));
  attachCaseFindingActions(elements.deviceRows);
  if (typeof refreshCustodyBadges === "function") void refreshCustodyBadges();
}

function cleanInventoryValue(value) {
  const text = String(value ?? "").trim();
  return /^(none|null|unknown|default string|system serial number|system manufacturer|system product name|to be filled by o\.?e\.?m\.?|0+)$/i.test(text) ? "" : text;
}

function detailCard(label, value, detail = "") {
  return `
    <article class="detail-card">
      <span>${escapeHtml(label)}</span>
      <b>${escapeHtml(value || "Sin datos")}</b>
      ${detail ? `<small>${escapeHtml(detail)}</small>` : ""}
    </article>`;
}

function renderTagSection(title, values, emptyMessage, note = "") {
  const tags = Array.isArray(values) && values.length
    ? values.map((value) => `<span class="tag">${escapeHtml(value)}</span>`).join("")
    : `<span class="tag">${escapeHtml(emptyMessage)}</span>`;
  return `
    <section class="detail-section">
      <h3>${escapeHtml(title)}</h3>
      <div class="tag-list">${tags}</div>
      ${note ? `<p class="portal-help">${escapeHtml(note)}</p>` : ""}
    </section>`;
}

function renderDeviceLifecycle(device) {
  if (!device.enabled) {
    return `
      <section class="detail-section retirement-summary">
        <h3>Identidad retirada</h3>
        <p>Esta credencial ya no puede autenticarse contra ATLAS.</p>
        <dl>
          <div><dt>Fecha</dt><dd>${escapeHtml(formatDate(device.retiredAtUtc))}</dd></div>
          <div><dt>Administrador</dt><dd>${escapeHtml(device.retiredBy || "No reportado")}</dd></div>
          <div><dt>Motivo</dt><dd>${escapeHtml(device.retirementReason || "No reportado")}</dd></div>
        </dl>
      </section>`;
  }

  if (state.session?.role !== "Administrator") {
    return "";
  }

  return `
    <section class="detail-section retirement-zone">
      <h3>Retirada controlada</h3>
      <p>
        Revoca la identidad de este equipo sin eliminar su inventario ni
        el historial de copias. Use esta accion antes de una reinstalacion limpia.
      </p>
      <label for="retirementReason">Motivo auditable</label>
      <textarea id="retirementReason" maxlength="256"
        rows="3">Reinstalacion limpia controlada del endpoint.</textarea>
      <button class="danger-button" id="retireDevice" type="button">
        Retirar identidad anterior
      </button>
    </section>`;
}

function renderBackupPolicy(device) {
  const provider = device.backupProvider || "ATLAS";
  const assignedNas = device.nasApplianceId || "";
  const agencyNas = state.nas.filter((nas) =>
    nas.enabled && nas.agencyCode === device.agencyCode);
  const summary = provider === "ATLAS"
    ? device.nasHost && device.nasShareName
      ? `\\\\${device.nasHost}\\${device.nasShareName}`
      : "Recurso SMB pendiente de asignar"
    : provider === "SHARED_FOLDER"
      ? "Puesto comunal; no requiere copia local por equipo"
      : backupProviderLabels[provider] || provider;
  if (state.session?.role !== "Administrator" || !device.enabled) {
    return `
      <section class="detail-section">
        <h3>Politica de respaldo</h3>
        <p>${escapeHtml(summary)}</p>
      </section>`;
  }
  return `
    <section class="detail-section backup-policy-editor">
      <h3>Politica de respaldo</h3>
      <p>Define si el equipo usa BackupNAS, un proveedor externo o trabaja directamente en una carpeta de red.</p>
      <div class="policy-fields">
        <label class="field"><span>Proveedor</span><select id="deviceBackupProvider">
          <option value="ATLAS" ${provider === "ATLAS" ? "selected" : ""}>ATLAS / BackupNAS</option>
          <option value="ACRONIS" ${provider === "ACRONIS" ? "selected" : ""}>Acronis</option>
          <option value="EXTERNAL" ${provider === "EXTERNAL" ? "selected" : ""}>Proveedor externo</option>
          <option value="SHARED_FOLDER" ${provider === "SHARED_FOLDER" ? "selected" : ""}>Puesto comunal / carpeta de red</option>
        </select></label>
        <label class="field"><span>Recurso SMB asignado</span><select id="deviceNasPolicy" ${provider === "ATLAS" ? "" : "disabled"}>
          <option value="">Pendiente de asignar</option>
          ${agencyNas.map((nas) => `<option value="${escapeHtml(nas.id)}" ${nas.id === assignedNas ? "selected" : ""}>\\\\${escapeHtml(nas.host)}\\${escapeHtml(nas.shareName)} · ${escapeHtml(nas.displayName)}</option>`).join("")}
        </select></label>
      </div>
      <button class="primary-button compact" id="saveBackupPolicy" type="button">Guardar politica</button>
    </section>`;
}

function renderUpdateRingPolicy(device) {
  const ring = device.updateRingCode || "GENERAL";
  if (state.session?.role !== "Administrator" || !device.enabled) {
    return `
      <section class="detail-section">
        <h3>Anillo de actualización</h3>
        <p>${escapeHtml(ring)}</p>
      </section>`;
  }
  return `
    <section class="detail-section backup-policy-editor">
      <h3>Anillo de actualización</h3>
      <p>
        PILOT recibe releases de validación antes que el resto de la flota.
        GENERAL conserva la versión global activa.
      </p>
      <div class="policy-fields">
        <label class="field"><span>Anillo asignado</span>
          <select id="deviceUpdateRing">
            <option value="GENERAL" ${ring === "GENERAL" ? "selected" : ""}>GENERAL</option>
            <option value="PILOT" ${ring === "PILOT" ? "selected" : ""}>PILOT</option>
          </select>
        </label>
      </div>
      <button class="primary-button compact" id="saveUpdateRing" type="button">
        Guardar anillo
      </button>
    </section>`;
}

async function saveUpdateRing(device) {
  const ringCode = document.getElementById("deviceUpdateRing")?.value || "";
  const button = document.getElementById("saveUpdateRing");
  if (button) {
    button.disabled = true;
  }
  try {
    await api(`/api/v1/admin/devices/${encodeURIComponent(device.id)}/update-ring`, {
      method: "PUT",
      headers: {
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify({ ringCode })
    });
    await refreshAll();
    await openDevice(device.id);
    showToast(`${device.machineName} quedó en el anillo ${ringCode}.`);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible cambiar el anillo del equipo.");
      if (button) {
        button.disabled = false;
      }
    }
  }
}

async function saveBackupPolicy(device) {
  const provider = document.getElementById("deviceBackupProvider")?.value || "";
  const nasValue = document.getElementById("deviceNasPolicy")?.value || "";
  const button = document.getElementById("saveBackupPolicy");
  if (button) {
    button.disabled = true;
  }
  try {
    await api(`/api/v1/admin/devices/${encodeURIComponent(device.id)}/backup-policy`, {
      method: "PUT",
      headers: { "Content-Type": "application/json", "X-Atlas-Request": "dashboard" },
      body: JSON.stringify({
        backupProvider: provider,
        nasApplianceId: provider === "ATLAS" && nasValue ? nasValue : null
      })
    });
    await refreshAll();
    await openDevice(device.id);
    showToast("Politica de respaldo actualizada y auditada.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const message = error.code === "invalid_backup_policy"
        ? "Seleccione una politica de respaldo valida."
        : error.code === "device_or_nas_policy_invalid" && provider === "ATLAS"
          ? "El NAS debe estar habilitado y pertenecer a la misma agencia."
          : error.code === "device_or_nas_policy_invalid"
            ? "El activo ya no esta disponible para modificar su politica."
            : "No fue posible guardar la politica de respaldo.";
      showToast(message);
      if (button) {
        button.disabled = false;
      }
    }
  }
}

async function retireDevice(device) {
  const reasonInput = document.getElementById("retirementReason");
  const button = document.getElementById("retireDevice");
  const reason = reasonInput?.value.trim().replace(/\s+/g, " ") || "";
  if (reason.length < 8) {
    showToast("Escriba un motivo de al menos 8 caracteres.");
    reasonInput?.focus();
    return;
  }

  const confirmation = window.prompt(
    `Para retirar esta identidad, escriba exactamente: ${device.machineName}`
  );
  if (confirmation !== device.machineName) {
    showToast("La retirada fue cancelada; el nombre del equipo no coincidio.");
    return;
  }

  if (button) {
    button.disabled = true;
    button.textContent = "Retirando...";
  }
  try {
    await api(`/api/v1/admin/devices/${encodeURIComponent(device.id)}/retire`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify({ reason })
    });
    closeDevice();
    await refreshAll();
    showToast(
      `${device.machineName} quedo retirado. Su credencial anterior fue revocada.`
    );
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(
        error.status === 409
          ? "La identidad ya estaba retirada."
          : "No fue posible retirar la identidad del equipo."
      );
      if (button) {
        button.disabled = false;
        button.textContent = "Retirar identidad anterior";
      }
    }
  }
}

let assetDetailRequest = 0;
let assetDetailReturnFocus = null;
let assetDetailReturnId = null;
let assetDetailBackgroundInert = false;
document.addEventListener("keydown", event => {
  if (event.key !== "Tab" || !elements.deviceDrawer.classList.contains("open") || document.querySelector("dialog[open]")) return;
  const controls = [...elements.deviceDrawer.querySelectorAll('button:not([disabled]), a[href], input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex="0"]')].filter(node => node.getClientRects().length && !node.closest("[hidden], .hidden"));
  const first = controls[0], last = controls.at(-1);
  if (!first) { event.preventDefault(); return; }
  if (!elements.deviceDrawer.contains(document.activeElement) || (!event.shiftKey && document.activeElement === last)) { event.preventDefault(); first.focus(); }
  else if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
});
document.getElementById("assetColumnsToggle")?.addEventListener("click", () => {
  state.inventoryColumns = state.inventoryColumns === "technical" ? "essential" : "technical";
  renderDevices();
});
elements.deviceDrawer.addEventListener("cancel", event => { event.preventDefault(); closeDevice(); });
elements.deviceDrawer.addEventListener("click", event => {
  if (event.target !== elements.deviceDrawer) return;
  const bounds = elements.deviceDrawer.getBoundingClientRect();
  if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) closeDevice();
});
async function openDevice(deviceId) {
  if (!deviceId) {
    return;
  }
  const request = ++assetDetailRequest;
  elements.drawerTitle.textContent = "Consultando…";
  elements.drawerSubtitle.textContent = "";
  elements.drawerContent.innerHTML =
    "<div class=\"empty-state\"><p>Cargando inventario real del equipo…</p></div>";
  if (!elements.deviceDrawer.classList.contains("open")) {
    assetDetailReturnFocus = document.activeElement;
    assetDetailReturnId = deviceId;
    elements.deviceDrawer.setAttribute("aria-hidden", "false");
    elements.deviceDrawer.classList.add("open");
    const background = document.querySelector(".app-shell");
    assetDetailBackgroundInert = background?.inert || false;
    if (background) background.inert = true;
    elements.drawerBackdrop.classList.remove("hidden");
    elements.closeDrawer.focus({ preventScroll: true });
  }

  try {
    const detail = await api(`/api/v1/admin/devices/${encodeURIComponent(deviceId)}`);
    if (request !== assetDetailRequest) return;
    const device = detail.device;
    const inventory = detail.inventory;
    const latestBackup = detail.latestBackup;
    const snapshotLabels = {
      vss: "VSS consistente",
      live: "Lectura directa",
      "live-fallback": "VSS no disponible"
    };
    const latestBackupDetail = latestBackup
      ? [
          formatDate(latestBackup.finishedAtUtc),
          snapshotLabels[latestBackup.snapshotMode] || "Modo no reportado"
        ].filter(Boolean).join(" · ")
      : "";
    const network = primaryNetwork(device);
    const clock = inventory?.processor?.maxClockMegahertz
      ? `Frecuencia informada por Windows: ${formatNumber(inventory.processor.maxClockMegahertz)} MHz`
      : "";
    const processorDetail = [
      device.logicalProcessors > 0 ? `${formatNumber(device.logicalProcessors)} procesadores lógicos` : "",
      clock
    ].filter(Boolean).join(" · ");
    const disks = inventory?.disks?.map((disk) =>
      `${disk.name} · ${formatBytes(disk.totalBytes - disk.freeBytes)} de ${formatBytes(disk.totalBytes)} utilizados · ${disk.driveFormat}`
    ) || [];
    const users = inventory?.userProfiles?.map((profile) =>
      profile.profileName
    ) || [];
    const observedUser = observedUserPresentation(device);
    const securityProducts = inventory?.securityProducts?.map((product) =>
      product.name
    ) || [];
    const operatingSystemDetail = [inventory?.operatingSystem?.architecture,
      inventory?.operatingSystem?.buildNumber ? `Compilación ${inventory.operatingSystem.buildNumber}` : ""].filter(Boolean).join(" · ");
    const windowsActivation = device.windowsLicenseStatus == null ? "Sin información"
      : device.windowsLicenseStatus === 1 ? "Activado según Windows"
        : device.windowsLicenseStatus === 0 ? "Windows informa que no está activado" : "Pendiente de comprobar";
    const networkDetails = (Array.isArray(device.networkInterfaces) ? device.networkInterfaces : [])
      .filter(adapter => Array.isArray(adapter.ipv4Addresses) && adapter.ipv4Addresses.length)
      .map(adapter => [adapter.description || "Adaptador reportado", adapter.ipv4Addresses.join(", "),
        adapter.dhcpEnabled === true ? "DHCP" : adapter.dhcpEnabled === false ? "Configuración estática" : "Asignación sin confirmar",
        adapter.macAddress ? `MAC ${adapter.macAddress}` : ""].filter(Boolean).join(" · "));
    const presence = devicePresence(device);

    elements.drawerTitle.textContent = device.machineName;
    elements.drawerSubtitle.textContent = device.agencyCode;
    elements.drawerContent.innerHTML = `
      <div class="detail-status">
        <div>
          <span class="cell-primary">${device.lastContactAtUtc ? "Último contacto con ATLAS" : "Último reporte del agente"}</span>
          <span class="cell-secondary">${escapeHtml(formatDate(deviceContactAt(device)))}</span>
          <span class="cell-secondary">Inventario capturado: ${escapeHtml(formatDate(inventory?.capturedAtUtc || device.inventoryCapturedAtUtc))}</span>
        </div>
        <span class="status-badge ${presence.status}" title="${escapeHtml(presence.detail)}">${escapeHtml(presence.label)}</span>
      </div>
      <div class="detail-grid">
        ${detailCard("Fabricante y modelo reportados", hardwareLabel(device), cleanInventoryValue(device.serialNumber) || "Serie no reportada")}
        ${detailCard("Sistema operativo", device.operatingSystem, operatingSystemDetail)}
        ${detailCard("Procesador", device.processor, processorDetail)}
        ${detailCard("RAM reconocida por Windows", device.memoryBytes > 0 ? formatBytes(device.memoryBytes) : "Sin datos")}
        ${detailCard("Agente ATLAS", device.agentVersion, `${formatNumber(device.pendingEvents)} eventos pendientes`)}
        ${detailCard("Anillo de actualización", device.updateRingCode || "GENERAL", "Despliegue controlado")}
        ${detailCard("Usuario actual observado", observedUser.primary, observedUser.secondary)}
        ${detailCard("Proveedor de respaldo", backupProviderLabels[device.backupProvider] || device.backupProvider, device.nasDisplayName || (device.backupProvider === "SHARED_FOLDER" ? "Sin copia local por equipo" : "Sin NAS asignado"))}
        ${detailCard("BackupNAS", device.backupClientVersion || "Sin reportar", device.backupRunning ? "Copia en ejecución" : "Sin copia en ejecución")}
        ${detailCard("Activación de Windows", windowsActivation, "Estado del último inventario")}
        ${detailCard("Última copia", latestBackup ? (backupLabels[latestBackup.status] || latestBackup.status) : "Sin telemetría", latestBackupDetail)}
        ${detailCard("Dirección IPv4 reportada", network.address, network.adapter)}
        ${detailCard("Asignación IPv4", network.assignment, network.gateways.length ? `Puerta de enlace ${network.gateways.join(", ")}` : "")}
      </div>
      ${renderTagSection("Discos", disks, "No se reportaron discos")}
      ${renderTagSection("Interfaces IPv4 reportadas", networkDetails, "Sin interfaces reportadas", "Direcciones y asignación observadas en cada adaptador; no se realiza un escaneo al abrir esta ficha.")}
      ${renderTagSection("Perfiles locales históricos", users, "No se reportaron perfiles")}
      ${renderUpdateRingPolicy(device)}
      ${renderBackupPolicy(device)}
      ${renderTagSection("Seguridad registrada", [...new Set(securityProducts)], "Sin productos reportados", "Productos registrados en Windows. Su presencia en el inventario no confirma que todos estén activos.")}
      ${typeof assetCustodySection === "function" ? assetCustodySection(device) : ""}
      ${renderDeviceLifecycle(device)}
    `;
    if (typeof organizeAssetDetail === "function") organizeAssetDetail(device);
    document.getElementById("retireDevice")?.addEventListener(
      "click",
      () => void retireDevice(device)
    );
    if (typeof refreshCustodyBadges === "function") void refreshCustodyBadges();
    document.getElementById("deviceBackupProvider")?.addEventListener(
      "change",
      (event) => {
        const nasSelect = document.getElementById("deviceNasPolicy");
        if (nasSelect) {
          nasSelect.disabled = event.target.value !== "ATLAS";
          if (event.target.value !== "ATLAS") {
            nasSelect.value = "";
          }
        }
      });
    document.getElementById("saveBackupPolicy")?.addEventListener(
      "click",
      () => void saveBackupPolicy(device));
    document.getElementById("saveUpdateRing")?.addEventListener(
      "click",
      () => void saveUpdateRing(device));
  } catch (error) {
    if (request !== assetDetailRequest) return;
    if (error.message !== "unauthorized") {
      elements.drawerTitle.textContent = "No disponible";
      elements.drawerContent.innerHTML =
        "<div class=\"empty-state\"><p>No fue posible cargar el detalle de este activo.</p></div>";
    }
  }
}

function closeDevice() {
  assetDetailRequest++;
  const wasOpen = elements.deviceDrawer.classList.contains("open");
  const background = document.querySelector(".app-shell");
  if (wasOpen && background) background.inert = assetDetailBackgroundInert;
  elements.deviceDrawer.classList.remove("open");
  elements.deviceDrawer.setAttribute("aria-hidden", "true");
  elements.drawerBackdrop.classList.add("hidden");
  if (wasOpen) {
    const replacement = [...document.querySelectorAll("[data-device-open]")].find(button => button.dataset.deviceOpen === assetDetailReturnId && button.getClientRects().length);
    const target = assetDetailReturnFocus?.isConnected && assetDetailReturnFocus !== document.body ? assetDetailReturnFocus : replacement;
    if (target && !target.closest('[aria-hidden="true"], [hidden]')) target.focus({ preventScroll: true });
    else { elements.pageTitle.tabIndex = -1; elements.pageTitle.focus({ preventScroll: true }); }
  }
  assetDetailReturnFocus = null;
}

function governanceMutationOptions(method, payload) {
  return {
    method,
    headers: {
      "Content-Type": "application/json",
      "X-Atlas-Request": "dashboard"
    },
    body: JSON.stringify(payload)
  };
}

function governanceDate(value) {
  if (!value) {
    return "Sin definir";
  }
  const date = new Date(`${value}T12:00:00`);
  return Number.isNaN(date.getTime())
    ? "Sin definir"
    : new Intl.DateTimeFormat("es-GT", { dateStyle: "medium" }).format(date);
}

function governanceStatusBadge(document) {
  const reviewDue = document.reviewDue && document.status === "APPROVED";
  const label = governanceStatusLabels[document.status] || document.status;
  let effectiveness = "";
  if (document.status === "APPROVED") {
    const parts = new Intl.DateTimeFormat("en", { timeZone: "America/Guatemala", year: "numeric", month: "2-digit", day: "2-digit" }).formatToParts(new Date());
    const part = type => parts.find(p => p.type === type).value;
    const today = `${part("year")}-${part("month")}-${part("day")}`;
    const v4 = String(document.currentSha256 || "").toUpperCase() === "A0E64776EAFD5D7598810B16124AF4E630C092282CA996AF052C0AF9385EB088";
    const effective = v4 ? "2027-06-01" : String(document.effectiveDate || "").slice(0, 10);
    const validDate = /^\d{4}-\d{2}-\d{2}$/.test(effective);
    const current = validDate && effective <= today;
    const text = v4 && !current ? "Aplicación anticipada · vigor formal 1 jun 2027"
      : !validDate ? "Vigencia por registrar" : current ? "Vigente" : `Programado · ${governanceDate(effective)}`;
    effectiveness = `<span class="governance-effectiveness${current ? " active" : ""}">${escapeHtml(text)}</span>`;
  }
  return `<span class="governance-status-badge ${escapeHtml(String(document.status).toLowerCase())}">${escapeHtml(label)}</span>${effectiveness}${reviewDue ? '<span class="governance-effectiveness">Revisión próxima</span>' : ""}`;
}

function governanceSymbol(type) {
  return {
    MASTER_POLICY: "books",
    POLICY: "shield-check",
    PROCEDURE: "list-details",
    STANDARD: "certificate",
    GUIDE: "file-description",
    FORM: "file-text",
    ANNEX: "archive"
  }[type] || "file-description";
}

function governanceDisplayTitle(title) {
  return String(title || "")
    .trim()
    .replace(/\.(pdf|docx|xlsx|pptx|odt|ods|odp|txt|csv)$/iu, "")
    .trim();
}

function governanceDocumentMatches(document, query) {
  const matchesSearch = !query || [
    document.code,
    document.title,
    document.summary,
    document.ownerName,
    document.parentTitle
  ].some((value) => String(value || "").toLocaleLowerCase("es").includes(query));
  return matchesSearch &&
    (!state.governanceUi.type || document.documentType === state.governanceUi.type) &&
    (!state.governanceUi.status || document.status === state.governanceUi.status);
}

function governanceHierarchyLabel(level, document) {
  const type = governanceTypeLabels[document.documentType] || document.documentType;
  if (level === 1 && document.documentType === "MASTER_POLICY") {
    return "Política principal";
  }
  if (level === 3 && ["FORM", "ANNEX"].includes(document.documentType)) {
    return document.documentType === "FORM" ? "Formato operativo" : "Anexo operativo";
  }
  return type;
}

function governanceDescendantCount(documentId, childrenByParent) {
  const pending = [...(childrenByParent.get(documentId) || [])];
  const visited = new Set();
  while (pending.length) {
    const current = pending.pop();
    if (!current || visited.has(current.id)) {
      continue;
    }
    visited.add(current.id);
    pending.push(...(childrenByParent.get(current.id) || []));
  }
  return visited.size;
}

function governanceRouteHtml(trail) {
  return `<div class="governance-card-route" aria-label="Ruta de dependencia">
    ${atlasIcon("route")}
    <span>${trail.map((item) => escapeHtml(item.code)).join(" <b aria-hidden=\"true\">›</b> ")}</span>
  </div>`;
}

function governanceToggleHtml(document, childCount, collapsed) {
  if (!childCount) {
    return "";
  }
  const label = collapsed ? "Expandir rama" : "Contraer rama";
  return `<button class="governance-branch-toggle" type="button"
                  data-governance-toggle="${escapeHtml(document.id)}"
                  aria-expanded="${collapsed ? "false" : "true"}"
                  aria-controls="governance-children-${escapeHtml(document.id)}"
                  aria-label="${label}: ${escapeHtml(governanceDisplayTitle(document.title))}">
            ${atlasIcon(collapsed ? "chevron-down" : "chevron-up")}
            <span>${label}</span>
          </button>`;
}

function governanceDocumentCard(
  document,
  level,
  trail,
  childCount,
  descendantCount,
  collapsed) {
  const master = level === 1 && document.documentType === "MASTER_POLICY";
  const className = [
    `hierarchy-level-${level}`,
    master ? "master-policy" : "",
    document.reviewDue ? "review-due" : "",
    document.status === "OBSOLETE" ? "obsolete" : ""
  ].filter(Boolean).join(" ");
  const title = governanceDisplayTitle(document.title);
  const version = document.currentVersionLabel
    ? `Versión ${document.currentVersionLabel}`
    : "Sin archivo";
  const relationship = level === 1
    ? descendantCount
      ? `${formatNumber(descendantCount)} ${descendantCount === 1 ? "documento depende" : "documentos dependen"} de esta raíz`
      : "Sin documentos subordinados"
    : `Depende de ${document.parentCode || trail.at(-2)?.code || "su documento superior"}`;
  const toggle = governanceToggleHtml(document, childCount, collapsed);

  return `
    <article class="governance-document-card ${className}"
             data-governance-document="${escapeHtml(document.id)}"
             tabindex="0" aria-label="Abrir ${escapeHtml(title)}">
      <div class="governance-card-main">
        <span class="governance-card-symbol">${atlasIcon(governanceSymbol(document.documentType))}</span>
        <div class="governance-card-copy">
          <div class="governance-card-kicker">
            <span>Nivel ${level}</span>
            ${escapeHtml(governanceHierarchyLabel(level, document))}
          </div>
          <h4>${escapeHtml(title)}</h4>
          <span class="governance-card-code">${escapeHtml(document.code)}</span>
          <p class="governance-card-summary"
             title="${escapeHtml(document.summary)}">${escapeHtml(document.summary)}</p>
          ${governanceRouteHtml(trail)}
        </div>
      </div>
      ${master
        ? `<aside class="governance-policy-side">
             ${governanceStatusBadge(document)}
             <div><strong>${formatNumber(descendantCount)}</strong><small>documentos subordinados</small></div>
             ${toggle}
             <span class="governance-open-document">Abrir política ${atlasIcon("arrow-up-right")}</span>
           </aside>`
        : `<footer class="governance-card-footer">
             <div>${governanceStatusBadge(document)}<span>${escapeHtml(relationship)}</span></div>
             <div>${toggle}<strong>${escapeHtml(version)} ${atlasIcon("arrow-right")}</strong></div>
           </footer>`}
    </article>`;
}

function renderGovernance() {
  const dashboard = state.governance;
  const summary = dashboard?.summary || {};
  elements.governanceApprovedCount.textContent = formatNumber(summary.approved);
  elements.governanceReviewCount.textContent = formatNumber(summary.reviewDue);
  elements.governanceDraftCount.textContent = formatNumber(
    Number(summary.draft || 0) + Number(summary.inReview || 0));
  elements.governanceVersionCount.textContent = formatNumber(summary.versionCount);

  const sourceDocuments = dashboard?.documents || [];
  const query = state.governanceUi.search.toLocaleLowerCase("es");
  const documents = sourceDocuments.filter((document) =>
    governanceDocumentMatches(document, query));
  const byId = new Map(sourceDocuments.map((document) => [document.id, document]));
  const visibleIds = new Set(documents.map((document) => document.id));
  documents.forEach((document) => {
    const visited = new Set();
    let parentId = document.parentDocumentId;
    while (parentId && !visited.has(parentId)) {
      visited.add(parentId);
      visibleIds.add(parentId);
      parentId = byId.get(parentId)?.parentDocumentId;
    }
  });
  const visibleDocuments = sourceDocuments.filter((document) => visibleIds.has(document.id));
  const childrenByParent = new Map();
  sourceDocuments.forEach((document) => {
    if (!document.parentDocumentId) {
      return;
    }
    const children = childrenByParent.get(document.parentDocumentId) || [];
    children.push(document);
    childrenByParent.set(document.parentDocumentId, children);
  });
  childrenByParent.forEach((children) =>
    children.sort((left, right) => left.code.localeCompare(right.code, "es")));
  const visibleChildrenByParent = new Map();
  visibleDocuments.forEach((document) => {
    if (!document.parentDocumentId || !visibleIds.has(document.parentDocumentId)) {
      return;
    }
    const children = visibleChildrenByParent.get(document.parentDocumentId) || [];
    children.push(document);
    visibleChildrenByParent.set(document.parentDocumentId, children);
  });
  visibleChildrenByParent.forEach((children) =>
    children.sort((left, right) => left.code.localeCompare(right.code, "es")));
  const roots = visibleDocuments
    .filter((document) => !document.parentDocumentId || !visibleIds.has(document.parentDocumentId))
    .sort((left, right) => {
      const masterOrder = Number(right.documentType === "MASTER_POLICY") -
        Number(left.documentType === "MASTER_POLICY");
      return masterOrder || left.code.localeCompare(right.code, "es");
    });

  const renderNode = (document, level, ancestors = []) => {
    const safeLevel = Math.min(level, 3);
    const trail = [...ancestors, document];
    const children = visibleChildrenByParent.get(document.id) || [];
    const descendantCount = governanceDescendantCount(document.id, childrenByParent);
    const collapsed = state.governanceUi.collapsed.has(document.id);
    const card = governanceDocumentCard(
      document,
      safeLevel,
      trail,
      children.length,
      descendantCount,
      collapsed);
    if (!children.length || collapsed) {
      const collapsedRegion = collapsed && children.length
        ? `<div id="governance-children-${escapeHtml(document.id)}" hidden></div>`
        : "";
      return `<section class="governance-tree-node level-${safeLevel}">${card}${collapsedRegion}</section>`;
    }
    if (safeLevel === 1) {
      const rows = [];
      for (let index = 0; index < children.length; index += 2) {
        rows.push(children.slice(index, index + 2));
      }
      return `<section class="governance-tree-node governance-tree-root level-1">
        ${card}
        <div class="governance-root-descendants ${rows.length === 1 ? "single-row" : "multiple-rows"}" id="governance-children-${escapeHtml(document.id)}">
          ${rows.map((row) => `<div class="governance-branch-row ${row.length === 1 ? "single" : "multiple"}">
            ${row.map((child) => `<div class="governance-branch">${renderNode(child, 2, trail)}</div>`).join("")}
          </div>`).join("")}
        </div>
      </section>`;
    }
    return `<section class="governance-tree-node level-${safeLevel}">
      ${card}
      <div class="governance-grandchildren" id="governance-children-${escapeHtml(document.id)}">
        ${children.map((child) => renderNode(child, safeLevel + 1, trail)).join("")}
      </div>
    </section>`;
  };

  elements.governanceResultLabel.textContent =
    `${formatNumber(documents.length)} ${documents.length === 1 ? "documento" : "documentos"}` +
    (visibleDocuments.length > documents.length ? " · se muestran documentos superiores para dar contexto" : "");
  elements.governanceDocumentGrid.innerHTML = documents.length
    ? `<div class="governance-document-forest">
        ${roots.map((document) => renderNode(document, 1)).join("")}
      </div>`
    : `<div class="governance-empty"><strong>No hay documentos para esta vista.</strong><p>Ajuste los filtros o cree la primera política controlada.</p></div>`;

  elements.governanceDocumentGrid
    .querySelectorAll("[data-governance-toggle]")
    .forEach((button) => {
      button.addEventListener("click", (event) => {
        event.preventDefault();
        event.stopPropagation();
        const documentId = button.dataset.governanceToggle;
        if (state.governanceUi.collapsed.has(documentId)) {
          state.governanceUi.collapsed.delete(documentId);
        } else {
          state.governanceUi.collapsed.add(documentId);
        }
        renderGovernance();
      });
    });

  elements.governanceDocumentGrid
    .querySelectorAll("[data-governance-document]")
    .forEach((card) => {
      const open = () => void openGovernanceDetail(card.dataset.governanceDocument);
      card.addEventListener("click", (event) => {
        if (!event.target.closest("button")) {
          open();
        }
      });
      card.addEventListener("keydown", (event) => {
        if (event.target.closest("button")) {
          return;
        }
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          open();
        }
      });
    });
}

function analyzeGovernanceParent(candidateId, excludedId, documents) {
  const byId = new Map(documents.map((item) => [item.id, item]));
  const visited = new Set();
  let currentId = candidateId;
  let lineageLength = 0;
  let cycle = false;
  while (currentId) {
    if (currentId === excludedId || visited.has(currentId)) {
      cycle = true;
      break;
    }
    visited.add(currentId);
    const current = byId.get(currentId);
    if (!current) {
      cycle = true;
      break;
    }
    lineageLength += 1;
    currentId = current.parentDocumentId;
  }
  return {
    cycle,
    lineageLength,
    tooDeep: lineageLength > 2
  };
}

function renderGovernanceSelectors(excludedId = null) {
  const documents = (state.governance?.documents || [])
    .filter((item) => item.id !== excludedId)
    .sort((left, right) => left.code.localeCompare(right.code, "es"));
  elements.governanceParent.innerHTML =
    `<option value="">Sin documento superior</option>` +
    documents.map((item) => {
      const analysis = analyzeGovernanceParent(item.id, excludedId, documents);
      const disabled = analysis.cycle || analysis.tooDeep;
      const suffix = analysis.cycle
        ? " · relación circular"
        : analysis.tooDeep
          ? " · alcanzó el nivel nieto"
          : ` · el nuevo documento será nivel ${analysis.lineageLength + 1}`;
      return `<option value="${escapeHtml(item.id)}" ${disabled ? "disabled" : ""}>${escapeHtml(item.code)} · ${escapeHtml(governanceDisplayTitle(item.title))}${escapeHtml(suffix)}</option>`;
    }).join("");
  elements.governanceRelated.innerHTML = documents.map((item) =>
    `<option value="${escapeHtml(item.id)}">${escapeHtml(item.code)} · ${escapeHtml(governanceDisplayTitle(item.title))}</option>`
  ).join("");
  syncGovernanceParentAvailability();
}

function syncGovernanceParentAvailability() {
  const isMasterPolicy = elements.governanceType.value === "MASTER_POLICY";
  elements.governanceParent.disabled = isMasterPolicy;
  if (isMasterPolicy) {
    elements.governanceParent.value = "";
    elements.governanceParent.title =
      "La política principal es el documento raíz y no puede heredar de otro documento.";
  } else {
    elements.governanceParent.removeAttribute("title");
  }
  removeParentFromRelatedDocuments();
}

function removeParentFromRelatedDocuments() {
  const parentId = elements.governanceParent.value;
  if (!parentId) {
    return;
  }
  Array.from(elements.governanceRelated.options).forEach((option) => {
    if (option.value === parentId) {
      option.selected = false;
    }
  });
}

function closeGovernanceEditor() {
  elements.governanceFormPanel.classList.add("hidden");
  state.governanceUi.editingId = null;
}

function closeGovernanceVersion() {
  elements.governanceVersionPanel.classList.add("hidden");
  elements.governanceFile.value = "";
  elements.governanceFileLabel.textContent =
    "Seleccionar PDF, Word, Excel o PowerPoint";
}

function closeGovernanceDetail() {
  elements.governanceDetailPanel.classList.add("hidden");
  elements.governanceApprovalBox.classList.add("hidden");
  elements.governanceDescriptionForm.classList.add("hidden");
  state.governanceUi.selectedId = null;
  state.governanceUi.detail = null;
}

function closeGovernanceDescriptionEditor() {
  elements.governanceDescriptionForm.classList.add("hidden");
}

function beginEditGovernanceDescription() {
  const detail = state.governanceUi.detail;
  if (!detail || detail.document.status !== "APPROVED") {
    showToast("La corrección descriptiva está disponible para documentos vigentes.");
    return;
  }
  const document = detail.document;
  elements.governanceApprovalBox.classList.add("hidden");
  elements.governanceDescriptionSummary.value = document.summary || "";
  elements.governanceDescriptionObjective.value = document.objective || "";
  elements.governanceDescriptionScope.value = document.scope || "";
  elements.governanceDescriptionForm.classList.remove("hidden");
  elements.governanceDescriptionForm.scrollIntoView({ behavior: "smooth", block: "nearest" });
  elements.governanceDescriptionSummary.focus();
}

async function saveGovernanceDescription(event) {
  event.preventDefault();
  const document = state.governanceUi.detail?.document;
  if (!document) {
    return;
  }
  const payload = {
    summary: elements.governanceDescriptionSummary.value,
    objective: elements.governanceDescriptionObjective.value || null,
    scope: elements.governanceDescriptionScope.value || null
  };
  elements.governanceDescriptionSave.disabled = true;
  try {
    await api(
      `/api/v1/admin/governance/documents/${encodeURIComponent(document.id)}/description`,
      governanceMutationOptions("PUT", payload));
    closeGovernanceDescriptionEditor();
    await loadGovernance(document.id, true);
    showToast("Descripción actualizada y registrada en auditoría.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        invalid_governance_description: "El resumen es obligatorio y debe ser válido.",
        governance_description_requires_approved_document:
          "La corrección descriptiva sólo aplica a documentos vigentes."
      };
      showToast(messages[error.code] || "No fue posible actualizar la descripción.");
    }
  } finally {
    elements.governanceDescriptionSave.disabled = false;
  }
}

function beginCreateGovernanceDocument() {
  closeGovernanceDetail();
  closeGovernanceVersion();
  state.governanceUi.editingId = null;
  elements.governanceForm.reset();
  elements.governanceDocumentId.value = "";
  elements.governanceType.value = "POLICY";
  elements.governanceFormTitle.textContent = "Nuevo documento";
  elements.governanceSave.textContent = "Crear documento";
  renderGovernanceSelectors();
  syncGovernanceParentAvailability();
  elements.governanceFormPanel.classList.remove("hidden");
  elements.governanceFormPanel.scrollIntoView({ behavior: "smooth", block: "start" });
  elements.governanceCode.focus();
}

function beginEditGovernanceDocument() {
  const detail = state.governanceUi.detail;
  if (!detail) {
    return;
  }
  const document = detail.document;
  if (document.status === "APPROVED" || document.status === "OBSOLETE") {
    showToast("Para modificar la ficha, incorpore primero una versión nueva.");
    return;
  }
  closeGovernanceVersion();
  state.governanceUi.editingId = document.id;
  elements.governanceForm.reset();
  elements.governanceDocumentId.value = document.id;
  elements.governanceCode.value = document.code;
  elements.governanceType.value = document.documentType;
  elements.governanceTitle.value = document.title;
  elements.governanceOwner.value = document.ownerName;
  elements.governanceSummary.value = document.summary;
  elements.governanceObjective.value = document.objective || "";
  elements.governanceScope.value = document.scope || "";
  renderGovernanceSelectors(document.id);
  elements.governanceParent.value = document.parentDocumentId || "";
  syncGovernanceParentAvailability();
  const related = new Set((detail.relatedDocuments || []).map((item) => item.id));
  Array.from(elements.governanceRelated.options).forEach((option) => {
    option.selected = related.has(option.value);
  });
  removeParentFromRelatedDocuments();
  elements.governanceFormTitle.textContent = `Editar ${document.code}`;
  elements.governanceSave.textContent = "Guardar ficha";
  elements.governanceFormPanel.classList.remove("hidden");
  elements.governanceFormPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function saveGovernanceDocument(event) {
  event.preventDefault();
  const id = elements.governanceDocumentId.value;
  const payload = {
    code: elements.governanceCode.value,
    title: elements.governanceTitle.value,
    documentType: elements.governanceType.value,
    summary: elements.governanceSummary.value,
    objective: elements.governanceObjective.value || null,
    scope: elements.governanceScope.value || null,
    ownerName: elements.governanceOwner.value,
    parentDocumentId: elements.governanceParent.value || null,
    relatedDocumentIds: Array.from(elements.governanceRelated.selectedOptions)
      .map((option) => option.value)
      .filter((relatedId) => relatedId !== elements.governanceParent.value)
  };
  elements.governanceSave.disabled = true;
  try {
    const detail = await api(
      id
        ? `/api/v1/admin/governance/documents/${encodeURIComponent(id)}`
        : "/api/v1/admin/governance/documents",
      governanceMutationOptions(id ? "PUT" : "POST", payload));
    closeGovernanceEditor();
    await loadGovernance(detail.document.id, true);
    showToast(id ? "Ficha documental actualizada." : "Documento creado como borrador.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        governance_document_code_exists: "Ese código documental ya está registrado.",
        approved_governance_document_is_locked: "La versión vigente está bloqueada. Incorpore una versión nueva.",
        invalid_governance_document_relationship: "La jerarquía o relación seleccionada no es válida.",
        governance_document_hierarchy_too_deep: "La jerarquía admite únicamente padre, hijo y nieto."
      };
      showToast(messages[error.code] || "No fue posible guardar el documento.");
    }
  } finally {
    elements.governanceSave.disabled = false;
  }
}

async function loadGovernance(selectedId = null, openDetail = false) {
  const sourceRequest = sourceBegin("governance");
  try {
    const sourceValue = await api("/api/v1/admin/governance");
    if (sourceRequest !== sourceRecord("governance").request) return;
    state.governance = sourceValue;
    sourceSuccess("governance", sourceRequest);
    renderGovernance();
    if (selectedId) {
      state.governanceUi.selectedId = selectedId;
      state.governanceUi.detail = await api(
        `/api/v1/admin/governance/documents/${encodeURIComponent(selectedId)}`);
      if (openDetail || !elements.governanceDetailPanel.classList.contains("hidden")) {
        renderGovernanceDetail();
      }
    }
  } catch (error) {
    if (sourceRequest !== sourceRecord("governance").request) return;
    sourceFailure("governance", sourceRequest);
    if (error.message !== "unauthorized") {
      showToast("No fue posible actualizar la biblioteca documental.");
    }
  }
}

async function openGovernanceDetail(id) {
  closeGovernanceEditor();
  closeGovernanceVersion();
  try {
    state.governanceUi.selectedId = id;
    state.governanceUi.detail = await api(
      `/api/v1/admin/governance/documents/${encodeURIComponent(id)}`);
    renderGovernanceDetail();
    elements.governanceDetailPanel.scrollIntoView({ behavior: "smooth", block: "start" });
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible abrir el documento.");
    }
  }
}

function renderGovernanceDetail() {
  const detail = state.governanceUi.detail;
  if (!detail) {
    return;
  }
  const document = detail.document;
  const versions = detail.versions || [];
  const ancestors = detail.ancestors || [];
  const hierarchyIds = new Set(ancestors.map((item) => item.id));
  const related = (detail.relatedDocuments || [])
    .filter((item) => !hierarchyIds.has(item.id));
  const currentIsPdf = String(document.currentFileName || "")
    .toLowerCase()
    .endsWith(".pdf");
  elements.governanceDetailCode.textContent =
    `${document.code} · ${governanceTypeLabels[document.documentType] || document.documentType}`;
  elements.governanceDetailTitle.textContent = governanceDisplayTitle(document.title);
  elements.governanceDetailMeta.textContent =
    `${governanceStatusLabels[document.status] || document.status} · Responsable: ${document.ownerName}` +
    (document.status === "IN_REVIEW" && !currentIsPdf
      ? " · La aprobación oficial requiere una versión PDF"
      : "");
  elements.governanceApprovalBox.classList.add("hidden");
  elements.governanceDescriptionForm.classList.add("hidden");
  const manageable = canManageGovernance();
  elements.governanceAddVersion.classList.toggle("hidden", !manageable);
  elements.governanceEdit.classList.toggle(
    "hidden", !manageable || ["APPROVED", "OBSOLETE"].includes(document.status));
  elements.governanceEditDescription.classList.toggle(
    "hidden", !manageable || document.status !== "APPROVED");
  elements.governanceReview.classList.toggle(
    "hidden", !manageable || document.status !== "DRAFT" || !document.currentVersionId);
  elements.governanceReturnDraft.classList.toggle(
    "hidden", !manageable || document.status !== "IN_REVIEW");
  elements.governanceRequestCorrection.classList.toggle(
    "hidden", !canApproveGovernance() || document.status !== "IN_REVIEW");
  elements.governanceApprove.classList.toggle(
    "hidden",
    !canApproveGovernance() || document.status !== "IN_REVIEW" || !currentIsPdf);
  elements.governanceObsolete.classList.toggle(
    "hidden", !manageable || document.status !== "APPROVED");

  const currentFile = document.currentVersionId
    ? `${governancePreviewButton(document.currentVersionId, document.currentFileName)}
       <a class="secondary-button compact" href="/api/v1/admin/governance/versions/${encodeURIComponent(document.currentVersionId)}/download">${atlasIcon("download")}Descargar ${escapeHtml(document.currentFileName)}</a>`
    : `<span class="case-empty-value">Todavía no se ha incorporado un archivo.</span>`;
  const relatedHtml = related.length
    ? related.map((item) => `
        <button class="governance-related-chip" type="button"
                data-related-document="${escapeHtml(item.id)}">
          <b>${escapeHtml(item.code)}</b> · ${escapeHtml(governanceDisplayTitle(item.title))}
        </button>`).join("")
    : `<span class="case-empty-value">Sin documentos relacionados.</span>`;
  const hierarchyHtml = ancestors.length
    ? `<div class="governance-hierarchy-chain">
        ${ancestors.map((item, index) => `
          <button type="button" class="governance-hierarchy-node"
                  data-related-document="${escapeHtml(item.id)}">
            <small>Nivel ${index + 1}</small>
            <strong>${escapeHtml(item.code)}</strong>
            <span>${escapeHtml(governanceDisplayTitle(item.title))}</span>
          </button>
          <span class="governance-hierarchy-arrow" aria-hidden="true">${atlasIcon("arrow-right")}</span>`).join("")}
        <span class="governance-hierarchy-node current">
          <small>Nivel ${ancestors.length + 1} · actual</small>
          <strong>${escapeHtml(document.code)}</strong>
          <span>${escapeHtml(governanceDisplayTitle(document.title))}</span>
        </span>
      </div>
      <p class="governance-hierarchy-note">Este documento desarrolla y debe mantenerse alineado con sus documentos superiores. La herencia no copia contenido ni resuelve contradicciones automáticamente.</p>`
    : `<div class="governance-hierarchy-empty">
        <strong>Documento raíz</strong>
        <span>Encabeza esta rama documental. Puede indicar un documento superior si corresponde.</span>
      </div>`;
  const versionsHtml = versions.length
    ? versions.map((version) => `
        <div class="governance-version-row">
          <div><strong>v${escapeHtml(version.versionLabel)}</strong>${version.isCurrent ? `<span class="governance-current-version">Actual</span>` : ""}</div>
          <div><strong>${escapeHtml(version.originalFileName)}</strong><span>${escapeHtml(version.changeSummary)}</span></div>
          <div><code title="${escapeHtml(version.sha256)}">SHA-256 ${escapeHtml(version.sha256)}</code><small>${formatBytes(version.sizeBytes)}</small></div>
          <div class="governance-version-provenance">
            <span><b>Incorporada</b> ${formatDate(version.createdAtUtc)}</span>
            <small>por ${escapeHtml(version.createdBy)}</small>
            ${version.publishedAtUtc ? `
              <span><b>Aprobada</b> ${formatDate(version.publishedAtUtc)}</span>
              <small>por ${escapeHtml(version.publishedBy || "Identidad no disponible")}</small>` : `
              <span><b>Aprobación pendiente</b></span>`}
          </div>
          <div class="governance-version-actions">
            ${governancePreviewButton(version.id, version.originalFileName, true)}
            <a class="secondary-button compact" href="/api/v1/admin/governance/versions/${encodeURIComponent(version.id)}/download">${atlasIcon("download")}Descargar</a>
          </div>
        </div>`).join("")
    : `<span class="case-empty-value">No hay versiones registradas.</span>`;

  elements.governanceDetailBody.innerHTML = `
    <div class="governance-detail-content">
      ${typeof governanceReferenceNotice === "function" ? governanceReferenceNotice(document) : ""}
      <div class="governance-detail-summary">
        <div><span>Estado documental</span><strong>${governanceStatusBadge(document)}</strong><small>${document.reviewDue ? "Requiere programar revisión" : "Flujo controlado"}</small></div>
        <div><span>Versión actual</span><strong>${escapeHtml(document.currentVersionLabel || "Sin versión")}</strong><small>${formatNumber(document.versionCount)} preservadas</small></div>
        <div><span>Fecha de vigencia registrada</span><strong>${escapeHtml(governanceDate(document.effectiveDate))}</strong><small>Aprobó: ${escapeHtml(document.approverName || "Pendiente")}</small></div>
        <div><span>Próxima revisión</span><strong>${escapeHtml(governanceDate(document.nextReviewDate))}</strong><small>${document.parentCode ? `Depende de ${escapeHtml(document.parentCode)}` : "Documento raíz"}</small></div>
      </div>
      <div class="governance-detail-text-grid">
        <section class="governance-text-block"><h4>Resumen</h4><p>${escapeHtml(document.summary)}</p></section>
        <section class="governance-text-block"><h4>Objetivo</h4><p>${escapeHtml(document.objective || "No definido.")}</p></section>
        <section class="governance-text-block"><h4>Alcance</h4><p>${escapeHtml(document.scope || "No definido.")}</p></section>
        <section class="governance-text-block"><h4>Archivo oficial actual</h4><p>${document.currentSha256 ? `Huella: ${escapeHtml(document.currentSha256)}` : "La ficha todavía no tiene archivo."}</p><div class="governance-form-actions">${currentFile}</div></section>
      </div>
      <section class="governance-hierarchy-block"><h4>Jerarquía documental</h4>${hierarchyHtml}</section>
      <section class="governance-related-block"><h4>Otros documentos relacionados</h4><div class="governance-related-list">${relatedHtml}</div></section>
      <section class="governance-version-history"><h4>Historial de versiones</h4>${versionsHtml}</section>
    </div>`;
  elements.governanceDetailBody
    .querySelectorAll("[data-related-document]")
    .forEach((button) => button.addEventListener(
      "click", () => void openGovernanceDetail(button.dataset.relatedDocument)));
  elements.governanceDetailBody
    .querySelectorAll("[data-governance-preview]")
    .forEach((button) => button.addEventListener(
      "click",
      () => void openGovernancePreview(
        button.dataset.governancePreview,
        button.dataset.governancePreviewName)));
  elements.governanceDetailPanel.classList.remove("hidden");
}

function governancePreviewKind(fileName) {
  const name = String(fileName || "").toLowerCase();
  if (name.endsWith(".pdf")) return "pdf";
  if (name.endsWith(".docx")) return "word";
  if (name.endsWith(".xlsx")) return "spreadsheet";
  return null;
}

function governancePreviewButton(versionId, fileName, compactLabel = false) {
  if (!versionId || !governancePreviewKind(fileName)) {
    return "";
  }
  return `<button class="primary-button compact" type="button"
                  data-governance-preview="${escapeHtml(versionId)}"
                  data-governance-preview-name="${escapeHtml(fileName)}">
            ${atlasIcon("file-description")}${compactLabel ? "Ver" : "Ver sin salir de ATLAS"}
          </button>`;
}

async function openGovernancePreview(versionId, fileName) {
  const kind = governancePreviewKind(fileName);
  if (!versionId || !kind) {
    showToast("Este formato sólo está disponible para descarga.");
    return;
  }
  const request = ++state.governanceUi.previewRequest;
  state.governanceUi.preview = null;
  elements.governancePreviewTitle.textContent = fileName || "Vista previa";
  elements.governancePreviewMeta.textContent = kind === "pdf"
    ? "PDF original dentro de ATLAS · sólo lectura"
    : kind === "word"
      ? "Contenido de Word representado de forma segura · sólo lectura"
      : "Valores guardados del libro de Excel · sólo lectura";
  elements.governancePreviewDownload.href =
    `/api/v1/admin/governance/versions/${encodeURIComponent(versionId)}/download`;
  elements.governancePreviewNotice.textContent =
    "Preparando una vista segura del documento…";
  elements.governancePreviewTabs.classList.add("hidden");
  elements.governancePreviewTabs.replaceChildren();
  elements.governancePreviewBody.innerHTML = `
    <div class="governance-preview-loading">
      <span class="spinner" aria-hidden="true"></span>
      <strong>Preparando vista de sólo lectura…</strong>
    </div>`;
  elements.governancePreviewOverlay.classList.remove("hidden");
  elements.governancePreviewOverlay.setAttribute("aria-hidden", "false");
  document.body.classList.add("governance-preview-open");
  elements.governancePreviewClose.focus();

  try {
    const preview = await api(
      `/api/v1/admin/governance/versions/${encodeURIComponent(versionId)}/preview`);
    if (request !== state.governanceUi.previewRequest ||
        elements.governancePreviewOverlay.classList.contains("hidden")) {
      return;
    }
    state.governanceUi.preview = preview;
    elements.governancePreviewNotice.textContent = preview.notice;
    if (preview.kind === "pdf") {
      elements.governancePreviewBody.innerHTML = `
        <iframe class="governance-pdf-frame"
                src="/api/v1/admin/governance/versions/${encodeURIComponent(versionId)}/inline"
                title="${escapeHtml(`Vista previa de ${fileName}`)}"></iframe>`;
    } else {
      renderGovernanceStructuredPreview(preview);
    }
  } catch (error) {
    if (request !== state.governanceUi.previewRequest ||
        error.message === "unauthorized") {
      return;
    }
    const messages = {
      unsupported_governance_preview:
        "Este formato sólo está disponible para descarga.",
      invalid_governance_preview_file:
        "ATLAS no pudo interpretar este archivo de forma segura.",
      unsafe_governance_preview_archive:
        "La vista fue bloqueada porque el archivo supera los límites seguros."
    };
    elements.governancePreviewNotice.textContent =
      messages[error.code] || "No fue posible generar la vista previa.";
    elements.governancePreviewBody.innerHTML = `
      <div class="governance-preview-empty">
        ${atlasIcon("alert-circle")}
        <strong>Vista previa no disponible</strong>
        <span>El original no fue modificado y continúa disponible para descarga.</span>
      </div>`;
  }
}

function renderGovernanceStructuredPreview(preview) {
  if (preview.kind === "word") {
    elements.governancePreviewTabs.classList.add("hidden");
    elements.governancePreviewBody.innerHTML = `
      <article class="governance-word-page">
        ${(preview.blocks || []).map((block) => {
          if (block.type === "table") {
            return governancePreviewTable(block.rows || []);
          }
          if (block.type === "heading") {
            const level = Math.min(6, Math.max(2, Number(block.level || 2) + 1));
            return `<h${level}>${escapeHtml(block.text)}</h${level}>`;
          }
          return `<p>${escapeHtml(block.text)}</p>`;
        }).join("") || `<p class="case-empty-value">El documento no contiene texto visible.</p>`}
      </article>`;
    return;
  }
  if (preview.kind === "spreadsheet") {
    renderGovernanceSpreadsheet(preview, 0);
  }
}

function renderGovernanceSpreadsheet(preview, selectedIndex) {
  const sheets = preview.sheets || [];
  if (!sheets.length) {
    elements.governancePreviewBody.innerHTML = `
      <div class="governance-preview-empty"><strong>Libro sin hojas visibles</strong></div>`;
    return;
  }
  const index = Math.min(Math.max(0, selectedIndex), sheets.length - 1);
  const sheet = sheets[index];
  elements.governancePreviewTabs.innerHTML = sheets.map((item, itemIndex) => `
    <button type="button" role="tab"
            class="governance-preview-tab${itemIndex === index ? " active" : ""}"
            aria-selected="${itemIndex === index}"
            data-governance-sheet="${itemIndex}">${escapeHtml(item.name)}</button>`)
    .join("");
  elements.governancePreviewTabs.classList.remove("hidden");
  elements.governancePreviewTabs
    .querySelectorAll("[data-governance-sheet]")
    .forEach((button) => button.addEventListener("click", () =>
      renderGovernanceSpreadsheet(preview, Number(button.dataset.governanceSheet))));

  const rows = sheet.rows || [];
  const columns = Math.max(1, ...rows.map((row) => (row.cells || []).length));
  elements.governancePreviewBody.innerHTML = `
    <div class="governance-sheet-wrap">
      <table class="governance-sheet-table">
        <thead><tr><th class="governance-sheet-corner"></th>${Array.from(
          { length: columns },
          (_, column) => `<th>${spreadsheetColumnName(column)}</th>`).join("")}</tr></thead>
        <tbody>${rows.length ? rows.map((row) => `
          <tr><th>${formatNumber(row.number)}</th>${Array.from(
            { length: columns },
            (_, column) => `<td>${escapeHtml(row.cells?.[column] || "")}</td>`).join("")}</tr>`).join("")
          : `<tr><td colspan="${columns + 1}">Esta hoja no contiene valores visibles.</td></tr>`}
        </tbody>
      </table>
    </div>`;
}

function governancePreviewTable(rows) {
  const columns = Math.max(1, ...rows.map((row) => row.length));
  return `<div class="governance-word-table-wrap"><table class="governance-word-table">
    <tbody>${rows.map((row) => `<tr>${Array.from(
      { length: columns },
      (_, column) => `<td>${escapeHtml(row[column] || "")}</td>`).join("")}</tr>`).join("")}</tbody>
  </table></div>`;
}

function spreadsheetColumnName(index) {
  let number = Number(index) + 1;
  let result = "";
  while (number > 0) {
    number--;
    result = String.fromCharCode(65 + number % 26) + result;
    number = Math.floor(number / 26);
  }
  return result;
}

function closeGovernancePreview() {
  state.governanceUi.previewRequest++;
  state.governanceUi.preview = null;
  elements.governancePreviewOverlay.classList.add("hidden");
  elements.governancePreviewOverlay.setAttribute("aria-hidden", "true");
  elements.governancePreviewTabs.replaceChildren();
  elements.governancePreviewBody.replaceChildren();
  document.body.classList.remove("governance-preview-open");
}

function beginGovernanceVersionUpload() {
  const detail = state.governanceUi.detail;
  if (!detail) {
    return;
  }
  elements.governanceVersionLabel.value = detail.versions.length
    ? ""
    : "1.0";
  elements.governanceChangeSummary.value = detail.versions.length
    ? ""
    : "Primera emisión del documento.";
  elements.governanceFile.value = "";
  elements.governanceFileLabel.textContent =
    "Seleccionar PDF, Word, Excel o PowerPoint";
  elements.governanceVersionTitle.textContent =
    `Nueva versión de ${detail.document.code}`;
  elements.governanceVersionPanel.classList.remove("hidden");
  elements.governanceVersionPanel.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function uploadGovernanceVersion() {
  const id = state.governanceUi.selectedId;
  const file = elements.governanceFile.files?.[0];
  const version = elements.governanceVersionLabel.value.trim();
  const changes = elements.governanceChangeSummary.value.trim();
  if (!id || !file || !version || !changes) {
    showToast("Seleccione el archivo e indique versión y resumen de cambios.");
    return;
  }
  if (file.size > 25_000_000) {
    showToast("El documento supera el límite de 25 MB.");
    return;
  }
  elements.governanceVersionUpload.disabled = true;
  try {
    const detail = await api(
      `/api/v1/admin/governance/documents/${encodeURIComponent(id)}/versions`,
      {
        method: "POST",
        headers: {
          "Content-Type": file.type || "application/octet-stream",
          "X-Atlas-Request": "dashboard",
          "X-Atlas-File-Name": encodeURIComponent(file.name),
          "X-Atlas-Document-Version": encodeURIComponent(version),
          "X-Atlas-Change-Summary": encodeURIComponent(changes)
        },
        body: file
      });
    closeGovernanceVersion();
    state.governanceUi.detail = detail;
    await loadGovernance(id, true);
    showToast("Versión registrada con huella SHA-256.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        governance_document_version_exists: "Esa versión ya existe para el documento.",
        governance_document_too_large: "El documento supera el límite permitido.",
        invalid_governance_document_content: "El contenido del archivo no coincide con su extensión.",
        invalid_governance_document_version: "Revise el tipo de archivo, la versión y el resumen."
      };
      showToast(messages[error.code] || "No fue posible registrar la versión.");
    }
  } finally {
    elements.governanceVersionUpload.disabled = false;
  }
}

async function changeGovernanceStatus(status, approval = {}) {
  const id = state.governanceUi.selectedId;
  if (!id) {
    return;
  }
  try {
    const detail = await api(
      `/api/v1/admin/governance/documents/${encodeURIComponent(id)}/status`,
      governanceMutationOptions("POST", {
        status,
        approverName: approval.approverName || null,
        effectiveDate: approval.effectiveDate || null,
        nextReviewDate: approval.nextReviewDate || null
      }));
    state.governanceUi.detail = detail;
    await loadGovernance(id, true);
    showToast({
      IN_REVIEW: "Documento enviado a revisión.",
      DRAFT: "Documento devuelto a borrador.",
      APPROVED: "Documento aprobado y publicado.",
      OBSOLETE: "Documento marcado como obsoleto."
    }[status] || "Estado documental actualizado.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        governance_document_version_required: "Debe registrar un archivo antes de aprobar.",
        invalid_governance_status_transition: "Ese cambio de estado no está permitido."
      };
      showToast(messages[error.code] || "No fue posible cambiar el estado.");
    }
  }
}

function openGovernanceApproval() {
  const detail = state.governanceUi.detail;
  if (!detail) {
    return;
  }
  const today = new Date();
  const review = new Date(today);
  review.setFullYear(review.getFullYear() + 1);
  elements.governanceApproverName.textContent =
    state.session?.displayName || "Sesión sin identificar";
  elements.governanceEffectiveDate.value = today.toISOString().slice(0, 10);
  elements.governanceNextReviewDate.value = review.toISOString().slice(0, 10);
  elements.governanceApprovalBox.classList.remove("hidden");
  elements.governanceEffectiveDate.focus();
}

async function confirmGovernanceApproval() {
  const id = state.governanceUi.selectedId;
  const effectiveDate = elements.governanceEffectiveDate.value;
  const nextReviewDate = elements.governanceNextReviewDate.value;
  if (!id || !effectiveDate || !nextReviewDate || nextReviewDate < effectiveDate) {
    showToast("Complete un período de vigencia válido.");
    return;
  }
  if (!window.confirm(governanceApprovalAttestation)) {
    return;
  }
  elements.governanceConfirmApproval.disabled = true;
  try {
    const detail = await api(
      `/api/v1/admin/governance/documents/${encodeURIComponent(id)}/approve`,
      governanceMutationOptions("POST", {
        effectiveDate,
        nextReviewDate,
        attestation: governanceApprovalAttestation
      }));
    state.governanceUi.detail = detail;
    await loadGovernance(id, true);
    showToast("Documento aprobado por Gerencia y publicado como oficial.");
    elements.governanceApprovalBox.classList.add("hidden");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        governance_official_pdf_required: "La versión oficial debe ser un archivo PDF válido.",
        invalid_governance_status_transition: "El documento ya no está pendiente de revisión.",
        invalid_governance_approval_attestation: "No se confirmó la declaración formal requerida."
      };
      showToast(messages[error.code] || "No fue posible aprobar el documento.");
    }
  } finally {
    elements.governanceConfirmApproval.disabled = false;
  }
}

async function requestGovernanceCorrection() {
  const id = state.governanceUi.selectedId;
  if (!id) {
    return;
  }
  const response = window.prompt(
    "Indique con claridad qué debe corregirse antes de volver a presentar el PDF."
  );
  if (response === null) {
    return;
  }
  const reason = response.trim();
  if (reason.length < 5) {
    showToast("El motivo de corrección debe tener al menos cinco caracteres.");
    return;
  }
  if (!window.confirm(
    "¿Solicitar la corrección? El PDF rechazado se eliminará del servidor; se conservarán únicamente su huella y el motivo en auditoría."
  )) {
    return;
  }

  elements.governanceRequestCorrection.disabled = true;
  try {
    const detail = await api(
      `/api/v1/admin/governance/documents/${encodeURIComponent(id)}/request-correction`,
      governanceMutationOptions("POST", { reason }));
    state.governanceUi.detail = detail;
    await loadGovernance(id, true);
    showToast("Corrección solicitada. El PDF rechazado fue eliminado del servidor.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      const messages = {
        invalid_governance_correction_reason: "Indique un motivo de corrección válido.",
        invalid_governance_status_transition: "El documento ya no está pendiente de revisión.",
        governance_rejected_file_purge_failed: "La solicitud quedó registrada, pero el servidor reportó un problema al eliminar el archivo."
      };
      showToast(messages[error.code] || "No fue posible solicitar la corrección.");
    }
  } finally {
    elements.governanceRequestCorrection.disabled = false;
  }
}

function switchView(view) {
  if (!Object.hasOwn(viewTitles, view)) {
    return;
  }
  if (state.session?.role === "Installer" && !installerViews.has(view)) {
    view = "assets";
  }
  if (view === "audit" && !canReadAudit()) {
    view = "overview";
  }
  state.view = view;
  document.querySelectorAll(".view").forEach((section) => {
    section.classList.toggle("active", section.id === `view-${view}`);
  });
  document.querySelectorAll(".nav-item[data-view]").forEach((button) => {
    button.classList.toggle("active", button.dataset.view === view);
  });
  elements.breadcrumb.textContent = viewTitles[view][0];
  elements.pageTitle.textContent = viewTitles[view][1];
  elements.sidebar.classList.remove("open");
  if (view === "audit") {
    void loadAudit();
  }
}

function applySearch(value) {
  state.filters.search = value.trim();
  state.devices.page = 1;
  elements.globalSearch.value = value;
  elements.assetSearch.value = value;
  switchView("assets");
  clearTimeout(state.searchTimer);
  state.searchTimer = setTimeout(() => void loadDevices(), 300);
}

function showToast(message) {
  elements.toast.textContent = message;
  elements.toast.classList.remove("hidden");
  window.setTimeout(() => elements.toast.classList.add("hidden"), 4200);
}

function notificationCategoryLabel(category) {
  return {
    ASSET_CUSTODY: "Asignación y custodia",
    SOFTWARE: "Software",
    BACKUP: "Copias",
    BACKUP_CONTROL: "Control de respaldos",
    POSTURE: "Postura",
    SECURITY: "Bitácoras"
  }[category] || "ATLAS";
}

// Presentation of the original severity; this does not change classification.
function notificationVisualPresentation(item) {
  const severity = String(item.severity || "INFO").toUpperCase();
  if (severity === "CRITICAL") return { label: "Crítico", icon: "alert-triangle" };
  if (severity === "WARNING") return { label: "Por revisar", icon: "alert-triangle" };
  return { label: severity === "INFO" ? "Informativo" : "Aviso", icon: "info-circle" };
}

function findingFromNotification(item) {
  const profile = item.category === "BACKUP_CONTROL"
    ? backupControlProfileById(item.targetEntityId)
    : null;
  const deviceId = profile?.deviceId || item.targetEntityId;
  const device = (state.backupControl?.devices || []).find(candidate => candidate.id === deviceId)
    || (state.devices.items || []).find(candidate => candidate.id === deviceId);
  const sourceKind = String(item.category || "NOTIFICATION").toUpperCase();
  const type = sourceKind === "BACKUP" || sourceKind === "BACKUP_CONTROL"
    ? "RESTORATION"
    : sourceKind === "POSTURE"
      ? "VULNERABILITY"
      : "INCIDENT";
  return {
    sourceKind,
    sourceKey: item.targetEntityId || item.id,
    sourceEntityId: item.targetEntityId || item.id,
    type,
    title: item.title,
    description: `${item.message}\n\nEl aviso fue detectado por ATLAS el ${formatDate(item.detectedAtUtc)}. Revise la evidencia y documente la decisión adoptada.`,
    source: `Aviso de ${notificationCategoryLabel(item.category)}`,
    severity: item.severity,
    agencyCode: profile?.agencyCode || device?.agencyCode || null,
    deviceId: profile?.deviceId || device?.id || (["BACKUP", "POSTURE", "SECURITY", "DEVICE", "ASSET_CUSTODY"].includes(sourceKind) ? item.targetEntityId : null),
    machineName: profile?.systemName || device?.machineName || null,
    snapshot: {
      notificationId: item.id,
      category: notificationCategoryLabel(item.category),
      machineName: device?.machineName || null,
      agencyCode: device?.agencyCode || null,
      detail: item.message,
      capturedAtUtc: item.detectedAtUtc
    }
  };
}

function renderNotifications() {
  if (typeof renderOperationalAttention === "function") renderOperationalAttention();
  const installerOnly = state.session?.role === "Installer";
  elements.notificationCenter.classList.toggle("hidden", installerOnly);
  if (installerOnly) {
    return;
  }

  const page = state.notifications || {
    page: 1,
    pageSize: 20,
    total: 0,
    unreadCount: 0,
    items: []
  };
  const unread = Number(page.unreadCount || 0);
  elements.notificationCount.textContent = unread > 99 ? "99+" : String(unread);
  elements.notificationCount.classList.toggle("hidden", unread === 0);
  elements.notificationButton.classList.toggle("has-alerts", unread > 0);
  elements.notificationPanelMeta.textContent = unread
    ? `${formatNumber(unread)} avisos sin leer · leer no resuelve su causa`
    : "Sin avisos sin leer · consulta la atención actual";
  elements.markAllNotifications.disabled = unread === 0;
  if (page.mutedCategories?.length) elements.notificationPanelMeta.textContent += ` · Silencio temporal: ${page.mutedCategories.map(notificationCategoryLabel).join(", ")}`;

  const items = Array.isArray(page.items) ? page.items : [];
  elements.notificationRows.innerHTML = items.length
    ? items.map((item) => `
      <article class="notification-row ${escapeHtml(String(item.severity || "INFO").toLowerCase())} ${item.isRead ? "read" : "unread"}">
        <button class="notification-open" type="button" data-notification-id="${escapeHtml(item.id)}">
          <span class="notification-severity" aria-hidden="true">${atlasIcon(notificationVisualPresentation(item).icon)}</span>
          <span class="notification-copy">
            <span class="notification-row-meta">
              <b>${escapeHtml(notificationCategoryLabel(item.category))}</b>
              <time title="${escapeHtml(formatDate(item.detectedAtUtc))}">${escapeHtml(formatRelative(item.detectedAtUtc))}</time>
            </span>
            <span class="notification-kind">${escapeHtml(notificationVisualPresentation(item).label)}</span>
            <strong>${escapeHtml(item.title)}</strong>
            <span>${escapeHtml(item.message)}</span>
          </span>
          ${item.isRead ? "" : "<i aria-label=\"No leído\"></i>"}
        </button>
        ${caseFindingButton(findingFromNotification(item))}
      </article>`).join("")
    : `<div class="notification-empty">
         <span>${atlasIcon("circle-check")}</span>
         <strong>Sin avisos en esta consulta</strong>
         <p>${page.mutedCategories?.length ? "Hay categorías silenciadas temporalmente por mantenimiento. Sus causas no se consideran resueltas." : "No hay avisos visibles para esta consulta."}</p>
       </div>`;
  elements.notificationRows
    .querySelectorAll("[data-notification-id]")
    .forEach((button) => button.addEventListener(
      "click",
      () => void openNotification(button.dataset.notificationId)));
  attachCaseFindingActions(elements.notificationRows);

  const totalPages = Math.max(
    1,
    Math.ceil(Number(page.total || 0) / Number(page.pageSize || 20)));
  elements.notificationPaginationLabel.textContent = page.total
    ? `Página ${page.page} de ${totalPages} · ${formatNumber(page.total)} avisos`
    : "0 avisos";
  elements.notificationPreviousPage.disabled = Number(page.page || 1) <= 1;
  elements.notificationNextPage.disabled = Number(page.page || 1) >= totalPages;
}

function notificationToastStorageKey() {
  return `atlas-notification-toasts:${state.session?.username || "session"}`;
}

function readSeenNotificationToasts() {
  try {
    const value = JSON.parse(
      window.sessionStorage.getItem(notificationToastStorageKey()) || "[]");
    return new Set(Array.isArray(value) ? value : []);
  } catch {
    return new Set();
  }
}

function showAttentionToasts() {
  if (state.session?.role === "Installer") {
    return;
  }
  const unseen = (state.notifications?.items || [])
    .filter((item) => !item.isRead);
  const seen = readSeenNotificationToasts();
  const fresh = unseen.filter((item) => !seen.has(item.id));
  if (!fresh.length) {
    return;
  }

  fresh.forEach((item) => seen.add(item.id));
  try {
    window.sessionStorage.setItem(
      notificationToastStorageKey(),
      JSON.stringify(Array.from(seen).slice(-250)));
  } catch {
    // La campana conserva el aviso aunque el navegador bloquee sessionStorage.
  }

  const visibleLimit = 2;
  fresh.slice(0, visibleLimit).forEach((item, index) => {
    const card = document.createElement("article");
    card.className = `attention-toast ${String(item.severity || "INFO").toLowerCase()}`;
    card.innerHTML = `
      <button class="attention-toast-close" type="button" aria-label="Cerrar aviso">${atlasIcon("x")}</button>
      <div class="attention-toast-heading">
        <span class="attention-toast-icon" aria-hidden="true">${atlasIcon(notificationVisualPresentation(item).icon)}</span>
        <span class="attention-toast-heading-copy"><span class="attention-toast-category">${escapeHtml(notificationCategoryLabel(item.category))}</span><span class="attention-toast-status">${escapeHtml(notificationVisualPresentation(item).label)}</span></span>
      </div>
      <strong>${escapeHtml(item.title)}</strong>
      <p>${escapeHtml(item.message)}</p>
      <div class="attention-toast-footer"><time title="${escapeHtml(formatDate(item.detectedAtUtc))}">${escapeHtml(formatRelative(item.detectedAtUtc))}</time><button class="attention-toast-open" type="button">Revisar en ATLAS ${atlasIcon("chevron-right")}</button></div>`;
    const remove = () => card.remove();
    card.querySelector(".attention-toast-close")
      .addEventListener("click", remove);
    card.querySelector(".attention-toast-open")
      .addEventListener("click", () => {
        remove();
        void openNotification(item.id);
      });
    window.setTimeout(() => remove(), 8500 + (index * 900));
    elements.attentionToastStack.appendChild(card);
  });

  if (fresh.length > visibleLimit) {
    const summary = document.createElement("article");
    summary.className = "attention-toast summary";
    summary.innerHTML = `
      <button class="attention-toast-close" type="button" aria-label="Cerrar resumen de avisos">${atlasIcon("x")}</button>
      <div class="attention-toast-heading"><span class="attention-toast-icon" aria-hidden="true">${atlasIcon("bell")}</span><span class="attention-toast-heading-copy"><span class="attention-toast-category">Resumen de avisos</span><span class="attention-toast-status">En la campana</span></span></div>
      <strong>${formatNumber(fresh.length - visibleLimit)} avisos adicionales</strong>
      <p>Quedaron guardados en la campana para revisarlos cuando quieras.</p>
      <div class="attention-toast-footer"><button class="attention-toast-open" data-notification-panel-open type="button">Abrir campana ${atlasIcon("chevron-right")}</button></div>`;
    const remove = () => summary.remove();
    summary.querySelector(".attention-toast-close")
      .addEventListener("click", remove);
    summary.querySelector(".attention-toast-open")
      .addEventListener("click", () => {
        remove();
        openNotificationPanel();
      });
    window.setTimeout(() => remove(), 12500);
    elements.attentionToastStack.appendChild(summary);
  }
}

function openNotificationPanel() {
  elements.notificationPanel.classList.remove("hidden");
  elements.notificationButton.setAttribute("aria-expanded", "true");
  document.getElementById("notificationCloseButton")?.focus();
}

function closeNotificationPanel(restoreFocus = false) {
  const wasOpen = !elements.notificationPanel.classList.contains("hidden");
  elements.notificationPanel.classList.add("hidden");
  elements.notificationButton.setAttribute("aria-expanded", "false");
  if (restoreFocus && wasOpen) elements.notificationButton.focus();
}

async function loadNotifications(showNewToasts = false) {
  const page = state.notifications?.page || 1;
  const pageSize = state.notifications?.pageSize || 20;
  try {
    state.notifications = await api(
      `/api/v1/admin/notifications?page=${page}&pageSize=${pageSize}`);
    renderNotifications();
    if (showNewToasts) {
      showAttentionToasts();
    }
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible actualizar la bandeja de atención.");
    }
  }
}

async function openNotification(notificationId) {
  const item = (state.notifications?.items || [])
    .find((candidate) => candidate.id === notificationId);
  try {
    await api(`/api/v1/admin/notifications/${encodeURIComponent(notificationId)}/read`, {
      method: "POST",
      headers: { "X-Atlas-Request": "dashboard" }
    });
    await loadNotifications(false);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible marcar el aviso como leído.");
    }
  }
  closeNotificationPanel();
  const targetView = item?.targetView === "devices" ? "assets" : item?.targetView;
  if (targetView && Object.hasOwn(viewTitles, targetView)) {
    if (targetView === "security" && item.targetEntityId) {
      state.securityPages.eventDeviceId = item.targetEntityId;
      state.securityPages.eventPage = 1;
      void loadSecurity();
    }
    switchView(targetView);
    if (targetView === "assets" && item.category === "ASSET_CUSTODY" && item.targetEntityId) {
      await openDevice(item.targetEntityId);
      if (typeof selectAssetDetailTab === "function") selectAssetDetailTab("custody");
    }
    if (item.category === "BACKUP_PLAN" && item.targetEntityId && typeof openBackupDemonstrations === "function") {
      await openBackupDemonstrations(item.targetEntityId);
    }
  }
}

async function markAllNotificationsRead() {
  try {
    await api("/api/v1/admin/notifications/read-all", {
      method: "POST",
      headers: { "X-Atlas-Request": "dashboard" }
    });
    await loadNotifications(false);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible marcar los avisos como leídos.");
    }
  }
}

async function exportInventory() {
  try {
    const response = await fetch("/api/v1/admin/devices/export.csv", {
      credentials: "same-origin",
      headers: { "Accept": "text/csv" }
    });
    if (response.status === 401) {
      window.location.replace("/login");
      return;
    }
    if (response.status === 403) {
      showToast("Tu rol no tiene permiso para exportar inventarios.");
      return;
    }
    if (!response.ok) {
      throw new Error("export_failed");
    }
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    const disposition = response.headers.get("Content-Disposition") || "";
    const fileNameMatch = disposition.match(/filename="?([^";]+)"?/i);
    link.href = url;
    link.download = fileNameMatch?.[1] || "miguel-inventario.csv";
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
    showToast("Inventario exportado y actividad registrada.");
  } catch {
    showToast("No fue posible exportar el inventario.");
  }
}

async function generateInstaller(event) {
  event.preventDefault();
  const agencyCode = elements.installerAgency.value;
  const lifetimeHours = Number(elements.installerLifetime.value);
  if (!agencyCode) {
    showToast("Selecciona la agencia del equipo.");
    return;
  }

  elements.generateInstaller.disabled = true;
  elements.generateInstaller.textContent = "Generando…";
  try {
    const response = await fetch("/api/v1/admin/installers", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Accept": "application/vnd.microsoft.portable-executable",
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify({ agencyCode, lifetimeHours })
    });
    if (response.status === 401) {
      window.location.replace("/login");
      return;
    }
    if (!response.ok) {
      throw new Error(`installer_${response.status}`);
    }

    const blob = await response.blob();
    const disposition = response.headers.get("Content-Disposition") || "";
    const fileNameMatch = disposition.match(/filename="?([^";]+)"?/i);
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileNameMatch?.[1] ||
      `ATLAS-Setup_${agencyCode}.exe`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
    showToast(
      "Instalador EXE generado. Trátalo como una credencial temporal."
    );
  } catch {
    showToast(
      "No fue posible generar el instalador. Verifica el catálogo publicado."
    );
  } finally {
    elements.generateInstaller.disabled = false;
    elements.generateInstaller.textContent = "Generar instalador EXE";
  }
}

function uploadReleaseFile(file) {
  return new Promise((resolve, reject) => {
    const request = new XMLHttpRequest();
    request.open("POST", "/api/v1/admin/releases");
    request.responseType = "json";
    request.setRequestHeader("Accept", "application/json");
    request.setRequestHeader(
      "Content-Type",
      "application/vnd.atlas.release"
    );
    request.setRequestHeader("X-Atlas-Request", "dashboard");
    request.setRequestHeader("X-Atlas-File-Name", file.name);
    request.upload.addEventListener("progress", (event) => {
      if (!event.lengthComputable) {
        elements.releaseProgressLabel.textContent =
          "Enviando y verificando versión…";
        return;
      }
      const percent = Math.min(
        99,
        Math.round(event.loaded * 100 / event.total)
      );
      elements.releaseProgressBar.value = percent;
      elements.releaseProgressValue.textContent = `${percent}%`;
      elements.releaseProgressLabel.textContent =
        percent < 99 ? "Cargando versión…" : "Verificando firma e integridad…";
    });
    request.addEventListener("load", () => {
      if (request.status === 401) {
        window.location.replace("/login");
        reject(new Error("unauthorized"));
        return;
      }
      if (request.status < 200 || request.status >= 300) {
        reject(new Error(
          request.response?.message ||
          `No fue posible cargar la versión (${request.status}).`
        ));
        return;
      }
      resolve(request.response);
    });
    request.addEventListener("error", () =>
      reject(new Error("Se perdió la conexión durante la carga.")));
    request.send(file);
  });
}

async function uploadRelease(event) {
  event.preventDefault();
  const file = elements.releaseFile.files?.[0];
  if (!file || !file.name.toLowerCase().endsWith(".atlasrelease")) {
    showToast("Selecciona un archivo .atlasrelease.");
    return;
  }

  elements.uploadRelease.disabled = true;
  elements.releaseFile.disabled = true;
  elements.releaseProgress.classList.remove("hidden");
  elements.releaseProgressBar.value = 0;
  elements.releaseProgressValue.textContent = "0%";
  try {
    const release = await uploadReleaseFile(file);
    elements.releaseProgressBar.value = 100;
    elements.releaseProgressValue.textContent = "100%";
    elements.releaseProgressLabel.textContent =
      `Firma verificada: ${release.releaseVersion}`;
    state.releaseStore = await api("/api/v1/admin/releases");
    state.releaseView.tab = "testing";
    state.releaseView.page = 1;
    renderReleaseStore();
    showToast(
      "Versión firmada y verificada. Revísala antes de activarla."
    );
    elements.releaseUploadForm.reset();
    elements.releaseFileLabel.textContent =
      "Seleccionar archivo .atlasrelease";
    globalThis.AtlasUpdateSections?.openReleaseWorkspace("library");
  } catch (error) {
    if (error.message !== "unauthorized") {
      elements.releaseProgressLabel.textContent =
        error.message || "La verificación falló";
      showToast(error.message || "El paquete firmado no es válido.");
    }
  } finally {
    elements.uploadRelease.disabled = false;
    elements.releaseFile.disabled = false;
  }
}

async function refreshReleaseManagement() {
  [
    state.distribution,
    state.releaseStore,
    state.serverReleaseStore,
    state.releaseDeployments
  ] = await Promise.all([
    api("/api/v1/admin/distribution"),
    api("/api/v1/admin/releases"),
    api("/api/v1/admin/server-releases"),
    api("/api/v1/admin/release-deployments")
  ]);
  renderDistribution();
  renderReleaseStore();
  renderServerReleaseStore();
  renderReleaseDeployments();
}

async function deployRelease(releaseId, releaseVersion) {
  const selectedRelease = (state.releaseStore?.releases || [])
    .find((release) => release.releaseId === releaseId);
  const activeRelease = (state.releaseStore?.releases || [])
    .find((release) => release.isActive);
  if (selectedRelease && activeRelease &&
      Date.parse(selectedRelease.createdAtUtc) <
        Date.parse(activeRelease.createdAtUtc)) {
    const historicalConfirmation = window.prompt(
      `ATLAS ${releaseVersion} es anterior a ${activeRelease.releaseVersion}. ` +
      "Asignarla no obliga al Updater a bajar componentes ya instalados. " +
      "Escriba exactamente REASIGNAR para continuar con la oferta histórica."
    );
    if (historicalConfirmation !== "REASIGNAR") {
      showToast("La reasignación histórica fue cancelada.");
      return;
    }
  }
  const selection = elements.deploymentScope.value;
  let scopeType;
  let scopeValue;
  let targetLabel;
  if (selection === "GENERAL") {
    const confirmation = window.prompt(
      `Esta acción ofrece ATLAS ${releaseVersion} a todos los equipos. ` +
      "Escriba exactamente GENERAL para continuar."
    );
    if (confirmation !== "GENERAL") {
      showToast("La activación general fue cancelada.");
      return;
    }
    targetLabel = "todos los equipos";
  } else if (selection === "AGENCY") {
    scopeType = "AGENCY";
    scopeValue = elements.deploymentAgency.value;
    if (!scopeValue) {
      showToast("Seleccione la agencia que recibirá el release.");
      elements.deploymentAgency.focus();
      return;
    }
    targetLabel = `la agencia ${scopeValue}`;
  } else {
    scopeType = "RING";
    scopeValue = "PILOT";
    targetLabel = "el anillo PILOTO";
  }

  if (!releaseId || !window.confirm(
    `¿Desplegar ATLAS ${releaseVersion} en ${targetLabel}?`
  )) {
    return;
  }

  const button = document.querySelector(
    `.deploy-release[data-release-id="${CSS.escape(releaseId)}"]`
  );
  if (button) {
    button.disabled = true;
    button.textContent = "Verificando…";
  }
  try {
    if (selection === "GENERAL") {
      await api(`/api/v1/admin/releases/${encodeURIComponent(releaseId)}/activate`, {
        method: "POST",
        headers: { "X-Atlas-Request": "dashboard" }
      });
    } else {
      await api(
        `/api/v1/admin/releases/${encodeURIComponent(releaseId)}/deployments`,
        {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-Atlas-Request": "dashboard"
          },
          body: JSON.stringify({ scopeType, scopeValue })
        }
      );
    }
    await refreshReleaseManagement();
    showToast(`ATLAS ${releaseVersion} quedó asignado a ${targetLabel}.`);
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast(
        error.message ||
        "No se desplegó la versión: revise integridad y destino."
      );
      if (button) {
        button.disabled = false;
        button.textContent = "Desplegar";
      }
    }
  }
}

async function setDeploymentPaused(scopeType, scopeValue, paused) {
  try {
    await api("/api/v1/admin/release-deployments/pause", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "X-Atlas-Request": "dashboard"
      },
      body: JSON.stringify({ scopeType, scopeValue, paused })
    });
    await refreshReleaseManagement();
    showToast(paused
      ? "Despliegue pausado; se detienen nuevas actualizaciones de este grupo."
      : "Despliegue reanudado.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible cambiar el estado del despliegue.");
    }
  }
}

async function removeDeployment(scopeType, scopeValue) {
  if (!window.confirm(
    "¿Retirar este despliegue dirigido? Los equipos usarán la versión general."
  )) {
    return;
  }
  const query = new URLSearchParams({ scopeType, scopeValue });
  try {
    await api(`/api/v1/admin/release-deployments?${query}`, {
      method: "DELETE",
      headers: { "X-Atlas-Request": "dashboard" }
    });
    await refreshReleaseManagement();
    showToast("Despliegue dirigido retirado.");
  } catch (error) {
    if (error.message !== "unauthorized") {
      showToast("No fue posible retirar el despliegue.");
    }
  }
}

document.querySelectorAll(".nav-item[data-view]").forEach((button) => {
  button.addEventListener("click", () => switchView(button.dataset.view));
});
document.querySelectorAll("[data-go]").forEach((button) => {
  button.addEventListener("click", () => switchView(button.dataset.go));
  if (button.tagName !== "BUTTON") {
    button.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        switchView(button.dataset.go);
      }
    });
  }
});
document.querySelectorAll("[data-export]").forEach((button) => {
  button.addEventListener("click", () => void exportInventory());
});
elements.globalSearch.addEventListener("input", (event) =>
  applySearch(event.target.value));
elements.assetSearch.addEventListener("input", (event) =>
  applySearch(event.target.value));
elements.agencyFilter.addEventListener("change", (event) => {
  state.filters.agency = event.target.value;
  state.devices.page = 1;
  void loadDevices();
});
elements.statusFilter.addEventListener("change", (event) => {
  state.filters.connectivity = event.target.value;
  state.devices.page = 1;
  void loadDevices();
});
elements.clearFilters.addEventListener("click", () => {
  state.filters = { search: "", agency: "", connectivity: "" };
  state.devices.page = 1;
  elements.globalSearch.value = "";
  elements.assetSearch.value = "";
  elements.agencyFilter.value = "";
  elements.statusFilter.value = "";
  void loadDevices();
});
elements.previousPage.addEventListener("click", () => {
  if (state.devices.page > 1) {
    state.devices.page--;
    void loadDevices();
  }
});
elements.nextPage.addEventListener("click", () => {
  const totalPages = Math.ceil(state.devices.total / state.devices.pageSize);
  if (state.devices.page < totalPages) {
    state.devices.page++;
    void loadDevices();
  }
});
document.querySelectorAll(".release-tab").forEach((button) => {
  button.addEventListener("click", () => {
    state.releaseView.tab = button.dataset.releaseTab;
    state.releaseView.page = 1;
    renderReleaseStore();
  });
});
elements.releasePreviousPage.addEventListener("click", () => {
  if (state.releaseView.page > 1) {
    state.releaseView.page--;
    renderReleaseStore();
  }
});
elements.releaseNextPage.addEventListener("click", () => {
  state.releaseView.page++;
  renderReleaseStore();
});
elements.newBackupControlButton.addEventListener("click", () =>
  beginBackupControlEdit());
elements.backupControlForm.addEventListener("submit", saveBackupControl);
elements.backupControlEnabled.addEventListener("change", updateBackupControlConditionalFields);
elements.backupControlCancel.addEventListener("click", hideBackupControlEditors);
elements.backupControlAgency.addEventListener("change", renderBackupControlOptions);
elements.backupControlDevice.addEventListener("change", applySelectedBackupDevice);
elements.backupControlStandard.addEventListener("change", () => {
  if (state.backupControlUi.applyingTemplate) {
    return;
  }
  applyBackupStandard(
    backupControlStandardById(elements.backupControlStandard.value), true);
});
elements.backupControlConfigurationMode.addEventListener("change", () => {
  if (state.backupControlUi.applyingTemplate) {
    return;
  }
  const mode = elements.backupControlConfigurationMode.value;
  if (mode === "MANUAL") {
    const base = backupControlBaseStandard();
    if (base) {
      elements.backupControlStandard.value = base.id;
      applyBackupStandard(base, true);
    }
  } else if (mode === "STANDARD") {
    const standard = recommendedBackupStandard(selectedBackupDevice());
    if (standard) {
      applyBackupStandard(standard, true);
    }
  }
  updateBackupControlConditionalFields();
});
elements.backupControlProvider.addEventListener("change", () => {
  const atlas = elements.backupControlProvider.value === "ATLAS_BACKUPNAS";
  elements.backupControlDevice.required = atlas;
  elements.backupControlTechnology.value = atlas
    ? "Atlas Backup"
    : elements.backupControlProvider.value === "ACRONIS"
      ? "Acronis Cloud"
      : elements.backupControlTechnology.value;
  updateBackupAutofillSummary();
});
elements.backupControlNas.addEventListener("change", () => {
  if (state.backupControlUi.applyingTemplate) {
    return;
  }
  const nas = (state.backupControl?.nas || []).find((item) =>
    item.id === elements.backupControlNas.value);
  if (nas) {
    elements.backupControlDestination.value = `\\\\${nas.host}\\${nas.shareName}`;
    setBackupFieldOrigin("destination", "DETECTED");
  }
});
elements.backupControlExceptionType.addEventListener(
  "change", updateBackupControlConditionalFields);
document.querySelectorAll("input[name=\"backupScopeCategory\"]")
  .forEach((checkbox) => checkbox.addEventListener("change", () => {
    syncBackupProtectedScope(true);
    const origin = elements.backupControlConfigurationMode.value === "STANDARD"
      ? "OVERRIDE"
      : "MANUAL";
    setBackupFieldOrigin("protectedScope", origin);
  }));
[elements.backupControlScopeAdditional, elements.backupControlScopeExclusions]
  .forEach((input) => input.addEventListener("input", () => {
    syncBackupProtectedScope(true);
    updateBackupAutofillSummary();
  }));
document.querySelectorAll("#backupControlForm [data-origin-field] input, #backupControlForm [data-origin-field] select, #backupControlForm [data-origin-field] textarea")
  .forEach((input) => input.addEventListener("change", () => {
    if (state.backupControlUi.applyingTemplate || input.readOnly) {
      return;
    }
    const field = input.closest("[data-origin-field]")?.dataset.originField;
    if (!field) {
      return;
    }
    const previous = state.backupControlUi.fieldOrigins[field];
    if (elements.backupControlConfigurationMode.value === "STANDARD" &&
        ["DETECTED", "INHERITED", "SUGGESTED"].includes(previous)) {
      elements.backupControlConfigurationMode.value = "CUSTOMIZED";
      updateBackupControlConditionalFields();
    }
    setBackupFieldOrigin(field,
      ["DETECTED", "INHERITED", "SUGGESTED"].includes(previous)
        ? "OVERRIDE"
        : "MANUAL");
  }));
elements.backupObservationForm.addEventListener("submit", saveBackupObservation);
elements.backupObservationCancel.addEventListener("click", hideBackupControlEditors);
elements.backupRestorationForm.addEventListener("submit", saveBackupRestoration);
elements.backupRestorationCancel.addEventListener("click", hideBackupControlEditors);
elements.backupEvidenceForm.addEventListener("submit", saveBackupEvidence);
elements.backupEvidenceCancel.addEventListener("click", hideBackupControlEditors);
elements.backupEvidenceFile.addEventListener("change", () => {
  const file = elements.backupEvidenceFile.files?.[0];
  elements.backupEvidenceFileLabel.textContent = file
    ? `${file.name} · ${formatBytes(file.size)}`
    : "PDF, Office, imagen, CSV, texto o log; máximo 25 MB.";
});
elements.backupControlDetailClose.addEventListener("click", () => {
  elements.backupControlDetailPanel.classList.add("hidden");
  state.backupControlUi.selectedId = null;
  state.backupControlUi.detail = null;
});
elements.backupSortBy.addEventListener("change", (event) => {
  state.backupCoverageSort.field = event.target.value;
  state.backupCoverageSort.direction =
    event.target.value === "lastBackupAtUtc" || event.target.value === "bytesCopied"
      ? "desc"
      : "asc";
  state.backupCoveragePage.page = 1;
  renderBackupCoverage();
});
elements.backupSortDirection.addEventListener("click", () => {
  state.backupCoverageSort.direction =
    state.backupCoverageSort.direction === "asc" ? "desc" : "asc";
  state.backupCoveragePage.page = 1;
  renderBackupCoverage();
});
elements.securityDeviceSortBy.addEventListener("change", (event) => {
  state.securityPages.deviceSort = event.target.value;
  state.securityPages.deviceDirection =
    event.target.value === "captured" || event.target.value === "attention"
      ? "desc"
      : "asc";
  state.securityPages.devicePage = 1;
  void loadSecurity();
});
elements.securityDeviceSortDirection.addEventListener("click", () => {
  state.securityPages.deviceDirection =
    state.securityPages.deviceDirection === "asc" ? "desc" : "asc";
  state.securityPages.devicePage = 1;
  void loadSecurity();
});
elements.securityEventDeviceFilter.addEventListener("change", (event) => {
  state.securityPages.eventDeviceId = event.target.value;
  state.securityPages.eventPage = 1;
  void loadSecurity();
});
elements.securityEventSortBy.addEventListener("change", (event) => {
  state.securityPages.eventSort = event.target.value;
  state.securityPages.eventDirection =
    event.target.value === "date" || event.target.value === "severity"
      ? "desc"
      : "asc";
  state.securityPages.eventPage = 1;
  void loadSecurity();
});
elements.securityEventSortDirection.addEventListener("click", () => {
  state.securityPages.eventDirection =
    state.securityPages.eventDirection === "asc" ? "desc" : "asc";
  state.securityPages.eventPage = 1;
  void loadSecurity();
});
elements.securityDevicePreviousPage.addEventListener("click", () => {
  if (state.securityPages.devicePage > 1) {
    state.securityPages.devicePage--;
    void loadSecurity();
  }
});
elements.securityDeviceNextPage.addEventListener("click", () => {
  const page = state.security?.devices;
  const totalPages = Math.max(
    1,
    Math.ceil(Number(page?.total || 0) / Number(page?.pageSize || 1)));
  if (state.securityPages.devicePage < totalPages) {
    state.securityPages.devicePage++;
    void loadSecurity();
  }
});
elements.securityEventPreviousPage.addEventListener("click", () => {
  if (state.securityPages.eventPage > 1) {
    state.securityPages.eventPage--;
    void loadSecurity();
  }
});
elements.securityEventNextPage.addEventListener("click", () => {
  const page = state.security?.recentEvents;
  const totalPages = Math.max(
    1,
    Math.ceil(Number(page?.total || 0) / Number(page?.pageSize || 1)));
  if (state.securityPages.eventPage < totalPages) {
    state.securityPages.eventPage++;
    void loadSecurity();
  }
});
elements.scanAllNetworks.addEventListener("click", () =>
  void queueNetworkScan(null));
elements.networkScopeFilter.addEventListener("change", (event) => {
  state.networkPage.scope = event.target.value;
  state.networkPage.page = 1;
  void loadNetworkDiscovery();
});
elements.networkSearch.addEventListener("input", (event) => {
  state.networkPage.search = event.target.value.trim();
  state.networkPage.page = 1;
  clearTimeout(state.networkSearchTimer);
  state.networkSearchTimer = setTimeout(
    () => void loadNetworkDiscovery(), 350);
});
elements.networkPreviousPage.addEventListener("click", () => {
  if (state.networkPage.page > 1) {
    state.networkPage.page--;
    void loadNetworkDiscovery();
  }
});
elements.networkNextPage.addEventListener("click", () => {
  if (state.networkScanner?.mode === "results") {
    state.networkPage.page++;
    globalThis.AtlasNetworkScanner?.render();
    return;
  }
  const assets = state.network?.assets;
  const totalPages = Math.max(
    1,
    Math.ceil(Number(assets?.total || 0) / Number(assets?.pageSize || 1)));
  if (state.networkPage.page < totalPages) {
    state.networkPage.page++;
    void loadNetworkDiscovery();
  }
});
elements.backupCoveragePreviousPage.addEventListener("click", () => {
  if (state.backupCoveragePage.page > 1) {
    state.backupCoveragePage.page--;
    renderBackupCoverage();
  }
});
elements.backupCoverageNextPage.addEventListener("click", () => {
  const totalPages = Math.max(
    1,
    Math.ceil(
      state.backupCoverage.length / state.backupCoveragePage.pageSize));
  if (state.backupCoveragePage.page < totalPages) {
    state.backupCoveragePage.page++;
    renderBackupCoverage();
  }
});
elements.nasExplorerPreviousPage.addEventListener("click", () => {
  if (state.nasBrowser.page > 1) {
    void loadNasBrowser(state.nasBrowser.path, state.nasBrowser.page - 1);
  }
});
elements.nasExplorerNextPage.addEventListener("click", () => {
  const totalPages = Math.max(
    1,
    Math.ceil(state.nasBrowser.total / state.nasBrowser.pageSize));
  if (state.nasBrowser.page < totalPages) {
    void loadNasBrowser(state.nasBrowser.path, state.nasBrowser.page + 1);
  }
});
elements.notificationButton.addEventListener("click", () => {
  if (elements.notificationPanel.classList.contains("hidden")) {
    openNotificationPanel();
  } else {
    closeNotificationPanel();
  }
});
elements.markAllNotifications.addEventListener("click", () =>
  void markAllNotificationsRead());
elements.notificationPreviousPage.addEventListener("click", () => {
  if ((state.notifications?.page || 1) > 1) {
    state.notifications.page--;
    void loadNotifications(false);
  }
});
elements.notificationNextPage.addEventListener("click", () => {
  const totalPages = Math.max(
    1,
    Math.ceil(
      Number(state.notifications?.total || 0) /
      Number(state.notifications?.pageSize || 20)));
  if ((state.notifications?.page || 1) < totalPages) {
    state.notifications.page++;
    void loadNotifications(false);
  }
});
elements.refreshButton.addEventListener("click", () => void refreshAll(true));
elements.logoutButton.addEventListener("click", async () => {
  try {
    await api("/api/v1/admin/logout", {
      method: "POST",
      headers: { "Content-Type": "application/json" }
    });
  } finally {
    window.location.replace("/login");
  }
});
elements.menuButton.addEventListener("click", () =>
  elements.sidebar.classList.toggle("open"));
elements.closeDrawer.addEventListener("click", closeDevice);
elements.drawerBackdrop.addEventListener("click", closeDevice);
elements.installerForm.addEventListener("submit", (event) =>
  void generateInstaller(event));
elements.releaseUploadForm.addEventListener("submit", (event) =>
  void uploadRelease(event));
elements.releaseFile.addEventListener("change", () => {
  const file = elements.releaseFile.files?.[0];
  elements.releaseFileLabel.textContent = file
    ? `${file.name} · ${formatBytes(file.size)}`
    : "Seleccionar archivo .atlasrelease";
});
elements.deploymentScope.addEventListener("change", () => {
  const scope = elements.deploymentScope.value;
  elements.deploymentAgencyField.classList.toggle(
    "hidden",
    scope !== "AGENCY"
  );
  elements.deploymentHint.textContent = scope === "GENERAL"
    ? "GENERAL alcanza toda la flota y exige una confirmación escrita."
    : scope === "AGENCY"
      ? "La agencia seleccionada recibe el release antes del despliegue general."
      : "PILOTO entrega la versión únicamente a los equipos marcados para validación.";
});
elements.newAgencyButton.addEventListener("click", beginCreateAgency);
elements.cancelAgency.addEventListener("click", () => {
  elements.agencyFormPanel.classList.add("hidden");
  resetAgencyForm();
});
elements.agencyForm.addEventListener("submit", (event) =>
  void saveAgency(event));
elements.newNasButton.addEventListener("click", beginCreateNas);
elements.cancelNas.addEventListener("click", () => {
  elements.nasFormPanel.classList.add("hidden");
  resetNasForm();
});
elements.nasForm.addEventListener("submit", (event) =>
  void saveNas(event));
elements.nasMountProtocol.addEventListener("change", updateNasMountFields);
elements.cancelNasMount.addEventListener("click", () => {
  elements.nasMountPassword.value = "";
  elements.nasMountPanel.classList.add("hidden");
  resetNasMountForm();
});
elements.nasMountForm.addEventListener("submit", (event) =>
  void provisionNasMount(event));
elements.nasSearch.addEventListener("input", () => {
  state.nasDirectory.search = elements.nasSearch.value;
  renderNas();
});
elements.nasAgencyFilter.addEventListener("change", () => {
  state.nasDirectory.agency = elements.nasAgencyFilter.value;
  renderNas();
});
elements.expandAllNas.addEventListener("click", () => {
  state.nas.forEach((nas) => state.nasDirectory.expanded.add(nasGroupKey(nas)));
  renderNas();
});
elements.collapseAllNas.addEventListener("click", () => {
  state.nasDirectory.expanded.clear();
  state.nasDirectory.initialized = true;
  renderNas();
});
elements.nasDetailClose.addEventListener("click", closeNasDetail);
elements.nasDetailOverlay.addEventListener("click", (event) => {
  if (event.target === elements.nasDetailOverlay) {
    closeNasDetail();
  }
});
elements.releaseDetailClose.addEventListener("click", closeReleaseDetailDialog);
elements.releaseDetailOverlay.addEventListener("click", (event) => {
  if (event.target === elements.releaseDetailOverlay) {
    closeReleaseDetailDialog();
  }
});
elements.governancePreviewClose.addEventListener(
  "click", closeGovernancePreview);
elements.governancePreviewOverlay.addEventListener("click", (event) => {
  if (event.target === elements.governancePreviewOverlay) {
    closeGovernancePreview();
  }
});
elements.nasDetailExplore.addEventListener("click", () => {
  document.querySelector(".atlas-nas-explorer")?.scrollIntoView({
    behavior: "smooth",
    block: "start"
  });
});
elements.nasDetailEdit.addEventListener("click", () => {
  const nasId = state.nasBrowser.nasId;
  closeNasDetail();
  beginEditNas(nasId);
});
elements.nasDetailMount.addEventListener("click", () => {
  const nasId = state.nasBrowser.nasId;
  closeNasDetail();
  beginMountNas(nasId);
});
elements.nasDetailCase.addEventListener("click", () => {
  const finding = state.cases.findingRegistry.get(
    elements.nasDetailCase.dataset.caseFinding || "");
  if (finding) {
    closeNasDetail();
    void prepareCaseFromFinding(finding);
  }
});
elements.nasExplorerSelect.addEventListener("change", () => {
  state.nasBrowser.nasId = elements.nasExplorerSelect.value;
  void loadNasBrowser("");
});
elements.nasUpButton.addEventListener("click", () =>
  void loadNasBrowser(state.nasBrowser.parentPath || ""));
elements.probeNasButton.addEventListener("click", () =>
  void probeSelectedNas());
elements.trainingCycleSelect.addEventListener("change", (event) =>
  void loadTraining(event.target.value));
elements.trainingNewCycle.addEventListener("click", beginCreateTrainingCycle);
elements.trainingEditCycle.addEventListener("click", beginEditTrainingCycle);
elements.trainingCloneCycle.addEventListener("click", beginCloneTrainingCycle);
elements.trainingCycleCancel.addEventListener("click", closeTrainingPanels);
elements.trainingCycleCancelSecondary.addEventListener("click", closeTrainingPanels);
elements.trainingCycleForm.addEventListener("submit", (event) =>
  void saveTrainingCycle(event));
elements.trainingNewSession.addEventListener("click", beginCreateTrainingSession);
elements.trainingSessionTopic.addEventListener("change", applyTrainingTopicToSession);
elements.trainingSessionCancel.addEventListener("click", closeTrainingPanels);
elements.trainingSessionCancelSecondary.addEventListener("click", closeTrainingPanels);
elements.trainingSessionForm.addEventListener("submit", (event) =>
  void saveTrainingSession(event));
elements.trainingNewTopic.addEventListener("click", beginCreateTrainingTopic);
elements.trainingTopicCancel.addEventListener("click", closeTrainingPanels);
elements.trainingTopicCancelSecondary.addEventListener("click", closeTrainingPanels);
elements.trainingTopicForm.addEventListener("submit", (event) =>
  void saveTrainingTopic(event));
elements.trainingAttendanceClose.addEventListener("click", closeTrainingPanels);
elements.trainingAttendanceFile.addEventListener(
  "change", selectTrainingAttendanceFile);
elements.trainingAttendanceUpload.addEventListener(
  "click", () => void uploadTrainingAttendance());
elements.governanceNewDocument.addEventListener(
  "click", beginCreateGovernanceDocument);
elements.governanceForm.addEventListener(
  "submit", (event) => void saveGovernanceDocument(event));
elements.governanceType.addEventListener(
  "change", syncGovernanceParentAvailability);
elements.governanceParent.addEventListener(
  "change", removeParentFromRelatedDocuments);
elements.governanceFormClose.addEventListener("click", closeGovernanceEditor);
elements.governanceFormCancel.addEventListener("click", closeGovernanceEditor);
elements.governanceDetailClose.addEventListener("click", closeGovernanceDetail);
elements.governanceEdit.addEventListener("click", beginEditGovernanceDocument);
elements.governanceEditDescription.addEventListener(
  "click", beginEditGovernanceDescription);
elements.governanceDescriptionForm.addEventListener(
  "submit", (event) => void saveGovernanceDescription(event));
elements.governanceDescriptionCancel.addEventListener(
  "click", closeGovernanceDescriptionEditor);
elements.governanceAddVersion.addEventListener(
  "click", beginGovernanceVersionUpload);
elements.governanceVersionClose.addEventListener("click", closeGovernanceVersion);
elements.governanceVersionCancel.addEventListener("click", closeGovernanceVersion);
elements.governanceVersionUpload.addEventListener(
  "click", () => void uploadGovernanceVersion());
elements.governanceFile.addEventListener("change", () => {
  const file = elements.governanceFile.files?.[0];
  elements.governanceFileLabel.textContent = file
    ? `${file.name} · ${formatBytes(file.size)}`
    : "Seleccionar PDF, Word, Excel o PowerPoint";
});
elements.governanceReview.addEventListener(
  "click", () => void changeGovernanceStatus("IN_REVIEW"));
elements.governanceReturnDraft.addEventListener(
  "click", () => void changeGovernanceStatus("DRAFT"));
elements.governanceApprove.addEventListener("click", openGovernanceApproval);
elements.governanceRequestCorrection.addEventListener(
  "click", () => void requestGovernanceCorrection());
elements.governanceCancelApproval.addEventListener("click", () =>
  elements.governanceApprovalBox.classList.add("hidden"));
elements.governanceConfirmApproval.addEventListener(
  "click", () => void confirmGovernanceApproval());
elements.governanceObsolete.addEventListener("click", () => {
  if (window.confirm(
    "¿Marcar este documento como obsoleto? La versión y su evidencia se conservarán."
  )) {
    void changeGovernanceStatus("OBSOLETE");
  }
});
elements.governanceSearch.addEventListener("input", (event) => {
  state.governanceUi.search = event.target.value.trim();
  renderGovernance();
});
elements.governanceTypeFilter.addEventListener("change", (event) => {
  state.governanceUi.type = event.target.value;
  renderGovernance();
});
elements.governanceStatusFilter.addEventListener("change", (event) => {
  state.governanceUi.status = event.target.value;
  renderGovernance();
});
elements.governanceClearFilters.addEventListener("click", () => {
  state.governanceUi.search = "";
  state.governanceUi.type = "";
  state.governanceUi.status = "";
  elements.governanceSearch.value = "";
  elements.governanceTypeFilter.value = "";
  elements.governanceStatusFilter.value = "";
  renderGovernance();
});
elements.newCaseButton.addEventListener("click", beginCreateCase);
elements.caseCancel.addEventListener("click", closeCaseEditor);
elements.caseCancelSecondary.addEventListener("click", closeCaseEditor);
elements.caseDetailClose.addEventListener("click", closeCaseDetail);
elements.caseEditButton.addEventListener("click", beginEditCase);
elements.caseForm.addEventListener("submit", (event) => void saveCase(event));
elements.caseCommentForm.addEventListener("submit", (event) =>
  void addCaseComment(event));
elements.caseSearch.addEventListener("input", (event) => {
  state.cases.filters.search = event.target.value.trim();
  state.cases.page = 1;
  clearTimeout(state.caseSearchTimer);
  state.caseSearchTimer = setTimeout(() => void loadCases(), 350);
});
elements.caseTypeFilter.addEventListener("change", (event) => {
  state.cases.filters.type = event.target.value;
  state.cases.page = 1;
  void loadCases();
});
elements.caseStatusFilter.addEventListener("change", (event) => {
  state.cases.filters.status = event.target.value;
  state.cases.page = 1;
  void loadCases();
});
elements.caseSeverityFilter.addEventListener("change", (event) => {
  state.cases.filters.severity = event.target.value;
  state.cases.page = 1;
  void loadCases();
});
elements.caseAgencyFilter.addEventListener("change", (event) => {
  state.cases.filters.agency = event.target.value;
  state.cases.page = 1;
  void loadCases();
});
elements.casePreviousPage.addEventListener("click", () => {
  if (state.cases.page > 1) {
    state.cases.page--;
    void loadCases();
  }
});
elements.caseNextPage.addEventListener("click", () => {
  const totalPages = Math.max(
    1,
    Math.ceil(Number(state.cases.total || 0) / Number(state.cases.pageSize || 20)));
  if (state.cases.page < totalPages) {
    state.cases.page++;
    void loadCases();
  }
});
document.querySelectorAll("[data-audit-tab]").forEach(button=>button.addEventListener("click",()=>selectAuditTab(button.dataset.auditTab)));
document.querySelectorAll("[data-audit-report]").forEach(button=>button.addEventListener("click",()=>void exportAudit(button.dataset.auditReport,button.dataset.format)));
document.querySelectorAll("[data-audit-clear]").forEach(button=>button.addEventListener("click",()=>{clearAuditFilters(button.dataset.auditClear);void loadAuditPage(button.dataset.auditClear);}));
document.querySelectorAll("[data-audit-dates]").forEach(button=>button.addEventListener("click",()=>{
  const kind=button.dataset.auditDates;
  try {
    const from=document.querySelector(`[data-audit-kind="${kind}"][data-audit-date="from"]`).value;
    const to=document.querySelector(`[data-audit-kind="${kind}"][data-audit-date="to"]`).value;
    const bounds=auditDateBounds(from,to),filters=state.audit[kind==="events"?"eventFilters":"enrollmentFilters"];
    Object.assign(filters,bounds,{period:"ALL"});if(kind==="events")elements.auditEventPeriod.value="ALL";
    state.audit[kind].page=1;void loadAuditPage(kind);
  }catch(error){showToast(error.message);}
}));
document.querySelectorAll("[data-audit-metric]").forEach(button=>button.addEventListener("click",()=>{
  const metric=button.dataset.auditMetric,kind=["enrollments","unavailable"].includes(metric)?"enrollments":"events";
  clearAuditFilters(kind);
  if(metric==="recent"||metric==="failed"){state.audit.eventFilters.period="24H";elements.auditEventPeriod.value="24H";}
  if(metric==="failed"){
    state.audit.eventFilters.outcome="FAILED";
    elements.auditEventOutcome.value="FAILED";
    state.audit.eventFilters.module="ACCESS";elements.auditEventModule.value="ACCESS";
  }
  if(metric==="unavailable"){state.audit.enrollmentFilters.attribution="UNAVAILABLE";elements.auditEnrollmentAttribution.value="UNAVAILABLE";}
  selectAuditTab(kind);void loadAuditPage(kind);
}));
elements.auditDetailOverlay.addEventListener("keydown",event=>{
  if(event.key!=="Tab")return;
  const focusable=[...elements.auditDetailOverlay.querySelectorAll('button:not([disabled]),a[href],summary,input,select,textarea,[tabindex="0"]')].filter(el=>el.getClientRects().length);
  const first=focusable[0],last=focusable.at(-1);
  if(event.shiftKey && document.activeElement===first){event.preventDefault();last.focus();}
  else if(!event.shiftKey && document.activeElement===last){event.preventDefault();first.focus();}
});


elements.auditRefresh.addEventListener("click", () => void loadAudit(true));
elements.auditEnrollmentExport.addEventListener(
  "click", () => exportAudit("enrollments"));
elements.auditEventExport.addEventListener(
  "click", () => exportAudit("events"));
elements.auditEnrollmentSearch.addEventListener("input", (event) => {
  state.audit.enrollmentFilters.search = event.target.value.trim();
  state.audit.enrollments.page = 1;
  clearTimeout(state.auditEnrollmentSearchTimer);
  state.auditEnrollmentSearchTimer = setTimeout(
    () => void loadAuditEnrollments(), 350);
});
elements.auditEnrollmentAgency.addEventListener("change", (event) => {
  state.audit.enrollmentFilters.agency = event.target.value;
  state.audit.enrollments.page = 1;
  void loadAuditEnrollments();
});
elements.auditEnrollmentAttribution.addEventListener("change", (event) => {
  state.audit.enrollmentFilters.attribution = event.target.value;
  state.audit.enrollments.page = 1;
  void loadAuditEnrollments();
});
elements.auditEventSearch.addEventListener("input", (event) => {
  state.audit.eventFilters.search = event.target.value.trim();
  state.audit.events.page = 1;
  clearTimeout(state.auditEventSearchTimer);
  state.auditEventSearchTimer = setTimeout(() => void loadAuditEvents(), 350);
});
for (const [element, key] of [
  [elements.auditEventActor, "actor"],
  [elements.auditEventModule, "module"],
  [elements.auditEventOutcome, "outcome"],
  [elements.auditEventPeriod, "period"]
]) {
  element.addEventListener("change", (event) => {
    state.audit.eventFilters[key] = event.target.value;
    if (key === "period") { delete state.audit.eventFilters.fromUtc; delete state.audit.eventFilters.toUtc; document.querySelectorAll(`[data-audit-kind="events"]`).forEach(input => input.value=""); }
    state.audit.events.page = 1;
    void loadAuditEvents();
  });
}
elements.auditEnrollmentPrevious.addEventListener("click", () => {
  if (state.audit.enrollments.page > 1) {
    state.audit.enrollments.page--;
    void loadAuditEnrollments();
  }
});
elements.auditEnrollmentNext.addEventListener("click", () => {
  const totalPages = Math.max(1, Math.ceil(
    state.audit.enrollments.total / state.audit.enrollments.pageSize));
  if (state.audit.enrollments.page < totalPages) {
    state.audit.enrollments.page++;
    void loadAuditEnrollments();
  }
});
elements.auditEventPrevious.addEventListener("click", () => {
  if (state.audit.events.page > 1) {
    state.audit.events.page--;
    void loadAuditEvents();
  }
});
elements.auditEventNext.addEventListener("click", () => {
  const totalPages = Math.max(1, Math.ceil(
    state.audit.events.total / state.audit.events.pageSize));
  if (state.audit.events.page < totalPages) {
    state.audit.events.page++;
    void loadAuditEvents();
  }
});
elements.auditDetailClose.addEventListener("click", closeAuditDetail);
elements.auditDetailOverlay.addEventListener("click", (event) => {
  if (event.target === elements.auditDetailOverlay) closeAuditDetail();
});
document.addEventListener("keydown", (event) => {
  if (event.key === "Escape") {
    // Native dialogs handle Escape themselves, including nested NAS confirmations.
    if (document.querySelector("dialog[open]")) return;
    closeDevice();
    closeNasDetail();
    closeCaseEditor();
    closeCaseDetail();
    closeTrainingPanels();
    closeGovernanceEditor();
    closeGovernanceVersion();
    closeGovernanceDetail();
    closeGovernancePreview();
    closeReleaseDetailDialog();
    closeAuditDetail();
    closeNotificationPanel(true);
    elements.sidebar.classList.remove("open");
  }
});
document.getElementById("notificationCloseButton")?.addEventListener("click", () => closeNotificationPanel(true));
document.addEventListener("click", (event) => {
  if (!elements.notificationCenter.contains(event.target)
      && !event.target.closest("[data-notification-panel-open], [data-attention-history]")) {
    closeNotificationPanel();
  }
});

void refreshAll();
window.setInterval(() => {
  if (!document.hidden) {
    void refreshAll();
  }
}, 60_000);
