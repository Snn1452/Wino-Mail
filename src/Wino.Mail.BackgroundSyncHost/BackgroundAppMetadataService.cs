using Windows.ApplicationModel;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Telemetry;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class BackgroundAppMetadataService : IAppMetadataService
{
    public string AppVersion
    {
        get
        {
            var version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
    }

    public string PackageName => Package.Current.Id.Name;
#if DEBUG
    public string BuildConfiguration => AppTelemetryMetadata.GetBuildConfiguration(true);
    public string SentryEnvironment => AppTelemetryMetadata.GetEnvironment(true);
#else
    public string BuildConfiguration => AppTelemetryMetadata.GetBuildConfiguration(false);
    public string SentryEnvironment => AppTelemetryMetadata.GetEnvironment(false);
#endif
    public string SentryRelease => AppTelemetryMetadata.GetRelease(AppVersion);
    public string SentryDist => AppTelemetryMetadata.NormalizeAppVersion(AppVersion);
}