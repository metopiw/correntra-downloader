"use strict";

const turkish = (navigator.language || "").toLowerCase().startsWith("tr");
const enabledBox = document.getElementById("enabled");
const label = document.getElementById("label");
const hint = document.getElementById("hint");

if (turkish) {
  label.textContent = "İndirmeleri ve videoları yakala";
  hint.textContent = "Correntra bu bilgisayarda çalışıyor olmalı.";
}

chrome.storage.local.get({ captureEnabled: true }, (stored) => {
  enabledBox.checked = stored.captureEnabled !== false;
});

enabledBox.addEventListener("change", () => {
  chrome.storage.local.set({ captureEnabled: enabledBox.checked });
});
