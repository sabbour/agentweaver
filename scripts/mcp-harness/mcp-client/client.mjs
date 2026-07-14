import { createHttpTransport } from './transport-http.mjs';
import { createStdioTransport } from './transport-stdio.mjs';

function normalizeToolResult(result) {
  const content = Array.isArray(result?.content) ? result.content : [];
  const rawContent = content.map((item) => item?.text ?? JSON.stringify(item)).join('\n');
  let structuredContent = result?.structuredContent ?? null;
  if (structuredContent == null && rawContent) try { structuredContent = JSON.parse(rawContent); } catch { /* raw content is retained */ }
  return { result, rawContent, structuredContent, isError: result?.isError === true };
}

export class McpHarnessClient {
  static async connect(options) {
    const transport = options.target === 'stdio' ? await createStdioTransport(options) : await createHttpTransport(options);
    const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
    const client = new Client({ name: 'agentweaver-mcp-harness', version: '0.1.0' });
    await client.connect(transport);
    return new McpHarnessClient(client, transport);
  }
  constructor(client, transport) { this.client = client; this.transport = transport; this.tools = []; }
  async discoverTools() {
    const response = await this.client.listTools();
    this.tools = Array.isArray(response?.tools) ? response.tools : [];
    return this.tools;
  }
  async callTool(name, arguments_ = {}) {
    const started = Date.now();
    try { return { toolName: name, toolArguments: arguments_, latencyMs: Date.now() - started, ...normalizeToolResult(await this.client.callTool({ name, arguments: arguments_ })) }; }
    catch (error) {
      return { toolName: name, toolArguments: arguments_, latencyMs: Date.now() - started, result: null, rawContent: String(error?.message ?? error), structuredContent: null, isError: true, protocolErrorCode: error?.code ?? null, error: { message: String(error?.message ?? error) } };
    }
  }
  async close() { await this.client.close?.(); }
}
