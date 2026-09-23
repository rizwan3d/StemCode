# stemcode-sdk

JavaScript and TypeScript SDK for running StemCode from Node.js.

`stemcode-sdk` is a thin Node.js facade over the .NET `StemCode.Sdk` API. It
uses [`edge-js`](https://github.com/agracio/edge-js) to call the bundled
`StemCode.dll` in-process and keeps the .NET SDK as the source of truth.

## Requirements

- Node.js 18 or newer
- A .NET runtime that can run the bundled StemCode target framework
- Native build tools if `edge-js` needs to compile for your Node/platform pair

## Install

```bash
npm install stemcode-sdk
```

Set provider credentials:

```bash
export STEMCODE_PROVIDER=openai
export STEMCODE_API_KEY=PASTE_NEW_ROTATED_KEY_HERE
export STEMCODE_MODEL=gpt-5
```

On Windows PowerShell:

```powershell
$env:STEMCODE_PROVIDER = "openai"
$env:STEMCODE_API_KEY = "PASTE_NEW_ROTATED_KEY_HERE"
$env:STEMCODE_MODEL = "gpt-5"
```

## Quick Start

```js
const { StemCodeClient } = require("stemcode-sdk");

async function main() {
  const client = new StemCodeClient({
    provider: "openai",
    apiKey: process.env.STEMCODE_API_KEY,
    model: "gpt-5",
    workspace: process.cwd(),
    useBuildTool: true,
    autoApproveTools: true,
  });

  client.on("assistantMessageChunk", ({ text }) => process.stdout.write(text));
  client.on("statusMessage", ({ severity, message }) => {
    console.error(`[${severity}] ${message}`);
  });

  await client.initialize();
  const result = await client.runTurn("Summarize the SDK entry points.");
  console.log(result.responseText);
  await client.dispose();
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
```

## TypeScript Usage

```ts
import { StemCodeClient, type StemCodeTurnResult } from "stemcode-sdk";

const client = new StemCodeClient({
  provider: "openai",
  apiKey: process.env.STEMCODE_API_KEY,
  model: "gpt-5",
  workspace: process.cwd(),
  useBuildTool: true,
});

client.on("assistantMessageChunk", ({ text }) => process.stdout.write(text));

await client.initialize();
const result: StemCodeTurnResult = await client.runTurn("Explain dependency injection in C#.");
console.log(result.responseText);
await client.dispose();
```

## Working From This Repository

```powershell
cd js
npm install
npm run build
npm run build:dotnet
```

Run the JavaScript example:

```powershell
node .\examples\basic-chat.js "Explain this repository in one paragraph."
```

## Runtime Path

The package loads its bundled `dotnet/StemCode.dll` by default. For local
development you can point at any published StemCode SDK directory:

```powershell
$env:STEMCODE_DOTNET_PATH = "F:\Projects\GrowBitLabs\StemCode\js\dotnet"
```

You can also pass `runtimePath` to `new StemCodeClient({ ... })`. The value may
be a directory containing `StemCode.dll` or the full path to `StemCode.dll`.

`edge-js` must use CoreCLR for StemCode; the wrapper sets `EDGE_USE_CORECLR=1`
before loading `edge-js` when the variable is not already set.
