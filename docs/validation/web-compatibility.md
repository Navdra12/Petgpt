# Web compatibility validation

## T1 decision

Date: 2026-09-17

- Reaction marker transport: **GO**
- Historical replay exclusion: **GO**
- Full T1: **complete with capability-specific results**

This is compatibility evidence for the tested live site and runtime, not a
claim that ChatGPT DOM selectors are stable or supported public APIs.

## Tested context

| Item | Context |
|---|---|
| Surface | Authenticated ChatGPT rendered inside the PetGPT WebView2 |
| Browser/runtime | Edge / WebView2 153 (`153.0.4234.32` detected during T0) |
| WebView2 NuGet package | `1.0.4191.47` |
| Project | Existing user-created PetChats project, reached manually |
| Landing route shape | `https://chatgpt.com/g/:opaque/project` |
| Conversation route shape | `https://chatgpt.com/g/:opaque/c/:opaque` |

The user supplied the actual PetChats landing URL as opaque evidence. Only its
route shape is recorded here; no project ID was inferred from the project name.

## Capability matrix

| Capability | Result | Tested structural evidence | Required fallback or constraint | Live user verification |
|---|---|---|---|---|
| Markdown marker renders as an anchor | SUPPORTED | Reserved Markdown link became an anchor | Disable reactions if this changes | Yes |
| Exact raw and absolute marker href survive | SUPPORTED | `https://petgpt.invalid/#r1/<epoch>/<pet>/<reaction>/<intensity>/end` remained exact | Reject rewritten or malformed hrefs | Yes |
| Short response marker | SUPPORTED | Exact marker observed | None beyond normal validation | Yes |
| Long/streaming response marker | SUPPORTED | Exact marker survived streaming/rendering | Stabilize and revalidate before dispatch | Yes |
| Regenerated response marker | SUPPORTED | Exact marker observed on regenerated output | Correlate to a new generation serial | Yes |
| Second valid marker intensity | SUPPORTED | Another grammar-valid intensity remained exact | Reparse and range-check in the host | Yes |
| Assistant ownership | SUPPORTED on tested version | Owner stabilized with `data-message-author-role="assistant"` | Treat selector as a tested adapter assumption, not an API | Yes |
| Opaque assistant identity | SUPPORTED on tested version | Distinct `data-message-id` values were exposed | Disable dispatch if a bounded opaque ID is unavailable | Yes |
| Regenerated-answer identity | SUPPORTED on tested version | Regenerate produced a different assistant message ID | Still allocate a local generation serial | Yes |
| Marker ownership during construction | TRANSIENT / REQUIRES GUARD | Marker can appear before it has an assistant owner | Never accept first raw appearance; require unchanged href plus stabilized owner revalidation | Yes |
| Existing/history marker baseline | SUPPORTED | Reopened messages were present at install with `presentAtInstall: true` and `ownerWasPresentAtInstall: true` | Baseline on attachment and never dispatch baseline markers | Yes |
| Hydrated/history replay exclusion | SUPPORTED for tested reopening flow | Old anchors appeared only as install-baseline evidence | Invalidate on route/document change; no replay | Yes |
| Alternate branch identity | UNKNOWN | Not independently verified beyond regenerate | Disable reactions when current-branch identity is ambiguous | No |
| Generation activity | SUPPORTED on tested version | `stop-button` appeared during generation and disappeared on completion | Report `unknown` if the control is absent or changes | Yes |
| Composer focus/input occurrence | SUPPORTED on tested version | Focus and input events observed structurally | Disable typing signal if unavailable | Yes |
| Send-button occurrence | SUPPORTED on tested version | Send-button event observed | Correlate with route/document state | Yes |
| Enter submit occurrence | SUPPORTED on tested version | Enter event observed | Do not infer submit from draft contents | Yes |
| Regenerate occurrence | SUPPORTED for tested flow | Native Regenerate followed by a distinct assistant message ID | Disable regenerate reactions if correlation fails | Yes |
| Composer empty/nonempty boolean | UNKNOWN | Current composer was approximately `div#prompt-textarea[role="textbox"]`; no allowed empty attribute was found | Do not read draft text; offer Copy context and leave staging unavailable | Yes |
| Persona staging into empty composer | UNKNOWN | Reliable safe empty detection was not established | Capability-gate staging; never overwrite or auto-send | Yes |
| IME submit distinction | UNKNOWN | Not live-verified | Mark IME path unsupported until tested | No |
| PetChats landing to first conversation | SUPPORTED | First native submission moved `/g/:opaque/project` to `/g/:opaque/c/:opaque` | Preserve activation only with same-document submit correlation | Yes |
| Open existing project conversation | SUPPORTED for tested reopening flow | Existing project conversation reopened with baseline messages | Invalidate old document/route state before attachment | Yes |
| Reload behavior | SUPPORTED with reattachment requirement | Reload destroyed the disposable page JS context | Issue a fresh document session and invalidate all old callbacks | Yes |
| Back/Forward edge cases | UNKNOWN | Some edge cases were not live-verified | Invalidate conservatively on uncertain route changes | Partial |
| Global/native New chat preserves PetChats | UNSUPPORTED | Normal New chat navigated to `https://chatgpt.com/` | Never use it for New PetChat | Yes |
| New PetChat starting surface | SUPPORTED | Configured project landing route is usable; native first submission creates the conversation route | Navigate directly to `ChatHomeUrl`; do not claim creation before submission | Yes |

