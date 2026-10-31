"use strict";
(() => {
  const view = document.getElementById("view-distribution");
  if (!view) return;
  const storageKey = "atlas.updates.sections.v1";
  let saved = {};
  try {
    const value = JSON.parse(sessionStorage.getItem(storageKey) || "{}");
    if (value && typeof value === "object" && !Array.isArray(value)) saved = value;
  } catch { /* A browser may disallow storage; controls still work in memory. */ }
  const sections = new WeakMap();
  let sequence = 0;
  const workspace = document.getElementById("releaseWorkspace");
  const workspaceTabs = workspace ? Array.from(workspace.querySelectorAll("[data-update-workspace-tab]")) : [];

  function selectWorkspace(key, focus = false) {
    const selected = workspaceTabs.find(button => button.dataset.updateWorkspaceTab === key);
    if (!selected) return;
    for (const button of workspaceTabs) {
      const active = button === selected;
      button.setAttribute("aria-selected", String(active));
      button.tabIndex = active ? 0 : -1;
      document.getElementById(button.getAttribute("aria-controls")).hidden = !active;
    }
    saved.workspaceTab = key;
    try { sessionStorage.setItem(storageKey, JSON.stringify(saved)); } catch { /* Optional preference. */ }
    if (focus) selected.focus();
  }

  for (const button of workspaceTabs) {
    button.addEventListener("click", () => selectWorkspace(button.dataset.updateWorkspaceTab));
    button.addEventListener("keydown", event => {
      const current = workspaceTabs.indexOf(button);
      const next = event.key === "ArrowRight" ? (current + 1) % workspaceTabs.length :
        event.key === "ArrowLeft" ? (current + workspaceTabs.length - 1) % workspaceTabs.length :
        event.key === "Home" ? 0 : event.key === "End" ? workspaceTabs.length - 1 : -1;
      if (next < 0) return;
      event.preventDefault();
      selectWorkspace(workspaceTabs[next].dataset.updateWorkspaceTab, true);
    });
  }
  selectWorkspace(workspaceTabs.some(button => button.dataset.updateWorkspaceTab === saved.workspaceTab) ? saved.workspaceTab : "library");

  function remember(key, expanded) {
    saved[key] = expanded;
    try { sessionStorage.setItem(storageKey, JSON.stringify(saved)); } catch { /* Optional preference. */ }
  }

  function fold(section, key, heading) {
    if (!heading || sections.has(section)) return;
    const title = heading.querySelector("h3")?.textContent.trim() || heading.textContent.trim();
    const button = document.createElement("button");
    button.type = "button";
    button.className = "secondary-button compact updates-fold-toggle";
    const body = document.createElement("div");
    body.className = "updates-fold-body";
    body.id = `updates-section-body-${++sequence}`;
    // Move the original nodes: file selections, form values and listeners survive.
    for (const child of Array.from(section.childNodes)) if (child !== heading) body.append(child);
    section.append(body);
    heading.append(button);
    heading.classList.add("updates-fold-heading");
    section.classList.add("updates-fold");
    section.dataset.updateSection = key;
    button.setAttribute("aria-controls", body.id);
    const controller = {
      set(expanded, persist = true) {
        body.hidden = !expanded;
        button.setAttribute("aria-expanded", String(expanded));
        button.textContent = expanded ? "Contraer" : "Expandir";
        button.setAttribute("aria-label", `${expanded ? "Contraer" : "Expandir"}: ${title}`);
        section.classList.toggle("updates-fold-closed", !expanded);
        if (persist) remember(key, expanded);
      },
      expanded: () => !body.hidden
    };
    sections.set(section, controller);
    controller.set(typeof saved[key] === "boolean" ? saved[key] : true, false);
    button.addEventListener("click", () => controller.set(!controller.expanded()));
  }

  function controls(scope, label, before = scope.firstChild, persistNative = false) {
    if (scope.querySelector(":scope > .updates-section-tools")) return;
    const toolbar = document.createElement("div");
    toolbar.className = "updates-section-tools";
    toolbar.setAttribute("role", "group");
    toolbar.setAttribute("aria-label", label);
    const caption = document.createElement("span");
    caption.textContent = label;
    toolbar.append(caption);
    for (const [expanded, text] of [[false, "Contraer todo"], [true, "Expandir todo"]]) {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "secondary-button compact";
      button.textContent = text;
      button.addEventListener("click", () => {
        for (const section of scope.querySelectorAll(".updates-fold")) sections.get(section)?.set(expanded);
        for (const detail of scope.querySelectorAll("details")) {
          detail.open = expanded;
          if (persistNative) remember(detail.dataset.updatePreference, expanded);
        }
      });
      toolbar.append(button);
    }
    scope.insertBefore(toolbar, before);
    // Let native validation reveal a required field even after a form is folded.
    scope.addEventListener("invalid", event => {
      for (let section = event.target.closest(".updates-fold"); section; section = section.parentElement?.closest(".updates-fold"))
        sections.get(section)?.set(true);
      const panel = event.target.closest("[data-update-workspace-panel]");
      if (panel) selectWorkspace(panel.dataset.updateWorkspacePanel);
    }, true);
  }

  function enhanceReleaseDetails(root) {
    for (const section of root.querySelectorAll(".release-detail-section, .release-restore-card")) {
      const heading = section.querySelector(":scope > h3");
      if (!heading) continue;
      // These keys are section labels, never release identifiers or form data.
      const key = `detail:${heading.textContent.trim()}`;
      const wrapper = document.createElement("div");
      heading.before(wrapper);
      wrapper.append(heading);
      fold(section, key, wrapper);
    }
    controls(root, "Secciones del detalle");
  }

  function enhanceHistory(root) {
    const details = root.querySelectorAll("details");
    if (!details.length) return;
    for (const detail of details) {
      detail.dataset.updatePreference = "history-references";
      if (typeof saved["history-references"] === "boolean") detail.open = saved["history-references"];
      detail.addEventListener("toggle", () => remember("history-references", detail.open));
    }
    controls(root, "Referencias del historial", root.firstChild, true);
  }

  for (const section of view.querySelectorAll("[data-update-section]"))
    fold(section, section.dataset.updateSection, section.querySelector(":scope > .panel-heading"));
  controls(view, "Organizar secciones", view.querySelector("#updaterWorkspace"));
  globalThis.AtlasUpdateSections = {
    enhanceReleaseDetails, enhanceHistory,
    openSection(key) {
      const section = view.querySelector(`[data-update-section="${key}"]`);
      if (section) sections.get(section)?.set(true);
    },
    openReleaseWorkspace(key) {
      if (!workspaceTabs.some(button => button.dataset.updateWorkspaceTab === key)) return;
      sections.get(workspace)?.set(true);
      selectWorkspace(key);
    }
  };
})();
