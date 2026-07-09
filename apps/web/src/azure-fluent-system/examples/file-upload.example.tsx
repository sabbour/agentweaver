import { FileUpload } from '..';

export function FileUploadExample() {
  return (
    <div className="azf-stack azf-gap-l">
      <FileUpload label="Certificate (.pfx)" placeholder="Select File" />
      <FileUpload label="Uploading" state="progress" fileName="prod-cert.pfx" progress={0.6} />
      <FileUpload label="Uploaded" state="success" fileName="prod-cert.pfx" />
      <FileUpload label="Bulk import" state="dragdrop" multiple />
    </div>
  );
}
