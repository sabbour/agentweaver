import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { GatewayError } from './api';
import { errorCode, errorMessage } from './errors';
import { ErrorNotice } from './pageParts';

describe('error notices', () => {
  it('shows one plain message for an HTTP 404 with an internal code', () => {
    const error = new GatewayError(404, { code: 'not_found' }, 'The request failed with HTTP 404.');
    const message = errorMessage(error);
    render(<ErrorNotice code={errorCode(error)}>{message}</ErrorNotice>);

    expect(screen.getByRole('alert').textContent).toBe('The requested item could not be found.');
    expect(screen.queryByText(/not_found|HTTP 404/)).toBeNull();
  });
});
