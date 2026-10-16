import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      'react-refresh/only-export-components': ['error', {
        allowConstantExport: true,
        allowExportNames: [
          'ARTIFACT_HOLD_MS',
          'mountLandingWorkflowDemo',
          'ExecutionModalContext',
          'ActiveEdgeContext',
          'CoordinatorSessionContext',
          'BrowseFilesContext',
          'roleDescForRole',
          'iconForRole',
          'useNodeStyles',
          'accentClass',
          'StatusBadge',
          'ElapsedTimer',
          'statusDescription',
          'NodeDetailPopover',
          'workflowNodeTypes',
          'workflowEdgeTypes',
          'forwardEdge',
          'loopbackEdge',
          'coordinatorLoopbackLabel',
          'MIN_ZOOM',
          'MAX_ZOOM',
          'ZOOM_STEP',
          'clampZoom',
          'useCtrlScrollZoom',
          'projectSwitchTarget',
          'useTypographyStyles',
          'useProjectList',
          'useRefreshCountdown',
          'readIneligibleSubtasks',
          'parseIneligibleIdsFromReason',
          'normalizeAssemblyBlockedReason',
          'compareRunTreeSiblings',
        ],
      }],
    },
  },
])
