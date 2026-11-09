# Knapper product site: design brief

**What this is:** a brief for layout prototyping of a product site for
Knapper. We want page structure, visual direction, and diagram and screenshot
treatments. Final copy and full technical detail come after a design is
chosen. All copy here is draft, written to show length and intent. Treat
screenshots and diagrams as labelled placeholders.

**What we want back:**

- One long-scroll landing page, desktop and mobile.
- Light and dark themes.
- A visual language for the diagrams (they appear throughout).
- A styled treatment for terminal output and JSON snippets.

---

## 1. The product

Knapper is an always-on server that gives AI agents (Claude on the web,
desktop, mobile, and Claude Code) safe read and write access to an Obsidian
vault. People keep using the normal Obsidian apps and Obsidian Sync. Agents
go through Knapper only, and Knapper makes sure no agent can overwrite a
change it hasn't seen, that every write is checked on disk, and that
anything unsafe stops instead of guessing.

It is open source, self-hosted, and built for one person's vault rather than
a team SaaS.

## 2. Audience

- **Primary:** technical Obsidian users who already let AI agents work in
  their vault and have lost an edit (or worry they will). They are
  comfortable self-hosting a Linux service.
- **Secondary:** people building MCP servers, interested in how to do safe
  writes properly. The engineering-rigor story is for them.

They are skeptical and detail-oriented, and reliability claims alone won't
convince them. The site should be calm, concrete and confident. Show the
mechanism, don't hype it.

## 3. Tone and voice

- Plain-spoken, a little dry, precise. Short sentences.
- Concrete over abstract: "the agent gets an error and re-reads" beats
  "robust conflict handling".
- Honest about limits and requirements; that honesty is part of the pitch.
- Avoid: "revolutionary", "seamless", "AI-powered", stock AI imagery (glowing
  brains, robots), and gradients standing in for meaning.

## 4. Brand

**Name story.** A *knapper* shapes obsidian (the volcanic glass) into tools
by striking flakes off it. Knapper shapes an Obsidian vault into tools for
AI agents. The pun is worth one line near the top, not a whole section.

**Mark.** A knapped obsidian point, tip up, with five flake facets struck
from the tip. Assets exist (we'll attach them):

| File | Use |
|---|---|
| `knapper-icon-light.svg` / `-dark.svg` | Primary mark, per theme |
| `knapper-icon-small-light.svg` / `-dark.svg` | Simplified 3-plane mark, 20px and below |
| `knapper-icon-mono.svg`, `knapper-icon-mono-cut.svg` | Single-colour silhouette (`currentColor`) |
| `knapper-wordmark-light.svg` / `-dark.svg` | Mark + "Knapper" lockup |
| `knapper-tile.svg`, `png/knapper-tile-*.png` | App tile / favicon / apple-touch-icon |

Rules: never rotate the mark (tip points up), keep 25% clear space, use the
small variant below 20px, and use the light variant on light backgrounds
(not the dark one faded).

**Palette (from the mark).** Violet family:

| Role | Dark UI | Light UI |
|---|---|---|
| Body / primary accent | `#8F78EA` | `#5C47AD` |
| Highlight facet | `#C9BAFF` | `#9781E8` |
| Shadow facet | `#7059CD` | `#4A3894` |
| Deep facet | `#5F49B8` | `#3C2C7C` |

Neutrals, success, warning and error colours are open. The diagrams need a
clear "safe/landed" colour and a "blocked/refused" colour that sit alongside
the violet.

**Visual direction (suggestion, open to alternatives).** Faceted, crisp,
geometric: flat planes and sharp edges echoing the knapped stone, rather
than soft blobs. Dark theme close to volcanic glass (near-black with a violet
cast). Generous whitespace. Monospace is a first-class typeface here, because
a lot of the proof is terminal output and JSON.

**Typography:** undecided. A clean sans for text and a monospace with a good
`0`/`O` distinction for code.

---

## 5. Page structure

The sections below are in scroll order. Each one lists its goal, draft copy,
and the visual it needs. Diagram and screenshot IDs (D1, S1, …) are specified
in §6 and §7.

### 5.1 Nav

Wordmark at left. Links: How it works · Features · Security · Get started ·
GitHub. Theme toggle.

### 5.2 Hero

- **Goal:** what it is and why it matters, in five seconds.
- **Headline options:**
  - "Let AI agents into your Obsidian vault without losing a note."
  - "Your vault, safe to share with agents."
  - "One careful door into your Obsidian vault."
- **Subhead:** "Knapper is a self-hosted MCP server that gives Claude and
  other agents read and write access to your Obsidian vault. Every write is
  checked, nothing gets overwritten blind, and anything unsafe stops instead
  of guessing."
- **CTAs:** "Get started" (primary), "View on GitHub" (secondary).
- **Visual:** D1 (topology) in simplified form, or a looping animation of D2
  (the race). Designer's choice; we lean towards the race.
- **Small print under CTAs:** "Open source · Self-hosted · Works with Obsidian
  Sync".

### 5.3 The problem

- **Goal:** name the pain readers already know.
- **Draft:** "Give a few agents access to your vault (Claude on your laptop,
  Claude Code on another machine, Claude on your phone) and they all edit the
  same files through Sync. Whoever saves last wins. Edits vanish quietly, and
  nobody tells you."
