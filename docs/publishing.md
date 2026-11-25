# Publishing to NuGet

Every CI run packs all ten packages and checks them (`eng/check-packages.cs`); the packages are kept as the `packages` artifact of each run for 7 days. Nothing reaches nuget.org until a `v*` tag is pushed, which runs `.github/workflows/publish.yml`.

## One-time setup

The publish workflow uses [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): nuget.org trusts this repository's workflow and hands it a short-lived API key for each run, so no long-lived key is stored in GitHub.

1. **On nuget.org**, signed in as the account that will own the packages, open *Trusted Publishing* and add a policy:
   - Repository owner: `hurshah7862`
   - Repository: `RagDotNet`
   - Workflow file: `publish.yml`
   - Environment: `nuget`
2. **In GitHub**, under *Settings → Secrets and variables → Actions → Variables*, add the repository variable `NUGET_USER` set to that nuget.org user name (the profile name, not the email address).
3. **Optional:** under *Settings → Environments → nuget*, add yourself as a required reviewer. Each publish run then waits for your approval before it starts.

The `RagDotNet.*` package names were free on nuget.org on 2026-11-24. The first push of each package claims its name for the account behind the policy.

## Releasing

```
git tag v1.0.0
git push origin v1.0.0
```

Tag a commit on `main` whose CI run passed. The publish job then:

1. checks the tagged commit is on `main`, and stops if it is not;
2. builds and runs the unit tests, as CI does;
3. packs every library under `src/`;
4. checks the packages, including that their version matches the tag (`v1.0.0` must give `1.0.0`). A tag that MinVer did not pick up fails here, before anything is pushed;
5. pushes the packages to nuget.org, then their symbol packages (`.snupkg`) in a second step.

nuget.org validates and indexes new packages, which takes a few minutes before they can be installed.

**A failed run is safe to re-run.** Both pushes use `--skip-duplicate`, so packages and symbols that already reached nuget.org are skipped and the rest are pushed. Symbols are pushed separately so that a re-run still sends the symbols of a package that reached nuget.org in an earlier run. A version on nuget.org can never be replaced, only unlisted: to fix a released package, release a new version.

## Versions

- MinVer computes the version from the latest `v*` tag reachable from the commit: `v1.2.0` gives `1.2.0`. Commits after a tag get a prerelease version such as `1.2.1-alpha.0.3`; untagged history gives `0.0.0-alpha.0.N`.
- Every package has the same version, except that `RagDotNet.Keyword.Lucene` and `RagDotNet.DataIngestion` always add a preview label, because the libraries they are built on (Lucene.NET 4.8, Microsoft.Extensions.DataIngestion) are prereleases: `v1.2.0` gives them `1.2.0-preview`, and `v1.2.0-rc.1` gives them `1.2.0-preview.rc.1`. A label that already starts with `preview` is kept: `v1.2.0-preview.1` gives every package `1.2.0-preview.1`.
- A prerelease tag such as `v1.0.0-rc.1` publishes prerelease packages; people get them only when they ask for prereleases.

## What the package check catches

`dotnet run eng/check-packages.cs -- <folder> [--version <version>]` fails, naming the package, when:

- a library is missing, or a package that is not one of the ten (a test project, the sample) is present;
- a package has no symbol package, or its symbol package lacks the .NET 8 or .NET 10 `.pdb`; or it has no MIT license expression, authors, description, tags, project URL, or repository URL and commit;
- a package has no README, or its README would not show on nuget.org as written. The check follows how nuget.org renders it (NuGetGallery's `MarkdownService` and its HTML sanitizer): a link, whether a Markdown link, a reference definition (`[name]: target`, also in a blockquote) or an `<...>` autolink, must be an absolute `http` or `https` URL or a `#` anchor (nuget.org drops the others, `mailto:` included); an image must come from one of the [domains nuget.org trusts](https://learn.microsoft.com/nuget/nuget-org/package-readme-on-nuget-org) or be a GitHub workflow badge; and any HTML tag (`<p align="center">`, `<br>`, `<details>`, `<img>`) fails, because nuget.org shows raw HTML as text. Generic types in prose such as `IChunker<T>` are not HTML. Like nuget.org, the check first removes every HTML comment, from `<!--` to the next `-->`, even one that starts inside code. Fenced code blocks and inline code are not checked; indented code blocks still are, so links, images and HTML in them can fail, and a footnote definition (`[^1]: text`) is not a link, though links inside its text are checked. The trusted domains are copied into the check; if nuget.org changes its list, update `TrustedImageHosts` in `eng/check-packages.cs`;
- the .NET 8 or .NET 10 assembly or its XML documentation is missing;
- `RagDotNet.VectorData.Sqlite` lacks its sqlite-vec natives for linux-x64, osx-arm64 or win-x64, or its third-party notices;
- the versions break the rules above, or a stable package (or Core at any version) depends on a prerelease.

Run it locally after `dotnet pack --configuration Release --output artifacts/packages`, into an empty folder: packages left from an earlier commit have another version, and the check fails on two versions of one package.
