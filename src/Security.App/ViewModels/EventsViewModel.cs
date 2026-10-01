using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Security.App.Mvvm;
using Security.App.Services;
using Security.App.Views;
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

/// <summary>One row in the result filter (All results + every recorded outcome).</summary>
public sealed class EventResultOption
{
    public required string Label { get; init; }

    public SecurityEventResult? Value { get; init; }

    public override string ToString() => Label;
}

/// <summary>One row in the session filter (All sessions + every observed state).</summary>
public sealed class SessionFilterOption
{
    public required string Label { get; init; }

    public SessionState? Value { get; init; }

    /// <summary>True for events recorded before session tracking (SessionState null).</summary>
    public bool MatchUnset { get; init; }

    public override string ToString() => Label;
}

/// <summary>
/// Event log browser: filter by type, date range, result, session state and
/// description text, live tail of new events, an event-details dialog, and a
/// confirmed clear action.
/// </summary>
public sealed class EventsViewModel : ViewModelBase
{
    private const int MaxRows = 500;

    private readonly ISecurityEventService _events;
    private readonly IUserDialogService _dialogs;
    private readonly ILogger<EventsViewModel>? _logger;

    private EventTypeOption _selectedType;
    private EventResultOption _selectedResult;
    private SessionFilterOption _selectedSession;
    private DateTime? _fromDate;
    private DateTime? _toDate;
    private string _searchText = string.Empty;
    private string _summary = "0 events";
    private SecurityEvent? _selectedEvent;

    /// <summary>Rows as returned by the type/date query; result/session/text filters are applied on top.</summary>
    private List<SecurityEvent> _rows = new();

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

        ResultFilters = BuildResultFilters();
        _selectedResult = ResultFilters[0];

        SessionFilters = BuildSessionFilters();
        _selectedSession = SessionFilters[0];

        Events = new ObservableCollection<SecurityEvent>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ClearCommand = new AsyncRelayCommand(ClearAsync);
        ShowDetailsCommand = new RelayCommand(
            () => ShowDetails(SelectedEvent),
            () => SelectedEvent is not null);

        _events.EventRecorded += OnEventRecorded;
    }

    #region Bindings

    public ObservableCollection<SecurityEvent> Events { get; }

    public IReadOnlyList<EventTypeOption> TypeFilters { get; }

    public IReadOnlyList<EventResultOption> ResultFilters { get; }

    public IReadOnlyList<SessionFilterOption> SessionFilters { get; }

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

    /// <summary>Result (outcome) filter. "All results" = null.</summary>
    public EventResultOption SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetProperty(ref _selectedResult, value))
                return;

            ApplyFilters();
        }
    }

    /// <summary>Windows session filter. "All sessions" = null.</summary>
    public SessionFilterOption SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!SetProperty(ref _selectedSession, value))
                return;

            ApplyFilters();
        }
    }

    /// <summary>Case-insensitive search over the description column.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? string.Empty))
                return;

            ApplyFilters();
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

    /// <summary>Event currently selected in the table (details dialog source).</summary>
    public SecurityEvent? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (!SetProperty(ref _selectedEvent, value))
                return;

            (ShowDetailsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public bool HasFilters =>
        SelectedType?.Value is not null ||
        SelectedResult?.Value is not null ||
        SelectedSession is { Value: not null } ||
        SelectedSession is { MatchUnset: true } ||
        !string.IsNullOrWhiteSpace(SearchText) ||
        FromDate is not null ||
        ToDate is not null;

    public ICommand RefreshCommand { get; }

    public ICommand ClearCommand { get; }

    public ICommand ShowDetailsCommand { get; }

    #endregion

    public override Task OnNavigatedAsync() => RefreshAsync();

    private static IReadOnlyList<EventTypeOption> BuildTypeFilters()
    {
        var options = new List<EventTypeOption> { new() { Label = "All types", Value = null } };

        foreach (SecurityEventType type in Enum.GetValues<SecurityEventType>())
            options.Add(new EventTypeOption { Label = SplitPascal(type.ToString()), Value = type });

        return options;
    }

    private static IReadOnlyList<EventResultOption> BuildResultFilters()
    {
        var options = new List<EventResultOption> { new() { Label = "All results", Value = null } };

        foreach (SecurityEventResult result in Enum.GetValues<SecurityEventResult>())
            options.Add(new EventResultOption { Label = SplitPascal(result.ToString()), Value = result });

        return options;
    }

    private static IReadOnlyList<SessionFilterOption> BuildSessionFilters()
    {
        var options = new List<SessionFilterOption> { new() { Label = "All sessions", Value = null } };

        // "Not recorded" matches events written before session tracking (null).
        options.Add(new SessionFilterOption { Label = "Not recorded", Value = null, MatchUnset = true });

        foreach (SessionState state in Enum.GetValues<SessionState>())
            options.Add(new SessionFilterOption { Label = state.ToDisplayText(), Value = state });

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

            _rows = new List<SecurityEvent>(rows);
            ApplyFilters();
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

    /// <summary>
    /// Project <see cref="_rows"/> through the result / session / description
    /// filters. Those three are evaluated here rather than in SQL because the
    /// type + date query already bounds the row set to <see cref="MaxRows"/>.
    /// </summary>
    private void ApplyFilters()
    {
        Events.Clear();

        foreach (var row in _rows)
        {
            if (Matches(row))
                Events.Add(row);
        }

        SelectedEvent = null;
        Summary = BuildSummary(Events.Count);
        OnPropertyChanged(nameof(HasFilters));
    }

    private bool Matches(SecurityEvent e)
    {
        if (SelectedResult?.Value is { } result && e.Result != result)
            return false;

        if (SelectedSession is { MatchUnset: true })
        {
            if (e.SessionState is not null)
                return false;
        }
        else if (SelectedSession?.Value is { } session && e.SessionState != session)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(SearchText) &&
            !e.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    /// <summary>Open the details dialog for the given event (no-op when null).</summary>
    private void ShowDetails(SecurityEvent? evt)
    {
        if (evt is null)
            return;

        try
        {
            var window = new EventDetailsWindow(evt);
            if (System.Windows.Application.Current?.MainWindow is { } owner &&
                owner.IsVisible && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
            }

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Opening event details failed");
            ReportError("Could not open the event details window.", ex);
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
            _rows.Clear();
            Events.Clear();
            SelectedEvent = null;
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
            // Respect active type/date filters rather than blindly tailing.
            if (SelectedType?.Value is { } type && type != e.EventType)
                return;

            if (FromDate is { } from && e.Timestamp < from.Date.ToUniversalTime())
                return;

            if (ToDate is { } to && e.Timestamp > to.Date.AddDays(1).AddTicks(-1).ToUniversalTime())
                return;

            _rows.Insert(0, e);

            while (_rows.Count > MaxRows)
                _rows.RemoveAt(_rows.Count - 1);

            ApplyFilters();
        }));
    }
}
