# Web compatibility validation

## T0 scope

T0 creates this evidence record only. It did not inspect the authenticated
ChatGPT DOM, execute a compatibility probe, read conversation output, or change
WebView behavior. The normal `https://chatgpt.com/` UI remains the chat surface.

## Questions for future T1 validation

T1 must gather live evidence before any web-dependent feature is enabled:

- Which exact ChatGPT origins and route forms appear during login, project
  landing, individual chats, history navigation, and new-chat navigation?
- Can origin checks reliably prevent PetGPT styling or bridge code from running
  on authentication and unrelated pages?
- Do current semantic, ARIA, or stable data attributes identify the composer,
  generation state, assistant turn, current branch, and route without reading
  message text?
- Does a reserved Markdown link retain its exact `href` while rendering and
  streaming, including its final terminator?
- Can a marker be associated with the current assistant turn without inspecting
  `textContent`, `innerText`, network bodies, clipboard data, or accessibility
  text containing conversation output?
- Can recognized marker links be prevented from navigating or reaching the
  network while ordinary links keep native behavior?
- Do SPA route changes or root replacements require an event-driven adapter to
  re-establish narrow observers without polling?
- Which capabilities fail safely when selectors or route assumptions change?
- Does compact mode preserve readable native ChatGPT behavior when every
  optional enhancement is unavailable or disabled?

## T1 evidence template

For each question, record:

- Windows and WebView2 Runtime versions;
- route/origin tested, without credentials or private chat identifiers;
- exact structural capability observed;
- pass, fail, or unavailable;
- privacy boundary used to gather the result;
- safe fallback when the capability is absent.

If narrow marker rendering or current-turn identity cannot be proven, model
reactions must remain disabled. T1 must not substitute broad response scraping,
conversation parsing, or network interception.
