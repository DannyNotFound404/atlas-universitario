"use strict";
(() => {
  const host = document.getElementById("updaterWorkspace");
  if (!host) return;
  let page = 1, search = "", ring = "", data = null, requestNumber = 0, lastRead = 0, busy = false;
  const labels = {NOT_REPORTED:"Sin reporte del actualizador",CHECKED:"Comprobación recibida",LEASED:"Preparando descarga",DOWNLOADING:"Descargando",READY:"Listo para instalar",INSTALLING:"Instalando",AWAITING_CONFIRMATION:"Esperando confirmación",CONFIRMED:"Confirmado",DEFERRED:"Aplazado",FAILED:"Falló",ROLLING_BACK:"Recuperando versión anterior",ROLLED_BACK:"Versión anterior recuperada",RECOVERY_FAILED:"Requiere recuperación",CANCELLED:"Detenido antes de instalar"};
  const reasons = {insufficient_space:"Espacio insuficiente para actualizar y conservar la recuperación",backup_busy:"Copia en curso o aplicación de respaldo abierta",network_or_preflight_failed:"Revisar conexión y requisitos locales",signature_rejected:"Firma rechazada",package_rejected:"Paquete rechazado",authorization_changed:"El despliegue cambió o fue pausado",heartbeat_confirmed:"Versión y reporte del agente comprobados",installed_waiting_report:"Instalación terminada; falta el reporte posterior",manual_recovery_required:"La recuperación necesita atención técnica",previous_version_restored:"Se comprobó la versión anterior",failure_threshold:"Freno automático por fallos",capacity_wait:"Esperando espacio en el lote"};
  const esc = value => escapeHtml(String(value ?? ""));
  const allowed = () => ["Administrator","Auditor","TechnologyManager"].includes(state.session?.role);
  const isAdmin = () => state.session?.role === "Administrator";
  const visible = () => allowed() && document.getElementById("view-distribution")?.classList.contains("active") && !document.hidden;
  host.innerHTML = `<article class="panel updater-panel" data-update-section="tracking"><div class="panel-heading"><div><h3>Seguimiento de actualizaciones</h3><p>Resultado por equipo, capacidad de despliegue y confirmación del agente.</p></div><button type="button" class="secondary-button compact" id="updaterRefresh">Actualizar</button></div>
    <div class="updater-toolbar"><label class="field"><span>Buscar equipo, usuario o agencia</span><input id="updaterSearch" maxlength="120" type="search" placeholder="Equipo, usuario o agencia"></label><label class="field updater-ring-field"><span>Anillo</span><select id="updaterRing"><option value="">Todos</option><option value="PILOT">Piloto</option><option value="GENERAL">General</option></select></label><button type="button" class="secondary-button" id="updaterExport">Exportar informe</button><button type="button" class="secondary-button" id="updaterPolicy">Configurar despliegue</button><button type="button" class="secondary-button" id="updaterControl">Registrar autorización firmada</button></div>
    <p id="updaterMessage" class="updater-message" role="status">Abra Actualizaciones para consultar el seguimiento.</p>
    <div class="table-wrap"><table class="data-table"><thead><tr><th>Equipo / usuario / agencia</th><th>Estado</th><th>Versiones instaladas</th><th>Última comunicación</th><th>Detalle</th></tr></thead><tbody id="updaterRows"></tbody></table></div>
    <div class="updater-pagination"><button type="button" class="secondary-button compact" id="updaterPrev">Anterior</button><span id="updaterPage"></span><button type="button" class="secondary-button compact" id="updaterNext">Siguiente</button></div></article>`;
  const el = id => document.getElementById(id);
  function render() {
    const items = data?.items || [];
    el("updaterPolicy").hidden = !isAdmin(); el("updaterControl").hidden = !isAdmin();
    el("updaterRows").innerHTML = items.length ? items.map(item => {
      const checkedAt = new Date(item.lastCheckAtUtc).getTime();
      const stale = !item.lastCheckAtUtc || !Number.isFinite(checkedAt) || Date.now() - checkedAt > 86400000;
      const status = !labels[item.phase] ? "Estado sin clasificar" : item.phase === "NOT_REPORTED" ? labels[item.phase] : stale ? "Sin comprobación reciente" : labels[item.phase];
      const warning = stale ? `Último estado recibido: ${labels[item.phase] || "Estado sin clasificar"}. No confirma el estado actual.` : item.stalled ? "Sin progreso confirmado; revisar el equipo" : reasons[item.code] || "";
      const ringLabel = item.updateRingCode === "PILOT" ? "Piloto" : item.updateRingCode === "GENERAL" ? "General" : "Sin dato de anillo";
      return `<tr><td><strong>${esc(item.machineName)}</strong><span class="cell-secondary">${esc(item.currentUser || "Usuario sin reportar")}</span><span class="cell-secondary">${esc(item.agencyCode)} · Anillo ${esc(ringLabel)}</span></td><td><span class="updater-state ${!stale && item.phase === "CONFIRMED" ? "good" : ["FAILED","RECOVERY_FAILED"].includes(item.phase) ? "bad" : "waiting"}">${esc(status)}</span>${warning ? `<span class="cell-secondary">${esc(warning)}</span>` : ""}${item.totalBytes > 0 && item.phase === "DOWNLOADING" ? `<progress aria-label="Descarga" value="${Math.min(item.downloadedBytes,item.totalBytes)}" max="${item.totalBytes}"></progress>` : ""}</td><td>Actualizador ${esc(item.updaterVersion || "Sin dato")}<span class="cell-secondary">Agente ${esc(item.agentVersion || "Sin dato")}</span><span class="cell-secondary">Atlas Backup ${esc(item.backupClientVersion || "Sin dato")}</span></td><td>Actualizador: ${item.lastCheckAtUtc ? esc(new Date(item.lastCheckAtUtc).toLocaleString()) : "Sin reporte"}<span class="cell-secondary">Agente: ${item.lastSeenAtUtc ? esc(new Date(item.lastSeenAtUtc).toLocaleString()) : "Sin reporte"}</span></td><td><button type="button" class="secondary-button compact updater-history" data-id="${esc(item.deviceId)}">Ver historial</button></td></tr>`;
    }).join("") : `<tr><td colspan="5" class="empty-state">${ring === "PILOT" && !search ? "No hay equipos habilitados vinculados al anillo piloto." : "No hay equipos para estos filtros."}</td></tr>`;
    const pages = Math.max(1,Math.ceil((data?.total || 0)/25));
    el("updaterPage").textContent = `Página ${page} de ${pages} · ${data?.total || 0} ${(data?.total||0)===1?"equipo":"equipos"}`;
    el("updaterPrev").disabled = page <= 1; el("updaterNext").disabled = page >= pages;
    el("updaterMessage").textContent = `${ring === "PILOT" ? "Equipos habilitados vinculados al piloto, con o sin despliegue activo. " : ""}${(data?.policies || []).filter(p=>p.halted||p.policy.paused).length} versiones pausadas. El usuario y las versiones corresponden al último reporte; un reporte antiguo no confirma el estado actual del equipo.`;
    host.querySelectorAll(".updater-history").forEach(button => button.addEventListener("click",()=>history(button.dataset.id)));
  }
  async function refresh(force = false) {
    if (!visible() || busy || !force && Date.now()-lastRead<30000) return;
    const current = ++requestNumber; busy = true; el("updaterRefresh").disabled = true;
    try {
      const result = await api(`/api/v1/admin/updater?${new URLSearchParams({page:String(page),search,ring})}`);
      if (current !== requestNumber || !allowed()) return;
      data = result; lastRead = Date.now(); render();
    } catch (error) {
      if (current === requestNumber) { data = null; el("updaterRows").innerHTML=""; el("updaterPrev").disabled=true; el("updaterNext").disabled=true; el("updaterPage").textContent="Consulta pendiente"; el("updaterMessage").textContent="No se pudo consultar el seguimiento. Intente actualizar nuevamente."; }
    } finally { if(current===requestNumber){busy=false; el("updaterRefresh").disabled=false;} }
  }
  function dialog(title, content) {
    const previous = document.activeElement;
    const modal = document.createElement("dialog"); modal.className="updater-dialog";
    modal.setAttribute("aria-label",title);
    modal.innerHTML=`<div class="panel-heading"><h3>${esc(title)}</h3><button type="button" class="secondary-button compact" data-close>Cerrar</button></div><div class="updater-dialog-body">${content}</div>`;
    document.body.appendChild(modal); modal.querySelector("[data-close]").addEventListener("click",()=>modal.close());
    modal.addEventListener("close",()=>{modal.remove();previous?.focus();}); modal.showModal(); return modal;
  }
  async function history(id) {
    const modal=dialog("Historial de actualización",'<p role="status">Consultando evidencia…</p>');
    try {
      const items=await api(`/api/v1/admin/updater/devices/${encodeURIComponent(id)}/history`);
      if(!modal.isConnected)return;
      modal.querySelector(".updater-dialog-body").innerHTML=items.length ? `<p>Hasta 200 eventos recientes. Las fechas de recepción indican cuándo llegaron a ATLAS.</p><ol class="updater-history-list">${items.map(item=>`<li><strong>${esc(labels[item.phase]||"Estado sin clasificar")}</strong><p>${esc(reasons[item.code]||item.code)}</p><small>Recibido ${esc(new Date(item.receivedAtUtc).toLocaleString())}</small><details><summary>Referencia del intento</summary><p>${esc(item.attemptId)}</p></details></li>`).join("")}</ol>` : "<p>Este equipo todavía no ha enviado eventos del actualizador coordinado.</p>";
      if(isAdmin()){
        const form=document.createElement("form");form.className="updater-form";
        form.innerHTML='<label class="field wide"><span>Motivo para permitir un nuevo intento</span><input name="reason" maxlength="400" required placeholder="Describa la corrección o revisión realizada"></label><p class="wide" data-message role="status"></p><button type="submit" class="secondary-button">Permitir reintento en la próxima comprobación</button>';
        modal.querySelector(".updater-dialog-body").appendChild(form);
        form.addEventListener("submit",async event=>{event.preventDefault();const button=form.querySelector("button");button.disabled=true;try{await api(`/api/v1/admin/updater/devices/${encodeURIComponent(id)}/retry`,{method:"POST",headers:{"Content-Type":"application/json","X-Atlas-Request":"dashboard"},body:JSON.stringify({reason:form.elements.reason.value})});form.querySelector("[data-message]").textContent="Reintento autorizado. Se respetarán la pausa, el lote y el horario del despliegue.";}catch{form.querySelector("[data-message]").textContent="No se pudo autorizar el reintento.";}finally{button.disabled=false;}});
      }
      globalThis.AtlasUpdateSections?.enhanceHistory(modal.querySelector(".updater-dialog-body"));
    } catch { modal.querySelector(".updater-dialog-body").textContent="No fue posible consultar el historial."; }
  }
  function policy() {
    if(!isAdmin())return;
    const releases=state.releaseStore?.releases||[];
    if(!releases.length){showToast("Cargue una versión firmada antes de configurar su despliegue.");return;}
    const modal=dialog("Condiciones del despliegue",`<form id="updaterPolicyForm" class="updater-form"><label class="field wide"><span>Versión</span><select name="release">${releases.map(r=>`<option value="${esc(r.releaseId)}">${esc(r.releaseVersion)}</option>`).join("")}</select></label>
      <label class="field"><span>Equipos habilitados · %</span><input name="wave" type="number" min="0" max="100" required></label><label class="field"><span>Descargas simultáneas · total</span><input name="concurrent" type="number" min="1" max="200" required></label><label class="field"><span>Descargas simultáneas · por agencia</span><input name="agency" type="number" min="1" max="200" required></label><label class="field"><span>Velocidad máxima por equipo · KiB/s</span><input name="speed" type="number" min="64" max="102400" required></label>
      <label class="field"><span>Frenar tras equipos fallidos</span><input name="failures" type="number" min="1" max="100" required></label><label class="field"><span>Porcentaje de fallos para frenar</span><input name="percent" type="number" min="1" max="100" required></label><label class="field"><span>Mínimo de resultados para evaluar %</span><input name="samples" type="number" min="1" max="100" required></label><label class="field"><span>Zona horaria</span><input name="zone" required></label>
      <label class="field"><span>Instalar desde</span><input name="start" type="time" required></label><label class="field"><span>Instalar hasta</span><input name="end" type="time" required></label><label class="updater-check wide"><input name="allDay" type="checkbox"> Permitir las 24 horas</label><label class="updater-check wide"><input name="modern" type="checkbox"> Requerir actualizador coordinado (0.4 o posterior)</label><label class="updater-check wide"><input name="paused" type="checkbox"> Pausar nuevas actualizaciones de esta versión</label><label class="updater-check wide"><input name="resume" type="checkbox"> Reanudar tras revisar los fallos y comenzar una nueva evaluación</label>
      <p class="wide">Los lotes mantienen los mismos equipos al aumentar el porcentaje. Para la versión puente, permita los actualizadores anteriores. La pausa permite terminar una instalación que ya comenzó.</p><p class="wide" role="status" data-message></p><div class="updater-form-actions wide"><button type="button" class="secondary-button" data-cancel>Cancelar</button><button class="primary-button" type="submit">Guardar condiciones</button></div></form>`);
    const form=modal.querySelector("form"),f=form.elements;
    function load(){const saved=(data?.policies||[]).find(p=>p.releaseId===f.release.value);const p=saved?.policy||{wavePercent:100,maxConcurrent:8,maxConcurrentPerAgency:2,downloadKiBPerSecond:1024,failureStopCount:3,failurePercent:25,minimumSamples:5,timeZoneId:"America/Guatemala",windowStartMinute:0,windowEndMinute:1440,requireModernUpdater:true,paused:false};form.dataset.revision=saved?.revision||0;f.wave.value=p.wavePercent;f.concurrent.value=p.maxConcurrent;f.agency.value=p.maxConcurrentPerAgency;f.speed.value=p.downloadKiBPerSecond;f.failures.value=p.failureStopCount;f.percent.value=p.failurePercent;f.samples.value=p.minimumSamples;f.zone.value=p.timeZoneId;f.start.value=minuteText(p.windowStartMinute);f.end.value=minuteText(p.windowEndMinute%1440);f.allDay.checked=p.windowStartMinute===0&&p.windowEndMinute===1440;f.modern.checked=p.requireModernUpdater;f.paused.checked=p.paused;f.resume.checked=false;}
    load();f.release.addEventListener("change",load);form.querySelector("[data-cancel]").addEventListener("click",()=>modal.close());
    form.addEventListener("submit",async event=>{event.preventDefault();const button=form.querySelector('[type="submit"]');button.disabled=true;try{const policy={wavePercent:+f.wave.value,maxConcurrent:+f.concurrent.value,maxConcurrentPerAgency:+f.agency.value,downloadKiBPerSecond:+f.speed.value,failureStopCount:+f.failures.value,failurePercent:+f.percent.value,minimumSamples:+f.samples.value,timeZoneId:f.zone.value,windowStartMinute:f.allDay.checked?0:minutes(f.start.value),windowEndMinute:f.allDay.checked?1440:minutes(f.end.value)||1440,requireModernUpdater:f.modern.checked,paused:f.paused.checked};await api(`/api/v1/admin/updater/releases/${encodeURIComponent(f.release.value)}/policy`,{method:"PUT",headers:{"Content-Type":"application/json","X-Atlas-Request":"dashboard"},body:JSON.stringify({expectedRevision:+form.dataset.revision,policy,resumeAfterFailure:f.resume.checked})});modal.close();await refresh(true);showToast("Condiciones guardadas.");}catch{form.querySelector("[data-message]").textContent="No se guardaron los cambios. Revise los campos o actualice la vista si otro administrador modificó esta versión.";}finally{button.disabled=false;}});
  }
  const minuteText=value=>`${String(Math.floor(value/60)).padStart(2,"0")}:${String(value%60).padStart(2,"0")}`;
  const minutes=value=>{const [h,m]=value.split(":").map(Number);return h*60+m;};
  function control(){if(!isAdmin())return;const modal=dialog("Registrar autorización firmada",'<form class="updater-form"><p class="wide">Seleccione una autorización de recuperación o transición de clave generada y firmada fuera del servidor. ATLAS verificará firma, vigencia y destino.</p><label class="field wide"><span>Archivo de autorización</span><input type="file" accept=".json,application/json" required></label><p class="wide" data-message role="status"></p><button class="primary-button" type="submit">Verificar y registrar</button></form>');const form=modal.querySelector("form");form.addEventListener("submit",async event=>{event.preventDefault();const button=form.querySelector("button");button.disabled=true;try{const file=form.querySelector("input").files[0];if(!file||file.size>40000)throw Error();const payload=JSON.parse(await file.text());await api("/api/v1/admin/updater/controls",{method:"POST",headers:{"Content-Type":"application/json","X-Atlas-Request":"dashboard"},body:JSON.stringify(payload)});modal.close();showToast("Autorización verificada y registrada.");}catch{form.querySelector("[data-message]").textContent="No se pudo registrar. Revise la firma, vigencia y versión de destino del archivo.";}finally{button.disabled=false;}});}
  async function exportReport(){try{const response=await fetch(`/api/v1/admin/updater/report.html?${new URLSearchParams({search,ring})}`,{credentials:"same-origin"});if(!response.ok)throw Error();const url=URL.createObjectURL(await response.blob());const a=document.createElement("a");a.href=url;a.download="atlas-actualizaciones.html";a.click();setTimeout(()=>URL.revokeObjectURL(url),30000);}catch{showToast("No fue posible exportar el informe. Acote la búsqueda e intente nuevamente.");}}
  el("updaterRefresh").addEventListener("click",()=>refresh(true));el("updaterPolicy").addEventListener("click",policy);el("updaterControl").addEventListener("click",control);el("updaterExport").addEventListener("click",exportReport);
  el("updaterPrev").addEventListener("click",()=>{if(!busy&&page>1){page--;refresh(true);}});el("updaterNext").addEventListener("click",()=>{if(!busy&&page<Math.ceil((data?.total||0)/25)){page++;refresh(true);}});
  let timer;el("updaterSearch").addEventListener("input",()=>{clearTimeout(timer);timer=setTimeout(()=>{search=el("updaterSearch").value;page=1;requestNumber++;busy=false;refresh(true);},300);});
  function changeRing(value) {
    if (!allowed() || !["","PILOT","GENERAL"].includes(value)) return;
    // Commit any search immediately so a pending debounce cannot resurrect it.
    clearTimeout(timer); search=el("updaterSearch").value; ring=value;
    el("updaterRing").value=ring; page=1; requestNumber++; busy=false;
    data=null; el("updaterRows").innerHTML=""; el("updaterPage").textContent="Consultando equipos…";
    el("updaterMessage").textContent="Consultando los equipos del anillo seleccionado…";
    el("updaterPrev").disabled=true; el("updaterNext").disabled=true;
    return refresh(true);
  }
  el("updaterRing").addEventListener("change",()=>changeRing(el("updaterRing").value));
  async function showRing(value) {
    if (!visible() || !["PILOT","GENERAL"].includes(value)) return;
    el("updaterSearch").value="";
    globalThis.AtlasUpdateSections?.openSection("tracking");
    el("updaterRing").focus?.({preventScroll:true});
    host.scrollIntoView?.({block:"start"});
    await changeRing(value);
  }
  document.querySelectorAll?.("[data-updater-ring]").forEach(button=>button.addEventListener("click",()=>showRing(button.dataset.updaterRing)));
  globalThis.AtlasUpdaterWorkspace={showRing};
  function syncRole(){host.hidden=!allowed();el("updaterPolicy").hidden=!isAdmin();el("updaterControl").hidden=!isAdmin();}
  new MutationObserver(()=>{syncRole();if(visible())refresh();}).observe(document.getElementById("view-distribution"),{attributes:true,attributeFilter:["class"]});
  setInterval(()=>{syncRole();if(visible())refresh();},30000);syncRole();
})();
