/* eslint-disable react-refresh/only-export-components */
/**
 * Approved Fluent primitive components.
 *
 * The Azure UI Kit is built directly on Fluent 2 (`@fluentui/react-components`),
 * so the base primitives on the Figma "Azure UI Kit (Fluent 2)" foundations page
 * (`q2TdO4dVcMhNWYp0N6Bc05`, node `25156-116`) are consumed straight from Fluent v9
 * rather than reimplemented. This module surfaces them from the Azure Fluent System
 * barrel so agents and consumers have a single, discoverable import site and know
 * these primitives are approved for use inside Azure surfaces.
 *
 * Names already re-exported by `components.tsx` (Card, Field, Input, Label, Link,
 * MessageBar, Button, ProgressBar, Slider, Text) are intentionally omitted here to
 * avoid duplicate barrel exports — import those from the same barrel as usual.
 *
 * See `catalog/COMPONENTS.md` → "Approved Fluent primitive components" for the catalog
 * table. These are approved building blocks for Azure Fluent components and
 * product surfaces and may be represented as Fluent foundation previews in the
 * showcase. The one non-visual export here, `useToastController`, is a Fluent
 * hook/helper used with `Toaster`; it is intentionally documented here instead of
 * shown as its own rendered component.
 */
export {
  // Avatar & presence
  Avatar,
  Badge,
  CounterBadge,
  PresenceBadge,
  Persona,
  // Navigation & wayfinding
  Breadcrumb,
  BreadcrumbItem,
  BreadcrumbButton,
  BreadcrumbDivider,
  NavDrawer,
  NavItem,
  NavCategory,
  // Containers & surfaces
  CardHeader,
  CardFooter,
  CardPreview,
  Carousel,
  CarouselCard,
  Divider,
  Drawer,
  OverlayDrawer,
  InlineDrawer,
  DrawerHeader,
  DrawerBody,
  Dialog,
  DialogSurface,
  DialogBody,
  DialogTitle,
  DialogActions,
  DialogContent,
  DialogTrigger,
  // Selection & input
  Checkbox,
  Dropdown,
  Option,
  OptionGroup,
  RadioGroup,
  Radio,
  Rating,
  RatingDisplay,
  SearchBox,
  SpinButton,
  SwatchPicker,
  ColorSwatch,
  Switch,
  Textarea,
  InfoLabel,
  // Data display
  List,
  ListItem,
  Skeleton,
  SkeletonItem,
  Spinner,
  Tag,
  InteractionTag,
  TagGroup,
  TagPicker,
  TagPickerControl,
  Tree,
  TreeItem,
  FlatTree,
  // Overlays & feedback
  Menu,
  MenuTrigger,
  MenuList,
  MenuItem,
  MenuPopover,
  MessageBarBody,
  MessageBarActions,
  MessageBarTitle,
  TeachingPopover,
  Toast,
  Toaster,
  useToastController,
  ToastTitle,
  Toolbar,
  ToolbarButton,
  ToolbarDivider,
  Tooltip,
} from '@fluentui/react-components';
