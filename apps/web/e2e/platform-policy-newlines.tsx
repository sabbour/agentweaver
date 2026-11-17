import { useState } from 'react';
import { createRoot } from 'react-dom/client';
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { PlatformPolicyTextarea } from '../src/v1/PlatformPolicyTextarea';
import {
  createPlatformPolicyTextBuffer,
  serializePlatformPolicyTextBuffer,
} from '../src/v1/platformPolicyContracts';

const longLineEndings = ['\r\n', '\r', '\n'] as const;
let longSource = 'line-0';
for (let index = 1; index < 23; index += 1)
  longSource += `${longLineEndings[(index - 1) % longLineEndings.length]}line-${index}`;

export function NewlineTest() {
  const initialValue = new URLSearchParams(window.location.search).has('many')
    ? longSource
    : 'first\r\nsecond\rthird\n';
  const [buffer, setBuffer] = useState(() => createPlatformPolicyTextBuffer(initialValue));

  return (
    <FluentProvider theme={webLightTheme}>
      <PlatformPolicyTextarea
        label="Platform Policy text"
        buffer={buffer}
        onChange={setBuffer}
        rows={8}
      />
      <button
        type="button"
        onClick={() => {
          document.querySelector('#serialized')!.textContent =
            serializePlatformPolicyTextBuffer(buffer);
        }}
      >
        Serialize
      </button>
      <button
        type="button"
        onClick={() => setBuffer(createPlatformPolicyTextBuffer('first\r\n\rthird\n'))}
      >
        Load adjacent separators
      </button>
      <pre id="serialized" />
    </FluentProvider>
  );
}

createRoot(document.getElementById('root')!).render(<NewlineTest />);
