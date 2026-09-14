import {
  ArrowRoutingRegular,
  BotRegular,
  BoxRegular,
  BranchRegular,
  DatabaseRegular,
  GlobeRegular,
  KeyRegular,
  ServerRegular,
  WindowRegular,
} from '@fluentui/react-icons';
import type { FluentIcon } from '@fluentui/react-icons';
import type { IconKind } from './types';

export const iconRegistry: Record<IconKind, FluentIcon> = {
  globe: GlobeRegular,
  branch: BranchRegular,
  route: ArrowRoutingRegular,
  window: WindowRegular,
  server: ServerRegular,
  bot: BotRegular,
  database: DatabaseRegular,
  key: KeyRegular,
  box: BoxRegular,
};
