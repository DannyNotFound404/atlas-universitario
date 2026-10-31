"use strict";

const operationalAttention = { search: "", category: "", open: new Set() };

function renderOperationalAttention() {
  const host = document.getElementById("operationalAttention");
  if (!host) return;
  const focused = host.contains(document.activeElement) ? document.activeElement.id : null;
  const selection = focused === 'attentionSearch' ? [document.activeElement.selectionStart, document.activeElement.selectionEnd] : null;
  host.hidden = state.session?.role === "Installer";
  if (host.hidden) return;
  const groups = state.notifications?.attentionGroups;
  const generated = state.notifications?.generatedAtUtc;
  if (!Array.isArray(groups)) {
    host.innerHTML = '<div class="panel-heading"><div><h3>Atención actual</h3><p>No se pudo obtener la evaluación actual. Actualiza para volver a consultar.</p></div></div>';
    return;
  }
  // Keep filters and expanded causes when the periodic refresh arrives.
  host.querySelectorAll('details[data-attention-key]').forEach(d => {
    if (d.open) operationalAttention.open.add(d.dataset.attentionKey);
    else operationalAttention.open.delete(d.dataset.attentionKey);
  });
  const query = operationalAttention.search.toLocaleLowerCase("es");
  const filtered = groups.filter(g => !operationalAttention.category || g.category === operationalAttention.category)
    .map(g => ({...g, items: g.items.filter(i => [g.title, i.name, i.agencyCode, i.detail, i.responsible].join(" ").toLocaleLowerCase("es").includes(query))}))
    .filter(g => g.items.length);
  const count = groups.reduce((sum,g) => sum + g.items.length, 0);
  const deviceCount = new Set(groups.flatMap(g => g.items.map(i => i.deviceId).filter(Boolean))).size;
  const categories = [...new Set(groups.map(g => g.category))];
  host.innerHTML = `<div class="panel-heading"><div><h3>Atención actual</h3><p>${count} señales agrupadas en ${groups.length} causas · ${deviceCount} equipos relacionados</p></div><button type="button" class="secondary-button compact" data-attention-history>Historial de avisos</button></div>
    <p class="chart-scope">Evaluado ${escapeHtml(formatDate(generated))}. Copias, postura, custodia, vulnerabilidades conocidas y eventos de hoy. La fecha del dato se muestra en cada origen; consultar una señal no resuelve su causa ni cierra expedientes.</p>
    <div class="attention-filters"><label>Buscar equipo o causa<input type="search" id="attentionSearch" value="${escapeHtml(operationalAttention.search)}" placeholder="Equipo, agencia o responsable"></label><label>Área<select id="attentionCategory"><option value="">Todas las áreas evaluadas</option>${categories.map(c => `<option value="${escapeHtml(c)}" ${c === operationalAttention.category ? "selected" : ""}>${escapeHtml(notificationCategoryLabel(c))}</option>`).join("")}</select></label></div>
    <div class="attention-groups">${filtered.length ? filtered.map(g => `<details class="attention-group ${g.severity === "CRITICAL" ? "critical" : "warning"}" data-attention-key="${escapeHtml(g.key)}" ${operationalAttention.open.has(g.key) ? "open" : ""}>
      <summary><span><strong>${escapeHtml(g.title)}</strong><small>${escapeHtml(notificationCategoryLabel(g.category))} · ${g.severity === "CRITICAL" ? "Prioridad alta de revisión" : "Requiere comprobación"}</small></span><b>${g.items.length}</b></summary>
      <p class="attention-next">${escapeHtml(g.nextAction)}</p><ul>${g.items.map(i => `<li><div><strong>${escapeHtml(i.name)}</strong><small>${escapeHtml(i.agencyCode || "Sin agencia")} · ${i.responsible ? `Responsable del control: ${escapeHtml(i.responsible)}` : "Responsable de tratamiento por asignar"}</small><p>${escapeHtml(i.detail)}</p><small>${i.observedAtUtc ? `Dato observado: ${escapeHtml(formatDate(i.observedAtUtc))}` : "Consulte fecha y evidencia en el origen"}</small></div><button type="button" class="secondary-button compact" data-attention-open="${escapeHtml(g.key)}" data-target="${escapeHtml(i.targetEntityId || "")}">Revisar origen</button></li>`).join("")}</ul></details>`).join("") : `<p class="attention-empty">${groups.length ? "No hay señales que coincidan con estos filtros." : "Sin señales en las fuentes evaluadas. Consulta también software, documentos y expedientes."}</p>`}</div>`;
  host.querySelector('[data-attention-history]').addEventListener('click', () => { openNotificationPanel(); });
  if (focused) {
    const replacement = document.getElementById(focused);
    replacement?.focus({preventScroll:true});
    if (selection && selection[0] !== null) replacement?.setSelectionRange(...selection);
  }
  host.querySelector('#attentionCategory').addEventListener('change', event => { operationalAttention.category = event.target.value; renderOperationalAttention(); });
  host.querySelector('#attentionSearch').addEventListener('input', event => {
    const position = event.target.selectionStart;
    operationalAttention.search = event.target.value;
    renderOperationalAttention();
    const input = host.querySelector('#attentionSearch'); input.focus({preventScroll:true});
    if (position !== null) input.setSelectionRange(position, position);
  });
  host.querySelectorAll('[data-attention-open]').forEach(button => button.addEventListener('click', async () => {
    const group = groups.find(g => g.key === button.dataset.attentionOpen);
    const item = group?.items.find(i => String(i.targetEntityId || "") === button.dataset.target);
    if (!item) return;
    button.disabled = true;
    try {
      if (group.category === 'BACKUP_CONTROL') { switchView('backups'); await openBackupControlDetail(item.targetEntityId); }
      else if (group.category === 'BACKUP') {
        switchView('backups');
        backupWorkspace.tab = 'all'; backupWorkspace.search = item.name; backupWorkspace.page = 1;
        renderBackupWorkspace();
      } else if (group.category === 'POSTURE' && item.deviceId) {
        await openDevice(item.deviceId); selectAssetDetailTab('security');
      } else if (group.category === 'SECURITY') {
        state.securityPages.eventDeviceId = item.deviceId || '';
        state.securityPages.eventPage = 1;
        switchView('security'); await loadSecurity();
      } else if (group.category === 'SOFTWARE') {
        switchView('software');
        document.dispatchEvent(new CustomEvent('atlas:vulnerabilities-device',{detail:{id:item.deviceId||''}}));
      } else if (item.deviceId) await openDevice(item.deviceId);
      else switchView(item.targetView);
    } catch { showToast('No fue posible abrir el origen. Vuelve a intentarlo.'); }
    finally { button.disabled = false; }
  }));
}

