# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project
follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0]

First release. `net8.0`, zero dependencies.

### Added

- `MarginFuseClient.Track` reports an AI call that already happened. Returns
  immediately, sends in the background with retries, and never throws into
  application code.
- `MarginFuseClient.DecideAsync` asks whether the next call should run. Fails
  open to `DecisionAction.Allow` with `Degraded` set on any timeout or error.
- `MarginFuseClient.GuardAsync` does the whole loop: ask, run your callback with
  the resolved model, report the real cost, acknowledge what the application did.
- `MarginFuseClient.FlushAsync`, plus `IAsyncDisposable`, for workers that would
  otherwise exit before their last events are sent.
- `OpenRouter.From` maps an OpenRouter usage object, including the gateway's own
  cost, so gateway figures are exact rather than estimated.
- `Contract.Version` reports the shared contract this build was verified against.

### Notes on the design

- **`net8.0`, not `netstandard2.0`.** `System.Text.Json` is in the box there,
  which is what keeps the package free of external references: a library that
  pulls one in forces its version on every application that references it.
  Targeting netstandard would have meant either a package dependency or a
  hand-written JSON layer, and neither is worth it for a workload nobody is
  building on .NET Framework.
- **`DecideAsync` has no failure path.** A failed decision is not a condition to
  branch on: it is an allow with `Degraded` set.
- **`GuardAsync` takes a callback.** If it returned a decision to act on,
  forgetting the check once would let a blocked request reach the provider.
- **A null `Usage` property means not reported.** It is omitted from the request
  rather than sent as zero, because those are different claims.
- **Enum wire names are mapped explicitly**, not derived from member names, so
  renaming a C# member can never silently change what the API receives.
- Verified against
  [marginfuse/sdk-contract](https://github.com/marginfuse/sdk-contract): 16
  behavioral scenarios and 13 gateway vectors, the same ones the Node, Python,
  Go and Java SDKs pass.
