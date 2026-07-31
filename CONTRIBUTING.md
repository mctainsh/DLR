# Contributing to Dumb Luck Rides

Contributions are welcome. This document is short on ceremony and specific about the three things
that will actually get a pull request rejected: the licence terms, the sign-off, and the tests.

## Licensing — inbound equals outbound

**Your contribution is licensed under the project's outbound terms**: AGPL-3.0-only
([`LICENSE`](LICENSE)) **including** the app-store additional permission
([`LICENSE.exceptions`](LICENSE.exceptions)). No copyright assignment is asked for and there is no
CLA.

There is no CLA because a CLA exists to let the maintainer relicense other people's work, which is
a real thing to ask of someone fixing a typo. Inbound-equals-outbound achieves what is actually
needed — every line in the tree carries the same terms, including the store permission, so no
contributed file can quietly make store distribution unsound. The accepted trade-off is that a
future relicence would need every contributor's agreement. That is the correct default.

## Sign your commits (DCO)

Every commit must carry a `Signed-off-by` line asserting the
[Developer Certificate of Origin](https://developercertificate.org/):

```bash
git commit -s -m "Your message"
```

which appends:

```
Signed-off-by: Your Name <your.email@example.com>
```

CI checks this and will fail the pull request without it. To fix a branch you already wrote:

```bash
git rebase --signoff main
```

Use a real name and a reachable email. The DCO is a statement about provenance, so a pseudonym you
actually go by is fine and `nobody@localhost` is not.

## Getting a build

**Prerequisites:** .NET 10 SDK and Docker. Nothing else — no credentials, no seed data, no access
to anything the maintainers run.

The solution lives in `Web/`, one level below the repository root that holds the shared build
settings and the licence gate. Run these from the repository root:

```bash
dotnet tool restore
dotnet restore Web/DLR.sln
dotnet build Web/DLR.sln
dotnet test Web/DLR.sln   # needs Docker running: tests spin up a throwaway PostgreSQL container
```

Every test runs against a Testcontainers PostgreSQL instance and a fake email sender. That was
chosen for test isolation and happens to be exactly what an outside contributor needs.

## How the work is done

**Tests come first, and this is not a slogan.** No production type is introduced without a red test
that demanded it. [`Documentation/tasks-server.md`](Documentation/tasks-server.md) is the ordered
task list, and each task names the test to write first, in the order to write them. If you are
adding behaviour, the pull request should contain the test that fails without your change.

Watch it fail for the right reason before you make it pass. A test that fails because a type does
not compile has not told you anything yet.

**If a rule matters, it gets a test rather than a paragraph.** `DLR.Architecture.Tests` holds the
conventions the project cannot afford to lose to a busy afternoon — no MAUI reference in the shared
UI, no `DateTime.UtcNow` outside the test support project, no second GPX parser, no image decode
path outside `DLR.Server/Photos/`. If you are proposing to change one of those rules, change the
test in the same pull request and say why in the description.

## Style

- **Tabs, width 4.** Enforced by `.editorconfig` and `EnforceCodeStyleInBuild`. The YAML and
  Markdown carve-outs in that file are mandatory rather than stylistic — tabs are invalid in YAML
  and change meaning in Markdown.
- `dotnet format Web/DLR.sln --verify-no-changes` must be clean. CI checks it; run it before you
  push.
- Razor markup indentation is convention plus review — the formatter reaches the C# in a `.razor`
  file but not the markup around it.

## Dependencies

**Permissive or nothing.** MIT, BSD, Apache-2.0, PostgreSQL and MS-PL are fine. AGPL can absorb
permissive dependencies but not the reverse, so no GPL-incompatible copyleft, no
source-available-but-not-open licences, and no packages that require a paid licence — a
licence-gated test dependency would mean every outside contributor has to buy something before
running `dotnet test`.

CI runs a licence scan over the full transitive package graph and **fails on any licence outside
the allow-list, including a licence it cannot determine**. The allow-list lives in the repository
as data ([`build/licences/allowed-licences.json`](build/licences/allowed-licences.json)) and adding
to it is a reviewed change — which is the moment to think about it, rather than during a legal
review six months later.

Two live examples of the rule working: **Shouldly rather than FluentAssertions**, whose v8 requires
a paid commercial licence; and **SkiaSharp rather than ImageSharp**, whose v3+ ships under the Six
Labors Split Licence. Both were decisions rather than defaults, and the gate is what forced them to
be.

## Things that will not be merged

- **Real GPX files from your own rides.** They start and end at your house, and this repository is
  public. Test fixtures are synthetic, generated in code — which is better for tests anyway, since
  you can construct tunnel gaps and GPS spikes rather than hoping to ride into them. There is a
  gitignored `fixtures/private/` if you need a real trace locally.
- **Anything from `.gitignore`'s secrets section.** Signing material, connection strings, API keys,
  `appsettings.Production.json`. If one lands in a commit, say so immediately: the fix is rotation,
  not deletion, because git history is permanent in practice.
- **A notification path for ordinary comments during a live ride.** The people this app notifies
  are operating vehicles. That rule has a test, and the test is not a bug.

## Pull requests

Small, focused, and described in terms of behaviour. Say what the change does, which design section
it comes from, and — if the design no longer describes what the code does — update the design in
the same pull request. Leaving the document wrong is a real cost, not a tidiness issue.

If you are planning something large, open an issue first. The design outline is opinionated and
there is usually a reason; it is written down, and it might already be in there.

## Security

Do not open a public issue for a security problem. [`SECURITY.md`](SECURITY.md) has the private
channel.
