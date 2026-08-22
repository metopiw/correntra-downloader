"use strict";

const turkish = (navigator.language || "").toLowerCase().startsWith("tr");
const enabledBox = document.getElementById("enabled");
const label = document.getElementById("label");
const hint = document.getElementById("hint");
const statusBox = document.getElementById("status");
const statusText = document.getElementById("statusText");
const lastCaptureBox = document.getElementById("lastCapture");
const lastCaptureText = document.getElementById("lastCaptureText");
const lastCaptureWhen = document.getElementById("lastCaptureWhen");

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

function outcomeInfo(entry) {
  const name = entry.fileName || "?";
  const outcomes = turkish
    ? {
        captured: { text: `✓ ${name} → Correntra'ya devredildi`, cls: "ok" },
        "agent-unreachable": { text: `${name} → Correntra'ya ulaşılamadı, Chrome indirdi`, cls: "err" },
        rejected: { text: `${name} → Correntra reddetti (${entry.reason || "?"})`, cls: "err" },
        error: { text: `${name} → aktarım hatası (${entry.reason || "?"})`, cls: "err" },
        "capture-off": { text: `${name} → yakalama anahtarı kapalı`, cls: "warn" },
        "already-complete": { text: `${name} → Chrome indirmeyi bitirmişti, devredilmedi`, cls: "warn" },
      }
    : {
        captured: { text: `✓ ${name} → handed to Correntra`, cls: "ok" },
        "agent-unreachable": { text: `${name} → Correntra unreachable; Chrome kept it`, cls: "err" },
        rejected: { text: `${name} → rejected by Correntra (${entry.reason || "?"})`, cls: "err" },
        error: { text: `${name} → hand-off failed (${entry.reason || "?"})`, cls: "err" },
        "capture-off": { text: `${name} → capture switch is off`, cls: "warn" },
        "already-complete": { text: `${name} → Chrome had already finished it`, cls: "warn" },
      };
  return outcomes[entry.outcome] || null;
}

chrome.storage.local.get({ captureLog: [] }, (stored) => {
  const log = Array.isArray(stored.captureLog) ? stored.captureLog : [];
  const latest = log.find((entry) => outcomeInfo(entry));
  if (!latest) {
    return;
  }

  const info = outcomeInfo(latest);
  lastCaptureText.textContent = info.text;
  lastCaptureBox.classList.add(info.cls);
  lastCaptureWhen.textContent = new Date(latest.at).toLocaleString();
  lastCaptureBox.hidden = false;
});
