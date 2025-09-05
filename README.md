﻿<div align="center">

[![Contributors][contributors-shield]][contributors-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![AGPL License][license-shield]][license-url]

</div>

<!-- PROJECT LOGO -->
<br />
<div align="center">
  <h1 align="center">MarkdownGitDiffMarker</h1>

  <p align="center">
    Highlight changes between two Git commits in a Markdown document.
    <br />
    <a href="https://github.com/thgossler/MarkdownGitDiffMarker/issues">Report Bug</a>
    ·
    <a href="https://github.com/thgossler/MarkdownGitDiffMarker/issues">Request Feature</a>
    ·
    <a href="https://github.com/thgossler/MarkdownGitDiffMarker#contributing">Contribute</a>
    ·
    <a href="https://github.com/sponsors/thgossler">Sponsor project</a>
  </p>
</div>

## About The Project

Highlight Markdown changes between two Git commits (or between your workspace and a commit) by inserting visible change markers directly into the .md files. The tool writes changes in-place and can also remove previously added markers to restore clean Markdown.

Note: This tool operates on files tracked in a Git repository and is cross-platform. It preserves original line endings.

## Features

- Compare working directory with HEAD or a specific commit
- Compare two commits (source vs target) without considering workspace changes
- In-place annotation of matched .md files using HTML markers
  - Generic changes: `<mark>**[CHANGE]**</mark>` banner
  - Table-aware: `<mark>**[CHANGE] in table**</mark>` and cell-level highlights
  - List-aware: banners and inline marking of bullet/numbered list items
  - Figure-aware: `<mark>**[CHANGE] in figure**</mark>` with OLD/NEW previews for image lines
  - Additions wrapped as `<mark>...</mark>` and deletions wrapped as `<mark>~~...~~</mark>`
- Cleanup mode to remove all previously added markers and restore plain Markdown
- Supports simple glob patterns (e.g., `README.md`, `docs/*.md`, `docs/**/*.md`)
- Respects Git commit-ish forms like `HEAD`, `HEAD~1`, and full/short hashes

## How it works (high level)

- For the selected files, the tool resolves content from the chosen Git commit(s) using LibGit2Sharp.
- It removes any existing markers from the new content to avoid compounding.
- It computes a unified diff using the local Git CLI (`git diff --no-index --unified=0`).
- It applies intelligent, Markdown-aware wrappers to changed lines and emits context-specific banners.
- It overwrites the file(s) on disk with the annotated content.

## Requirements / Dependencies

- .NET SDK 9.0 or later
- Git CLI available on PATH (the tool invokes `git diff`). On Windows, common locations are probed as a fallback.
- A Git repository (files must reside within a repo).
- NuGet packages (restored during build):
  - LibGit2Sharp (0.30.0)
  - System.CommandLine (2.0.0-beta4.22272.1)

## Safety notes

- Files are modified in place. Commit or back up your work before running.
- Only `.md` files are processed; non-Markdown files matched by the pattern are skipped.

## Build

- Restore and build:
  - `dotnet build`

Optionally publish a self-contained or single-file binary for your OS/arch if desired.

## Run

Run from the repository root (or any directory within the repo). The `-f/--file` option is required and accepts simple glob patterns.

Synopsis:

- Compare workspace with HEAD:
  - `dotnet run -- -f "docs/**/*.md"`
- Compare workspace with a specific commit:
  - `dotnet run -- -s <sourceCommit> -f README.md`
- Compare two commits (overwrite working file with annotated target content):
  - `dotnet run -- -s <sourceCommit> -t <targetCommit> -f "docs/*.md"`
- Remove previously added markers:
  - `dotnet run -- -r -f "docs/**/*.md"`

### PowerShell helper scripts

- Run.ps1
  - Use this script to start the program without rebuilding. It forwards all arguments, so you can pass them naturally.
  - Examples:
    - `./Run.ps1 --help`
    - `./Run.ps1 -f "docs/**/*.md"`
- RevertLastCommit.ps1
  - Use this script to undo the last Git commit by creating a new revert commit and pushing it to `origin` (you will be prompted to confirm).
  - Internally runs: `git revert HEAD --no-edit` followed by `git push origin HEAD`.

