using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SldFile.Mfc;

namespace SldFile;

/// <summary>
/// Privacy sanitization: replaces user names recorded inside the document
/// (Header2 stream and per-configuration Config-0 front-end) with equal-length
/// placeholders. Byte lengths are preserved so all MFC stream offsets stay valid.
/// </summary>
public abstract partial class SwFile
{
    // Streams patched by SanitizeUsers, written back on Save().
    private readonly Dictionary<string, byte[]> _pendingStreamChanges = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces all recorded user names with an equal-length placeholder built from
    /// <paramref name="replacement"/> (padded with itself or truncated to the original
    /// character count). Also clears nothing else — use <see cref="Metadata"/> for
    /// Author / LastSavedBy. Call <see cref="Save()"/> to persist.
    /// </summary>
    /// <returns>The original user names that were replaced.</returns>
    public IReadOnlyList<string> SanitizeUsers(string replacement = "user")
    {
        if (string.IsNullOrEmpty(replacement))
            throw new ArgumentException("A non-empty replacement is required.", nameof(replacement));

        var replaced = new List<string>();

        var header2 = SanitizeStream(Header2.RawData, replacement, "Header2");
        if (header2 != null)
        {
            replaced.AddRange(header2.Value.OriginalNames);
            Header2.UsersBuilder.Clear();
            Header2.UsersBuilder.AddRange(header2.Value.NewNames);
            Header2.Freeze();
        }

        var config0 = SanitizeStream(Config0.RawData, replacement, "Contents/Config-0");
        if (config0 != null)
        {
            replaced.AddRange(config0.Value.OriginalNames);
            Config0.UsersBuilder.Clear();
            Config0.UsersBuilder.AddRange(config0.Value.NewNames);
            Config0.Freeze();
        }

        return replaced;
    }

    private (List<string> OriginalNames, List<string> NewNames)? SanitizeStream(byte[]? streamData, string replacement, string streamPath)
    {
        if (streamData == null || streamData.Length == 0)
            return null;

        var patched = PatchCStringArrayUsers(streamData, replacement);
        if (patched == null)
            return null;

        _pendingStreamChanges[streamPath] = patched.Value.PatchedData;
        return (patched.Value.OriginalNames, patched.Value.NewNames);
    }

