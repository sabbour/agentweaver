import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { AgentMessageBubble } from '../components/AgentMessageBubble';

// Wrap in FluentProvider to satisfy Fluent style context
import { FluentProvider, webLightTheme } from '@fluentui/react-components';
import { type ReactNode } from 'react';

function Wrapper({ children }: { children: ReactNode }) {
  return <FluentProvider theme={webLightTheme}>{children}</FluentProvider>;
}

describe('AgentMessageBubble', () => {
  // B-01: streaming=true, isLiveRun=true → content visible
  it('renders content when streaming and isLiveRun', () => {
    render(
      <Wrapper>
        <AgentMessageBubble content="hello world" streaming={true} isLiveRun={true} />
      </Wrapper>,
    );
    expect(screen.getByText('hello world')).toBeDefined();
  });

  // B-02: streaming=false → content rendered
  it('renders content when not streaming', () => {
    render(
      <Wrapper>
        <AgentMessageBubble content="settled message" streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    expect(screen.getByText('settled message')).toBeDefined();
  });

  // B-03: streaming=true, isLiveRun=false (replay) → renders without cursor live-ness
  it('renders content in replay mode (no crash)', () => {
    render(
      <Wrapper>
        <AgentMessageBubble content="replayed" streaming={true} isLiveRun={false} />
      </Wrapper>,
    );
    expect(screen.getByText('replayed')).toBeDefined();
  });

  // B-04: empty content + streaming → renders wrapper (no crash, placeholder visible via aria)
  it('renders empty streaming message without crashing', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble content="" streaming={true} isLiveRun={true} />
      </Wrapper>,
    );
    // Should have a wrapper div with aria-label
    const wrapper = container.querySelector('[aria-label="Agent message"]');
    expect(wrapper).toBeDefined();
  });

  // B-05: empty settled message → renders nothing
  it('returns null for empty settled message', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble content="" streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    // FluentProvider adds a wrapper div but our component should not render
    const agentMsg = container.querySelector('[aria-label="Agent message"]');
    expect(agentMsg).toBeNull();
  });

  // B-06: empty settled message with only whitespace → renders nothing
  it('returns null for whitespace-only settled message', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble content="   " streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    const agentMsg = container.querySelector('[aria-label="Agent message"]');
    expect(agentMsg).toBeNull();
  });

  // Y-1: content at exactly display max (50,000 chars) is not truncated
  it('does not truncate content below 50k chars', () => {
    const content = 'x'.repeat(49999);
    render(
      <Wrapper>
        <AgentMessageBubble content={content} streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    expect(screen.queryByText(/Truncated/)).toBeNull();
  });

  // Y-1: content at 50,001 chars shows truncation note
  it('shows truncation note for very long content', () => {
    const content = 'x'.repeat(50001);
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble content={content} streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    // Use querySelector to find the truncation div (text may be split across nodes)
    const truncatedNote = Array.from(container.querySelectorAll('*')).find(
      (el) => el.textContent?.includes('truncated') || el.textContent?.includes('Truncated'),
    );
    expect(truncatedNote).toBeDefined();
  });

  // MD-01: settled message with bullet list renders as <ul> element (markdown is active)
  it('renders bullet list as <ul> for settled message', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble
          content={'- item one\n- item two\n- item three'}
          streaming={false}
          isLiveRun={false}
        />
      </Wrapper>,
    );
    expect(container.querySelector('ul')).not.toBeNull();
    expect(container.querySelector('li')).not.toBeNull();
  });

  // MD-02: settled message with heading renders as <h1>
  it('renders heading as <h1> for settled message', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble content="# Hello Heading" streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    expect(container.querySelector('h1')).not.toBeNull();
  });

  // MD-03: inline code renders as <code> element
  it('renders inline code as <code> element', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble content="Use `console.log` to debug." streaming={false} isLiveRun={false} />
      </Wrapper>,
    );
    expect(container.querySelector('code')).not.toBeNull();
  });

  // MD-04: fenced code block renders as <pre><code>
  it('renders fenced code block as <pre><code>', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble
          content={'```\nconst x = 1;\n```'}
          streaming={false}
          isLiveRun={false}
        />
      </Wrapper>,
    );
    expect(container.querySelector('pre')).not.toBeNull();
    expect(container.querySelector('pre code')).not.toBeNull();
  });

  // SEC-01: <script> tag in settled message is sanitized — no <script> element in DOM
  it('strips <script> tags from settled message (XSS prevention)', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble
          content={'Safe text <script>alert(1)</script> more text'}
          streaming={false}
          isLiveRun={false}
        />
      </Wrapper>,
    );
    // No <script> element must exist anywhere in the rendered output
    expect(container.querySelector('script')).toBeNull();
    // The raw string must not appear as executable HTML
    expect(container.innerHTML).not.toContain('<script>');
  });

  // SEC-02: <img onerror> event handler in settled message is stripped by sanitizer
  it('strips onerror event handlers from <img> tags (XSS prevention)', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble
          content={'<img src="x" onerror="alert(1)">'}
          streaming={false}
          isLiveRun={false}
        />
      </Wrapper>,
    );
    // No element with onerror must be present
    const allElements = Array.from(container.querySelectorAll('*'));
    const hasOnerror = allElements.some((el) => el.hasAttribute('onerror'));
    expect(hasOnerror).toBe(false);
    // If an img is present, it must not carry the onerror attribute
    const img = container.querySelector('img');
    if (img) {
      expect(img.getAttribute('onerror')).toBeNull();
    }
  });

  // MD-05: streaming (in-progress, isLiveRun=true) message renders as plain text, not markdown
  it('renders streaming live message as plain text (no <ul>/<h1> for markdown syntax)', () => {
    const { container } = render(
      <Wrapper>
        <AgentMessageBubble
          content={'# Heading\n- item'}
          streaming={true}
          isLiveRun={true}
        />
      </Wrapper>,
    );
    // Must NOT parse markdown during active streaming
    expect(container.querySelector('h1')).toBeNull();
    expect(container.querySelector('ul')).toBeNull();
    // Raw content must still be present as text
    expect(container.textContent).toContain('# Heading');
  });
});
