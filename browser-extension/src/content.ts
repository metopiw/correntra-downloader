import { OPAQUE_ID_PATTERN } from "./shared/candidate-index";
import type { PublicCandidate, RuntimeResponse } from "./shared/types";
import { isSocialVideoHost } from "./shared/url";

const globalMarker = globalThis as typeof globalThis & { __correntraContentLoaded?: boolean };

function message(key: string, fallback: string): string {
  return chrome.i18n.getMessage(key) || fallback;
}

function sendMessage(request: Record<string, unknown>): Promise<RuntimeResponse> {
  return new Promise((resolve) => {
    try {
      chrome.runtime.sendMessage(request, (response: RuntimeResponse | undefined) => {
        if (chrome.runtime.lastError) {
          resolve({ ok: false, error: "appUnavailable" });
          return;
        }
        resolve(response ?? { ok: false, error: "appUnavailable" });
      });
    } catch {
      resolve({ ok: false, error: "appUnavailable" });
    }
  });
}

function validCandidate(value: unknown): value is PublicCandidate {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    return false;
  }
  const candidate = value as Partial<PublicCandidate>;
  return (
    typeof candidate.id === "string" &&
    OPAQUE_ID_PATTERN.test(candidate.id) &&
    typeof candidate.kind === "string" &&
    ["video", "audio", "hls", "dash"].includes(candidate.kind) &&
    typeof candidate.title === "string" &&
    candidate.title.length <= 140 &&
    typeof candidate.lastSeenAt === "number"
  );
}

class MediaOverlay {
  readonly #host: HTMLDivElement;
  readonly #shadow: ShadowRoot;
  readonly #candidates = new Map<string, PublicCandidate>();
  #enabled = false;
  #notice = "";
  #noticeTimer: number | undefined;
  #videoElement: HTMLVideoElement | null = null;
  #dismissedFor = new Map<string, number>();
  #pageActive = false;
  #pageDismissedAt = 0;
  #audioFallbackId: string | null = null;
  #manualLeft: number | null = null;
  #manualTop: number | null = null;

  constructor() {
    this.#host = document.createElement("div");
    this.#host.dataset.correntra = "overlay";
    this.#host.style.all = "initial";
    this.#host.style.position = "fixed";
    this.#host.style.top = "0";
    this.#host.style.left = "0";
    this.#host.style.zIndex = "2147483647";
    this.#shadow = this.#host.attachShadow({ mode: "closed" });
    this.#shadow.append(this.#style(), document.createElement("div"));
  }

  setEnabled(enabled: boolean): void {
    this.#enabled = enabled;
    if (!enabled) {
      this.#host.remove();
      this.#candidates.clear();
      this.#videoElement = null;
      this.#pageActive = false;
      this.#manualLeft = null;
      this.#manualTop = null;
    }
    this.#render();
  }

  setPageCapture(active: boolean): void {
    this.#pageActive = active;
    if (active) {
      this.#positionFromVideo();
    }
    this.#render();
  }

  setVideoElement(element: HTMLVideoElement | null): void {
    this.#videoElement = element;
    if (this.#manualLeft === null && this.#manualTop === null) {
      this.#positionFromVideo();
    }
  }

  add(candidate: PublicCandidate): void {
    if (!this.#enabled || !validCandidate(candidate)) {
      return;
    }
    this.#candidates.set(candidate.id, candidate);
    this.#positionFromVideo();
    this.#render();
  }

