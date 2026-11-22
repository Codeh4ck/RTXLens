using System.Text;
using System.Text.Json;
using RTXLens.Presentation.Msi.Afterburner;
using RTXLens.Plugins.Msi.Contracts.Safeguard;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.UI.Tests.Configuration;

internal static class AfterburnerConfigurationTests
{
    public static void Run(TestRunner runner)
    {
        runner.Run(CurrentLimitsEnforceOneDecimalPlace);
        if (OperatingSystem.IsWindows())
        {
            runner.Run(DiscoverySkipsInvalidRememberedInstallations);
            runner.Run(BundledScriptBindingsRoundTripWithoutChangingTheOtherConnector);
        }
        runner.Run(ConnectorConfigurationsAndActionsAreIndependent);
        runner.Run(IndependentConnectorDraftsSurviveTheElevatedRequest);
        runner.Run(ConnectorTimeoutsChangeOnlyTheirOwnNativeKeys);
        runner.Run(MissingTimeoutsArePreservedAndInvalidTimeoutsRejected);
        runner.Run(LowPowerPresetCanBeRemovedAndRestoredWithoutRemovingTheAlarm);
        runner.Run(CustomActionsArePreservedWhenTheNativePresetIsUnchecked);
        runner.Run(DeletesOnlyTheSelectedBackupWithoutChangingConfiguration);
        runner.Run(DeletesMultipleSelectedBackupsAndPreservesTheRest);
        runner.Run(ValidatesEveryBackupBeforeDeletingAnySelection);
        runner.Run(BatchDeletionSurvivesTheElevatedRequest);
        runner.Run(LegacyBackupsRemainReadableAndImportWithoutLosingHistory);
        runner.Run(LegacyRecoveryJournalFollowsTheImportedBackups);
        runner.Run(IncompleteLegacyJournalStillAllowsVerifiedRecovery);
        runner.Run(LegacyImportNeverOverwritesAConflictingSnapshot);
        runner.Run(RejectsUnsafeOrRecoveryCriticalBackupDeletion);
        runner.Run(IniRoundTripsOriginalEncodingsAndLineEndings);
        runner.Run(PlannerChangesOnlyRequestedKeysAndKeepsSourceOrder);
        runner.Run(SoftwareAndFirmwareModesAndDisarmingAreExplicit);
        runner.Run(RejectsDuplicateTargetsMalformedFilesAndUnknownOptions);
        runner.Run(BackupsArePairedImmutableAndRestorable);
        runner.Run(ExternalChangesInvalidateTheReviewedPlan);
        runner.Run(FailedSecondReplacementRollsBackTheFirstFile);
        runner.Run(CorruptedOrForeignBackupsCannotBeRestored);
        runner.Run(InterruptedChangesRequireExplicitRecovery);
        runner.Run(RestorationRecoversCorruptAndMissingCurrentFiles);
    }

    private static readonly byte[] Plugin = Encoding.UTF8.GetBytes("; plugin comment\r\n[Debug]\r\nEnableLog \t= 0\r\n\r\n[Settings]\r\nDataLayout \t= 0\r\nGPUSafeguard\t= 1\r\nSoftwareAlarm\t= 1\r\nMaxCurrent \t= 9.2\r\nCurrentDif \t= 1.2\r\nUnknown=leave this alone\r\n");

    private static readonly byte[] Profile = Encoding.UTF8.GetBytes("; profile comment\r\n[Settings]\r\nCurrentGpu=0\r\nSources=+GPU usage,-PSU 12VHPWR1 alarm,+PSU 12VHPWR2 alarm,+Core clock\r\n[Monitoring]\r\nPSU.dll=1\r\nOther.dll=1\r\n[Source PSU 12VHPWR1 alarm]\r\nAlarmThresholdMin=\r\nAlarmThresholdMax=\r\nAlarmFlags=3\r\nAlarmTimeout=2000\r\nAlarmApp=%AB%\r\nAlarmAppCmdLine=-lowpowerprofile -message GPUSafeguard\r\nFormula=\r\n[Source PSU 12VHPWR2 alarm]\r\nAlarmThresholdMin=\r\nAlarmThresholdMax=1\r\nAlarmFlags=3\r\nAlarmTimeout=3000\r\nAlarmApp=%AB%\r\nAlarmAppCmdLine=-lowpowerprofile -message GPUSafeguard\r\nFormula=\r\n[Source GPU usage]\r\nAlarmFlags=0\r\nGroup=untouched\r\n");

