# Signing certificates

## What goes in git?

| File | Commit? | Why |
|------|---------|-----|
| `*.pfx` / `*.p12` | **No** | Private key. Anyone with the file + password can sign as you. |
| `*.cer` | Optional | Public only. Safe to share so users can trust the publisher. |
| This README | Yes | Documents the policy. |

`Package.ps1` will create `devcert.pfx` (+ `devcert.cer`) here if they are missing:

```powershell
.\Package.ps1
# certs\devcert.pfx  (private, gitignored)
# certs\devcert.cer  (public, also gitignored by default)
# dist\LinuxDo-Signing.cer  (copy for Release install)
```

## Why not commit the PFX?

- Public repo → private key leak = permanent trust / supply-chain risk.
- Each machine can generate its own dev cert for local sideload testing.
- CI should store the release PFX as a **GitHub Actions secret**, not in the tree.

## How users install without your private key

Release assets should include:

1. `LinuxDo_<BuildId>_x64.msix` (signed with your PFX)
2. `LinuxDo-Signing.cer` (public cert only)
3. `Install-LinuxDo.ps1` (trusts the .cer, then `Add-AppxPackage`)

Users never need the `.pfx`.

## Optional: stable publisher for all releases

If you want every Release signed with the **same** identity:

1. Generate once: `.\Package.ps1` (or `winapp cert generate ...`)
2. Back up `certs\devcert.pfx` offline + password manager
3. Put PFX bytes + password in CI secrets (e.g. `SIGNING_PFX_BASE64`, `SIGNING_PFX_PASSWORD`)
4. Ship the matching `.cer` with every Release (or commit only the `.cer` under `certs/` by removing the `*.cer` ignore line)

Do **not** put the PFX in git even then.

## Local trust (developers)

```powershell
# Admin PowerShell — once per machine / per cert
winapp cert install .\certs\devcert.pfx --password password
# or:
.\Package.ps1 -InstallCert
```
