using FluentAssertions;
using Wino.NotificationHost.Contracts;
using Xunit;

namespace Wino.NotificationHost.Tests;

public sealed class NotificationHostCodecTests
{
    [Fact]
    public void Activation_RoundTripsInputInStableOrder()
    {
        var activation = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            NotificationHostApplication.Calendar,
            "ToastCalendarActionKey=ToastCalendarSnoozeAction",
            new Dictionary<string, string>
            {
                ["snooze"] = "15",
                ["reply"] = "Hello"
            });
        var reversedInput = activation.UserInput
            .Reverse()
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var first = NotificationHostCodec.EncodeActivation(activation);
        var second = NotificationHostCodec.EncodeActivation(activation with { UserInput = reversedInput });

        first.Should().Equal(second);
        NotificationHostCodec.DecodeActivation(first).Should().BeEquivalentTo(activation);
    }

    [Fact]
    public void DecodeActivation_RejectsTruncatedAndTrailingData()
    {
        var activation = new NotificationHostActivation(
            DateTimeOffset.UtcNow,
            NotificationHostApplication.Mail,
            "ToastModeKey=ToastModeMail",
            new Dictionary<string, string>());
        var encoded = NotificationHostCodec.EncodeActivation(activation);

        var truncated = () => NotificationHostCodec.DecodeActivation(encoded[..^1]);
        var withTrailingData = () => NotificationHostCodec.DecodeActivation([.. encoded, 0x7F]);

        truncated.Should().Throw<EndOfStreamException>();
        withTrailingData.Should().Throw<InvalidDataException>();
    }
}
