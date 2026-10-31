"use strict";

// Calendars and historical context reviews administer control metadata only.
const backupSchedules = { data: null, loading: false, request: 0 };
const backupContextReview = { data: null, loading: false, selected: new Set(), request: 0 };
const backupWeekdays = [[1,"Lunes"],[2,"Martes"],[3,"Miércoles"],[4,"Jueves"],[5,"Viernes"],[6,"Sábado"],[7,"Domingo"]];
const backupScheduleModeLabels = { INHERIT: "Heredar configuración", CALENDAR: "Días de trabajo confirmados", LEGACY: "Frecuencia por horas" };
function backupWorkspaceFooter(text) {
  bw("bwCount").textContent = text;
  bw("bwCount").nextElementSibling.hidden = true;
}
function backupScheduleDescription(setting) {
  if (!setting || setting.mode === "INHERIT") return "Sin calendario propio. Se revisa la configuración heredada; si no existe, se conserva la frecuencia por horas.";
  if (setting.mode === "LEGACY") return "Frecuencia por horas declarada en el control. No se deducen días laborales.";
  const days = backupWeekdays.filter(([id]) => (setting.weekdays || []).includes(id)).map(([,label]) => label).join(", ");
  return `${days} · día completo en ${setting.timeZone}${setting.validFrom ? ` · ${setting.validFrom} a ${setting.validUntil}` : ""}${setting.excludedDates?.length ? ` · ${setting.excludedDates.length} fechas excluidas` : ""}`;
}
async function renderBackupSchedules(force = false) {
  if (backupWorkspace.tab !== "schedules") return;
  if (force) backupSchedules.data = null;
  if (!backupSchedules.data) {
    bw("bwContent").innerHTML = '<div class="bw-empty" role="status">Consultando calendarios…</div>';
    backupWorkspaceFooter("Calendarios operativos");
    if (backupSchedules.loading) return;
    backupSchedules.loading = true; const request = ++backupSchedules.request;
    try {
      const data = await api("/api/v1/admin/backup-controls/schedules");
      if (request !== backupSchedules.request) return;
      if (![data.agencies,data.plans,data.profiles].every(Array.isArray)) throw Error("No se recibió un catálogo de calendarios válido.");
      backupSchedules.data = data;
    } catch (error) {
      if (backupWorkspace.tab === "schedules") bw("bwContent").innerHTML = `<div class="source-status source-error" role="status"><div><strong>Calendarios pendientes de consulta</strong><p>No se interpreta esta interrupción como ausencia de calendarios. Vuelve a consultar.</p></div>${bwButton("Volver a consultar","schedules-refresh")}</div>`;
      return;
    } finally { backupSchedules.loading = false; }
  }
  if (backupWorkspace.tab !== "schedules") return;
  const query = backupWorkspace.search.toLocaleLowerCase(), data = backupSchedules.data;
  bw("bwContent").innerHTML = `<div class="bw-calendar-intro"><h3>Calendarios de seguimiento</h3><p>Se usan días completos de la zona indicada: una copia de hoy no vence a las 17:00. Primero aplica una agenda individual vigente; después el plan y la agencia. Los días no se suponen ni se deducen de la conexión de un equipo.</p><p>Sin calendario confirmado se conserva la frecuencia por horas del control. Las ausencias particulares necesitan fechas de inicio y revisión; no cambian el historial.</p>${bwButton("Actualizar calendarios","schedules-refresh")}</div>` +
    [["agencies","Agencias"],["plans","Planes"],["profiles","Controles individuales"]].map(([kind,title]) => {
      const rows = data[kind].filter(row => [row.label,row.agencyCode].join(" ").toLocaleLowerCase().includes(query));
      return `<section class="bw-calendar-section"><h4>${title}</h4>${rows.length ? `<div class="table-wrap"><table class="data-table"><thead><tr><th>Ámbito</th><th>Configuración propia</th><th>Detalle</th><th>Acción</th></tr></thead><tbody>${rows.map(row => `<tr><td><strong>${bwEscape(row.label)}</strong>${row.agencyCode ? `<small class="cell-secondary">${bwEscape(row.agencyCode)}</small>` : ""}</td><td>${bwEscape(backupScheduleModeLabels[row.setting?.mode] || "Por confirmar")}</td><td>${bwEscape(backupScheduleDescription(row.setting))}${row.setting?.reason ? `<small class="cell-secondary">${bwEscape(row.setting.reason)}</small>` : ""}${row.setting?.appliesFromUtc ? `<small class="cell-secondary">Registrado ${bwEscape(formatDate(row.setting.appliesFromUtc))}</small>` : ""}</td><td>${canManageBackupControls() ? `<button type="button" class="secondary-button compact" data-calendar-kind="${kind}" data-calendar-key="${bwEscape(row.key)}">Configurar</button>` : "Consulta"}</td></tr>`).join("")}</tbody></table></div>` : '<p class="bw-note">Sin coincidencias en este ámbito.</p>'}</section>`;
    }).join("");
  if (canManageBackupControls() && data.agencies.length) {
    const bulk = document.createElement("button"); bulk.type = "button"; bulk.className = "primary-button";
    bulk.textContent = "Aplicar lunes a sábado a todas las agencias"; bulk.dataset.calendarInstitutional = "true";
    bulk.addEventListener("click", applyInstitutionalBackupCalendar);
    bw("bwContent").querySelector(".bw-calendar-intro").append(bulk);
  }
  bw("bwContent").querySelectorAll("[data-calendar-key]").forEach(button => button.addEventListener("click", () => editBackupSchedule(button.dataset.calendarKind,button.dataset.calendarKey)));
  backupWorkspaceFooter(`Consulta ${formatDate(data.generatedAtUtc)} · Las obligaciones efectivas se muestran en cada control`);
}
function applyInstitutionalBackupCalendar() {
  if (!canManageBackupControls() || !backupSchedules.data?.agencies.length) return;
  const agencies = backupSchedules.data.agencies;
  bwOpen("Calendario institucional · lunes a sábado", `<p>Aplicarás lunes a sábado, con el día completo en America/Guatemala, a ${agencies.length} agencias. El domingo no crea una obligación nueva. Se conservan las fechas excluidas y las agendas propias de planes o controles individuales.</p><p>Los nuevos calendarios comienzan a contar desde su registro. No convierten un día anterior en incumplimiento ni acreditan una copia.</p><ul>${agencies.map(row=>`<li>${bwEscape(row.label)}</li>`).join("")}</ul>${bwArea("Motivo del calendario institucional","reason","Calendario confirmado para todas las agencias: lunes a sábado, con el día local completo.",'required minlength="10" maxlength="500"')}`, "Aplicar calendario a las agencias", async form => {
    let saved = 0, unchanged = 0;
    for (const row of agencies) {
      const current = row.setting || {};
      if (current.mode === "CALENDAR" && current.timeZone === "America/Guatemala" && [1,2,3,4,5,6].every(d=>current.weekdays?.includes(d)) && current.weekdays.length === 6) { unchanged++; continue; }
      const body = { expectedRevision:row.revision,mode:"CALENDAR",timeZone:"America/Guatemala",weekdays:[1,2,3,4,5,6],excludedDates:current.excludedDates || [],reason:form.get("reason") };
      try {
        await api(`/api/v1/admin/backup-controls/schedules/agencies/${encodeURIComponent(row.key)}`,backupControlMutationOptions("PUT",body));
        row.revision++; row.setting = body; saved++;
      } catch {
        backupSchedules.data = null; bw("bwDialog").dataset.submitLocked = "true";
        throw Error(`Se confirmaron ${saved} cambios y ${unchanged} calendarios ya coincidían. No se confirmó el resto del lote. Cierra y actualiza calendarios para revisar el resultado antes de repetir; los cambios guardados se conservan.`);
      }
    }
    bw("bwDialog").close(); backupSchedules.data = null; await loadBackupControl(); await renderBackupSchedules();
    showToast(`Calendario lunes a sábado: ${saved} agencias actualizadas; ${unchanged} ya coincidían. Las agendas individuales se conservan.`);
  });
}
function editBackupSchedule(kind,key) {
  if (!canManageBackupControls()) return;
  const row = backupSchedules.data?.[kind]?.find(item => item.key === key); if (!row) return;
  const setting = row.setting || {}, individual = kind === "profiles";
  bwOpen(`Calendario · ${row.label}`, `<p class="bw-note">Configura cuándo corresponde una copia. Cada día seleccionado termina a medianoche en la zona indicada. Esto no programa ni ejecuta la herramienta de respaldo y no acredita una copia por sí mismo.</p>
    <div class="bw-form-grid"><label class="bw-field bw-wide"><span>Modo de seguimiento</span><select name="mode">${Object.entries(backupScheduleModeLabels).map(([value,label]) => `<option value="${value}"${value === (setting.mode || "INHERIT") ? " selected" : ""}>${label}</option>`).join("")}</select></label>
    ${bwField("Zona horaria","timeZone",setting.timeZone || "America/Guatemala","text",'maxlength="100"')}
    <fieldset class="bw-weekday-choice bw-wide"><legend>Días de trabajo confirmados</legend>${backupWeekdays.map(([day,label]) => `<label><input type="checkbox" name="weekday" value="${day}"${(setting.weekdays || []).includes(day) ? " checked" : ""}><span>${label}</span></label>`).join("")}</fieldset>
    ${bwArea("Fechas excluidas confirmadas · una por línea (AAAA-MM-DD)","excludedDates",(setting.excludedDates || []).join("\n"),'maxlength="4026" placeholder="AAAA-MM-DD"')}
    ${individual ? bwField("Agenda individual desde","validFrom",setting.validFrom || "","date") + bwField("Revisar / finalizar agenda individual","validUntil",setting.validUntil || "","date") + '<p class="bw-note bw-wide">Al finalizar esta agenda se vuelve a evaluar la configuración heredada. No se marca automáticamente como ausencia ni como copia realizada.</p>' : ""}
    ${bwArea("Motivo y confirmación del calendario","reason",setting.reason || "",'required minlength="5" maxlength="500"')}
    <p class="bw-note bw-wide">Heredar no establece días nuevos. La frecuencia por horas usa los valores ya declarados en el control. Las fechas excluidas deben confirmarse; no se cargan feriados automáticamente.</p></div>`,"Guardar calendario",async form => {
      const mode = form.get("mode"), weekdays = form.getAll("weekday").map(Number), dates = String(form.get("excludedDates") || "").split(/\r?\n/).map(v=>v.trim()).filter(Boolean);
      if (mode === "CALENDAR" && !weekdays.length) throw Error("Selecciona al menos un día confirmado de trabajo.");
      if (mode === "CALENDAR" && dates.some(v=>!/^\d{4}-\d{2}-\d{2}$/.test(v))) throw Error("Indica cada fecha excluida con el formato AAAA-MM-DD.");
      if (individual && mode === "CALENDAR" && (!form.get("validFrom") || !form.get("validUntil"))) throw Error("Indica el inicio y el final de la agenda individual.");
      const body = { expectedRevision:row.revision,mode,timeZone:form.get("timeZone"),weekdays:mode === "CALENDAR" ? weekdays : [],excludedDates:mode === "CALENDAR" ? dates : [],validFrom:individual && mode === "CALENDAR" ? form.get("validFrom") : null,validUntil:individual && mode === "CALENDAR" ? form.get("validUntil") : null,reason:form.get("reason") };
      try { await api(`/api/v1/admin/backup-controls/schedules/${kind}/${encodeURIComponent(key)}`,backupControlMutationOptions("PUT",body)); }
      catch (error) { if(error.status === 409) throw Error("El calendario cambió desde que lo abriste. Cierra esta ventana y vuelve a consultar antes de guardar."); throw error; }
      bw("bwDialog").close(); backupSchedules.data = null; await loadBackupControl(); await renderBackupSchedules(); showToast("Calendario registrado. Se conserva la fecha de las evidencias existentes.");
    });
  const form = bw("bwForm"), sync = () => {
    const calendar = form.elements.mode.value === "CALENDAR";
    form.querySelector(".bw-weekday-choice").disabled = !calendar;
    form.elements.timeZone.required = calendar;
    form.elements.excludedDates.disabled = !calendar;
    if (individual) { form.elements.validFrom.required = calendar; form.elements.validUntil.required = calendar; form.elements.validFrom.disabled = !calendar; form.elements.validUntil.disabled = !calendar; }
  };
  form.elements.mode.addEventListener("change",sync); sync();
}
function backupContextSourceLabel(source) {
  return ({ ACRONIS_EVIDENCE: "Informe de Acronis", MANUAL: "Comprobación manual", AGENT: "Reporte del agente", RESTORATION: "Prueba de restauración" })[source] || "Fuente histórica registrada";
}
function backupContextSummary(context) {
  const destination = typeof context?.destination === "string" ? context.destination : [context?.destination?.host,context?.destination?.share].filter(Boolean).join(" / ");
  return `<dl class="bw-context-definition"><dt>Proveedor / tarea</dt><dd>${bwEscape(backupControlProviderLabels[context?.provider] || context?.provider || "Por confirmar")} · ${bwEscape(context?.task || "Sin tarea declarada")}</dd><dt>Destino actual</dt><dd>${bwEscape(destination || "Sin destino declarado")}</dd><dt>Carpetas actuales</dt><dd>${context?.folders?.length ? context.folders.map(bwEscape).join("<br>") : bwEscape(context?.unstructuredScope || "Alcance sin lista de carpetas")}</dd><dt>Exclusiones</dt><dd>${bwEscape(context?.exclusions || "Sin exclusiones declaradas")}</dd></dl>`;
}
async function renderBackupContextReview(force = false) {
  if (backupWorkspace.tab !== "context") return;
  if (force) { backupContextReview.data = null; backupContextReview.selected.clear(); }
  if (!backupContextReview.data) {
    bw("bwContent").innerHTML = '<div class="bw-empty" role="status">Consultando evidencias históricas pendientes de contexto…</div>';
    backupWorkspaceFooter("Revisión de evidencias existentes");
    if (backupContextReview.loading) return;
    backupContextReview.loading = true; const request = ++backupContextReview.request;
    try {
      const data = await api("/api/v1/admin/backup-controls/context-reviews");
      if (request !== backupContextReview.request) return;
      if (!Array.isArray(data.items)) throw Error("Catálogo de revisión no disponible.");
      backupContextReview.data = data;
    } catch { if (backupWorkspace.tab === "context") bw("bwContent").innerHTML = `<div class="source-status source-error" role="status"><div><strong>Revisión pendiente de consulta</strong><p>La interrupción no confirma que todas las evidencias tengan contexto validado.</p></div>${bwButton("Volver a consultar","context-refresh")}</div>`; return; }
    finally { backupContextReview.loading = false; }
  }
  if (backupWorkspace.tab !== "context") return;
  const query = backupWorkspace.search.toLocaleLowerCase(), rows = backupContextReview.data.items.filter(row=>[row.systemName,row.code,row.agencyCode,row.detail].join(" ").toLocaleLowerCase().includes(query));
  const identity = row=>`${row.kind}:${row.recordId}`;
  bw("bwContent").innerHTML = `<div class="bw-calendar-intro"><h3>Revisar el contexto de evidencia anterior</h3><p>Estas copias o restauraciones ya están registradas. Una revisión documenta si corresponden a la configuración actual; conserva su fecha original y no crea una ejecución nueva ni renueva su vigencia.</p><p>Si las carpetas, el destino o el equipo son distintos, registra evidencia nueva en el control correspondiente. No selecciones registros cuyo contexto no hayas comprobado.</p><div class="bw-card-actions">${bwButton("Actualizar lista","context-refresh")}${canManageBackupControls() ? bwButton("Revisar selección","context-confirm","",true) : ""}</div></div>` + (rows.length ? `<div class="table-wrap"><table class="data-table bw-context-table"><thead><tr><th>Seleccionar</th><th>Equipo / control</th><th>Evidencia conservada</th><th>Configuración actual a contrastar</th></tr></thead><tbody>${rows.map(row=>`<tr><td><input type="checkbox" data-context-select="${bwEscape(identity(row))}" aria-label="Revisar ${row.kind === "RESTORATION" ? "restauración" : "copia"} de ${bwEscape(row.systemName)}"${backupContextReview.selected.has(identity(row)) ? " checked" : ""}${canManageBackupControls() ? "" : " disabled"}></td><td><strong>${bwEscape(row.systemName)}</strong><small class="cell-secondary">${bwEscape(row.agencyCode)} · ${bwEscape(row.code)}</small></td><td><strong>${row.kind === "RESTORATION" ? "Restauración" : "Copia"} · satisfactoria registrada</strong><small class="cell-secondary">${bwEscape(formatDate(row.observedAtUtc || row.periodEndUtc))} · ${bwEscape(backupContextSourceLabel(row.source))}</small><p>${bwEscape(row.detail || "Sin detalle adicional")}</p>${row.externalReference ? `<small class="cell-secondary">Referencia: ${bwEscape(row.externalReference)}</small>` : ""}</td><td>${backupContextSummary(row.currentContext)}</td></tr>`).join("")}</tbody></table></div>` : '<div class="bw-empty"><h3>Sin evidencias históricas para esta revisión</h3><p>Los registros cuyo contexto cambió necesitan evidencia nueva. Consulta la actividad de cada control.</p></div>');
  bw("bwContent").querySelectorAll("[data-context-select]").forEach(input=>input.addEventListener("change",()=>{input.checked ? backupContextReview.selected.add(input.dataset.contextSelect) : backupContextReview.selected.delete(input.dataset.contextSelect);backupWorkspaceFooter(`${backupContextReview.selected.size} seleccionados · ${rows.length} coincidencias de ${backupContextReview.data.count}`);}));
  backupWorkspaceFooter(`${backupContextReview.selected.size} seleccionados · ${rows.length} coincidencias de ${backupContextReview.data.count}`);
}
function confirmBackupContextReview() {
  if (!canManageBackupControls()) return;
  const rows = (backupContextReview.data?.items || []).filter(row=>backupContextReview.selected.has(`${row.kind}:${row.recordId}`));
  if (!rows.length) { showToast("Selecciona las evidencias cuyo contexto comprobaste."); return; }
  if (rows.length > 500) { showToast("Revisa hasta 500 registros por lote."); return; }
  let pending = null;
  bwOpen("Confirmar revisión de contexto histórico",`<p class="bw-note">Vas a validar el contexto de ${rows.length} evidencias existentes. Las fechas originales se conservan. No se registra una copia o restauración nueva.</p><ul class="bw-context-review">${rows.map(row=>`<li><strong>${bwEscape(row.systemName)} · ${row.kind === "RESTORATION" ? "Restauración" : "Copia"}</strong><small>${bwEscape(formatDate(row.observedAtUtc || row.periodEndUtc))} · ${bwEscape(backupContextSourceLabel(row.source))}</small>${backupContextSummary(row.currentContext)}</li>`).join("")}</ul>${bwArea("Cómo comprobaste que esta evidencia corresponde al contexto mostrado","reason","",'required minlength="10" maxlength="2000"')}<label class="check-field"><input name="contextVerified" type="checkbox" required><span>Comprobé equipo, destino, carpetas y exclusiones de todos los registros seleccionados; corresponden a la evidencia original.</span></label>`,"Registrar revisión",async form=>{
    if (!pending) {
      if (!form.get("contextVerified")) throw Error("Confirma la comprobación de todos los contextos seleccionados.");
      pending = {requestId:atlasRequestId(),reason:form.get("reason"),contextVerified:true,items:rows.map(row=>({kind:row.kind,recordId:row.recordId,profileId:row.profileId,expectedContextKey:row.expectedContextKey}))};
    }
    try {
      const result = await api("/api/v1/admin/backup-controls/context-reviews",backupControlMutationOptions("POST",pending));
      bw("bwDialog").close(); backupContextReview.data = null; backupContextReview.selected.clear(); await loadBackupControl(); await renderBackupContextReview(); showToast(`Revisión registrada: ${result.validated} validaciones nuevas; ${result.reused} ya registradas. Las fechas originales se conservan.`);
    } catch (error) {
      if (error.status === 400) { pending = null; throw Error(error.code || "Revisa el motivo y la selección."); }
      if (error.status === 409) { bw("bwDialog").dataset.submitLocked = "true"; throw Error("El contexto cambió o la revisión ya no coincide. Cierra y vuelve a consultar; no se aplicó una confirmación automática."); }
      bw("bwForm").querySelectorAll("textarea,input").forEach(input=>input.disabled=true);
      throw Error("No se confirmó la respuesta. Reintentar conserva el mismo identificador y contenido de revisión para evitar duplicarla; también puedes cerrar y consultar de nuevo.");
    }
  });
}
