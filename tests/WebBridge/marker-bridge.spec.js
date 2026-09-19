const { test, expect } = require("@playwright/test");
const fs = require("node:fs");
const path = require("node:path");

const fixture = fs.readFileSync(path.join(__dirname, "fixtures", "chat.html"), "utf8");
const bridgeSource = fs.readFileSync(path.join(__dirname, "..", "..", "Web", "pet-marker-bridge.js"), "utf8");
const marker = "https://petgpt.invalid/#r1/0123456789abcdef/trixie/smug/65/end";

async function boot(page, options = {}) {
  await page.route("https://chatgpt.com/fixture", route => route.fulfill({ status: 200, contentType: "text/html", body: fixture }));
  await page.addInitScript(() => {
    window.__petMessages = [];
    window.chrome = { webview: {
      postMessage: value => window.__petMessages.push(value),
      addEventListener() {}, removeEventListener() {}
    }};
  });
  await page.goto("https://chatgpt.com/fixture");
  await page.addScriptTag({ content: bridgeSource });
  await page.evaluate(config => window.__petgptMarkerBridgeV1.reconfigure({
    documentSession: "aaaaaaaaaaaaaaaa", routeRevision: 0,
    enabled: config.enabled !== false, showControlMarkers: config.show === true,
    routeKind: "ProjectConversation"
  }), options);
  if (options.turn !== false) {
    await page.evaluate(() => {
      window.__petgptMarkerBridgeV1.observeTurn(1);
      window.__petgptMarkerBridgeV1.observeGeneration("generating", 1);
    });
  }
}

async function addMessage(page, { id = "assistant-1", role = "assistant", href = marker, where = "plain", prepend = false } = {}) {
  const key = await page.evaluate(({ id, role, where, prepend }) => {
    const owner = document.createElement("article");
    const key = crypto.randomUUID();
    owner.setAttribute("data-fixture-key", key);
    if (role !== null) owner.setAttribute("data-message-author-role", role);
    if (id !== null) owner.setAttribute("data-message-id", id);
    let parent = owner;
    if (where !== "plain") {
      parent = document.createElement(where === "tool" ? "section" : where);
      if (where === "tool") parent.setAttribute("data-message-author-role", "tool");
      owner.append(parent);
    }
    const root = document.querySelector("main");
    prepend ? root.prepend(owner) : root.append(owner);
    return key;
  }, { id, role, where, prepend });
  await page.evaluate(({ key, href }) => {
    const owner = document.querySelector(`[data-fixture-key="${key}"]`);
    const parent = owner.querySelector("section,code,pre,blockquote") || owner;
    const anchor = document.createElement("a");
    anchor.setAttribute("href", href);
    parent.append(anchor);
  }, { key, href });
}

async function hydrateCompleteMessage(page, id = "hydrated-old") {
  await page.evaluate(({ id, href }) => {
    const owner = document.createElement("article");
    owner.dataset.messageAuthorRole = "assistant";
    owner.dataset.messageId = id;
    const anchor = document.createElement("a");
    anchor.setAttribute("href", href);
    owner.append(anchor);
    document.querySelector("main").append(owner);
  }, { id, href: marker });
}

const reactions = page => page.evaluate(() => window.__petMessages.filter(message => message.kind === "reaction"));

test("valid marker dispatches only after stabilization", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(170);
  const found = await reactions(page); expect(found).toHaveLength(1); expect(found[0].payload.href).toBe(marker);
});

test("first raw appearance never dispatches synchronously", async ({ page }) => {
  await boot(page); await addMessage(page); expect(await reactions(page)).toHaveLength(0);
});

test("marker gaining assistant owner is rechecked and accepted", async ({ page }) => {
  await boot(page);
  await addMessage(page, { role: null, id: null });
  await page.waitForTimeout(40);
  await page.evaluate(() => { const owner = document.querySelector("article"); owner.dataset.messageAuthorRole = "assistant"; owner.dataset.messageId = "assistant-1"; });
  await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(1);
});

