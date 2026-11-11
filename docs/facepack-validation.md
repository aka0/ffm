# Facepack and sources UI validation

The sources dialog uses automatic height, matching 40-DIP path and browse controls, an independently measured footer and the app-wide green button style. The textbox content host has no extra template margin: WPF already applies the textbox padding. Accept and Reject use equal star-width columns with a 10-DIP gap.

The native catalog searches arbitrary descendant directories for `config.xml` (case insensitive). It streams standard FM `<record from="..." to="graphics/pictures/person/ID/portrait"/>` entries, supports numeric, `rID` and `r-ID` references, and resolves images beside each config. Icon and unrelated graphics entries do not become portraits. Equivalent mappings deduplicate; distinct images remain conflicts. Malformed configs cannot contribute partial mappings. The app builds the catalog once for the loaded newgen IDs.

The dependency-free `src/FFM.FacepackCheck` fixture check covers nested/unpredictable directory names, Unicode/spaces, explicit extensions, relative parent paths, duplicate aliases, conflicting images, malformed configs, missing images, graphics containment, ID filtering and cancellation. A read-only scan of the user's graphics directory found 78 configs and the four supplied newgen portraits in separate nested folders, with zero config issues.

The bottom progress bar reserves 0–85% for save loading and 85–100% for config indexing, with indeterminate progress during folder discovery. Source selection is disabled during loading; cancellation does not publish a partial roster/catalog. No facepack or save files are written by these checks.
