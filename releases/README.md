# Release notes

One Markdown file per release, named after the tag: `releases/v0.1.0.md`, `releases/v0.2.0.md`, ...

`tools/release/publish.sh` uses the file for `VERSION` twice:

1. as the GitHub release body (`gh release create --notes-file releases/v<ver>.md`), and
2. as the "What's new" block on the website (copied into `website/src/data/release.json`,
   with HTML comments removed).

Start from the template and replace every line that begins with `TODO`. publish.sh refuses
to run while such a line is left, or while the file is missing or empty.

```sh
cp releases/TEMPLATE.md releases/v$(cat VERSION).md
```

Writing guidelines:

- Only list what is in this build and was checked; do not announce plans as features.
- Keep a "Known issues" section, and say plainly which platforms are untested
  (Windows and Intel Macs stay "experimental" on the site until `verified` is set).
- The first lines show on the website, so lead with what a player notices.
- Markdown headings, lists, links and `code` render on both GitHub and the site.
