# Contributing

Build the solution with the .NET 10 SDK on Windows. Run both check commands in the [README](README.md) before submitting. Use `src/VMCrate.Core` for lifecycle and state logic, `src/VMCrate.Windows` for platform operations, and `src/VMCrate.Desktop` for presentation. Keep Hyper-V cmdlets in the adapter.

Use four-space indentation in C# and PowerShell. Use the existing English keys in `locales/en.json` and update all catalogs when adding user-facing text. Use short imperative commit subjects. Describe the reason for the change and include test results and screenshots for UI changes. Use synthetic names and sample files in screenshots and fixtures.

## Automated Hyper-V acceptance

Use a Windows account that can manage the local Hyper-V host. Prepare a local JSON file with `windows` and `linux` objects. Each object has an absolute `iso` path, its publisher's `sha256` and a trusted, prepared Generation 2 `vhdx`. Use standalone VHDX files with Windows Secure Boot enabled or Linux Secure Boot disabled for these checks.

```powershell
.\tests\Start-HyperVAcceptance.ps1 -Fixtures C:\TestMedia\fixtures.json
```

The runner creates a new workspace below `artifacts/hyperv-live` and records its results in `run.log`. It runs the official ISO startup and recovery checks, boots copies of the prepared disks, checks DVD attachment and disk reset, and tests private blueprint cleanup and a small resumed Kali download. The runner also compares live switch attachments and guest integration settings with the selected isolation profile. Add `-CheckWindowsGuest` on an interactive Hyper-V host to read the Windows fixture's default routes and confirm its inbox is accessible. The runner prompts for the fixture credentials. The remaining guest console, proxy and Windows Sandbox checks are listed below.

## Hyper-V host validation

Run on a test host with the Hyper-V role and local installation media. Use a fresh data directory and test guests you own.

1. Import a prepared Generation 2 VHDX, create a clone, and verify its adapter is disconnected in Hyper-V Manager. Boot the clone and open VMConnect in Basic Session.
2. Mount a harmless text file from **Send files** and read it from the guest DVD. Eject the inbox and verify the virtual DVD is empty.
3. Create a file inside the guest, shut it down, reset its disk, and boot again. Verify that the guest file is gone and that the imported parent VHDX has the same checksum.
4. Switch two stopped guests to **Private lab**. Assign test IPs in the guests and verify peer connectivity and the absence of a host virtual adapter for that switch. Return them to **Offline**.
5. Create a VM from a local ISO. Complete setup, eject the ISO, and capture a template from the stopped guest. Verify that its clone boots.
6. Deploy the same two-node blueprint twice. Check that the preview reuses the existing VMs and that a second blueprint with an equally named segment gets a distinct switch.
7. Start a disposable VM and shut down from inside the guest. Confirm that VMCrate removes that VM on refresh. Export a report with a synthetic file and compare its SHA-256.
8. Start Windows Sandbox with the offline profile on a host with the feature enabled. Check networking and clipboard redirection from the guest.
9. Run host diagnostics and compare the selected route with Find-NetRoute on an enabled host. Check one offline test guest through PowerShell Direct and two owned peers on one private segment.
10. Resume a build after closing the app. Complete a disk boot, capture its template, and review the storage graph. With an existing WinNAT test subnet and a trusted update proxy, verify builder access to the approved endpoint and denial of other IPv4 and IPv6 traffic before testing the return to Offline.
11. Extract the release ZIP to a fresh folder, verify its SHA-256 and run the bundled executable. Pick a workspace outside the app folder, restart and confirm the selection persists. Note the status when Windows Sandbox is unavailable.
12. On a dedicated test workspace, interrupt a template build and reopen it. Check **Workspace recovery** and resume the operation. Simulate a corrupted catalog on a separate test workspace with a known backup, restore it from the UI and compare the damaged copy and disk hashes.
13. Add an official ISO from the catalog. Stop the download and retry the same ISO. Confirm the digest, cached file reuse and the prefilled build options. Check one Microsoft media entry using the official download page and a local ISO.
14. On a running update builder, compare the host route and proxy TCP result with the live network. Run the Windows PowerShell Direct probe and the Linux console probe. Check one reachable repository and compare it with a controlled proxy authentication or gateway failure. Confirm the VM returns to Offline after updates.
15. Remove the test VMs and then their templates. Review the private switch before deleting it in Hyper-V Manager if it was created for this run.

Keep host screenshots, local media, runtime catalogs and guest files outside commits.
