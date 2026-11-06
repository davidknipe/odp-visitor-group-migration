#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using EPiServer.Personalization.VisitorGroups;
using Microsoft.Extensions.Configuration;
using Optimizely.Cms.Odp.Audiences.Criteria;
using Optimizely.Cms.Odp.Audiences.Models;
using UNRVLD.ODP.VisitorGroups.Criteria.Models;

namespace OdpVisitorGroupMigration;

public enum MappingOutcome
{
    /// <summary>Real Time Segment criterion that can be swapped automatically.</summary>
    Migrate,
    /// <summary>Criterion type with no equivalent in Optimizely.Cms.Odp.</summary>
    Unsupported,
    /// <summary>Real Time Segment criterion that could not be mapped (missing settings or instance).</summary>
    Unresolved
}

public sealed record CriterionMapping(
    int Index,
    Guid CriterionId,
    string TypeName,
    string CriterionName,
    string? CurrentValue,
    MappingOutcome Outcome,
    string Detail,
    string? OdpInstanceName = null,
    string? PublicApiKey = null,
    string? AudienceId = null);

public sealed record GroupPlan(VisitorGroup Group, IReadOnlyList<CriterionMapping> Criteria)
{
    public bool HasWorkToDo => Criteria.Any(c => c.Outcome == MappingOutcome.Migrate);

    /// <summary>True if UNRVLD criteria will remain in this group after the swap, so the old package cannot be removed yet.</summary>
    public bool BlocksUninstall => Criteria.Any(c => c.Outcome != MappingOutcome.Migrate);
}

/// <summary>
/// Works out which UNRVLD.ODP.VisitorGroups criteria can become an Optimizely.Cms.Odp "Audience Membership" criterion.
/// </summary>
public sealed class OdpCriterionMapper
{
    private const string UnrvldAssemblyName = "UNRVLD.ODP.VisitorGroups";
    private const string RealTimeSegmentClassName = "AudienceCriterion";

    // The official model stores the condition as a string: "MemberOf" or "NotaMemberOf".
    private const string MemberOfCondition = "MemberOf";

