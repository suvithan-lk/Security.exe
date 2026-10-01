namespace Security.Core.Enums;

/// <summary>
/// Outcome attached to a security event.
/// </summary>
public enum SecurityEventResult
{
    Success,
    Failure,
    Info,
    Warning,
    Known,
    Unknown,
    Denied,
}
