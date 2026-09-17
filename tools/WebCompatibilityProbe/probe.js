(() => {
  "use strict";

  const globalName = "__petgptT1Probe";
  if (window[globalName]?.version === 1) {
    console.info("PetGPT T1 structural probe is already installed.");
    return;
  }

  if (window.top !== window || location.origin !== "https://chatgpt.com") {
    throw new Error("Run the PetGPT T1 probe only in the top-level https://chatgpt.com page.");
  }

  const exactMarker =
    "https://petgpt.invalid/#r1/6c73a04a8842e90b/trixie/smug/65/end";
  const markerPrefix = "https://petgpt.invalid/#r1/";
  const maxEvents = 500;
  const maxSnapshots = 30;
  const events = [];
  const snapshots = [];
  const baselineAssistants = new WeakSet();
  let stopped = false;
  let truncated = false;
  let lastRouteShape = null;
  let lastGenerationSignature = null;

  const bounded = (value, maximum = 128) =>
    typeof value === "string" ? value.slice(0, maximum) : null;

  const routeShape = () => {
    const url = new URL(location.href);
    const stableSegments = new Set([
      "c",
      "g",
      "project",
      "projects",
      "chat",
      "chats",
      "new",
    ]);
    const segments = url.pathname.split("/").map((segment) => {
      if (!segment || stableSegments.has(segment.toLowerCase())) return segment;
      if (/^[a-z]{2}(?:-[A-Z]{2})?$/.test(segment)) return segment;
      return ":opaque";
    });
    return `${url.origin}${segments.join("/")}`;
  };

  const visible = (element) => {
    if (!(element instanceof Element)) return false;
    const style = getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return (
      style.display !== "none" &&
      style.visibility !== "hidden" &&
      rect.width > 0 &&
      rect.height > 0
    );
  };

  const opaqueId = (element) => {
    if (!(element instanceof Element)) return null;
    for (const name of [
      "data-message-id",
      "data-turn-id",
      "data-branch-id",
      "data-testid",
      "id",
    ]) {
      const value = element.getAttribute(name);
      if (value) return { attribute: name, value: bounded(value) };
    }
    return null;
  };

  const structuralAttributes = (element) => {
    if (!(element instanceof Element)) return {};
    const allowed = /^(?:role|id|data-(?:message-author-role|message-id|turn-id|branch-id|is-current|current|testid)|aria-current)$/;
    const result = {};
    for (const attribute of element.attributes) {
      if (allowed.test(attribute.name)) {
        result[attribute.name] = bounded(attribute.value);
      }
    }
    return result;
  };

  const assistantOwner = (element) =>
    element instanceof Element
      ? element.closest('[data-message-author-role="assistant"]')
      : null;

  const markerSummary = (anchor) => {
    const owner = assistantOwner(anchor);
    const rawHref = bounded(anchor.getAttribute("href"), 192);
    const absoluteHref = bounded(anchor.href, 192);
    return {
      rawHref,
      absoluteHref,
      exact: rawHref === exactMarker || absoluteHref === exactMarker,
      visible: visible(anchor),
      ownerRole: owner?.getAttribute("data-message-author-role") ?? null,
      ownerId: opaqueId(owner),
      ownerWasPresentAtInstall: owner ? baselineAssistants.has(owner) : null,
      ownerAttributes: structuralAttributes(owner),
    };
  };

  const findMarkers = (root = document) => {
    const found = [];
    if (root instanceof HTMLAnchorElement && root.href.startsWith(markerPrefix)) {
      found.push(root);
    }
    if (root instanceof Document || root instanceof Element) {
      for (const anchor of root.querySelectorAll('a[href^="https://petgpt.invalid/#r1/"]')) {
        found.push(anchor);
      }
    }
    return [...new Set(found)].slice(0, 100);
  };

  const composerSummary = (element) => {
    const tagName = element.tagName.toLowerCase();
    let empty = "unknown";
    let evidence = "no safe structural empty signal";

    if (tagName === "textarea" || tagName === "input") {
      empty = element.value.length === 0;
      evidence = "boolean value length only; value not recorded";
    } else if (element.getAttribute("data-empty") === "true") {
      empty = true;
      evidence = "data-empty=true";
    } else if (element.matches(":empty")) {
      empty = true;
      evidence = ":empty structural match";
    }

    return {
      tagName,
      focused: document.activeElement === element,
      empty,
      evidence,
      attributes: structuralAttributes(element),
      childElementCount: element.childElementCount,
    };
  };

  const composers = () =>
    [...document.querySelectorAll('textarea, [contenteditable="true"]')]
      .filter(visible)
      .slice(0, 10)
      .map(composerSummary);

  const generationControls = () =>
    [...document.querySelectorAll("button[data-testid]")]
      .filter((button) =>
        /(?:stop|generat|regenerat|send)/i.test(button.getAttribute("data-testid") ?? ""),
      )
      .slice(0, 20)
      .map((button) => ({
        dataTestId: bounded(button.getAttribute("data-testid")),
        visible: visible(button),
        disabled: button.matches(":disabled"),
      }));

  const assistantSummaries = () =>
    [...document.querySelectorAll('[data-message-author-role="assistant"]')]
      .slice(0, 100)
      .map((element) => ({
        id: opaqueId(element),
        visible: visible(element),
        presentAtInstall: baselineAssistants.has(element),
        attributes: structuralAttributes(element),
        markerCount: findMarkers(element).length,
      }));

  const api = {
    version: 1,
    startedAt: new Date().toISOString(),
    startedAtPerformance: performance.now(),
    snapshot(label) {
      if (typeof label !== "string" || !label.trim()) {
        throw new Error("snapshot(label) requires a nonempty label.");
      }
      if (snapshots.length >= maxSnapshots) {
        throw new Error("Snapshot limit reached.");
      }
      const snapshot = {
        label: label.slice(0, 80),
        at: new Date().toISOString(),
        routeShape: routeShape(),
        composers: composers(),
        generationControls: generationControls(),
        assistants: assistantSummaries(),
        markers: findMarkers().map(markerSummary),
      };
      snapshots.push(snapshot);
      console.info("PetGPT T1 snapshot recorded:", snapshot.label);
      return snapshot;
    },
    report() {
      return {
        probeVersion: 1,
        startedAt: api.startedAt,
        reportedAt: new Date().toISOString(),
        userAgent: bounded(navigator.userAgent, 256),
        exactMarker,
        currentRouteShape: routeShape(),
        truncated,
        privacy: {
          readsTextContent: false,
          readsInnerText: false,
          serializesHtml: false,
          readsNetworkBodies: false,
          readsDraftText: false,
        },
        events: [...events],
        snapshots: [...snapshots],
      };
    },
    reportJson() {
      const json = JSON.stringify(api.report(), null, 2);
      console.info(json);
      return json;
    },
    stop() {
      if (stopped) return;
      stopped = true;
      observer.disconnect();
      document.removeEventListener("focusin", onFocus, true);
      document.removeEventListener("focusout", onFocus, true);
      document.removeEventListener("input", onInput, true);
      document.removeEventListener("keydown", onKeyDown, true);
      document.removeEventListener("submit", onSubmit, true);
      document.removeEventListener("click", onClick, true);
      window.removeEventListener("popstate", onPopState);
      window.removeEventListener("hashchange", onHashChange);
      console.info("PetGPT T1 structural probe stopped.");
    },
  };

  const record = (kind, detail = {}) => {
    if (stopped) return;
    if (events.length >= maxEvents) {
      truncated = true;
      return;
    }
    events.push({
      sequence: events.length + 1,
      millisecondsSinceInstall: Math.round(performance.now() - api.startedAtPerformance),
      kind,
      routeShape: routeShape(),
      ...detail,
    });
  };

  const recordRouteIfChanged = (source) => {
    const current = routeShape();
    if (current !== lastRouteShape) {
      lastRouteShape = current;
      record("route", { source, shape: current });
    }
  };

  const recordGenerationIfChanged = (source) => {
    const controls = generationControls();
    const signature = JSON.stringify(controls);
    if (signature !== lastGenerationSignature) {
      lastGenerationSignature = signature;
      record("generation-controls", { source, controls });
    }
  };

  const recordMarkers = (root, source) => {
    for (const anchor of findMarkers(root)) {
      record("marker", { source, marker: markerSummary(anchor) });
    }
  };

  const composerForTarget = (target) =>
    target instanceof Element
      ? target.closest('textarea, [contenteditable="true"]')
      : null;

  const onFocus = (event) => {
    const composer = composerForTarget(event.target);
    if (composer) record(event.type, { composer: composerSummary(composer) });
  };

  const onInput = (event) => {
    const composer = composerForTarget(event.target);
    if (composer) record("input", { composer: composerSummary(composer) });
  };

  const onKeyDown = (event) => {
    const composer = composerForTarget(event.target);
    if (composer && event.key === "Enter") {
      record("composer-enter", {
        isComposing: Boolean(event.isComposing),
        shiftKey: Boolean(event.shiftKey),
        composer: composerSummary(composer),
      });
    }
  };

  const onSubmit = (event) => {
    const composer = event.target instanceof Element
      ? event.target.querySelector('textarea, [contenteditable="true"]')
      : null;
    record("form-submit", {
      hasComposer: Boolean(composer),
      composer: composer ? composerSummary(composer) : null,
    });
  };

  const onClick = (event) => {
    const button = event.target instanceof Element ? event.target.closest("button") : null;
    if (!button) return;
    const dataTestId = button.getAttribute("data-testid") ?? "";
    if (/(?:send|regenerat|stop)/i.test(dataTestId)) {
      record("control-click", { dataTestId: bounded(dataTestId) });
    }
  };

  const observer = new MutationObserver((records) => {
    if (stopped) return;
    for (const mutation of records.slice(0, 200)) {
      if (mutation.type === "attributes") {
        recordMarkers(mutation.target, `attribute:${mutation.attributeName}`);
      } else {
        for (const node of [...mutation.addedNodes].slice(0, 200)) {
          if (node instanceof Element) recordMarkers(node, "added-node");
        }
      }
    }
    recordRouteIfChanged("mutation");
    recordGenerationIfChanged("mutation");
  });

  const onPopState = () => recordRouteIfChanged("popstate");
  const onHashChange = () => recordRouteIfChanged("hashchange");

  for (const assistant of document.querySelectorAll('[data-message-author-role="assistant"]')) {
    baselineAssistants.add(assistant);
  }

  observer.observe(document.documentElement, {
    subtree: true,
    childList: true,
    attributes: true,
    attributeFilter: [
      "href",
      "data-message-author-role",
      "data-message-id",
      "data-turn-id",
      "data-branch-id",
      "data-is-current",
      "aria-current",
      "data-testid",
    ],
  });
  document.addEventListener("focusin", onFocus, true);
  document.addEventListener("focusout", onFocus, true);
  document.addEventListener("input", onInput, true);
  document.addEventListener("keydown", onKeyDown, true);
  document.addEventListener("submit", onSubmit, true);
  document.addEventListener("click", onClick, true);
  window.addEventListener("popstate", onPopState);
  window.addEventListener("hashchange", onHashChange);

  window[globalName] = api;
  recordRouteIfChanged("install");
  recordGenerationIfChanged("install");
  recordMarkers(document, "install-baseline");
  console.info(
    "PetGPT T1 structural probe installed. It records only bounded structural metadata.",
  );
})();
