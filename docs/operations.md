# Host and lab operations

The workflows are available under **Lab tools**. VMCrate reads workspace schemas 1–7 and writes schema 7 on the next change. The previous catalog is retained as part of the existing workspace backup workflow.

## Choose a workspace

At first launch, select a local folder with enough space for installation images and virtual disks. Check the reported Hyper-V and Windows Sandbox status. Opening an existing workspace requires selecting the folder that already contains its catalog. To reopen the status panel, choose **Workspace setup** under **Lab tools**.

## Find and verify official media

Choose a publisher channel in **Official image catalog**. Automated channels list the x64 ISOs they publish in official HTTPS feeds. These include Ubuntu and flavors, Debian stable, Fedora, Kali stable and weekly, Alpine, Arch, Rocky, AlmaLinux, CentOS Stream and openSUSE Tumbleweed. Choose a variant and select **Download and verify**. On interruption, retry that variant to resume if the server supports byte ranges. A matching SHA-256 places the ISO in the workspace cache and opens the build form with the digest filled in.

For Windows and other downloads served through a language selector, evaluation form or subscription page, use **Open official download page** and **Use a local ISO**. Enter the publisher's SHA-256 when available. A hash you compute in the build form pins the local bytes for future checks. For Kali stable and weekly feeds, VMCrate verifies the detached checksum signature with an installed GnuPG executable and the pinned Kali signing fingerprint. The catalog distinguishes locally calculated hashes, publisher checksums and publisher signatures. It records the source on templates built from a verified cache entry. Prepared Hyper-V VHDX files use **Templates → Import VHDX**. [Microsoft downloads](https://www.microsoft.com/software-download/) [Debian checksum verification](https://www.debian.org/CD/verify) [Kali download verification](https://www.kali.org/docs/introduction/download-official-kali-linux-images/)

## Recover from an interruption

Open **Workspace recovery** to compare the catalog, registered Hyper-V resources and local files. Select a finding to review the VM, switch or file and the proposed action. Confirm one repair at a time. VM creation can resume with the same identity or acknowledge a matching VM that was already created. A stopped builder with drift in its update proxy policy can be returned to Offline. Only an owned private segment with no catalog members, active VM attachments or checkpoint references is eligible for removal.

The workspace keeps the previous catalog in `state.previous.json`. When the active catalog cannot be loaded, the recovery view can restore the previous copy for that workspace and save a damaged original as `state.failed-<id>.json`. Review the inventory again afterward. Extra disks, unmatched VM registrations and working directories are listed for review. If a clone registration is missing and its base disk is available, review **Recreate the missing clone from its template**. This creates a new working disk for the catalog entry and leaves the previous disk available for review.

## Host diagnostics and support

**Workspace setup** displays the Windows build, edition and free physical RAM. Review its guidance if firmware virtualization is disabled or a servicing restart is pending.

Refresh to see Hyper-V access, Enhanced Session, configuration findings, available disk space, internal switch prefixes, visible WinNAT instances and overlapping routes. Enter a public IPv4 address to see which Windows interface the route lookup selects. Tunnel and VPN detection uses the reported interface type and name. The export contains counts and statuses and omits host paths, IP addresses, interface names and credentials.

