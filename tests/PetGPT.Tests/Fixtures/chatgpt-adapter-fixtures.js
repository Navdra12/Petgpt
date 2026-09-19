"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const adapterSource = fs.readFileSync(
  path.resolve(__dirname, "..", "..", "..", "Web", "chatgpt-adapter.js"),
  "utf8");

class FakeElement {
  constructor(tagName, document) {
    this.tagName = tagName.toLowerCase();
    this.ownerDocument = document;
    this.parentElement = null;
    this.children = [];
    this.attributes = new Map();
    this.id = "";
  }

  appendChild(child) {
    child.parentElement = this;
    this.children.push(child);
    return child;
  }

  replaceChildren(...children) {
    this.children = [];
    for (const child of children) this.appendChild(child);
  }

  remove() {
    if (!this.parentElement) return;
    this.parentElement.children = this.parentElement.children.filter((child) => child !== this);
    this.parentElement = null;
  }

  setAttribute(name, value) {
    this.attributes.set(name, String(value));
    if (name === "id") this.id = String(value);
  }

  removeAttribute(name) {
    this.attributes.delete(name);
  }

  getAttribute(name) {
    return this.attributes.get(name) ?? null;
  }

  matches(selector) {
    if (selector === "main") return this.tagName === "main";
    if (selector === "form") return this.tagName === "form";
    if (selector === "#prompt-textarea[role=\"textbox\"]") {
      return this.id === "prompt-textarea" && this.getAttribute("role") === "textbox";
    }
    if (selector === "[data-testid=\"stop-button\"]") {
      return this.getAttribute("data-testid") === "stop-button";
    }
    if (selector === "[data-petgpt-surface]") return this.attributes.has("data-petgpt-surface");
    return false;
  }

  querySelector(selector) {
    for (const child of this.children) {
      if (child.matches?.(selector)) return child;
      const nested = child.querySelector?.(selector);
      if (nested) return nested;
    }
    return null;
  }

  querySelectorAll(selector, output = []) {
    for (const child of this.children) {
      if (child.matches?.(selector)) output.push(child);
      child.querySelectorAll?.(selector, output);
    }
    return output;
  }

  closest(selector) {
    let current = this;
    while (current) {
      if (current.matches?.(selector)) return current;
      current = current.parentElement;
    }
    return null;
  }
}

class FakeDocument {
  constructor() {
    this.documentElement = new FakeElement("html", this);
    this.head = this.documentElement.appendChild(new FakeElement("head", this));
    this.body = this.documentElement.appendChild(new FakeElement("body", this));
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }

  createTextNode(value) {
    return { value, parentElement: null };
  }

  getElementById(id) {
    return this.documentElement.querySelectorAll("[data-petgpt-surface]")
      .concat(this.allElements())
      .find((element) => element.id === id) ?? null;
  }

  querySelector(selector) {
    if (this.documentElement.matches(selector)) return this.documentElement;
    return this.documentElement.querySelector(selector);
  }

  querySelectorAll(selector) {
    const output = [];
    if (this.documentElement.matches(selector)) output.push(this.documentElement);
    return this.documentElement.querySelectorAll(selector, output);
  }

  allElements() {
    const output = [];
    const visit = (element) => {
      output.push(element);
      for (const child of element.children) if (child instanceof FakeElement) visit(child);
    };
    visit(this.documentElement);
    return output;
  }
}

function createHarness({ stop = true } = {}) {
  const document = new FakeDocument();
  const appRoot = document.body.appendChild(new FakeElement("div", document));
  const main = appRoot.appendChild(new FakeElement("main", document));
  const explicitlyColored = main.appendChild(new FakeElement("p", document));
  explicitlyColored.setAttribute("style", "color:#111111");
  const form = appRoot.appendChild(new FakeElement("form", document));
  const composer = form.appendChild(new FakeElement("div", document));
  composer.setAttribute("id", "prompt-textarea");
  composer.setAttribute("role", "textbox");
  let stopButton = null;
  if (stop) {
    stopButton = form.appendChild(new FakeElement("button", document));
    stopButton.setAttribute("data-testid", "stop-button");
  }

  const messages = [];
  const observers = [];
  const webviewListeners = new Map();
  const windowListeners = new Map();

  class FakeMutationObserver {
    constructor(callback) {
      this.callback = callback;
      this.observations = [];
      this.disconnected = false;
      observers.push(this);
    }

    observe(target, options) {
      this.observations.push({ target, options });
      this.disconnected = false;
    }

    disconnect() {
      this.disconnected = true;
    }

    emit(records) {
      if (!this.disconnected) this.callback(records);
    }
  }

  const window = {
    location: { origin: "https://chatgpt.com" },
    chrome: {
      webview: {
        postMessage: (message) => messages.push(message),
        addEventListener: (name, listener) => webviewListeners.set(name, listener),
        removeEventListener: (name) => webviewListeners.delete(name)
      }
    },
    addEventListener: (name, listener) => windowListeners.set(name, listener),
    removeEventListener: (name) => windowListeners.delete(name)
  };
  window.top = window;

  const context = vm.createContext({
    window,
    document,
    location: window.location,
    MutationObserver: FakeMutationObserver,
    queueMicrotask: (callback) => callback(),
    console
  });
  vm.runInContext(adapterSource, context, { filename: "chatgpt-adapter.js" });

  const configure = (routeRevision) => {
    webviewListeners.get("message")({
      data: {
        v: 1,
        op: "configure",
        documentSession: "0123456789abcdef",
        routeRevision,
        historyMode: false,
        compact: { enabled: true, css: "main{max-width:100%}" },
        theme: { enabled: false, css: null }
      }
    });
  };

  return {
    document,
    appRoot,
    main,
    form,
    composer,
    messages,
    observers,
    configure,
    removeStop() {
      stopButton?.remove();
      stopButton = null;
    },
    replaceRoot() {
      appRoot.remove();
      const replacement = document.body.appendChild(new FakeElement("div", document));
      const replacementMain = replacement.appendChild(new FakeElement("main", document));
      const replacementForm = replacement.appendChild(new FakeElement("form", document));
      const replacementComposer = replacementForm.appendChild(new FakeElement("div", document));
      replacementComposer.setAttribute("id", "prompt-textarea");
      replacementComposer.setAttribute("role", "textbox");
      return { replacement, replacementMain, replacementComposer };
    }
  };
}

