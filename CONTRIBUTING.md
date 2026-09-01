# Contributing

## Getting set up

The conformance contract is a submodule, so clone with it:

```bash
git clone --recurse-submodules https://github.com/marginfuse/marginfuse-dotnet
cd marginfuse-dotnet
dotnet test
```

If you already cloned without it: `git submodule update --init --recursive`.

Any .NET 8 SDK or newer works. The projects target `net8.0` and roll forward at
run time, so a machine with only a newer runtime can still run the tests.

## Before you open a pull request

```bash
dotnet build -c Release
dotnet test
dotnet build tools/ConformanceRunner/ConformanceRunner.csproj -c Release
npm --prefix contract/harness install
npm --prefix contract/harness run conformance dotnet
```

CI runs all of it on .NET 8, 9 and 10.

## Four rules worth knowing before you change behavior

**This SDK never throws into application code.** It sits in the request path of
somebody else's product. A transport error goes to `OnError` and the call
proceeds. The one exception is `GuardAsync`, which propagates whatever your own
callback threw, because your error handling owns provider failures.

**`GuardAsync` keeps its callback.** Returning a decision for the caller to act
on reads better and would be wrong: enforcement would depend on remembering a
check, and forgetting once means a blocked request reaches the provider.

**No package references.** `System.Text.Json` is in the box on `net8.0` and that
is the point. A reference here becomes every consumer's version conflict.

**Behavior is defined in the contract, not here.** The expectations live in
[marginfuse/sdk-contract](https://github.com/marginfuse/sdk-contract) as data,
and every MarginFuse SDK in every language reads the same files. If you are
changing what the SDK does rather than how it does it, the change starts with a
pull request there.

## Style

Match the surrounding code. Warnings are errors. Comments explain why, not what.
No em dashes.