    private static AfterburnerSettings Settings => new(true, true, true, 6, 1.2, 2, true);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void DiscoverySkipsInvalidRememberedInstallations()
    {
        using var fixture = new Files();
        var directory = fixture.Store.Installation;
        var expected = AfterburnerDiscovery.Find();

        // The remembered folder may be gone or contain an unrelated executable.
        CheckCandidate(Path.Combine(directory, "removed"));
        File.Copy(typeof(AfterburnerConfigurationTests).Assembly.Location, Path.Combine(directory, "MSIAfterburner.exe"));
        CheckCandidate(directory);

        void CheckCandidate(string candidate)
        {
            TestAssert.Throws<InvalidDataException>(() => AfterburnerDiscovery.Inspect(candidate));
            TestAssert.Equal(0, AfterburnerDiscovery.InspectCandidates([candidate]).Length);
            var discovered = AfterburnerDiscovery.InspectCandidates([candidate, .. expected.Select(item => item.Directory)]);
            TestAssert.True(expected.SequenceEqual(discovered),
                "An invalid installation must be skipped while preserving all other discovered installations.");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void BundledScriptBindingsRoundTripWithoutChangingTheOtherConnector()
    {
        var install = Path.GetFullPath("fixture-afterburner");
        var hash = new string('A', 64);
        var id = Guid.NewGuid();
        var script = new SafeguardBinding(id, SafeguardScripts.LowPower, 1, 2, 5000,
            Path.Combine(install, "MSIAfterburner.exe"), hash, hash, hash, null);
        var settings = Settings with { ConnectorAlarms = [new(2, true, false) { Script = script }] };
        var plan = AfterburnerConfiguration.Plan(install, Plugin, Profile, settings);
        var before = AfterburnerConfiguration.InspectConnector(Profile, 1);
        var after = AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 1);
        TestAssert.Equal(before, after);
        var connector = AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 2);
        TestAssert.Equal<Guid?>(id, connector.ScriptBindingId);
        TestAssert.Equal("LowPowerAndShutdown.ps1", connector.BuiltInScriptFileName!);

        const string hiddenOption = "-WindowStyle Hidden ";
        TestAssert.True(connector.Arguments.StartsWith(hiddenOption, StringComparison.Ordinal), "Bundled alarms must hide the PowerShell window.");
        var legacyProfile = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(plan.Files[1].After)
            .Replace(hiddenOption, "", StringComparison.Ordinal));
        var legacyConnector = AfterburnerConfiguration.InspectConnector(legacyProfile, 2);
        TestAssert.Equal<Guid?>(id, legacyConnector.ScriptBindingId);

        var migrated = AfterburnerConfiguration.Plan(install, plan.Files[0].After, legacyProfile, settings);
        TestAssert.True(migrated.HasChanges, "Reapplying an existing alarm must update the launch command.");
        TestAssert.True(migrated.Files[1].After.SequenceEqual(plan.Files[1].After), "Migration must only restore the hidden window option.");
        TestAssert.False(AfterburnerConfiguration.Plan(install, migrated.Files[0].After, migrated.Files[1].After, settings).HasChanges,
            "Reapplying a hidden alarm must leave the files unchanged.");

        TestAssert.False(Encoding.UTF8.GetString(plan.Files[1].After).Contains("-Delay"), "Parameters belong in the protected binding.");
        TestAssert.Throws<ArgumentException>(() => (script with { DelayMs = -1 }).Validate());
        TestAssert.Throws<ArgumentException>(() => (script with { ScriptId = "user-script" }).Validate());

        var operation = new AfterburnerOperation(install, "apply", settings, plan.Id, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(operation, AfterburnerJsonContext.Default.AfterburnerOperation);
        var restored = JsonSerializer.Deserialize(bytes, AfterburnerJsonContext.Default.AfterburnerOperation)!;
        TestAssert.Equal(script, restored.Settings!.ConnectorAlarms.Single().Script!);
    }

    private static void DeletesOnlyTheSelectedBackupWithoutChangingConfiguration()
    {
        using var fixture = new Files();
        var original = fixture.Store.Read();
        var first = fixture.Store.Save(original);
        var second = fixture.Store.Save(original);
        fixture.Store.DeleteBackup(first.Id, fixture.Store.BackupFingerprint(first.Id));

        TestAssert.Equal(1, fixture.Store.Backups().Length);
        TestAssert.Equal(second.Id, fixture.Store.Backups()[0].Id);
        TestAssert.False(Directory.Exists(Path.Combine(fixture.Store.BackupDirectory, first.Id)), "Only the selected backup directory must be removed.");
        TestAssert.Equal(original.Id, fixture.Store.Read().Id);
    }