const checks = [];
function check(name, callback) {
  try {
    callback();
    checks.push({ name, passed: true });
  } catch (error) {
    checks.push({ name, passed: false, error });
  }
}

check("generation is freshly reported for every route revision", () => {
  const harness = createHarness({ stop: true });
  harness.configure(0);
  harness.messages.length = 0;
  harness.configure(1);
  assert.ok(harness.messages.some((message) =>
    message.kind === "activity" && message.payload.event === "generation" && message.payload.state === "generating"));
  harness.messages.length = 0;
  harness.removeStop();
  harness.configure(2);
  assert.ok(harness.messages.some((message) =>
    message.kind === "activity" && message.payload.event === "generation" && message.payload.state === "unknown"));
});

check("root observation is narrow and capability messages are deduplicated", () => {
  const harness = createHarness();
  harness.configure(0);
  assert.equal(
    harness.observers.some((observer) => observer.observations.some((entry) =>
      entry.target === harness.document.documentElement && entry.options.subtree === true)),
    false);
  harness.messages.length = 0;
  const active = harness.observers.filter((observer) => !observer.disconnected);
  for (const observer of active) observer.emit([{ addedNodes: [new FakeElement("div", harness.document)] }]);
  assert.equal(harness.messages.filter((message) => message.kind === "ready").length, 0);
});

check("overflow degradation is sticky for the current identity", () => {
  const harness = createHarness();
  harness.configure(0);
  const active = harness.observers.filter((observer) => !observer.disconnected);
  for (const observer of active) {
    observer.emit([{ addedNodes: Array.from({ length: 201 }, () => new FakeElement("div", harness.document)) }]);
  }
  const observerCount = harness.observers.length;
  harness.configure(0);
  assert.equal(harness.observers.length, observerCount);
  assert.equal(harness.observers.some((observer) => !observer.disconnected), false);
});

check("unsafe page color pair stays unavailable with explicit descendants", () => {
  const harness = createHarness();
  harness.configure(0);
  const ready = harness.messages.find((message) => message.kind === "ready");
  assert.ok(ready);
  assert.equal(ready.payload.capabilities.pageSurface, false);
  assert.equal(ready.payload.capabilities.composerSurface, true);
  assert.equal(harness.composer.getAttribute("data-petgpt-surface"), "composer");
});

check("top-level root replacement re-establishes bounded landmarks", () => {
  const harness = createHarness();
  harness.configure(0);
  const replacement = harness.replaceRoot();
  harness.messages.length = 0;
  const rootObservers = harness.observers.filter((observer) => !observer.disconnected &&
    observer.observations.some((entry) => entry.target === harness.document.body));
  assert.ok(rootObservers.length > 0);
  for (const observer of rootObservers) observer.emit([{ addedNodes: [replacement.replacement] }]);
  assert.equal(replacement.replacementMain.getAttribute("data-petgpt-surface"), "page");
  assert.equal(replacement.replacementComposer.getAttribute("data-petgpt-surface"), "composer");
});

const failed = checks.filter((entry) => !entry.passed);
for (const entry of checks) {
  console.log(`${entry.passed ? "PASS" : "FAIL"}: ${entry.name}`);
  if (!entry.passed) console.error(entry.error.stack || entry.error);
}
if (failed.length > 0) process.exit(1);
console.log(`adapter fixtures: ${checks.length}/${checks.length} pass`);
