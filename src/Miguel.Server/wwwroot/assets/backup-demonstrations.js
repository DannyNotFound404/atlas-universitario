"use strict";

const backupDemonstrationLabels = {
  CURRENT: "Demostración vigente", PENDING: "Primera demostración pendiente", OVERDUE: "Demostración vencida",
  FOLLOW_UP: "Muestras por resolver", IN_PROGRESS: "Demostración en curso", PLAN_CHANGED: "Revisar nueva versión del plan", NO_TARGETS: "Sin equipos activos"
};
const bdTone = s => s === "CURRENT" ? "healthy" : s === "FOLLOW_UP" ? "critical" : s === "NO_TARGETS" ? "muted" : "attention";
const bdSummary = id => (state.backupControl?.planDemonstrations || []).find(p => p.planId === id);
const bdSamples = count => `${count} ${count === 1 ? "muestra" : "muestras"}`;
const bdProgress = (tested, required) => tested > required
  ? `${bdSamples(tested)} registradas · mínimo requerido: ${bdSamples(required)}`
  : `${tested}/${required} ${tested === 1 && required === 1 ? "muestra registrada" : "muestras registradas"}`;
const bdSuccesses = count => `${count} ${count === 1 ? "satisfactoria" : "satisfactorias"}`;

function backupDemonstrationCard(planId) {
  const s = bdSummary(planId);
  if (!s) return "";
  return `<div class="bd-plan-status"><span class="bw-status ${bdTone(s.status)}">${bwEscape(backupDemonstrationLabels[s.status] || s.status)}</span>
    <small>${bdSamples(s.requiredSamples)} por demostración · ${s.eligibleCount} equipos o sistemas activos</small>
    ${s.latestId ? `<small>Última sesión: ${bdProgress(s.tested, s.latestRequiredSamples ?? s.requiredSamples)} · ${bdSuccesses(s.successful)} · ${bwEscape(formatDate(s.lastAtUtc))}</small>` : ""}
    ${s.unresolvedSamples ? `<small>${s.unresolvedSamples} muestra(s) pendiente(s) de resolver en el historial</small>` : ""}
    ${s.nextDueUtc ? `<small>Próxima fecha: ${bwEscape(formatDate(s.nextDueUtc))}</small>` : ""}
    <div class="bw-card-actions">${bwButton("Ver demostraciones", "demonstrations", planId)}${canManageBackupControls() && s.requiredSamples ? bwButton("Registrar muestra", "new-demonstration", planId, true) : ""}</div></div>`;
}

function backupDemonstrationPending() {
  const q = backupWorkspace.search.toLocaleLowerCase();
  const pending = (state.backupControl?.planDemonstrations || []).filter(p => p.pending && p.name.toLocaleLowerCase().includes(q) &&
    (!backupWorkspace.agency || (state.backupControl?.profiles || []).some(c => c.planId === p.planId && c.agencyCode === backupWorkspace.agency)));
  if (!pending.length) return "";
  return `<section class="bd-pending"><h3>Demostraciones de planes pendientes · ${pending.length}</h3><p>Un seguimiento por plan. Las muestras comprobadas conservan su equipo y destino.</p>
    <div class="bw-plan-grid">${pending.map(p => `<article class="bw-plan-card"><h3>${bwEscape(p.name)}</h3>${backupDemonstrationCard(p.planId)}</article>`).join("")}</div></section>`;
}

