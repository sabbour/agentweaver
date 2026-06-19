import { useEffect, useState } from 'react';
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
  SpinButton,
  Switch,
  Text,
  makeStyles,
  tokens,
} from '@fluentui/react-components';
import { SettingsRegular } from '@fluentui/react-icons';
import { apiClient } from '../../api/apiClient';
import { ApiError } from '../../api/client';
import type { BacklogSettingsDto } from '../../api/types';

const useStyles = makeStyles({
  fields: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
  },
  error: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
  },
});

export interface PickupSettingsProps {
  projectId: string;
}

// Per-project pickup settings (FR-008a): max Ready items per heartbeat (default 3,
// range 1..20) plus the autopilot / auto-approve-tools toggles from BacklogSettingsDto.
export function PickupSettings({ projectId }: PickupSettingsProps) {
  const styles = useStyles();
  const [open, setOpen] = useState(false);
  const [settings, setSettings] = useState<BacklogSettingsDto | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    let cancelled = false;
    apiClient.getBacklogSettings(projectId)
      .then((s) => { if (!cancelled) setSettings(s); })
      .catch((e) => { if (!cancelled) setError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e)); });
    return () => { cancelled = true; };
  }, [open, projectId]);

  const save = async () => {
    if (!settings) return;
    setBusy(true);
    setError(null);
    try {
      const saved = await apiClient.setBacklogSettings(projectId, settings);
      setSettings(saved);
      setOpen(false);
    } catch (e) {
      setError(e instanceof ApiError ? `API error ${e.status}: ${e.body}` : e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={(_, d) => { setOpen(d.open); if (d.open) setError(null); }}>
      <DialogTrigger disableButtonEnhancement>
        <Button appearance="secondary" icon={<SettingsRegular />}>Pickup settings</Button>
      </DialogTrigger>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>Pickup settings</DialogTitle>
          <DialogContent>
            <div className={styles.fields}>
              <Field label="Max Ready items per heartbeat" hint="How many Ready tasks the coordinator may claim per tick (1-20).">
                <SpinButton
                  min={1}
                  max={20}
                  value={settings?.max_ready_per_heartbeat ?? 3}
                  disabled={!settings || busy}
                  onChange={(_, data) => {
                    const v = data.value ?? (data.displayValue ? Number(data.displayValue) : undefined);
                    if (settings && v != null && !Number.isNaN(v)) {
                      setSettings({ ...settings, max_ready_per_heartbeat: Math.min(20, Math.max(1, Math.round(v))) });
                    }
                  }}
                />
              </Field>
              <Switch
                label="Autopilot"
                checked={settings?.pickup_autopilot ?? false}
                disabled={!settings || busy}
                onChange={(_, d) => settings && setSettings({ ...settings, pickup_autopilot: d.checked })}
              />
              <Switch
                label="Auto-approve tools"
                checked={settings?.pickup_auto_approve_tools ?? false}
                disabled={!settings || busy}
                onChange={(_, d) => settings && setSettings({ ...settings, pickup_auto_approve_tools: d.checked })}
              />
              {error && <Text className={styles.error}>{error}</Text>}
            </div>
          </DialogContent>
          <DialogActions>
            <DialogTrigger disableButtonEnhancement>
              <Button appearance="secondary" disabled={busy}>Cancel</Button>
            </DialogTrigger>
            <Button appearance="primary" disabled={!settings || busy} onClick={() => void save()}>Save</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  );
}
