#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using EPiServer.Personalization.VisitorGroups;
using EPiServer.Scheduler;
using EPiServer.Security;
using Microsoft.Extensions.Logging;
#if !CMS13
using EPiServer.PlugIn;
#endif

namespace OdpVisitorGroupMigration;

/// <summary>
/// Step 2: swaps each UNRVLD Real Time Segment criterion for an Optimizely.Cms.Odp Audience Membership criterion
/// inside the existing visitor group. The visitor group keeps its ID, so personalised content and access rights
/// keep working without being edited. Every swap is backed up first so it can be undone with the rollback job.
/// Safe to re-run: criteria that have already been swapped are no longer UNRVLD criteria and are skipped.
/// </summary>
#if CMS13
[ScheduledJob(
#else
[ScheduledPlugIn(
#endif
    DisplayName = "ODP migration 2 - Swap criteria",
    Description = "Replaces UNRVLD Real Time Segment criteria with Optimizely.Cms.Odp Audience Membership in place. Run the report job first.",
    GUID = "125b3cbe-d379-4206-b3ac-9e6dad85ef3b")]
public class OdpMigrationSwapJob : ScheduledJobBase
{
    internal const string JobIdentity = "OdpVisitorGroupMigrationJob";

    private readonly IVisitorGroupRepository _visitorGroupRepository;
    private readonly OdpCriterionMapper _mapper;
    private readonly OdpMigrationBackupStore _backupStore;
    private readonly IPrincipalAccessor _principalAccessor;
    private readonly ILogger<OdpMigrationSwapJob> _logger;
    private volatile bool _stopRequested;

    public OdpMigrationSwapJob(
        IVisitorGroupRepository visitorGroupRepository,
        OdpCriterionMapper mapper,
        OdpMigrationBackupStore backupStore,
        IPrincipalAccessor principalAccessor,
        ILogger<OdpMigrationSwapJob> logger)
    {
        _visitorGroupRepository = visitorGroupRepository;
        _mapper = mapper;
        _backupStore = backupStore;
        _principalAccessor = principalAccessor;
        _logger = logger;
        IsStoppable = true;
    }

    public override void Stop() => _stopRequested = true;

    public override string Execute()
    {
        _stopRequested = false;

        var previousPrincipal = _principalAccessor.Principal;
        var runBy = RunIdentity.Describe(previousPrincipal);
        var runId = Guid.NewGuid();

        // A named identity with no roles, so changes are attributable without granting extra rights.
        _principalAccessor.Principal = new GenericPrincipal(new GenericIdentity(JobIdentity), Array.Empty<string>());

        try
        {
            _logger.LogInformation("ODP migration swap {RunId} started by {RunBy}", runId, runBy);

            var plans = _mapper.Plan(_visitorGroupRepository.List()).Where(p => p.HasWorkToDo).ToList();
            int swapped = 0, failedGroups = 0, processedGroups = 0;

            foreach (var plan in plans)
            {
                if (_stopRequested)
                {
                    break;
                }

                try
                {
                    swapped += Migrate(plan, runId, runBy);
                }
                catch (Exception ex)
                {
                    failedGroups++;
                    _logger.LogError(ex, "ODP migration swap {RunId} failed for visitor group {VisitorGroupId} ({VisitorGroupName})",
                        runId, plan.Group.Id, plan.Group.Name);
                }

                processedGroups++;
                OnStatusChanged($"Processed {processedGroups} of {plans.Count} visitor groups");
            }

            _logger.LogInformation("ODP migration swap {RunId} finished: {Swapped} criteria swapped, {FailedGroups} group(s) failed",
                runId, swapped, failedGroups);

            var stopped = _stopRequested ? " Stopped early: run again to finish." : string.Empty;
            var failed = failedGroups > 0 ? $" {failedGroups} group(s) failed, see the log." : string.Empty;
            return $"Swapped {swapped} criteria across {processedGroups} visitor group(s).{failed}{stopped} "
                   + "Run the report job again to confirm nothing is left.";
        }
        finally
        {
            _principalAccessor.Principal = previousPrincipal;
        }
    }

    private int Migrate(GroupPlan plan, Guid runId, string runBy)
    {
        var original = _visitorGroupRepository.Load(plan.Group.Id)
                       ?? throw new InvalidOperationException("Visitor group no longer exists.");

        // Work on a copy so a failed save cannot leave a half-edited object in the repository cache.
        var updated = original.Copy();
        if (updated.Id != original.Id)
        {
            throw new InvalidOperationException("VisitorGroup.Copy() changed the ID. Aborting so a duplicate group is not created.");
        }

        if (ReferenceEquals(updated.Criteria, original.Criteria))
        {
            throw new InvalidOperationException("VisitorGroup.Copy() shares its criteria list with the cached original. Aborting.");
        }

        var now = DateTime.UtcNow;
        var backups = new List<OdpMigrationBackup>();

        foreach (var mapping in plan.Criteria.Where(c => c.Outcome == MappingOutcome.Migrate))
        {
            // Check the criterion is still where the plan found it, in case an editor changed the group in between.
            if (mapping.Index >= original.Criteria.Count
                || original.Criteria[mapping.Index].Id != mapping.CriterionId
                || !string.Equals(original.Criteria[mapping.Index].TypeName, mapping.TypeName, StringComparison.Ordinal)
                || !string.Equals(updated.Criteria[mapping.Index].TypeName, mapping.TypeName, StringComparison.Ordinal))
            {
                _logger.LogWarning("ODP migration skipped criterion {CriterionId} in visitor group {VisitorGroupId}: the group changed since it was analysed",
                    mapping.CriterionId, original.Id);
                continue;
            }

            var old = original.Criteria[mapping.Index];
            var replacement = OdpCriterionMapper.BuildReplacement(old, mapping);

            backups.Add(new OdpMigrationBackup
            {
                VisitorGroupId = original.Id,
                VisitorGroupName = original.Name,
                OldCriterionId = old.Id,
                OldTypeName = old.TypeName,
                OldAudienceValue = mapping.CurrentValue ?? string.Empty,
                Enabled = old.Enabled,
                Required = old.Required,
                Points = old.Points,
                Notes = old.Notes,
                NamingContainer = old.NamingContainer,
                NewCriterionId = replacement.Id,
                OdpInstanceName = mapping.OdpInstanceName ?? string.Empty,
                InstancePublicApiKey = mapping.PublicApiKey ?? string.Empty,
                AudienceId = mapping.AudienceId ?? string.Empty,
                RunId = runId,
                RunBy = runBy,
                MigratedUtc = now,
                Applied = false
            });

            updated.Criteria[mapping.Index] = replacement;
        }

        if (backups.Count == 0)
        {
            return 0;
        }

        // Backups are written before the change, then marked Applied once the save succeeds.
        // If anything fails in between, the unapplied records are ignored by the rollback job.
        _backupStore.SaveAll(backups);
        _visitorGroupRepository.Save(updated);

        foreach (var backup in backups)
        {
            backup.Applied = true;
            _backupStore.Save(backup);

            _logger.LogInformation(
                "ODP migration swap {RunId} by {RunBy}: visitor group {VisitorGroupId} ({VisitorGroupName}) criterion {OldCriterionId} swapped to Audience Membership {NewCriterionId} (instance {OdpInstance}, audience {AudienceId})",
                runId, runBy, backup.VisitorGroupId, backup.VisitorGroupName, backup.OldCriterionId,
                backup.NewCriterionId, backup.OdpInstanceName, backup.AudienceId);
        }

        return backups.Count;
    }
}

internal static class RunIdentity
{
    /// <summary>Who started the job, for the audit trail. Scheduled (unattended) runs have no user.</summary>
    public static string Describe(IPrincipal? principal) =>
        principal?.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? name : "scheduler";
}
