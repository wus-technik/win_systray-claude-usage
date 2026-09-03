# Application icon

`speedometer.svg` is the third-party source artwork; `../../src/ClaudeUsageTray/app.ico` is built
from it and is the file the csproj and both `vpk pack` call sites reference. Provenance and licence
live in `../../THIRD-PARTY-NOTICES.md` — read that before reusing or replacing the artwork.

This is the *static identity* icon: the exe in Explorer, Setup.exe, the desktop shortcut, the Start
menu entry and the window title bars. The tray badge is unrelated and still drawn at runtime by
`IconRenderer`.

## Rebuilding `app.ico`

The `.ico` is committed, so a normal build needs none of this. Regenerate it only when the artwork
changes:

```powershell
npm install sharp png-to-ico          # not a project dependency; install where you run this
node assets/icon/build-icon.mjs assets/icon/speedometer.svg src/ClaudeUsageTray/app.ico
```

The script renders the SVG large, trims its empty margin (this artwork is a wide gauge sitting in a
square viewBox — untrimmed it would render as a small shape in a lot of nothing), then writes the
seven sizes Windows asks for: 16, 24, 32, 48, 64, 128, 256. It leaves the `.<size>.png` renders next
to the `.ico` for eyeballing; they are throwaway and not committed. 16 and 24 px fill their canvas
edge to edge because at that size a margin costs more than it buys.
