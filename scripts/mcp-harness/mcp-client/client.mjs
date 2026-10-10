import { createHttpTransport } from './transport-http.mjs';
import { redact } from '../../harness-shared/redaction.mjs';

export class McpHarnessClient {
  static async connect(options) {
    const transport = await createHttpTransport(options);
    const { Client } = await import('@modelcontextprotocol/sdk/client/index.js');
    const client = new Client({ name: 'agentweaver-p1-mcp-harness', version: '0.1.0' });
    const protocol = [];
    const send = transport.send.bind(transport);
    transport.send = async message => {
      protocol.push(redact({ direction: 'request', at: new Date().toISOString(), message }));
      return send(message);
    };
    transport.onmessage = message => {
      protocol.push(redact({ direction: 'response', at: new Date().toISOString(), message }));
    };
    try {
      await client.connect(transport);
      return new McpHarnessClient(client, protocol);
    } catch (error) {
      try {
        await client.close();
      } catch (cleanupError) {
        throw new AggregateError([error, cleanupError], 'MCP connection and cleanup failed.');
      }
      throw error;
    }
  }

  constructor(client, protocol = []) {
    this.client = client;
    this.protocol = protocol;
    this.tools = new Map();
    this.calls = [];
  }

  async discoverTools() {
    this.tools.clear();
    const tools = new Map();
    const cursors = new Set();
    let cursor;
    do {
      const result = await this.client.listTools(cursor === undefined ? {} : { cursor });
      if (!Array.isArray(result?.tools)) throw new Error('The live MCP tools/list result is invalid.');
      for (const tool of result.tools) {
        if (typeof tool.name !== 'string' || !tool.name.startsWith('agentweaver_')
          || tools.has(tool.name) || !tool.inputSchema || tool.inputSchema.type !== 'object') {
          throw new Error('The live MCP P1 tool identity or schema is missing or duplicated.');
        }
        tools.set(tool.name, tool);
      }
      cursor = result.nextCursor;
      if (cursor !== undefined) {
        if (typeof cursor !== 'string' || !cursor || cursors.has(cursor)) {
          throw new Error('The live MCP tool cursor is invalid or repeated.');
        }
        cursors.add(cursor);
      }
    } while (cursor !== undefined);
    if (!tools.size) throw new Error('The live MCP server advertises no P1 tools.');
    this.tools = tools;
    return [...tools.values()];
  }

  async callTool(name, arguments_ = {}) {
    if (!this.tools.has(name)) throw new Error('The requested tool has not been discovered from the live MCP server.');
    const started = Date.now();
    const call = { toolName: name, toolArguments: redact(arguments_), result: null };
    try {
      const result = await this.client.callTool({ name, arguments: arguments_ });
      if (!Array.isArray(result?.content) || typeof result.isError !== 'boolean'
        || !result.structuredContent || typeof result.structuredContent !== 'object') {
        throw new Error('The live MCP result does not contain the P1 owner-result contract.');
      }
      const structured = result.structuredContent;
      if (result.isError !== true
        && (!Number.isInteger(structured.status) || structured.status < 200 || structured.status >= 400
          || !Object.hasOwn(structured, 'ownerResponse') || structured.ownerResponse?.accepted === false)) {
        throw new Error('The live MCP success result is missing its actual accepted owner response.');
      }
      call.result = redact(result);
      Object.defineProperty(call, 'transientResult', { value: result });
      return call;
    } catch (error) {
      call.error = redact(error);
      throw error;
    } finally {
      call.latencyMs = Date.now() - started;
      this.calls.push(call);
    }
  }

  async close() {
    await this.client.close();
  }
}
