# FM Facepack Manager — MVP implementation plan

Updated November 11, 2026. Deliverables at this stage: implementation plan and interactive UI concept, not a working Windows executable.

## Product contract

Manage one newgen player's mapped portrait at a time. Both a facepack location and a successfully imported compatible save are required. Load player ID, name and age from the save; then allow search by exact ID or name. Selecting a save alone must never imply that metadata was loaded. Keep search and editing unavailable until the required sources validate and player metadata finishes loading.

The selected `.fm` save is strictly read-only. Never write, patch, rename, move, or change its attributes. Hash before and after import in integration verification. Keep imported indexes and application settings outside the save. Never upload the save to an AI service.

Edit the existing mapped image, rather than create a replacement identity. Every request includes that image, explicit preservation instructions, and (where supported) a localized edit mask. User acceptance is always required before applying an edit. Model output cannot guarantee identity or pixel preservation by prompt alone; use source compositing for supported localized edits and allow rejection of any unwanted change.

No batch editing, automatic aging, mass assignment, save editing, or automatic replacement in MVP.

## Feasibility gates — do these first

1. **FM27 save reader:** build on the local NGMapper reader at `C:\Users\aka0\Documents\ChatGPT\NGMapper`. Its `SaveScanService.cs` opens saves with `FileAccess.Read`, indexes the container, decompresses `game_db.dat` with ZstdSharp, and verifies before/after SHA-256 hashes. Its `PlayerRecordScanner.cs` adapts an MIT-licensed fmsave record locator. This is useful existing work, not a finished name/newgen importer: the data model has no name field, and newgen classification is unresolved. Verify against the exact game/build and extract player ID, name, confirmed newgen status and age; club is optional. Direct save support remains a release dependency for the full requested MVP.
2. **Required metadata gate:** if direct save reading is unavailable or fails, keep the user in setup with a specific error and a retry/change-save action. There is no save-free or ID-only editing mode in the requested MVP. Compatible player export import would require an explicit future scope change and does not satisfy required direct save loading. Do not infer that a numerical ID alone proves a player is a newgen.
3. **Provider authentication:** investigate eligibility for Sign in with ChatGPT and image-editing authorization. Current documentation limits developer integration to participating partners; do not assume this application qualifies. Design the connection screen for approved browser sign-in when available and OpenAI API-key connection otherwise. Grok integration uses the documented xAI API-key flow until an appropriate delegated flow is verified. Never collect ChatGPT/Grok passwords or reuse another app's session tokens. Identity sign-in alone does not authorize image usage.
4. **Portrait preservation:** evaluate beard-only edits on real facepack dimensions and transparency. Verify each provider's image-edit and masking capabilities. Fail or mark an unsupported option clearly; never fall back silently to text-only generation. Broad aging edits require whole-face changes and stronger visual review.

## Recommended architecture

Use C# with WPF on a supported .NET LTS release for a native Windows desktop app. Use MVVM, async image/import work, virtualized search results, native folder/file pickers, per-user settings, and a small local SQLite index. Prefer a self-contained Windows x64 installer; verify licensing and runtime requirements before choosing distribution tooling.

Separate these services:

- `GraphicsLocator`: Documents Known Folder (including redirected/OneDrive Documents), version folders and remembered custom user-data location.
- `FacepackIndex`: safe `config.xml` parsing and player-to-file mappings.
- `IPlayerDataReader`: version-specific, read-only save import; unsupported builds produce actionable status.
- `IPortraitEditor`: provider capabilities, connection verification, image-edit submission, cancellation and normalized result.
- `PortraitPreparation`: image decoding, supported masks, alpha handling, output dimensions and source compositing.
- `VersionStore`: backups, commit journal, apply, crash recovery and restore.

Keep credentials in Windows Credential Manager or DPAPI-protected per-user storage. Provider HTTP calls run in the desktop backend. Redact keys from logs; send only the selected portrait, mask and prompt. Treat provider images as untrusted input and enforce decode/dimension/size limits.