### Intended workflow

1. Commit all changes to the Markdown document.
2. Run the tool to add change markers to the Markdown document (e.g., `./Run.ps1 -f README.md`).
3. Commit the Markdown document with change markers (for review).
4. Generate PDF with change markers. --> this is the whole point of the tool!
5. Collect review comments on PDF.
6. Revert the last commit to remove the change markers again (e.g., `./RevertLastCommit.ps1`).
7. Incorporate the review feedback into the Markdown document.
8. Commit the final Markdown document (release).

Options (from source):

- `-f, --file <pattern>`
  - Glob pattern for Markdown files to process (e.g., `README.md`, `docs/*.md`, `docs/**/*.md`). Required.
- `-s, --source-git-hash <hash-or-commitish>`
  - Source commit (e.g., `HEAD`, `HEAD~1`, short/full hash). Optional.
- `-t, --target-git-hash <hash-or-commitish>`
  - Target commit. Only valid when `--source-git-hash` is also provided.

Valid usage patterns:

- File pattern only: compare workspace with `HEAD`.
- `-s` + file pattern: compare workspace with the specified source commit.
- `-s` + `-t` + file pattern: compare two commits (workspace ignored). The annotated result for the target commit is written to the working file.

## Pattern/Path behavior

- Relative or absolute paths are accepted. Patterns are matched starting from the current directory unless an absolute path is provided.
- `**` performs a recursive search from the given base path segment.
- Only files ending in `.md` are processed.

## Markers and cleanup

Examples of emitted markers:

- Generic change banner: `<mark>**[CHANGE]**</mark>`
- Table banner: `<mark>**[CHANGE] in table**</mark>` (with cell-level `<mark>...</mark>`/`<mark>~~...~~</mark>`)
- Figure banner: `<mark>**[CHANGE] in figure**</mark>` with `OLD:`/`NEW:` previews
- Inline additions: `<mark>text</mark>`
- Inline deletions: `<mark>~~text~~</mark>`

## Examples

- Compare workspace with HEAD for a single file:
  - `dotnet run -- -f README.md`
- Compare workspace with a previous commit for docs tree:
  - `dotnet run -- -s HEAD~1 -f "docs/**/*.md"`
- Compare two specific commits for a file:
  - `dotnet run -- -s a1b2c3d -t d4e5f6a -f "docs/guide.md"`

## Troubleshooting

- "No files found matching pattern": Verify the pattern and quoting in your shell.
- "No Git repository found": Run inside a Git repo or point to files that are inside one.
- "Invalid or non-existent Git hash": Use valid commit-ish (e.g., `HEAD`, `HEAD~2`, full/short SHA).
- Nothing happens: Ensure Git is installed and available on PATH. The tool uses `git diff --no-index` under the hood.


## Contributing

Contributions are what make the open source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

If you have a suggestion that would make this better, please fork the repo and create a pull request. You can also simply open an issue with the tag "enhancement".
Don't forget to give the project a star :wink: Thanks!

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request


## License

Distributed under the MIT License. See [`LICENSE`](LICENSE.txt) for more information.


<!-- MARKDOWN LINKS & IMAGES (https://www.markdownguide.org/basic-syntax/#reference-style-links) -->
[contributors-shield]: https://img.shields.io/github/contributors/thgossler/MarkdownGitDiffMarker.svg
[contributors-url]: https://github.com/thgossler/MarkdownGitDiffMarker/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/thgossler/MarkdownGitDiffMarker.svg
[forks-url]: https://github.com/thgossler/MarkdownGitDiffMarker/network/members
[stars-shield]: https://img.shields.io/github/stars/thgossler/MarkdownGitDiffMarker.svg
[stars-url]: https://github.com/thgossler/MarkdownGitDiffMarker/stargazers
[issues-shield]: https://img.shields.io/github/issues/thgossler/MarkdownGitDiffMarker.svg
[issues-url]: https://github.com/thgossler/MarkdownGitDiffMarker/issues
[license-shield]: https://img.shields.io/github/license/thgossler/MarkdownGitDiffMarker.svg
[license-url]: https://github.com/thgossler/MarkdownGitDiffMarker/blob/main/LICENSE.txt
