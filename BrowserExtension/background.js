// Bastion background service worker
// The app rotates a session token on launch. The extension fetches and caches it,
// then refreshes once if Bastion rejects a stale token.

const API = "http://localhost:59432/bastion";
const SAVE_PROMPT_COOLDOWN_MS = 5 * 60 * 1000;
const IGNORED_PROMPTS_KEY = "ignoredSavePromptHosts";

async function getToken() {
  const stored = await chrome.storage.local.get(["bastionToken"]);
  if (stored.bastionToken) return stored.bastionToken;
  return null;
}

async function fetchAndStoreToken() {
  try {
    const r = await fetch(`${API}/token`);
    if (!r.ok) return null;
    const d = await r.json();
    if (d.token) {
      await chrome.storage.local.set({ bastionToken: d.token });
      return d.token;
    }
  } catch { }
  return null;
}

async function authedFetch(url, options = {}) {
  let token = await getToken();
  if (!token) token = await fetchAndStoreToken();
  if (!token) return null;
  try {
    const r = await fetch(url, {
      ...options,
      headers: { ...(options.headers || {}), "X-Bastion-Token": token, "Content-Type": "application/json" }
    });
    if (r.status === 401) {
      // Token stale — refetch once
      token = await fetchAndStoreToken();
      if (!token) return null;
      return fetch(url, {
        ...options,
        headers: { ...(options.headers || {}), "X-Bastion-Token": token, "Content-Type": "application/json" }
      });
    }
    return r;
  } catch { return null; }
}

function addUrlCandidate(urls, value) {
  if (!value || typeof value !== "string") return;
  try {
    const parsed = new URL(value);
    if (parsed.hostname) urls.add(parsed.hostname);
    urls.add(parsed.href);
  } catch {
    urls.add(value);
  }
}

