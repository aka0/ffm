# FFM

Windows app for managing Football Manager newgen portraits, one player at a time. Save files are read-only; portrait changes require preview and acceptance.

## Credits

The Football Manager save reader is being implemented in C# using [rhiever's fmsave](https://github.com/rhiever/fmsave) as a reference. Thank you to rhiever for documenting the save format and publishing the reader as open source. Adapted parsing code retains the attribution and MIT license in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

FFM does not require Python or fmsave at runtime. The upstream source is used for reference and comparison during development.

## Development status

The Windows UI and native read-only save reader are implemented. The scanner extracts game-visible player IDs, first/last names, birth dates and ages; the UI searches newgens by name or ID. Validation against an FM27 backup save matches the six supplied player examples and the FMRTE count of 35,292 newgens. Some metadata remains unresolved; see [reader validation](docs/reader-validation.md). Club and readable nationality parsing, AI editing and portrait replacement are not ready yet.

Select `graphics` or a facepack folder beneath it. FFM discovers `config.xml` files recursively without assuming folder names or order, reads standard FM `from`/`to` portrait mappings, and resolves image paths relative to each config's directory. Different images mapped to the same player are reported as a conflict. Save loading and facepack indexing run in the background with progress and cancellation shown along the bottom.
