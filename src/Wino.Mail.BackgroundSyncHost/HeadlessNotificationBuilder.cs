using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private static readonly MailOperation[] SupportedMailNotificationActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.MoveToJunk,
        MailOperation.Archive,
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

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

                    var account = accounts.FirstOrDefault(candidate => candidate.Id == mail.AssignedFolder?.MailAccountId);
                    var settings = NotificationSettingsResolver.ResolveMail(preferencesService, account?.Preferences);

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
                var payload = HeadlessNotificationPayloadBuilder.Build(
                    [
                        Translator.Notifications_MultipleNotificationsTitle,
                        string.Format(Translator.Notifications_MultipleNotificationsMessage, notifications.Count)
                    ],
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastModeKey, Constants.ToastModeMail)),
                    [
                        (Translator.Buttons_Dismiss, HeadlessNotificationPayloadBuilder.Arguments(
                            (Constants.ToastDismissActionKey, bool.TrueString)))
                    ],
                    audioEvent: HeadlessNotificationPayloadBuilder.GetAudioEvent(NotificationSoundEvent.Default));

                await ShowAsync(NotificationHostApplication.Mail, payload, accountPreferences: null).ConfigureAwait(false);
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

    private async Task CreateMailNotificationAsync(MailCopy mail, MailAccountPreferences? accountPreferences)
    {
        try
        {
            var settings = NotificationSettingsResolver.ResolveMail(preferencesService, accountPreferences);
            var texts = new List<string>();

            if (settings.Content == MailNotificationContent.Nothing)
            {
                texts.Add(Translator.Notifications_MultipleNotificationsTitle);
            }
            else
            {
                texts.Add(mail.FromName);

                if (settings.Content != MailNotificationContent.SenderOnly)
                {
                    texts.Add(mail.Subject);

                    if (settings.Content == MailNotificationContent.SenderSubjectPreview)
                        texts.Add(mail.PreviewText);
                }
            }

            var (firstAction, secondAction) = GetConfiguredMailNotificationActions();
            var arguments = HeadlessNotificationPayloadBuilder.Arguments(
                (Constants.ToastMailUniqueIdKey, mail.UniqueId.ToString()),
                (Constants.ToastActionKey, MailOperation.Navigate.ToString()),
                (Constants.ToastModeKey, Constants.ToastModeMail));

            var buttons = new List<(string Content, IReadOnlyDictionary<string, string> Arguments)>
            {
                (
                    GetOperationString(firstAction),
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastMailUniqueIdKey, mail.UniqueId.ToString()),
                        (Constants.ToastActionKey, firstAction.ToString()),
                        (Constants.ToastModeKey, Constants.ToastModeMail))
                ),
                (
                    GetOperationString(secondAction),
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastMailUniqueIdKey, mail.UniqueId.ToString()),
                        (Constants.ToastActionKey, secondAction.ToString()),
                        (Constants.ToastModeKey, Constants.ToastModeMail))
                ),
                (
                    Translator.Buttons_Dismiss,
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastDismissActionKey, bool.TrueString))
                )
            };

            var payload = HeadlessNotificationPayloadBuilder.Build(
                texts,
                arguments,
                buttons,
                audioEvent: HeadlessNotificationPayloadBuilder.GetAudioEvent(settings.Sound));

            await ShowAsync(
                NotificationHostApplication.Mail,
                payload,
                mail.UniqueId.ToString(),
                accountPreferences).ConfigureAwait(false);
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
        _ = notificationHostClient.RemoveByTagAsync(NotificationHostApplication.Mail, mailUniqueId.ToString());
    }

    public void CreateAttentionRequiredNotification(MailAccount account)
        => _ = ShowAttentionSafeAsync(account);

    public void CreateWebView2RuntimeMissingNotification()
    {
    }

    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems) => Task.CompletedTask;
    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem calendarItem) => Task.CompletedTask;
    public Task CreateTestPeopleNotificationAsync(AccountContact contact) => Task.CompletedTask;
    public Task CreateTestTaskReminderNotificationAsync(AccountTask task) => Task.CompletedTask;
    public Task UpdateJumpListOptionsAsync() => Task.CompletedTask;

    public async Task CreateCalendarReminderNotificationAsync(CalendarItem calendarItem, long duration)
    {
        if (calendarItem is null)
            return;

        try
        {
            var accountPreferences = calendarItem.AssignedCalendar?.AccountId is { } accountId
                ? (await accountService.GetAccountAsync(accountId).ConfigureAwait(false))?.Preferences
                : null;

            var localStart = calendarItem.GetLocalStartDate();
            var arguments = HeadlessNotificationPayloadBuilder.Arguments(
                (Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction),
                (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString()),
                (Constants.ToastModeKey, Constants.ToastModeCalendar));

            var buttons = new List<(string Content, IReadOnlyDictionary<string, string> Arguments)>();
            (string Id, string DefaultInput, IReadOnlyDictionary<string, string> Options)? selection = null;

            var allowedSnoozeMinutes = CalendarReminderSnoozeOptions.GetAllowedSnoozeMinutes(
                duration,
                preferencesService.DefaultReminderDurationInSeconds);

            if (allowedSnoozeMinutes.Count > 0)
            {
                var preferredSnoozeMinutes = preferencesService.DefaultSnoozeDurationInMinutes;
                var defaultSnoozeMinutes = allowedSnoozeMinutes.Contains(preferredSnoozeMinutes)
                    ? preferredSnoozeMinutes
                    : allowedSnoozeMinutes[0];

                selection = (
                    Constants.ToastCalendarSnoozeDurationInputId,
                    defaultSnoozeMinutes.ToString(),
                    allowedSnoozeMinutes.ToDictionary(
                        minutes => minutes.ToString(),
                        minutes => string.Format(
                            Translator.CalendarReminder_SnoozeMinutesOption,
                            minutes),
                        StringComparer.Ordinal));

                buttons.Add(
                    (
                        Translator.CalendarReminder_SnoozeAction,
                        HeadlessNotificationPayloadBuilder.Arguments(
                            (Constants.ToastCalendarActionKey, Constants.ToastCalendarSnoozeAction),
                            (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString()),
                            (Constants.ToastModeKey, Constants.ToastModeCalendar))
                    ));
            }

            buttons.Add(
                (
                    Translator.Buttons_Open,
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastCalendarActionKey, Constants.ToastCalendarNavigateAction),
                        (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString()),
                        (Constants.ToastModeKey, Constants.ToastModeCalendar))
                ));

            if (CalendarJoinLinkResolver.TryGetEffectiveJoinUri(calendarItem, out _))
            {
                buttons.Add(
                    (
                        Translator.CalendarEventDetails_JoinOnline,
                        HeadlessNotificationPayloadBuilder.Arguments(
                            (Constants.ToastCalendarActionKey, Constants.ToastCalendarJoinOnlineAction),
                            (Constants.ToastCalendarItemIdKey, calendarItem.Id.ToString()),
                            (Constants.ToastModeKey, Constants.ToastModeCalendar))
                    ));
            }

            buttons.Add(
                (
                    Translator.Buttons_Dismiss,
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastDismissActionKey, bool.TrueString))
                ));

            var payload = HeadlessNotificationPayloadBuilder.Build(
                [
                    calendarItem.Title,
                    $"{GetCalendarReminderContext(localStart, DateTime.Now)} - {localStart:g}",
                    calendarItem.Location
                ],
                arguments,
                buttons,
                scenario: "reminder",
                audioEvent: HeadlessNotificationPayloadBuilder.GetAudioEvent(
                    preferencesService.CalendarNotificationSoundEvent),
                selection: selection);

            await ShowAsync(
                NotificationHostApplication.Calendar,
                payload,
                $"calendar-reminder-{calendarItem.Id:N}-{duration}",
                accountPreferences).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background calendar reminder notification failed for event {CalendarItemId}", calendarItem.Id);
        }
    }

    private async Task ShowAttentionSafeAsync(MailAccount account)
    {
        try
        {
            if (account?.Preferences?.IsNotificationsEnabled != true)
                return;

            var arguments = HeadlessNotificationPayloadBuilder.Arguments(
                (Constants.ToastMailAccountIdKey, account.Id.ToString()),
                (Constants.ToastModeKey, Constants.ToastModeMail));

            var buttons = new[]
            {
                (
                    Translator.Buttons_FixAccount,
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastMailAccountIdKey, account.Id.ToString()),
                        (Constants.ToastModeKey, Constants.ToastModeMail))
                ),
                (
                    Translator.Buttons_Dismiss,
                    HeadlessNotificationPayloadBuilder.Arguments(
                        (Constants.ToastDismissActionKey, bool.TrueString))
                )
            };

            var payload = HeadlessNotificationPayloadBuilder.Build(
                [
                    Translator.Exception_AccountNeedsAttention_Title,
                    string.Format(Translator.Exception_AccountNeedsAttention_Message, account.Name)
                ],
                arguments,
                buttons,
                audioEvent: HeadlessNotificationPayloadBuilder.GetAudioEvent(NotificationSoundEvent.Default));

            await ShowAsync(
                NotificationHostApplication.Mail,
                payload,
                kindOverride: NotificationKind.Other).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Background account-attention notification failed for account {AccountId}", account?.Id);
        }
    }

    private async Task ShowAsync(
        NotificationHostApplication application,
        string payload,
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

        await notificationHostClient
            .ShowAsync(application, payload, tag)
            .ConfigureAwait(false);
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

    private static void UpdateBadge(string applicationId, int? count)
    {
        var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication(
            $"{Package.Current.Id.FamilyName}!{applicationId}");

        if (!count.HasValue || count.Value <= 0)
        {
            updater.Clear();
            return;
        }

        var document = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
        if (document.SelectSingleNode("/badge") is not XmlElement badgeElement)
        {
            updater.Clear();
            return;
        }

        badgeElement.SetAttribute("value", count.Value.ToString());
        updater.Update(new BadgeNotification(document));
    }

    private static bool IsWithinNotificationScope(MailCopy mail, MailNotificationScope scope)
    {
        if (scope == MailNotificationScope.AllFolders)
            return true;

        var isInbox = mail.AssignedFolder?.SpecialFolderType == SpecialFolderType.Inbox;

        return scope switch
        {
            MailNotificationScope.InboxOnly => isInbox,
            MailNotificationScope.FocusedInboxOnly => isInbox && mail.IsFocused,
            MailNotificationScope.InboxAndCustomFolders => isInbox || mail.AssignedFolder?.SpecialFolderType == SpecialFolderType.Other,
            _ => true
        };
    }

    private (MailOperation FirstAction, MailOperation SecondAction) GetConfiguredMailNotificationActions()
    {
        var first = SupportedMailNotificationActions.Contains(preferencesService.FirstMailNotificationAction)
            ? preferencesService.FirstMailNotificationAction
            : MailOperation.MarkAsRead;
        var second = SupportedMailNotificationActions.Contains(preferencesService.SecondMailNotificationAction)
            ? preferencesService.SecondMailNotificationAction
            : MailOperation.SoftDelete;

        if (second == first)
            second = SupportedMailNotificationActions.First(action => action != first);

        return (first, second);
    }

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
                return string.Format(Translator.CalendarReminder_StartsInHours, Math.Max(1, (int)Math.Floor(delta.TotalHours)));

            return string.Format(Translator.CalendarReminder_StartsInMinutes, Math.Max(1, (int)Math.Floor(delta.TotalMinutes)));
        }

        if (absoluteDelta.TotalHours >= 1)
            return string.Format(Translator.CalendarReminder_StartedHoursAgo, Math.Max(1, (int)Math.Floor(absoluteDelta.TotalHours)));

        return string.Format(
            Translator.CalendarReminder_StartedMinutesAgo,
            Math.Max(1, (int)Math.Floor(absoluteDelta.TotalMinutes)));
    }
}
