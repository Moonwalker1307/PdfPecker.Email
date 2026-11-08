# Releasing

PdfPecker.Email has its own version, starting at 0.1.0; it is not tied to PdfPecker's version. Each release names the
PdfPecker version it was built and tested with in `Directory.Packages.props`.

1. Merge a pull request that sets `<Version>` in `Directory.Build.props` to the new version (CI packs with the
   release tag's version, so this is only the local default) and updates the README for what changed.
2. The owner creates the GitHub release `v<version>`. The `publish` job of `build.yml` builds, tests, packs and pushes
   the package to nuget.org with Trusted Publishing (environment `nuget`, repository variable `NUGET_USER`; nuget.org
   policy: owner Moonwalker1307, repository PdfPecker.Email, workflow `build.yml`, environment `nuget`).
3. After the first release is on nuget.org, a pull request adds
   `<PackageValidationBaselineVersion>` with that version to `src/PdfPecker.Email/PdfPecker.Email.csproj`, so `dotnet
   pack` rejects breaking API changes from then on; later releases raise it.

Agents never publish, tag or create a release.

## After a PdfPecker release

A pull request bumps `PdfPecker` in `Directory.Packages.props`, runs the tests, looks at the PDFs of the fixtures, and
passes through any new library option that matters for mail.
