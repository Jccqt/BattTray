using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BattTray.Devices;

namespace BattTray.Tests;

/// <summary>
/// The version the exe reports about itself — the one thing a user who downloaded a single
/// unsigned file can check before running it.
/// </summary>
/// <remarks>
/// The release workflow refuses to build when the tag and the csproj disagree, and that check
/// can only run on a tag, which leaves it unexercised on every ordinary push: a script nobody
/// executes until the one moment it matters. These cover the half that needs no tag — that the
/// element the gate reads is still there to be read, and that the version in it survives the
/// build into the fields Explorer shows. What is left to the workflow is only the comparison
/// against the tag, which nothing here could stand in for.
/// </remarks>
public class VersionPropertiesTests
{
    /// <summary>
    /// The app's csproj, found from this source file rather than from the working directory,
    /// which is not the same under `dotnet test`, under the IDE and under CI.
    /// </summary>
    static XDocument AppProject([CallerFilePath] string thisFile = "") =>
        XDocument.Load(Path.Combine(
            Path.GetDirectoryName(thisFile)!, "..", "..", "BattTray", "BattTray.csproj"));

    /// <summary>What the csproj declares, e.g. "0.1.0".</summary>
    static string Declared =>
        AppProject().Root!.Elements("PropertyGroup").Elements("Version").Single().Value.Trim();

    /// <summary>The numeric part, without any prerelease suffix — what the build carries into
    /// the file version, which has no way to express the suffix.</summary>
    static string DeclaredCore => Declared.Split('-')[0];

    static readonly Assembly App = typeof(Peripheral).Assembly;

    [Fact]
    public void TheCsprojStatesItsVersionExactlyOnce()
    {
        // The release gate reads /Project/PropertyGroup/Version and nothing else. Split into
        // VersionPrefix and VersionSuffix it would read nothing and say so; stated twice it
        // would silently pick the first and could pass against a value that never ships.
        var stated = AppProject().Root!.Elements("PropertyGroup").Elements("Version").ToList();

        Assert.Single(stated);
    }

    [Fact]
    public void TheVersionIsThreeNumbersAndAnOptionalSuffix() =>
        // Not pedantry: the file version below is derived by dropping the suffix and padding
        // the rest to four parts, and that arithmetic only means anything against a known
        // shape. A two-part or five-part version would still build and still be wrong here.
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"), Declared);

    [Fact]
    public void TheBinaryCarriesTheCsprojVersionWhereExplorerShowsIt()
    {
        // Properties > Details reads the binary's version resource, not the csproj, and every
        // step between the two is an MSBuild default. An explicit <FileVersion>, or a
        // Directory.Build.targets added later, moves one without the other — and this is the
        // only place an unsigned download can be checked, so the drift would be invisible to
        // everyone here and visible only to the person least able to explain it.
        string fileVersion = App.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;

        Assert.Equal(DeclaredCore, string.Join('.', fileVersion.Split('.').Take(3)));
        Assert.All(fileVersion.Split('.').Skip(3), part => Assert.Equal("0", part));
    }

    [Fact]
    public void TheVersionTheRunningAppStatesIsTheOneTheCsprojDeclares() =>
        // What the tray's footer row shows and what the dump header stamps both come from here,
        // so this is the assertion that makes either of them worth reading: a row quoting a
        // number the build did not produce would be worse than no row, since a bug report would
        // be filed against a binary that never existed.
        Assert.Equal(Declared, AppVersion.Display);

    [Fact]
    public void TheDisplayVersionDropsOnlyTheBuildMetadata()
    {
        // Split on '+' rather than on '-': a "0.2.0-beta1" build is a different thing from
        // "0.2.0" to whoever reads the report, and the prerelease suffix is part of the version
        // rather than a note about which commit it came from.
        Assert.StartsWith(AppVersion.Display, AppVersion.Full, StringComparison.Ordinal);
        Assert.DoesNotContain('+', AppVersion.Display);
    }

    [Fact]
    public void TheProductVersionKeepsTheWholeDeclaredVersion() =>
        // The tab shows this one too, and unlike the file version it can carry a prerelease
        // suffix — so it is the field a "-beta1" release is actually legible in. The commit
        // hash MSBuild appends after a '+' is welcome; it is what makes a dump attributable.
        Assert.StartsWith(
            Declared,
            App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            StringComparison.Ordinal);
}
