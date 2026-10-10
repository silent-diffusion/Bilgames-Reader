# Bilgames Reader — Design Handoff

On-device EPUB → audiobook app. Runs on **Windows**, touch-first, and must adapt
fluidly from a phone-width window to a full desktop window (also suited to
tablets and foldables such as iPad / iPhone Duo).

Live design canvas (interactive prototype): https://claude.ai/artifact/BSkUysKgupWV1PXfyavFNe
(private to the owner unless shared)

## Folder contents

| Path | What it is |
|---|---|
| `README.md` | This file — the build spec. Start here. |
| `tokens.json` | Colors, type, radii, spacing, touch sizes, breakpoints, pane widths. |
| `product-spec.html` | The product requirements (features, pipeline, privacy, MVP plan). |
| `screens/AppShell.dc.html` | **Source of truth for the adaptive app.** One layout that switches between compact / medium / expanded. |
| `screens/Phone.dc.html`, `screens/Tablet.dc.html` | Thin wrappers that render AppShell forced to compact (390×844) and medium (1000×760). |
| `screens/Main, Import, Cast, Review, Generate, Player .dc.html` | Earlier detailed phone screens (390×844). Same visual language; useful for per-screen detail. Where they differ from AppShell, **AppShell wins**. |
| `screens/canvas.json` | Canvas layout metadata for the design tool. Ignore for building. |

### Reading the `.dc.html` files
They are HTML files for a design tool. Read them as plain HTML + inline CSS:
- All styling is inline `style="…"`; values there are exact.
- `{{name}}` = a value computed in the `<script type="text/x-dc">` class at the
  bottom (`renderVals()`); look it up there. Layout-dependent values (pane
  widths, display on/off, nav orientation) are all computed there per layout.
- `<sc-for list="{{items}}" as="x">` = repeat per item; `<sc-if>` = conditional.
- `<dc-import name="AppShell">` = embeds AppShell.
- `support.js`, `<x-dc>`, `<helmet>` are design-tool plumbing — ignore.

Do not ship these files; rebuild the UI in the chosen framework.

## Platform guidance
Windows target. Any framework that can switch layout by **window width** works:
WinUI 3 (VisualStateManager / AdaptiveTrigger), .NET MAUI, or Flutter
(LayoutBuilder). Flutter/MAUI keep the door open for iPad/iPhone later.
Keep touch targets ≥ 44 px at every size (Windows tablets are touch devices),
and also support mouse hover, keyboard focus rings and shortcuts.

## Visual language
- Dark theme only (for now). Ground `#0E1116`, cards `#171B22`, hairlines `#1F242D`.
- One accent: amber `#F0A848`, text on accent `#1A1205`. Accent marks the primary
  action, current nav item, progress, and the selected option.
- Two typefaces: **Manrope** for UI, **Literata** for anything that is book
  content (titles, passages, transcript).
- Book covers are typographic placeholders: solid color block + title in
  Literata, author in tiny uppercase. Real cover images can replace them.
- Each character gets a speaker color + initials avatar. Color is never the
  only signal — always pair with name/initials.
- Warnings (merge suggestion, thermal) use the warm amber-brown panel:
  bg `#2A2312`, border `#4A3B17`, text `#F3E3C3`.
- Icons: 24-px-grid stroke icons, 1.8 px stroke, round caps/joins.
  (Fluent System Icons "Regular" are a good real-world substitute on Windows.)
- No emoji, no gradients, no drop shadows beyond hairline borders.

## Adaptive layout (the key requirement)

Breakpoint is the **app window's** width (Windows effective pixels):

| Class | Width | Navigation | Panes |
|---|---|---|---|
| Compact | < 640 | Bottom tab bar (icon over label) | One pane at a time. Mini-player bar above tabs when the player isn't open. |
| Medium | 640–1007 | Left rail, 92 px (icon over label) | Two equal panes: Library list (left) + Book detail **or** Player (right). Mini-player across the bottom when the player isn't open. 50/50 split so it lines up with a foldable's hinge. |
| Expanded | ≥ 1008 | Left sidebar, 248 px (icon + label in a row), app name on top, offline + storage card at bottom | Center pane: Library (card grid + search) or Book detail. Right pane: **Now Playing, always visible**, 360 px. No mini-player. Keyboard hints shown. |

Resizing must be live and lossless: current section, tab, selection, playback
state all persist across layout changes.

