using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Concierge.WorkflowServer.Setup;

public sealed class KnowledgeMarkdownCatalog(
    IHostEnvironment environment,
    IOptions<SetupOptions> options)
{
    private static readonly Regex FrontMatterValue = new(
        @"(?m)^(sourceId|title):\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Heading = new(
        @"^(#{2,3})\s+(.+?)\s*#*\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExplicitSectionId = new(
        @"^\s*<!--\s*sectionId:\s*([a-z0-9]+(?:-[a-z0-9]+)*)\s*-->\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Finds every Markdown file under the configured knowledge directory (resolved against the content root,
    /// then the app base directory) and parses front matter and <c>##</c>/<c>###</c> sections.
    /// Throws when the directory is missing or a file is malformed.
    /// </summary>
    public IReadOnlyList<KnowledgeDocument> DiscoverAndParse()
    {
        var configuredDirectory = options.Value.KnowledgeDirectory;
        var roots = new[]
        {
            Path.GetFullPath(configuredDirectory, environment.ContentRootPath),
            Path.GetFullPath(configuredDirectory, AppContext.BaseDirectory),
        }.Distinct(StringComparer.Ordinal).ToArray();
        var root = roots.FirstOrDefault(Directory.Exists);
        if (root is null)
        {
            throw new DirectoryNotFoundException(
                $"Configured setup knowledge directory '{configuredDirectory}' was not found. "
                + $"Checked: {string.Join(", ", roots)}.");
        }

        var files = Directory.GetFiles(root, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            throw new InvalidOperationException(
                $"No Markdown knowledge sources were found under '{root}'. Refusing to mark setup ready.");
        }

        var documents = files.Select(file => Parse(file, root)).ToArray();
        var duplicateId = documents
            .GroupBy(document => document.SourceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null)
        {
            throw new InvalidOperationException(
                $"Markdown source ID '{duplicateId.Key}' is declared more than once.");
        }

        return documents;
    }

    private static KnowledgeDocument Parse(string path, string root)
    {
        var markdown = File.ReadAllText(path);
        var frontMatterEnd = markdown.StartsWith("---", StringComparison.Ordinal)
            ? markdown.IndexOf("\n---", StringComparison.Ordinal)
            : -1;
        if (frontMatterEnd < 0)
        {
            throw new InvalidDataException(
                $"Markdown source '{path}' must begin with YAML front matter containing sourceId and title.");
        }

        var frontMatter = markdown[..frontMatterEnd];
        var fields = FrontMatterValue.Matches(frontMatter)
            .ToDictionary(
                match => match.Groups[1].Value,
                match => match.Groups[2].Value.Trim().Trim('"', '\''),
                StringComparer.Ordinal);
        if (!fields.TryGetValue("sourceId", out var sourceId)
            || !Regex.IsMatch(sourceId, @"^zgy-[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)
            || !fields.TryGetValue("title", out var sourceTitle)
            || string.IsNullOrWhiteSpace(sourceTitle))
        {
            throw new InvalidDataException(
                $"Markdown source '{path}' requires a valid zgy-* sourceId and non-empty title.");
        }

        var body = markdown[(frontMatterEnd + "\n---".Length)..];
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sections = new List<KnowledgeSection>();
        var pendingHeading = default((int Level, string Title, string Id)?);
        string? parentSectionId = null;
        StringBuilder? content = null;

        void AddSection()
        {
            if (pendingHeading is null || content is null)
            {
                return;
            }

            var text = NormalizeSectionText(content.ToString());
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var canonical = $"{sourceTitle}\n{pendingHeading.Value.Title}\n{text}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
            sections.Add(new KnowledgeSection(
                pendingHeading.Value.Id,
                pendingHeading.Value.Title,
                text,
                hash));
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                var level = heading.Groups[1].Length;
                if (level == 2)
                {
                    AddSection();
                    var title = heading.Groups[2].Value.Trim();
                    var slug = Slugify(title);
                    var explicitId = index + 1 < lines.Length
                        ? ExplicitSectionId.Match(lines[index + 1])
                        : Match.Empty;
                    var id = explicitId.Success ? explicitId.Groups[1].Value : slug;
                    if (explicitId.Success)
                    {
                        index++;
                    }

                    pendingHeading = (level, title, id);
                    parentSectionId = id;
                    content = new StringBuilder();
                    continue;
                }

                if (pendingHeading is { Level: 2 })
                {
                    AddSection();
                    var title = heading.Groups[2].Value.Trim();
                    pendingHeading = (3, title, $"{parentSectionId}-{Slugify(title)}");
                    content = new StringBuilder();
                    continue;
                }

                if (pendingHeading is { Level: 3 })
                {
                    AddSection();
                    var title = heading.Groups[2].Value.Trim();
                    pendingHeading = (3, title, $"{parentSectionId}-{Slugify(title)}");
                    content = new StringBuilder();
                    continue;
                }
            }

            if (pendingHeading is not null)
            {
                content!.AppendLine(line);
            }
        }

        AddSection();
        if (sections.Count == 0)
        {
            throw new InvalidDataException(
                $"Markdown source '{path}' contains no non-empty level-two sections.");
        }

        var duplicateSection = sections
            .GroupBy(section => section.SectionId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSection is not null)
        {
            throw new InvalidDataException(
                $"Markdown source '{sourceId}' has duplicate section ID '{duplicateSection.Key}'.");
        }

        return new KnowledgeDocument(
            sourceId,
            sourceTitle,
            Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
            sections);
    }

    private static string NormalizeSectionText(string text) =>
        string.Join(
            "\n",
            text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));

    private static string Slugify(string title)
    {
        var slug = Regex.Replace(title.ToLowerInvariant(), @"[^a-z0-9]+", "-")
            .Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            throw new InvalidDataException($"Heading '{title}' does not produce a stable section ID.");
        }

        return slug;
    }
}
