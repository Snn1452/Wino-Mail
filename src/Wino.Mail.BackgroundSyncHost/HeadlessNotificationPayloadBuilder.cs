using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Xml.Linq;

namespace Wino.Mail.BackgroundSyncHost;

internal static class HeadlessNotificationPayloadBuilder
{
    public static string Build(
        IEnumerable<string> texts,
        IEnumerable<KeyValuePair<string, string>>? arguments = null,
        IEnumerable<(string Content, IReadOnlyDictionary<string, string> Arguments)>? buttons = null,
        string? scenario = null,
        string? audioEvent = null,
        (string Id, string DefaultInput, IReadOnlyDictionary<string, string> Options)? selection = null)
    {
        var root = new XElement("toast");
        var argumentPairs = arguments?
            .Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}")
            .ToArray();

        if (argumentPairs is { Length: > 0 })
            root.SetAttributeValue("launch", string.Join(";", argumentPairs));

        if (!string.IsNullOrWhiteSpace(scenario))
            root.SetAttributeValue("scenario", scenario);

        var binding = new XElement(
            "binding",
            new XAttribute("template", "ToastGeneric"),
            texts
                .Where(static text => text != null)
                .Take(3)
                .Select(static text => new XElement("text", text)));

        root.Add(new XElement("visual", binding));

        var buttonList = buttons?.ToList();
        if (selection.HasValue || buttonList is { Count: > 0 })
        {
            var actions = new XElement("actions");

            if (selection.HasValue)
            {
                var input = new XElement(
                    "input",
                    new XAttribute("id", selection.Value.Id),
                    new XAttribute("type", "selection"),
                    new XAttribute("defaultInput", selection.Value.DefaultInput));

                foreach (var option in selection.Value.Options)
                {
                    input.Add(
                        new XElement(
                            "selection",
                            new XAttribute("id", option.Key),
                            new XAttribute("content", option.Value)));
                }

                actions.Add(input);
            }

            if (buttonList is { Count: > 0 })
            {
                foreach (var button in buttonList.Take(5))
                {
                    var buttonPairs = button.Arguments
                        .Select(pair => $"{WebUtility.UrlEncode(pair.Key)}={WebUtility.UrlEncode(pair.Value)}");

                    var action = new XElement(
                        "action",
                        new XAttribute("content", button.Content ?? string.Empty),
                        new XAttribute("arguments", string.Join(";", buttonPairs)));

                    actions.Add(action);
                }
            }

            root.Add(actions);
        }

        if (!string.IsNullOrWhiteSpace(audioEvent))
            root.Add(new XElement("audio", new XAttribute("src", $"ms-winsoundevent:Notification.{audioEvent}")));

        return root.ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyDictionary<string, string> Arguments(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    public static string GetAudioEvent(Wino.Core.Domain.Enums.NotificationSoundEvent sound)
        => sound switch
        {
            Wino.Core.Domain.Enums.NotificationSoundEvent.IM => "IM",
            Wino.Core.Domain.Enums.NotificationSoundEvent.Mail => "Mail",
            Wino.Core.Domain.Enums.NotificationSoundEvent.Reminder => "Reminder",
            Wino.Core.Domain.Enums.NotificationSoundEvent.SMS => "SMS",
            _ => "Default"
        };
}