  showProtected(): void {
    if (!this.#enabled) {
      return;
    }
    this.#notice = message("protectedMedia", "Protected media cannot be downloaded.");
    this.#render();
    if (this.#noticeTimer !== undefined) {
      window.clearTimeout(this.#noticeTimer);
    }
    this.#noticeTimer = window.setTimeout(() => {
      this.#notice = "";
      this.#render();
    }, 5_000);
  }

  #mount(): void {
    if (!this.#host.isConnected && document.documentElement) {
      document.documentElement.append(this.#host);
    }
  }

  #positionFromVideo(): void {
    if (!this.#videoElement || !this.#videoElement.isConnected) {
      this.#videoElement = null;
      return;
    }
    const rect = this.#videoElement.getBoundingClientRect();
    if (rect.width < 120 || rect.height < 70) {
      return;
    }
    const gap = 8;
    let left = rect.right - 260;
    let top = rect.top + gap;
    // Keep the panel inside the viewport.
    if (left < gap) {
      left = gap;
    }
    if (top < gap) {
      top = gap;
    }
    this.#host.style.left = `${Math.round(left)}px`;
    this.#host.style.top = `${Math.round(top)}px`;
  }

  #style(): HTMLStyleElement {
    const style = document.createElement("style");
    style.textContent = `
      :host { all: initial; }
      .panel {
        display: flex; flex-direction: column; gap: 6px;
        font: 600 12px/1.25 "Segoe UI Variable", "Segoe UI", sans-serif;
        color: #eef9f6; pointer-events: auto;
      }
      .row {
        all: unset; box-sizing: border-box; display: flex; align-items: center; gap: 7px;
        border: 1px solid rgba(96, 229, 184, .38);
        border-radius: 8px; background: rgba(11, 18, 24, .92);
        box-shadow: 0 8px 22px rgba(0, 0, 0, .34), inset 0 1px rgba(255, 255, 255, .05);
        backdrop-filter: blur(12px); -webkit-backdrop-filter: blur(12px);
        padding: 5px 6px 5px 7px; cursor: grab; user-select: none;
      }
      .row.dragging { cursor: grabbing; }
      .row:hover { border-color: #70f1c6; }
      .mark { display: grid; place-items: center; width: 22px; height: 22px; border-radius: 6px;
        background: linear-gradient(145deg, rgba(92, 231, 184, .2), rgba(50, 167, 255, .16));
        color: #70f1c6; font-size: 12px; flex: 0 0 auto; }
      .action { color: #f0faf7; white-space: nowrap; font-size: 11px; }
      .meta { color: #849a95; font-size: 9px; font-weight: 500; }
      .spacer { flex: 1 1 auto; }
      .close { all: unset; box-sizing: border-box; display: grid; place-items: center; width: 18px; height: 18px;
        border-radius: 5px; color: #8da29e; cursor: pointer; flex: 0 0 auto; font: 500 13px/1 sans-serif; }
      .close:hover { color: #fff; background: rgba(255, 255, 255, .12); }
      .notice { box-sizing: border-box; border: 1px solid rgba(255, 174, 87, .38); color: #ffd09b;
        border-radius: 8px; background: rgba(11, 18, 24, .92); padding: 7px 10px; font-size: 10px;
        backdrop-filter: blur(12px); -webkit-backdrop-filter: blur(12px); }
      .qualities { display: flex; flex-direction: column; gap: 4px; padding-left: 12px; }
      .quality { all: unset; box-sizing: border-box; padding: 5px 10px; border-radius: 7px;
        border: 1px solid rgba(96, 229, 184, .28); background: rgba(11, 18, 24, .9);
        color: #cfe9e2; font-size: 11px; cursor: pointer; text-align: left; }
      .quality:hover { border-color: #70f1c6; color: #fff; background: rgba(22, 46, 48, .96); }
      @media (prefers-reduced-motion: reduce) { * { scroll-behavior: auto !important; } }
    `;
    return style;
  }

  #selectedCandidates(): PublicCandidate[] {
    const active = [...this.#candidates.values()]
      .filter((candidate) => candidate.expiresAt > Date.now())
      .sort((left, right) => right.lastSeenAt - left.lastSeenAt);
    // Prefer a manifest (HLS/DASH) over a raw media segment so the whole
    // stream is handed to the app instead of a single fragmented fragment.
    const manifest = active.find((candidate) => candidate.kind === "hls" || candidate.kind === "dash");
    const audio = active.find((candidate) => candidate.kind === "audio");
    const video = manifest ?? active.find((candidate) => candidate.kind === "video");
    return [video, audio].filter((candidate): candidate is PublicCandidate => !!candidate);
  }

  #render(): void {
    const container = this.#shadow.querySelector("div");
    if (!container) {
      return;
    }
    container.replaceChildren();
    container.className = "panel";
    if (!this.#enabled) {
      this.#host.remove();
      return;
    }

    let renderedCandidates = 0;
    const selected = this.#selectedCandidates();
    const hasVideo = selected.some((candidate) => candidate.kind !== "audio");
    this.#audioFallbackId = null;
    for (const candidate of selected) {
      const isAudio = candidate.kind === "audio";
      // Social platforms serve fragmented tracks; the single page-capture row
      // (resolved through yt-dlp) is the only meaningful entry there.
      if (this.#pageActive) {
        break;
      }
      // When a video and its audio track are both observed, keep one compact
      // video row; the audio track becomes an option in the quality list.
      if (isAudio && hasVideo) {
        this.#audioFallbackId = candidate.id;
        continue;
      }
      const dismissedAt = this.#dismissedFor.get(candidate.id);
      if (dismissedAt !== undefined && Date.now() - dismissedAt < 30_000) {
        continue;
      }

      const row = document.createElement("div");
      row.className = "row";

      const mark = document.createElement("span");
      mark.className = "mark";
      mark.textContent = isAudio ? "♫" : "▶";

      const copy = document.createElement("div");
      copy.style.display = "grid";
      copy.style.gap = "1px";
      copy.style.minWidth = "0";
      const action = document.createElement("span");
      action.className = "action";
      action.textContent = isAudio ? message("downloadMusic", "Download music") : message("downloadVideo", "Download video");
      const meta = document.createElement("span");
      meta.className = "meta";
      meta.textContent = [candidate.quality, candidate.container?.toUpperCase()].filter(Boolean).join(" · ");
      copy.append(action, meta);

      const spacer = document.createElement("span");
      spacer.className = "spacer";

      const close = document.createElement("button");
      close.className = "close";
      close.type = "button";
      close.textContent = "✕";
      close.setAttribute("aria-label", message("close", "Close"));
      close.addEventListener("click", (event) => {
        event.stopPropagation();
        this.#dismissedFor.set(candidate.id, Date.now());
        this.#render();
      });

      row.append(mark, copy, spacer, close);
      row.addEventListener("click", () => void this.#start(row, candidate.id, isAudio));
      this.#attachDrag(row, container);
      container.append(row);
      renderedCandidates++;
    }

    // Social platforms (YouTube, Facebook, X, Instagram, ...) expose media
    // only through script-managed streams, so raw observed URLs are fragments.
    // Offer a single IDM-style page capture bar that the app resolves via
    // yt-dlp, with qualities (and an audio-only option) behind one click.
    if (this.#pageActive && Date.now() - this.#pageDismissedAt >= 30_000) {
      container.append(this.#buildPageRow(container));
    }

    if (this.#notice) {
      const notice = document.createElement("div");
      notice.className = "notice";
      notice.textContent = this.#notice;
      container.append(notice);
    }
    if (container.childElementCount > 0) {
      this.#mount();
      // Only re-anchor to the video when the user has not dragged the panel.
      if (this.#manualLeft === null && this.#manualTop === null) {
        this.#positionFromVideo();
      }
    } else {
      this.#host.remove();
    }
  }

  #attachDrag(row: HTMLElement, container: HTMLElement): void {
    row.addEventListener("pointerdown", (event) => {
      if ((event.target as HTMLElement | null)?.className === "close") {
        return;
      }
      event.preventDefault();
      row.setPointerCapture?.(event.pointerId);
      const startX = event.clientX;
      const startY = event.clientY;
      const hostLeft = this.#manualLeft ?? this.#host.offsetLeft;
      const hostTop = this.#manualTop ?? this.#host.offsetTop;
      let dragged = false;

      const onMove = (moveEvent: PointerEvent) => {
        const dx = moveEvent.clientX - startX;
        const dy = moveEvent.clientY - startY;
        if (!dragged && Math.abs(dx) < 4 && Math.abs(dy) < 4) {
          return;
        }
        dragged = true;
        row.classList.add("dragging");
        this.#manualLeft = hostLeft + dx;
        this.#manualTop = hostTop + dy;
        this.#host.style.left = `${Math.round(this.#manualLeft)}px`;
        this.#host.style.top = `${Math.round(this.#manualTop)}px`;
      };
      const onUp = () => {
        row.classList.remove("dragging");
        row.releasePointerCapture?.(event.pointerId);
        window.removeEventListener("pointermove", onMove);
        window.removeEventListener("pointerup", onUp);
        // A drag must not also fire the download click.
        if (dragged) {
          row.addEventListener("click", (e) => e.stopImmediatePropagation(), { once: true });
        }
      };
      window.addEventListener("pointermove", onMove);
      window.addEventListener("pointerup", onUp);
    });
  }

  async #start(row: HTMLElement, candidateId: string, isAudio: boolean): Promise<void> {
    row.style.opacity = "0.6";
    row.style.pointerEvents = "none";
    // Resolve the manifest first so the user can pick a quality instead of a
    // silently-chosen default stream.
    let result: RuntimeResponse;
    if (isAudio) {
      result = await sendMessage({ type: "candidate.start", candidateId });
    } else {
      const resolved = await sendMessage({ type: "candidate.resolve", candidateId });
      const qualities = Array.isArray(resolved.qualities) ? resolved.qualities : [];
      if (resolved.ok && qualities.length > 0) {
        row.style.opacity = "";
        row.style.pointerEvents = "";
        this.#showQualities(row, candidateId, qualities, this.#audioFallbackId);
        return;
      }
      result = await sendMessage({ type: "candidate.start", candidateId });
    }
    this.#notice = result.ok
      ? message("queued", "Sent to Correntra.")
      : result.error === "candidateExpired"
        ? message("candidateExpired", "This media address expired. Play it again.")
        : message("appUnavailable", "Correntra app is unavailable.");
    row.style.opacity = "";
    row.style.pointerEvents = "";
    this.#render();
    window.setTimeout(() => {
      this.#notice = "";
      this.#render();
    }, 3_500);
  }

  #showQualities(
    row: HTMLElement,
    candidateId: string,
    qualities: Array<{ id: string; displayName: string }>,
    audioFallbackId: string | null = null
  ): void {
    const container = this.#shadow.querySelector("div.panel");
    if (!container) {
      return;
    }
    // Insert a quality list right after the clicked row.
    const list = document.createElement("div");
    list.className = "qualities";
    list.setAttribute("role", "listbox");
    for (const quality of qualities) {
      const option = document.createElement("button");
      option.type = "button";
      option.className = "quality";
      option.textContent = quality.displayName;
      option.setAttribute("role", "option");
      option.addEventListener("click", () => {
        list.remove();
        void this.#startChosen(option, candidateId);
      });
      list.append(option);
    }
    if (audioFallbackId) {
      const audioOption = document.createElement("button");
      audioOption.type = "button";
      audioOption.className = "quality";
      audioOption.textContent = "♪ " + message("downloadMusic", "Download music");
      audioOption.setAttribute("role", "option");
      audioOption.addEventListener("click", () => {
        list.remove();
        void this.#startChosen(audioOption, audioFallbackId);
      });
      list.append(audioOption);
    }
    row.after(list);
  }

  async #startChosen(button: HTMLElement, candidateId: string): Promise<void> {
    button.style.opacity = "0.6";
    const result = await sendMessage({ type: "candidate.start", candidateId });
    this.#notice = result.ok
      ? message("queued", "Sent to Correntra.")
      : result.error === "candidateExpired"
        ? message("candidateExpired", "This media address expired. Play it again.")
        : message("appUnavailable", "Correntra app is unavailable.");
    this.#render();
    window.setTimeout(() => {
      this.#notice = "";
      this.#render();
    }, 3_500);
  }

  #buildPageRow(container: HTMLElement): HTMLDivElement {
    const row = document.createElement("div");
    row.className = "row";

    const mark = document.createElement("span");
    mark.className = "mark";
    mark.textContent = "▶";

    const copy = document.createElement("div");
    copy.style.display = "grid";
    copy.style.gap = "1px";
    copy.style.minWidth = "0";
    const action = document.createElement("span");
    action.className = "action";
    action.textContent = message("downloadVideo", "Download video");
    const meta = document.createElement("span");
    meta.className = "meta";
    meta.textContent = location.hostname;
    copy.append(action, meta);

    const spacer = document.createElement("span");
    spacer.className = "spacer";

    const close = document.createElement("button");
    close.className = "close";
    close.type = "button";
    close.textContent = "✕";
    close.setAttribute("aria-label", message("close", "Close"));
    close.addEventListener("click", (event) => {
      event.stopPropagation();
      this.#pageDismissedAt = Date.now();
      this.#render();
    });

    row.append(mark, copy, spacer, close);
    row.addEventListener("click", () => void this.#startPage(row));
    this.#attachDrag(row, container);
    return row;
  }

  async #startPage(row: HTMLElement): Promise<void> {
    row.style.opacity = "0.6";
    row.style.pointerEvents = "none";
    const pageUrl = location.href;
    const title = document.title.slice(0, 140);
    // Ask the app to enumerate qualities first (yt-dlp), exactly like IDM
    // does, instead of silently grabbing whatever default is available.
    const resolved = await sendMessage({ type: "page.resolve", pageUrl, title });
    const qualities = Array.isArray(resolved.qualities) ? resolved.qualities : [];
    if (resolved.ok && qualities.length > 0) {
      row.style.opacity = "";
      row.style.pointerEvents = "";
      this.#showPageQualities(row, qualities as Array<{ id: string; displayName: string }>);
      return;
    }

    const result = await sendMessage({ type: "page.start", pageUrl, title });
    this.#announce(result);
  }

  #showPageQualities(row: HTMLElement, qualities: Array<{ id: string; displayName: string }>): void {
    const container = this.#shadow.querySelector("div.panel");
    if (!container) {
      return;
    }
    const list = document.createElement("div");
    list.className = "qualities";
    list.setAttribute("role", "listbox");
    for (const quality of qualities) {
      const option = document.createElement("button");
      option.type = "button";
      option.className = "quality";
      option.textContent = quality.displayName;
      option.setAttribute("role", "option");
      option.addEventListener("click", async () => {
        list.remove();
        option.style.opacity = "0.6";
        const result = await sendMessage({
          type: "page.start",
          pageUrl: location.href,
          title: document.title.slice(0, 140),
          formatId: quality.id
        });
        this.#announce(result);
      });
      list.append(option);
    }
    if (!qualities.some((quality) => quality.id.includes("bestaudio"))) {
      const audioOption = document.createElement("button");
      audioOption.type = "button";
      audioOption.className = "quality";
      audioOption.textContent = "♪ " + message("downloadMusic", "Download music");
      audioOption.setAttribute("role", "option");
      audioOption.addEventListener("click", async () => {
        list.remove();
        audioOption.style.opacity = "0.6";
        const result = await sendMessage({
          type: "page.start",
          pageUrl: location.href,
          title: document.title.slice(0, 140),
          formatId: "bestaudio/best"
        });
        this.#announce(result);
      });
      list.append(audioOption);
    }
    row.after(list);
  }

  #announce(result: RuntimeResponse): void {
    this.#notice = result.ok
      ? message("queued", "Sent to Correntra.")
      : result.error === "candidateExpired"
        ? message("candidateExpired", "This media address expired. Play it again.")
        : message("appUnavailable", "Correntra app is unavailable.");
    this.#render();
    window.setTimeout(() => {
      this.#notice = "";
      this.#render();
    }, 3_500);
  }
}

