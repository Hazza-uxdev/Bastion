const statusEl = document.getElementById("status");
const statusTitle = document.getElementById("statusTitle");
const statusCopy = document.getElementById("statusCopy");
const refreshBtn = document.getElementById("refresh");

function pingBastion() {
  return new Promise(resolve => {
    chrome.runtime.sendMessage({ type: "PING" }, response => {
      resolve(response || { status: "offline" });
    });
  });
}

function setStatus(kind, title, copy) {
  statusEl.className = `status ${kind}`;
  statusTitle.textContent = title;
  statusCopy.textContent = copy;
}

async function checkConnection() {
  setStatus("", "Checking Bastion", "Looking for the local vault bridge...");
  refreshBtn.disabled = true;

  const response = await pingBastion();
  refreshBtn.disabled = false;

  if (response.status === "ok") {
    setStatus("ok", "Connected", `Bastion desktop API ${response.version || "local"} is unlocked and ready.`);
    return;
  }

  setStatus("err", "Locked or offline", "Open Bastion and enter your master password. Autofill locks again when the vault auto-locks.");
}

refreshBtn.addEventListener("click", checkConnection);
checkConnection();
