namespace Security.Core.Interfaces;

/// <summary>Coarse health of one monitored subsystem.</summary>
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unavailable,
}

/// <summary>Subsystem identifiers tracked by <see cref="IHealthMonitor"/>.</summary>
public enum HealthComponent
{
    Database,
    Camera,
    RecognitionEngine,
    BackgroundService,
    Notifications,
}

/// <summary>Snapshot of one component's health.</summary>
public sealed class ComponentHealth
{
    public ComponentHealth(HealthComponent component, HealthStatus status, string detail)
    {
        Component = component;
        Status = status;
        Detail = detail;
    }

    public HealthComponent Component { get; }

    public HealthStatus Status { get; }

    /// <summary>Concrete, actionable text — never "System compromised".</summary>
    public string Detail { get; }
}

/// <summary>
/// Aggregates the live health of the application's subsystems so the
/// dashboard and tray can report facts instead of guesses.
///
/// Implementations must be cheap to read (no I/O on the calling thread) —
/// components publish their status, the monitor only stores the latest value.
/// </summary>
public interface IHealthMonitor
{
    /// <summary>Latest known health per component. Never null; may be empty before the first publish.</summary>
    IReadOnlyList<ComponentHealth> Components { get; }

    /// <summary>Overall status: worst of all components (Unavailable beats Degraded beats Healthy).</summary>
    HealthStatus Overall { get; }

    /// <summary>True while every tracked component is <see cref="HealthStatus.Healthy"/>.</summary>
    bool IsHealthy { get; }

    /// <summary>Raised after any component's status changes (any thread).</summary>
    event EventHandler? HealthChanged;

    /// <summary>Publish (or replace) the status of one component.</summary>
    void Report(HealthComponent component, HealthStatus status, string detail);

    /// <summary>Convenience: mark a component healthy.</summary>
    void ReportHealthy(HealthComponent component, string detail)
        => Report(component, HealthStatus.Healthy, detail);

    /// <summary>Convenience: mark a component degraded.</summary>
    void ReportDegraded(HealthComponent component, string detail)
        => Report(component, HealthStatus.Degraded, detail);

    /// <summary>Convenience: mark a component unavailable.</summary>
    void ReportUnavailable(HealthComponent component, string detail)
        => Report(component, HealthStatus.Unavailable, detail);
}
