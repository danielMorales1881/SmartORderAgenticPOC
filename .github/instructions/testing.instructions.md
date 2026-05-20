---
applyTo: "tests/**/*.cs"
description: "Testing conventions for Smart Orders .NET unit and integration tests"
---

# Testing Conventions

## Directory layout

```
tests/
├── SmartOrders.Unit/        ← mock all external I/O; no network, no DB
└── SmartOrders.Integration/ ← real pipeline via WebApplicationFactory<Program>
```

## Framework

- Test runner: `xUnit`
- Mocks: `NSubstitute`
- Assertions: `FluentAssertions`
- Integration: `Microsoft.AspNetCore.Mvc.Testing`

## Mocking external I/O

| Dependency | How to mock |
|---|---|
| `IOrderCatalogRepository` | `Substitute.For<IOrderCatalogRepository>()` |
| `ITwOrderQueueRepository` | `Substitute.For<ITwOrderQueueRepository>()` |
| `IChatCompletionService` | `Substitute.For<IChatCompletionService>()` |
| `ILogger<T>` | `Substitute.For<ILogger<T>>()` or `NullLogger<T>.Instance` |
| `HttpClient` (TW queue) | `MockHttpMessageHandler` or `Substitute.For<HttpMessageHandler>()` |

## Unit test style

```csharp
public sealed class SearchPluginTests
{
    private readonly IOrderCatalogRepository _catalog = Substitute.For<IOrderCatalogRepository>();
    private readonly ILogger<SearchPlugin> _logger = NullLogger<SearchPlugin>.Instance;

    [Fact]
    public async Task SearchOrdersAsync_ReturnsMappedJson()
    {
        // Arrange
        _catalog.SearchAsync("CBC", null, 5, default)
            .Returns([new CatalogItem("id1", "CBC with Differential", "Lab", 0.95)]);
        var sut = new SearchPlugin(_catalog, _logger);

        // Act
        var json = await sut.SearchOrdersAsync("CBC", limit: 5);

        // Assert
        json.Should().Contain("CBC with Differential");
    }
}
```

## Integration test setup

Integration tests load `appsettings.Development.json` (gitignored) and skip when gateway config is absent:

```csharp
public sealed class OrdersPipelineIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Skip if no real Gemini gateway configured
    private static readonly bool _hasGateway =
        !string.IsNullOrEmpty(
            new ConfigurationBuilder()
                .AddJsonFile("appsettings.Development.json", optional: true)
                .Build()["GeminiGateway:ApiBase"]);

    [SkippableFact]
    public async Task ProcessAsync_ReturnsIntents()
    {
        Skip.IfNot(_hasGateway, "Requires GeminiGateway:ApiBase in appsettings.Development.json");
        // ...
    }
}
```

## Coverage

Coverage threshold is **80%** (enforced in CI). Run locally with:

```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Naming conventions

- Unit test files: `tests/SmartOrders.Unit/Test<ClassName>.cs`
- Integration test files: `tests/SmartOrders.Integration/Test<Feature>Integration.cs`
- Test method: `MethodName_Scenario_ExpectedResult()`
