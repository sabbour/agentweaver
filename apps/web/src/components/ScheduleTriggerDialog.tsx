import {
  Button,
  Dialog,
  DialogActions,
  DialogBody,
  DialogContent,
  DialogSurface,
  DialogTitle,
  DialogTrigger,
  Field,
  Input,
  MessageBar,
  MessageBarBody,
  Select,
  tokens,
} from '@fluentui/react-components';
import { DismissRegular } from '@fluentui/react-icons';
import { useState } from 'react';
import type { WorkflowScheduleTrigger } from '../utils/workflowYaml';

export interface ScheduleTriggerDialogProps {
  open: boolean;
  trigger: WorkflowScheduleTrigger | null;
  saving?: boolean;
  readOnly?: boolean;
  onDismiss: () => void;
  onSave: (trigger: WorkflowScheduleTrigger) => void | Promise<void>;
  onRemove: () => void | Promise<void>;
}

export function ScheduleTriggerDialog({
  open,
  ...props
}: ScheduleTriggerDialogProps) {
  if (!open) return null;
  const triggerKey = props.trigger
    ? `${props.trigger.interval}:${props.trigger.timeOfDay}:${props.trigger.dayOfWeek ?? ''}:${props.trigger.dayOfMonth ?? ''}`
    : 'none';
  return <ScheduleTriggerDialogContent key={triggerKey} {...props} />;
}

function ScheduleTriggerDialogContent({
  trigger,
  saving = false,
  readOnly = false,
  onDismiss,
  onSave,
  onRemove,
}: Omit<ScheduleTriggerDialogProps, 'open'>) {
  const [interval, setInterval] = useState<WorkflowScheduleTrigger['interval']>(trigger?.interval ?? 'daily');
  const [timeOfDay, setTimeOfDay] = useState(trigger?.timeOfDay ?? '09:00');
  const [dayOfWeek, setDayOfWeek] = useState(trigger?.dayOfWeek ?? 'monday');
  const [dayOfMonth, setDayOfMonth] = useState(String(trigger?.dayOfMonth ?? 1));

  const monthlyDay = Number(dayOfMonth);
  const valid = /^\d{2}:\d{2}$/.test(timeOfDay)
    && (interval !== 'monthly' || (monthlyDay >= 1 && monthlyDay <= 28));

  return (
    <Dialog open onOpenChange={(_, data) => { if (!saving && !data.open) onDismiss(); }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle
              action={
                <DialogTrigger disableButtonEnhancement>
                  <Button appearance="subtle" aria-label="Close" icon={<DismissRegular />} />
                </DialogTrigger>
              }
            >Schedule workflow</DialogTitle>
          <DialogContent>
            {readOnly && (
              <MessageBar intent="info">
                <MessageBarBody>
                  Built-in workflows are read-only. Duplicate this workflow to configure its schedule.
                </MessageBarBody>
              </MessageBar>
            )}
            <Field label="Cadence">
              <Select
                value={interval}
                onChange={(_, data) => setInterval(data.value as WorkflowScheduleTrigger['interval'])}
                disabled={saving || readOnly}
              >
                <option value="daily">Daily</option>
                <option value="weekly">Weekly</option>
                <option value="monthly">Monthly</option>
              </Select>
            </Field>
            {interval === 'weekly' && (
              <Field label="Day of week" style={{ marginTop: tokens.spacingVerticalS }}>
                <Select
                  value={dayOfWeek}
                  onChange={(_, data) => setDayOfWeek(data.value)}
                  disabled={saving || readOnly}
                >
                  {['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday']
                    .map((day) => <option key={day} value={day}>{day}</option>)}
                </Select>
              </Field>
            )}
            {interval === 'monthly' && (
              <Field label="Day of month (1–28)" style={{ marginTop: tokens.spacingVerticalS }}>
                <Input
                  type="number"
                  min="1"
                  max="28"
                  value={dayOfMonth}
                  onChange={(_, data) => setDayOfMonth(data.value)}
                  disabled={saving || readOnly}
                />
              </Field>
            )}
            <Field
              label="UTC time"
              hint="Schedules are evaluated in UTC."
              style={{ marginTop: tokens.spacingVerticalS }}
            >
              <Input
                type="time"
                value={timeOfDay}
                onChange={(_, data) => setTimeOfDay(data.value)}
                disabled={saving || readOnly}
              />
            </Field>
          </DialogContent>
          <DialogActions>
            {trigger && !readOnly && (
              <Button appearance="subtle" disabled={saving} onClick={() => { void onRemove(); }}>
                Remove schedule
              </Button>
            )}
            <Button appearance="subtle" disabled={saving} onClick={onDismiss}>
              {readOnly ? 'Close' : 'Cancel'}
            </Button>
            {!readOnly && (
              <Button
                appearance="primary"
                disabled={saving || !valid}
                onClick={() => {
                  void onSave({
                    interval,
                    timeOfDay,
                    dayOfWeek: interval === 'weekly' ? dayOfWeek : undefined,
                    dayOfMonth: interval === 'monthly' ? monthlyDay : undefined,
                  });
                }}
              >
                {saving ? 'Saving…' : 'Save schedule'}
              </Button>
            )}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
