"use strict";

// Shared plans resolve into existing profiles, preserving profile IDs and activity.
const backupWorkspace = { tab: "pending", copyFilter: "", search: "", agency: "", plans: [], loaded: false, loading: false, page: 1 };
const backupCopyFilters = { current: "Copias al día", attention: "Copias por atender" };
const bw = id => document.getElementById(id);
const bwEscape = value => escapeHtml(String(value ?? ""));
const bwButton = (label, action, id = "", primary = false) => `<button type="button" class="${primary ? "primary" : "secondary"}-button compact" data-bw-action="${action}" data-id="${bwEscape(id)}">${bwEscape(label)}</button>`;

function backupCopyStatus(profile) {
  return profile.compliance === "RESTORE_TEST_DUE" ? "COMPLIANT" : profile.compliance;
}
function backupLatestExecutionAcceptable(profile) {
  return profile.latestExecutionAssessment?.acceptableForControl ?? profile.latestExecution?.acceptable ?? false;
}
function backupAcceptedPendingAssessment(profile) {
  const accepted = profile.lastAcceptableAssessment;
  if (accepted) return accepted.code === "ACCEPTABLE_WITH_PENDING_FILES" && accepted.acceptableForControl ? accepted : null;
  const latest = profile.latestExecutionAssessment;
  const sameExecution = profile.lastAcceptableAtUtc && profile.latestExecution?.finishedAtUtc &&
    Date.parse(profile.lastAcceptableAtUtc) === Date.parse(profile.latestExecution.finishedAtUtc);
  return sameExecution && latest?.code === "ACCEPTABLE_WITH_PENDING_FILES" && latest.acceptableForControl ? latest : null;
}
function backupAcceptedWithPendingFiles(profile) {
  return backupCopyStatus(profile) === "COMPLIANT" && !!backupAcceptedPendingAssessment(profile);
}
function backupPendingFilesLabel(assessment) {
  if (assessment?.pendingFiles == null) return "Cantidad de archivos pendientes por confirmar";
  const count = assessment.pendingFiles;
  return `${assessment.pendingFilesCountComplete === false ? "Al menos " : ""}${count} ${count === 1 ? "archivo pendiente" : "archivos pendientes"}`;
}
function backupRecoveryState(profile) {
  if (profile.deviceRetired) return { label: "Historial conservado", pending: false, tone: "muted" };
  if (profile.trackingPaused) return { label: "Pausado por devolución", pending: false, tone: "muted" };
  if (!profile.enabled || !["ACTIVE", "APPROVED"].includes(profile.approvalStatus)) return { label: "Sin seguimiento activo", pending: false, tone: "muted" };
  if (profile.planId) return { label: "Demostración en el plan", pending: false, tone: "muted" };
  if (!profile.lastRestorationAtUtc) return { label: "Primera prueba pendiente", pending: true, tone: "attention" };
  if (profile.lastRestorationResult !== "SUCCESS") return { label: "Prueba no satisfactoria", pending: true, tone: "critical" };
  return profile.restoreTestDue ? { label: "Prueba vencida", pending: true, tone: "attention" } : { label: "Prueba vigente", pending: false, tone: "healthy" };
}
function backupNeedsAttention(profile) {
  if (profile.deviceRetired) return false;
  if (profile.communal) return false;
  if (profile.trackingPaused) return profile.compliance === "PAUSE_REVIEW_DUE";
  return profile.enabled && (!["COMPLIANT", "SCHEDULED"].includes(backupCopyStatus(profile)) || backupRecoveryState(profile).pending ||
    (!!profile.latestExecution && !backupLatestExecutionAcceptable(profile) && profile.latestExecution.result !== "RUNNING"));
}

function backupCopyTrackingActive(profile) {
  return !profile.gap && !profile.communal && !profile.deviceRetired && profile.enabled && !profile.trackingPaused;
}
function backupMatchesCopyFilter(profile, filter) {
  if (!backupCopyTrackingActive(profile)) return false;
  if (filter === "current") return backupCopyStatus(profile) === "COMPLIANT";
  return filter === "attention" && (!["COMPLIANT", "SCHEDULED"].includes(backupCopyStatus(profile)) ||
    (!!profile.latestExecution && !backupLatestExecutionAcceptable(profile) && profile.latestExecution.result !== "RUNNING"));
}
function showBackupCopyFilter(filter) {
  if (!Object.hasOwn(backupCopyFilters, filter)) return;
  backupWorkspace.tab = "all";
  backupWorkspace.copyFilter = filter;
  backupWorkspace.search = "";
  backupWorkspace.agency = "";
  backupWorkspace.page = 1;
  bw("bwSearch").value = "";
  renderBackupWorkspace();
  const heading = bw("bwCopyFilter");
  const topbar = document.querySelector(".topbar");
  if (topbar) heading.style.scrollMarginTop = `${Math.ceil(topbar.getBoundingClientRect().height) + 16}px`;
  heading.focus({ preventScroll: true });
  heading.scrollIntoView({ block: "start" });
}

function backupExecutionLabel(run) {
  if (!run) return "Sin reporte";
  if (run.result === "WARNING" && run.acceptable) return "Completada con avisos · aceptable";
  if (["PARTIAL", "FAILED", "UNCONFIRMED"].includes(run.result) && run.evidence?.phase === "FINISHED") {
    const folders = run.evidence.folders || [];
    const failed = folders.reduce((sum, f) => sum + (f.filesFailed ?? 0), 0);
    if (failed > 0) return `${folders.some(f => f.filesFailed == null) ? "Al menos " : ""}${failed} ${failed === 1 ? "archivo pendiente" : "archivos pendientes"} de copiar`;
    const missing = folders.filter(f => f.state === "MISSING").length;
    if (missing) return `${missing} ${missing === 1 ? "carpeta no encontrada" : "carpetas no encontradas"}`;
    return folders.some(f => f.state === "FAILED") ? "Finalizada con errores de copia" : "Finalizada · resumen por confirmar";
  }
  return ({ SUCCESS: "Completada", WARNING: "Finalizada con avisos · ver detalle", FAILED: "Ejecución con fallos",
    CANCELLED: "Cancelada", SKIPPED: "No iniciada", RUNNING: "En curso", PARTIAL: "Finalizada con carpetas pendientes",
    UNCONFIRMED: "Resultado final pendiente", UNKNOWN: "Resultado no reconocido" })[run.result] || "Resultado no reconocido";
}

function backupCopyPresentation(profile) {
  const copy = backupCopyStatus(profile), run = profile.latestExecution, assessment = profile.latestExecutionAssessment;
  const acceptedPending = backupAcceptedWithPendingFiles(profile);
  const labels = { SCHEDULED: "Pendiente dentro del día", COMPLIANT: "Copia aceptable al día", FAILED: "Ejecución con fallos", CANCELLED: "Copia cancelada",
    OVERDUE: "Hace falta una copia reciente", WARNING: "Copia con avisos", NOT_CONFIRMED: "Resultado por confirmar",
    PENDING_APPROVAL: "Por activar", INACTIVE: "Pausado", DESTINATION_MISMATCH: "Revisar destino",
    EXCEPTION_EXPIRED: "Revisar excepción", CONFIGURATION_REQUIRED: "Completar configuración",
    RUNNING: "Copia en curso", SKIPPED: "Copia no iniciada", PARTIAL: "Archivos o carpetas pendientes" };
  let label = labels[copy] || copy;
  if (run && copy !== "COMPLIANT" && !["SCHEDULED", "PENDING_APPROVAL", "INACTIVE", "EXCEPTION_EXPIRED"].includes(copy)) {
    if (assessment?.code === "SCOPE_MISMATCH") label = "Revisar carpetas del control";
    else if (assessment?.code === "SCOPE_CONFIGURATION_REQUIRED") label = "Completar alcance del control";
    else if (assessment?.code === "DESTINATION_MISMATCH") label = "Revisar destino de la copia";
    else if (assessment?.acceptableForControl === false || (!assessment && !run.acceptable)) label = backupExecutionLabel(run);
  }
  return { label, tone: acceptedPending ? "bw-copy-acceptable-pending" : copy === "COMPLIANT" ? "healthy" : copy === "SCHEDULED" ? "muted" : ["FAILED", "DESTINATION_MISMATCH"].includes(copy) ? "critical" : "attention",
    acceptedPending, pendingLabel: acceptedPending ? backupPendingFilesLabel(backupAcceptedPendingAssessment(profile)) : "", detail: assessment?.detail || "" };
}

