#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EPiServer;
using EPiServer.Core;
using EPiServer.Core.Html.StringParsing;
using EPiServer.DataAbstraction;
using EPiServer.Personalization.VisitorGroups;
using EPiServer.Security;
using Microsoft.Extensions.Logging;

namespace OdpVisitorGroupMigration;

public sealed record UsageRow(
    Guid VisitorGroupId,
    string VisitorGroupName,
    int ContentId,
    int WorkId,
    string Language,
    string VersionStatus,
    string ContentName,
    string Location,
    string UsageType);

/// <summary>
/// Finds where visitor groups are used: personalised content areas, personalised rich text,
/// local blocks (recursively) and access rights. Read only.
/// </summary>
public sealed class VisitorGroupUsageScanner
{
    private const int MaxDepth = 10;

    private static readonly VersionStatus[] CurrentStatuses =
    {
        VersionStatus.Published,
        VersionStatus.CheckedOut,
        VersionStatus.CheckedIn,
        VersionStatus.AwaitingApproval,
        VersionStatus.DelayedPublish,
        VersionStatus.Rejected
    };

    private readonly IContentLoader _contentLoader;
    private readonly IContentVersionRepository _versionRepository;
    private readonly IContentSecurityRepository _securityRepository;
    private readonly ILogger<VisitorGroupUsageScanner> _logger;

    public VisitorGroupUsageScanner(
        IContentLoader contentLoader,
        IContentVersionRepository versionRepository,
        IContentSecurityRepository securityRepository,
        ILogger<VisitorGroupUsageScanner> logger)
    {
        _contentLoader = contentLoader;
        _versionRepository = versionRepository;
        _securityRepository = securityRepository;
        _logger = logger;
    }

    public List<UsageRow> Scan(
        IReadOnlyCollection<VisitorGroup> groups,
        bool includePreviousVersions,
        Func<bool> shouldStop,
        Action<string> reportProgress)
    {
        var rows = new List<UsageRow>();
        if (groups.Count == 0)
        {
            return rows;
        }

        var byId = groups.ToDictionary(g => g.Id);
        var byName = groups
            .GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var wastebasket = new HashSet<int>(_contentLoader.GetDescendents(ContentReference.WasteBasket).Select(r => r.ID));
        var processed = 0;

        foreach (var contentLink in _contentLoader.GetDescendents(ContentReference.RootPage))
        {
            if (shouldStop())
            {
                break;
            }

            if (wastebasket.Contains(contentLink.ID))
            {
                continue;
            }

            // One broken item (e.g. a removed content type) should not stop the whole report.
            try
            {
                ScanAccessRights(contentLink, byId, byName, rows);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ODP migration usage scan could not read access rights for content {ContentId}", contentLink.ID);
            }

            try
            {
                foreach (var version in _versionRepository.List(contentLink)
                             .Where(v => includePreviousVersions || CurrentStatuses.Contains(v.Status)))
                {
                    try
                    {
                        ScanVersion(version, byId, rows);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ODP migration usage scan skipped version {ContentLink} ({Language})",
                            version.ContentLink, version.LanguageBranch);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ODP migration usage scan could not list versions for content {ContentId}", contentLink.ID);
            }

            if (++processed % 250 == 0)
            {
                reportProgress($"Scanned {processed} content items, {rows.Count} usages found");
            }
        }

        return rows;
    }

    private void ScanVersion(ContentVersion version, IReadOnlyDictionary<Guid, VisitorGroup> byId, List<UsageRow> rows)
    {
        var content = string.IsNullOrEmpty(version.LanguageBranch)
            ? _contentLoader.Get<IContent>(version.ContentLink)
            : _contentLoader.Get<IContent>(version.ContentLink, CultureInfo.GetCultureInfo(version.LanguageBranch));

        WalkProperties(content, string.Empty, 0, (location, usageType, roles) =>
        {
            foreach (var group in MatchGroups(roles, byId))
            {
                rows.Add(new UsageRow(group.Id, group.Name, version.ContentLink.ID, version.ContentLink.WorkID,
                    version.LanguageBranch ?? string.Empty, version.Status.ToString(), content.Name, location, usageType));
            }
        });
    }

    private void WalkProperties(IContentData data, string path, int depth, Action<string, string, IEnumerable<string>> onRoles)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        foreach (var property in data.Property)
        {
            if (property.IsNull)
            {
                continue;
            }

            var location = path.Length == 0 ? property.Name : $"{path}.{property.Name}";

            // ContentArea must be matched before XhtmlString: in CMS 12 ContentArea inherits from XhtmlString.
            switch (property.Value)
            {
                case ContentArea contentArea:
                    foreach (var item in contentArea.Items)
                    {
                        onRoles(location, "Content area", item.AllowedRoles ?? Enumerable.Empty<string>());
#if CMS13
                        if (item.InlineBlock is IContentData inlineBlock)
                        {
                            WalkProperties(inlineBlock, $"{location}[inline]", depth + 1, onRoles);
                        }
#endif
                    }
                    break;

                case XhtmlString xhtml:
                    WalkFragments(xhtml.Fragments, location, onRoles);
                    break;

                case BlockData localBlock:
                    WalkProperties(localBlock, location, depth + 1, onRoles);
                    break;

                // Lists of local blocks, e.g. IList<SomeBlock> properties.
                case IEnumerable<BlockData> blockList:
                    var position = 0;
                    foreach (var block in blockList)
                    {
                        WalkProperties(block, $"{location}[{position++}]", depth + 1, onRoles);
                    }
                    break;
            }
        }
    }

