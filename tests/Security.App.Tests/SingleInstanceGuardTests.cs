using Security.App.Services;
using Xunit;

namespace Security.App.Tests;

/// <summary>
/// Single-instance coordination: the first guard in a session is primary, a
/// second guard with the same key is not, and the second can signal the first
/// to raise its activation event (bring the window forward).
///
/// Each test uses a unique key so parallel xUnit collections cannot collide
/// on the named kernel objects.
/// </summary>
public class SingleInstanceGuardTests
{
    private static string UniqueKey() => "Security.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void First_guard_is_primary()
    {
        using var guard = new SingleInstanceGuard(UniqueKey());

        Assert.True(guard.IsPrimary);
    }

    [Fact]
    public void Second_guard_with_the_same_key_is_not_primary()
    {
        var key = UniqueKey();

        using var first = new SingleInstanceGuard(key);
        using var second = new SingleInstanceGuard(key);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public async Task SignalActivation_reaches_the_primary_guard()
    {
        var key = UniqueKey();
        using var primary = new SingleInstanceGuard(key);
        using var secondary = new SingleInstanceGuard(key);

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, _) => tcs.TrySetResult();

        secondary.SignalActivation();

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public void SignalActivation_from_a_secondary_does_not_raise_its_own_event()
    {
        var key = UniqueKey();
        using var primary = new SingleInstanceGuard(key);
        using var secondary = new SingleInstanceGuard(key);

        var primaryCount = 0;
        var secondaryCount = 0;
        primary.ActivationRequested += (_, _) => Interlocked.Increment(ref primaryCount);
        secondary.ActivationRequested += (_, _) => Interlocked.Increment(ref secondaryCount);

        secondary.SignalActivation();
        Thread.Sleep(300);

        Assert.Equal(1, primaryCount);
        Assert.Equal(0, secondaryCount);
    }

    [Fact]
    public void Different_keys_are_independent_instances()
    {
        using var first = new SingleInstanceGuard(UniqueKey());
        using var second = new SingleInstanceGuard(UniqueKey());

        Assert.True(first.IsPrimary);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void Disposing_the_primary_releases_the_lock_for_the_next_instance()
    {
        var key = UniqueKey();

        using (var first = new SingleInstanceGuard(key))
        {
            Assert.True(first.IsPrimary);
        }

        using var second = new SingleInstanceGuard(key);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void SignalActivation_after_dispose_throws_rather_than_silently_lying()
    {
        var key = UniqueKey();
        var guard = new SingleInstanceGuard(key);
        guard.Dispose();

        Assert.Throws<ObjectDisposedException>(() => guard.SignalActivation());
    }
}
