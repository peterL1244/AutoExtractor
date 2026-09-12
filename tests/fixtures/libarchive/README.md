# Pinned libarchive RAR5 interoperability fixtures

Origin: [libarchive/libarchive](https://github.com/libarchive/libarchive/tree/c719b9b1f56621d92063a85361cc8d114f5575a9), revision `c719b9b1f56621d92063a85361cc8d114f5575a9`.

These are unchanged upstream uuencoded reference archives from `libarchive/test/`, plus decoded bytes. `manifest.json` identifies every source URL and SHA-256 digest of both downloaded and decoded files. `reproduce.ps1` downloads the pinned files and decodes uuencode without executing their contents. Tests run offline; the reproduction script is optional and requires network access.

## Redistribution notices

The upstream distribution permits redistribution in source and binary form under its BSD-style notice. Preserve `COPYING` and the complete notice at the beginning of `test_read_format_rar5.c` (Copyright 2018 Grzegorz Antoniak), both included verbatim. The upstream author's source notice requires retaining the copyright, conditions and disclaimer for source redistribution and reproducing them in accompanying materials for binary redistribution. These files are third-party test fixtures and are not relicensed under AutoExtractor's own MIT license.

## Actual archive contents

| Archive | Compressed bytes | Extracted contents | Verification |
| --- | ---: | --- | --- |
| `test_read_format_rar5_compressed.rar` | 436 | `test.bin`, 1,200 bytes of generated integers | CRC32 `7CCA70CD`, independently regenerated integer sequence |
| `test_read_format_rar5_multiarchive.part01.rar` through `part08.rar` | 116,728 total | `home/antek/temp/build/unrar5/libarchive/bin/bsdcat_test` (144,608 bytes), `bsdtar_test` (365,672 bytes) | CRC32 `35277473` and `E59665F8`, as asserted by upstream tests |

The multiarchive payloads are upstream Unix test programs, not synthetic text. They are retained only as archive extraction fixtures: tests never launch them. Listing verified there are two ordinary file entries, no symbolic links, hard links or copy links. Their 510,280-byte total expansion is bounded. No downloaded fixture is installed or used as an extraction engine.

## Tests

`RarFixtureTests.Register()` adds fixture-digest checks, real compressed RAR5 content verification, disguised eight-volume scanner/coordinator extraction and rename-restoration hash checks, an interior missing-volume guard and a missing-final-volume engine guard. Every test copies fixtures to a fresh temporary directory, leaving this source directory unchanged.

Legacy RAR volumes and malformed RAR corpus coverage are not claimed by this fixture set. Upstream C source is retained for provenance, expected CRCs and notices; it is not built as part of AutoExtractor.

Verified with bundled 7-Zip 26.03: all nine RAR fixture tests passed; the full combined suite passed 58/58 after scanner and engine review corrections. The known integer sequence, upstream CRCs, original-volume SHA-256 hashes after restoration and pinned fixture digests were all checked.


## Encrypted RAR4 and RAR5

Four additional fixtures are `test_read_format_rar4_solid_encrypted.rar` (323 bytes), `test_read_format_rar4_encrypted_filenames.rar` (460 bytes), `test_read_format_rar5_solid_encrypted.rar` (475 bytes), and `test_read_format_rar5_encrypted_filenames.rar` (718 bytes). They contain only four 18-byte text files (`a.txt` through `d.txt`), with exact content `This is from a.txt` and corresponding filenames. No links or executable content are involved.

The documented password is the public fixture value `password`, explicitly supplied in unchanged upstream `test_read_format_rar_encryption.c` lines 28-35. This is not a user credential and no guessing or cracking is performed. That source, its original license notice, and the additional inspected upstream source notices are included and hashed in the manifest.

Each fixture test rejects a wrong password during extraction, gives the coordinator an incorrect password followed by the documented password, verifies both retry requests and every plaintext, confirms no password is serialized into the rename journal, and restores the source archive with its original SHA-256 digest. Data-only encryption allows header listing without the correct password; therefore wrong-password rejection is checked by extraction, not merely by listing.