**Check inside Windows VM** uses PowerShell Direct on a running VM you own. Supply credentials for that guest. For Offline, inspect the live adapter and default route counts. For Private, optionally enter the RFC 1918 address of an owned peer on the same segment and check the ICMP response. A missing ping response can reflect filtering at the peer. PowerShell Direct must already work on the host and guest. [Microsoft PowerShell Direct](https://learn.microsoft.com/windows-server/virtualization/hyper-v/powershell-direct)

Open **Isolation checks** from the VM or **Lab tools** to compare its selected profile with attached switches, private segment membership, guest integration services and the inbox. Each reading shows its time and source. The VMConnect row reads the host policy. With Enhanced Session allowed, the active session's redirection is unknown. Windows Sandbox displays the requested Offline profile pending guest verification.

On a running Windows Offline or Private VM, **Check inside Windows VM** reads IPv4 and IPv6 default route counts, DNS server configuration and inbox presence via PowerShell Direct. Use credentials for that guest. For an ICMP measurement, use the peer probe under **Host diagnostics** with an owned Private peer.

Use **Support package** to preview the exact JSON contained in the ZIP. Select a machine for its reproduction settings and recent operation codes, or choose the workspace summary. The export includes typed status, capacity and operation fields. **Bug report draft** opens an editable Markdown report with those observations and fields for the steps, expected result and observed result. Use **Security reporting** to open the local disclosure policy.

## Build a versioned template

Choose **Check disk space** on the new build form to see the available space, additional ISO cache size and maximum growth if both the working disk and captured disk reach the configured capacity. The cache and capture steps check the space required for their actual copy. The progress bar shows bytes processed, and **Cancel** leaves the job at its current stage.

1. Open **Template builds** and select **New build**. Choose a local installation ISO, enter its published SHA-256, and optionally enter a template revision.
2. Select **Continue build** to verify the ISO. VMCrate places a checked copy in the local media cache under its SHA-256. Select **Continue build** again to create the builder.
3. Start the builder and install the guest through VMConnect. For Windows, you can supply a trusted `Autounattend.xml` when creating the job. VMCrate mounts it on a separate read-only disk. Keep answer files with credentials outside source control. [Microsoft Windows Setup automation](https://learn.microsoft.com/windows-hardware/manufacture/desktop/windows-setup-automation-overview?view=windows-11)
4. For Kali amd64, use **Create Kali setup disc** after selecting an official installer ISO and entering its SHA-256. Choose a local account and a unique password. VMCrate reads the verified ISO, writes a separate UEFI setup disc with a salted password hash, and fills in the seed path and checksum. Keep the setup ISO private. During the first boot the builder partitions its new disk and installs the offline Xfce profile. Use **Eject installer** after the installer powers off. You can also select an existing trusted seed ISO and its SHA-256. [Kali automation reference](https://www.kali.org/docs/installation/network-pxe/)
5. Boot the guest from its system disk. Select **Continue build** after Hyper-V shows a heartbeat. Prepare Windows with Sysprep if applicable, then shut the guest down and capture the disk.
6. Run the Offline clone boot test. A successful heartbeat and host isolation check publish the captured template.
7. Reopen the build after closing the app or a failed stage. The VM keeps its identity and the history shows the stage and failure. A finished or unattached build entry can be removed after review.

Use **Build queue** to prepare cached media and stopped builders in order. The limits cover RAM, vCPUs and conservative working disk and capture space. Open the individual build to start the VM and finish installation.

## Proxy mode for updates

The builder must be stopped. Open **Updates proxy**, choose a preconfigured internal Hyper-V switch backed by an existing WinNAT subnet, and enter the public IPv4 address and TCP port of a trusted HTTP proxy. Set the access duration in minutes. The confirmation displays the selected Windows route. A local VMCrate process closes the expired lease and verifies disconnection. A running builder keeps its power state. Workspace refresh also reconciles expired leases, and startup rejects an expired policy. VMCrate verifies the adapter policy while the NIC is disconnected. That policy grants outbound TCP to the specified endpoint and denies other IPv4 and IPv6 traffic. Its stateful allow rule carries return traffic. [Microsoft Hyper-V extended ACLs](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/create-security-policies-extended-port-access-control-lists)

Configure the guest address, prefix and gateway for that existing network. Configure the HTTP proxy and its authentication inside the guest. WinNAT requires an address configuration in the guest. The policy limits the VM to the selected endpoint and the upstream proxy governs destinations reached through it. Return the stopped builder to **Offline** after updating. Capturing a template through the build wizard returns its builder to Offline. [Microsoft WinNAT configuration](https://learn.microsoft.com/virtualization/hyper-v-on-windows/user-guide/setup-nat-network)

## Trace update proxy connections

From a builder in Updates proxy mode, open **Proxy diagnostics**. **Check host path** reports the selected host interface, VPN indication, internal switch, overlapping routes, Hyper-V proxy policy and TCP reachability. For the guest check, start the builder and pick an HTTPS repository. An optional local relay port checks an HTTP relay at 127.0.0.1, such as a relay configured inside the guest.

For Windows, enter the guest credentials for PowerShell Direct and supply proxy credentials if required. You can store validated proxy credentials for the configured endpoint in Windows Credential Manager and load them for another test. For Linux, choose **Mount Linux probe**, open VMConnect and run `bash VMCrate-proxy.sh` from the mounted read-only inbox. Run it in an interactive terminal when using upstream proxy credentials. Enter the `VMCRATE_PROXY1` result line in the diagnostic window. Review the guest route and neighbor state, direct and relay TCP, CONNECT status and the repository response. Run the host check again after changing the connection to compare the selected route to the previous observation in the window. The diagnostic sends a single HTTPS HEAD request through the configured proxy. [PowerShell Direct](https://learn.microsoft.com/windows-server/virtualization/hyper-v/powershell-direct)

## Manage a lab

Use **Blueprint editor** to create or edit a local JSON blueprint in a table. Save it and open it in the existing deployment preview. In **Manage labs**, select a deployed blueprint and review its members, segments and configured memory and CPU total. Choose a group operation and preview the affected VMs and files before applying it. Reset and removal require stopped VMs. The app rechecks membership and state before applying the operation. It removes an owned private switch only if no VM or checkpoint references it.

Choose **Network topology** to inspect switches and review proposed private IPv4 addresses. The Windows apply action configures a running VM through PowerShell Direct when its owned switch matches the current plan. It assigns an address without a gateway. For a Linux guest, use the exported address plan with its configured network manager.

## Save recovery points

Create ordinary clones for persistent work. Stop the VM and choose **Recovery points** to save a named copy of its working disk. In **Manage labs**, the same action captures all stopped VMs in the selected lab. Restore revalidates template hashes and member identities before replacing attachments. Previous working disks appear in **Workspace recovery**. If a restore is interrupted, resume the same journal entry before starting the affected VMs.

A recovery point captures working disks. Full VM exports require separate handling for firmware, virtual TPM and memory state. In **Template revisions**, verify the selected revision before creating Offline trial copies of ordinary clones. Use the advisor to compare two package inventories for the same ecosystem. **Collect Windows updates** reads the OS build and installed CBS hotfix IDs from a running Offline clone through PowerShell Direct. Use that inventory for a local comparison. For a Linux template, open **Collect Linux packages** and mount the collector on an Offline clone. Run the displayed command at the actual DVD mount point. It writes version 1 JSON from the local package database. Provide that JSON to **Compare inventories** through your lab’s reviewed evidence process. The collector uses no guest network or reverse file-share integration. Debian stable and Ubuntu LTS use source package names and versions for the matching OSV feeds. Kali, RPM-based distributions, Alpine and Arch use local comparisons. [OSV ecosystems](https://ossf.github.io/osv-schema/#affectedpackage-field) [Microsoft Get-HotFix](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/get-hotfix)

## Review storage and run a clean session

**Storage** shows file sizes for template disks, working disks, inboxes and cached ISOs, along with saved recovery points and free space on the workspace volume. Template rows list revisions and dependent VMs. ISO cleanup checks both build jobs and attached media before removing a cached copy.

**Clean session** opens an offline disposable VM, read-only inbox, VMConnect, report export and session end controls in one window. Set the name, template, memory and processors, then use **Launch profiles → Save current settings** to reuse that configuration. Select a saved profile and choose **Save Windows shortcut** to open a prefilled clean session from Explorer. The shortcut opens a review form before VM creation. Select and stage files before start. Their recorded hashes come from the copies in the inbox. Export the report before ending the session or shutting down its guest. The app reclaims started sessions after it observes them off. Open **Console and inbox** to send a note or files on the read-only DVD, review the VMConnect host policy and check display and graphics details. In **Session report**, compare the current evidence with a saved session report.

## Prepare an offline library

Open **Portable projects → Copy template library** for checked copies of published templates, cached ISOs, launch profiles, recipes and the local guide. VMCrate creates a new workspace with a checksum manifest. Verify it with the bundled CLI before opening the destination catalog. Use a project ZIP to carry a selected topology and its template disks. The library copies prepared assets. Use **Move a virtual machine** for the installed system and data of an existing VM.

## Move a virtual machine

Shut down an ordinary VM and open **Lab tools → Move a virtual machine**. Export creates a folder with `vm.json` and a standalone `system.vhdx`. Hyper-V converts a detached copy of the working disk and its parent if the current disk is differencing. The copy can require up to the full virtual disk size, the size of the working disk and temporary space. [Microsoft Convert-VHD](https://learn.microsoft.com/powershell/module/hyper-v/convert-vhd)

Copy the folder to the destination host and select `vm.json` in the import form. VMCrate checks the manifest and disk, then registers a new, stopped Offline VM with the configured RAM, CPUs and Secure Boot setting. Import assigns fresh VM hardware and Windows TPM identity. Keep the recovery key for a disk encrypted by the source guest. Firmware variables, checkpoints, saved memory and virtual switch membership stay associated with the source environment. The system disk must boot using its UEFI files under the newly created firmware. An interrupted registration is available in **Workspace recovery**.

## Share a Hyper-V host

**Team queue** uses a dedicated host process and per-operator client certificates. Deploy, start and power off approved labs in each operator’s own workspace. The host journals job receipts and preserves the assigned scope on retry. [Team queue setup](remote-queue.md)
