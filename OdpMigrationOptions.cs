#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace OdpVisitorGroupMigration;

/// <summary>
/// Settings for the one-off migration, read from the "OdpMigration" section of appsettings.json.
/// </summary>
public sealed class OdpMigrationOptions
{
    public const string SectionName = "OdpMigration";

    private static readonly string[] DefaultReportReadRoles = { "CmsAdmins", "Administrators", "WebAdmins" };

    /// <summary>
    /// Maps an UNRVLD endpoint Name (EPiServer:OdpVisitorGroupOptions:OdpEndpoints[].Name) to an
    /// Optimizely.Cms.Odp instance name (a key under Optimizely:Odp:Instances).
    /// Only needed when the names differ and there is more than one instance.
    /// </summary>
    public Dictionary<string, string> InstanceMap { get; set; } = new();

    /// <summary>
    /// Instance used for unprefixed segment values when it cannot be worked out automatically
    /// (e.g. several official instances, or no UNRVLD endpoints left in configuration).
    /// </summary>
    public string? DefaultInstance { get; set; }

    /// <summary>Also scan previously published versions in the usage report. Off by default as it can be slow on large sites.</summary>
    public bool IncludePreviousVersions { get; set; }

    /// <summary>Folder created under "For All Sites" to hold the CSV reports.</summary>
    public string ReportFolderName { get; set; } = "ODP migration reports";

    /// <summary>Roles allowed to read the report folder. Everyone else (including anonymous visitors) is denied.</summary>
    public string[]? ReportReadRoles { get; set; }

    // Arrays bound from configuration append to defaults, so the defaults are applied here instead.
    public IReadOnlyList<string> EffectiveReportReadRoles =>
        ReportReadRoles is { Length: > 0 } ? ReportReadRoles : DefaultReportReadRoles;
}

public static class OdpMigrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the migration services. Remove this call (and the migration code) once the migration is complete.
    /// </summary>
    public static IServiceCollection AddOdpVisitorGroupMigration(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(OdpMigrationOptions.SectionName).Get<OdpMigrationOptions>()
                      ?? new OdpMigrationOptions();

        services.AddSingleton(options);
        services.AddTransient<OdpCriterionMapper>();
        services.AddTransient<VisitorGroupUsageScanner>();
        services.AddTransient<MigrationReportWriter>();
        services.AddTransient<OdpMigrationBackupStore>();
        return services;
    }
}
