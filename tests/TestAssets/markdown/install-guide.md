# Install

Quill is a small command line tool for managing release notes. This section explains how to get it onto your machine.

## Linux

On Debian and Ubuntu, run `sudo apt install quill`. Other distributions can download the tarball from the releases page and place the binary on the PATH.

## Windows

On Windows 11, run `winget install quill`. The installer adds quill to your PATH automatically.

# Use

Run `quill init` in a repository to create a notes file, then `quill add "Fixed login bug"` to record an entry. Use `quill publish` to produce the final changelog.
