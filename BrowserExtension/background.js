// Bastion background service worker
// The app rotates a session token on launch. The extension fetches and caches it,
// then refreshes once if Bastion rejects a stale token.

const API = "http://localhost:59432/bastion";

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
    authedFetch(`${API}/exists`, {
      method: "POST",
      body: JSON.stringify(msg.credential || {})
    })
      .then(r => r?.json() ?? { exists: false })
      .then(d => sendResponse(d))
      .catch(() => sendResponse({ exists: false }));
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
