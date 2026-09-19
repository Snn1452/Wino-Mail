using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
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

internal sealed class HeadlessNotificationBuilder : INotificationBuilder
{
    private const string IconRoot = "ms-appx:///Assets/NotificationIcons/";
    private readonly IAccountService _accountService;
    private readonly IMailService _mailService;
    private readonly IUnreadBadgeService _unreadBadgeService;
    private readonly IPreferencesService _preferencesService;
    private readonly INotificationPolicyService _notificationPolicyService;
    private readonly BackgroundNotificationHostClient _notificationHostClient;

    public HeadlessNotificationBuilder(IAccountService accountService, IMailService mailService, IUnreadBadgeService unreadBadgeService, IPreferencesService preferencesService, INotificationPolicyService notificationPolicyService, BackgroundNotificationHostClient notificationHostClient)
    {
        _accountService=accountService; _mailService=mailService; _unreadBadgeService=unreadBadgeService; _preferencesService=preferencesService; _notificationPolicyService=notificationPolicyService; _notificationHostClient=notificationHostClient;
    }

    public async Task CreateNotificationsAsync(IEnumerable<MailCopy> downloadedMailItems)
    {
        var accounts=await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var items=new List<(MailCopy,MailAccountPreferences?)>();
        foreach(var downloaded in downloadedMailItems)
        {
            var mail=await _mailService.GetSingleMailItemAsync(downloaded.UniqueId).ConfigureAwait(false);
            var account=accounts.FirstOrDefault(a=>a.Id==mail.AssignedFolder.MailAccountId);
            var settings=NotificationSettingsResolver.ResolveMail(_preferencesService,account?.Preferences);
            var inbox=mail.AssignedFolder?.SpecialFolderType==SpecialFolderType.Inbox;
            var inScope=settings.Scope switch
            {
                MailNotificationScope.AllFolders=>true,
                MailNotificationScope.InboxOnly=>inbox,
                MailNotificationScope.FocusedInboxOnly=>inbox&&mail.IsFocused,
                MailNotificationScope.InboxAndCustomFolders=>inbox||mail.AssignedFolder?.SpecialFolderType==SpecialFolderType.Other,
                _=>true
            };
            if(settings.IsEnabled&&inScope)items.Add((mail,account?.Preferences));
        }
        if(items.Count==0)return;
        if(items.Count>3)
        {
            var b=new AppNotificationBuilder().AddText(Translator.Notifications_MultipleNotificationsTitle).AddText(string.Format(Translator.Notifications_MultipleNotificationsMessage,items.Count)).AddArgument(Constants.ToastModeKey,Constants.ToastModeMail).AddButton(new AppNotificationButton(Translator.Buttons_Dismiss).AddArgument(Constants.ToastDismissActionKey,bool.TrueString));
            await ShowAsync(NotificationHostApplication.Mail,b).ConfigureAwait(false);
        }
        else foreach(var (mail,prefs) in items) await CreateMailAsync(mail,prefs).ConfigureAwait(false);
        await UpdateTaskbarIconBadgeAsync().ConfigureAwait(false);
    }

    private async Task CreateMailAsync(MailCopy mail,MailAccountPreferences? accountPreferences)
    {
        var settings=NotificationSettingsResolver.ResolveMail(_preferencesService,accountPreferences);
        var b=new AppNotificationBuilder().AddText(settings.Content==MailNotificationContent.Nothing?Translator.Notifications_MultipleNotificationsTitle:mail.FromName);
        if(settings.Content!=MailNotificationContent.Nothing&&settings.Content!=MailNotificationContent.SenderOnly)b.AddText(mail.Subject);
        if(settings.Content==MailNotificationContent.SenderSubjectPreview)b.AddText(mail.PreviewText);
        b.AddArgument(Constants.ToastMailUniqueIdKey,mail.UniqueId.ToString()).AddArgument(Constants.ToastActionKey,MailOperation.Navigate.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeMail);
        var first=_preferencesService.FirstMailNotificationAction;
        var second=_preferencesService.SecondMailNotificationAction;
        b.AddButton(new AppNotificationButton(GetOperationString(first)).AddArgument(Constants.ToastMailUniqueIdKey,mail.UniqueId.ToString()).AddArgument(Constants.ToastActionKey,first.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeMail));
        b.AddButton(new AppNotificationButton(GetOperationString(second)).AddArgument(Constants.ToastMailUniqueIdKey,mail.UniqueId.ToString()).AddArgument(Constants.ToastActionKey,second.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeMail));
        b.AddButton(new AppNotificationButton(Translator.Buttons_Dismiss).AddArgument(Constants.ToastDismissActionKey,bool.TrueString));
        b.SetAudioEvent((AppNotificationSoundEvent)settings.Sound);
        await ShowAsync(NotificationHostApplication.Mail,b,mail.UniqueId.ToString(),accountPreferences).ConfigureAwait(false);
    }

