using System.Globalization;
using System.Text;

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
}
