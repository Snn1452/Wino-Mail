using System.Text.Json;
using FluentAssertions;
using Wino.Mail.BackgroundSyncHost;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class ReleaseIdentityTests
{
    [Fact]
    public void ProfilesIsolateAllRuntimeKeysAndIgnoreVersion()
    {
        var root = FindRepository();
        var paths = new[] { "src/Wino.Mail.WinUI/release-profile.json", "scripts/release/windows/profiles/Sideload.json", "scripts/release/windows/profiles/Beta.json" };
        var identities = paths.Select(path =>
        {
            path = Path.Combine(root, path);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var name = json.RootElement.GetProperty("PackageName").GetString()!;
            var publisher = json.RootElement.GetProperty("Publisher").GetString()!;
            return ReleaseIdentity.Load(path, name, publisher, name + "_publisherHash");
        }).ToArray();

        identities.Select(identity => identity.SingleInstanceKey).Should().OnlyHaveUniqueItems();
        identities.Select(identity => identity.MailHostMutexName).Should().OnlyHaveUniqueItems();
        identities.Select(identity => identity.AlternateModeEventName).Should().OnlyHaveUniqueItems();
        identities.SelectMany(identity => identity.NotificationActivatorIds.Values).Should().OnlyHaveUniqueItems();
        identities[2].AllowsLegacyMigration.Should().BeFalse();
        identities[2].DisplayNames.Values.Should().OnlyContain(name => name.EndsWith(" Beta"));
        identities.Take(2).Should().OnlyContain(identity => identity.AllowsLegacyMigration);
    }

    [Fact]
    public void RejectsProfileFromAnotherInstallation()
    {
        var path = Path.Combine(FindRepository(), "scripts/release/windows/profiles/Beta.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var publisher = json.RootElement.GetProperty("Publisher").GetString()!;
        var wrongName = () => ReleaseIdentity.Load(path, "WinoMail.Sideload", publisher, "family");
        var wrongPublisher = () => ReleaseIdentity.Load(path, "WinoMail.Beta", "CN=other", "family");
        wrongName.Should().Throw<InvalidDataException>();
        wrongPublisher.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void AutomaticSynchronizationStartsOnlyWhenUiIsClosedAndNotTerminating(
        bool isWinUiProcessRunning,
        bool terminateRequested,
        bool expected)
    {
        BackgroundSyncLoopPolicy
            .ShouldStartLoops(isWinUiProcessRunning, terminateRequested)
            .Should()
            .Be(expected);
    }

    [Fact]
    public void ActiveBackgroundSynchronizationYieldsWhenWinUiStarts()
    {
        BackgroundSyncLoopPolicy.ShouldYieldToUi(isWinUiProcessRunning: true).Should().BeTrue();
        BackgroundSyncLoopPolicy.ShouldYieldToUi(isWinUiProcessRunning: false).Should().BeFalse();
    }

    [Fact]
    public void PackageManifestHasSingleUserStartupTaskAndKeepsBackgroundHostOnDemand()
    {
        var root = FindRepository();
        var manifestPath = Path.Combine(root, "src/Wino.Mail.WinUI/Package.appxmanifest");
        var document = System.Xml.Linq.XDocument.Load(manifestPath);
        var ns = document.Root!.Name.Namespace;

        var startupTasks = document
            .Descendants()
            .Where(element => (string?)element.Attribute("Category") == "windows.startupTask")
            .ToArray();

        startupTasks.Should().ContainSingle();
        var startupTask = startupTasks[0].Elements().SingleOrDefault(element => element.Name.LocalName == "StartupTask");
        startupTask.Should().NotBeNull();
        var confirmedStartupTask = startupTask!;
        var startupTaskId = confirmedStartupTask.Attribute("TaskId")!.Value;
        startupTaskId.Should().Be("WinoStartupId");

        var activationSourcePath = Path.Combine(root, "src/Wino.Mail.WinUI/Activation/SessionStartActivation.cs");
        File.ReadAllText(activationSourcePath)
            .Should()
            .Contain($"private const string StartupTaskId = \"{startupTaskId}\";");

        var startupOwner = confirmedStartupTask.Ancestors().Single(element => element.Name.LocalName == "Application");
        ((string?)startupOwner.Attribute("Id")).Should().Be("App");

        var backgroundSyncHost = document.Descendants(ns + "Application")
            .SingleOrDefault(application => (string?)application.Attribute("Id") == "BackgroundSyncHost");
        backgroundSyncHost.Should().NotBeNull();
        var confirmedBackgroundSyncHost = backgroundSyncHost!;
        confirmedBackgroundSyncHost.Descendants().Should().NotContain(element => element.Name.LocalName == "StartupTask");
        confirmedBackgroundSyncHost.Descendants().Should().Contain(element => element.Name.LocalName == "FullTrustProcess");

        document.Descendants()
            .Should()
            .Contain(element => (string?)element.Attribute("Category") == "windows.backgroundTasks");

        document.Descendants()
            .Should()
            .Contain(element => element.Name.LocalName == "ActivatableClass" &&
                (string?)element.Attribute("ActivatableClassId") == "Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.Task");
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "WinoMail.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository fixture not found.");
    }
}