- **Visual:** a small three-device illustration with one note being
  overwritten, or a short "before" panel that sets up D2.

### 5.4 How it works

- **Goal:** the architecture in one picture.
- **Draft:** "You keep using Obsidian and Obsidian Sync. Knapper runs next to
  a synced copy of your vault, and agents talk only to Knapper. Many agents
  editing at once becomes one server handling one change at a time."
- **Visual:** D1 (topology), full version.

### 5.5 The signature moment: no blind overwrites

- **Goal:** the single most persuasive idea on the page.
- **Draft:** "Every edit carries a fingerprint of the version the agent
  read. If the note changed in the meantime, the edit is refused, the agent
  re-reads, and it tries again. There is no 'just overwrite it' tool to
  misuse."
- **Visual:** D2 (the race), side-by-side "without Knapper / with Knapper",
  plus S1 (the agent recovering in a real chat).

### 5.6 Feature grid

Eight cards. Each has an icon, a headline and one or two lines. Possibly a
"learn more" that expands later.

| # | Headline | Line |
|---|---|---|
| 1 | No blind overwrites | Every edit proves it's working on the latest version. |
| 2 | Surgical edits | Agents change the paragraph they mean, not the whole file. Guard text must survive every edit. |
| 3 | Search you can trust | "No results" means the whole scope was searched. Totals are never guessed. |
| 4 | Stops instead of guessing | A Sync conflict or unhealthy sync blocks writes until you look. Reads keep working. |
| 5 | Every write checked | Atomic writes, re-read and byte-compared afterwards. Soft deletes, an audit log and git history. |
| 6 | Speaks Obsidian | Wikilinks, link checking, your own writing conventions, archived folders. |
| 7 | Exact file transfers | Big or binary files move by a one-time link and a hash check, never through the model. |
| 8 | Private by design | No open ports. Cloudflare Access at the edge, checked again by the server. Notes are data, never instructions. |

### 5.7 Life of a write

- **Goal:** let the technical reader see the mechanism.
- **Draft:** "Every change follows the same path, in the same order."
- **Visual:** D3 (write pipeline). Optionally interactive: hovering a step
  shows a one-line explanation.

### 5.8 Stops instead of guessing

- **Goal:** turn "it refuses things" into a feature.
- **Draft:** "When Obsidian Sync creates a conflict copy, or sync stops
  working, Knapper blocks writes to the affected notes until a human sorts
  it out. Agents never pick a winner. Reading keeps working the whole time."
- **Visual:** D4 (gates), plus S4 (the `MutationBlocked` message in a chat).

### 5.9 Search you can trust

- **Goal:** explain the completeness promise.
- **Draft:** "Search is built on ripgrep and every answer says how complete
  it is. `truncated: false` means every file in scope was searched. If
  results are cut off, a cursor picks up exactly where they stopped, with
  nothing skipped or repeated."
