using OneBox.Plugins.Clipboard.Core;

namespace OneBox.Tests;

public class ClipboardHtmlTests
{
    [Fact]
    public void ExtractFragment_WithHeader_ReturnsFragment()
    {
        const string body = "<html><body><!--StartFragment--><b>Hello</b><!--EndFragment--></body></html>";
        const string cf =
            "Version:0.9\n" +
            "StartHTML:0000000100\n" +
            "EndHTML:0000000200\n" +
            "StartFragment:0000000120\n" +
            "EndFragment:0000000150\n" +
            body;

        var fragment = ClipboardHtml.ExtractFragment(cf);
        Assert.Contains("<b>Hello</b>", fragment);
    }

    [Fact]
    public void ExtractFragment_WithoutHeader_ReturnsInput()
    {
        const string raw = "<b>Hello</b>";
        Assert.Equal(raw, ClipboardHtml.ExtractFragment(raw));
    }

    [Fact]
    public void ExtractFragment_Malformed_ReturnsInput()
    {
        const string bad = "Version:0.9\nStartFragment:abc\nEndFragment:xyz\nwhatever";
        Assert.Equal(bad, ClipboardHtml.ExtractFragment(bad));
    }
}
