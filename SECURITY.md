# Security policy

QuantDesk is intended for paper-trading development and testing. Do not use
this repository or its default configuration for live trading.

## Reporting a vulnerability

Please report security issues privately to the repository owner rather than
opening a public issue with credentials, account data, or an exploit.

Never commit Alpaca API keys, secrets, account exports, private research,
model artifacts containing sensitive data, or generated runtime state. Use
environment variables and rotate any credential that may have been exposed.

The public repository intentionally excludes the private `Docs/` and research
workspace; changes that would expose those materials should be rejected.