function initialize(): void {
  const overlay = new MediaOverlay();
  const recentlySent = new Map<string, number>();
  let enabled = false;
  let observer: MutationObserver | undefined;

  const reportElement = (element: HTMLMediaElement): void => {
    if (!enabled) {
      return;
    }
    if (element instanceof HTMLVideoElement) {
      overlay.setVideoElement(element);
    }
    const urls = new Set<string>();
    if (element.currentSrc) urls.add(element.currentSrc);
    if (element.src) urls.add(element.src);
    for (const source of element.querySelectorAll<HTMLSourceElement>("source[src]")) {
      if (source.src) urls.add(source.src);
    }
    for (const url of urls) {
      if (!/^https?:\/\//i.test(url)) {
        continue;
      }
      const previous = recentlySent.get(url) ?? 0;
      if (Date.now() - previous < 8_000) {
        continue;
      }
      recentlySent.set(url, Date.now());
      if (recentlySent.size > 100) {
        recentlySent.clear();
      }
      void sendMessage({
        type: "dom.candidate",
        url,
        elementKind: element instanceof HTMLAudioElement ? "audio" : "video",
        videoHeight: element instanceof HTMLVideoElement ? element.videoHeight : undefined
      }).then((response) => {
        if (response.ok && validCandidate(response.candidate)) {
          overlay.add(response.candidate);
        }
      });
    }
  };

  const scan = (): void => {
    document.querySelectorAll<HTMLMediaElement>("video, audio").forEach(reportElement);
  };

  const start = (): void => {
    if (enabled) return;
    enabled = true;
    overlay.setEnabled(true);
    overlay.setPageCapture(isSocialVideoHost(location.href));
    scan();
    observer = new MutationObserver(scan);
    observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ["src"] });
  };

  const stop = (): void => {
    enabled = false;
    observer?.disconnect();
    observer = undefined;
    overlay.setEnabled(false);
  };

  document.addEventListener("loadedmetadata", (event) => {
    if (event.target instanceof HTMLMediaElement) reportElement(event.target);
  }, true);
  document.addEventListener("play", (event) => {
    if (event.target instanceof HTMLMediaElement) reportElement(event.target);
  }, true);
  document.addEventListener("encrypted", () => overlay.showProtected(), true);

  chrome.runtime.onMessage.addListener((raw: unknown) => {
    if (typeof raw !== "object" || raw === null || Array.isArray(raw)) return;
    const request = raw as Record<string, unknown>;
    if (request.type === "integration.state") {
      request.enabled === true ? start() : stop();
    } else if (request.type === "candidate.detected" && validCandidate(request.candidate)) {
      overlay.add(request.candidate);
    }
  });

  void sendMessage({ type: "content.ready" }).then((response) => {
    if (response.enabled === true) {
      start();
      if (Array.isArray(response.candidates)) {
        response.candidates.filter(validCandidate).forEach((candidate) => overlay.add(candidate));
      }
    }
  });
}

if (!globalMarker.__correntraContentLoaded) {
  globalMarker.__correntraContentLoaded = true;
  initialize();
}
