"use strict";

async function openAssetCustodyBulk() {
  if(state.session?.role!=="Administrator" || document.querySelector('.custody-dialog'))return;
  const previousFocus=document.activeElement,dialog=document.createElement('dialog');
  dialog.className='custody-dialog custody-bulk-dialog';dialog.setAttribute('aria-labelledby','custodyBulkTitle');
  dialog.innerHTML=`<header><div><span class="eyebrow">Activos · Asignaciones existentes</span><h2 id="custodyBulkTitle">Completar asignaciones en conjunto</h2></div><button type="button" class="secondary-button" data-close>Cerrar</button></header><div class="custody-body"><p role="status" data-status></p><div data-content></div></div>`;
  document.body.append(dialog);dialog.showModal();
  const status=dialog.querySelector('[data-status]'),content=dialog.querySelector('[data-content]');
  let rows=[],busy=false,page=1,agency='',search='';const pageSize=50;
  const close=()=>{if(!busy)dialog.close();};dialog.querySelector('[data-close]').onclick=close;
  dialog.addEventListener('cancel',e=>{if(busy)e.preventDefault();});
  dialog.addEventListener('close',()=>{dialog.remove();if(previousFocus?.isConnected)previousFocus.focus();});
  const esc=custodyEscape;
  const complete=r=>['assignedTo','custodian','location'].every(k=>r[k]?.trim().length>=2);
  const filtered=()=>rows.filter(r=>(!agency || r.agencyCode===agency) && (!search || `${r.machineName} ${r.assignedTo} ${r.agencyName}`.toLocaleLowerCase().includes(search)));
  const selected=()=>rows.filter(r=>r.selected);
  function updateCount(){const count=selected().length;const button=content.querySelector('[data-review]');if(button){button.disabled=!count;button.textContent=`Revisar ${count} asignaciones`;}
    const label=content.querySelector('[data-selected]');if(label)label.textContent=`${count} seleccionados en todas las páginas${selected().some(r=>!filtered().includes(r))?' · Hay seleccionados fuera del filtro actual':''}`;}
  function renderRows(){
    const list=filtered(),pages=Math.max(1,Math.ceil(list.length/pageSize));page=Math.min(page,pages);
    content.querySelector('[data-rows]').innerHTML=list.slice((page-1)*pageSize,page*pageSize).map(r=>`<tr data-id="${esc(r.deviceId)}"><td><input type="checkbox" data-select aria-label="Seleccionar ${esc(r.machineName)}" ${r.selected?'checked':''} ${complete(r)?'':'disabled'}></td><td><strong>${esc(r.machineName)}</strong><small>${esc(r.agencyName)}</small><small>Usuario reportado: ${esc(r.observedAccount || r.assignedTo || 'No disponible')}</small><small>${r.observedAtUtc?esc(formatDate(r.observedAtUtc)):'Sin fecha de reporte'}</small></td>${[['assignedTo','Responsable'],['custodian','Custodio'],['location','Ubicación']].map(([key,label])=>`<td><label><span>${label}</span><input data-field="${key}" aria-label="${label} de ${esc(r.machineName)}" maxlength="${key==='location'?200:160}" value="${esc(r[key])}"></label></td>`).join('')}<td><span class="custody-state">${r.ready?'Propuesto':'Revisar'}</span><small>${r.warnings?.length?esc(r.warnings.map(custodyWarning).join(' · ')):'Datos recientes del agente'}</small></td></tr>`).join('') || '<tr><td colspan="6">No hay equipos pendientes con estos filtros.</td></tr>';
    content.querySelector('[data-page-label]').textContent=`Página ${page} de ${pages} · ${list.length} pendientes`;
    content.querySelector('[data-prev]').disabled=page===1;content.querySelector('[data-next]').disabled=page===pages;updateCount();
  }
  function renderList(){
    status.textContent=`${rows.length} equipos sin asignación formal. Las asignaciones existentes y sus planes de copia se conservan.`;
    const agencies=[...new Map(rows.map(r=>[r.agencyCode,r.agencyName])).entries()].sort((a,b)=>a[1].localeCompare(b[1]));
    content.innerHTML=`<p>Revisa las propuestas del inventario. Puedes corregir los datos aquí. Los equipos compartidos, usuarios genéricos y reportes antiguos requieren revisión. La ubicación propuesta procede de la agencia; comprueba dónde está el equipo.</p><div class="custody-bulk-tools"><label>Agencia<select data-agency><option value="">Todas las agencias</option>${agencies.map(([code,name])=>`<option value="${esc(code)}" ${agency===code?'selected':''}>${esc(name)}</option>`).join('')}</select></label><label>Buscar equipo o usuario<input type="search" data-search value="${esc(search)}"></label></div><div class="custody-actions"><button type="button" class="secondary-button" data-select-ready>Seleccionar propuestos de todas las páginas</button><button type="button" class="secondary-button" data-clear>Quitar toda la selección</button></div><p data-selected aria-live="polite"></p><div class="custody-bulk-table"><table><thead><tr><th>Incluir</th><th>Equipo / origen</th><th>Responsable</th><th>Custodio</th><th>Ubicación</th><th>Revisión</th></tr></thead><tbody data-rows></tbody></table></div><div class="custody-bulk-pager"><span data-page-label></span><button type="button" class="secondary-button" data-prev>Anterior</button><button type="button" class="secondary-button" data-next>Siguiente</button></div><footer class="custody-bulk-footer"><button type="button" class="secondary-button" data-cancel>Cancelar</button><button type="button" class="primary-button" data-review></button></footer>`;
    content.querySelector('[data-agency]').onchange=e=>{agency=e.target.value;page=1;renderRows();};
    content.querySelector('[data-search]').oninput=e=>{search=e.target.value.trim().toLocaleLowerCase();page=1;renderRows();};
    content.querySelector('[data-select-ready]').onclick=()=>{filtered().filter(r=>r.ready && complete(r)).forEach(r=>r.selected=true);renderRows();};
    content.querySelector('[data-clear]').onclick=()=>{rows.forEach(r=>r.selected=false);renderRows();};
    content.querySelector('[data-prev]').onclick=()=>{page--;renderRows();};content.querySelector('[data-next]').onclick=()=>{page++;renderRows();};
    content.querySelector('[data-cancel]').onclick=close;content.querySelector('[data-review]').onclick=renderReview;
    content.querySelector('[data-rows]').oninput=e=>{const row=rows.find(r=>r.deviceId===e.target.closest('[data-id]')?.dataset.id);if(!row)return;
      if(e.target.dataset.field){row[e.target.dataset.field]=e.target.value;const checkbox=e.target.closest('tr').querySelector('[data-select]');checkbox.disabled=!complete(row);if(!complete(row)){row.selected=false;checkbox.checked=false;}}
      if(e.target.matches('[data-select]'))row.selected=e.target.checked;updateCount();};
    renderRows();
  }
  function renderReview(){
    const chosen=selected();if(!chosen.length || chosen.some(r=>!complete(r)))return;
    status.textContent=`Confirmar ${chosen.length} asignaciones iniciales. Todavía no se ha guardado ningún cambio.`;
    content.innerHTML=`<p>Esta confirmación registra quién tiene actualmente cada equipo. No acredita una entrega anterior ni modifica los planes o resultados de copia.</p><div class="custody-bulk-review">${chosen.map(r=>`<article><strong>${esc(r.machineName)} · ${esc(r.agencyName)}</strong><span>Responsable: ${esc(r.assignedTo)}</span><span>Custodio: ${esc(r.custodian)}</span><span>Ubicación: ${esc(r.location)}</span>${r.warnings.length?`<small>${esc(r.warnings.map(custodyWarning).join(' · '))}</small>`:''}</article>`).join('')}</div><form class="custody-form"><div class="custody-fields"><label>Referencia de la revisión<textarea name="reason" required maxlength="1000">Registro inicial revisado con el inventario de ATLAS.</textarea></label><label class="custody-check"><input type="checkbox" name="reviewed" required>Verifiqué los responsables, custodios y ubicaciones de estos ${chosen.length} equipos.</label></div><footer><button type="button" class="secondary-button" data-back>Volver a la lista</button><button type="button" class="secondary-button" data-cancel>Cancelar</button><button type="submit" class="primary-button">Confirmar ${chosen.length} asignaciones</button></footer></form>`;
    content.querySelector('[data-back]').onclick=renderList;content.querySelector('[data-cancel]').onclick=close;
    content.querySelector('form').onsubmit=async e=>{
      e.preventDefault();if(busy)return;const form=e.currentTarget;if(!form.reportValidity())return;
      const reason=new FormData(form).get('reason').trim();if(reason.length<2)return;
      busy=true;dialog.querySelectorAll('button,input,textarea,select').forEach(el=>el.disabled=true);
      const results=[];let uncertain=false;
      try{
        for(let offset=0;offset<chosen.length;offset+=100){
          status.textContent=`Registrando ${offset+1}–${Math.min(offset+100,chosen.length)} de ${chosen.length}…`;
          const items=chosen.slice(offset,offset+100).map(r=>({deviceId:r.deviceId,suggestionVersion:r.version,assignedTo:r.assignedTo.trim(),custodian:r.custodian.trim(),location:r.location.trim()}));
          const response=await api('/api/v1/admin/devices/custody/register-many',{method:'POST',headers:{'Content-Type':'application/json','X-Atlas-Request':'dashboard'},body:JSON.stringify({items,reviewed:true,reason})});
          if(!Array.isArray(response.results) || response.results.length!==items.length || new Set(response.results.map(r=>r.deviceId)).size!==items.length || response.results.some(r=>!items.some(i=>i.deviceId===r.deviceId) || typeof r.saved!=='boolean'))throw new Error('unconfirmed_batch');
          results.push(...response.results);
        }
      }catch{uncertain=true;}
      finally{busy=false;dialog.querySelector('[data-close]').disabled=false;}
      const saved=results.filter(r=>r.saved).length,failed=results.filter(r=>!r.saved);
      status.textContent=`${saved} asignaciones confirmadas${failed.length?` · ${failed.length} requieren revisión`:''}.`;
      const failedHtml=failed.length?'<h3>Equipos pendientes</h3>'+failed.map(r=>`<p><strong>${esc(chosen.find(x=>x.deviceId===r.deviceId)?.machineName || r.deviceId)}</strong>: ${esc(custodyError(r.error))}</p>`).join(''):'<p>Los registros confirmados conservaron sus controles de copia e historial.</p>';
      content.innerHTML=`${uncertain?'<p role="alert">No se pudo confirmar la respuesta de uno de los grupos. Puede haber registros guardados. Consulta los pendientes antes de continuar; no se repetirá el envío automáticamente.</p>':''}${failedHtml}<div class="custody-actions"><button type="button" class="primary-button" data-reload>Volver a consultar pendientes</button><button type="button" class="secondary-button" data-cancel>Cerrar</button></div>`;
      content.querySelector('[data-reload]').onclick=load;content.querySelector('[data-cancel]').onclick=close;status.scrollIntoView({block:'start'});
      void refreshCustodyBadges();try{await refreshAll();}catch{/* Results remain visible even if a separate dashboard refresh fails. */}
    };
    dialog.scrollTop=0;
  }
  async function load(){
    rows=[];page=1;agency='';search='';content.innerHTML='';status.textContent='Consultando equipos pendientes de todas las páginas…';
    try{
      let after=null;const seen=new Set();
      do{const data=await api(`/api/v1/admin/devices/custody/proposals${after?'?after='+encodeURIComponent(after):''}`);if(!dialog.open)return;
        if(!Array.isArray(data.items))throw new Error('invalid_proposals');
        for(const r of data.items)if(!seen.has(r.deviceId)){seen.add(r.deviceId);rows.push({...r,selected:false});}
        if(data.nextAfter && data.nextAfter===after)throw new Error('invalid_cursor');after=data.nextAfter;
        status.textContent=`${rows.length} pendientes encontrados. Consultando las siguientes páginas…`;
      }while(after);
      renderList();
    }catch{if(!dialog.open)return;rows=[];status.textContent='No se pudo completar la consulta. No se ha cambiado ninguna asignación.';content.innerHTML='<button type="button" class="secondary-button" data-retry>Volver a consultar</button>';content.querySelector('[data-retry]').onclick=load;}
  }
  await load();
}
document.addEventListener('click',e=>{if(e.target.closest('[data-custody-bulk]'))void openAssetCustodyBulk();});
