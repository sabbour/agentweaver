import { redact } from '../../harness-shared/redaction.mjs';

function requireProject(call, projectId, name) {
  const project = call.transientResponseBody;
  if (call.status !== 200 && call.status !== 201 || typeof project?.projectId !== 'string'
    || !project.projectId || projectId !== null && project.projectId !== projectId
    || project.name !== name || !['Active', 'Archived'].includes(project.state)
    || !Number.isSafeInteger(project.revision) || project.revision < 1) {
    throw new Error('The actual owner project identity, name, state, or revision is invalid.');
  }
  return project;
}

export class OwnedProjectFixture {
  static async create(client, { name }) {
    if (typeof name !== 'string' || !name.trim()) throw new Error('A test-owned project name is required.');
    for (const operation of ['createProject', 'getProject', 'updateProject']) {
      if (!client.operations.has(operation)) throw new Error(`Required project fixture operation is missing: ${operation}`);
    }
    const call = await client.invoke('createProject', { body: { name: name.trim() } });
    if (call.status !== 201) throw new Error('The owner did not return a created-project receipt.');
    const project = requireProject(call, null, name.trim());
    if (project.state !== 'Active') throw new Error('The created project is not active.');
    return new OwnedProjectFixture(client, project, call);
  }

  constructor(client, project, creation) {
    if (!client.calls.includes(creation) || creation.operationId !== 'createProject' || creation.status !== 201
      || creation.transientResponseBody?.projectId !== project.projectId) {
      throw new Error('A fixture can only own this client\'s actual created-project receipt.');
    }
    this.client = client;
    this.authProvider = client.authProvider;
    this.origin = client.target.origin;
    this.tenantId = client.tenantId;
    this.projectId = project.projectId;
    this.name = project.name;
    this.evidence = {
      targetOrigin: this.origin, tenantId: this.tenantId, projectId: this.projectId,
      creation, cleanupIntent: 'archive-created-project', cleanupResult: 'not-started',
      cleanupReceipts: [],
    };
  }

  async archive() {
    this.evidence.cleanupResult = 'pending';
    try {
      if (this.client.target.origin !== this.origin || this.client.tenantId !== this.tenantId
        || this.client.authProvider !== this.authProvider) {
        throw new Error('Refusing cleanup after a target, tenant, or auth-provider change.');
      }
      const read = await this.client.invoke('getProject', { pathParameters: { projectId: this.projectId } });
      this.evidence.cleanupReceipts.push(read);
      const current = requireProject(read, this.projectId, this.name);
      if (current.state !== 'Archived') {
        const archive = await this.client.invoke('updateProject', {
          pathParameters: { projectId: this.projectId },
          body: { expectedRevision: current.revision, name: current.name, state: 'Archived' },
        });
        this.evidence.cleanupReceipts.push(archive);
        if (archive.status !== 200) throw new Error('The owner rejected the revisioned project archive.');
        const verify = await this.client.invoke('getProject', { pathParameters: { projectId: this.projectId } });
        this.evidence.cleanupReceipts.push(verify);
        if (requireProject(verify, this.projectId, this.name).state !== 'Archived') {
          throw new Error('The owner has not confirmed that the created project is archived.');
        }
      }
      this.evidence.cleanupResult = 'verified-archived';
      delete this.evidence.cleanupError;
      return this.evidence;
    } catch (error) {
      this.evidence.cleanupResult = 'failed';
      this.evidence.cleanupError = redact(error);
      throw error;
    }
  }
}
