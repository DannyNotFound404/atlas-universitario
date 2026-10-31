"use strict";

const acronisImports = { items: [], loaded: false, loading: false, error: "" };
const acronisCategories = { MATCHED: "Coincide con Acronis en ATLAS", INVALID_ROW: "Datos por revisar", DUPLICATE_SOURCE: "Nombre repetido en el Excel",
  NOT_IN_ATLAS: "Sin activo habilitado en ATLAS", AMBIGUOUS_ATLAS: "Identidad ambigua en ATLAS", PROVIDER_MISMATCH: "Proveedor diferente",
  PAUSED: "Seguimiento pausado", NO_ACTIVE_CONTROL: "Sin control activo", AMBIGUOUS_CONTROL: "Varios controles activos", CONTROL_MISMATCH: "Control con otro proveedor", CONTROL_CHANGED: "Configuración modificada" };
const acronisEvidenceStates = { WITHIN_INTERVAL: "Dentro del plazo al corte", OLDER_COPY: "Copia anterior al plazo", RESULT_REVIEW: "Resultado por revisar",
  NO_SUCCESS_DATE: "Sin fecha de copia correcta", FUTURE_DATE: "Fecha por revisar", BEFORE_CUSTODY: "Anterior a la custodia actual", COUNTS_REVIEW: "Conteo bloqueado por el criterio anterior", COUNTS_INVALID: "Contadores inválidos" };

async function loadAcronisImports() {
  if (acronisImports.loading) return;
  acronisImports.loading = true;
  try { acronisImports.items = await api("/api/v1/admin/backup-controls/acronis-imports"); acronisImports.loaded = true; acronisImports.error = ""; }
  catch (error) { acronisImports.error = error.message === "unauthorized" ? "" : "No se pudo cargar el historial de Acronis."; }
  finally { acronisImports.loading = false; if (backupWorkspace.tab === "acronis") renderAcronisImports(); }
}

function renderAcronisImports() {
  if (!bw("bwContent")) return;
  if (!acronisImports.loaded && !acronisImports.loading && !acronisImports.error) void loadAcronisImports();
  bw("bwAgency").hidden = true;
  bw("bwSearch").placeholder = "Buscar informe de Acronis";
  const query = backupWorkspace.search.toLocaleLowerCase();
  const items = acronisImports.items.filter(i => i.fileName.toLocaleLowerCase().includes(query));
  const latest = acronisImports.items[0];
  bw("bwContent").innerHTML = `<section class="acronis-intro"><div><span class="eyebrow">Evidencia externa</span><h3>Informes de Acronis</h3>
    <p>Una carga conserva el Excel original y compara sus equipos con el inventario de ATLAS. Puedes repetirla cuando obtengas un nuevo informe.</p>
    <p>Los resultados corresponden a las fechas del archivo. Las coincidencias se revisan antes de registrar copias; el proveedor de los equipos se administra en su ficha.</p>
    ${latest ? `<small>Último corte cargado: ${bwEscape(formatDate(latest.reportAtUtc))} · ${latest.rows} filas · ${latest.distinctNames} nombres distintos</small>` : ""}</div>
    <div class="bw-card-actions">${canManageBackupControls() ? bwButton("Subir Excel de Acronis", "acronis-upload", "", true) : ""}${bwButton("Actualizar historial", "acronis-refresh")}</div></section>
    ${acronisImports.error ? `<p class="bw-error" role="alert">${bwEscape(acronisImports.error)}</p>` : ""}
    ${items.length ? `<div class="table-wrap"><table class="data-table"><thead><tr><th>Informe y fecha de corte</th><th>Comparación al cargar</th><th>Revisión</th><th>Acción</th></tr></thead><tbody>${items.map(i => `<tr>
      <td><strong>${bwEscape(i.fileName)}</strong><small>${bwEscape(formatDate(i.reportAtUtc))}</small><small>Cargó: ${bwEscape(i.createdBy)} · ${bwEscape(formatDate(i.createdAtUtc))}</small></td>
      <td>${i.matches} coincidencias · ${i.differences} filas por revisar<small>${i.notListed} activos Acronis de ATLAS sin fila en el informe</small></td>
      <td><span class="bw-status ${i.reviewedAtUtc ? "muted" : "attention"}">${i.reviewedAtUtc ? "Revisión registrada" : "Pendiente de revisión"}</span>${i.reviewedAtUtc ? `<small>${i.registeredCopies} copias registradas · ${i.reusedCopies} ya documentadas</small>` : ""}</td>
      <td>${bwButton("Ver comparación", "acronis-detail", i.id)}</td></tr>`).join("")}</tbody></table></div>` :
      `<div class="bw-empty"><h3>${acronisImports.loading ? "Cargando informes" : "Sin informes registrados"}</h3><p>En Acronis, exporta Informes → Resumen → Descargar → Excel.</p></div>`}`;
  bw("bwCount").textContent = `${items.length} informes · se muestran las 30 cargas más recientes`;
  bw("bwCount").nextElementSibling.hidden = true;
}

