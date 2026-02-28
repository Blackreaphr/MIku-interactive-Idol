using Companion.Core;

namespace Companion.Tests;

public sealed class MischiefGateTests
{
    [Fact]
    public void CanExecute_AllowsNonMischiefAlways()
    {
        var gate = new MischiefGate();
        Assert.True(gate.CanExecute(CompanionActionKind.Harmless));
        Assert.True(gate.CanExecute(CompanionActionKind.Assistive));
    }

    [Fact]
    public void SetEnabled_ControlsMischiefExecutionAndRaisesChanged()
    {
        var changes = 0;
        var gate = new MischiefGate();
        gate.Changed += (_, _) => changes++;

        gate.SetEnabled(true, TimeSpan.FromMinutes(5));

        Assert.True(gate.Enabled);
        Assert.True(gate.CanExecute(CompanionActionKind.Mischief));
        Assert.Equal(1, changes);

        gate.ForceOff();
        Assert.False(gate.Enabled);
        Assert.False(gate.CanExecute(CompanionActionKind.Mischief));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CanExecute_AutoOffDisablesMischiefWhenExpired()
    {
        var now = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
        var changes = 0;
        var gate = new MischiefGate(() => now);
        gate.Changed += (_, _) => changes++;

        gate.SetEnabled(true, TimeSpan.FromMinutes(1));
        Assert.True(gate.CanExecute(CompanionActionKind.Mischief));

        now = now.AddMinutes(2);
        Assert.False(gate.CanExecute(CompanionActionKind.Mischief));
        Assert.False(gate.Enabled);
        Assert.Equal(2, changes);
    }
}
