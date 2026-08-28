# QuantDesk development

QuantDesk is a paper-trading research platform. The C# runtime is the financial
authority; research artifacts and external agent integrations are asynchronous
inputs and must pass validation before becoming actionable.

## Local setup

1. Install the .NET 10 SDK.
2. Copy `.env.example` to a local environment file and provide paper-account
   credentials through environment variables.
3. Run `dotnet test QuantDesk.slnx`.
4. Run the API with `dotnet run --project src/QuantDesk.Api`.

## Paper connectivity checks

These commands are read-only: they do not submit, replace, or cancel orders.

```powershell
dotnet run --project src/QuantDesk.Cli -- capabilities
dotnet run --project src/QuantDesk.Cli -- stream-test
dotnet run --project src/QuantDesk.Cli -- paper-order-smoke
```

`stream-test` uses Alpaca's test market-data feed and its `FAKEPACA` symbol,
then exits after receiving one normalized quote or trade event. It has a
20-second timeout.

`paper-order-smoke` is the only command above that writes to the paper account.
It submits a single one-share SPY day-limit order at $1, reconciles it by client
order ID, cancels it immediately, and polls for the terminal canceled state.
Do not run it against a live account.

Never commit API keys, secrets, account exports, private research, or generated
runtime state. The private `Docs/` and research workspace are intentionally
excluded by `.gitignore` and `.dockerignore`.

Docker packaging is provided for local paper-only operation; live trading is not
enabled by this project configuration.
