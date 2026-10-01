using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Security.Core.Interfaces;
using Security.Core.Models;
using Security.Infrastructure.Security;
using Security.Infrastructure.Services;

namespace Security.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Register DPAPI protection, settings, and security event services.
    /// Call AFTER <c>AddSecurityData</c> (needs <see cref="IApplicationSettingRepository"/>).
    /// </summary>
    public static IServiceCollection AddSecurityInfrastructure(
        this IServiceCollection services,
        AppSettings? appSettings = null,
        RecognitionOptions? recognitionOptions = null)
    {
        services.AddSingleton(appSettings ?? new AppSettings());
        services.AddSingleton(recognitionOptions ?? new RecognitionOptions());

        // Windows DPAPI, scoped to the current user.
        services.AddSingleton<IDataProtectionService, DataProtectionService>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // Windows session observation (SystemEvents + WTS). Reads state only —
        // never hooks winlogon, never touches credentials.
        services.AddSingleton<IWindowsSessionService, WindowsSessionService>();

        // SecurityEventService resolves the session service optionally to stamp
        // SessionState on every recorded event.
        services.AddSingleton<ISecurityEventService, SecurityEventService>();

        services.AddSingleton<IHealthMonitor, HealthMonitor>();

        // Retention sweeps: delete only expired events + expired snapshots.
        // Logs its summary to the app log — never writes new event rows.
        services.AddSingleton<IRetentionService, RetentionService>();

        return services;
    }
}