    private static void WalkFragments(StringFragmentCollection fragments, string location, Action<string, string, IEnumerable<string>> onRoles)
    {
        foreach (var fragment in fragments)
        {
            if (fragment is PersonalizedContentFragment personalized)
            {
#if CMS13
                onRoles(location, "Rich text", personalized.RoleIdentities ?? Enumerable.Empty<string>());
#else
                onRoles(location, "Rich text", personalized.GetRoles() ?? Enumerable.Empty<string>());
#endif
                WalkFragments(personalized.Fragments, location, onRoles);
            }
#if CMS13
            // In CMS 13 blocks dropped into rich text can be personalised too.
            else if (fragment is IPersonalizedGroup group)
            {
                onRoles(location, "Rich text (inline block)", group.RoleIdentities ?? Enumerable.Empty<string>());
            }
#endif
        }
    }

    private void ScanAccessRights(
        ContentReference contentLink,
        IReadOnlyDictionary<Guid, VisitorGroup> byId,
        IReadOnlyDictionary<string, VisitorGroup> byName,
        List<UsageRow> rows)
    {
        var acl = _securityRepository.Get(contentLink);

        // Inherited entries are reported on the item that defines them, not on every descendant.
        if (acl is null || acl.IsInherited)
        {
            return;
        }

        foreach (var entry in acl.Entries.Where(e => e.EntityType == SecurityEntityType.VisitorGroup))
        {
            // Visitor group ACL entries are registered by name; match by ID as well to be safe.
            var group = Guid.TryParse(entry.Name, out var id) && byId.TryGetValue(id, out var byIdMatch)
                ? byIdMatch
                : byName.TryGetValue(entry.Name, out var byNameMatch) ? byNameMatch : null;

            if (group is null)
            {
                continue;
            }

            var name = _contentLoader.TryGet<IContent>(contentLink, out var content) ? content.Name : string.Empty;
            rows.Add(new UsageRow(group.Id, group.Name, contentLink.ID, 0, string.Empty, "n/a", name,
                "Access rights", "Access rights"));
        }
    }

    private static IEnumerable<VisitorGroup> MatchGroups(IEnumerable<string> roles, IReadOnlyDictionary<Guid, VisitorGroup> byId) =>
        roles
            .Select(r => Guid.TryParse(r, out var id) && byId.TryGetValue(id, out var g) ? g : null)
            .Where(g => g is not null)
            .Distinct()!;
}
