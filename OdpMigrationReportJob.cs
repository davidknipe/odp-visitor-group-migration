#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using EPiServer.Personalization.VisitorGroups;
using EPiServer.Scheduler;
#if !CMS13
using EPiServer.PlugIn;
#endif

namespace OdpVisitorGroupMigration;

/// <summary>
/// Step 1 (read only): lists every visitor group using UNRVLD.ODP.VisitorGroups criteria, says what will happen
/// to each criterion, and finds every place in content those groups are used. Changes nothing apart from
/// saving the two CSV reports. Safe to run as many times as you like.
/// </summary>
#if CMS13
[ScheduledJob(
#else
[ScheduledPlugIn(
#endif
    DisplayName = "ODP migration 1 - Report (read only)",
    Description = "Reports visitor groups using UNRVLD.ODP.VisitorGroups criteria and where they are used. Changes nothing.",
    GUID = "f64600cc-9eac-482d-a0f8-9a323fd7ce89")]
public class OdpMigrationReportJob : ScheduledJobBase
{
    private readonly IVisitorGroupRepository _visitorGroupRepository;
    private readonly OdpCriterionMapper _mapper;
    private readonly VisitorGroupUsageScanner _scanner;
    private readonly MigrationReportWriter _reportWriter;
    private readonly OdpMigrationOptions _options;
    private volatile bool _stopRequested;

    public OdpMigrationReportJob(
        IVisitorGroupRepository visitorGroupRepository,
        OdpCriterionMapper mapper,
        VisitorGroupUsageScanner scanner,
        MigrationReportWriter reportWriter,
        OdpMigrationOptions options)
    {
        _visitorGroupRepository = visitorGroupRepository;
        _mapper = mapper;
        _scanner = scanner;
        _reportWriter = reportWriter;
        _options = options;
        IsStoppable = true;
    }

    public override void Stop() => _stopRequested = true;

    public override string Execute()
    {
        _stopRequested = false;
        _reportWriter.EnsureCanWrite();

        OnStatusChanged("Analysing visitor groups...");
        var plans = _mapper.Plan(_visitorGroupRepository.List());
        if (plans.Count == 0)
        {
            return "No visitor groups use UNRVLD.ODP.VisitorGroups criteria. Nothing to migrate.";
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var criteriaReport = _reportWriter.Write($"odp-migration-criteria-{stamp}.csv", CriteriaRows(plans));

        OnStatusChanged("Scanning content for visitor group usage...");
        var usage = _scanner.Scan(plans.Select(p => p.Group).ToList(), _options.IncludePreviousVersions,
            () => _stopRequested, OnStatusChanged);
        var usageReport = _reportWriter.Write($"odp-migration-usage-{stamp}.csv", UsageRows(usage, plans));

        var automatic = plans.Sum(p => p.Criteria.Count(c => c.Outcome == MappingOutcome.Migrate));
        var manual = plans.Sum(p => p.Criteria.Count(c => c.Outcome != MappingOutcome.Migrate));
        var blocking = plans.Count(p => p.BlocksUninstall);

        return $"{plans.Count} visitor group(s) use UNRVLD criteria. "
               + $"{automatic} criteria can be swapped automatically and {manual} need manual work "
               + $"({blocking} group(s) will still block uninstalling UNRVLD after the swap). "
               + $"{usage.Count} content usage(s) found{(_stopRequested ? " (stopped early, so the usage report is incomplete)" : string.Empty)}. "
               + $"Reports: {criteriaReport} and {usageReport}.";
    }

    private static IEnumerable<IEnumerable<string?>> CriteriaRows(IEnumerable<GroupPlan> plans)
    {
        yield return new[]
        {
            "VisitorGroupId", "VisitorGroupName", "CriterionId", "Criterion", "CurrentValue",
            "Outcome", "Detail", "OdpInstance", "AudienceId", "GroupBlocksUninstall"
        };

        foreach (var plan in plans)
        {
            foreach (var c in plan.Criteria)
            {
                yield return new[]
                {
                    plan.Group.Id.ToString(), plan.Group.Name, c.CriterionId.ToString(), c.CriterionName, c.CurrentValue,
                    c.Outcome.ToString(), c.Detail, c.OdpInstanceName, c.AudienceId, plan.BlocksUninstall ? "Yes" : "No"
                };
            }
        }
    }

    private static IEnumerable<IEnumerable<string?>> UsageRows(IEnumerable<UsageRow> usage, IEnumerable<GroupPlan> plans)
    {
        var fullyMigratable = plans.ToDictionary(p => p.Group.Id, p => !p.BlocksUninstall);

        yield return new[]
        {
            "VisitorGroupId", "VisitorGroupName", "ContentId", "WorkId", "Language", "VersionStatus",
            "ContentName", "Location", "UsageType", "VisitorGroupFullyMigratable"
        };

        foreach (var row in usage.OrderBy(u => u.VisitorGroupName).ThenBy(u => u.ContentId).ThenBy(u => u.WorkId))
        {
            yield return new[]
            {
                row.VisitorGroupId.ToString(), row.VisitorGroupName, row.ContentId.ToString(), row.WorkId.ToString(),
                row.Language, row.VersionStatus, row.ContentName, row.Location, row.UsageType,
                fullyMigratable.TryGetValue(row.VisitorGroupId, out var ok) && ok ? "Yes" : "No"
            };
        }
    }
}
