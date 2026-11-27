# AI and portable lab workflows

## Connect a subscription account

Open **Lab tools → AI connections**, select a provider and use **Open sign-in** if needed. An empty CLI path searches the Windows PATH. A custom path can point to the native executable or the official npm shim for Codex or DeepSeek Harness. The optional profile folder selects a Claude Code or DeepSeek Harness account profile. The settings store contains paths and the chosen provider.

- **Claude Code CLI** uses `claude auth login` and the saved claude.ai sign-in. Use a CLI version with `--safe-mode` and JSON schema output. [Claude Code CLI](https://code.claude.com/docs/en/cli-reference)
- **Codex CLI** uses `codex login` with ChatGPT. Use a CLI version with `codex exec --ignore-user-config` and `--output-schema`. The invocation keeps normal CLI authentication and uses a read-only sandbox. [Codex authentication](https://learn.chatgpt.com/docs/auth) [Codex CLI options](https://learn.chatgpt.com/docs/cli/reference)
- **DeepSeek Harness** uses the official `dsh` launcher. Sign in to the DeepSeek account in its web profile. VMCrate starts the headless profile with a temporary overlay for the account model route and a tool guard. Model access follows the signed-in account. Harness manages its own session history. [Headless authentication](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/bundle/headless#credentials) [Account model route](https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/llm/llm-deepseek-account)

The workflow displays the exact VMCrate request and chosen provider before submission. Diagnostic and session requests contain typed observations, error codes and hashes. Blueprint requests contain the description you enter and the available template labels. AI output is validated against the expected JSON structure and shown for review. VMCrate's own blueprint validation and deployment preview apply before a lab can be created.

## Work with lab evidence

Choose **Explain with AI** in proxy diagnostics or template build history. The explanation cites identifiers shown in the reviewed context. **Operation log** shows recent recorded operations. **Session report** shows file hashes and the sequence of operations for active and recently removed disposable VMs. The JSON export can include an AI explanation alongside the observations.

Choose **Lab from description**, describe the VMs and any shared Private segments, and review the returned blueprint. Open it in the editor to adjust names or resources before saving. Use **Preview** to review actual deployment before confirming.

## Check template updates

Choose a template in **Template update advisor** and open a version 1 JSON package inventory. The inventory contains the OSV ecosystem and exact source package or dependency versions from that template. See the [example inventory](../examples/package-inventory.json). VMCrate sends the package names and versions to OSV after review, then checks advisory identifiers and aliases against CISA KEV. The report shows the number of package matches and the coverage of fetched advisory details. Use the matching vendor ecosystem for the inventory. [OSV batch query](https://google.github.io/osv.dev/post-v1-querybatch/) [CISA KEV catalog](https://github.com/cisagov/kev-data)

Open **Template revisions** to build a separate image from an ISO, see which VMs reference each revision, and run a boot test. The test verifies SHA-256, starts a disposable Offline clone, checks its Hyper-V heartbeat and records the verification time after cleanup.

## Move a lab

Open **Portable projects** with a saved blueprint or from **Manage labs**. Export the topology, versioned template metadata and SHA-256 digests. Choose **Include VHDX files** to carry validated template disks in the archive. The destination preview shows locally matched templates, embedded disks and missing prerequisites. Imported disks are verified before the blueprint opens for editing and saving.
