import { Button } from '..';
import { ArrowClockwiseRegular, DismissRegular, SparkleRegular } from '@fluentui/react-icons';
import { BladeHeader } from '..';
export function BladeHeaderExample() {
  return (
    <BladeHeader
      title="Storage accounts"
      subtitle="Sample shared services"
      resourceIcon={<SparkleRegular />}
      menuLabel="Storage account actions"
      actions={[
        { id: 'refresh', label: 'Refresh', icon: <ArrowClockwiseRegular />, onClick: () => undefined },
        { id: 'open-copilot', label: 'Ask Copilot', appearance: 'primary', icon: <SparkleRegular />, onClick: () => undefined },
      ]}
      overflowActions={[
        { id: 'dismiss', label: 'Close blade', icon: <DismissRegular />, onClick: () => undefined },
      ]}
      onDismiss={() => undefined}
      promptRibbon={<Button appearance="subtle">Summarize recent changes</Button>}
    />
  );
}
