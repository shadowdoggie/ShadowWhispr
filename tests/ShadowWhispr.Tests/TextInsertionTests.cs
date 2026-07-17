using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class TextInsertionTests
{
    [Fact]
    public void WindowsFocusCaptureApiIsAvailable()
    {
        var service = new TextInsertionService();
        var error = Record.Exception(() => service.CaptureTarget());

        Assert.Null(error);
    }
}
