using Concierge.Web.Components.Shared;

namespace Concierge.Core.Tests;

public sealed class AssistantResponseMarkdownTests
{
    /// <summary>Renders the Markdown the concierge model commonly returns.</summary>
    [Fact]
    public void RendersEmphasisListsAndInlineCode()
    {
        var html = Render("**Breakfast** is *daily*.\n\n- Gym\n- Pool\n\n1. Lobby\n2. Desk\n\nUse `ZGY-Guest`.");

        Assert.Contains("<strong>Breakfast</strong>", html);
        Assert.Contains("<em>daily</em>", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>Gym</li>", html);
        Assert.Contains("<ol>", html);
        Assert.Contains("<li>Lobby</li>", html);
        Assert.Contains("<code>ZGY-Guest</code>", html);
        Assert.DoesNotContain("**", html);
    }

    /// <summary>Fenced code keeps markup and Markdown markers as encoded text.</summary>
    [Fact]
    public void FencedCodeStaysEncoded()
    {
        var html = Render("```html\n<script>alert(1)</script> **not bold**\n```");

        Assert.Contains("<pre><code>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; **not bold**", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<strong>", html);
        Assert.DoesNotContain("class=", html);
    }

    /// <summary>Raw HTML from the model never becomes active markup.</summary>
    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("Hi <img src=x onerror=alert(1)> there", "<img")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>", "<iframe")]
    [InlineData("<b onclick=\"alert(1)\">bold</b>", "<b")]
    public void RawHtmlIsInert(string text, string forbidden)
    {
        var html = Render(text);

        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;", html);
    }

    /// <summary>Absolute HTTPS links stay clickable and open safely in a new tab.</summary>
    [Fact]
    public void KeepsHttpsLinks()
    {
        var html = Render("See [the spa](https://example.com/spa?a=1&b=2).");

        Assert.Contains("<a href=\"https://example.com/spa?a=1&amp;b=2\" target=\"_blank\" rel=\"noopener noreferrer\">the spa</a>", html);
    }

    /// <summary>Unsafe or non-absolute links keep their label without becoming clickable.</summary>
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("jav&#x61;script:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/path")]
    [InlineData("/spa")]
    [InlineData("spa.html")]
    [InlineData("http://example.com")]
    [InlineData("mailto:desk@example.com")]
    public void RejectsUnsafeLinksButKeepsLabel(string url)
    {
        var html = Render($"Visit [the spa]({url}) today.");

        Assert.DoesNotContain("<a", html);
        Assert.DoesNotContain("href", html);
        Assert.Contains("Visit the spa today.", html);
    }

    /// <summary>Autolinks follow the same HTTPS-only rule.</summary>
    [Fact]
    public void AutolinksFollowTheSameRule()
    {
        var html = Render("<https://example.com> and <javascript:alert(1)> and <desk@example.com>");

        Assert.Contains("<a href=\"https://example.com\"", html);
        Assert.Single(html.Split("<a ").Skip(1));
        Assert.Contains("javascript:alert(1)", html);
        Assert.Contains("desk@example.com", html);
    }

    /// <summary>Images make no request and keep their alt text readable.</summary>
    [Theory]
    [InlineData("![Pool at sunset](https://example.com/pool.png)")]
    [InlineData("![Pool at sunset](javascript:alert(1))")]
    public void ImagesBecomeAltText(string text)
    {
        var html = Render(text);

        Assert.DoesNotContain("<img", html);
        Assert.DoesNotContain("example.com", html);
        Assert.Contains("Pool at sunset", html);
    }

    /// <summary>Plain replies keep their text and line breaks.</summary>
    [Fact]
    public void PlainTextKeepsMeaning()
    {
        var html = Render("Breakfast is 7–10 AM.\nThe pool opens at 8.");

        Assert.Equal("<p>Breakfast is 7–10 AM.<br>\nThe pool opens at 8.</p>", html);
    }

    /// <summary>Empty replies use the encoded-text fallback.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n")]
    public void EmptyTextFallsBack(string? text) => Assert.Null(AssistantResponseMarkdown.Render(text));

    /// <summary>Pipe tables render with only allowlisted table markup.</summary>
    [Fact]
    public void RendersPipeTables()
    {
        var html = Render("| Amenity | Hours |\n| :--- | ---: |\n| Gym | 24h |");

        Assert.Contains("<table>", html);
        Assert.Contains("<th>Amenity</th>", html);
        Assert.Contains("<td>24h</td>", html);
        Assert.DoesNotContain("style=", html);
    }

    private static string Render(string text) =>
        AssistantResponseMarkdown.Render(text) ?? throw new Xunit.Sdk.XunitException("Expected rendered HTML.");
}
