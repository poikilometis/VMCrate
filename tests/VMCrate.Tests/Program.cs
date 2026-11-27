namespace VMCrate.Tests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 0 && Environment.GetEnvironmentVariable("VMCRATE_CLI_FIXTURE") == "1")
            return AssistantChecks.RunCliFixture(args);
        if (args.Length is 3 or 4 && args[0] == "--hyperv-live" && (args.Length == 3 || args[3] == "--windows-guest"))
        {
            try { return LiveAcceptance.RunAsync(args[1], args[2], args.Length == 4).GetAwaiter().GetResult(); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL live acceptance · {error.Message}");
                return 1;
            }
        }
        if (args.Length == 4 && args[0] == "--kali-seed-media")
            return KaliSeedChecks.VerifyInstallerMedia(args[1], args[2], args[3]);
        if (args.Length == 4 && args[0] == "--kali-publisher")
            return KaliSeedChecks.VerifyPublisher(args[1], args[2], args[3]);
        if (args.Length != 0)
        {
            Console.Error.WriteLine("Usage: VMCrate.Tests [--hyperv-live <new working directory> <fixtures.json> [--windows-guest] | --kali-seed-media <installer.iso> <sha256> <output.iso> | --kali-publisher <SHA256SUMS> <signature> <public-key>]");
            return 2;
        }

        (string Name, Func<Task> Run)[] checks =
        [
            ("Template lifecycle and offline reset", LabChecks.TemplateLifecycle),
            ("ISO installer capture and cleanup", LabChecks.InstallerCapture),
            ("Failed creation retains a manageable entry", LabChecks.FailedCreation),
            ("Interrupted disk reset remains retryable", LabChecks.FailedReset),
            ("Workspace operations are serialized", LabChecks.ConcurrentWrites),
            ("Invalid input leaves the workspace untouched", LabChecks.InvalidInputs),
            ("Corrupted state fails closed", LabChecks.CorruptedState),
            ("Inbox rejects duplicate names and leaves sources intact", LabChecks.Inbox),
            ("Host failures keep the catalog accessible", LabChecks.UnavailableHost),
            ("Blueprint preview, reuse and private segments", BlueprintChecks.Topology),
            ("Blueprint retries interrupted deployment and protects existing VMs", BlueprintChecks.Retry),
            ("Blueprint rejects unknown keys and unsafe input", BlueprintChecks.Validation),
            ("Disposable lifecycle preserves ordinary VMs and the template", SessionChecks.Lifecycle),
            ("Disposable boot and cleanup failures preserve records", SessionChecks.Recovery),
            ("Template digests detect changes and support older catalogs", EvidenceChecks.TemplateHashes),
            ("Evidence report tracks files and VM findings with no source paths", EvidenceChecks.Report),
            ("Offline Sandbox profile disables redirection and networking", Checks.Run(EvidenceChecks.SandboxProfile)),
            ("Template build verifies and caches media, then resumes without duplicating resource IDs", BuildWorkflowChecks.Lifecycle),
            ("A mismatched ISO cannot reach a builder", BuildWorkflowChecks.BadMedia),
            ("Cancelled file stages retain build identity and remain retryable", BuildWorkflowChecks.CancelAndResume),
            ("Lab operations preview membership and protect other resources", GroupWorkflowChecks.Lifecycle),
            ("Diagnostics export and builder proxy preserve boundaries", HostWorkflowChecks.DiagnosticsAndProxy),
            ("Isolation evidence is scoped to the current VM and profile", IsolationChecks.CurrentEvidence),
            ("Clean session records file evidence and storage dependencies", HostWorkflowChecks.CleanSessionAndStorage),
            ("Launch profiles persist and write a native Windows shortcut", SessionProfileChecks.ProfileAndShortcut),
            ("A group disk restore resumes without losing former disks", RecoveryPointChecks.ResumeGroupRestore),
            ("Offline library and lock pins verify without Hyper-V", RoadmapChecks.OfflineKitAndPins),
            ("The build queue checks boot media and publishes after a test clone starts", RoadmapChecks.InstallerPreparation),
            ("The topology applies only the reviewed private address", RoadmapChecks.PrivateAddressChecks),
            ("Expired proxy leases detach egress while the builder keeps running", RoadmapChecks.ExpiredProxyAndDiff),
            ("First-run registration and backup recovery preserve workspace data", ReleaseWorkflowChecks.FirstRunAndBackup),
            ("Retry provision, recover lost replies and restore isolation", ReleaseWorkflowChecks.ProvisionAndIsolation),
            ("Missing clone recovery retains the previous working disk", SupportChecks.MissingCloneRecovery),
            ("Support archive matches its preview and omits local identifiers", SupportChecks.PrivacyAndArchive),
            ("Proxy stages distinguish gateway, TLS and authentication", ReleaseWorkflowChecks.ProxyTrace),
            ("Build proxy history compares attempts and keeps sensitive route details out of AI", TrialAndDiagnosticsChecks.BuildDiagnostics),
            ("An update trial tests an independent revision and keeps existing clone references", TrialAndDiagnosticsChecks.RevisionTrial),
            ("A lab backup verifies disks and resumes restoring the same private topology", LabBackupChecks.Restore),
            ("Local jobs replay after worker loss and fence completed build stages", Release07Checks.QueueReplay),
            ("A changed gateway closes a live proxy lease during a busy workspace", Release07Checks.RouteDrift),
            ("Guest isolation counters and scoped outbox receipts survive attachment handoff", Release07Checks.OutboxAndGuestReadings),
            ("Workspace schema rollback preserves the old bytes and refuses lost updates", Release07Checks.SchemaRollback),
            ("Official metadata scopes publisher, architecture and variants", ImageCatalogChecks.PublisherFeeds),
            ("Kali metadata retains its signature status and rejects invalid signatures", ImageCatalogChecks.SignedManifest),
            ("Kali preseed uses a standard password hash and rejects mismatched media", KaliSeedChecks.PasswordAndMedia),
            ("ISO downloads resume, validate SHA-256 and reuse cache", ImageCatalogChecks.ResumesAndVerifies),
            ("AI evidence and blueprint schemas reject unsupported data", AssistantChecks.Contracts),
            ("All three subscription CLI transports carry prompts only on stdin", AssistantChecks.CliTransports),
            ("Session reports survive disposal and redact assistant input", AssistantChecks.ArchivedSessions),
            ("Portable projects check template reuse, collisions and disk integrity", PortableProjectChecks.TransferAndRejectTampering),
            ("A stopped machine transfers verified disk bytes and resumes a lost registration reply", MachineTransferChecks.CopyAndResume),
            ("Template revisions keep original clone references and verify Offline boot", PortableProjectChecks.Revisions),
            ("OSV exact-version paging, fix matching and CISA KEV alias resolution", AdvisoryChecks.ExactInventory),
            ("The Linux collector uses a scoped offline inbox and local comparisons", LinuxInventoryChecks.OfflineCollector),
            ("Remote jobs are isolated by certificate and survive worker restart", RemoteQueueChecks.AccountIsolationAndRestart),
            ("A remote start or deployment verifies the pinned disk while holding the workspace lock", RemoteQueueChecks.PinnedExecution),
            ("All 15 locales contain matching keys and placeholders", Checks.Run(LocalizationChecks.Catalogs)),
            ("WPF windows and runtime right-to-left switching", Checks.Run(LocalizationChecks.Windows)),
            ("Windows creates a real UDF inbox image", () => Task.Run(InboxChecks.WindowsUdf))
        ];
        var failures = 0;
        foreach (var (name, run) in checks)
        {
            try
            {
                run().GetAwaiter().GetResult();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception failure)
            {
                Console.Error.WriteLine($"FAIL {name}: {failure}");
                failures++;
            }
        }

        Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
        return failures == 0 ? 0 : 1;
    }
}
