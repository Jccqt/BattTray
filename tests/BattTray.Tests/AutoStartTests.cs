using BattTray.Settings;

namespace BattTray.Tests;

/// <summary>
/// Reading the executable back out of a stored Run command.
/// </summary>
/// <remarks>
/// Everything else in <see cref="AutoStart"/> is the registry and belongs to the machine it
/// runs on, but this one comparison decides whether the checkbox agrees with what happens at
/// login. Get it wrong in one direction and an entry this app wrote reads as off, so the user
/// ticks a box that was already true; wrong in the other and an entry belonging to a different
/// copy of the app gets hijacked on the next toggle.
///
/// Note that nothing here touches HKCU. These tests can run anywhere, including on a build
/// agent, and they leave no trace on the developer's own startup.
/// </remarks>
public class AutoStartTests
{
    [Fact]
    public void ReadsAQuotedPathWithArguments()
    {
        // The shape this app writes, and the only one it writes.
        Assert.Equal(
            @"C:\Program Files\BattTray\BattTray.exe",
            AutoStart.ExecutableFrom($"\"C:\\Program Files\\BattTray\\BattTray.exe\" {AutoStart.StartupSwitch}"));
    }

    [Fact]
    public void ReadsAQuotedPathWithNoArguments()
    {
        // What an entry written before --autostart existed looks like. It must still read as
        // this executable, or the upgrade path in UpgradeCommand never runs.
        Assert.Equal(
            @"C:\Apps\BattTray.exe",
            AutoStart.ExecutableFrom("\"C:\\Apps\\BattTray.exe\""));
    }

    [Fact]
    public void ReadsAnUnquotedPathWithNoSpaces()
    {
        Assert.Equal(@"C:\Apps\BattTray.exe", AutoStart.ExecutableFrom(@"C:\Apps\BattTray.exe"));
    }

    [Fact]
    public void ReadsAnUnquotedPathUpToTheFirstArgument()
    {
        // Read the way the shell reads its simplest case. Entries this app writes are always
        // quoted, so an unquoted one was left by hand.
        Assert.Equal(
            @"C:\Apps\BattTray.exe",
            AutoStart.ExecutableFrom($@"C:\Apps\BattTray.exe {AutoStart.StartupSwitch}"));
    }

    [Fact]
    public void UnquotedPathsWithSpacesStopAtTheSpace()
    {
        // Not the right answer about the file, but it is the shell's answer, and matching the
        // shell is the point: the comparison decides whether this entry starts *this* exe,
        // and if the shell cannot start it either then reporting "off" is correct.
        Assert.Equal(@"C:\Program", AutoStart.ExecutableFrom(@"C:\Program Files\BattTray\BattTray.exe"));
    }

    [Fact]
    public void TrimsSurroundingWhitespace()
    {
        Assert.Equal(@"C:\Apps\BattTray.exe", AutoStart.ExecutableFrom("   \"C:\\Apps\\BattTray.exe\"  "));
    }

    [Fact]
    public void AnUnterminatedQuoteYieldsTheRest()
    {
        // Malformed, and it must not throw: the value came out of a registry key anyone can
        // edit, and an exception here would be an exception on the settings dialog opening.
        Assert.Equal(@"C:\Apps\BattTray.exe", AutoStart.ExecutableFrom("\"C:\\Apps\\BattTray.exe"));
    }

    [Fact]
    public void AnEmptyCommandYieldsAnEmptyPath()
    {
        Assert.Equal(string.Empty, AutoStart.ExecutableFrom(string.Empty));
        Assert.Equal(string.Empty, AutoStart.ExecutableFrom("   "));
    }

    [Fact]
    public void ComparisonIsCaseInsensitiveAsWindowsPathsAre()
    {
        // IsEnabled compares with OrdinalIgnoreCase; this pins the assumption behind that,
        // since the registry preserves whatever case the writer used.
        string stored = AutoStart.ExecutableFrom("\"C:\\APPS\\BATTTRAY.EXE\" --autostart");

        Assert.Equal(@"C:\Apps\BattTray.exe", stored, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheStartupSwitchIsWhatTheRunEntryPasses()
    {
        // Changing this string silently is the failure it guards: the exe would open the
        // settings dialog at every login, having stopped recognising its own launch.
        Assert.Equal("--autostart", AutoStart.StartupSwitch);
    }
}
