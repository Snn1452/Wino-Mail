using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Wino.Platform.Windows;

public static class ToastNotificationDispatcher
{
    public static void Show(
        string appUserModelId,
        string payload,
        string? tag = null,
        string? group = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        var notifier = ToastNotificationManager.CreateToastNotifier(appUserModelId);
        if (notifier.Setting != NotificationSetting.Enabled)
        {
            throw new InvalidOperationException(
                $"Windows toast notifications are not enabled for '{appUserModelId}'. Current setting: {notifier.Setting}.");
        }

        var document = new XmlDocument();
        document.LoadXml(payload);

        var toast = new ToastNotification(document);

        if (!string.IsNullOrWhiteSpace(tag))
            toast.Tag = tag;

        if (!string.IsNullOrWhiteSpace(group))
            toast.Group = group;

        notifier.Show(toast);
    }

    public static void RemoveByTag(string appUserModelId, string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appUserModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        ToastNotificationManager.History.Remove(tag, string.Empty, appUserModelId);
    }
}
