"use strict";

const custodyLabels = {UNREGISTERED:"Asignación por registrar",IN_USE:"En uso",RECEIVED:"Recibido por Soporte",PREPARING:"En preparación",AVAILABLE:"Disponible"};
const custodyActions = {LINK_IDENTITY:"Vincular identidad reinstalada",REGISTER:"Registrar asignación actual",RETURN:"Registrar devolución",START_PREPARATION:"Iniciar preparación",SAVE_PREPARATION:"Guardar avance",MAKE_AVAILABLE:"Validar y marcar disponible",REOPEN_PREPARATION:"Volver a preparación",REVIEW_CUSTODY:"Actualizar custodia y revisión",ASSIGN:"Registrar nueva entrega"};
const custodyChecks = {information:["Información resguardada","Referencia de la copia final y recuperación comprobada; destino autorizado de la información del área. V4 pp.9–10."],access:["Accesos anteriores revisados","Referencia de la baja de accesos o justificación de que no aplica. V4 pp.13–15,23–26."],sanitization:["Limpieza y preparación","Método, herramienta, fecha y resultado verificado de la limpieza de datos; configuración institucional instalada."],hardware:["Funcionamiento y accesorios","Pruebas de hardware, condición y accesorios que se entregarán. V4 pp.6,16."],security:["Protección y actualizaciones","Comprobación de protección, cifrado y actualizaciones según la configuración institucional. V4 pp.12–13."],software:["Software y licencias","Aplicaciones autorizadas y revisión de las licencias necesarias. V4 p.16."],atlas:["Agente e inventario ATLAS","Último reporte y coincidencia con la identidad y serie del equipo. Si la reinstalación cambió la identidad, resolver la vinculación antes de entregar."],backup:["Copias preparadas para la entrega","Herramienta, carpetas, destino y permisos revisados. No usar las credenciales ni el destino privado del colaborador anterior. V4 pp.9–10."]};
const custodyEscape = v => escapeHtml(String(v ?? ""));
let custodyBadgeRequest=0;
function custodyWarning(code){return ({missing_user:'Sin usuario reportado',stale_user:'Usuario sin reporte reciente (7 días)',generic_user:'Cuenta genérica o técnica',unconfirmed_session:'Sesión local sin confirmar',shared_workstation:'Trabajo en carpeta compartida: revisar responsable',missing_location:'Ubicación pendiente'})[code] || code;}
function custodyReviewDefault(current){const date=current && Date.parse(current)>Date.now()?new Date(current):new Date(Date.now()+7*86400000);const local=new Date(date.getTime()-date.getTimezoneOffset()*60000);return local.toISOString().slice(0,16);}

function custodyReturnFeedback(form) {
  const summary=form.querySelector('[data-return-feedback]'),fields=[...form.querySelectorAll('[required]')];
  let attempted=false;
  const labels=new Map(fields.map(input=>[input.name,input.labels[0].textContent.trim()]));
  for(const input of fields){
    input.setAttribute('aria-label',labels.get(input.name));
    const error=document.createElement('span');error.id=`custody-error-${input.name}`;error.className='custody-field-error';error.hidden=true;
    input.after(error);input.setAttribute('aria-describedby',error.id);
  }
  function validateField(input){
    const value=input.value.trim();
    if(!value)return 'Completa este campo.';
    if(input.name==='reviewDueUtc'){
      const due=Date.parse(value),now=Date.now();
      return !Number.isFinite(due)||due<=now||due>now+90*86400000?'Indica una fecha futura dentro de los próximos 90 días.':'';
    }
    if(value.length<2)return 'Escribe al menos 2 caracteres.';
    if(value.length>input.maxLength)return `Usa como máximo ${input.maxLength} caracteres.`;
    if(/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f-\u009f]/.test(value))return 'Elimina los caracteres de control del texto.';
    return '';
  }
  function render(errors,message,focus){
    for(const input of fields){const text=errors.get(input.name)||'',error=form.querySelector(`#custody-error-${input.name}`);error.textContent=text;error.hidden=!text;if(text)input.setAttribute('aria-invalid','true');else input.removeAttribute('aria-invalid');}
    summary.hidden=!message;
    summary.innerHTML=message?`<strong>${custodyEscape(message)}</strong>${errors.size?`<ul>${[...errors].map(([name,text])=>`<li><button type="button" data-return-field="${name}">${custodyEscape(labels.get(name))}</button><span>${custodyEscape(text)}</span></li>`).join('')}</ul>`:''}`:'';
    if(focus && message){summary.focus({preventScroll:true});summary.scrollIntoView({block:'nearest'});}
  }
  function validate(focus=false){
    const errors=new Map(fields.map(input=>[input.name,validateField(input)]).filter(([,text])=>text));
    render(errors,errors.size?'La devolución todavía no se ha registrado. Revisa estos campos:':'',focus);
    return errors.size===0;
  }
  summary.addEventListener('click',event=>{const name=event.target.closest('[data-return-field]')?.dataset.returnField;if(name)form.elements[name]?.focus();});
  form.addEventListener('input',event=>{if(attempted && fields.includes(event.target))validate();});
  return {
    validate(){attempted=true;return validate(true);},
    saving(){render(new Map(),'Registrando devolución…',false);},
    serverError(error){
      if(error.status===409 || error.status>=500 || !error.status)attempted=false;
      const field=({invalid_previous_assignee:'previousAssignee',invalid_custodian:'custodian',invalid_location:'location',invalid_receipt_reference:'receiptReference',invalid_condition:'condition',invalid_accessories:'accessories',invalid_reason:'reason',invalid_review_date:'reviewDueUtc'})[error.code];
      const message=custodyError(error.code);
      render(new Map(field?[[field,field==='reviewDueUtc'?message:'Revisa este dato; el servidor no lo aceptó.']]:[]),message,true);
    }
  };
}

