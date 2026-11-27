# CSV reliability corpus

UTF-8 fixtures have deliberately fixed checkout line endings: `quoted-crlf.csv` is CRLF (including its embedded newline); other files are LF. `.gitattributes` preserves this distinction. Expected literal values in `manifest.json` are independent of the reader.

The shared contract uses the listed source ordinals in order, excludes all other columns, renames selected columns to `select` and `quote"bracket]`, and explicitly imports both as Text. Each valid fixture runs as a new-table import and as an existing-table replacement on all five providers. Empty and quoted-empty cells remain NULL; the literal word `NULL` remains text. Identifiers deliberately include reserved words, quote/bracket characters, Unicode, apostrophes and a literal dot in the table name.

The deterministic large-file recipe is the header `id,label`, followed by records 1 through 10,000 with values `i,record i`, except data record 8,000 uses key 1. A unique constraint rejects that record. Tests compare the entire original row before cleanup, then replace the input with corrected records and retry. Separate CHECK fixtures reject record 1 or the final record. New-table integer failures use 2147483648 with native INTEGER on strict engines; SQLite instead uses a controlled database-boundary CHECK fixture because its INTEGER type is permissive.

Parser-specific invalid mappings, ordinal errors and stale previews retain coverage in CsvColumnIdentityTests, CsvValidationTests and CsvImportWizardTests rather than repeating their exhaustive matrices in every provider/UI flow.
