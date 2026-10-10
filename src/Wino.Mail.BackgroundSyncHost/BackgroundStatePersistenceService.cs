using System;
using System.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.BackgroundSyncHost;

internal sealed partial class BackgroundStatePersistenceService : IStatePersistanceService
{
    public event EventHandler<string>? StatePropertyChanged
    {
        add { }
        remove { }
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add { }
        remove { }
    }
    public bool IsReadingMail { get; set; }
    public string CoreWindowTitle { get; set; } = string.Empty;
    public string AppModeTitle { get; set; } = "Wino Mail";
    public bool IsReaderNarrowed { get; set; }
    public WinoApplicationMode ApplicationMode { get; set; } = WinoApplicationMode.Mail;
    public bool IsEventDetailsVisible { get; set; }
    public double OpenPaneLength { get; set; }
    public bool ShouldShiftMailRenderingDesign { get; set; }
    public double MailListPaneLength { get; set; }
    public CalendarDisplayType CalendarDisplayType { get; set; } = CalendarDisplayType.Week;
    public int DayDisplayCount { get; set; } = 1;
}