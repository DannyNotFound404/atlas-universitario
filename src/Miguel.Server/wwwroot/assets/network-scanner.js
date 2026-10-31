"use strict";
(() => {
  const view=document.getElementById("view-network");if(!view)return;
  const el=id=>document.getElementById(id),esc=v=>escapeHtml(String(v??""));
  const scanner=state.networkScanner={mode:"inventory",runId:null,live:null,pending:false,revision:0,lastFetch:0,rangeScope:null};
  const canRead=()=>["Administrator","TechnologyManager","Auditor","Operator"].includes(state.session?.role);
  const admin=()=>state.session?.role==="Administrator";
  const status={QUEUED:"En cola",RUNNING:"Escaneando",SUCCEEDED:"Completado",PARTIAL:"Completado parcialmente",FAILED:"Fallido",TIMED_OUT:"Tiempo agotado",CANCELLED:"Cancelado",INTERRUPTED:"Interrumpido",SKIPPED:"Omitido"};
  const phases={DISCOVERY:"Buscando dispositivos",IDENTIFICATION:"Identificando servicios",NAMES:"Consultando nombres DNS",FINALIZING:"Guardando resultados",FINISHED:"Resultados guardados"};
  const source={DNS:"DNS",SERVICES:"Servicio detectado",RECENT_AGENT:"Agente reciente",NMAP:"Escaneo de red",ARP_CACHE:"Caché local",LEGACY:"Dato anterior"};
  const ipNumber=s=>s.split('.').reduce((n,p)=>n*256+Number(p),0);
  const ipText=n=>[24,16,8,0].map(shift=>(n>>>shift)&255).join('.');
  const active=r=>r&&["QUEUED","RUNNING"].includes(r.status);
  function bounds(scope){const [ip,prefix]=scope.network.split('/'),n=ipNumber(ip),count=2**(32-Number(prefix)),first=Math.floor(n/count)*count,skip=Number(prefix)<31?1:0;return[ipText(first+skip),ipText(first+count-1-skip)];}
  function recent(a){const scope=state.network?.scopes.find(s=>s.code===a.scopeCode);return Date.now()-new Date(a.lastSeenAtUtc).getTime()<=Math.max(10,scope?.intervalMinutes||30)*120000&&!(a.consecutiveMisses>0);}
  function measured(a){return Number.isFinite(a.responseTimeMs)?`${a.responseTimeMs.toLocaleString(undefined,{maximumFractionDigits:2})} ms`:"—";}
  function currentItems(){
    if(scanner.mode!=="results"||!scanner.live||!scanner.live.progress)return state.network?.assets.items||[];
    const run=scanner.live.run,observations=scanner.live.progress?.observations||[],saved=new Map((state.network?.assets.items||[]).map(a=>[a.ipAddress,a]));
    let items=observations.map(o=>({scopeCode:run.scopeCode,agencyName:state.network.scopes.find(s=>s.code===run.scopeCode)?.displayName||run.scopeCode,
      lastSeenAtUtc:scanner.live.updatedAtUtc||run.startedAtUtc,consecutiveMisses:0,deviceKind:"UNKNOWN",classificationSource:"AUTOMATIC",...o,
      ...(state.networkPage.runId===run.id?saved.get(o.ipAddress)||{}:{}),id:saved.get(o.ipAddress)?.id===o.assetId?o.assetId:undefined,historyId:o.assetId,
      deviceKind:o.deviceKind||"UNKNOWN",linkedDeviceName:o.deviceName||o.hostname,provisional:active(run)}));
    const search=(state.networkPage.search||"").toLocaleLowerCase(),presence=state.networkPage.presence||"ALL";
    items=items.filter(a=>(!search||[a.ipAddress,a.hostname,a.macAddress,a.vendor,a.linkedDeviceName,a.deviceKind].some(v=>String(v||"").toLocaleLowerCase().includes(search)))&&
      (presence!=="UNKNOWN"||a.deviceKind==="UNKNOWN"||a.identificationBasis==="AMBIGUOUS")&&presence!=="MISSING");
    const sort=state.networkPage.sort||"ip",direction=state.networkPage.descending?-1:1;
    const value=a=>sort==="name"?(a.linkedDeviceName||a.hostname||""):sort==="vendor"?(a.vendor||""):sort==="latency"?(a.responseTimeMs??Infinity):sort==="seen"?new Date(a.lastSeenAtUtc).getTime():sort==="state"?(a.consecutiveMisses||0):ipNumber(a.ipAddress);
    items.sort((a,b)=>{if(sort==="latency"&&(a.responseTimeMs==null||b.responseTimeMs==null)){if(a.responseTimeMs==null&&b.responseTimeMs!=null)return 1;if(b.responseTimeMs==null&&a.responseTimeMs!=null)return -1;}const x=value(a),y=value(b);return(x===y?ipNumber(a.ipAddress)-ipNumber(b.ipAddress):typeof x==="string"?x.localeCompare(y):x<y?-1:1)*direction;});
    return items;
  }
  function renderRows(){
    const snapshot=scanner.mode==="results"&&scanner.live?.progress;
    let items=currentItems(),total=snapshot?items.length:state.network.assets.total;
    if(snapshot){const size=state.networkPage.pageSize||25;state.networkPage.page=Math.max(1,Math.min(state.networkPage.page||1,Math.ceil(total/size)||1));items=items.slice((state.networkPage.page-1)*size,state.networkPage.page*size);}
    scanner.visibleItems=items;
    el("networkResultsTitle").textContent=scanner.mode==="results"?"Resultado del escaneo":"Inventario observado";
    el("networkResultsSubtitle").textContent=scanner.mode==="results"?(active(scanner.live?.run)?"Las filas aparecen según responde la red. La identificación continúa durante el escaneo.":`Respuestas de este sondeo · ${formatDate(scanner.live?.updatedAtUtc||scanner.live?.run?.completedAtUtc)}. Conserva la fecha de la comprobación.`):"Últimas observaciones guardadas; cada dispositivo conserva su fecha y fuente.";
    el("networkPresenceFilter").querySelector('[value="CURRENT"]').textContent=snapshot?"Respondieron en este escaneo":"Respuesta reciente";
    el("networkPresenceFilter").querySelector('[value="MISSING"]').textContent=snapshot?"Sin respuesta registrada":"Sin respuesta reciente";
    el("networkAssetRows").innerHTML=items.length?items.map((a,index)=>{
      const isRecent=!!snapshot||a.provisional||recent(a),name=a.linkedDeviceName||a.linkedNasName||a.hostname||a.displayName||"Por identificar";
      const nameSource=({RECENT_AGENT:"Agente reciente",REGISTERED_NAS:"NAS registrado",REGISTERED_GATEWAY:"Gateway registrado",MANUAL:"Nombre asignado",AMBIGUOUS:"Coincidencia ambigua"})[a.identificationBasis]||source[a.hostnameSource]||"Nombre pendiente";
      const services=(a.services||[]).slice(0,3).map(s=>`<span class="network-service" title="${esc([s.product,s.version].filter(Boolean).join(' '))}">${esc(s.port+"/"+s.protocol+" "+s.name)}</span>`).join('');
      const kind=networkKindLabels[a.deviceKind]||"Por identificar";
      return `<tr data-scanner-row="${index}"><td><span class="scanner-state ${isRecent?"responded":"previous"}"><i></i>${isRecent?"Respondió":a.consecutiveMisses>0?"Sin respuesta":"Anterior"}</span><small class="cell-secondary" title="${esc(formatDate(a.lastSeenAtUtc))}">${esc(formatRelative(a.lastSeenAtUtc))}</small></td>
        <td class="scanner-ip"><strong>${esc(a.ipAddress)}</strong><small class="cell-secondary">${esc(a.agencyName)}</small></td>
        <td><strong>${esc(name)}</strong><small class="cell-secondary">${esc(nameSource)}${a.provisional?" · en comprobación":""}</small></td>
        <td class="scanner-mac">${esc(a.macAddress||"—")}<small class="cell-secondary">${a.macAddress?esc(source[a.macSource]||"Dato anterior")+(a.macCheckedAtUtc?" · "+esc(formatRelative(a.macCheckedAtUtc)):""):"No disponible desde esta ubicación"}</small></td>
        <td>${esc(a.hardwareHistorical?a.vendor||"Por identificar":a.computerManufacturer||a.vendor||"Por identificar")}<small class="cell-secondary">${esc(a.hardwareHistorical?"Referencia de equipo anterior":a.computerModel||a.deviceTypeHint||"")}</small></td>
        <td class="scanner-latency">${esc(measured(a))}<small class="cell-secondary">${esc((a.presenceSources||[]).filter(v=>v!=="ARP_CACHE").join(" + "))}</small></td>
        <td><div class="network-service-list">${services||'<span class="cell-secondary">'+(a.provisional?"Comprobando…":"Sin servicios identificados")+'</span>'}</div>${a.services?.length>3?`<small class="cell-secondary">+${a.services.length-3} servicios</small>`:""}</td>
        <td>${esc(kind)}${a.identificationBasis==="AMBIGUOUS"?'<small class="cell-secondary">Coincidencia ambigua</small>':""}</td>
        <td><button class="secondary-button compact" type="button" data-scanner-detail="${index}">Detalle</button><button class="secondary-button compact scanner-copy" type="button" data-scanner-copy="${esc(a.ipAddress)}" aria-label="Copiar IP ${esc(a.ipAddress)}">Copiar IP</button></td></tr>`;
    }).join(''):emptyRow(9,scanner.mode==="results"&&active(scanner.live?.run)?"Esperando las primeras respuestas…":"No hay dispositivos con estos filtros.");
    renderPagination({page:state.networkPage.page||1,pageSize:state.networkPage.pageSize||25,total},el("networkPaginationLabel"),el("networkPreviousPage"),el("networkNextPage"),"dispositivos");
    view.querySelectorAll("[data-network-sort]").forEach(b=>{b.setAttribute("aria-label","Ordenar por "+b.textContent.toLocaleLowerCase());const chosen=(state.networkPage.sort||"ip")===b.dataset.networkSort;b.parentElement.setAttribute("aria-sort",chosen?(state.networkPage.descending?"descending":"ascending"):"none");});
  }
  function renderProgress(){
    const run=scanner.live?.run,p=scanner.live?.progress,busy=active(run);
    el("networkScanStart").disabled=!admin()||scanner.pending||!state.network?.enabled||!state.network?.engineAvailable||busy;
    el("networkScanStop").disabled=!admin()||!busy;
    el("networkExport").disabled=!canRead()||scanner.mode==="results"&&(!run||busy);
    if(!run){el("networkScanProgress").innerHTML='<span>'+(!state.network?.enabled?'Motor pausado. Los resultados conservan su fecha; no se iniciarán sondeos.':!state.network?.engineAvailable?'Motor no disponible. Consulta la configuración del servidor.':'Listo para verificar el alcance seleccionado.')+'</span><div class="scanner-view-switch"><button type="button" data-scanner-mode="inventory" aria-pressed="true">Inventario observado</button><button type="button" data-scanner-mode="latest">Último escaneo</button></div>';return;}
    const detail=p?.phase==="DISCOVERY"?`ICMP: ${p.icmpCompleted} / ${p.icmpTotal} · TCP: ${p.tcpFinished?"terminado":"en curso"}`:p?.phase==="IDENTIFICATION"?`${p.identificationCompleted} / ${p.identificationTotal} dispositivos con servicios recibidos`:p?.phase==="NAMES"?"Resolución de nombres de los dispositivos encontrados":busy?"Preparando el sondeo":"Se conserva el resultado y su historial";
    const count=p?.observations?.length??run.hostsObserved??0;
    el("networkScanProgress").innerHTML=`<div class="scanner-progress-meta"><strong>${esc(status[run.status]||"Pendiente")} · ${esc(state.network?.scopes.find(s=>s.code===run.scopeCode)?.displayName||run.scopeCode)}</strong><span>${count} dispositivos · ${run.addressesTargeted} IP en el alcance</span><small>${esc(busy?phases[p?.phase]||"Esperando trabajador":detail)}${busy?" · "+esc(detail):""}</small></div>${busy?`<progress aria-label="${p?.phase==="DISCOVERY"&&p.icmpTotal?"Direcciones comprobadas por ICMP":"Escaneo en curso"}" ${p?.phase==="DISCOVERY"&&p.icmpTotal?`value="${p.icmpCompleted}" max="${p.icmpTotal}"`:""}></progress>`:""}<div class="scanner-view-switch"><button type="button" data-scanner-mode="inventory" aria-pressed="${scanner.mode==="inventory"}">Inventario observado</button><button type="button" data-scanner-mode="results" aria-pressed="${scanner.mode==="results"}">Resultado del escaneo</button></div>`;
  }
  function render(){
    if(!canRead()||!state.network)return;
    const n=state.network,scopes=n.scopes||[],selection=state.networkPage.scope||"";
    el("networkScopeFilter").innerHTML='<option value="">Todas las redes</option>'+scopes.map(s=>`<option value="${esc(s.code)}">${esc(s.displayName)} · ${esc(s.network)}</option>`).join('');el("networkScopeFilter").value=selection;
    if(scanner.rangeScope!==selection){const s=scopes.find(s=>s.code===selection),range=s?bounds(s):['',''];el("networkRangeStart").value=range[0];el("networkRangeEnd").value=range[1];el("networkRangeStart").disabled=el("networkRangeEnd").disabled=!s||!admin();scanner.rangeScope=selection;}
    el("networkEngineState").textContent=!n.enabled?"Motor pausado":!n.engineAvailable?"Motor no disponible":n.scanInProgress?"Escaneo en curso":"Servidor listo";
    el("networkEngineDot").className="pulse-dot "+(n.engineAvailable&&n.enabled?n.scanInProgress?"working":"online":"attention");
    el("networkEngineMeta").textContent=`ICMP ${n.icmpAvailable?"disponible":"pendiente"} · TCP · DNS · ${n.automaticEnabled?"Programación automática activa":"Comprobación manual"}`;
    el("scanAllNetworks").disabled=!admin()||!n.engineAvailable||!n.enabled;
    el("networkPresenceFilter").value=state.networkPage.presence||"ALL";
    el("networkScopeCards").innerHTML=scopes.map(s=>`<article class="network-scope-card"><strong>${esc(s.displayName)}</strong><small>${esc(s.network)} · ${esc(status[s.lastScanStatus]||"Sin escaneo")}</small><p>${n.automaticEnabled&&s.automaticEnabled?`Cada ${s.intervalMinutes} min · servicios cada ${s.identificationIntervalHours} h`:"Programación pausada"}</p><small>${s.excludedAddresses?.length||0} IP excluidas · última observación ${esc(formatRelative(s.lastScanAtUtc))}</small>${admin()?`<button type="button" class="secondary-button compact" data-network-config="${esc(s.code)}">Programación</button>`:""}</article>`).join('');
    renderProgress();renderRows();
  }
  async function fetchProgress(force=false){
    const id=scanner.runId;if(!id||!canRead()||!force&&Date.now()-scanner.lastFetch<3500)return;
    const revision=scanner.revision;scanner.lastFetch=Date.now();
    try{const live=await api(`/api/v1/admin/network-discovery/scans/${encodeURIComponent(id)}/progress`);if(id!==scanner.runId||revision!==scanner.revision||!canRead())return;scanner.live=live;
      if(!active(live.run)&&scanner.mode==="results"&&state.networkPage.runId!==id){state.networkPage.runId=id;await loadNetworkDiscovery();}
      renderProgress();renderRows();
    }catch{if(id===scanner.runId&&revision===scanner.revision&&canRead())el("networkScanProgress").textContent="No se pudo actualizar el escaneo. Se conserva la última respuesta recibida.";}
  }
  async function start(event){
    event.preventDefault();if(!admin()||scanner.pending)return;scanner.pending=true;renderProgress();
    try{const scopeCode=state.networkPage.scope||null,result=await api("/api/v1/admin/network-discovery/scans",{method:"POST",headers:{"Content-Type":"application/json","X-Atlas-Request":"dashboard"},body:JSON.stringify({scopeCode,startAddress:scopeCode?el("networkRangeStart").value:null,endAddress:scopeCode?el("networkRangeEnd").value:null,fullIdentification:true})});
      await loadNetworkDiscovery();const run=result.runIds[0]||(state.network.recentRuns||[]).find(r=>active(r)&&(!scopeCode||r.scopeCode===scopeCode))?.id;
      if(run){scanner.runId=run;scanner.live=null;scanner.mode="results";scanner.revision++;state.networkPage.page=1;delete state.networkPage.runId;await fetchProgress(true);}
      showToast(result.alreadyQueued?"La red ya tiene un escaneo pendiente; se muestra su estado.":"Escaneo iniciado desde el servidor.");
    }catch(error){showToast(["invalid_network_range","network_range_outside_scope","empty_network_range"].includes(error.code)?"Revisa las IP inicial y final: deben pertenecer a la red seleccionada.":"No fue posible iniciar el escaneo.");}
    finally{scanner.pending=false;renderProgress();}
  }
  async function stop(){
    if(!admin()||!scanner.runId)return;el("networkScanStop").disabled=true;
    try{await api(`/api/v1/admin/network-discovery/scans/${encodeURIComponent(scanner.runId)}/cancel`,{method:"POST",headers:{"X-Atlas-Request":"dashboard"}});await loadNetworkDiscovery();await fetchProgress(true);showToast("Escaneo detenido. Las respuestas recibidas se conservan.");}
    catch{showToast("No fue posible detenerlo; actualiza su estado.");renderProgress();}
  }
  function detail(index){
    const a=scanner.visibleItems?.[index];if(!a)return;const modal=document.createElement("dialog"),previous=document.activeElement;modal.className="network-dialog";modal.setAttribute("aria-label","Dispositivo · "+a.ipAddress);
    const services=(a.services||[]).map(s=>`<tr><td>${s.port}/${esc(s.protocol)}</td><td>${esc(s.name)}</td><td>${esc([s.product,s.version,s.extraInfo].filter(Boolean).join(" ")||"Sin versión identificada")}</td></tr>`).join('');
    const web=(a.services||[]).filter(s=>s.protocol==="tcp"&&/http/i.test(s.name)&&[80,443,5000,5001,8000,8080,8443].includes(s.port)).map(s=>`<a class="secondary-button compact" target="_blank" rel="noopener noreferrer" href="${s.port===443||s.port===5001||s.port===8443||/ssl|https/i.test(s.name)?"https":"http"}://${esc(a.ipAddress)}:${s.port}/">Abrir web · ${s.port}</a>`).join('');
    modal.innerHTML=`<div class="panel-heading"><div><h3>${esc(a.linkedDeviceName||a.hostname||a.ipAddress)}</h3><p>${esc(a.ipAddress)} · ${esc(a.agencyName)}</p></div><button class="secondary-button compact" type="button" data-scanner-close>Cerrar</button></div><div class="network-dialog-body"><dl class="scanner-device-facts"><div><dt>Dirección MAC</dt><dd>${esc(a.macAddress||"No disponible")}<small>${esc(source[a.macSource]||"Sin comprobación actual")}</small></dd></div><div><dt>Respuesta medida</dt><dd>${esc(measured(a))}</dd></div><div><dt>Fabricante / modelo del equipo</dt><dd>${esc([a.computerManufacturer,a.computerModel].filter(Boolean).join(' ')||"Por identificar")}${a.hardwareHistorical?'<small>Referencia histórica del equipo anterior</small>':""}</dd></div><div><dt>Fabricante del adaptador</dt><dd>${esc(a.vendor||"Por identificar")}</dd></div><div><dt>Nombre observado</dt><dd>${esc(a.hostname||"No disponible")}<small>${esc(source[a.hostnameSource]||"Fuente anterior")}</small></dd></div><div><dt>Última respuesta</dt><dd>${esc(formatDate(a.lastSeenAtUtc))}<small>${esc((a.presenceSources||[]).join(' + '))}</small></dd></div></dl><p>${a.provisional?"Resultado provisional. La identificación se completa durante el escaneo.":"La identificación procede de las fuentes indicadas y puede necesitar validación."}</p><div class="table-wrap"><table class="data-table"><thead><tr><th>Puerto</th><th>Servicio</th><th>Producto / versión</th></tr></thead><tbody>${services||'<tr><td colspan="3">No hay servicios identificados.</td></tr>'}</tbody></table></div><small>Servicios: ${esc(({COMPLETE:"Comprobados",PARTIAL:"Identificación parcial",NOT_RUN:"Pendientes"})[a.servicesCheckStatus]||"Información anterior")} · ${esc(formatDate(a.servicesCheckedAtUtc))}</small><div class="network-form-actions">${web}${a.historyId||a.id?`<button class="secondary-button compact" type="button" data-network-history="${esc(a.historyId||a.id)}">Historial</button>`:""}</div>${admin()&&a.id?`<form class="scanner-classification"><label class="field"><span>Clasificación del dispositivo</span><select class="network-kind-select" data-asset-id="${esc(a.id)}" data-display-name="${esc(a.displayName||"")}">${Object.entries(networkKindLabels).map(([v,l])=>`<option value="${v}" ${v===a.deviceKind?"selected":""}>${esc(l)}</option>`).join('')}</select></label><button class="primary-button" type="submit">Guardar clasificación</button></form>`:""}</div>`;
    document.body.appendChild(modal);modal.querySelector("[data-scanner-close]").onclick=()=>modal.close();modal.addEventListener("close",()=>{modal.remove();previous?.focus();});modal.querySelector('[data-network-history]')?.addEventListener('click',()=>globalThis.AtlasNetworkWorkspace?.history(a.historyId||a.id));modal.querySelector('form')?.addEventListener('submit',async e=>{e.preventDefault();await updateNetworkAsset(a.id);});modal.showModal();
  }
  el("networkScanForm").addEventListener("submit",start);el("networkScanStop").addEventListener("click",()=>void stop());
  el("networkScopeFilter").addEventListener("change",()=>{scanner.runId=null;scanner.live=null;scanner.mode="inventory";scanner.revision++;delete state.networkPage.runId;},true);
  el("networkPresenceFilter").addEventListener("change",e=>{state.networkPage.presence=e.target.value;state.networkPage.page=1;void loadNetworkDiscovery();});
  view.addEventListener("click",async e=>{
    const mode=e.target.closest("[data-scanner-mode]"),sort=e.target.closest("[data-network-sort]"),info=e.target.closest("[data-scanner-detail]"),copy=e.target.closest("[data-scanner-copy]");
    if(mode){state.networkPage.page=1;if(mode.dataset.scannerMode==="inventory"){scanner.mode="inventory";delete state.networkPage.runId;await loadNetworkDiscovery();}else{if(mode.dataset.scannerMode==="latest"){const run=state.network.recentRuns?.find(r=>!state.networkPage.scope||r.scopeCode===state.networkPage.scope);if(!run){showToast("Todavía no hay escaneos para esta red.");return;}scanner.runId=run.id;scanner.live=null;scanner.revision++;}scanner.mode="results";await fetchProgress(true);render();}}
    else if(sort){const value=sort.dataset.networkSort;state.networkPage.descending=state.networkPage.sort===value?!state.networkPage.descending:false;state.networkPage.sort=value;state.networkPage.page=1;await loadNetworkDiscovery();}
    else if(info)detail(Number(info.dataset.scannerDetail));
    else if(copy){try{if(navigator.clipboard)await navigator.clipboard.writeText(copy.dataset.scannerCopy);else{const text=document.createElement('textarea');text.value=copy.dataset.scannerCopy;document.body.append(text);text.select();if(!document.execCommand('copy'))throw new Error('copy');text.remove();}showToast("IP copiada.");}catch{showToast("No fue posible copiar la IP.");}}
  });
  el("networkExport").addEventListener("click",async()=>{const button=el("networkExport");button.disabled=true;try{const response=await fetch(`/api/v1/admin/network-discovery/export?${buildNetworkQuery()}`,{credentials:"same-origin"});if(!response.ok)throw new Error('export');const url=URL.createObjectURL(await response.blob()),link=document.createElement('a');link.href=url;link.download='atlas-redes.csv';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);}catch{showToast("No se pudo exportar el resultado.");}finally{renderProgress();}});
  setInterval(()=>{if(!view.classList.contains('active')||document.hidden||!canRead())return;if(state.network?.recentRuns?.some(active))void loadNetworkDiscovery();if(scanner.runId&&active(scanner.live?.run))void fetchProgress();},4000);
  globalThis.AtlasNetworkScanner={render,fetchProgress,currentItems};render();
})();
