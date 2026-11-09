#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using EPiServer;
using EPiServer.Core;
using EPiServer.DataAccess;
using EPiServer.DataAbstraction;
using EPiServer.Framework.Blobs;
using EPiServer.Security;
using Microsoft.Extensions.Logging;

namespace OdpVisitorGroupMigration;

/// <summary>
/// Saves CSV reports as media in a folder under "For All Sites" that only the configured admin roles can read.
/// DXP has no persistent local file system, so the media library is the simplest place to put them.
/// </summary>
public sealed class MigrationReportWriter
{
    private const string Extension = ".csv";

    private readonly IContentRepository _contentRepository;
    private readonly IContentTypeRepository _contentTypeRepository;
    private readonly ContentMediaResolver _mediaResolver;
    private readonly IBlobFactory _blobFactory;
    private readonly IContentSecurityRepository _securityRepository;
    private readonly OdpMigrationOptions _options;
    private readonly ILogger<MigrationReportWriter> _logger;

    public MigrationReportWriter(
        IContentRepository contentRepository,
        IContentTypeRepository contentTypeRepository,
        ContentMediaResolver mediaResolver,
        IBlobFactory blobFactory,
        IContentSecurityRepository securityRepository,
        OdpMigrationOptions options,
        ILogger<MigrationReportWriter> logger)
    {
        _contentRepository = contentRepository;
        _contentTypeRepository = contentTypeRepository;
        _mediaResolver = mediaResolver;
        _blobFactory = blobFactory;
        _securityRepository = securityRepository;
        _options = options;
        _logger = logger;
    }

    /// <summary>Fails fast, before any slow work, if reports cannot be saved.</summary>
    public void EnsureCanWrite()
    {
        if (_mediaResolver.GetFirstMatching(Extension) is null)
        {
            throw new InvalidOperationException(
                "No media type accepts .csv files. Add \"csv\" to the ExtensionString of a MediaData type's [MediaDescriptor] attribute.");
        }
    }

    public string Write(string fileName, IEnumerable<IEnumerable<string?>> rows)
    {
        EnsureCanWrite();

        var folderLink = GetOrCreateLockedFolder();
        var mediaType = _contentTypeRepository.Load(_mediaResolver.GetFirstMatching(Extension));
        var file = _contentRepository.GetDefault<MediaData>(folderLink, mediaType.ID);
        file.Name = fileName;

        var blob = _blobFactory.CreateBlob(file.BinaryDataContainer, Extension);
        using (var stream = new MemoryStream(Csv.Build(rows)))
        {
            blob.Write(stream);
        }

        file.BinaryData = blob;

        // AccessLevel.NoAccess skips the editor permission check: the job runs without a logged-in user.
        var savedLink = _contentRepository.Save(file, SaveAction.Publish, AccessLevel.NoAccess);
        _logger.LogInformation("ODP migration report saved as content {ContentId} ({FileName})", savedLink.ID, fileName);

        return $"For All Sites > {_options.ReportFolderName} > {fileName}";
    }

    private ContentReference GetOrCreateLockedFolder()
    {
        var folder = _contentRepository
            .GetChildren<ContentFolder>(ContentReference.GlobalBlockFolder)
            .FirstOrDefault(f => string.Equals(f.Name, _options.ReportFolderName, StringComparison.OrdinalIgnoreCase));

        ContentReference folderLink;
        if (folder is null)
        {
            var newFolder = _contentRepository.GetDefault<ContentFolder>(ContentReference.GlobalBlockFolder);
            newFolder.Name = _options.ReportFolderName;
            folderLink = _contentRepository.Save(newFolder, SaveAction.Publish, AccessLevel.NoAccess);
        }
        else
        {
            folderLink = folder.ContentLink;
        }

        // Re-applied on every run. "For All Sites" usually grants Everyone read access, and the reports list
        // internal content and audience names, so stop inheriting and allow the admin roles only.
        var acl = (IContentSecurityDescriptor)_securityRepository.Get(folderLink).CreateWritableClone();
        if (acl.IsInherited)
        {
            acl.ToLocal();
        }
        acl.Clear();
        foreach (var role in _options.EffectiveReportReadRoles)
        {
            // Read to open the reports, Delete to tidy up afterwards. Nothing more is needed.
            acl.AddEntry(new AccessControlEntry(role, AccessLevel.Read | AccessLevel.Delete, SecurityEntityType.Role));
        }

        _securityRepository.Save(folderLink, acl, SecuritySaveType.Replace);
        return folderLink;
    }
}

internal static class Csv
{
    public static byte[] Build(IEnumerable<IEnumerable<string?>> rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        // A byte order mark (BOM) so Excel opens the file as UTF-8.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(builder.ToString())).ToArray();
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        // Content and visitor group names are typed by editors: stop spreadsheet apps running them as formulas.
        // Includes the full-width variants listed by OWASP's CSV injection guidance.
        if (text.Length > 0 && "=+-@\t\r\n＝＋－＠".IndexOf(text[0]) >= 0)
        {
            text = "'" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
