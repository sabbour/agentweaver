import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { useParams } from 'react-router-dom';
/** Binds the run page lifecycle to the run ID from the current route. */
export function CoordinatorRunRoute() {
  const { runId } = useParams();
  return <CoordinatorRunPage key={runId} />;
}
