// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

public class BundleWriteReportTests
{
    private static BundleKey K(string s) => BundleKey.Parse(s);

    [Fact]
    public void A_report_succeeds_exactly_when_nothing_failed()
    {
        var clean = new BundleWriteReport([K("a.md")], [K("b.md")], []);
        Assert.True(clean.Succeeded);

        var partial = new BundleWriteReport([K("a.md")], [], [new BundleWriteFailure(K("c.md"), "'c.md' changed since it was read; the write was refused")]);
        Assert.False(partial.Succeeded);
        Assert.Equal(K("c.md"), Assert.Single(partial.Failed).Key);
    }

    [Fact]
    public void The_empty_report_has_nothing_and_succeeds()
    {
        Assert.Empty(BundleWriteReport.Empty.Written);
        Assert.Empty(BundleWriteReport.Empty.Deleted);
        Assert.Empty(BundleWriteReport.Empty.Failed);
        Assert.True(BundleWriteReport.Empty.Succeeded);
    }

    [Fact]
    public void Null_lists_and_a_null_or_empty_reason_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new BundleWriteReport(null!, [], []));
        Assert.Throws<ArgumentNullException>(() => new BundleWriteReport([], null!, []));
        Assert.Throws<ArgumentNullException>(() => new BundleWriteReport([], [], null!));
        Assert.Throws<ArgumentNullException>(() => new BundleWriteFailure(null!, "why"));
        Assert.ThrowsAny<ArgumentException>(() => new BundleWriteFailure(K("a.md"), ""));
    }

    [Fact]
    public void Conflict_reasons_have_fixed_library_authored_messages()
    {
        var changed = new BundleWriteConflictException(K("a.md"));
        Assert.Equal(BundleWriteConflictReason.ConditionFailed, changed.Reason);
        Assert.Equal("'a.md' changed since it was read; the write was refused", changed.Message);

        var taken = new BundleWriteConflictException(K("a.md"), BundleWriteConflictReason.NameUnavailable);
        Assert.Equal(BundleWriteConflictReason.NameUnavailable, taken.Reason);
        Assert.Equal("'a.md' cannot be stored under that name: the storage holds an entry it would confuse with it (a directory, or a name differing only in case or Unicode form), or cannot hold the name", taken.Message);
    }

    [Fact]
    public void An_unknown_outcome_names_the_key_and_keeps_the_raw_failure_for_the_host_log()
    {
        var raw = new IOException(@"ReplaceFile failed for C:\secret\a.md", unchecked((int)0x80070498));
        var ex = new BundleWriteOutcomeUnknownException(K("a.md"), raw);
        Assert.Equal(K("a.md"), ex.Key);
        Assert.Same(raw, ex.InnerException);
        Assert.Equal("the write of 'a.md' failed with an unknown outcome: the object may hold its previous content, the new content, or nothing", ex.Message);
        Assert.IsAssignableFrom<BundleProviderException>(ex);
    }
}
