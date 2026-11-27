# Lab workflows

## Deploy a laboratory from a blueprint

1. Import or capture the templates you want to use. Copy [two-node-lab.json](../examples/two-node-lab.json) and set each `template` to an imported template name.
2. In **Lab tools**, choose **Open blueprint** and review the VM names, templates, CPU, memory and network segments.
3. Choose **Deploy VMs**. The new VMs remain off. Start individual VMs from **Virtual machines** when ready.
4. To resume an interrupted deployment, open the same file again. The preview distinguishes registered VMs from ones still to be created.

Version 1 blueprints contain a lowercase `id` and 1–64 machine definitions. Each machine has a `name`, a local `template` name, `memoryGiB` and `cpuCount`. An optional `segment` groups machines into one private switch within this blueprint and workspace. Omit it for an offline VM. IDs and segment names use 1–32 ASCII lowercase letters, digits and hyphens and begin with a letter or digit.

Each blueprint segment has its own Hyper-V private switch. Assign guest IP addresses manually when practicing peer connectivity. A name collision or a configuration change detected by preflight blocks the deployment. Each plan only provisions new VMs and preserves matching ones.

## Use a disposable virtual machine

Select a template and choose **Create disposable VM**. The session starts with a fresh differencing disk and an offline adapter. Use **Start VM** once, then shut down in the guest or choose **End session**. The latter cuts power after confirmation.

VMCrate deletes the session VM and its working directory after observing it powered off. The main window and the Clean session window check started sessions every 15 seconds while idle. When the app was closed during shutdown, cleanup takes place on the next successful refresh. If the host is unavailable, cleanup waits for a confirmed power state. Use an ordinary clone for work that needs to persist across shutdowns.

## Open the offline Windows Sandbox

Enable the Windows Sandbox feature on your Windows host. Choose **Open offline Sandbox** in **Lab tools** or save a portable `.wsb` profile and open it in Windows. This profile enables Protected Client and disables networking, clipboard, virtual GPU, and audio, video and printer redirection. It establishes an empty workspace without mapped host folders. Windows Sandbox handles its own close and discard lifecycle.

## Verify a template and export evidence

VMCrate records the SHA-256 of an imported or captured VHDX during the copy. Select **Check SHA-256** to compare the current disk with its recorded digest. The first check on a legacy template records its current digest as a baseline.

Choose **Create report** under **Lab tools** or **Export report** on a VM. Select files, a VM, or both. The local JSON file records the names, sizes and SHA-256 of selected files and the observed VM state, expected network mode and hypervisor configuration findings. The report includes UTC timestamps and omits absolute source paths. It records file identity and host settings for review.

## Project references

| Project | Pattern adopted in VMCrate |
| --- | --- |
| [HyperVSecLab](https://github.com/edprybylko/HyperVSecLab) | Hyper-V template and session lifecycle, with scoped cleanup and a template fingerprint |
| [Windows Sandbox](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file) | A configurable disposable Windows workspace with a locked-down `.wsb` profile |
| [Ludus](https://docs.ludus.cloud/docs/using-ludus/blueprints/) | Declarative lab deployment and resume from an existing deployment |
| [Qubes OS](https://doc.qubes-os.org/en/development/developer/services/disposablevm-implementation.html) | Template-based disposables reclaimed after shutdown |
| [CAPE](https://capev2.readthedocs.io/en/latest/usage/results.html) | Structured analysis evidence with a JSON export |