    private static void DeletesMultipleSelectedBackupsAndPreservesTheRest()
    {
        using var fixture = new Files();
        var current = fixture.Store.Read();
        var first = fixture.Store.Save(current);
        var second = fixture.Store.Save(current);
        var retained = fixture.Store.Save(current);
        fixture.Store.DeleteBackups([
            new(first.Id, fixture.Store.BackupFingerprint(first.Id)),
            new(second.Id, fixture.Store.BackupFingerprint(second.Id))
        ]);

        TestAssert.Equal(1, fixture.Store.Backups().Length);
        TestAssert.Equal(retained.Id, fixture.Store.Backups()[0].Id);
        TestAssert.Equal(current.Id, fixture.Store.Read().Id);
    }

    private static void ValidatesEveryBackupBeforeDeletingAnySelection()
    {
        using var fixture = new Files();
        var current = fixture.Store.Read();
        var first = fixture.Store.Save(current);
        var second = fixture.Store.Save(current);
        var valid = new AfterburnerBackupDeletion(first.Id, fixture.Store.BackupFingerprint(first.Id));
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.DeleteBackups([valid, new(second.Id, "changed")]));
        TestAssert.Equal(2, fixture.Store.Backups().Length);

        var selection = new[] { valid, new AfterburnerBackupDeletion(second.Id, fixture.Store.BackupFingerprint(second.Id)) };
        File.WriteAllText(Path.Combine(fixture.Store.BackupDirectory, "pending.json"), second.Id);
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.DeleteBackups(selection));
        TestAssert.Equal(2, fixture.Store.Backups().Length);
        TestAssert.Throws<ArgumentException>(() => fixture.Store.DeleteBackups([valid, valid]));
        TestAssert.Throws<ArgumentException>(() => fixture.Store.DeleteBackups([]));
        TestAssert.Equal(current.Id, fixture.Store.Read().Id);
    }

    private static void BatchDeletionSurvivesTheElevatedRequest()
    {
        var operation = new AfterburnerOperation("fixture", "delete", null, "", null)
        {
            BackupsToDelete = [new("first", "first-fingerprint"), new("second", "second-fingerprint")]
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(operation, AfterburnerJsonContext.Default.AfterburnerOperation);
        var restored = JsonSerializer.Deserialize(bytes, AfterburnerJsonContext.Default.AfterburnerOperation)!;
        TestAssert.Equal(2, restored.BackupsToDelete.Length);
        TestAssert.Equal(operation.BackupsToDelete[0], restored.BackupsToDelete[0]);
        TestAssert.Equal(operation.BackupsToDelete[1], restored.BackupsToDelete[1]);
    }
    private static void LegacyBackupsRemainReadableAndImportWithoutLosingHistory()
    {
        using var fixture = new Files();
        var current = fixture.Store.Read();
        var first = fixture.Store.Save(current);
        var second = fixture.Store.Save(current);
        var destination = Path.Combine(Path.GetDirectoryName(fixture.Store.BackupDirectory)!, "AfterburnerBackups");
        var store = new AfterburnerFileStore(fixture.Store.Installation, destination, fixture.Store.BackupDirectory);
        TestAssert.Equal(Path.GetFullPath(destination), store.BackupDirectory);
        TestAssert.Equal(2, store.Backups().Length);
        TestAssert.Equal(fixture.Store.BackupFingerprint(first.Id), store.BackupFingerprint(first.Id));
        TestAssert.True(store.PlanRestore(first.Id).Files[0].After.SequenceEqual(Plugin), "Legacy backups must remain available before import.");

        TestAssert.Equal(2, store.ImportLegacyBackups());
        TestAssert.True(File.Exists(Path.Combine(destination, first.Id, "manifest.json")), "Snapshots must be stored directly under AfterburnerBackups.");
        TestAssert.Equal(2, fixture.Store.Backups().Length);
        TestAssert.Equal(0, store.ImportLegacyBackups());
        store.DeleteBackup(first.Id, store.BackupFingerprint(first.Id));
        TestAssert.Equal(1, store.Backups().Length);
        TestAssert.Equal(second.Id, store.Backups()[0].Id);
        TestAssert.Equal(2, fixture.Store.Backups().Length);
        TestAssert.Equal(current.Id, store.Read().Id);
    }

    private static void LegacyRecoveryJournalFollowsTheImportedBackups()
    {
        using var fixture = new Files();
        var current = fixture.Store.Read();
        var backup = fixture.Store.Save(current);
        File.WriteAllText(Path.Combine(fixture.Store.BackupDirectory, "pending.json"), backup.Id);
        var destination = Path.Combine(Path.GetDirectoryName(fixture.Store.BackupDirectory)!, "AfterburnerBackups");
        var store = new AfterburnerFileStore(fixture.Store.Installation, destination, fixture.Store.BackupDirectory);
        TestAssert.True(store.HasInterruptedChange, "An interrupted legacy operation must still block unsafe changes.");
        store.ImportLegacyBackups();
        TestAssert.True(store.HasInterruptedChange, "Import must preserve the recovery journal.");
        TestAssert.Throws<InvalidOperationException>(() => store.DeleteBackup(backup.Id, store.BackupFingerprint(backup.Id)));
        store.Apply(store.PlanRestore(backup.Id), true);
        TestAssert.False(store.HasInterruptedChange, "Successful recovery must not reactivate the archived legacy journal.");
        TestAssert.Equal(current.Id, store.Read().Id);
    }

    private static void IncompleteLegacyJournalStillAllowsVerifiedRecovery()
    {
        using var fixture = new Files();
        var original = fixture.Store.Read();
        var backup = fixture.Store.Save(original);
        File.WriteAllText(Path.Combine(fixture.Store.BackupDirectory, "pending.json"), "incomplete-journal");
        var destination = Path.Combine(Path.GetDirectoryName(fixture.Store.BackupDirectory)!, "AfterburnerBackups");
        var store = new AfterburnerFileStore(fixture.Store.Installation, destination, fixture.Store.BackupDirectory);
        store.ImportLegacyBackups();
        TestAssert.True(store.HasInterruptedChange, "An incomplete journal must remain a recovery flag after import.");
        store.Apply(store.PlanRestore(backup.Id), true);
        TestAssert.False(store.HasInterruptedChange, "A verified working backup must still be able to complete recovery.");
        TestAssert.Equal(original.Id, store.Read().Id);
    }
    private static void LegacyImportNeverOverwritesAConflictingSnapshot()
    {
        using var fixture = new Files();
        var backup = fixture.Store.Save(fixture.Store.Read());
        var destination = Path.Combine(Path.GetDirectoryName(fixture.Store.BackupDirectory)!, "AfterburnerBackups");
        var conflict = Path.Combine(destination, backup.Id);
        Directory.CreateDirectory(conflict);
        foreach (var name in new[] { "PSU.cfg", "MSIAfterburner.cfg", "manifest.json" })
        {
            File.Copy(Path.Combine(fixture.Store.BackupDirectory, backup.Id, name), Path.Combine(conflict, name));
        }

        var manifestPath = Path.Combine(conflict, "manifest.json");
        var manifest = File.ReadAllText(manifestPath).Replace("Saved working configuration", "Different existing snapshot");
        File.WriteAllText(manifestPath, manifest);
        var store = new AfterburnerFileStore(fixture.Store.Installation, destination, fixture.Store.BackupDirectory);
        TestAssert.Throws<InvalidDataException>(() => store.ImportLegacyBackups());
        TestAssert.Equal(manifest, File.ReadAllText(manifestPath));
        TestAssert.Equal(1, fixture.Store.Backups().Length);
    }
    private static void RejectsUnsafeOrRecoveryCriticalBackupDeletion()
    {
        using var fixture = new Files();
        var backup = fixture.Store.Save(fixture.Store.Read());
        var fingerprint = fixture.Store.BackupFingerprint(backup.Id);
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.DeleteBackup(backup.Id, "wrong fingerprint"));
        TestAssert.Throws<InvalidDataException>(() => fixture.Store.DeleteBackup("../escape", fingerprint));

        var extra = Path.Combine(fixture.Store.BackupDirectory, backup.Id, "unexpected.txt");
        File.WriteAllText(extra, "must remain untouched");
        TestAssert.Throws<InvalidDataException>(() => fixture.Store.DeleteBackup(backup.Id, fingerprint));
        TestAssert.True(File.Exists(extra), "Unexpected files must never be deleted.");
        File.Delete(extra);

        File.WriteAllText(Path.Combine(fixture.Store.BackupDirectory, "pending.json"), backup.Id);
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.DeleteBackup(backup.Id, fingerprint));
        TestAssert.Equal(1, fixture.Store.Backups().Length);
    }

    private static void ConnectorConfigurationsAndActionsAreIndependent()
    {
        var settings = Settings with
        {
            ConnectorAlarms = [new(1, true, true), new(2, true, false)]
        };
        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, settings);
        var first = AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 1);
        var second = AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 2);
        TestAssert.True(first.AlarmConfigured && second.AlarmConfigured, "Both connector alarms must be configured independently.");
        TestAssert.True(first.LowPowerAction, "Connector 1 must retain its requested native action.");
        TestAssert.False(second.LowPowerAction, "Connector 2's unchecked native action must be removed.");
        TestAssert.Equal("", second.Application);
        TestAssert.Equal("3000", second.Timeout);

        var otherSelection = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, settings with { Connector = 1 });
        TestAssert.True(plan.Files[1].After.SequenceEqual(otherSelection.Files[1].After),
            "Highlighting a different connector must not change or discard either connector's settings.");
    }

    private static void IndependentConnectorDraftsSurviveTheElevatedRequest()
    {
        var settings = Settings with { ConnectorAlarms = [new(1, true, true) { AlarmTimeoutMs = 1500 }, new(2, true, false) { AlarmTimeoutMs = 4500 }] };
        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, settings);
        var operation = new AfterburnerOperation("fixture", "apply", settings, plan.Id, null);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(operation, AfterburnerJsonContext.Default.AfterburnerOperation);
        var restored = JsonSerializer.Deserialize(bytes, AfterburnerJsonContext.Default.AfterburnerOperation)!;
        TestAssert.Equal(2, restored.Settings!.ConnectorAlarms.Length);
        TestAssert.Equal(plan.Id, AfterburnerConfiguration.Plan("fixture", Plugin, Profile, restored.Settings).Id);
    }

    private static void ConnectorTimeoutsChangeOnlyTheirOwnNativeKeys()
    {
        var settings = AfterburnerConfiguration.Inspect(Plugin, Profile).Settings with
        {
            ConnectorAlarms = [new(1, false, true) { AlarmTimeoutMs = 2500 }, new(2, true, true)]
        };
        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, settings);
        TestAssert.True(Plugin.SequenceEqual(plan.Files[0].After), "Changing one timeout must not edit the PSU plugin configuration.");
        TestAssert.Equal(Encoding.UTF8.GetString(Profile).Replace("AlarmTimeout=2000", "AlarmTimeout=2500"),
            Encoding.UTF8.GetString(plan.Files[1].After));
        TestAssert.Equal<int?>(2500, AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 1).AlarmTimeoutMs);
        TestAssert.Equal<int?>(3000, AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 2).AlarmTimeoutMs);

        settings = settings with { ConnectorAlarms = [new(1, false, true) { AlarmTimeoutMs = 0 }, new(2, true, true) { AlarmTimeoutMs = 5000 }] };
        plan = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, settings);
        TestAssert.Equal<int?>(0, AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 1).AlarmTimeoutMs);
        TestAssert.Equal<int?>(5000, AfterburnerConfiguration.InspectConnector(plan.Files[1].After, 2).AlarmTimeoutMs);
    }

    private static void MissingTimeoutsArePreservedAndInvalidTimeoutsRejected()
    {
        var profile = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Profile).Replace("AlarmTimeout=2000\r\n", ""));
        var settings = AfterburnerConfiguration.Inspect(Plugin, profile).Settings with
        {
            ConnectorAlarms = [new(1, false, true), new(2, true, true)]
        };
        TestAssert.Equal<int?>(null, AfterburnerConfiguration.InspectConnector(profile, 1).AlarmTimeoutMs);
        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, profile, settings);
        TestAssert.False(plan.HasChanges, "An absent timeout must keep Afterburner's own default without writing an invented value.");

        TestAssert.Throws<ArgumentException>(() => AfterburnerConfiguration.Plan("fixture", Plugin, Profile,
            settings with { ConnectorAlarms = [new(1, false, true) { AlarmTimeoutMs = -1 }] }));
        var malformed = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Profile).Replace("AlarmTimeout=2000", "AlarmTimeout=1.5"));
        TestAssert.Throws<InvalidDataException>(() => { _ = AfterburnerConfiguration.InspectConnector(malformed, 1).AlarmTimeoutMs; });
    }
    private static void LowPowerPresetCanBeRemovedAndRestoredWithoutRemovingTheAlarm()
    {
        var removed = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { UseLowPowerAction = false });
        var inspection = AfterburnerConfiguration.InspectConnector(removed.Files[1].After, 2);
        TestAssert.True(removed.Files[1].Changed, "Unticking the native action must produce an actual configuration edit.");
        TestAssert.True(inspection.AlarmConfigured, "Removing an action must not remove the connector alarm.");
        TestAssert.Equal("", inspection.Application);
        TestAssert.Equal("", inspection.Arguments);
        TestAssert.True(AfterburnerConfiguration.InspectConnector(removed.Files[1].After, 1).LowPowerAction,
            "Changing connector 2 must not remove connector 1's action.");

        var restored = AfterburnerConfiguration.Plan("fixture", removed.Files[0].After, removed.Files[1].After, Settings);
        TestAssert.True(AfterburnerConfiguration.InspectConnector(restored.Files[1].After, 2).LowPowerAction,
            "Reticking the native action must restore it.");
    }

    private static void CustomActionsArePreservedWhenTheNativePresetIsUnchecked()
    {
        var profile = new IniDocument(Profile);
        profile.Set(AfterburnerConfiguration.AlarmSection(2), "AlarmApp", @"C:\Tools\existing-command.cmd");
        profile.Set(AfterburnerConfiguration.AlarmSection(2), "AlarmAppCmdLine", "existing arguments");
        var original = profile.ToBytes();
        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, original, Settings with { UseLowPowerAction = false });
        TestAssert.True(plan.Files[1].After.SequenceEqual(original), "An unchecked native preset must preserve an existing custom command.");
    }

    private static void CurrentLimitsEnforceOneDecimalPlace()
    {
        (Settings with { MaxCurrent = 9.2, CurrentDifference = 0.1 }).Validate();
        TestAssert.Throws<ArgumentException>(() => (Settings with { MaxCurrent = 9.25 }).Validate());
        TestAssert.Throws<ArgumentException>(() => (Settings with { CurrentDifference = 1.23 }).Validate());
        TestAssert.Throws<ArgumentException>(() => (Settings with { MaxCurrent = 9.200000002 }).Validate());

        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { MaxCurrent = 9.3 });
        TestAssert.Equal("9.3", new IniDocument(plan.Files[0].After).Get("Settings", "MaxCurrent"));
    }

    private static void IniRoundTripsOriginalEncodingsAndLineEndings()
    {
        var text = "; retained\r\n[Settings]\r\n MaxCurrent \t= 9.2  \r\nName=café\r\nOther = \"x=y\"";
        foreach (var encoding in new Encoding[] { new UTF8Encoding(true), Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.Latin1 })
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
            var document = new IniDocument(bytes);
            TestAssert.True(document.ToBytes().SequenceEqual(bytes), "INI decoding must preserve every original byte.");
            document.Set("Settings", "MaxCurrent", "6");
            var expected = encoding.GetPreamble().Concat(encoding.GetBytes(text.Replace("9.2", "6"))).ToArray();
            TestAssert.True(document.ToBytes().SequenceEqual(expected), "Only the requested value may change, including for legacy ANSI and UTF-16 files.");
        }

        var mixed = Encoding.UTF8.GetBytes("[Settings]\nA=1\r\nB=2\rC=3");
        TestAssert.True(new IniDocument(mixed).ToBytes().SequenceEqual(mixed), "Mixed line endings and a missing final newline must survive.");
    }

    private static void PlannerChangesOnlyRequestedKeysAndKeepsSourceOrder()
    {
        var plan = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings);
        var expected = Encoding.UTF8.GetString(Plugin).Replace("MaxCurrent \t= 9.2", "MaxCurrent \t= 6");
        TestAssert.Equal(expected, new IniDocument(plan.Files[0].After).Text);
        TestAssert.True(Profile.SequenceEqual(plan.Files[1].After), "An already configured source must not change the monitoring list, timing, action or unrelated sections.");
        TestAssert.True(Plugin.SequenceEqual(plan.Files[0].Before), "Planning and diff inspection must not mutate the original contents.");

        var connector1 = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { Connector = 1 });
        var profile = new IniDocument(connector1.Files[1].After);
        TestAssert.Equal("+GPU usage,+PSU 12VHPWR1 alarm,+PSU 12VHPWR2 alarm,+Core clock", profile.Get("Settings", "Sources"));
        TestAssert.Equal("1", profile.Get(AfterburnerConfiguration.AlarmSection(1), "AlarmThresholdMax"));
        TestAssert.Equal("3000", profile.Get(AfterburnerConfiguration.AlarmSection(2), "AlarmTimeout"));
        TestAssert.True(AfterburnerConfiguration.Inspect(connector1.Files[0].After, connector1.Files[1].After).Connector2Configured,
            "Enabling another connector must preserve the existing connector alarm.");
    }

    private static void SoftwareAndFirmwareModesAndDisarmingAreExplicit()
    {
        var firmware = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { SoftwareAlarm = false });
        TestAssert.Equal("0", new IniDocument(firmware.Files[0].After).Get("Settings", "SoftwareAlarm"));
        TestAssert.True(AfterburnerConfiguration.Inspect(firmware.Files[0].After, firmware.Files[1].After).Connector2Configured,
            "Selecting firmware alarms must not disable the connector alarm.");

        var disabled = AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { SafeguardEnabled = false });
        TestAssert.False(AfterburnerConfiguration.Inspect(disabled.Files[0].After, disabled.Files[1].After).Connector2Configured,
            "Disabling Safeguard+ must disarm the selected threshold, rather than only hide the plugin preset.");
        TestAssert.Equal("", new IniDocument(disabled.Files[1].After).Get(AfterburnerConfiguration.AlarmSection(2), "AlarmThresholdMax"));
    }

    private static void RejectsDuplicateTargetsMalformedFilesAndUnknownOptions()
    {
        TestAssert.Throws<InvalidDataException>(() => AfterburnerConfiguration.Plan("fixture",
            [.. Plugin, .. Encoding.UTF8.GetBytes("MaxCurrent=7\r\n")], Profile, Settings));
        TestAssert.Throws<InvalidDataException>(() => AfterburnerConfiguration.Plan("fixture",
            [.. Plugin, .. Encoding.UTF8.GetBytes("[Settings]\r\nOther=1\r\n")], Profile, Settings));
        TestAssert.Throws<InvalidDataException>(() => new IniDocument(Encoding.UTF8.GetBytes("[Settings]\nmalformed")));
        TestAssert.Throws<ArgumentException>(() => AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { MaxCurrent = double.NaN }));
        TestAssert.Throws<ArgumentException>(() => AfterburnerConfiguration.Plan("fixture", Plugin, Profile, Settings with { Connector = 3 }));
        TestAssert.Throws<ArgumentException>(() => AfterburnerConfiguration.Plan("fixture", Plugin, Profile,
            Settings with { MonitoringEnabled = false }));

        var profile = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Profile).Replace("AlarmFlags=3", "AlarmFlags=19"));
        TestAssert.Throws<InvalidDataException>(() => AfterburnerConfiguration.Plan("fixture", Plugin, profile, Settings));
        var formula = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Profile).Replace("Formula=\r\n", "Formula=x*2\r\n"));
        TestAssert.Throws<InvalidDataException>(() => AfterburnerConfiguration.Plan("fixture", Plugin, formula, Settings));
    }

    private static void BackupsArePairedImmutableAndRestorable()
    {
        using var fixture = new Files();
        var original = fixture.Store.Read();
        var working = fixture.Store.Save(original);
        var plan = fixture.Plan();
        fixture.Store.Apply(plan);
        TestAssert.Equal(2, fixture.Store.Backups().Length);
        var restore = fixture.Store.PlanRestore(working.Id);
        fixture.Store.Apply(restore, true);

        TestAssert.True(File.ReadAllBytes(fixture.PluginPath).SequenceEqual(Plugin), "Restoring must recover the exact original plugin bytes.");
        TestAssert.True(File.ReadAllBytes(fixture.ProfilePath).SequenceEqual(Profile), "Restoring must recover the matching profile file.");
        TestAssert.Equal(3, fixture.Store.Backups().Length);
        TestAssert.True(fixture.Store.PlanRestore(working.Id).Files[0].After.SequenceEqual(Plugin), "Explicit working backups must never be overwritten.");
    }

    private static void ExternalChangesInvalidateTheReviewedPlan()
    {
        using var fixture = new Files();
        var plan = fixture.Plan();
        File.AppendAllText(fixture.ProfilePath, "; external edit\r\n");
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.Apply(plan));
        TestAssert.Equal(0, fixture.Store.Backups().Length);
        TestAssert.True(File.ReadAllText(fixture.ProfilePath).EndsWith("; external edit\r\n"), "External edits must be kept.");
    }

    private static void FailedSecondReplacementRollsBackTheFirstFile()
    {
        using var fixture = new Files();
        var plan = fixture.Plan(Settings with { Connector = 1 });
        TestAssert.Throws<IOException>(() => fixture.Store.Apply(plan, beforeReplace: name =>
        {
            if (name == AfterburnerConfiguration.ProfileFile)
            {
                throw new IOException("Simulated second-file failure.");
            }
        }));

        TestAssert.True(File.ReadAllBytes(fixture.PluginPath).SequenceEqual(Plugin), "The first file must roll back when the second file fails.");
        TestAssert.True(File.ReadAllBytes(fixture.ProfilePath).SequenceEqual(Profile), "The second file must remain untouched.");
        TestAssert.False(fixture.Store.HasInterruptedChange, "A successful rollback must clear the interrupted-change marker.");
        TestAssert.Equal(1, fixture.Store.Backups().Length);
    }

    private static void CorruptedOrForeignBackupsCannotBeRestored()
    {
        using var fixture = new Files();
        var backup = fixture.Store.Save(fixture.Store.Read());
        var path = Path.Combine(fixture.Store.BackupDirectory, backup.Id, "PSU.cfg");
        File.AppendAllText(path, "; corrupted\r\n");
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.PlanRestore(backup.Id));
        TestAssert.Throws<InvalidDataException>(() => fixture.Store.PlanRestore("../escape"));
    }

    private static void InterruptedChangesRequireExplicitRecovery()
    {
        using var fixture = new Files();
        var working = fixture.Store.Save(fixture.Store.Read());
        File.WriteAllText(Path.Combine(fixture.Store.BackupDirectory, "pending.json"), working.Id);
        TestAssert.Throws<InvalidOperationException>(() => fixture.Store.Apply(fixture.Plan()));
        fixture.Store.Apply(fixture.Store.PlanRestore(working.Id), true);
        TestAssert.False(fixture.Store.HasInterruptedChange, "Restoring a verified backup must complete recovery.");
    }

    private static void RestorationRecoversCorruptAndMissingCurrentFiles()
    {
        using var fixture = new Files();
        var working = fixture.Store.Save(fixture.Store.Read());
        File.WriteAllText(fixture.PluginPath, "broken INI contents");
        fixture.Store.Apply(fixture.Store.PlanRestore(working.Id), true);
        TestAssert.True(File.ReadAllBytes(fixture.PluginPath).SequenceEqual(Plugin), "A corrupt current file must not prevent restoration.");

        File.Delete(fixture.ProfilePath);
        fixture.Store.Apply(fixture.Store.PlanRestore(working.Id), true);
        TestAssert.True(File.ReadAllBytes(fixture.ProfilePath).SequenceEqual(Profile), "A missing current file must be recreated from the backup.");
    }

    private sealed class Files : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "rtx-afterburner-tests-" + Guid.NewGuid().ToString("N"));
        public string PluginPath { get; }
        public string ProfilePath { get; }
        public AfterburnerFileStore Store { get; }

        public Files()
        {
            var installation = Path.Combine(_root, "installation");
            PluginPath = Path.Combine(installation, AfterburnerConfiguration.PluginFile);
            ProfilePath = Path.Combine(installation, AfterburnerConfiguration.ProfileFile);
            Directory.CreateDirectory(Path.GetDirectoryName(PluginPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
            File.WriteAllBytes(PluginPath, Plugin);
            File.WriteAllBytes(ProfilePath, Profile);
            Store = new AfterburnerFileStore(installation, Path.Combine(_root, "backups"));
        }

        public AfterburnerChangeSet Plan(AfterburnerSettings? settings = null)
        {
            var current = Store.Read();
            return AfterburnerConfiguration.Plan(current.Installation, current.Files[0].Before, current.Files[1].Before, settings ?? Settings);
        }

        public void Dispose()
        {
            var fullPath = Path.GetFullPath(_root);
            if (fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(fullPath).StartsWith("rtx-afterburner-tests-", StringComparison.Ordinal))
            {
                Directory.Delete(fullPath, true);
            }
        }
    }
}