function acronisDateInput(utc, offset) { return new Date(new Date(utc).getTime() + offset * 60000).toISOString().slice(0, 16); }
function acronisUtcFromInput(local, offset) {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/.exec(local);
  if (!match) throw Error("Indica la fecha y hora de corte del informe.");
  return new Date(Date.UTC(...match.slice(1).map((n, i) => Number(n) - (i === 1 ? 1 : 0))) - offset * 60000).toISOString();
}

function beginAcronisUpload() {
  if (!canManageBackupControls()) return;
  bwOpen("Subir informe general de Acronis", `<p class="bw-note">Selecciona el Excel exportado desde Resumen. ATLAS conservará el archivo y preparará la comparación con su inventario actual.</p>
    <div class="bw-form-grid"><label class="bw-field bw-wide"><span>Excel de Acronis</span><input name="file" type="file" accept=".xlsx" required><small>Un archivo .xlsx, máximo 25 MB y 2000 filas de copia. Conserva todas las hojas del informe.</small></label>
    ${bwField("Fecha y hora de corte del informe", "reportAt", acronisDateInput(new Date().toISOString(), -360), "datetime-local", "required")}
    <label class="bw-field"><span>Zona horaria de las fechas del Excel</span><select name="offset"><option value="-360">Guatemala · UTC−06:00</option><option value="0">UTC</option></select></label>
    <p class="bw-note bw-wide">Confirma la hora que usa tu consola de Acronis. La fecha del nombre del archivo termina en UTC; las fechas de sus celdas pueden estar en hora local.</p></div>`, "Cargar y comparar", async form => {
      const file = form.get("file"); if (!file?.size) throw Error("Selecciona un Excel con contenido.");
      if (file.size > 25000000) throw Error("El Excel supera 25 MB.");
      const offset = Number(form.get("offset"));
      const body = new FormData(); body.append("file", file); body.append("record", JSON.stringify({ reportAtUtc: acronisUtcFromInput(form.get("reportAt"), offset), dateOffsetMinutes: offset }));
      const detail = await api("/api/v1/admin/backup-controls/acronis-imports", { method: "POST", headers: { "X-Atlas-Request": "dashboard" }, body });
      bw("bwDialog").close(); backupWorkspace.tab = "acronis"; await loadAcronisImports(); await openAcronisImport(detail.summary.id);
      showToast(detail.duplicateUpload ? "Este archivo ya estaba registrado. Se abrió su carga original." : "Excel conservado. Revisa la comparación y sus fechas.");
    });
  let fileUtc = null;
  const form = bw("bwForm");
  form.elements.file.addEventListener("change", () => {
    const match = /(\d{4})-(\d{2})-(\d{2})-(\d{2})-(\d{2})UTC\.xlsx$/i.exec(form.elements.file.files[0]?.name || "");
    if (match) { fileUtc = new Date(Date.UTC(...match.slice(1).map((n, i) => Number(n) - (i === 1 ? 1 : 0)))).toISOString(); form.elements.reportAt.value = acronisDateInput(fileUtc, Number(form.elements.offset.value)); }
  });
  form.elements.offset.addEventListener("change", () => { if (fileUtc) form.elements.reportAt.value = acronisDateInput(fileUtc, Number(form.elements.offset.value)); });
}

function acronisRunNote(row) {
  if (row.runCountNote) return row.runCountNote;
  const s = row.source;
  return Number.isInteger(s.successfulRuns) && Number.isInteger(s.totalRuns) && s.successfulRuns < s.totalRuns ?
    `${s.successfulRuns} de ${s.totalRuns} ejecuciones figuran como correctas. Hay otras ejecuciones por revisar; el conteo no invalida la última copia correcta.` : "";
}

