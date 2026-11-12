# FFM

Windows app for managing Football Manager newgen portraits, one player at a time. Save files are read-only; portrait changes require preview and acceptance.

## Credits

The Football Manager save reader is being implemented in C# using [rhiever's fmsave](https://github.com/rhiever/fmsave) as a reference. Thank you to rhiever for documenting the save format and publishing the reader as open source. Adapted parsing code retains the attribution and MIT license in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

FFM does not require Python or fmsave at runtime. The upstream source is used for reference and comparison during development.

## Development status

The Windows UI and native read-only save reader are implemented. The scanner extracts game-visible player IDs, first/last names, birth dates and ages; the UI searches newgens by name or ID. Validation against an FM27 backup save matches the six supplied player examples and the FMRTE count of 35,292 newgens. Some metadata remains unresolved; see [reader validation](docs/reader-validation.md). Club and readable nationality parsing remain outstanding.

Select `graphics` or a facepack folder beneath it. FFM discovers `config.xml` files recursively without assuming folder names or order, reads standard FM `from`/`to` portrait mappings, and resolves image paths relative to each config's directory. Different images mapped to the same player are reported as a conflict. Save loading and facepack indexing run in the background with progress and cancellation shown along the bottom.

## Editing with ChatGPT or Grok

The MVP uses your existing browser subscription. Choose a mapped newgen and changes, then click **Copy image path**. In ChatGPT or Grok, choose **Add file** and paste that full path into the file picker's **File name** box. Use the separate **Copy prompt** button to paste the editing instructions into chat. Copy the finished image in the browser and use **Paste edited image**, Ctrl+V or drag/drop on FFM's main screen for side-by-side review. File import remains available under **More options**. No API key is needed; browser sign-in stays with the provider. See [the browser workflow](docs/browser-handoff.md).

**Reduce edge halos** is enabled by default. Import preserves the original cutout alpha and prevents transparent generated pixels from exposing black colors. Cleanup restores only the outer two pixels and softens the next pixel, using a matching earliest backup when available. Toggle it to compare the preview before accepting; interior colors are not globally filtered.

Accept applies the reviewed preview after verifying the source and mapping have not changed. An exact previous portrait is kept beside the active file as `<stem>_prev1.<extension>`, then `_prev2`, and so on. Shared images are isolated to a player-specific PNG and only that player's mappings are updated. Version history prepares a restoration for review, and accepting it creates another backup. Rejecting the preview leaves the facepack unchanged.