    /// <summary>
    /// Deletes every recorded user name and empties the user list completely.
    ///
    /// Two passes are required because SolidWorks stores names in many streams and
    /// rebuilds Header2 from them on the next save:
    ///   1. Structural: every stream holding a su_CStringArray (Header2,
    ///      Config-0, Config-0-ModelHeader) gets a zero-count array.
    ///   2. Deep: remaining name occurrences anywhere else (CMgr, Definition,
    ///      DisplayLists, VisualStates, Biography, ...) are blanked in place with
    ///      equal-length spaces, so byte offsets never move.
    ///
    /// Call <see cref="Save()"/> to persist.
    /// </summary>
    /// <returns>The user names that were removed.</returns>
    public IReadOnlyList<string> ClearUsers()
    {
        var removed = new List<string>();

        // ── Pass 1: structured — every stream with a CStringArray of users ──
        foreach (var streamPath in StructuralStreams)
        {
            if (_pendingStreamChanges.TryGetValue(streamPath, out var pending))
            {
                if (StripCStringArrayUsers(pending) is { } again)
                {
                    _pendingStreamChanges[streamPath] = again.Data;
                    removed.AddRange(again.RemovedNames);
                }
                continue;
            }

            if (!RawStreams.TryGetValue(streamPath, out var raw) || raw.Length == 0)
                continue;

            if (StripCStringArrayUsers(raw) is { } result)
            {
                _pendingStreamChanges[streamPath] = result.Data;
                removed.AddRange(result.RemovedNames);
            }
        }

        if (removed.Count == 0)
        {
            // Users list may already be empty (e.g. re-cleaning); fall back to names
            // harvested from Biography-style history strings is not possible, so stop.
            return removed;
        }

        // Keep the in-memory models consistent with what will be written.
        Header2.UsersBuilder.Clear();
        Header2.Freeze();
        Config0.UsersBuilder.Clear();
        Config0.Freeze();

        // ── Pass 2: deep — blank leftover occurrences across all streams ──
        var names = removed
            .Where(n => n.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return removed;

        foreach (var streamPath in RawStreams.Keys.ToList())
        {
            byte[] source = _pendingStreamChanges.TryGetValue(streamPath, out var updated)
                ? updated
                : RawStreams[streamPath];
            if (source.Length < 8)
                continue;

            // Streams that carry external file references must keep their paths
            // intact, otherwise assemblies lose their components.
            bool protectPaths = StructuralStreams.Contains(streamPath);

            var copy = (byte[])source.Clone();
            if (!BlankUtf16Names(copy, names, protectPaths))
                continue;

            _pendingStreamChanges[streamPath] = copy;
        }

        return removed;
    }

    private static readonly string[] StructuralStreams =
    {
        "Header2", "Contents/Config-0", "Contents/Config-0-ModelHeader"
    };

    /// <summary>
    /// Overwrites every bounded UTF-16 occurrence of the given names with spaces.
    /// Length is preserved, so no surrounding structure or offset moves.
    /// When <paramref name="protectPaths"/> is set, occurrences that form a path
    /// segment (surrounded by separators) are left alone.
    /// </summary>
    internal static bool BlankUtf16Names(byte[] data, IReadOnlyList<string> names, bool protectPaths)
    {
        bool changed = false;
        foreach (var name in names)
        {
            byte[] pattern = Encoding.Unicode.GetBytes(name);
            int limit = data.Length - pattern.Length;
            for (int i = 0; i <= limit; i++)
            {
                if (data[i] != pattern[0]) continue;

                bool match = true;
                for (int j = 1; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (!match) continue;

                // Boundary check: the neighbouring UTF-16 code units must not be
                // letters/digits, otherwise this is part of a longer word.
                if (i >= 2 && IsWordUnit(data, i - 2))
                    continue;
                if (i + pattern.Length + 1 < data.Length && IsWordUnit(data, i + pattern.Length))
                    continue;

                if (protectPaths && IsPathSegment(data, i, pattern.Length))
                    continue;

                for (int j = 0; j < pattern.Length; j += 2)
                {
                    data[i + j] = 0x20;      // space
                    data[i + j + 1] = 0x00;
                }
                changed = true;
                i += pattern.Length - 1;
            }
        }
        return changed;
    }

    private static bool IsPathSegment(byte[] data, int offset, int length)
    {
        char before = offset >= 2 ? (char)(data[offset - 2] | (data[offset - 1] << 8)) : '\0';
        int afterOffset = offset + length;
        char after = afterOffset + 1 < data.Length
            ? (char)(data[afterOffset] | (data[afterOffset + 1] << 8))
            : '\0';
        bool beforeIsSeparator = before is '\\' or '/' or ':';
        bool afterIsSeparator = after is '\\' or '/' or '\0';
        return beforeIsSeparator && afterIsSeparator;
    }

    private static bool IsWordUnit(byte[] data, int offset)
    {
        char c = (char)(data[offset] | (data[offset + 1] << 8));
        // ASCII-only word characters. Non-ASCII neighbours (Chinese labels,
        // structure bytes) must not count as "part of a longer word", otherwise
        // names embedded next to CJK text would never be blanked.
        return c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    }

    /// <summary>
    /// Rewrites the su_CStringArray as an empty array: the count is set to zero and
    /// all entry bytes are dropped, while everything after the array (terminator,
    /// following elements) stays byte-identical. Returns null when the array is
    /// already empty.
    /// </summary>
    internal static (byte[] Data, List<string> RemovedNames)? StripCStringArrayUsers(byte[] streamData)
    {
        var doc = MfcDocument.Parse(streamData);
        var csa = doc.FindFirstDescendant("su_CStringArray");
        if (csa == null)
            return null;

        int offset = csa.DataStart;
        if (!MfcPrimitives.TryReadU16(streamData, ref offset, out ushort count) || count == 0)
            return null;

        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            if (!MfcPrimitives.IsWCharPrefix(streamData, offset))
                return null; // unexpected layout — leave the stream untouched
            string? name = MfcPrimitives.PeekWChar(streamData, offset);
            if (name == null)
                return null;
            names.Add(name);
            int chars = streamData[offset + 3];
            offset += 4 + chars * 2;
        }
        int stringsEnd = offset;

        // Rebuild as an empty array: count = 0, no entry bytes, suffix untouched.
        var output = new byte[csa.DataStart + 2 + (streamData.Length - stringsEnd)];

        Buffer.BlockCopy(streamData, 0, output, 0, csa.DataStart);
        output[csa.DataStart] = 0;
        output[csa.DataStart + 1] = 0;
        Buffer.BlockCopy(streamData, stringsEnd, output, csa.DataStart + 2, streamData.Length - stringsEnd);

        return (output, names.Where(n => !string.IsNullOrEmpty(n)).ToList());
    }

    /// <summary>
    /// Overwrites the user-name WChar strings inside the su_CStringArray element with
    /// equal-character-count placeholders. Mirrors <see cref="Binary.Header2SchemaMapper"/>
    /// exactly: a u16 count followed by that many WChar strings — nothing beyond the
    /// counted strings is touched (the element region may extend over other data).
    /// Returns null when the element is absent or no string actually changes.
    /// </summary>
    internal static (byte[] PatchedData, List<string> OriginalNames, List<string> NewNames)? PatchCStringArrayUsers(byte[] streamData, string replacement)
    {
        var doc = MfcDocument.Parse(streamData);
        var csa = doc.FindFirstDescendant("su_CStringArray");
        if (csa == null)
            return null;

        byte[] data = (byte[])streamData.Clone();
        var originals = new List<string>();
        var replacements = new List<string>();
        bool changed = false;

        int offset = csa.DataStart;
        if (!MfcPrimitives.TryReadU16(data, ref offset, out ushort count))
            return null;

        for (int i = 0; i < count; i++)
        {
            if (!MfcPrimitives.IsWCharPrefix(data, offset))
                break;

            int stringStart = offset;
            string? original = MfcPrimitives.PeekWChar(data, offset);
            if (original == null)
                break;

            // WChar layout: FF FE FF prefix (3 bytes) + char count (1 byte) + charCount * 2 bytes.
            int charCount = data[stringStart + 3];
            offset += 4 + charCount * 2;
            if (charCount == 0 || original.Length == 0)
                continue;

            string placeholder = BuildPlaceholder(replacement, charCount);
            byte[] utf16 = Encoding.Unicode.GetBytes(placeholder);
            Buffer.BlockCopy(utf16, 0, data, stringStart + 4, charCount * 2);

            originals.Add(original);
            replacements.Add(placeholder);
            if (!string.Equals(original, placeholder, StringComparison.Ordinal))
                changed = true;
        }

        if (!changed)
            return null;

        return (data, originals, replacements);
    }

    private static string BuildPlaceholder(string replacement, int charCount)
    {
        if (replacement.Length >= charCount)
            return replacement.Substring(0, charCount);

        var sb = new StringBuilder(charCount);
        while (sb.Length < charCount)
            sb.Append(replacement, 0, Math.Min(replacement.Length, charCount - sb.Length));
        return sb.ToString();
    }
}
