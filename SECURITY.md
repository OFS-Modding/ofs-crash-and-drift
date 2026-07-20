# Security policy

This project executes third-party mods with full process trust. Mods are not a
sandbox boundary: a native detour, IL2CPP pointer or ordinary .NET API can read
and modify user-accessible data.

Before installing a mod, users should be shown its publisher, hashes and
requested capabilities. Distribution work must use staged extraction, path
confinement, size limits and transactional promotion.

Do not publish exploitable reports involving path traversal, package signature
bypass or arbitrary code execution through supposedly data-only packages until
maintainers have prepared a fix. Until a private reporting address exists, keep
the report local and open a minimal issue asking maintainers for a secure
contact channel.

Game vulnerabilities and anti-cheat bypasses belong with the game developer,
not this repository.
