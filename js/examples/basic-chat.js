const { StemCodeClient } = require("../dist");

async function main() {
  const prompt = process.argv.slice(2).join(" ") || "Explain StemCode in one paragraph.";
  const provider = process.env.STEMCODE_PROVIDER || "openai";
  const apiKey = process.env.STEMCODE_API_KEY;
  const model = process.env.STEMCODE_MODEL;

  const client = new StemCodeClient({
    provider,
    apiKey,
    model,
    workspace: process.cwd(),
    useBuildTool: true,
    autoApproveTools: true,
  });

  client.on("assistantMessageChunk", ({ text }) => process.stdout.write(text));
  client.on("toolCallsStarted", ({ toolCalls }) => {
    console.error(`\nRunning ${toolCalls.length} tool(s)...`);
  });
  client.on("statusMessage", ({ severity, message }) => {
    console.error(`\n[${severity}] ${message}`);
  });

  try {
    const session = await client.initialize();
    console.error(`Using ${session.providerName} / ${session.modelId}`);
    const result = await client.runTurn(prompt);
    console.log(`\n\n${result.responseText}`);
  } finally {
    await client.dispose();
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
