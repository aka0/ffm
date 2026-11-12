# FM27 reader validation

Input: `Arthur Kao - Tottenham Backup.fm`. The production save is not used for ongoing checks. The scanner opens saves with `FileAccess.Read`, checks file size and modification time before/after inspection, and hashes the save header plus directory before/after scanning.

FFM uses native C# parsing with ZstdSharp for container decompression. Python and fmsave are not runtime dependencies. The format reference is rhiever's MIT-licensed fmsave; attribution is in the README and third-party notices.

The corrected identity lookup follows the person-index reference to the doubled game-visible ID. It does not assume IDs increase between records: that assumption lost Sam Bolton and José Maieco. Internal record IDs must not be used as portrait IDs.

The backup's in-game date is 2027-12-29. Age is calculated against that date, not the computer's date.

| Player ID | First name | Last name | Age | Birth date | In newgen list |
| --- | --- | --- | --- | --- | --- |
| 2002095290 | Shakille | Drakes | 16 | 2011-03-09 | Yes |
| 2002098250 | Sam | Bolton | 16 | 2011-10-19 | Yes |
| 2002098449 | José | Maieco | 16 | 2011-12-05 | Yes |
| 2002069230 | David | Ferreira | 17 | 2010-09-14 | Yes |
| 2000026194 | Lucas | Høgsberg | 21 | 2006-06-23 | No |
| 2000011972 | Sávio | Moreira de Oliveira | 23 | 2004-04-10 | No |

The current marker scan finds 35,292 kind-2 player objects with a 2002 game-visible ID, matching the user's FMRTE benchmark. Eight additional 2002 identities have kind 3 and do not enter the roster. This evidence covers this save/build, not every FM27 build or future career.

First/last names and ages resolve for 34,091 newgens. The remaining 1,201 are retained by ID with unavailable metadata; ambiguous name windows are rejected. Club and readable nationality decoding remain to be ported. The primary nation byte is read but is a save-local identifier, not a country name.

Run the native reader check with `--synthetic` for isolated valid/corrupt container and player-record fixtures, or use the backup path and `--validate` for the six identity/name/age fixtures, real-player exclusions, unique IDs and FMRTE count. The console project links the same C# services as the WPF application.