- **Visual:** S5, an annotated JSON response with callouts on `truncated`,
  `scannedFiles`, `totalMatches`, `nextCursor`. This is a good place for the
  code-block styling to shine.

### 5.10 Built for Obsidian

- **Goal:** show it understands vaults, not just files.
- **Content:** three small items:
  - **Link checking:** finds broken wikilinks and heading anchors, and only
    reports what's new since you last accepted the list. (S6)
  - **Your conventions:** tell agents your house rules (wikilinks only, no
    new tags, where new notes go) and get a warning when a write breaks one.
    (S7)
  - **Archived folders:** files can be added to an archive, but nothing
    already in it can be changed.

### 5.11 Exact file transfers

- **Goal:** a clever feature, briefly.
- **Draft:** "Need a PDF or image byte for byte? The agent asks Knapper for
  a one-time link, its shell downloads the file with `curl`, and the hash is
  checked before the file lands. Uploads pass the same checks as any other
  write, plus a scan for leaked secrets. Off by default."
- **Visual:** D5 (transfer flow).

### 5.12 Security

- **Goal:** reassure, briefly. This vault holds someone's whole life.
- **Draft:** "Your vault never listens on your network. Agents reach it
  through a Cloudflare Tunnel behind Cloudflare Access, and Knapper checks
  the Access token again itself, so a mistake at the edge doesn't expose
  your notes. Paths are confined to the vault. Every action is logged with
  who made it. A read-only mode lets you start cautiously."
- **Visual:** D6 (security rings).

### 5.13 Proof

- **Goal:** credibility for the skeptical reader.
- **Stat strip (placeholders, to be confirmed):** "700+ tests" · "18 tools"
  · "5 review rounds" · "Running daily in production".
- **Lessons cards (3–4):** short stories of real silent failures it now
  guards against. Draft titles:
  - "Sync said 'fully synced'. It wasn't." (files over Sync's size limit are
    silently refused)
  - "The search that found nothing because it never looked."
  - "The tool description that got cut off mid-sentence."
- **Visual:** S8 (`knapper verify` all green) as a terminal panel.

### 5.14 Tips for fast agents (optional, may move to docs)

- **Draft:** "Knapper does about 12 ms of work per call. The round trip
  through the chat app is about 3 seconds. So agents that batch reads and
  writes feel fast, and Knapper tells them to."
- **Visual:** D7 (call economics bar).

### 5.15 Get started

- **Goal:** an honest path in.
- **Three-step path:**
  1. **Try it locally:** generate a sample vault and connect Claude Code.
     Nothing real involved.
  2. **Deploy read-only:** point it at your vault, with agents able to look
     but not touch.
  3. **Turn on writes:** when you trust it.
- **Requirements box:** Linux host · Obsidian Sync subscription with
  `obsidian-headless` · Cloudflare Tunnel + Access · .NET 10, ripgrep 15+,
  git.
- **Visual:** S9 (a short terminal snippet for step 1).

### 5.16 Footer

