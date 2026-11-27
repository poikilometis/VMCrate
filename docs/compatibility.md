# Compatibility and release verification

VMCrate targets Windows 11 x64 and the local Hyper-V PowerShell module. Windows Sandbox and PowerShell Direct availability are recorded separately.

| Build | Source | Observed result |
| --- | --- | --- |
| 0.4 | Local Hyper-V acceptance | Three automated groups passed. Interactive installation and network checks were tracked separately. |
| 0.6.0-preview.2 | Local test fixtures and extracted release | Windows adapter checks, application tests, localization, desktop startup and offline CLI validation passed. Live acceptance was blocked at host readiness. |
| 0.6.0-preview.3 | Local tests and Kali 2026.3 installer | Local application and adapter checks include working-disk transfer and queue recovery. Kali EFI seed generation and the upstream checksum signature were verified. Live acceptance was blocked at host readiness. |
| 0.7.0 | Physical Hyper-V on Windows build 26300 x64 | Windows and Kali installers started, and both prepared guests booted Offline. Interrupted ISO copy, publisher download resume, outbox handoff, Private lab disk recovery and a complete Offline restore were verified. |

Run `tests/Start-HyperVAcceptance.ps1` with local, verified fixture images. The installer group interrupts and resumes an ISO copy and starts the installers. The clone group exercises Offline and Private networking, guest outbox attachment, disk recovery and restore from a lab backup. Add `-CheckWindowsGuest` to check the Windows guest's inbox, routes and CBS inventory. The compatibility receipt records the OS build, VMCrate version, group results and elapsed time. Attach it with the affected workflow when reporting a regression.

The 0.7.0 restore check used a 47.9 GiB lab backup and recorded both guest heartbeats in 307 seconds, including cleanup. Acceptance continued after shortening the Hyper-V configuration path for the drill. The run covered Hyper-V startup and isolation settings. Full interactive installations, authenticated guest probes, guest file inspection and changing the host VPN remain separate checks.

The manually dispatched Hyper-V workflow uses a dedicated Windows x64 runner with the `vmcrate-hyperv` label. Set `VMCRATE_TEST_FIXTURES` to the fixture JSON path on that runner. Use the exported receipt to record each result in the compatibility matrix.

## Signed releases

Run `scripts/New-Release.ps1 -SigningThumbprint <store-thumbprint>` with a code-signing certificate in the current user's Windows certificate store. This produces a detached CMS signature over the ZIP checksum manifest. Publish the ZIP, manifest and signature together. Publish the SHA-256 fingerprint of the signing certificate through a trusted release channel.

Run `scripts/Test-Release.ps1 -Archive <archive> -SignerSha256 <trusted-fingerprint>` to verify the signer, filename and archive hash. For reproducible local previews, the ordinary release command emits a ZIP and checksum.

## GitHub tag releases

Set `Version` in `Directory.Build.props`. Validate the changes in a release branch and merge to `main` before pushing a matching `v<version>` tag. CI lints the workflows, tests the app and PowerShell adapter, verifies the package checksum, and launches the CLI and desktop from the extracted ZIP. The publish job attests that ZIP, verifies its provenance and GitHub upload hashes, and publishes the release. A `-preview.N` tag appears as a prerelease. [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)

To sign the VMCrate executables and checksum manifest, configure the repository secrets `VMCRATE_SIGNING_PFX` with a base64-encoded code-signing PFX and `VMCRATE_SIGNING_PASSWORD` with its password. Set the `VMCRATE_TIMESTAMP_SERVER` repository variable to the certificate provider’s timestamp URL. The tagged build verifies the timestamped Authenticode signatures and publishes the detached `.sha256.p7s` with a signed package.

## Workspace schema

VMCrate 0.7 writes catalog schema 9. Use **App updates** to verify an official stable ZIP with GitHub CLI and stage a separate copy. Opening the staged copy preserves the previous catalog before migration. The rollback action restores that catalog when its SHA-256 still matches the migration receipt. For a workspace changed after migration, use a verified backup or keep using the newer schema.
