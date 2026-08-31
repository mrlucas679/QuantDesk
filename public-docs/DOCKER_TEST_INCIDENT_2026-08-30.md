# Docker test restore incident — closed

The initial Docker test run failed before executing tests because the mounted NuGet cache lacked the resolved `xunit.analyzers` assemblies (`CS0006`). This was a test-environment defect, not an application or strategy failure.

## Reproduction and repair

- SDK/container: `mcr.microsoft.com/dotnet/sdk:10.0` (SDK `10.0.400`)
- Isolated cache volume: `quantdesk-nuget-test-cache`
- Restore/test command:

```text
docker run --rm -v "${PWD}:/src" -v quantdesk-nuget-test-cache:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/QuantDesk.Runtime.Tests/QuantDesk.Runtime.Tests.csproj --configuration Release --verbosity minimal
```

- Verification command:

```text
docker run --rm -v "${PWD}:/src" -v quantdesk-nuget-test-cache:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test tests/QuantDesk.Runtime.Tests/QuantDesk.Runtime.Tests.csproj --configuration Release --no-restore --verbosity minimal
```

The original repair produced `QuantDesk.Runtime.Tests` **71 passed, 0 failed,
0 skipped** and focused `EquityFeeSchedule` tests **3 passed, 0 failed,
0 skipped**. After the diagnostic lifecycle and recovery work, the final full
.NET solution result was **165 passed, 0 failed, 0 skipped**.

CI now clears local NuGet caches and forces restore before build/test, preventing a polluted cache from masking dependency defects.
