using System;
using System.Collections.Generic;
using System.Linq;
using SldFile.Binary;

namespace SldFile;

/// <summary>
/// Public data surface of an opened document. Focused on what the cleanup
/// tool needs: metadata, users, revision log, references — plus the
/// sanitization hooks implemented in <see cref="SwFile.Sanitize"/>.
/// </summary>
public abstract partial class SwFile
{
    private MetadataStore _metadataStore = null!;
    private bool _disposed;

    public bool IsReadOnly => _readOnly;

    public DocumentKind Kind => this is SldAssembly ? DocumentKind.Assembly
        : this is SldDrawing ? DocumentKind.Drawing
        : DocumentKind.Part;

    public DocumentMetadata Metadata { get; private set; } = null!;

    public PropertyCollection CustomProperties { get; } = new();

    public IReadOnlyList<Configuration> Configurations { get; private set; } = Array.Empty<Configuration>();

    /// <summary>User names recorded in the document (first entry is the creator).</summary>
    public IReadOnlyList<string> Users { get; private set; } = Array.Empty<string>();

    /// <summary>Revision log entries from the Header2 stream.</summary>
    public IReadOnlyList<Revision> Revisions { get; private set; } = Array.Empty<Revision>();

    /// <summary>External file references (component paths for assemblies and drawings).</summary>
    public IReadOnlyList<DocumentReference> References { get; private set; } = Array.Empty<DocumentReference>();

    private void InitializeUserData(Dictionary<string, byte[]> streams)
    {
        _metadataStore = new MetadataStore(streams);
        Metadata = _metadataStore.ReadMetadata();
        _metadataStore.ReadCustomProperties(CustomProperties);
        Configurations = _metadataStore.ReadConfigurations();
        Users = Array.AsReadOnly(Header2.Users.ToArray());
        Revisions = Array.AsReadOnly(Header2.LogEntries.Select(e =>
        {
            uint? timestamp = e.Timestamp ?? (e.IsTerminal ? Header2.BaseTimestamp : (uint?)null);
            return new Revision(e.Action, e.FeatureName, e.UserName, timestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(timestamp!.Value) : (DateTimeOffset?)null);
        }).ToArray());
        References = Array.AsReadOnly(Header2.ExternalReferences.Select(e => new DocumentReference(e.FullPath, e.ShortName)).ToArray());
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().Name);
    }
}