## Implementation sequence and completion criteria

### 0. Prove save import and identity-preserving edits

Build small technical spikes for the gates above. First isolate and reuse NGMapper's container/hash/decompression logic with its MIT attribution; keep the other project unchanged. Record supported FM versions and image-provider capabilities. Names and confirmed newgen classification are the remaining save-reader work; appearance/ethnicity decoding is not required for editing an already mapped portrait.

**Count validation:** the user states the reference save should contain fewer than 40,000 newgens. NGMapper's status reports 174,837 candidate player records and 129,947 IDs above 2,000,000,000. Those are scanner observations, not verified newgen counts. Reject the threshold-only classification. Validate deduplication and record boundaries, confirm internal IDs versus visible game IDs, decode/verify the actual newgen marker, and compare known real/newgen player IDs and aggregate counts against an independent reference for the same save. Do not cap results at 39,999 or tune a heuristic merely to match the expected count. A result below 40K is a useful sanity check, not proof of correct classification. Do not label an unverified classification as a confirmed newgen.

**User-supplied reference fixtures for the Tottenham Hotspur first-team men's squad:** player ID `2002095290`, Shakille Drakes, must classify as a newgen; player ID `2000026194`, Lucas Høgsberg, must classify as a real player and be excluded from the MVP edit target list. Verify both IDs, names, club and team against the same imported save before treating these expected outcomes as passing evidence. A failed name/ID join, team membership mismatch, or classification mismatch is a reader failure; never include a real player's portrait for editing. These two fixture checks supplement, and do not replace, wider newgen count and classification validation.

Pass when the reference FM27 save yields verified IDs/names/ages/team assignments/newgen flags, identifies Shakille Drakes (`2002095290`) as a Tottenham Hotspur first-team men's newgen and Lucas Høgsberg (`2000026194`) as a real player excluded from editing, a defensible count consistent with the under-40K reference, and unchanged save bytes; and a source-image beard edit preserves the player's recognizable identity. Preserve third-party license notices on reuse. Do not commit to a completion date for unknown parser work.

### 1. Set up the Windows shell and sources

Show `Save & facepack` configuration on first launch: required facepack folder and required save selection, followed by provider connection when previewing an edit. Keep the same `Save & facepack` button in the top-left toolbar for later changes. Open FFM only after both sources validate and required metadata loads. Start facepack detection at `C:\Users\aka0\Documents\Sports Interactive\Football Manager 27\graphics`, but use Windows Known Folders rather than hard-code the username. Detect candidate packs by their mapping configs and show choices when ambiguous. The save content remains strictly read-only.

Require the selected facepack's canonical resolved path to lie inside the chosen game's actual graphics directory; reject prefix tricks and junction/symlink escapes. A custom game user-data location is supported only with its graphics directory. Check write access to the pack and backup folder separately from read access to the save. Do not create a supposedly valid graphics root merely because a directory is named `graphics`.

Pass: valid packs can be selected, outside paths show an inline error, a save can be selected read-only, and unsupported save formats show accurate status.

### 2. Index mappings and find one player

Read actual `config.xml` mappings; do not assume the filename equals the player ID. Preserve raw mapping targets and version-specific forms such as `r-` prefixes while normalizing lookup aliases only where verified. Disable XML external entities. Resolve relative image paths under approved graphics scope; exclude sibling backup files matching `<portrait-stem>_prev<number>.<extension>` from active portrait discovery, along with staging and cache files. Report duplicate/conflicting mappings instead of choosing arbitrarily.

Search exact ID first, name when available, and show a compact matching list only as needed. A selected player shows its loaded name, ID, age, data source, mapped filename and current portrait. Store IDs as strings. Missing images, unknown newgen status and unsupported mappings have explicit states.

Pass: actual mapped portraits display, accented names and duplicate names are distinguishable, ID/name lookup uses the required loaded save metadata, and missing/failed save imports prevent entry into editing.

