"use strict";

function nasLifecycleMessage(code) {
  return ({nas_has_active_dependencies:"Todavía hay equipos o controles activos vinculados. Revisa sus destinos y pulsa Volver a comprobar.",
    nas_lifecycle_changed:"Los recursos cambiaron desde la revisión. Pulsa Volver a comprobar antes de continuar.",
    nas_reactivation_conflict:"Ya existe un recurso activo con ese destino o un recurso principal en la agencia. Revisa los datos antes de reactivar.",
    nas_agency_inactive:"Reactiva primero la agencia a la que pertenece este NAS.",
    nas_not_found:"El recurso ya no está disponible.",invalid_nas_lifecycle_request:"Escribe un motivo de entre 3 y 1.000 caracteres."})[code] || "No se pudo completar el cambio. Actualiza la vista y comprueba el estado antes de repetir.";
}

function nasLifecycleButton(id, enabled, scope, label) {
  return `<button type="button" class="secondary-button compact nas-lifecycle-action" data-nas-lifecycle="${escapeHtml(id)}" data-enable="${enabled}" data-scope="${scope}">${escapeHtml(label)}</button>`;
}

async function beginNasLifecycle(id, enabled, scope = "RESOURCE") {
  if (state.session?.role !== "Administrator" || document.querySelector('.nas-lifecycle-dialog')) return;
  const returnFocus = document.activeElement, dialog = document.createElement('dialog');
  dialog.className = 'nas-lifecycle-dialog'; dialog.setAttribute('aria-labelledby','nasLifecycleTitle');
  const title = `${enabled ? 'Reactivar' : 'Dar de baja'} ${scope === 'NAS' ? 'NAS' : 'recurso'}`;
  dialog.innerHTML = `<form><header><div><span class="eyebrow">Almacenamiento NAS</span><h2 id="nasLifecycleTitle">${title}</h2></div><button class="secondary-button" type="button" data-close>Cerrar</button></header>
    <div class="nas-lifecycle-body"><p role="status" data-status>Revisando recursos y dependencias…</p><div data-preview></div>
      <label class="nas-lifecycle-reason" hidden>Motivo de ${enabled?'reactivación':'baja'}<textarea name="reason" rows="3" required minlength="3" maxlength="1000" placeholder="Describe el motivo del cambio" aria-describedby="nasLifecycleHelp"></textarea></label>
      <p id="nasLifecycleHelp" class="nas-lifecycle-help"></p></div>
    <footer><button type="button" class="secondary-button" data-close>Cancelar</button><button type="button" class="secondary-button" data-recheck>Volver a comprobar</button><button type="submit" class="primary-button" aria-describedby="nasLifecycleHelp" disabled>${title}</button></footer></form>`;
  document.body.append(dialog); dialog.showModal();
  let busy = false, reviewing = false, preview;
  const status = dialog.querySelector('[data-status]'), submit = dialog.querySelector('[type=submit]');
  const reasonInput = dialog.querySelector('[name=reason]'), help = dialog.querySelector('#nasLifecycleHelp'), recheck = dialog.querySelector('[data-recheck]');
  dialog.querySelectorAll('[data-close]').forEach(b => b.addEventListener('click',()=>{if(!busy)dialog.close();}));
  dialog.addEventListener('cancel',e=>{if(busy)e.preventDefault();});
  dialog.addEventListener('close',()=>{dialog.remove();if(returnFocus?.isConnected)returnFocus.focus();});
  async function review() {
    if(busy || reviewing || !dialog.open)return;
    reviewing=true;preview=null;submit.disabled=true;recheck.disabled=true;
    status.textContent='Revisando recursos y dependencias…';
    help.textContent='Espera a que termine la comprobación antes de confirmar.';
    try {
    const current = await api(`/api/v1/admin/nas/${encodeURIComponent(id)}/lifecycle?scope=${scope}`);
    if(!dialog.open)return;
    preview=current;
    const changes = preview.resources.filter(r=>r.enabled !== enabled), blocked = !enabled && preview.dependencies.length > 0;
    status.textContent = `${preview.host}:${preview.port} · ${preview.agencyCode} · ${changes.length} recurso(s) por ${enabled?'reactivar':'dar de baja'}`;
    dialog.querySelector('[data-preview]').innerHTML = `<p>${enabled ? 'Los recursos indicados volverán al monitoreo y a la selección de destinos. Su disponibilidad se comprobará con un nuevo sondeo.' : 'Los recursos indicados pasarán a Dados de baja y dejarán de sondearse y ofrecerse como destinos.'}</p>
      <p>Se conservan los archivos, montajes e historial. Esta acción administra el registro en ATLAS; las tareas de copia de los equipos mantienen su configuración.</p>
      <ul class="nas-lifecycle-resources">${changes.map(r=>`<li><strong>${escapeHtml(r.name)}</strong><span>\\\\${escapeHtml(preview.host)}\\${escapeHtml(r.share)}</span></li>`).join('')}</ul>
      ${blocked ? `<div class="nas-lifecycle-blocked"><strong>La baja está pendiente de resolver estos vínculos</strong><p>ATLAS todavía tiene estos registros asociados al NAS. Un mismo equipo puede aparecer en su ficha y en su control de respaldo.</p><ul>${preview.dependencies.map(d=>`<li><strong>${d.kind==='DEVICE'?'Equipo':'Control de respaldo'} · ${escapeHtml(d.name)} · ${escapeHtml(d.agencyCode)}</strong><span>${d.kind==='DEVICE'?'En la ficha del equipo, revisa Política de respaldo → Recurso SMB asignado.':'En Control de copias, revisa el destino de este control; si dejó de aplicar, pausa su seguimiento.'}</span></li>`).join('')}</ul><p>Si el respaldo ya cambió de destino, actualiza ambos registros para que coincidan con la configuración real. Cambiar estos vínculos en ATLAS no modifica la tarea de copia del equipo.</p></div>` : ''}`;
    reasonInput.closest('label').hidden=false;
    help.textContent=blocked?'Puedes escribir el motivo. Para habilitar la baja, resuelve los vínculos y pulsa Volver a comprobar; el texto se conserva.':changes.length?'Revisa los recursos y escribe el motivo antes de confirmar.':'No hay cambios pendientes; el estado ya coincide.';
    submit.disabled = blocked || changes.length === 0;
    submit.textContent = blocked ? 'Baja pendiente de vínculos' : title;
    if(!changes.length)status.textContent += ' · El estado ya coincide.';
    } catch(error) {if(dialog.open){status.textContent=nasLifecycleMessage(error.code);help.textContent='No se pudo comprobar el estado actual. Pulsa Volver a comprobar para reintentar; tu motivo se conserva.';}}
    finally {reviewing=false;recheck.disabled=false;}
  }
  recheck.addEventListener('click',()=>void review());
  dialog.querySelector('form').addEventListener('submit',async event=>{
    event.preventDefault();if(busy || reviewing || submit.disabled || !preview)return;
    const reason=reasonInput.value.trim();
    if(reason.length<3){status.textContent='Escribe el motivo del cambio.';return;}
    busy=true;dialog.querySelectorAll('button,textarea').forEach(el=>el.disabled=true);status.textContent='Guardando el cambio…';
    try {
      await api(`/api/v1/admin/nas/${encodeURIComponent(id)}/lifecycle`,{method:'POST',headers:{'Content-Type':'application/json','X-Atlas-Request':'dashboard'},body:JSON.stringify({scope,enabled,expectedVersion:preview.version,reason})});
      dialog.close();closeNasDetail();
      // Reflect the confirmed state immediately; even a failed refresh must not show retired files.
      const changed=new Set(preview.resources.filter(r=>r.enabled!==enabled).map(r=>r.id));
      state.nas=state.nas.map(n=>changed.has(n.id)?{...n,enabled,retiredAtUtc:enabled?null:new Date().toISOString(),retiredBy:enabled?null:state.session.displayName,retirementReason:enabled?null:reason,lastProbeStatus:enabled?'UNKNOWN':n.lastProbeStatus,lastProbeAtUtc:enabled?null:n.lastProbeAtUtc}:n);
      elements.nasExplorerSelect.value='';state.nasBrowser.nasId='';await loadNasBrowser('');renderNas();
      showToast(enabled?'Reactivación registrada. Pendiente de un nuevo sondeo.':'Baja registrada. El historial se conserva.');
      try {await refreshAll();}catch{showToast('El cambio se guardó. Actualiza la vista para consultar todos los datos.');}
    } catch(error) {preview=null;status.textContent=nasLifecycleMessage(error.code);help.textContent='Vuelve a comprobar el estado antes de confirmar otra vez. El motivo se conserva.';dialog.querySelectorAll('[data-close],[data-recheck],textarea').forEach(el=>el.disabled=false);}
    finally {busy=false;}
  });
  await review();
}

document.addEventListener('click',event=>{
  const action=event.target.closest('[data-nas-lifecycle]');
  if(action)void beginNasLifecycle(action.dataset.nasLifecycle,action.dataset.enable==='true',action.dataset.scope);
  const tab=event.target.closest('[data-nas-status]');
  if(tab){state.nasDirectory.status=tab.dataset.nasStatus;renderNas();}
});
