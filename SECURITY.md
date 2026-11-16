# Security policy

## Supported versions

Only the latest release receives fixes.

| Version | Supported |
| --- | --- |
| 1.0.x | ✅ |

## Reporting a vulnerability

Please **do not open a public issue** for security problems.
Report them privately through GitHub: **Security → Report a vulnerability** on this repository
([private vulnerability reporting](https://github.com/unlimiteq/SideScreen/security/advisories/new)).

Include what is affected, how to reproduce it, and the impact you expect. You should get a reply within a week.

## Security notes

- The PC side listens on `127.0.0.1:38917` only; the tablet reaches it through the `adb reverse` USB tunnel. Nothing is exposed to the network.
- Installing the virtual display driver requires administrator rights once. The driver is the unmodified, signed release from the Virtual Display Driver project.
- The only outbound network request is the GitHub release check, made only when you click **Check for updates**.
- USB debugging gives a connected computer broad access to the tablet; only enable it for computers you trust.
