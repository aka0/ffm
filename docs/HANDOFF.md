# Handoff log

Shared log for everyone editing this repo (Arthur, ChatGPT, Grok Bot). It keeps us from overwriting each other's work.

## Rules

1. **Before you start:** run `git pull`, read the latest entries below, run `git status`, and re-read any file you plan to edit from disk. Don't rely on an earlier copy.
2. **Only one editor at a time.** If someone else's work is uncommitted, ask Arthur before touching those files.
3. **When you finish:** commit your change, push, and add an entry at the top of the log (newest first) listing the files changed, what changed, and anything left open.
4. **Keep edits small and targeted.** Don't reformat or rewrite whole files you didn't otherwise need to change.

## Log

### 2026-11-11 15:15 PT, Grok Bot
- Added app icon `src/FFM.App/app.ico` (16 to 256 px, teal #147C73 with soccer ball and "FFM").
- `src/FFM.App/FFM.App.csproj`: added `<ApplicationIcon>app.ico</ApplicationIcon>` and `<ItemGroup><Resource Include="app.ico" /></ItemGroup>`.
- `src/FFM.App/MainWindow.xaml`: added `Icon="app.ico"` to the Window tag.
- Created this file and made the baseline commit of the whole repo as it stood, including in-progress work.
- Not built yet; next build should confirm the icon shows on the exe and title bar.