using Microsoft.Extensions.Logging;
using Security.Core.Entities;
using Security.Core.Enums;
using Security.Core.Interfaces;

namespace Security.Infrastructure.Services;

/// <summary>
/// Persists security events and mirrors them to the structured log.
///
/// Guarantees:
///  - never throws (an event failure must not break the caller);
///  - never accepts or records biometric payloads — descriptions are plain text;
///  - confidence values are clamped to 0..1.
/// </summary>
public sealed class SecurityEventService : ISecurityEventService
{
    private readonly ISecurityEventRepository _repository;
    private readonly ISettingsService? _settings;
    private readonly ILogger<SecurityEventService>? _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SecurityEventService(
        ISecurityEventRepository repository,
        ISettingsService? settings = null,
        ILogger<SecurityEventService>? logger = null)
    {
        _repository = repository;
        _settings = settings;
        _logger = logger;
    }

    public event EventHandler<SecurityEvent>? EventRecorded;

    public async Task<SecurityEvent> RecordAsync(
        SecurityEventType eventType,
        SecurityEventResult result,
        string description,
        double? confidence = null,
        CancellationToken cancellationToken = default)
    {
        var securityEvent = Core.Services.SecurityEventFactory.Create(
            eventType, result, description, confidence);

        var storeEvents = _settings?.Current.StoreSecurityEvents ?? true;

        if (storeEvents)
        {
            try
            {
                await _writeLock.WaitAsync(cancellationToken);
                try
                {
                    await _repository.AddAsync(securityEvent, cancellationToken);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown — ignore.
            }
            catch (Exception ex)
            {
                // Never crash on logging. Technical detail goes to the app log only.
                _logger?.LogError(ex, "Failed to persist security event {EventType}", eventType);
            }
        }

        var level = result switch
        {
            SecurityEventResult.Failure => LogLevel.Warning,
            SecurityEventResult.Unknown => LogLevel.Warning,
            SecurityEventResult.Denied => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        // Structured log line. Contains no biometric data by construction.
        _logger?.Log(level,
            "SecurityEvent {EventType} {Result} Confidence={Confidence} {Description}",
            eventType, result, confidence, description);

        try
        {
            EventRecorded?.Invoke(this, securityEvent);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "An event subscriber threw while handling {EventType}", eventType);
        }

        return securityEvent;
    }

    public async Task<IReadOnlyList<SecurityEvent>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.GetRecentAsync(count, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read recent security events");
            return Array.Empty<SecurityEvent>();
        }
    }

    public async Task<IReadOnlyList<SecurityEvent>> QueryAsync(
        SecurityEventType? type = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.QueryAsync(
                new SecurityEventTypeFilter
                {
                    EventType = type,
                    FromUtc = fromUtc,
                    ToUtc = toUtc,
                },
                limit,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to query security events");
            return Array.Empty<SecurityEvent>();
        }
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _repository.CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to count security events");
            return 0;
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                await _repository.ClearAsync(cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }

            _logger?.LogInformation("Security events cleared by user");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear security events");
            throw;
        }
    }
}
