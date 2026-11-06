#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using EPiServer.Personalization.VisitorGroups;
using EPiServer.Scheduler;
using EPiServer.Security;
using Microsoft.Extensions.Logging;
using UNRVLD.ODP.VisitorGroups.Criteria.Criterion;
using UNRVLD.ODP.VisitorGroups.Criteria.Models;
#if !CMS13
using EPiServer.PlugIn;
#endif

namespace OdpVisitorGroupMigration;

/// <summary>
/// Undo: puts the original UNRVLD Real Time Segment criteria back, using the backups written by the swap job.
/// Only works while UNRVLD.ODP.VisitorGroups is still installed. Criteria that an editor has changed since
/// the swap are left alone and logged.
/// </summary>
#if CMS13
[ScheduledJob(
#else
[ScheduledPlugIn(
#endif
    DisplayName = "ODP migration 3 - Roll back swap",
    Description = "Restores the UNRVLD Real Time Segment criteria replaced by the swap job. Needs UNRVLD.ODP.VisitorGroups installed.",
    GUID = "5cb76b2b-6d9f-49e3-bef2-638c5911aab0")]
public class OdpMigrationRollbackJob : ScheduledJobBase
{
    // Built from the compiled type, not from stored data, so the rollback can only ever restore this one criterion type.
    private static readonly string UnrvldAudienceTypeName = OdpCriterionMapper.TypeNameFor(typeof(AudienceCriterion));

    private readonly IVisitorGroupRepository _visitorGroupRepository;
    private readonly OdpMigrationBackupStore _backupStore;
    private readonly IPrincipalAccessor _principalAccessor;
    private readonly ILogger<OdpMigrationRollbackJob> _logger;
    private volatile bool _stopRequested;

    public OdpMigrationRollbackJob(
        IVisitorGroupRepository visitorGroupRepository,
        OdpMigrationBackupStore backupStore,
        IPrincipalAccessor principalAccessor,
        ILogger<OdpMigrationRollbackJob> logger)
    {
        _visitorGroupRepository = visitorGroupRepository;
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
        _principalAccessor.Principal = new GenericPrincipal(new GenericIdentity(OdpMigrationSwapJob.JobIdentity), Array.Empty<string>());

        try
        {
            int restored = 0, skipped = 0, failedGroups = 0;
            var groups = _backupStore.AppliedAndNotRolledBack().GroupBy(b => b.VisitorGroupId).ToList();

            foreach (var groupBackups in groups)
            {
                if (_stopRequested)
                {
                    break;
                }

                try
                {
                    var (done, notFound) = RollBack(groupBackups.Key, groupBackups.ToList(), runBy);
                    restored += done;
                    skipped += notFound;
                }
                catch (Exception ex)
                {
                    failedGroups++;
                    _logger.LogError(ex, "ODP migration rollback failed for visitor group {VisitorGroupId}", groupBackups.Key);
                }
            }

            var failed = failedGroups > 0 ? $" {failedGroups} group(s) failed, see the log." : string.Empty;
            var stopped = _stopRequested ? " Stopped early: run again to finish." : string.Empty;
            return $"Restored {restored} UNRVLD criteria. {skipped} were skipped because they were deleted or edited after the swap.{failed}{stopped}";
        }
        finally
        {
            _principalAccessor.Principal = previousPrincipal;
        }
    }

    private (int Restored, int Skipped) RollBack(Guid visitorGroupId, IReadOnlyList<OdpMigrationBackup> backups, string runBy)
    {
        var original = _visitorGroupRepository.Load(visitorGroupId);
        if (original is null)
        {
            _logger.LogWarning("ODP migration rollback: visitor group {VisitorGroupId} no longer exists", visitorGroupId);
            return (0, backups.Count);
        }

        var updated = original.Copy();
        if (updated.Id != original.Id || ReferenceEquals(updated.Criteria, original.Criteria))
        {
            throw new InvalidOperationException("VisitorGroup.Copy() did not return an independent copy with the same ID. Aborting.");
        }

        var restoredBackups = new List<OdpMigrationBackup>();
        var usedIndexes = new HashSet<int>();

        foreach (var backup in backups)
        {
            var index = FindUnchangedReplacement(updated, backup, usedIndexes);
            if (index < 0)
            {
                _logger.LogWarning("ODP migration rollback: criterion {NewCriterionId} in visitor group {VisitorGroupId} was deleted or edited after the swap, so it was left alone",
                    backup.NewCriterionId, visitorGroupId);
                continue;
            }

            usedIndexes.Add(index);
            updated.Criteria[index] = new VisitorGroupCriterion
            {
                Id = Guid.NewGuid(),
                TypeName = UnrvldAudienceTypeName,
                Model = new AudienceCriterionModel { Audience = backup.OldAudienceValue },
                Enabled = backup.Enabled,
                Required = backup.Required,
                Points = backup.Points,
                Notes = backup.Notes,
                NamingContainer = backup.NamingContainer
            };
            restoredBackups.Add(backup);
        }

        if (restoredBackups.Count == 0)
        {
            return (0, backups.Count);
        }

        _visitorGroupRepository.Save(updated);

        var now = DateTime.UtcNow;
        foreach (var backup in restoredBackups)
        {
            backup.RolledBack = true;
            backup.RolledBackUtc = now;
            backup.RolledBackBy = runBy;
            _backupStore.Save(backup);

            _logger.LogInformation(
                "ODP migration rollback by {RunBy}: visitor group {VisitorGroupId} ({VisitorGroupName}) restored UNRVLD criterion for audience {AudienceId} (swap run {RunId})",
                runBy, backup.VisitorGroupId, backup.VisitorGroupName, backup.AudienceId, backup.RunId);
        }

        return (restoredBackups.Count, backups.Count - restoredBackups.Count);
    }

    private static int FindUnchangedReplacement(VisitorGroup group, OdpMigrationBackup backup, ISet<int> usedIndexes)
    {
        bool IsOurs(int i) =>
            !usedIndexes.Contains(i)
            && OdpCriterionMapper.IsUnchangedReplacement(group.Criteria[i], backup.InstancePublicApiKey, backup.AudienceId);

        // Prefer the exact criterion the swap job wrote. If it exists but has been edited, leave it alone.
        for (var i = 0; i < group.Criteria.Count; i++)
        {
            if (group.Criteria[i].Id == backup.NewCriterionId)
            {
                return IsOurs(i) ? i : -1;
            }
        }

        // Fallback in case the repository assigned new criterion IDs on save: an identical
        // Audience Membership criterion (same instance, audience and "Member of" condition).
        for (var i = 0; i < group.Criteria.Count; i++)
        {
            if (IsOurs(i))
            {
                return i;
            }
        }

        return -1;
    }
}
