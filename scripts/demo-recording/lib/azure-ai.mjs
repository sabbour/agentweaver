import fs from 'node:fs/promises';
import path from 'node:path';

export class AISettings {
  constructor({
    endpoint,
    apiKey,
    chatDeployment = 'gpt-5-chat',
    narrationModel = 'gpt-5-chat',
    chatApiVersion = '2025-04-01-preview',
    ttsVoice = 'en-US-Ava:DragonHDLatestNeural',
    speechRegion,
  }) {
    this.endpoint = endpoint;
    this.apiKey = apiKey;
    this.chatDeployment = chatDeployment;
    this.narrationModel = narrationModel;
    this.chatApiVersion = chatApiVersion;
    this.ttsVoice = ttsVoice;
    this.speechRegion = speechRegion;
  }

  static fromEnv(env = process.env) {
    const endpoint = env.AGENTWEAVER_DEMO_AI_ENDPOINT;
    const apiKey = env.AGENTWEAVER_DEMO_AI_KEY;
    if (!endpoint || !apiKey) {
      throw new Error('Missing AGENTWEAVER_DEMO_AI_ENDPOINT or AGENTWEAVER_DEMO_AI_KEY.');
    }
    return new AISettings({
      endpoint,
      apiKey,
      chatDeployment: env.AGENTWEAVER_DEMO_CHAT_DEPLOYMENT || env.AGENTWEAVER_DEMO_NARRATION_MODEL || 'gpt-5-chat',
      narrationModel: env.AGENTWEAVER_DEMO_NARRATION_MODEL || env.AGENTWEAVER_DEMO_CHAT_DEPLOYMENT || 'gpt-5-chat',
      chatApiVersion: env.AGENTWEAVER_DEMO_CHAT_API_VERSION || '2025-04-01-preview',
      ttsVoice: env.AGENTWEAVER_DEMO_TTS_VOICE || 'en-US-Ava:DragonHDLatestNeural',
      speechRegion: env.AGENTWEAVER_DEMO_SPEECH_REGION || '',
    });
  }
}

function trimNarration(text) {
  return text.replace(/\s+/g, ' ').trim();
}

export async function generateNarrationText(settings, { beat, contextSummary }) {
  const endpoint = new URL(`/openai/deployments/${settings.chatDeployment}/chat/completions?api-version=${encodeURIComponent(settings.chatApiVersion)}`, settings.endpoint);
  const system = [
    'You are writing spoken narration for a product demo.',
    'Do NOT write closed captions.',
    'Keep the writing natural for speech synthesis.',
    'Capture the main point of the slice, not a caption-by-caption replay.',
    'Keep it concise: 1-4 natural sentences.',
    'Do not mention that anything is blocked unless the context explicitly says this beat is blocked.',
  ].join(' ');
  const user = [
    `Beat: ${beat.id} — ${beat.title}`,
    `Reference narration / beat doc: ${beat.narrationSource || beat.markdown.slice(0, 1200)}`,
    contextSummary ? `Live context: ${contextSummary}` : '',
    beat.blockers?.length ? `Known blockers: ${beat.blockers.join('; ')}` : '',
  ].filter(Boolean).join('\n\n');

  for (let attempt = 0; attempt < 4; attempt += 1) {
    const response = await fetch(endpoint, {
      method: 'POST',
      headers: {
        'api-key': settings.apiKey,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        messages: [
          { role: 'system', content: system },
          { role: 'user', content: user },
        ],
        max_completion_tokens: 220,
      }),
    });
    const text = await response.text();
    if (response.ok) {
      const data = JSON.parse(text);
      const content = data?.choices?.[0]?.message?.content;
      if (!content) throw new Error('Narration generation returned no content.');
      return trimNarration(content);
    }
    if (response.status === 429 && attempt < 3) {
      await new Promise((resolve) => setTimeout(resolve, 3000 * (attempt + 1)));
      continue;
    }
    throw new Error(`Narration generation failed (${response.status}): ${text}`);
  }
  throw new Error('Narration generation exhausted retries.');
}

export async function synthesizeSpeechToFile(settings, { text, outputPath, voiceName }) {
  await fs.mkdir(path.dirname(outputPath), { recursive: true });
  const synthesisUrl = new URL('/tts/cognitiveservices/v1', settings.endpoint);
  const body = [
    `<speak version="1.0" xml:lang="en-US">`,
    `  <voice name="${voiceName || settings.ttsVoice}">`,
    text
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;'),
    '  </voice>',
    '</speak>',
  ].join('');
  const response = await fetch(synthesisUrl, {
    method: 'POST',
    headers: {
      'Ocp-Apim-Subscription-Key': settings.apiKey,
      'Content-Type': 'application/ssml+xml',
      'X-Microsoft-OutputFormat': 'riff-24khz-16bit-mono-pcm',
      'User-Agent': 'agentweaver-demo-recording',
    },
    body,
  });
  if (!response.ok) {
    throw new Error(`Speech synthesis failed (${response.status}): ${await response.text()}`);
  }
  const audioBuffer = Buffer.from(await response.arrayBuffer());
  await fs.writeFile(outputPath, audioBuffer);
}

export function deriveSpeechRegion(endpoint) {
  const host = new URL(endpoint).hostname.toLowerCase();
  const known = ['eastus2', 'eastus', 'westus2', 'westus3', 'westus', 'centralus', 'uksouth', 'northeurope', 'westeurope'];
  return known.find((region) => host.includes(region)) || '';
}
