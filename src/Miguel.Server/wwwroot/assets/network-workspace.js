"use strict";
(() => {
  const view=document.getElementById("view-network"),host=document.getElementById("networkAutomationWorkspace");
  if(!view||!host)return;
  const esc=v=>escapeHtml(String(v??""));
  const admin=()=>state.session?.role==="Administrator";
  const canRead=()=>["Administrator","TechnologyManager","Auditor","Operator"].includes(state.session?.role);
  const status={QUEUED:"En cola",RUNNING:"Escaneando",SUCCEEDED:"Completado",PARTIAL:"Parcial",FAILED:"Fallido",TIMED_OUT:"Tiempo agotado",SKIPPED:"Omitido",INTERRUPTED:"Interrumpido",CANCELLED:"Cancelado"};
  const warning={tcp_incomplete:"Comprobación TCP incompleta",icmp_incomplete:"Comprobación ICMP incompleta o sin permisos",identification_incomplete:"Identificación de servicios incompleta",names_incomplete:"Consulta de nombres incompleta"};
  const date=v=>v?esc(new Date(v).toLocaleString()):"Pendiente";
  const basis={RECENT_AGENT:"Coincidencia reciente del agente",REGISTERED_NAS:"NAS registrado",REGISTERED_GATEWAY:"Gateway registrado",INFERRED:"Identificación probable",MANUAL:"Clasificación registrada",AMBIGUOUS:"Coincidencia ambigua",UNKNOWN:"Por identificar",LEGACY:"Información anterior"};
  const event={DISCOVERED:"Dirección detectada",OBSERVED:"Presencia comprobada",SERVICES_CHANGED:"Servicios cambiaron",IDENTITY_CHANGED:"La identidad observada cambió"};
  function render(){
    host.hidden=!canRead();if(!canRead())return;
    if(!state.network){host.innerHTML='<article class="panel"><div class="panel-heading"><p role="status">Consulta de red pendiente.</p></div></article>';return;}
    const runs=state.network?.recentRuns||[],scopes=state.network?.scopes||[];
    const scheduled=scopes.filter(s=>s.automaticEnabled).length;
    host.innerHTML=`<article class="panel network-automation"><div class="panel-heading"><div><h3>Programación y últimas comprobaciones</h3><p>${state.network?.automaticEnabled?`${scheduled} red(es) con programación habilitada.`:"La programación global está pausada en el servidor."} Los sondeos parciales conservan la evidencia obtenida.</p></div></div><details open><summary>Historial de sondeos · ${runs.length}</summary><div class="table-wrap"><table class="data-table"><thead><tr><th>Agencia / red</th><th>Resultado</th><th>Presencia / identificación</th><th>Inicio / fin</th><th>Acción</th></tr></thead><tbody>${runs.length?runs.map(r=>`<tr><td>${esc(scopes.find(s=>s.code===r.scopeCode)?.displayName||r.scopeCode)}<span class="cell-secondary">${esc(r.requestedBy.startsWith("system:")?"Programación automática":"Solicitud administrativa")} · ${r.addressesTargeted} direcciones</span></td><td><strong>${esc(status[r.status]||"Sin clasificar")}</strong><span class="cell-secondary">${r.hostsObserved==null?"Resultado pendiente":`${r.hostsObserved} observadas`}</span>${(r.warnings||[]).map(w=>`<span class="cell-secondary">${esc(warning[w.code]||"Revisar resultado de la fase")}</span>`).join("")}</td><td>${r.discoveryComplete?"Presencia completada":"Presencia pendiente o parcial"}<span class="cell-secondary">Servicios: ${esc(({COMPLETE:"comprobados",PARTIAL:"parciales",NOT_DUE:"sin revisión pendiente",NOT_RUN:"pendientes"})[r.identificationStatus]||"pendientes")}</span><span class="cell-secondary">${esc((r.methods||[]).join(" + "))}</span></td><td>${date(r.startedAtUtc)}<span class="cell-secondary">${date(r.completedAtUtc)}</span></td><td>${admin()&&["QUEUED","RUNNING"].includes(r.status)?`<button type="button" class="secondary-button compact" data-network-cancel="${esc(r.id)}">Detener sondeo</button>`:"—"}</td></tr>`).join(""):'<tr><td colspan="5" class="empty-state">Todavía no hay sondeos registrados para este filtro.</td></tr>'}</tbody></table></div></details></article>`;
  }
  function dialog(title,content){
    const previous=document.activeElement,modal=document.createElement("dialog");modal.className="network-dialog";modal.setAttribute("aria-label",title);
    modal.innerHTML=`<div class="panel-heading"><h3>${esc(title)}</h3><button type="button" class="secondary-button compact" data-close>Cerrar</button></div><div class="network-dialog-body">${content}</div>`;
    document.body.appendChild(modal);modal.querySelector("[data-close]").addEventListener("click",()=>modal.close());modal.addEventListener("close",()=>{modal.remove();previous?.focus();});modal.showModal();return modal;
  }
  function configure(code){
    if(!admin())return;const scope=state.network?.scopes?.find(s=>s.code===code);if(!scope)return;
    const modal=dialog(`Programación · ${scope.displayName}`,`<form class="network-schedule-form"><p class="wide">${esc(scope.network)} · gateway ${esc(scope.gateway)}. Cambiar esta configuración cancela los sondeos pendientes de esta red.</p><label class="wide network-check"><input type="checkbox" name="automatic" ${scope.automaticEnabled?"checked":""}> Sondear automáticamente esta red</label><label class="field"><span>Presencia · cada cuántos minutos</span><input name="interval" type="number" min="5" max="10080" value="${scope.intervalMinutes}" required></label><label class="field"><span>Servicios · cada cuántas horas</span><input name="identification" type="number" min="1" max="168" value="${scope.identificationIntervalHours}" required></label><label class="field wide"><span>IP excluidas de los sondeos · una por línea</span><textarea name="exclusions" rows="4" maxlength="2200" placeholder="Solo direcciones de esta red">${esc((scope.excludedAddresses||[]).join("\n"))}</textarea></label><p class="wide">Los dispositivos nuevos se identifican en su primera detección. Pausar esta programación conserva el inventario y permite verificaciones manuales.</p><p class="wide" data-message role="status"></p><div class="wide network-form-actions"><button type="button" class="secondary-button" data-cancel>Cancelar</button><button type="submit" class="primary-button">Guardar programación</button></div></form>`);
    const form=modal.querySelector("form");form.querySelector("[data-cancel]").addEventListener("click",()=>modal.close());
    form.addEventListener("submit",async e=>{
      e.preventDefault();if(!admin())return;const button=form.querySelector('[type="submit"]');button.disabled=true;
      try{
        const f=form.elements,request={automaticEnabled:f.automatic.checked,intervalMinutes:+f.interval.value,identificationIntervalHours:+f.identification.value,excludedAddresses:f.exclusions.value.split(/\r?\n/).map(v=>v.trim()).filter(Boolean),expectedRevision:scope.configurationRevision};
        await api(`/api/v1/admin/network-discovery/scopes/${encodeURIComponent(code)}`,{method:"PUT",headers:{"Content-Type":"application/json","X-Atlas-Request":"dashboard"},body:JSON.stringify(request)});
        modal.close();await loadNetworkDiscovery();render();showToast("Programación guardada.");
      }catch(error){form.querySelector("[data-message]").textContent=error.code==="network_revision_conflict"?"La configuración cambió. Actualiza la vista y vuelve a abrir este formulario.":"No se guardaron los cambios. Revisa los intervalos y que las IP pertenezcan a esta red.";}
      finally{button.disabled=false;}
    });
  }
  async function history(id){
    if(!canRead())return;const modal=dialog("Historial de red",'<p role="status">Consultando evidencia…</p>');
    try{
      const items=await api(`/api/v1/admin/network-discovery/assets/${encodeURIComponent(id)}/history`);if(!modal.isConnected)return;
      modal.querySelector(".network-dialog-body").innerHTML=items.length?`<p>Hasta 100 observaciones recientes. La evidencia anterior se conserva cuando cambia la identidad de una dirección.</p><ol class="network-history">${items.map(i=>{const v=i.evidence;return `<li><strong>${esc(event[i.eventType]||"Observación registrada")}</strong><span class="cell-secondary">${date(i.occurredAtUtc)}</span><p>IP: ${esc(v.ipAddress)}${v.macAddress?` · MAC: ${esc(v.macAddress)}`:""}${v.previousMac?` · MAC anterior: ${esc(v.previousMac)}`:""}</p>${v.identificationBasis?`<p>${esc(basis[v.identificationBasis]||"Por identificar")}</p>`:""}${v.sources?.length?`<p>Fuentes: ${esc(v.sources.join(" + "))}</p>`:""}${v.ports?.length?`<p>Servicios observados: ${esc(v.ports.map(p=>`${p.Port||p.port}/${p.Protocol||p.protocol}`).join(", "))}</p>`:""}</li>`;}).join("")}</ol>`:"<p>Este registro todavía no tiene observaciones en el historial nuevo.</p>";
    }catch{modal.querySelector(".network-dialog-body").textContent="No fue posible consultar el historial. Intenta nuevamente.";}
  }
  view.addEventListener("click",async e=>{
    const config=e.target.closest("[data-network-config]"),record=e.target.closest("[data-network-history]"),cancel=e.target.closest("[data-network-cancel]");
    if(config)configure(config.dataset.networkConfig);else if(record)await history(record.dataset.networkHistory);
    else if(cancel&&admin()){
      cancel.disabled=true;
      try{await api(`/api/v1/admin/network-discovery/scans/${encodeURIComponent(cancel.dataset.networkCancel)}/cancel`,{method:"POST",headers:{"X-Atlas-Request":"dashboard"}});await loadNetworkDiscovery();render();showToast("Sondeo detenido. Se conserva el historial.");}
      catch{showToast("No fue posible detener el sondeo; actualiza su estado.");cancel.disabled=false;}
    }
  });
  new MutationObserver(render).observe(document.getElementById("networkScopeCards"),{childList:true});
  render();globalThis.AtlasNetworkWorkspace={render,history};
})();