function governanceReferenceNotice(document) {
  // Identify the approved source by content, never by a title that might refer
  // to another policy or version. Preserve portal metadata for human review.
  if (String(document.currentSha256 || '').toUpperCase() !== 'A0E64776EAFD5D7598810B16124AF4E630C092282CA996AF052C0AF9385EB088') return '';
  return `<aside class="reference-notice"><strong>Política V4 · referencia normativa</strong><p>La base aprobada distingue aplicación anticipada y entrada formal en vigor el <b>1 de junio de 2027</b>. Aprobación: Consejo de Administración, Acta 24-2026, 10 de julio de 2026.</p><p>La fecha y aprobación registradas en esta ficha se conservan. Si difieren de la referencia, requieren revisión documental; no se han corregido automáticamente. Referencia: base V4, sección 3.</p></aside>`;
}

function selectAssetDetailTab(tab) {
  const host = elements.drawerContent;
  host.querySelectorAll('[data-asset-panel]').forEach(p => p.hidden = p.dataset.assetPanel !== tab);
  host.querySelectorAll('[data-asset-tab]').forEach(b => b.setAttribute('aria-pressed', String(b.dataset.assetTab === tab)));
  if (tab === 'security') void loadAssetPosture(host.querySelector('[data-asset-posture]'));
  if (tab === 'administration') void loadAssetDeliveryHealth(host.querySelector('[data-asset-delivery]'));
}

