using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.NotificationHost.Contracts;
using Wino.NotificationHost;

namespace Wino.Mail.NotificationHost;

internal sealed class BackgroundNotificationBuilder(
    IAccountService accountService,
    IMailService mailService,
    IPreferencesService preferencesService,
    INotificationPolicyService notificationPolicyService) : INotificationBuilder
{
    private readonly IAccountService _accountService = accountService;
    private readonly IMailService _mailService = mailService;
    private readonly IPreferencesService _preferencesService = preferencesService;
    private readonly INotificationPolicyService _notificationPolicyService = notificationPolicyService;

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> downloadedMailItems)
    {
        try
        {
            var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);

            foreach (var item in downloadedMailItems)
            {
                var mail = await _mailService.GetSingleMailItemAsync(item.UniqueId).ConfigureAwait(false);
                if (mail == null)
                    continue;

                var account = accounts.FirstOrDefault(a => a.Id == mail.AssignedFolder?.MailAccountId);
                var prefs = account?.Preferences;
                var settings = NotificationSettingsResolver.ResolveMail(_preferencesService, prefs);

                if (!settings.IsEnabled || !IsInScope(mail, settings.Scope))
                    continue;

                var decision = _notificationPolicyService.Evaluate(
                    NotificationKind.Mail,
                    prefs,
                    DateTimeOffset.Now);

                if (!decision.ShouldDeliver)
                    continue;

                var builder = new AppNotificationBuilder();
                builder.AddText(GetSenderText(mail, settings.Content));
                if (settings.Content is MailNotificationContent.SenderSubject or MailNotificationContent.SenderSubjectPreview)
                    builder.AddText(mail.Subject);
                if (settings.Content == MailNotificationContent.SenderSubjectPreview)
                    builder.AddText(mail.PreviewText);

                builder.AddArgument(Constants.ToastMailUniqueIdKey, mail.UniqueId.ToString());
                builder.AddArgument(Constants.ToastActionKey, MailOperation.Navigate.ToString());
                builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

                await DispatchAsync(builder.BuildNotification(), mail.UniqueId.ToString()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            NotificationHostLogger.Write("background-notification-failed", exception: ex);
        }
    }

    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems) => Task.CompletedTask;

    public Task UpdateTaskbarIconBadgeAsync() => Task.CompletedTask;
    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;
    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount) => Task.CompletedTask;
    public Task ClearCalendarTaskbarBadgeAsync() => Task.CompletedTask;
    public void RemoveNotification(Guid mailUniqueId) { }
    public void CreateWebView2RuntimeMissingNotification() { }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true)
            return;

        var decision = _notificationPolicyService.Evaluate(
            NotificationKind.Other,
            account.Preferences,
            DateTimeOffset.Now);

        if (!decision.ShouldDeliver)
            return;

        var builder = new AppNotificationBuilder();
        builder.AddText(Translator.Exception_AccountNeedsAttention_Title);
        builder.AddText(string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name));
        builder.AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString());
        builder.AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

        _ = DispatchAsync(builder.BuildNotification());
    }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
        => Task.CompletedTask;

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => Task.CompletedTask;
    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => Task.CompletedTask;
    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => Task.CompletedTask;

    private static string GetSenderText(MailCopy mail, MailNotificationContent content)
        => content == MailNotificationContent.Nothing
            ? Translator.Notifications_MultipleNotificationsTitle
            : mail.FromName;

    private static bool IsInScope(MailCopy mail, MailNotificationScope scope)
    {
        if (scope == MailNotificationScope.AllFolders)
            return true;

        var inbox = mail.AssignedFolder?.SpecialFolderType == SpecialFolderType.Inbox;

        return scope switch
        {
            MailNotificationScope.InboxOnly => inbox,
            MailNotificationScope.FocusedInboxOnly => inbox && mail.IsFocused,
            MailNotificationScope.InboxAndCustomFolders => inbox ||
                mail.AssignedFolder?.SpecialFolderType == SpecialFolderType.Other,
            _ => true
        };
    }

    private static async Task DispatchAsync(AppNotification notification, string? tag = null)
    {
        if (!string.IsNullOrWhiteSpace(tag))
            notification.Tag = tag;

        var requestId = Guid.NewGuid();
        var cachePath = Windows.Storage.ApplicationData.Current.LocalCacheFolder.Path;

        await NotificationHostFileStore.WriteRequestAsync(
            cachePath,
            requestId,
            new NotificationHostRequest(
                DateTimeOffset.UtcNow,
                NotificationHostOperation.Show,
                NotificationHostApplication.Mail,
                notification.Payload,
                notification.Tag,
                notification.Group)).ConfigureAwait(false);

        var aumid = $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!{NotificationHostApplicationIds.Mail}";
        PackagedApplicationActivator.Activate(
            aumid,
            NotificationHostLaunchArguments.CreateRequest(requestId));
    }
}