async function openAcronisImport(id, supplement = false) {
  try {
    const detail = await api(`/api/v1/admin/backup-controls/acronis-imports/${encodeURIComponent(id)}`);
    const preview = supplement ? await api(`/api/v1/admin/backup-controls/acronis-imports/${encodeURIComponent(id)}/supplement`) : null;
    const s = detail.summary, reviewable = canManageBackupControls() && (!s.reviewedAtUtc || supplement);
    const comparison = preview?.comparison || detail.comparison, registeredRows = preview?.registeredRows || detail.registeredRows;
    const requestId = supplement ? (typeof atlasRequestId === "function" ? atlasRequestId() : crypto.randomUUID()) : null;
    bwOpen(supplement ? "Completar copias del informe" : "Evidencia y comparación de Acronis", `<div class="acronis-report-heading"><h4>${bwEscape(s.fileName)}</h4>
      <p>Corte: ${bwEscape(formatDate(s.reportAtUtc))} · ${s.rows} filas · ${s.distinctNames} nombres distintos · Fechas del Excel: UTC${s.dateOffsetMinutes < 0 ? "−" : "+"}${String(Math.floor(Math.abs(s.dateOffsetMinutes) / 60)).padStart(2, "0")}:${String(Math.abs(s.dateOffsetMinutes) % 60).padStart(2, "0")}</p>
      <p>Comparación con el inventario al ${bwEscape(formatDate(comparison.comparedAtUtc))}. Un nombre ausente del informe requiere revisión; no demuestra que el agente de Acronis esté desinstalado.</p>
      <a href="/api/v1/admin/backup-controls/acronis-imports/${encodeURIComponent(id)}/file" target="_blank" rel="noopener">Descargar Excel original</a>
      <details><summary>Integridad y registro de la evidencia</summary><p class="acronis-hash">SHA-256: ${bwEscape(s.sha256)}</p><p>Cargó ${bwEscape(s.createdBy)} · ${bwEscape(formatDate(s.createdAtUtc))}</p></details></div>
      <p class="bw-note">«Inactiva» se conserva como aparece en Acronis. La fecha de última copia correcta se evalúa por separado. Los informes no comprueban carpetas, retención ni restauración.</p>
      ${supplement ? '<p class="bw-note">La revisión inicial y el Excel se conservan. Esta revisión complementaria añade únicamente copias pendientes, con sus fechas originales. Las ejecuciones no correctas se muestran aparte.</p>' :
        canManageBackupControls() && s.reviewedAtUtc ? `<div class="bw-card-actions">${bwButton("Completar copias del informe", "acronis-supplement", id, true)}</div>` : ""}
      ${reviewable ? `<div class="bw-card-actions">${bwButton("Seleccionar coincidencias disponibles", "acronis-select")}${bwButton("Quitar selección", "acronis-clear")}</div>` : ""}
      <div class="table-wrap acronis-comparison"><table class="data-table"><thead><tr>${reviewable ? "<th>Registrar copia</th>" : ""}<th>Equipo y plan en Acronis</th><th>Resultado y última copia</th><th>Equipo y proveedor en ATLAS</th><th>Comparación</th></tr></thead><tbody>${comparison.rows.map(r => `<tr>
        ${reviewable ? `<td>${r.canRegister ? `<label class="bw-check"><input type="checkbox" name="sourceRows" value="${r.source.rowNumber}" aria-label="Registrar copia de ${bwEscape(r.source.deviceName)}"><span>Fila ${r.source.rowNumber}</span></label>` : registeredRows.includes(r.source.rowNumber) ? "Ya documentada" : "Por revisar"}</td>` : ""}
        <td><strong>${bwEscape(r.source.deviceName)}</strong><small>${bwEscape(r.source.planName)}</small><small>Fila ${r.source.rowNumber}</small></td>
        <td>${bwEscape(r.source.lastResult || "Sin resultado")}<small>${bwEscape(r.source.lastSuccessfulBackupRaw || "Sin fecha de copia correcta")}</small><small>${r.source.successfulRuns ?? "—"} correctas / ${r.source.totalRuns ?? "—"} ejecuciones</small>${registeredRows.includes(r.source.rowNumber) ? '<small class="bw-status muted">Copia documentada</small>' : ""}</td>
        <td>${bwEscape(r.atlasName || "Sin coincidencia")}${r.agency ? `<small>${bwEscape(r.agency)}</small>` : ""}<small>${bwEscape(backupControlProviderLabels[r.atlasProvider] || r.atlasProvider || "Sin proveedor comparable")}</small>${r.protectedScope ? `<details><summary>Control declarado</summary><p>${bwEscape(r.taskName || "Sin nombre de tarea")}</p><p>${bwEscape(r.protectedScope)}</p></details>` : ""}</td>
        <td><span class="bw-status ${r.category === "MATCHED" ? r.evidenceState === "WITHIN_INTERVAL" ? "muted" : "attention" : "attention"}">${bwEscape(acronisCategories[r.category] || "Por revisar")}</span>${r.evidenceState ? `<small>${bwEscape(acronisEvidenceStates[r.evidenceState] || "Por revisar")}</small>` : ""}<p>${bwEscape(r.detail)}</p>${acronisRunNote(r) ? `<p class="acronis-run-note"><strong>Otras ejecuciones</strong><br>${bwEscape(acronisRunNote(r))}</p>` : ""}</td></tr>`).join("")}</tbody></table></div>
      <details class="acronis-missing"><summary>Acronis declarado en ATLAS sin fila en el informe · ${comparison.notListed.length}</summary><p>Revisa el alcance exportado, nombres y pertenencia a los planes antes de modificar una vinculación.</p><ul>${comparison.notListed.map(d => `<li>${bwEscape(d.name)} · ${bwEscape(d.agency)}</li>`).join("")}</ul></details>
      ${reviewable ? `<div class="bw-form-grid"><label class="bw-check bw-wide"><input type="checkbox" name="plansReviewed"> Revisé que los planes de Acronis corresponden a los controles y alcances seleccionados.</label>
      ${bwArea("Observaciones de la revisión", "notes", "", 'maxlength="4000"')}</div><p class="bw-note">${supplement ? "Selecciona únicamente las copias pendientes que verificaste. La revisión inicial se conserva." : "Guardar sin seleccionar filas conserva la revisión y la evidencia."} Al seleccionar copias, se registrarán sus fechas originales; las copias antiguas mantienen su antigüedad.</p>` :
      `<p>Revisión inicial: ${bwEscape(s.reviewedBy || "Pendiente")} ${s.reviewedAtUtc ? `· ${bwEscape(formatDate(s.reviewedAtUtc))}` : ""}</p><p>${bwEscape(detail.reviewNotes || "")}</p>`}
      ${detail.supplements?.length ? `<details><summary>Revisiones complementarias · ${detail.supplements.length}</summary><ul>${detail.supplements.map(item => `<li>${bwEscape(formatDate(item.atUtc))} · ${bwEscape(item.actor)} · ${item.registered} copias añadidas, ${item.reused} ya documentadas<p>${bwEscape(item.notes || "")}</p></li>`).join("")}</ul></details>` : ""}`,
      reviewable ? supplement ? "Añadir copias seleccionadas" : "Guardar revisión" : "Cerrar", async form => {
        if (!reviewable) { bw("bwDialog").close(); return; }
        const sourceRows = form.getAll("sourceRows").map(Number);
        if (supplement && !sourceRows.length) throw Error("Selecciona al menos una copia pendiente para añadir.");
        if (sourceRows.length && !form.has("plansReviewed")) throw Error("Confirma la revisión de los planes y alcances seleccionados.");
        const payload = { sourceRows, plansReviewed: form.has("plansReviewed"), notes: form.get("notes") || null };
        if (supplement) Object.assign(payload, { requestId, expectedStamp: preview.stamp });
        const result = await api(`/api/v1/admin/backup-controls/acronis-imports/${encodeURIComponent(id)}/${supplement ? "supplement" : "review"}`, backupControlMutationOptions("POST", payload));
        bw("bwDialog").close(); await refreshBackupWorkspace(); await loadAcronisImports(); await openAcronisImport(id);
        showToast(`Revisión conservada: ${result.registered} copias registradas y ${result.reused} ya documentadas.`);
      });
    bw("bwDialog").classList.add("acronis-dialog");
  } catch (error) { if (error.message !== "unauthorized") showToast(error.message || "No se pudo abrir el informe."); }
}
