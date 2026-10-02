namespace Wino.NotificationHost.Contracts;

public static class NotificationHostApplicationIds
{
    public const string Main = "App";
    public const string Mail = "MailNotificationHost";
    public const string Calendar = "CalendarNotificationHost";
    public const string People = "PeopleNotificationHost";
    public const string Tasks = "ToDoNotificationHost";

    public static string GetToastTargetApplicationId(NotificationHostApplication application)
        => application switch
        {
            NotificationHostApplication.Mail => Main,
            NotificationHostApplication.Calendar => "CalendarApp",
            NotificationHostApplication.People => "ContactsApp",
            NotificationHostApplication.Tasks => "ToDoApp",
            _ => throw new ArgumentOutOfRangeException(nameof(application), application, "Unknown notification target.")
        };


    public static string GetApplicationId(NotificationHostApplication application)
        => application switch
        {
            NotificationHostApplication.Mail => Mail,
            NotificationHostApplication.Calendar => Calendar,
            NotificationHostApplication.People => People,
            NotificationHostApplication.Tasks => Tasks,
            _ => throw new ArgumentOutOfRangeException(nameof(application), application, "Unknown notification host application.")
        };

    public static bool TryResolveFromAppUserModelId(string? appUserModelId, out NotificationHostApplication application)
    {
        application = default;

        if (string.IsNullOrWhiteSpace(appUserModelId))
            return false;

        var separatorIndex = appUserModelId.LastIndexOf('!');
        var applicationId = separatorIndex >= 0
            ? appUserModelId[(separatorIndex + 1)..]
            : appUserModelId;

        application = applicationId switch
        {
            Mail => NotificationHostApplication.Mail,
            Calendar => NotificationHostApplication.Calendar,
            People => NotificationHostApplication.People,
            Tasks => NotificationHostApplication.Tasks,
            _ => default
        };

        return applicationId is Mail or Calendar or People or Tasks;
    }
}
