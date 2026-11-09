# Knapper landing page: build handoff

> **Status (2026-11-08):** the tasks below are done. The site lives in `site/`
> (the deployable root) and `.github/workflows/pages.yml` publishes it. These
> notes and the design brief sit in `docs/site/` so they aren't published.
> Every JSON, terminal and error string on the page was captured from a real
> dev server over a `tools/dev-vault.sh` vault, with invented notes added
> through the tools. Nothing was written by hand. Still open:
> - S1/S4 are labelled illustrations. Their tool names and error codes are
>   real, but they aren't screen captures.
> - Mobile nav menu; absolute `og:image` URL once a domain exists.
> - Pages must be enabled in the repo settings (Source: GitHub Actions).

This folder is the approved landing page design, exported as a plain static site.
No framework, no build step, no runtime dependencies beyond Google Fonts.

```
index.html      the whole page, one long scroll
styles.css      all styles; design tokens at the top
main.js         theme toggle + copy buttons (≈50 lines, no deps)
assets/         favicons, apple-touch icon, social image
```

Open `index.html` directly or serve the folder with any static server
(`python3 -m http.server`). It is ready to deploy as-is; the tasks below make it
production-final.

## Task for the code agent

1. **Place the site** in the repo, either as `site/` or `docs/`, matching how this repo
   deploys (check `.github/workflows/` for an existing Pages or deploy workflow first and
   follow it; if none exists, add a GitHub Pages workflow using `actions/upload-pages-artifact`
   + `actions/deploy-pages` that publishes this folder). Keep it a static site. Don't
   introduce a framework or bundler unless the repo already has one.
2. **Favicons.** `assets/` holds copies from `assets/icon/` in the repo. Prefer
   referencing the canonical files (`assets/icon/knapper-icon-small-{dark,light}.svg`,
   `assets/icon/png/knapper-tile-180.png`) if the site is served from a path that can
   reach them; otherwise keep the copies.
3. **Remove designer captions.** Every element with class `ph` (small mono captions such
   as "D2 · hero loop…", "S1 · real capture goes here", "S9 · commands illustrative…")
   is a design note, not site copy. Delete them, or keep the S-captions only until the
   screenshot placeholders are replaced.
4. **Make the placeholder content true.** Check each item against the actual code and
   README in this repo and correct it. Do not invent output:
   - S9 quickstart commands (`knapper sample-vault`, `claude mcp add … knapper serve`):
     replace with the real local quickstart.
   - S8 `knapper verify` output in Get started: use the real command name and checks.
   - S10 audit log + `git log` lines (Security section): match the real formats.
   - S5 search JSON (`totalMatches`, `scannedFiles`, `truncated`, `nextCursor`) and the
     S6/S7 lint/receipt JSON: match the real tool response field names.
   - S1 / S4 chat mockups: tool names `vault_edit`, `vault_read`, and the
     `[MutationBlocked]` message text should match what the server really returns.
   - Requirements box: .NET 10, ripgrep 15+, git, Obsidian Sync with `obsidian-headless`,
     Cloudflare Tunnel + Access.
   - Footer links: Docs → README anchor, Licence → actual licence file path.
5. **Keep the guardrails** from the brief: no real vault content, hostnames, IPs or
   personal names; no Obsidian or Anthropic logos; nothing implying a hosted service.

## Page structure (scroll order, with anchor ids)

| # | Section | id | Notes |
|---|---|---|---|
| 1 | Nav | n/a | sticky; links collapse at ≤860px; theme toggle |
| 2 | Hero | `top` | animated race log (CSS keyframes, 16s loop, static under reduced motion) |
| 3 | Name strip | n/a | "knap·per" one-liner |
| 4 | The problem | n/a | three devices, two lost edits |
| 5 | How it works | `how` | D1 topology; flex row → column at ≤1080px |
| 6 | Features | `features` | 8 cards, each a jump link to its section |
| 7 | No blind overwrites + Inside that save | `overwrites`, `write` | D2 race (two panels), S1 chat, D3 10-step pipeline with hover tips |
| 8 | Stops instead of guessing | `stops` | intro + S4 chat, then D4 gate strips full width |
| 9 | Search you can trust | `search` | S5 annotated JSON with numbered callouts |
| 10 | Built for Obsidian | `obsidian` | S6, S7, archive tree |
| 11 | Exact file transfers | `transfers` | D5 three-lane flow |
| 12 | Security | `security` | D6 octagon rings + S10 terminal |
| 13 | Tips for fast agents | n/a | D7 call economics (optional per brief) |
| 14 | Get started | `start` | 3 steps, S9 quickstart, S8 verify, requirements |
| 15 | Footer | n/a | |

Sections alternate background (`.sec` / `.sec.alt`). If you add or remove a section,
re-alternate the ones after it.

## Design system

- **Type:** IBM Plex Sans (text, 400/500/600/700) and JetBrains Mono (code, labels,
  eyebrows, fingerprints). Loaded from Google Fonts in `<head>`; self-host if preferred.
- **Theme:** tokens are CSS custom properties on `.k[data-theme="dark|light"]` at the top
  of `styles.css`. The page is dark by default whatever the system setting
  (changed 2026-11-09). The toggle switches to light and remembers the choice in
  `localStorage` (`knapper-theme`). An inline script
  at the top of `<body>` sets the theme before first paint to avoid a flash.
- **Brand violets (from the mark):** dark UI `#8F78EA / #C9BAFF / #7059CD / #5F49B8`,
  light UI `#5C47AD / #9781E8 / #4A3894 / #3C2C7C`. Logo is inlined as SVG (exact paths
  from `assets/icon/knapper-icon-*.svg`) with fills bound to `--m-*` tokens so it swaps
  per theme.
- **Status colours:** landed `--ok`, refused `--no`, blocked/stale `--wa`. Colour is never
  the only signal: always paired with ✓ / ✕ / ◐ and a word.
- **Shape language:** one chamfered top-right corner (`.cut` 16px, `.cut-s` 9px) via
  `clip-path`. Outlined + chamfered elements draw the diagonal edge with a
  `linear-gradient(225deg, …)` background so the outline doesn't break at the corner.
  If you add a new outlined chamfered element, copy that pattern (see the
  "chamfer edge" block in `styles.css`).
- **Code frames** (`.frame`) stay dark "volcanic glass" in both themes.
- **Breakpoints:** ≤1080px (diagrams go vertical, features 2-up), ≤860px (single
  column, mobile nav, gate strips stack).

## Accessibility already in place

- Diagrams are `<figure>`s with `aria-label` text equivalents; decorative SVGs are
  `aria-hidden`.
- Pipeline steps are real `<button>`s; tips show on hover and keyboard focus, and inline
  on narrow screens.
- `prefers-reduced-motion` disables the hero animation.
- Text contrast checked at ≥4.5:1 in both themes.

## Nice-to-haves (not required)

- A mobile menu for the nav links (currently hidden at ≤860px; only the toggle and
  wordmark show).
- `og:image` as an absolute URL once the site's domain is known.
- Real screenshots for S1–S10 from the synthetic sample vault.
