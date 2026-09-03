import { createHttpTransport } from './transport-http.mjs';
import { createStdioTransport } from './transport-stdio.mjs';
import { redact } from '../../harness-shared/redaction.mjs';

function normalizeToolResult(result) {
  const content = Array.isArray(result?.content) ? result.content : [];
  const rawContent = content.map((item) => item?.text ?? JSON.stringify(item)).join('\n');
  let structuredContent = result?.structuredContent ?? null;
  if (structuredContent == null && rawContent) try { structuredContent = JSON.parse(rawContent); } catch { /* raw content is retained */ }
  return redact({ result, rawContent, structuredContent, isError: result?.isError === true });
}

export class McpHarnessClient {
  static async connect(options) {
    const transport = options.target === 'stdio' ? await createStdioTransport(options) : await createHttpTransport(options);
    const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
    const client = new Client({ name: 'agentweaver-mcp-harness', version: '0.1.0' });
    await client.connect(transport);
    return new McpHarnessClient(client, transport, options.ownershipPolicy);
  }
  constructor(client, transport, ownershipPolicy = undefined) {
    this.client = client;
    this.transport = transport;
    this.tools = [];
    this.ownershipPolicy = ownershipPolicy;
  }
  async discoverAllTools() {
    const response = await this.client.listTools();
    return Array.isArray(response?.tools) ? response.tools : [];
  }
  async discoverTools() {
    this.tools = await this.discoverAllTools();
    if (this.ownershipPolicy && !this.ownershipPolicy.ownedProjectId) {
      this.tools = this.tools.filter((tool) => tool.name !== 'project_delete');
    }
    return this.tools;
  }
  async callTool(name, arguments_ = {}) {
    const started = Date.now();
    if (name === 'project_delete' && this.ownershipPolicy) {
      const owned = this.ownershipPolicy.ownedProjectId;
      if (!owned || arguments_?.project_id !== owned) {
        return {
          toolName: name,
          toolArguments: arguments_,
          latencyMs: Date.now() - started,
          result: null,
          rawContent: 'project_delete denied: the dynamic harness may delete only its harness-created project',
          structuredContent: null,
          isError: true,
          protocolErrorCode: -32602,
          error: { message: 'project_delete denied by harness ownership policy' },
        };
      }
    }
    try { return redact({ toolName: name, toolArguments: arguments_, latencyMs: Date.now() - started, ...normalizeToolResult(await this.client.callTool({ name, arguments: arguments_ })) }); }
    catch (error) {
      return redact({ toolName: name, toolArguments: arguments_, latencyMs: Date.now() - started, result: null, rawContent: String(error?.message ?? error), structuredContent: null, isError: true, protocolErrorCode: error?.code ?? null, error: { message: String(error?.message ?? error) } });
    }
  }
  async close() { await this.client.close?.(); }
}
