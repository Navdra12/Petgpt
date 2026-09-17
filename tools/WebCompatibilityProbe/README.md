# Disposable Web compatibility probe

This development-only probe collects structural metadata for PetGPT v2 T1. It
is not production code and must not be wired into PetGPT services.

The initial T1 live run is complete. Its capability-specific results are in
`docs/validation/web-compatibility.md`, and the supported marker grammar and
acceptance rules are drafted in `docs/contracts/reaction-link-v1.md`. This tool
is retained only for disposable future compatibility revalidation.

## Before running

1. Launch PetGPT normally and open its ChatGPT bubble.
2. Manually navigate to the existing **PetChats** project. Do not search or
   discover it with a script.
3. Copy the current PetChats landing URL and keep it with the report. Treat it
   as opaque evidence; do not edit or guess its project identifier.
4. Create or open a disposable PetChats conversation.
5. With the WebView focused, open its WebView2 developer tools using the normal
   developer-tools command available on your machine.
6. Run the complete contents of `probe.js` once in the top-level
   `https://chatgpt.com` page console. If developer tools blocks pasted code,
   stop and report that blocker; do not bypass a browser safety warning.

The probe prints an installation message and creates
`window.__petgptT1Probe`. It does not read message or draft text.

## User-operated test sequence

Run this after installing the probe:

```javascript
window.__petgptT1Probe.snapshot("pet-chats-landing")
```

Then perform the following with native ChatGPT controls. After each numbered
step, call `snapshot()` with a short label.

1. Start a new disposable project chat and submit exactly:

   ```text
   Return a short ordinary response and finish with exactly this Markdown link on its own line:

   [·](https://petgpt.invalid/#r1/6c73a04a8842e90b/trixie/smug/65/end)
   ```

2. Use native Regenerate on that response.
3. Submit a long-response variation, keeping the marker URL exactly unchanged:

   ```text
   Return a long ordinary response of at least 1,500 words and finish with exactly this Markdown link on its own line:

   [·](https://petgpt.invalid/#r1/6c73a04a8842e90b/trixie/smug/65/end)
   ```

4. Submit a second marker response with a different grammar-valid URL:

   ```text
   Return a short ordinary response and finish with exactly this Markdown link on its own line:

   [·](https://petgpt.invalid/#r1/6c73a04a8842e90b/trixie/smug/66/end)
   ```

5. Exercise Back, Forward, Reload, open an existing project chat, and native New
   chat. Record whether New chat remains inside PetChats.
6. In an empty composer, test focus and a harmless input event, then clear it
   manually. Repeat with a nonempty composer. The probe records only occurrence
   and structural empty evidence, never the draft.
7. Exercise Send-button, Enter-to-send, an IME composition if available, and
   Regenerate. Do not paste persona content yet and do not auto-send anything.

Suggested snapshots:

```javascript
window.__petgptT1Probe.snapshot("short-marker")
window.__petgptT1Probe.snapshot("regenerate")
window.__petgptT1Probe.snapshot("long-marker")
window.__petgptT1Probe.snapshot("second-marker")
window.__petgptT1Probe.snapshot("after-route-tests")
window.__petgptT1Probe.snapshot("composer-tests")
```

At the end, run:

```javascript
window.__petgptT1Probe.reportJson()
```

Copy only the returned JSON string and send it back together with:

- the PetChats landing URL;
- whether native New chat stayed in PetChats;
- whether the IME path was available;
- any step that could not be completed.

Do not send a DOM snapshot, screenshots containing conversation prose, cookies,
tokens, profile files, or the actual response text. Stop the probe with:

```javascript
window.__petgptT1Probe.stop()
```