function backupPendingFilesDetail(assessment) {
  if (!assessment || !(assessment.pendingFiles > 0)) return "";
  return `<section class="bw-pending-files${assessment.acceptableForControl && assessment.code === "ACCEPTABLE_WITH_PENDING_FILES" ? " bw-copy-acceptable-pending" : ""}"><h4>${bwEscape(backupPendingFilesLabel(assessment))}</h4>
    <p>${bwEscape(assessment.detail)}</p>${assessment.pendingReasons?.length ? `<ul>${assessment.pendingReasons.map(reason => `<li>${bwEscape(reason)}</li>`).join("")}</ul>` : ""}
    <small>El informe identifica carpetas y recuentos. Los nombres de archivo y el error de cada archivo se consultan en el registro local de esa ejecución. Una copia posterior deberá confirmar si se resolvieron los pendientes.</small></section>`;
}

function backupExecutionHistory(run) {
  const e = run.evidence;
  const assessment = run.controlAssessment;
  const acceptedPending = assessment?.code === "ACCEPTABLE_WITH_PENDING_FILES" && assessment.acceptableForControl;
  const states = { COMPLETED: "Completada", MISSING: "Carpeta no encontrada", FAILED: "Incidencia en la carpeta", PENDING: "No iniciada", RUNNING: "En proceso al último reporte", UNCONFIRMED: "Resumen por confirmar" };
  const reasons = { USER_CLOSED_WITHOUT_STARTING: "El usuario cerró la aplicación sin iniciar la copia.",
    USER_CANCELLED_PROCESS_STOPPED: "Cancelación solicitada por el usuario; el proceso de copia terminó.",
    NAS_UNREACHABLE_BEFORE_START: "El NAS no estaba accesible antes de iniciar.", CONFIGURATION_INVALID: "Configuración ausente o inválida.",
    PROCESS_FINISHED: "Proceso finalizado; resultado calculado a partir de cada carpeta.", STARTED: "Inicio registrado.", PROCESS_ACTIVE: "Actividad del proceso registrada." };
  return `<li><strong>${acceptedPending ? `<span class="bw-status bw-copy-acceptable-pending"><span class="bw-copy-ok" aria-hidden="true">✓</span>Aceptable · ${bwEscape(backupPendingFilesLabel(assessment))}</span>` : bwEscape(backupExecutionLabel(run))}</strong>
    <small>${run.startedAtUtc ? `Inicio: ${bwEscape(formatDate(run.startedAtUtc))} · ` : ""}${e?.phase === "RUNNING" ? "Última señal" : "Fin"}: ${bwEscape(formatDate(run.finishedAtUtc))}</small>
    <small>Recibido por ATLAS: ${run.receivedAtUtc ? bwEscape(formatDate(run.receivedAtUtc)) : "Fecha no disponible en reporte histórico"}</small>
    <small>${bwEscape(run.reference)} · ${bwEscape(run.destination || "Destino no informado")}</small>
    ${assessment?.code === "DESTINATION_MISMATCH" ? "<small>Destino diferente del de este control · ejecución del mismo equipo.</small>" : ""}
    ${acceptedPending ? `<small>${bwEscape(assessment.detail)}</small>${assessment.pendingReasons?.length ? `<ul>${assessment.pendingReasons.map(reason => `<li>${bwEscape(reason)}</li>`).join("")}</ul>` : ""}` : ""}
    ${e ? `<p>${bwEscape(reasons[e.reason] || (e.reason?.startsWith("EXECUTION_ERROR_") ? "La ejecución terminó con un error. Consulte el registro local." : "Resultado informado por el cliente."))}</p>
      <ul>${(e.folders || []).map(f => `<li><strong>${bwEscape(f.name)}</strong>: ${bwEscape(states[f.state] || "Resumen por confirmar")}<small>${f.filesCopied == null ? "Contadores no disponibles" : `${f.filesCopied} copiados · ${f.filesUnchanged ?? "—"} sin cambios o excluidos por configuración · ${f.filesFailed ?? "—"} no se pudieron copiar · ${f.filesTotal ?? "—"} contabilizados`}${f.exitCode != null ? ` · Código de Robocopy: ${f.exitCode}` : ""}</small></li>`).join("")}</ul>
      ${e.warnings?.length ? `<details><summary>Avisos de la ejecución</summary><p>${e.warnings.map(bwEscape).join(" · ")}</p></details>` : ""}
      <small>Exclusiones configuradas: ${e.excludesHidden ? "ocultos/sistema" : "sin exclusión de ocultos"} · ${e.excludesTemporary ? "temporales" : "sin exclusión de temporales"}.</small>
      <small>Robocopy agrupa archivos sin cambios y excluidos por configuración; no significan una ejecución omitida. El resultado no sustituye una prueba de restauración.</small>` :
      `<p>Reporte histórico: sin desglose verificable por carpeta. Los ceros antiguos no demuestran que se copiaran cero archivos.</p>`}
    ${run.failedFolders?.length ? `<small>Carpetas con fallos: ${bwEscape(run.failedFolders.join(", "))}</small>` : ""}
    ${run.missingFolders?.length ? `<small>Carpetas ausentes: ${bwEscape(run.missingFolders.join(", "))}</small>` : ""}</li>`;
}
function backupIsCommunal(device) {
  return (device?.workstationBackupPolicy || device?.backupProvider) === "SHARED_FOLDER";
}
function backupCommunalItems() {
  return (state.backupControl?.devices || []).filter(backupIsCommunal).map(d => ({
    id: d.id, deviceId: d.id, systemName: d.machineName, agencyCode: d.agencyCode,
    agencyName: (state.agencies || []).find(a => a.agencyCode === d.agencyCode)?.displayName || d.agencyCode,
    provider: "SHARED_FOLDER", communal: true, enabled: true
  }));
}
function backupWorkspaceItems() {
  const module = state.backupControl || {};
  const profiles = module.profiles || [];
  const assigned = new Set(profiles.map(p => p.deviceId).filter(Boolean));
  const gaps = (module.devices || []).filter(d => !assigned.has(d.id) && !backupIsCommunal(d)).map(d => ({
    id: d.id, deviceId: d.id, systemName: d.machineName, agencyCode: d.agencyCode,
    agencyName: (state.agencies || []).find(a => a.agencyCode === d.agencyCode)?.displayName || d.agencyCode,
    provider: d.backupProvider, gap: true, enabled: true
  }));
  return [...profiles, ...gaps, ...backupCommunalItems().filter(d => !assigned.has(d.deviceId))];
}
function ensureBackupWorkspace() {
  if (bw("backupWorkspace")) return;
  const host = document.createElement("div");
  host.id = "backupWorkspace";
  host.innerHTML = `<div class="bw-overview" id="bwOverview"></div>
    <article class="panel bw-main">
      <div class="bw-toolbar"><div class="bw-tabs" role="group" aria-label="Vista de respaldos">
        <button type="button" data-tab="pending" aria-pressed="true">Pendientes</button>
        <button type="button" data-tab="all" aria-pressed="false">Todos</button>
        <button type="button" data-tab="retired" aria-pressed="false">Desvinculados</button>
        <button type="button" data-tab="shared" aria-pressed="false">Carpetas en red</button>
        <button type="button" data-tab="plans" aria-pressed="false">Planes</button>
        <button type="button" data-tab="acronis" aria-pressed="false">Acronis</button>
        <button type="button" data-tab="schedules" aria-pressed="false">Calendarios</button>
        <button type="button" data-tab="context" aria-pressed="false">Revisión histórica</button>
        <button type="button" data-tab="destinations" aria-pressed="false">Destinos NAS</button>
      </div><div class="bw-filters"><input id="bwSearch" type="search" placeholder="Buscar equipo, usuario o copia" aria-label="Buscar equipo, usuario, copia, recurso, plan o carpeta">
        <select id="bwAgency" aria-label="Filtrar por agencia"><option value="">Todas las agencias</option></select></div></div>
      <div id="bwCopyFilter" class="bw-copy-filter" role="region" aria-label="Filtro de copias" tabindex="-1" hidden></div>
      <div id="bwContent" aria-live="polite"></div>
      <div class="bw-footer"><span id="bwCount"></span><div>${bwButton("Anterior", "previous")}${bwButton("Siguiente", "next")}</div></div>
    </article>
    <dialog id="bwDialog" class="bw-dialog" aria-labelledby="bwDialogTitle"></dialog>`;
  document.querySelector("#view-backups .backup-control-heading").after(host);
  document.querySelector("#view-backups").classList.add("bw-enabled");
  const controls = document.createElement("div"); controls.className = "bw-heading-actions";
  controls.innerHTML = bwButton("Control individual", "individual") + bwButton("Crear plan", "new-plan", "", true);
  document.querySelector("#view-backups .backup-control-heading").append(controls);
  host.querySelectorAll("[data-tab]").forEach(button => button.addEventListener("click", () => {
    backupWorkspace.copyFilter = "";
    if (button.dataset.tab === "destinations") { switchView("nas"); return; }
    backupWorkspace.tab = button.dataset.tab; backupWorkspace.page = 1; renderBackupWorkspace();
  }));
  bw("bwSearch").addEventListener("input", event => { backupWorkspace.search = event.target.value; backupWorkspace.page = 1; renderBackupWorkspaceContent(); });
  bw("bwAgency").addEventListener("change", event => { backupWorkspace.agency = event.target.value; backupWorkspace.page = 1; renderBackupWorkspaceContent(); });
  document.querySelector("#view-backups").addEventListener("click", event => {
    const button = event.target.closest("[data-bw-action]");
    if (button && !button.disabled) void backupWorkspaceAction(button);
  });
  bw("bwDialog").addEventListener("cancel", event => {
    if (bw("bwDialog").dataset.busy === "true") event.preventDefault();
  });
}
async function loadBackupPlans() {
  if (backupWorkspace.loading) return;
  backupWorkspace.loading = true;
  try { backupWorkspace.plans = await api("/api/v1/admin/backup-controls/plans"); backupWorkspace.loaded = true; }
  catch (error) { backupWorkspace.error = error.message === "unauthorized" ? "" : "No fue posible cargar los planes. Usa Actualizar para reintentar."; }
  finally { backupWorkspace.loading = false; renderBackupWorkspaceContent(); }
}
function renderBackupWorkspace() {
  ensureBackupWorkspace();
  document.querySelector(".bw-heading-actions").hidden = !canManageBackupControls();
  if (!backupWorkspace.loaded && !backupWorkspace.loading) void loadBackupPlans();
  const current = backupWorkspace.agency;
  const agencies = [...new Map(backupWorkspaceItems().map(p => [p.agencyCode, p.agencyName || p.agencyCode])).entries()];
  bw("bwAgency").innerHTML = '<option value="">Todas las agencias</option>' + agencies.map(([code, name]) => `<option value="${bwEscape(code)}">${bwEscape(name)}</option>`).join("");
  bw("bwAgency").value = current;
  document.querySelectorAll(".bw-tabs button").forEach(b => b.setAttribute("aria-pressed", String(b.dataset.tab === backupWorkspace.tab)));
  renderBackupWorkspaceContent();
}
function renderBackupWorkspaceContent() {
  if (!bw("bwContent")) return;
  const items = backupWorkspaceItems();
  const profiles = items.filter(backupCopyTrackingActive);
  const gaps = items.filter(p => p.gap);
  const copyFilter = backupWorkspace.tab === "all" && Object.hasOwn(backupCopyFilters, backupWorkspace.copyFilter) ? backupWorkspace.copyFilter : "";
  bw("bwOverview").innerHTML = [
    ["Copias al día", items.filter(p => backupMatchesCopyFilter(p, "current")).length, "healthy", "current"],
    ["Copias por atender", items.filter(p => backupMatchesCopyFilter(p, "attention")).length, "attention", "attention"],
    ["Dentro del día · por completar", profiles.filter(p => backupCopyStatus(p) === "SCHEDULED").length, "muted"],
    ["Demostraciones de planes pendientes", (state.backupControl?.planDemonstrations || []).filter(p => p.pending).length, "attention"],
    ["Pruebas individuales pendientes", profiles.filter(p => !p.planId && backupRecoveryState(p).pending).length, "attention"],
    ["Sin seguimiento", gaps.length, "muted"],
    ["En carpeta de red", backupCommunalItems().length, "muted"]
  ].map(([label, count, tone, filter]) => filter
    ? `<button type="button" class="bw-stat bw-stat-link ${tone}" data-bw-action="copy-filter" data-id="${filter}" aria-pressed="${copyFilter === filter}" aria-controls="bwContent" aria-label="${label}: ${count}. Ver lista"><strong>${count}</strong><span>${label}</span></button>`
    : `<div class="bw-stat ${tone}"><strong>${count}</strong><span>${label}</span></div>`).join("");
  const filterHeading = bw("bwCopyFilter");
  if (filterHeading) {
    filterHeading.hidden = !copyFilter;
    filterHeading.innerHTML = copyFilter ? `<div><h3>${backupCopyFilters[copyFilter]}</h3><p>Puedes buscar un equipo o filtrar por agencia dentro de esta lista.</p></div>${bwButton("Quitar filtro", "clear-copy-filter")}` : "";
  }
  bw("bwAgency").hidden = ["plans", "schedules", "context"].includes(backupWorkspace.tab);
  bw("bwSearch").placeholder = backupWorkspace.tab === "plans" ? "Buscar plan o carpeta" : backupWorkspace.tab === "schedules" ? "Buscar agencia, plan o equipo" : backupWorkspace.tab === "context" ? "Buscar equipo, control o evidencia" : "Buscar equipo, usuario o copia";
  if (backupWorkspace.tab === "schedules" && typeof renderBackupSchedules === "function") { void renderBackupSchedules(); return; }
  if (backupWorkspace.tab === "context" && typeof renderBackupContextReview === "function") { void renderBackupContextReview(); return; }
  if (backupWorkspace.tab === "plans") { renderBackupPlanList(); return; }
  if (backupWorkspace.tab === "acronis") { renderAcronisImports(); return; }
  if (backupWorkspace.tab === "shared") { renderBackupCommunalList(); return; }
  const query = backupWorkspace.search.toLocaleLowerCase();
  const filtered = items.filter(p => (backupWorkspace.tab === "retired" ? p.deviceRetired : !p.deviceRetired) &&
    (backupWorkspace.tab !== "pending" || p.gap || backupNeedsAttention(p)) &&
    (!copyFilter || backupMatchesCopyFilter(p, copyFilter)) &&
    (!backupWorkspace.agency || p.agencyCode === backupWorkspace.agency) &&
    backupWorkspaceSearchText(p).includes(query));
  const rank = p => p.gap ? 4 : ({ FAILED: 0, CANCELLED: 1, DESTINATION_MISMATCH: 0, OVERDUE: 1, WARNING: 2, NOT_CONFIRMED: 3, COMPLIANT: 6, INACTIVE: 9 }[backupCopyStatus(p)] ?? 4);
  filtered.sort((a, b) => rank(a) - rank(b) || a.systemName.localeCompare(b.systemName, "es"));
  backupWorkspace.page = Math.min(backupWorkspace.page, Math.max(1, Math.ceil(filtered.length / 25)));
  const shown = filtered.slice((backupWorkspace.page - 1) * 25, backupWorkspace.page * 25);
  bw("bwContent").innerHTML = shown.length ? `<div class="table-wrap"><table class="data-table bw-table"><thead><tr><th>Equipo / sistema</th><th>Copia</th><th>Restauración</th><th>Plan y alcance</th><th><span class="sr-only">Acción</span></th></tr></thead><tbody>${shown.map(backupWorkspaceRow).join("")}</tbody></table></div>` :
    `<div class="bw-empty"><h3>${backupWorkspace.tab === "pending" && !query && !backupWorkspace.agency ? "No hay equipos con pendientes individuales" : "Sin resultados"}</h3><p>${items.length ? "Puedes consultar el resto de los equipos en Todos." : "Crea un plan para empezar a dar seguimiento a tus respaldos."}</p></div>`;
  if (backupWorkspace.tab === "pending") { const pendingPlans = backupDemonstrationPending(); if (pendingPlans) bw("bwContent").insertAdjacentHTML("afterbegin", pendingPlans); }
  bw("bwCount").textContent = `${filtered.length} equipos o controles · Página ${backupWorkspace.page} de ${Math.max(1, Math.ceil(filtered.length / 25))}`;
  bw("bwCount").nextElementSibling.hidden = filtered.length <= 25;
  bw("bwCount").nextElementSibling.querySelector('[data-bw-action="previous"]').disabled = backupWorkspace.page <= 1;
  bw("bwCount").nextElementSibling.querySelector('[data-bw-action="next"]').disabled = backupWorkspace.page * 25 >= filtered.length;
}
function backupCommunalStatus(p) {
  const controls = (state.backupControl?.profiles || []).filter(c => c.deviceId === p.deviceId);
  return `<span class="bw-status muted">Trabajo en carpeta de red</span><small>Sin copia local por equipo</small>${controls.length ? `<small class="bw-communal-warning">${controls.length} control(es) individual(es) registrado(s). Revisa si siguen aplicando.</small><div class="bw-card-actions">${controls.map(c => bwButton(`Ver control · ${c.code || c.systemName}`, "detail", c.id)).join("")}</div>` : ""}`;
}
function renderBackupCommunalList() {
  const query = backupWorkspace.search.toLocaleLowerCase();
  bw("bwSearch").placeholder = "Buscar equipo, usuario o agencia";
  const filtered = backupCommunalItems().filter(p => (!backupWorkspace.agency || p.agencyCode === backupWorkspace.agency) && backupWorkspaceSearchText(p).includes(query));
  filtered.sort((a, b) => a.agencyName.localeCompare(b.agencyName, "es") || a.agencyCode.localeCompare(b.agencyCode) || a.systemName.localeCompare(b.systemName, "es"));
  const pages = Math.max(1, Math.ceil(filtered.length / 25));
  backupWorkspace.page = Math.min(backupWorkspace.page, pages);
  const groups = new Map();
  for (const p of filtered.slice((backupWorkspace.page - 1) * 25, backupWorkspace.page * 25)) {
    if (!groups.has(p.agencyCode)) groups.set(p.agencyCode, { name: p.agencyName, items: [] });
    groups.get(p.agencyCode).items.push(p);
  }
  bw("bwContent").innerHTML = `<div class="bw-communal-intro"><div><h3>Puestos comunales</h3><p>Estos equipos guardan el trabajo en una carpeta de red y no requieren una copia local por equipo. El respaldo y las pruebas de recuperación se controlan sobre el recurso compartido.</p><p>Esta clasificación no confirma que la carpeta esté respaldada.</p></div>${canManageBackupControls() ? bwButton("Clasificar equipo", "communal-picker", "", true) : ""}</div>` +
    (groups.size ? [...groups.values()].map(group => `<section class="bw-communal-group"><h3>${bwEscape(group.name)}</h3><ul>${group.items.map(p => {
      const identity = backupWorkspaceIdentity(p);
      return `<li><div><strong>${bwEscape(p.systemName)}</strong><span title="${bwEscape(identity.userDetail)}">${bwEscape(identity.user)}</span>${identity.userObserved ? `<small>${bwEscape(identity.userObserved)}</small>` : ""}</div><div>${backupCommunalStatus(p)}</div><div>${bwButton(canManageBackupControls() ? "Configurar equipo" : "Ver equipo", "device-policy", p.deviceId)}</div></li>`;
    }).join("")}</ul></section>`).join("") : '<div class="bw-empty"><h3>Sin equipos en esta vista</h3><p>Los activos marcados como Puesto comunal / carpeta de red aparecerán aquí.</p></div>');
  bw("bwCount").textContent = `${filtered.length} puestos comunales · Página ${backupWorkspace.page} de ${pages}`;
  const navigation = bw("bwCount").nextElementSibling;
  navigation.hidden = filtered.length <= 25;
  navigation.querySelector('[data-bw-action="previous"]').disabled = backupWorkspace.page <= 1;
  navigation.querySelector('[data-bw-action="next"]').disabled = backupWorkspace.page >= pages;
}
async function openBackupDevicePolicy(id) {
  await openDevice(id);
  if (typeof selectAssetDetailTab === "function") selectAssetDetailTab("backups");
  document.querySelector("#deviceBackupProvider")?.closest("section")?.scrollIntoView({ block: "center" });
}
function chooseCommunalDevice() {
  const devices = (state.backupControl?.devices || []).filter(d => !backupIsCommunal(d));
  bwOpen("Clasificar un puesto comunal", `<p class="bw-note">Selecciona el activo. En su ficha, elige «Puesto comunal / carpeta de red» y guarda la política. Los controles individuales existentes conservan su historial y seguimiento hasta que los revises.</p>
    <label class="bw-field"><span>Buscar equipo, usuario o agencia</span><input id="bwCommunalSearch" type="search"></label>
    <label class="bw-field bw-communal-select"><span>Equipo</span><select name="device" required></select></label>`, "Abrir configuración", async form => {
      const id = form.get("device");
      if (!devices.some(d => d.id === id)) throw Error("Selecciona un equipo disponible.");
      bw("bwDialog").close(); await openBackupDevicePolicy(id);
    });
  const render = () => {
    const query = bw("bwCommunalSearch").value.toLocaleLowerCase();
    const select = bw("bwForm").elements.device;
    select.innerHTML = '<option value="">Seleccionar equipo…</option>' + devices.filter(d => [d.machineName, d.interactiveUserDisplayName, d.interactiveUserAccount, d.agencyCode].join(" ").toLocaleLowerCase().includes(query)).map(d => `<option value="${bwEscape(d.id)}">${bwEscape(d.machineName)} · ${bwEscape(d.interactiveUserDisplayName || d.interactiveUserAccount || d.agencyCode)}</option>`).join("");
  };
  bw("bwCommunalSearch").addEventListener("input", render); render();
}
function backupWorkspaceIdentity(p) {
  // Use this module's complete device list; the inventory only holds one page.
  const module = state.backupControl || {};
  const device = (module.devices || []).find(d => d.id === p.deviceId);
  const observed = p.deviceId ? observedUserPresentation(device) : null;
  const source = p.gap ? device || {} : p;
  const nas = (module.nas || []).find(n => n.id === source.nasApplianceId);
  const text = value => String(value || "").trim();
  const host = text(source.nasHost) || text(nas?.host);
  const share = text(source.nasShareName) || text(nas?.shareName);
  const copyName = text(source.nasDisplayName) || text(nas?.displayName) ||
    (p.provider !== "ATLAS_BACKUPNAS" ? text(p.taskName) : "");
  const resource = host && share ? `\\\\${host}\\${share}` : share || text(p.expectedDestination);
  const reportedHost = text(device?.lastReportedDestinationHost), reportedShare = text(device?.lastReportedDestinationShare);
  const mismatch = p.provider === "ATLAS_BACKUPNAS" && host && share && reportedHost && reportedShare &&
    (host.toLocaleLowerCase() !== reportedHost.toLocaleLowerCase() || share.toLocaleLowerCase() !== reportedShare.toLocaleLowerCase());
  return {
    user: observed?.primary, userDetail: observed?.secondary,
    userObserved: device?.interactiveUserCheckedAtUtc ? `Observado ${formatRelative(device.interactiveUserCheckedAtUtc)}` : "",
    account: text(device?.interactiveUserAccount),
    copyName: copyName || "Sin nombre registrado", resource: resource || "Sin recurso definido",
    reportedResource: mismatch ? `\\\\${reportedHost}\\${reportedShare}` : ""
  };
}
function backupWorkspaceSearchText(p) {
  const identity = backupWorkspaceIdentity(p);
  return [p.systemName, p.code, p.protectedScope, p.agencyName,
    backupWorkspace.plans.find(plan => plan.id === p.planId)?.definition.name,
    identity.user, identity.account, identity.copyName, identity.resource, identity.reportedResource]
    .join(" ").toLocaleLowerCase();
}
function backupWorkspaceRow(p) {
  if (p.communal) {
    const identity = backupWorkspaceIdentity(p);
    return `<tr><td><strong>${bwEscape(p.systemName)}</strong><span class="cell-secondary">${bwEscape(p.agencyName || p.agencyCode)}</span><dl class="bw-asset-meta"><div><dt>Usuario</dt><dd title="${bwEscape(identity.userDetail)}">${bwEscape(identity.user)}<span class="bw-user-observed">${bwEscape(identity.userObserved)}</span></dd></div></dl></td><td>${backupCommunalStatus(p)}</td><td>Se controla sobre el recurso compartido</td><td>Sin plan de copia individual</td><td>${bwButton(canManageBackupControls() ? "Configurar equipo" : "Ver equipo", "device-policy", p.deviceId)}</td></tr>`;
  }
  const plan = backupWorkspace.plans.find(item => item.id === p.planId);
  const identity = backupWorkspaceIdentity(p);
  const name = `<strong class="cell-primary">${bwEscape(p.systemName)}</strong><span class="cell-secondary">${bwEscape(p.agencyName || p.agencyCode)} · ${bwEscape(backupControlProviderLabels[p.provider] || p.provider)}</span>
    <dl class="bw-asset-meta">${identity.user ? `<div title="${bwEscape(identity.userDetail)}"><dt>Usuario</dt><dd>${bwEscape(identity.user)}${identity.userObserved ? `<span class="bw-user-observed">${bwEscape(identity.userObserved)}</span>` : ""}</dd></div>` : ""}
      <div><dt>Copia</dt><dd>${bwEscape(identity.copyName)}</dd></div>
      <div><dt>Recurso</dt><dd>${bwEscape(identity.resource)}</dd></div>
      ${identity.reportedResource ? `<div class="bw-resource-mismatch"><dt>Reportado</dt><dd>${bwEscape(identity.reportedResource)}</dd></div>` : ""}</dl>`;
  if (p.gap) {
    const run = (state.backupCoverage || []).find(r => r.deviceId === p.deviceId);
    return `<tr><td>${name}</td><td><span class="bw-status muted">Sin seguimiento</span><small>${run?.lastBackupAtUtc ? `Último reporte: ${bwEscape(formatRelative(run.lastBackupAtUtc))}` : "Frecuencia y alcance por definir"}</small></td><td>Por definir</td><td>Sin plan asignado</td><td><div class="bw-row-actions">${canManageBackupControls() ? bwButton("Asignar plan", "assign-device", p.id) + bwButton("Configurar equipo", "device-policy", p.deviceId) : "—"}</div></td></tr>`;
  }
  const copy = backupCopyStatus(p), recovery = backupRecoveryState(p);
  if (p.deviceRetired) return `<tr><td>${name}</td><td><span class="bw-status muted">Equipo desvinculado</span><small>${p.deviceRetiredAtUtc ? `Baja registrada: ${bwEscape(formatDate(p.deviceRetiredAtUtc))}` : "Seguimiento finalizado"}</small><small>${p.lastAcceptableAtUtc ? `Última copia histórica: ${bwEscape(formatDate(p.lastAcceptableAtUtc))}` : "Sin copia aceptable registrada"}</small></td><td>Historial conservado<small>Fuera del seguimiento activo</small></td><td><strong>${bwEscape(plan?.definition.name || "Configuración individual")}</strong>${p.planId ? `<small>Versión histórica ${bwEscape(p.planRevision)}</small>` : ""}<small class="bw-scope">${bwEscape(p.protectedScope)}</small></td><td>${bwButton("Ver historial","detail",p.id)}</td></tr>`;
  if (p.trackingPaused) return `<tr><td>${name}</td><td><span class="bw-status muted">${copy === "PAUSE_REVIEW_DUE" ? "Revisión de pausa vencida" : "Pausado por devolución"}</span><small>${bwEscape(p.pauseReason)}</small><small>Revisión: ${bwEscape(formatDate(p.pauseReviewDueUtc))}</small></td><td>Seguimiento pausado; historial conservado</td><td>${bwEscape(plan?.definition.name || "Configuración individual")}<small>${bwEscape(p.protectedScope)}</small></td><td><div class="bw-row-actions"><button class="secondary-button compact" type="button" data-custody-open="${bwEscape(p.deviceId)}">Revisar custodia</button>${bwButton("Ver actividad","detail",p.id)}</div></td></tr>`;
  const presentation = backupCopyPresentation(p);
  let action = bwButton(presentation.acceptedPending ? "Ver pendientes y motivos" : "Ver actividad", "detail", p.id);
  if (canManageBackupControls() && p.enabled && !p.trackingPaused && !["PENDING_APPROVAL", "INACTIVE"].includes(copy)) {
    if (p.provider !== "ATLAS_BACKUPNAS" && copy !== "COMPLIANT") action = bwButton("Registrar copia", "observation", p.id, true) + action;
    else if (copy === "COMPLIANT" && recovery.pending) action = bwButton("Registrar prueba", "restoration", p.id, true) + action;
  }
  return `<tr><td>${name}</td><td><span class="bw-status ${presentation.tone}">${presentation.acceptedPending ? '<span class="bw-copy-ok" aria-hidden="true">✓</span>' : ""}${bwEscape(presentation.label)}</span>${presentation.pendingLabel ? `<small class="bw-pending-count">${bwEscape(presentation.pendingLabel)} · ver motivos</small>` : ""}<small>${p.lastAcceptableAtUtc ? `Última copia aceptable: ${bwEscape(formatRelative(p.lastAcceptableAtUtc))}${p.lastAcceptableResult === "WARNING" ? " · con avisos" : ""}` : "Sin copia aceptable para este control"}</small><small>${p.latestExecution ? `Última ejecución: ${bwEscape(backupExecutionLabel(p.latestExecution))} · ${bwEscape(formatRelative(p.latestExecution.finishedAtUtc))}` : p.lastObservationSource ? "Comprobación registrada" : "Sin evidencia de ejecución"}</small>${presentation.detail ? `<small class="bw-execution-detail">${bwEscape(presentation.detail)}</small>` : ""}${p.scheduleStatus ? `<small class="bw-schedule-detail">${bwEscape(p.scheduleStatus.detail)}${p.scheduleStatus.waitingForFirstCopy ? " · Primera copia del período pendiente" : ""}</small>` : ""}${p.observationContextStatus && p.observationContextStatus !== "CURRENT" ? `<small class="bw-context-detail">${bwEscape(p.observationContextDetail || (p.observationContextStatus === "CONTEXT_CHANGED" ? "La evidencia corresponde a una configuración anterior; registra evidencia de la actual." : "Evidencia histórica pendiente de validar su contexto."))}</small>` : ""}</td>
    <td><span class="bw-status ${recovery.tone}">${recovery.label}</span><small>${p.lastRestorationAtUtc ? bwEscape(formatDate(p.lastRestorationAtUtc)) : "La copia no demuestra recuperación"}</small></td>
    <td><strong>${bwEscape(plan?.definition.name || "Configuración individual")}</strong>${p.planId ? `<small>Versión ${p.planRevision}${plan && plan.revision > p.planRevision ? " · Nueva versión disponible" : ""}</small>` : ""}<small class="bw-scope" title="${bwEscape(p.protectedScope)}">${bwEscape(p.protectedScope)}</small>${p.scopeOverride ? '<small>Carpetas particulares conservadas</small>' : ""}</td><td><div class="bw-row-actions">${action}</div></td></tr>`;
}
function renderBackupPlanList() {
  const q = backupWorkspace.search.toLocaleLowerCase();
  const plans = backupWorkspace.plans.filter(p => [p.definition.name, ...(p.definition.folders || [])].join(" ").toLocaleLowerCase().includes(q));
  bw("bwContent").innerHTML = backupWorkspace.error ? `<div class="bw-empty">${bwEscape(backupWorkspace.error)}</div>` : plans.length ? `<div class="bw-plan-grid">${plans.map(plan => {
    const d = plan.definition, assigned = (state.backupControl?.profiles || []).filter(p => p.planId === plan.id && !p.deviceRetired);
    return `<article class="bw-plan-card"><div><span class="eyebrow">${bwEscape(backupControlProviderLabels[d.provider] || d.provider)} · V${plan.revision}</span><h3>${bwEscape(d.name)}</h3></div><p>Cada ${d.frequencyHours} h · Demostración cada ${d.restoreTestIntervalDays} días</p><ul>${d.folders.map(f => `<li>${bwEscape(f)}</li>`).join("")}</ul><small>${assigned.length} controles asignados${assigned.some(p => p.planRevision < plan.revision) ? " · Hay controles en una versión anterior" : ""}</small>${backupDemonstrationCard(plan.id)}<div class="bw-card-actions">${canManageBackupControls() ? bwButton("Asignar a equipos", "apply", plan.id, true) + bwButton("Editar plan", "edit-plan", plan.id) : ""}</div></article>`;
  }).join("")}</div>` : `<div class="bw-empty"><h3>${backupWorkspace.loaded ? "Crea tu primer plan" : "Cargando planes…"}</h3><p>Define las carpetas y la frecuencia una vez; después selecciona los equipos.</p></div>`;
  bw("bwCount").textContent = `${plans.length} planes · Los cambios se aplican a los equipos cuando tú los seleccionas.`;
  bw("bwCount").nextElementSibling.hidden = true;
}
function bwOpen(title, content, submitLabel, onSubmit) {
  const dialog = bw("bwDialog");
  if (dialog.open) dialog.close();
  dialog.classList.remove("acronis-dialog");
  dialog.dataset.busy = "false";
  dialog.dataset.submitLocked = "false";
  dialog.innerHTML = `<form id="bwForm"><div class="bw-dialog-heading"><h3 id="bwDialogTitle">${bwEscape(title)}</h3>${bwButton("Cerrar", "close")}</div><div class="bw-form-body">${content}</div><p class="bw-error" id="bwError" role="alert"></p><div class="bw-dialog-footer">${bwButton("Cancelar", "close")}<button class="primary-button" id="bwSubmit" type="submit">${bwEscape(submitLabel)}</button></div></form>`;
  bw("bwForm").addEventListener("submit", async event => {
    event.preventDefault(); if (dialog.dataset.busy === "true") return;
    bw("bwError").textContent = "";
    dialog.dataset.busy = "true";
    dialog.querySelectorAll("button").forEach(b => b.disabled = true);
    try { await onSubmit(new FormData(bw("bwForm"))); }
    catch (error) { if (error.message !== "unauthorized") {
      const message = error.code || error.message || "";
      bw("bwError").textContent = /^[a-z0-9_]+$/i.test(message) ? "No se pudo completar la operación. Se conservan los datos del formulario; revisa los campos o vuelve a consultar antes de reintentar." : message || "No se pudo completar la operación.";
    } }
    finally { dialog.dataset.busy = "false"; dialog.querySelectorAll("button").forEach(b => b.disabled = false); if (dialog.dataset.submitLocked === "true") bw("bwSubmit").disabled = true; }
  });
  dialog.showModal();
}
function bwField(label, name, value = "", type = "text", extra = "") {
  return `<label class="bw-field"><span>${bwEscape(label)}</span><input name="${name}" type="${type}" value="${bwEscape(value)}" ${extra}></label>`;
}
function bwArea(label, name, value = "", extra = "") {
  return `<label class="bw-field bw-wide"><span>${bwEscape(label)}</span><textarea name="${name}" rows="3" ${extra}>${bwEscape(value)}</textarea></label>`;
}
function bwLines(value) { return String(value || "").split(/\r?\n/).map(s => s.trim()).filter(Boolean); }
async function refreshBackupWorkspace() {
  backupWorkspace.loaded = false; backupWorkspace.error = null;
  await Promise.all([loadBackupControl(), loadBackupPlans()]);
  renderBackupWorkspace();
}
function editBackupPlan(id) {
  const plan = backupWorkspace.plans.find(p => p.id === id);
  const d = plan?.definition || { name: "", provider: "ATLAS_BACKUPNAS", folders: [], frequencyHours: 24, graceMinutes: 120, rpoHours: 24, rtoMinutes: "", restoreTestIntervalDays: 90, taskName: "Respaldo diario", processOwner: "", exclusions: "", expectedDestination: "" };
  bwOpen(plan ? `Editar plan · ${d.name}` : "Crear plan de respaldo", `
    <p class="bw-note">El plan define lo que ATLAS espera comprobar. Las carpetas deben coincidir con la configuración de la herramienta de respaldo.</p>
    <div class="bw-form-grid">${bwField("Nombre del plan", "name", d.name, "text", 'required maxlength="160"')}
    <label class="bw-field"><span>Herramienta</span><select name="provider" ${plan ? "disabled" : ""}>${["ATLAS_BACKUPNAS", "ACRONIS", "OTHER"].map(v => `<option value="${v}" ${v === d.provider ? "selected" : ""}>${bwEscape(backupControlProviderLabels[v] || v)}</option>`).join("")}</select></label>
    ${bwArea("Carpetas o ubicaciones que cubre el plan · una por línea", "folders", d.folders.join("\n"), 'required maxlength="4000" placeholder="D:\\Datos de trabajo&#10;E:\\Aplicación\\Exportaciones"')}
    <p class="bw-note bw-wide">Puedes usar rutas completas o carpetas del perfil del usuario. Documentos y Escritorio son opcionales.</p>
    ${bwField("Cada cuántas horas se espera una copia", "frequencyHours", d.frequencyHours, "number", 'required min="1" max="8760"')}
    ${bwField("Demostración de tres muestras cada cuántos días", "restoreTestIntervalDays", d.restoreTestIntervalDays, "number", 'required min="1" max="730"')}
    ${bwField("Responsable del proceso o área", "processOwner", d.processOwner, "text", 'required maxlength="160"')}
    ${bwField("Tiempo objetivo para recuperar · minutos", "rtoMinutes", d.rtoMinutes, "number", 'required min="1" max="10080"')}
    <div class="bw-wide" id="bwDestination">${bwField("Destino esperado", "expectedDestination", d.expectedDestination || "", "text", 'maxlength="500" placeholder="Repositorio o política de Acronis"')}</div>
    <details class="bw-wide"><summary>Otros ajustes del plan</summary><div class="bw-form-grid">
    ${bwField("Nombre de la tarea", "taskName", d.taskName, "text", 'required maxlength="160"')}
    ${bwField("Margen antes de avisar · minutos", "graceMinutes", d.graceMinutes, "number", 'required min="0" max="10080"')}
    ${bwField("Pérdida máxima de información · horas (RPO)", "rpoHours", d.rpoHours, "number", 'required min="1" max="8760"')}
    ${bwArea("Exclusiones, si corresponden", "exclusions", d.exclusions || "", 'maxlength="1000"')}
    </div></details></div><p class="bw-note">Retención institucional: seis meses. Responsable técnico: Coordinador de Ciberseguridad.</p>
    ${plan ? '<p class="bw-note">Guardar crea una nueva versión. Los controles conservan su versión actual hasta que les apliques el cambio.</p>' : ""}`, "Guardar plan", async form => {
      const definition = Object.fromEntries(form); definition.provider = plan ? d.provider : definition.provider;
      definition.folders = bwLines(definition.folders);
      for (const key of ["frequencyHours", "graceMinutes", "rpoHours", "rtoMinutes", "restoreTestIntervalDays"]) definition[key] = Number(definition[key]);
      await api(`/api/v1/admin/backup-controls/plans${plan ? "/" + plan.id : ""}`, backupControlMutationOptions(plan ? "PUT" : "POST", { expectedRevision: plan?.revision || 0, definition }));
      bw("bwDialog").close(); backupWorkspace.tab = "plans"; await refreshBackupWorkspace(); showToast("Plan guardado. Ya puedes asignarlo a los equipos.");
    });
  const provider = bw("bwForm").elements.provider;
  const toggle = () => { const atlas = provider.value === "ATLAS_BACKUPNAS"; bw("bwDestination").hidden = atlas; bw("bwForm").elements.expectedDestination.required = !atlas; };
  provider.addEventListener("change", toggle); toggle();
}
function chooseBackupPlan(targetId, isDevice) {
  const target = isDevice ? state.backupControl.devices.find(d => d.id === targetId) : backupControlProfileById(targetId);
  const provider = isDevice ? target?.backupProvider : target?.provider;
  const plans = backupWorkspace.plans.filter(p => p.definition.provider === provider);
  bwOpen("Seleccionar plan", `<p>Selecciona el plan para ${bwEscape(target?.systemName || target?.machineName)}.</p>${plans.length ? `<label class="bw-field"><span>Plan</span><select name="plan">${plans.map(p => `<option value="${p.id}">${bwEscape(p.definition.name)} · v${p.revision}</option>`).join("")}</select></label>` : '<p>No hay un plan para este proveedor. Crea uno en la pestaña Planes.</p>'}`, "Continuar", async form => {
    if (!form.get("plan")) throw Error("Crea primero un plan para este proveedor.");
    beginBackupPlanApply(form.get("plan"), targetId);
  });
}
function beginBackupPlanApply(planId, selectedId = null) {
  const plan = backupWorkspace.plans.find(p => p.id === planId); if (!plan) return;
  const candidates = backupWorkspaceItems().filter(p => !p.deviceRetired && !p.communal && p.provider === plan.definition.provider && p.exceptionType !== "NORMATIVE");
  let reviewed = false;
  bwOpen(`Asignar · ${plan.definition.name}`, `<p class="bw-note">Versión ${plan.revision} · Cada ${plan.definition.frequencyHours} h · Prueba cada ${plan.definition.restoreTestIntervalDays} días. Al aplicar se activa el seguimiento de los seleccionados.</p>
    <details><summary>Ver alcance y condiciones del plan</summary><pre class="bw-scope-full">${bwEscape(plan.definition.folders.join("\n"))}</pre><p>${bwEscape(plan.definition.exclusions || "Sin exclusiones")}</p><p>Responsable del proceso: ${bwEscape(plan.definition.processOwner)} · RPO ${plan.definition.rpoHours} h · RTO ${plan.definition.rtoMinutes} min · Margen ${plan.definition.graceMinutes} min · Retención seis meses.</p><p>Destino: ${plan.definition.provider === "ATLAS_BACKUPNAS" ? "NAS seleccionado para cada equipo" : bwEscape(plan.definition.expectedDestination)}</p></details>
    <div id="bwApplyChoices"><label class="bw-field"><span>Buscar equipo o agencia</span><input id="bwTargetSearch" type="search"></label><div class="bw-target-list">
    ${candidates.map((p, index) => {
      const sourceDevice = state.backupControl.devices.find(d => d.id === p.deviceId);
      const nasId = p.nasApplianceId || sourceDevice?.nasApplianceId;
      const nasOptions = (state.backupControl.nas || []).filter(n => n.agencyCode === p.agencyCode);
      const folders = p.protectedFolders?.length ? p.protectedFolders.join("\n") : p.protectedScope || plan.definition.folders.join("\n");
      return `<div class="bw-target" data-index="${index}"><label class="bw-target-pick"><input type="checkbox" name="target" value="${index}" ${selectedId === p.id ? "checked" : ""}><span><strong>${bwEscape(p.systemName)}</strong><small>${bwEscape(p.agencyName || p.agencyCode)} · ${p.gap ? "Nuevo seguimiento" : "Conservar historial"}</small></span></label>
      <div class="bw-target-config"><label class="bw-field"><span>Alcance para este equipo</span><select name="scope-${index}">${!p.gap ? '<option value="KEEP">Conservar las carpetas actuales</option>' : ""}<option value="PLAN">Usar las carpetas del plan</option><option value="CUSTOM">Definir carpetas particulares</option></select></label>
      <p class="bw-current-scope">${!p.gap ? bwEscape(p.protectedScope) : "Se usará el alcance del plan."}</p>
      ${bwArea("Carpetas particulares · una por línea", `folders-${index}`, folders, 'maxlength="4000"')}
      ${plan.definition.provider === "ATLAS_BACKUPNAS" ? `<label class="bw-field"><span>NAS autorizado</span><select name="nas-${index}"><option value="">Seleccionar NAS</option>${nasOptions.map(n => `<option value="${n.id}" ${n.id === nasId ? "selected" : ""}>${bwEscape(n.displayName)} · ${bwEscape(n.shareName)}</option>`).join("")}</select></label>` : ""}</div></div>`;
    }).join("") || '<p>No hay equipos de este proveedor disponibles para asignar.</p>'}</div></div><div id="bwApplyReview" hidden></div>`, "Revisar selección", async form => {
      const targets = form.getAll("target").map(value => {
        const index = Number(value), p = candidates[index];
        return { profileId: p.gap ? null : p.id, deviceId: p.gap ? p.deviceId : null, expectedUpdatedAtUtc: p.gap ? null : p.updatedAtUtc,
          scopeMode: form.get(`scope-${index}`), folders: bwLines(form.get(`folders-${index}`)), nasApplianceId: form.get(`nas-${index}`) || null };
      });
      if (!targets.length) throw Error("Selecciona al menos un equipo.");
      if (targets.some(t => t.scopeMode === "CUSTOM" && !t.folders.length)) throw Error("Completa las carpetas particulares de los equipos seleccionados.");
      if (plan.definition.provider === "ATLAS_BACKUPNAS" && targets.some(t => !t.nasApplianceId)) throw Error("Selecciona el NAS autorizado para cada equipo.");
      if (!reviewed) {
        reviewed = true; bw("bwApplyChoices").hidden = true; bw("bwApplyReview").hidden = false;
        bw("bwApplyReview").innerHTML = `<h4>Se aplicará a ${targets.length} equipos o controles</h4><ul>${form.getAll("target").map(value => { const i = Number(value), p = candidates[i], mode = form.get(`scope-${i}`); return `<li><strong>${bwEscape(p.systemName)}</strong> · ${mode === "KEEP" ? "Conservar alcance actual" : mode === "CUSTOM" ? "Carpetas particulares" : "Alcance del plan"}<pre class="bw-scope-full">${bwEscape(mode === "KEEP" ? p.protectedScope : mode === "CUSTOM" ? form.get(`folders-${i}`) : plan.definition.folders.join("\n"))}</pre></li>`; }).join("")}</ul>${bwButton("Volver a la selección", "review-back")}`;
        bw("bwSubmit").textContent = "Aplicar y activar seguimiento";
        bw("bwApplyReview").querySelector("button").onclick = () => { reviewed = false; bw("bwApplyChoices").hidden = false; bw("bwApplyReview").hidden = true; bw("bwSubmit").textContent = "Revisar selección"; };
        return;
      }
      const response = await api(`/api/v1/admin/backup-controls/plans/${planId}/apply`, backupControlMutationOptions("POST", { revision: plan.revision, targets }));
      bw("bwDialog").close(); await refreshBackupWorkspace(); showToast(`Seguimiento actualizado en ${response.applied} controles.`);
    });
  const updateChoices = () => {
    bw("bwApplyChoices").querySelectorAll(".bw-target").forEach(row => {
      const i = row.dataset.index, selected = row.querySelector('[name="target"]').checked;
      row.querySelector(".bw-target-config").hidden = !selected;
      const mode = bw("bwForm").elements[`scope-${i}`].value;
      bw("bwForm").elements[`folders-${i}`].closest("label").hidden = mode !== "CUSTOM";
      row.querySelector(".bw-current-scope").hidden = mode === "CUSTOM";
      if (mode === "PLAN") row.querySelector(".bw-current-scope").textContent = plan.definition.folders.join(" · ");
      if (mode === "KEEP") row.querySelector(".bw-current-scope").textContent = candidates[Number(i)].protectedScope;
    });
  };
  bw("bwApplyChoices").addEventListener("change", updateChoices); updateChoices();
  bw("bwTargetSearch").addEventListener("input", event => {
    const q = event.target.value.toLocaleLowerCase();
    bw("bwApplyChoices").querySelectorAll(".bw-target").forEach(row => { const p = candidates[Number(row.dataset.index)]; row.hidden = ![p.systemName, p.agencyName, p.agencyCode].join(" ").toLocaleLowerCase().includes(q); });
  });
}
function beginBackupActivity(id, kind) {
  const p = backupControlProfileById(id); if (!p) return;
  if (kind === "RESTORATION" && p.planId) { void beginBackupDemonstration(p.planId); return; }
  const restoration = kind === "RESTORATION", now = formatDateTimeLocal(new Date().toISOString());
  const requestId = (typeof atlasRequestId === "function" ? atlasRequestId() : crypto.randomUUID());
  bwOpen(`${restoration ? "Registrar prueba de restauración" : "Registrar resultado de copia"} · ${p.systemName}`, `
    <div class="bw-form-grid"><label class="bw-field"><span>Resultado</span><select name="result"><option value="SUCCESS">Satisfactorio</option><option value="WARNING">Con advertencias</option><option value="FAILED">Fallido</option></select></label>
    ${bwField(restoration ? "Inicio de la prueba" : "Inicio del período comprobado", "started", now, "datetime-local", 'required')}
    ${bwField(restoration ? "Fin de la prueba" : "Fin del período comprobado", "finished", now, "datetime-local", 'required')}
    ${restoration ? `<label class="bw-field"><span>Tipo de restauración</span><select name="modality"><option value="FILE">Archivos</option><option value="APPLICATION">Aplicación</option><option value="SYSTEM">Sistema</option><option value="HOT_RECOVERY">Recuperación en caliente</option></select></label>
      ${bwArea("Qué se restauró y comprobó", "testedScope", "", 'required maxlength="8000"')}
      ${bwField("Ejecutado por", "executedBy", state.session?.displayName || "", "text", 'required maxlength="160"')}
      ${bwField("Fecha de la copia utilizada", "backupUsed", "", "datetime-local")}
      <label class="bw-check"><input type="checkbox" name="integrity"> Comprobé la integridad de lo restaurado</label><label class="bw-check"><input type="checkbox" name="functionality"> Comprobé que puede utilizarse correctamente</label>
      <details class="bw-wide"><summary>Validación adicional y datos de recuperación</summary><div class="bw-form-grid">
      ${bwField("Validado por, si corresponde", "validatedBy", "", "text", 'maxlength="160"')}${bwField("Fecha de validación", "validatedAt", "", "datetime-local")}
      ${bwField("Duración real de recuperación · minutos", "rtoAchieved", "", "number", 'min="0" max="10080"')}
      ${bwField("Próxima prueba, si tiene una fecha específica", "nextDue", "", "datetime-local")}</div></details>` : bwField("Referencia del informe o trabajo · opcional", "reference", "", "text", 'maxlength="255"')}
      ${bwArea(restoration ? "Resultado, observaciones y acciones pendientes" : "Descripción del resultado", "notes", "", `maxlength="${restoration ? 4000 : 2000}"`)}
      <label class="bw-field bw-wide"><span>Adjuntar evidencia · opcional</span><input type="file" name="evidence" accept=".pdf,.docx,.xlsx,.png,.jpg,.jpeg,.csv,.txt,.log"><small>Informe, imagen o log; máximo 25 MB. El resultado y el archivo se guardan juntos.</small></label>
    </div>`, "Guardar registro", async form => {
      const iso = key => form.get(key) ? new Date(form.get(key)).toISOString() : null;
      const result = form.get("result");
      if (restoration && result === "SUCCESS" && (!form.has("integrity") || !form.has("functionality"))) throw Error("Para una prueba satisfactoria, comprueba la integridad y el funcionamiento.");
      const request = { requestId, kind, observation: restoration ? null : {
        source: p.provider === "ACRONIS" ? "ACRONIS_EVIDENCE" : "MANUAL", result,
        periodStartUtc: iso("started"), periodEndUtc: iso("finished"), startedAtUtc: null, finishedAtUtc: null,
        externalReference: form.get("reference") || null, detail: form.get("notes") || null
      }, restoration: restoration ? {
        modality: form.get("modality"), result, operationalCaseId: null, backupUsedAtUtc: iso("backupUsed"),
        startedAtUtc: iso("started"), finishedAtUtc: iso("finished"), testedScope: form.get("testedScope"),
        integrityVerified: form.has("integrity"), functionalityVerified: form.has("functionality"),
        rtoAchievedMinutes: form.get("rtoAchieved") ? Number(form.get("rtoAchieved")) : null,
        executedBy: form.get("executedBy"), validatedBy: form.get("validatedBy") || null, validatedAtUtc: iso("validatedAt"),
        notes: form.get("notes") || null, historical: false, nextTestDueUtc: iso("nextDue")
      } : null };
      const body = new FormData(); body.set("record", JSON.stringify(request));
      const file = bw("bwForm").elements.evidence.files[0];
      if (file) { if (!file.size || file.size > 25_000_000) throw Error("El archivo debe tener contenido y no superar 25 MB."); body.set("file", file); }
      await api(`/api/v1/admin/backup-controls/profiles/${id}/activity`, { method: "POST", headers: { "X-Atlas-Request": "dashboard" }, body });
      bw("bwDialog").close(); await loadBackupControl(id, true); showToast("Registro guardado con su evidencia.");
    });
}
function renderBackupActivityDetail(detail) {
  const p = detail.profile, body = elements.backupControlDetailBody;
  const plan = backupWorkspace.plans.find(item => item.id === p.planId);
  const presentation = backupCopyPresentation(p);
  const latestAcceptedPending = p.latestExecutionAssessment?.code === "ACCEPTABLE_WITH_PENDING_FILES" && p.latestExecutionAssessment.acceptableForControl;
  const acceptedPending = backupAcceptedPendingAssessment(p);
  const top = document.createElement("div"); top.className = "bw-activity-top";
  top.innerHTML = `<div><strong>${bwEscape(plan?.definition.name || "Configuración individual")}${p.planId ? ` · versión ${p.planRevision}` : ""}</strong><p>${p.deviceRetired ? "Equipo desvinculado. Consulta de historial; fuera del seguimiento activo." : p.provider === "ATLAS_BACKUPNAS" ? "ATLAS compara las carpetas y el destino reportados con este control. La restauración se comprueba por separado." : "El control conserva las comprobaciones y evidencias registradas. La restauración se comprueba por separado."}</p></div>
    <p><span class="bw-status ${presentation.tone}">${presentation.acceptedPending ? '<span class="bw-copy-ok" aria-hidden="true">✓</span>' : ""}${bwEscape(presentation.label)}</span>${p.lastAcceptableAtUtc ? ` · Última copia aceptable: ${bwEscape(formatDate(p.lastAcceptableAtUtc))}` : ""}</p>
    ${acceptedPending ? backupPendingFilesDetail(acceptedPending) : ""}
    ${p.lastObservationSource === "AGENT" ? `<div class="bw-auto-evidence backup-result-note${latestAcceptedPending ? " bw-copy-acceptable-pending" : ""}" data-result="${bwEscape(p.lastObservationResult)}"><strong>Informe automático recibido · ${bwEscape(latestAcceptedPending ? `Aceptable · ${backupPendingFilesLabel(p.latestExecutionAssessment)}` : p.latestExecution ? backupExecutionLabel(p.latestExecution) : backupControlResultLabels[p.lastObservationResult] || p.lastObservationResult)}</strong><span>${bwEscape(formatDate(p.lastObservationAtUtc))}</span>${p.latestExecutionAssessment ? `<small>${bwEscape(p.latestExecutionAssessment.detail)}</small>` : ""}<small>${bwEscape(p.complianceDetail || "Consulte resultado, antigüedad y destino.")} El reporte de ejecución no acredita una restauración comprobada.</small>${p.latestExecutionAssessment?.missingExpectedFolders?.length ? `<p>Carpetas esperadas sin acreditar:</p><ul>${p.latestExecutionAssessment.missingExpectedFolders.map(f => `<li>${bwEscape(f)}</li>`).join("")}</ul>` : ""}<details><summary>Referencia técnica</summary>${bwEscape(p.lastExternalReference)}</details></div>` : ""}
    <div class="bw-card-actions">${canManageBackupControls() && !p.deviceRetired ?
      (p.provider !== "ATLAS_BACKUPNAS" ? bwButton("Registrar copia", "observation", p.id) : "") + (p.planId ? bwButton("Demostraciones del plan", "demonstrations", p.planId) : bwButton("Registrar prueba", "restoration", p.id)) + bwButton(p.planId ? "Plan y carpetas" : "Asignar plan", "assign-profile", p.id) + bwButton("Configuración individual", "edit-profile", p.id) + bwButton("Adjuntar documento", "evidence", p.id) +
      (p.enabled && !["ACTIVE", "APPROVED"].includes(p.approvalStatus) ? bwButton("Activar seguimiento", "activate", p.id) : "") : ""}</div>`;
  body.prepend(top);
  if (detail.automaticReports?.length) {
    const section = document.createElement("section"); section.className = "bw-automatic-history";
    section.innerHTML = `<h4>Informes automáticos de Atlas Backup · últimas ${detail.automaticReports.length} ejecuciones</h4><ul>${detail.automaticReports.map(backupExecutionHistory).join("")}</ul>`;
    body.append(section);
  }
}
async function backupWorkspaceAction(button) {
  const id = button.dataset.id;
  switch (button.dataset.bwAction) {
    case "copy-filter": showBackupCopyFilter(id); break;
    case "clear-copy-filter": {
      const previous = backupWorkspace.copyFilter;
      backupWorkspace.copyFilter = ""; backupWorkspace.page = 1; renderBackupWorkspace();
      document.querySelector(`[data-bw-action="copy-filter"][data-id="${previous}"]`)?.focus({ preventScroll: true });
      break;
    }
    case "acronis-detail": await openAcronisImport(id); break;
    case "acronis-supplement": if (canManageBackupControls()) await openAcronisImport(id, true); break;
    case "acronis-refresh": await loadAcronisImports(); break;
    case "close": if (bw("bwDialog").dataset.busy !== "true") bw("bwDialog").close(); break;
    case "previous": backupWorkspace.page--; renderBackupWorkspaceContent(); break;
    case "next": backupWorkspace.page++; renderBackupWorkspaceContent(); break;
    case "detail": if (bw("bwDialog").open) bw("bwDialog").close(); await openBackupControlDetail(id); break;
    case "demonstrations": await openBackupDemonstrations(id); break;
    case "device-policy": await openBackupDevicePolicy(id); break;
    case "context-refresh": if (typeof renderBackupContextReview === "function") await renderBackupContextReview(true); break;
    case "schedules-refresh": if (typeof renderBackupSchedules === "function") await renderBackupSchedules(true); break;
    default:
      if (!canManageBackupControls()) return;
      switch (button.dataset.bwAction) {
        case "acronis-upload": beginAcronisUpload(); break;
        case "acronis-select": bw("bwForm").querySelectorAll('[name="sourceRows"]').forEach(c => c.checked = true); break;
        case "acronis-clear": bw("bwForm").querySelectorAll('[name="sourceRows"]').forEach(c => c.checked = false); break;
        case "individual": beginBackupControlEdit(); break;
        case "communal-picker": chooseCommunalDevice(); break;
        case "edit-profile":
          if (backupControlProfileById(id)?.planId) {
            bwOpen("Configuración individual", "<p>Los datos administrativos pueden conservar la vinculación al plan. Si cambias equipo, proveedor, tarea, destino, alcance o condiciones de seguimiento, el control pasará a configuración individual y conservará su historial. Para aplicar una versión del plan o sus carpetas, usa Plan y carpetas.</p>", "Abrir configuración", async () => { bw("bwDialog").close(); beginBackupControlEdit(id); });
          } else beginBackupControlEdit(id); break;
        case "new-demonstration": await beginBackupDemonstration(id); break;
        case "continue-demonstration": await beginBackupDemonstration(id, button.dataset.session); break;
        case "retry-demonstration": await beginBackupDemonstration(id, button.dataset.session, button.dataset.attempt); break;
        case "context-confirm": if (typeof confirmBackupContextReview === "function") confirmBackupContextReview(); break;
        case "new-plan": editBackupPlan(); break;
        case "edit-plan": editBackupPlan(id); break;
        case "apply": beginBackupPlanApply(id); break;
        case "assign-device": chooseBackupPlan(id, true); break;
        case "assign-profile": {
          const p = backupControlProfileById(id); if (p?.planId) beginBackupPlanApply(p.planId, id); else chooseBackupPlan(id, false); break;
        }
        case "observation": beginBackupActivity(id, "OBSERVATION"); break;
        case "restoration": beginBackupActivity(id, "RESTORATION"); break;
        case "evidence": beginBackupEvidence(id); break;
        case "activate": await activateBackupControl(id, button); break;
      }
  }
}

ensureBackupWorkspace();
if (state.session) renderBackupWorkspace();
