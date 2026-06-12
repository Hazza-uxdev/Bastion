const statusEl = document.getElementById("status");

async function check() {
  return new Promise(res => {
    chrome.runtime.sendMessage({ type: "PING" }, r => {
      res(r?.status === "ok");
    });
  });
}

async function init() {
  const ok = await check();
  if (ok) {
    statusEl.textContent = "✓ Connected to Bastion";
    statusEl.className = "status ok";
  } else {
    // Try to auto-fetch token
    statusEl.textContent = "Connecting…";
    await new Promise(r => setTimeout(r, 600));
    const ok2 = await check();
    if (ok2) {
      statusEl.textContent = "✓ Connected to Bastion";
      statusEl.className = "status ok";
    } else {
      statusEl.textContent = "✗ Bastion app not running. Open it first.";
      statusEl.className = "status err";
    }
  }
}

init();
