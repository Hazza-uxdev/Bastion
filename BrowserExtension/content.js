// Detects login fields, adds a Bastion autofill button, and offers non-blocking save/update prompts.
(function () {
  const promptCache = new Map();
  const PROMPT_COOLDOWN_MS = 5 * 60 * 1000;
  const FILL_SUPPRESS_MS = 15000;

  function getUrlCandidates() {
    const urls = new Set([window.location.href, window.location.hostname, document.baseURI]);
    if (document.referrer) urls.add(document.referrer);

    try {
      if (window.top && window.top !== window) urls.add(window.top.location.href);
    } catch {
      // Cross-origin frames cannot read top.location; background.js adds sender.tab.url.
    }

    return [...urls].filter(Boolean);
  }

  function findUsernameInput(scope) {
    return (scope || document).querySelector([
      "input[autocomplete='username']",
      "input[type=email]",
      "input[name*=email i]",
      "input[id*=email i]",
      "input[name*=user i]",
      "input[id*=user i]",
      "input[type=text]"
    ].join(","));
  }

  function runtimeMessage(message) {
    return new Promise(resolve => chrome.runtime.sendMessage(message, response => resolve(response || {})));
  }

  function hashString(value) {
    let hash = 2166136261;
    for (let i = 0; i < value.length; i++) {
      hash ^= value.charCodeAt(i);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(36);
  }

  function normalizeHost(value) {
    try {
      return new URL(value).hostname.replace(/^www\./i, "").toLowerCase();
    } catch {
      return String(value || window.location.hostname || "")
        .replace(/^https?:\/\//i, "")
        .split("/")[0]
        .replace(/^www\./i, "")
        .toLowerCase();
    }
  }

  function promptKey(credential) {
    const host = normalizeHost(credential.url);
    return `${host}:${credential.username.toLowerCase()}:${hashString(credential.password)}`;
  }

  function wasPromptedRecently(key) {
    const now = Date.now();
    for (const [cachedKey, timestamp] of promptCache) {
      if (now - timestamp > PROMPT_COOLDOWN_MS) promptCache.delete(cachedKey);
    }
    return promptCache.has(key);
  }

  function rememberPrompt(key) {
    promptCache.set(key, Date.now());
  }

  function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  function findLoginFields() {
    if (!document.body) return;
    const passwordInputs = document.querySelectorAll("input[type=password], input[autocomplete='current-password'], input[autocomplete='new-password']");
    passwordInputs.forEach(passwordInput => {
      if (passwordInput.dataset.bastionInjected) return;
      passwordInput.dataset.bastionInjected = "1";

      injectAutofillButton(passwordInput);
      attachSavePrompt(passwordInput);
    });
  }

  function injectAutofillButton(passwordInput) {
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = "B";
    button.title = "Autofill with Bastion";
    button.style.cssText = `
      position:absolute; z-index:999999; width:24px; height:22px;
      background:#7c3aed; color:#fff; border:0; border-radius:6px;
      padding:0; cursor:pointer; font:700 12px/22px Segoe UI, sans-serif;
      box-shadow:0 2px 10px rgba(0,0,0,0.28);
    `;

    function positionButton() {
      const rect = passwordInput.getBoundingClientRect();
      button.style.top = `${window.scrollY + rect.top + Math.max((rect.height - 22) / 2, 0)}px`;
      button.style.left = `${window.scrollX + rect.right - 30}px`;
    }

    positionButton();
    window.addEventListener("scroll", positionButton, { passive: true });
    window.addEventListener("resize", positionButton, { passive: true });

    button.addEventListener("click", event => {
      event.preventDefault();
      event.stopPropagation();

      chrome.runtime.sendMessage({ type: "SEARCH_URL", urls: getUrlCandidates() }, results => {
        if (!Array.isArray(results) || results.length === 0) {
          showNotification("No matching credentials in Bastion.");
          return;
        }

        if (results.length === 1) fillCredentials(results[0], passwordInput);
        else showPicker(results, passwordInput);
      });
    });

    document.body.appendChild(button);
  }

  function attachSavePrompt(passwordInput) {
    let form = passwordInput;
    while (form && form.tagName !== "FORM") form = form.parentElement;

    const target = form || passwordInput;
    if (target.dataset.bastionSaveHooked) return;
    target.dataset.bastionSaveHooked = "1";

    let inFlight = false;

    async function maybePromptToSave() {
      const usernameInput = findUsernameInput(form || document);
      const username = usernameInput?.value?.trim() || "";
      const password = passwordInput.value || "";
      const lastFill = Number(passwordInput.dataset.bastionFilledAt || 0);
      if (!username || !password || inFlight || Date.now() - lastFill < FILL_SUPPRESS_MS) return;

      const credential = {
        url: window.location.href,
        title: document.title || window.location.hostname,
        username,
        password
      };

      const key = promptKey(credential);
      if (wasPromptedRecently(key)) return;
      inFlight = true;

      try {
        const firstCheck = await runtimeMessage({ type: "CHECK_CREDENTIALS", credential });
        if (firstCheck.exists) {
          rememberPrompt(key);
          return;
        }

        await delay(350);
        const secondCheck = await runtimeMessage({ type: "CHECK_CREDENTIALS", credential });
        if (secondCheck.exists) {
          rememberPrompt(key);
          return;
        }

        const claim = await runtimeMessage({ type: "CLAIM_SAVE_PROMPT", key });
        if (!claim.claimed) return;

        rememberPrompt(key);
        showSaveCard(credential, secondCheck.usernameMatch || firstCheck.usernameMatch ? "update" : "save");
      } finally {
        setTimeout(() => { inFlight = false; }, 1400);
      }
    }

    if (form) {
      form.addEventListener("submit", () => setTimeout(maybePromptToSave, 350), true);
    }

    passwordInput.addEventListener("change", () => setTimeout(maybePromptToSave, 900), true);
    passwordInput.addEventListener("blur", () => setTimeout(maybePromptToSave, 900), true);
  }

  function setInputValue(input, value) {
    input.focus();

    const prototype = Object.getPrototypeOf(input);
    const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
    if (descriptor?.set) descriptor.set.call(input, value);
    else input.value = value;

    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
    input.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true }));
  }

  function fillCredentials(entry, passwordInput) {
    chrome.runtime.sendMessage({ type: "FILL_REQUEST", id: entry.id }, response => {
      if (response?.error) {
        showNotification(response.error);
        return;
      }
      if (!response?.password) {
        showNotification("Bastion returned no password for this entry.");
        return;
      }

      let form = passwordInput;
      while (form && form.tagName !== "FORM") form = form.parentElement;

      const usernameInput = findUsernameInput(form || document);
      if (usernameInput) setInputValue(usernameInput, response.username || "");
      passwordInput.dataset.bastionFilledAt = String(Date.now());
      setInputValue(passwordInput, response.password);
      showNotification(`Filled from Bastion: ${entry.title || entry.username || "credential"}`);
    });
  }

  function showPicker(results, passwordInput) {
    const existing = document.getElementById("bastion-picker");
    if (existing) existing.remove();

    const rect = passwordInput.getBoundingClientRect();
    const picker = document.createElement("div");
    picker.id = "bastion-picker";
    picker.style.cssText = `
      position:fixed; top:${rect.bottom + 6}px; left:${rect.left}px;
      background:#171717; border:1px solid #303030; border-radius:10px;
      z-index:9999999; min-width:270px; max-width:360px;
      box-shadow:0 18px 42px rgba(0,0,0,0.42);
      font-family:Segoe UI, system-ui, sans-serif; overflow:hidden;
    `;

    const header = document.createElement("div");
    header.textContent = "BASTION AUTOFILL";
    header.style.cssText = "padding:10px 12px;font-size:11px;color:#888;border-bottom:1px solid #282828;letter-spacing:.05em;";
    picker.appendChild(header);

    results.forEach(result => {
      const item = document.createElement("button");
      item.type = "button";
      item.style.cssText = `
        display:block;width:100%;text-align:left;padding:11px 12px;
        border:0;background:transparent;color:#ddd;cursor:pointer;
        font:13px Segoe UI, system-ui, sans-serif;
      `;
      item.textContent = `${result.title || "Credential"} - ${result.username || ""}`;
      item.addEventListener("mouseenter", () => { item.style.background = "#252525"; });
      item.addEventListener("mouseleave", () => { item.style.background = "transparent"; });
      item.addEventListener("click", event => {
        event.preventDefault();
        picker.remove();
        fillCredentials(result, passwordInput);
      });
      picker.appendChild(item);
    });

    document.body.appendChild(picker);
    setTimeout(() => document.addEventListener("click", () => picker.remove(), { once: true }), 0);
  }

  function showSaveCard(credential, mode) {
    const existing = document.getElementById("bastion-save-card");
    if (existing) existing.remove();

    const card = document.createElement("div");
    card.id = "bastion-save-card";
    card.style.cssText = `
      position:fixed; top:18px; right:18px; z-index:2147483647;
      width:286px; background:#171717; color:#e8e8e8;
      border:1px solid #303030; border-radius:12px; overflow:hidden;
      box-shadow:0 18px 45px rgba(0,0,0,0.42);
      font-family:Segoe UI, system-ui, sans-serif;
    `;

    const accent = mode === "update" ? "#06b6d4" : "#7c3aed";
    const host = normalizeHost(credential.url);
    card.innerHTML = `
      <div style="display:flex;align-items:center;gap:10px;padding:12px 13px;border-bottom:1px solid #262626;">
        <div style="width:28px;height:28px;border-radius:8px;background:${accent};display:grid;place-items:center;font-weight:700;color:white;box-shadow:0 0 0 1px rgba(255,255,255,0.12) inset;">B</div>
        <div style="min-width:0;">
          <div style="font-size:13px;font-weight:650;">${mode === "update" ? "Update saved login?" : "Save login to Bastion?"}</div>
          <div style="font-size:11px;color:#888;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${escapeHtml(host)} - ${escapeHtml(credential.username)}</div>
        </div>
        <button data-close style="margin-left:auto;border:0;background:transparent;color:#777;cursor:pointer;font-size:15px;line-height:1;">x</button>
      </div>
      <div style="padding:11px 13px 12px;">
        <div style="font-size:12px;color:#aaa;line-height:1.45;margin-bottom:12px;">${mode === "update" ? "Bastion found this username. Update the saved password?" : "Add this login to your encrypted vault?"}</div>
        <div style="display:flex;gap:8px;">
          <button data-save style="flex:1;height:30px;border:0;border-radius:7px;background:${accent};color:white;font:600 12px Segoe UI,system-ui;cursor:pointer;">${mode === "update" ? "Update" : "Save"}</button>
          <button data-close style="width:82px;height:30px;border:1px solid #343434;border-radius:7px;background:transparent;color:#bbb;font:12px Segoe UI,system-ui;cursor:pointer;">Not now</button>
        </div>
      </div>
    `;

    card.querySelectorAll("[data-close]").forEach(button => {
      button.addEventListener("click", () => card.remove());
    });
    card.querySelector("[data-save]").addEventListener("click", async () => {
      const saveButton = card.querySelector("[data-save]");
      saveButton.disabled = true;
      saveButton.textContent = "Checking...";

      const finalCheck = await runtimeMessage({ type: "CHECK_CREDENTIALS", credential });
      if (finalCheck.exists) {
        card.remove();
        showNotification("Already saved in Bastion.");
        return;
      }

      saveButton.textContent = mode === "update" ? "Updating..." : "Saving...";
      const response = await runtimeMessage({ type: "SAVE_CREDENTIALS", credential });
      card.remove();
      if (response.error) showNotification(response.error);
      else if (response.duplicate) showNotification("Already saved in Bastion.");
      else showNotification(response.status === "updated" ? "Updated saved login in Bastion." : "Saved login to Bastion.");
    });

    document.body.appendChild(card);
    setTimeout(() => card.remove(), 15000);
  }

  function showNotification(message) {
    const notification = document.createElement("div");
    notification.style.cssText = `
      position:fixed; bottom:20px; right:20px; background:#171717;
      color:#e8e8e8; border:1px solid #333; border-radius:10px;
      padding:11px 14px; z-index:9999999; font-size:13px;
      font-family:Segoe UI, system-ui, sans-serif;
      box-shadow:0 10px 26px rgba(0,0,0,0.36);
    `;
    notification.textContent = message;
    document.body.appendChild(notification);
    setTimeout(() => notification.remove(), 3000);
  }

  function escapeHtml(value) {
    return String(value || "").replace(/[&<>"']/g, char => ({
      "&": "&amp;",
      "<": "&lt;",
      ">": "&gt;",
      "\"": "&quot;",
      "'": "&#039;"
    }[char]));
  }

  setTimeout(findLoginFields, 500);

  const observer = new MutationObserver(() => findLoginFields());
  if (document.body) {
    observer.observe(document.body, { childList: true, subtree: true });
  }
})();
