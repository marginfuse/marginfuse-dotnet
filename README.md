# MarginFuse for .NET

[![NuGet](https://img.shields.io/nuget/v/MarginFuse)](https://www.nuget.org/packages/MarginFuse)
[![ci](https://github.com/marginfuse/marginfuse-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/marginfuse/marginfuse-dotnet/actions/workflows/ci.yml)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

Server-side SDK for [MarginFuse](https://marginfuse.com): profitability
guardrails for AI SaaS. Connect revenue to per-request AI cost, see gross margin
per customer, and stop loss-making requests before they run.

- **Metadata only, by construction.** The event shape has no field for prompts
  or responses, so they cannot be sent. Not a policy, an absence.
- **Never breaks your app.** It does not throw into your code, and it does not
  block your request on MarginFuse being up. If MarginFuse is unreachable, your
  requests proceed unchanged.
- **Zero dependencies.** `net8.0`, using `System.Text.Json` from the box.
  Nothing to conflict with what your application already references.

> **Server side only.** This SDK carries a secret API key. Never ship it in a
> desktop or mobile application, or anything else a user can read.

## Install

```bash
dotnet add package MarginFuse
```

## Track an AI call

Monitoring. One call after each AI request, metadata only.

```csharp
await using var mf = new MarginFuseClient(new MarginFuseOptions
{
    ApiKey = Environment.GetEnvironmentVariable("MARGINFUSE_KEY")!,
});

mf.Track(new TrackParams
{
    CustomerId = "cus_8x2m91",   // your Stripe customer id, or your own
    Feature = "ai_chat",
    Provider = "openai",
    Model = "gpt-4.1",
    Usage = new Usage { InputTokens = 1204, OutputTokens = 388 },
});
```

`Track` returns immediately and sends in the background with retries. In a
worker or a short-lived process, flush before exiting. The client is
`IAsyncDisposable`, so `await using` does it for you.

A null property in `Usage` means *not reported*, not "used none": it is left off
the request entirely, because claiming a call used zero input tokens is a
different statement from not knowing what it used.

### With dependency injection

The client is safe to share, so register it once:

```csharp
builder.Services.AddSingleton(_ => new MarginFuseClient(new MarginFuseOptions
{
    ApiKey = builder.Configuration["MarginFuse:ApiKey"]!,
    OnError = (error, context) => logger.LogWarning(error, "marginfuse {Context}", context),
}));
```

## Guard a call

Protection. Ask before the call runs, and act on the answer.

```csharp
var outcome = await mf.GuardAsync(
    new DecideParams
    {
        CustomerId = "cus_8x2m91",
        Feature = "ai_chat",
        Provider = "openai",
        Model = "gpt-4.1",
    },
    async decision =>
    {
        // decision.Model is the one to call: a downgrade verdict changes it.
        var response = await client.ChatAsync(decision.Model, messages);
        return new ProviderCall<ChatResponse>
        {
            Result = response,
            Usage = new Usage
            {
                InputTokens = response.PromptTokens,
                OutputTokens = response.CompletionTokens,
            },
        };
    });

switch (outcome.Kind)
{
    case GuardKind.Completed: Use(outcome.Result!); break;
    case GuardKind.TopupRequired: ShowTopup(outcome.Decision.TopupContext); break;
    case GuardKind.Blocked: ShowLimitReached(); break;
}
```

One call does the whole loop: ask, run with the resolved model, report the real
cost, acknowledge what your application did.

### Why a callback

Enforcement must not depend on you remembering to check anything. If
`GuardAsync` returned a decision for you to act on, forgetting the check once
would mean a blocked request reaches the provider anyway. With a callback that
is structurally impossible: when the verdict is `Block`, your lambda is never
invoked.

### Why DecideAsync has no failure path

There is no failure a caller should branch on. A decision that times out or
errors is an *allow* with `Degraded` set, because MarginFuse being unreachable
must never become your outage. Transport failures go to `OnError`.

## OpenRouter and other gateways

Gateways report the real cost of every call. Forward it and your figures are
exact instead of estimated.

```csharp
// usage is the decoded "usage" object from the response
var mapped = OpenRouter.From(usage);

mf.Track(new TrackParams
{
    CustomerId = "cus_8x2m91",
    Feature = "ai_chat",
    Provider = "openrouter",
    Model = "anthropic/claude-sonnet-4.5",
    Usage = mapped.Usage,
    CostUsd = mapped.CostUsd,
});
```

`OpenRouter.From` takes a `JsonElement`, so no particular HTTP client is
implied. Use it rather than mapping the fields yourself: OpenRouter's
`prompt_tokens` already includes cached reads and cache writes, which MarginFuse
prices separately, so passing it through directly charges every cached token
twice at the full input rate. The helper also formats the cost as a decimal
string, because the default numeric formatting produces `1.2E-07` for small
costs and the API rejects that.

## Configuration

```csharp
new MarginFuseOptions
{
    ApiKey = Environment.GetEnvironmentVariable("MARGINFUSE_KEY")!,
    BaseUrl = "https://api.marginfuse.com",       // your own deployment in dev
    Timeout = TimeSpan.FromMilliseconds(1500),    // decide budget before failing open
    OnError = (error, context) => log.Warn(error, context),
    HttpClient = myClient,                        // proxies, IHttpClientFactory
}
```

`OnError` is the only place transport failures surface. The SDK swallows them so
they cannot become your outage; without the handler they are silent.

## What it sends

Everything, and nothing else:

```
eventId  customerId  feature  provider  model  requestedModel
usage { inputTokens, outputTokens, cachedInputTokens,
        cacheCreationTokens, images, audioSeconds }
costUsd  occurredAt  outcome  decisionId  retryOfEventId  correctsEventId
```

There is no field for message content anywhere in the wire types. The
[conformance suite](https://github.com/marginfuse/sdk-contract) checks this
against the bytes that actually leave the process, on every scenario.

## Conformance

This SDK is verified against
[marginfuse/sdk-contract](https://github.com/marginfuse/sdk-contract), the same
contract every MarginFuse SDK in every language is held to. It is a submodule
here, so the pinned commit records exactly which contract a release passed, and
`Contract.Version` reports it at runtime.

```bash
git clone --recurse-submodules https://github.com/marginfuse/marginfuse-dotnet
cd marginfuse-dotnet
dotnet test                       # unit tests, plus the shared gateway vectors
dotnet build tools/ConformanceRunner/ConformanceRunner.csproj -c Release
npm --prefix contract/harness install
npm --prefix contract/harness run conformance dotnet
```

## Links

- [MarginFuse](https://marginfuse.com), product and pricing
- [Documentation](https://marginfuse.com/docs)
- [API reference](https://api.marginfuse.com/openapi.json)
- [Security policy](SECURITY.md)
- [Contributing](CONTRIBUTING.md)

MIT, Pemira Labs.
