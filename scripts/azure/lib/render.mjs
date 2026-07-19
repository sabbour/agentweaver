// render.mjs -- envsubst replacement matching the deploy scripts' allow-list
// substitution behavior.
//
// 30-deploy.sh renders k8s/*.yaml with:
//
//   envsubst '${HOST} ${ACR_LOGIN_SERVER} ${IMAGE_TAG} ${AGENTHOST_IMAGE_TAG} ...' \
//     < "${yaml_file}" > "${RENDERED_DIR}/${fname}"
//
// GNU envsubst's second argument is a "shell-format" string: it is parsed
// purely to extract variable NAMES (regardless of whether they appear as
// `$VAR` or `${VAR}` inside that string) and produces an allow-list. Only
// variables in that allow-list are substituted in the input; everything
// else -- including other `${...}`/`$...` references that are NOT on the
// list -- is left byte-for-byte untouched in the output. A listed variable
// that has no value substitutes as the empty string (never removed, never
// left as the literal placeholder).
//
// This module reproduces exactly that: renderTemplate() substitutes only
// names present in `allowList`, using either `${VAR}` or `$VAR` reference
// syntax in the template, replaces missing values with '', and leaves any
// other `$...` reference in the input completely alone.

const VAR_REF = /\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)/g;

/**
 * Parses an envsubst-style allow-list string (e.g. `'${A} ${B} $C'`) into an
 * array of variable names, matching how GNU envsubst reads its second
 * argument.
 * @param {string} allowListString
 * @returns {string[]}
 */
export function parseAllowList(allowListString) {
  const names = [];
  let match;
  const re = new RegExp(VAR_REF);
  while ((match = re.exec(allowListString)) !== null) {
    names.push(match[1] ?? match[2]);
  }
  return names;
}

/**
 * Substitutes `${VAR}`/`$VAR` references in `template` for names present in
 * `allowList`, using values from `variables`. Unset/undefined/null values for
 * an allow-listed name become the empty string (matches envsubst semantics).
 * Any `$...` reference NOT in the allow-list is left completely literal.
 *
 * @param {string} template
 * @param {Record<string, unknown>} variables
 * @param {string[] | string} allowList array of names, or an envsubst-style
 *   allow-list string (e.g. `'${A} ${B}'`) which will be parsed first.
 * @returns {string}
 */
export function renderTemplate(template, variables, allowList) {
  const names = Array.isArray(allowList) ? allowList : parseAllowList(allowList);
  const allowSet = new Set(names);
  return template.replace(VAR_REF, (match, braced, bare) => {
    const name = braced ?? bare;
    if (!allowSet.has(name)) return match;
    const value = variables[name];
    return value === undefined || value === null ? "" : String(value);
  });
}
