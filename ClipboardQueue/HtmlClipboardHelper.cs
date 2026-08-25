using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipboardQueue;

public static class HtmlClipboardHelper
{
    public static string CreateHtmlClipboardData(string htmlFragment)
    {
        htmlFragment ??= string.Empty;

        const string headerFormat =
            "Version:0.9\r\n" +
            "StartHTML:{0:D8}\r\n" +
            "EndHTML:{1:D8}\r\n" +
            "StartFragment:{2:D8}\r\n" +
            "EndFragment:{3:D8}\r\n";

        const string startHtml = "<html><body>";
        const string startFragmentTag = "<!--StartFragment-->";
        const string endFragmentTag = "<!--EndFragment-->";
        const string endHtml = "</body></html>";

        string zeroHeader = string.Format(CultureInfo.InvariantCulture, headerFormat, 0, 0, 0, 0);

        int startHtmlOffset = Encoding.UTF8.GetByteCount(zeroHeader);
        int startFragmentOffset = startHtmlOffset + Encoding.UTF8.GetByteCount(startHtml + startFragmentTag);
        int endFragmentOffset = startFragmentOffset + Encoding.UTF8.GetByteCount(htmlFragment);
        int endHtmlOffset = endFragmentOffset + Encoding.UTF8.GetByteCount(endFragmentTag + endHtml);

        string header = string.Format(
            CultureInfo.InvariantCulture,
            headerFormat,
            startHtmlOffset,
            endHtmlOffset,
            startFragmentOffset,
            endFragmentOffset);

        return header + startHtml + startFragmentTag + htmlFragment + endFragmentTag + endHtml;
    }

    /// <summary>
    /// Some sites (e.g. Qwen chat) put raw newline characters inside text and
    /// rely on CSS "white-space: pre-wrap" to display them. That CSS does not
    /// travel with the clipboard, so target apps collapse the newlines to
    /// spaces. This method converts such in-text newlines into explicit
    /// &lt;br&gt; tags so line breaks survive pasting anywhere.
    /// </summary>
    public static string NormalizeLineBreaks(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html;

        // Newlines that sit purely between tags are formatting whitespace
        // (e.g. "</p>\n<p>") - remove them so we don't create extra blank lines.
        string result = Regex.Replace(html, ">\s*[\r\n]+\s*<", "><");

        // Remaining newlines are inside text nodes - make them explicit breaks.
        result = result
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Replace("\n", "<br>");

        return result;
    }

    /// <summary>
    /// Extracts the HTML fragment from a raw CF_HTML ("HTML Format") string,
    /// e.g. the HTML that a browser puts on the clipboard.
    /// Returns null if the header cannot be parsed.
    /// </summary>
    public static string? ExtractFragment(string cfHtml)
    {
        if (string.IsNullOrEmpty(cfHtml))
            return null;

        int start = ParseOffset(cfHtml, "StartFragment:");
        int end = ParseOffset(cfHtml, "EndFragment:");

        if (start < 0 || end < 0 || end <= start)
            return null;

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(cfHtml);

            if (end > bytes.Length)
                return null;

            return Encoding.UTF8.GetString(bytes, start, end - start);
        }
        catch
        {
            return null;
        }
    }

    private static int ParseOffset(string cfHtml, string key)
    {
        int index = cfHtml.IndexOf(key, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
            return -1;

        index += key.Length;

        int value = 0;
        bool any = false;

        while (index < cfHtml.Length && char.IsDigit(cfHtml[index]))
        {
            value = value * 10 + (cfHtml[index] - '0');
            any = true;
            index++;
        }

        return any ? value : -1;
    }
}
