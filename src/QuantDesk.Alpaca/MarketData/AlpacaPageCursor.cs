namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Drives an Alpaca <c>page_token</c> loop and fails closed when the broker repeats a token or
/// exceeds the caller's page budget, so an acquisition can never spin unbounded on broker input.
/// </summary>
internal sealed class AlpacaPageCursor(int maximumPages, string resourceName)
{
    private readonly HashSet<string> _observedTokens = new(StringComparer.Ordinal);
    private int _pagesRead;

    private string? Token { get; set; }

    public bool HasMorePages { get; private set; } = true;

    /// <summary>Query-string fragment carrying the current token, or empty on the first page.</summary>
    public string PageTokenQuery => Token is null ? string.Empty : $"&page_token={Uri.EscapeDataString(Token)}";

    /// <summary>Records the token returned by the page just read and decides whether to continue.</summary>
    public void Advance(string? nextPageToken)
    {
        if (++_pagesRead > maximumPages)
            throw new InvalidDataException($"Alpaca {resourceName} pagination exceeded {maximumPages} pages.");

        if (string.IsNullOrWhiteSpace(nextPageToken))
        {
            Token = null;
            HasMorePages = false;
            return;
        }

        if (!_observedTokens.Add(nextPageToken))
            throw new InvalidDataException($"Alpaca {resourceName} pagination repeated a page token.");

        Token = nextPageToken;
    }
}