function backupDemonstrationHistory(session, planId, revision) {
  const groups = new Map();
  for (const a of session.attempts) { if (!groups.has(a.profileId)) groups.set(a.profileId, []); groups.get(a.profileId).push(a); }
  const success = [...groups.values()].filter(a => a.at(-1).restoration.result === "SUCCESS").length;
  return `<article class="bd-session"><h4>Demostración · ${bwEscape(formatDate(session.createdAtUtc))}</h4>
    <p>Plan v${session.planRevision} · ${bdProgress(groups.size, session.requiredSamples)} · ${bdSuccesses(success)} · ${session.populationCount} equipos o sistemas en el alcance al registrar</p>
    ${session.originalRequiredSamples > session.requiredSamples ? `<p class="bw-note">Esta sesión se creó con un requisito de ${bdSamples(session.originalRequiredSamples)}. El criterio actual de Acronis requiere una muestra por demostración. Se conservan todos los intentos y las evidencias originales.</p>` : ""}
    ${groups.size < session.requiredSamples ? `<p class="bw-note">Faltan ${session.requiredSamples - groups.size} muestras. Las guardadas se conservan; la demostración todavía no está completa.${session.planRevision !== revision ? " El plan cambió de versión: registra una nueva demostración con su configuración actual." : ""}</p>` : ""}
    ${canManageBackupControls() && groups.size < session.requiredSamples && session.planRevision === revision ? `<div class="bw-card-actions"><button type="button" class="primary-button" data-bw-action="continue-demonstration" data-id="${bwEscape(planId)}" data-session="${bwEscape(session.id)}">Registrar siguiente muestra</button></div>` : ""}
    <p>${bwEscape(session.notes || "")}</p>
    ${[...groups.values()].map(attempts => {
      const last = attempts.at(-1);
      return `<section class="bd-history-sample"><strong>${bwEscape(last.snapshot.name)} · ${bwEscape(last.snapshot.agency)}</strong><small>${bwEscape(last.snapshot.destination)}</small>
      ${attempts.map((a, i) => `<details ${i === attempts.length - 1 ? "open" : ""}><summary>${i ? "Repetición" : "Prueba inicial"} · ${bwEscape(backupControlResultLabels[a.restoration.result] || a.restoration.result)} · ${bwEscape(formatDate(a.restoration.finishedAtUtc))}</summary>
        <p>${bwEscape(a.restoration.testedScope)}</p><p>Copia utilizada: ${bwEscape(formatDate(a.restoration.backupUsedAtUtc))} · Duración: ${a.restoration.rtoAchievedMinutes ?? "Sin registrar"} min</p>
        <p>Integridad: ${a.restoration.integrityVerified ? "Comprobada" : "No comprobada"} · Funcionamiento: ${a.restoration.functionalityVerified ? "Comprobado" : "No comprobado"}</p>
        <p>Ejecutó: ${bwEscape(a.restoration.executedBy)}${a.restoration.validatedBy ? ` · Validó: ${bwEscape(a.restoration.validatedBy)}` : ""} · Registró: ${bwEscape(a.createdBy)}</p>
        <p>${bwEscape(a.restoration.notes || "")}</p></details>`).join("")}
        ${canManageBackupControls() && last.restoration.result !== "SUCCESS" ? `<button type="button" class="secondary-button compact" data-bw-action="retry-demonstration" data-id="${bwEscape(planId)}" data-session="${bwEscape(session.id)}" data-attempt="${bwEscape(last.id)}">Repetir esta muestra</button>` : ""}</section>`;
    }).join("")}
    ${session.evidence.length ? `<h5>Archivos de evidencia</h5><ul>${session.evidence.map(e => `<li><a href="/api/v1/admin/backup-controls/evidence/${encodeURIComponent(e.id)}/file" target="_blank" rel="noopener">${bwEscape(e.fileName)}</a><small>SHA-256: ${bwEscape(e.sha256)}</small></li>`).join("")}</ul>` : '<p class="bw-note">Sin archivo adjunto; se conservan los resultados documentados.</p>'}
    </article>`;
}

async function openBackupDemonstrations(planId) {
  try {
    const data = await api(`/api/v1/admin/backup-controls/plans/${encodeURIComponent(planId)}/demonstrations`);
    const s = data.summary;
    bwOpen(`Demostraciones · ${s.name}`, `<p class="bw-note">${s.provider === "ACRONIS" ? "Acronis requiere una sola muestra de restauración por plan en cada período configurado." : "Tres copias de equipos distintos por período; si hay menos, se incluyen todos."} Es una demostración operativa, sin porcentaje de confianza estadística. Solo acredita las muestras y destinos que se comprobaron.</p>
      <span class="bw-status ${bdTone(s.status)}">${bwEscape(backupDemonstrationLabels[s.status])}</span>
      ${canManageBackupControls() && s.requiredSamples ? `<div class="bw-card-actions">${bwButton("Registrar muestra", "new-demonstration", planId, true)}</div>` : ""}
      <p>${s.eligibleCount} equipos o sistemas activos · Próxima fecha: ${s.nextDueUtc ? bwEscape(formatDate(s.nextDueUtc)) : "Pendiente de completar y resolver las muestras"}</p>
      ${data.legacyFailures?.length ? `<section class="bd-session"><h4>Pruebas individuales anteriores por resolver</h4><p>Incluye estos equipos en una demostración o registra su nueva prueba individual. El fallo anterior se conserva.</p>${data.legacyFailures.map(f=>`<p>${bwEscape(f.name)} · ${bwEscape(backupControlResultLabels[f.result] || f.result)} · ${bwEscape(formatDate(f.atUtc))} ${bwButton("Ver prueba individual","detail",f.profileId)}</p>`).join("")}</section>` : ""}
      ${data.history.length ? data.history.map(h => backupDemonstrationHistory(h, planId, s.revision)).join("") : '<div class="bw-empty">Todavía no hay demostraciones registradas. Las pruebas individuales anteriores siguen en la actividad de cada equipo.</div>'}
      <p class="bw-note">Se muestran las 30 sesiones más recientes y todas las que están incompletas o conservan muestras por resolver.</p>`, "Cerrar", async () => bw("bwDialog").close());
  } catch (error) { if (error.message !== "unauthorized") showToast(error.message || "No se pudieron cargar las demostraciones."); }
}

