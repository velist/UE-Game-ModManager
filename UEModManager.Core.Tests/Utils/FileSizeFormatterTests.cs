using UEModManager.Core.Utils;

namespace UEModManager.Core.Tests.Utils;

public class FileSizeFormatterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
    public void Format_UsesBinaryUnitsWithOneDecimalAboveBytes(long bytes, string expected)
    {
        Assert.Equal(expected, FileSizeFormatter.Format(bytes));
    }

    [Fact]
    public void Format_NegativeBytes_TreatsAsZero()
    {
        Assert.Equal("0 B", FileSizeFormatter.Format(-1));
    }
}