## Reaction evidence and acceptance rule

The marker was tested with short output, long streaming output, Regenerate, and
a second valid intensity. Raw and absolute href values remained exact. The
anchor eventually acquired an assistant owner and bounded opaque message ID.

During DOM construction, however, an anchor can appear before its assistant
owner. Production code must therefore keep the design's stabilization rule:
wait for the complete href to remain unchanged, then re-resolve and revalidate
assistant ownership, message ID, document session, route revision, local
generation serial, epoch, pet, and reaction vocabulary before dispatch.

An adapter attachment must enumerate current assistant IDs and reserved anchors
as a baseline. Baseline items may be hidden later but never emitted as live
reactions. Reopened conversation markers observed at installation proved this
exclusion strategy on the tested version.

## Routing decision

The verified route transition is:

```text
https://chatgpt.com/g/:opaque/project
  -- native user submission -->
https://chatgpt.com/g/:opaque/c/:opaque
```

The ordinary global New chat control navigated to `https://chatgpt.com/` and
left PetChats. `New PetChat` must navigate directly to the configured
`ChatHomeUrl`, present the project landing page, and wait for the user's normal
submission. It must never claim that a project conversation exists before that
submission creates the conversation route.

## Composer and persona staging

Focus, input, Send-button, and Enter occurrences were observed without reading
draft text. Safe structural empty detection was not established for the current
`div#prompt-textarea[role="textbox"]`. T8/T9 must keep composer-empty and persona
staging as false/unknown capability flags until a later privacy-safe method is
verified. Copy context remains the required fallback.

## Reload and document identity

Full Reload destroys page JavaScript state, including the disposable probe.
This is expected and is not a marker-transport failure. A production adapter
must attach a fresh document session after reload/navigation, invalidate the
previous session and route revision, and reject late messages from it.

## Privacy boundary used

The live test observed only the reserved marker href, structural role and
message-ID attributes, baseline membership, boolean/occurrence control events,
generation-control presence, and route shapes. It did not use assistant
`textContent`, `innerText`, serialized HTML, draft text, response bodies,
cookies, storage, clipboard data, React internals, history scraping, or private
ChatGPT APIs. No real conversation DOM snapshot was saved.

## Scope of the GO decision

The **GO** authorizes later implementation of the bounded reaction-link
transport under `docs/contracts/reaction-link-v1.md`. It does not enable the
bridge in T1, authorize a text-scanning fallback, resolve composer staging, or
promise permanent selector stability. Missing identity or capability signals
must disable the affected feature safely.
