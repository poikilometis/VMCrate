# Compatibility and release verification

VMCrate targets Windows 11 x64 and the local Hyper-V PowerShell module. Windows Sandbox and PowerShell Direct availability are recorded separately.

| Build | Source | Observed result |
| --- | --- | --- |
| 0.4 | Local Hyper-V acceptance | Three automated groups passed. Interactive installation and network checks were tracked separately. |
| 0.6.0-preview.2 | Local test fixtures and extracted release | Windows adapter checks, application tests, localization, desktop startup and offline CLI validation passed. Live acceptance was blocked at host readiness. |
| 0.6.0-preview.3 | Local tests and Kali 2026.3 installer | Local application and adapter checks include working-disk transfer and queue recovery. Kali EFI seed generation and the upstream checksum signature were verified. Live acceptance was blocked at host readiness. |

Run `tests/Start-HyperVAcceptance.ps1` with local, verified fixture images. The clone group tests a recovery point across two private lab VMs and starts an imported Linux system disk. The installer group verifies attachment of a generated Kali setup disc. Add `-CheckWindowsGuest` to check the Windows guest's inbox, routes and CBS inventory. Each attempted run writes `compatibility.json` with the OS build, VMCrate version, feature availability and result. Use the receipt when reporting a regression. Attach it with the guest family and the affected workflow. Remove private details from any optional logs.

The manually dispatched Hyper-V workflow uses a dedicated Windows x64 runner with the `vmcrate-hyperv` label. Set `VMCRATE_TEST_FIXTURES` to the fixture JSON path on that runner. Use the exported receipt to record each result in the compatibility matrix.

## Signed releases

Run `scripts/New-Release.ps1 -SigningThumbprint <store-thumbprint>` with a code-signing certificate in the current user's Windows certificate store. This produces a detached CMS signature over the ZIP checksum manifest. Publish the ZIP, manifest and signature together. Publish the SHA-256 fingerprint of the signing certificate through a trusted release channel.

Run `scripts/Test-Release.ps1 -Archive <archive> -SignerSha256 <trusted-fingerprint>` to verify the signer, filename and archive hash. For reproducible local previews, the ordinary release command emits a ZIP and checksum.

## Workspace schema

VMCrate 0.6.0-preview.3 writes catalog schema 7. Keep a copy of a schema 6 workspace when upgrading if you need to return to preview.2. The copied offline library and project ZIP workflows provide portable templates and metadata.
