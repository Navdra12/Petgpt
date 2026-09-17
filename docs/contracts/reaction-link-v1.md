# Reaction link contract v1

Status: draft supported by PetGPT v2 T1 live compatibility evidence dated
2026-09-17. Production implementation is deferred to its planned task.

## Purpose and authority

A reaction link is bounded visual metadata emitted by an assistant response.
An accepted link may select one validated local character reaction and
intensity. It cannot invoke commands, navigate, send prompts, select a pet,
access files or credentials, change settings, or exit PetGPT.

## Marker form

The assistant emits a standalone Markdown link whose rendered anchor has this
exact href grammar:

```text
https://petgpt.invalid/#r1/<epoch>/<petId>/<reactionId>[/<intensity>]/end
```

```text
epoch       = exactly 16 lowercase hexadecimal characters
petId       = [a-z][a-z0-9_]{0,31}
reactionId  = [a-z][a-z0-9_]{0,31}
intensity   = canonical decimal integer 0..100, no leading zero except "0"
```

Example:

```markdown
[·](https://petgpt.invalid/#r1/6c73a04a8842e90b/trixie/smug/65/end)
```

The href is at most 192 ASCII characters. `/end` is mandatory. Query strings,
whitespace, percent encoding, ports, user information, extra segments,
alternate origins, trailing slashes, floats, noncanonical integers, and Unicode
lookalikes are invalid. Missing intensity uses the reaction's declared default;
out-of-range intensity is rejected rather than clamped.

## Page-side observation boundary

The page adapter may read only:

- reserved marker anchor `href` attributes;
- `data-message-author-role="assistant"` on the tested adapter surface;
- a bounded opaque `data-message-id` on the tested adapter surface;
- required current-branch structural attributes when available;
- structural generation, submit, and regenerate occurrences needed for live
  turn correlation.

These selectors are tested compatibility assumptions, not public ChatGPT APIs.
The adapter must expose false/unknown capability flags and disable reactions if
required identity cannot be established.

The adapter must not read response `textContent`, `innerText`, `innerHTML`,
Markdown copies, network bodies, React internals, clipboard data, cookies,
storage, user draft text, or conversation history.

## Eligibility and stabilization

A reserved anchor is not eligible merely because it appeared. T1 observed that
an anchor can exist temporarily before acquiring its assistant owner.

Before dispatch, the adapter must:

1. require the complete href, including `/end`, to remain unchanged for the
   configured stabilization interval (initially 150 ms);
2. re-resolve the anchor in the current document after that interval;
3. revalidate assistant ownership and a bounded opaque message ID;
4. prove that the message was not present in the attachment baseline;
5. correlate it to a locally observed current send or regenerate serial;
6. require the current document session, route revision, activation epoch, pet
   ID, and character reaction vocabulary;
7. reparse and validate the original href in the native host.

Failure of any step rejects the candidate. The first raw anchor appearance must
never dispatch a reaction.

## History and duplicate exclusion

At attachment, enumerate existing assistant message IDs and reserved marker
anchors into a structural baseline. Baseline/history markers never dispatch,
including when an existing conversation is reopened or history is hydrated.

Accept at most one reaction per document session + route revision + local
generation serial + assistant message ID. Regenerate allocates a new local
generation serial; on the tested surface it also produced a different
`data-message-id`. Duplicate markers in one eligible turn are ignored.

If the current branch or live-turn relationship is ambiguous, disable reaction
dispatch for that page rather than guessing.

## Document and route lifecycle

Reload and full navigation destroy the page adapter context. Before navigation,
invalidate pending reactions and the previous document session. After the new
document is ready, create a fresh document session, route revision, capability
state, and structural baseline. Reject late messages from older sessions.

The tested PetChats route transition is:

```text
https://chatgpt.com/g/:opaque/project
  -- native user submission -->
https://chatgpt.com/g/:opaque/c/:opaque
```

The ordinary global New chat control is outside this contract and was observed
to leave PetChats.

## Generation signal

On Edge/WebView2 153, visible `stop-button` presence during generation and its
disappearance at completion provided a usable structural signal. Treat this as
a capability, not a permanent selector promise. If unavailable, report
generation as `unknown`; a valid marker may still be processed only when all
other live-turn requirements hold.

## Host validation

The native host is authoritative. It must validate exact source/top-level
origin, document session, route revision, message schema and size, bounded
opaque IDs, local generation serial, full href grammar, active epoch and pet,
and exact reaction vocabulary membership. A syntactically valid unknown
reaction is ignored; no nearest-reaction mapping exists.

Native guards must cancel navigation/new windows to `petgpt.invalid` and return
an empty local response for resource requests to that exact reserved host.
Never pass a marker href to the shell.

## Compatibility result

T1 live testing on authenticated ChatGPT in PetGPT WebView2 / Edge 153
confirmed exact href preservation for short, long/streaming, regenerated, and
second-intensity responses; assistant ownership and opaque message identity;
history baseline exclusion; and a structural generation signal. This is a
versioned evidence point, not a permanent guarantee. Revalidation is required
when the adapter's structural assumptions change.
