using Microsoft.Extensions.Configuration;
using QuantDesk.Api.Security;

namespace QuantDesk.Api.Tests;

public sealed class OperatorKeyAuthorizerTests
{
    [Fact]
    public void RequiresExactConfiguredOperatorKey()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["QUANTDESK_OPERATOR_KEY"] = "correct-key" })
            .Build();
        var authorizer = new OperatorKeyAuthorizer(configuration);
        Assert.True(authorizer.IsAuthorized("correct-key"));
        Assert.False(authorizer.IsAuthorized("wrong-key"));
        Assert.False(authorizer.IsAuthorized(null));
    }
}
