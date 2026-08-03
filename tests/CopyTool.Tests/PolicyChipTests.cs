using CopyTool.Core;
using CopyTool.Host.Ui;
using Xunit;

namespace CopyTool.Tests;

/// <summary>
/// What the policy chips do beyond storing a value.
///
/// The elevation chip is the one with a side effect, and it shipped without it:
/// picking "elevate now" set the field and nothing else, so the promise the label
/// makes — the prompt appears now, the permission is in hand before the copy needs
/// it — was not kept. Nothing failed and nothing was logged; the copy simply
/// reached a protected file later and parked it, exactly as if the default had
/// been left alone.
/// </summary>
public sealed class PolicyChipTests
{
    private static (JobViewModel Vm, JobPolicies Policies) NewJob()
    {
        var policies = new JobPolicies();
        var vm = new JobViewModel("מעתיק", @"C:\dst", new JobControl(), policies);
        return (vm, policies);
    }

    private static PolicyChip Chip(JobViewModel vm, string name) =>
        vm.Chips.Single(c => c.Name == name);

    private static PolicyOption Option(PolicyChip chip, object value) =>
        chip.Options.Single(o => Equals(o.Value, value));

    [Fact]
    public void Elevate_now_asks_for_consent_on_the_click()
    {
        var (vm, policies) = NewJob();
        int asked = 0;
        vm.ElevationRequested += () => asked++;

        PolicyChip chip = Chip(vm, "הרשאות");
        chip.Select(Option(chip, ElevationPolicy.ElevateNow));

        Assert.Equal(ElevationPolicy.ElevateNow, policies.Elevation);
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// The prompt must not wait for something to be refused first. Asking up front
    /// is the entire difference between "elevate now" and "ask": the user is buying
    /// the right to walk away.
    /// </summary>
    [Fact]
    public void Elevate_now_asks_even_when_nothing_is_known_to_need_it()
    {
        var (vm, _) = NewJob();
        bool asked = false;
        vm.ElevationRequested += () => asked = true;

        Assert.False(vm.CanRequestElevation);      // the banner button would be dead

        PolicyChip chip = Chip(vm, "הרשאות");
        chip.Select(Option(chip, ElevationPolicy.ElevateNow));

        Assert.True(asked);
    }

    /// <summary>
    /// A queued job has no elevation coordinator yet, so there is nobody to answer.
    /// The click must still take effect as a policy — the job raises the prompt when
    /// it starts — and must not leave the button disabled on the strength of a
    /// request that went nowhere.
    /// </summary>
    [Fact]
    public void Elevate_now_before_the_job_starts_only_sets_the_policy()
    {
        var (vm, policies) = NewJob();

        PolicyChip chip = Chip(vm, "הרשאות");
        chip.Select(Option(chip, ElevationPolicy.ElevateNow));

        Assert.Equal(ElevationPolicy.ElevateNow, policies.Elevation);

        // Now the job starts and something turns out to need admin rights.
        bool asked = false;
        vm.ElevationRequested += () => asked = true;
        vm.SetElevationNeeded(1);

        Assert.True(vm.CanRequestElevation);
        vm.RequestElevation();
        Assert.True(asked);
    }

    [Fact]
    public void The_other_elevation_values_ask_for_nothing()
    {
        foreach (ElevationPolicy value in (ElevationPolicy[])[ElevationPolicy.Ask, ElevationPolicy.SkipProtected])
        {
            var (vm, policies) = NewJob();
            bool asked = false;
            vm.ElevationRequested += () => asked = true;

            PolicyChip chip = Chip(vm, "הרשאות");
            chip.Select(Option(chip, value));

            Assert.Equal(value, policies.Elevation);
            Assert.False(asked);
        }
    }

    [Fact]
    public void Every_chip_writes_its_value_through()
    {
        var (vm, policies) = NewJob();

        PolicyChip conflict = Chip(vm, "התנגשות");
        conflict.Select(Option(conflict, ConflictPolicy.KeepBoth));
        Assert.Equal(ConflictPolicy.KeepBoth, policies.Conflict);
        Assert.True(conflict.IsModified);
        Assert.Equal("התנגשות: שמור שניהם", conflict.Text);

        PolicyChip verify = Chip(vm, "אימות");
        verify.Select(Option(verify, VerifyPolicy.EveryFile));
        Assert.Equal(VerifyPolicy.EveryFile, policies.Verify);

        PolicyChip priority = Chip(vm, "עדיפות");
        priority.Select(Option(priority, true));
        Assert.True(policies.BackgroundIo);

        // Back to the default: the accent has to go away too, or a chip the user
        // reset still reads as changed.
        conflict.Select(Option(conflict, ConflictPolicy.Ask));
        Assert.False(conflict.IsModified);
    }
}