async function loadAssetPosture(panel) {
  if (!panel || panel.dataset.loaded) return;
  panel.dataset.loaded = 'loading';
  panel.textContent = 'Consultando postura del equipo…';
  try {
    const item = await api(`/api/v1/admin/security/devices/${encodeURIComponent(panel.dataset.assetPosture)}`);
    if (!panel.isConnected) return;
    const freshness = typeof postureObservation === 'function' ? postureObservation(item) : { label: 'Vigencia por confirmar', current: false };
    panel.innerHTML = `<h3>Postura observada</h3><p><strong>${escapeHtml(freshness.label)}</strong> · ${item.capturedAtUtc ? `Capturada ${escapeHtml(formatDate(item.capturedAtUtc))}` : 'Sin reporte de postura'}. ${freshness.current ? `Ventana técnica: ${item.freshnessHours ?? 24} horas.` : 'Los valores históricos no confirman protección actual.'} Un dato sin confirmar no acredita protección.</p><div class="detail-grid">${[['Firewall',item.firewallEnabled],['BitLocker en C:',item.systemDriveProtected],['Arranque seguro',item.secureBootEnabled],['TPM presente',item.tpmPresent]].map(([name,value]) => detailCard(name, freshness.current ? value === true ? 'Confirmado' : value === false ? 'No confirmado como activo' : 'Sin información' : value == null ? 'Sin información' : `Última observación: ${value ? 'Sí' : 'No'}`, freshness.current ? '' : 'Estado actual por confirmar')).join('')}</div>${item.collectionWarnings?.length ? '<p>Recopilación parcial: hay controles que el agente no pudo consultar.</p>' : ''}`;
    panel.dataset.loaded = 'ready';
  } catch {
    if (!panel.isConnected) return;
    delete panel.dataset.loaded;
    panel.innerHTML = '<p>No fue posible consultar la postura. Vuelve a abrir esta sección para reintentar.</p>';
  }
}

async function loadAssetDeliveryHealth(panel) {
  if (!panel || panel.dataset.loaded) return;
  panel.dataset.loaded = 'loading'; panel.textContent = 'Consultando entrega de telemetría…';
  try {
    const value = await api(`/api/v1/admin/devices/${encodeURIComponent(panel.dataset.assetDelivery)}/delivery-health`);
    if (!panel.isConnected) return;
    const known = value.reported && value.health;
    panel.innerHTML = `<h3>Entrega de telemetría del agente</h3>${known ? `<p>Recibida ${escapeHtml(formatDate(value.receivedAtUtc))}.</p><dl class="portal-definition"><dt>Eventos retenidos por rechazo</dt><dd>${escapeHtml(String(value.health.quarantinedEvents ?? 'Sin recuento'))}</dd><dt>Última retención</dt><dd>${value.health.lastQuarantineAtUtc ? escapeHtml(formatDate(value.health.lastQuarantineAtUtc)) : 'Sin fecha reportada'}</dd><dt>Diagnóstico del último rechazo</dt><dd>${escapeHtml(value.health.lastRejectionCode || 'Sin rechazo reportado')}</dd></dl>` : '<p>Diagnóstico no reportado por esta versión del agente. No implica pérdida confirmada ni ausencia de errores.</p>'}<p class="portal-help">Este diagnóstico corresponde al transporte de eventos; no es la cantidad de copias o archivos pendientes y no determina una acción incorrecta del usuario.</p>`;
    panel.dataset.loaded = 'ready';
  } catch { if (!panel.isConnected) return; delete panel.dataset.loaded; panel.innerHTML = '<h3>Entrega de telemetría del agente</h3><p>No se pudo consultar este diagnóstico. Vuelve a abrir Administración para reintentar.</p>'; }
}

