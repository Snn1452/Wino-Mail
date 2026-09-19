using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Serilog;
using Windows.ApplicationModel;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Notifications;
using Wino.NotificationHost.Contracts;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed class HeadlessNotificationBuilder(
    IAccountService accountService,
    IMailService mailService,
    IUnreadBadgeService unreadBadgeService,
    IPreferencesService preferencesService,
    INotificationPolicyService notificationPolicyService,
    BackgroundNotificationHostClient notificationHostClient) : INotificationBuilder
{
    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> downloadedMailItems)
    {
        try
        {
            var accounts = await accountService.GetAccountsAsync().ConfigureAwait(false);
            var notifications = new List<(MailCopy Mail, MailAccountPreferences? Preferences)>();

            foreach (var downloadedMail in downloadedMailItems ?? [])
            {
                try
                {
                    var mail = await mailService.GetSingleMailItemAsync(downloadedMail.UniqueId).ConfigureAwait(false);
                    if (mail is null)
                        continue;

                    var account = accounts.FirstOrDefault(candidate =>
                        candidate.Id == mail.AssignedFolder?.MailAccountId);
                    var settings = NotificationSettingsResolver.ResolveMail(
                        preferencesService,
                        account?.Preferences);

                    if (settings.IsEnabled && IsWithinNotificationScope(mail, settings.Scope))
                        notifications.Add((mail, account?.Preferences));
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Background notification preparation failed for mail {MailUniqueId}", downloadedMail.UniqueId);
                }
            }

            if (notifications.Count == 0)
                return;

            if (notifications.Count > 3)
            {
                var builder = new AppNotificationBuilder()
                    .AddText(Translator.Notifications_MultipleNotificationsTitle)
                    .AddText(string.Format(
                        Translator.Notifications_MultipleNotificationsMessage,
                        notifications.Count))
                    .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail)
                    .AddButton(CreateDismissButton());

                builder.SetAudioEvent(AppNotificationSoundEvent.Default);
                await ShowAsync(NotificationHostApplication.Mail, builder).ConfigureAwait(false);
            }
            else
            {
                foreach (var (mail, accountPreferences) in notifications)
                    await CreateMailNotificationAsync(mail, accountPreferences).ConfigureAwait(false);
            }

            await UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background mail notification creation failed.");
        }
    }

    private async Task CreateMailNotificationAsync(
        MailCopy mail,
        MailAccountPreferences? accountPreferences)
    {
        try
        {
            var settings = NotificationSettingsResolver.ResolveMail(
                preferencesService,
                accountPreferences);

            var builder = new AppNotificationBuilder();
            builder.SetTimeStamp(mail.CreationDate.ToLocalTime());

            if (settings.Content == MailNotificationContent.Nothing)
            {
                builder.AddText(Translator.Notifications_MultipleNotificationsTitle);
            }
            else
            {
                builder.AddText(mail.FromName);

                if (settings.Content != MailNotificationContent.SenderOnly)
                {
                    builder.AddText(mail.Subject);

                    if (settings.Content == MailNotificationContent.SenderSubjectPreview)
                        builder.AddText(mail.PreviewText);
                }
            }

            builder
                .AddArgument(Constants.ToastMailUniqueIdKey, mail.UniqueId.ToString())
                .AddArgument(Constants.ToastActionKey, MailOperation.Navigate.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

            var firstAction = preferencesService.FirstMailNotificationAction;
            var secondAction = preferencesService.SecondMailNotificationAction;

            builder
                .AddButton(CreateMailActionButton(firstAction, mail.UniqueId))
                .AddButton(CreateMailActionButton(secondAction, mail.UniqueId))
                .AddButton(CreateDismissButton())
                .SetAudioEvent((AppNotificationSoundEvent)settings.Sound);

            await ShowAsync(
                    NotificationHostApplication.Mail,
                    builder,
                    mail.UniqueId.ToString(),
                    accountPreferences)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background mail notification failed for mail {MailUniqueId}", mail.UniqueId);
        }
    }

    public Task UpdateTaskbarIconBadgeAsync() => UpdateBadgeAsync();

    public Task AddCalendarTaskbarBadgeCountAsync(int count)
    {
        if (count > 0)
            UpdateBadge("CalendarApp", count);

        return Task.CompletedTask;
    }

    public Task ClearCalendarTaskbarBadgeAsync()
    {
        UpdateBadge("CalendarApp", null);
        return Task.CompletedTask;
    }

    public void RemoveNotification(Guid mailUniqueId)
    {
        // Read-state driven removal is owned by the interactive notification client.
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
        => _ = ShowAttentionSafeAsync(account);

    public void CreateWebView2RuntimeMissingNotification()
    {
        // The background process never hosts WebView2.
    }

    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems)
        => Task.CompletedTask;

    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem)
        => Task.CompletedTask;

    public Task CreateTestPeopleNotificationAsync(AccountContact contact)
        => Task.CompletedTask;

    public Task CreateTestTaskReminderNotificationAsync(AccountTask task)
        => Task.CompletedTask;

    public Task UpdateJumpListOptionsAsync()
        => Task.CompletedTask;

    public async Task CreateCalendarReminderNotificationAsync(
        CalendarItem calendarItem,
        long duration)
    {
        if (calendarItem is null)
            return;

        try
        {
            var accountPreferences = calendarItem.AssignedCalendar?.AccountId is { } accountId
                ? (await accountService.GetAccountAsync(accountId).ConfigureAwait(false))?.Preferences
                : null;

            var localStart = calendarItem.GetLocalStartDate();
            var builder = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.Reminder)
                .AddText(calendarItem.Title)
                .AddText($\"{GetCalendarReminderContext(localStart, DateTime.Now)} - {localStart:g}\");

            if (!string.IsNullOrWhiteSpace(calendarItem.Location))
                builder.AddText(calendarItem.Location);

            builder
                .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction)
                .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar)
                .SetAudioEvent((AppNotificationSoundEvent)preferencesService.CalendarNotificationSoundEvent);

            var allowedSnoozeMinutes = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
                duration,
                preferencesService.DefaultReminderDurationInSeconds);

            if (allowedSnoozeMinutes.Count > 0)
            {
                var preferredSnoozeMinutes = preferencesService.DefaultSnoozeDurationInMinutes;
                var defaultSnoozeMinutes = allowedSnoozeMinutes.Contains(preferredSnoozeMinutes)
                    ? preferredSnoozeMinutes
                    : allowedSnoozeMinutes[0];

                var selectionBox = new AppNotificationComboBox(
                    Constants.ToastCalendarSnoozeDurationInputId)
                    .SetSelectedItem(defaultSnoozeMinutes.ToString());

                foreach (var snoozeMinutes in allowedSnoozeMinutes)
                {
                    selectionBox.AddItem(
                        snoozeMinutes.ToString(),
                        string.Format(
                            Translator.CalendarReminder_SnoozeMinutesOption,
                            snoozeMinutes));
                }

                builder.AddComboBox(selectionBox);
                builder.AddButton(
                    new AppNotificationButton(Translator.CalendarReminder_SnoozeAction)
                        .SetIcon(GetNotificationIconUri("calendar-snooze"))
                        .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarSnoozeAction)
                        .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                        .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));
            }

            builder.AddButton(
                new AppNotificationButton(Translator.Buttons_Open)
                    .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction)
                    .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                    .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));

            if (CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out _))
            {
                builder.AddButton(
                    new AppNotificationButton(Translator.CalendarEventDetails_JoinOnline)
                        .SetIcon(GetNotificationIconUri("calendar-join"))
                        .AddArgument(Constants.ToastCalendarActionKey, Constants.ToastCalendarJoinOnlineAction)
                        .AddArgument(Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString())
                        .AddArgument(Constants.ToastModeKey, Constants.ToastModeCalendar));
            }

            builder.AddButton(CreateDismissButton());

            await ShowAsync(
                    NotificationHostApplication.Calendar,
                    builder,
                    $\"calendar-reminder-{calendarItem.Id:N}-{duration}\",
                    accountPreferences)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background calendar reminder notification failed for event {CalendarItemId}", calendarItem.Id);
        }
    }

    private async Task UpdateBadgeAsync()
    {
        try
        {
            var snapshot = await unreadBadgeService.GetSnapshotAsync().ConfigureAwait(false);
            UpdateBadge("App", snapshot.TaskbarUnreadCount > 0 ? snapshot.TaskbarUnreadCount : null);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background taskbar badge update failed.");
        }
    }

    private async Task ShowAttentionSafeAsync(MailAccount account)
    {
        try
        {
            if (account?.Preferences?.IsNotificationsEnabled != true)
                return;

            var builder = new AppNotificationBuilder()
                .AddText(Translator.Exception_AccountNeedsAttention_Title)
                .AddText(string.Format(
                    Translator.Exception_AccountNeedsAttention_Message,
                    account.Name))
                .AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString())
                .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail)
                .AddButton(
                    new AppNotificationButton(Translator.Buttons_FixAccount)
                        .AddArgument(Constants.ToastMailAccountIdKey, account.Id.ToString())
                        .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail))
                .AddButton(CreateDismissButton());

            await ShowAsync(
                    NotificationHostApplication.Mail,
                    builder,
                    kindOverride: NotificationKind.Other)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background account-attention notification failed for account {AccountId}", account?.Id);
        }
    }

    private async Task ShowAsync(
        NotificationHostApplication application,
        AppNotificationBuilder builder,
        string? tag = null,
        MailAccountPreferences? accountPreferences = null,
        NotificationKind? kindOverride = null)
    {
        var decision = notificationPolicyService.Evaluate(
            kindOverride ?? (application == NotificationHostApplication.Mail
                ? NotificationKind.Mail
                : NotificationKind.CalendarReminder),
            accountPreferences,
            DateTimeOffset.Now);

        if (!decision.ShouldDeliver)
            return;

        var notification = builder.BuildNotification();
        if (!string.IsNullOrWhiteSpace(tag))
            notification.Tag = tag;

        await notificationHostClient.ShowAsync(application, notification).ConfigureAwait(false);
    }

    private static AppNotificationButton CreateDismissButton()
        => new AppNotificationButton(Translator.Buttons_Dismiss)
            .AddArgument(Constants.ToastDismissActionKey, bool.TrueString);

    private static AppNotificationButton CreateMailActionButton(MailOperation operation, Guid mailUniqueId)
        => new AppNotificationButton(GetOperationString(operation))
            .AddArgument(Constants.ToastMailUniqueIdKey, mailUniqueId.ToString())
            .AddArgument(Constants.ToastActionKey, operation.ToString())
            .AddArgument(Constants.ToastModeKey, Constants.ToastModeMail);

    private static string GetOperationString(MailOperation operation)
        => operation switch
        {
            MailOperation.Archive => Translator.MailOperation_Archive,
            MailOperation.SoftDelete => Translator.MailOperation_Delete,
            MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
            MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
            MailOperation.Reply => Translator.MailOperation_Reply,
            MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
            MailOperation.Forward => Translator.MailOperation_Forward,
            _ => operation.ToString()
        };

    private static bool IsWithinNotificationScope(
        MailCopy mail,
        MailNotificationScope scope)
    {
        if (scope == MailNotificationScope.AllFolders)
            return true;

        var isInbox = mail.AssignedFolder?.SpecialFolderType == SpecialFolderType.Inbox;

        return scope switch
        {
            MailNotificationScope.InboxOnly => isInbox,
            MailNotificationScope.FocusedInboxOnly => isInbox && mail.IsFocused,
            MailNotificationScope.InboxAndCustomFolders =>
                isInbox || mail.AssignedFolder?.SpecialFolderType == SpecialFolderType.Other,
            _ => true
        };
    }

    private static string GetCalendarReminderContext(DateTime localStart, DateTime nowLocal)
    {
        var delta = localStart - nowLocal;
        var absoluteDelta = delta.Duration();

        if (absoluteDelta < TimeSpan.FromMinutes(1))
            return delta.TotalSeconds >= 0
                ? Translator.CalendarReminder_StartingNow
                : Translator.CalendarReminder_StartedNow;

        if (delta.TotalSeconds > 0)
        {
            if (delta.TotalHours >= 1)
            {
                var hours = Math.Max(1, (int)Math.Floor(delta.TotalHours));
                return string.Format(Translator.CalendarReminder_StartsInHours, hours);
            }

            var minutes = Math.Max(1, (int)Math.Floor(delta.TotalMinutes));
            return string.Format(Translator.CalendarReminder_StartsInMinutes, minutes);
        }

        if (absoluteDelta.TotalHours >= 1)
        {
            var hours = Math.Max(1, (int)Math.Floor(absoluteDelta.TotalHours));
            return string.Format(Translator.CalendarReminder_StartedHoursAgo, hours);
        }

        var minutesSinceStart = Math.Max(1, (int)Math.Floor(absoluteDelta.TotalMinutes));
        return string.Format(Translator.CalendarReminder_StartedMinutesAgo, minutesSinceStart);
    }

    private static string GetNotificationIconUri(string name)
        => $\"ms-appx:///Assets/NotificationIcons/{name}.png\";

    private static void UpdateBadge(string applicationId, int? count)
    {
        var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication(
            $\"{Package.Current.Id.FamilyName}!{applicationId}\");

        if (!count.HasValue || count.Value <= 0)
        {
            updater.Clear();
            return;
        }

        var document = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
        if (document.SelectSingleNode(\"/badge\") is not XmlElement badgeElement)
        {
            updater.Clear();
            return;
        }

        badgeElement.SetAttribute("value", count.Value.ToString());
        updater.Update(new BadgeNotification(document));
    }
}
