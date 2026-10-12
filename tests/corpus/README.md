# Golden-test corpus

Public-domain EPUBs from [Project Gutenberg](https://www.gutenberg.org/), used by
`tests/Bilgames.Core.Tests/GoldenFileTests.cs` to pin the importer, dialogue analysis and synthesis
planner against the fixtures in `tests/Bilgames.Core.Tests/Golden/`.

| File | Book | Gutenberg # | Source |
|---|---|---|---|
| `alice.epub` | Alice's Adventures in Wonderland, Lewis Carroll | 11 | https://www.gutenberg.org/ebooks/11 |
| `christmas-carol.epub` | A Christmas Carol, Charles Dickens | 46 | https://www.gutenberg.org/ebooks/46 |
| `sherlock-holmes.epub` | The Adventures of Sherlock Holmes, Arthur Conan Doyle | 1661 | https://www.gutenberg.org/ebooks/1661 |
| `frankenstein.epub` | Frankenstein; or, The Modern Prometheus, Mary Wollstonecraft Shelley | 84 | https://www.gutenberg.org/ebooks/84 |

These books are not covered by the repository's MIT License. All four are in the public domain in the United States. The files are unmodified Project Gutenberg
EPUB3 downloads (renamed only); the Project Gutenberg header and licence inside each file are kept
intact, which is what permits redistributing them here. Check copyright law in your own country
before using them elsewhere.

Rules for this folder:

- Only public-domain Project Gutenberg books. Never add a purchased or otherwise copyrighted book
  (the repository's `.gitignore` excludes `*.epub` everywhere except here).
- Never strip or edit the Gutenberg header or licence.
- Keep files small (under ~1 MB each) so the golden suite stays fast.
- Adding or replacing a book changes the fixtures: run the tests with `BILGAMES_UPDATE_GOLDEN=1`
  and add the new book's name to `Golden.Books` in `GoldenFileTests.cs`.
