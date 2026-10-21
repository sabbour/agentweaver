/** Test-only rendering of the actual 0.x components with the same output fixture as the PR journey. */
import { FluentProvider } from '@fluentui/react-components';
import { createRoot } from 'react-dom/client';
import { useState } from 'react';
import { FilesTabPanel } from '../src/components/ArtifactBrowser';
import { FileViewer } from '../src/components/FileViewer';
import { agentweaverLightTheme } from '../src/theme';
import { capturedPath, capturedText } from '../src/v1/__fixtures__/outputCapture';

const getContent = async () => ({ path: capturedPath, content: capturedText, is_binary: false, language: null });
export default function Reference() {
  const [path, setPath] = useState<string | null>(null);
  return <FluentProvider theme={agentweaverLightTheme}>
    <style>{`
      body { margin: 0; }
      .reference { display: grid; grid-template-columns: minmax(180px, 1fr) minmax(0, 3fr); min-height: 340px; max-width: 600px; border: 1px solid #e0e0e0; }
      .files { padding: 8px; overflow: auto; }
      .viewer { display: flex; min-width: 0; min-height: 300px; max-height: 600px; overflow: auto; }
      @media(max-width: 640px) { .reference { grid-template-columns: minmax(0, 1fr); } }
    `}</style>
    <section className="reference" aria-label="0.x retained file browser">
      <div className="files"><FilesTabPanel workspaceFiles={[{ path: capturedPath, is_folder: false, status: null }]}
        workspaceLoading={false} workspaceError={null} selectedPath={path} onFileClick={setPath} /></div>
      <div className="viewer"><FileViewer runId="r-1" filePath={path} getContent={getContent} /></div>
    </section>
  </FluentProvider>;
}
createRoot(document.getElementById('root')!).render(<Reference />);
