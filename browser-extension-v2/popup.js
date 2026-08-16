const master = document.querySelector("#master-toggle");
const status = document.querySelector("#host-status");

function refresh() {
  chrome.runtime.sendMessage({ type: "popup.getState" }, (response) => {
    if (chrome.runtime.lastError || !response) {
      status.textContent = "offline";
      status.className = "status offline";
      return;
    }
    master.checked = response.masterEnabled === true;
    status.textContent = response.hostOnline ? "app ready" : "app offline";
    status.className = "status " + (response.hostOnline ? "online" : "offline");
  });
}

master.addEventListener("change", () => {
  chrome.runtime.sendMessage({ type: "settings.set", masterEnabled: master.checked }, refresh);
});

refresh();
