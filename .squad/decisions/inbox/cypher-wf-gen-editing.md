# Cypher workflow generation editing decisions

- Implemented #59 as an API-first extension of the existing workflow generate endpoint: `base_workflow_id` edits saved/built-in workflows and `base_yaml` supports iterative unsaved draft edits. The endpoint still returns an unsaved draft preview; saving remains the existing validated PUT path.
- Built-in/library workflow edits are treated as immutable-source edits. The generator prompt requires a project-owned customized copy, and validation/correction rejects built-in edit output that keeps the reserved base id.
- Implemented #116 as prompt hardening plus structural validation in the existing blueprint validation path. Generated blueprint failures now return plain-language details with regenerate/edit options.
- Added workflow graph reachability/bindability checks to blueprint validation instead of a separate save path so generated, inline, and predefined blueprint flows share the same guard.
- No UI changes were required for this slice.