### 3. Connect AI and compose a constrained edit

Implement verified OpenAI image editing first, then xAI image editing behind the same interface. Keep model IDs configurable and capability-checked. Connection status requires a valid credential/capability check. Show service/model and usage estimate if reliable pricing metadata is available; otherwise label cost unknown and note provider billing.

Preset controls are three dropdowns, each with exactly one selection:

- **Alter Hairstyle:** Keep current hairstyle (default), plus five styles: Buzz cut, Crew cut, Textured crop, Undercut, Slicked back.
- **Add beard:** Keep current facial hair (production default), plus eight styles: Light stubble, Heavy stubble, Short beard, Full beard, Goatee, Circle beard, Van Dyke, Balbo. The mockup preselects Short beard to match its illustrative result. Keep current does not remove an existing beard.
- **Age Appearance:** default to the player's current age loaded from the save. Offer that age plus 5, 10, 15 and 20 years, displaying the target age and increment together. Example: `23 years · current player age`, `28 years (+5)`, `33 years (+10)`, `38 years (+15)`, `43 years (+20)`. The baseline comes from the selected player's age in the save, never from the existing portrait's apparent age or the computer's calendar. The current-age default means no aging instruction. If age is missing or unverified, disable the selector and show `Player age unavailable`; never invent a baseline. Rebuild choices when selecting a different player or reimporting the save.

Age is an appearance instruction only and never changes save data. Put the complete editable instructions in a `View / edit prompt` section, collapsed by default. Generate an initial prompt from the dropdown selections. Allow the user to edit it; retain custom text when dropdowns or connection status change rather than silently overwriting it. Show a custom-prompt status and provide `Reset to selected changes` to explicitly regenerate the text. Never reopen the section automatically. Reject empty instructions. Prompt edits, resets and dropdown changes invalidate a pending image, requiring a fresh result before acceptance. Custom instructions can request edits even when dropdowns are unchanged. Always submit the original/current image and retain the product's same-person preservation requirements in the edit request.

For generated prompts, include instructions only for changed selections; with all defaults and no custom request, disable submission because no edit is requested. Preserve hairstyle unless Alter Hairstyle is selected; allow only the chosen hairstyle change when it is selected, retaining natural hair color. Likewise preserve facial hair when its selector is unchanged. Always preserve identity, facial proportions, pose, clothing, framing, lighting and background except for specifically selected appearance changes.

Default beard prompt:

> Edit the attached existing player portrait. Add only a natural, neatly groomed short beard and matching moustache. Preserve the exact person's identity, facial proportions, eyes, hair, skin tone, pose, expression, clothing, lighting, crop and background. Do not replace the person, reframe or beautify. Preserve transparency. Change no other feature.

For beard-only editing, generate or let the user refine a beard-region mask. Hairstyle editing needs an appropriate hair-region mask that allows silhouette changes while preserving the rest of the image. For combined changes use the union of approved edit regions. If the provider lacks masking, treat its result as a donor and composite only the approved feathered regions onto the source. Keep untouched pixels from the source. For age increments of +5/+10/+15/+20 years use an appropriate larger editable region, disclose that more of the face will change, and retain the same identity requirements. Mask alignment and edge quality must pass evaluation before this is claimed to work.

Pass: every edit uses the source image; no generation-only fallback; timeouts, invalid credentials, quota and cancellation leave the original intact. Never retry an uncertain paid submission automatically without reconciliation.

### 4. Review, accept and keep history

Show current and proposed portraits side by side, with zoom and a before/after comparison control. Clearly label the result as pending. Options: Reject, Retry, Accept portrait. Rejected output never touches mapped files. Disable Accept until the result passes image decoding, dimension and alpha validation and still matches the selected player/source revision. Changing presets invalidates a pending result.

On acceptance:

