# The licence gate

AGPL-3.0 can absorb permissive dependencies but not the reverse, so the standing rule is
**permissive or nothing** (§14.6.3). This folder is that rule as data, and
`.github/workflows/ci.yml` is what makes it a build failure rather than a convention.

Run it locally exactly as CI does — **from the repository root**, which is what makes the two
`build/licences/` paths resolve. The solution itself is one level down, in `Web/`:

```bash
dotnet tool restore
dotnet restore Web/DLR.sln
dotnet nuget-license \
  -i Web/DLR.sln \
  --include-transitive \
  --allowed-license-types build/licences/allowed-licences.json \
  --licenseurl-to-license-mappings build/licences/licence-url-mappings.json
```

`dotnet restore` first is not optional: the scan reads `project.assets.json`, so without a
restore it reports the previous dependency graph and can pass on a package you just added.

## `allowed-licences.json`

SPDX identifiers, and only SPDX identifiers. **Adding to this list is a reviewed change** —
which is precisely the moment to think about it, rather than six months later during a legal
review.

The gate fails on a licence outside the list **and on a licence it cannot determine**. The
second half matters more than the first: a package that declares its licence as a file rather
than an expression is not a package with no licence, it is a package nobody has read.

## `licence-url-mappings.json`

Packages published before SPDX expressions existed declare a licence URL instead. Each entry
here is a human saying "I opened that URL and it is this licence". Two live examples:

- `xunit.abstractions 2.0.3` points at xunit's `license.txt`, which is Apache-2.0.
- `http://go.microsoft.com/fwlink/?LinkId=329770` is the Microsoft .NET Library EULA, which is
  **proprietary**. It is mapped to an identifier deliberately absent from the allow-list so
  that a package carrying it fails by name rather than as an anonymous unknown.

## What the gate has already caught

Both of these were watched go red before this file was committed, which is the only way to
know a gate works:

- **FluentAssertions 8.0.0** — Xceed's commercial licence, declared as a file the scanner
  cannot read. It fails as *unknown*. This is why §10.4 specifies Shouldly: a licence-gated
  test dependency would mean every outside contributor has to buy something before running
  `dotnet test`.
- **MySql.Data** — `GPL-2.0-only WITH Universal-FOSS-exception-1.0`, a licence the scanner
  reads perfectly well and the allow-list rejects.

## What the gate cannot see

It scans NuGet, not npm. **MapLibre GL JS** (BSD-3-Clause) is a review-time judgement, and it
is the same decision as Shouldly over FluentAssertions made about a JavaScript dependency —
MapLibre is the community fork created when Mapbox GL JS v2 went proprietary. Take the fork.
