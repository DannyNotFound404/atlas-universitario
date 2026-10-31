"use strict";

const loginForm = document.getElementById("loginForm");
const loginButton = document.getElementById("loginButton");
const loginError = document.getElementById("loginError");
const setupWarning = document.getElementById("setupWarning");
const usernameInput = document.getElementById("username");
const passwordInput = document.getElementById("password");
const revealPassword = document.getElementById("revealPassword");

function showError(message) {
  loginError.textContent = message;
  loginError.classList.remove("hidden");
}

function hideError() {
  loginError.textContent = "";
  loginError.classList.add("hidden");
}

function setPasswordVisible(visible) {
  passwordInput.type = visible ? "text" : "password";
  revealPassword.classList.toggle("pressed", visible);
}

async function checkServerState() {
  try {
    const sessionResponse = await fetch("/api/v1/admin/session", {
      credentials: "same-origin",
      headers: { "Accept": "application/json" }
    });
    if (sessionResponse.ok) {
      window.location.replace("/dashboard");
      return;
    }

    const response = await fetch("/api/v1/admin/bootstrap-status", {
      credentials: "same-origin",
      headers: { "Accept": "application/json" }
    });
    if (!response.ok) {
      throw new Error("bootstrap_status_unavailable");
    }

    const serverState = await response.json();
    if (!serverState.configured) {
      setupWarning.classList.remove("hidden");
      loginButton.disabled = true;
    }
  } catch {
    showError(
      "No se pudo consultar el servidor. Verifica la conexión e inténtalo nuevamente."
    );
  }
}

loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  hideError();
  loginButton.disabled = true;
  loginButton.classList.add("loading");

  try {
    const response = await fetch("/api/v1/admin/login", {
      method: "POST",
      credentials: "same-origin",
      headers: {
        "Accept": "application/json",
        "Content-Type": "application/json"
      },
      body: JSON.stringify({
        username: usernameInput.value,
        password: passwordInput.value,
        code: document.getElementById("mfaCode").value || null
      })
    });

    setPasswordVisible(false);

    if (response.ok) {
      passwordInput.value = "";
      window.location.replace("/dashboard");
      return;
    }

    if (response.status === 429) {
      showError(
        "Demasiados intentos. Espera unos minutos antes de volver a probar."
      );
      return;
    }

    if (response.status === 401) {
      const detail = await response.json();
      if (detail.mfaRequired) {
        document.getElementById("mfaField").classList.remove("hidden");
        document.getElementById("mfaCode").required = true;
        document.getElementById("mfaCode").value = "";
        document.getElementById("mfaCode").focus();
        showError(detail.error === "mfa_required" ? "Ingrese el segundo factor para continuar." : "Código incorrecto, expirado o utilizado. Use uno nuevo.");
        return;
      }
      passwordInput.value = "";
      showError(
        "Usuario o contraseña incorrectos, o la cuenta está temporalmente bloqueada."
      );
      passwordInput.focus();
      return;
    }

    showError("El servidor rechazó la solicitud. Inténtalo nuevamente.");
  } catch {
    showError(
      "No fue posible comunicarse con el servidor. Revisa la conexión de red."
    );
  } finally {
    loginButton.disabled = false;
    loginButton.classList.remove("loading");
  }
});

revealPassword.addEventListener("pointerdown", (event) => {
  event.preventDefault();
  setPasswordVisible(true);
});
revealPassword.addEventListener("keydown", (event) => {
  if (event.key === "Enter" || event.key === " ") {
    event.preventDefault();
    setPasswordVisible(true);
  }
});

for (const eventName of [
  "pointerup",
  "pointercancel",
  "pointerleave",
  "keyup",
  "blur"
]) {
  revealPassword.addEventListener(eventName, () => {
    setPasswordVisible(false);
  });
}

void checkServerState();
