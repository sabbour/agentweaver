import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, waitFor, cleanup } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useNavigate } from 'react-router-dom';

const mockState = vi.hoisted(() => ({ mountCount: 0 }));

vi.mock('../pages/CoordinatorRunPage', async () => {
  const React = await vi.importActual<typeof import('react')>('react');
  const Router = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return {
    CoordinatorRunPage: () => {
      const { runId } = Router.useParams();
      const [mountId] = React.useState(() => ++mockState.mountCount);
      return (
        <div>
          <span>run:{runId}</span>
          <span>mount:{mountId}</span>
        </div>
      );
    },
  };
});

import { CoordinatorRunRoute } from '../routes/CoordinatorRunRoute';

function RouteHarness() {
  const navigate = useNavigate();
  return (
    <>
      <CoordinatorRunRoute />
      <button onClick={() => navigate('/projects/p1/orchestrations/run-b')}>next run</button>
    </>
  );
}

beforeEach(() => {
  cleanup();
  mockState.mountCount = 0;
});

describe('CoordinatorRunRoute', () => {
  it('remounts CoordinatorRunPage when the runId route param changes', async () => {
    render(
      <MemoryRouter initialEntries={['/projects/p1/orchestrations/run-a']}>
        <Routes>
          <Route path="/projects/:projectId/orchestrations/:runId" element={<RouteHarness />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText('run:run-a')).toBeDefined();
    expect(screen.getByText('mount:1')).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'next run' }));

    await waitFor(() => expect(screen.getByText('run:run-b')).toBeDefined());
    expect(screen.getByText('mount:2')).toBeDefined();
  });
});