### Navigation model
State: `section ∈ {library, book, player}`, `bookTab ∈ {structure, cast, review, generate}`, `selectedBook`.

Nav items (same four everywhere):
1. **Library** → section = library
2. **Listening** → section = player
3. **Voices** → section = book, tab = cast
4. **Jobs** → section = book, tab = generate

Pane visibility:
- Compact: show only the pane matching `section`.
- Medium: Library always; right pane = Player if section = player, else Book.
- Expanded: center = Library if section = library, else Book; Player always on the right.

Compact Book pane shows a "‹ Library" back button; compact/medium Player has a
collapse (chevron-down) button returning to Library. In medium, the selected
book row in the list is highlighted.

## Screens / panes

**Library**
- Title "Library" + subtitle "N books · nothing leaves this device".
- Primary "Import EPUB" pill (opens system file picker → Book › Structure).
- Expanded only: search field (280 px) with `Ctrl K` hint.
- "Continue listening" card: cover, title, chapter + time left, and generation
  progress ("Generating chapter 14 of 61", %, bar). Tap → Player.
- All books: list rows in compact/medium; card grid
  `repeat(auto-fill, minmax(300px, 1fr))` in expanded. Each row: cover, title,
  author · length/status text, status badge (Ready = green, Needs review / Generating = amber, Imported = grey).

**Book detail** — header (cover, title, "author · chapters · voices") and a
4-segment control: Structure / Cast / Review / Generate. Content max width ~760 px.
- *Structure*: checklist of what to read aloud (front matter, preface, chapters,
  chapter headings, back matter) with stats; footnotes segmented control
  (Inline / End of chapter / Skip); "Find speakers" primary button. Phone
  version also shows est. audio length, storage, and "Uploaded: Nothing".
- *Cast*: merge suggestion banner ("Lizzy" and "Miss Eliza" look like
  Elizabeth Bennet — Merge / Keep apart); cast rows (avatar, name, line count +
  confidence, voice dropdown, preview ▶) in a `minmax(300px, 1fr)` grid;
  dashed card "37 lines have an unsure speaker… read by the narrator for now" → Check them.
- *Review*: progress "Chapter 1 · 3 of 37"; context lines above with speaker
  labels and delivery cues (e.g. "impatient"); the uncertain line in an
  accent-bordered card with ranked speaker choices (name + % likely, Narrator
  as fallback), "Use this for similar back-and-forth lines" checkbox, Skip /
  Confirm · next line.
- *Generate*: big % + time left, bar, chapter & segment counts; quality
  segmented control (Fast / Balanced / High quality with sublabels); thermal
  warning panel; chapter list with status dots (ready / generating / queued);
  storage line; "Resumes if the app closes"; Pause + "Listen to ready chapters".

**Player / Now Playing**
- Header "Now playing · Chapter N", chapters list button.
- Cover + title + author.
- "Now speaking" chip: speaker avatar, "Elizabeth · voice Mira", **Fix** button
  (jumps to correcting that line's speaker).
- Live transcript: current paragraph in full text color, neighbors dimmed (`#6B7482`).
- Scrubber with elapsed / remaining (tabular numbers).
- Controls: back 15 s, play/pause (72 px accent circle), forward 30 s.
- Row: speed (cycles 0.8 / 1.0 / 1.25 / 1.5 / 2.0×), Sleep timer, Export.
- Expanded only: hint "Space play/pause · ← → skip · [ ] speed".

**Mini-player** (compact & medium when Player hidden): small cover, title,
"Chapter 3 · Elizabeth speaking", 44 px play/pause. Tapping the body opens Player.

## Interaction & accessibility
- Real buttons/links/inputs; segmented controls expose pressed/selected state.
- Icon-only buttons need accessible names (e.g. "Back 15 seconds").
- Text contrast ≥ 4.5:1 against its background (the token pairs above meet it).
- Keyboard (expanded/desktop): Space play/pause, ←/→ skip, [ / ] speed,
  Ctrl+K search, full tab focus order matching visual order.
- Long lists scroll inside their pane; panes never overflow the window.

## Sample content
Sample books are public-domain titles (Pride and Prejudice, Moby-Dick, Little
Women, Frankenstein, Dracula). All numbers (chapters, line counts, sizes,
percentages, voice names like Ada/Mira/Rowan) are placeholder data.
