# Azure Blob Object Store foundation

`AzureBlobObjectStore` implements `IObjectStore` for large opaque platform objects,
not sandbox/workspace files. Construct it with a platform-owned
`BlobContainerClient` created by the composition root using a service endpoint
and an injected `TokenCredential` (for example workload identity); the adapter
does not accept a connection string, create a container, or fall back to disk.
The container must already exist and be authorized for the service identity.

Keys are case-sensitive relative slash-separated opaque identities; they are
not filesystem paths or tenant authorization tokens. Callers must enforce
authorization and record authoritative identities, references, journal positions,
and retention in Postgres. Never treat a successful Blob write as a committed
Postgres reference. Create-only writes reject an existing key with the SDK's
precondition failure; choose a new key for new content. The caller retains
ownership of the upload stream and must dispose the `ObjectRead` returned by
reads to close the download transport. `Length` is the reported Blob content
length. Missing blobs return null on read and false on delete; container errors,
permission failures, and other SDK/transport errors propagate.

The transport-backed tests use a fake HTTP handler through the real Azure SDK.
They do not prove managed-identity configuration, real Blob access, cloud
durability, or a deployed platform. Those checks await the dedicated v1 Azure
integration environment.
