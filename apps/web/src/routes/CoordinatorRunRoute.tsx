import { useParams } from 'react-router-dom';
import { CoordinatorRunPage } from '../pages/CoordinatorRunPage';

export function CoordinatorRunRoute() {
  const { runId } = useParams();
  return <CoordinatorRunPage key={runId} />;
}
