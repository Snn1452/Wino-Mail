using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Notifications;
using Wino.Core.Domain.Translations;
using Wino.Messaging.UI;

namespace Wino.Mail.NotificationHost;

internal sealed class BackgroundNotificationBuilder : INotificationBuilder
{
    private readonly IAccountService _accountService;
    private readonly IMailService _mailService;
    private readonly IPreferencesService _preferencesService;
    private readonly INotificationPolicyService _notificationPolicyService;

    public BackgroundNotificationBuilder(
        IAccountService accountService,
        IMailService mailService,
        IPreferencesService preferencesService,
        INotificationPolicyService notificationPolicyService)
    {
        _accountService = accountService;
        _mailService = mailService;
        _preferencesService = preferencesService;
        _notificationPolicyService = notificationPolicyService;
    }

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> downloadedMailItems)
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var pending = new List<MailCopy>();

        foreach (var downloaded in downloadedMailItems)
        {
            var item = await _mailService.GetSingleMailItemAsync(downloaded.UniqueId).ConfigureAwait(false);
            if (item == null)
                continue;

            var account = accounts.FirstOrDefault(x => x.Id == item.AssignedFolder?.MailAccountId);
            if (account == null)
                continue;

            var settings = NotificationSettingsResolver.ResolveMail(_preferencesService, account.Preferences);
            if (!settings.IsEnabled)
                continue;

            if (settings.Scope == MailNotificationScope.InboxOnly &&
                item.AssignedFolder?.SpecialFolderType != SpecialFolderType.Inbox)
                continue;

            if (settings.Scope == MailNotificationScope.FocusedInboxOnly &&
                (item.AssignedFolder?.SpecialFolderType != SpecialFolderType.Inbox || !item.IsFocused))
                continue;

            pending.Add(item);
        }

        if (pending.Count == 0)
            return;

        if (pending.Count > 3)
        {
            var builder = new AppNotificationBuilder()
                .AddText(Translator.Notifications_MultipleNotificationsTitle)
                .AddText(string.Format(Translator.Notifications_MultipleNotificationsMessage, pending.Count))
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail)
                .SetAudioUri(new Uri("ms-winsoundevent:Notification.Mail"));

            Show(builder.BuildNotification(), null);
            return;
        }

        foreach (var item in pending)
        {
            var settings = NotificationSettingsResolver.ResolveMail(_preferencesService, null);
            var builder = new AppNotificationBuilder()
                .AddText(item.FromName)
                .AddText(item.Subject)
                .AddArgument(Constants.ToastMailUniqueIdKey, item.UniqueId.ToString())
                .AddArgument(Constants.ToastActionKey, MailOperation.Navigate.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail)
                .SetAudioEvent((AppNotificationSoundEvent)settings.Sound);

            Show(builder.BuildNotification(), item.UniqueId.ToString());
        }
    }

    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems)
        => Task.CompletedTask;

    public Task UpdateTaskbarIconBadgeAsync()
        => Task.CompletedTask;

    public Task UpdateJumpListOptionsAsync()
        => Task.CompletedTask;

    public Task AddCalendarTaskbarBadgeCountAsync(int newlyDownloadedCount)
        => Task.CompletedTask;

    public Task ClearCalendarTaskbarBadgeAsync()
        => Task.CompletedTask;

    public void RemoveNotification(Guid mailUniqueId)
    {
        try
        {
            AppNotificationManager.Default.RemoveByTagAsync(mailUniqueId.ToString()).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
    {
        if (account?.Preferences?.IsNotificationsEnabled != true)
            return;

        var builder = new AppNotificationBuilder()
            .AddText(Translator.Exception_AccountNeedsAttention_Title)
            .AddText(string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name))
            .AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

        Show(builder.BuildNotification(), $"attention-{account.Id:N}");
    }

    public void CreateWebView2RuntimeMissingNotification()
    {
    }

    public Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long reminderDurationInSeconds)
        => Task.CompletedTask;

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem)
        => Task.CompletedTask;

    public Task CreateTestPeopleNotificationAsync(AccountContact contact)
        => Task.CompletedTask;

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task)
        => Task.CompletedTask;

    private static void Show(AppNotification notification, string? tag)
    {
        if (!string.IsNullOrWhiteSpace(tag))
            notification.Tag = tag;

        AppNotificationManager.Default.Show(notification);
    }
}
