using System.Collections.Concurrent;
using Security.Core.Interfaces;

namespace Security.Infrastructure.Services;

/// <summary>
/// In-memory health registry. Components publish their latest status; the
/// dashboard and tray read it.
///
/// Design notes:
///  - reads are lock-free snapshots (never I/O) so binding to it is cheap;
///  - <see cref="HealthChanged"/> fires only when a status actually changes,
///    so a 5 s refresh loop cannot generate binding churn;
///  - a component that has never reported is treated as
///    <see cref="HealthStatus.Degraded"/> ("Not checked yet") rather than
///    silently healthy — no invented green states.
/// </summary>
public sealed class HealthMonitor : IHealthMonitor
{
    private readonly ConcurrentDictionary<HealthComponent, ComponentHealth> _components = new();

    public event EventHandler? HealthChanged;

    public IReadOnlyList<ComponentHealth> Components
        => _components.Values.OrderBy(c => c.Component).ToList();

    public HealthStatus Overall
    {
        get
        {
            var worst = HealthStatus.Healthy;

            // Unassigned components count as degraded: an unmonitored
            // subsystem is not proof of health.
            var missing = Enum.GetValues<HealthComponent>().Length - _components.Count;
            if (missing > 0)
                worst = HealthStatus.Degraded;

            foreach (var component in _components.Values)
            {
                if (component.Status == HealthStatus.Unavailable)
                    return HealthStatus.Unavailable;

                if (component.Status == HealthStatus.Degraded)
                    worst = HealthStatus.Degraded;
            }

            return worst;
        }
    }

    public bool IsHealthy => Overall == HealthStatus.Healthy;

    public void Report(HealthComponent component, HealthStatus status, string detail)
    {
        var next = new ComponentHealth(component, status, string.IsNullOrWhiteSpace(detail) ? string.Empty : detail.Trim());

        if (_components.TryGetValue(component, out var existing) &&
            existing.Status == status &&
            string.Equals(existing.Detail, next.Detail, StringComparison.Ordinal))
        {
            return; // No change → no event.
        }

        _components[component] = next;

        try
        {
            HealthChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A UI subscriber throwing must not break the monitor.
        }
    }
}
