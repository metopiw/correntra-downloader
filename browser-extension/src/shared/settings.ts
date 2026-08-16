import type { ExtensionSettings } from "./types";

const SETTINGS_KEY = "settingsV1";
const DEFAULT_SETTINGS: ExtensionSettings = {
  masterEnabled: false,
  disabledHosts: [],
  sessionEnabled: false
};

let cachedSettings: ExtensionSettings = DEFAULT_SETTINGS;
let loadPromise: Promise<ExtensionSettings> | undefined;

function normalizeSettings(value: unknown): ExtensionSettings {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return { ...DEFAULT_SETTINGS };
  }

  const record = value as Record<string, unknown>;
  const hosts = Array.isArray(record.disabledHosts)
    ? record.disabledHosts
        .filter((host): host is string => typeof host === "string" && /^[a-z0-9.-]{1,253}$/i.test(host))
        .map((host) => host.toLowerCase())
        .slice(0, 200)
    : [];

  return {
    masterEnabled: record.masterEnabled === true,
    disabledHosts: [...new Set(hosts)],
    sessionEnabled: record.sessionEnabled === true
  };
}

export function loadSettings(): Promise<ExtensionSettings> {
  loadPromise ??= chrome.storage.local.get(SETTINGS_KEY).then((result) => {
    cachedSettings = normalizeSettings(result[SETTINGS_KEY]);
    return cachedSettings;
  });
  return loadPromise;
}

export function currentSettings(): ExtensionSettings {
  return cachedSettings;
}

async function saveSettings(settings: ExtensionSettings): Promise<ExtensionSettings> {
  cachedSettings = normalizeSettings(settings);
  await chrome.storage.local.set({ [SETTINGS_KEY]: cachedSettings });
  return cachedSettings;
}

export async function setMasterEnabled(enabled: boolean): Promise<ExtensionSettings> {
  const settings = await loadSettings();
  return saveSettings({ ...settings, masterEnabled: enabled });
}

export async function setHostEnabled(host: string, enabled: boolean): Promise<ExtensionSettings> {
  const settings = await loadSettings();
  const normalizedHost = host.trim().toLowerCase();
  if (!/^[a-z0-9.-]{1,253}$/i.test(normalizedHost)) {
    return settings;
  }

  const disabled = new Set(settings.disabledHosts);
  if (enabled) {
    disabled.delete(normalizedHost);
  } else {
    disabled.add(normalizedHost);
  }
  return saveSettings({ ...settings, disabledHosts: [...disabled].slice(-200) });
}

export async function setSessionEnabled(enabled: boolean): Promise<ExtensionSettings> {
  const settings = await loadSettings();
  return saveSettings({ ...settings, sessionEnabled: enabled });
}

export function isHostEnabled(host: string | null | undefined, settings = cachedSettings): boolean {
  return settings.masterEnabled && !!host && !settings.disabledHosts.includes(host.toLowerCase());
}
