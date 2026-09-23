# StemCode Python Wrapper

This package is a thin Python facade over the `StemCode.Sdk` .NET API. It uses
[`pythonnet`](https://pythonnet.github.io/) to load a published `StemCode.dll`
and keeps the .NET SDK as the source of truth.

## Requirements

- Python 3.10 or newer
- .NET SDK/runtime that can run the bundled StemCode target framework

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

The package build publishes and bundles the StemCode .NET SDK automatically.
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

The package loads its bundled StemCode .NET SDK by default. To override it for
local development, call `load_stemcode(runtime_path=...)`, pass `runtime_path`
to `StemCodeClient.builder(...)`, or set `STEMCODE_DOTNET_PATH`. The path can be:

- a folder containing `StemCode.dll`
- the full path to `StemCode.dll`

Publish output is preferred over copying a single assembly because pythonnet
must resolve StemCode's dependency assemblies too.
