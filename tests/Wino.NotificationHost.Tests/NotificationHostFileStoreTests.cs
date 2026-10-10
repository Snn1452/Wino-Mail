using FluentAssertions;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationHostFileStoreTests : IDisposable
{
    private readonly string _tempFolder = Path.Combine(Path.GetTempPath(), $"WinoNotificationHost_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempFolder))
            Directory.Delete(_tempFolder, recursive: true);
    }

    [Fact]
    public async Task WriteActivationAsync_PublishesOnlyFinalEnvelope()
    {
        var activationId = Guid.NewGuid();
        var activation = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            NotificationHostApplication.People,
            "ToastModeKey=ToastModePeople",
            new Dictionary<string, string> { ["reply"] = "hello" });

        await NotificationHostFileStore.WriteActivationAsync(_tempFolder, activationId, activation);

        Directory.EnumerateFiles(NotificationHostPaths.GetActivationDirectory(_tempFolder))
            .Should().ContainSingle()
            .Which.Should().Be(NotificationHostPaths.GetActivationPath(_tempFolder, activationId));
        NotificationHostFileStore.ReadActivation(_tempFolder, activationId).Should().BeEquivalentTo(activation);
    }

    [Fact]
    public async Task CleanupStaleFiles_RemovesLegacyRequestsAndPreservesCurrentActivations()
    {
        var oldRequestId = Guid.NewGuid();
        var currentActivationId = Guid.NewGuid();
        var requestDirectory = NotificationHostPaths.GetRequestDirectory(_tempFolder);
        Directory.CreateDirectory(requestDirectory);
        var oldRequestPath = Path.Combine(requestDirectory, $"{oldRequestId:N}.bin");
        await File.WriteAllTextAsync(oldRequestPath, "legacy request");

        await NotificationHostFileStore.WriteActivationAsync(
            _tempFolder,
            currentActivationId,
            new NotificationHostActivation(
                DateTimeOffset.UtcNow,
                NotificationHostApplication.Mail,
                "ToastModeKey=ToastModeMail",
                new Dictionary<string, string>()));

        File.SetLastWriteTimeUtc(oldRequestPath, DateTime.UtcNow.AddDays(-2));

        NotificationHostFileStore.CleanupStaleFiles(_tempFolder, TimeSpan.FromHours(24)).Should().Be(1);
        File.Exists(oldRequestPath).Should().BeFalse();
        File.Exists(NotificationHostPaths.GetActivationPath(_tempFolder, currentActivationId)).Should().BeTrue();
    }
}
