# PetGPT agent guide

PetGPT is a Windows desktop companion that visually wraps the normal ChatGPT
website. It must not use the OpenAI API or scrape/copy ChatGPT output.

## Stack
- C#
- .NET 8
- WPF
- Microsoft WebView2

## Build
```powershell
dotnet restore
dotnet build
dotnet run
```

## Constraints
- Keep `https://chatgpt.com/` as the actual chat UI.
- No OpenAI API key or API billing.
- Do not automate login or store credentials.
- Do not scrape, parse, or copy conversation output.
- Use persistent WebView2 user data.
- Compact mode is cosmetic and must fail safely.
- Prefer semantic/ARIA/data selectors over generated CSS classes.
- Avoid polling or unnecessary rendering loops.

## Repository map
- `Windows/` — pet and chat bubble
- `Services/` — settings, WebView2, positioning
- `Models/` — app settings
- `Web/` — compact-mode CSS/JS
- `Assets/` — pet graphics
- `ARCHITECTURE.md`
- `docs/product.md`
- `docs/decisions.md`
- `docs/current-state.md`

Read this file first, then only docs relevant to the task.

## Definition of done
- Build succeeds on Windows.
- Existing behavior remains intact unless explicitly changed.
- Runtime failures do not break normal ChatGPT usage.
- Update `docs/current-state.md` after material implementation changes.
