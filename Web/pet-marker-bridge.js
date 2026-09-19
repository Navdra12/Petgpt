(() => {
  "use strict";

  if (window.top !== window || location.origin !== "https://chatgpt.com") return;
  if (window.__petgptMarkerBridgeV1?.reconfigure) return;

  const RESERVED_PREFIX = "https://petgpt.invalid/#r1/";
  const MARKER_PATTERN = /^https:\/\/petgpt\.invalid\/#r1\/[0-9a-f]{16}\/[a-z][a-z0-9_]{0,31}\/[a-z][a-z0-9_]{0,31}(?:\/(?:0|[1-9][0-9]{0,2}))?\/end$/;
  const STABILIZE_MS = 150;
  const MAX_BATCH = 200;
  const MAX_QUEUE = 1000;
  const state = {
    active: true,
    configured: false,
    enabled: false,
    showControlMarkers: false,
    documentSession: null,
    routeRevision: 0,
    routeKind: null,
    generationSerial: 0,
    generationState: "unknown",
    observer: null,
    queue: new Set(),
    scheduled: false,
    pending: new Map(),
    baselineIds: new Set(),
    baselineAnchors: new WeakSet(),
    liveAssistantIds: new Set(),
    ownerlessCandidates: new WeakSet(),
    lastDispatchedTurnKey: null,
    degraded: false,
    maximumBatchObserved: 0
  };

  const isCanonicalMarker = href =>
    typeof href === "string" && href.length <= 192 &&
    /^[\x00-\x7f]*$/.test(href) && MARKER_PATTERN.test(href) &&
    (!href.includes("/") || (() => {
      const part = href.split("/").at(-2);
      return part === "end" || !/^\d+$/.test(part) || Number(part) <= 100;
    })());

  const markerHref = anchor => anchor?.getAttribute?.("href");

  const applyMarkerVisibility = anchor => {
    const href = markerHref(anchor);
    if (!isCanonicalMarker(href)) {
      anchor?.removeAttribute?.("data-petgpt-control-marker");
      return;
    }
    anchor.setAttribute("data-petgpt-control-marker", state.showControlMarkers ? "visible" : "hidden");
  };

  const ensureStyle = () => {
    let style = document.getElementById("petgpt-marker-style");
    if (!style) {
      style = document.createElement("style");
      style.id = "petgpt-marker-style";
      style.append(document.createTextNode('a[data-petgpt-control-marker="hidden"]{display:none!important}'));
      document.head?.append(style);
    }
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

  const cancelPending = () => {
    for (const pending of state.pending.values()) clearTimeout(pending.timer);
    state.pending.clear();
  };

  const disconnectObserver = () => {
    state.observer?.disconnect();
    state.observer = null;
    state.queue.clear();
    state.scheduled = false;
    cancelPending();
  };

  const degrade = () => {
    if (state.degraded) return;
    state.degraded = true;
    disconnectObserver();
    post("activity", { event: "reactionCapability", available: false });
  };

  const structuralOwner = anchor => {
    if (!anchor?.isConnected || anchor.ownerDocument !== document) return null;
    if (anchor.closest("code,pre,blockquote,iframe")) return null;
    const nearestRole = anchor.closest("[data-message-author-role]");
    if (!nearestRole || nearestRole.getAttribute("data-message-author-role") !== "assistant") return null;
    if (nearestRole.closest('[aria-hidden="true"],[hidden],[inert],[data-branch-state="inactive"],[data-active="false"]')) return null;
    const id = nearestRole.getAttribute("data-message-id");
    if (!id || id.length > 128 || !/^[\x21-\x7e]+$/.test(id)) return null;
    return { element: nearestRole, id };
  };

  const currentAssistantOwner = () => {
    const ids = new Set();
    let last = null;
    let count = 0;
    walkElements(document.documentElement, owner => {
      if (!owner.matches?.('[data-message-author-role="assistant"][data-message-id]')) return true;
      if (owner.closest('[aria-hidden="true"],[hidden],[inert],[data-branch-state="inactive"],[data-active="false"]')) return true;
      const id = owner.getAttribute("data-message-id");
      if (!id || id.length > 128 || !/^[\x21-\x7e]+$/.test(id) || ids.has(id)) {
        last = null;
        return false;
      }
      ids.add(id);
      last = owner;
      count += 1;
      if (count > MAX_QUEUE) {
        degrade();
        last = null;
        return false;
      }
      return true;
    });
    return last;
  };

  const stabilize = anchor => {
    if (!state.active || !state.enabled || state.degraded || !anchor?.isConnected) return;
    const href = markerHref(anchor);
    applyMarkerVisibility(anchor);
    if (!isCanonicalMarker(href) || state.baselineAnchors.has(anchor)) return;
    const firstOwner = structuralOwner(anchor);
    if (state.generationSerial <= 0 && firstOwner) {
      state.baselineIds.add(firstOwner.id);
      state.baselineAnchors.add(anchor);
      return;
    }
    if (!firstOwner) state.ownerlessCandidates.add(anchor);

    const previous = state.pending.get(anchor);
    if (previous) clearTimeout(previous.timer);
    const identity = {
      href,
      documentSession: state.documentSession,
      routeRevision: state.routeRevision,
      generationSerial: state.generationSerial
    };
    const timer = setTimeout(() => {
      state.pending.delete(anchor);
      if (!state.active || !state.enabled || state.degraded || !anchor.isConnected ||
          markerHref(anchor) !== identity.href ||
          state.documentSession !== identity.documentSession ||
          state.routeRevision !== identity.routeRevision ||
          state.generationSerial !== identity.generationSerial ||
          identity.generationSerial <= 0) return;
      const owner = structuralOwner(anchor);
      if (owner && state.ownerlessCandidates.has(anchor)) state.liveAssistantIds.add(owner.id);
      if (!owner || !state.liveAssistantIds.has(owner.id) ||
          owner.element !== currentAssistantOwner() || state.baselineIds.has(owner.id)) return;
      const turnKey = `${identity.documentSession}/${identity.routeRevision}/${identity.generationSerial}`;
      if (state.lastDispatchedTurnKey === turnKey) return;
      state.lastDispatchedTurnKey = turnKey;
      post("reaction", {
        href: identity.href,
        generationSerial: identity.generationSerial,
        assistantId: owner.id
      });
    }, STABILIZE_MS);
    state.pending.set(anchor, { href, timer });
  };

  const processQueue = () => {
    state.scheduled = false;
    if (!state.active || !state.enabled || state.degraded) return;
    const batch = Array.from(state.queue).slice(0, MAX_BATCH);
    for (const anchor of batch) state.queue.delete(anchor);
    state.maximumBatchObserved = Math.max(state.maximumBatchObserved, batch.length);
    for (const anchor of batch) stabilize(anchor);
    if (state.queue.size) scheduleProcessing();
  };

  const scheduleProcessing = () => {
    if (state.scheduled || state.degraded) return;
    state.scheduled = true;
    queueMicrotask(processQueue);
  };

  const enqueueAnchor = anchor => {
    if (state.degraded) return;
    if (!anchor?.matches?.(`a[href^="${RESERVED_PREFIX}"]`)) return;
    applyMarkerVisibility(anchor);
    state.queue.add(anchor);
    if (state.queue.size > MAX_QUEUE) {
      degrade();
      return;
    }
    scheduleProcessing();
  };

  const walkElements = (root, visit) => {
    const stack = [];
    if (root instanceof Element) stack.push(root);
    else if (root?.children) {
      for (let index = root.children.length - 1; index >= 0; index -= 1) stack.push(root.children[index]);
    }
    while (stack.length && !state.degraded) {
      const element = stack.pop();
      if (visit(element) === false) return;
      for (let index = element.children.length - 1; index >= 0; index -= 1) stack.push(element.children[index]);
    }
  };

  const discover = node => {
    if (state.degraded) return;
    walkElements(node, element => {
      enqueueAnchor(element);
      return !state.degraded;
    });
  };

  const noteAssistantOwners = (node, historical) => {
    walkElements(node, owner => {
      if (!owner.matches?.('[data-message-author-role="assistant"][data-message-id]')) return true;
      const id = owner.getAttribute("data-message-id");
      if (!id || id.length > 128 || !/^[\x21-\x7e]+$/.test(id)) return true;
      if (historical || state.generationSerial <= 0) {
        state.baselineIds.add(id);
      } else if (!owner.querySelector?.(`a[href^="${RESERVED_PREFIX}"]`)) {
        state.liveAssistantIds.add(id);
      }
      return true;
    });
  };

  const onMutations = records => {
    if (!state.active || !state.enabled || state.degraded) return;
    for (const record of records) {
      if (record.type === "attributes") discover(record.target);
      const replacement = (record.removedNodes?.length || 0) > 0;
      for (const node of record.addedNodes || []) {
        noteAssistantOwners(node, replacement);
        discover(node);
        if (state.degraded) return;
      }
      if (state.degraded) return;
    }
  };

  const baselineCurrentStructure = () => {
    let structuralCount = 0;
    walkElements(document.documentElement, element => {
      if (element.matches?.('[data-message-author-role="assistant"][data-message-id]')) {
        const id = element.getAttribute("data-message-id");
        if (id && id.length <= 128 && /^[\x21-\x7e]+$/.test(id)) state.baselineIds.add(id);
        structuralCount += 1;
      }
      if (element.matches?.(`a[href^="${RESERVED_PREFIX}"]`)) {
        applyMarkerVisibility(element);
        if (isCanonicalMarker(markerHref(element))) state.baselineAnchors.add(element);
        structuralCount += 1;
      }
      if (structuralCount > MAX_QUEUE) {
        degrade();
        return false;
      }
      return true;
    });
  };

  const attachObserver = () => {
    disconnectObserver();
    if (!state.active || !state.enabled || state.degraded) return;
    ensureStyle();
    state.observer = new MutationObserver(onMutations);
    state.observer.observe(document.documentElement, {
      childList: true,
      subtree: true,
      attributes: true,
      attributeFilter: ["href", "data-message-author-role", "data-message-id", "aria-hidden", "hidden", "inert", "data-branch-state", "data-active"]
    });
  };

  const reconfigure = configuration => {
    if (!state.active || !configuration ||
        !/^[0-9a-f]{16}$/.test(configuration.documentSession) ||
        !Number.isSafeInteger(configuration.routeRevision) || configuration.routeRevision < 0) return;
    const newDocument = configuration.documentSession !== state.documentSession;
    const changed = newDocument ||
      configuration.routeRevision !== state.routeRevision;
    const preserveLiveTurn = changed &&
      configuration.documentSession === state.documentSession &&
      configuration.routeRevision === state.routeRevision + 1 &&
      (configuration.preserveLiveTurn === true ||
        state.routeKind === "ProjectLanding" && configuration.routeKind === "ProjectConversation");

    if (changed) {
      disconnectObserver();
      state.queue.clear();
      state.baselineIds.clear();
      state.baselineAnchors = new WeakSet();
      state.liveAssistantIds.clear();
      state.ownerlessCandidates = new WeakSet();
      state.lastDispatchedTurnKey = null;
      if (newDocument) {
        state.degraded = false;
        state.maximumBatchObserved = 0;
      }
      if (!preserveLiveTurn) {
        state.generationSerial = 0;
        state.generationState = "unknown";
      }
    }

    state.configured = true;
    state.documentSession = configuration.documentSession;
    state.routeRevision = configuration.routeRevision;
    state.routeKind = configuration.routeKind || null;
    state.enabled = configuration.enabled === true;
    state.showControlMarkers = configuration.showControlMarkers === true;
    baselineCurrentStructure();
    if (state.enabled && !state.degraded) {
      attachObserver();
      post("activity", { event: "reactionCapability", available: true });
    } else {
      disconnectObserver();
      post("activity", { event: "reactionCapability", available: false });
    }
  };

  const observeTurn = generationSerial => {
    if (!state.active || !state.configured || !Number.isSafeInteger(generationSerial) || generationSerial <= 0) return;
    state.generationSerial = generationSerial;
    state.generationState = "unknown";
    state.liveAssistantIds.clear();
    state.ownerlessCandidates = new WeakSet();
    state.lastDispatchedTurnKey = null;
    baselineCurrentStructure();
  };

  const observeGeneration = (generationState, generationSerial) => {
    if (!state.active || !state.configured ||
        !["unknown", "generating", "idle"].includes(generationState) ||
        !Number.isSafeInteger(generationSerial) || generationSerial <= 0 ||
        generationSerial !== state.generationSerial) return;
    state.generationState = generationState;
  };

  const onHostMessage = event => {
    const message = event.data;
    if (message?.v !== 1) return;
    if (message.op === "teardown") {
      teardown();
      return;
    }
    if (message.op !== "configure") return;
    reconfigure({
      documentSession: message.documentSession,
      routeRevision: message.routeRevision,
      routeKind: message.routeKind,
      enabled: message.reaction?.enabled === true,
      showControlMarkers: message.reaction?.showControlMarkers === true
    });
  };

  const teardown = () => {
    if (!state.active) return;
    state.active = false;
    state.enabled = false;
    disconnectObserver();
    document.getElementById("petgpt-marker-style")?.remove();
    for (const anchor of document.querySelectorAll("[data-petgpt-control-marker]"))
      anchor.removeAttribute("data-petgpt-control-marker");
    window.chrome?.webview?.removeEventListener?.("message", onHostMessage);
    delete window.__petgptMarkerBridgeV1;
  };

  window.__petgptMarkerBridgeV1 = {
    reconfigure,
    observeTurn,
    observeGeneration,
    teardown,
    getDiagnostics: () => ({
      degraded: state.degraded,
      queuedCandidates: state.queue.size,
      maximumBatchObserved: state.maximumBatchObserved
    })
  };
  window.chrome?.webview?.addEventListener?.("message", onHostMessage);
})();
