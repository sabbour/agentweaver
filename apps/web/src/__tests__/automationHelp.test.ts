import { describe, it, expect } from 'vitest';
import { AUTOMATION_HELP } from '../components/automationHelp';

// (c) Autopilot + Auto-approve UI clarification: the helper copy must explain each
// toggle in the agreed wording. These assertions lock the explanation in so the
// toggles never ship without their inline explanation again.

describe('AUTOMATION_HELP copy', () => {
  it('explains Autopilot: auto-answers clarifying questions, approvals still asked, logged', () => {
    const text = AUTOMATION_HELP.autopilotOrchestration;
    expect(text).toContain("Auto-answers the coordinator's clarifying questions");
    expect(text).toContain('using the coordinator model');
    expect(text).toContain('Tool/permission approvals are still asked');
    expect(text).toContain('logged in the timeline');
  });

  it('explains Auto-approve tools: approves tool calls except sandbox-blocked ones', () => {
    const text = AUTOMATION_HELP.autoApproveOrchestration;
    expect(text).toContain('Automatically approves tool calls');
    expect(text).toContain('blocked by sandbox policy');
    expect(text).toContain('require explicit approval');
  });

  it('reuses the same base copy on the pickup-defaults surface', () => {
    expect(AUTOMATION_HELP.autopilotPickup).toContain("Auto-answers the coordinator's clarifying questions");
    expect(AUTOMATION_HELP.autoApprovePickup).toContain('Automatically approves tool calls');
  });
});
