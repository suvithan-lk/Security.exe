using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;

namespace Security.App.ViewModels;

/// <summary>One row in the type filter, including an "All types" option.</summary>
public sealed class EventTypeOption
{
    public required string Label { get; init; }

    public SecurityEventType? Value { get; init; }

    public override string ToString() => Label;
}

/// <summary>
/// Event log browser: filter by type and date range, live tail of new events,
/// and a confirmed clear action.
/// </summary>
public sealed class EventsViewModel : ViewModelBase
{
    private const int MaxRows = 500;

    private readonly ISecurityEventService _events;
    private readonly IUserDialogService _dialogs;
    private readonly ILogger<EventsViewModel>? _logger;

    private EventTypeOption _selectedType;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private string _summary = "0 events";

    public EventsViewModel(
        ISecurityEventService events,
        IUserDialogService dialogs,
        ILogger<EventsViewModel>? logger = null)
    {
        _events = events;
        _dialogs = dialogs;
        _logger = logger;

        TypeFilters = BuildTypeFilters();
        _selectedType = TypeFilters[0];

        Events = new ObservableCollection<SecurityEvent>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ClearCommand = new AsyncRelayCommand(ClearAsync);

        _events.EventRecorded += OnEventRecorded;
    }

    #region Bindings

    public ObservableCollection<SecurityEvent> Events { get; }

    public IReadOnlyList<EventTypeOption> TypeFilters { get; }

    public EventTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value))
                return;

            _ = RefreshAsync();
        }
    }

    /// <summary>Inclusive start date (local). Null = no lower bound.</summary>
    public DateTime? FromDate
    {
        get => _fromDate;
        set
        {
            if (!SetProperty(ref _fromDate, value))
                return;

            _ = RefreshAsync();
        }
    }

    /// <summary>Inclusive end date (local). Null = no upper bound.</summary>
    public DateTime? ToDate
    {
        get => _toDate;
        set
        {
            if (!SetProperty(ref _toDate, value))
                return;

            _ = RefreshAsync();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool HasFilters => SelectedType?.Value is not null || FromDate is not null || ToDate is not null;

    public ICommand RefreshCommand { get; }

    public ICommand ClearCommand { get; }

    #endregion

    public override Task OnNavigatedAsync() => RefreshAsync();

    private static IReadOnlyList<EventTypeOption> BuildTypeFilters()
    {
        var options = new List<EventTypeOption> { new() { Label = "All types", Value = null } };

        foreach (SecurityEventType type in Enum.GetValues<SecurityEventType>())
            options.Add(new EventTypeOption { Label = SplitPascal(type.ToString()), Value = type });

        return options;
    }

    /// <summary>"KnownFaceDetected" -> "Known face detected".</summary>
    private static string SplitPascal(string text)
    {
        var chars = new List<char>(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(text[i - 1]))
                chars.Add(' ');

            chars.Add(char.ToLowerInvariant(c));
        }

        if (chars.Count == 0)
            return text;

        chars[0] = char.ToUpperInvariant(chars[0]);
        return new string(chars.ToArray());
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        ClearError();

        try
        {
            // Local calendar day -> UTC bounds so the filter matches what the
            // operator sees next to the localised timestamp column.
            DateTime? fromUtc = FromDate is { } from
                ? from.Date.ToUniversalTime()
                : null;

            DateTime? toUtc = ToDate is { } to
                ? to.Date.AddDays(1).AddTicks(-1).ToUniversalTime()
                : null;

            var rows = await _events.QueryAsync(
                SelectedType?.Value,
                fromUtc,
                toUtc,
                MaxRows);

            Events.Clear();
            foreach (var row in rows)
                Events.Add(row);

            Summary = BuildSummary(rows.Count);
            OnPropertyChanged(nameof(HasFilters));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Event query failed");
            ReportError("Could not load security events. See logs for details.", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string BuildSummary(int count)
    {
        if (count == 0)
            return HasFilters ? "No events match the current filters." : "No security events recorded yet.";

        return $"{count} event{(count == 1 ? "" : "s")}{(HasFilters ? " (filtered)" : string.Empty)}";
    }

    private async Task ClearAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            "Clear event log",
            "Permanently delete ALL recorded security events?\n\n" +
            "This cannot be undone. Face profiles and settings are not affected.");

        if (!confirmed)
            return;

        try
        {
            await _events.ClearAsync();
            Events.Clear();
            Summary = "No security events recorded yet.";
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Clearing events failed");
            ReportError("Could not clear the event log. See logs for details.", ex);
        }
    }

    private void OnEventRecorded(object? sender, SecurityEvent e)
    {
        _ = System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Respect active filters rather than blindly tailing.
            if (SelectedType?.Value is { } type && type != e.EventType)
                return;

            if (FromDate is { } from && e.Timestamp < from.Date.ToUniversalTime())
                return;

            if (ToDate is { } to && e.Timestamp > to.Date.AddDays(1).AddTicks(-1).ToUniversalTime())
                return;

            Events.Insert(0, e);

            while (Events.Count > MaxRows)
                Events.RemoveAt(Events.Count - 1);

            Summary = BuildSummary(Events.Count);
        }));
    }
}
