(() => {
  "use strict";

  if (window.top !== window || location.origin !== "https://chatgpt.com") return;

  const existing = window.__petgptAdapterV1;
  if (existing && typeof existing.reconfigure === "function") return;

  const MAX_BATCH = 200;
  const unavailableCapabilities = () => ({
    generation: false,
    composerEmpty: false,
    pageSurface: false,
    secondarySurface: false,
    composerSurface: false,
    scrollbar: false,
    compactNavigation: false,
    submission: false
  });
  const state = {
    active: true,
    configured: false,
    documentSession: null,
    routeRevision: 0,
    generationWasSeen: false,
    lastGeneration: null,
    lastCapabilitiesKey: null,
    styleText: new Map(),
    rootObserver: null,
    controlObserver: null,
    rootRefreshScheduled: false,
    queuedNodes: 0,
    degraded: false,
    page: null,
    composer: null,
    controlRoot: null,
    allowSafeComposerEmpty: false,
    composerEmptySupported: false,
    generationSerial: 0,
    submissionQueued: false,
    listenersAttached: false
  };

  const post = (kind, payload) => {
    if (!state.active || !state.configured) return;
    window.chrome?.webview?.postMessage({
      v: 1,
      kind,
      documentSession: state.documentSession,
      routeRevision: state.routeRevision,
      payload
    });
  };

  const setStyle = (id, css) => {
    let style = document.getElementById(id);
    if (!css) {
      style?.remove();
      state.styleText.delete(id);
      return;
    }
    if (!style) {
      style = document.createElement("style");
      style.id = id;
      document.head?.appendChild(style);
    }
    if (style && state.styleText.get(id) !== css) {
      style.replaceChildren(document.createTextNode(css));
      state.styleText.set(id, css);
    }
  };

  const clearOwnedAttributes = () => {
    state.page?.removeAttribute("data-petgpt-surface");
    state.composer?.removeAttribute("data-petgpt-surface");
    state.page = null;
    state.composer = null;
    state.controlRoot = null;
    state.composerEmptySupported = false;
    document.documentElement.removeAttribute("data-petgpt-compact");
    document.documentElement.removeAttribute("data-petgpt-history");
  };

  const postCapabilities = (capabilities) => {
    const key = JSON.stringify(capabilities);
    if (key === state.lastCapabilitiesKey) return;
    state.lastCapabilitiesKey = key;
    post("ready", { adapterVersion: "1", capabilities });
  };

  const observeGeneration = () => {
    if (state.degraded || !state.controlRoot) return;
    const stop = state.controlRoot.querySelector("[data-testid=\"stop-button\"]");
    let next = "unknown";
    if (stop) {
      state.generationWasSeen = true;
      next = "generating";
    } else if (state.generationWasSeen) {
      next = "idle";
    }
    if (next !== state.lastGeneration) {
      state.lastGeneration = next;
      post("activity", { event: "generation", state: next });
    }
  };

  const isSafeEmptyComposer = (composer) => {
    const tagName = composer?.tagName?.toLowerCase?.();
    return (tagName === "textarea" || tagName === "input") &&
      composer.hasAttribute?.("placeholder") &&
      typeof composer.matches === "function";
  };

  const observeComposerEmpty = () => {
    if (!state.composerEmptySupported || !state.composer) return;
    post("activity", {
      event: "composer",
      empty: state.composer.matches(":placeholder-shown")
    });
  };

  const emitSubmission = (eventName) => {
    if (!state.active || !state.configured || state.degraded) return;
    state.generationSerial += 1;
    post("activity", { event: eventName, generationSerial: state.generationSerial });
  };

  const queueSubmit = () => {
    if (state.submissionQueued) return;
    state.submissionQueued = true;
    queueMicrotask(() => {
      state.submissionQueued = false;
      emitSubmission("submit");
    });
  };

  const onNativeSubmit = () => queueSubmit();
  const onControlClick = (event) => {
    const send = event.target?.closest?.("[data-testid=\"send-button\"]");
    if (send &&
        !state.controlRoot?.querySelector("[data-testid=\"stop-button\"]") &&
        !send.hasAttribute?.("disabled") &&
        send.getAttribute?.("aria-disabled") !== "true") {
      queueSubmit();
    }
  };
  const onComposerInput = () => observeComposerEmpty();
  const onDocumentClick = (event) => {
    if (event.target?.closest?.("[data-testid=\"regenerate-button\"]")) emitSubmission("regenerate");
  };

  const detachActivityListeners = () => {
    state.controlRoot?.removeEventListener?.("submit", onNativeSubmit, true);
    state.controlRoot?.removeEventListener?.("click", onControlClick, true);
    state.composer?.removeEventListener?.("input", onComposerInput, true);
  };

  const attachActivityListeners = () => {
    detachActivityListeners();
    state.controlRoot?.addEventListener?.("submit", onNativeSubmit, true);
    state.controlRoot?.addEventListener?.("click", onControlClick, true);
    state.composer?.addEventListener?.("input", onComposerInput, true);
    if (!state.listenersAttached) {
      document.addEventListener?.("click", onDocumentClick, true);
      state.listenersAttached = true;
    }
  };

  const degrade = () => {
    if (state.degraded) return;
    state.degraded = true;
    state.composerEmptySupported = false;
    state.rootObserver?.disconnect();
    state.controlObserver?.disconnect();
    state.queuedNodes = 0;
    state.rootRefreshScheduled = false;
    postCapabilities(unavailableCapabilities());
  };

  const countAddedNodes = (records) => {
    for (const record of records) {
      state.queuedNodes += record.addedNodes?.length || 0;
      if (state.queuedNodes > MAX_BATCH) {
        degrade();
        return false;
      }
    }
    return true;
  };

  const onControlMutations = (records) => {
    if (!state.active || !state.configured || state.degraded) return;
    state.queuedNodes = 0;
    if (!countAddedNodes(records)) return;
    state.queuedNodes = 0;
    observeGeneration();
  };

  const attachControlObserver = () => {
    state.controlObserver?.disconnect();
    state.controlObserver = null;
    if (!state.controlRoot || state.degraded) return;
    state.controlObserver = new MutationObserver(onControlMutations);
    state.controlObserver.observe(state.controlRoot, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["data-testid", "aria-label"]
    });
  };

  const refreshLandmarks = () => {
    state.rootRefreshScheduled = false;
    state.queuedNodes = 0;
    if (!state.active || !state.configured || state.degraded) return;

    state.page?.removeAttribute("data-petgpt-surface");
    state.composer?.removeAttribute("data-petgpt-surface");
    detachActivityListeners();
    state.page = document.querySelector("main");
    state.composer = document.querySelector("#prompt-textarea[role=\"textbox\"]");
    state.controlRoot = state.composer?.closest("form") || state.composer?.parentElement || null;
    state.composerEmptySupported = state.allowSafeComposerEmpty && isSafeEmptyComposer(state.composer);

    // The page marker is structural only. Its color pair stays unavailable
    // because descendants can own colors that cannot be safely overridden.
    state.page?.setAttribute("data-petgpt-surface", "page");
    // Both composer colors target the same verified editable element.
    state.composer?.setAttribute("data-petgpt-surface", "composer");
    attachControlObserver();
    attachActivityListeners();

    postCapabilities({
      generation: Boolean(state.controlRoot),
      composerEmpty: state.composerEmptySupported,
      pageSurface: false,
      secondarySurface: false,
      composerSurface: Boolean(state.composer),
      scrollbar: Boolean(state.page),
      compactNavigation: false,
      submission: Boolean(state.controlRoot)
    });
    observeGeneration();
    observeComposerEmpty();
  };

  const scheduleRootRefresh = (records) => {
    if (!state.active || !state.configured || state.degraded) return;
    if (!countAddedNodes(records)) return;
    if (!state.rootRefreshScheduled) {
      state.rootRefreshScheduled = true;
      queueMicrotask(refreshLandmarks);
    }
  };

  const ensureRootObserver = () => {
    if (state.rootObserver || state.degraded) return;
    state.rootObserver = new MutationObserver(scheduleRootRefresh);
    // Observe only top-level replacement. Ordinary conversation mutations are
    // handled by the narrowly scoped control observer above.
    state.rootObserver.observe(document.documentElement, { childList: true });
    if (document.body) state.rootObserver.observe(document.body, { childList: true });
  };

  const resetForIdentity = () => {
    state.generationWasSeen = false;
    state.lastGeneration = null;
    state.lastCapabilitiesKey = null;
    state.degraded = false;
    state.queuedNodes = 0;
    state.rootRefreshScheduled = false;
    state.rootObserver?.disconnect();
    state.controlObserver?.disconnect();
    state.rootObserver = null;
    state.controlObserver = null;
    state.generationSerial = 0;
    state.submissionQueued = false;
  };

  const postStageResult = (requestId, result) => {
    post("activity", { event: "stageResult", requestId, result });
  };

  const stagePersona = (message) => {
    const keys = Object.keys(message).sort();
    const expected = ["documentSession", "op", "requestId", "routeRevision", "text", "v"].sort();
    if (keys.length !== expected.length || keys.some((key, index) => key !== expected[index])) return;
    if (message.v !== 1 || message.op !== "stagePersona") return;
    if (!/^[0-9a-f]{16}$/.test(message.documentSession) ||
        !/^[0-9a-f]{16}$/.test(message.requestId) ||
        !Number.isSafeInteger(message.routeRevision) || message.routeRevision < 0 ||
        typeof message.text !== "string" || new TextEncoder().encode(message.text).length > 65536) return;
    if (message.documentSession !== state.documentSession || message.routeRevision !== state.routeRevision) return;
    const composer = state.composer;
    if (!state.active || !state.configured || state.degraded ||
        !state.composerEmptySupported || !composer) {
      postStageResult(message.requestId, "unsupported");
      return;
    }
    if (state.controlRoot?.querySelector("[data-testid=\"stop-button\"]")) {
      postStageResult(message.requestId, "generating");
      return;
    }
    if (!composer.matches(":placeholder-shown")) {
      postStageResult(message.requestId, "composerNotEmpty");
      return;
    }

    if (state.degraded || !state.composerEmptySupported || state.composer !== composer ||
        !composer.matches(":placeholder-shown")) {
      postStageResult(message.requestId, "unsupported");
      return;
    }
    composer.value = message.text;
    if (composer.value !== message.text) {
      postStageResult(message.requestId, "unsupported");
      return;
    }
    postStageResult(message.requestId, "staged");
    composer.dispatchEvent(new Event("input", { bubbles: true }));
  };

  const applyConfiguration = (message) => {
    if (!message || message.v !== 1 || message.op !== "configure") return;
    if (!/^[0-9a-f]{16}$/.test(message.documentSession)) return;
    if (!Number.isSafeInteger(message.routeRevision) || message.routeRevision < 0) return;

    const identityChanged =
      message.documentSession !== state.documentSession ||
      message.routeRevision !== state.routeRevision;
    if (identityChanged) resetForIdentity();

    state.configured = true;
    state.documentSession = message.documentSession;
    state.routeRevision = message.routeRevision;
    state.allowSafeComposerEmpty = message.composer?.allowSafeEmpty === true;
    document.documentElement.setAttribute("data-petgpt-compact", message.compact?.enabled ? "true" : "false");
    document.documentElement.setAttribute("data-petgpt-history", message.historyMode ? "true" : "false");
    setStyle("petgpt-compact-style", message.compact?.enabled ? message.compact.css : null);
    setStyle("petgpt-theme-style", message.theme?.enabled ? message.theme.css : null);

    // An overflow is sticky for its identity. A later configuration may update
    // style text, but cannot silently re-enable selectors for that route.
    if (state.degraded) return;
    ensureRootObserver();
    refreshLandmarks();
  };

  const teardown = () => {
    if (!state.active) return;
    state.active = false;
    state.configured = false;
    state.rootObserver?.disconnect();
    state.controlObserver?.disconnect();
    state.rootObserver = null;
    state.controlObserver = null;
    detachActivityListeners();
    if (state.listenersAttached) document.removeEventListener?.("click", onDocumentClick, true);
    state.listenersAttached = false;
    setStyle("petgpt-compact-style", null);
    setStyle("petgpt-theme-style", null);
    clearOwnedAttributes();
    window.chrome?.webview?.removeEventListener("message", onHostMessage);
    window.removeEventListener("popstate", onPopState);
    delete window.__petgptAdapterV1;
  };

  const onHostMessage = (event) => {
    const message = event.data;
    if (message?.v === 1 && message?.op === "teardown") {
      teardown();
      return;
    }
    if (message?.op === "stagePersona") {
      stagePersona(message);
      return;
    }
    applyConfiguration(message);
  };

  const onPopState = () => scheduleRootRefresh([]);

  window.__petgptAdapterV1 = {
    reconfigure: applyConfiguration,
    teardown
  };
  window.chrome?.webview?.addEventListener("message", onHostMessage);
  window.addEventListener("popstate", onPopState);
})();
