"use strict";

// Presentation only. All navigation, reads, permissions and mutations stay in
// the existing portal modules. Numeric results are never counted from zero.
(() => {
  if (!document.body.classList.contains("mineral-motion-portal")) return;
  const root = document.documentElement;
  const motionQuery = window.matchMedia("(prefers-reduced-motion: reduce)");
  const motionKey = "atlas-motion";
  const rail = document.querySelector(".main-nav");
  const nav = document.getElementById("motionTopNav");
  const subnav = document.getElementById("motionSubnav");
  const indicator = nav?.querySelector(".motion-nav-indicator");
  const profile = document.getElementById("motionProfileMenu");
  const profileButton = document.getElementById("motionProfileButton");
  const searchButton = document.getElementById("motionSearchButton");
  const search = document.getElementById("globalSearch");
  const preference = document.getElementById("motionAnimationToggle");
  const groups = [
    { id: "overview", views: ["overview"] },
    { id: "assets", views: ["assets", "agencies", "network"] },
    { id: "backups", views: ["backups", "nas"] },
    { id: "security", views: ["security", "software"] },
    { id: "distribution", views: ["distribution"] },
    { id: "management", views: ["reports", "governance", "training", "cases", "audit", "settings"] }
  ];
  const originalButtons = new Map([...rail.querySelectorAll("[data-view]")].map(button => [button.dataset.view, button]));
  const labels = new Map();
  let enabled = true, themeBusy = false, lastView = "", schedule = 0;
  let enterDeadline = 0, lastFocus = null;
  const seen = new WeakSet();
  try { enabled = localStorage.getItem(motionKey) !== "off"; } catch { /* Optional browser preference. */ }
  const motionEnabled = () => enabled && !motionQuery.matches;
  const currentView = () => document.querySelector(".content-area > .view.active")?.id.replace(/^view-/, "") || "overview";

  originalButtons.forEach((button, view) => {
    const span = button.querySelector(".nav-text");
    const name = span ? [...span.childNodes].filter(node => node.nodeType === Node.TEXT_NODE).map(node => node.textContent).join(" ").trim() : view;
    labels.set(view, name);
    button.setAttribute("aria-label", name);
    button.title = name;
  });

  function allowed(view) {
    const button = originalButtons.get(view);
    return Boolean(button && !button.hidden && !button.disabled && !button.classList.contains("hidden"));
  }

  function applyMotionPreference() {
    const active = motionEnabled();
    root.dataset.motion = active ? "on" : "off";
    preference?.setAttribute("aria-checked", String(active));
    if (preference) {
      preference.disabled = motionQuery.matches;
      preference.title = motionQuery.matches ? "Movimiento reducido según la preferencia del sistema" : "Activar o reducir las animaciones";
    }
    if (!active) document.getAnimations().filter(animation => animation.effect?.target?.closest?.(".mineral-motion-portal")).forEach(animation => animation.cancel());
  }

  function updateIndicator() {
    if (!nav || !indicator) return;
    const button = nav.querySelector('[aria-current="page"]');
    if (!button || button.hidden) { indicator.hidden = true; return; }
    indicator.hidden = false;
    indicator.style.width = `${button.offsetWidth}px`;
    indicator.style.transform = `translateX(${button.offsetLeft}px)`;
    const clipped = button.offsetLeft < nav.scrollLeft || button.offsetLeft + button.offsetWidth > nav.scrollLeft + nav.clientWidth;
    if (clipped) nav.scrollTo({ left: Math.max(0, button.offsetLeft - 8), behavior: motionEnabled() ? "smooth" : "auto" });
  }

  function refreshNavigation() {
    const view = currentView();
    const group = groups.find(item => item.views.includes(view)) || groups[0];
    originalButtons.forEach((button, key) => {
      if (key === view) button.setAttribute("aria-current", "page");
      else button.removeAttribute("aria-current");
    });
    nav?.querySelectorAll("[data-motion-group]").forEach(button => {
      const item = groups.find(candidate => candidate.id === button.dataset.motionGroup);
      button.hidden = !item?.views.some(allowed);
      if (item?.id === group.id) button.setAttribute("aria-current", "page");
      else button.removeAttribute("aria-current");
    });
    const visibleViews = group.views.filter(allowed);
    const signature = visibleViews.join("|");
    if (subnav && subnav.dataset.signature !== signature) {
      subnav.replaceChildren(...visibleViews.map(key => {
        const button = document.createElement("button");
        button.type = "button";
        button.className = "motion-subnav-button";
        button.dataset.motionView = key;
        button.textContent = labels.get(key) || key;
        return button;
      }));
      subnav.dataset.signature = signature;
    }
    subnav?.querySelectorAll("[data-motion-view]").forEach(button => {
      if (button.dataset.motionView === view) button.setAttribute("aria-current", "page");
      else button.removeAttribute("aria-current");
    });
    // A group with one destination needs no duplicated navigation row.
    if (subnav) subnav.hidden = visibleViews.length <= 1;
    updateIndicator();
  }

  function animateElement(element, index) {
    if (!motionEnabled() || !element || seen.has(element)) return;
    seen.add(element);
    element.animate([
      { opacity: 0, transform: "translateY(13px) scale(.992)", filter: "blur(2px)" },
      { opacity: 1, transform: "translateY(0) scale(1)", filter: "blur(0)" }
    ], { duration: 430, delay: Math.min(index, 7) * 35, easing: "cubic-bezier(.22,.8,.24,1)", fill: "backwards" });
  }

  function animateEntrance(section, fresh = false) {
    if (!section || !motionEnabled()) return;
    const panels = [...section.querySelectorAll(":scope > .panel, :scope > .section-heading, .motion-overview-grid > .panel, .motion-metric-grid > .kpi-card, .motion-overview-lower > .panel")].slice(0, 13);
    if (fresh) panels.forEach(element => seen.delete(element));
    panels.forEach(animateElement);
    section.querySelectorAll(".motion-hero-progress-fill, .horizontal-bar-fill, .bar-fill").forEach(element => {
      if (seen.has(element)) return;
      seen.add(element);
      element.animate([{ transform: "scaleX(.02)" }, { transform: "scaleX(1)" }], { duration: 650, delay: 110, easing: "cubic-bezier(.22,.8,.24,1)", fill: "backwards" });
    });
  }

  function closeProfile(restore = false) {
    if (!profile || profile.hidden) return;
    profile.hidden = true;
    profileButton?.setAttribute("aria-expanded", "false");
    if (restore) profileButton?.focus();
  }

  function closeSearch(restore = false) {
    if (!document.body.classList.contains("motion-search-open")) return;
    document.body.classList.remove("motion-search-open");
    searchButton?.setAttribute("aria-expanded", "false");
    if (restore) searchButton?.focus();
  }

  function openSearch() {
    // Do not open a second presentation layer over an unfinished form/dialog.
    const modalVisible = [...document.querySelectorAll('dialog[open], [role="dialog"][aria-modal="true"]')]
      .some(element => element.getClientRects().length && getComputedStyle(element).visibility !== "hidden");
    if (modalVisible) return;
    closeProfile();
    document.body.classList.add("motion-search-open");
    searchButton?.setAttribute("aria-expanded", "true");
    search?.focus();
  }

  function sync() {
    refreshNavigation();
    const view = currentView();
    if (view !== lastView) {
      lastView = view;
      enterDeadline = performance.now() + 1800;
      animateEntrance(document.getElementById(`view-${view}`), true);
    } else if (performance.now() < enterDeadline) {
      animateEntrance(document.getElementById(`view-${view}`));
    }
  }

  function requestSync() {
    if (schedule) return;
    schedule = requestAnimationFrame(() => { schedule = 0; sync(); });
  }

  // Keep switchView synchronous: workspace modules rely on its immediate state
  // and permission checks when opening forms, notices and contextual details.
  if (typeof window.switchView === "function") {
    const existing = window.switchView;
    window.switchView = function (...args) {
      const before = currentView();
      const result = existing.apply(this, args);
      const after = currentView();
      if (after === "overview" && before !== after && typeof window.renderExecutiveOverview === "function") window.renderExecutiveOverview();
      sync();
      if (before !== after) {
        closeProfile();
        // Typing in the global search intentionally opens Assets. Keep that
        // same input focused and visible so the next keystroke is not lost.
        if (document.activeElement !== search) closeSearch();
        window.scrollTo({ top: 0, behavior: "instant" });
      }
      return result;
    };
  }
  if (typeof window.renderSession === "function") {
    const existing = window.renderSession;
    window.renderSession = function (...args) { const result = existing.apply(this, args); refreshNavigation(); return result; };
  }

  nav?.addEventListener("click", event => {
    const button = event.target.closest("[data-motion-group]");
    if (!button || button.hidden) return;
    const item = groups.find(group => group.id === button.dataset.motionGroup);
    const view = item?.views.find(allowed);
    if (view) originalButtons.get(view)?.click();
  });
  subnav?.addEventListener("click", event => {
    const view = event.target.closest("[data-motion-view]")?.dataset.motionView;
    if (view && allowed(view)) originalButtons.get(view)?.click();
  });
  profileButton?.addEventListener("click", () => {
    closeSearch();
    const show = profile.hidden;
    profile.hidden = !show;
    profileButton.setAttribute("aria-expanded", String(show));
    if (show && motionEnabled()) profile.animate([{ opacity: 0, transform: "translateY(-6px) scale(.98)" }, { opacity: 1, transform: "none" }], { duration: 180, easing: "ease-out" });
    if (show) lastFocus = profileButton;
  });
  profile?.addEventListener("click", event => {
    if (event.target.closest("#logoutButton, #accountSecurityButton")) closeProfile();
  });
  preference?.addEventListener("click", () => {
    if (motionQuery.matches) return;
    enabled = !enabled;
    applyMotionPreference();
    try { localStorage.setItem(motionKey, enabled ? "on" : "off"); } catch { /* In-memory only. */ }
  });
  searchButton?.addEventListener("click", () => document.body.classList.contains("motion-search-open") ? closeSearch(true) : openSearch());
  document.addEventListener("keydown", event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") { event.preventDefault(); openSearch(); }
    if (event.key === "Escape") {
      if (!profile?.hidden) { event.preventDefault(); closeProfile(true); }
      else if (document.body.classList.contains("motion-search-open")) { event.preventDefault(); closeSearch(true); }
    }
  });
  document.addEventListener("click", event => {
    if (!event.target.closest(".motion-profile-wrap")) closeProfile();
    if (!event.target.closest(".motion-global-search, #motionSearchButton")) closeSearch();
  });
  // Tab out of the small account popover returns to the normal document flow;
  // no modal focus trap is imposed on the rest of the portal.
  profile?.addEventListener("focusout", () => requestAnimationFrame(() => {
    if (!profile.contains(document.activeElement) && document.activeElement !== lastFocus) closeProfile();
  }));

  new MutationObserver(requestSync).observe(rail, { subtree: true, attributes: true, attributeFilter: ["class", "hidden", "disabled"] });
  new MutationObserver(requestSync).observe(document.querySelector(".content-area"), { childList: true, subtree: true, attributes: true, attributeFilter: ["class"] });
  window.addEventListener("resize", updateIndicator);
  document.fonts?.ready.then(updateIndicator).catch(() => {});
  motionQuery.addEventListener("change", applyMotionPreference);
  window.addEventListener("storage", event => { if (event.key === motionKey) { enabled = event.newValue !== "off"; applyMotionPreference(); } });

  async function transitionTheme(apply) {
    if (themeBusy) return;
    if (!motionEnabled() || typeof document.startViewTransition !== "function") { apply(); return; }
    themeBusy = true;
    let applied = false;
    const once = () => { if (!applied) { apply(); applied = true; } };
    try {
      const transition = document.startViewTransition(once);
      await transition.ready;
      const wave = progress => {
        const height = innerHeight, width = innerWidth;
        const edge = -height + (width + 2 * height) * progress;
        return `path("M 0 0 L ${edge + .35 * height} 0 C ${edge + .05 * height} ${.15 * height} ${edge + .65 * height} ${.25 * height} ${edge + .20 * height} ${.48 * height} C ${edge - .25 * height} ${.70 * height} ${edge + .12 * height} ${.82 * height} ${edge - .32 * height} ${height} L 0 ${height} Z")`;
      };
      const animation = root.animate({ clipPath: [wave(0), wave(1)] }, { duration: 900, easing: "cubic-bezier(.45,0,.15,1)", pseudoElement: "::view-transition-new(root)", fill: "forwards" });
      await animation.finished;
      await transition.finished;
    } catch { once(); } finally { themeBusy = false; }
  }

  window.AtlasMotion = Object.freeze({ refresh: requestSync, transitionTheme });
  applyMotionPreference();
  sync();
})();
