import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';
import { useParams } from 'react-router-dom';
export function CoordinatorRunRoute() {
  const { runId } = useParams();
  return <CoordinatorRunPage key={runId} />;
}
