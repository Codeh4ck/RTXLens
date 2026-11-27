using RTXLens.Service.Services;
using RTXLens.Tests.Infrastructure;

namespace RTXLens.Tests.Service;

internal static class PowerLimitReadbackTests
{
    public static void KeepsRestoreTargetWhenOnlyEnforcedLimitChanges()
    {
        var lease = new PowerLease("astral", 600000, 400000, ["overcurrent"], false, OriginalEnforced: 600000);
        TestAssert.False(lease.IsUnrelatedSetting(600000), "An unchanged configured limit is not an external edit.");
        TestAssert.False(lease.ReductionVisible(600000, 600000), "Immediate readback can precede enforcement.");
        TestAssert.True(lease.ReductionVisible(600000, 400000), "The enforced limit confirms the cap.");
        var restore = lease with { Restoring = true, RestoreNeedsEnforced = true };
        TestAssert.False(restore.RestorationVisible(600000, 400000), "The cap is still enforced.");
        TestAssert.False(restore.RestorationVisible(600000, null), "The unchanged configured value cannot confirm recovery alone.");
        TestAssert.True(restore.RestorationVisible(600000, 600000), "The driver released the cap.");
        TestAssert.True(lease.IsUnrelatedSetting(475000), "Preserve a distinct external setting.");
        var xoc = lease with { Original = 825125, OriginalEnforced = 825125 };
        TestAssert.True(xoc.RestorationVisible(825125, 825125), "Keep the exact XOC limit in milliwatts.");
    }
}
