# VMCrate

[![Windows validation](https://github.com/poikilometis/VMCrate/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/poikilometis/VMCrate/actions/workflows/ci.yml)
[![Windows 11 x64](https://img.shields.io/badge/Windows%2011-x64-056C62)](#requirements)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)](#build-and-test)
[![MIT](https://img.shields.io/badge/license-MIT-056C62)](LICENSE)

**Reusable Hyper-V labs from a native Windows desktop.** Prepare a Windows or Linux template, launch an isolated test VM, and reset or discard the session when you're done.

[Quick start](#quick-start) · [Features](#features) · [Documentation](#documentation) · [Contributing](CONTRIBUTING.md)

![VMCrate workspace with an Offline Debian VM and a private lab node](docs/images/workspace.png)

*VMCrate with example virtual machines.*

## Features

| Workflow | What VMCrate does |
| --- | --- |
| Templates | Find official install media, verify checksums, import Generation 2 VHDX files, and test new template revisions before publication. The wizard can generate a Kali amd64 setup disc. |
| Isolation | Start with networking disconnected. Choose a private VM network for peers or a scoped update proxy for a builder. Send files on a read-only virtual DVD. |
| Recovery and portability | Reset a clone, save disk recovery points for stopped VMs, export a lab kit, or copy a stopped VM's system disk to another host. |
| Lab operations | Deploy repeatable labs from JSON blueprints, review isolation and proxy diagnostics, compare session reports, and inspect Windows and Linux update inventories. |
| Team queue | Deploy and operate approved lab recipes on a dedicated Hyper-V host. Mutual TLS maps each operator to a separate workspace. |

The interface supports 15 languages. Optional AI integrations use a signed-in CLI to draft blueprints and explain selected findings. [AI workflows](docs/assistant.md)

## Quick start

### Requirements

- Windows 11 x64 with Hyper-V, its PowerShell management tools, and hardware virtualization enabled.
- An account authorized to manage Hyper-V and a local workspace with room for ISOs and virtual disks.
- Windows Sandbox is optional. Use the x64 .NET 10 SDK to build from source.

### Run your first VM

1. Download **VMCrate-win-x64** from a successful [Windows validation run](https://github.com/poikilometis/VMCrate/actions/workflows/ci.yml), or [build a ZIP locally](#build-and-test). Compare the inner ZIP's SHA-256 with the included `.sha256` file using `Get-FileHash`. Extract the whole package, then run `VMCrate.exe`.
2. Choose a workspace. In **Lab tools → Official image catalog**, select an installer, or import a prepared standalone disk from **Templates → Import VHDX**. The [template workflow](docs/operations.md#build-a-versioned-template) covers installation and capture.
3. Select a template and choose **Create VM**, then **Start VM → Open console**. Use **Clean session** for disposable work or **Reset disk** to return a stopped clone to its base template.

## Build and test

```powershell
dotnet build VMCrate.slnx -c Release
dotnet run --project src/VMCrate.Desktop -c Release --no-build
dotnet run --project tests/VMCrate.Tests -c Release --no-build
powershell -NoProfile -NonInteractive -File tests/HyperV.Tests.ps1
```

With the matching x64 Desktop and ASP.NET Core runtimes installed, run `pwsh -NoProfile -File scripts/New-Release.ps1`. The script writes the ZIP and SHA-256 file to `artifacts/releases`.

The [compatibility log](docs/compatibility.md) records live Hyper-V results and release signature instructions. The [security model](SECURITY.md) explains host and guest isolation assumptions.

## Documentation

| Start here | Topics |
| --- | --- |
| [Host and lab operations](docs/operations.md) | Template builds, proxy setup, disk recovery, and VM transfer |
| [Workflows and examples](docs/workflows.md) | Blueprints, recipes, CLI validation, and offline kits |
| [Team queue setup](docs/remote-queue.md) | Host configuration, certificates, and operator workspaces |
| [Architecture](docs/architecture.md) | Workspace storage, Hyper-V adapter, and lifecycle invariants |
| [Contributing](CONTRIBUTING.md) | Development conventions and Hyper-V acceptance |
| [Localization](docs/localization.md) | Language catalogs and translation checks |

VMCrate is available under the [MIT License](LICENSE). See [Security reporting](SECURITY.md#reporting) for disclosure.
