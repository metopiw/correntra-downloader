"use strict";

const turkish = (navigator.language || "").toLowerCase().startsWith("tr");
const enabledBox = document.getElementById("enabled");
const label = document.getElementById("label");
const hint = document.getElementById("hint");
const statusBox = document.getElementById("status");
const statusText = document.getElementById("statusText");

if (turkish) {
  label.textContent = "İndirmeleri ve videoları yakala";
  hint.textContent = "Correntra bu bilgisayarda çalışıyor olmalı; kapalıyken tarayıcı dosyayı kendisi indirir.";
}

chrome.storage.local.get({ captureEnabled: true }, (stored) => {
  enabledBox.checked = stored.captureEnabled !== false;
});

enabledBox.addEventListener("change", () => {
  chrome.storage.local.set({ captureEnabled: enabledBox.checked });
});

// Show whether Correntra can actually receive captures right now — a red dot
// explains why a download "was not captured" without any guessing.
chrome.runtime.sendMessage({ type: "correntra.ping" }, (response) => {
  const online = Boolean(response && response.online);
  statusBox.classList.add(online ? "online" : "offline");
  if (turkish) {
    statusText.textContent = online
      ? "Correntra çalışıyor — yakalama açık"
      : "Correntra kapalı — önce baslat.bat";
  } else {
    statusText.textContent = online
      ? "Correntra is running — capture active"
      : "Correntra is not running — start it first";
  }
});