Wordmark, GitHub, docs, licence, name-origin line ("A knapper shapes
obsidian into tools.").

---

## 6. Diagram specs

One consistent style across all diagrams: same stroke weight, node shapes and
colour meaning. Violet = Knapper, neutral = everything else, green = landed or
safe, red/amber = refused or blocked. They must read in both themes and
reflow or simplify on mobile.

**D1. Topology.** Left: people's devices (laptop, phone) with Obsidian apps,
connected via an "Obsidian Sync" cloud to a server box. Right: agents
(claude.ai, Claude Desktop, Claude mobile, Claude Code). Agent arrows pass
through a "Cloudflare Access" gate into Knapper. Inside the server: Knapper
next to a vault folder, with small attached tags for "audit log" and "git
history". The core point: people go through Sync, agents go through Knapper,
and both meet at one vault.

**D2. The race (signature).** Two-column sequence diagram.

- *Without Knapper:* Agent A reads note v1 and Agent B reads note v1. A saves
  v2. B saves its v2 on top, and A's change is gone (red).
- *With Knapper:* A and B read v1 (with fingerprint `#a1`). A saves with
  `#a1` and it lands as v2. B saves with `#a1` and gets "changed since you
  read it". B re-reads v2, saves with the new fingerprint, and it lands as v3
  with both changes (green).
- Candidate for a short looping animation in the hero.

**D3. Life of a write.** A horizontal pipeline of steps: Lock → Fresh read →
Fingerprint check → Apply edit → Check guards → Log intent → Write to hidden
temp → Atomic swap → Re-read and compare → Unlock. A refusal branch drops
down from "Fingerprint check" and "Check guards" with "nothing written".
Wraps to vertical on mobile.

**D4. Gates.** A state view. A note is "writable". A conflict copy appears,
writes become "blocked" while reads stay "OK". A human reconciles, and the
note is writable again. A second, smaller strip: sync heartbeat healthy →
stale → writes blocked.

**D5. Transfer flow.** Three lanes: Model, Agent shell, Knapper. The model
asks for a grant, and Knapper returns a one-time link. The shell fetches the
file with `curl` and checks the hash, and the file lands. A dashed line
labelled "file bytes never pass through the model" runs alongside.

**D6. Security rings.** Concentric rings from outside in: Cloudflare Access
→ Knapper token check → Host check → Path confinement → Vault. Label each
ring with one short phrase.

**D7. Call economics.** Two horizontal bars on the same scale: "Knapper's
work ~12 ms" (a sliver) against "Round trip through the chat app ~3 s"
(full width). Under them: "3 separate edits = 6 calls" against "1 batched
read + 1 batched write = 2 calls".

---

## 7. Screenshot placeholders

All real screenshots will come from a synthetic sample vault, never a real
one. For prototyping, use framed placeholders with these captions. Frames:
"chat" (claude.ai/Desktop style), "terminal", "phone" and "code" (JSON),
each needing a frame style.

| ID | Frame | Shows | Section |
|---|---|---|---|
| S1 | Chat | Agent's edit refused ("changed since you read it"), agent re-reads and succeeds | 5.5 |
| S2 | Chat | Agent asked to find notes and add a summary; tool calls visible; edit lands | Hero or 5.4 |
| S3 | Phone | The edited note appearing in Obsidian mobile moments later | 5.4 |
| S4 | Chat | Write blocked because of a Sync conflict copy; agent reports it to the user | 5.8 |
| S5 | Code | Search response JSON with annotations on completeness fields | 5.9 |
| S6 | Code/terminal | Link-check results listing a broken wikilink | 5.10 |
| S7 | Code | Write receipt with a convention warning | 5.10 |
| S8 | Terminal | `knapper verify` checklist, all green | 5.13 |
| S9 | Terminal | Two-line local quickstart | 5.15 |
| S10 | Terminal | Audit log one-liner showing who changed what, plus a `git log` of vault commits | 5.12 or 5.13 |

---

## 8. Layout and interaction notes

- Single long-scroll page. Sections alternate between full-bleed diagrams
  and text-plus-visual splits.
- Mobile first-class: diagrams simplify or go vertical, and the feature grid
  goes to one column.
- Light and dark themes, both following the system setting, with a toggle.
  The mark swaps variant per theme.
- Code and terminal blocks: consistent framing, copy button, and annotation
  callouts usable on JSON.
- Motion: restrained. At most the hero race animation and gentle diagram
  reveals. Respect reduced-motion.
- Accessibility: diagrams need text equivalents, and colour is never the only
  signal for landed versus refused (pair it with icons or labels).

## 9. Constraints

- No real vault content, hostnames, IP addresses or personal names anywhere
  in prototypes. Use invented note titles ("Trip planning", "Reading list",
  "Garden log").
- Don't imply a hosted service or signup. It's self-hosted and open source.
- Don't use Obsidian's or Anthropic's logos as decoration. Plain text
  references and client-style frames are fine.

## 10. Open questions for later

- Final headline and tagline.
- Typography choices.
- Whether 5.14 (call economics) stays on the landing page or moves to docs.
- Whether the hero visual is the topology or the animated race.
- A dedicated docs/install section on the site, or link out to the repo.