1. Acquire a per-pack write lock and recheck source/config hashes against the reviewed revision.
2. Reserve the next monotonically increasing backup number for this portrait. Keep each backup in the same directory as the active image, inserting `_prev<number>` before the original extension: `2000123456.png` becomes `2000123456_prev1.png`, then `2000123456_prev2.png`. Use the actual mapped filename stem, which may differ from the player ID; preserve the image format/extension. Scan existing sibling backups and the version manifest to choose the next unused number, and create it exclusively so an existing backup is never overwritten. Keep transaction metadata in per-user application storage, with player ID, canonical pack/path, old/new hashes, timestamp, provider/model, chosen edits and affected mappings; never credentials. No separate backup directory is created.
3. Verify backup bytes and durably record the intended transaction. A backup failure prevents replacement.
4. Decode and normalize the candidate to original dimensions, format and required alpha. Stage it beside the destination and atomically replace the original where the filesystem supports it. Journal/recover any multi-file operation; never claim image plus config updates are jointly atomic.
5. Verify committed bytes and complete the journal. On failure recover from the verified backup and surface status. Detect external pack changes rather than overwriting them.

Shared-image exception: when several players reference one portrait, create a player-specific edited image and update only this player's mapping. Preserve the selected player's previous portrait as a sibling `<player-specific-stem>_prev<number>.<extension>` beside the new active image. Back up the affected `config.xml` bytes in the per-user transaction store and record the complete before/after mapping. Never alter every mapped player's appearance accidentally.

History permits preview and restore. Restore itself creates the next sibling `_prev<number>` backup before changing the current image/config, preserving the forward history. Backup files are not added to active `config.xml` mappings. Provide game-version-appropriate graphics refresh instructions; changing files does not prove FM has reloaded its cache.

Pass: accepting creates an exact backup first; rejecting changes nothing; restore works; shared source images affect only the selected player; interrupted transactions recover.

### 5. Verify and package

Meaningful tests: mapping parsing and ambiguous mappings; canonical graphics containment; no writes to save; mocked provider requests include original image; pixels outside a localized composite remain unchanged; stale result rejection; alpha/dimension preservation; backup failure; file locks; duplicate filenames; shared-image copy-on-write; apply/restore and crash recovery. Exercise a real isolated sample pack, then verify one accepted portrait in the game. AI visual quality requires human review as well as pixel checks.

Inspect the app at 100%, 150% and 200% Windows scaling, light/dark themes, keyboard navigation, long names/paths, and a large pack while imports run. Ship a per-user installer, version information, and concise source/provider/restore instructions.

## UI direction

Use FFM as the app name and workspace label; do not call it Portrait studio. Clean neutral surfaces, teal actions, Segoe UI typography and generous portrait space. Setup lives behind `Save & facepack` in the top-left toolbar and opens on first launch; connection settings live behind `Connect AI`. Both source locations are required and persist per user. The main workspace focuses on one selected player, original/proposed portrait comparison, modifications and explicit acceptance. No bulk dashboard.

The interactive concept uses a fictional adult portrait and sample player/save details. Its controls simulate setup, search, connection, preview, acceptance and restore locally. Dropdown selections generate the editable prompt until the user customizes it; custom text is retained until explicitly reset. The prompt editor starts collapsed. Only the unchanged short-beard preset prompt has a sample image preview. They do not authenticate, read saves, call providers or write facepack files. The sample beard edit was produced with the built-in image tool from the sample original, using the preservation prompt recorded in `design/asset-notes.md`.

## Official/primary references consulted

- WPF Windows desktop framework: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/
- OpenAI image generation and edits: https://developers.openai.com/api/docs/guides/image-generation
- ChatGPT sign-in availability and developer participation: https://help.openai.com/en/articles/20001410-sign-in-with-chatgpt
- xAI image editing: https://docs.x.ai/developers/model-capabilities/images/editing
- xAI key setup: https://docs.x.ai/developers/quickstart
- Legacy NewGAN repository (RTF workflow; not proof of FM27 save support): https://github.com/Maradonna90/NewGAN-Manager
