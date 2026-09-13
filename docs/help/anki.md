# Anki collections

WinOld Recovery copies each Anki profile that contains `collection.anki2`, including a non-empty WAL if Anki was not closed cleanly, plus media files, `deleted.txt`, and `.colpkg` backups.

It does **not** copy `collection.media.db2` or `media.trash` — Anki rebuilds those. If a profile of the same name already exists, the restore uses `Profile (recovered)`. Close Anki first. AnkiWeb can also bring decks back, but not add-on settings.
