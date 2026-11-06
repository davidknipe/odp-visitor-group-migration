#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using EPiServer.Data;
using EPiServer.Data.Dynamic;

namespace OdpVisitorGroupMigration;

/// <summary>
/// One record per swapped criterion, kept in the Dynamic Data Store (not the media library) so it is
/// never publicly reachable. Used by the rollback job.
/// </summary>
[EPiServerDataStore(AutomaticallyCreateStore = true, AutomaticallyRemapStore = true, StoreName = "OdpVisitorGroupMigrationBackup")]
public class OdpMigrationBackup : IDynamicData
{
    public Identity Id { get; set; } = Identity.NewIdentity();

    public Guid VisitorGroupId { get; set; }

    public string VisitorGroupName { get; set; } = string.Empty;

    public Guid OldCriterionId { get; set; }

    public string OldTypeName { get; set; } = string.Empty;

    /// <summary>The original UNRVLD value, e.g. "my_segment" or "[US] - my_segment".</summary>
    public string OldAudienceValue { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public bool Required { get; set; }

    public int Points { get; set; }

    public string? Notes { get; set; }

    public string? NamingContainer { get; set; }

    public Guid NewCriterionId { get; set; }

    public string OdpInstanceName { get; set; } = string.Empty;

    /// <summary>The ODP instance's public key written to the new criterion. Public by design, not a secret.</summary>
    public string InstancePublicApiKey { get; set; } = string.Empty;

    public string AudienceId { get; set; } = string.Empty;

    public Guid RunId { get; set; }

    public string RunBy { get; set; } = string.Empty;

    public DateTime MigratedUtc { get; set; }

    /// <summary>Set only after the visitor group save succeeded. Rollback ignores records where this is false.</summary>
    public bool Applied { get; set; }

    public bool RolledBack { get; set; }

    public DateTime? RolledBackUtc { get; set; }

    public string? RolledBackBy { get; set; }
}

public sealed class OdpMigrationBackupStore
{
    private readonly DynamicDataStoreFactory _storeFactory;

    public OdpMigrationBackupStore(DynamicDataStoreFactory storeFactory)
    {
        _storeFactory = storeFactory;
    }

    private DynamicDataStore Store =>
        _storeFactory.GetStore(typeof(OdpMigrationBackup)) ?? _storeFactory.CreateStore(typeof(OdpMigrationBackup));

    public void Save(OdpMigrationBackup backup) => Store.Save(backup);

    public void SaveAll(IEnumerable<OdpMigrationBackup> backups)
    {
        var store = Store;
        foreach (var backup in backups)
        {
            store.Save(backup);
        }
    }

    public IReadOnlyList<OdpMigrationBackup> AppliedAndNotRolledBack() =>
        Store.Items<OdpMigrationBackup>().Where(b => b.Applied && !b.RolledBack).ToList();
}
