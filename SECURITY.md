# Security

VMCrate is a tool for managing local Hyper-V labs. It relies on Windows, Hyper-V, host configuration and the integrity of the installation media and base templates. Keep the host patched and use a test host for untrusted guests.

## Isolation behavior

- New VMs have a disconnected virtual network adapter. Private mode connects them to a Hyper-V private switch, where peers can communicate. Keep guests that span different trust boundaries off the same switch.
- Inbox images are read-only virtual DVDs. VMCrate disables the guest file-copy and key-value exchange integration services for managed VMs.
- VMConnect follows host Enhanced Session policy. Use Basic Session for isolated work, and disable Enhanced Session on hosts with stricter isolation requirements. [Microsoft VMConnect guidance](https://learn.microsoft.com/windows-server/virtualization/hyper-v/enhanced-session-mode)
- A blueprint can select existing templates and private switch segments. The schema accepts VM settings and uses only local templates.
- The Windows Sandbox profile disables host device redirection and networking and enables Protected Client. Its configuration exposes no shared folders.
- An evidence export includes only selected file metadata and VM configuration collected on the host. Review names and identifiers before sharing the report.
- Host configuration checks verify the configured settings. Validate network behavior inside a test guest when deploying VMCrate as part of a lab isolation policy.

The app works with an existing Hyper-V installation. Host-wide firewall, Defender, NAT and Enhanced Session policies are administered by the host operator. Keep the VMCrate data directory private and use verified installation media.

## Team queue

The host requires mutual TLS, trusted certificate chains and the configured certificate fingerprints. Keep the endpoint on an authorized private interface and protect the recipe catalog, settings, queue journal and workspace folders with Windows access controls. Each certificate maps to a separate workspace. The server accepts approved recipe IDs and fixed lab operations. It verifies template hashes under the workspace lock before deploy or start.

## Reporting

Use GitHub private vulnerability reporting if it is enabled for the published repository. Include the version, host and guest OS, and a minimal scenario with synthetic VM data. Keep disk images, credentials and private host details out of public issues.
