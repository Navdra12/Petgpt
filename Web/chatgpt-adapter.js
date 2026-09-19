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
    compactNavigation: false
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
    controlRoot: null
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

  const degrade = () => {
    if (state.degraded) return;
    state.degraded = true;
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
    state.page = document.querySelector("main");
    state.composer = document.querySelector("#prompt-textarea[role=\"textbox\"]");
    state.controlRoot = state.composer?.closest("form") || state.composer?.parentElement || null;

    // The page marker is structural only. Its color pair stays unavailable
    // because descendants can own colors that cannot be safely overridden.
    state.page?.setAttribute("data-petgpt-surface", "page");
    // Both composer colors target the same verified editable element.
    state.composer?.setAttribute("data-petgpt-surface", "composer");
    attachControlObserver();

    postCapabilities({
      generation: Boolean(state.controlRoot),
      composerEmpty: false,
      pageSurface: false,
      secondarySurface: false,
      composerSurface: Boolean(state.composer),
      scrollbar: Boolean(state.page),
      compactNavigation: false
    });
    observeGeneration();
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