function normalizeHost(value) {
  try {
    return new URL(value).hostname.replace(/^www\./i, "").toLowerCase();
  } catch {
    return String(value || "")
      .replace(/^https?:\/\//i, "")
      .split("/")[0]
      .replace(/^www\./i, "")
      .toLowerCase();
  }
}

function normalizeEntry(entry) {
  return {
    id: entry.id ?? entry.Id ?? "",
    title: entry.title ?? entry.Title ?? "",
    username: entry.username ?? entry.Username ?? "",
    url: entry.url ?? entry.Url ?? ""
  };
}

function normalizeFillResponse(res) {
  return {
    username: res.username ?? res.Username ?? "",
    password: res.password ?? res.Password ?? "",
    error: res.error ?? res.Error ?? ""
  };
}

function getTab(tabId) {
  return new Promise(resolve => {
    if (typeof tabId !== "number") {
      resolve(null);
      return;
    }

    chrome.tabs.get(tabId, tab => {
      if (chrome.runtime.lastError) {
        resolve(null);
        return;
      }
      resolve(tab);
    });
  });
}

function getAllFrames(tabId) {
  return new Promise(resolve => {
    if (typeof tabId !== "number" || !chrome.webNavigation?.getAllFrames) {
      resolve([]);
      return;
    }

    chrome.webNavigation.getAllFrames({ tabId }, frames => {
      if (chrome.runtime.lastError || !Array.isArray(frames)) {
        resolve([]);
        return;
      }
      resolve(frames);
    });
  });
}

function sendTabMessage(tabId, message, frameId = undefined) {
  return new Promise(resolve => {
    const callback = response => {
      if (chrome.runtime.lastError) {
        resolve({ error: chrome.runtime.lastError.message });
        return;
      }
      resolve(response || {});
    };

    if (typeof frameId === "number")
      chrome.tabs.sendMessage(tabId, message, { frameId }, callback);
    else
      chrome.tabs.sendMessage(tabId, message, callback);
  });
}

async function getSearchCandidates(msg, sender) {
  const urls = new Set();
  (msg.urls || []).forEach(u => addUrlCandidate(urls, u));
  addUrlCandidate(urls, msg.url);
  addUrlCandidate(urls, sender?.url);
  addUrlCandidate(urls, sender?.tab?.url);

  const tabId = sender?.tab?.id;
  const tab = await getTab(tabId);
  addUrlCandidate(urls, tab?.url);
  addUrlCandidate(urls, tab?.pendingUrl);

  const frames = await getAllFrames(tabId);
  frames.forEach(frame => addUrlCandidate(urls, frame.url));

  return [...urls].filter(Boolean);
}

async function searchCredentials(msg, sender) {
  const seen = new Map();
  for (const url of await getSearchCandidates(msg, sender)) {
    const r = await authedFetch(`${API}/search?url=${encodeURIComponent(url)}`);
    const matches = await (r?.json() ?? []);
    if (!Array.isArray(matches)) continue;
    matches.map(normalizeEntry).forEach(entry => {
      if (entry.id) seen.set(entry.id, entry);
    });
  }
  return [...seen.values()];
}

async function fillActiveTab(tabId, entry) {
  if (typeof tabId !== "number") return { error: "No active tab" };

  const tried = new Set();
  const frameIds = [undefined];
  const frames = await getAllFrames(tabId);
  frames.forEach(frame => {
    if (typeof frame.frameId === "number" && !tried.has(frame.frameId)) {
      tried.add(frame.frameId);
      frameIds.push(frame.frameId);
    }
  });

  for (const frameId of frameIds) {
    const response = await sendTabMessage(tabId, { type: "BASTION_FILL_ENTRY", entry }, frameId);
    if (response?.ok) return response;
  }

  return { error: "No password field found on this tab" };
}

async function getIgnoredPromptHosts() {
  const stored = await chrome.storage.local.get([IGNORED_PROMPTS_KEY]);
  const hosts = stored[IGNORED_PROMPTS_KEY] || {};
  return typeof hosts === "object" && hosts ? hosts : {};
}

async function isPromptIgnored(url) {
  const host = normalizeHost(url);
  if (!host) return false;
  const hosts = await getIgnoredPromptHosts();
  return hosts[host] === true;
}

async function setPromptIgnored(url, ignored) {
  const host = normalizeHost(url);
  if (!host) return { host: "", ignored: false };
  const hosts = await getIgnoredPromptHosts();
  if (ignored) hosts[host] = true;
  else delete hosts[host];
  await chrome.storage.local.set({ [IGNORED_PROMPTS_KEY]: hosts });
  return { host, ignored: hosts[host] === true };
}

async function claimSavePrompt(key) {
  if (!key) return false;
  const now = Date.now();
  const stored = await chrome.storage.local.get(["savePromptClaims"]);
  const claims = stored.savePromptClaims || {};

  for (const [claimKey, timestamp] of Object.entries(claims)) {
    if (now - Number(timestamp) > SAVE_PROMPT_COOLDOWN_MS) delete claims[claimKey];
  }

  if (claims[key] && now - Number(claims[key]) <= SAVE_PROMPT_COOLDOWN_MS) {
    await chrome.storage.local.set({ savePromptClaims: claims });
    return false;
  }

  claims[key] = now;
  await chrome.storage.local.set({ savePromptClaims: claims });
  return true;
}

chrome.runtime.onInstalled.addListener(async () => {
  await fetchAndStoreToken();
});

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg.type === "SEARCH_URL") {
    searchCredentials(msg, sender)
      .then(d => sendResponse(d))
      .catch(() => sendResponse([]));
    return true;
  }
  if (msg.type === "FILL_REQUEST") {
    if (!msg.id) {
      sendResponse({ error: "Missing Bastion entry id" });
      return false;
    }

    authedFetch(`${API}/fill?id=${encodeURIComponent(msg.id)}`)
      .then(r => r?.json() ?? { error: "Failed" })
      .then(d => sendResponse(normalizeFillResponse(d)))
      .catch(() => sendResponse({ error: "Bastion not running" }));
    return true;
  }
  if (msg.type === "SAVE_CREDENTIALS") {
    authedFetch(`${API}/save`, {
      method: "POST",
      body: JSON.stringify(msg.credential || {})
    })
      .then(r => r?.json() ?? { error: "Failed" })
      .then(d => sendResponse(d))
      .catch(() => sendResponse({ error: "Bastion not running" }));
    return true;
  }
  if (msg.type === "CHECK_CREDENTIALS") {
    isPromptIgnored(msg.credential?.url || msg.url)
      .then(async ignored => {
        if (ignored) return { exists: true, ignored: true, status: "ignored" };
        const r = await authedFetch(`${API}/exists`, {
          method: "POST",
          body: JSON.stringify(msg.credential || {})
        });
        return r?.json() ?? { exists: false };
      })
      .then(d => sendResponse(d))
      .catch(() => sendResponse({ exists: false }));
    return true;
  }
  if (msg.type === "GET_SITE_PROMPT_STATE") {
    isPromptIgnored(msg.url || sender?.tab?.url || sender?.url)
      .then(ignored => sendResponse({ ignored, host: normalizeHost(msg.url || sender?.tab?.url || sender?.url) }))
      .catch(() => sendResponse({ ignored: false, host: "" }));
    return true;
  }
  if (msg.type === "SET_SITE_PROMPT_IGNORED") {
    setPromptIgnored(msg.url || sender?.tab?.url || sender?.url, msg.ignored === true)
      .then(state => sendResponse(state))
      .catch(() => sendResponse({ host: "", ignored: false }));
    return true;
  }
  if (msg.type === "GET_ACTIVE_TAB_CREDENTIALS") {
    searchCredentials({ urls: msg.urls || [] }, sender)
      .then(d => sendResponse(d))
      .catch(() => sendResponse([]));
    return true;
  }
  if (msg.type === "FILL_ACTIVE_TAB_ENTRY") {
    fillActiveTab(msg.tabId, msg.entry || {})
      .then(d => sendResponse(d))
      .catch(() => sendResponse({ error: "Could not fill this tab" }));
    return true;
  }
  if (msg.type === "CHECK_CREDENTIALS_LEGACY") {
    authedFetch(`${API}/exists`, {
      method: "POST",
      body: JSON.stringify(msg.credential || {})
    })
      .then(r => r?.json() ?? { exists: false })
      .then(d => sendResponse(d))
      .catch(() => sendResponse({ exists: false }));
    return true;
  }
  if (msg.type === "CLAIM_SAVE_PROMPT") {
    claimSavePrompt(msg.key || "")
      .then(claimed => sendResponse({ claimed }))
      .catch(() => sendResponse({ claimed: false }));
    return true;
  }
  if (msg.type === "PING") {
    authedFetch(`${API}/ping`)
      .then(r => r?.json() ?? { status: "offline" })
      .then(d => sendResponse(d))
      .catch(() => sendResponse({ status: "offline" }));
    return true;
  }
});
