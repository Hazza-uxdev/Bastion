const statusEl = document.getElementById("status");
const statusTitle = document.getElementById("statusTitle");
const statusCopy = document.getElementById("statusCopy");
const refreshBtn = document.getElementById("refresh");
const searchBox = document.getElementById("search");
const credentialsEl = document.getElementById("credentials");
const siteHostEl = document.getElementById("siteHost");
const togglePromptsBtn = document.getElementById("togglePrompts");

let activeTab = null;
let credentials = [];
let promptsIgnored = false;

function runtimeMessage(message) {
  return new Promise(resolve => chrome.runtime.sendMessage(message, response => resolve(response || {})));
}

function queryActiveTab() {
  return new Promise(resolve => {
    chrome.tabs.query({ active: true, currentWindow: true }, tabs => resolve(tabs?.[0] || null));
  });
}

function normalizeHost(value) {
  try {
    return new URL(value).hostname.replace(/^www\./i, "").toLowerCase();
  } catch {
    return "";
  }
}

function setStatus(kind, title, copy) {
  statusEl.className = `status ${kind}`;
  statusTitle.textContent = title;
  statusCopy.textContent = copy;
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

async function pingBastion() {
  return runtimeMessage({ type: "PING" });
}

async function loadSiteState() {
  activeTab = await queryActiveTab();
  const url = activeTab?.url || "";
  const host = normalizeHost(url);
  siteHostEl.textContent = host || "No active website";

  const state = await runtimeMessage({ type: "GET_SITE_PROMPT_STATE", url });
  promptsIgnored = state.ignored === true;
  togglePromptsBtn.textContent = promptsIgnored ? "Enable prompts" : "Ignore site";
  togglePromptsBtn.disabled = !host;
}

function renderCredentials() {
  const query = searchBox.value.trim().toLowerCase();
  const filtered = credentials.filter(entry =>
    `${entry.title || ""} ${entry.username || ""} ${entry.url || ""}`.toLowerCase().includes(query));

  credentialsEl.innerHTML = "";
  if (filtered.length === 0) {
    const empty = document.createElement("div");
    empty.className = "muted";
    empty.textContent = credentials.length === 0 ? "No matching logins for this tab." : "No search results.";
    credentialsEl.appendChild(empty);
    return;
  }

  filtered.forEach(entry => {
    const row = document.createElement("div");
    row.className = "entry";
    row.innerHTML = `
      <div style="min-width:0;">
        <div class="entry-title">${escapeHtml(entry.title || "Credential")}</div>
        <div class="entry-sub">${escapeHtml(entry.username || "")}</div>
      </div>
      <button class="primary" data-fill="${escapeHtml(entry.id)}">Fill</button>
    `;
    row.querySelector("[data-fill]").addEventListener("click", async () => {
      if (!activeTab?.id) return;
      const response = await runtimeMessage({ type: "FILL_ACTIVE_TAB_ENTRY", tabId: activeTab.id, entry });
      if (response?.error) {
        setStatus("err", "Cannot fill this page", response.error || "Refresh the page and try again.");
        return;
      }
      window.close();
    });
    credentialsEl.appendChild(row);
  });
}

async function loadCredentials() {
  refreshBtn.disabled = true;
  const response = await pingBastion();

  if (response.status !== "ok") {
    setStatus("err", "Vault locked or desktop app closed", "Open Bastion and enter the vault password. The extension locks again when Bastion auto-locks.");
    credentials = [];
    renderCredentials();
    refreshBtn.disabled = false;
    return;
  }

  setStatus("ok", "Connected", `Bastion desktop API ${response.version || "local"} is unlocked.`);
  const urls = [activeTab?.url, activeTab?.pendingUrl].filter(Boolean);
  credentials = await runtimeMessage({ type: "SEARCH_URL", urls });
  if (!Array.isArray(credentials)) credentials = [];
  renderCredentials();
  refreshBtn.disabled = false;
}

async function refreshAll() {
  await loadSiteState();
  await loadCredentials();
}

refreshBtn.addEventListener("click", refreshAll);
searchBox.addEventListener("input", renderCredentials);
togglePromptsBtn.addEventListener("click", async () => {
  const url = activeTab?.url || "";
  const state = await runtimeMessage({ type: "SET_SITE_PROMPT_IGNORED", url, ignored: !promptsIgnored });
  promptsIgnored = state.ignored === true;
  togglePromptsBtn.textContent = promptsIgnored ? "Enable prompts" : "Ignore site";
});

refreshAll();