    public Task UpdateTaskbarIconBadgeAsync()=>UpdateBadgeAsync();
    public Task AddCalendarTaskbarBadgeCountAsync(int count){if(count>0)UpdateBadge("CalendarApp",count);return Task.CompletedTask;}
    public Task ClearCalendarTaskbarBadgeAsync()=>Task.CompletedTask;
    public void RemoveNotification(Guid id)=>_ = _notificationHostClient.ShowAsync(NotificationHostApplication.Mail,new AppNotificationBuilder().BuildNotification());
    public void CreateAttentionRequiredNotification(MailAccount account)=>_ = Task.Run(()=>ShowAttentionAsync(account));
    public void CreateWebView2RuntimeMissingNotification(){}
    public Task CreateTestNotificationsAsync(IEnumerable<MailCopy> mailItems)=>Task.CompletedTask;
    public Task CreateTestCalendarReminderNotificationAsync(CalendarItem item)=>Task.CompletedTask;
    public Task CreateTestPeopleNotificationAsync(AccountContact contact)=>Task.CompletedTask;
    public Task CreateTestTaskReminderNotificationAsync(AccountTask task)=>Task.CompletedTask;
    public Task UpdateJumpListOptionsAsync()=>Task.CompletedTask;
    public async Task CreateCalendarReminderNotificationAsync(CalendarItem item,long duration)
    {
        var prefs=item?.AssignedCalendar?.AccountId is Guid id?(await _accountService.GetAccountAsync(id).ConfigureAwait(false))?.Preferences:null;
        var b=new AppNotificationBuilder().SetScenario(AppNotificationScenario.Reminder).AddText(item.Title).AddText($"{item.GetLocalStartDate():g}");
        b.AddArgument(Constants.ToastCalendarActionKey,Constants.ToastCalendarNavigateAction).AddArgument(Constants.ToastCalendarItemIdKey,item.Id.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeCalendar);
        b.AddButton(new AppNotificationButton(Translator.Buttons_Open).AddArgument(Constants.ToastCalendarActionKey,Constants.ToastCalendarNavigateAction).AddArgument(Constants.ToastCalendarItemIdKey,item.Id.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeCalendar));
        b.AddButton(new AppNotificationButton(Translator.Buttons_Dismiss).AddArgument(Constants.ToastDismissActionKey,bool.TrueString));
        await ShowAsync(NotificationHostApplication.Calendar,b,$"calendar-reminder-{item.Id:N}-{duration}",prefs).ConfigureAwait(false);
    }
    private async Task UpdateBadgeAsync()
    {
        var s=await _unreadBadgeService.GetSnapshotAsync().ConfigureAwait(false);
        UpdateBadge("App",s.TaskbarUnreadCount>0?s.TaskbarUnreadCount:null);
    }
    private async Task ShowAttentionAsync(MailAccount a)
    {
        if(a?.Preferences?.IsNotificationsEnabled!=true)return;
        var b=new AppNotificationBuilder().AddText(Translator.Exception_AccountNeedsAttention_Title).AddText(string.Format(Translator.Exception_AccountNeedsAttention_Message,a.Name)).AddArgument(Constants.ToastMailAccountIdKey,a.Id.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeMail).AddButton(new AppNotificationButton(Translator.Buttons_FixAccount).AddArgument(Constants.ToastMailAccountIdKey,a.Id.ToString()).AddArgument(Constants.ToastModeKey,Constants.ToastModeMail));
        await ShowAsync(NotificationHostApplication.Mail,b,kindOverride:NotificationKind.Other);
    }
    private async Task ShowAsync(NotificationHostApplication app,AppNotificationBuilder builder,string tag=null,MailAccountPreferences prefs=null,NotificationKind? kindOverride=null)
    {
        var decision=_notificationPolicyService.Evaluate(kindOverride??(app==NotificationHostApplication.Mail?NotificationKind.Mail:NotificationKind.CalendarReminder),prefs,DateTimeOffset.Now);
        if(!decision.ShouldDeliver)return;
        var n=builder.BuildNotification(); if(!string.IsNullOrWhiteSpace(tag))n.Tag=tag;
        await _notificationHostClient.ShowAsync(app,n).ConfigureAwait(false);
    }
    private static string GetOperationString(MailOperation op)=>op switch
    {
        MailOperation.Archive=>Translator.MailOperation_Archive,MailOperation.SoftDelete=>Translator.MailOperation_Delete,MailOperation.MoveToJunk=>Translator.MailOperation_MarkAsJunk,MailOperation.MarkAsRead=>Translator.MailOperation_MarkAsRead,MailOperation.Reply=>Translator.MailOperation_Reply,MailOperation.ReplyAll=>Translator.MailOperation_ReplyAll,MailOperation.Forward=>Translator.MailOperation_Forward,_=>op.ToString()
    };
    private static void UpdateBadge(string appId,int? count)
    {
        var updater=BadgeUpdateManager.CreateBadgeUpdaterForApplication($"{Package.Current.Id.FamilyName}!{appId}");
        if(!count.HasValue||count<=0){updater.Clear();return;}
        var doc=BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
        if(doc.SelectSingleNode("/badge") is not XmlElement el){updater.Clear();return;}
        el.SetAttribute("value",count.Value.ToString());updater.Update(new BadgeNotification(doc));
    }
}