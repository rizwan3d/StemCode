# StemCode Python Wrapper

This package is a thin Python facade over the `StemCode.Sdk` .NET API. It uses
[`pythonnet`](https://pythonnet.github.io/) to load a published `StemCode.dll`
and keeps the .NET SDK as the source of truth.

## Requirements

- Python 3.10 or newer
- .NET runtime that can run the StemCode target framework

## Setup From This Repository

From the repository root, enter the Python wrapper folder:

```powershell
cd python
```

Create a virtual environment and install the wrapper:

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -e .
```

The package loads the StemCode .NET SDK from `STEMCODE_DOTNET_PATH`, a bundled
payload if one was built, or a NuGet-backed cache. On first use, the wrapper can
download the NuGet package into that cache when no local payload is present.
Set your provider credentials:

```powershell
$env:STEMCODE_PROVIDER = "openai"
$env:STEMCODE_API_KEY = "PASTE_NEW_ROTATED_KEY_HERE"
$env:STEMCODE_MODEL = "gpt-5"
```

Run the example:

```powershell
python .\examples\basic_chat.py "Explain this repository in one paragraph."
```

## Basic Usage

```python
from stemcode import StemCodeClient

with (
    StemCodeClient.builder()
    .use_openai("PASTE_NEW_ROTATED_KEY_HERE", "gpt-5")
    .use_build_tool()
    .with_workspace(r"F:\Projects\GrowBitLabs\StemCode")
    .auto_approve_tools()
    .build()
) as client:
    client.on_assistant_message_chunk(print, end="")
    session = client.initialize()
    print(f"Using {session.provider_name} / {session.model_id}")

    result = client.run_turn("Summarize the SDK entry points.")
    print(result.response_text)
```

The wrapper exposes synchronous convenience methods around the .NET async API.
Callbacks are kept alive for the lifetime of the Python client.

## Runtime Path

The package resolves the StemCode .NET SDK in this order:

1. `load_stemcode(runtime_path=...)`, `StemCodeClient.builder(runtime_path=...)`,
   or `STEMCODE_DOTNET_PATH`
2. a bundled `stemcode/_dotnet` payload, when the wheel was built with one
3. a NuGet package downloaded into the StemCode Python cache

The runtime path can be:

- a folder containing `StemCode.dll`
- the full path to `StemCode.dll`

Publish output is preferred over copying a single assembly because pythonnet
must resolve StemCode's dependency assemblies too.

NuGet download settings:

- `STEMCODE_NUGET_PACKAGE`: package ID, default `StemCode`
- `STEMCODE_NUGET_VERSION`: package version, default matching `stemcode-sdk`
- `STEMCODE_NUGET_SOURCE`: flat-container source, default NuGet.org
- `STEMCODE_DOTNET_CACHE`: cache folder override

The NuGet package must contain `StemCode.dll` and its dependency DLLs in one of
these layouts:

- `tools/stemcode/<rid>/`
- `tools/stemcode/any/`
- `tools/<rid>/`
- `tools/`
- `lib/net10.0/`

To build a wheel with the .NET payload bundled instead of downloaded, set:

```powershell
$env:STEMCODE_BUNDLE_DOTNET = "1"
python -m build
```

To verify the NuGet download path end to end:

```powershell
python .\scripts\test_nuget_runtime.py --version 1.1.22
```