function assetCustodySection(device) {
  return `<section class="detail-section custody-summary"><h3>Asignación y custodia</h3><div data-custody-summary="${custodyEscape(device.id)}">Consultar responsable y estado de preparación.</div><p>El usuario observado en Windows se mantiene separado de la entrega formal.</p><button type="button" class="secondary-button" data-custody-open="${custodyEscape(device.id)}">Asignación, devolución e historial</button></section>`;
}
async function refreshCustodyBadges() {
  const nodes=[...document.querySelectorAll('[data-custody-summary]')],ids=[...new Set(nodes.map(n=>n.dataset.custodySummary))];
  if(!ids.length)return;
  const generation=++custodyBadgeRequest;
  try {
    const rows=await api(`/api/v1/admin/devices/custody?ids=${ids.map(encodeURIComponent).join(',')}`);
    if(generation!==custodyBadgeRequest)return;
    for(const node of nodes)if(node.isConnected){const row=rows.find(r=>r.deviceId===node.dataset.custodySummary);
      node.innerHTML=`<span class="custody-state">${custodyEscape(custodyLabels[row?.state || 'UNREGISTERED'])}</span>${row?.assignedTo?`<span class="cell-secondary">Asignado: ${custodyEscape(row.assignedTo)}</span>`:''}${row?.custodian?`<span class="cell-secondary">Custodia: ${custodyEscape(row.custodian)}</span>`:''}${row?.reviewDueUtc && Date.parse(row.reviewDueUtc)<Date.now()?'<span class="cell-secondary custody-overdue">Revisión de custodia vencida</span>':''}`;
    }
  }catch{for(const node of nodes)if(node.isConnected)node.textContent='Custodia no disponible; consulta el historial para reintentar.';}
}
function custodyError(code) {
  return ({custody_already_registered:"Este equipo ya tiene una asignación. Se conservó el registro existente.",custody_suggestion_changed:"Cambió el usuario o la ubicación propuestos. Consulta nuevamente antes de confirmar.",custody_review_required:"Confirma la revisión conjunta antes de registrar las asignaciones.",identity_verification_required:"No hay una identidad nueva elegible con la misma serie y agencia. Comprueba físicamente el equipo; no se vincula solo por nombre.",custody_changed:"La ficha o sus controles de copia cambiaron. Vuelve al resumen y revisa los datos actuales antes de guardar.",device_retired:"Esta identidad está retirada. Su historial sigue disponible, pero no puede reasignarse.",device_not_found:"El equipo ya no está disponible.",invalid_custody_transition:"El equipo cambió de estado. Actualiza el resumen.",preparation_incomplete:"Completa todas las comprobaciones antes de marcar el equipo disponible.",required_preparation_check:"Esta comprobación es necesaria para preparar una nueva entrega.",backup_review_required:"Confirma la revisión de carpetas, destino y permisos para la nueva entrega.",backup_control_required:"Configura y activa un control de copias para este equipo antes de entregarlo.",backup_configuration_required:"Revisa que el proveedor y el destino de los controles coincidan con la configuración del equipo.",invalid_review_date:"Indica una fecha de revisión futura, dentro de los próximos 90 días."})[code] || (code?.startsWith('invalid_')?"Completa los datos requeridos y las referencias de las comprobaciones.":"No se pudo confirmar el cambio. Vuelve al resumen para comprobar si se guardó antes de repetir.");
}

