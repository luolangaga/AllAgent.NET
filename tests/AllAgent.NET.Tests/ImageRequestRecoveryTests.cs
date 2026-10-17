using AllAgent.NET.Core.Models;
using AllAgent.NET.Core.Services.AI;
using Xunit;

namespace AllAgent.NET.Tests;

public sealed class ImageRequestRecoveryTests
{
    [Theory]
    [InlineData("HTTP 500: messages[0].image[0]: unsupported image")]
    [InlineData("HTTP 400: unsupported image")]
    [InlineData("HTTP 400: messages[99].image[0]: unsupported image")]
    [InlineData("HTTP 400: messages[0].image[2]: unsupported image")]
    [InlineData("HTTP 400: messages[0].image[0]: invalid api key")]
    public void UnrelatedOrAmbiguousErrorsCannotRemoveImages(string error)
    {
        var image = new ImageAttachment("example.png", "image/png", "unused");
        Assert.Null(ImageRequestRecovery.RejectedImage(error, [new ChatMessage("user", "text") { Images = [image] }]));
    }
}
