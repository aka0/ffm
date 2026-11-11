# Handoff log

Shared log for everyone editing this repo (Arthur, ChatGPT, Grok Bot). It keeps us from overwriting each other's work.

## Rules

1. **Before you start:** run `git pull`, read the latest entries below, run `git status`, and re-read any file you plan to edit from disk. Don't rely on an earlier copy.
2. **Only one editor at a time.** If someone else's work is uncommitted, ask Arthur before touching those files.
3. **When you finish:** commit your change, push, and add an entry at the top of the log (newest first) listing the files changed, what changed, and anything left open.
4. **Keep edits small and targeted.** Don't reformat or rewrite whole files you didn't otherwise need to change.

## Log

### 2026-11-11 15:56 PT, ChatGPT / Codex — IN PROGRESS, hold audit
- Arthur reported that combined clipboard paste omitted the portrait in ChatGPT and that the Paste edited image label is clipped.
- Codex is the active editor for `PortraitClipboard.cs`, the main-window controls/handlers, related checks and docs. Please wait for the next **DONE / ready for audit** entry before editing or auditing the finished fix.
- Starting from clean `main` at `bbfd1b6`; `git pull --ff-only` was already up to date. Grok's icon settings remain preserved.

### 2026-11-11 15:50 PT, ChatGPT / Codex — ready for Grok audit
- Ran `git pull --ff-only` (already up to date), read this log, and re-read files before finishing. The existing dirty changes were this chat's in-progress implementation. This entry and the implementation are committed together; no other editor's uncommitted work was included.
- Main-screen workflow: **Copy portrait + prompt** writes PNG, bitmap, text and HTML clipboard formats together. **Paste edited image**, Ctrl+V, and drag/drop stage the result for review. The original image can be dragged into the browser. File import, prompt-only copy and the optional handoff dialog live under **More options**. No provider API or browser authentication automation is used.
- Edge handling: strengthened the PNG/transparency/no-halo prompt; avoid exposing undefined transparent RGB as black when restoring alpha. Optional **Reduce edge halos** restores the outer two cutout pixels and blends the third. It snapshots the earliest readable sibling backup with the same dimensions/alpha for boundary colors when available. Interior colors are not globally filtered; cleanup can be toggled on an imported preview. History restoration stages with cleanup off.
- Clipboard/link handling: native PNG preferred, bitmap/file/inline image and public HTTPS image links supported; 32 MB input cap, bounded stream reads and redirects, no forwarded browser credentials. Private/blob-only links explain the Copy image/file fallback. The app's own original clipboard is marked so it cannot be accepted as a finished edit by mistake.
- File-operation follow-through from the prior baseline: shared-image checks include sibling facepacks under the enclosing graphics directory, including real-player references. Export snapshot tampering is rejected. Numbered backups, shared-image isolation, stale-file detection, rollback and startup recovery remain in place.
- Files changed: `src/FFM.App/MainWindow.xaml`, `MainWindow.xaml.cs`, `HandoffWindow.xaml`, `HandoffWindow.xaml.cs`; services `PortraitClipboard.cs` (new), `PortraitEdgeCleanup.cs` (new), `PortraitEditSession.cs`, `PortraitCommitService.cs`; `src/FFM.EditCheck/FFM.EditCheck.csproj`, `Program.cs`, `ImageTransferChecks.cs` (new); `README.md`, `docs/browser-handoff.md` (new), and this log. Grok's `app.ico`, project icon/resource settings and main-window `Icon="app.ico"` are preserved.
- Validation: `dotnet build src/FFM.App/FFM.App.csproj --no-restore` passed with **0 warnings / 0 errors**. `dotnet run --no-restore --project src/FFM.EditCheck` passed **45 checks**, including combined clipboard formats/HTML offsets, image input variants and mocked HTTP failures, synthetic black/red boundary artifacts, interior colors/alpha, backup/reference recovery, shared real-player protection, rollback and stale inputs. Fixtures only: no real facepack/save writes, system clipboard changes or live HTTP calls by these checks. Rebuilt app launched; new controls and icon observed.
- **Audit/open items:** actual ChatGPT/Grok browser paste behavior is not guaranteed by publishing multiple clipboard formats; verify one-paste image+prompt in supported browsers. Prompt-only copy is the explicit fallback. Verify real browser drag payloads, cleanup on actual generated results, and high-DPI layout. Image identity/pose/clothing preservation still needs user review. Cleanup keeps the old silhouette, so expanded hairstyles can be clipped; the two-pixel reference strip may need toggling off for a particular edit. The source snapshot is preserved; the current preview only reaches the facepack on Accept.
- Existing reader limitations remain: 1,201 newgen names/ages unresolved on the validation save; readable club/nationality and packaging still outstanding. Please pull this commit before auditing; this chat is releasing the editor role after push.

### 2026-11-11 15:15 PT, Grok Bot
- Added app icon `src/FFM.App/app.ico` (16 to 256 px, teal #147C73 with soccer ball and "FFM").
- `src/FFM.App/FFM.App.csproj`: added `<ApplicationIcon>app.ico</ApplicationIcon>` and `<ItemGroup><Resource Include="app.ico" /></ItemGroup>`.
- `src/FFM.App/MainWindow.xaml`: added `Icon="app.ico"` to the Window tag.
- Created this file and made the baseline commit of the whole repo as it stood, including in-progress work.
- Not built yet; next build should confirm the icon shows on the exe and title bar.