async function openAssetCustody(id) {
  if(document.querySelector('.custody-dialog'))return;
  const focus=document.activeElement,dialog=document.createElement('dialog');dialog.className='custody-dialog';dialog.setAttribute('aria-labelledby','custodyTitle');
  dialog.innerHTML=`<header><div><span class="eyebrow">Activos · Asignación y custodia</span><h2 id="custodyTitle">Consultando equipo…</h2></div><button type="button" class="secondary-button" data-close>Cerrar</button></header><div class="custody-body"><p role="status" data-status>Cargando…</p><div data-content></div></div>`;
  document.body.append(dialog);dialog.showModal();let view,busy=false;
  const status=dialog.querySelector('[data-status]'),content=dialog.querySelector('[data-content]');
  dialog.querySelector('[data-close]').addEventListener('click',()=>{if(!busy)dialog.close();});
  dialog.addEventListener('cancel',e=>{if(busy)e.preventDefault();});dialog.addEventListener('close',()=>{dialog.remove();if(focus?.isConnected)focus.focus();});
  const button=(action,label=custodyActions[action],primary=false)=>`<button type="button" class="${primary?'primary':'secondary'}-button" data-action="${action}">${custodyEscape(label)}</button>`;
  const field=(name,label,value='',max=160)=>`<label>${label}<input name="${name}" value="${custodyEscape(value)}" maxlength="${max}" required></label>`;
  const area=(name,label,value='',required=true,max=1000)=>`<label>${label}<textarea name="${name}" rows="3" maxlength="${max}" ${required?'required':''}>${custodyEscape(value)}</textarea></label>`;
  function backupContext(){return `<section class="custody-backups"><h3>Seguimiento de copias</h3><p>${view.custody.backupsPaused?'Pausado por devolución. Las tareas reales de Acronis o Atlas Backup continúan según su configuración.':'Las tareas reales se administran en la herramienta de respaldo.'}</p>${view.backups.length?view.backups.map(b=>`<article><strong>${custodyEscape(b.code)} · ${custodyEscape(b.provider)}</strong><span>${custodyEscape(b.scope)}</span><span>Destino: ${custodyEscape(b.destination || 'Pendiente de configurar')}</span><small>${b.enabled && ['ACTIVE','APPROVED'].includes(b.status)?'Seguimiento activo':'Sin seguimiento activo'}${b.destinationValid?'':' · Revisar destino'}</small></article>`).join(''):`<p>${view.backupProvider==='SHARED_FOLDER'?'Trabajo en carpeta de red; revisar la protección del recurso compartido.':'Sin control de copias configurado. Será necesario configurarlo antes de una nueva entrega.'}</p>`}</section>`;}
  function renderSummary(){
    const s=view.custody,admin=state.session?.role==='Administrator',proposed=s.state==='UNREGISTERED'?view.suggestion:null;
    dialog.querySelector('h2').textContent=view.machineName;
    status.textContent=`${view.agencyCode} · ${view.serialNumber || 'Serie no reportada'} · ${view.enabled?custodyLabels[s.state]:'Identidad retirada'}`;
    const actions=s.state==='UNREGISTERED'?['REGISTER','RETURN']:s.state==='IN_USE'?['RETURN']:s.state==='RECEIVED'?['START_PREPARATION','REVIEW_CUSTODY']:s.state==='PREPARING'?['SAVE_PREPARATION','REVIEW_CUSTODY']:['ASSIGN','REOPEN_PREPARATION','REVIEW_CUSTODY'];
    if(s.state==='PREPARING')actions.push('LINK_IDENTITY');
    const allowedActions=view.enabled?actions:actions.filter(a=>a==='LINK_IDENTITY');
    content.innerHTML=`<div class="custody-facts"><div><span>${proposed?'Responsable propuesto':'Responsable asignado'}</span><strong>${custodyEscape(s.assignedTo || proposed?.assignedTo || 'Sin asignación registrada')}</strong></div><div><span>${proposed?'Custodio propuesto':'Custodio actual'}</span><strong>${custodyEscape(s.custodian || proposed?.custodian || 'Por registrar')}</strong></div><div><span>${proposed?'Ubicación propuesta · agencia':'Ubicación física'}</span><strong>${custodyEscape(s.location || proposed?.location || 'Por registrar')}</strong></div><div><span>Próxima revisión de custodia</span><strong>${s.reviewDueUtc?custodyEscape(formatDate(s.reviewDueUtc)):'No aplica'}</strong></div></div>
      ${s.reviewDueUtc&&Date.parse(s.reviewDueUtc)<Date.now()?'<p class="custody-overdue">La revisión está vencida. Actualiza la custodia y define el siguiente paso. El seguimiento no se reactiva automáticamente.</p>':''}
      ${proposed?`<p>Propuesta a partir del usuario reportado ${proposed.observedAtUtc?custodyEscape(formatDate(proposed.observedAtUtc)):'sin fecha'} y la ubicación registrada de la agencia. Confirma o corrige los datos antes de guardar.</p>${proposed.warnings?.length?`<p class="custody-overdue">${custodyEscape(proposed.warnings.map(custodyWarning).join(' · '))}</p>`:''}${admin?'<button type="button" class="secondary-button" data-all-pending>Revisar todos los equipos pendientes</button>':''}`:''}<p>La asignación formal no cambia cuando un técnico inicia sesión en Windows. ATLAS registra el trabajo; la preparación física y la gestión de accesos se realizan fuera del portal.</p>
      ${admin?`<div class="custody-actions">${allowedActions.map((a,i)=>button(a,a==='SAVE_PREPARATION'?'Revisar preparación':custodyActions[a],i===0)).join('')}</div>`:''}${backupContext()}
      <h3>Historial de asignaciones y preparación</h3>${view.events.length?view.events.map(e=>`<details class="custody-event"><summary>${custodyEscape(custodyActions[e.action] || e.action)} · ${custodyEscape(formatDate(e.occurredAtUtc))}</summary><p>Registrado por ${custodyEscape(e.actor)} · ${custodyEscape(custodyLabels[e.detail.before?.state] || '')} → ${custodyEscape(custodyLabels[e.detail.after?.state] || '')}</p><p>${custodyEscape(e.detail.reason)}</p><p>Asignación: ${custodyEscape(e.detail.after?.assignedTo || 'Sin asignar')} · Custodia: ${custodyEscape(e.detail.after?.custodian)} · ${custodyEscape(e.detail.after?.location)}</p>${e.detail.after?.receipt?`<p>Devolución de ${custodyEscape(e.detail.after.receipt.previousAssignee)} · ${custodyEscape(e.detail.after.receipt.reference)}</p><p>Condición: ${custodyEscape(e.detail.after.receipt.condition)}</p><p>Accesorios: ${custodyEscape(e.detail.after.receipt.accessories)}</p>`:''}${e.detail.after?.preparation?`<p>Ejecutó: ${custodyEscape(e.detail.after.preparation.executedBy)} · Validó: ${custodyEscape(e.detail.after.preparation.validatedBy || 'Pendiente')}</p>${Object.entries(e.detail.after.preparation.checks || {}).map(([key,c])=>`<p><strong>${custodyEscape(custodyChecks[key]?.[0] || key)}: ${{DONE:'Comprobado',NA:'No aplica',PENDING:'Pendiente'}[c.result] || 'Pendiente'}</strong><br>${custodyEscape(c.reference)}</p>`).join('')}`:''}${e.detail.after?.pauseReason?`<p>Pausa: ${custodyEscape(e.detail.after.pauseReason)}</p>`:''}</details>`).join(''):'<p>No hay entregas ni devoluciones registradas. El usuario observado no se toma como evidencia de asignación.</p>'}`;
  }
  async function load(){status.textContent='Consultando estado actual…';content.innerHTML='';try{const current=await api(`/api/v1/admin/devices/${encodeURIComponent(id)}/custody`);if(!dialog.open)return;view=current;renderSummary();}catch(error){status.textContent=custodyError(error.code);content.innerHTML=button('RELOAD','Volver a consultar');}}
  function renderForm(action){
    const s=view.custody,proposal=view.suggestion;let fields='';
    if(['REGISTER','ASSIGN'].includes(action))fields+=`<p>Datos propuestos del inventario; comprueba quién recibe el equipo y su ubicación.${proposal?.warnings?.length?' '+custodyEscape(proposal.warnings.map(custodyWarning).join(' · ')):''}</p>`+field('assignedTo','Responsable que recibe',proposal?.assignedTo || '')+field('custodian','Custodio después de la entrega',proposal?.custodian || '')+field('location','Ubicación de uso',proposal?.location || s.location,200);
    if(action==='RETURN')fields+=field('previousAssignee','Colaborador que devuelve',s.assignedTo || proposal?.assignedTo || '')+field('custodian','Recibido por / custodio','Soporte Técnico')+field('location','Ubicación física',s.location,200)+field('receiptReference','Referencia de devolución o acta','',200)+area('condition','Condición física y observaciones')+area('accessories','Accesorios recibidos')+`<label class="custody-check"><input type="checkbox" name="pauseBackups" checked>Pausar seguimiento de copias en ATLAS durante la preparación</label><p>Conserva planes y evidencias. No detiene ni borra las copias de Acronis o Atlas Backup.</p>`;
    if(action==='REVIEW_CUSTODY')fields+=field('custodian','Custodio actual',s.custodian)+field('location','Ubicación física',s.location,200);
    if(['RETURN','REVIEW_CUSTODY'].includes(action))fields+=`<label>Próxima revisión de custodia<input type="datetime-local" name="reviewDueUtc" value="${custodyReviewDefault(action==='REVIEW_CUSTODY'?s.reviewDueUtc:null)}" required></label><p>Se propone revisar en 7 días cuando no hay una fecha futura registrada. Puedes cambiarla dentro de los próximos 90 días. La pausa no se levantará por vencimiento.</p>`;
    if(action==='SAVE_PREPARATION'){
      fields+=`<p>Registra lo que se ha comprobado. Puedes guardar el avance con tareas pendientes. Las referencias pueden ser números de acta, expediente o informe; no escribas contraseñas ni secretos.</p>${field('executedBy','Preparación ejecutada por',s.preparation?.executedBy || '')}`;
      fields+=Object.entries(custodyChecks).map(([key,[label,help]])=>{const c=s.preparation?.checks?.[key]||{result:'PENDING',reference:''};return `<fieldset class="custody-prep"><legend>${label}</legend><p>${help}</p><label>Resultado<select name="check_${key}"><option value="PENDING" ${c.result==='PENDING'?'selected':''}>Pendiente</option><option value="DONE" ${c.result==='DONE'?'selected':''}>Comprobado</option>${['access','software'].includes(key)?`<option value="NA" ${c.result==='NA'?'selected':''}>No aplica, justificado</option>`:''}</select></label>${area('ref_'+key,'Referencia y resultado de la comprobación',c.reference,false)}</fieldset>`;}).join('');
      fields+=field('validatedBy','Validación realizada por · requerida al marcar disponible',s.preparation?.validatedBy || '').replace(' required','');
    }
    if(action==='LINK_IDENTITY')fields+=`<p>Selecciona la nueva inscripción del mismo equipo después de reinstalar Windows. Solo se ofrecen identidades activas de la misma agencia y serie, sin asignación ni controles propios. La credencial anterior quedará retirada y sus controles conservarán el historial. Las comprobaciones de preparación deberán realizarse de nuevo.</p><label>Nueva identidad<select name="newDeviceId" required><option value="">Selecciona la identidad verificada</option>${(view.identityCandidates || []).map(d=>`<option value="${custodyEscape(d.deviceId)}">${custodyEscape(d.machineName)} · Serie ${custodyEscape(d.serialNumber)}</option>`).join('')}</select></label>${view.identityCandidates?.length?'':'<p>No hay candidatos elegibles. Comprueba la inscripción y el reporte de inventario del equipo reinstalado antes de continuar.</p>'}<label class="custody-check"><input type="checkbox" name="physicalIdentityVerified" required>Comprobé físicamente que la nueva identidad corresponde a esta laptop y autorizo retirar la credencial anterior.</label>`;
    if(action==='ASSIGN')fields+=backupContext()+`<label class="custody-check"><input type="checkbox" name="backupReviewed" required>Revisé carpetas, destino y permisos para el nuevo colaborador. Autorizo reanudar el seguimiento de los controles activos.</label><p>Se conservará el historial anterior. ATLAS esperará una nueva comprobación de copia y restauración para esta asignación.</p>`;
    fields+=area('reason',['REGISTER','ASSIGN'].includes(action)?'Referencia de entrega y observaciones':'Motivo o referencia del cambio',action==='REGISTER'?'Registro inicial revisado con el inventario de ATLAS.':'');
    content.innerHTML=`<form class="custody-form" ${action==='RETURN'?'novalidate':''}><h3>${custodyActions[action]}</h3>${action==='RETURN'?'<p>Completa los datos de recepción y pulsa <strong>Confirmar devolución</strong> al final. Todos los campos de texto y la fecha son obligatorios. Si no se reciben accesorios, indícalo expresamente.</p>':''}<div class="custody-fields">${fields}</div>${action==='RETURN'?'<div class="custody-return-feedback" data-return-feedback role="alert" tabindex="-1" hidden></div>':''}<footer><button type="button" class="secondary-button" data-action="CANCEL_FORM">Cancelar</button><button type="submit" name="saveAction" value="${action}" class="primary-button">${action==='RETURN'?'Confirmar devolución':custodyActions[action]}</button>${action==='SAVE_PREPARATION'?'<button type="submit" name="saveAction" value="MAKE_AVAILABLE" class="secondary-button">Validar y marcar disponible</button>':''}</footer></form>`;
    const form=content.querySelector('form'),returnFeedback=action==='RETURN'?custodyReturnFeedback(form):null;form.querySelector('input,textarea,select')?.focus();
    if(['REGISTER','ASSIGN'].includes(action)){const assignee=form.elements.assignedTo,custodian=form.elements.custodian;let previous=assignee.value;assignee.addEventListener('input',()=>{if(custodian.value===previous)custodian.value=assignee.value;previous=assignee.value;});}
    form.addEventListener('submit',async event=>{
      event.preventDefault();if(busy)return;if(returnFeedback && !returnFeedback.validate())return;const f=new FormData(form),saveAction=event.submitter?.value || action;
      const get=name=>String(f.get(name)||'').trim();
      const request={action:saveAction,expectedVersion:view.version,reason:get('reason'),assignedTo:get('assignedTo')||null,custodian:get('custodian')||null,location:get('location')||null};
      if(action==='REGISTER' && proposal)request.suggestionVersion=proposal.version;
      if(action==='RETURN'){request.receipt={previousAssignee:get('previousAssignee'),condition:get('condition'),accessories:get('accessories'),reference:get('receiptReference')};request.pauseBackups=f.has('pauseBackups');}
      if(['RETURN','REVIEW_CUSTODY'].includes(action)){const due=new Date(get('reviewDueUtc'));if(!Number.isFinite(due.getTime())){status.textContent=custodyError('invalid_review_date');return;}request.reviewDueUtc=due.toISOString();}
      if(action==='SAVE_PREPARATION')request.preparation={executedBy:get('executedBy'),validatedBy:get('validatedBy')||null,checks:Object.fromEntries(Object.keys(custodyChecks).map(k=>[k,{result:get('check_'+k),reference:get('ref_'+k)}]))};
      if(action==='LINK_IDENTITY'){request.newDeviceId=get('newDeviceId');request.physicalIdentityVerified=f.has('physicalIdentityVerified');}
      if(action==='ASSIGN')request.backupReviewed=f.has('backupReviewed');
      busy=true;dialog.querySelectorAll('button,input,textarea,select').forEach(e=>e.disabled=true);status.textContent='Guardando registro…';returnFeedback?.saving();
      try{await api(`/api/v1/admin/devices/${encodeURIComponent(id)}/custody`,{method:'POST',headers:{'Content-Type':'application/json','X-Atlas-Request':'dashboard'},body:JSON.stringify(request)});await load();showToast('Registro guardado. El historial del activo se conserva.');void refreshCustodyBadges();try{await refreshAll();}catch{showToast('Registro guardado; actualiza el portal para refrescar los indicadores.');}}
      catch(error){status.textContent=custodyError(error.code);if(returnFeedback)returnFeedback.serverError(error);else status.scrollIntoView({block:"center"});dialog.querySelectorAll('button,input,textarea,select').forEach(e=>e.disabled=false);if(error.status===409 || error.status>=500 || !error.status){form.querySelectorAll('[type=submit]').forEach(e=>e.disabled=true);}}
      finally{busy=false;dialog.querySelector('[data-close]').disabled=false;}
    });
  }
  content.addEventListener('click',event=>{if(event.target.closest('[data-all-pending]') && !busy){dialog.addEventListener('close',()=>void openAssetCustodyBulk(),{once:true});dialog.close();return;}const action=event.target.closest('[data-action]')?.dataset.action;if(!action||busy)return;if(action==='RELOAD'||action==='CANCEL_FORM'){void load();return;}if(state.session?.role==='Administrator'&&(view.enabled || action==='LINK_IDENTITY'))renderForm(action);});
  await load();
}
document.addEventListener('click',event=>{const el=event.target.closest('[data-custody-open]');if(el){event.stopPropagation();void openAssetCustody(el.dataset.custodyOpen);}});