test("href mutation restarts stabilization and rejects old href", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(60);
  await page.evaluate(value => document.querySelector("a").setAttribute("href", value), marker.replace("/65/", "/80/"));
  await page.waitForTimeout(110); expect(await reactions(page)).toHaveLength(0);
  await page.waitForTimeout(70); expect((await reactions(page))[0].payload.href).toContain("/80/end");
});

test("partial streamed intensity never dispatches", async ({ page }) => {
  await boot(page); await addMessage(page, { href: marker.replace("/65/end", "/6") });
  await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
  await page.evaluate(value => document.querySelector("a").setAttribute("href", value), marker);
  await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(1);
});

test("user marker is ignored", async ({ page }) => { await boot(page); await addMessage(page, { role: "user" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });
test("tool marker is ignored", async ({ page }) => { await boot(page); await addMessage(page, { where: "tool" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });
test("code marker is ignored", async ({ page }) => { await boot(page); await addMessage(page, { where: "code" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });
test("pre marker is ignored", async ({ page }) => { await boot(page); await addMessage(page, { where: "pre" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });
test("blockquote marker is ignored", async ({ page }) => { await boot(page); await addMessage(page, { where: "blockquote" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });

test("iframe marker is outside the observed top document", async ({ page }) => {
  await boot(page); await page.evaluate(value => { const frame = document.createElement("iframe"); document.body.append(frame); const a = frame.contentDocument.createElement("a"); a.href = value; frame.contentDocument.body.append(a); }, marker);
  await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
});

test("missing assistant id fails closed", async ({ page }) => { await boot(page); await addMessage(page, { id: null }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });
test("oversized assistant id fails closed", async ({ page }) => { await boot(page); await addMessage(page, { id: "a".repeat(129) }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });

test("initial marker is baseline history and never dispatches", async ({ page }) => {
  await page.route("https://chatgpt.com/fixture", route => route.fulfill({ status: 200, contentType: "text/html", body: fixture.replace("</main>", `<article data-message-author-role="assistant" data-message-id="old"><a href="${marker}"></a></article></main>`) }));
  await page.addInitScript(() => { window.__petMessages=[]; window.chrome={webview:{postMessage:v=>window.__petMessages.push(v),addEventListener(){},removeEventListener(){}}}; });
  await page.goto("https://chatgpt.com/fixture"); await page.addScriptTag({ content: bridgeSource });
  await page.evaluate(() => { window.__petgptMarkerBridgeV1.reconfigure({documentSession:"aaaaaaaaaaaaaaaa",routeRevision:0,enabled:true,showControlMarkers:false,routeKind:"ProjectConversation"}); window.__petgptMarkerBridgeV1.observeTurn(1); window.__petgptMarkerBridgeV1.observeGeneration("generating",1); });
  await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
});

test("history hydration prepended behind current structural tail is not replayed", async ({ page }) => {
  await boot(page); await addMessage(page, { id: "current-no-marker", href: "https://example.com/" });
  await addMessage(page, { id: "old", prepend: true }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
});

test("duplicate mutation for same marker emits once", async ({ page }) => {
  await boot(page); await addMessage(page); await page.evaluate(() => document.querySelector("a").setAttribute("href", document.querySelector("a").getAttribute("href"))); await page.waitForTimeout(180); expect(await reactions(page)).toHaveLength(1);
});

test("two markers in one assistant turn emit at most once", async ({ page }) => {
  await boot(page); await addMessage(page); await page.evaluate(value => { const a=document.createElement("a"); a.href=value.replace("/65/", "/80/"); document.querySelector("article").append(a); }, marker); await page.waitForTimeout(180); expect(await reactions(page)).toHaveLength(1);
});

test("same reaction on the next turn is allowed", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(170);
  await page.evaluate(() => { window.__petgptMarkerBridgeV1.observeTurn(2); window.__petgptMarkerBridgeV1.observeGeneration("generating",2); });
  await addMessage(page, { id: "assistant-2" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(2);
});

test("regenerate serial creates a new correlation", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(170);
  await page.evaluate(() => window.__petgptMarkerBridgeV1.observeTurn(2));
  await addMessage(page, { id: "assistant-regenerated" }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(2);
});

test("root replacement does not replay baseline markers", async ({ page }) => {
  await boot(page, { turn: false }); await addMessage(page, { id: "old" });
  await page.evaluate(() => { const old=document.querySelector("main"); const next=old.cloneNode(true); old.replaceWith(next); window.__petgptMarkerBridgeV1.observeTurn(1); });
  await page.waitForTimeout(180); expect(await reactions(page)).toHaveLength(0);
});

test("route reconfigure invalidates stale candidate", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(60);
  await page.evaluate(() => window.__petgptMarkerBridgeV1.reconfigure({documentSession:"aaaaaaaaaaaaaaaa",routeRevision:1,enabled:true,showControlMarkers:false,routeKind:"ProjectConversation"}));
  await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
});

test("queue overflow disables only reaction observer", async ({ page }) => {
  await boot(page);
  await page.evaluate(value => { const f=document.createDocumentFragment(); for(let i=0;i<1001;i++){const o=document.createElement("article");o.dataset.messageAuthorRole="assistant";o.dataset.messageId=`a-${i}`;const a=document.createElement("a");a.href=value;o.append(a);f.append(o);}document.querySelector("main").append(f);}, marker);
  await page.waitForTimeout(20); expect(await page.evaluate(() => window.__petgptMarkerBridgeV1.getDiagnostics().degraded)).toBe(true); expect(await reactions(page)).toHaveLength(0);
});

test("overflow cannot repopulate the candidate queue after degradation", async ({ page }) => {
  await boot(page);
  await page.evaluate(value => { const f=document.createDocumentFragment(); for(let i=0;i<5000;i++){const o=document.createElement("article");o.dataset.messageAuthorRole="assistant";o.dataset.messageId=`overflow-${i}`;const a=document.createElement("a");a.href=value;o.append(a);f.append(o);}document.querySelector("main").append(f);}, marker);
  await page.waitForTimeout(20); const diagnostics=await page.evaluate(() => window.__petgptMarkerBridgeV1.getDiagnostics()); expect(diagnostics.degraded).toBe(true); expect(diagnostics.queuedCandidates).toBe(0);
});

test("overflow degradation is sticky across route revision", async ({ page }) => {
  await boot(page);
  await page.evaluate(value => { const f=document.createDocumentFragment(); for(let i=0;i<1001;i++){const o=document.createElement("article");o.dataset.messageAuthorRole="assistant";o.dataset.messageId=`sticky-${i}`;const a=document.createElement("a");a.href=value;o.append(a);f.append(o);}document.querySelector("main").append(f);}, marker);
  await page.waitForTimeout(20);
  await page.evaluate(() => window.__petgptMarkerBridgeV1.reconfigure({documentSession:"aaaaaaaaaaaaaaaa",routeRevision:1,enabled:true,showControlMarkers:false,routeKind:"ProjectConversation"}));
  expect(await page.evaluate(() => window.__petgptMarkerBridgeV1.getDiagnostics().degraded)).toBe(true);
});

test("fully formed unseen historical marker cannot claim an active generation", async ({ page }) => {
  await boot(page); await hydrateCompleteMessage(page); await page.waitForTimeout(180); expect(await reactions(page)).toHaveLength(0);
});

test("scheduled batch processes no more than 200 candidates", async ({ page }) => {
  await boot(page); await page.evaluate(value => { const f=document.createDocumentFragment(); for(let i=0;i<350;i++){const o=document.createElement("article");o.dataset.messageAuthorRole="assistant";o.dataset.messageId=`a-${i}`;const a=document.createElement("a");a.href=value;o.append(a);f.append(o);}document.querySelector("main").append(f);}, marker);
  await page.waitForTimeout(10); expect(await page.evaluate(() => window.__petgptMarkerBridgeV1.getDiagnostics().maximumBatchObserved)).toBeLessThanOrEqual(200);
});

test("malformed marker remains visible", async ({ page }) => { await boot(page); await addMessage(page, { href: marker.replace("/end", "") }); await page.waitForTimeout(20); expect(await page.locator("a").getAttribute("data-petgpt-control-marker")).toBeNull(); });
test("unsupported version remains visible", async ({ page }) => { await boot(page); await addMessage(page, { href: marker.replace("#r1/", "#r2/") }); await page.waitForTimeout(20); expect(await page.locator("a").getAttribute("data-petgpt-control-marker")).toBeNull(); });
test("valid marker is hidden when control markers are off", async ({ page }) => { await boot(page); await addMessage(page); await page.waitForTimeout(20); expect(await page.locator("a").getAttribute("data-petgpt-control-marker")).toBe("hidden"); });
test("valid marker remains visible when control markers are on", async ({ page }) => { await boot(page, { show: true }); await addMessage(page); await page.waitForTimeout(20); expect(await page.locator("a").getAttribute("data-petgpt-control-marker")).toBe("visible"); });
test("ordinary links are untouched", async ({ page }) => { await boot(page); await addMessage(page, { href: "https://example.com/" }); await page.waitForTimeout(20); expect(await page.locator("a").getAttribute("data-petgpt-control-marker")).toBeNull(); });

test("response text getters are never read", async ({ page }) => {
  await boot(page);
  await page.evaluate(() => { for (const [prototype, name] of [[Node.prototype,"textContent"],[HTMLElement.prototype,"innerText"],[Element.prototype,"innerHTML"],[Element.prototype,"outerHTML"]]) Object.defineProperty(prototype,name,{configurable:true,get(){if(this.closest?.("[data-message-author-role]"))throw new Error(`forbidden ${name}`);return "";},set(){throw new Error(`forbidden set ${name}`);}}); const original=XMLSerializer.prototype.serializeToString; XMLSerializer.prototype.serializeToString=function(node){if(node?.closest?.("[data-message-author-role]"))throw new Error("forbidden serialization");return original.call(this,node);}; });
  await addMessage(page); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(1);
});

test("hidden and inactive branch markers fail closed", async ({ page }) => {
  await boot(page); await addMessage(page, { id: "hidden" }); await page.evaluate(() => document.querySelector("article").setAttribute("aria-hidden", "true")); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
});

test("ambiguous duplicate assistant identity fails closed", async ({ page }) => {
  await boot(page); await addMessage(page, { id: "duplicate" }); await page.evaluate(() => { const other=document.createElement("article"); other.dataset.messageAuthorRole="assistant"; other.dataset.messageId="duplicate"; document.querySelector("main").append(other); }); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0);
});

test("teardown disconnects observer", async ({ page }) => { await boot(page); await page.evaluate(() => window.__petgptMarkerBridgeV1.teardown()); await addMessage(page); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });

test("late stabilization callback after teardown emits nothing", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(50); await page.evaluate(() => window.__petgptMarkerBridgeV1.teardown()); await page.waitForTimeout(150); expect(await reactions(page)).toHaveLength(0);
});

test("same-document landing to conversation preserves correlated turn", async ({ page }) => {
  await boot(page); await page.evaluate(() => window.__petgptMarkerBridgeV1.reconfigure({documentSession:"aaaaaaaaaaaaaaaa",routeRevision:1,enabled:true,showControlMarkers:false,routeKind:"ProjectConversation",preserveLiveTurn:true})); await addMessage(page); await page.waitForTimeout(170); const found=await reactions(page); expect(found).toHaveLength(1); expect(found[0].payload.generationSerial).toBe(1);
});

test("disabled setting installs no dispatching observer", async ({ page }) => { await boot(page, { enabled: false }); await addMessage(page); await page.waitForTimeout(170); expect(await reactions(page)).toHaveLength(0); });

test("payload contains only href generation serial and assistant id", async ({ page }) => {
  await boot(page); await addMessage(page); await page.waitForTimeout(170); expect(Object.keys((await reactions(page))[0].payload).sort()).toEqual(["assistantId","generationSerial","href"]);
});