function organizeAssetDetail(device) {
  const host = elements.drawerContent;
  const grid = host.querySelector('.detail-grid');
  if (!grid) return;
  const names = {summary:'Resumen',backups:'Copias',security:'Seguridad',custody:'Custodia',administration:'Administración'};
  const panels = Object.fromEntries(Object.keys(names).map(key => {
    const panel = document.createElement('section'); panel.dataset.assetPanel = key;
    panel.setAttribute('aria-label', names[key]); return [key,panel];
  }));
  // Move existing elements, preserving IDs, handlers, permissions and data.
  const cards = [...grid.children];
  const copyLabels = new Set(['Proveedor de respaldo','BackupNAS','Última copia']);
  const adminLabels = new Set(['Agente ATLAS','Anillo de actualización']);
  const grids = {};
  for (const card of cards) {
    const title = card.firstElementChild?.textContent.trim();
    const key = copyLabels.has(title) ? 'backups' : adminLabels.has(title) ? 'administration' : 'summary';
    if (!grids[key]) { grids[key] = document.createElement('div'); grids[key].className = 'detail-grid'; panels[key].append(grids[key]); }
    grids[key].append(card);
  }
  grid.remove();
  for (const node of [...host.querySelectorAll(':scope > .detail-section')]) {
    const heading = node.querySelector('h3')?.textContent || '';
    const key = node.querySelector('#deviceBackupProvider') || heading.includes('respaldo') ? 'backups'
      : node.classList.contains('custody-summary') ? 'custody'
      : heading.includes('Seguridad') ? 'security'
      : /Anillo|Retirada|Identidad retirada|Perfiles/.test(heading) ? 'administration' : 'summary';
    panels[key].append(node);
  }
  const nav = document.createElement('nav'); nav.className = 'asset-detail-tabs'; nav.setAttribute('aria-label','Secciones del activo');
  nav.innerHTML = Object.entries(names).map(([key,label]) => `<button type="button" data-asset-tab="${key}" aria-pressed="${key === 'summary'}">${label}</button>`).join('');
  host.querySelector('.detail-status')?.after(nav);
  Object.values(panels).forEach(panel => host.append(panel));
  if (state.session?.role !== 'Installer') {
    const posture = document.createElement('section'); posture.className = 'detail-section';
    posture.dataset.assetPosture = device.id; panels.security.prepend(posture);
    const delivery = document.createElement('section'); delivery.className = 'detail-section'; delivery.dataset.assetDelivery = device.id; panels.administration.append(delivery);
  }
  nav.querySelectorAll('button').forEach(button => button.addEventListener('click', () => selectAssetDetailTab(button.dataset.assetTab)));
  const links = document.createElement('div'); links.className = 'asset-related-actions';
  links.hidden = state.session?.role === 'Installer';
  links.innerHTML = '<button class="secondary-button compact" type="button" data-asset-software>Software de este equipo</button>';
  panels.summary.append(links);
  links.querySelector('button').addEventListener('click', () => {
    closeDevice(); switchView('software');
    document.dispatchEvent(new CustomEvent('atlas:software-device',{detail:{id:device.id}}));
  });
  const controlLinks = document.createElement('div'); controlLinks.className = 'asset-related-actions';
  controlLinks.hidden = state.session?.role === 'Installer';
  const profiles = (state.backupControl?.profiles || []).filter(p => p.deviceId === device.id);
  controlLinks.innerHTML = profiles.length ? profiles.map(p => `<button type="button" class="secondary-button compact" data-asset-control="${escapeHtml(p.id)}">Seguimiento · ${escapeHtml(p.systemName)}</button>`).join('') : '<p>No hay control individual vinculado. Consulta planes y carpetas comunales en Control de respaldos.</p>';
  panels.backups.prepend(controlLinks);
  controlLinks.querySelectorAll('button').forEach(b => b.addEventListener('click', () => { closeDevice(); switchView('backups'); void openBackupControlDetail(b.dataset.assetControl); }));
  selectAssetDetailTab('summary');
}
