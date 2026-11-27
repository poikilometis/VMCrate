# Hyper-V team queue

Run `host/VMCrate.Host.exe` on a dedicated Windows Hyper-V host. The desktop **Team queue** connects to this host over HTTPS and shows only the authenticated operator’s recipes and recent jobs. Each job deploys a stopped lab, starts a lab, or powers off all of its VMs immediately.

## Prepare the host

1. Create one local VMCrate workspace per operator. Import or copy trusted templates into each workspace. Give the Windows account that runs the host access to Hyper-V and those folders. Keep the configuration, recipe catalogs, queue and workspaces writable only by that account and authorized administrators.
2. Copy the [sample settings](../examples/remote-host.json). Paths are relative to this configuration file. Set `recipeCatalog` to `examples/recipes.json` when the settings file sits next to `VMCrate.exe`. Use distinct, non-overlapping workspaces and a separate queue folder. Choose the host’s specific interface address on your private network or VPN and an authorized port. Use `127.0.0.1` for same-host testing.
3. Add a server certificate with Server Authentication usage and the HTTPS name in its SAN. Give each operator a separate Client Authentication certificate. Install each private key in **Current User → Personal** on its respective machine and establish certificate chain and revocation validation on both ends. Use the 64 hexadecimal SHA-256 fingerprints of the certificates, available with `$certificate.GetCertHashString([Security.Cryptography.HashAlgorithmName]::SHA256)`. Keep a stable account ID for each operator.
4. Approve recipes in a local catalog and create a dependency lock for each operator. The blueprint used by the lock must match the approved recipe exactly. A recipe can only select installed templates and Offline or Private networking. [Kestrel HTTPS configuration](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0)

Run these commands in the extracted release directory to pin the bundled recipe and start the worker.

```powershell
$recipe = Get-Content .\examples\recipes.json -Raw | ConvertFrom-Json |
    Where-Object id -eq 'private-two-node'
$recipe.blueprint | ConvertTo-Json -Depth 12 | Set-Content -Encoding utf8 .\private-two-node.json
.\cli\VMCrate.Cli.exe lock .\private-two-node.json C:\VmCrate\Alice .\alice-private-two-node.lock.json
.\host\VMCrate.Host.exe --check .\host.json
.\host\VMCrate.Host.exe --serve .\host.json
```

The worker runs in the foreground under its current Windows account. Use your existing service supervision to keep it available. It processes one job at a time and checks template content against the approved lock before deploy and start. The workspace lock covers that check and the VM operation.

## Submit and recover jobs

Enter the HTTPS address and both certificate fingerprints in **Lab tools → Team queue**. Select a recipe, choose the operation and confirm. Use **Refresh** to read the latest receipt. Duplicate submissions with the same ID return the current receipt. A client sees its own jobs and learns operational outcomes without local server paths or exception details.

The queue records jobs on disk before returning acceptance. After an interruption, review the affected lab in a local workspace and retry from the queue. Retry keeps the same job ID. The server checks the recipe and pinned dependencies again before resuming. The worker refuses an old queued request if its workspace or recipe binding has changed.

The journal holds up to 4,096 receipts, with up to eight pending jobs per operator and 128 pending jobs total. After pending work finishes, an administrator can archive `queue.json` while the worker is stopped and start with a new journal. Keep the old journal for audit and avoid replaying archived request IDs.
