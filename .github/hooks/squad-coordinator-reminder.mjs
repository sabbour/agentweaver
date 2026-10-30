const reminder =
  "Squad reminder: when coordinating Squad work, use the actual roster and route by ownership; read the selected agent's charter and relevant canonical decisions from TEAM_ROOT. Capture accepted durable decisions through the governed Scribe process. If you are a specialist, stay in that role rather than claiming coordinator authority.";

const chunks = [];
for await (const chunk of process.stdin) chunks.push(chunk);

let output = {};
try {
  const input = JSON.parse(Buffer.concat(chunks).toString("utf8"));
  if (
    input !== null &&
    typeof input === "object" &&
    !Array.isArray(input) &&
    !Object.hasOwn(input, "agentId") &&
    typeof input.sessionId === "string"
  ) {
    output = { additionalContext: reminder };
  }
} catch {
  output = {};
}

process.stdout.write(`${JSON.stringify(output)}\n`);
