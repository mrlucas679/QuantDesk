using System.Security.Cryptography;
using System.Text;

namespace QuantDesk.Api.Security;

public sealed class OperatorKeyAuthorizer(IConfiguration configuration)
{
    public bool IsAuthorized(string? suppliedKey)
    {
        string? configured = configuration["QUANTDESK_OPERATOR_KEY"];
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(suppliedKey))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(configured), Encoding.UTF8.GetBytes(suppliedKey));
    }
}
