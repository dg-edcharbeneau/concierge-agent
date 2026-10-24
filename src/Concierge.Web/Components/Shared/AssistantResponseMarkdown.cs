using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Concierge.Web.Components.Shared;

/// <summary>Renders assistant replies as restricted, sanitized Markdown HTML.</summary>
internal static class AssistantResponseMarkdown
{
    private static readonly string[] AllowedTags =
    [
        "p", "br", "strong", "em", "ul", "ol", "li", "blockquote", "code", "pre",
        "table", "thead", "tbody", "tr", "th", "td", "a", "img"
    ];

    // Raw HTML stays literal text; only the extensions the chat needs are enabled.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    /// <summary>
    /// Returns sanitized HTML for <paramref name="text"/>, or <see langword="null"/> when the
    /// caller should show the original text encoded instead.
    /// </summary>
    public static string? Render(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            var document = Markdown.Parse(text, Pipeline);
            RestrictLinks(document);
            var html = CreateSanitizer().Sanitize(document.ToHtml(Pipeline)).Trim();
            return html.Length == 0 ? null : html;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Keeps HTTPS links and same-origin weather images; other links become their text.</summary>
    private static void RestrictLinks(MarkdownDocument document)
    {
        foreach (var autolink in document.Descendants<AutolinkInline>().ToList())
        {
            if (autolink.IsEmail || !IsAllowedUrl(autolink.Url))
            {
                autolink.ReplaceBy(new LiteralInline(autolink.Url));
            }
        }

        foreach (var link in document.Descendants<LinkInline>().ToList())
        {
            if (link.IsImage && IsAllowedImageUrl(link.Url)) continue;
            if (!link.IsImage && IsAllowedUrl(link.Url)) continue;

            while (link.FirstChild is { } child)
            {
                child.Remove();
                link.InsertBefore(child);
            }

            link.Remove();
        }
    }

    private static bool IsAllowedUrl(string? url) =>
        url is not null
        && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && !string.IsNullOrEmpty(uri.Host);

    private static bool IsAllowedImageUrl(string? url)
    {
        if (url is null
            || !url.StartsWith("/images/", StringComparison.Ordinal)
            || url.Contains('\\')
            || url.Contains('%')
            || url.Contains('?')
            || url.Contains('#')
            || url.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '/' or '-' or '_' or '.')))
        {
            return false;
        }

        return url.Split('/').All(segment => segment is not ("." or ".."));
    }

    // Created per render so no shared mutable sanitizer state crosses circuits; the component caches output.
    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer
        {
            AllowDataAttributes = false,
            KeepChildNodes = true
        };
        sanitizer.AllowedTags.Clear();
        sanitizer.AllowedTags.UnionWith(AllowedTags);
        sanitizer.AllowedAttributes.Clear();
        sanitizer.AllowedAttributes.UnionWith(["href", "src", "alt"]);
        sanitizer.AllowedCssProperties.Clear();
        sanitizer.AllowedAtRules.Clear();
        sanitizer.AllowedClasses.Clear();
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add(Uri.UriSchemeHttps);
        sanitizer.UriAttributes.Clear();
        sanitizer.UriAttributes.Add("href");
        sanitizer.UriAttributes.Add("src");
        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is not IElement { LocalName: "a" } anchor) return;
            if (anchor.HasAttribute("href"))
            {
                anchor.SetAttribute("target", "_blank");
                anchor.SetAttribute("rel", "noopener noreferrer");
                return;
            }

            // A link whose URL the sanitizer rejected keeps only its label text.
            foreach (var child in anchor.ChildNodes.ToList()) e.ReplacementNodes.Add(child);
            if (e.ReplacementNodes.Count == 0) e.ReplacementNodes.Add(e.Document.CreateTextNode(string.Empty));
        };
        return sanitizer;
    }
}
