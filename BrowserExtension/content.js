// Content script — detects login forms and injects autofill button
(function () {
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

  function findLoginForms() {
    if (!document.body) return;
    const inputs = document.querySelectorAll("input[type=password], input[autocomplete='current-password'], input[autocomplete='new-password']");
    inputs.forEach(pwInput => {
      if (pwInput.dataset.bastionInjected) return;
      pwInput.dataset.bastionInjected = "1";

      const btn = document.createElement("button");
      btn.textContent = "🔒";
      btn.title = "Autofill with Bastion";
      btn.style.cssText = `
        position:absolute; z-index:999999; background:#7C3AED; color:white;
        border:none; border-radius:4px; padding:2px 6px; cursor:pointer;
        font-size:13px; box-shadow:0 2px 8px rgba(0,0,0,0.3);
      `;

      // Position button inside the password field
      function positionBtn() {
        const rect = pwInput.getBoundingClientRect();
        btn.style.top  = (window.scrollY + rect.top + (rect.height - 22) / 2) + "px";
        btn.style.left = (window.scrollX + rect.right - 32) + "px";
      }
      positionBtn();
      window.addEventListener("scroll", positionBtn, { passive: true });
      window.addEventListener("resize", positionBtn, { passive: true });

      btn.addEventListener("click", async (e) => {
        e.preventDefault(); e.stopPropagation();
        chrome.runtime.sendMessage({ type: "SEARCH_URL", urls: getUrlCandidates() }, (results) => {
          if (!results || results.length === 0) {
            showNotification("No matching credentials in Bastion.");
            return;
          }
          if (results.length === 1) {
            fillCredentials(results[0], pwInput);
          } else {
            showPicker(results, pwInput);
          }
        });
      });

      document.body.appendChild(btn);
      attachSavePrompt(pwInput);
    });
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

  function sendRuntimeMessage(message) {
    return new Promise(resolve => chrome.runtime.sendMessage(message, resolve));
  }

  function attachSavePrompt(pwInput) {
    let form = pwInput;
    while (form && form.tagName !== "FORM") form = form.parentElement;
    const target = form || pwInput;
    if (target.dataset.bastionSaveHooked) return;
    target.dataset.bastionSaveHooked = "1";

    let promptInFlight = false;
    let lastPromptKey = "";
    let lastPromptAt = 0;

    const handler = () => {
      const userInput = findUsernameInput(form || document);
      const username = userInput?.value || "";
      const password = pwInput.value || "";
      if (!username || !password) return;

      const credential = {
        url: window.location.href,
        title: document.title || window.location.hostname,
        username,
        password
      };
      const promptKey = `${window.location.origin}\n${username}\n${password}`;
      const now = Date.now();
      if (promptInFlight || (promptKey === lastPromptKey && now - lastPromptAt < 15000)) return;
      promptInFlight = true;
      lastPromptKey = promptKey;
      lastPromptAt = now;

      setTimeout(async () => {
        try {
          const exists = await sendRuntimeMessage({ type: "CHECK_CREDENTIALS", credential });
          if (exists?.exists) return;

        if (!confirm("Save this login to Bastion?")) return;
          chrome.runtime.sendMessage({
          type: "SAVE_CREDENTIALS",
            credential
        }, (res) => {
          if (res?.error) showNotification(res.error);
          else showNotification("Saved login to Bastion.");
        });
        } finally {
          setTimeout(() => { promptInFlight = false; }, 1000);
        }
      }, 300);
    };

    if (form) form.addEventListener("submit", handler, true);
    pwInput.addEventListener("change", handler, true);
  }

  function setInputValue(input, value) {
    input.focus();

    const prototype = Object.getPrototypeOf(input);
    const descriptor = Object.getOwnPropertyDescriptor(prototype, "value");
    if (descriptor?.set) {
      descriptor.set.call(input, value);
    } else {
      input.value = value;
    }

    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));
    input.dispatchEvent(new KeyboardEvent("keyup", { bubbles: true }));
  }

  function fillCredentials(entry, pwInput) {
    chrome.runtime.sendMessage({ type: "FILL_REQUEST", id: entry.id }, (res) => {
      if (res.error) { showNotification(res.error); return; }
      if (!res.password) { showNotification("Bastion returned no password for this entry."); return; }

      // Find username field — closest input[type=text/email] before password
      let el = pwInput;
      while (el && el.tagName !== "FORM") el = el.parentElement;
      const form = el;
      const userInput = findUsernameInput(form || document);
      if (userInput) {
        setInputValue(userInput, res.username);
      }
      setInputValue(pwInput, res.password);
      showNotification(`✓ Filled from Bastion: ${entry.title}`);
    });
  }

  function showPicker(results, pwInput) {
    const existing = document.getElementById("bastion-picker");
    if (existing) existing.remove();

    const rect = pwInput.getBoundingClientRect();
    const picker = document.createElement("div");
    picker.id = "bastion-picker";
    picker.style.cssText = `
      position:fixed; top:${rect.bottom + 4}px; left:${rect.left}px;
      background:#1A1A1A; border:1px solid #333; border-radius:8px;
      z-index:9999999; min-width:240px; box-shadow:0 8px 24px rgba(0,0,0,0.5);
      font-family:Segoe UI,sans-serif;
    `;
    picker.innerHTML = `<div style="padding:8px 12px;font-size:11px;color:#666;border-bottom:1px solid #222">BASTION AUTOFILL</div>`;
    results.forEach(r => {
      const item = document.createElement("div");
      item.style.cssText = "padding:10px 14px;cursor:pointer;color:#CCCCCC;font-size:13px;";
      item.textContent = `🔐 ${r.title} — ${r.username}`;
      item.addEventListener("mouseenter", () => item.style.background = "#2D2D2D");
      item.addEventListener("mouseleave", () => item.style.background = "");
      item.addEventListener("click", () => { picker.remove(); fillCredentials(r, pwInput); });
      picker.appendChild(item);
    });
    document.body.appendChild(picker);
    document.addEventListener("click", () => picker.remove(), { once: true });
  }

  function showNotification(msg) {
    const n = document.createElement("div");
    n.style.cssText = `
      position:fixed; bottom:20px; right:20px; background:#1A1A1A;
      color:#E8E8E8; border:1px solid #333; border-radius:8px;
      padding:12px 16px; z-index:9999999; font-size:13px;
      font-family:Segoe UI,sans-serif; box-shadow:0 4px 16px rgba(0,0,0,0.4);
    `;
    n.textContent = msg;
    document.body.appendChild(n);
    setTimeout(() => n.remove(), 3000);
  }

  // Initial scan + mutation observer for SPAs
  setTimeout(findLoginForms, 500);
  const obs = new MutationObserver(() => findLoginForms());
  if (document.body) obs.observe(document.body, { childList: true, subtree: true });
})();
