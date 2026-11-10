using System.Text.Json;
using FluentAssertions;
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

    [Fact]
    public void PackageManifestHasOneBackgroundStartupTaskAndKeepsUiOutOfStartupTaskList()
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
        startupTask!.Attribute("TaskId")!.Value.Should().Be("WinoBackgroundSyncStartupId");

        document.Descendants(ns + "Application")
            .Should()
            .ContainSingle(application => (string?)application.Attribute("Id") == "BackgroundSyncHost");

        document.Descendants()
            .Where(element => element.Name.LocalName == "StartupTask")
            .Should()
            .NotContain(task => (string?)task.Attribute("TaskId") == "WinoStartupId");
    }

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "WinoMail.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository fixture not found.");
    }
}
