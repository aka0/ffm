# FFM: Football Manager Facepack Manager

FFM is a Windows desktop app for editing Football Manager newgen portraits one player at a time. It reads your save to find newgens, finds each player's portrait in your facepack, and lets you send that portrait to ChatGPT or Grok in your browser for a targeted edit (hairstyle, facial hair, or an older appearance). Nothing touches your facepack until you review the result side by side and accept it, and every accepted change keeps a numbered backup you can restore.

Your save file is only ever read, never written.

## Features

- **Read-only save scanning.** A native C# reader pulls game-visible player IDs, names, birth dates and ages from an FM27 `.fm` save. Newgens are searchable by name or ID.
- **Facepack discovery.** Point FFM at `graphics` or any folder inside it. It finds every `config.xml` recursively, reads the standard `from`/`to` portrait mappings, and flags players mapped to more than one image.
- **Guided browser edits.** Pick changes from presets (or write your own prompt), copy the portrait's file path and the prompt, and finish the edit in the AI chat you already use.
- **Safe review.** Pasted or dropped results are checked for aspect ratio, resized to the original dimensions, and keep the original cutout transparency. An optional **Reduce edge halos** step cleans the outline.
- **Backups and history.** Accepting writes an exact copy of the old portrait as `<name>_prev1`, `_prev2`, and so on, and **Version history** lets you review and restore any of them.
- **Crash-safe writes.** Changes are journaled, verified by hash, and rolled back automatically if something goes wrong mid-write.

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source
- Football Manager 27 with a facepack installed under `Documents\Sports Interactive\Football Manager 27\graphics`
- A ChatGPT or Grok account that can edit images in the browser

Python and fmsave are not needed at runtime.

## Getting started

```powershell
git clone https://github.com/aka0/ffm.git
cd ffm
dotnet run --project src/FFM.App
```

1. Click **Save & facepack**, choose your facepack folder (or `graphics` itself) and an `.fm` save, then click **Load sources**. Progress shows along the bottom and can be cancelled.
2. Search for a newgen by name or ID and select them. Their current portrait appears on the left.
3. Choose the changes you want under **Portrait changes**. Open **View / edit prompt** if you want to fine-tune the wording.

## Editing a portrait in your browser

1. Click **Copy image path**. FFM prepares a private copy of the portrait and copies its full path.
2. Click **Open ChatGPT** (or **Open Grok**), choose **Add file** in the chat, paste the path into the file picker's **File name** box, and open it. You can also drag the current portrait straight into the browser.
3. Click **Copy prompt** and paste it into the chat, then send.
4. When the edit is done, use the browser's **Copy image**, then click **Paste edited image** or press Ctrl+V in FFM. Dragging the image onto FFM or **More options > Import image file…** also work.
5. Compare the two panes (**Compare original** flips the preview back to the original), then click **Accept portrait** or **Reject**.
6. Refresh Football Manager's graphics cache to see the new portrait in game.

See [the browser workflow](docs/browser-handoff.md) for the full details of each step.

## Why FFM uses your browser instead of an AI API

FFM only supports browser handoff for AI portrait editing, on purpose. Most people who'd use FFM already pay for ChatGPT or Grok, and those subscriptions include image editing in the browser. Calling the providers' APIs instead would mean buying separate API credits on top of a subscription you already have, just to edit a few portraits.

So FFM never asks for an API key. It prepares the portrait and the prompt, opens the provider's homepage, and leaves the upload and the edit to you in your own signed-in browser. FFM never stores your provider password or browser session, never reads browser cookies, and never puts your image or prompt in a URL. The only thing it remembers is which provider you picked.

## How your files are protected

- **Save files are read-only.** FFM only opens them for reading.
- **Nothing changes until you accept.** Preparing, importing and toggling edge cleanup all work on private copies under `%LOCALAPPDATA%\FFM\edits`.
- **Every accept is verified.** Before writing, FFM re-checks the original portrait, the config and the preview by hash and stops if anything changed in the meantime.
- **Shared images are left alone.** If several players share one image, or the original isn't a PNG, FFM writes a new player-specific PNG and updates only that player's mappings.
- **Real players are excluded.** Only confirmed newgens can enter the editing workflow.
- **Interrupted writes are recovered.** Journals under `%LOCALAPPDATA%\FFM\transactions` let FFM roll back an incomplete change at startup, and it refuses to overwrite files that something else changed.

## Project layout

| Path | What it is |
| --- | --- |
| `src/FFM.App` | The WPF app: windows plus the save reader, facepack catalog, edit session and commit services |
| `src/FFM.EditCheck` | Self-contained checks for the edit, import, backup, rollback and recovery logic using synthetic files |
| `src/FFM.UiCheck` | STA smoke checks that exercise the WPF player-selection to portrait-preparation path |
| `src/FFM.ReaderCheck` | Diagnostics that validate the save reader against a real save |
| `src/FFM.FacepackCheck` | Checks for facepack config discovery using isolated fixture folders |
| `docs/` | Workflow notes, validation reports and the implementation plan |
| `design/` | UI mockups and asset notes |

Run the edit checks with:

```powershell
dotnet run --project src/FFM.EditCheck
dotnet run --project src/FFM.UiCheck
```

They never touch your real facepack or save, change the system clipboard, or make network requests.

## Status and known limitations

- Validated against an FM27 backup save: it matches six hand-checked players and FMRTE's count of 35,292 newgens. About 1,200 newgen names or ages are still unresolved; see [reader validation](docs/reader-validation.md).
- Club and readable nationality aren't parsed yet.
- Edits keep the original cutout silhouette, so a hairstyle that grows beyond it can be clipped.
- How well the AI keeps the same face is up to the provider, so always review before accepting.
- There's no installer yet; build and run from source.

## Contributing

Several people and assistants edit this repo. Before you change anything, pull first, keep edits small and targeted, and describe what you changed in your commit message.

## License

FFM is released under the [MIT License](LICENSE).

## Credits

The save reader is a C# implementation that uses [rhiever's fmsave](https://github.com/rhiever/fmsave) as a reference. Thank you to rhiever for documenting the Football Manager save format and publishing the reader as open source. Adapted parsing code keeps its attribution and MIT license in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

FFM does not require Python or fmsave at runtime; the upstream source is used for reference and comparison during development.