    // UNRVLD prefixes the value with the endpoint name when several endpoints are configured, e.g. "[US] - my_segment".
    private static readonly Regex PrefixRegex =
        new(@"^\[(.+)\] - (.+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    public static readonly string OfficialCriterionTypeName = TypeNameFor(typeof(OdpAudienceCriterion));

    private readonly IConfiguration _configuration;
    private readonly OdpMigrationOptions _options;

    public OdpCriterionMapper(IConfiguration configuration, OdpMigrationOptions options)
    {
        _configuration = configuration;
        _options = options;
    }

    public IReadOnlyList<GroupPlan> Plan(IEnumerable<VisitorGroup> groups)
    {
        var unrvldEndpoints = GetUnrvldEndpointNames();
        var officialInstances = GetOfficialInstances();

        return groups
            .Select(g => new GroupPlan(g, g.Criteria
                .Select((c, i) => (Criterion: c, Index: i))
                .Where(x => IsUnrvldCriterion(x.Criterion))
                .Select(x => Map(x.Criterion, x.Index, unrvldEndpoints, officialInstances))
                .ToList()))
            .Where(p => p.Criteria.Count > 0)
            .OrderBy(p => p.Group.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static VisitorGroupCriterion BuildReplacement(VisitorGroupCriterion old, CriterionMapping mapping)
    {
        if (mapping.Outcome != MappingOutcome.Migrate)
        {
            throw new InvalidOperationException($"Criterion {mapping.CriterionId} is not migratable: {mapping.Detail}");
        }

        return new VisitorGroupCriterion
        {
            Id = Guid.NewGuid(),
            TypeName = OfficialCriterionTypeName,
            Model = new OdpAudienceCriterionModel
            {
                Instance = mapping.PublicApiKey,   // the official criterion stores the instance's PublicApiKey, not its name
                Condition = MemberOfCondition,
                AudienceId = mapping.AudienceId    // bare ODP audience name, with no "[Endpoint] - " prefix
            },
            Enabled = old.Enabled,
            Required = old.Required,
            Points = old.Points,
            Notes = old.Notes,
            NamingContainer = old.NamingContainer
        };
    }

    /// <summary>True if the criterion is still exactly what <see cref="BuildReplacement"/> wrote, i.e. nobody has edited it since.</summary>
    public static bool IsUnchangedReplacement(VisitorGroupCriterion criterion, string publicApiKey, string audienceId) =>
        string.Equals(criterion.TypeName, OfficialCriterionTypeName, StringComparison.Ordinal)
        && criterion.Model is OdpAudienceCriterionModel model
        && string.Equals(model.Condition, MemberOfCondition, StringComparison.Ordinal)
        && string.Equals(model.Instance, publicApiKey, StringComparison.Ordinal)
        && string.Equals(model.AudienceId, audienceId, StringComparison.Ordinal);

    public static bool IsUnrvldCriterion(VisitorGroupCriterion criterion) =>
        string.Equals(AssemblyNameOf(criterion.TypeName), UnrvldAssemblyName, StringComparison.OrdinalIgnoreCase);

    public static string TypeNameFor(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

    private CriterionMapping Map(
        VisitorGroupCriterion criterion,
        int index,
        IReadOnlyList<string> unrvldEndpoints,
        IReadOnlyDictionary<string, string> officialInstances)
    {
        var className = ClassNameOf(criterion.TypeName);

        if (!string.Equals(className, RealTimeSegmentClassName, StringComparison.Ordinal))
        {
            return new CriterionMapping(index, criterion.Id, criterion.TypeName, className, DescribeModel(criterion.Model),
                MappingOutcome.Unsupported,
                "No equivalent in Optimizely.Cms.Odp. Recreate this rule as an ODP real-time audience, then replace it with an Audience Membership criterion by hand.");
        }

        // Matches the v2.x model only. Groups saved by UNRVLD 1.x use a type name that no longer exists, so the model will be null.
        if (criterion.Model is not AudienceCriterionModel model)
        {
            return new CriterionMapping(index, criterion.Id, criterion.TypeName, className, null,
                MappingOutcome.Unresolved,
                "Criterion settings could not be loaded (possibly saved by UNRVLD 1.x). Recreate this criterion by hand.");
        }

        if (string.IsNullOrWhiteSpace(model.Audience))
        {
            return new CriterionMapping(index, criterion.Id, criterion.TypeName, className, model.Audience,
                MappingOutcome.Unresolved, "No segment is selected on this criterion.");
        }

        var (prefix, audienceId) = SplitPrefix(model.Audience);

        // An unprefixed value resolves to the first configured endpoint, which matches UNRVLD's runtime behaviour.
        var endpointName = prefix ?? unrvldEndpoints.FirstOrDefault();
        var instanceName = ResolveInstance(endpointName, prefix is not null, unrvldEndpoints.Count, officialInstances);

        if (instanceName is null
            || !officialInstances.TryGetValue(instanceName, out var publicApiKey)
            || string.IsNullOrWhiteSpace(publicApiKey))
        {
            return new CriterionMapping(index, criterion.Id, criterion.TypeName, className, model.Audience,
                MappingOutcome.Unresolved,
                $"Could not map UNRVLD endpoint '{endpointName ?? "(none)"}' to an entry under Optimizely:Odp:Instances. Add it to OdpMigration:InstanceMap.");
        }

        return new CriterionMapping(index, criterion.Id, criterion.TypeName, className, model.Audience,
            MappingOutcome.Migrate,
            $"Audience Membership: member of '{audienceId}' on instance '{instanceName}'.",
            instanceName, publicApiKey, audienceId);
    }

    private string? ResolveInstance(
        string? endpointName,
        bool endpointWasExplicit,
        int unrvldEndpointCount,
        IReadOnlyDictionary<string, string> officialInstances)
    {
        if (endpointName is not null)
        {
            var mapped = _options.InstanceMap
                .FirstOrDefault(kv => string.Equals(kv.Key, endpointName, StringComparison.OrdinalIgnoreCase)).Value;
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                return mapped;
            }

            if (officialInstances.ContainsKey(endpointName))
            {
                return endpointName;
            }
        }

        // A value that names its endpoint ("[EU] - x") must resolve to that endpoint, never to a guess.
        if (endpointWasExplicit)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(_options.DefaultInstance))
        {
            return _options.DefaultInstance;
        }

        // Only pick automatically when there is nothing to choose between: one UNRVLD endpoint and one official instance.
        return unrvldEndpointCount <= 1 && officialInstances.Count == 1 ? officialInstances.Keys.Single() : null;
    }

    private IReadOnlyList<string> GetUnrvldEndpointNames() =>
        _configuration.GetSection("EPiServer:OdpVisitorGroupOptions:OdpEndpoints")
            .GetChildren()
            .Select(s => string.IsNullOrWhiteSpace(s["Name"]) ? "Default" : s["Name"]!) // UNRVLD defaults Name to "Default"
            .ToList();

    private IReadOnlyDictionary<string, string> GetOfficialInstances() =>
        _configuration.GetSection("Optimizely:Odp:Instances")
            .GetChildren()
            .ToDictionary(s => s.Key, s => s["PublicApiKey"] ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    private static (string? Prefix, string Value) SplitPrefix(string value)
    {
        var match = PrefixRegex.Match(value);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Value) : (null, value);
    }

    private static string? AssemblyNameOf(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
        {
            return null;
        }

        var parts = typeName.Split(',');
        return parts.Length > 1 ? parts[1].Trim() : null;
    }

    private static string ClassNameOf(string? typeName)
    {
        var fullName = (typeName ?? string.Empty).Split(',')[0].Trim();
        var lastDot = fullName.LastIndexOf('.');
        return lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
    }

    // A readable summary of an unsupported criterion's settings for the report, e.g. "Comparison=GreaterThan; EngagementRank=50".
    private static string? DescribeModel(ICriterionModel? model) =>
        model is null
            ? null
            : string.Join("; ", model.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.Name != nameof(CriterionModelBase.Id))
                .Select(p => $"{p.Name}={p.GetValue(model)}"));
}
