# Decisions

- Embed the normal `https://chatgpt.com/` site, not the OpenAI API.
- Keep a persistent WebView2 profile; PetGPT never handles credentials itself.
- Compact mode is cosmetic only and must not scrape or copy ChatGPT output.
- Use WPF/.NET 8 for the Windows MVP.
- Keep `AGENTS.md` short and place details in task-specific docs.
- Treat `petgpt.invalid` links as bounded visual metadata only. The page bridge
  may observe the reserved raw href plus narrow structural live-turn identity;
  native code reparses and validates it against the current persona, pack,
  document, generation, and turn before emitting the existing animation event.
  Reserved-host navigation/new-window/resource requests are contained locally,
  and same-origin forgery can affect only protocol status or bounded animation.
