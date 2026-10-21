/**
 * Table of the platform components a run may use and the implementations it
 * was allowed to choose from, read from the run's accepted selection. It shows
 * allowed choices, not which implementation is running: the service does not
 * report active implementations yet. When it does, the platform settings page
 * can reuse this table with an "Active" column.
 */
import { Badge } from '@fluentui/react-components';
import type { EffectiveProviderSelection } from './contracts';

/** Plain labels for selection cardinality values. Unknown values show as sent. */
const CARDINALITY_LABELS: Record<string, string> = {
  single: 'One',
  exactlyOne: 'One',
  one: 'One',
  ordered: 'Ordered list',
  orderedList: 'Ordered list',
  many: 'Several',
  multiple: 'Several',
};

/** Readable name for a platform component key such as `sandboxRuntime`. */
function componentLabel(key: string): string {
  // Split camelCase into words ("sandboxRuntime" -> "sandbox Runtime"), then turn
  // dashes, underscores, and dots into spaces.
  const words = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[-_.]+/g, ' ').trim();
  // Sentence case: capital first letter, rest lower. Fall back to the raw key if nothing is left.
  return words ? words[0].toUpperCase() + words.slice(1).toLowerCase() : key;
}

/** Renders one row per platform component with its allowed implementations. */
export function PlatformComponentsTable({ components, caption = 'Allowed implementations for this run' }: {
  components: EffectiveProviderSelection[];
  caption?: string;
}) {
  if (components.length === 0) return <p className="v1-muted">This run lists no platform components.</p>;
  // Rows are keyed by component, meter source, and index. The index keeps keys
  // unique if the same component appears more than once.
  return (
    <div className="v1-table-wrap">
      <table className="v1-table">
        <caption>{caption}</caption>
        <thead>
          <tr>
            <th scope="col">Platform component</th>
            <th scope="col">Choice</th>
            <th scope="col">Implementations, in order</th>
          </tr>
        </thead>
        <tbody>
          {components.map((component, index) => (
            <tr key={`${component.seam}/${component.meterSource ?? ''}/${index}`}>
              <th scope="row">
                {componentLabel(component.seam)}
                {component.meterSource && <span className="v1-muted"> · {component.meterSource}</span>}
              </th>
              <td>{CARDINALITY_LABELS[component.cardinality] ?? component.cardinality}</td>
              <td>
                {component.candidates.length === 0 ? <span className="v1-muted">None</span> : (
                  <ol className="v1-implementation-list">
                    {component.candidates.map((candidate) => (
                      <li key={`${candidate.providerId}/${candidate.optionsRevision}`}>
                        <code>{candidate.providerId}</code>
                        <span className="v1-muted"> · version {candidate.adapterVersion} · {candidate.hosting}</span>
                        {candidate.layer && <Badge appearance="outline" size="small">{candidate.layer}</Badge>}
                        {candidate.advertisedCapabilities.length > 0 && (
                          <div className="v1-muted">Can: {candidate.advertisedCapabilities.join(', ')}</div>
                        )}
                      </li>
                    ))}
                  </ol>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
