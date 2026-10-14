using System.Net.Http;
using UEModManager.Diagnostics;

namespace UEModManager.Core.Tests.Diagnostics;

public class UserFacingErrorsTests
{
    [Fact]
    public void UnauthorizedAccess_OffersRestartAsAdmin()
    {
        var e = UserFacingErrors.Describe(new UnauthorizedAccessException("Access to the path 'C:\\x' is denied."));

        Assert.Equal(UserFacingErrorAction.RestartAsAdmin, e.Action);
        Assert.DoesNotContain("Access to the path", e.Summary);
    }

    [Fact]
    public void IOException_WithAccessDeniedHResult_IsTreatedAsPermissionProblem()
    {
        var io = new IOException("denied", unchecked((int)0x80070005));

        Assert.Equal(UserFacingErrorAction.RestartAsAdmin, UserFacingErrors.Describe(io).Action);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void SharingOrLockViolation_TellsUserToCloseGame(int hresult)
    {
        var e = UserFacingErrors.Describe(new IOException("in use", hresult));

        Assert.Contains("占用", e.Summary);
        Assert.Contains("关闭游戏", e.Suggestion);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    public void DiskFull_IsRecognized(int hresult)
    {
        Assert.Contains("磁盘空间不足", UserFacingErrors.Describe(new IOException("full", hresult)).Summary);
    }

    [Fact]
    public void FileNotFound_IsRecognized()
    {
        Assert.Contains("找不到", UserFacingErrors.Describe(new FileNotFoundException("x")).Summary);
    }

    [Fact]
    public void PathTooLong_IsRecognized()
    {
        Assert.Contains("路径太长", UserFacingErrors.Describe(new PathTooLongException()).Summary);
    }

    [Fact]
    public void CorruptArchive_SuggestsRedownload()
    {
        Assert.Contains("重新下载", UserFacingErrors.Describe(new InvalidDataException()).Suggestion);
    }

    [Fact]
    public void HttpFailure_IsNetworkProblem()
    {
        Assert.Contains("服务器", UserFacingErrors.Describe(new HttpRequestException("boom")).Summary);
    }

    [Fact]
    public void TimeoutInsideCancellation_IsReportedAsTimeout()
    {
        var ex = new TaskCanceledException("timed out", new TimeoutException());

        Assert.Contains("超时", UserFacingErrors.Describe(ex).Summary);
    }

    [Fact]
    public void WrappedExceptions_AreUnwrappedToTheRealCause()
    {
        var ex = new AggregateException(
            new Exception("导入失败", new IOException("in use", unchecked((int)0x80070020))));

        Assert.Contains("占用", UserFacingErrors.Describe(ex).Summary);
    }

    [Fact]
    public void Unknown_FallsBackToGenericMessageWithoutRawText()
    {
        var e = UserFacingErrors.Describe(new ArgumentException("Value cannot be null. (Parameter 'x')"));

        Assert.Equal(UserFacingErrorAction.None, e.Action);
        Assert.DoesNotContain("Parameter", e.Summary + e.Suggestion);
        Assert.Contains("复制诊断信息", e.Suggestion);
    }

    [Fact]
    public void AuthoredChineseMessage_IsShownAsIs()
    {
        var e = UserFacingErrors.Describe(new InvalidOperationException("不能删除最后一个方案"));

        Assert.Equal("不能删除最后一个方案", e.Summary);
    }

    [Fact]
    public void KnownInnerCause_WinsOverAuthoredWrapperMessage()
    {
        var ex = new Exception("导入失败", new UnauthorizedAccessException());

        Assert.Equal(UserFacingErrorAction.RestartAsAdmin, UserFacingErrors.Describe(ex).Action);
    }
}
