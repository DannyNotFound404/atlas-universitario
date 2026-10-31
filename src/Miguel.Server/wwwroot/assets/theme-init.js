"use strict";

(() => {
  const key = "atlas-theme";
  const root = document.documentElement;
  const valid = value => value === "light" || value === "dark";
  let theme = "dark";
  try {
    const saved = localStorage.getItem(key);
    if (valid(saved)) theme = saved;
  } catch { /* The selected appearance still works when browser storage is blocked. */ }

  function apply(value) {
    root.dataset.theme = value;
    document.querySelectorAll("[data-theme-toggle]").forEach(button => {
      const dark = value === "dark";
      button.setAttribute("aria-pressed", String(dark));
      button.setAttribute("aria-label", dark
        ? "Modo oscuro Mineral Gris. Cambiar a modo claro Mineral original"
        : "Modo claro Mineral original. Cambiar a modo oscuro Mineral Gris");
      button.title = dark ? "Cambiar a Mineral original · claro" : "Cambiar a Mineral Gris · oscuro";
      const label = button.querySelector("[data-theme-label]");
      if (label) label.textContent = dark ? "Modo oscuro" : "Modo claro";
    });
  }

  // Runs in the head before styles: no flash of the other appearance.
  apply(theme);
  document.addEventListener("DOMContentLoaded", () => {
    apply(root.dataset.theme);
    document.querySelectorAll("[data-theme-toggle]").forEach(button => {
      button.addEventListener("click", () => {
        const next = root.dataset.theme === "dark" ? "light" : "dark";
        const change = () => {
          apply(next);
          try { localStorage.setItem(key, next); } catch { /* In-memory preference only. */ }
        };
        if (typeof window.AtlasMotion?.transitionTheme === "function") {
          void window.AtlasMotion.transitionTheme(change);
        } else {
          change();
        }
      });
    });
  }, { once: true });
  window.addEventListener("storage", event => {
    if (event.key === key && valid(event.newValue)) apply(event.newValue);
  });
})();