async function beginBackupDemonstration(planId, sessionId = null, attemptId = null) {
  if (!canManageBackupControls()) return;
  try {
    const data = await api(`/api/v1/admin/backup-controls/plans/${encodeURIComponent(planId)}/demonstrations`);
    const session = sessionId ? data.history.find(h => h.id === sessionId) : data.history.find(h =>
      h.planRevision === data.summary.revision && new Set(h.attempts.map(a => a.profileId)).size < h.requiredSamples);
    const prior = attemptId ? session?.attempts.find(a => a.id === attemptId) : null;
    if ((sessionId && !session) || (attemptId && !prior)) throw Error("Actualiza el historial antes de continuar la demostración.");
    if (session && !prior && session.planRevision !== data.summary.revision) throw Error("El plan cambió. Registra una demostración de su versión actual.");
    const registered = new Set((session?.attempts || []).map(a => a.profileId)).size;
    const required = session?.requiredSamples ?? data.summary.requiredSamples;
    if (session && !prior && registered >= required) throw Error("La demostración ya tiene todas sus muestras. Actualiza el historial.");
    const usedIdentities = new Set((session?.attempts || []).map(a => a.snapshot.identity));
    const usedProfiles = new Set((session?.attempts || []).map(a => a.profileId));
    const available = prior ? data.candidates : data.candidates.filter(c => !usedProfiles.has(c.profileId) && !usedIdentities.has(c.identity));
    const suggestion = data.suggestedProfiles.find(id => available.some(c => c.profileId === id)) || available[0]?.profileId;
    const selected = prior ? [prior.profileId] : suggestion ? [suggestion] : [];
    if (!selected.length) throw Error("No quedan equipos activos disponibles para otra muestra. Las muestras guardadas se conservan; revisa las asignaciones del plan.");
    if (selected.some(id => !data.candidates.some(c => c.profileId === id))) throw Error("El equipo ya no está activo en este plan. Conserva el seguimiento del fallo y revisa su asignación.");
    const now = formatDateTimeLocal(new Date().toISOString()), requestId = (typeof atlasRequestId === "function" ? atlasRequestId() : crypto.randomUUID());
    bwOpen(`${prior ? "Repetir una muestra" : "Registrar muestra"} · ${data.summary.name}`, `
      <p class="bw-note">${prior ? "Se conserva el intento anterior y se añade este resultado a la misma demostración." : required === 1 ? "Esta demostración requiere una sola muestra de un equipo o sistema del plan. Puedes cambiar el equipo sugerido. Un resultado satisfactorio completa la demostración si no hay fallos pendientes." : `${registered}/${required} muestras registradas. Guarda esta muestra y podrás continuar después; la demostración se completa con ${required} equipos o sistemas distintos. Puedes cambiar el equipo sugerido.`} El formulario no ejecuta copias ni restaura archivos.</p>
      <div class="bw-form-grid">${bwField("Inicio de esta prueba", "started", now, "datetime-local", "required")}${bwField("Fin de esta prueba", "finished", now, "datetime-local", "required")}
      ${bwField("Ejecutado por", "executedBy", state.session?.displayName || "", "text", 'required maxlength="160"')}
      <label class="bw-field"><span>Tipo de restauración</span><select name="modality"><option value="FILE">Archivos</option><option value="APPLICATION">Aplicación</option><option value="SYSTEM">Sistema</option><option value="HOT_RECOVERY">Recuperación en caliente</option></select></label></div>
      ${selected.map((id, i) => `<fieldset class="bd-sample"><legend>${prior ? "Repetición de la muestra" : `Muestra ${registered + 1} de ${required}`}</legend>
        <label class="bw-field"><span>Equipo / sistema</span><select name="profile-${i}" ${prior ? "disabled" : ""}>${available.map(c => `<option value="${bwEscape(c.profileId)}" ${c.profileId === id ? "selected" : ""}>${bwEscape(c.name)} · ${bwEscape(c.agency)}</option>`).join("")}</select></label>
        <p class="bd-destination" data-destination="${i}"></p>
        <div class="bw-form-grid">${bwField("Fecha de la copia utilizada", `backup-${i}`, "", "datetime-local", "required")}
        <label class="bw-field"><span>Resultado de esta muestra</span><select name="result-${i}" required><option value="">Seleccionar resultado</option><option value="SUCCESS">Satisfactorio</option><option value="WARNING">Con advertencias</option><option value="FAILED">Fallido</option></select></label>
        ${bwArea("Qué se restauró y cómo se comprobó", `scope-${i}`, prior?.restoration.testedScope || "", 'required maxlength="8000"')}
        ${bwField("Duración de recuperación · minutos", `duration-${i}`, "", "number", 'required min="0" max="10080"')}
        ${bwArea("Observaciones o motivo del fallo", `notes-${i}`, "", 'maxlength="4000"')}
        <label class="bw-check"><input type="checkbox" name="integrity-${i}"> Integridad comprobada</label>
        <label class="bw-check"><input type="checkbox" name="functionality-${i}"> Funcionamiento comprobado</label></div></fieldset>`).join("")}
      <div class="bw-form-grid">${!session ? bwArea("Notas de la demostración", "notes", "", 'maxlength="4000"') : ""}
      <label class="bw-field bw-wide"><span>Evidencia de esta muestra · opcional</span><input type="file" name="evidence" accept=".pdf,.docx,.xlsx,.png,.jpg,.jpeg,.txt,.csv,.log"><small>Informe o capturas, máximo 25 MB. No adjuntes el contenido del respaldo ni credenciales.</small></label>
      <details class="bw-wide"><summary>Validación adicional, si corresponde</summary><div class="bw-form-grid">${bwField("Validado por", "validatedBy", "", "text", 'maxlength="160"')}${bwField("Fecha de validación", "validatedAt", "", "datetime-local")}</div></details></div>`, "Guardar muestra", async form => {
        const samples = selected.map((id, i) => {
          const candidate = data.candidates.find(c => c.profileId === (prior ? id : form.get(`profile-${i}`)));
          const result = form.get(`result-${i}`), integrity = form.has(`integrity-${i}`), functionality = form.has(`functionality-${i}`);
          if (result === "SUCCESS" && (!integrity || !functionality)) throw Error(`Muestra ${i + 1}: confirma integridad y funcionamiento para registrar un resultado satisfactorio.`);
          return { profileId: candidate.profileId, bindingStamp: candidate.bindingStamp, repeatsSampleId: prior?.id || null, restoration: {
            modality: form.get("modality"), result, operationalCaseId: null, backupUsedAtUtc: new Date(form.get(`backup-${i}`)).toISOString(),
            startedAtUtc: new Date(form.get("started")).toISOString(), finishedAtUtc: new Date(form.get("finished")).toISOString(),
            testedScope: form.get(`scope-${i}`), integrityVerified: integrity, functionalityVerified: functionality,
            rtoAchievedMinutes: Number(form.get(`duration-${i}`)), executedBy: form.get("executedBy"),
            validatedBy: form.get("validatedBy") || null, validatedAtUtc: form.get("validatedAt") ? new Date(form.get("validatedAt")).toISOString() : null,
            notes: form.get(`notes-${i}`) || null, historical: false, nextTestDueUtc: null
          } };
        });
        const identities = samples.map(s => data.candidates.find(c => c.profileId === s.profileId).identity);
        if (new Set(identities).size !== samples.length) throw Error("Selecciona equipos o sistemas distintos para las muestras.");
        const body = new FormData();
        body.append("record", JSON.stringify({ requestId, expectedRevision: data.summary.revision, samples, notes: form.get("notes") || null }));
        const file = form.get("evidence");
        if (file?.size) { if (file.size > 25000000) throw Error("La evidencia supera 25 MB."); body.append("file", file); }
        await api(`/api/v1/admin/backup-controls/plans/${encodeURIComponent(planId)}/demonstrations${session ? `/${encodeURIComponent(session.id)}/${prior ? "retry" : "samples"}` : ""}`, { method: "POST", headers: { "X-Atlas-Request": "dashboard" }, body });
        bw("bwDialog").close(); await refreshBackupWorkspace(); await openBackupDemonstrations(planId);
        showToast(prior ? "Repetición registrada. El intento anterior se conserva." : `Muestra guardada: ${registered + 1}/${required}. Puedes consultar el resultado en el historial.`);
      });
    const updateDestinations = () => selected.forEach((id, i) => {
      const c = data.candidates.find(c => c.profileId === bw("bwForm").querySelector(`[name="profile-${i}"]`).value);
      bw("bwForm").querySelector(`[data-destination="${i}"]`).textContent = `${c.destination} · Plan v${c.planRevision} · Alcance: ${c.scope}`;
    });
    bw("bwForm").addEventListener("change", updateDestinations); updateDestinations();
    if (prior) bw("bwForm").querySelector('[name="modality"]').value = prior.restoration.modality;
  } catch (error) { if (error.message !== "unauthorized") showToast(error.message || "No se pudo abrir la demostración."); }
}
