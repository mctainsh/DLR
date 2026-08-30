# Security Policy

Dumb Luck Rides handles live location, so a vulnerability here has a physical dimension that most
web application bugs do not. Reports are welcome and taken seriously.

## Reporting a vulnerability

**Please do not open a public issue.**

Use one of these, in order of preference:

1. **GitHub private vulnerability reporting** — the *Security* tab → *Report a vulnerability*. This
   is the preferred channel because it keeps the report, the fix and the advisory in one place.
2. **Email** — `security@dumbluckrides.example`. If you want to encrypt, say so in a first message
   with no detail in it and a key will come back.

Please include enough to reproduce: the endpoint or screen, what you sent, what you got back, and
what you expected instead. A proof of concept against your own account is ideal. If you found it
against a deployed instance rather than a local build, say which one and roughly when.

## What to expect

| Stage | Target |
|---|---|
| Acknowledgement that a human has read it | 3 working days |
| An initial assessment — severity, whether it reproduces, likely shape of the fix | 10 working days |
| Fix released for a confirmed high-severity issue | 30 days, sooner if it is being exploited |
| Public advisory and credit, if you want it | With the fix, or on your timeline if you prefer |

This is a small project run by volunteers. If a deadline above is going to slip you will be told,
rather than left to guess.

## Scope

**In scope** — anything in this repository, and any instance the maintainers run:

- The ride membership check. It is the only thing between an account and a stranger's live
  location, so anything that reaches a position without an admitted membership is the highest
  severity class in the project.
- Authentication and session handling — token issuance, refresh rotation and reuse detection,
  lockout, the web session cookie.
- The two untrusted-input parsers: **GPX import** and **image ingest**. These are the only places
  the server parses a file a stranger chose the bytes of.
- Anything that causes a shared profile field, an email address or a position to reach someone the
  organiser never admitted.
- Anything that lets one account read, modify or delete another account's tracks, markers or
  comments.
- Stored data that should have been deleted: a position surviving a rider turning sharing off or
  being removed, an idle position the nightly sweep never reclaims, an account deletion that leaves
  blobs behind.

**Out of scope:**

- Findings from an automated scanner with no demonstrated impact. Please reproduce it by hand
  first.
- Missing security headers, cookie flags or TLS configuration on an instance that is not run by the
  maintainers.
- Denial of service by volume. The project runs on one small VPS and does not pretend otherwise.
- Social engineering, physical attacks, or anything requiring a compromised device the user already
  owns.
- Self-XSS, clickjacking on pages with no state-changing action, and missing rate limits on
  endpoints that change nothing.
- The known and documented trade-off that **an account registered without an email address cannot
  be recovered**. That is a product decision, stated to the user at registration.

## Testing rules

Test against **your own accounts and your own rides**, on a local build wherever possible. Do not
test against other people's data, do not run automated scanners against a hosted instance, and stop
as soon as you have shown a problem exists — you do not need to prove how far it goes.

Good-faith research that follows the above will not be met with legal action.

## Where the design already says what it is doing

The [design outline](Documentation/design-outline.md) is public and detailed on purpose. §7.8
covers abuse resistance, §10.1 covers what is stored and what is deleted, §15.3 covers hostile GPX,
and §16.4 covers image ingest. If you think one of those sections describes something unsafe — as
opposed to something implemented unsafely — that is also worth reporting, and it goes to the same
address.
