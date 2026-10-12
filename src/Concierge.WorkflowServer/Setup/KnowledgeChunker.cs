using System.Security.Cryptography;
using System.Text;

namespace Concierge.WorkflowServer.Setup;

public static class KnowledgeChunker
{
    public const int MaximumChunkCharacters = 1600;

    /// <summary>
    /// Splits a section on paragraph boundaries into chunks of at most <see cref="MaximumChunkCharacters"/>.
    /// Chunk IDs derive from content, so re-running setup on unchanged Markdown yields the same IDs.
    /// </summary>
    public static IReadOnlyList<KnowledgeChunk> Chunk(SectionChunkingRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Section.SectionId);

        var paragraphs = request.Section.Content.Split(
            ["\n\n"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parts = new List<string>();
        var current = new StringBuilder();

        foreach (var original in paragraphs)
        {
            foreach (var paragraph in SplitLongParagraph(original))
            {
                if (current.Length > 0 && current.Length + paragraph.Length + 2 > MaximumChunkCharacters)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.AppendLine().AppendLine();
                }

                current.Append(paragraph);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts.Select((content, index) =>
        {
            var ordinal = index + 1;
            var chunkKey = $"{request.Section.SectionId}#{ordinal:D4}";
            var chunkId = DeterministicId(
                $"{request.SourceId}#{chunkKey}#{request.Section.Title}\n{content}");
            var anchor = $"{request.SourceId}#{request.Section.SectionId}";
            return new KnowledgeChunk(
                chunkId.ToString(),
                chunkKey,
                index,
                request.Section.Title,
                anchor,
                content,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sourceId"] = request.SourceId,
                    ["sectionId"] = request.Section.SectionId,
                    ["evidenceKey"] = anchor,
                });
        }).ToArray();
    }

    /// <summary>
    /// Derives a stable UUID (version 5 layout) from the SHA-256 of <paramref name="value"/>.
    /// </summary>
    public static Guid DeterministicId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guid = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guid);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        return new Guid(guid);
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph)
    {
        if (paragraph.Length <= MaximumChunkCharacters)
        {
            yield return paragraph;
            yield break;
        }

        var offset = 0;
        while (offset < paragraph.Length)
        {
            var length = Math.Min(MaximumChunkCharacters, paragraph.Length - offset);
            if (offset + length < paragraph.Length)
            {
                var lastSpace = paragraph.LastIndexOf(' ', offset + length - 1, length);
                if (lastSpace > offset)
                {
                    length = lastSpace - offset;
                }
            }

            yield return paragraph.Substring(offset, length).Trim();
            offset += length;
            while (offset < paragraph.Length && char.IsWhiteSpace(paragraph[offset]))
            {
                offset++;
            }
        }
    }
}
